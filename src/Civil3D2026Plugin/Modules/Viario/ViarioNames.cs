using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>Texto do desenho candidato a nome de eixo (ponto = centro do texto; angulo em radianos, NaN se nao houver).</summary>
internal readonly record struct TextCandidate(string Text, V2 Point, double Angle, string Layer);

/// <summary>Nome proposto para um eixo a partir dos textos proximos.</summary>
internal sealed class NameProposal
{
    public string AxisKey { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Distancia do texto escolhido mais perto do eixo.</summary>
    public double Distance { get; init; }

    /// <summary>Quantas vezes o nome aparece junto do eixo.</summary>
    public int Hits { get; init; }

    public double Score { get; init; }

    /// <summary>Outros nomes encontrados junto do eixo (para conferir).</summary>
    public List<string> Others { get; init; } = new();
}

/// <summary>
/// Nomes de eixo a partir de textos do desenho (C# puro, testavel): cada texto vai para o
/// eixo que mais "o reivindica" - perto e, com a opcao de angulo, paralelo ao eixo, como os
/// rotulos de rua -, e cada eixo fica com o nome de maior peso somado (rotulos repetidos
/// ao longo da via contam mais). Um rotulo perto de um cruzamento fica so com a via a que
/// esta alinhado.
/// </summary>
internal static class ViarioNames
{
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex OnlyNumber = new(@"^[\d\s.,+\-/]*$", RegexOptions.Compiled);

    /// <summary>Texto limpo: espacos juntados, sem quebras de linha.</summary>
    public static string Clean(string text) => Spaces.Replace(text.Replace("\\P", " "), " ").Trim();

    /// <summary>Texto que nao serve de nome (vazio, curto demais ou so numeros, como cotas e numeros de lote).</summary>
    public static bool Useless(string cleaned, bool ignoreNumbers) =>
        cleaned.Length < 2 || (ignoreNumbers && OnlyNumber.IsMatch(cleaned));

    /// <summary>Peso pelo angulo entre o texto e o eixo (0 a 90 graus; leitura invertida conta como paralela).</summary>
    public static double AngleFactor(double textAngle, V2 tangent)
    {
        if (double.IsNaN(textAngle))
            return 0.6;
        double axis = Math.Atan2(tangent.Y, tangent.X);
        double diff = Math.Abs(GMath.Norm(textAngle - axis)) % Math.PI;
        if (diff > Math.PI / 2)
            diff = Math.PI - diff;
        double deg = diff * 180.0 / Math.PI;
        return deg <= 20 ? 1.0 : deg <= 45 ? 0.35 : 0.1;
    }

    public static List<NameProposal> Propose(IReadOnlyList<AxisInput> axes, IReadOnlyList<TextCandidate> texts, double maxDist,
        bool useAngle = true, bool ignoreNumbers = true)
    {
        List<NameProposal> result = new();
        if (axes.Count == 0 || texts.Count == 0 || maxDist <= 0)
            return result;

        // Textos validos numa grade (celula = distancia maxima).
        double cell = Math.Max(maxDist, 1.0);
        List<(string Clean, TextCandidate T)> valid = new();
        foreach (TextCandidate t in texts)
        {
            string c = Clean(t.Text);
            if (!Useless(c, ignoreNumbers))
                valid.Add((c, t));
        }

        Dictionary<(long, long), List<int>> grid = new();
        (long, long) Cell(V2 p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));
        for (int i = 0; i < valid.Count; i++)
        {
            (long, long) k = Cell(valid[i].T.Point);
            if (!grid.TryGetValue(k, out List<int>? list))
                grid[k] = list = new List<int>();
            list.Add(i);
        }

        // Cada texto fica com o eixo de maior peso.
        (int Axis, double W, double D)[] best = Enumerable.Repeat((-1, 0.0, 0.0), valid.Count).ToArray();
        for (int ai = 0; ai < axes.Count; ai++)
        {
            GPath path = axes[ai].Path;
            if (path.IsEmpty)
                continue;
            HashSet<int> near = new();
            foreach (Seg s in path.Segs)
            {
                Box2 b = s.Bounds().Inflate(maxDist);
                long x0 = (long)Math.Floor(b.MinX / cell), x1 = (long)Math.Floor(b.MaxX / cell);
                long y0 = (long)Math.Floor(b.MinY / cell), y1 = (long)Math.Floor(b.MaxY / cell);
                for (long x = x0; x <= x1; x++)
                {
                    for (long y = y0; y <= y1; y++)
                    {
                        if (grid.TryGetValue((x, y), out List<int>? list))
                            near.UnionWith(list);
                    }
                }
            }

            foreach (int ti in near)
            {
                TextCandidate t = valid[ti].T;
                (double s, _, double d) = path.Project(t.Point);
                if (d > maxDist)
                    continue;
                // Texto alem da ponta do eixo e de outra via (salvo bem em cima da ponta).
                if (!path.Closed && (s <= 1e-6 || s >= path.Length - 1e-6) && d > 1.0)
                    continue;
                double f = useAngle ? AngleFactor(t.Angle, path.TangentAt(s)) : 1.0;
                double w = f / (1.0 + (2.0 * d / maxDist));
                if (w > best[ti].W)
                    best[ti] = (ai, w, d);
            }
        }

        // Soma por nome em cada eixo.
        Dictionary<int, Dictionary<string, (string Text, double Sum, int Hits, double MinD)>> byAxis = new();
        for (int ti = 0; ti < valid.Count; ti++)
        {
            if (best[ti].Axis < 0)
                continue;
            if (!byAxis.TryGetValue(best[ti].Axis, out Dictionary<string, (string, double, int, double)>? names))
                byAxis[best[ti].Axis] = names = new Dictionary<string, (string, double, int, double)>(StringComparer.Ordinal);
            string key = valid[ti].Clean.ToLowerInvariant();
            names[key] = names.TryGetValue(key, out (string Text, double Sum, int Hits, double MinD) cur)
                ? (cur.Text, cur.Sum + best[ti].W, cur.Hits + 1, Math.Min(cur.MinD, best[ti].D))
                : (valid[ti].Clean, best[ti].W, 1, best[ti].D);
        }

        foreach ((int ai, Dictionary<string, (string Text, double Sum, int Hits, double MinD)> names) in byAxis)
        {
            List<(string Text, double Sum, int Hits, double MinD)> ranked = names.Values.OrderByDescending(n => n.Sum).ThenBy(n => n.MinD).ToList();
            (string text, double sum, int hits, double minD) = ranked[0];
            result.Add(new NameProposal
            {
                AxisKey = axes[ai].Key,
                Name = text,
                Distance = minD,
                Hits = hits,
                Score = sum,
                Others = ranked.Skip(1).Take(3).Select(n => n.Text).ToList()
            });
        }

        return result;
    }
}
