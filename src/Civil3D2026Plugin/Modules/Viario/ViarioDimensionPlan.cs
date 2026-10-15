using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>Quebras da cadeia de cotas da secao numa estaca (C# puro, testavel fora do Civil).</summary>
internal static class ViarioDimensionPlan
{
    /// <summary>Afastamentos (esquerda positiva) das quebras da cadeia de cotas nesta estaca.</summary>
    public static List<double> Breaks(AxisInput axis, double s, ViarioResult result, bool detail)
    {
        RoadSection sec = axis.Section;
        GPath path = axis.Path;
        V2 p0 = path.PointAt(s);
        V2 n = path.TangentAt(s).Left;

        // Linhas geradas cortadas pela normal: a posicao real de cada nivel, por lado.
        double reach = sec.EdgeOffset(EdgeLevel.Alinhamento) + 8.0;
        GPath probe = new(new List<Seg> { Seg.Line(p0 - (n * reach), p0 + (n * reach)) });
        Dictionary<(int Side, EdgeLevel Level), double> actual = new();
        foreach (OutputLine line in result.Lines)
        {
            if (line.Level == EdgeLevel.Ilha)
                continue;
            if (!Bounds(line.Segs).Inflate(0.01).Intersects(probe.Bounds()))
                continue;

            foreach ((_, _, V2 x) in probe.Intersections(new GPath(line.Segs)))
            {
                double off = V2.Dot(x - p0, n);
                int side = off >= 0 ? RoadSection.Esquerda : RoadSection.Direita;
                double nominal = sec.EdgeOffset(line.Level, side);
                double d = Math.Abs(Math.Abs(off) - nominal);
                if (d > 6.0)
                    continue;
                var key = (side, line.Level);
                if (!actual.TryGetValue(key, out double cur) || d < Math.Abs(Math.Abs(cur) - nominal))
                    actual[key] = Math.Abs(off);
            }
        }

        List<double> breaks = new();
        double c = sec.Canteiro / 2.0;
        double half = sec.Pista / 2.0;
        int lanes = sec.FaixasPorSentido;

        foreach (int side in new[] { RoadSection.Esquerda, RoadSection.Direita })
        {
            double sg = side == RoadSection.Esquerda ? 1 : -1;
            double Of(EdgeLevel level) => actual.TryGetValue((side, level), out double v) ? v : sec.EdgeOffset(level, side);

            double face = Of(EdgeLevel.FaceMF);
            double edge = actual.TryGetValue((side, EdgeLevel.Pista), out double pe) ? pe : face - sec.SarjetaEm(side);
            double back = sec.MeioFioEm(side) > 1e-3 ? Of(EdgeLevel.CostasMF) : face;
            double walk = sec.CalcadaEm(side) > 1e-3 ? Of(EdgeLevel.Alinhamento) : back;

            double cant = c > 1e-3 && actual.TryGetValue((side, EdgeLevel.Canteiro), out double ce) ? ce : c;
            breaks.Add(sg * cant);

            // Faixas nominais e, se houver alargamento, a largura extra como quebra propria.
            double lane = half / lanes;
            for (int i = 1; i <= lanes; i++)
                breaks.Add(sg * (cant + (i * lane)));
            if (edge > cant + half + 0.05)
                breaks.Add(sg * edge);

            if (detail)
            {
                if (sec.SarjetaEm(side) > 1e-3)
                    breaks.Add(sg * face);
                if (sec.MeioFioEm(side) > 1e-3)
                    breaks.Add(sg * back);
            }
            else if (back > edge + 1e-3)
            {
                breaks.Add(sg * back);
            }

            if (walk > back + 1e-3)
                breaks.Add(sg * walk);
        }

        // Canteiro: uma unica quebra quando nao ha (eixo = divisoria).
        if (c <= 1e-3)
            breaks.Add(0.0);

        List<double> ordered = breaks.OrderBy(v => v).ToList();
        List<double> clean = new();
        foreach (double v in ordered)
        {
            if (clean.Count == 0 || v - clean[^1] > 0.005)
                clean.Add(v);
        }

        return clean;
    }

    private static Box2 Bounds(IEnumerable<Seg> segs)
    {
        Box2 box = Box2.Empty;
        foreach (Seg sg in segs)
            box = box.Union(sg.Bounds());
        return box;
    }
}
