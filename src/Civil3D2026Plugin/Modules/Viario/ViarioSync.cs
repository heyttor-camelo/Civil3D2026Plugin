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
/// - Atualizacao automatica pode ser desligada por desenho (redes grandes): os eixos
///   mudam, a rede fica pendente e so e refeita no "Atualizar rede".
/// - A ultima rede calculada fica guardada enquanto nem os eixos nem os registros do
///   modulo mudarem: o painel e os comandos nao recalculam a mesma rede de novo.
/// </summary>
internal static class ViarioSync
{
    private sealed class DbState
    {
        public readonly HashSet<AcadDb.ObjectId> Axes = new();
        public readonly HashSet<AcadDb.ObjectId> Generated = new();
        /// <summary>Ultima geometria concordante de cada eixo (para reconhecer a edicao do arco).</summary>
        public readonly Dictionary<AcadDb.ObjectId, (List<V2> Pts, List<double> Bulges)> ChainSnapshot = new();
        public bool Scanned;
        public bool Pending;

        /// <summary>Muda a cada alteracao de eixo (fora da propria regeneracao).</summary>
        public long Version;

        /// <summary>Ultima rede calculada e de que estado ela veio.</summary>
        public Snapshot? Last;

        /// <summary>Atualizacao automatica (null = ainda nao lida do desenho).</summary>
        public bool? Auto;

        /// <summary>Ja avisou que a rede e pesada para a atualizacao automatica.</summary>
        public bool SlowHinted;

        /// <summary>Versao que o painel ja recebeu (atualizacao manual: so avisa quando muda).</summary>
        public long NotifiedVersion = -1;
    }

    private sealed class Snapshot
    {
        public long Version;
        public string Signature = "";
        public List<AxisInput> Axes = new();
        public ViarioResult Result = new();
    }

