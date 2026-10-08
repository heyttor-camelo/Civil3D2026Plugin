using Autodesk.AutoCAD.Geometry;

namespace Civil3D2026Plugin.Modules.CorridorTools.RaisedCrossing;

internal enum CrossingAnchorMode
{
    Middle = 0,
    Start = 1,
    End = 2
}

internal enum CrossingHeightMode
{
    // Mantido por retrocompatibilidade com registros antigos.
    Automatic = 0,
    Manual = 1
}

internal enum CrossingLengthInputMode
{
    Click = 0,
    Length = 1
}

internal sealed class RaisedCrossingSettings
{
    // Extensao da passagem para fora de cada par de Feature Lines, no sentido do pedestre.
    public double DefaultOverhang { get; set; } = 1.00;

    // Comprimento longitudinal das rampas de entrada/saida dos veiculos.
    public double DefaultRampLength { get; set; } = 1.00;

    // Ultima altura H informada; usada apenas como valor padrao no proximo PASSAGEM.
    public double DefaultHeight { get; set; } = 0.15;

    public double SampleStep { get; set; } = 0.50;
    public CrossingAnchorMode LastAnchorMode { get; set; } = CrossingAnchorMode.Middle;
    public CrossingLengthInputMode LastLengthInputMode { get; set; } = CrossingLengthInputMode.Click;
}

internal sealed class RaisedCrossingData
{
    public string GroupId { get; set; } = string.Empty;

    // Ordem transversal obrigatoria:
    // FL1 externa VIA 1 -> FL2 interna VIA 1 -> FL3 interna VIA 2 -> FL4 externa VIA 2.
    public string FeatureLine1Handle { get; set; } = string.Empty;
    public string FeatureLine2Handle { get; set; } = string.Empty;
    public string FeatureLine3Handle { get; set; } = string.Empty;
    public string FeatureLine4Handle { get; set; } = string.Empty;

    public string SolidHandle { get; set; } = string.Empty;

    public CrossingAnchorMode AnchorMode { get; set; } = CrossingAnchorMode.Middle;
    public double ReferenceFraction { get; set; }

    // Comprimento do plato elevado, SEM incluir as rampas veiculares.
    public double Length { get; set; }

    // Extensao transversal para alem de cada Feature Line de bordo.
    // Nas bordas internas, o conector do canteiro inicia somente APOS essa extensao.
    public double Overhang { get; set; }

    // Comprimento de CADA rampa veicular, antes/depois do plato, em cada via.
    public double RampLength { get; set; } = 1.00;

    public double SampleStep { get; set; } = 0.50;

    // PASSAGEM v1.1.0 usa altura manual como regra principal.
    // O campo de modo permanece para compatibilidade com dados anteriores.
    public CrossingHeightMode HeightMode { get; set; } = CrossingHeightMode.Manual;
    public double Height { get; set; }

    // Campos antigos de Corridor/Subassembly mantidos para leitura de DWGs v1.0.x.
    public string CorridorName { get; set; } = string.Empty;
    public string BaselineName { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;
    public string SubassemblyName { get; set; } = string.Empty;
    public double CgStation { get; set; }
    public bool HasReferenceStation { get; set; }
    public double ReferenceStation { get; set; }

    public Point3d LastReferencePoint { get; set; } = Point3d.Origin;

    public bool IsDualRoad =>
        !string.IsNullOrWhiteSpace(FeatureLine3Handle) &&
        !string.IsNullOrWhiteSpace(FeatureLine4Handle);
}

internal readonly record struct CrossingPlacement(
    Point3d ReferencePoint,
    Point3d CgPoint,
    double StartDistance1,
    double EndDistance1,
    double StartDistance2,
    double EndDistance2);

/// <summary>
/// Perfil vertical/transversal usado pelo loft.
/// Base1/Base2 definem a base; Top1/Top2 = Base + elevacao local.
/// </summary>
internal readonly record struct CrossingSection(
    Point3d Base1Outer,
    Point3d Base2Outer,
    Point3d Top2Outer,
    Point3d Top1Outer);

internal sealed class CrossingGeometryResult
{
    public required AcadDb.Solid3d Solid { get; init; }
    public required Point3d ReferencePoint { get; init; }
    public required Point3d CgPoint { get; init; }
    public required double ReferenceDistance { get; init; }

    // Limites do plato elevado na FL1 de referencia.
    public required double StartDistance { get; init; }
    public required double EndDistance { get; init; }

    // Limites totais das vias, incluindo as rampas de veiculos.
    public required double RampStartDistance { get; init; }
    public required double RampEndDistance { get; init; }

    public required string LoftMode { get; init; }
    public required int SectionCount { get; init; }

    // Pontos das 4 Feature Lines na secao do CG. Usados no Property Set/auditoria.
    public required Point3d FeaturePoint1AtCg { get; init; }
    public required Point3d FeaturePoint2AtCg { get; init; }
    public required Point3d FeaturePoint3AtCg { get; init; }
    public required Point3d FeaturePoint4AtCg { get; init; }

    public required double Lane1WidthAtCg { get; init; }
    public required double MedianWidthAtCg { get; init; }
    public required double Lane2WidthAtCg { get; init; }
}
