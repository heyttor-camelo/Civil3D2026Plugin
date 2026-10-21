using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;
using AecPropDb = Autodesk.Aec.PropertyData.DatabaseServices;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Dica no cursor (PointMonitor) sobre tubos e estruturas: nome, tampa, fundo, diametro,
/// declividade, recobrimento e, quando calculado, Q, lamina, velocidade e avisos (Property Set HYDRO).
/// Mesmo esquema do tooltip dos estais: cache por objeto, limpo ao fim de cada comando.
/// </summary>
internal static class HydroTooltip
{
    private const string Module = "Hydro";
    private static readonly HashSet<Document> AttachedDocuments = new();
    private static bool _initialized;
    private static AcadDb.ObjectId _cachedId = AcadDb.ObjectId.Null;
    private static string? _cachedText;

    public static bool Enabled { get; set; } = true;

    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        DocumentCollection docs = AcApp.DocumentManager;
        docs.DocumentCreated += OnDocumentCreated;
        docs.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        foreach (Document doc in docs)
            Attach(doc);
    }

    public static void Terminate()
    {
        if (!_initialized)
            return;
        _initialized = false;
        try
        {
            DocumentCollection docs = AcApp.DocumentManager;
            docs.DocumentCreated -= OnDocumentCreated;
            docs.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            foreach (Document doc in new List<Document>(AttachedDocuments))
                Detach(doc);
        }
        catch
        {
            // Encerramento do AutoCAD.
        }
    }

    public static void ClearCache()
    {
        _cachedId = AcadDb.ObjectId.Null;
        _cachedText = null;
    }

    private static void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e) => Attach(e.Document);

    private static void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e) => Detach(e.Document);

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
            AcadDb.ObjectId id = ids[0];
            if (id.IsNull || id.IsErased)
                return;
            bool pipe = id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(CivilDb.Pipe)));
            bool structure = !pipe && id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(CivilDb.Structure)));
            if (!pipe && !structure)
                return;

            string? text = id == _cachedId ? _cachedText : BuildText(id, pipe);
            _cachedId = id;
            _cachedText = text;
            if (!string.IsNullOrEmpty(text))
                e.AppendToolTipText(text);
        }
        catch
        {
            // Tooltip e so informativo.
        }
    }

    private static string N(double v, int d = 3) => double.IsNaN(v) ? "-" : v.ToString("F" + d, CultureInfo.InvariantCulture);

    private static string? BuildText(AcadDb.ObjectId id, bool pipe)
    {
        AcadDb.Database db = id.Database;
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        List<string> lines = new();
        if (pipe)
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not CivilDb.Pipe p)
                return null;
            bool startUp = p.FlowDirection != CivilDb.FlowDirectionType.EndToStart;
            double half = (p.InnerHeight > 0 ? p.InnerHeight : p.InnerDiameterOrWidth) / 2.0;
            double up = (startUp ? p.StartPoint.Z : p.EndPoint.Z) - half, down = (startUp ? p.EndPoint.Z : p.StartPoint.Z) - half;
            double slope = p.Length2D > 1e-9 ? (up - down) / p.Length2D : 0;
            lines.Add($"HYDRO  {p.Name}  ({p.PartFamilyName} / {p.PartSizeName})");
            lines.Add($"D {p.InnerDiameterOrWidth * 1000:0} mm   L {N(p.Length2D, 2)} m   i {N(slope * 100, 3)} %   sentido {p.FlowDirection}");
            lines.Add($"Fundo montante {N(up)}   jusante {N(down)}   recobrimento {N(Math.Min(p.CoverOfStartPoint, p.CoverOfEndpoint), 2)} m");
            AppendPropertySet(tr, p, lines);
        }
        else
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not CivilDb.Structure s)
                return null;
            lines.Add($"HYDRO  {s.Name}  ({s.PartFamilyName} / {s.PartSizeName})");
            lines.Add($"Tampa {N(s.RimElevation)}   fundo {N(s.SumpElevation)}   profundidade {N(s.RimElevation - s.SumpElevation, 2)} m   tubos {s.ConnectedPipesCount}");
            AppendPropertySet(tr, s, lines);
        }

        return string.Join("\n", lines);
    }

    private static void AppendPropertySet(AcadDb.Transaction tr, AcadDb.DBObject obj, List<string> lines)
    {
        try
        {
            AecPropDb.DictionaryPropertySetDefinitions dict = new(obj.Database);
            if (!dict.Has(HydroPropertySet.DefinitionName, tr))
                return;
            AcadDb.ObjectId defId = dict.GetAt(HydroPropertySet.DefinitionName);
            AcadDb.ObjectId psId = AecPropDb.PropertyDataServices.GetPropertySet(obj, defId);
            if (psId.IsNull || tr.GetObject(psId, AcadDb.OpenMode.ForRead, false) is not AecPropDb.PropertySet ps)
                return;
            double q = Read(ps, "Q_m3s"), yd = Read(ps, "Lamina_yD"), v = Read(ps, "V_ms"), a = Read(ps, "Area_ha");
            if (q > 0 || a > 0)
                lines.Add($"Q {N(q, 4)} m3/s   A {N(a, 3)} ha   y/D {N(yd * 100, 0)} %   V {N(v, 2)} m/s");
            string avisos = ps.GetAt(ps.PropertyNameToId("Avisos")) as string ?? "";
            if (avisos.Length > 0)
                lines.Add("Avisos: " + avisos);
        }
        catch (System.Exception ex)
        {
            PluginLog.Diag(Module, "Tooltip sem Property Set: " + ex.Message);
        }
    }

    private static double Read(AecPropDb.PropertySet ps, string name)
    {
        try
        {
            object v = ps.GetAt(ps.PropertyNameToId(name));
            return v is double d ? d : v is int i ? i : double.NaN;
        }
        catch
        {
            return double.NaN;
        }
    }
}
