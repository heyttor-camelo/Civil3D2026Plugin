using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// C3DVIARIO: rede viaria urbana em planta (etapa 1).
/// - eixos com perfil de via (pista, calcada, raio de esquina);
/// - bordos (meio-fio) e alinhamentos gerados automaticamente;
/// - intersecoes detectadas sozinhas, com esquinas concordadas;
/// - preview ao lancar o eixo; edicao associativa (mexeu no eixo, refaz).
/// C3DVIARIOEIXO: atalho para lancar eixos direto.
/// </summary>
public sealed class ViarioCommands
{
    public const string Versao = C3DVersions.Viario;

    [CommandMethod(C3DCommands.Viario.Rede, CommandFlags.Modal)]
    public void Rede()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        ViarioSync.Initialize();

        try
        {
            ViarioSync.Suspended = true;
            WriteHeader(doc);

            PromptKeywordOptions options = new(
                "\nOpcao [Eixo/Adicionar/Perfil/Atribuir/Remover/Atualizar/Config] <Eixo>: ",
                "Eixo Adicionar Perfil Atribuir Remover Atualizar Config")
            {
                AllowNone = true
            };

            PromptResult result = ed.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
                return;

            string choice = result.Status == PromptStatus.OK ? result.StringResult : "Eixo";

            switch (choice)
            {
                case "Adicionar":
                    AddAxes(doc);
                    break;
                case "Perfil":
                    ManageProfiles(doc);
                    break;
                case "Atribuir":
                    AssignProfile(doc);
                    break;
                case "Remover":
                    RemoveAxes(doc);
                    break;
                case "Atualizar":
                    ViarioSync.Regenerate(doc, quiet: false);
                    break;
                case "Config":
                    Configure(doc);
                    break;
                default:
                    DrawAxes(doc);
                    break;
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO " + C3DCommands.Viario.Rede + "] " + ex.Message);
        }
        finally
        {
            ViarioSync.Suspended = false;
        }
    }

    [CommandMethod(C3DCommands.Viario.Eixo, CommandFlags.Modal)]
    public void Eixo()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        ViarioSync.Initialize();

        try
        {
            ViarioSync.Suspended = true;
            WriteHeader(doc);
            DrawAxes(doc);
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\n[ERRO " + C3DCommands.Viario.Eixo + "] " + ex.Message);
        }
        finally
        {
            ViarioSync.Suspended = false;
        }
    }

    private static void WriteHeader(Document doc)
    {
        ViarioStore.Settings settings = ViarioStore.LoadSettings(doc.Database);
        RoadProfile current = CurrentProfile(doc.Database, settings);
        doc.Editor.WriteMessage(
            "\n" + C3DCommands.Viario.Rede + " v" + Versao + " - rede viaria (bordos e intersecoes automaticos)." +
            "\n  Perfil corrente: " + Describe(current));
    }

    // ------------------------------------------------------------------
    // Eixo (com preview)
    // ------------------------------------------------------------------

    private static void DrawAxes(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        while (true)
        {
            ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
            RoadProfile profile = CurrentProfile(db, settings);

            PromptPointOptions first = new(
                "\nPrimeiro ponto do eixo [" + profile.Name + "] ou [Perfil] <sair>: ")
            {
                AllowNone = true
            };
            first.Keywords.Add("Perfil");
            first.AppendKeywordsToMessage = false;

            PromptPointResult start = ed.GetPoint(first);
            if (start.Status == PromptStatus.Keyword)
            {
                ChooseCurrentProfile(doc);
                continue;
            }

            if (start.Status != PromptStatus.OK)
                return;

            Point3d p0 = start.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            List<V2> points = new() { new V2(p0.X, p0.Y) };

            // Contexto do preview: rede atual sem o novo eixo.
            List<AxisInput> existing;
            HashSet<string> existingKeys;
            using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                existing = ViarioSync.CollectAxes(db, tr, ViarioSync.ProfileMap(db));
                existingKeys = ViarioSync.ExistingKeys(db, tr);
            }

            bool finished = false;
            while (!finished)
            {
                AxisJig jig = new(points, profile, existing, existingKeys, settings);
                PromptResult drag = ed.Drag(jig);

                if (jig.Finished || drag.Status == PromptStatus.None)
                {
                    finished = true;
                }
                else if (drag.Status == PromptStatus.Keyword || jig.Keyword is not null)
                {
                    if (points.Count > 1)
                        points.RemoveAt(points.Count - 1);
                }
                else if (drag.Status == PromptStatus.OK)
                {
                    if (V2.Dist(points[^1], jig.Current) > 1e-6)
                        points.Add(jig.Current);
                }
                else
                {
                    return; // ESC: descarta o eixo em andamento
                }
            }

            if (points.Count < 2)
                continue;

            List<V2> final = AxisJig.Capture(points, existing, settings.Capture);
            CreateAxis(doc, final, profile);
            ViarioResult? result = ViarioSync.Regenerate(doc, quiet: true);
            if (result is not null)
            {
                ed.WriteMessage($"\n  Eixo criado. Rede: {result.Nodes.Count} intersecao(oes), {result.Lines.Count} linha(s).");
                ViarioSync.WriteWarnings(doc, result);
            }
        }
    }

    private static void CreateAxis(Document doc, List<V2> points, RoadProfile profile)
    {
        AcadDb.Database db = doc.Database;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();

        ViarioStore.EnsureRegApp(tr, db);
        ViarioStore.EnsureLayers(tr, db);

        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

        AcadDb.Polyline pl = new();
        for (int i = 0; i < points.Count; i++)
            pl.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0, 0, 0);

        pl.SetDatabaseDefaults(db);
        pl.Layer = ViarioStore.LayerAxis;
        ms.AppendEntity(pl);
        tr.AddNewlyCreatedDBObject(pl, true);
        ViarioStore.SetTag(pl, ViarioStore.TagAxis, profile.Id);
        tr.Commit();
    }

    // ------------------------------------------------------------------
    // Adicionar / Atribuir / Remover
    // ------------------------------------------------------------------

    private static AcadDb.ObjectId[]? SelectCurves(Editor ed, string message)
    {
        AcadDb.TypedValue[] filter =
        {
            new((int)AcadDb.DxfCode.Start, "LWPOLYLINE,LINE,ARC")
        };

        PromptSelectionOptions options = new() { MessageForAdding = message };
        PromptSelectionResult result = ed.GetSelection(options, new SelectionFilter(filter));
        return result.Status == PromptStatus.OK ? result.Value.GetObjectIds() : null;
    }

    private static void AddAxes(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        RoadProfile profile = CurrentProfile(db, settings);

        AcadDb.ObjectId[]? ids = SelectCurves(ed,
            "\nSelecione os eixos existentes (polilinhas, linhas, arcos) para o perfil " + profile.Name + ": ");
        if (ids is null)
            return;

        int count = 0, skipped = 0;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.EnsureRegApp(tr, db);
            foreach (AcadDb.ObjectId id in ids)
            {
                AcadDb.Curve curve = (AcadDb.Curve)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                if (ViarioStore.IsGenerated(curve) || ViarioStore.ToPath(curve) is null)
                {
                    skipped++;
                    continue;
                }

                ViarioStore.SetTag(curve, ViarioStore.TagAxis, profile.Id);
                count++;
            }

            tr.Commit();
        }

        ed.WriteMessage($"\n  {count} eixo(s) adicionado(s) com o perfil {profile.Name}." +
                        (skipped > 0 ? $" Ignorados: {skipped} (linha gerada ou fora do plano XY)." : ""));
        ViarioSync.Regenerate(doc, quiet: false);
    }

    private static void AssignProfile(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        AcadDb.ObjectId[]? ids = SelectCurves(ed, "\nSelecione os eixos que vao mudar de perfil: ");
        if (ids is null)
            return;

        RoadProfile? profile = PickProfile(doc, "Perfil para os eixos selecionados");
        if (profile is null)
            return;

        int count = 0;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.EnsureRegApp(tr, db);
            foreach (AcadDb.ObjectId id in ids)
            {
                AcadDb.Curve curve = (AcadDb.Curve)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                if (!ViarioStore.IsAxis(curve, out _))
                    continue;

                ViarioStore.SetTag(curve, ViarioStore.TagAxis, profile.Id);
                count++;
            }

            tr.Commit();
        }

        ed.WriteMessage($"\n  {count} eixo(s) agora com o perfil {profile.Name}.");
        ViarioSync.Regenerate(doc, quiet: false);
    }

    private static void RemoveAxes(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        AcadDb.ObjectId[]? ids = SelectCurves(ed,
            "\nSelecione os eixos a tirar da rede (a polilinha do eixo continua no desenho): ");
        if (ids is null)
            return;

        int count = 0;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (AcadDb.ObjectId id in ids)
            {
                AcadDb.Curve curve = (AcadDb.Curve)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                if (!ViarioStore.IsAxis(curve, out _))
                    continue;

                ViarioStore.ClearTag(curve);
                count++;
            }

            tr.Commit();
        }

        ed.WriteMessage($"\n  {count} eixo(s) removido(s) da rede.");
        ViarioSync.Regenerate(doc, quiet: false);
    }

    // ------------------------------------------------------------------
    // Perfis
    // ------------------------------------------------------------------

    private static RoadProfile CurrentProfile(AcadDb.Database db, ViarioStore.Settings settings)
    {
        List<RoadProfile> profiles = ViarioStore.LoadProfiles(db);
        return profiles.FirstOrDefault(p => p.Id == settings.CurrentProfileId) ?? profiles[0];
    }

    private static string Describe(RoadProfile p) =>
        $"{p.Id} {p.Name}: pista {Num(p.Pista)} m | calcada {Num(p.Calcada)} m | raio de esquina {Num(p.Raio)} m";

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void ListProfiles(Document doc)
    {
        ViarioStore.Settings settings = ViarioStore.LoadSettings(doc.Database);
        foreach (RoadProfile p in ViarioStore.LoadProfiles(doc.Database))
            doc.Editor.WriteMessage("\n  " + Describe(p) + (p.Id == settings.CurrentProfileId ? "  (corrente)" : ""));
    }

    private static void ManageProfiles(Document doc)
    {
        Editor ed = doc.Editor;
        ListProfiles(doc);

        PromptKeywordOptions options = new(
            "\nPerfis [Corrente/Novo/Editar/Excluir] <Corrente>: ",
            "Corrente Novo Editar Excluir")
        {
            AllowNone = true
        };

        PromptResult result = ed.GetKeywords(options);
        if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
            return;

        switch (result.Status == PromptStatus.OK ? result.StringResult : "Corrente")
        {
            case "Novo":
                NewProfile(doc);
                break;
            case "Editar":
                EditProfile(doc);
                break;
            case "Excluir":
                DeleteProfile(doc);
                break;
            default:
                ChooseCurrentProfile(doc);
                break;
        }
    }

    private static RoadProfile? PickProfile(Document doc, string label)
    {
        List<RoadProfile> profiles = ViarioStore.LoadProfiles(doc.Database);
        ViarioStore.Settings settings = ViarioStore.LoadSettings(doc.Database);
        RoadProfile current = profiles.FirstOrDefault(p => p.Id == settings.CurrentProfileId) ?? profiles[0];

        ListProfiles(doc);
        PromptStringOptions options = new("\n" + label + " (codigo ou nome) <" + current.Id + ">: ")
        {
            AllowSpaces = true
        };

        PromptResult result = doc.Editor.GetString(options);
        if (result.Status != PromptStatus.OK)
            return null;

        string text = result.StringResult.Trim();
        if (text.Length == 0)
            return current;

        RoadProfile? found = profiles.FirstOrDefault(p => p.Id.Equals(text, StringComparison.OrdinalIgnoreCase)) ??
                             profiles.FirstOrDefault(p => p.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (found is null)
            doc.Editor.WriteMessage("\nPerfil nao encontrado.");
        return found;
    }

    private static void ChooseCurrentProfile(Document doc)
    {
        RoadProfile? profile = PickProfile(doc, "Perfil corrente");
        if (profile is null)
            return;

        AcadDb.Database db = doc.Database;
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        settings.CurrentProfileId = profile.Id;

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            EnsureProfilesSaved(tr, db);
            ViarioStore.SaveSettings(tr, db, settings);
            tr.Commit();
        }

        doc.Editor.WriteMessage("\n  Perfil corrente: " + Describe(profile));
    }

    /// <summary>Grava o perfil padrao no DWG na primeira vez.</summary>
    private static void EnsureProfilesSaved(AcadDb.Transaction tr, AcadDb.Database db)
    {
        foreach (RoadProfile p in ViarioStore.LoadProfiles(db))
            ViarioStore.SaveProfile(tr, db, p);
    }

    private static bool AskProfileValues(Editor ed, RoadProfile p)
    {
        PromptStringOptions name = new("\nNome do perfil <" + p.Name + ">: ") { AllowSpaces = true };
        PromptResult nameResult = ed.GetString(name);
        if (nameResult.Status != PromptStatus.OK)
            return false;
        if (nameResult.StringResult.Trim().Length > 0)
            p.Name = nameResult.StringResult.Trim();

        double? pista = AskDouble(ed, "Largura total da pista (m)", p.Pista, allowZero: false);
        if (pista is null)
            return false;
        double? calcada = AskDouble(ed, "Largura da calcada de cada lado (m, 0 = sem calcada)", p.Calcada, allowZero: true);
        if (calcada is null)
            return false;
        double? raio = AskDouble(ed, "Raio de esquina no meio-fio (m, 0 = canto vivo)", p.Raio, allowZero: true);
        if (raio is null)
            return false;

        p.Pista = pista.Value;
        p.Calcada = calcada.Value;
        p.Raio = raio.Value;
        return true;
    }

    private static double? AskDouble(Editor ed, string label, double current, bool allowZero)
    {
        PromptDoubleOptions options = new("\n" + label + " <" + Num(current) + ">: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = allowZero
        };

        PromptDoubleResult result = ed.GetDouble(options);
        if (result.Status == PromptStatus.None)
            return current;
        return result.Status == PromptStatus.OK ? result.Value : null;
    }

    private static void NewProfile(Document doc)
    {
        AcadDb.Database db = doc.Database;
        List<RoadProfile> profiles = ViarioStore.LoadProfiles(db);
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        RoadProfile basis = profiles.FirstOrDefault(p => p.Id == settings.CurrentProfileId) ?? profiles[0];

        RoadProfile profile = basis.Clone();
        profile.Id = ViarioStore.NextProfileId(profiles);
        profile.Name = "Perfil " + profile.Id;

        if (!AskProfileValues(doc.Editor, profile))
            return;

        settings.CurrentProfileId = profile.Id;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            EnsureProfilesSaved(tr, db);
            ViarioStore.SaveProfile(tr, db, profile);
            ViarioStore.SaveSettings(tr, db, settings);
            tr.Commit();
        }

        doc.Editor.WriteMessage("\n  Perfil criado e definido como corrente: " + Describe(profile));
    }

    private static void EditProfile(Document doc)
    {
        RoadProfile? profile = PickProfile(doc, "Perfil a editar");
        if (profile is null || !AskProfileValues(doc.Editor, profile))
            return;

        AcadDb.Database db = doc.Database;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            EnsureProfilesSaved(tr, db);
            ViarioStore.SaveProfile(tr, db, profile);
            tr.Commit();
        }

        doc.Editor.WriteMessage("\n  Perfil atualizado: " + Describe(profile));
        ViarioSync.Regenerate(doc, quiet: false);
    }

    private static void DeleteProfile(Document doc)
    {
        AcadDb.Database db = doc.Database;
        List<RoadProfile> profiles = ViarioStore.LoadProfiles(db);
        if (profiles.Count <= 1)
        {
            doc.Editor.WriteMessage("\nE preciso manter pelo menos um perfil.");
            return;
        }

        RoadProfile? profile = PickProfile(doc, "Perfil a excluir");
        if (profile is null)
            return;

        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        RoadProfile replacement = profiles.First(p => p.Id != profile.Id);

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            EnsureProfilesSaved(tr, db);
            ViarioStore.DeleteProfile(tr, db, profile.Id);
            if (settings.CurrentProfileId == profile.Id)
            {
                settings.CurrentProfileId = replacement.Id;
                ViarioStore.SaveSettings(tr, db, settings);
            }

            tr.Commit();
        }

        doc.Editor.WriteMessage(
            "\n  Perfil excluido. Eixos que usavam " + profile.Name + " passam a usar " + replacement.Name + ".");
        ViarioSync.Regenerate(doc, quiet: false);
    }

    // ------------------------------------------------------------------
    // Configuracao
    // ------------------------------------------------------------------

    private static void Configure(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);

        ed.WriteMessage(
            "\n  Tolerancia de encontro entre eixos: " + Num(settings.SnapTolerance) +
            "\n  Captura das pontas ao lancar eixos: " + Num(settings.Capture));

        double? snap = AskDouble(ed, "Tolerancia de encontro entre eixos (ponta encostada em outro eixo)", settings.SnapTolerance, allowZero: false);
        if (snap is null)
            return;
        double? capture = AskDouble(ed, "Captura das pontas ao lancar eixos (0 = desligada)", settings.Capture, allowZero: true);
        if (capture is null)
            return;

        settings.SnapTolerance = snap.Value;
        settings.Capture = capture.Value;

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.SaveSettings(tr, db, settings);
            tr.Commit();
        }

        ed.WriteMessage("\n  Configuracao salva no desenho.");
        ViarioSync.Regenerate(doc, quiet: false);
    }
}

