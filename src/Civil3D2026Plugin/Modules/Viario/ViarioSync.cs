using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Edicao associativa do modulo Viario.
///
/// - Acompanha os eixos (XData EIXO) e as linhas geradas (XData GERADO) de
///   cada desenho por eventos do banco de dados.
/// - Quando um eixo e criado, alterado, copiado ou apagado, a rede fica
///   pendente e e refeita ao fim do comando (ou no Idle, para edicoes pela
///   paleta de propriedades).
/// - A regeneracao compara as linhas desejadas com as existentes pela
///   geometria: so cria/apaga o que mudou.
/// - UNDO/REDO nao disparam regeneracao (o desfazer ja restaura tudo).
/// </summary>
internal static class ViarioSync
{
    private sealed class DbState
    {
        public readonly HashSet<AcadDb.ObjectId> Axes = new();
        public readonly HashSet<AcadDb.ObjectId> Generated = new();
        public bool Scanned;
        public bool Pending;
    }

    private static readonly Dictionary<AcadDb.Database, DbState> States = new();
    private static readonly HashSet<Document> Attached = new();
    private static bool _initialized;
    private static bool _syncing;

    /// <summary>Durante comandos do proprio modulo a regeneracao e chamada explicitamente.</summary>
    public static bool Suspended { get; set; }

