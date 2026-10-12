using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Autodesk.AutoCAD.Runtime;
using AecPropData = Autodesk.Aec.PropertyData;
using AecPropDb = Autodesk.Aec.PropertyData.DatabaseServices;

namespace Civil3D2026Plugin.Modules.CorridorTools.SolidArray;

/// <summary>Valores calculados de um estai, gravados no Property Set.</summary>
internal sealed record SolidLinkStayValues(
    string GroupId,
    string Code,
    int Number,
    string Material,
    double DiameterMm,
    double AreaCm2,
    double LengthM,
    double LinearMassKgM,
    double MassKg,
    double DensityKgM3,
    double YieldMPa,
    double UltimateMPa,
    double ElasticModulusGPa,
    double YieldLoadKN,
    double BreakingLoadKN,
    double InclinationDeg,
    double TopElevationM,
    double BottomElevationM,
    string StartPoint,
    string EndPoint,
    string TrimLine1,
    string TrimLine2,
    string Line1Handle,
    string Line2Handle);

/// <summary>
/// Property Set AEC "C3D_ESTAI" anexado a cada barra do C3DSOLIDLIGAR.
/// Mesmo padrao do C3D_PASSAGEM: falha no Property Set nao cancela a geometria.
/// </summary>
internal static class SolidLinkPropertySet
{
    public const string DefinitionName = "C3D_ESTAI";

    private static readonly PropertySpec[] Properties =
    {
        new("ID_Conjunto", AecPropData.DataType.Text, "Identificador do conjunto C3DSOLIDLIGAR."),
        new("Codigo", AecPropData.DataType.Text, "Codigo do estai no conjunto (E01, E02...)."),
        new("Numero", AecPropData.DataType.Integer, "Numero sequencial do estai ao longo da linha 1."),
        new("Versao", AecPropData.DataType.Text, "Versao do C3DSOLIDLIGAR que gerou o objeto."),
        new("Material", AecPropData.DataType.Text, "Tipo de aco/cordoalha."),
        new("Diametro_mm", AecPropData.DataType.Real, "Diametro do solido modelado, em mm."),
        new("Area_Metalica_cm2", AecPropData.DataType.Real, "Area metalica efetiva usada nos calculos, em cm2."),
        new("Comprimento_m", AecPropData.DataType.Real, "Comprimento do eixo do estai, em m."),
        new("Massa_Linear_kg_m", AecPropData.DataType.Real, "Area metalica x densidade, em kg/m."),
        new("Massa_kg", AecPropData.DataType.Real, "Massa do estai, em kg."),
        new("Densidade_kg_m3", AecPropData.DataType.Real, "Densidade do material, em kg/m3."),
        new("fyk_MPa", AecPropData.DataType.Real, "Resistencia caracteristica de escoamento, em MPa."),
        new("fuk_MPa", AecPropData.DataType.Real, "Resistencia caracteristica de ruptura, em MPa."),
        new("Modulo_E_GPa", AecPropData.DataType.Real, "Modulo de elasticidade, em GPa."),
        new("Carga_Escoamento_kN", AecPropData.DataType.Real, "Area metalica x fyk, em kN."),
        new("Carga_Ruptura_kN", AecPropData.DataType.Real, "Area metalica x fuk, em kN."),
        new("Inclinacao_graus", AecPropData.DataType.Real, "Angulo do estai com a horizontal, em graus."),
        new("Cota_Superior_m", AecPropData.DataType.Real, "Maior cota entre as extremidades, em m."),
        new("Cota_Inferior_m", AecPropData.DataType.Real, "Menor cota entre as extremidades, em m."),
        new("Ponto_Linha1", AecPropData.DataType.Text, "Coordenadas X;Y;Z da extremidade na linha 1 (unidades do desenho)."),
        new("Ponto_Linha2", AecPropData.DataType.Text, "Coordenadas X;Y;Z da extremidade na linha 2 (unidades do desenho)."),
        new("Desconto_Linha1", AecPropData.DataType.Text, "Percentuais descontados no inicio/fim da linha 1."),
        new("Desconto_Linha2", AecPropData.DataType.Text, "Percentuais descontados no inicio/fim da linha 2."),
        new("Linha1_Handle", AecPropData.DataType.Text, "Handle da linha 1."),
        new("Linha2_Handle", AecPropData.DataType.Text, "Handle da linha 2.")
    };