/// <summary>
/// Jig do eixo: a cada movimento do mouse recalcula a rede com o eixo em
/// andamento e desenha os bordos/esquinas que vao mudar.
/// </summary>
internal sealed class AxisJig : DrawJig
{
    private readonly List<V2> _points;
    private readonly RoadProfile _profile;
    private readonly List<AxisInput> _existing;
    private readonly HashSet<string> _existingKeys;
    private readonly ViarioStore.Settings _settings;

    public AxisJig(List<V2> points, RoadProfile profile, List<AxisInput> existing, HashSet<string> existingKeys, ViarioStore.Settings settings)
    {
        _points = points;
        _profile = profile;
        _existing = existing;
        _existingKeys = existingKeys;
        _settings = settings;
        Current = points[^1];
    }

    public V2 Current { get; private set; }
    public bool Finished { get; private set; }
    public string? Keyword { get; private set; }

    protected override SamplerStatus Sampler(JigPrompts prompts)
    {
        V2 last = _points[^1];
        JigPromptPointOptions options = new(
            "\nProximo ponto" + (_points.Count > 1 ? " ou [Desfazer]" : "") + " <terminar>: ")
        {
            UserInputControls = UserInputControls.Accept3dCoordinates |
                                UserInputControls.NullResponseAccepted |
                                UserInputControls.NoNegativeResponseAccepted,
            BasePoint = new Point3d(last.X, last.Y, 0),
            UseBasePoint = true,
            Cursor = CursorType.RubberBand
        };

        if (_points.Count > 1)
            options.Keywords.Add("Desfazer");

        PromptPointResult result = prompts.AcquirePoint(options);

        switch (result.Status)
        {
            case PromptStatus.Keyword:
                Keyword = result.StringResult;
                return SamplerStatus.OK;
            case PromptStatus.None:
                Finished = true;
                return SamplerStatus.Cancel;
            case PromptStatus.OK:
                break;
            default:
                return SamplerStatus.Cancel;
        }

        V2 p = new(result.Value.X, result.Value.Y);
        if (V2.Dist(p, Current) < 1e-9)
            return SamplerStatus.NoChange;

        Current = p;
        return SamplerStatus.OK;
    }

