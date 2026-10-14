using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>Trecho de bordo (LD/LE) de um eixo: offset na face do meio-fio entre duas estacas.</summary>
internal sealed class CivilEdgePlan
{
    public string AxisKey { get; init; } = "";

    /// <summary>+1 = direita (LD), -1 = esquerda (LE), no sentido do eixo.</summary>
    public int Side { get; init; }

    /// <summary>Offset com sinal (Civil: positivo = direita).</summary>
    public double Offset { get; init; }

    /// <summary>Inicio e fim no comprimento do eixo (0..L).</summary>
    public double S0 { get; init; }
    public double S1 { get; init; }

    public int Index { get; set; }

    public string Code => (Side > 0 ? "LD" : "LE") + Index;

    public double Length => S1 - S0;

    /// <summary>Alargamentos (regioes do alinhamento offset) dentro do trecho.</summary>
    public List<CivilWidenRegion> Widenings { get; } = new();

    /// <summary>Alargamento que o Civil nao representa (taper cortado): usar geometria fixa.</summary>
    public bool ForceFixed { get; set; }

    /// <summary>Largura extra na estaca s do eixo (alargamentos, gotas).</summary>
    public Func<double, double> Extra { get; set; } = _ => 0.0;

    /// <summary>Offset com sinal na estaca s, incluindo alargamentos.</summary>
    public double OffsetAt(double s) => Offset + (Side * Extra(s));

    public string WidenKey => ForceFixed ? "FIXO|" + string.Join(";", Widenings.Select(w => w.Key)) : string.Join(";", Widenings.Select(w => w.Key));
}

/// <summary>Regiao alargada de um trecho: patamar [Start, End] com transicoes.</summary>
internal sealed class CivilWidenRegion
{
    public double Start { get; init; }
    public double End { get; init; }
    public double TaperIn { get; init; }
    public double TaperOut { get; init; }
    public double Width { get; init; }
    public bool Smooth { get; init; }

    public string Key => FormattableString.Invariant($"{Start:0.###},{End:0.###},{TaperIn:0.###},{TaperOut:0.###},{Width:0.###},{(Smooth ? 1 : 0)}");
}

/// <summary>Curb return de uma esquina concava.</summary>
internal sealed class CivilCurbPlan
{
    public string Key { get; init; } = "";
    public Seg Arc { get; init; }
    public double Radius { get; init; }

    public string AxisIn { get; init; } = "";
    public int SideIn { get; init; }
    public double StationIn { get; init; }
    public double OffsetIn { get; set; }

    public string AxisOut { get; init; } = "";
    public int SideOut { get; init; }
    public double StationOut { get; init; }
    public double OffsetOut { get; set; }

    public CivilEdgePlan? EdgeIn { get; set; }
    public CivilEdgePlan? EdgeOut { get; set; }
}