    private static readonly HashSet<string> UndoCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "UNDO", "U", "REDO", "MREDO", "OOPS"
    };

    // ------------------------------------------------------------------
    // Ciclo de vida
    // ------------------------------------------------------------------

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

        AcApp.Idle += OnIdle;
    }

    public static void Terminate()
    {
        if (!_initialized)
            return;

        _initialized = false;
        try
        {
            AcApp.Idle -= OnIdle;
            AcApp.DocumentManager.DocumentCreated -= OnDocumentCreated;
            AcApp.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        }
        catch
        {
            // Encerramento.
        }

        foreach (Document doc in Attached.ToList())
            Detach(doc);
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
        if (!Attached.Add(doc))
            return;

        AcadDb.Database db = doc.Database;
        db.ObjectAppended += OnObjectAppended;
        db.ObjectModified += OnObjectModified;
        db.ObjectErased += OnObjectErased;
        doc.CommandEnded += OnCommandEnded;
    }

    private static void Detach(Document doc)
    {
        if (!Attached.Remove(doc))
            return;

        try
        {
            AcadDb.Database db = doc.Database;
            db.ObjectAppended -= OnObjectAppended;
            db.ObjectModified -= OnObjectModified;
            db.ObjectErased -= OnObjectErased;
            doc.CommandEnded -= OnCommandEnded;
            States.Remove(db);
        }
        catch
        {
            // Documento fechando.
        }
    }

    private static DbState State(AcadDb.Database db)
    {
        if (!States.TryGetValue(db, out DbState? state))
        {
            state = new DbState();
            States[db] = state;
        }

        return state;
    }

    // ------------------------------------------------------------------
    // Eventos do banco
    // ------------------------------------------------------------------

    private static bool IsCandidate(AcadDb.DBObject obj) =>
        obj is AcadDb.Polyline || obj is AcadDb.Line || obj is AcadDb.Arc || obj is AcadDb.Circle;

    private static void OnObjectAppended(object? sender, AcadDb.ObjectEventArgs e)
    {
        try
        {
            AcadDb.DBObject obj = e.DBObject;
            if (!IsCandidate(obj))
                return;

            DbState state = State(obj.Database);
            string? tag = ViarioStore.ReadTag(obj, out _);

            if (tag == ViarioStore.TagAxis)
            {
                state.Axes.Add(obj.ObjectId);
                MarkPending(state);
            }
            else if (tag == ViarioStore.TagGenerated)
            {
                state.Generated.Add(obj.ObjectId);
            }
        }
        catch
        {
            // Nunca interromper a edicao do usuario.
        }
    }

    private static void OnObjectModified(object? sender, AcadDb.ObjectEventArgs e)
    {
        try
        {
            AcadDb.DBObject obj = e.DBObject;
            if (!IsCandidate(obj))
                return;

            DbState state = State(obj.Database);
            string? tag = ViarioStore.ReadTag(obj, out _);

            if (tag == ViarioStore.TagGenerated)
            {
                state.Generated.Add(obj.ObjectId);
                return;
            }

            bool wasAxis = state.Axes.Contains(obj.ObjectId);
            bool isAxis = tag == ViarioStore.TagAxis;

            if (isAxis)
                state.Axes.Add(obj.ObjectId);
            else if (wasAxis)
                state.Axes.Remove(obj.ObjectId);

            if (isAxis || wasAxis)
                MarkPending(state);
        }
        catch
        {
            // Nunca interromper a edicao do usuario.
        }
    }

    private static void OnObjectErased(object? sender, AcadDb.ObjectErasedEventArgs e)
    {
        try
        {
            AcadDb.DBObject obj = e.DBObject;
            if (!IsCandidate(obj))
                return;

            DbState state = State(obj.Database);
            string? tag = ViarioStore.ReadTag(obj, out _);

            if (tag == ViarioStore.TagGenerated)
            {
                // Linha gerada que volta por UNDO/REDO precisa voltar ao controle.
                if (!e.Erased)
                    state.Generated.Add(obj.ObjectId);
                return;
            }

            if (state.Axes.Contains(obj.ObjectId) || tag == ViarioStore.TagAxis)
            {
                state.Axes.Add(obj.ObjectId);
                MarkPending(state);
            }
        }
        catch
        {
            // Nunca interromper a edicao do usuario.
        }
    }

    private static void MarkPending(DbState state)
    {
        if (!_syncing)
            state.Pending = true;
    }

    // ------------------------------------------------------------------
    // Disparo da atualizacao
    // ------------------------------------------------------------------

    private static void OnCommandEnded(object? sender, CommandEventArgs e)
    {
        if (sender is not Document doc || Suspended)
            return;

        try
        {
            DbState state = State(doc.Database);
            if (!state.Pending)
                return;

            if (UndoCommands.Contains(e.GlobalCommandName))
            {
                // O desfazer restaurou eixos e linhas juntos.
                state.Pending = false;
                return;
            }

            using (doc.LockDocument())
                Regenerate(doc, quiet: true);
        }
        catch
        {
            // Tenta de novo no Idle.
        }
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        if (_syncing || Suspended)
            return;

        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        try
        {
            DbState state = State(doc.Database);
            if (!state.Pending)
                return;

            if (!doc.Editor.IsQuiescent ||
                !string.IsNullOrEmpty(doc.CommandInProgress) ||
                Convert.ToInt32(AcApp.GetSystemVariable("CMDACTIVE")) != 0 ||
                System.Windows.Forms.Control.MouseButtons != System.Windows.Forms.MouseButtons.None)
            {
                return;
            }

            using (doc.LockDocument())
                Regenerate(doc, quiet: true);
        }
        catch
        {
            // Proxima oportunidade.
        }
    }

    // ------------------------------------------------------------------
    // Regeneracao
    // ------------------------------------------------------------------

    /// <summary>Entradas do motor a partir dos eixos do desenho.</summary>
    public static List<AxisInput> CollectAxes(AcadDb.Database db, AcadDb.Transaction tr, Dictionary<string, RoadSection> sections)
    {
        DbState state = State(db);
        EnsureScanned(db, tr, state);

        List<AxisInput> axes = new();
        RoadSection fallback = sections.Values.FirstOrDefault() ?? ViarioStore.DefaultSection();

        foreach (AcadDb.ObjectId id in state.Axes.ToList())
        {
            if (id.IsNull || id.IsErased)
                continue;

            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Curve curve)
                continue;

            if (!ViarioStore.ReadAxis(curve, out string sectionId, out double? radius, out List<AxisEvent> events))
            {
                state.Axes.Remove(id);
                continue;
            }

            if (!string.Equals(curve.BlockName, AcadDb.BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
                continue;

            GPath? path = ViarioStore.ToPath(curve);
            if (path is null)
                continue;

            RoadSection section = sections.TryGetValue(sectionId, out RoadSection? sec) ? sec : fallback;
            axes.Add(new AxisInput(curve.Handle.ToString() ?? string.Empty, path, section, radius, events));
        }

        return axes;
    }

    /// <summary>Configuracao do motor a partir do DWG.</summary>
    public static EngineSettings EngineSettingsFor(AcadDb.Database db)
    {
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        return new EngineSettings
        {
            SnapTolerance = settings.SnapTolerance,
            MedianSetback = settings.MedianSetback,
            CornerRadius = ViarioStore.LoadCornerRadii(db),
            SlipLanes = ViarioStore.LoadSlipLanes(db)
        };
    }

    /// <summary>Calcula a rede atual sem alterar o desenho.</summary>
    public static (List<AxisInput> Axes, ViarioResult Result, HashSet<string> ExistingKeys) ComputeCurrent(AcadDb.Database db)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        List<AxisInput> axes = CollectAxes(db, tr, SectionMap(db));
        ViarioResult result = ViarioEngine.Compute(axes, EngineSettingsFor(db));
        return (axes, result, ExistingKeys(db, tr));
    }

    /// <summary>Chaves geometricas das linhas geradas que existem hoje no desenho.</summary>
    public static HashSet<string> ExistingKeys(AcadDb.Database db, AcadDb.Transaction tr)
    {
        DbState state = State(db);
        EnsureScanned(db, tr, state);

        HashSet<string> keys = new();
        foreach (AcadDb.ObjectId id in state.Generated)
        {
            if (id.IsNull || id.IsErased)
                continue;

            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.Polyline pl &&
                ViarioStore.ReadTag(pl, out string levelText) == ViarioStore.TagGenerated)
            {
                keys.Add(ViarioStore.GeometryKey(pl, ViarioStore.ParseLevel(levelText)));
            }
        }

        return keys;
    }

    private static void EnsureScanned(AcadDb.Database db, AcadDb.Transaction tr, DbState state)
    {
        if (state.Scanned)
            return;

        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);

        foreach (AcadDb.ObjectId id in ms)
        {
            string dxf = id.ObjectClass.DxfName;
            if (dxf != "LWPOLYLINE" && dxf != "LINE" && dxf != "ARC" && dxf != "CIRCLE")
                continue;

            AcadDb.DBObject obj = tr.GetObject(id, AcadDb.OpenMode.ForRead, false);
            string? tag = ViarioStore.ReadTag(obj, out _);
            if (tag == ViarioStore.TagAxis)
                state.Axes.Add(id);
            else if (tag == ViarioStore.TagGenerated)
                state.Generated.Add(id);
        }

        state.Scanned = true;
    }

    public static Dictionary<string, RoadSection> SectionMap(AcadDb.Database db) =>
        ViarioStore.LoadSections(db).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>
    /// Refaz a rede inteira e aplica so as diferencas. O documento precisa estar
    /// travado (comando em andamento ou LockDocument).
    /// </summary>
    public static ViarioResult? Regenerate(Document doc, bool quiet)
    {
        if (_syncing)
            return null;

        AcadDb.Database db = doc.Database;
        DbState state = State(db);
        _syncing = true;

        try
        {
            Dictionary<string, RoadSection> sections = SectionMap(db);
            HashSet<string> released = ViarioStore.LoadReleased(db);
            ViarioResult result;
            int created = 0, erased = 0;

            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<AxisInput> axes = CollectAxes(db, tr, sections);
                result = ViarioEngine.Compute(axes, EngineSettingsFor(db));

                // Linhas desejadas por chave (multiconjunto).
                Dictionary<string, List<OutputLine>> wanted = new();
                foreach (OutputLine line in result.Lines)
                {
                    string key = ViarioStore.GeometryKey(line);
                    if (released.Contains(key))
                        continue;
                    if (!wanted.TryGetValue(key, out List<OutputLine>? list))
                        wanted[key] = list = new List<OutputLine>();
                    list.Add(line);
                }

                // Linhas existentes: mantem as iguais, apaga as demais.
                foreach (AcadDb.ObjectId id in state.Generated.ToList())
                {
                    if (id.IsNull || id.IsErased)
                    {
                        state.Generated.Remove(id);
                        continue;
                    }

                    AcadDb.DBObject obj = tr.GetObject(id, AcadDb.OpenMode.ForRead, false);
                    if (obj is not AcadDb.Polyline pl || ViarioStore.ReadTag(pl, out string levelText) != ViarioStore.TagGenerated)
                    {
                        state.Generated.Remove(id);
                        continue;
                    }

                    EdgeLevel level = ViarioStore.ParseLevel(levelText);
                    string key = ViarioStore.GeometryKey(pl, level);
                    bool rightLayer = string.Equals(pl.Layer, ViarioStore.LayerFor(level), StringComparison.OrdinalIgnoreCase);
                    if (rightLayer && wanted.TryGetValue(key, out List<OutputLine>? list) && list.Count > 0)
                    {
                        list.RemoveAt(list.Count - 1);
                        continue;
                    }

                    pl.UpgradeOpen();
                    pl.Erase();
                    state.Generated.Remove(id);
                    erased++;
                }

                List<OutputLine> toCreate = wanted.Values.SelectMany(l => l).ToList();
                if (toCreate.Count > 0)
                {
                    ViarioStore.EnsureRegApp(tr, db);
                    ViarioStore.EnsureLayers(tr, db);

                    AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
                    AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

                    foreach (OutputLine line in toCreate)
                    {
                        AcadDb.Polyline pl = ViarioStore.ToPolyline(line.Segs, line.Closed);
                        pl.SetDatabaseDefaults(db);
                        pl.Layer = ViarioStore.LayerFor(line.Level);
                        AcadDb.ObjectId id = ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);
                        ViarioStore.SetTag(pl, ViarioStore.TagGenerated, line.Level.ToString());
                        state.Generated.Add(id);
                        created++;
                    }
                }

                tr.Commit();
            }

            state.Pending = false;

            if (!quiet)
            {
                doc.Editor.WriteMessage(
                    $"\n  Rede viaria: {result.Nodes.Count} no(s), {result.Lines.Count} linha(s) | criadas {created}, removidas {erased}.");
                WriteWarnings(doc, result);
            }

            return result;
        }
        finally
        {
            _syncing = false;
        }
    }

    public static void WriteWarnings(Document doc, ViarioResult result)
    {
        List<string> warnings = result.Warnings.Distinct().ToList();
        foreach (string warning in warnings.Take(8))
            doc.Editor.WriteMessage("\n  [AVISO] " + warning);
        if (warnings.Count > 8)
            doc.Editor.WriteMessage($"\n  ... mais {warnings.Count - 8} aviso(s).");
    }
}
