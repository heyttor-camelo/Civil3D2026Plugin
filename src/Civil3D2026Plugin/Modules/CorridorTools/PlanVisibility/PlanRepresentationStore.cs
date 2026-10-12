using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.CorridorTools.PlanVisibility;

/// <summary>
/// Representacao de planta "Exato": linhas visiveis de cima de cada solido.
///
/// - gravada no DWG, no dicionario C3D_PLANREP_V1 do Named Objects Dictionary
///   (uma Xrecord por handle). Os solidos em si nao sao modificados;
/// - mantida em cache por desenho para o desenho em tela ser rapido;
/// - qualquer solido-alvo criado/alterado/apagado fica "pendente": enquanto
///   pendente, e desenhado normalmente em planta (sinal visual de que precisa
///   atualizar). Com a atualizacao automatica ligada, ao ficar ocioso o plugin
///   recalcula os pendentes e os vizinhos afetados e redesenha.
/// </summary>
internal static class PlanRepresentationStore
{
    private const string RootDictionaryName = "C3D_PLANREP_V1";
    private const string RecordSignature = "PLANREP1";

    private sealed class DbState
    {
        public readonly object Sync = new();
        public readonly Dictionary<long, List<Point3d[]>?> Cache = new();
        public readonly HashSet<long> Pending = new();
        public readonly Dictionary<long, PlanHiddenLineEngine.Extents2dBox> OldBoxes = new();
        public readonly HashSet<long> Erased = new();
    }

    private static readonly Dictionary<AcadDb.Database, DbState> States = new();
    private static readonly HashSet<Document> AttachedDocuments = new();

