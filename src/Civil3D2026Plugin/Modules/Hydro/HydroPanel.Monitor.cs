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
    private static readonly WM.Brush GroundBrush = Brush(110, 210, 110);
    private static readonly WM.Brush OtherNetwork = Brush(130, 135, 140);
    private static readonly WM.Brush RulerBrush = Brush(44, 46, 50);
    private static readonly WM.Brush OkBrush = Brush(110, 210, 110);
    private static readonly WM.Brush WarnBrush = Brush(244, 196, 64);
    private static readonly WM.Brush BadBrush = Brush(240, 70, 70);
    private static readonly WM.Brush NoDataBrush = Brush(140, 145, 150);
    private static readonly WM.Typeface MonoFace = new("Consolas");

    /// <summary>O que a cor dos tubos mostra no monitor.</summary>
    private enum ColorMode
    {
        Rede,
        Lamina,
        Velocidade,
        Recobrimento,
        Declividade
    }

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
        _coords.Text = _profileShown ? $"dist {w.X,9:0.00} m   cota {w.Y,9:0.000}" : $"E {w.X,12:0.000}   N {w.Y,12:0.000}";
        if (_dragFrom is SW.Point f && (e.LeftButton == WI.MouseButtonState.Pressed || e.MiddleButton == WI.MouseButtonState.Pressed))
        {
            if (Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) > 3)
                _dragged = true;
            _pan = new P2(_pan.X - ((p.X - f.X) / _vScale), _pan.Y + ((p.Y - f.Y) / _vScale));
            _dragFrom = p;
            RequestRender();
            return;
        }

        if (_profileShown)
            return;
        (string? handle, string text) = Under(w);
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
        if (!click || _profileShown || _vScale <= 0)
            return;

        // Clique simples no monitor seleciona a peca na lista (troca de categoria se precisar).
        (string? handle, _) = Under(ToWorld(e.GetPosition(_graphic)));
        if (handle is not null)
            ShowInPanel(handle);
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
            return (s.Handle, $"{s.Name}: tampa {N(s.Rim, 3)}  fundo {N(s.Sump, 3)}  prof {N(s.Depth, 2)} m  ({s.Family} / {s.Size})");

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

        if (p is not null)
        {
            HydroResult? r = ResultOf(p.Handle);
            return (p.Handle, $"{p.Name}: D {Mm(p.InnerDiameter)} mm  L {N(p.Length2D, 2)} m  i {Pct(p.Slope)}  recob. {N(p.MinCoverActual, 2)} m" +
                (r is null ? "" : $"  Q {N(r.Flow, 4)} m3/s  y/D {N(r.DepthRatio * 100, 0)}%  V {N(r.Velocity, 2)} m/s"));
        }

        return (null, "");
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
            string? start = net.StructureByHandle.ContainsKey(highlight) ? highlight
                : net.PipeByHandle.GetValueOrDefault(highlight)?.FromHandle;
            if (start is not null)
            {
                RenderProfile(net, start, highlight);
                return;
            }
        }

        _profileShown = false;
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

                    if (_showNames && scale * Math.Sqrt(Math.Max(c.Area, 1)) > 30)
                        Label(c.Name, ToScreen(c.Centroid).X - 10, ToScreen(c.Centroid).Y - 7, 10, CatchmentBrush);
                }
            }

            // Tubos.
            int namesDrawn = 0;
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
                        Line(a, b, hover && !hi ? HoverBrush : HighlightBrush, thickness + 3);
                    Line(a, b, brush, thickness);
                    if (_showArrows && active && p.Defined)
                        Arrows(a, b, brush, thickness);
                    double len = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
                    if (_showNames && len > 60 && namesDrawn < 600)
                    {
                        namesDrawn++;
                        SW.Point m = new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                        Label(p.Name, m.X + 5, m.Y + 3, 10, hi ? HighlightBrush : GraphicText);
                    }
                }
            }

            // Estruturas por cima.
            namesDrawn = 0;
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
                    if (_showNames && (scale > 0.3 || hi || hover) && namesDrawn < 800)
                    {
                        namesDrawn++;
                        Label(s.Name, c.X + r + 2, c.Y - r - 6, 10, hi ? HighlightBrush : GraphicText);
                    }
                }
            }

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
    /// Setas do fluxo: cabecas cheias com contorno escuro (legiveis sobre qualquer fundo), uma a
    /// cada ~110 px ao longo do tubo e sempre ao menos uma no meio; tamanho acompanha a espessura.
    /// </summary>
    private void Arrows(SW.Point a, SW.Point b, WM.Brush brush, double thickness)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt((dx * dx) + (dy * dy));
        double size = Math.Clamp(thickness * 2.2, 7, 14);
        if (len < size * 2.2)
            return;
        dx /= len;
        dy /= len;
        int count = Math.Max(1, (int)(len / 110));
        for (int i = 1; i <= count; i++)
        {
            double t = len * i / (count + 1);
            SW.Point tip = new(a.X + (dx * (t + (size / 2))), a.Y + (dy * (t + (size / 2))));
            SW.Point back = new(tip.X - (dx * size), tip.Y - (dy * size));
            double half = size * 0.55;
            SW.Point[] tri =
            {
                tip,
                new(back.X - (dy * half), back.Y + (dx * half)),
                new(back.X + (dx * size * 0.25), back.Y + (dy * size * 0.25)),
                new(back.X + (dy * half), back.Y - (dx * half))
            };
            Poly(tri, GraphicBack, 1.2, brush, true);
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

    private void SetupView(Bounds box, double w, double h)
    {
        double dw = w - RulerLeft, dh = h - RulerBottom;
        _vScale = 0.90 * Math.Min(dw / Math.Max(box.MaxX - box.MinX, 1e-6), dh / Math.Max(box.MaxY - box.MinY, 1e-6)) * _zoom;
        if (double.IsInfinity(_vScale) || _vScale > 1e6)
            _vScale = 1;
        _vCx = ((box.MinX + box.MaxX) / 2.0) + _pan.X;
        _vCy = ((box.MinY + box.MaxY) / 2.0) + _pan.Y;
        _vOx = RulerLeft + (dw / 2.0);
        _vOy = dh / 2.0;
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

    /// <summary>Perfil do caminho de montante para jusante a partir de uma estrutura.</summary>
    private void RenderProfile(NetworkInfo net, string startHandle, string? highlight)
    {
        _profileShown = true;
        if (!Begin())
        {
            End();
            return;
        }

        try
        {
            double w = _graphic.ActualWidth, h = _graphic.ActualHeight;
            List<PipeInfo> path = net.PathDownstream(startHandle);
            List<(double S, StructureInfo S0)> nodes = new();
            double s = 0;
            StructureInfo? first = net.StructureByHandle.GetValueOrDefault(startHandle);
            if (first is null)
                return;
            nodes.Add((0, first));
            foreach (PipeInfo p in path)
            {
                s += p.Length2D;
                if (p.ToHandle is string to && net.StructureByHandle.GetValueOrDefault(to) is StructureInfo ts)
                    nodes.Add((s, ts));
            }

            double minZ = nodes.Min(n => n.S0.Sump), maxZ = nodes.Max(n => Math.Max(n.S0.Rim, double.IsNaN(n.S0.SurfaceElevation) ? n.S0.Rim : n.S0.SurfaceElevation));
            double total = Math.Max(s, 1);
            double exag = Math.Max(1, (total / Math.Max(maxZ - minZ, 0.5)) / 4);

            // Escala vertical exagerada: mundo = (dist, cota * exag).
            Bounds box = new(0, minZ * exag, total, maxZ * exag);
            SetupView(box.Inflate(total * 0.03), w, h);
            SW.Point P(double dist, double z) => ToScreen(new P2(dist, z * exag));

            double dh = h - RulerBottom;
            double stepZ = NiceStep((maxZ - minZ) / 6);
            for (double z = Math.Floor(minZ / stepZ) * stepZ; z <= maxZ + stepZ; z += stepZ)
            {
                double py = P(0, z).Y;
                if (py > 0 && py < dh)
                    Line(new SW.Point(RulerLeft, py), new SW.Point(w, py), GraphicGrid, 1);
            }

            // Terreno (tampa ou superficie).
            Poly(nodes.Select(n => P(n.S, double.IsNaN(n.S0.SurfaceElevation) ? n.S0.Rim : n.S0.SurfaceElevation)).ToList(), GroundBrush, 1.5);

            // Tubos: fundo e geratriz superior, lamina calculada e seta do fluxo.
            double dist = 0;
            foreach (PipeInfo p in path)
            {
                bool hi = p.Handle == highlight;
                WM.Brush brush = hi ? HighlightBrush : PipeColor(p);
                SW.Point a = P(dist, p.UpInvert), b = P(dist + p.Length2D, p.DownInvert);
                SW.Point a2 = P(dist, p.UpInvert + p.InnerDiameter), b2 = P(dist + p.Length2D, p.DownInvert + p.InnerDiameter);
                Line(a, b, brush, hi ? 2.5 : 1.5);
                Line(a2, b2, brush, hi ? 2.5 : 1.5);
                HydroResult? r = ResultOf(p.Handle);
                if (r is not null && r.DepthRatio > 0)
                    Line(P(dist, p.UpInvert + (r.DepthRatio * p.InnerDiameter)), P(dist + p.Length2D, p.DownInvert + (r.DepthRatio * p.InnerDiameter)), Brush(80, 160, 255), 1, true);
                if (_showArrows)
                    Arrows(new SW.Point((a.X + a2.X) / 2, (a.Y + a2.Y) / 2), new SW.Point((b.X + b2.X) / 2, (b.Y + b2.Y) / 2), brush, 2);
                if (_showNames)
                    Label($"{p.Name}  D{Mm(p.InnerDiameter)}  i={Pct(p.Slope)}  L={N(p.Length2D, 1)}", ((a.X + b.X) / 2) - 40, Math.Min(a.Y, b.Y) + 6, 10, hi ? HighlightBrush : GraphicText);
                dist += p.Length2D;
            }

            // Estruturas: retangulo da tampa ao fundo.
            foreach ((double d, StructureInfo st) in nodes)
            {
                bool hi = st.Handle == highlight;
                double half = Math.Max(Math.Max(st.InnerDiameter, 0.8) * _vScale / 2, 3);
                SW.Point top = P(d, st.Rim), bottom = P(d, st.Sump);
                Rect(top.X - half, top.Y, half * 2, Math.Max(bottom.Y - top.Y, 1), GraphicBack, hi ? HighlightBrush : StructureBrush, hi ? 2.5 : 1.4);
                if (_showNames)
                {
                    Label(st.Name, top.X - half, top.Y - 16, 10, hi ? HighlightBrush : GraphicText);
                    Label($"T {N(st.Rim, 2)} F {N(st.Sump, 2)}", top.X + half + 2, bottom.Y - 12, 9, GraphicText);
                }
            }

            // Reguas: distancia embaixo e cota a esquerda.
            Rect(0, 0, RulerLeft, dh, RulerBrush);
            Rect(0, dh, w, RulerBottom, RulerBrush);
            double stepX = NiceStep(total / 8);
            for (double x = 0; x <= total + 1e-6; x += stepX)
                Label(x.ToString("0", CultureInfo.InvariantCulture), P(x, minZ).X + 2, dh + 1, 9, GraphicText);
            for (double z = Math.Floor(minZ / stepZ) * stepZ; z <= maxZ + stepZ; z += stepZ)
            {
                double py = P(0, z).Y;
                if (py > 0 && py < dh)
                    Label(z.ToString("0.0", CultureInfo.InvariantCulture), 2, py - 6, 9, GraphicText);
            }

            _graphicInfo.Text = $"Perfil de {first.Name} para jusante: {path.Count} trecho(s), {N(total, 1)} m, exagero vertical {N(exag, 1)}x";
            _coords.Text = "";
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