    /// <summary>
    /// Cria (se preciso) a definicao e grava os valores no solido.
    /// O solido precisa estar no banco de dados e aberto para escrita.
    /// </summary>
    public static bool TryApply(
        AcadDb.Transaction tr,
        AcadDb.Database db,
        AcadDb.Solid3d solid,
        SolidLinkStayValues stay,
        out string message)
    {
        message = string.Empty;

        try
        {
            AcadDb.ObjectId definitionId = EnsureDefinition(tr, db);
            AcadDb.ObjectId propertySetId = TryGetPropertySetId(solid, definitionId);

            if (propertySetId.IsNull)
            {
                AecPropDb.PropertyDataServices.AddPropertySet(solid, definitionId);
                propertySetId = AecPropDb.PropertyDataServices.GetPropertySet(solid, definitionId);
            }

            if (propertySetId.IsNull)
            {
                message = "A definicao foi criada, mas o Property Set nao foi anexado ao Solid3d.";
                return false;
            }

            if (tr.GetObject(propertySetId, AcadDb.OpenMode.ForWrite, false) is not AecPropDb.PropertySet propertySet)
            {
                message = "O objeto AEC anexado nao pode ser aberto como PropertySet.";
                return false;
            }

            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID_Conjunto"] = stay.GroupId,
                ["Codigo"] = stay.Code,
                ["Numero"] = stay.Number,
                ["Versao"] = C3DVersions.SolidLink,
                ["Material"] = stay.Material,
                ["Diametro_mm"] = Math.Round(stay.DiameterMm, 2),
                ["Area_Metalica_cm2"] = Math.Round(stay.AreaCm2, 4),
                ["Comprimento_m"] = Math.Round(stay.LengthM, 3),
                ["Massa_Linear_kg_m"] = Math.Round(stay.LinearMassKgM, 3),
                ["Massa_kg"] = Math.Round(stay.MassKg, 2),
                ["Densidade_kg_m3"] = stay.DensityKgM3,
                ["fyk_MPa"] = stay.YieldMPa,
                ["fuk_MPa"] = stay.UltimateMPa,
                ["Modulo_E_GPa"] = stay.ElasticModulusGPa,
                ["Carga_Escoamento_kN"] = Math.Round(stay.YieldLoadKN, 1),
                ["Carga_Ruptura_kN"] = Math.Round(stay.BreakingLoadKN, 1),
                ["Inclinacao_graus"] = Math.Round(stay.InclinationDeg, 2),
                ["Cota_Superior_m"] = Math.Round(stay.TopElevationM, 3),
                ["Cota_Inferior_m"] = Math.Round(stay.BottomElevationM, 3),
                ["Ponto_Linha1"] = stay.StartPoint,
                ["Ponto_Linha2"] = stay.EndPoint,
                ["Desconto_Linha1"] = stay.TrimLine1,
                ["Desconto_Linha2"] = stay.TrimLine2,
                ["Linha1_Handle"] = stay.Line1Handle,
                ["Linha2_Handle"] = stay.Line2Handle
            };

            foreach ((string propertyName, object value) in values)
            {
                int propertyId = propertySet.PropertyNameToId(propertyName);
                propertySet.SetAt(propertyId, value);
            }

            return true;
        }
        catch (System.Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static AcadDb.ObjectId EnsureDefinition(AcadDb.Transaction tr, AcadDb.Database db)
    {
        var dictionary = new AecPropDb.DictionaryPropertySetDefinitions(db);

        AecPropDb.PropertySetDefinition definition;
        AcadDb.ObjectId definitionId;

        if (dictionary.Has(DefinitionName, tr))
        {
            definitionId = dictionary.GetAt(DefinitionName);
            definition = (AecPropDb.PropertySetDefinition)tr.GetObject(definitionId, AcadDb.OpenMode.ForWrite, false);
        }
        else
        {
            definition = new AecPropDb.PropertySetDefinition();
            definition.SetToStandard(db);
            definition.SubSetDatabaseDefaults(db);
            definition.Description = "Estais/pendurais gerados pelo C3DSOLIDLIGAR (Civil3D2026Plugin).";

            var appliesTo = new StringCollection
            {
                RXObject.GetClass(typeof(AcadDb.Solid3d)).Name
            };
            definition.SetAppliesToFilter(appliesTo, false);

            // Em definicoes novas, os campos entram antes do registro no dicionario AEC.
            EnsureProperties(db, definition);

            dictionary.AddNewRecord(DefinitionName, definition);
            tr.AddNewlyCreatedDBObject(definition, true);
            definitionId = definition.ObjectId;
        }

        // Garante campos novos em definicoes criadas por versoes anteriores.
        EnsureProperties(db, definition);
        return definitionId;
    }

    private static void EnsureProperties(AcadDb.Database db, AecPropDb.PropertySetDefinition definition)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AecPropDb.PropertyDefinition property in definition.Definitions)
            existing.Add(property.Name);

        foreach (PropertySpec spec in Properties)
        {
            if (existing.Contains(spec.Name))
                continue;

            var property = new AecPropDb.PropertyDefinition();
            property.SetToStandard(db);
            property.SubSetDatabaseDefaults(db);
            property.Name = spec.Name;
            property.Description = spec.Description;
            property.DataType = spec.DataType;
            property.IsVisible = true;
            definition.Definitions.Add(property);
        }
    }

    private static AcadDb.ObjectId TryGetPropertySetId(AcadDb.DBObject entity, AcadDb.ObjectId definitionId)
    {
        try
        {
            return AecPropDb.PropertyDataServices.GetPropertySet(entity, definitionId);
        }
        catch
        {
            return AcadDb.ObjectId.Null;
        }
    }

    private readonly record struct PropertySpec(string Name, AecPropData.DataType DataType, string Description);
}
