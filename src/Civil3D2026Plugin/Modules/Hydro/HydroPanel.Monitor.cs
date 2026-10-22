using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using SW = System.Windows;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Superficie do monitor: um elemento so, desenhado por DrawingContext (sem um UIElement por
/// linha/circulo/texto). Redes de milhares de pecas redesenham em milissegundos.
/// </summary>
internal sealed class HydroMonitorSurface : SW.FrameworkElement
{
    public WM.Brush Background { get; set; } = WM.Brushes.Black;

    /// <summary>Desenho corrente (o painel reabre e redesenha a cada mudanca).</summary>
    public WM.DrawingGroup Drawing { get; } = new();

    public HydroMonitorSurface()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    protected override void OnRender(WM.DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new SW.Rect(RenderSize));
        dc.DrawDrawing(Drawing);
    }
}

/// <summary>Monitor do painel HYDRO: planta esquematica da rede (setas do fluxo, bacias) ou perfil do caminho.</summary>
internal sealed partial class HydroPanel
{
    private static readonly WM.Brush GraphicBack = Brush(56, 58, 62);
    private static readonly WM.Brush GraphicGrid = Brush(66, 68, 73);
    private static readonly WM.Brush GraphicGridMajor = Brush(86, 88, 94);
    private static readonly WM.Brush GraphicText = Brush(205, 205, 205);
    private static readonly WM.Brush HighlightBrush = Brush(255, 176, 0);
    private static readonly WM.Brush HoverBrush = Brush(255, 255, 255);
    private static readonly WM.Brush PipeBrush = Brush(120, 220, 235);
    private static readonly WM.Brush PipeUndefined = Brush(240, 120, 120);
    private static readonly WM.Brush StructureBrush = Brush(235, 240, 245);
    private static readonly WM.Brush CatchmentBrush = Brush(230, 90, 230);
    private static readonly WM.Brush CatchmentFill = Frozen(new WM.SolidColorBrush(WM.Color.FromArgb(40, 230, 90, 230)));
    private static readonly WM.Brush GroundBrush = Brush(150, 200, 110);
    private static readonly WM.Brush OtherNetwork = Brush(130, 135, 140);
    private static readonly WM.Brush RulerBrush = Brush(44, 46, 50);
    private static readonly WM.Brush OkBrush = Brush(110, 210, 110);
    private static readonly WM.Brush WarnBrush = Brush(244, 196, 64);
    private static readonly WM.Brush BadBrush = Brush(240, 70, 70);
    private static readonly WM.Brush NoDataBrush = Brush(140, 145, 150);
    private static readonly WM.Brush MutedText = Brush(150, 153, 158);
    private static readonly WM.Typeface MonoFace = new("Consolas");

    // Perfil (cores no espirito da "Network View" do Project Explorer: solo sombreado, faixa de
    // recobrimento minimo, tubo com parede e interior, lamina e linha piezometrica).
    private static readonly WM.Brush SoilFill = Frozen(new WM.LinearGradientBrush(
        WM.Color.FromArgb(150, 122, 98, 66), WM.Color.FromArgb(40, 122, 98, 66), 90));
    private static readonly WM.Brush CoverZoneFill = Frozen(new WM.SolidColorBrush(WM.Color.FromArgb(46, 244, 196, 64)));
    private static readonly WM.Brush CoverZoneLine = Frozen(new WM.SolidColorBrush(WM.Color.FromArgb(150, 244, 196, 64)));
    private static readonly WM.Brush PipeWallFill = Brush(128, 134, 142);
    private static readonly WM.Brush PipeInsideFill = Brush(30, 32, 36);
    private static readonly WM.Brush WaterFill = Frozen(new WM.SolidColorBrush(WM.Color.FromArgb(185, 64, 146, 255)));
    private static readonly WM.Brush HglBrush = Brush(110, 190, 255);
    private static readonly WM.Brush StructureFill = Brush(46, 48, 53);
    private static readonly WM.Brush OrdinateBrush = Frozen(new WM.SolidColorBrush(WM.Color.FromArgb(90, 205, 205, 205)));

    /// <summary>Exageros verticais oferecidos no perfil (Shift+roda percorre a lista).</summary>
    private static readonly double[] Exaggerations = { 1, 2, 2.5, 5, 10, 20, 25, 50, 100, 200 };

    /// <summary>O que a cor dos tubos mostra no monitor.</summary>
    private enum ColorMode
    {
        Rede,
        Lamina,
        Velocidade,
        Recobrimento,
        Declividade
    }

    /// <summary>Area clicavel do perfil: retangulo (estrutura) ou segmento com tolerancia (tubo).</summary>
    private sealed record ProfileHit(string Handle, string Text, SW.Rect Box, SW.Point A, SW.Point B, double Tol);

    private ColorMode _colorMode = ColorMode.Rede;
    private double _vScale, _vCx, _vCy, _vOx, _vOy;
    private double _zoom = 1;
    private P2 _pan = new(0, 0);
    private SW.Point? _dragFrom;
    private SW.Point _pressAt;
    private bool _dragged;
    private bool _profileShown;
    private bool _renderQueued;
    private string? _hover;
    private WM.DrawingContext? _dc;
    private double _dip = 1;
    private readonly Dictionary<(WM.Brush, double, bool), WM.Pen> _pens = new();

    // Estado do perfil: exagero (0 = automatico), caminho mostrado e areas clicaveis.
    private double _profileExag;
    private double _exagNow = 1;
    private string? _profileStart;
    private string? _profileNet;
    private readonly HashSet<string> _profileMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProfileHit> _profileHits = new();

    private static WM.Brush Frozen(WM.Brush b)
    {
        b.Freeze();
        return b;
    }

    private P2 ToWorld(SW.Point p) => new(_vCx + ((p.X - _vOx) / _vScale), _vCy - ((p.Y - _vOy) / _vScale));

    private SW.Point ToScreen(P2 p) => new(_vOx + ((p.X - _vCx) * _vScale), _vOy - ((p.Y - _vCy) * _vScale));

