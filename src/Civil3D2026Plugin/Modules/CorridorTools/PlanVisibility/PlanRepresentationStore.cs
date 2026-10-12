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
/// - mantida em cache por desenho; o desenho em tela le SOMENTE o cache (nunca
///   abre o DWG durante a regeneracao). O cache e carregado do DWG no Idle;
/// - qualquer solido-alvo criado/alterado/apagado fica "pendente": enquanto
///   pendente, e desenhado normalmente em planta (sinal visual de que precisa
///   atualizar). Com a atualizacao automatica ligada, ao ficar ocioso o plugin
///   recalcula os pendentes e os vizinhos afetados e redesenha. Atualizacoes
///   automaticas grandes sao adiadas (C3DPLANTAATUALIZAR); qualquer calculo
///   pode ser cancelado com ESC.
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

        /// <summary>True depois que as representacoes gravadas no DWG foram lidas para o cache.</summary>
        public bool Loaded;

        /// <summary>Arestas de cada solido (coordenadas do proprio objeto), para o 2D Wireframe fora da planta.</summary>
        public readonly Dictionary<long, List<Point3d[]>?> Wire = new();

        /// <summary>Solidos cujas arestas o desenho pediu e ainda nao estao no cache.</summary>
        public readonly HashSet<long> WireRequests = new();
    }

    /// <summary>Maximo de solidos com arestas montadas por Idle (mantem a interface responsiva).</summary>
    private const int WireBatch = 400;

    /// <summary>Acima disso (solidos envolvidos), a atualizacao automatica e adiada.</summary>
    private const int AutoRefreshLimit = 300;

    private static readonly Dictionary<AcadDb.Database, DbState> States = new();
    private static readonly HashSet<Document> AttachedDocuments = new();

    private static bool _initialized;
    private static bool _refreshScheduled;
    private static bool _loadScheduled;
    private static bool _wireScheduled;
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

        PlanLayerIndex.Rebuild(db);
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

            PlanLayerIndex.Remove(db);
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

        // So objetos do model space; edicoes dentro de definicoes de bloco
        // pedem C3DPLANTAATUALIZAR.
        if (obj is not AcadDb.Entity entity || !IsInModelSpace(entity))
            return false;

        // Inserções de bloco: se nao tiverem solidos, a atualizacao simplesmente as ignora.
        return (entity is AcadDb.Solid3d || entity is AcadDb.Body || entity is AcadDb.BlockReference) &&
               (PlanVisibilitySettings.Matches(entity.Layer) || entity is AcadDb.BlockReference && PlanVisibilitySettings.MatchesAll);
    }

    private static bool IsInModelSpace(AcadDb.Entity entity)
    {
        try
        {
            return entity.BlockName.Equals(AcadDb.BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void OnObjectChanged(object? sender, AcadDb.ObjectEventArgs e)
    {
        try
        {
            // Layers criados/renomeados: mantem o indice usado no desenho.
            if (e.DBObject is AcadDb.LayerTableRecord layer)
            {
                PlanLayerIndex.Update(layer, erased: false);
                return;
            }

            // Arestas em cache ficam invalidas quando o solido muda (inclusive dentro de blocos).
            if (e.DBObject is AcadDb.Solid3d || e.DBObject is AcadDb.Body)
                InvalidateWire(e.DBObject.Database, e.DBObject.Handle.Value);

            // Insercao em layer-alvo: os solidos do bloco passam a ser alvos.
            if (e.DBObject is AcadDb.BlockReference insert &&
                PlanVisibilitySettings.Matches(insert.Layer))
            {
                PlanLayerIndex.AddMatchedBlock(insert.Database, insert.BlockTableRecord);
            }

            if (IsTrackedTarget(e.DBObject))
            {
                MarkPending(e.DBObject.Database, e.DBObject.Handle.Value);
                return;
            }

            // Deixou de ser alvo (ex.: layer trocado para fora dos prefixos):
            // a representacao antiga precisa sair.
            if (!_refreshing &&
                PlanVisibilitySettings.Mode == PlanDisplayMode.Exato &&
                e.DBObject is AcadDb.Entity &&
                HasRepresentation(e.DBObject.Database, e.DBObject.Handle.Value))
            {
                MarkPending(e.DBObject.Database, e.DBObject.Handle.Value);
            }
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
            if (e.DBObject is AcadDb.LayerTableRecord layer)
            {
                PlanLayerIndex.Update(layer, e.Erased);
                return;
            }

            if (e.DBObject is AcadDb.Solid3d || e.DBObject is AcadDb.Body)
                InvalidateWire(e.DBObject.Database, e.DBObject.Handle.Value);

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

    private static bool HasRepresentation(AcadDb.Database db, long key)
    {
        DbState state = GetState(db);
        lock (state.Sync)
            return state.Cache.TryGetValue(key, out List<Point3d[]>? lines) && lines is not null;
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
        // Chamado durante a regeneracao: le apenas memoria, nunca o DWG.
        AcadDb.Database? db = entity.Database;
        if (db is null || entity.ObjectId.IsNull)
            return null;

        long key = entity.Handle.Value;
        DbState state = GetState(db);

        lock (state.Sync)
        {
            if (!state.Loaded)
            {
                _loadScheduled = true;
                return null;
            }

            if (state.Pending.Contains(key))
                return null;

            return state.Cache.TryGetValue(key, out List<Point3d[]>? cached) ? cached : null;
        }
    }

    /// <summary>
    /// Arestas do solido (coordenadas do proprio objeto) para o 2D Wireframe fora
    /// da planta. Chamado durante a regeneracao: le apenas memoria; se ainda nao
    /// houver, agenda a montagem no Idle e retorna null.
    /// </summary>
    public static List<Point3d[]>? TryGetWire(AcadDb.Entity entity)
    {
        AcadDb.Database? db = entity.Database;
        if (db is null || entity.ObjectId.IsNull)
            return null;

        long key = entity.Handle.Value;
        DbState state = GetState(db);

        lock (state.Sync)
        {
            if (state.Wire.TryGetValue(key, out List<Point3d[]>? wire))
                return wire;

            state.WireRequests.Add(key);
        }

        _wireScheduled = true;
        return null;
    }

    private static void InvalidateWire(AcadDb.Database db, long key)
    {
        DbState state = GetState(db);
        lock (state.Sync)
            state.Wire.Remove(key);
    }

    /// <summary>Monta as arestas pedidas pelo desenho (thread principal, no Idle).</summary>
    private static int BuildRequestedWires(AcadDb.Database db)
    {
        DbState state = GetState(db);
        List<long> keys;

        lock (state.Sync)
        {
            if (state.WireRequests.Count == 0)
                return 0;

            keys = state.WireRequests.Take(WireBatch).ToList();
            foreach (long key in keys)
                state.WireRequests.Remove(key);

            if (state.WireRequests.Count > 0)
                _wireScheduled = true;
        }

        Dictionary<long, List<Point3d[]>?> built = new();

        using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            foreach (long key in keys)
            {
                List<Point3d[]>? edges = null;

                try
                {
                    AcadDb.ObjectId id = db.GetObjectId(false, new AcadDb.Handle(key), 0);
                    if (!id.IsNull && !id.IsErased &&
                        tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.Entity entity)
                    {
                        edges = PlanHiddenLineEngine.ExtractEdges(entity);
                    }
                }
                catch
                {
                    // Sem arestas legiveis: o solido nao aparece em 2D Wireframe fora da planta.
                }

                built[key] = edges ?? new List<Point3d[]>();
            }
        }

        lock (state.Sync)
        {
            foreach ((long key, List<Point3d[]>? edges) in built)
                state.Wire[key] = edges;
        }

        return built.Count;
    }

    /// <summary>Le todas as representacoes gravadas no DWG para o cache (fora da regeneracao).</summary>
    private static void LoadAll(AcadDb.Database db)
    {
        Dictionary<long, List<Point3d[]>?> loaded = new();

        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();

            AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
            if (nod.Contains(RootDictionaryName))
            {
                AcadDb.DBDictionary root = (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(RootDictionaryName), AcadDb.OpenMode.ForRead);

                foreach (AcadDb.DBDictionaryEntry entry in root)
                {
                    if (!entry.Key.StartsWith("H", StringComparison.Ordinal) ||
                        !long.TryParse(entry.Key.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out long key))
                    {
                        continue;
                    }

                    if (tr.GetObject(entry.Value, AcadDb.OpenMode.ForRead, false) is AcadDb.Xrecord record)
                        loaded[key] = Parse(record.Data?.AsArray());
                }
            }
        }
        catch
        {
            // Sem representacoes legiveis: tudo e desenhado normalmente.
        }

        DbState state = GetState(db);
        lock (state.Sync)
        {
            foreach ((long key, List<Point3d[]>? lines) in loaded)
            {
                if (!state.Pending.Contains(key))
                    state.Cache[key] = lines;
            }

            state.Loaded = true;
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
    public static int Refresh(Document doc, bool full, out string summary) =>
        Refresh(doc, full, automatic: false, out summary);

    /// <summary>
    /// automatic = chamado pela atualizacao automatica: se houver solidos
    /// demais envolvidos, nada e calculado e retorna -2 (pendentes mantidos).
    /// Lanca OperationCanceledException se o usuario apertar ESC (nada e gravado).
    /// </summary>
    public static int Refresh(Document doc, bool full, bool automatic, out string summary)
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

            // Regioes afetadas: caixa de CADA pendente (posicao nova) e de onde
            // as linhas antigas estavam. Caixas separadas, para que alteracoes
            // espalhadas nao virem uma regiao do tamanho do desenho.
            List<PlanHiddenLineEngine.Extents2dBox> regions = new();
            if (!full)
            {
                regions.AddRange(targets.Where(t => pending.Contains(t.Key)).Select(t => t.Box));
                regions.AddRange(oldBoxes);
            }

            const double margin = 1e-6;

            List<Target> subjects = full
                ? targets
                : targets.Where(t => pending.Contains(t.Key) || regions.Any(r => t.Box.Overlaps(r, margin))).ToList();

            // Ocultadores: tudo que pode ficar por cima de algum sujeito.
            List<Target> occluders = full
                ? targets
                : targets.Where(t => subjects.Any(sj => t.Box.Overlaps(sj.Box, margin))).ToList();

            if (automatic && occluders.Count > AutoRefreshLimit)
            {
                summary =
                    occluders.Count + " solido(s) envolvidos - atualizacao automatica adiada." +
                    " Use " + C3DCommands.Corridor.PlantaAtualizar + " (ESC cancela).";
                return -2;
            }

            // Pendentes que deixaram de ser alvo (layer trocado, apagados):
            // a representacao gravada sai.
            HashSet<long> targetKeys = new(targets.Select(t => t.Key));
            foreach (long key in pending)
            {
                if (!targetKeys.Contains(key))
                    erased.Add(key);
            }

            Func<bool> cancel = () =>
            {
                try
                {
                    return AcadDb.HostApplicationServices.Current.UserBreak();
                }
                catch
                {
                    return false;
                }
            };

            using Autodesk.AutoCAD.Runtime.ProgressMeter meter = new();
            meter.Start("Planta: lendo solidos (ESC cancela)");
            meter.SetLimit(occluders.Count + subjects.Count);

            Dictionary<long, PlanHiddenLineEngine.EntityGeometry> geometries = new();
            int failed = 0;

            foreach (Target t in occluders)
            {
                if (cancel())
                    throw new OperationCanceledException();

                meter.MeterProgress();

                if (tr.GetObject(t.Id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Entity entity)
                    continue;

                PlanHiddenLineEngine.EntityGeometry? geometry = ExtractTarget(tr, entity, t.Key);
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
                geometries.Values.ToList(),
                cancel,
                () => meter.MeterProgress());

            meter.Stop();

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

                    if (!computed.ContainsKey(key) && !erased.Contains(key))
                        state.Cache[key] = null;
                }

                state.Loaded = true;

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

    /// <summary>Geometria de um alvo: solido/body direto ou todos os solidos de uma insercao de bloco.</summary>
    private static PlanHiddenLineEngine.EntityGeometry? ExtractTarget(AcadDb.Transaction tr, AcadDb.Entity entity, long key)
    {
        if (entity is not AcadDb.BlockReference reference)
            return PlanHiddenLineEngine.Extract(entity, key);

        List<PlanHiddenLineEngine.EntityGeometry> parts = new();
        bool referenceMatches = PlanVisibilitySettings.Matches(reference.Layer);

        foreach ((AcadDb.Entity nested, Matrix3d transform) in EnumerateNestedSolids(tr, reference, reference.BlockTransform, referenceMatches, 0))
        {
            PlanHiddenLineEngine.EntityGeometry? part = PlanHiddenLineEngine.Extract(nested, key, transform);
            if (part is not null)
                parts.Add(part);
        }

        return parts.Count == 0 ? null : PlanHiddenLineEngine.Merge(key, parts);
    }

    /// <summary>
    /// Solidos/bodies dentro de um bloco (recursivo), com a transformacao
    /// acumulada para o WCS. Entram todos se a insercao casar com o filtro;
    /// senao, so os que casam pelo proprio layer.
    /// </summary>
    private static IEnumerable<(AcadDb.Entity Entity, Matrix3d Transform)> EnumerateNestedSolids(
        AcadDb.Transaction tr,
        AcadDb.BlockReference reference,
        Matrix3d transform,
        bool includeAll,
        int depth)
    {
        if (depth > 16)
            yield break;

        if (tr.GetObject(reference.BlockTableRecord, AcadDb.OpenMode.ForRead, false) is not AcadDb.BlockTableRecord definition)
            yield break;

        foreach (AcadDb.ObjectId id in definition)
        {
            if (id.IsErased || tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Entity child)
                continue;

            if (!child.Visible)
                continue;

            if (child is AcadDb.Solid3d || child is AcadDb.Body)
            {
                if (includeAll || PlanVisibilitySettings.Matches(child.Layer))
                    yield return (child, transform);
            }
            else if (child is AcadDb.BlockReference inner)
            {
                bool innerAll = includeAll || PlanVisibilitySettings.Matches(inner.Layer);
                foreach (var item in EnumerateNestedSolids(tr, inner, transform * inner.BlockTransform, innerAll, depth + 1))
                    yield return item;
            }
        }
    }

    /// <summary>True se o bloco (recursivo) contem algum Solid3d/Body.</summary>
    private static bool ContainsSolids(
        AcadDb.Transaction tr,
        AcadDb.ObjectId blockId,
        Dictionary<AcadDb.ObjectId, bool> memo,
        int depth = 0)
    {
        if (memo.TryGetValue(blockId, out bool known))
            return known;

        memo[blockId] = false; // protege contra referencia circular
        bool found = false;

        if (depth <= 16 && tr.GetObject(blockId, AcadDb.OpenMode.ForRead, false) is AcadDb.BlockTableRecord definition)
        {
            foreach (AcadDb.ObjectId id in definition)
            {
                if (id.IsErased)
                    continue;

                if (id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AcadDb.Solid3d))) ||
                    id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AcadDb.Body))))
                {
                    found = true;
                    break;
                }

                if (id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AcadDb.BlockReference))) &&
                    tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.BlockReference inner &&
                    ContainsSolids(tr, inner.BlockTableRecord, memo, depth + 1))
                {
                    found = true;
                    break;
                }
            }
        }

        memo[blockId] = found;
        return found;
    }

    private static List<Target> CollectTargets(AcadDb.Transaction tr, AcadDb.Database db)
    {
        List<Target> targets = new();
        Dictionary<AcadDb.ObjectId, bool> blocksWithSolids = new();

        AcadDb.BlockTable table = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord modelSpace = (AcadDb.BlockTableRecord)tr.GetObject(
            table[AcadDb.BlockTableRecord.ModelSpace],
            AcadDb.OpenMode.ForRead);

        foreach (AcadDb.ObjectId id in modelSpace)
        {
            if (id.IsErased)
                continue;

            bool isSolid =
                id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AcadDb.Solid3d))) ||
                id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AcadDb.Body)));
            bool isBlock =
                id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AcadDb.BlockReference)));

            if (!isSolid && !isBlock)
                continue;

            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Entity entity)
                continue;

            if (isSolid && !PlanVisibilitySettings.Matches(entity.Layer))
                continue;

            if (entity is AcadDb.BlockReference reference)
            {
                // Bloco entra se contem solidos e (o proprio layer casa ou algum solido interno casa).
                if (!ContainsSolids(tr, reference.BlockTableRecord, blocksWithSolids))
                    continue;

                bool matches = PlanVisibilitySettings.Matches(reference.Layer) ||
                    EnumerateNestedSolids(tr, reference, Matrix3d.Identity, false, 0).Any();

                if (!matches)
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
                    state.Wire.Clear();
                    state.WireRequests.Clear();
                    state.Loaded = false;
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Atualizacao automatica
    // ------------------------------------------------------------------

    private static void OnIdle(object? sender, EventArgs e)
    {
        if (_refreshing || !_initialized)
            return;

        if (!PlanVisibilityManager.IsActive)
            return;

        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        // Nunca durante interacao: comando/orbita/zoom em andamento ou botao do mouse pressionado.
        try
        {
            if (!doc.Editor.IsQuiescent ||
                !string.IsNullOrEmpty(doc.CommandInProgress) ||
                Convert.ToInt32(AcApp.GetSystemVariable("CMDACTIVE")) != 0 ||
                System.Windows.Forms.Control.MouseButtons != System.Windows.Forms.MouseButtons.None)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        DbState state = GetState(doc.Database);
        bool needsLoad;
        lock (state.Sync)
            needsLoad = !state.Loaded;

        // 1) Carregar as representacoes gravadas (leitura rapida, fora da regeneracao).
        if (PlanVisibilitySettings.Mode == PlanDisplayMode.Exato && (needsLoad || _loadScheduled))
        {
            _loadScheduled = false;

            if (needsLoad)
            {
                try
                {
                    using (doc.LockDocument())
                    {
                        LoadAll(doc.Database);
                        doc.Editor.Regen();
                    }
                }
                catch
                {
                    // Tenta de novo no proximo Idle se ainda for necessario.
                }

                return;
            }
        }

        // 2) Arestas pedidas pelo desenho (2D Wireframe fora da planta).
        if (_wireScheduled)
        {
            _wireScheduled = false;

            try
            {
                int built;
                using (doc.LockDocument())
                {
                    built = BuildRequestedWires(doc.Database);
                    if (built > 0)
                        doc.Editor.Regen();
                }

                if (built > 0)
                    return;
            }
            catch
            {
                // Tenta de novo quando o desenho pedir.
            }
        }

        // 3) Atualizacao automatica dos pendentes.
        if (!_refreshScheduled ||
            !PlanVisibilitySettings.AutoRefresh ||
            PlanVisibilitySettings.Mode != PlanDisplayMode.Exato)
        {
            return;
        }

        _refreshScheduled = false;

        try
        {
            using (doc.LockDocument())
            {
                int count = Refresh(doc, full: false, automatic: true, out string summary);

                if (count > 0)
                {
                    doc.Editor.Regen();
                    doc.Editor.WriteMessage("\n[C3DOCULTARPLANTA] Planta atualizada: " + summary + "\n");
                }
                else if (count == -2)
                {
                    doc.Editor.WriteMessage("\n[C3DOCULTARPLANTA] " + summary + "\n");
                }
            }
        }
        catch (OperationCanceledException)
        {
            WriteSafe(doc, "\n[C3DOCULTARPLANTA] Atualizacao cancelada. Os solidos alterados aparecem completos em planta ate " +
                C3DCommands.Corridor.PlantaAtualizar + ".\n");
        }
        catch (System.Exception ex)
        {
            WriteSafe(doc, "\n[C3DOCULTARPLANTA] Falha na atualizacao automatica: " + ex.Message + "\n");
        }
    }

    private static void WriteSafe(Document doc, string message)
    {
        try
        {
            doc.Editor.WriteMessage(message);
        }
        catch
        {
            // Sem editor disponivel.
        }
    }
}