    protected override bool WorldDraw(WorldDraw draw)
    {
        try
        {
            List<V2> pts = new(_points);
            if (V2.Dist(pts[^1], Current) > 1e-6)
                pts.Add(Current);

            if (pts.Count < 2)
                return true;

            pts = Capture(pts, _existing, _settings.Capture);
            GPath path = PathOf(pts);
            if (path.IsEmpty)
                return true;

            // Eixo em andamento.
            using (AcadDb.Polyline axis = ViarioStore.ToPolyline(path.Segs, closed: false))
            {
                axis.ColorIndex = 1;
                draw.Geometry.Draw(axis);
            }

            // Rede recalculada: desenha o que muda.
            List<AxisInput> axes = new(PreviewSubset(path));
            axes.Add(new AxisInput("__NOVO__", path, _profile));
            ViarioResult result = ViarioEngine.Compute(axes, new EngineSettings { SnapTolerance = _settings.SnapTolerance });

            foreach (OutputLine line in result.Lines)
            {
                if (_existingKeys.Contains(ViarioStore.GeometryKey(line)))
                    continue;

                using AcadDb.Polyline pl = ViarioStore.ToPolyline(line.Segs, line.Closed);
                pl.ColorIndex = line.Level == EdgeLevel.Bordo ? 4 : 9;
                draw.Geometry.Draw(pl);
            }

            // Intersecoes do eixo novo.
            double size = Math.Max(_profile.Pista * 0.25, 0.5);
            foreach (NodeInfo node in result.Nodes.Where(n => n.AxisKeys.Contains("__NOVO__")))
            {
                using AcadDb.Circle marker = new(new Point3d(node.Point.X, node.Point.Y, 0), Vector3d.ZAxis, size);
                marker.ColorIndex = 3;
                draw.Geometry.Draw(marker);
            }
        }
        catch
        {
            // Preview nunca derruba o comando.
        }

        return true;
    }