    /// <summary>Redesenho agrupado: varios pedidos (arrasto, roda, hover) viram um so por quadro.</summary>
    private void RequestRender()
    {
        if (_renderQueued)
            return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _renderQueued = false;
            _render?.Invoke();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    // ------------------------------------------------------------------
    // Mouse
    // ------------------------------------------------------------------

    private void OnGraphicMouseMove(object sender, WI.MouseEventArgs e)
    {
        if (_vScale <= 0)
            return;
        SW.Point p = e.GetPosition(_graphic);
        P2 w = ToWorld(p);
        _coords.Text = _profileShown
            ? $"dist {w.X,9:0.00} m   cota {w.Y / Math.Max(_exagNow, 1e-9),9:0.000}"
            : $"E {w.X,12:0.000}   N {w.Y,12:0.000}";
        if (_dragFrom is SW.Point f && (e.LeftButton == WI.MouseButtonState.Pressed || e.MiddleButton == WI.MouseButtonState.Pressed))
        {
            if (Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) > 3)
                _dragged = true;
            _pan = new P2(_pan.X - ((p.X - f.X) / _vScale), _pan.Y + ((p.Y - f.Y) / _vScale));
            _dragFrom = p;
            RequestRender();
            return;
        }

        (string? handle, string text) = _profileShown ? UnderProfile(p) : Under(w);
        if (_profileShown && handle is null && _profileHits.Count == 0)
            return;
        _graphicInfo.Text = text;
        if (handle != _hover)
        {
            _hover = handle;
            RequestRender();
        }
    }

    private void OnGraphicWheel(object sender, WI.MouseWheelEventArgs e)
    {
        if (_vScale <= 0)
            return;

        // Shift+roda no perfil: troca o exagero vertical (como a lista de escalas do Project Explorer).
        if (_profileShown && (WI.Keyboard.Modifiers & WI.ModifierKeys.Shift) != 0)
        {
            double now = _profileExag > 0 ? _profileExag : _exagNow;
            int i = Array.FindIndex(Exaggerations, x => x >= now - 1e-9);
            if (i < 0)
                i = Exaggerations.Length - 1;
            i = Math.Clamp(e.Delta > 0 ? i + 1 : i - 1, 0, Exaggerations.Length - 1);
            _profileExag = Exaggerations[i];
            _pan = new P2(_pan.X, 0);
            RequestRender();
            e.Handled = true;
            return;
        }

        SW.Point p = e.GetPosition(_graphic);
        P2 before = ToWorld(p);
        double factor = e.Delta > 0 ? 1.25 : 0.8;
        double zoom = Math.Clamp(_zoom * factor, 0.2, 500);
        factor = zoom / _zoom;
        _zoom = zoom;

        // O ponto sob o cursor fica parado: centro novo = ponto - (ponto - centro) / fator.
        double cx = before.X - ((before.X - _vCx) / factor), cy = before.Y - ((before.Y - _vCy) / factor);
        _pan = new P2(_pan.X + (cx - _vCx), _pan.Y + (cy - _vCy));
        RequestRender();
    }

    private void OnGraphicMouseDown(object sender, WI.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _zoom = 1;
            _pan = new P2(0, 0);
            if (_profileShown && (WI.Keyboard.Modifiers & WI.ModifierKeys.Shift) != 0)
                _profileExag = 0;
            RequestRender();
            return;
        }