    private static bool _initialized;
    private static bool _refreshScheduled;
    private static bool _refreshing;

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
            DocumentCollection documents = AcApp.DocumentManager;
            documents.DocumentCreated -= OnDocumentCreated;
            documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        }
        catch
        {
            // Encerramento do AutoCAD.
        }

        foreach (Document doc in AttachedDocuments.ToList())
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
        if (!AttachedDocuments.Add(doc))
            return;

        AcadDb.Database db = doc.Database;
        db.ObjectAppended += OnObjectChanged;
        db.ObjectModified += OnObjectChanged;
        db.ObjectErased += OnObjectErased;
    }

    private static void Detach(Document doc)
    {
        if (!AttachedDocuments.Remove(doc))
            return;

        try
        {
            AcadDb.Database db = doc.Database;
            db.ObjectAppended -= OnObjectChanged;
            db.ObjectModified -= OnObjectChanged;
            db.ObjectErased -= OnObjectErased;

            lock (States)
                States.Remove(db);
        }
        catch
        {
            // Documento em fechamento.
        }
    }

    private static DbState GetState(AcadDb.Database db)
    {
        lock (States)
        {
            if (!States.TryGetValue(db, out DbState? state))
            {
                state = new DbState();
                States[db] = state;
            }

            return state;
        }
    }

    // ------------------------------------------------------------------
    // Rastreamento de alteracoes
    // ------------------------------------------------------------------

    private static bool IsTrackedTarget(AcadDb.DBObject obj)
    {
        if (_refreshing || PlanVisibilitySettings.Mode != PlanDisplayMode.Exato)
            return false;

        return obj is AcadDb.Entity entity &&
               (entity is AcadDb.Solid3d || entity is AcadDb.Body) &&
               PlanVisibilitySettings.Matches(entity.Layer);
    }

    private static void OnObjectChanged(object? sender, AcadDb.ObjectEventArgs e)
    {
        try
        {
            if (!IsTrackedTarget(e.DBObject))
                return;

            MarkPending(e.DBObject.Database, e.DBObject.Handle.Value);
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
            if (!IsTrackedTarget(e.DBObject))
                return;

            AcadDb.Database db = e.DBObject.Database;
            long key = e.DBObject.Handle.Value;
            DbState state = GetState(db);

            lock (state.Sync)
            {
                RememberOldBox(state, key);

                if (e.Erased)
                    state.Erased.Add(key);
                else
                    state.Erased.Remove(key); // UNDO do apagar

                state.Pending.Add(key);
                state.Cache.Remove(key);
            }

            _refreshScheduled = true;
        }
        catch
        {
            // Nunca interromper a edicao do usuario.
        }
    }

    private static void MarkPending(AcadDb.Database db, long key)
    {
        DbState state = GetState(db);

        lock (state.Sync)
        {
            RememberOldBox(state, key);
            state.Pending.Add(key);
            state.Cache.Remove(key);
        }

        _refreshScheduled = true;
    }

    /// <summary>Guarda onde as linhas antigas estavam, para recalcular os vizinhos de la tambem.</summary>
    private static void RememberOldBox(DbState state, long key)
    {
        if (state.OldBoxes.ContainsKey(key))
            return;

        if (state.Cache.TryGetValue(key, out List<Point3d[]>? lines) && lines is not null)
        {
            PlanHiddenLineEngine.Extents2dBox box = default;
            foreach (Point3d[] line in lines)
            {
                foreach (Point3d p in line)
                    box.Add(p);
            }

            if (box.IsValid)
                state.OldBoxes[key] = box;
        }
    }

    public static int PendingCount(AcadDb.Database db)
    {
        DbState state = GetState(db);
        lock (state.Sync)
            return state.Pending.Count;
    }

    // ------------------------------------------------------------------
    // Leitura para o desenho em tela
    // ------------------------------------------------------------------

    /// <summary>
    /// Linhas visiveis do solido em planta, ou null se nao houver representacao
    /// atualizada (o chamador desenha o solido normalmente).
    /// </summary>
    public static List<Point3d[]>? TryGet(AcadDb.Entity entity)
    {
        AcadDb.Database? db = entity.Database;
        if (db is null || entity.ObjectId.IsNull)
            return null;

        long key = entity.Handle.Value;
        DbState state = GetState(db);

        lock (state.Sync)
        {
            if (state.Pending.Contains(key))
                return null;

            if (state.Cache.TryGetValue(key, out List<Point3d[]>? cached))
                return cached;
        }

        List<Point3d[]>? loaded = LoadFromDrawing(db, key);

        lock (state.Sync)
        {
            if (!state.Pending.Contains(key))
                state.Cache[key] = loaded;
        }

        return loaded;
    }

    private static List<Point3d[]>? LoadFromDrawing(AcadDb.Database db, long key)
    {
        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();

            AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
            if (!nod.Contains(RootDictionaryName))
                return null;

            AcadDb.DBDictionary root = (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(RootDictionaryName), AcadDb.OpenMode.ForRead);
            string recordKey = RecordKey(key);
            if (!root.Contains(recordKey))
                return null;

            AcadDb.Xrecord record = (AcadDb.Xrecord)tr.GetObject(root.GetAt(recordKey), AcadDb.OpenMode.ForRead);
            return Parse(record.Data?.AsArray());
        }
        catch
        {
            return null;
        }
    }

    private static List<Point3d[]>? Parse(AcadDb.TypedValue[]? values)
    {
        if (values is null || values.Length < 2 || values[0].Value as string != RecordSignature)
            return null;

        List<Point3d[]> lines = new();
        int pos = 1;

        int lineCount = values[pos++].Value is int c ? c : 0;
        for (int i = 0; i < lineCount && pos < values.Length; i++)
        {
            int n = values[pos++].Value is int count ? count : 0;
            if (n < 2 || pos + n > values.Length)
                break;

            Point3d[] points = new Point3d[n];
            for (int k = 0; k < n; k++)
                points[k] = values[pos++].Value is Point3d p ? p : Point3d.Origin;

            lines.Add(points);
        }

        return lines;
    }

    private static string RecordKey(long key) => "H" + key.ToString("X");

    // ------------------------------------------------------------------
    // Calculo / atualizacao
    // ------------------------------------------------------------------

    private sealed class Target
    {
        public AcadDb.ObjectId Id { get; init; }
        public long Key { get; init; }
        public PlanHiddenLineEngine.Extents2dBox Box { get; init; }
    }

    /// <summary>
    /// Recalcula a representacao de planta. full = todos os alvos do model
    /// space; senao, apenas pendentes + vizinhos afetados. Retorna quantos
    /// solidos foram recalculados; -1 se nada precisava ser feito.
    /// </summary>
    public static int Refresh(Document doc, bool full, out string summary)
    {
        summary = string.Empty;
        AcadDb.Database db = doc.Database;
        DbState state = GetState(db);

        HashSet<long> pending;
        List<PlanHiddenLineEngine.Extents2dBox> oldBoxes;
        HashSet<long> erased;

        lock (state.Sync)
        {
            if (!full && state.Pending.Count == 0)
                return -1;

            pending = new HashSet<long>(state.Pending);
            oldBoxes = state.OldBoxes.Values.ToList();
            erased = new HashSet<long>(state.Erased);
        }

        Stopwatch watch = Stopwatch.StartNew();
        _refreshing = true;

        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();

            List<Target> targets = CollectTargets(tr, db);

            // Regiao afetada: novas posicoes dos pendentes + onde estavam antes.
            PlanHiddenLineEngine.Extents2dBox region = default;
            if (!full)
            {
                foreach (Target t in targets.Where(t => pending.Contains(t.Key)))
                    region.Add(t.Box);
                foreach (PlanHiddenLineEngine.Extents2dBox box in oldBoxes)
                    region.Add(box);
            }

            double margin = Math.Max(region.Diagonal * 1e-6, 1e-6);

            List<Target> subjects = full
                ? targets
                : targets.Where(t => pending.Contains(t.Key) || t.Box.Overlaps(region, margin)).ToList();

            // Ocultadores: tudo que pode ficar por cima dos sujeitos.
            PlanHiddenLineEngine.Extents2dBox subjectBox = default;
            foreach (Target t in subjects)
                subjectBox.Add(t.Box);

            List<Target> occluders = full
                ? targets
                : targets.Where(t => t.Box.Overlaps(subjectBox, margin)).ToList();

            Dictionary<long, PlanHiddenLineEngine.EntityGeometry> geometries = new();
            int failed = 0;

            foreach (Target t in occluders)
            {
                if (tr.GetObject(t.Id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Entity entity)
                    continue;

                PlanHiddenLineEngine.EntityGeometry? geometry = PlanHiddenLineEngine.Extract(entity, t.Key);
                if (geometry is null)
                {
                    failed++;
                    continue;
                }

                geometries[t.Key] = geometry;
            }

            List<PlanHiddenLineEngine.EntityGeometry> subjectGeometries = subjects
                .Where(t => geometries.ContainsKey(t.Key))
                .Select(t => geometries[t.Key])
                .ToList();

            Dictionary<long, List<Point3d[]>> computed = PlanHiddenLineEngine.Compute(
                subjectGeometries,
                geometries.Values.ToList());

            AcadDb.DBDictionary root = GetOrCreateRoot(tr, db);

            if (full)
            {
                // Remove representacoes de objetos que nao existem mais.
                HashSet<string> valid = new(targets.Select(t => RecordKey(t.Key)));
                List<string> stale = new();
                foreach (AcadDb.DBDictionaryEntry entry in root)
                {
                    if (!valid.Contains(entry.Key))
                        stale.Add(entry.Key);
                }

                foreach (string key in stale)
                    RemoveRecord(tr, root, key);
            }
            else
            {
                foreach (long key in erased)
                    RemoveRecord(tr, root, RecordKey(key));
            }

            foreach ((long key, List<Point3d[]> lines) in computed)
                WriteRecord(tr, root, RecordKey(key), lines);

            tr.Commit();

            lock (state.Sync)
            {
                foreach ((long key, List<Point3d[]> lines) in computed)
                    state.Cache[key] = lines;

                foreach (long key in erased)
                    state.Cache.Remove(key);

                // Pendentes sem geometria legivel saem da fila (sao desenhados normalmente).
                foreach (long key in pending)
                {
                    state.Pending.Remove(key);
                    state.OldBoxes.Remove(key);
                    state.Erased.Remove(key);

                    if (!computed.ContainsKey(key))
                        state.Cache[key] = null;
                }

                if (full)
                {
                    state.Pending.Clear();
                    state.OldBoxes.Clear();
                    state.Erased.Clear();
                }
            }

            watch.Stop();
            summary =
                computed.Count + " solido(s) recalculado(s) em " +
                (watch.ElapsedMilliseconds / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s" +
                (failed > 0 ? " | " + failed + " sem geometria legivel (desenhados normalmente)" : string.Empty);

            return computed.Count;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static List<Target> CollectTargets(AcadDb.Transaction tr, AcadDb.Database db)
    {
        List<Target> targets = new();

        AcadDb.BlockTable table = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord modelSpace = (AcadDb.BlockTableRecord)tr.GetObject(
            table[AcadDb.BlockTableRecord.ModelSpace],
            AcadDb.OpenMode.ForRead);

        foreach (AcadDb.ObjectId id in modelSpace)
        {
            if (id.IsErased)
                continue;

            if (!id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AcadDb.Solid3d))) &&
                !id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AcadDb.Body))))
            {
                continue;
            }

            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Entity entity ||
                !PlanVisibilitySettings.Matches(entity.Layer))
            {
                continue;
            }

            PlanHiddenLineEngine.Extents2dBox box = default;
            try
            {
                AcadDb.Extents3d extents = entity.GeometricExtents;
                box.Add(extents.MinPoint);
                box.Add(extents.MaxPoint);
            }
            catch
            {
                continue;
            }

            targets.Add(new Target { Id = id, Key = id.Handle.Value, Box = box });
        }

        return targets;
    }

    private static void WriteRecord(AcadDb.Transaction tr, AcadDb.DBDictionary root, string key, List<Point3d[]> lines)
    {
        List<AcadDb.TypedValue> values = new()
        {
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, RecordSignature),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, lines.Count)
        };

        foreach (Point3d[] line in lines)
        {
            values.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, line.Length));
            foreach (Point3d p in line)
                values.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.XCoordinate, p));
        }

        AcadDb.ResultBuffer buffer = new(values.ToArray());

        if (root.Contains(key))
        {
            AcadDb.Xrecord existing = (AcadDb.Xrecord)tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForWrite);
            existing.Data = buffer;
            return;
        }

        AcadDb.Xrecord record = new() { Data = buffer };
        root.SetAt(key, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    private static void RemoveRecord(AcadDb.Transaction tr, AcadDb.DBDictionary root, string key)
    {
        if (!root.Contains(key))
            return;

        AcadDb.DBObject record = tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForWrite);
        record.Erase();
    }

    private static AcadDb.DBDictionary GetOrCreateRoot(AcadDb.Transaction tr, AcadDb.Database db)
    {
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);

        if (nod.Contains(RootDictionaryName))
            return (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(RootDictionaryName), AcadDb.OpenMode.ForWrite);

        nod.UpgradeOpen();
        AcadDb.DBDictionary root = new();
        nod.SetAt(RootDictionaryName, root);
        tr.AddNewlyCreatedDBObject(root, true);
        return root;
    }

    /// <summary>Esvazia o cache em memoria (ex.: apos trocar prefixos ou modo).</summary>
    public static void ClearAllCaches()
    {
        lock (States)
        {
            foreach (DbState state in States.Values)
            {
                lock (state.Sync)
                {
                    state.Cache.Clear();
                    state.Pending.Clear();
                    state.OldBoxes.Clear();
                    state.Erased.Clear();
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Atualizacao automatica
    // ------------------------------------------------------------------

    private static void OnIdle(object? sender, EventArgs e)
    {
        if (!_refreshScheduled || _refreshing)
            return;

        if (!PlanVisibilitySettings.AutoRefresh ||
            PlanVisibilitySettings.Mode != PlanDisplayMode.Exato ||
            !PlanVisibilityManager.IsActive)
        {
            return;
        }

        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || !string.IsNullOrEmpty(doc.CommandInProgress))
            return;

        _refreshScheduled = false;

        try
        {
            int count;
            string summary;

            using (doc.LockDocument())
            {
                count = Refresh(doc, full: false, out summary);
            }

            if (count > 0)
            {
                doc.Editor.Regen();
                doc.Editor.WriteMessage("\n[C3DOCULTARPLANTA] Planta atualizada: " + summary + "\n");
            }
        }
        catch (System.Exception ex)
        {
            try
            {
                doc.Editor.WriteMessage("\n[C3DOCULTARPLANTA] Falha na atualizacao automatica: " + ex.Message + "\n");
            }
            catch
            {
                // Sem editor disponivel.
            }
        }
    }
}