    /// <summary>Com redes grandes, so os eixos perto do eixo em andamento entram no preview.</summary>
    private IEnumerable<AxisInput> PreviewSubset(GPath path)
    {
        if (_existing.Count <= 150)
            return _existing;

        Box2 box = path.Bounds().Inflate(200.0);
        return _existing.Where(a => a.Path.Bounds().Intersects(box));
    }

    public static GPath PathOf(List<V2> pts)
    {
        List<Seg> segs = new();
        for (int i = 1; i < pts.Count; i++)
            segs.Add(Seg.Line(pts[i - 1], pts[i]));
        return new GPath(segs);
    }

    /// <summary>Puxa a primeira e a ultima ponta para o eixo vizinho mais proximo (dentro da captura).</summary>
    public static List<V2> Capture(List<V2> pts, List<AxisInput> existing, double capture)
    {
        List<V2> result = new(pts);
        if (capture <= 0 || existing.Count == 0 || result.Count < 2)
            return result;

        foreach (int index in new[] { 0, result.Count - 1 })
        {
            V2 p = result[index];
            double best = capture;
            V2? target = null;

            foreach (AxisInput axis in existing)
            {
                if (!axis.Path.Bounds().Inflate(capture).Intersects(Box2.Empty.Add(p)))
                    continue;

                (_, V2 q, double dist) = axis.Path.Project(p);
                if (dist <= best)
                {
                    best = dist;
                    target = q;
                }
            }

            if (target is V2 t)
                result[index] = t;
        }

        return result;
    }
}