    /// <summary>
    /// Rede para o painel: eixos e a rede calculada. Stale = ha mudancas nos eixos ainda nao
    /// aplicadas ao desenho; Missing = a rede ainda nao foi calculada nesta sessao (atualizacao manual).
    /// </summary>
    public readonly record struct NetworkView(List<AxisInput> Axes, ViarioResult Result, bool Stale, bool Missing);

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
        ViarioGripPreview.Register();
    }

    public static void Terminate()
    {
        if (!_initialized)
            return;

        _initialized = false;
        ViarioGripPreview.Unregister();
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
        db.ObjectOpenedForModify += OnObjectOpenedForModify;
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
            db.ObjectOpenedForModify -= OnObjectOpenedForModify;
            db.ObjectErased -= OnObjectErased;
            doc.CommandEnded -= OnCommandEnded;
            States.Remove(db);
            ViarioCivil.Forget(db);
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

    /// <summary>
    /// Guarda a geometria do eixo concordante antes da primeira edicao (quando ainda nao
    /// ha retrato da ultima atualizacao): e o que permite reconhecer "so o arco mudou".
    /// </summary>
    private static void OnObjectOpenedForModify(object? sender, AcadDb.ObjectEventArgs e)
    {
        try
        {
            if (_syncing || e.DBObject is not AcadDb.Polyline pl || pl.Closed || pl.NumberOfVertices < 3)
                return;
            DbState state = State(pl.Database);
            if (state.ChainSnapshot.ContainsKey(pl.ObjectId) || !ViarioStore.IsChained(pl))
                return;
            (List<V2> pts, List<double> bulges) = ViarioStore.Vertices(pl);
            state.ChainSnapshot[pl.ObjectId] = (pts, bulges);
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
        if (_syncing)
            return;
        state.Pending = true;
        state.Version++;
    }

    // ------------------------------------------------------------------
    // Atualizacao automatica
    // ------------------------------------------------------------------

    /// <summary>Atualizacao automatica ligada neste desenho (padrao: sim).</summary>
    public static bool IsAuto(AcadDb.Database db)
    {
        DbState state = State(db);
        state.Auto ??= ViarioStore.LoadAutoUpdate(db);
        return state.Auto.Value;
    }

    /// <summary>
    /// Liga/desliga a atualizacao automatica do desenho (fica salvo no DWG). Ao religar
    /// com mudancas pendentes, a rede e refeita na hora. O documento precisa estar travado.
    /// </summary>
    public static void SetAuto(Document doc, bool on)
    {
        AcadDb.Database db = doc.Database;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.SaveAutoUpdate(tr, db, on);
            tr.Commit();
        }

        DbState state = State(db);
        state.Auto = on;
        if (on)
        {
            doc.Editor.WriteMessage("\n  Atualizacao automatica da rede ligada.");
            if (state.Pending)
                Regenerate(doc, quiet: false, force: true);
        }
        else
        {
            doc.Editor.WriteMessage("\n  Atualizacao automatica da rede desligada: mexa nos eixos a vontade e use \"Atualizar rede\" (C3DVIARIO > ATUalizar) quando quiser ver o resultado.");
        }

        ViarioPanel.NotifyChanged(doc);
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
                // O desfazer restaurou eixos e linhas juntos (com a atualizacao manual,
                // a rede pode continuar pendente). A opcao salva pode ter sido desfeita.
                state.Auto = null;
                if (IsAuto(doc.Database))
                    state.Pending = false;
                ViarioPanel.NotifyChanged(doc);
                return;
            }

            if (!IsAuto(doc.Database))
            {
                // Sem refazer a rede; so a concordancia dos eixos que a mantem (barato).
                using (doc.LockDocument())
                    Rechain(doc, state);
                if (state.NotifiedVersion != state.Version)
                {
                    state.NotifiedVersion = state.Version;
                    ViarioPanel.NotifyChanged(doc);
                }

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
            if (!state.Pending || !IsAuto(doc.Database))
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

            if (!ViarioStore.ReadAxis(curve, out string sectionId, out double? radius, out List<AxisEvent> events, out bool chained))
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
            axes.Add(new AxisInput(curve.Handle.ToString() ?? string.Empty, path, section, radius, events, chained));
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
            EndCapture = settings.Capture,
            CornerRadius = ViarioStore.LoadCornerRadii(db),
            SlipLanes = ViarioStore.LoadSlipLanes(db),
            RemovedIslands = ViarioStore.LoadRemovedIslands(db),
            IslandRadii = ViarioStore.LoadIslandRadii(db)
        };
    }

    /// <summary>
    /// Calcula a rede atual sem alterar o desenho (reaproveita a ultima rede calculada
    /// se nada mudou desde entao).
    /// </summary>
    public static (List<AxisInput> Axes, ViarioResult Result, HashSet<string> ExistingKeys) ComputeCurrent(AcadDb.Database db)
    {
        DbState state = State(db);
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        if (Valid(db, state) is Snapshot last)
            return (new List<AxisInput>(last.Axes), last.Result, ExistingKeys(db, tr));

        List<AxisInput> axes = CollectAxes(db, tr, SectionMap(db));
        ViarioResult result = ViarioEngine.Compute(axes, EngineSettingsFor(db));
        Remember(db, state, axes, result);
        return (new List<AxisInput>(axes), result, ExistingKeys(db, tr));
    }

    /// <summary>Eixos atuais e chaves das linhas existentes, sem calcular a rede.</summary>
    public static (List<AxisInput> Axes, HashSet<string> ExistingKeys) CurrentAxes(AcadDb.Database db)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        return (CollectAxes(db, tr, SectionMap(db)), ExistingKeys(db, tr));
    }

    /// <summary>
    /// Rede para o painel. Com a atualizacao automatica, a atual (calculada se preciso);
    /// desligada, os eixos atuais com a ultima rede calculada, sem recalcular.
    /// </summary>
    public static NetworkView View(AcadDb.Database db)
    {
        DbState state = State(db);
        bool auto = IsAuto(db);
        if (Valid(db, state) is Snapshot last)
            return new NetworkView(new List<AxisInput>(last.Axes), last.Result, !auto && state.Pending, false);

        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        List<AxisInput> axes = CollectAxes(db, tr, SectionMap(db));
        if (!auto)
            return new NetworkView(axes, state.Last?.Result ?? new ViarioResult(), state.Pending, state.Last is null);

        ViarioResult result = ViarioEngine.Compute(axes, EngineSettingsFor(db));
        Remember(db, state, axes, result);
        return new NetworkView(new List<AxisInput>(axes), result, false, false);
    }

    /// <summary>Esquece a rede guardada (o proximo calculo e completo).</summary>
    public static void Forget(AcadDb.Database db) => State(db).Last = null;

    private static Snapshot? Valid(AcadDb.Database db, DbState state) =>
        state.Last is Snapshot last && last.Version == state.Version &&
        string.Equals(last.Signature, ViarioStore.RecordsSignature(db), StringComparison.Ordinal)
            ? last
            : null;

    private static void Remember(AcadDb.Database db, DbState state, List<AxisInput> axes, ViarioResult result) =>
        state.Last = new Snapshot { Version = state.Version, Signature = ViarioStore.RecordsSignature(db), Axes = axes, Result = result };

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

    /// <summary>
    /// Eixos com concordancia mantida: depois de um grip, STRETCH ou MOVE de vertice,
    /// refaz a cadeia (retas na direcao corrente, arcos tangentes) a partir dos
    /// vertices atuais. Devolve quantos eixos foram reescritos.
    /// </summary>
    private static int EnforceChains(AcadDb.Transaction tr, DbState state)
    {
        int count = 0;
        foreach (AcadDb.ObjectId id in state.Axes.ToList())
        {
            if (id.IsNull || id.IsErased)
                continue;

            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Polyline pl ||
                pl.Closed || pl.NumberOfVertices < 3 || !ViarioStore.IsChained(pl))
            {
                continue;
            }

            (List<V2> pts, List<double> bulges) = ViarioStore.Vertices(pl);
            state.ChainSnapshot.TryGetValue(id, out (List<V2> Pts, List<double> Bulges) prev);
            (List<V2> np, List<double> nb, bool changed) = ViarioChain.Solve(pts, bulges, prev.Pts, prev.Bulges);
            state.ChainSnapshot[id] = (np, nb);
            if (!changed)
                continue;

            pl.UpgradeOpen();
            for (int i = 0; i < np.Count; i++)
            {
                pl.SetPointAt(i, new Autodesk.AutoCAD.Geometry.Point2d(np[i].X, np[i].Y));
                pl.SetBulgeAt(i, i < nb.Count ? nb[i] : 0.0);
            }

            count++;
        }

        return count;
    }

    /// <summary>Refaz so a concordancia dos eixos editados (atualizacao manual).</summary>
    private static void Rechain(Document doc, DbState state)
    {
        if (_syncing)
            return;

        _syncing = true;
        try
        {
            AcadDb.Database db = doc.Database;
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            EnsureScanned(db, tr, state);
            int count = EnforceChains(tr, state);
            tr.Commit();
            if (count > 0)
                state.Version++;
        }
        finally
        {
            _syncing = false;
        }
    }

    public static Dictionary<string, RoadSection> SectionMap(AcadDb.Database db) =>
        ViarioStore.LoadSections(db).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>
    /// Refaz a rede inteira e aplica so as diferencas. O documento precisa estar
    /// travado (comando em andamento ou LockDocument). Com a atualizacao automatica
    /// desligada, so marca a rede como pendente (salvo com force: o "Atualizar rede").
    /// </summary>
    public static ViarioResult? Regenerate(Document doc, bool quiet, bool force = false)
    {
        if (_syncing)
            return null;

        AcadDb.Database db = doc.Database;
        DbState state = State(db);
        if (!force && !IsAuto(db))
        {
            // Avisa uma vez, quando a rede passa a ficar pendente.
            if (!state.Pending || !quiet)
                doc.Editor.WriteMessage("\n  Atualizacao automatica desligada: a rede fica pendente ate \"Atualizar rede\" (C3DVIARIO > ATUalizar).");
            state.Pending = true;
            ViarioPanel.NotifyChanged(doc);
            return null;
        }

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        _syncing = true;

        try
        {
            Dictionary<string, RoadSection> sections = SectionMap(db);
            HashSet<string> released = ViarioStore.LoadReleased(db);
            ViarioResult result;
            List<AxisInput> axes;
            int created = 0, erased = 0;

            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureScanned(db, tr, state);
                int rechained = EnforceChains(tr, state);
                if (rechained > 0 && !quiet)
                    doc.Editor.WriteMessage($"\n  Concordancia refeita em {rechained} eixo(s).");

                if (rechained == 0 && Valid(db, state) is Snapshot last)
                {
                    axes = new List<AxisInput>(last.Axes);
                    result = last.Result;
                }
                else
                {
                    axes = CollectAxes(db, tr, sections);
                    result = ViarioEngine.Compute(axes, EngineSettingsFor(db));
                    Remember(db, state, axes, result);
                    axes = new List<AxisInput>(axes);
                }

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
            ViarioGripPreview.Invalidate();

            // Objetos do Civil acompanham a rede quando ja foram gerados.
            ViarioCivil.Report? civil = null;
            if (ViarioCivil.IsActive(db))
            {
                try
                {
                    civil = ViarioCivil.Sync(doc, axes, result, create: false);
                }
                catch (System.Exception ex)
                {
                    doc.Editor.WriteMessage("\n  [AVISO] Objetos do Civil nao atualizados: " + ex.Message);
                }
            }

            // Sinalizacao previa acompanha a rede quando o bloco existe.
            try
            {
                ViarioSignageSync.Sync(doc, axes, result, create: false);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\n  [AVISO] Sinalizacao nao atualizada: " + ex.Message);
            }

            // Hachura do pavimento acompanha a rede enquanto existir.
            try
            {
                ViarioHatchSync.Sync(doc, axes, result, create: false);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\n  [AVISO] Hachura nao atualizada: " + ex.Message);
            }

            // O que as saidas gravaram nos registros do modulo nao muda a rede.
            if (state.Last is Snapshot done && ReferenceEquals(done.Result, result))
                done.Signature = ViarioStore.RecordsSignature(db);

            ViarioPanel.NotifyChanged(doc);

            if (civil is not null)
            {
                foreach (string m in civil.Messages.Distinct().Take(6))
                    doc.Editor.WriteMessage("\n  [CIVIL] " + m);
            }

            if (!quiet)
            {
                doc.Editor.WriteMessage(
                    $"\n  Rede viaria: {result.Nodes.Count} no(s), {result.Lines.Count} linha(s) | criadas {created}, removidas {erased}.");
                WriteWarnings(doc, result);
            }

            // Rede pesada para refazer a cada edicao: sugere a atualizacao manual uma vez.
            if (!force && !state.SlowHinted && clock.Elapsed.TotalSeconds > 4.0)
            {
                state.SlowHinted = true;
                doc.Editor.WriteMessage(
                    $"\n  A rede levou {clock.Elapsed.TotalSeconds:0} s para ser refeita. Em redes grandes, desligue a atualizacao automatica " +
                    "(botao Auto no explorador ou C3DVIARIO > AUtomatico) e use \"Atualizar rede\" quando quiser.");
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