internal sealed class CivilPlan
{
    public List<AxisInput> Axes { get; } = new();
    public List<CivilEdgePlan> Edges { get; } = new();
    public List<CivilCurbPlan> Curbs { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Traduz a rede calculada em objetos do Civil (sem tocar na API do Civil):
/// eixos, trechos de bordo LD/LE (cortados nas esquinas) e curb returns.
/// </summary>
internal static class ViarioCivilPlanner
{
    private const double MinEdge = 0.5;

    private readonly record struct Gap(double A, double B);

    public static CivilPlan Build(IReadOnlyList<AxisInput> axes, ViarioResult result)
    {
        CivilPlan plan = new();
        plan.Axes.AddRange(axes);
        Dictionary<string, AxisInput> byKey = axes.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
        Dictionary<(string, int), List<Gap>> gaps = new();

        foreach (CornerInfo corner in result.Corners)
        {
            if (!byKey.TryGetValue(corner.AxisA, out AxisInput? axA) || !byKey.TryGetValue(corner.AxisB, out AxisInput? axB))
                continue;

            Foot? fA;
            Foot? fB;
            if (corner.Arc is Seg a0)
            {
                fA = FootOn(a0.A, axA, axB);
                fB = FootOn(a0.B, axA, axB);
            }
            else
            {
                // Canto vivo: o ponto da esquina esta nos dois bordos.
                fA = FootOn(corner.Corner, axA, axA);
                fB = FootOn(corner.Corner, axB, axB);
            }

            if (fA is null || fB is null)
            {
                plan.Warnings.Add($"Esquina {corner.Key}: nao consegui localizar os bordos.");
                continue;
            }

            AddGap(gaps, fA.Value, corner.Node, byKey, corner);
            AddGap(gaps, fB.Value, corner.Node, byKey, corner);

            if (corner.Arc is Seg arc)
            {
                plan.Curbs.Add(new CivilCurbPlan
                {
                    Key = corner.Key,
                    Arc = arc,
                    Radius = corner.Radius,
                    AxisIn = fA.Value.Axis.Key,
                    SideIn = fA.Value.Side,
                    StationIn = fA.Value.S,
                    OffsetIn = fA.Value.Side * fA.Value.Axis.Section.EdgeOffset(EdgeLevel.FaceMF),
                    AxisOut = fB.Value.Axis.Key,
                    SideOut = fB.Value.Side,
                    StationOut = fB.Value.S,
                    OffsetOut = fB.Value.Side * fB.Value.Axis.Section.EdgeOffset(EdgeLevel.FaceMF)
                });
            }
        }

        // Trechos de bordo = eixo inteiro menos as lacunas das esquinas.
        foreach (AxisInput axis in axes)
        {
            foreach (int side in new[] { -1, +1 })
            {
                List<Gap> list = gaps.TryGetValue((axis.Key, side), out List<Gap>? g) ? g : new List<Gap>();
                List<(double A, double B)> pieces = axis.Path.Closed
                    ? ClosedComplement(axis.Path.Length, list)
                    : OpenComplement(axis.Path.Length, list);

                int index = 1;
                foreach ((double a, double b) in pieces.OrderBy(p => p.A))
                {
                    if (b - a < MinEdge)
                        continue;

                    plan.Edges.Add(new CivilEdgePlan
                    {
                        AxisKey = axis.Key,
                        Side = side,
                        Offset = side * axis.Section.EdgeOffset(EdgeLevel.FaceMF),
                        S0 = a,
                        S1 = b,
                        Index = index++
                    });
                }
            }
        }

        // Alargamentos de cada trecho.
        foreach (CivilEdgePlan e in plan.Edges)
        {
            List<WidenInfo> ws = result.Widenings.Where(w => w.AxisKey == e.AxisKey && w.Side == e.Side).ToList();
            if (ws.Count == 0)
                continue;

            e.Extra = s => ws.Max(w => w.ValueAt(s));
            foreach (WidenInfo w in ws)
                AddWidenRegion(e, w);
        }

        // Liga cada curb return aos trechos que ele encosta.
        foreach (CivilCurbPlan curb in plan.Curbs)
        {
            curb.EdgeIn = EdgeAt(plan, curb.AxisIn, curb.SideIn, curb.StationIn);
            curb.EdgeOut = EdgeAt(plan, curb.AxisOut, curb.SideOut, curb.StationOut);
            curb.OffsetIn += curb.SideIn * Extra(result, curb.AxisIn, curb.SideIn, curb.StationIn);
            curb.OffsetOut += curb.SideOut * Extra(result, curb.AxisOut, curb.SideOut, curb.StationOut);
        }

        return plan;
    }

    private readonly record struct Foot(AxisInput Axis, double S, int Side);

    /// <summary>Em qual eixo (e lado) o ponto da face do meio-fio esta.</summary>
    private static Foot? FootOn(V2 p, AxisInput a, AxisInput b)
    {
        Foot? best = null;
        double bestErr = double.MaxValue;
        foreach (AxisInput axis in a.Key == b.Key ? new[] { a } : new[] { a, b })
        {
            (double s, V2 q, double dist) = axis.Path.Project(p);
            double err = Math.Abs(dist - axis.Section.EdgeOffset(EdgeLevel.FaceMF));
            if (err >= bestErr)
                continue;

            V2 t = axis.Path.TangentAt(s);
            int side = V2.Cross(t, p - q) > 0 ? -1 : +1;
            best = new Foot(axis, s, side);
            bestErr = err;
        }

        return best;
    }

    private static void AddGap(Dictionary<(string, int), List<Gap>> gaps, Foot f, V2 node,
        Dictionary<string, AxisInput> byKey, CornerInfo corner)
    {
        GPath path = f.Axis.Path;
        double sn = path.Project(node).S;
        double a = f.S;
        double b = sn;

        if (path.Closed)
        {
            double len = path.Length;
            double d = b - a;
            while (d > len / 2.0) d -= len;
            while (d < -len / 2.0) d += len;
            b = a + d;
        }
        else
        {
            // Ponta do eixo que passa um pouco do no (dentro da outra via): some junto.
            string otherKey = corner.AxisA == f.Axis.Key ? corner.AxisB : corner.AxisA;
            double reach = byKey.TryGetValue(otherKey, out AxisInput? other)
                ? other.Section.EdgeOffset(EdgeLevel.Alinhamento) + 0.5
                : 0.0;

            if (b >= a && path.Length - b <= reach)
                b = path.Length;
            else if (b < a && b <= reach)
                b = 0.0;
        }

        if (!gaps.TryGetValue((f.Axis.Key, f.Side), out List<Gap>? list))
            gaps[(f.Axis.Key, f.Side)] = list = new List<Gap>();
        list.Add(new Gap(Math.Min(a, b), Math.Max(a, b)));
    }

    private static List<(double A, double B)> OpenComplement(double length, List<Gap> gaps)
    {
        List<(double, double)> result = new();
        double cursor = 0.0;
        foreach (Gap g in gaps.OrderBy(g => g.A))
        {
            double a = Math.Max(0.0, g.A);
            double b = Math.Min(length, g.B);
            if (a > cursor)
                result.Add((cursor, a));
            cursor = Math.Max(cursor, b);
        }

        if (cursor < length)
            result.Add((cursor, length));
        return result;
    }

    /// <summary>Complemento num eixo fechado; trechos que passam pela estaca zero sao quebrados em dois.</summary>
    private static List<(double A, double B)> ClosedComplement(double length, List<Gap> gaps)
    {
        if (gaps.Count == 0)
            return new List<(double, double)> { (0.0, length) };

        // Normaliza lacunas para [0, L), quebrando as que cruzam o zero.
        List<Gap> norm = new();
        foreach (Gap g in gaps)
        {
            double a = g.A;
            double b = g.B;
            if (b - a >= length)
                return new List<(double, double)>();

            double shift = Math.Floor(a / length) * length;
            a -= shift;
            b -= shift;
            if (b <= length)
            {
                norm.Add(new Gap(a, b));
            }
            else
            {
                norm.Add(new Gap(a, length));
                norm.Add(new Gap(0.0, b - length));
            }
        }

        return OpenComplement(length, norm);
    }

    private static double Extra(ViarioResult result, string axis, int side, double s) =>
        result.Widenings.Where(w => w.AxisKey == axis && w.Side == side).Select(w => w.ValueAt(s)).DefaultIfEmpty(0.0).Max();

    private static void AddWidenRegion(CivilEdgePlan e, WidenInfo w)
    {
        const double eps = 0.01;
        double p0, p1, lowTaper, highTaper;
        if (w.Dir > 0)
        {
            p0 = w.Behind ? double.NegativeInfinity : w.A;
            p1 = w.A + w.Full;
            lowTaper = 0.0;
            highTaper = w.Taper;
        }
        else
        {
            p0 = w.A - w.Full;
            p1 = w.Behind ? double.PositiveInfinity : w.A;
            lowTaper = w.Taper;
            highTaper = 0.0;
        }

        double start = Math.Max(p0, e.S0);
        double end = Math.Min(p1, e.S1);
        if (end - start < eps)
        {
            // Trecho so dentro do taper: o Civil nao tem regiao para isso.
            bool inTaper = (w.Dir > 0 && e.S0 < p1 + highTaper && e.S1 > p1) || (w.Dir < 0 && e.S1 > p0 - lowTaper && e.S0 < p0);
            if (inTaper)
                e.ForceFixed = true;
            return;
        }

        double tin = start > e.S0 + eps ? Math.Min(lowTaper, start - e.S0) : 0.0;
        double tout = end < e.S1 - eps ? Math.Min(highTaper, e.S1 - end) : 0.0;
        if (tin < lowTaper - eps && start > e.S0 + eps)
            e.ForceFixed = true;
        if (tout < highTaper - eps && end < e.S1 - eps)
            e.ForceFixed = true;

        e.Widenings.Add(new CivilWidenRegion
        {
            Start = start, End = end, TaperIn = tin, TaperOut = tout, Width = w.Width, Smooth = w.Smooth
        });
    }

    private static CivilEdgePlan? EdgeAt(CivilPlan plan, string axis, int side, double s)
    {
        CivilEdgePlan? best = null;
        double bestDist = 0.05;
        foreach (CivilEdgePlan e in plan.Edges)
        {
            if (e.AxisKey != axis || e.Side != side)
                continue;

            double d = Math.Min(Math.Abs(e.S0 - s), Math.Abs(e.S1 - s));
            if (d <= bestDist)
            {
                bestDist = d;
                best = e;
            }
        }

        return best;
    }
}
