using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Autodesk.AutoCAD.Runtime;
using AecPropData = Autodesk.Aec.PropertyData;
using AecPropDb = Autodesk.Aec.PropertyData.DatabaseServices;

namespace Civil3D2026Plugin.Modules.CorridorTools.RaisedCrossing;

/// <summary>
/// Property Set AEC anexado ao Solid3d final da PASSAGEM.
///
/// A rotina e deliberadamente "nao fatal": falha de Property Set nao cancela a
/// criacao/atualizacao geometrica da passagem. O chamador recebe o aviso para exibir.
/// </summary>
internal static class RaisedCrossingPropertySet
{
    public const string DefinitionName = "C3D_PASSAGEM";

    private static readonly PropertySpec[] Properties =
    {
        new("ID", AecPropData.DataType.Text, "Identificador interno da passagem."),
        new("Versao", AecPropData.DataType.Text, "Versao do modulo PASSAGEM que atualizou o objeto."),
        new("Tipo", AecPropData.DataType.Text, "Tipo de objeto gerado pelo plugin."),
        new("Altura_H", AecPropData.DataType.Real, "Altura vertical da passagem acima das cotas-base."),
        new("Comprimento_Plato", AecPropData.DataType.Real, "Comprimento do plato sem as rampas veiculares."),
        new("Comprimento_Rampa", AecPropData.DataType.Real, "Comprimento de cada rampa veicular."),
        new("Extensao", AecPropData.DataType.Real, "Extensao transversal aplicada alem das Feature Lines."),
        new("Modo_Ancoragem", AecPropData.DataType.Text, "Meio, Inicio ou Fim."),
        new("FL1_Handle", AecPropData.DataType.Text, "Handle da Feature Line externa da VIA 1."),
        new("FL2_Handle", AecPropData.DataType.Text, "Handle da Feature Line interna da VIA 1."),
        new("FL3_Handle", AecPropData.DataType.Text, "Handle da Feature Line interna da VIA 2."),
        new("FL4_Handle", AecPropData.DataType.Text, "Handle da Feature Line externa da VIA 2."),
        new("Cota_FL1_CG", AecPropData.DataType.Real, "Cota da FL1 na secao do CG."),
        new("Cota_FL2_CG", AecPropData.DataType.Real, "Cota da FL2 na secao do CG."),
        new("Cota_FL3_CG", AecPropData.DataType.Real, "Cota da FL3 na secao do CG."),
        new("Cota_FL4_CG", AecPropData.DataType.Real, "Cota da FL4 na secao do CG."),
        new("Largura_Via1_CG", AecPropData.DataType.Real, "Distancia em planta FL1-FL2 na secao do CG."),
        new("Largura_Canteiro_CG", AecPropData.DataType.Real, "Distancia em planta FL2-FL3 na secao do CG."),
        new("Largura_Via2_CG", AecPropData.DataType.Real, "Distancia em planta FL3-FL4 na secao do CG.")
    };

    public static bool TryApplyOrUpdate(
        AcadDb.Transaction tr,
        AcadDb.Database db,
        AcadDb.Solid3d solid,
        RaisedCrossingData data,
        CrossingGeometryResult geometry,
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

            var propertySet = tr.GetObject(
                propertySetId,
                AcadDb.OpenMode.ForWrite,
                false) as AecPropDb.PropertySet;

            if (propertySet is null)
            {
                message = "O objeto AEC anexado nao pode ser aberto como PropertySet.";
                return false;
            }

            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID"] = data.GroupId,
                ["Versao"] = C3DVersions.RaisedCrossing,
                ["Tipo"] = "Passagem semi-elevada - duas vias com canteiro central",
                ["Altura_H"] = data.Height,
                ["Comprimento_Plato"] = data.Length,
                ["Comprimento_Rampa"] = data.RampLength,
                ["Extensao"] = data.Overhang,
                ["Modo_Ancoragem"] = GetAnchorLabel(data.AnchorMode),
                ["FL1_Handle"] = data.FeatureLine1Handle,
                ["FL2_Handle"] = data.FeatureLine2Handle,
                ["FL3_Handle"] = data.FeatureLine3Handle,
                ["FL4_Handle"] = data.FeatureLine4Handle,
                ["Cota_FL1_CG"] = geometry.FeaturePoint1AtCg.Z,
                ["Cota_FL2_CG"] = geometry.FeaturePoint2AtCg.Z,
                ["Cota_FL3_CG"] = geometry.FeaturePoint3AtCg.Z,
                ["Cota_FL4_CG"] = geometry.FeaturePoint4AtCg.Z,
                ["Largura_Via1_CG"] = geometry.Lane1WidthAtCg,
                ["Largura_Canteiro_CG"] = geometry.MedianWidthAtCg,
                ["Largura_Via2_CG"] = geometry.Lane2WidthAtCg
            };

            foreach ((string propertyName, object value) in values)
            {
                int propertyId = propertySet.PropertyNameToId(propertyName);
                propertySet.SetAt(propertyId, value);
            }

            message = $"Property Set '{DefinitionName}' anexado/atualizado.";
            return true;
        }
        catch (System.Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static AcadDb.ObjectId EnsureDefinition(
        AcadDb.Transaction tr,
        AcadDb.Database db)
    {
        var dictionary = new AecPropDb.DictionaryPropertySetDefinitions(db);

        AecPropDb.PropertySetDefinition definition;
        AcadDb.ObjectId definitionId;

        if (dictionary.Has(DefinitionName, tr))
        {
            definitionId = dictionary.GetAt(DefinitionName);
            definition = (AecPropDb.PropertySetDefinition)tr.GetObject(
                definitionId,
                AcadDb.OpenMode.ForWrite,
                false);
        }
        else
        {
            definition = new AecPropDb.PropertySetDefinition();
            definition.SetToStandard(db);
            definition.SubSetDatabaseDefaults(db);
            definition.Description = "Parametros da passagem semi-elevada gerada pelo Civil3D2026Plugin.";

            var appliesTo = new StringCollection
            {
                RXObject.GetClass(typeof(AcadDb.Solid3d)).Name
            };
            definition.SetAppliesToFilter(appliesTo, false);

            // Em definicoes novas, adicione os campos antes de registrar a definicao
            // no dicionario AEC.
            EnsureProperties(db, definition);

            dictionary.AddNewRecord(DefinitionName, definition);
            tr.AddNewlyCreatedDBObject(definition, true);
            definitionId = definition.ObjectId;
        }

        // Tambem garante campos novos caso a definicao ja exista de uma versao anterior.
        EnsureProperties(db, definition);
        return definitionId;
    }

    private static void EnsureProperties(
        AcadDb.Database db,
        AecPropDb.PropertySetDefinition definition)
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

    private static AcadDb.ObjectId TryGetPropertySetId(
        AcadDb.DBObject entity,
        AcadDb.ObjectId definitionId)
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

    private static string GetAnchorLabel(CrossingAnchorMode mode) =>
        mode switch
        {
            CrossingAnchorMode.Start => "Inicio",
            CrossingAnchorMode.End => "Fim",
            _ => "Meio"
        };

    private readonly record struct PropertySpec(
        string Name,
        AecPropData.DataType DataType,
        string Description);
}
