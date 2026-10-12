using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AecPropDb = Autodesk.Aec.PropertyData.DatabaseServices;

namespace Civil3D2026Plugin.Modules.CorridorTools.SolidArray;

/// <summary>
/// Tooltip dos estais: ao passar o cursor sobre um Solid3d com o Property Set
/// C3D_ESTAI, acrescenta os principais valores ao tooltip do AutoCAD.
///
/// - registrado no Editor.PointMonitor de cada documento aberto;
/// - leitura com OpenCloseTransaction (segura dentro do PointMonitor);
/// - cache do ultimo objeto sob o cursor, limpo ao fim de cada comando;
/// - qualquer falha e silenciosa: o tooltip nunca pode interromper o desenho.
/// </summary>
internal static class SolidLinkTooltip
{
    private static readonly HashSet<Document> AttachedDocuments = new();

    private static bool _initialized;
    private static AcadDb.ObjectId _cachedId = AcadDb.ObjectId.Null;
    private static string? _cachedText;

    /// <summary>Liga/desliga o tooltip na sessao atual (padrao: ligado).</summary>
    public static bool Enabled { get; set; } = true;

    public static void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;

        DocumentCollection documents = AcApp.DocumentManager;
        documents.DocumentCreated += OnDocumentCreated;
        documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;

        foreach (Document doc in documents)
            Attach(doc);
    }

    public static void Terminate()
    {
        if (!_initialized)
            return;

        _initialized = false;

        try
        {
            DocumentCollection documents = AcApp.DocumentManager;
            documents.DocumentCreated -= OnDocumentCreated;
            documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        }
        catch
        {
            // Encerramento do AutoCAD: o gerenciador pode ja estar indisponivel.
        }

        foreach (Document doc in new List<Document>(AttachedDocuments))
            Detach(doc);
    }

    public static void ClearCache()
    {
        _cachedId = AcadDb.ObjectId.Null;
        _cachedText = null;
    }

    private static void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e)
    {
        if (e.Document is not null)
            Attach(e.Document);
    }

    private static void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        if (e.Document is not null)
            Detach(e.Document);
    }

    private static void Attach(Document doc)
    {
        if (!AttachedDocuments.Add(doc))
            return;

        doc.Editor.PointMonitor += OnPointMonitor;
        doc.CommandEnded += OnCommandFinished;
        doc.CommandCancelled += OnCommandFinished;
    }

    private static void Detach(Document doc)
    {
        if (!AttachedDocuments.Remove(doc))
            return;

        try
        {
            doc.Editor.PointMonitor -= OnPointMonitor;
            doc.CommandEnded -= OnCommandFinished;
            doc.CommandCancelled -= OnCommandFinished;
        }
        catch
        {
            // Documento em fechamento.
        }

        ClearCache();
    }

    private static void OnCommandFinished(object? sender, CommandEventArgs e) => ClearCache();

    private static void OnPointMonitor(object? sender, PointMonitorEventArgs e)
    {
        if (!Enabled)
            return;

        try
        {
            AcadDb.FullSubentityPath[]? paths = e.Context.GetPickedEntities();
            if (paths is null || paths.Length == 0)
                return;

            AcadDb.ObjectId[] ids = paths[0].GetObjectIds();
            if (ids.Length == 0)
                return;

            // Estais ficam direto no espaco do desenho: o primeiro id e o proprio solido.
            AcadDb.ObjectId id = ids[0];
            if (id.IsNull || id.IsErased ||
                !id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(AcadDb.Solid3d))))
            {
                return;
            }

            string? text = id == _cachedId ? _cachedText : BuildText(id);

            _cachedId = id;
            _cachedText = text;

            if (!string.IsNullOrEmpty(text))
                e.AppendToolTipText(text);
        }
        catch
        {
            // Tooltip e apenas informativo.
        }
    }

    private static string? BuildText(AcadDb.ObjectId id)
    {
        AcadDb.Database db = id.Database;
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();

        var definitions = new AecPropDb.DictionaryPropertySetDefinitions(db);
        if (!definitions.Has(SolidLinkPropertySet.DefinitionName, tr))
            return null;

        AcadDb.ObjectId definitionId = definitions.GetAt(SolidLinkPropertySet.DefinitionName);

        if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Solid3d solid)
            return null;

        AcadDb.ObjectId propertySetId;
        try
        {
            propertySetId = AecPropDb.PropertyDataServices.GetPropertySet(solid, definitionId);
        }
        catch
        {
            return null;
        }

        if (propertySetId.IsNull ||
            tr.GetObject(propertySetId, AcadDb.OpenMode.ForRead, false) is not AecPropDb.PropertySet propertySet)
        {
            return null;
        }

        string code = ReadText(propertySet, "Codigo");
        string material = ReadText(propertySet, "Material");
        double? diameter = ReadDouble(propertySet, "Diametro_mm");
        double? area = ReadDouble(propertySet, "Area_Metalica_cm2");
        double? length = ReadDouble(propertySet, "Comprimento_m");
        double? mass = ReadDouble(propertySet, "Massa_kg");
        double? breaking = ReadDouble(propertySet, "Carga_Ruptura_kN");
        double? yield = ReadDouble(propertySet, "Carga_Escoamento_kN");
        double? inclination = ReadDouble(propertySet, "Inclinacao_graus");

        StringBuilder sb = new();
        sb.Append("Estai ").Append(string.IsNullOrWhiteSpace(code) ? "?" : code);
        if (!string.IsNullOrWhiteSpace(material))
            sb.Append(" | ").Append(material);

        sb.Append('\n')
          .Append("D=").Append(Format(diameter, "0.#")).Append(" mm")
          .Append(" | A=").Append(Format(area, "0.##")).Append(" cm2")
          .Append(" | L=").Append(Format(length, "0.000")).Append(" m");

        sb.Append('\n')
          .Append("Massa=").Append(Format(mass, "0.0")).Append(" kg")
          .Append(" | Incl.=").Append(Format(inclination, "0.0")).Append(" graus");

        sb.Append('\n')
          .Append("Escoamento=").Append(Format(yield, "0.0")).Append(" kN")
          .Append(" | Ruptura=").Append(Format(breaking, "0.0")).Append(" kN");

        return sb.ToString();
    }

    private static object? ReadValue(AecPropDb.PropertySet propertySet, string name)
    {
        try
        {
            int propertyId = propertySet.PropertyNameToId(name);
            return propertySet.GetAt(propertyId);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadText(AecPropDb.PropertySet propertySet, string name) =>
        ReadValue(propertySet, name)?.ToString() ?? string.Empty;

    private static double? ReadDouble(AecPropDb.PropertySet propertySet, string name) =>
        ReadValue(propertySet, name) switch
        {
            double d => d,
            int i => i,
            float f => f,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => null
        };

    private static string Format(double? value, string format) =>
        value is null ? "-" : value.Value.ToString(format, CultureInfo.InvariantCulture);
}
