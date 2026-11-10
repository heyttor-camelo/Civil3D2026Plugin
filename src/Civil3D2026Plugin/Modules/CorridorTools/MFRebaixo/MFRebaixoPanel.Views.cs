using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.UI.Common;

using SW = System.Windows;
using W3 = System.Windows.Media.Media3D;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WS = System.Windows.Shapes;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>
/// Vistas do rebaixo selecionado alem das plantas (MFREBAIXO 1.11): perfil ao longo do meio-fio,
/// secao transversal comparada com a secao normal e 3D interativo (girar, aproximar, clicar numa
/// face para ver as medidas). Todas leem o mesmo RebaixoModel da planta do rebaixo.
/// </summary>
internal sealed partial class MFRebaixoPanel
{
    // ------------------------------------------------------------------
    // Comum
    // ------------------------------------------------------------------

    private Func<SW.Point, string>? _chartCursor;

    private string UnderCursorChart(SW.Point p)
    {
        try
        {
            return _chartCursor?.Invoke(p) ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>0 = fora do rebaixo, 1 = rampa, 2 = trecho rebaixado.</summary>
    private static int ZoneOf(RebaixoInfo r, double station)
    {
        if (station < r.S0 - 1e-4 || station > r.S3 + 1e-4)
            return 0;
        return station < r.S1 - 1e-4 || station > r.S2 + 1e-4 ? 1 : 2;
    }

    private static string ZoneName(RebaixoInfo r, double station) =>
        station < r.S0 - 1e-4 ? "antes do rebaixo"
        : station < r.S1 - 1e-4 ? "rampa de entrada"
        : station <= r.S2 + 1e-4 ? "trecho rebaixado"
        : station <= r.S3 + 1e-4 ? "rampa de saida"
        : "depois do rebaixo";

    private static bool Has(string[] codes, string code) =>
        codes.Any(c => c.Equals(code, StringComparison.OrdinalIgnoreCase));

    private static bool IsWalk(string[] codes) =>
        codes.Any(c => c.StartsWith("Calc", StringComparison.OrdinalIgnoreCase));

    /// <summary>Chave da subassembly do meio-fio do rebaixo no modelo (pelo nome; senao, a que tem link Curb do lado dele).</summary>
    private static long? MfKey(RebaixoModel model, RebaixoInfo r)
    {
        foreach (KeyValuePair<long, string> kv in model.Names)
        {
            if (kv.Value.Equals(r.SubassemblyName, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }

        foreach (ModelSection s in model.Sections)
        {
            foreach ((long key, List<ModelLink> links) in s.Subassemblies)
            {
                ModelLink? curb = links.FirstOrDefault(l => Has(l.Codes, "Curb") && l.Soe.Length > 0);
                if (curb is null)
                    continue;
                bool left = curb.Soe[0].Y < 0;
                if (left == r.Left)
                    return key;
            }
        }

        return null;
    }

    /// <summary>Do meio-fio numa secao: elevacao do topo, da linha d'agua (sarjeta) e a origem (borda da pista).</summary>
    private static (double Top, double Flow, Point3d Origin)? CurbOf(ModelSection s, long key)
    {
        List<ModelLink>? links = s.Subassemblies.FirstOrDefault(x => x.Key == key).Links;
        if (links is null)
            return null;

        double top = double.MinValue, flow = double.MaxValue;
        Point3d? first = null;
        foreach (ModelLink l in links)
        {
            if (IsWalk(l.Codes))
                continue;
            bool curb = Has(l.Codes, "Curb");
            bool topCode = Has(l.Codes, "Top") || Has(l.Codes, "TOPO");
            foreach (Point3d q in l.Soe)
            {
                first ??= q;
                if (curb || topCode)
                    top = Math.Max(top, q.Z);
                if (topCode)
                    flow = Math.Min(flow, q.Z);
            }
        }

        if (first is null || top == double.MinValue)
            return null;

        Point3d origin = s.Origins.TryGetValue(key, out Point3d o) ? o : first.Value;
        if (flow == double.MaxValue)
            flow = origin.Z;
        return (top, flow, origin);
    }

    private void ChartMessage(string text)
    {
        _scale = 0;
        _chartCursor = null;
        PlanLabel(text, 10, 10, 12);
    }

    private void ChartLine(double x1, double y1, double x2, double y2, WM.Brush brush, double thickness, bool dashed = false)
    {
        var line = new WS.Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false };
        if (dashed)
            line.StrokeDashArray = new WM.DoubleCollection { 5, 4 };
        _plan.Children.Add(line);
    }

    // ------------------------------------------------------------------
    // Perfil ao longo do meio-fio
    // ------------------------------------------------------------------

    private void RenderProfile()
    {
        _plan.Children.Clear();
        _scale = 0;
        _chartCursor = null;
        double w = _plan.ActualWidth, h = _plan.ActualHeight;
        if (w < 80 || h < 80 || !_plan.IsVisible)
            return;

        RebaixoInfo? r = _current;
        RebaixoModel? model = CurrentModel();
        if (r is null || model is null)
        {
            ChartMessage("Selecione um rebaixo.");
            return;
        }

        if (MfKey(model, r) is not long key)
        {
            ChartMessage("Nao achei o meio-fio deste rebaixo nas secoes do corredor (Rebuild no corredor?).");
            return;
        }

        List<(double St, double Top, double Flow)> pts = new();
        foreach (ModelSection s in model.Sections)
        {
            if (CurbOf(s, key) is { } c)
                pts.Add((s.Station, c.Top, c.Flow));
        }

        if (pts.Count < 2)
        {
            ChartMessage("Sem secoes calculadas neste trecho (Rebuild no corredor?).");
            return;
        }

        double sa = pts[0].St, sb = pts[^1].St;
        double zmin = pts.Min(p => p.Flow), zmax = pts.Max(p => p.Top);
        double pad = Math.Max(0.03, (zmax - zmin) * 0.3);
        zmin -= pad;
        zmax += pad * 1.8;

        const double left = 62, right = 18, top = 30, bottom = 36;
        double sx = (w - left - right) / Math.Max(sb - sa, 1e-6);
        double sy = (h - top - bottom) / Math.Max(zmax - zmin, 1e-6);
        if (sy > sx * 30)
            sy = sx * 30; // limite do exagero vertical
        double X(double st) => left + ((st - sa) * sx);
        double Y(double z) => h - bottom - ((z - zmin) * sy);

        // Faixa entre a linha d'agua e o topo do meio-fio, colorida pelo trecho.
        WM.Brush normalFill = ToolTheme.Brush(196, 198, 204, 150);
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            (double St, double Top, double Flow) a = pts[i], b = pts[i + 1];
            int zone = ZoneOf(r, (a.St + b.St) / 2.0);
            var pg = new WS.Polygon { Fill = zone == 2 ? RebaixoBrush : zone == 1 ? RampBrush : normalFill, Opacity = zone == 0 ? 1 : 0.85, IsHitTestVisible = false };
            pg.Points.Add(new SW.Point(X(a.St), Y(a.Top)));
            pg.Points.Add(new SW.Point(X(b.St), Y(b.Top)));
            pg.Points.Add(new SW.Point(X(b.St), Y(b.Flow)));
            pg.Points.Add(new SW.Point(X(a.St), Y(a.Flow)));
            _plan.Children.Add(pg);
        }

        var topLine = new WS.Polyline { Stroke = WM.Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        var flowLine = new WS.Polyline { Stroke = ToolTheme.GraphicLine, StrokeThickness = 1.5, IsHitTestVisible = false };
        foreach ((double st, double zt, double zf) in pts)
        {
            topLine.Points.Add(new SW.Point(X(st), Y(zt)));
            flowLine.Points.Add(new SW.Point(X(st), Y(zf)));
        }

        _plan.Children.Add(flowLine);
        _plan.Children.Add(topLine);

        // Marcos S0..S3.
        WM.Brush markBrush = ToolTheme.Brush(255, 205, 80);
        foreach ((string name, double st) in new[] { ("S0", r.S0), ("S1", r.S1), ("S2", r.S2), ("S3", r.S3) })
        {
            if (st < sa || st > sb)
                continue;
            ChartLine(X(st), top - 4, X(st), h - bottom, markBrush, 0.8, dashed: true);
            if (Show("MARCOS"))
                PlanLabel(name, X(st) + 3, top - 6, 11, markBrush);
        }

        // Altura do meio-fio (topo - linha d'agua) nos pontos que interessam.
        foreach (double st in new[] { sa, r.S0, (r.S1 + r.S2) / 2.0, r.S3, sb }.Distinct())
        {
            (double St, double Top, double Flow) n = pts.OrderBy(p => Math.Abs(p.St - st)).First();
            PlanLabel($"{(n.Top - n.Flow) * 100:0.0} cm", X(n.St) - 16, Y(n.Top) - 18, 11);
        }

        // Eixos.
        ChartLine(left, h - bottom, w - right, h - bottom, ToolTheme.GraphicLine, 1);
        ChartLine(left, top, left, h - bottom, ToolTheme.GraphicLine, 1);
        double step = NiceStep((sb - sa) / 7.0);
        for (double st = Math.Ceiling(sa / step) * step; st <= sb + 1e-9; st += step)
        {
            ChartLine(X(st), h - bottom, X(st), h - bottom + 4, ToolTheme.GraphicLine, 1);
            PlanLabel(N(st, "0.##"), X(st) - 12, h - bottom + 5, 10);
        }

        double zstep = NiceStep((zmax - zmin) / 5.0);
        for (double z = Math.Ceiling(zmin / zstep) * zstep; z <= zmax + 1e-9; z += zstep)
        {
            if (Y(z) < top)
                break;
            ChartLine(left - 4, Y(z), left, Y(z), ToolTheme.GraphicLine, 1);
            PlanLabel(N(z, "0.00"), 6, Y(z) - 7, 10);
        }

        PlanLabel("estaca (m)", w - right - 60, h - 15, 10);
        if (Show("LEGENDA"))
        {
            PlanLabel($"Perfil do meio-fio   |   exagero vertical {sy / sx:0.#}x", left + 8, 6, 11);
            ChartLine(left + 300, 14, left + 322, 14, WM.Brushes.White, 2);
            PlanLabel("topo do meio-fio", left + 326, 6, 11);
            ChartLine(left + 440, 14, left + 462, 14, ToolTheme.GraphicLine, 1.5);
            PlanLabel("sarjeta (linha d'agua)", left + 466, 6, 11);
        }

        _chartCursor = p =>
        {
            double st = sa + ((p.X - left) / sx);
            if (st < sa || st > sb)
                return "";
            (double St, double Top, double Flow) n = pts.OrderBy(q => Math.Abs(q.St - st)).First();
            return $"estaca {N(n.St)}  -  {ZoneName(r, n.St)}  -  topo {N(n.Top)}  sarjeta {N(n.Flow)}  altura {(n.Top - n.Flow) * 100:0.0} cm";
        };
    }

    // ------------------------------------------------------------------
    // Secao transversal
    // ------------------------------------------------------------------

    private int? _secIndex;
    private string? _secKey;

    private void StepSection(int delta)
    {
        _secIndex = (_secIndex ?? 0) + delta;
        RenderGraphic();
    }

    private void RenderCross()
    {
        _plan.Children.Clear();
        _scale = 0;
        _chartCursor = null;
        double w = _plan.ActualWidth, h = _plan.ActualHeight;
        if (w < 80 || h < 80 || !_plan.IsVisible)
            return;

        RebaixoInfo? r = _current;
        RebaixoModel? model = CurrentModel();
        if (r is null || model is null)
        {
            ChartMessage("Selecione um rebaixo.");
            return;
        }

        if (MfKey(model, r) is not long key)
        {
            ChartMessage("Nao achei o meio-fio deste rebaixo nas secoes do corredor (Rebuild no corredor?).");
            return;
        }

        List<ModelSection> secs = model.Sections.Where(s => CurbOf(s, key) is not null).ToList();
        if (secs.Count == 0)
        {
            ChartMessage("Sem secoes calculadas neste trecho (Rebuild no corredor?).");
            return;
        }

        string secKey = $"{r.CorridorId.Handle.Value}|{r.SetName}|{secs.Count}";
        if (_secKey != secKey)
        {
            _secKey = secKey;
            _secIndex = null;
        }

        double mid = (r.S1 + r.S2) / 2.0;
        int idx = _secIndex ?? secs.Select((s, i) => (s, i)).OrderBy(x => Math.Abs(x.s.Station - mid)).First().i;
        idx = Math.Clamp(idx, 0, secs.Count - 1);
        _secIndex = idx;

        ModelSection cur = secs[idx];
        ModelSection? normal = secs.LastOrDefault(s => s.Station < r.S0 - 1e-4) ?? secs.FirstOrDefault(s => s.Station > r.S3 + 1e-4);
        (double Top, double Flow, Point3d Origin) c = CurbOf(cur, key)!.Value;
        (double Top, double Flow, Point3d Origin)? n = normal is null ? null : CurbOf(normal, key);

        // Coordenadas relativas a borda da pista (origem do meio-fio) de cada secao.
        List<(ModelLink Link, bool Mf)> curLinks = cur.Subassemblies.SelectMany(a => a.Links.Select(l => (l, a.Key == key))).Where(x => x.l.Soe.Length > 1).ToList();
        List<ModelLink> normalLinks = normal is null || n is null
            ? new List<ModelLink>()
            : (normal.Subassemblies.FirstOrDefault(a => a.Key == key).Links ?? new List<ModelLink>()).Where(l => l.Soe.Length > 1).ToList();

        List<(double U, double V)> box = new();
        foreach ((ModelLink l, bool mf) in curLinks)
        {
            if (mf)
                box.AddRange(l.Soe.Select(q => (q.Y - c.Origin.Y, q.Z - c.Origin.Z)));
        }

        if (n is { } nn)
            box.AddRange(normalLinks.SelectMany(l => l.Soe.Select(q => (q.Y - nn.Origin.Y, q.Z - nn.Origin.Z))));
        if (box.Count == 0)
        {
            ChartMessage("Sem geometria do meio-fio nesta secao.");
            return;
        }

        double umin = box.Min(p => p.U) - 0.45, umax = box.Max(p => p.U) + 0.45;
        double vmin = box.Min(p => p.V) - 0.12, vmax = box.Max(p => p.V) + 0.18;
        const double marginX = 24, marginTop = 46, marginBottom = 30;
        double scale = Math.Min((w - (2 * marginX)) / Math.Max(umax - umin, 1e-6), (h - marginTop - marginBottom) / Math.Max(vmax - vmin, 1e-6));
        double ox = (w / 2.0) - (((umin + umax) / 2.0) * scale);
        double oy = marginTop + ((h - marginTop - marginBottom) / 2.0) + (((vmin + vmax) / 2.0) * scale);
        SW.Point P(double u, double v) => new(ox + (u * scale), oy - (v * scale));

        void Draw(ModelLink l, Point3d origin, WM.Brush brush, double thickness, bool dashed)
        {
            var pl = new WS.Polyline { Stroke = brush, StrokeThickness = thickness, StrokeLineJoin = WM.PenLineJoin.Round, IsHitTestVisible = false };
            if (dashed)
                pl.StrokeDashArray = new WM.DoubleCollection { 5, 4 };
            foreach (Point3d q in l.Soe)
                pl.Points.Add(P(q.Y - origin.Y, q.Z - origin.Z));
            _plan.Children.Add(pl);
        }

        // Vizinhos (pista, calcada de outra peca) em cinza fino; secao normal tracejada; meio-fio atual por cima.
        foreach ((ModelLink l, bool mf) in curLinks)
        {
            if (!mf)
                Draw(l, c.Origin, ToolTheme.GraphicLine, 1, false);
        }

        if (n is { } n2)
        {
            WM.Brush ghost = ToolTheme.Brush(255, 255, 255, 150);
            foreach (ModelLink l in normalLinks)
                Draw(l, n2.Origin, ghost, 1.2, true);
        }

        int zone = ZoneOf(r, cur.Station);
        foreach ((ModelLink l, bool mf) in curLinks)
        {
            if (!mf)
                continue;
            RebaixoStyle.Look look = RebaixoStyle.Of(l.Codes);
            WM.Brush brush = look.Color.A == 0 ? ToolTheme.GraphicText : new WM.SolidColorBrush(look.Color);
            if (zone > 0 && (Has(l.Codes, "Curb") || Has(l.Codes, "Top") || Has(l.Codes, "TOPO")))
                brush = zone == 2 ? RebaixoBrush : RampBrush;
            Draw(l, c.Origin, brush, 2.2, false);
        }

        PlanLabel($"Secao na estaca {N(cur.Station)}   |   {ZoneName(r, cur.Station)}   |   {idx + 1} de {secs.Count}", 10, 6, 12);
        string normalText = n is { } n3 && normal is not null ? $"   (secao normal, estaca {N(normal.Station)}: {(n3.Top - n3.Flow) * 100:0.0} cm, tracejada)" : "";
        PlanLabel($"Altura do meio-fio: {(c.Top - c.Flow) * 100:0.0} cm{normalText}", 10, 24, 11);
        if (Show("ESCALA"))
        {
            double step = NiceStep(w / scale / 8.0);
            double len = step * scale;
            ChartLine(w - len - 14, h - 12, w - 14, h - 12, ToolTheme.GraphicText, 2);
            PlanLabel($"{step:0.##} m", w - len - 14, h - 28, 10);
        }

        _chartCursor = p =>
        {
            double u = (p.X - ox) / scale, v = (oy - p.Y) / scale;
            return $"estaca {N(cur.Station)}  -  offset {N(c.Origin.Y + u)}  elevacao {N(c.Origin.Z + v)}  (da borda da pista: {u:0.000} / {v:0.000})";
        };
    }

    // ------------------------------------------------------------------
    // 3D interativo
    // ------------------------------------------------------------------

    private sealed record Face3D(long Key, int Link, int Zone, string[] Codes);

    private readonly WC.Grid _host3D = new() { Visibility = SW.Visibility.Collapsed };
    private readonly WC.Viewport3D _viewport = new() { ClipToBounds = true };
    private readonly WC.TextBlock _info3D = new();
    private readonly Dictionary<W3.GeometryModel3D, Face3D> _faces = new();
    private double _exagero3D = 1.0;
    private string? _key3D;
    private double _yaw = -60, _pitch = 28, _dist = 20;
    private W3.Point3D _target;
    private double _cx3, _cy3, _z03;
    private SW.Point? _orbitFrom;
    private SW.Point _downAt;
    private W3.GeometryModel3D? _picked;
    private W3.Material? _pickedMaterial;

    private SW.UIElement Build3DHost()
    {
        _host3D.Background = ToolTheme.GraphicBack;
        _viewport.Camera = new W3.PerspectiveCamera { FieldOfView = 40, NearPlaneDistance = 0.05, FarPlaneDistance = 10000 };
        _host3D.Children.Add(_viewport);

        _info3D.Foreground = ToolTheme.GraphicText;
        _info3D.Background = ToolTheme.Brush(30, 32, 36, 215);
        _info3D.Padding = new SW.Thickness(8, 6, 8, 6);
        _info3D.Margin = new SW.Thickness(8);
        _info3D.FontSize = 11.5;
        _info3D.HorizontalAlignment = SW.HorizontalAlignment.Right;
        _info3D.VerticalAlignment = SW.VerticalAlignment.Top;
        _info3D.IsHitTestVisible = false;
        _info3D.Visibility = SW.Visibility.Collapsed;
        _host3D.Children.Add(_info3D);

        _host3D.Children.Add(new WC.TextBlock
        {
            Text = "arraste: girar   |   botao do meio: mover   |   roda: aproximar   |   clique numa face: medidas",
            Foreground = ToolTheme.GraphicLine,
            FontSize = 10.5,
            Margin = new SW.Thickness(8, 0, 0, 6),
            HorizontalAlignment = SW.HorizontalAlignment.Left,
            VerticalAlignment = SW.VerticalAlignment.Bottom,
            IsHitTestVisible = false
        });

        _host3D.MouseDown += (_, e) =>
        {
            _orbitFrom = e.GetPosition(_host3D);
            _downAt = _orbitFrom.Value;
            _host3D.CaptureMouse();
        };
        _host3D.MouseMove += (_, e) =>
        {
            if (_orbitFrom is not SW.Point f)
                return;
            SW.Point p = e.GetPosition(_host3D);
            double dx = p.X - f.X, dy = p.Y - f.Y;
            if (e.MiddleButton == WI.MouseButtonState.Pressed || e.RightButton == WI.MouseButtonState.Pressed)
            {
                // Move o alvo no plano da tela.
                double k = _dist * 0.0016;
                double yaw = _yaw * Math.PI / 180.0;
                _target = new W3.Point3D(
                    _target.X + ((Math.Sin(yaw) * dx) * k),
                    _target.Y - ((Math.Cos(yaw) * dx) * k),
                    _target.Z + (dy * k));
            }
            else if (e.LeftButton == WI.MouseButtonState.Pressed)
            {
                _yaw -= dx * 0.4;
                _pitch = Math.Clamp(_pitch + (dy * 0.4), 2, 89);
            }
            else
            {
                return;
            }

            _orbitFrom = p;
            UpdateCamera();
        };
        _host3D.MouseUp += (_, e) =>
        {
            SW.Point p = e.GetPosition(_host3D);
            bool click = _orbitFrom is not null && e.ChangedButton == WI.MouseButton.Left && (p - _downAt).Length < 4;
            _orbitFrom = null;
            _host3D.ReleaseMouseCapture();
            if (click)
                Pick(p);
        };
        _host3D.MouseWheel += (_, e) =>
        {
            _dist = Math.Clamp(_dist * (e.Delta > 0 ? 0.85 : 1.18), 0.4, 5000);
            UpdateCamera();
        };
        return _host3D;
    }

    private void UpdateCamera()
    {
        if (_viewport.Camera is not W3.PerspectiveCamera cam)
            return;
        double yaw = _yaw * Math.PI / 180.0, pitch = _pitch * Math.PI / 180.0;
        var offset = new W3.Vector3D(Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch)) * _dist;
        cam.Position = _target + offset;
        cam.LookDirection = -offset;
        cam.UpDirection = new W3.Vector3D(0, 0, 1);
    }

    private void Render3D()
    {
        RebaixoInfo? r = _current;
        RebaixoModel? model = CurrentModel();
        if (r is null || model is null || model.Sections.Count < 2)
        {
            _viewport.Children.Clear();
            _faces.Clear();
            _picked = null;
            _key3D = null;
            _info3D.Text = r is null ? "Selecione um rebaixo." : "Sem secoes calculadas neste trecho (Rebuild no corredor?).";
            _info3D.Visibility = SW.Visibility.Visible;
            return;
        }

        string key3D = $"{_modelKey}|{_exagero3D:0.##}";
        if (key3D == _key3D)
            return;
        _key3D = key3D;

        _viewport.Children.Clear();
        _faces.Clear();
        _picked = null;
        _info3D.Visibility = SW.Visibility.Collapsed;

        List<Point3d> all = model.Sections.SelectMany(s => s.Subassemblies.SelectMany(a => a.Links.SelectMany(l => l.Points))).ToList();
        if (all.Count == 0)
            return;
        Point3d center = r.Path.Count > 0 ? r.Path[r.Path.Count / 2] : all[all.Count / 2];
        _cx3 = center.X;
        _cy3 = center.Y;
        _z03 = all.Min(p => p.Z);
        double ex = _exagero3D;
        W3.Point3D L(Point3d p) => new(p.X - _cx3, p.Y - _cy3, (p.Z - _z03) * ex);

        long? mf = MfKey(model, r);
        var meshes = new Dictionary<(long Key, int Link, int Zone), (W3.MeshGeometry3D Mesh, string[] Codes)>();
        for (int k = 0; k + 1 < model.Sections.Count; k++)
        {
            ModelSection a = model.Sections[k], b = model.Sections[k + 1];
            double midStation = (a.Station + b.Station) / 2.0;
            foreach ((long key, List<ModelLink> linksA) in a.Subassemblies)
            {
                List<ModelLink>? linksB = b.Subassemblies.FirstOrDefault(x => x.Key == key).Links;
                if (linksB is null)
                    continue;
                int n = Math.Min(linksA.Count, linksB.Count);
                for (int j = 0; j < n; j++)
                {
                    ModelLink la = linksA[j], lb = linksB[j];
                    if (la.Points.Length < 2 || la.Points.Length != lb.Points.Length)
                        continue;
                    int zone = 0;
                    if (mf == key && !IsWalk(la.Codes) && (Has(la.Codes, "Curb") || Has(la.Codes, "Top") || Has(la.Codes, "TOPO") || Has(la.Codes, "Sarjeta")))
                        zone = ZoneOf(r, midStation);
                    if (!meshes.TryGetValue((key, j, zone), out (W3.MeshGeometry3D Mesh, string[] Codes) entry))
                    {
                        entry = (new W3.MeshGeometry3D(), la.Codes);
                        meshes[(key, j, zone)] = entry;
                    }

                    for (int p = 0; p + 1 < la.Points.Length; p++)
                    {
                        int i0 = entry.Mesh.Positions.Count;
                        entry.Mesh.Positions.Add(L(la.Points[p]));
                        entry.Mesh.Positions.Add(L(la.Points[p + 1]));
                        entry.Mesh.Positions.Add(L(lb.Points[p + 1]));
                        entry.Mesh.Positions.Add(L(lb.Points[p]));
                        entry.Mesh.TriangleIndices.Add(i0);
                        entry.Mesh.TriangleIndices.Add(i0 + 1);
                        entry.Mesh.TriangleIndices.Add(i0 + 2);
                        entry.Mesh.TriangleIndices.Add(i0);
                        entry.Mesh.TriangleIndices.Add(i0 + 2);
                        entry.Mesh.TriangleIndices.Add(i0 + 3);
                    }
                }
            }
        }

        var group = new W3.Model3DGroup();
        group.Children.Add(new W3.AmbientLight(WM.Color.FromRgb(125, 125, 125)));
        group.Children.Add(new W3.DirectionalLight(WM.Color.FromRgb(190, 190, 190), new W3.Vector3D(-0.45, -0.3, -1)));
        group.Children.Add(new W3.DirectionalLight(WM.Color.FromRgb(70, 70, 70), new W3.Vector3D(0.5, 0.6, -0.3)));
        foreach (KeyValuePair<(long Key, int Link, int Zone), (W3.MeshGeometry3D Mesh, string[] Codes)> kv in meshes)
        {
            RebaixoStyle.Look look = RebaixoStyle.Of(kv.Value.Codes);
            WM.Color color = kv.Key.Zone == 2 ? WM.Color.FromRgb(225, 45, 60)
                : kv.Key.Zone == 1 ? WM.Color.FromRgb(245, 160, 40)
                : look.Color.A == 0 ? WM.Color.FromRgb(132, 134, 140) : look.Color;
            var material = new W3.DiffuseMaterial(new WM.SolidColorBrush(color));
            var gm = new W3.GeometryModel3D(kv.Value.Mesh, material) { BackMaterial = material };
            group.Children.Add(gm);
            _faces[gm] = new Face3D(kv.Key.Key, kv.Key.Link, kv.Key.Zone, kv.Value.Codes);
        }

        _viewport.Children.Add(new W3.ModelVisual3D { Content = group });

        double zTarget = r.Band.Count > 0 || r.Path.Count > 0
            ? all.Where(p => Math.Abs(p.X - _cx3) < 1.5 && Math.Abs(p.Y - _cy3) < 1.5).Select(p => p.Z).DefaultIfEmpty(_z03).Average()
            : _z03;
        _target = new W3.Point3D(0, 0, (zTarget - _z03) * ex);
        _dist = Math.Max(5, r.TotalLength * 1.8);
        UpdateCamera();
    }

    private void ClearPick()
    {
        if (_picked is not null && _pickedMaterial is not null)
        {
            _picked.Material = _pickedMaterial;
            _picked.BackMaterial = _pickedMaterial;
        }

        _picked = null;
        _pickedMaterial = null;
    }

    /// <summary>Clique numa face do 3D: destaca e mostra peca, codes, estaca e medidas do link.</summary>
    private void Pick(SW.Point p)
    {
        try
        {
            RebaixoInfo? r = _current;
            RebaixoModel? model = _model;
            ClearPick();
            if (r is null || model is null ||
                WM.VisualTreeHelper.HitTest(_viewport, p) is not W3.RayMeshGeometry3DHitTestResult hit ||
                hit.ModelHit is not W3.GeometryModel3D gm || !_faces.TryGetValue(gm, out Face3D? face))
            {
                _info3D.Visibility = SW.Visibility.Collapsed;
                return;
            }

            _picked = gm;
            _pickedMaterial = gm.Material;
            var yellow = new W3.DiffuseMaterial(new WM.SolidColorBrush(WM.Color.FromRgb(255, 215, 60)));
            gm.Material = yellow;
            gm.BackMaterial = yellow;

            double x = hit.PointHit.X + _cx3, y = hit.PointHit.Y + _cy3, z = (hit.PointHit.Z / _exagero3D) + _z03;

            // Secao mais perto do ponto clicado, no mesmo link.
            ModelSection? best = null;
            ModelLink? link = null;
            double bestD = double.MaxValue;
            foreach (ModelSection s in model.Sections)
            {
                List<ModelLink>? links = s.Subassemblies.FirstOrDefault(a => a.Key == face.Key).Links;
                if (links is null || face.Link >= links.Count)
                    continue;
                foreach (Point3d q in links[face.Link].Points)
                {
                    double d = ((q.X - x) * (q.X - x)) + ((q.Y - y) * (q.Y - y));
                    if (d < bestD)
                    {
                        bestD = d;
                        best = s;
                        link = links[face.Link];
                    }
                }
            }

            var lines = new List<string>
            {
                "Peca: " + (model.Names.TryGetValue(face.Key, out string? name) ? name : "?"),
                "Codes: " + (face.Codes.Length == 0 ? "(sem code)" : string.Join(", ", face.Codes))
            };
            if (best is not null && link is not null && link.Soe.Length > 1)
            {
                Point3d a = link.Soe[0], b = link.Soe[^1];
                double width = Math.Abs(b.Y - a.Y), dz = b.Z - a.Z;
                double length = 0;
                for (int i = 1; i < link.Soe.Length; i++)
                    length += Math.Sqrt(Math.Pow(link.Soe[i].Y - link.Soe[i - 1].Y, 2) + Math.Pow(link.Soe[i].Z - link.Soe[i - 1].Z, 2));
                lines.Add($"Secao: estaca {N(best.Station)}  ({ZoneName(r, best.Station)})");
                lines.Add($"Offset: {N(a.Y)} a {N(b.Y)}   largura {width:0.000} m");
                lines.Add($"Elevacao: {N(a.Z)} a {N(b.Z)}   desnivel {dz * 100:0.0} cm");
                lines.Add(width > 1e-4 ? $"Inclinacao: {Math.Abs(dz) / width * 100:0.00}%" : "Inclinacao: vertical");
                lines.Add($"Comprimento do link: {length:0.000} m");
                if (face.Key == MfKey(model, r) && CurbOf(best, face.Key) is { } c)
                    lines.Add($"Altura do meio-fio nesta secao: {(c.Top - c.Flow) * 100:0.0} cm");
            }

            lines.Add($"Ponto: X {x:0.000}  Y {y:0.000}  elevacao {z:0.000}");
            _info3D.Text = string.Join("\n", lines);
            _info3D.Visibility = SW.Visibility.Visible;
        }
        catch (System.Exception ex)
        {
            Civil3D2026Plugin.Infrastructure.PluginLog.Aviso("MFREBAIXO", "3D: clique nao lido: " + ex.Message);
        }
    }
}
