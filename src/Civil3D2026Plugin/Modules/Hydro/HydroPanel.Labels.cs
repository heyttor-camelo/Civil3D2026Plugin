using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using SW = System.Windows;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Rotulos e lotes do monitor: textos com fundo (legiveis sobre tubo e grade), posicionados sem
/// sobrepor (grade espacial de ocupacao), nomes dos tubos ao longo do tubo; FormattedText em
/// cache; tubos e setas desenhados em lote (uma geometria por cor/espessura, em vez de uma
/// chamada por peca).
/// </summary>
internal sealed partial class HydroPanel
{
    private static readonly WM.Typeface UiFace = new(new WM.FontFamily("Segoe UI"), SW.FontStyles.Normal, SW.FontWeights.Normal, SW.FontStretches.Normal);
    private static readonly WM.Typeface UiBold = new(new WM.FontFamily("Segoe UI"), SW.FontStyles.Normal, SW.FontWeights.SemiBold, SW.FontStretches.Normal);
    private static readonly WM.Brush HaloBrush = Frozen(new WM.SolidColorBrush(WM.Color.FromArgb(205, 34, 36, 40)));
    private static readonly WM.Brush HaloStrong = Frozen(new WM.SolidColorBrush(WM.Color.FromArgb(235, 28, 30, 34)));

    private readonly Dictionary<(string, double, WM.Brush, bool), WM.FormattedText> _texts = new();
    private readonly Dictionary<long, List<SW.Rect>> _labelGrid = new();
    private double _textDip = -1;
    private const double LabelCell = 80;

    /// <summary>Zera a ocupacao dos rotulos (inicio de cada desenho).</summary>
    private void ResetLabels()
    {
        _labelGrid.Clear();
        if (Math.Abs(_textDip - _dip) > 1e-6)
        {
            _texts.Clear();
            _textDip = _dip;
        }
    }

    private WM.FormattedText Ft(string text, double size, WM.Brush brush, bool bold = false)
    {
        (string, double, WM.Brush, bool) key = (text, size, brush, bold);
        if (_texts.TryGetValue(key, out WM.FormattedText? ft))
            return ft;
        if (_texts.Count > 8000)
            _texts.Clear();
        ft = new WM.FormattedText(text, CultureInfo.InvariantCulture, SW.FlowDirection.LeftToRight, bold ? UiBold : UiFace, size, brush, _dip);
        _texts[key] = ft;
        return ft;
    }

