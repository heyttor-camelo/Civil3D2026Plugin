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

    /// <summary>Base (fundo enterrado) do meio-fio numa secao: a menor elevacao dos links com code Curb.</summary>
    private static double? CurbBottom(ModelSection s, long key)
    {
        List<ModelLink>? links = s.Subassemblies.FirstOrDefault(x => x.Key == key).Links;
        if (links is null)
            return null;
        double bottom = double.MaxValue;
        foreach (ModelLink l in links)
        {
            if (IsWalk(l.Codes) || !Has(l.Codes, "Curb"))
                continue;
            foreach (Point3d q in l.Soe)
                bottom = Math.Min(bottom, q.Z);
        }

        // MFSARJETA: os links nao tem "Curb"; o fundo vem da shape do meio-fio (GUIA / MF_T30).
        if (s.Shapes.TryGetValue(key, out List<ModelShape>? shapes))
        {
            foreach (ModelShape sh in shapes.Where(x => IsGuiaShape(x.Codes)))
            {
                foreach (Point3d q in sh.Soe)
                    bottom = Math.Min(bottom, q.Z);
            }
        }

        return bottom == double.MaxValue ? null : bottom;
    }

    /// <summary>Shape do meio-fio ou da sarjeta (o que muda de cor nas rampas e no trecho rebaixado).</summary>
    private static bool IsCurbShape(string[] codes) =>
        IsGuiaShape(codes) ||
        (!IsWalk(codes) && codes.Any(c => c.Contains("Sarjeta", StringComparison.OrdinalIgnoreCase) || c.Contains("Gutter", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Shape da peca do meio-fio em si. No MFSARJETA os codes da shape sao GUIA + o tipo (MF_T30, MF_T40...),
    /// sem "Curb": so pelo "Curb" o meio-fio ficava cinza no 3D e na secao (teste de 11/11/2026).
    /// </summary>
    private static bool IsGuiaShape(string[] codes) =>
        !IsWalk(codes) && (Has(codes, "Curb") || codes.Any(c =>
            c.StartsWith("GUIA", StringComparison.OrdinalIgnoreCase) || c.StartsWith("MF_", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Centro do rebaixo (o "CG" que identifica o rebaixo no modelo): centro em planta da faixa de S0 a
    /// S3 (borda da pista ate o fundo do meio-fio) e a elevacao media do topo do meio-fio no trecho.
    /// </summary>
    private static Point3d? RebaixoCenter(RebaixoInfo r, RebaixoModel? model)
    {
        double x, y, z;
        if (r.Band.Count >= 2)
        {
            // Centroide do poligono da faixa (ida pela borda de dentro, volta pela de fora).
            // Conta feita relativa ao primeiro ponto: com coordenadas UTM (600000, 7788000) os produtos
            // cruzados perdiam precisao e o centro caia ~1 km fora (teste de 11/11/2026).
            List<Point3d> poly = r.Band.Select(b => b.Inner).Concat(Enumerable.Reverse(r.Band).Select(b => b.Outer)).ToList();
            double rx = poly[0].X, ry = poly[0].Y;
            double area = 0, cx = 0, cy = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                double ax = poly[i].X - rx, ay = poly[i].Y - ry;
                double bx = poly[(i + 1) % poly.Count].X - rx, by = poly[(i + 1) % poly.Count].Y - ry;
                double cross = (ax * by) - (bx * ay);
                area += cross;
                cx += (ax + bx) * cross;
                cy += (ay + by) * cross;
            }

            if (Math.Abs(area) > 1e-9)
            {
                x = rx + (cx / (3 * area));
                y = ry + (cy / (3 * area));
            }
            else
            {
                x = poly.Average(q => q.X);
                y = poly.Average(q => q.Y);
            }

            z = poly.Average(q => q.Z);
        }
        else if (r.Path.Count > 0)
        {
            Point3d m = r.Path[r.Path.Count / 2];
            (x, y, z) = (m.X, m.Y, m.Z);
        }
        else
        {
            return null;
        }

        if (model is not null && model.CorridorId == r.CorridorId && model.BaselineKey == r.BaselineKey && MfKey(model, r) is long key)
        {
            List<double> tops = model.Sections.Where(s => s.Station >= r.S0 - 1e-4 && s.Station <= r.S3 + 1e-4)
                .Select(s => CurbTopWorld(s, key)).Where(q => q is not null).Select(q => q!.Value.Z).ToList();
            if (tops.Count > 0)
                z = tops.Average();
        }

        return new Point3d(x, y, z);
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

        // Base do meio-fio em cada secao (parte enterrada, abaixo da linha d'agua).
        Dictionary<double, double> bots = new();
        foreach (ModelSection s in model.Sections)
        {
            if (CurbBottom(s, key) is double bt)
                bots[s.Station] = bt;
        }

        double Bot((double St, double Top, double Flow) q) => bots.TryGetValue(q.St, out double bt) ? Math.Min(bt, q.Flow) : q.Flow;

        double sa = pts[0].St, sb = pts[^1].St;
        double zmin = pts.Min(Bot), zmax = pts.Max(p => p.Top);
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

        // Parte enterrada do meio-fio (abaixo da linha d'agua): mesma cor, bem transparente.
        var botLine = new WS.Polyline { Stroke = ToolTheme.GraphicLine, StrokeThickness = 1, StrokeDashArray = new WM.DoubleCollection { 4, 3 }, IsHitTestVisible = false };
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            (double St, double Top, double Flow) a = pts[i], b = pts[i + 1];
            if (Bot(a) >= a.Flow - 1e-4 && Bot(b) >= b.Flow - 1e-4)
                continue;
            int zone = ZoneOf(r, (a.St + b.St) / 2.0);
            var pg = new WS.Polygon { Fill = zone == 2 ? RebaixoBrush : zone == 1 ? RampBrush : normalFill, Opacity = 0.28, IsHitTestVisible = false };
            pg.Points.Add(new SW.Point(X(a.St), Y(a.Flow)));
            pg.Points.Add(new SW.Point(X(b.St), Y(b.Flow)));
            pg.Points.Add(new SW.Point(X(b.St), Y(Bot(b))));
            pg.Points.Add(new SW.Point(X(a.St), Y(Bot(a))));
            _plan.Children.Add(pg);
        }

        foreach ((double St, double Top, double Flow) q in pts)
            botLine.Points.Add(new SW.Point(X(q.St), Y(Bot(q))));
        if (bots.Count > 0)
            _plan.Children.Add(botLine);

        WM.Brush waterBrush = ToolTheme.Brush(80, 170, 255);
        var topLine = new WS.Polyline { Stroke = WM.Brushes.White, StrokeThickness = 2.5, IsHitTestVisible = false };
        var flowLine = new WS.Polyline { Stroke = waterBrush, StrokeThickness = 2, IsHitTestVisible = false };
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
            PlanLabel(N(Math.Abs(z) < zstep * 1e-6 ? 0 : z, "0.00"), 6, Y(z) - 7, 10);
        }

        PlanLabel("estaca (m)", w - right - 60, h - 15, 10);
        if (Show("LEGENDA"))
        {
            PlanLabel($"Perfil do meio-fio   |   exagero vertical {sy / sx:0.#}x", left + 8, 6, 11);
            ChartLine(left + 300, 14, left + 322, 14, WM.Brushes.White, 2.5);
            PlanLabel("topo do meio-fio", left + 326, 6, 11);
            ChartLine(left + 440, 14, left + 462, 14, waterBrush, 2);
            PlanLabel("linha d'agua (sarjeta)", left + 466, 6, 11);
            ChartLine(left + 610, 14, left + 632, 14, ToolTheme.GraphicLine, 1, dashed: true);
            PlanLabel("base do meio-fio (parte enterrada, transparente)", left + 636, 6, 11);
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

    // Zoom e arrasto da vista Secao (em pixels de tela) e chamadas arrastadas pelo usuario.
    private const double SecTop = 46, SecBottom = 30;
    private double _secZoom = 1, _secPanX, _secPanY;
    private readonly Dictionary<string, SW.Vector> _secLabelShift = new();

    private void ResetSectionView(bool labels)
    {
        _secZoom = 1;
        _secPanX = _secPanY = 0;
        if (labels)
            _secLabelShift.Clear();
    }

    private SW.Point SectionCenter() =>
        new(_plan.ActualWidth / 2.0, SecTop + ((_plan.ActualHeight - SecTop - SecBottom) / 2.0));

    private void SectionWheel(WI.MouseWheelEventArgs e)
    {
        SW.Point p = e.GetPosition(_plan);
        double nz = Math.Clamp(_secZoom * (e.Delta > 0 ? 1.25 : 0.8), 0.3, 80);
        double f = nz / _secZoom;
        SW.Point c0 = SectionCenter();
        // O ponto sob o cursor fica parado.
        _secPanX = p.X - c0.X - ((p.X - c0.X - _secPanX) * f);
        _secPanY = p.Y - c0.Y - ((p.Y - c0.Y - _secPanY) * f);
        _secZoom = nz;
        RenderCross();
    }

    private void SectionDrag(SW.Vector d)
    {
        _secPanX += d.X;
        _secPanY += d.Y;
        RenderCross();
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
        const double marginX = 24;
        double baseScale = Math.Min((w - (2 * marginX)) / Math.Max(umax - umin, 1e-6), (h - SecTop - SecBottom) / Math.Max(vmax - vmin, 1e-6));
        double scale = baseScale * _secZoom;
        SW.Point c0 = SectionCenter();
        double ox = c0.X - (((umin + umax) / 2.0) * scale) + _secPanX;
        double oy = c0.Y + (((vmin + vmax) / 2.0) * scale) + _secPanY;
        SW.Point P(double u, double v) => new(ox + (u * scale), oy - (v * scale));

        // Grade: elevacao (esquerda) e afastamento do eixo (embaixo), em valores reais do corredor.
        bool grid = Show("GRADE");
        double gridStep = NiceStep(80.0 / scale);
        if (grid)
        {
            WM.Brush gridBrush = ToolTheme.Brush(70, 72, 78);
            WM.Brush gridText = ToolTheme.Brush(150, 152, 158);
            string fmt = gridStep >= 1 ? "0" : gridStep >= 0.1 ? "0.0" : gridStep >= 0.01 ? "0.00" : "0.000";
            double a0 = c.Origin.Y - (ox / scale), a1 = c.Origin.Y + ((w - ox) / scale);
            for (double a = Math.Floor(a0 / gridStep) * gridStep; a <= a1; a += gridStep)
            {
                double x = ox + ((a - c.Origin.Y) * scale);
                if (x < 0 || x > w)
                    continue;
                ChartLine(x, 0, x, h, gridBrush, 1);
                PlanLabel(N(Math.Abs(a) < gridStep * 1e-6 ? 0 : a, fmt), x + 3, h - 15, 10, gridText);
            }

            double e0 = c.Origin.Z + ((oy - h) / scale), e1 = c.Origin.Z + (oy / scale);
            for (double el = Math.Floor(e0 / gridStep) * gridStep; el <= e1; el += gridStep)
            {
                double y = oy - ((el - c.Origin.Z) * scale);
                if (y < 0 || y > h)
                    continue;
                ChartLine(0, y, w, y, gridBrush, 1);
                if (y > SecTop && y < h - 22)
                    PlanLabel(N(Math.Abs(el) < gridStep * 1e-6 ? 0 : el, fmt), 4, y - 13, 10, gridText);
            }
        }

        int zone = ZoneOf(r, cur.Station);

        // Preenchimento das pecas (shapes): meio-fio e sarjeta mais fortes, vizinhos bem leves.
        foreach (KeyValuePair<long, List<ModelShape>> kv in cur.Shapes)
        {
            bool own = kv.Key == key;
            foreach (ModelShape sh in kv.Value)
            {
                RebaixoStyle.Look look = RebaixoStyle.Of(sh.Codes);
                WM.Color color = look.Color.A == 0 ? WM.Color.FromRgb(132, 134, 140) : look.Color;
                byte alpha = (byte)(own ? 95 : 45);
                if (own && zone > 0 && IsCurbShape(sh.Codes))
                {
                    color = zone == 2 ? WM.Color.FromRgb(225, 45, 60) : WM.Color.FromRgb(245, 160, 40);
                    alpha = 120;
                }

                var pg = new WS.Polygon { Fill = new WM.SolidColorBrush(WM.Color.FromArgb(alpha, color.R, color.G, color.B)), IsHitTestVisible = false };
                foreach (Point3d q in sh.Soe)
                    pg.Points.Add(P(q.Y - c.Origin.Y, q.Z - c.Origin.Z));
                _plan.Children.Add(pg);
            }
        }

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
        // Cotas do meio-fio e da sarjeta (medidas em cm), no lugar das chamadas de elevacao: o Heyvid
        // pediu cotas (11/11/2026); a elevacao continua na grade e na barra de baixo (cursor).
        if (Show("COTAS") && CurbMarks(cur, key) is { } marks)
        {
            WM.Brush dimBrush = ToolTheme.Brush(255, 205, 80);
            WM.Brush labelBack = ToolTheme.Brush(34, 36, 40, 225);
            // +1 quando a pista fica a direita do meio-fio na tela.
            double road = P(0, 0).X >= P(marks.Top.Y - c.Origin.Y, 0).X ? 1 : -1;

            // Texto da cota: pode ser arrastado e fica onde foi deixado (duplo clique na vista volta).
            void DimText(string id, string text, double centerX, double centerY)
            {
                var tb = new WC.TextBlock
                {
                    Text = text,
                    Foreground = dimBrush,
                    Background = labelBack,
                    Padding = new SW.Thickness(4, 1, 4, 2),
                    FontSize = 11,
                    Cursor = WI.Cursors.SizeAll,
                    ToolTip = "Arraste para mover este texto"
                };
                tb.Measure(new SW.Size(double.PositiveInfinity, double.PositiveInfinity));
                double defX = centerX - (tb.DesiredSize.Width / 2.0), defY = centerY - (tb.DesiredSize.Height / 2.0);
                double x = defX, y = defY;
                if (_secLabelShift.TryGetValue(id, out SW.Vector shift))
                {
                    x += shift.X;
                    y += shift.Y;
                }

                WC.Canvas.SetLeft(tb, x);
                WC.Canvas.SetTop(tb, y);
                SW.Point? from = null;
                tb.MouseLeftButtonDown += (_, e) =>
                {
                    from = e.GetPosition(_plan);
                    tb.CaptureMouse();
                    e.Handled = true;
                };
                tb.MouseMove += (_, e) =>
                {
                    if (from is not SW.Point f)
                        return;
                    SW.Point now = e.GetPosition(_plan);
                    x += now.X - f.X;
                    y += now.Y - f.Y;
                    from = now;
                    WC.Canvas.SetLeft(tb, x);
                    WC.Canvas.SetTop(tb, y);
                    e.Handled = true;
                };
                tb.MouseLeftButtonUp += (_, e) =>
                {
                    if (from is null)
                        return;
                    from = null;
                    tb.ReleaseMouseCapture();
                    _secLabelShift[id] = new SW.Vector(x - defX, y - defY);
                    e.Handled = true;
                };
                _plan.Children.Add(tb);
            }

            // Cota vertical entre duas elevacoes, na coluna xd da tela; as linhas de chamada saem dos pontos medidos.
            void VDim(string id, double xd, SW.Point from, SW.Point to, string text, double textSide)
            {
                ChartLine(from.X, from.Y, xd, from.Y, dimBrush, 0.8, dashed: true);
                ChartLine(to.X, to.Y, xd, to.Y, dimBrush, 0.8, dashed: true);
                ChartLine(xd, from.Y, xd, to.Y, dimBrush, 1.4);
                ChartLine(xd - 4, from.Y, xd + 4, from.Y, dimBrush, 1.4);
                ChartLine(xd - 4, to.Y, xd + 4, to.Y, dimBrush, 1.4);
                DimText(id, text, xd + (textSide * ((text.Length * 3.2) + 12)), (from.Y + to.Y) / 2.0);
            }

            // Cota horizontal entre dois afastamentos, na linha yd da tela.
            void HDim(string id, double yd, SW.Point from, SW.Point to, string text, double textSide)
            {
                ChartLine(from.X, from.Y, from.X, yd, dimBrush, 0.8, dashed: true);
                ChartLine(to.X, to.Y, to.X, yd, dimBrush, 0.8, dashed: true);
                ChartLine(from.X, yd, to.X, yd, dimBrush, 1.4);
                ChartLine(from.X, yd - 4, from.X, yd + 4, dimBrush, 1.4);
                ChartLine(to.X, yd - 4, to.X, yd + 4, dimBrush, 1.4);
                DimText(id, text, (from.X + to.X) / 2.0, yd + (textSide * 12));
            }

            SW.Point pTop = P(marks.Top.Y - c.Origin.Y, marks.Top.Z - c.Origin.Z);
            SW.Point pBack = P(marks.Back.Y - c.Origin.Y, marks.Back.Z - c.Origin.Z);
            SW.Point pFlow = P(marks.Flow.Y - c.Origin.Y, marks.Flow.Z - c.Origin.Z);
            SW.Point pEdge = P(0, 0);

            // Altura do meio-fio (linha d'agua ate o topo), do lado da calcada.
            double xd1 = pBack.X - (road * 26);
            VDim("ALTURA", xd1, new SW.Point(pBack.X, pTop.Y), pFlow, $"altura {(c.Top - c.Flow) * 100:0.0} cm", -road);

            // Altura total da peca (base enterrada ate o topo), mais para fora.
            if (CurbBottom(cur, key) is double bottom && c.Flow - bottom > 0.01)
            {
                SW.Point pBase = P(marks.Back.Y - c.Origin.Y, bottom - c.Origin.Z);
                VDim("PECA", xd1 - (road * 150), new SW.Point(pBack.X, pTop.Y), pBase, $"peca {(c.Top - bottom) * 100:0.0} cm", -road);
            }

            // Largura do topo do meio-fio.
            double topWidth = Math.Abs(marks.Back.Y - marks.Top.Y);
            if (topWidth > 0.02)
                HDim("TOPO", Math.Min(pTop.Y, pBack.Y) - 20, pTop, pBack, $"topo {topWidth * 100:0.0} cm", -1);

            // Sarjeta: largura (borda da pista ate a linha d'agua) e caimento.
            double gutter = Math.Abs(marks.Flow.Y - c.Origin.Y);
            if (gutter > 0.02)
            {
                double drop = c.Origin.Z - marks.Flow.Z;
                HDim("SARJETA", Math.Max(pEdge.Y, pFlow.Y) + 46, pEdge, pFlow,
                    $"sarjeta {gutter * 100:0.0} cm   caimento {drop / gutter * 100:0.0}% ({drop * 100:0.0} cm)", 1);
            }
        }

        if (Show("ESCALA") && !grid)
        {
            double step = NiceStep(w / scale / 8.0);
            double len = step * scale;
            ChartLine(w - len - 14, h - 12, w - 14, h - 12, ToolTheme.GraphicText, 2);
            PlanLabel($"{step:0.##} m", w - len - 14, h - 28, 10);
        }

        _chartCursor = p =>
        {
            double u = (p.X - ox) / scale, v = (oy - p.Y) / scale;
            return $"estaca {N(cur.Station)}  -  afastamento do eixo {N(c.Origin.Y + u)}  elevacao {N(c.Origin.Z + v)}  (da borda da pista: {u:0.000} / {v:0.000})";
        };
    }

    // ------------------------------------------------------------------
    // 3D interativo
    // ------------------------------------------------------------------

    /// <summary>Peca do 3D: de qual modelo, de qual subassembly, em que trecho do rebaixo e de qual rebaixo (-1 = nenhum).</summary>
    private sealed record Face3D(int Model, long Key, int Zone, int Owner, WM.Color Color);

    private readonly WC.Grid _host3D = new() { Visibility = SW.Visibility.Collapsed };
    private readonly WC.Viewport3D _viewport = new() { ClipToBounds = true };
    private readonly WC.TextBlock _info3D = new();
    private readonly Dictionary<W3.GeometryModel3D, Face3D> _faces = new();
    private bool _built3D;
    private double _exagero3D = 1.0;
    private string? _key3D;
    private double _yaw = -60, _pitch = 28, _dist = 20;
    private W3.Point3D _target;
    private double _cx3, _cy3, _z03;
    private SW.Point? _orbitFrom;
    private SW.Point _downAt;

    // 3D geral: todos os corredores com todos os rebaixos (senao, so o trecho do rebaixo selecionado).
    private bool _tudo3D;
    private List<RebaixoModel>? _models3D;
    private string? _modelsKey3D;
    private bool _near3D;
    private string? _focus3D;
    private bool _keepCamera3D;
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

    private Point3d World(W3.Point3D p) => new(p.X + _cx3, p.Y + _cy3, (p.Z / _exagero3D) + _z03);

    private SW.UIElement Build3DHost()
    {
        // A tela e remontada ao trocar o tema: o 3D e o mesmo, so muda de pai.
        if (_built3D)
        {
            (_host3D.Parent as WC.Panel)?.Children.Remove(_host3D);
            return _host3D;
        }

        _built3D = true;
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
            Text = "arrastar: girar   |   botao do meio ou direito: mover   |   Shift + botao do meio: girar   |   roda: zoom no cursor   |   duplo clique: centralizar ali   |   clique num rebaixo: selecionar",
            Foreground = ToolTheme.GraphicText,
            Background = ToolTheme.Brush(30, 32, 36, 190),
            Padding = new SW.Thickness(6, 2, 6, 2),
            FontSize = 10.5,
            Margin = new SW.Thickness(8, 0, 0, 6),
            HorizontalAlignment = SW.HorizontalAlignment.Left,
            VerticalAlignment = SW.VerticalAlignment.Bottom,
            IsHitTestVisible = false
        });

        _host3D.MouseDown += (_, e) =>
        {
            SW.Point p = e.GetPosition(_host3D);
            if (e.ClickCount == 2 && e.ChangedButton == WI.MouseButton.Left)
            {
                // Duplo clique: o ponto clicado vira o centro do giro; no vazio, enquadra tudo.
                if (Hit3D(p) is { } hit)
                {
                    _target = hit.Point;
                    UpdateCamera();
                }
                else
                {
                    FitAll3D();
                }

                _orbitFrom = null;
                return;
            }

            _orbitFrom = p;
            _downAt = p;
            _host3D.CaptureMouse();
        };
        _host3D.MouseMove += (_, e) =>
        {
            if (_orbitFrom is not SW.Point f)
                return;
            SW.Point p = e.GetPosition(_host3D);
            double dx = p.X - f.X, dy = p.Y - f.Y;
            bool shift = (WI.Keyboard.Modifiers & WI.ModifierKeys.Shift) != 0;
            bool middle = e.MiddleButton == WI.MouseButtonState.Pressed;
            bool pan = e.RightButton == WI.MouseButtonState.Pressed || (middle && !shift);
            bool orbit = e.LeftButton == WI.MouseButtonState.Pressed || (middle && shift);
            if (pan)
            {
                // Move no plano da tela: o ponto agarrado acompanha o cursor (como o PAN do AutoCAD).
                (W3.Vector3D right, W3.Vector3D up, _) = CameraAxes();
                double k = 2.0 * _dist * Math.Tan(20.0 * Math.PI / 180.0) / Math.Max(_host3D.ActualWidth, 1);
                _target += (right * (-dx * k)) + (up * (dy * k));
            }
            else if (orbit)
            {
                _yaw -= dx * 0.35;
                _pitch = Math.Clamp(_pitch + (dy * 0.35), 2, 89);
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
            double nd = Math.Clamp(_dist * (e.Delta > 0 ? 0.85 : 1.18), 0.4, 50000);
            double f = nd / _dist;
            // Zoom no cursor: o alvo anda na direcao do ponto sob o mouse.
            if (Hit3D(e.GetPosition(_host3D)) is { } hit)
                _target = hit.Point + ((_target - hit.Point) * f);
            _dist = nd;
            UpdateCamera();
        };
        return _host3D;
    }

    /// <summary>Eixos da camera: direita, cima e a direcao do alvo para a camera.</summary>
    private (W3.Vector3D Right, W3.Vector3D Up, W3.Vector3D Back) CameraAxes()
    {
        double yaw = _yaw * Math.PI / 180.0, pitch = _pitch * Math.PI / 180.0;
        var back = new W3.Vector3D(Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch));
        W3.Vector3D right = W3.Vector3D.CrossProduct(-back, new W3.Vector3D(0, 0, 1));
        if (right.Length < 1e-9)
            right = new W3.Vector3D(1, 0, 0);
        right.Normalize();
        W3.Vector3D up = W3.Vector3D.CrossProduct(right, -back);
        up.Normalize();
        return (right, up, back);
    }

    private void UpdateCamera()
    {
        if (_viewport.Camera is not W3.PerspectiveCamera cam)
            return;
        W3.Vector3D offset = CameraAxes().Back * _dist;
        cam.Position = _target + offset;
        cam.LookDirection = -offset;
        cam.UpDirection = new W3.Vector3D(0, 0, 1);
    }

    /// <summary>Vistas prontas: muda so a inclinacao da camera (89 = de cima, 6 = de frente).</summary>
    private void View3D(double pitch)
    {
        _pitch = pitch;
        UpdateCamera();
    }

    private (W3.GeometryModel3D Model, W3.Point3D Point)? Hit3D(SW.Point p)
    {
        try
        {
            SW.Point vp = _host3D.TranslatePoint(p, _viewport);
            if (WM.VisualTreeHelper.HitTest(_viewport, vp) is W3.RayMeshGeometry3DHitTestResult hit && hit.ModelHit is W3.GeometryModel3D gm)
                return (gm, hit.PointHit);
        }
        catch
        {
            // Sem cena: nada sob o cursor.
        }

        return null;
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

    private static W3.Material Material3D(WM.Color color, bool selected)
    {
        var diffuse = new W3.DiffuseMaterial(new WM.SolidColorBrush(color));
        if (!selected)
            return diffuse;
        // Rebaixo selecionado: a mesma cor, acesa.
        var group = new W3.MaterialGroup();
        group.Children.Add(diffuse);
        // Emissivo da propria cor: acende sem mudar o tom (amarelo fixo fazia o vermelho parecer laranja
        // e o laranja parecer amarelo, teste de 11/11/2026).
        group.Children.Add(new W3.EmissiveMaterial(new WM.SolidColorBrush(WM.Color.FromRgb((byte)(color.R * 0.22), (byte)(color.G * 0.22), (byte)(color.B * 0.22)))));
        return group;
    }

    /// <summary>Acende as pecas do rebaixo selecionado e mostra a identificacao dele (nome e centro).</summary>
    private void ApplySelection3D()
    {
        RebaixoInfo? r = _current;
        foreach (KeyValuePair<W3.GeometryModel3D, Face3D> kv in _faces)
        {
            Face3D f = kv.Value;
            bool selected = r is not null && f.Owner >= 0 && f.Model < _owners3D.Count && f.Owner < _owners3D[f.Model].Count &&
                            FocusKey(_owners3D[f.Model][f.Owner].R) == FocusKey(r);
            W3.Material mat = Material3D(f.Color, selected);
            kv.Key.Material = mat;
            kv.Key.BackMaterial = mat;
        }

        // Ficha do rebaixo selecionado: tudo menos as coordenadas dos marcos e do ponto clicado (era so
        // esse bloco que o Heyvid achou demais, 11/11/2026); para identificar fica a coordenada do centro.
        RebaixoModel? model = r is null ? null : _shown3D.FirstOrDefault(m => m.CorridorId == r.CorridorId && m.BaselineKey == r.BaselineKey);
        if (r is null)
        {
            _info3D.Visibility = SW.Visibility.Collapsed;
            return;
        }

        List<string> lines = model is not null && MfKey(model, r) is long key
            ? RebaixoCard(model, r, key)
            : new List<string> { "REBAIXO " + r.Label };
        if (RebaixoCenter(r, model) is Point3d cg)
        {
            lines.Add("");
            lines.Add($"Centro do rebaixo:  X {cg.X:0.000}   Y {cg.Y:0.000}   elevacao {cg.Z:0.000}");
        }

        _info3D.Text = string.Join("\n", lines);
        _info3D.Visibility = SW.Visibility.Visible;
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
    /// Ficha do rebaixo no 3D: identificacao, tipo do meio-fio, comprimento e inclinacao das rampas e
    /// alturas (dados que vao servir para a federacao do modelo BIM).
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

        return new List<string>
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
                (normal is double nn && final is double ff ? $"   (desce {Cm(nn - ff)})" : "")
        };
    }

    private static void Quad(W3.MeshGeometry3D mesh, W3.Point3D a, W3.Point3D b, W3.Point3D c, W3.Point3D d)
    {
        int i0 = mesh.Positions.Count;
        mesh.Positions.Add(a);
        mesh.Positions.Add(b);
        mesh.Positions.Add(c);
        mesh.Positions.Add(d);
        mesh.TriangleIndices.Add(i0);
        mesh.TriangleIndices.Add(i0 + 1);
        mesh.TriangleIndices.Add(i0 + 2);
        mesh.TriangleIndices.Add(i0);
        mesh.TriangleIndices.Add(i0 + 2);
        mesh.TriangleIndices.Add(i0 + 3);
    }

    /// <summary>Tampa de uma shape (poligono simples, pode ser concavo): triangulos por corte de orelhas no plano da secao.</summary>
    private void Cap(W3.MeshGeometry3D mesh, ModelShape shape)
    {
        int n = shape.Soe.Length;
        if (n < 3 || shape.Points.Length != n)
            return;
        List<int> idx = Enumerable.Range(0, n).ToList();
        double area = 0;
        for (int i = 0; i < n; i++)
        {
            Point3d a = shape.Soe[i], b = shape.Soe[(i + 1) % n];
            area += (a.Y * b.Z) - (b.Y * a.Z);
        }

        double sign = area >= 0 ? 1 : -1;
        double Cross(int i, int j, int k) =>
            ((shape.Soe[j].Y - shape.Soe[i].Y) * (shape.Soe[k].Z - shape.Soe[i].Z)) - ((shape.Soe[j].Z - shape.Soe[i].Z) * (shape.Soe[k].Y - shape.Soe[i].Y));
        bool Inside(int i, int j, int k, int q)
        {
            double d1 = Cross(i, j, q) * sign, d2 = Cross(j, k, q) * sign, d3 = Cross(k, i, q) * sign;
            return d1 > 1e-12 && d2 > 1e-12 && d3 > 1e-12;
        }

        void Tri(int i, int j, int k)
        {
            int i0 = mesh.Positions.Count;
            mesh.Positions.Add(Loc(shape.Points[i]));
            mesh.Positions.Add(Loc(shape.Points[j]));
            mesh.Positions.Add(Loc(shape.Points[k]));
            mesh.TriangleIndices.Add(i0);
            mesh.TriangleIndices.Add(i0 + 1);
            mesh.TriangleIndices.Add(i0 + 2);
        }

        int guard = n * n;
        while (idx.Count > 3 && guard-- > 0)
        {
            bool cut = false;
            for (int t = 0; t < idx.Count; t++)
            {
                int i = idx[(t + idx.Count - 1) % idx.Count], j = idx[t], k = idx[(t + 1) % idx.Count];
                if (Cross(i, j, k) * sign <= 1e-12)
                    continue; // canto concavo ou reto
                bool ear = true;
                foreach (int q in idx)
                {
                    if (q != i && q != j && q != k && Inside(i, j, k, q))
                    {
                        ear = false;
                        break;
                    }
                }

                if (!ear)
                    continue;
                Tri(i, j, k);
                idx.RemoveAt(t);
                cut = true;
                break;
            }

            if (!cut)
                break; // poligono degenerado: fecha em leque com o que sobrou
        }

        for (int t = 1; t + 1 < idx.Count; t++)
            Tri(idx[0], idx[t], idx[t + 1]);
    }

    private void Render3D()
    {
        RebaixoInfo? r = _current;
        List<RebaixoModel> models = Models3D().Where(m => m.Sections.Count >= 2).ToList();
        if (models.Count == 0)
        {
            _viewport.Children.Clear();
            _faces.Clear();
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
            ApplySelection3D();
            return;
        }

        // Rebaixo escolhido com um clique no proprio 3D: a camera fica onde esta.
        bool keep = _keepCamera3D && _shown3D.Count > 0;
        _keepCamera3D = false;
        Point3d keepTarget = keep ? World(_target) : Point3d.Origin;

        _key3D = key3D;
        _viewport.Children.Clear();
        _faces.Clear();
        _shown3D = models;

        // Pontos para a caixa e para enquadrar (as pontas de cada link, no maximo ~6000).
        List<Point3d> ends = models.SelectMany(m => m.Sections.SelectMany(s => s.Subassemblies.SelectMany(a => a.Links.Where(l => l.Points.Length > 0).SelectMany(l => new[] { l.Points[0], l.Points[^1] })))).ToList();
        if (ends.Count == 0)
            return;
        double minX = ends.Min(p => p.X), maxX = ends.Max(p => p.X), minY = ends.Min(p => p.Y), maxY = ends.Max(p => p.Y);
        double minZ = ends.Min(p => p.Z), maxZ = ends.Max(p => p.Z);
        foreach (ModelShape sh in models.SelectMany(m => m.Sections.SelectMany(s => s.Shapes.Values.SelectMany(v => v))))
        {
            foreach (Point3d q in sh.Points)
                minZ = Math.Min(minZ, q.Z);
        }

        _cx3 = (minX + maxX) / 2.0;
        _cy3 = (minY + maxY) / 2.0;
        _z03 = minZ;
        _min3 = Loc(new Point3d(minX, minY, minZ));
        _max3 = Loc(new Point3d(maxX, maxY, maxZ));
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

        // Uma malha por (modelo, peca, parte, trecho, rebaixo). Parte >= 0: shape (solido); negativa: link solto (face).
        var meshes = new Dictionary<(int Model, long Key, int Part, int Zone, int Owner), (W3.MeshGeometry3D Mesh, string[] Codes)>();
        W3.MeshGeometry3D MeshOf(int mi, long key, int part, int zone, int owner, string[] codes)
        {
            if (!meshes.TryGetValue((mi, key, part, zone, owner), out (W3.MeshGeometry3D Mesh, string[] Codes) entry))
            {
                entry = (new W3.MeshGeometry3D(), codes);
                meshes[(mi, key, part, zone, owner)] = entry;
            }

            return entry.Mesh;
        }

        for (int mi = 0; mi < models.Count; mi++)
        {
            RebaixoModel model = models[mi];
            List<(RebaixoInfo R, long Key)> owners = _owners3D[mi];
            int last = model.Sections.Count - 1;

            // Trecho (0 fora, 1 rampa, 2 rebaixado) e rebaixo dono de uma peca do meio-fio entre duas secoes.
            (int Zone, int Owner) ZoneAt(long key, double station)
            {
                int zone = 0, owner = -1;
                for (int o = 0; o < owners.Count; o++)
                {
                    if (owners[o].Key != key)
                        continue;
                    int z = ZoneOf(owners[o].R, station);
                    if (z > zone)
                    {
                        zone = z;
                        owner = o;
                    }
                }

                return (zone, owner);
            }

            // A shape j da peca existe igual (mesmo numero de pontos) nas secoes k e k+1?
            bool Solid(int k, long key, int j) =>
                k >= 0 && k < last &&
                model.Sections[k].Shapes.TryGetValue(key, out List<ModelShape>? xa) && j < xa.Count &&
                model.Sections[k + 1].Shapes.TryGetValue(key, out List<ModelShape>? xb) && j < xb.Count &&
                xa[j].Points.Length >= 3 && xa[j].Points.Length == xb[j].Points.Length;

            for (int k = 0; k < last; k++)
            {
                ModelSection a = model.Sections[k], b = model.Sections[k + 1];
                double midStation = (a.Station + b.Station) / 2.0;

                // Solidos: cada shape vira um prisma entre as duas secoes, com tampa nas pontas.
                foreach (KeyValuePair<long, List<ModelShape>> kv in a.Shapes)
                {
                    for (int j = 0; j < kv.Value.Count; j++)
                    {
                        if (!Solid(k, kv.Key, j))
                            continue;
                        ModelShape sa = kv.Value[j], sb = b.Shapes[kv.Key][j];
                        (int zone, int owner) = IsCurbShape(sa.Codes) ? ZoneAt(kv.Key, midStation) : (0, -1);
                        W3.MeshGeometry3D mesh = MeshOf(mi, kv.Key, j, zone, owner, sa.Codes);
                        int n = sa.Points.Length;
                        for (int p = 0; p < n; p++)
                        {
                            int q = (p + 1) % n;
                            Quad(mesh, Loc(sa.Points[p]), Loc(sa.Points[q]), Loc(sb.Points[q]), Loc(sb.Points[p]));
                        }

                        if (!Solid(k - 1, kv.Key, j))
                            Cap(mesh, sa);
                        if (!Solid(k + 1, kv.Key, j))
                            Cap(mesh, sb);
                    }
                }

                // Links que nao sao borda de nenhuma shape (calcada, taludes): continuam como faces.
                foreach ((long key, List<ModelLink> linksA) in a.Subassemblies)
                {
                    List<ModelLink>? linksB = b.Subassemblies.FirstOrDefault(x => x.Key == key).Links;
                    if (linksB is null)
                        continue;
                    List<Point3d> verts = a.Shapes.TryGetValue(key, out List<ModelShape>? shapesA)
                        ? Enumerable.Range(0, shapesA.Count).Where(j => Solid(k, key, j)).SelectMany(j => shapesA[j].Soe).ToList()
                        : new List<Point3d>();
                    int n = Math.Min(linksA.Count, linksB.Count);
                    for (int j = 0; j < n; j++)
                    {
                        ModelLink la = linksA[j], lb = linksB[j];
                        if (la.Points.Length < 2 || la.Points.Length != lb.Points.Length)
                            continue;
                        if (verts.Count > 0 && la.Soe.Length > 0 &&
                            la.Soe.All(q => verts.Any(v => Math.Abs(v.Y - q.Y) < 2e-4 && Math.Abs(v.Z - q.Z) < 2e-4)))
                        {
                            continue; // ja esta no solido
                        }

                        (int zone, int owner) = !IsWalk(la.Codes) && (Has(la.Codes, "Curb") || Has(la.Codes, "Top") || Has(la.Codes, "TOPO") || Has(la.Codes, "Sarjeta"))
                            ? ZoneAt(key, midStation)
                            : (0, -1);
                        W3.MeshGeometry3D mesh = MeshOf(mi, key, -1 - j, zone, owner, la.Codes);
                        for (int p = 0; p + 1 < la.Points.Length; p++)
                            Quad(mesh, Loc(la.Points[p]), Loc(la.Points[p + 1]), Loc(lb.Points[p + 1]), Loc(lb.Points[p]));
                    }
                }
            }
        }

        var group = new W3.Model3DGroup();
        group.Children.Add(new W3.AmbientLight(WM.Color.FromRgb(110, 110, 110)));
        group.Children.Add(new W3.DirectionalLight(WM.Color.FromRgb(175, 175, 175), new W3.Vector3D(-0.45, -0.3, -1)));
        group.Children.Add(new W3.DirectionalLight(WM.Color.FromRgb(95, 95, 95), new W3.Vector3D(0.6, 0.7, -0.25)));
        foreach (KeyValuePair<(int Model, long Key, int Part, int Zone, int Owner), (W3.MeshGeometry3D Mesh, string[] Codes)> kv in meshes)
        {
            RebaixoStyle.Look look = RebaixoStyle.Of(kv.Value.Codes);
            WM.Color color = kv.Key.Zone == 2 ? WM.Color.FromRgb(225, 45, 60)
                : kv.Key.Zone == 1 ? WM.Color.FromRgb(245, 160, 40)
                : look.Color.A == 0 ? WM.Color.FromRgb(132, 134, 140) : look.Color;
            W3.Material material = Material3D(color, false);
            var gm = new W3.GeometryModel3D(kv.Value.Mesh, material) { BackMaterial = material };
            group.Children.Add(gm);
            _faces[gm] = new Face3D(kv.Key.Model, kv.Key.Key, kv.Key.Zone, kv.Key.Owner, color);
        }

        _viewport.Children.Add(new W3.ModelVisual3D { Content = group });
        ApplySelection3D();

        if (keep)
        {
            _target = Loc(keepTarget);
            _focus3D = r is null ? null : FocusKey(r);
            UpdateCamera();
        }
        else if (_tudo3D)
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
        (W3.Vector3D right, W3.Vector3D up, W3.Vector3D dir) = CameraAxes();
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
        foreach (RebaixoModel model in _shown3D)
        {
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

        if (r.Path.Count > 0 && _shown3D.Count > 0)
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

    /// <summary>
    /// Clique no 3D: se caiu num rebaixo (rampa ou trecho rebaixado), ele vira o selecionado do painel
    /// (lista, propriedades, destaque no desenho) e o 3D mostra so a identificacao: nome e centro.
    /// As medidas de face/peca sairam (pedido do Heyvid em 11/11/2026: nao interessam).
    /// </summary>
    private void Pick(SW.Point p)
    {
        try
        {
            if (Hit3D(p) is not { } hit || !_faces.TryGetValue(hit.Model, out Face3D? face) ||
                face.Owner < 0 || face.Model >= _owners3D.Count || face.Owner >= _owners3D[face.Model].Count)
            {
                return;
            }

            RebaixoInfo owner = _owners3D[face.Model][face.Owner].R;
            if (_current is not null && FocusKey(_current) == FocusKey(owner))
                return;
            // A camera nao sai do lugar por causa desta selecao.
            _focus3D = FocusKey(owner);
            _keepCamera3D = true;
            SelectRebaixo(owner);
            _keepCamera3D = false;
        }
        catch (System.Exception ex)
        {
            Civil3D2026Plugin.Infrastructure.PluginLog.Aviso("MFREBAIXO", "3D: clique nao lido: " + ex.Message);
        }
    }

    /// <summary>Seleciona um rebaixo no painel (na lista, se ele estiver nela).</summary>
    private void SelectRebaixo(RebaixoInfo r)
    {
        // Fora da categoria REBAIXOS (planta geral, avisos...), volta para ela com este rebaixo marcado.
        if (_category != "REBAIXOS")
        {
            _current = r;
            _secMark = null;
            _category = "REBAIXOS";
            FillCategories();
            FillList();
        }

        foreach (object o in _list.Items)
        {
            if (o is WC.ListViewItem { Tag: Row { Data: RebaixoInfo x } } item && FocusKey(x) == FocusKey(r))
            {
                if (!ReferenceEquals(_list.SelectedItem, item))
                {
                    _list.SelectedItem = item;
                    _list.ScrollIntoView(item);
                }

                return;
            }
        }

        _current = r;
        _secMark = null;
        BuildTabs();
        RenderGraphic();
        HighlightCurrent();
    }
}
