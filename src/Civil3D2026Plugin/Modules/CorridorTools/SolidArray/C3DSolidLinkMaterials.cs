using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.CorridorTools.SolidArray;

/// <summary>
/// Material de um estai/barra do C3DSOLIDLIGAR.
/// Resistencias em MPa, modulo de elasticidade em GPa, densidade em kg/m3.
/// </summary>
internal sealed record SolidLinkMaterial(
    string Key,
    string Name,
    double Density,
    double YieldMPa,
    double UltimateMPa,
    double ElasticModulusGPa);

/// <summary>
/// Catalogo de materiais usuais para estais, pendurais e tirantes.
///
/// Valores NOMINAIS de referencia para pre-dimensionamento e quantitativos.
/// Confirme sempre com a especificacao do projeto/fornecedor; use a opcao
/// Outro para informar valores proprios.
/// </summary>
internal static class SolidLinkMaterials
{
    public const string CustomKey = "Outro";

    public static IReadOnlyList<SolidLinkMaterial> Catalog { get; } = new[]
    {
        // NBR 7483: fpyk >= 0,9 fptk; Ep = 200 GPa (NBR 6118).
        new SolidLinkMaterial("CP190", "Cordoalha CP-190 RB", 7850.0, 1710.0, 1900.0, 200.0),

        // ASTM A416 Grau 270: fpu = 1860 MPa; fpy = 0,9 fpu.
        new SolidLinkMaterial("A416", "Cordoalha ASTM A416 Gr. 270", 7850.0, 1674.0, 1860.0, 196.5),

        // NBR 7480: fyk = 500 MPa; fst >= 1,08 fyk.
        new SolidLinkMaterial("CA50", "Barra CA-50", 7850.0, 500.0, 540.0, 210.0),

        // Barras roscadas de alta resistencia tipo ST 85/105.
        new SolidLinkMaterial("ST85", "Barra roscada ST 85/105", 7850.0, 835.0, 1030.0, 205.0),

        // AISI 316 recozido (barras); cabos inox trefilados tem resistencia bem maior.
        new SolidLinkMaterial("INOX", "Aco inox AISI 316 (recozido)", 8000.0, 205.0, 515.0, 193.0)
    };

    public static SolidLinkMaterial Default => Catalog[0];

    public static string KeywordList =>
        string.Join(" ", Catalog.Select(m => m.Key).Append(CustomKey));

    public static string KeywordDisplay =>
        string.Join("/", Catalog.Select(m => m.Key).Append(CustomKey));

    public static SolidLinkMaterial? Find(string key) =>
        Catalog.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
}
