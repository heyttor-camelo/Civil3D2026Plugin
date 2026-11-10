using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
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

        // Linha d'agua = pe da face do meio-fio (ponto FLUXO_SARJETA / Flowline_Gutter): o ponto mais
        // baixo do topo da peca SEM contar a sarjeta, cujo outro lado e a borda da pista (mais baixa ou
        // mais alta conforme a inclinacao) e dava altura errada (13,3 em vez de 11,5 cm).
        double top = double.MinValue, flow = double.MaxValue;
        Point3d? first = null;
        foreach (ModelLink l in links)
        {
            if (IsWalk(l.Codes))
                continue;
            bool curb = Has(l.Codes, "Curb");
            bool topCode = Has(l.Codes, "Top") || Has(l.Codes, "TOPO");
            bool gutter = Has(l.Codes, "Sarjeta") || Has(l.Codes, "Gutter");
            foreach (Point3d q in l.Soe)
            {
                first ??= q;
                if (curb || topCode)
                    top = Math.Max(top, q.Z);
                if (topCode && !gutter)
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

    /// <summary>
    /// Pontos do meio-fio numa secao, em estaca (X), offset (Y) e elevacao (Z): quina do topo do lado
    /// da pista, pe da face (linha d'agua) e quina do topo do lado da calcada (fundo).
    /// </summary>
    private static (Point3d Top, Point3d Flow, Point3d Back)? CurbMarks(ModelSection s, long key)
    {
        List<ModelLink>? links = s.Subassemblies.FirstOrDefault(x => x.Key == key).Links;
        if (links is null || CurbOf(s, key) is not { } c)
            return null;

        List<Point3d> tops = new(), flows = new(), curbs = new();
        foreach (ModelLink l in links)
        {
            if (IsWalk(l.Codes))
                continue;
            bool curb = Has(l.Codes, "Curb");
            bool topCode = Has(l.Codes, "Top") || Has(l.Codes, "TOPO");
            bool gutter = Has(l.Codes, "Sarjeta") || Has(l.Codes, "Gutter");
            foreach (Point3d q in l.Soe)
            {
                if (curb || topCode)
                    tops.Add(q);
                if (topCode && !gutter)
                    flows.Add(q);
                if (curb)
                    curbs.Add(q);
            }
        }

        if (tops.Count == 0)
            return null;
        double side(Point3d q) => Math.Abs(q.Y - c.Origin.Y);
        Point3d top = tops.Where(q => q.Z > c.Top - 0.002).OrderBy(side).First();
        Point3d flow = flows.Count == 0 ? c.Origin : flows.Where(q => q.Z < c.Flow + 0.002).OrderBy(q => Math.Abs(q.Y - top.Y)).First();
        List<Point3d> pool = curbs.Count > 0 ? curbs : tops;
        double far = pool.Max(side);
        Point3d back = pool.Where(q => side(q) > far - 0.002).OrderByDescending(q => q.Z).First();
        return (top, flow, back);
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
        double lastMarkX = double.MinValue;
        bool lowRow = false;
        foreach ((string name, double st) in new[] { ("S0", r.S0), ("S1", r.S1), ("S2", r.S2), ("S3", r.S3) })
        {
            if (st < sa || st > sb)
                continue;
            ChartLine(X(st), top - 4, X(st), h - bottom, markBrush, 0.8, dashed: true);
            // Marcos colados (rampa curta): o rotulo seguinte desce uma linha para nao sobrepor.
            lowRow = X(st) - lastMarkX < 26 && !lowRow;
            lastMarkX = X(st);
            if (Show("MARCOS"))
                PlanLabel(name, X(st) + 3, top - 6 + (lowRow ? 13 : 0), 11, markBrush);
        }

        // Secao escolhida na aba Secoes / mostrada na vista Secao.
        if (_secMark is double sm && sm >= sa && sm <= sb)
        {
            WM.Brush secBrush = ToolTheme.Brush(90, 200, 255);
            ChartLine(X(sm), top + 12, X(sm), h - bottom, secBrush, 1.2);
            PlanLabel("secao " + N(sm, "0.00"), X(sm) + 3, h - bottom - 16, 10, secBrush);
        }

        // Altura do meio-fio (topo - linha d'agua) nos pontos que interessam (dentro da area do grafico).
        foreach (double st in new[] { sa, r.S0, (r.S1 + r.S2) / 2.0, r.S3, sb }.Distinct())
        {
            (double St, double Top, double Flow) n = pts.OrderBy(p => Math.Abs(p.St - st)).First();
            // Ao lado da linha do marco (centrado, o tracejado de S0/S3 cortava o numero).
            double lx = Math.Clamp(X(n.St) + 4, left + 2, w - right - 48);
            PlanLabel($"{(n.Top - n.Flow) * 100:0.0} cm", lx, Math.Max(top + 14, Y(n.Top) - 18), 11);
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

    // Estaca pedida pela aba Secoes (consumida no proximo desenho) e a estaca mostrada (marca no perfil).
    private double? _secWanted;
    private double? _secMark;
    private WC.ListView? _sectionsList;
    private bool _syncingSections;

    private void StepSection(int delta)
    {
        _secIndex = (_secIndex ?? 0) + delta;
        RenderGraphic();
    }

    /// <summary>Aba Secoes: leva a vista Secao (e a marca do Perfil) para a estaca da linha clicada.</summary>
    private void GoToSection(double station, bool open)
    {
        _secWanted = station;
        _secMark = station;
        if (open && Mode != "SECAO")
        {
            SetMode("SECAO");
            return;
        }

        if (Mode is "SECAO" or "PERFIL")
            RenderGraphic();
    }

    /// <summary>Vista Secao andou (anterior/proxima): marca a mesma estaca na tabela da aba Secoes.</summary>
    private void SelectSectionRow(double station)
    {
        WC.ListView? lv = _sectionsList;
        if (lv is null || _syncingSections)
            return;
        try
        {
            foreach (object o in lv.Items)
            {
                if (o is not WC.ListViewItem { Tag: Row { Data: RebaixoSection sec } } item || Math.Abs(sec.Station - station) > 0.002)
                    continue;
                if (ReferenceEquals(lv.SelectedItem, item))
                    return;
                _syncingSections = true;
                lv.SelectedItem = item;
                lv.ScrollIntoView(item);
                return;
            }
        }
        catch
        {
            // Tabela ja descartada (outra aba): nada a marcar.
        }
        finally
        {
            _syncingSections = false;
        }
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

        if (_secWanted is double want)
        {
            _secIndex = secs.Select((s, i) => (s, i)).OrderBy(x => Math.Abs(x.s.Station - want)).First().i;
            _secWanted = null;
        }

        double mid = (r.S1 + r.S2) / 2.0;
        int idx = _secIndex ?? secs.Select((s, i) => (s, i)).OrderBy(x => Math.Abs(x.s.Station - mid)).First().i;
        idx = Math.Clamp(idx, 0, secs.Count - 1);
        _secIndex = idx;

        ModelSection cur = secs[idx];
        _secMark = cur.Station;
        SelectSectionRow(cur.Station);
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
        // Elevacoes dos pontos do meio-fio (pedido do Heyvid em 10/11/2026).
        if (Show("ELEVACOES") && CurbMarks(cur, key) is { } marks)
        {
            // Rotulos para os lados (topo e linha d'agua para o lado da pista, fundo para o lado da
            // calcada): centrados em cima do ponto, topo e fundo (7 cm um do outro) se empilhavam e a
            // linha d'agua caia em cima das linhas do meio-fio (teste de 10/11/2026).
            double road = P(c.Origin.Y - c.Origin.Y, 0).X >= P(marks.Top.Y - c.Origin.Y, 0).X ? 1 : -1;
            List<SW.Rect> used = new();
            void Mark(Point3d q, string name, bool above, double dir)
            {
                SW.Point sp = P(q.Y - c.Origin.Y, q.Z - c.Origin.Z);
                var dot = new WS.Ellipse { Width = 6, Height = 6, Fill = ToolTheme.Brush(90, 200, 255), IsHitTestVisible = false };
                WC.Canvas.SetLeft(dot, sp.X - 3);
                WC.Canvas.SetTop(dot, sp.Y - 3);
                _plan.Children.Add(dot);
                string text = $"{name}  {N(q.Z)}";
                double tw = (text.Length * 6.2) + 6;
                double x = Math.Clamp(dir > 0 ? sp.X + 8 : sp.X - tw - 8, 4, Math.Max(4, w - tw - 4));
                double y = above ? sp.Y - 26 : sp.Y + 6;
                for (int k = 0; k < 6 && used.Any(u => u.IntersectsWith(new SW.Rect(x, y, tw, 14))); k++)
                    y += above ? -15 : 15;
                y = Math.Clamp(y, 42, h - 18);
                used.Add(new SW.Rect(x, y, tw, 14));
                ChartLine(sp.X, sp.Y, dir > 0 ? x : x + tw, above ? y + 14 : y, ToolTheme.Brush(90, 200, 255, 140), 0.8);
                PlanLabel(text, x, y, 11, ToolTheme.Brush(150, 220, 255));
            }

            Mark(marks.Top, "topo do meio-fio", true, road);
            if (Math.Abs(marks.Back.Y - marks.Top.Y) > 0.02)
                Mark(marks.Back, "fundo do meio-fio", true, -road);
            // Linha d'agua acima da sarjeta (embaixo ficam as camadas do pavimento).
            Mark(marks.Flow, "linha d'agua", true, road);
            if (Math.Abs(c.Origin.Y - marks.Flow.Y) > 0.02)
                Mark(c.Origin, "borda da pista", false, road);
        }

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

    private sealed record Face3D(int Model, long Key, int Link, int Zone, string[] Codes);

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

    // 3D geral: todos os corredores com todos os rebaixos (senao, so o trecho do rebaixo selecionado).
    private bool _tudo3D;
    private List<RebaixoModel>? _models3D;
    private string? _modelsKey3D;
    private bool _near3D;
    private string? _focus3D;
    private List<RebaixoModel> _shown3D = new();
    // Para cada modelo mostrado: os rebaixos dele e a chave do meio-fio de cada um.
    private List<List<(RebaixoInfo R, long Key)>> _owners3D = new();
    private W3.Point3D _min3, _max3;
    private List<W3.Point3D> _fit3D = new();

    private void Set3DAll(bool all)
    {
        _tudo3D = all;
        _key3D = null;
        BuildViewToggles();
        RenderGraphic();
    }

    private static string FocusKey(RebaixoInfo r) => $"{r.CorridorId.Handle.Value}|{r.SetName}";

    private W3.Point3D Loc(Point3d p) => new(p.X - _cx3, p.Y - _cy3, (p.Z - _z03) * _exagero3D);

    private SW.UIElement Build3DHost()
    {
        _host3D.Background = ToolTheme.GraphicBack;
        _viewport.Camera = new W3.PerspectiveCamera { FieldOfView = 40, NearPlaneDistance = 0.05, FarPlaneDistance = 100000 };
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
            Text = "arraste: girar   |   botao do meio: mover   |   roda: aproximar   |   clique num rebaixo: ficha dele   |   clique em outra face: medidas",
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
            _dist = Math.Clamp(_dist * (e.Delta > 0 ? 0.85 : 1.18), 0.4, 50000);
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

    /// <summary>Modelos do 3D: so o trecho do rebaixo selecionado, ou todos os corredores ("tudo").</summary>
    private List<RebaixoModel> Models3D()
    {
        if (!_tudo3D)
        {
            RebaixoModel? one = CurrentModel();
            return one is null ? new List<RebaixoModel>() : new List<RebaixoModel> { one };
        }

        if (Doc is not Document doc)
            return new List<RebaixoModel>();
        string key = "ALL|" + string.Join(";", _items.Select(i => $"{FocusKey(i)}|{N(i.S0)}|{N(i.S3)}"));
        if (_models3D is not null && key == _modelsKey3D)
            return _models3D;
        try
        {
            using (doc.LockDocument())
                _models3D = MFRebaixoData.ModelsAll(doc.Database, _items, Math.Max(15.0, _settings.PainelMargem), out _near3D);
            _modelsKey3D = key;
            _key3D = null;
            if (_near3D)
                Say("Corredores grandes: o 3D geral mostra so o entorno dos rebaixos.");
        }
        catch (System.Exception ex)
        {
            _models3D = null;
            _modelsKey3D = null;
            Say("Erro ao ler os corredores: " + ex.Message);
            return new List<RebaixoModel>();
        }

        return _models3D;
    }

    private void Render3D()
    {
        RebaixoInfo? r = _current;
        List<RebaixoModel> models = Models3D().Where(m => m.Sections.Count >= 2).ToList();
        if (models.Count == 0)
        {
            _viewport.Children.Clear();
            _faces.Clear();
            _picked = null;
            _key3D = null;
            _shown3D = new List<RebaixoModel>();
            _info3D.Text = !_tudo3D && r is null ? "Selecione um rebaixo (ou use \"tudo\" para ver todos os corredores)." : "Sem secoes calculadas (Rebuild no corredor?).";
            _info3D.Visibility = SW.Visibility.Visible;
            return;
        }

        string key3D = $"{(_tudo3D ? _modelsKey3D : _modelKey)}|{_items.Count}|{_exagero3D:0.##}";
        if (key3D == _key3D)
        {
            // 3D geral: escolher outro rebaixo na lista leva a camera ate ele, sem refazer a cena.
            if (_tudo3D && r is not null && FocusKey(r) != _focus3D)
                Focus3D(r);
            return;
        }

        _key3D = key3D;
        _viewport.Children.Clear();
        _faces.Clear();
        _picked = null;
        _info3D.Visibility = SW.Visibility.Collapsed;
        _shown3D = models;

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (Point3d p in models.SelectMany(m => m.Sections.SelectMany(s => s.Subassemblies.SelectMany(a => a.Links.SelectMany(l => l.Points)))))
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            minZ = Math.Min(minZ, p.Z);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
            maxZ = Math.Max(maxZ, p.Z);
        }

        if (minX == double.MaxValue)
            return;
        _cx3 = (minX + maxX) / 2.0;
        _cy3 = (minY + maxY) / 2.0;
        _z03 = minZ;
        _min3 = Loc(new Point3d(minX, minY, minZ));
        _max3 = Loc(new Point3d(maxX, maxY, maxZ));
        // Pontos para enquadrar (as pontas de cada link, no maximo ~6000).
        List<Point3d> ends = models.SelectMany(m => m.Sections.SelectMany(s => s.Subassemblies.SelectMany(a => a.Links.Where(l => l.Points.Length > 0).SelectMany(l => new[] { l.Points[0], l.Points[^1] })))).ToList();
        int stride = Math.Max(1, ends.Count / 6000);
        _fit3D = ends.Where((_, i) => i % stride == 0).Select(Loc).ToList();

        // Rebaixos de cada modelo (todos, nao so o selecionado) e o meio-fio de cada um.
        _owners3D = new List<List<(RebaixoInfo R, long Key)>>();
        foreach (RebaixoModel m in models)
        {
            List<(RebaixoInfo R, long Key)> owners = new();
            IEnumerable<RebaixoInfo> pool = r is not null && !_items.Contains(r) ? _items.Append(r) : _items;
            foreach (RebaixoInfo i in pool)
            {
                if (i.CorridorId == m.CorridorId && i.BaselineKey == m.BaselineKey && MfKey(m, i) is long k)
                    owners.Add((i, k));
            }

            _owners3D.Add(owners);
        }

        var meshes = new Dictionary<(int Model, long Key, int Link, int Zone), (W3.MeshGeometry3D Mesh, string[] Codes)>();
        for (int mi = 0; mi < models.Count; mi++)
        {
            RebaixoModel model = models[mi];
            List<(RebaixoInfo R, long Key)> owners = _owners3D[mi];
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
                        if (!IsWalk(la.Codes) && (Has(la.Codes, "Curb") || Has(la.Codes, "Top") || Has(la.Codes, "TOPO") || Has(la.Codes, "Sarjeta")))
                        {
                            foreach ((RebaixoInfo o, long ok) in owners)
                            {
                                if (ok == key)
                                    zone = Math.Max(zone, ZoneOf(o, midStation));
                            }
                        }

                        if (!meshes.TryGetValue((mi, key, j, zone), out (W3.MeshGeometry3D Mesh, string[] Codes) entry))
                        {
                            entry = (new W3.MeshGeometry3D(), la.Codes);
                            meshes[(mi, key, j, zone)] = entry;
                        }

                        for (int p = 0; p + 1 < la.Points.Length; p++)
                        {
                            int i0 = entry.Mesh.Positions.Count;
                            entry.Mesh.Positions.Add(Loc(la.Points[p]));
                            entry.Mesh.Positions.Add(Loc(la.Points[p + 1]));
                            entry.Mesh.Positions.Add(Loc(lb.Points[p + 1]));
                            entry.Mesh.Positions.Add(Loc(lb.Points[p]));
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
        }

        var group = new W3.Model3DGroup();
        group.Children.Add(new W3.AmbientLight(WM.Color.FromRgb(125, 125, 125)));
        group.Children.Add(new W3.DirectionalLight(WM.Color.FromRgb(190, 190, 190), new W3.Vector3D(-0.45, -0.3, -1)));
        group.Children.Add(new W3.DirectionalLight(WM.Color.FromRgb(70, 70, 70), new W3.Vector3D(0.5, 0.6, -0.3)));
        foreach (KeyValuePair<(int Model, long Key, int Link, int Zone), (W3.MeshGeometry3D Mesh, string[] Codes)> kv in meshes)
        {
            RebaixoStyle.Look look = RebaixoStyle.Of(kv.Value.Codes);
            WM.Color color = kv.Key.Zone == 2 ? WM.Color.FromRgb(225, 45, 60)
                : kv.Key.Zone == 1 ? WM.Color.FromRgb(245, 160, 40)
                : look.Color.A == 0 ? WM.Color.FromRgb(132, 134, 140) : look.Color;
            var material = new W3.DiffuseMaterial(new WM.SolidColorBrush(color));
            var gm = new W3.GeometryModel3D(kv.Value.Mesh, material) { BackMaterial = material };
            group.Children.Add(gm);
            _faces[gm] = new Face3D(kv.Key.Model, kv.Key.Key, kv.Key.Link, kv.Key.Zone, kv.Value.Codes);
        }

        _viewport.Children.Add(new W3.ModelVisual3D { Content = group });

        if (_tudo3D)
        {
            _focus3D = r is null ? null : FocusKey(r);
            FitAll3D();
        }
        else if (r is not null)
        {
            Focus3D(r);
        }
        else
        {
            FitAll3D();
        }
    }

    /// <summary>Enquadra tudo o que esta no 3D.</summary>
    private void FitAll3D()
    {
        if (_shown3D.Count == 0)
            return;
        _target = new W3.Point3D((_min3.X + _max3.X) / 2.0, (_min3.Y + _max3.Y) / 2.0, (_min3.Z + _max3.Z) / 2.0);
        _pitch = 40;
        // Distancia em que todos os pontos cabem na tela, nos dois sentidos: o FieldOfView da camera
        // e o horizontal e a vista e larga e baixa; so pela largura a parte de perto ficava fora da
        // tela (teste de 10/11/2026).
        double aspect = _host3D.ActualWidth > 1 && _host3D.ActualHeight > 1 ? _host3D.ActualHeight / _host3D.ActualWidth : 0.4;
        double tanH = Math.Tan(20.0 * Math.PI / 180.0), tanV = tanH * aspect;
        double yaw = _yaw * Math.PI / 180.0, pitch = _pitch * Math.PI / 180.0;
        var dir = new W3.Vector3D(Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch));
        W3.Vector3D right = W3.Vector3D.CrossProduct(-dir, new W3.Vector3D(0, 0, 1));
        right.Normalize();
        W3.Vector3D up = W3.Vector3D.CrossProduct(right, -dir);
        double need = 0;
        foreach (W3.Point3D q in _fit3D)
        {
            W3.Vector3D c = q - _target;
            double depth = W3.Vector3D.DotProduct(c, dir);
            need = Math.Max(need, depth + (Math.Abs(W3.Vector3D.DotProduct(c, right)) / tanH));
            need = Math.Max(need, depth + (Math.Abs(W3.Vector3D.DotProduct(c, up)) / tanV));
        }

        _dist = Math.Max(8, need * 1.08);
        UpdateCamera();
    }

    /// <summary>Leva a camera ate um rebaixo, do lado da pista.</summary>
    private void Focus3D(RebaixoInfo r)
    {
        _focus3D = FocusKey(r);
        double mid = (r.S1 + r.S2) / 2.0;
        for (int mi = 0; mi < _shown3D.Count && mi < _owners3D.Count; mi++)
        {
            RebaixoModel model = _shown3D[mi];
            if (model.CorridorId != r.CorridorId || model.BaselineKey != r.BaselineKey || MfKey(model, r) is not long key)
                continue;
            ModelSection? s = model.Sections.OrderBy(x => Math.Abs(x.Station - mid)).FirstOrDefault();
            List<Point3d> curb = s?.Subassemblies.FirstOrDefault(a => a.Key == key).Links?.SelectMany(l => l.Points).ToList() ?? new List<Point3d>();
            if (curb.Count == 0)
                continue;
            _target = Loc(new Point3d(curb.Average(p => p.X), curb.Average(p => p.Y), curb.Average(p => p.Z)));
            _dist = Math.Max(6, (r.TotalLength * 2.2) + 2);
            if (RoadSideYaw(model, r, key) is double yaw)
            {
                _yaw = yaw;
                _pitch = 32;
            }

            UpdateCamera();
            return;
        }

        if (r.Path.Count > 0)
        {
            _target = Loc(r.Path[r.Path.Count / 2]);
            _dist = Math.Max(6, (r.TotalLength * 2.2) + 2);
        }

        UpdateCamera();
    }

    /// <summary>
    /// Direcao da camera vinda do lado da pista e um pouco antes do rebaixo (olhando o meio-fio de
    /// frente, em diagonal). Sem isso a camera fixa caia atras do meio-fio, com a pista tapando a vista.
    /// O sentido das estacas vem de duas secoes vizinhas; o lado, do proprio rebaixo.
    /// </summary>
    private static double? RoadSideYaw(RebaixoModel model, RebaixoInfo r, long key)
    {
        double mid = (r.S1 + r.S2) / 2.0;
        List<(double St, double X, double Y)> c = new();
        foreach (ModelSection s in model.Sections)
        {
            List<Point3d> pts = s.Subassemblies.FirstOrDefault(a => a.Key == key).Links?.SelectMany(l => l.Points).ToList() ?? new List<Point3d>();
            if (pts.Count > 0)
                c.Add((s.Station, pts.Average(p => p.X), pts.Average(p => p.Y)));
        }

        if (c.Count < 2)
            return null;
        int i = Enumerable.Range(0, c.Count).OrderBy(k => Math.Abs(c[k].St - mid)).First();
        (double St, double X, double Y) a0 = c[Math.Max(0, i - 1)], a1 = c[Math.Min(c.Count - 1, i + 1)];
        double tx = a1.X - a0.X, ty = a1.Y - a0.Y, tl = Math.Sqrt((tx * tx) + (ty * ty));
        if (tl < 1e-6)
            return null;
        tx /= tl;
        ty /= tl;
        // Da baseline para o meio-fio: a esquerda do sentido das estacas e (-ty, tx).
        double nx = r.Left ? -ty : ty, ny = r.Left ? tx : -tx;
        // Do meio-fio para a pista (-n) e para tras na estaca (-t).
        double dx = (-nx * 0.85) - (tx * 0.5), dy = (-ny * 0.85) - (ty * 0.5);
        return Math.Atan2(dy, dx) * 180.0 / Math.PI;
    }

    /// <summary>Topo do meio-fio numa secao, em coordenadas do desenho (X, Y, elevacao).</summary>
    private static Point3d? CurbTopWorld(ModelSection s, long key)
    {
        List<ModelLink>? links = s.Subassemblies.FirstOrDefault(x => x.Key == key).Links;
        if (links is null)
            return null;
        Point3d? top = null;
        foreach (ModelLink l in links)
        {
            if (IsWalk(l.Codes) || !(Has(l.Codes, "Curb") || Has(l.Codes, "Top") || Has(l.Codes, "TOPO")))
                continue;
            foreach (Point3d q in l.Points)
            {
                if (top is null || q.Z > top.Value.Z + 1e-6)
                    top = q;
            }
        }

        return top;
    }

    private static string CurbTypeName(string code) => code.ToUpperInvariant() switch
    {
        "MF_T30" => "SUDECAP 30",
        "MF_T40" => "SUDECAP 40",
        "MF_KASSEL" => "Kassel (guia de atracagem)",
        "MF_PERS" => "Personalizado",
        "MF_MOLDADO" => "Moldado no local",
        _ => code
    };

    /// <summary>
    /// Ficha do rebaixo para o clique no 3D: identificacao, tipo do meio-fio, comprimento e inclinacao
    /// das rampas, alturas e coordenadas dos quatro marcos (topo do meio-fio). Pedido do Heyvid em
    /// 10/11/2026, pensando na federacao do modelo BIM.
    /// </summary>
    private static List<string> RebaixoCard(RebaixoModel model, RebaixoInfo r, long key)
    {
        ModelSection? At(double st) => model.Sections.Where(s => CurbOf(s, key) is not null).OrderBy(s => Math.Abs(s.Station - st)).FirstOrDefault();
        double? Height(double st) => At(st) is ModelSection s && Math.Abs(s.Station - st) < 0.05 && CurbOf(s, key) is { } c ? c.Top - c.Flow : null;

        double? h0 = Height(r.S0), h1 = Height(r.S1), h2 = Height(r.S2), h3 = Height(r.S3);
        ModelSection? before = model.Sections.LastOrDefault(s => s.Station < r.S0 - 1e-4 && CurbOf(s, key) is not null);
        double? normal = h0 ?? (before is not null && CurbOf(before, key) is { } cb ? cb.Top - cb.Flow : r.NormalHeight);
        double? final = h1 is double a1 && h2 is double a2 ? (a1 + a2) / 2.0 : r.FinalHeight;

        string type = "";
        ModelSection? mid = At((r.S1 + r.S2) / 2.0);
        if (mid is not null && mid.ShapeCodes.TryGetValue(key, out string[]? shapeCodes))
            type = shapeCodes.FirstOrDefault(c => c.StartsWith("MF_", StringComparison.OrdinalIgnoreCase)) ?? "";
        string typeText = type.Length > 0 ? $"{CurbTypeName(type)}  ({type})" : "(nao informado pela peca)";

        string Slope(double? from, double? to, double length) =>
            from is double f && to is double t && length > 1e-4 ? $"{Math.Abs(f - t) / length * 100:0.00}%" : "-";

        var lines = new List<string>
        {
            "REBAIXO " + r.Label,
            $"Corredor: {r.CorridorName}",
            $"Baseline: {r.BaselineName}   lado {r.SideLabel.ToLowerInvariant()}",
            $"Peca: {r.SubassemblyName}",
            "Tipo de meio-fio: " + typeText,
            "",
            $"Rampa de entrada: {N(r.EntryLength)} m   inclinacao {Slope(h0, h1, r.EntryLength)}",
            $"Trecho rebaixado: {N(r.LoweredLength)} m",
            $"Rampa de saida: {N(r.ExitLength)} m   inclinacao {Slope(h3, h2, r.ExitLength)}",
            $"Comprimento total: {N(r.TotalLength)} m",
            "",
            $"Altura do meio-fio: normal {Cm(normal)}   rebaixado {Cm(final)}" +
                (normal is double nn && final is double ff ? $"   (desce {Cm(nn - ff)})" : ""),
            "",
            "Coordenadas do topo do meio-fio (X, Y, elevacao):"
        };
        foreach ((string name, double st) in new[] { ("S0 inicio da rampa", r.S0), ("S1 inicio do rebaixo", r.S1), ("S2 fim do rebaixo", r.S2), ("S3 fim da rampa", r.S3) })
        {
            Point3d? w = At(st) is ModelSection s && Math.Abs(s.Station - st) < 0.05 ? CurbTopWorld(s, key) : null;
            lines.Add(w is Point3d q
                ? $"  {name}: {q.X:0.000}  {q.Y:0.000}  {q.Z:0.000}   (estaca {N(st)})"
                : $"  {name}: estaca {N(st)} (sem secao calculada)");
        }

        return lines;
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
            ClearPick();
            if (WM.VisualTreeHelper.HitTest(_viewport, p) is not W3.RayMeshGeometry3DHitTestResult hit ||
                hit.ModelHit is not W3.GeometryModel3D gm || !_faces.TryGetValue(gm, out Face3D? face) ||
                face.Model >= _shown3D.Count)
            {
                _info3D.Visibility = SW.Visibility.Collapsed;
                return;
            }

            RebaixoModel model = _shown3D[face.Model];
            List<(RebaixoInfo R, long Key)> owners = face.Model < _owners3D.Count ? _owners3D[face.Model] : new List<(RebaixoInfo R, long Key)>();
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

            // Clique num rebaixo: a ficha do rebaixo (o que interessa para o modelo BIM), nao a da face.
            RebaixoInfo? hitOwner = best is null ? null
                : owners.Where(o => o.Key == face.Key && ZoneOf(o.R, best.Station) > 0).Select(o => o.R).FirstOrDefault();
            if (hitOwner is not null)
            {
                List<string> card = RebaixoCard(model, hitOwner, face.Key);
                card.Add("");
                card.Add($"Ponto clicado: estaca {N(best!.Station)} ({ZoneName(hitOwner, best.Station)})");
                card.Add($"   X {x:0.000}   Y {y:0.000}   elevacao {z:0.000}");
                _info3D.Text = string.Join("\n", card);
                _info3D.Visibility = SW.Visibility.Visible;
                return;
            }

            var lines = new List<string>
            {
                "Peca: " + (model.Names.TryGetValue(face.Key, out string? name) ? name : "?"),
                "Codes: " + (face.Codes.Length == 0 ? "(sem code)" : string.Join(", ", face.Codes))
            };
            if (best is not null && link is not null && link.Soe.Length > 1)
            {
                // Rebaixo dono da face: o do mesmo meio-fio cuja faixa S0-S3 pega a estaca.
                RebaixoInfo? owner = owners.Where(o => o.Key == face.Key && ZoneOf(o.R, best.Station) > 0).Select(o => o.R).FirstOrDefault();
                Point3d a = link.Soe[0], b = link.Soe[^1];
                double width = Math.Abs(b.Y - a.Y), dz = b.Z - a.Z;
                double length = 0;
                for (int i = 1; i < link.Soe.Length; i++)
                    length += Math.Sqrt(Math.Pow(link.Soe[i].Y - link.Soe[i - 1].Y, 2) + Math.Pow(link.Soe[i].Z - link.Soe[i - 1].Z, 2));
                if (owner is not null)
                    lines.Insert(0, "Rebaixo: " + owner.Label);
                lines.Add(owner is null ? $"Secao: estaca {N(best.Station)}" : $"Secao: estaca {N(best.Station)}  ({ZoneName(owner, best.Station)})");
                lines.Add($"Offset: {N(a.Y)} a {N(b.Y)}   largura {width:0.000} m");
                lines.Add($"Elevacao: {N(a.Z)} a {N(b.Z)}   desnivel {dz * 100:0.0} cm");
                lines.Add(width > 1e-4 ? $"Inclinacao: {Math.Abs(dz) / width * 100:0.00}%" : "Inclinacao: vertical");
                lines.Add($"Comprimento do link: {length:0.000} m");
                if (owners.Any(o => o.Key == face.Key) && CurbOf(best, face.Key) is { } c)
                    lines.Add($"Altura do meio-fio nesta secao: {(c.Top - c.Flow) * 100:0.0} cm   topo {N(c.Top)}");
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
