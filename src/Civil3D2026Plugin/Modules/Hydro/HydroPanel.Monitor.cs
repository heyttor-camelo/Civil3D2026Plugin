using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using SW = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WS = System.Windows.Shapes;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>Monitor do painel HYDRO: planta esquematica da rede (setas do fluxo, bacias) ou perfil do caminho.</summary>
internal sealed partial class HydroPanel
{
    private static readonly WM.Brush GraphicBack = Brush(56, 58, 62);
    private static readonly WM.Brush GraphicGrid = Brush(66, 68, 73);
    private static readonly WM.Brush GraphicGridMajor = Brush(86, 88, 94);
    private static readonly WM.Brush GraphicText = Brush(205, 205, 205);
    private static readonly WM.Brush HighlightBrush = Brush(255, 176, 0);
    private static readonly WM.Brush PipeBrush = Brush(120, 220, 235);
    private static readonly WM.Brush PipeUndefined = Brush(240, 120, 120);
    private static readonly WM.Brush StructureBrush = Brush(235, 240, 245);
    private static readonly WM.Brush CatchmentBrush = Brush(230, 90, 230);
    private static readonly WM.Brush CatchmentFill = new WM.SolidColorBrush(WM.Color.FromArgb(40, 230, 90, 230));
    private static readonly WM.Brush GroundBrush = Brush(110, 210, 110);
    private static readonly WM.Brush OtherNetwork = Brush(130, 135, 140);

    private double _vScale, _vCx, _vCy, _vOx, _vOy;
    private double _zoom = 1;
    private P2 _pan = new(0, 0);
    private SW.Point? _dragFrom;
    private bool _profileShown;

    private P2 ToWorld(SW.Point p) => new(_vCx + ((p.X - _vOx) / _vScale), _vCy - ((p.Y - _vOy) / _vScale));

    private SW.Point ToScreen(P2 p) => new(_vOx + ((p.X - _vCx) * _vScale), _vOy - ((p.Y - _vCy) * _vScale));

    private void OnGraphicMouseMove(object sender, WI.MouseEventArgs e)
    {
        if (_vScale <= 0)
            return;
        SW.Point p = e.GetPosition(_graphic);
        P2 w = ToWorld(p);
        _coords.Text = _profileShown ? $"dist {w.X,9:0.00} m   cota {w.Y,9:0.000}" : $"E {w.X,12:0.000}   N {w.Y,12:0.000}";
        if (_dragFrom is SW.Point f && (e.LeftButton == WI.MouseButtonState.Pressed || e.MiddleButton == WI.MouseButtonState.Pressed))
        {
            _pan = new P2(_pan.X - ((p.X - f.X) / _vScale), _pan.Y + ((p.Y - f.Y) / _vScale));
            _dragFrom = p;
            _render?.Invoke();
        }
        else if (!_profileShown)
        {
            _graphicInfo.Text = Under(w);
        }
    }