    private static IEnumerable<long> Cells(SW.Rect r)
    {
        int x0 = (int)Math.Floor(r.Left / LabelCell), x1 = (int)Math.Floor(r.Right / LabelCell);
        int y0 = (int)Math.Floor(r.Top / LabelCell), y1 = (int)Math.Floor(r.Bottom / LabelCell);
        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
                yield return ((long)x << 32) ^ (uint)y;
        }
    }

    private bool Free(SW.Rect r)
    {
        foreach (long k in Cells(r))
        {
            if (!_labelGrid.TryGetValue(k, out List<SW.Rect>? list))
                continue;
            foreach (SW.Rect o in list)
            {
                if (o.IntersectsWith(r))
                    return false;
            }
        }

        return true;
    }

    /// <summary>Reserva uma area (rotulo ou simbolo) para os proximos rotulos nao cobrirem.</summary>
    private void Occupy(SW.Rect r)
    {
        foreach (long k in Cells(r))
        {
            if (!_labelGrid.TryGetValue(k, out List<SW.Rect>? list))
                _labelGrid[k] = list = new List<SW.Rect>();
            list.Add(r);
        }
    }

    /// <summary>
    /// Rotulo em volta de um ponto: tenta direita-cima, direita-baixo, esquerda-cima,
    /// esquerda-baixo, cima e baixo; fica na primeira posicao livre (ou na primeira, se forcado).
    /// </summary>
    private bool LabelAround(string text, SW.Point anchor, double gap, double size, WM.Brush brush, bool force = false, bool bold = false, WM.Brush? halo = null)
    {
        if (_dc is null || text.Length == 0)
            return false;
        WM.FormattedText ft = Ft(text, size, brush, bold);
        double w = ft.Width + 6, h = ft.Height + 1;
        Span<SW.Point> spots = stackalloc SW.Point[]
        {
            new(anchor.X + gap, anchor.Y - gap - h),
            new(anchor.X + gap, anchor.Y + gap),
            new(anchor.X - gap - w, anchor.Y - gap - h),
            new(anchor.X - gap - w, anchor.Y + gap),
            new(anchor.X - (w / 2), anchor.Y - gap - h),
            new(anchor.X - (w / 2), anchor.Y + gap)
        };
        foreach (SW.Point p in spots)
        {
            SW.Rect r = new(p.X, p.Y, w, h);
            if (!Free(r))
                continue;
            DrawLabel(ft, r, halo ?? HaloBrush);
            return true;
        }

        if (!force)
            return false;
        DrawLabel(ft, new SW.Rect(spots[0].X, spots[0].Y, w, h), halo ?? HaloStrong);
        return true;
    }

    /// <summary>Rotulo com o canto superior esquerdo dado (so se couber, ou forcado).</summary>
    private bool LabelAt(string text, double x, double y, double size, WM.Brush brush, bool force = false, bool bold = false)
    {
        if (_dc is null || text.Length == 0)
            return false;
        WM.FormattedText ft = Ft(text, size, brush, bold);
        SW.Rect r = new(x, y, ft.Width + 6, ft.Height + 1);
        if (!force && !Free(r))
            return false;
        DrawLabel(ft, r, HaloBrush);
        return true;
    }

    private void DrawLabel(WM.FormattedText ft, SW.Rect r, WM.Brush halo)
    {
        _dc!.DrawRoundedRectangle(halo, null, r, 2.5, 2.5);
        _dc.DrawText(ft, new SW.Point(r.X + 3, r.Y + 0.5));
        Occupy(r);
    }

    /// <summary>
    /// Rotulo ao longo de um segmento (nome do tubo), girado para ler da esquerda para a direita,
    /// afastado da linha; so se couber no comprimento e nao sobrepor.
    /// </summary>
    private bool LabelAlong(string text, SW.Point a, SW.Point b, double offset, double size, WM.Brush brush, bool force = false)
    {
        if (_dc is null || text.Length == 0)
            return false;
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt((dx * dx) + (dy * dy));
        WM.FormattedText ft = Ft(text, size, brush);
        double w = ft.Width + 6, h = ft.Height + 1;
        if (len < 1 || (!force && w > len * 0.8))
            return false;
        double ang = Math.Atan2(dy, dx);
        if (ang > Math.PI / 2)
            ang -= Math.PI;
        else if (ang < -Math.PI / 2)
            ang += Math.PI;
        double nx = Math.Sin(ang), ny = -Math.Cos(ang);
        foreach (double side in new[] { 1.0, -1.0 })
        {
            double dist = offset + (h / 2);
            SW.Point c = new(((a.X + b.X) / 2) + (nx * dist * side), ((a.Y + b.Y) / 2) + (ny * dist * side));
            double cos = Math.Abs(Math.Cos(ang)), sin = Math.Abs(Math.Sin(ang));
            double bw = (w * cos) + (h * sin), bh = (w * sin) + (h * cos);
            SW.Rect box = new(c.X - (bw / 2), c.Y - (bh / 2), bw, bh);
            if (!force && !Free(box))
                continue;
            _dc.PushTransform(new WM.RotateTransform(ang * 180 / Math.PI, c.X, c.Y));
            SW.Rect r = new(c.X - (w / 2), c.Y - (h / 2), w, h);
            _dc.DrawRoundedRectangle(HaloBrush, null, r, 2.5, 2.5);
            _dc.DrawText(ft, new SW.Point(r.X + 3, r.Y + 0.5));
            _dc.Pop();
            Occupy(box);
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Lotes
    // ------------------------------------------------------------------

    private readonly Dictionary<(WM.Brush, double), List<(SW.Point A, SW.Point B)>> _lineBatch = new();
    private readonly Dictionary<WM.Brush, List<SW.Point[]>> _arrowBatch = new();

    private void BatchLine(SW.Point a, SW.Point b, WM.Brush brush, double thickness)
    {
        (WM.Brush, double) key = (brush, Math.Round(thickness * 2) / 2);
        if (!_lineBatch.TryGetValue(key, out List<(SW.Point, SW.Point)>? list))
            _lineBatch[key] = list = new List<(SW.Point, SW.Point)>();
        list.Add((a, b));
    }

    /// <summary>Setas do fluxo em lote: cabecas cheias com contorno, uma a cada ~110 px.</summary>
    private void BatchArrows(SW.Point a, SW.Point b, WM.Brush brush, double thickness)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt((dx * dx) + (dy * dy));
        double size = Math.Clamp(thickness * 2.2, 7, 14);
        if (len < size * 2.2)
            return;
        dx /= len;
        dy /= len;
        if (!_arrowBatch.TryGetValue(brush, out List<SW.Point[]>? list))
            _arrowBatch[brush] = list = new List<SW.Point[]>();
        int count = Math.Max(1, (int)(len / 110));
        for (int i = 1; i <= count; i++)
        {
            double t = len * i / (count + 1);
            SW.Point tip = new(a.X + (dx * (t + (size / 2))), a.Y + (dy * (t + (size / 2))));
            SW.Point back = new(tip.X - (dx * size), tip.Y - (dy * size));
            double half = size * 0.55;
            list.Add(new[]
            {
                tip,
                new SW.Point(back.X - (dy * half), back.Y + (dx * half)),
                new SW.Point(back.X + (dx * size * 0.25), back.Y + (dy * size * 0.25)),
                new SW.Point(back.X + (dy * half), back.Y - (dx * half))
            });
        }
    }

    /// <summary>Desenha os lotes acumulados (linhas e depois setas por cima) e esvazia.</summary>
    private void FlushBatches()
    {
        if (_dc is null)
            return;
        foreach (KeyValuePair<(WM.Brush, double), List<(SW.Point A, SW.Point B)>> kv in _lineBatch)
        {
            WM.StreamGeometry g = new();
            using (WM.StreamGeometryContext ctx = g.Open())
            {
                foreach ((SW.Point a, SW.Point b) in kv.Value)
                {
                    ctx.BeginFigure(a, false, false);
                    ctx.LineTo(b, true, false);
                }
            }

            g.Freeze();
            _dc.DrawGeometry(null, Pen(kv.Key.Item1, kv.Key.Item2), g);
        }

        WM.Pen outline = Pen(GraphicBack, 1.2);
        foreach (KeyValuePair<WM.Brush, List<SW.Point[]>> kv in _arrowBatch)
        {
            WM.StreamGeometry g = new();
            using (WM.StreamGeometryContext ctx = g.Open())
            {
                foreach (SW.Point[] tri in kv.Value)
                {
                    ctx.BeginFigure(tri[0], true, true);
                    ctx.PolyLineTo(new[] { tri[1], tri[2], tri[3] }, true, true);
                }
            }

            g.Freeze();
            _dc.DrawGeometry(kv.Key, outline, g);
        }

        _lineBatch.Clear();
        _arrowBatch.Clear();
    }
}
