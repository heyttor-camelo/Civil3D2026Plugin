using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Nomes em portugues das medidas do catalogo (PartData), pelo contexto do Civil. O codigo da
/// coluna (SIL, SIW, WTh...) continua valendo nas regras; o nome em portugues e o que aparece
/// no painel, na Peca 3D e nos prompts, e tambem e aceito como variavel (comprimento, largura...).
/// </summary>
internal static class HydroNames
{
    /// <summary>Contexto -> (nome longo, nome curto/variavel).</summary>
    private static readonly Dictionary<string, (string Long, string Short)> ByContext = new(StringComparer.OrdinalIgnoreCase)
    {
        ["StructInnerLength"] = ("Comprimento interno", "comprimento"),
        ["StructInnerWidth"] = ("Largura interna", "largura"),
        ["StructInnerDiameter"] = ("Diametro interno", "diametro"),
        ["WallThickness"] = ("Espessura da parede", "parede"),
        ["FloorThickness"] = ("Espessura do fundo", "espfundo"),
        ["StructRimToSumpHeight"] = ("Profundidade (tampa ao fundo)", "profundidade"),
        ["StructSlabThickness"] = ("Espessura da laje (tampa)", "laje"),
        ["StructFrameHeight"] = ("Altura da moldura", "moldura"),
        ["StructFrameDiameter"] = ("Diametro do tampao", "tampao"),
        ["StructFrameWidth"] = ("Largura da moldura", "largmoldura"),
        ["StructFrameLength"] = ("Comprimento da moldura", "compmoldura"),
        ["StructConeHeight"] = ("Altura do cone (chamine)", "cone"),
        ["StructBarrelHeight"] = ("Altura do corpo", "corpo"),
        ["StructHeight"] = ("Altura total", "altura"),
        ["StructLength"] = ("Comprimento externo", "compext"),
        ["StructWidth"] = ("Largura externa", "largext"),
        ["StructDiameter"] = ("Diametro externo", "diamext"),
        ["StructVertPipeClearance"] = ("Folga vertical dos tubos", "folgavert"),
        ["StructVerticalPipeClearance"] = ("Folga vertical dos tubos", "folgavert"),
        ["StructBarrelPipeClearance"] = ("Folga dos tubos no corpo", "folgacorpo"),
        ["PipeInnerDiameter"] = ("Diametro interno", "diametro"),
        ["PipeInnerWidth"] = ("Largura interna", "largura"),
        ["PipeInnerHeight"] = ("Altura interna", "alturaint"),
        ["PipeWallThickness"] = ("Espessura da parede", "parede"),
    };

    /// <summary>Medidas que so desenham detalhes de outro tipo de peca (aparecem como "fixo" e nao interessam).</summary>
    public static readonly HashSet<string> Minor = new(StringComparer.OrdinalIgnoreCase)
    {
        "StructBarrelHeight", "StructConeHeight", "StructFrameDiameter", "StructVertPipeClearance",
        "StructVerticalPipeClearance", "StructBarrelPipeClearance"
    };

    private static readonly string[] EnglishHints = { "Inner", "Structure", "Thickness", "Height", "Width", "Length", "Diameter", "Distance", "Frame", "Slab", "Wall", "Floor", "Rim", "Sump" };

    /// <summary>Nome em portugues: pela tabela do contexto; senao a descricao do catalogo, se ja estiver em portugues.</summary>
    public static string Long(string context, string description, string name)
    {
        if (ByContext.TryGetValue(context ?? "", out (string Long, string Short) t))
            return t.Long;
        string d = (description ?? "").Trim();
        if (d.Length > 0 && !EnglishHints.Any(h => d.Contains(h, StringComparison.OrdinalIgnoreCase)))
            return d;
        return name;
    }

    /// <summary>Nome curto (variavel das regras e rotulo das cotas), ou o codigo da coluna.</summary>
    public static string Short(string context, string name) =>
        ByContext.TryGetValue(context ?? "", out (string Long, string Short) t) ? t.Short : name;

    /// <summary>Contexto pelo nome curto em portugues (comprimento -> StructInnerLength), ou null.</summary>
    public static string? ContextOf(string shortName)
    {
        foreach (KeyValuePair<string, (string Long, string Short)> kv in ByContext)
        {
            if (kv.Value.Short.Equals(shortName, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }

        return null;
    }
}
