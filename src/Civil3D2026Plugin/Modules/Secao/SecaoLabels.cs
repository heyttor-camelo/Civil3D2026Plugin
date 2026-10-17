using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Secao;

/// <summary>Um rotulo da secao: chave estavel (para o deslocamento salvo), texto e ponto de ancoragem (S, Z).</summary>
internal readonly record struct LabelSpec(string Key, string Text, double S, double Z);

/// <summary>
/// Rotulos de um item conforme o menu Textos (C# puro; a janela e o desenho usam os
/// mesmos): nomes de superficies/corredores no fim da linha, nomes de tubos/estruturas/
/// solidos/blocos no topo, nomes das linhas cortadas e pontos COGO no ponto, com a
/// elevacao opcional. A chave junta a secao, o item e a parte, e e nela que fica o
/// deslocamento quando o rotulo e arrastado com Ctrl.
/// </summary>
internal static class SecaoLabels
{
    private static string Z(double z) => z.ToString("0.00", CultureInfo.InvariantCulture);

    public static string KeyPrefix(string section, SectionItem it) => section + "|" + it.Key + "|";

    public static IEnumerable<LabelSpec> For(SectionItem it, string section, Func<string, bool> texto)
    {
        bool elev = texto("elevacao");
        if (it.Ghost && !texto("fantasmas") && it.Kind != ItemKind.Ponto)
            yield break;
        string pre = KeyPrefix(section, it);
        switch (it.Kind)
        {
            case ItemKind.Superficie or ItemKind.Corredor:
                if (texto("nomes") && it.Polylines.FirstOrDefault(p => p.Count > 0) is { } pl)
                {
                    (double s, double z) = pl[^1];
                    yield return new LabelSpec(pre + "n", it.Name + (elev ? $" ({Z(z)})" : ""), s, z);
                }

                break;

            case ItemKind.Tubo or ItemKind.Estrutura or ItemKind.Solido or ItemKind.Bloco:
                if (!texto("objetos"))
                    break;
                if (it.Circles.Count > 0)
                {
                    (double s, double z, double r) = it.Circles.OrderByDescending(c => c.R).First();
                    string d = it.Info.TryGetValue("Diametro", out string? dia) ? " D" + dia.Replace(" m", "") : "";
                    yield return new LabelSpec(pre + "o", it.Name + d + (elev ? $" ({Z(z)})" : ""), s, z + r);
                }
                else if (Top(it) is (double s, double z))
                {
                    yield return new LabelSpec(pre + "o", it.Name + (elev ? $" ({Z(z)})" : ""), s, z);
                }

                break;

            case ItemKind.FeatureLine or ItemKind.Polilinha3d:
                if (!texto("linhas"))
                    break;
                if (it.Points.Count > 0)
                {
                    for (int i = 0; i < it.Points.Count; i++)
                    {
                        (double s, double z, string label) = it.Points[i];
                        if (label.Length > 0)
                            yield return new LabelSpec(pre + "p" + i, label + (elev ? $" ({Z(z)})" : ""), s, z);
                    }
                }
                else if (it.Polylines.FirstOrDefault(p => p.Count > 0) is { } gl)
                {
                    (double s, double z) = gl[^1];
                    yield return new LabelSpec(pre + "n", it.Name, s, z);
                }

                break;

            case ItemKind.Ponto:
                if (!texto("pontos"))
                    break;
                for (int i = 0; i < it.Points.Count; i++)
                {
                    (double s, double z, string label) = it.Points[i];
                    if (label.Length > 0)
                        yield return new LabelSpec(pre + "p" + i, label + (elev ? $" ({Z(z)})" : ""), s, z);
                }

                break;
        }
    }

    /// <summary>Ponto mais alto das linhas do item (topo, no meio quando ha um patamar).</summary>
    private static (double S, double Z)? Top(SectionItem it)
    {
        double best = double.MinValue, s0 = 0, s1 = 0;
        foreach (List<(double S, double Z)> pl in it.Polylines)
        {
            foreach ((double s, double z) in pl)
            {
                if (z > best + 1e-6)
                {
                    best = z;
                    s0 = s1 = s;
                }
                else if (Math.Abs(z - best) <= 1e-6)
                {
                    s0 = Math.Min(s0, s);
                    s1 = Math.Max(s1, s);
                }
            }
        }

        return best == double.MinValue ? null : ((s0 + s1) / 2, best);
    }
}
