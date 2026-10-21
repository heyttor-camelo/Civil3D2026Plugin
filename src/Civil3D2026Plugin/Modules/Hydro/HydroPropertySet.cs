using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Autodesk.AutoCAD.Runtime;
using AecPropData = Autodesk.Aec.PropertyData;
using AecPropDb = Autodesk.Aec.PropertyData.DatabaseServices;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Property Set "HYDRO" em tubos e estruturas: o que nao tem campo nativo no Civil (area, C, Tc,
/// intensidade, lamina, velocidade, Q plena, tempo de percurso, avisos). Falha no Property Set
/// nao cancela o calculo (mesmo padrao do C3D_ESTAI).
/// </summary>
internal static class HydroPropertySet
{
    public const string DefinitionName = "HYDRO";

    private sealed record Spec(string Name, AecPropData.DataType Type, string Description);

    private static readonly Spec[] Properties =
    {
        new("Q_m3s", AecPropData.DataType.Real, "Vazao de projeto (metodo racional acumulado), m3/s."),
        new("Area_ha", AecPropData.DataType.Real, "Area contribuinte acumulada, ha."),
        new("C", AecPropData.DataType.Real, "Coeficiente de escoamento medio ponderado."),
        new("Tc_min", AecPropData.DataType.Real, "Tempo de concentracao no inicio do trecho, min."),
        new("I_mmh", AecPropData.DataType.Real, "Intensidade da chuva, mm/h."),
        new("Lamina_yD", AecPropData.DataType.Real, "Relacao lamina/diametro (0 a 1)."),
        new("V_ms", AecPropData.DataType.Real, "Velocidade do escoamento, m/s."),
        new("Qplena_m3s", AecPropData.DataType.Real, "Vazao a secao plena, m3/s."),
        new("Tpercurso_min", AecPropData.DataType.Real, "Tempo de percurso no trecho, min."),
        new("Tr_anos", AecPropData.DataType.Real, "Periodo de retorno usado, anos."),
        new("IDF", AecPropData.DataType.Text, "Equacao IDF usada."),
        new("Avisos", AecPropData.DataType.Text, "Violacoes de criterio no item."),
        new("Versao", AecPropData.DataType.Text, "Versao do HYDRO que calculou.")
    };

    public static bool TryApply(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.DBObject target, Dictionary<string, object> values, out string message)
    {
        message = "";
        try
        {
            AcadDb.ObjectId definitionId = EnsureDefinition(tr, db);
            AcadDb.ObjectId propertySetId = AcadDb.ObjectId.Null;
            try
            {
                propertySetId = AecPropDb.PropertyDataServices.GetPropertySet(target, definitionId);
            }
            catch
            {
                // Ainda nao anexado.
            }

            if (propertySetId.IsNull)
            {
                AecPropDb.PropertyDataServices.AddPropertySet(target, definitionId);
                propertySetId = AecPropDb.PropertyDataServices.GetPropertySet(target, definitionId);
            }

            if (propertySetId.IsNull || tr.GetObject(propertySetId, AcadDb.OpenMode.ForWrite, false) is not AecPropDb.PropertySet set)
            {
                message = "Property Set nao anexado.";
                return false;
            }

            foreach ((string name, object value) in values)
            {
                int id = set.PropertyNameToId(name);
                set.SetAt(id, value);
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
        AecPropDb.DictionaryPropertySetDefinitions dictionary = new(db);
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
            definition.Description = "Resultados hidraulicos do HYDRO (Civil3D2026Plugin).";
            StringCollection appliesTo = new()
            {
                RXObject.GetClass(typeof(CivilDb.Pipe)).Name,
                RXObject.GetClass(typeof(CivilDb.Structure)).Name
            };
            definition.SetAppliesToFilter(appliesTo, false);
            EnsureProperties(db, definition);
            dictionary.AddNewRecord(DefinitionName, definition);
            tr.AddNewlyCreatedDBObject(definition, true);
            definitionId = definition.ObjectId;
        }

        EnsureProperties(db, definition);
        return definitionId;
    }

    private static void EnsureProperties(AcadDb.Database db, AecPropDb.PropertySetDefinition definition)
    {
        HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase);
        foreach (AecPropDb.PropertyDefinition property in definition.Definitions)
            existing.Add(property.Name);

        foreach (Spec spec in Properties)
        {
            if (existing.Contains(spec.Name))
                continue;
            AecPropDb.PropertyDefinition property = new();
            property.SetToStandard(db);
            property.SubSetDatabaseDefaults(db);
            property.Name = spec.Name;
            property.Description = spec.Description;
            property.DataType = spec.Type;
            property.IsVisible = true;
            definition.Definitions.Add(property);
        }
    }
}