        _dragFrom = e.GetPosition(_graphic);
        _pressAt = _dragFrom.Value;
        _dragged = false;
        _graphic.CaptureMouse();
    }

    private void OnGraphicMouseUp(object sender, WI.MouseButtonEventArgs e)
    {
        bool click = _dragFrom is not null && !_dragged && e.ChangedButton == WI.MouseButton.Left;
        _dragFrom = null;
        _graphic.ReleaseMouseCapture();
        if (!click || _vScale <= 0)
            return;

        // Clique simples (planta ou perfil) seleciona a peca na lista (troca de categoria se precisar).
        SW.Point at = e.GetPosition(_graphic);
        (string? handle, _) = _profileShown ? UnderProfile(at) : Under(ToWorld(at));
        if (handle is not null)
            ShowInPanel(handle);
    }

    private string StructureText(StructureInfo s) =>
        $"{s.Name}: tampa {N(s.Rim, 3)}  fundo {N(s.Sump, 3)}  prof {N(s.Depth, 2)} m  ({s.Family} / {s.Size})" +
        (double.IsNaN(s.Hgl) ? "" : $"  HGL {N(s.Hgl, 3)}");

    private string PipeText(PipeInfo p)
    {
        HydroResult? r = ResultOf(p.Handle);
        return $"{p.Name}: D {Mm(p.InnerDiameter)} mm  L {N(p.Length2D, 2)} m  i {Pct(p.Slope)}  recob. {N(p.MinCoverActual, 2)} m" +
            (r is null ? "" : $"  Q {N(r.Flow, 4)} m3/s  y/D {N(r.DepthRatio * 100, 0)}%  V {N(r.Velocity, 2)} m/s");
    }

    /// <summary>O que esta sob o cursor na planta: handle e texto da barra.</summary>
    private (string? Handle, string Text) Under(P2 w)
    {
        NetworkInfo? net = Net;
        if (net is null || _vScale <= 0)
            return (null, "");
        double tol = 8 / _vScale;
        StructureInfo? s = null;
        double best = tol;
        foreach (StructureInfo x in net.Structures)
        {
            double d = P2.Dist(x.Point, w);
            if (d < best)
            {
                best = d;
                s = x;
            }
        }

        if (s is not null)
            return (s.Handle, StructureText(s));

        PipeInfo? p = null;
        best = tol;
        foreach (PipeInfo x in net.Pipes)
        {
            double d = DistToSeg(w, x.Start, x.End);
            if (d < best)
            {
                best = d;
                p = x;
            }
        }

        return p is null ? (null, "") : (p.Handle, PipeText(p));
    }

    /// <summary>O que esta sob o cursor no perfil (estruturas tem prioridade sobre tubos).</summary>
    private (string? Handle, string Text) UnderProfile(SW.Point p)
    {
        foreach (ProfileHit h in _profileHits)
        {
            if (!h.Box.IsEmpty && h.Box.Contains(p))
                return (h.Handle, h.Text);
        }

        ProfileHit? best = null;
        double bd = double.MaxValue;
        foreach (ProfileHit h in _profileHits)
        {
            if (!h.Box.IsEmpty)
                continue;
            double d = DistToSeg(new P2(p.X, p.Y), new P2(h.A.X, h.A.Y), new P2(h.B.X, h.B.Y));
            if (d <= h.Tol && d < bd)
            {
                bd = d;
                best = h;
            }
        }

        return best is null ? (null, "") : (best.Handle, best.Text);
    }

    private static double DistToSeg(P2 p, P2 a, P2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = (dx * dx) + (dy * dy);
        double t = len2 < 1e-12 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / len2, 0, 1);
        return P2.Dist(p, new P2(a.X + (t * dx), a.Y + (t * dy)));
    }

    private static double NiceStep(double raw)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
        return new[] { 1.0, 2.0, 5.0, 10.0 }.Select(k => k * mag).First(v => v >= raw);
    }

    // ------------------------------------------------------------------
    // Primitivas (DrawingContext)
    // ------------------------------------------------------------------

    private bool Begin()
    {
        _dc?.Close();
        _dc = _graphic.Drawing.Open();
        try
        {
            _dip = WM.VisualTreeHelper.GetDpi(_graphic).PixelsPerDip;
        }
        catch
        {
            _dip = 1;
        }

        ResetLabels();
        return _graphic.ActualWidth >= 20 && _graphic.ActualHeight >= 20;
    }

    private void End()
    {
        _dc?.Close();
        _dc = null;
        _graphic.InvalidateVisual();
    }

    private WM.Pen Pen(WM.Brush brush, double thickness, bool dashed = false)
    {
        thickness = Math.Round(thickness * 4) / 4;
        if (_pens.TryGetValue((brush, thickness, dashed), out WM.Pen? pen))
            return pen;
        pen = new WM.Pen(brush, thickness) { StartLineCap = WM.PenLineCap.Round, EndLineCap = WM.PenLineCap.Round, LineJoin = WM.PenLineJoin.Round };
        if (dashed)
            pen.DashStyle = new WM.DashStyle(new double[] { 3, 2.5 }, 0);
        pen.Freeze();
        if (_pens.Count > 400)
            _pens.Clear();
        _pens[(brush, thickness, dashed)] = pen;
        return pen;
    }

    private void Line(SW.Point a, SW.Point b, WM.Brush brush, double thickness, bool dashed = false) =>
        _dc?.DrawLine(Pen(brush, thickness, dashed), a, b);

    private void Poly(IReadOnlyList<SW.Point> pts, WM.Brush? stroke, double thickness, WM.Brush? fill = null, bool closed = false, bool dashed = false)
    {
        if (_dc is null || pts.Count < 2)
            return;
        WM.StreamGeometry g = new();
        using (WM.StreamGeometryContext ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], fill is not null, closed);
            ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }

        g.Freeze();
        _dc.DrawGeometry(fill, stroke is null ? null : Pen(stroke, thickness, dashed), g);
    }

    private void Circle(SW.Point c, double r, WM.Brush? stroke, double thickness, WM.Brush? fill) =>
        _dc?.DrawEllipse(fill, stroke is null ? null : Pen(stroke, thickness), c, r, r);

    private void Rect(double x, double y, double w, double h, WM.Brush? fill, WM.Brush? stroke = null, double thickness = 1) =>
        _dc?.DrawRectangle(fill, stroke is null ? null : Pen(stroke, thickness), new SW.Rect(x, y, Math.Max(w, 0), Math.Max(h, 0)));

    private void Label(string text, double x, double y, double size = 11, WM.Brush? brush = null)
    {
        if (_dc is null || text.Length == 0)
            return;
        WM.FormattedText ft = new(text, CultureInfo.InvariantCulture, SW.FlowDirection.LeftToRight, MonoFace, size, brush ?? GraphicText, _dip);
        _dc.DrawText(ft, new SW.Point(x, y));
    }

    /// <summary>Texto simples (sem fundo) no centro horizontal dado, via cache.</summary>
    private void TextCentered(string text, double cx, double y, double size, WM.Brush brush, bool bold = false)
    {
        if (_dc is null || text.Length == 0)
            return;
        WM.FormattedText ft = Ft(text, size, brush, bold);
        _dc.DrawText(ft, new SW.Point(cx - (ft.Width / 2), y));
    }

    private const double RulerLeft = 44;
    private const double RulerBottom = 16;

    // ------------------------------------------------------------------
    // Planta
    // ------------------------------------------------------------------

    /// <summary>Planta: a rede ativa (ou a dada) com setas; item realcado; conjunto marcado.</summary>
    private void RenderPlan(NetworkInfo? net, string? highlight = null, HashSet<string>? marked = null)
    {
        net ??= Net;
        if (_profileView && net is not null && highlight is not null)
        {
            // O perfil fica no mesmo caminho quando o item clicado ja faz parte dele.
            string? start = _profileStart is not null && _profileNet == net.Handle && _profileMembers.Contains(highlight)
                ? _profileStart
                : net.StructureByHandle.ContainsKey(highlight) ? highlight : net.PipeByHandle.GetValueOrDefault(highlight)?.FromHandle;
            if (start is not null && net.StructureByHandle.ContainsKey(start))
            {
                RenderProfile(net, start, highlight);
                return;
            }
        }

        _profileShown = false;
        _profileHits.Clear();
        if (!Begin())
        {
            End();
            return;
        }

        double w = _graphic.ActualWidth, h = _graphic.ActualHeight;
        try
        {
            Bounds extents = Bounds.Empty;
            bool all = _category == "REDES" && _current?.Data is not NetworkInfo;
            List<NetworkInfo> shown = all ? _snap.Networks : net is null ? new List<NetworkInfo>() : new List<NetworkInfo> { net };
            foreach (NetworkInfo n in shown)
                extents = extents.Union(n.Bounds);
            if (_showCatchments)
            {
                foreach (CatchmentInfo c in _snap.Catchments)
                {
                    foreach (P2 pt in c.Boundary)
                        extents = extents.Add(pt);
                }
            }

            if (extents.IsEmpty)
            {
                _vScale = 0;
                Label("Nenhuma Pipe Network no desenho.", 10, 10);
                return;
            }

            SetupView(extents, w, h);
            double scale = _vScale;
            SW.Rect view = new(-50, -50, w + 100, h + 100);
            bool Visible(SW.Point a, SW.Point b) =>
                !(Math.Max(a.X, b.X) < view.Left || Math.Min(a.X, b.X) > view.Right || Math.Max(a.Y, b.Y) < view.Top || Math.Min(a.Y, b.Y) > view.Bottom);

            if (_showGrid)
                DrawGrid(w, h);

            HashSet<string> flagged = new(_violations.Select(v => v.Handle), StringComparer.OrdinalIgnoreCase);

            // Bacias.
            if (_showCatchments)
            {
                foreach (CatchmentInfo c in _snap.Catchments)
                {
                    bool hi = c.Handle == highlight;
                    if (c.Boundary.Count >= 3)
                        Poly(c.Boundary.Select(ToScreen).ToList(), hi ? HighlightBrush : CatchmentBrush, hi ? 2 : 1, CatchmentFill, true, true);

                    if (c.StructureHandle.Length > 0 && net?.StructureByHandle.GetValueOrDefault(c.StructureHandle) is StructureInfo target)
                        Line(ToScreen(c.Centroid), ToScreen(target.Point), hi ? HighlightBrush : CatchmentBrush, 1, true);
                }
            }

            // Tubos em lote (uma geometria por cor/espessura); realce e hover por baixo, direto.
            List<(PipeInfo P, SW.Point A, SW.Point B, double Len, bool Hi)> pipeLabels = new();
            foreach (NetworkInfo n in shown)
            {
                bool active = n.Handle == net?.Handle;
                foreach (PipeInfo p in n.Pipes)
                {
                    SW.Point a = ToScreen(p.UpPoint), b = ToScreen(p.DownPoint);
                    if (!Visible(a, b))
                        continue;
                    bool hi = p.Handle == highlight || marked?.Contains(p.Handle) == true;
                    bool hover = p.Handle == _hover;
                    WM.Brush brush = hi ? HighlightBrush : !active ? OtherNetwork : PipeColor(p);
                    double thickness = Math.Clamp(p.OuterDiameter * scale, 1.5, 8);
                    if (hi || hover)
                        Line(a, b, hover && !hi ? HoverBrush : HighlightBrush, thickness + 3.5);
                    BatchLine(a, b, brush, thickness);
                    if (_showArrows && active && p.Defined)
                        BatchArrows(a, b, brush, thickness);
                    double len = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
                    if (_showNames && active && len > 40)
                        pipeLabels.Add((p, a, b, len, hi || hover));
                }
            }

            FlushBatches();

            // Estruturas por cima; cada simbolo reserva o seu lugar para os rotulos nao o cobrirem.
            List<(StructureInfo S, SW.Point C, double R, bool Hi)> structLabels = new();
            foreach (NetworkInfo n in shown)
            {
                bool active = n.Handle == net?.Handle;
                foreach (StructureInfo s in n.Structures)
                {
                    SW.Point c = ToScreen(s.Point);
                    if (!view.Contains(c))
                        continue;
                    bool hi = s.Handle == highlight || marked?.Contains(s.Handle) == true;
                    bool hover = s.Handle == _hover;
                    double r = Math.Clamp(Math.Max(s.InnerDiameter, 0.6) * scale / 2, 3.5, 16);
                    WM.Brush brush = hi ? HighlightBrush : hover ? HoverBrush : active ? StructureBrush : OtherNetwork;
                    Circle(c, r, brush, hi || hover ? 2.6 : 1.5, GraphicBack);
                    if (s.Rules.Count > 0 && active)
                        Circle(c, Math.Max(r - 3, 1.2), null, 0, brush);
                    if (s.Warnings.Count > 0 || flagged.Contains(s.Handle))
                        Circle(new SW.Point(c.X + r, c.Y - r), 3, null, 0, T.Warn);
                    Occupy(new SW.Rect(c.X - r - 1, c.Y - r - 1, (2 * r) + 2, (2 * r) + 2));
                    if (_showNames && (active || hi || hover))
                        structLabels.Add((s, c, r, hi || hover));
                }
            }

            if (_showNames)
                DrawPlanLabels(structLabels, pipeLabels, scale);

            DrawRulers(w, h);
            if (_colorMode != ColorMode.Rede)
                DrawColorLegend(w);
            if (_hover is null)
            {
                _graphicInfo.Text = net is null ? "" : $"{net.Name}: {net.Structures.Count} estrutura(s), {net.Pipes.Count} tubo(s)" +
                    (all && _snap.Networks.Count > 1 ? $"  |  {_snap.Networks.Count} redes (ativa em branco)" : "") +
                    "  |  clique seleciona, roda = zoom, arrastar = mover, duplo clique = enquadrar";
            }
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Rotulos da planta, por prioridade: realcados (sempre), estruturas, tubos mais longos.
    /// Nenhum cobre outro nem um simbolo; com zoom, ganham dados (profundidade, D e i).
    /// </summary>
    private void DrawPlanLabels(List<(StructureInfo S, SW.Point C, double R, bool Hi)> structures,
        List<(PipeInfo P, SW.Point A, SW.Point B, double Len, bool Hi)> pipes, double scale)
    {
        bool detail = scale > 2.5;
        foreach ((StructureInfo s, SW.Point c, double r, bool hi) in structures.Where(x => x.Hi))
            LabelAround(detail ? $"{s.Name}  h {N(s.Depth, 2)}" : s.Name, c, r + 2, 11, HighlightBrush, force: true, bold: true, halo: HaloStrong);
        foreach ((PipeInfo p, SW.Point a, SW.Point b, double _, bool hi) in pipes.Where(x => x.Hi))
            LabelAlong($"{p.Name}  Ø{Mm(p.InnerDiameter)}  {Pct(p.Slope)}", a, b, 6, 10.5, HighlightBrush, force: true);

        int count = 0;
        foreach ((StructureInfo s, SW.Point c, double r, bool hi) in structures)
        {
            if (hi || count > 900)
                continue;
            if (LabelAround(detail ? $"{s.Name}  h {N(s.Depth, 2)}" : s.Name, c, r + 2, 10.5, GraphicText))
                count++;
        }

        count = 0;
        foreach ((PipeInfo p, SW.Point a, SW.Point b, double len, bool hi) in pipes.OrderByDescending(x => x.Len))
        {
            if (hi || count > 700)
                continue;
            WM.Brush brush = MutedText;
            bool done = len > 230 && LabelAlong($"{p.Name}  Ø{Mm(p.InnerDiameter)}  {Pct(p.Slope)}", a, b, 5, 10, brush);
            if (!done)
                done = LabelAlong(p.Name, a, b, 5, 10, brush);
            if (done)
                count++;
        }
    }

    /// <summary>Cor do tubo pelo modo escolhido em "Cor ▾" (verde atende, amarelo no limite, vermelho viola).</summary>
    private WM.Brush PipeColor(PipeInfo p)
    {
        if (_colorMode == ColorMode.Rede)
            return p.Defined ? PipeBrush : PipeUndefined;
        HydroResult? r = ResultOf(p.Handle);
        Calc.Criteria c = _settings.Criteria;
        switch (_colorMode)
        {
            case ColorMode.Lamina:
                if (r is null)
                    return NoDataBrush;
                return !r.Flows || r.DepthRatio > c.MaxDepthRatio ? BadBrush : r.DepthRatio > c.MaxDepthRatio * 0.9 ? WarnBrush : OkBrush;
            case ColorMode.Velocidade:
                if (r is null)
                    return NoDataBrush;
                return r.Velocity < c.MinVelocity || r.Velocity > c.MaxVelocity ? BadBrush
                    : r.Velocity < c.MinVelocity * 1.1 || r.Velocity > c.MaxVelocity * 0.9 ? WarnBrush : OkBrush;
            case ColorMode.Recobrimento:
                double cover = p.MinCoverActual;
                if (double.IsNaN(cover))
                    return NoDataBrush;
                return cover < c.MinCover ? BadBrush : cover < c.MinCover + 0.2 ? WarnBrush : OkBrush;
            case ColorMode.Declividade:
                double i = p.Slope;
                return i <= 0 || i < c.MinSlope || i > c.MaxSlope ? BadBrush : i < c.MinSlope * 1.2 ? WarnBrush : OkBrush;
            default:
                return PipeBrush;
        }
    }

    private void DrawColorLegend(double w)
    {
        string title = _colorMode switch
        {
            ColorMode.Lamina => "Lamina y/D",
            ColorMode.Velocidade => "Velocidade",
            ColorMode.Recobrimento => "Recobrimento",
            ColorMode.Declividade => "Declividade",
            _ => ""
        };
        double x = w - 170, y = 8;
        Rect(x - 6, y - 4, 166, 74, RulerBrush, GraphicGridMajor, 1);
        Label(title, x, y, 11, GraphicText);
        (WM.Brush b, string t)[] rows = { (OkBrush, "atende"), (WarnBrush, "perto do limite"), (BadBrush, "fora do criterio"), (NoDataBrush, "sem calculo") };
        for (int k = 0; k < rows.Length; k++)
        {
            Line(new SW.Point(x, y + 22 + (k * 12)), new SW.Point(x + 22, y + 22 + (k * 12)), rows[k].b, 4);
            Label(rows[k].t, x + 28, y + 15 + (k * 12), 10, GraphicText);
        }
    }

    private void SetupView(Bounds box, double w, double h) =>
        SetupView(box, new SW.Rect(RulerLeft, 0, Math.Max(w - RulerLeft, 1), Math.Max(h - RulerBottom, 1)), 0.90);

    /// <summary>Enquadra a caixa (mundo) no retangulo da tela, com zoom e deslocamento correntes.</summary>
    private void SetupView(Bounds box, SW.Rect area, double fill)
    {
        _vScale = fill * Math.Min(area.Width / Math.Max(box.MaxX - box.MinX, 1e-6), area.Height / Math.Max(box.MaxY - box.MinY, 1e-6)) * _zoom;
        if (double.IsInfinity(_vScale) || _vScale > 1e6)
            _vScale = 1;
        _vCx = ((box.MinX + box.MaxX) / 2.0) + _pan.X;
        _vCy = ((box.MinY + box.MaxY) / 2.0) + _pan.Y;
        _vOx = area.X + (area.Width / 2.0);
        _vOy = area.Y + (area.Height / 2.0);
    }

    private void DrawGrid(double w, double h)
    {
        double dw = w - RulerLeft, dh = h - RulerBottom;
        double step = NiceStep(Math.Max(dw, dh) / _vScale / 8.0);
        double x0 = _vCx - ((_vOx - RulerLeft) / _vScale), x1 = _vCx + ((w - _vOx) / _vScale);
        double y0 = _vCy - ((dh - _vOy) / _vScale), y1 = _vCy + (_vOy / _vScale);
        for (double x = Math.Ceiling(x0 / step) * step; x <= x1; x += step)
        {
            double px = Math.Round(ToScreen(new P2(x, 0)).X) + 0.5;
            if (px >= RulerLeft)
                Line(new SW.Point(px, 0), new SW.Point(px, dh), GraphicGridMajor, 1);
        }

        for (double y = Math.Ceiling(y0 / step) * step; y <= y1; y += step)
        {
            double py = Math.Round(ToScreen(new P2(0, y)).Y) + 0.5;
            if (py <= dh)
                Line(new SW.Point(RulerLeft, py), new SW.Point(w, py), GraphicGridMajor, 1);
        }
    }

    private void DrawRulers(double w, double h)
    {
        double dh = h - RulerBottom;
        Rect(0, 0, RulerLeft, dh, RulerBrush);
        Rect(0, dh, w, RulerBottom, RulerBrush);
        double step = NiceStep(Math.Max(w, h) / _vScale / 6.0);
        double x0 = _vCx - ((_vOx - RulerLeft) / _vScale), x1 = _vCx + ((w - _vOx) / _vScale);
        double y0 = _vCy - ((dh - _vOy) / _vScale), y1 = _vCy + (_vOy / _vScale);
        for (double x = Math.Ceiling(x0 / step) * step; x <= x1; x += step)
        {
            double px = ToScreen(new P2(x, 0)).X;
            if (px > RulerLeft)
                Label(x.ToString("0", CultureInfo.InvariantCulture), px + 2, dh + 1, 9, GraphicText);
        }

        for (double y = Math.Ceiling(y0 / step) * step; y <= y1; y += step)
        {
            double py = ToScreen(new P2(0, y)).Y;
            if (py < dh)
                Label(y.ToString("0", CultureInfo.InvariantCulture), 2, py - 6, 9, GraphicText);
        }
    }

    // ------------------------------------------------------------------
    // Perfil e IDF
    // ------------------------------------------------------------------

    private const double ProfileTop = 22;
    private const double BandRow = 15;

    private static double Ground(StructureInfo s) => double.IsNaN(s.SurfaceElevation) ? s.Rim : s.SurfaceElevation;

    /// <summary>
    /// Perfil do caminho de montante para jusante a partir de uma estrutura, no modelo da
    /// "Network View" do Project Explorer: grade, solo sombreado, faixa de recobrimento minimo,
    /// tubos com parede/interior/lamina, linha piezometrica, estruturas na largura real, nomes no
    /// topo com linha de chamada e faixa de dados embaixo (distancia, terreno, tampa, fundo,
    /// profundidade). Shift+roda muda o exagero vertical; hover e clique como na planta.
    /// </summary>
    private void RenderProfile(NetworkInfo net, string startHandle, string? highlight)
    {
        _profileShown = true;
        _profileHits.Clear();
        if (!Begin())
        {
            End();
            return;
        }

        try
        {
            double w = _graphic.ActualWidth, h = _graphic.ActualHeight;
            List<PipeInfo> path = net.PathDownstream(startHandle);
            StructureInfo? first = net.StructureByHandle.GetValueOrDefault(startHandle);
            if (first is null)
                return;

            if (_profileStart != startHandle || _profileNet != net.Handle)
            {
                _profileStart = startHandle;
                _profileNet = net.Handle;
                _profileMembers.Clear();
                _profileMembers.Add(startHandle);
                foreach (PipeInfo p in path)
                {
                    _profileMembers.Add(p.Handle);
                    if (p.ToHandle is not null)
                        _profileMembers.Add(p.ToHandle);
                }
            }

            List<(double S, StructureInfo St)> nodes = new() { (0, first) };
            List<double> pipeStart = new();
            double s = 0;
            foreach (PipeInfo p in path)
            {
                pipeStart.Add(s);
                s += p.Length2D;
                if (p.ToHandle is string to && net.StructureByHandle.GetValueOrDefault(to) is StructureInfo ts)
                    nodes.Add((s, ts));
            }

            double total = Math.Max(s, 1);
            double minCover = _settings.Criteria.MinCover;
            double minZ = Math.Min(nodes.Min(n => n.St.Sump), path.Count == 0 ? double.MaxValue : path.Min(p => Math.Min(p.UpInvert, p.DownInvert)));
            double maxZ = nodes.Max(n => Math.Max(n.St.Rim, Ground(n.St)));
            double range = Math.Max(maxZ - minZ, 0.5);

            // Faixa de dados embaixo: some em monitores baixos.
            string[] rows = h >= 300 ? new[] { "Dist.", "Terreno", "Tampa", "Fundo", "Prof." } : h >= 200 ? new[] { "Dist.", "Tampa", "Fundo" } : Array.Empty<string>();
            double band = rows.Length * BandRow;
            SW.Rect plot = new(RulerLeft, ProfileTop, Math.Max(w - RulerLeft - 6, 10), Math.Max(h - ProfileTop - band - 4, 10));

            // Exagero automatico: o desnivel ocupa ~1/4 da largura, arredondado para a lista.
            double auto = Math.Max(1, total / range * (plot.Height / plot.Width) / 1.2);
            auto = Exaggerations.LastOrDefault(x => x <= auto, 1);
            double exag = _profileExag > 0 ? _profileExag : auto;
            _exagNow = exag;

            Bounds box = new(0, (minZ - (range * 0.08)) * exag, total, (maxZ + (range * 0.05)) * exag);
            SetupView(box.Inflate(total * 0.02), plot, 0.97);
            SW.Point P(double dist, double z) => ToScreen(new P2(dist, z * exag));

            _dc!.PushClip(new WM.RectangleGeometry(plot));

            // Grade: cotas e distancias com passo "redondo".
            double stepZ = NiceStep(range / Math.Max(plot.Height / 45, 2));
            double zLo = ToWorld(new SW.Point(0, plot.Bottom)).Y / exag, zHi = ToWorld(new SW.Point(0, plot.Top)).Y / exag;
            if (_showGrid)
            {
                for (double z = Math.Floor(zLo / stepZ) * stepZ; z <= zHi; z += stepZ)
                {
                    double py = Math.Round(P(0, z).Y) + 0.5;
                    Line(new SW.Point(plot.Left, py), new SW.Point(plot.Right, py), GraphicGrid, 1);
                }

                double dLo = ToWorld(new SW.Point(plot.Left, 0)).X, dHi = ToWorld(new SW.Point(plot.Right, 0)).X;
                double stepD = NiceStep((dHi - dLo) / Math.Max(plot.Width / 90, 2));
                for (double d = Math.Ceiling(dLo / stepD) * stepD; d <= dHi; d += stepD)
                {
                    double px = Math.Round(P(d, 0).X) + 0.5;
                    Line(new SW.Point(px, plot.Top), new SW.Point(px, plot.Bottom), GraphicGrid, 1);
                }
            }

            // Solo sombreado abaixo do terreno e faixa de recobrimento minimo.
            List<SW.Point> ground = nodes.Select(n => P(n.S, Ground(n.St))).ToList();
            if (ground.Count >= 2)
            {
                List<SW.Point> soil = new(ground) { new SW.Point(ground[^1].X, plot.Bottom + 2), new SW.Point(ground[0].X, plot.Bottom + 2) };
                Poly(soil, null, 0, SoilFill, true);
                if (minCover > 0)
                {
                    List<SW.Point> zone = new(ground);
                    zone.AddRange(nodes.Select(n => P(n.S, Ground(n.St) - minCover)).Reverse());
                    Poly(zone, null, 0, CoverZoneFill, true);
                    Poly(nodes.Select(n => P(n.S, Ground(n.St) - minCover)).ToList(), CoverZoneLine, 1, null, false, true);
                }

                Poly(ground, GroundBrush, 1.8);
            }

            // Ordenadas: da faixa dos nomes ate a tampa de cada estrutura.
            foreach ((double d, StructureInfo st) in nodes)
            {
                SW.Point top = P(d, st.Rim);
                Line(new SW.Point(top.X, plot.Top), top, OrdinateBrush, 1, true);
            }

            // Tubos: parede, interior, lamina calculada, linha piezometrica e seta do fluxo.
            for (int k = 0; k < path.Count; k++)
            {
                PipeInfo p = path[k];
                double x0 = pipeStart[k], x1 = x0 + p.Length2D;
                bool hi = p.Handle == highlight;
                bool hover = p.Handle == _hover;
                double hIn = p.InnerHeight > 0 ? p.InnerHeight : p.InnerDiameter;
                double wall = Math.Max((p.OuterDiameter - p.InnerDiameter) / 2, 0);
                double i0 = p.UpInvert, i1 = p.DownInvert;
                WM.Brush outline = hi ? HighlightBrush : hover ? HoverBrush : PipeColor(p);

                if (wall > 0)
                    Poly(new[] { P(x0, i0 - wall), P(x1, i1 - wall), P(x1, i1 + hIn + wall), P(x0, i0 + hIn + wall) }, null, 0, PipeWallFill, true);
                SW.Point a = P(x0, i0), b = P(x1, i1), a2 = P(x0, i0 + hIn), b2 = P(x1, i1 + hIn);
                Poly(new[] { a, b, b2, a2 }, outline, hi || hover ? 2.2 : 1.2, PipeInsideFill, true);

                HydroResult? r = ResultOf(p.Handle);
                if (r is not null && r.DepthRatio > 0)
                {
                    double y = Math.Min(r.DepthRatio, 1) * hIn;
                    Poly(new[] { a, b, P(x1, i1 + y), P(x0, i0 + y) }, null, 0, WaterFill, true);
                }

                if (!double.IsNaN(p.HglUp) && !double.IsNaN(p.HglDown))
                {
                    double hu = p.StartIsUpstream ? p.HglUp : p.HglDown, hd = p.StartIsUpstream ? p.HglDown : p.HglUp;
                    Line(P(x0, hu), P(x1, hd), HglBrush, 1.4, true);
                }

                SW.Point ca = new((a.X + a2.X) / 2, (a.Y + a2.Y) / 2), cb = new((b.X + b2.X) / 2, (b.Y + b2.Y) / 2);
                if (_showArrows)
                    BatchArrows(ca, cb, outline, Math.Clamp(Math.Abs(a2.Y - a.Y) / 2, 2, 5));
                double tol = Math.Max(Math.Abs(a2.Y - a.Y) / 2 + 3, 5);
                _profileHits.Add(new ProfileHit(p.Handle, PipeText(p), SW.Rect.Empty, ca, cb, tol));
            }

            FlushBatches();

            // Estruturas na largura real (minimo legivel), tampa marcada.
            foreach ((double d, StructureInfo st) in nodes)
            {
                bool hi = st.Handle == highlight;
                bool hover = st.Handle == _hover;
                double half = Math.Max(Math.Max(st.InnerDiameter, 0.6) * _vScale / 2, 4);
                SW.Point top = P(d, st.Rim), bottom = P(d, st.Sump);
                SW.Rect body = new(top.X - half, top.Y, half * 2, Math.Max(bottom.Y - top.Y, 2));
                WM.Brush edge = hi ? HighlightBrush : hover ? HoverBrush : StructureBrush;
                Rect(body.X, body.Y, body.Width, body.Height, StructureFill, edge, hi || hover ? 2.4 : 1.3);
                Line(new SW.Point(body.Left - 3, top.Y), new SW.Point(body.Right + 3, top.Y), edge, 3);
                _profileHits.Insert(0, new ProfileHit(st.Handle, StructureText(st), SW.Rect.Inflate(body, 3, 3), default, default, 0));
            }

            // Rotulo de cada tubo acima da geratriz superior, se couber.
            if (_showNames)
            {
                for (int k = 0; k < path.Count; k++)
                {
                    PipeInfo p = path[k];
                    double x0 = pipeStart[k], x1 = x0 + p.Length2D;
                    double hIn = p.InnerHeight > 0 ? p.InnerHeight : p.InnerDiameter;
                    double wall = Math.Max((p.OuterDiameter - p.InnerDiameter) / 2, 0);
                    SW.Point a = P(x0, p.UpInvert + hIn + wall), b = P(x1, p.DownInvert + hIn + wall);
                    WM.Brush brush = p.Handle == highlight ? HighlightBrush : GraphicText;
                    if (!LabelAlong($"{p.Name}  Ø{Mm(p.InnerDiameter)}  i {Pct(p.Slope)}  L {N(p.Length2D, 1)}", a, b, 4, 10, brush))
                        LabelAlong($"Ø{Mm(p.InnerDiameter)}  {Pct(p.Slope)}", a, b, 4, 9.5, brush);
                }
            }

            _dc.Pop();

            // Faixa dos nomes no topo (com a ordenada como linha de chamada).
            Rect(0, 0, w, ProfileTop, RulerBrush);
            if (_showNames)
            {
                foreach ((double d, StructureInfo st) in nodes.OrderByDescending(n => n.St.Handle == highlight))
                {
                    double x = P(d, 0).X;
                    if (x < plot.Left - 20 || x > plot.Right + 20)
                        continue;
                    bool hi = st.Handle == highlight;
                    WM.FormattedText ft = Ft(st.Name, 10.5, hi ? HighlightBrush : GraphicText, true);
                    LabelAt(st.Name, x - ((ft.Width + 6) / 2), 3, 10.5, hi ? HighlightBrush : GraphicText, force: hi, bold: true);
                }
            }

            // Regua de cotas a esquerda.
            Rect(0, ProfileTop, RulerLeft, plot.Height + 4, RulerBrush);
            for (double z = Math.Floor(zLo / stepZ) * stepZ; z <= zHi; z += stepZ)
            {
                double py = P(0, z).Y;
                if (py > plot.Top + 4 && py < plot.Bottom - 4)
                    Label(z.ToString(stepZ < 1 ? "0.0" : "0", CultureInfo.InvariantCulture), 2, py - 6, 9, GraphicText);
            }

            // Faixa de dados.
            if (rows.Length > 0)
            {
                double y0 = h - band;
                Rect(0, y0 - 4, w, band + 4, RulerBrush);
                for (int k = 0; k < rows.Length; k++)
                {
                    double ry = y0 + (k * BandRow);
                    Line(new SW.Point(0, Math.Round(ry - 1) + 0.5), new SW.Point(w, Math.Round(ry - 1) + 0.5), GraphicGrid, 1);
                    Label(rows[k], 3, ry, 9, MutedText);
                }

                Line(new SW.Point(RulerLeft - 0.5, y0 - 4), new SW.Point(RulerLeft - 0.5, h), GraphicGrid, 1);
                foreach ((double d, StructureInfo st) in nodes)
                {
                    double x = P(d, 0).X;
                    if (x < plot.Left - 2 || x > plot.Right + 2)
                        continue;
                    bool hi = st.Handle == highlight;
                    WM.Brush brush = hi ? HighlightBrush : GraphicText;
                    for (int k = 0; k < rows.Length; k++)
                    {
                        string v = rows[k] switch
                        {
                            "Dist." => N(d, 2),
                            "Terreno" => N(Ground(st), 2),
                            "Tampa" => N(st.Rim, 2),
                            "Fundo" => N(st.Sump, 2),
                            _ => N(st.Depth, 2)
                        };
                        WM.FormattedText ft = Ft(v, 9, brush);
                        LabelAt(v, x - ((ft.Width + 6) / 2), y0 + (k * BandRow), 9, brush, force: hi);
                    }
                }
            }

            if (_hover is null)
            {
                _graphicInfo.Text = $"Perfil de {first.Name} para jusante: {path.Count} trecho(s), {N(total, 1)} m, exagero {N(exag, 1)}x" +
                    (_profileExag > 0 ? "" : " (auto)") + "  |  Shift+roda = exagero, Shift+duplo clique = exagero automatico, clique seleciona";
            }
        }
        finally
        {
            End();
        }
    }

    /// <summary>Curva IDF corrente na categoria Calculo.</summary>
    private void RenderIdf()
    {
        _profileShown = true;
        _profileHits.Clear();
        _exagNow = 1;
        if (!Begin())
        {
            End();
            return;
        }

        try
        {
            double w = _graphic.ActualWidth, h = _graphic.ActualHeight;
            HydroSettings s = HydroStore.FromFields(_settings, k => Text(k));
            double[] trs = { 2, 5, 10, 25, 50 };
            double tMax = 120;
            double iMax = Math.Max(1, trs.Max(tr => s.Idf.Intensity(5, tr)));
            Bounds box = new(0, 0, tMax, iMax);
            SetupView(box, w, h);
            double dh = h - RulerBottom;
            SW.Point P(double t, double i) => ToScreen(new P2(t, i));
            WM.Brush[] brushes = { Brush(150, 150, 150), Brush(120, 220, 235), Brush(110, 210, 110), Brush(244, 196, 64), Brush(240, 120, 120) };
            for (int k = 0; k < trs.Length; k++)
            {
                List<SW.Point> pts = new();
                for (double t = 5; t <= tMax; t += 2.5)
                    pts.Add(P(t, s.Idf.Intensity(t, trs[k])));
                bool current = trs[k] == s.ReturnPeriod;
                Poly(pts, current ? HighlightBrush : brushes[k], current ? 2.5 : 1.2);
                SW.Point end = P(tMax, s.Idf.Intensity(tMax, trs[k]));
                Label($"Tr {trs[k]}", end.X - 40, end.Y - 14, 10, brushes[k]);
            }

            Rect(0, 0, RulerLeft, dh, RulerBrush);
            Rect(0, dh, w, RulerBottom, RulerBrush);
            for (double t = 0; t <= tMax; t += 20)
                Label(t.ToString("0", CultureInfo.InvariantCulture), P(t, 0).X + 2, dh + 1, 9, GraphicText);
            double stepI = NiceStep(iMax / 6);
            for (double i = 0; i <= iMax; i += stepI)
                Label(i.ToString("0", CultureInfo.InvariantCulture), 2, P(0, i).Y - 6, 9, GraphicText);
            _graphicInfo.Text = $"IDF {s.Idf.Name}: i = {s.Idf.Formula()}  (t em min, i em mm/h; Tr {s.ReturnPeriod} anos em destaque)";
            _coords.Text = "";
        }
        finally
        {
            End();
        }
    }

    /// <summary>Menu "Cor ▾" da barra do monitor.</summary>
    private void OpenColorMenu(SW.FrameworkElement anchor)
    {
        System.Windows.Controls.ContextMenu menu = new() { PlacementTarget = anchor };
        foreach ((ColorMode mode, string text) in new[]
        {
            (ColorMode.Rede, "Rede (sentido definido / indefinido)"),
            (ColorMode.Lamina, "Lamina y/D (calculo)"),
            (ColorMode.Velocidade, "Velocidade (calculo)"),
            (ColorMode.Recobrimento, "Recobrimento"),
            (ColorMode.Declividade, "Declividade")
        })
        {
            System.Windows.Controls.MenuItem mi = new() { Header = text, IsCheckable = true, IsChecked = _colorMode == mode };
            ColorMode m = mode;
            mi.Click += (_, _) =>
            {
                _colorMode = m;
                RequestRender();
            };
            menu.Items.Add(mi);
        }

        menu.IsOpen = true;
    }
}