    private void OnGraphicWheel(object sender, WI.MouseWheelEventArgs e)
    {
        if (_vScale <= 0)
            return;
        SW.Point p = e.GetPosition(_graphic);
        P2 before = ToWorld(p);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25 : 0.8), 0.2, 200);
        _render?.Invoke();
        P2 after = ToWorld(p);
        _pan = new P2(_pan.X + (before.X - after.X), _pan.Y + (before.Y - after.Y));
        _render?.Invoke();
    }

    /// <summary>O que esta sob o cursor na planta.</summary>
    private string Under(P2 w)
    {
        NetworkInfo? net = Net;
        if (net is null || _vScale <= 0)
            return "";
        double tol = 8 / _vScale;
        StructureInfo? s = net.Structures.OrderBy(x => P2.Dist(x.Point, w)).FirstOrDefault(x => P2.Dist(x.Point, w) < tol);
        if (s is not null)
            return $"{s.Name}: tampa {N(s.Rim, 3)}  fundo {N(s.Sump, 3)}  prof {N(s.Depth, 2)} m";
        PipeInfo? p = net.Pipes.OrderBy(x => DistToSeg(w, x.Start, x.End)).FirstOrDefault(x => DistToSeg(w, x.Start, x.End) < tol);
        if (p is not null)
        {
            HydroResult? r = ResultOf(p.Handle);
            return $"{p.Name}: D {Mm(p.InnerDiameter)} mm  L {N(p.Length2D, 2)} m  i {Pct(p.Slope)}" + (r is null ? "" : $"  Q {N(r.Flow, 4)} m3/s  y/D {N(r.DepthRatio * 100, 0)}%  V {N(r.Velocity, 2)} m/s");
        }

        return "";
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

    private const double RulerLeft = 44;
    private const double RulerBottom = 16;

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
        _graphic.Children.Clear();
        double w = _graphic.ActualWidth, h = _graphic.ActualHeight;
        if (w < 20 || h < 20)
            return;

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
        if (_showGrid)
            DrawGrid(w, h);

        // Bacias.
        if (_showCatchments)
        {
            foreach (CatchmentInfo c in _snap.Catchments)
            {
                bool hi = c.Handle == highlight;
                if (c.Boundary.Count >= 3)
                {
                    WS.Polygon poly = new() { Stroke = hi ? HighlightBrush : CatchmentBrush, StrokeThickness = hi ? 2 : 1, Fill = CatchmentFill, StrokeDashArray = new WM.DoubleCollection { 4, 3 } };
                    foreach (P2 pt in c.Boundary)
                        poly.Points.Add(ToScreen(pt));
                    _graphic.Children.Add(poly);
                }

                if (c.StructureHandle.Length > 0 && net?.StructureByHandle.GetValueOrDefault(c.StructureHandle) is StructureInfo target)
                {
                    SW.Point a = ToScreen(c.Centroid), b = ToScreen(target.Point);
                    _graphic.Children.Add(new WS.Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = hi ? HighlightBrush : CatchmentBrush, StrokeThickness = 1, StrokeDashArray = new WM.DoubleCollection { 2, 3 } });
                }

                if (_showNames && scale * Math.Sqrt(Math.Max(c.Area, 1)) > 30)
                    Label(c.Name, ToScreen(c.Centroid).X - 10, ToScreen(c.Centroid).Y - 7, 10, CatchmentBrush);
            }
        }

        // Tubos.
        foreach (NetworkInfo n in shown)
        {
            bool active = n.Handle == net?.Handle;
            foreach (PipeInfo p in n.Pipes)
            {
                bool hi = p.Handle == highlight || marked?.Contains(p.Handle) == true;
                WM.Brush brush = hi ? HighlightBrush : !active ? OtherNetwork : p.Defined ? PipeBrush : PipeUndefined;
                double thickness = hi ? 3 : Math.Clamp(p.OuterDiameter * scale, 1.2, 6);
                SW.Point a = ToScreen(p.UpPoint), b = ToScreen(p.DownPoint);
                _graphic.Children.Add(new WS.Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = brush, StrokeThickness = thickness });
                if (_showArrows && p.Defined && P2.Dist(p.Start, p.End) * scale > 18)
                    Arrow(a, b, brush);
                if (_showNames && P2.Dist(p.Start, p.End) * scale > 50)
                {
                    SW.Point m = new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    Label(p.Name, m.X + 4, m.Y + 2, 10, hi ? HighlightBrush : GraphicText);
                }
            }
        }

        // Estruturas por cima.
        foreach (NetworkInfo n in shown)
        {
            bool active = n.Handle == net?.Handle;
            foreach (StructureInfo s in n.Structures)
            {
                bool hi = s.Handle == highlight || marked?.Contains(s.Handle) == true;
                double r = Math.Clamp(Math.Max(s.InnerDiameter, 0.6) * scale / 2, 3, 14);
                SW.Point c = ToScreen(s.Point);
                WM.Brush brush = hi ? HighlightBrush : active ? StructureBrush : OtherNetwork;
                _graphic.Children.Add(new WS.Ellipse { Width = r * 2, Height = r * 2, Stroke = brush, StrokeThickness = hi ? 2.5 : 1.4, Fill = GraphicBack }.At(c.X - r, c.Y - r));
                if (s.Warnings.Count > 0 || _violations.Any(v => v.Handle == s.Handle))
                    _graphic.Children.Add(new WS.Ellipse { Width = 5, Height = 5, Fill = T.Warn }.At(c.X + r - 2, c.Y - r - 2));
                if (_showNames && (scale > 0.3 || hi))
                    Label(s.Name, c.X + r + 2, c.Y - r - 4, 10, hi ? HighlightBrush : GraphicText);
            }
        }

        DrawRulers(w, h);
        _graphicInfo.Text = net is null ? "" : $"{net.Name}: {net.Structures.Count} estrutura(s), {net.Pipes.Count} tubo(s)" + (all && _snap.Networks.Count > 1 ? $"  |  {_snap.Networks.Count} redes (ativa em branco)" : "");
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
            if (px < RulerLeft)
                continue;
            _graphic.Children.Add(new WS.Line { X1 = px, Y1 = 0, X2 = px, Y2 = dh, Stroke = GraphicGridMajor, StrokeThickness = 1 });
        }

        for (double y = Math.Ceiling(y0 / step) * step; y <= y1; y += step)
        {
            double py = Math.Round(ToScreen(new P2(0, y)).Y) + 0.5;
            if (py > dh)
                continue;
            _graphic.Children.Add(new WS.Line { X1 = RulerLeft, Y1 = py, X2 = w, Y2 = py, Stroke = GraphicGridMajor, StrokeThickness = 1 });
        }
    }

    private void DrawRulers(double w, double h)
    {
        double dh = h - RulerBottom;
        _graphic.Children.Add(new WS.Rectangle { Width = RulerLeft, Height = dh, Fill = Brush(44, 46, 50) }.At(0, 0));
        _graphic.Children.Add(new WS.Rectangle { Width = w, Height = RulerBottom, Fill = Brush(44, 46, 50) }.At(0, dh));
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

    private void Arrow(SW.Point a, SW.Point b, WM.Brush brush)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt((dx * dx) + (dy * dy));
        if (len < 1e-6)
            return;
        dx /= len;
        dy /= len;
        SW.Point m = new((a.X + b.X) / 2 + (dx * 5), (a.Y + b.Y) / 2 + (dy * 5));
        WS.Polygon tri = new() { Fill = brush };
        tri.Points.Add(m);
        tri.Points.Add(new SW.Point(m.X - (dx * 10) - (dy * 4), m.Y - (dy * 10) + (dx * 4)));
        tri.Points.Add(new SW.Point(m.X - (dx * 10) + (dy * 4), m.Y - (dy * 10) - (dx * 4)));
        _graphic.Children.Add(tri);
    }

    private void Label(string text, double x, double y, double size = 11, WM.Brush? brush = null)
    {
        _graphic.Children.Add(new WC.TextBlock { Text = text, Foreground = brush ?? GraphicText, FontSize = size, FontFamily = MonoFont }.At(x, y));
    }

    /// <summary>Perfil do caminho de montante para jusante a partir de uma estrutura.</summary>
    private void RenderProfile(NetworkInfo net, string startHandle, string? highlight)
    {
        _profileShown = true;
        _graphic.Children.Clear();
        double w = _graphic.ActualWidth, h = _graphic.ActualHeight;
        if (w < 20 || h < 20)
            return;

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
        // Terreno (tampa ou superficie).
        WS.Polyline ground = new() { Stroke = GroundBrush, StrokeThickness = 1.5 };
        foreach ((double d, StructureInfo st) in nodes)
            ground.Points.Add(P(d, double.IsNaN(st.SurfaceElevation) ? st.Rim : st.SurfaceElevation));
        _graphic.Children.Add(ground);

        // Tubos: fundo e geratriz superior.
        double dist = 0;
        foreach (PipeInfo p in path)
        {
            bool hi = p.Handle == highlight;
            WM.Brush brush = hi ? HighlightBrush : PipeBrush;
            SW.Point a = P(dist, p.UpInvert), b = P(dist + p.Length2D, p.DownInvert);
            SW.Point a2 = P(dist, p.UpInvert + p.InnerDiameter), b2 = P(dist + p.Length2D, p.DownInvert + p.InnerDiameter);
            _graphic.Children.Add(new WS.Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = brush, StrokeThickness = hi ? 2.5 : 1.5 });
            _graphic.Children.Add(new WS.Line { X1 = a2.X, Y1 = a2.Y, X2 = b2.X, Y2 = b2.Y, Stroke = brush, StrokeThickness = hi ? 2.5 : 1.5 });
            HydroResult? r = ResultOf(p.Handle);
            if (r is not null && r.DepthRatio > 0)
            {
                SW.Point wa = P(dist, p.UpInvert + (r.DepthRatio * p.InnerDiameter)), wb = P(dist + p.Length2D, p.DownInvert + (r.DepthRatio * p.InnerDiameter));
                _graphic.Children.Add(new WS.Line { X1 = wa.X, Y1 = wa.Y, X2 = wb.X, Y2 = wb.Y, Stroke = Brush(80, 160, 255), StrokeThickness = 1, StrokeDashArray = new WM.DoubleCollection { 3, 2 } });
            }

            if (_showNames)
                Label($"{p.Name}  D{Mm(p.InnerDiameter)}  i={Pct(p.Slope)}  L={N(p.Length2D, 1)}", (a.X + b.X) / 2 - 40, Math.Min(a.Y, b.Y) + 6, 10, hi ? HighlightBrush : GraphicText);
            dist += p.Length2D;
        }

        // Estruturas: retangulo da tampa ao fundo.
        foreach ((double d, StructureInfo st) in nodes)
        {
            bool hi = st.Handle == highlight;
            double half = Math.Max(Math.Max(st.InnerDiameter, 0.8) * _vScale / 2, 3);
            SW.Point top = P(d, st.Rim), bottom = P(d, st.Sump);
            _graphic.Children.Add(new WS.Rectangle { Width = half * 2, Height = Math.Max(bottom.Y - top.Y, 1), Stroke = hi ? HighlightBrush : StructureBrush, StrokeThickness = hi ? 2.5 : 1.4, Fill = GraphicBack }.At(top.X - half, top.Y));
            if (_showNames)
            {
                Label(st.Name, top.X - half, top.Y - 16, 10, hi ? HighlightBrush : GraphicText);
                Label($"T {N(st.Rim, 2)} F {N(st.Sump, 2)}", top.X + half + 2, bottom.Y - 12, 9, GraphicText);
            }
        }

        // Reguas: distancia embaixo e cota a esquerda.
        _graphic.Children.Add(new WS.Rectangle { Width = RulerLeft, Height = dh, Fill = Brush(44, 46, 50) }.At(0, 0));
        _graphic.Children.Add(new WS.Rectangle { Width = w, Height = RulerBottom, Fill = Brush(44, 46, 50) }.At(0, dh));
        double stepX = NiceStep(total / 8);
        for (double x = 0; x <= total + 1e-6; x += stepX)
            Label(x.ToString("0", CultureInfo.InvariantCulture), P(x, minZ).X + 2, dh + 1, 9, GraphicText);
        double stepZ = NiceStep((maxZ - minZ) / 6);
        for (double z = Math.Floor(minZ / stepZ) * stepZ; z <= maxZ + stepZ; z += stepZ)
        {
            double py = P(0, z).Y;
            if (py > 0 && py < dh)
            {
                Label(z.ToString("0.0", CultureInfo.InvariantCulture), 2, py - 6, 9, GraphicText);
                _graphic.Children.Add(new WS.Line { X1 = RulerLeft, Y1 = py, X2 = w, Y2 = py, Stroke = GraphicGrid, StrokeThickness = 1 });
            }
        }

        _graphicInfo.Text = $"Perfil de {first.Name} para jusante: {path.Count} trecho(s), {N(total, 1)} m, exagero vertical {N(exag, 1)}x";
        _coords.Text = "";
    }

    /// <summary>Curva IDF corrente na categoria Calculo.</summary>
    private void RenderIdf()
    {
        _profileShown = true;
        _graphic.Children.Clear();
        double w = _graphic.ActualWidth, h = _graphic.ActualHeight;
        if (w < 20 || h < 20)
            return;
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
            WS.Polyline line = new() { Stroke = trs[k] == s.ReturnPeriod ? HighlightBrush : brushes[k], StrokeThickness = trs[k] == s.ReturnPeriod ? 2.5 : 1.2 };
            for (double t = 5; t <= tMax; t += 2.5)
                line.Points.Add(P(t, s.Idf.Intensity(t, trs[k])));
            _graphic.Children.Add(line);
            Label($"Tr {trs[k]}", P(tMax, s.Idf.Intensity(tMax, trs[k])).X - 40, P(tMax, s.Idf.Intensity(tMax, trs[k])).Y - 14, 10, brushes[k]);
        }

        _graphic.Children.Add(new WS.Rectangle { Width = RulerLeft, Height = dh, Fill = Brush(44, 46, 50) }.At(0, 0));
        _graphic.Children.Add(new WS.Rectangle { Width = w, Height = RulerBottom, Fill = Brush(44, 46, 50) }.At(0, dh));
        for (double t = 0; t <= tMax; t += 20)
            Label(t.ToString("0", CultureInfo.InvariantCulture), P(t, 0).X + 2, dh + 1, 9, GraphicText);
        double stepI = NiceStep(iMax / 6);
        for (double i = 0; i <= iMax; i += stepI)
            Label(i.ToString("0", CultureInfo.InvariantCulture), 2, P(0, i).Y - 6, 9, GraphicText);
        _graphicInfo.Text = $"IDF {s.Idf.Name}: i = {s.Idf.Formula()}  (t em min, i em mm/h; Tr {s.ReturnPeriod} anos em destaque)";
        _coords.Text = "";
    }
}

internal static class HydroCanvasExtensions
{
    public static T At<T>(this T element, double left, double top) where T : SW.UIElement
    {
        WC.Canvas.SetLeft(element, left);
        WC.Canvas.SetTop(element, top);
        return element;
    }
}
