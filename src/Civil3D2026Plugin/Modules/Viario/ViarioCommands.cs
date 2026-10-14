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
/// C3DVIARIO: rede viaria urbana em planta.
/// - eixos com secao (pista, canteiro, sarjeta, meio-fio, calcada, raio);
/// - linhas por elemento da secao, cada uma no seu layer;
/// - intersecoes automaticas com esquinas concordadas; rotatoria;
/// - preview ao lancar eixo (retas e arcos) e ao mudar raios;
/// - edicao associativa (mexeu no eixo, refaz).
/// C3DVIARIOEIXO: lancar eixos. C3DVIARIORAIO: raio de esquina/ilha/eixo.
/// </summary>
public sealed class ViarioCommands
{
    public const string Versao = C3DVersions.Viario;

    [CommandMethod(C3DCommands.Viario.Rede, CommandFlags.Modal)]
    public void Rede()
    {
        Run(C3DCommands.Viario.Rede, doc =>
        {
            Editor ed = doc.Editor;
            PromptKeywordOptions options = new(
                "\nOpcao [Eixo/MOdelos/ADicionar/Secao/ATribuir/Raio/ROtatoria/ELementos/SOltar/REmover/ATUalizar/CIvil/Config] <Eixo>: ",
                "Eixo MOdelos ADicionar Secao ATribuir Raio ROtatoria ELementos SOltar REmover ATUalizar CIvil Config")
            {
                AllowNone = true
            };

            PromptResult result = ed.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
                return;

            switch (result.Status == PromptStatus.OK ? result.StringResult : "Eixo")
            {
                case "MOdelos": ViarioTemplateCommands.InsertTemplate(doc); break;
                case "ADicionar": AddAxes(doc); break;
                case "Secao": ManageSections(doc); break;
                case "ATribuir": AssignSection(doc); break;
                case "Raio": EditRadius(doc); break;
                case "ROtatoria": CreateRoundabout(doc); break;
                case "ELementos": ViarioElementCommands.Menu(doc); break;
                case "SOltar": Release(doc); break;
                case "REmover": RemoveAxes(doc); break;
                case "ATUalizar": ViarioSync.Regenerate(doc, quiet: false); break;
                case "CIvil": CivilMenu(doc); break;
                case "Config": Configure(doc); break;
                default: DrawAxes(doc); break;
            }
        });
    }

    [CommandMethod(C3DCommands.Viario.Eixo, CommandFlags.Modal)]
    public void Eixo() => Run(C3DCommands.Viario.Eixo, DrawAxes);

    [CommandMethod(C3DCommands.Viario.Raio, CommandFlags.Modal)]
    public void Raio() => Run(C3DCommands.Viario.Raio, EditRadius);

    [CommandMethod(C3DCommands.Viario.Modelos, CommandFlags.Modal)]
    public void Modelos() => Run(C3DCommands.Viario.Modelos, ViarioTemplateCommands.InsertTemplate);

    [CommandMethod(C3DCommands.Viario.Alargar, CommandFlags.Modal)]
    public void Alargar() => Run(C3DCommands.Viario.Alargar, ViarioElementCommands.Alargar);

    [CommandMethod(C3DCommands.Viario.Gota, CommandFlags.Modal)]
    public void Gota() => Run(C3DCommands.Viario.Gota, ViarioElementCommands.Gota);

    [CommandMethod(C3DCommands.Viario.Conversao, CommandFlags.Modal)]
    public void Conversao() => Run(C3DCommands.Viario.Conversao, ViarioElementCommands.Conversao);

    [CommandMethod(C3DCommands.Viario.Abertura, CommandFlags.Modal)]
    public void Abertura() => Run(C3DCommands.Viario.Abertura, ViarioElementCommands.Abertura);

    [CommandMethod(C3DCommands.Viario.Livre, CommandFlags.Modal)]
    public void Livre() => Run(C3DCommands.Viario.Livre, ViarioElementCommands.Livre);

    [CommandMethod(C3DCommands.Viario.ElemRemover, CommandFlags.Modal)]
    public void ElemRemover() => Run(C3DCommands.Viario.ElemRemover, ViarioElementCommands.Remove);

    [CommandMethod(C3DCommands.Viario.Civil, CommandFlags.Modal)]
    public void Civil() => Run(C3DCommands.Viario.Civil, CivilMenu);

    /// <summary>Gera/atualiza ou remove os alinhamentos do Civil (eixos, LD/LE, curb returns).</summary>
    private static void CivilMenu(Document doc)
    {
        Editor ed = doc.Editor;
        bool active = ViarioCivil.IsActive(doc.Database);
        ed.WriteMessage(active
            ? "\n  Objetos do Civil ativos: acompanham a rede automaticamente."
            : "\n  Ainda nao ha objetos do Civil gerados para esta rede.");

        PromptKeywordOptions options = new("\nObjetos do Civil [Gerar/Remover] <Gerar>: ", "Gerar Remover")
        {
            AllowNone = true
        };
        PromptResult r = ed.GetKeywords(options);
        if (r.Status == PromptStatus.Cancel || r.Status == PromptStatus.Error)
            return;

        if (r.Status == PromptStatus.OK && r.StringResult == "Remover")
        {
            PromptKeywordOptions confirm = new("\nApagar eixos, bordos e curb returns gerados no Civil (perfis juntos)? [Sim/Nao] <Nao>: ", "Sim Nao")
            {
                AllowNone = true
            };
            PromptResult c = ed.GetKeywords(confirm);
            if (c.Status != PromptStatus.OK || c.StringResult != "Sim")
                return;

            int n = ViarioCivil.RemoveAll(doc);
            ed.WriteMessage($"\n  {n} alinhamento(s) removido(s). A rede em polilinhas continua.");
            return;
        }

        ViarioCivil.Report report = ViarioCivil.Sync(doc, create: true);
        ed.WriteMessage("\n  " + report);
        if (report.Styles.Length > 0)
            ed.WriteMessage("\n  Alinhamentos novos: " + report.Styles);
        foreach (string m in report.Messages.Distinct().Take(12))
            ed.WriteMessage("\n  [CIVIL] " + m);
        ed.WriteMessage($"\n  Site \"{ViarioCivil.SiteName}\", layers {ViarioCivil.LayerAxis}, {ViarioCivil.LayerEdge}, {ViarioCivil.LayerCurb}.");
    }

    private static void Run(string command, Action<Document> action)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        ViarioSync.Initialize();

        try
        {
            ViarioSync.Suspended = true;
            WriteHeader(doc);
            action(doc);
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\n[ERRO " + command + "] " + ex.Message);
        }
        finally
        {
            ViarioSync.Suspended = false;
        }
    }

    private static void WriteHeader(Document doc)
    {
        ViarioStore.Settings settings = ViarioStore.LoadSettings(doc.Database);
        RoadSection current = CurrentSection(doc.Database, settings);
        doc.Editor.WriteMessage(
            "\n" + C3DCommands.Viario.Rede + " v" + Versao + " - rede viaria (bordos e intersecoes automaticos)." +
            "\n  Secao corrente: " + Describe(current));
    }

    // ------------------------------------------------------------------
    // Eixo (retas e arcos, com preview)
    // ------------------------------------------------------------------

    private static void DrawAxes(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        while (true)
        {
            ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
            RoadSection section = CurrentSection(db, settings);

            PromptPointOptions first = new(
                "\nPrimeiro ponto do eixo [" + section.Name + "] ou [Secao] <sair>: ")
            {
                AllowNone = true
            };
            first.Keywords.Add("Secao");
            first.AppendKeywordsToMessage = false;

            PromptPointResult start = ed.GetPoint(first);
            if (start.Status == PromptStatus.Keyword)
            {
                ChooseCurrentSection(doc);
                continue;
            }

            if (start.Status != PromptStatus.OK)
                return;

            Point3d p0 = start.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            List<V2> points = new() { new V2(p0.X, p0.Y) };
            List<double> bulges = new();
            bool arcMode = false;

            (List<AxisInput> existing, _, HashSet<string> existingKeys) = ViarioSync.ComputeCurrent(db);
            EngineSettings engine = ViarioSync.EngineSettingsFor(db);

            bool finished = false;
            while (!finished)
            {
                AxisJig jig = new(points, bulges, arcMode, section, existing, existingKeys, engine, settings.Capture);
                PromptResult drag = ed.Drag(jig);

                if (jig.Finished || drag.Status == PromptStatus.None)
                {
                    finished = true;
                }
                else if (jig.Keyword is string keyword)
                {
                    switch (keyword)
                    {
                        case "Arco":
                            arcMode = true;
                            break;
                        case "Linha":
                            arcMode = false;
                            break;
                        default:
                            if (points.Count > 1)
                            {
                                points.RemoveAt(points.Count - 1);
                                bulges.RemoveAt(bulges.Count - 1);
                            }

                            break;
                    }
                }
                else if (drag.Status == PromptStatus.OK)
                {
                    if (V2.Dist(points[^1], jig.Current) > 1e-6)
                    {
                        points.Add(jig.Current);
                        bulges.Add(jig.CurrentBulge);
                    }
                }
                else
                {
                    return; // ESC: descarta o eixo em andamento
                }
            }

            if (points.Count < 2)
                continue;

            List<V2> final = AxisJig.Capture(points, existing, settings.Capture);
            CreateAxis(doc, final, bulges, section);
            ViarioResult? result = ViarioSync.Regenerate(doc, quiet: true);
            if (result is not null)
            {
                ed.WriteMessage($"\n  Eixo criado. Rede: {result.Nodes.Count} intersecao(oes), {result.Lines.Count} linha(s).");
                ViarioSync.WriteWarnings(doc, result);
            }
        }
    }

    private static void CreateAxis(Document doc, List<V2> points, List<double> bulges, RoadSection section)
    {
        AcadDb.Database db = doc.Database;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();

        ViarioStore.EnsureRegApp(tr, db);
        ViarioStore.EnsureLayers(tr, db);

        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

        AcadDb.Polyline pl = new();
        for (int i = 0; i < points.Count; i++)
            pl.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), i < bulges.Count ? bulges[i] : 0.0, 0, 0);

        pl.SetDatabaseDefaults(db);
        pl.Layer = ViarioStore.LayerAxis;
        ms.AppendEntity(pl);
        tr.AddNewlyCreatedDBObject(pl, true);
        ViarioStore.SetAxis(pl, section.Id, null);
        tr.Commit();
    }

    // ------------------------------------------------------------------
    // Rotatoria
    // ------------------------------------------------------------------

    private static void CreateRoundabout(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptPointResult centerResult = ed.GetPoint(new PromptPointOptions("\nCentro da rotatoria: "));
        if (centerResult.Status != PromptStatus.OK)
            return;

        Point3d center = centerResult.Value.TransformBy(ed.CurrentUserCoordinateSystem);

        RoadSection? section = PickSection(doc, "Secao do anel (pista de giro)");
        if (section is null)
            return;

        PromptDistanceOptions radiusOptions = new("\nRaio da ilha central, na face do meio-fio <15>: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = false,
            BasePoint = centerResult.Value,
            UseBasePoint = true
        };

        PromptDoubleResult radiusResult = ed.GetDistance(radiusOptions);
        if (radiusResult.Status != PromptStatus.OK && radiusResult.Status != PromptStatus.None)
            return;

        double island = radiusResult.Status == PromptStatus.OK ? radiusResult.Value : 15.0;
        double axisRadius = island + section.EdgeOffset(EdgeLevel.FaceMF);

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.EnsureRegApp(tr, db);
            ViarioStore.EnsureLayers(tr, db);

            AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
            AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

            AcadDb.Circle ring = new(new Point3d(center.X, center.Y, 0), Vector3d.ZAxis, axisRadius);
            ring.SetDatabaseDefaults(db);
            ring.Layer = ViarioStore.LayerAxis;
            ms.AppendEntity(ring);
            tr.AddNewlyCreatedDBObject(ring, true);
            ViarioStore.SetAxis(ring, section.Id, null);
            tr.Commit();
        }

        ed.WriteMessage(
            $"\n  Rotatoria criada: ilha central R={Num(island)} m, eixo do anel R={Num(axisRadius)} m, pista de giro {Num(section.Pista)} m." +
            "\n  Lance os ramos ate o anel: as pontas sao capturadas no eixo do anel e as entradas se formam sozinhas." +
            "\n  Raio de entrada/saida: " + C3DCommands.Viario.Raio + ".");
        ViarioSync.Regenerate(doc, quiet: false);
    }

    // ------------------------------------------------------------------
    // Raios (esquina, ilha, eixo) com preview
    // ------------------------------------------------------------------

    private static void EditRadius(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptPointOptions options = new("\nClique perto da esquina ou [Ilha/Eixo/Limpar]: ");
        options.Keywords.Add("Ilha");
        options.Keywords.Add("Eixo");
        options.Keywords.Add("Limpar");
        options.AppendKeywordsToMessage = false;

        PromptPointResult pick = ed.GetPoint(options);
        if (pick.Status == PromptStatus.Keyword)
        {
            switch (pick.StringResult)
            {
                case "Ilha": EditIslandRadius(doc); break;
                case "Eixo": EditAxisRadius(doc); break;
                default: ClearCornerRadii(doc); break;
            }

            return;
        }

        if (pick.Status != PromptStatus.OK)
            return;

        Point3d wcs = pick.Value.TransformBy(ed.CurrentUserCoordinateSystem);
        V2 p = new(wcs.X, wcs.Y);

        (List<AxisInput> axes, ViarioResult result, HashSet<string> keys) = ViarioSync.ComputeCurrent(db);
        CornerInfo? corner = result.Corners
            .OrderBy(c => DistanceToCorner(c, p))
            .FirstOrDefault();

        if (corner is null || DistanceToCorner(corner, p) > Math.Max(30.0, corner.Radius * 3.0))
        {
            ed.WriteMessage("\nNenhuma esquina perto do ponto clicado.");
            return;
        }

        EngineSettings engine = ViarioSync.EngineSettingsFor(db);
        ed.WriteMessage($"\n  Esquina: raio atual {Num(corner.Radius)} m.");

        double? value = DragRadius(ed, axes, engine, keys, corner.Corner, corner.Radius,
            r => (axes, With(engine, new[] { corner.Key }, r)), out bool toDefault);

        if (value is null && !toDefault)
            return;

        Dictionary<string, double> radii = ViarioStore.LoadCornerRadii(db);
        if (toDefault)
            radii.Remove(corner.Key);
        else
            radii[corner.Key] = value!.Value;

        SaveRadiiAndRegenerate(doc, radii);
    }

    private static void EditIslandRadius(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptPointResult pick = ed.GetPoint(new PromptPointOptions("\nClique dentro da ilha: "));
        if (pick.Status != PromptStatus.OK)
            return;

        Point3d wcs = pick.Value.TransformBy(ed.CurrentUserCoordinateSystem);
        V2 p = new(wcs.X, wcs.Y);

        (List<AxisInput> axes, ViarioResult result, HashSet<string> keys) = ViarioSync.ComputeCurrent(db);

        OutputLine? island = result.Lines
            .Where(l => l.Level == EdgeLevel.FaceMF && l.Closed && Contains(l, p))
            .OrderBy(l => Area(l))
            .FirstOrDefault();

        if (island is null)
        {
            ed.WriteMessage("\nO ponto nao esta dentro de uma ilha/quadra fechada pela face do meio-fio.");
            return;
        }

        List<CornerInfo> corners = result.Corners
            .Where(c => c.Arc is not null && DistanceToLine(island, c.Arc.PointAt(0.5)) < 1e-3)
            .ToList();

        if (corners.Count == 0)
        {
            ed.WriteMessage("\nEssa ilha nao tem esquinas concordadas.");
            return;
        }

        EngineSettings engine = ViarioSync.EngineSettingsFor(db);
        string[] cornerKeys = corners.Select(c => c.Key).ToArray();
        double current = corners.Average(c => c.Radius);
        ed.WriteMessage($"\n  Ilha com {corners.Count} esquina(s), raio medio {Num(current)} m.");

        double? value = DragRadius(ed, axes, engine, keys, p, current,
            r => (axes, With(engine, cornerKeys, r)), out bool toDefault);

        if (value is null && !toDefault)
            return;

        Dictionary<string, double> radii = ViarioStore.LoadCornerRadii(db);
        foreach (string key in cornerKeys)
        {
            if (toDefault)
                radii.Remove(key);
            else
                radii[key] = value!.Value;
        }

        SaveRadiiAndRegenerate(doc, radii);
    }

    private static void EditAxisRadius(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptEntityOptions options = new("\nSelecione o eixo: ");
        options.SetRejectMessage("\nSelecione um eixo da rede.");
        options.AddAllowedClass(typeof(AcadDb.Curve), false);
        PromptEntityResult pick = ed.GetEntity(options);
        if (pick.Status != PromptStatus.OK)
            return;

        string sectionId;
        double? radius;
        string handle;
        using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            AcadDb.DBObject obj = tr.GetObject(pick.ObjectId, AcadDb.OpenMode.ForRead);
            if (!ViarioStore.ReadAxis(obj, out sectionId, out radius))
            {
                ed.WriteMessage("\nO objeto nao e um eixo da rede.");
                return;
            }

            handle = obj.Handle.ToString() ?? string.Empty;
        }

        (List<AxisInput> axes, _, HashSet<string> keys) = ViarioSync.ComputeCurrent(db);
        AxisInput? target = axes.FirstOrDefault(a => a.Key == handle);
        if (target is null)
            return;

        EngineSettings engine = ViarioSync.EngineSettingsFor(db);
        double current = radius ?? target.Section.Raio;
        ed.WriteMessage($"\n  Eixo: raio {Num(current)} m" + (radius is null ? " (da secao)." : " (especifico do eixo)."));

        Point3d wcs = pick.PickedPoint.TransformBy(ed.CurrentUserCoordinateSystem);
        V2 basePoint = target.Path.Project(new V2(wcs.X, wcs.Y)).Point;

        // Raio do eixo vale para todas as esquinas dele: sem os especificos dessas esquinas.
        EngineSettings withoutCorners = engine.Clone();
        withoutCorners.CornerRadius = engine.CornerRadius
            .Where(kv => !CornerHasAxis(kv.Key, handle))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        double? value = DragRadius(ed, axes, engine, keys, basePoint, current,
            r => (axes.Select(a => a.Key == handle ? a.With((double?)r) : a).ToList(), withoutCorners),
            out bool toDefault);

        if (value is null && !toDefault)
            return;

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            AcadDb.Entity entity = (AcadDb.Entity)tr.GetObject(pick.ObjectId, AcadDb.OpenMode.ForWrite);
            ViarioStore.UpdateAxis(entity, radius: toDefault ? null : value, clearRadius: toDefault);
            ViarioStore.SaveCornerRadii(tr, db, withoutCorners.CornerRadius);
            tr.Commit();
        }

        ViarioSync.Regenerate(doc, quiet: false);
    }

    private static void ClearCornerRadii(Document doc)
    {
        PromptKeywordOptions confirm = new(
            "\nApagar todos os raios especificos de esquina/ilha (voltam ao raio da secao ou do eixo)? [Sim/Nao] <Nao>: ",
            "Sim Nao")
        {
            AllowNone = true
        };

        PromptResult answer = doc.Editor.GetKeywords(confirm);
        if (answer.Status == PromptStatus.OK && answer.StringResult == "Sim")
            SaveRadiiAndRegenerate(doc, new Dictionary<string, double>());
    }

    private static void SaveRadiiAndRegenerate(Document doc, Dictionary<string, double> radii)
    {
        AcadDb.Database db = doc.Database;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.SaveCornerRadii(tr, db, radii);
            tr.Commit();
        }

        ViarioSync.Regenerate(doc, quiet: false);
    }

    internal static bool CornerHasAxis(string cornerKey, string axisKey) =>
        cornerKey.Split('#')[0].Split('|').Any(part => part.Length > 1 && part[..^1] == axisKey);

    private static EngineSettings With(EngineSettings engine, IEnumerable<string> keys, double radius)
    {
        EngineSettings result = engine.Clone();
        foreach (string key in keys)
            result.CornerRadius[key] = radius;
        return result;
    }

    /// <summary>Jig de raio: distancia do mouse ao ponto base = raio. Null = cancelado.</summary>
    private static double? DragRadius(
        Editor ed, List<AxisInput> axes, EngineSettings engine, HashSet<string> keys, V2 basePoint, double current,
        Func<double, (List<AxisInput>, EngineSettings)> apply, out bool toDefault)
    {
        toDefault = false;
        ValueJig jig = new("Raio", keys, basePoint, current, apply);
        PromptResult result = ed.Drag(jig);

        if (jig.ToDefault)
        {
            toDefault = true;
            return null;
        }

        if (result.Status == PromptStatus.None)
            return current;

        return result.Status == PromptStatus.OK ? jig.Value : null;
    }

    private static double DistanceToCorner(CornerInfo c, V2 p) =>
        c.Arc is not null ? Math.Min(c.Arc.Closest(p).Dist, V2.Dist(c.Corner, p)) : V2.Dist(c.Corner, p);

    private static double DistanceToLine(OutputLine line, V2 p) =>
        line.Segs.Min(s => s.Closest(p).Dist);

    private static List<V2> Sample(OutputLine line)
    {
        List<V2> pts = new();
        foreach (Seg s in line.Segs)
        {
            int n = s.IsArc ? 16 : 1;
            for (int k = 0; k < n; k++)
                pts.Add(s.PointAt(k / (double)n));
        }

        return pts;
    }

    private static bool Contains(OutputLine line, V2 p)
    {
        List<V2> pts = Sample(line);
        bool inside = false;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            if (((pts[i].Y > p.Y) != (pts[j].Y > p.Y)) &&
                (p.X < ((pts[j].X - pts[i].X) * (p.Y - pts[i].Y) / (pts[j].Y - pts[i].Y)) + pts[i].X))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double Area(OutputLine line)
    {
        List<V2> pts = Sample(line);
        double area = 0;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            area += V2.Cross(pts[j], pts[i]);
        return Math.Abs(area / 2.0);
    }

    // ------------------------------------------------------------------
    // Adicionar / Atribuir / Remover / Soltar
    // ------------------------------------------------------------------

    private static AcadDb.ObjectId[]? SelectCurves(Editor ed, string message)
    {
        AcadDb.TypedValue[] filter =
        {
            new((int)AcadDb.DxfCode.Start, "LWPOLYLINE,LINE,ARC,CIRCLE")
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
        RoadSection section = CurrentSection(db, settings);

        AcadDb.ObjectId[]? ids = SelectCurves(ed,
            "\nSelecione os eixos existentes (polilinhas, linhas, arcos, circulos) para a secao " + section.Name + ": ");
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

                ViarioStore.SetAxis(curve, section.Id, null);
                count++;
            }

            tr.Commit();
        }

        ed.WriteMessage($"\n  {count} eixo(s) adicionado(s) com a secao {section.Name}." +
                        (skipped > 0 ? $" Ignorados: {skipped} (linha gerada ou fora do plano XY)." : ""));
        ViarioSync.Regenerate(doc, quiet: false);
    }

    private static void AssignSection(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        AcadDb.ObjectId[]? ids = SelectCurves(ed, "\nSelecione os eixos que vao mudar de secao: ");
        if (ids is null)
            return;

        RoadSection? section = PickSection(doc, "Secao para os eixos selecionados");
        if (section is null)
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

                ViarioStore.UpdateAxis(curve, sectionId: section.Id);
                count++;
            }

            tr.Commit();
        }

        ed.WriteMessage($"\n  {count} eixo(s) agora com a secao {section.Name}.");
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

    private static void Release(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptKeywordOptions options = new(
            "\nSoltar [Selecionar/Restaurar] <Selecionar>: ",
            "Selecionar Restaurar")
        {
            AllowNone = true
        };

        PromptResult choice = ed.GetKeywords(options);
        if (choice.Status == PromptStatus.Cancel || choice.Status == PromptStatus.Error)
            return;

        if (choice.Status == PromptStatus.OK && choice.StringResult == "Restaurar")
        {
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                ViarioStore.SaveReleased(tr, db, Array.Empty<string>());
                tr.Commit();
            }

            ed.WriteMessage("\n  O plugin volta a gerar todas as linhas. As linhas soltas continuam no desenho (apague as que nao quiser).");
            ViarioSync.Regenerate(doc, quiet: false);
            return;
        }

        AcadDb.TypedValue[] filter = { new((int)AcadDb.DxfCode.Start, "LWPOLYLINE") };
        PromptSelectionResult selection = ed.GetSelection(
            new PromptSelectionOptions { MessageForAdding = "\nSelecione as linhas geradas a soltar (viram polilinhas comuns): " },
            new SelectionFilter(filter));
        if (selection.Status != PromptStatus.OK)
            return;

        HashSet<string> released = ViarioStore.LoadReleased(db);
        int count = 0;

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (AcadDb.ObjectId id in selection.Value.GetObjectIds())
            {
                AcadDb.Polyline pl = (AcadDb.Polyline)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                if (ViarioStore.ReadTag(pl, out string levelText) != ViarioStore.TagGenerated)
                    continue;

                released.Add(ViarioStore.GeometryKey(pl, ViarioStore.ParseLevel(levelText)));
                ViarioStore.ClearTag(pl);
                count++;
            }

            ViarioStore.SaveReleased(tr, db, released);
            tr.Commit();
        }

        ed.WriteMessage(
            $"\n  {count} linha(s) solta(s): pode editar a vontade, o plugin nao recria nem apaga." +
            "\n  Se a rede mudar naquele trecho, a linha nova aparece por cima. Soltar > Restaurar desfaz.");
    }

    // ------------------------------------------------------------------
    // Secoes
    // ------------------------------------------------------------------

    private static RoadSection CurrentSection(AcadDb.Database db, ViarioStore.Settings settings)
    {
        List<RoadSection> sections = ViarioStore.LoadSections(db);
        return sections.FirstOrDefault(p => p.Id == settings.CurrentSectionId) ?? sections[0];
    }

    private static string Describe(RoadSection s) =>
        $"{s.Id} {s.Name}: pista {Num(s.Pista)}" +
        (s.Canteiro > 0 ? $" | canteiro {Num(s.Canteiro)}" : "") +
        $" | sarjeta {Num(s.Sarjeta)} | meio-fio {Num(s.MeioFio)} | calcada {Num(s.Calcada)} | raio {Num(s.Raio)}" +
        $" (total {Num(s.TotalWidth)} m)";

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void ListSections(Document doc)
    {
        ViarioStore.Settings settings = ViarioStore.LoadSettings(doc.Database);
        foreach (RoadSection s in ViarioStore.LoadSections(doc.Database))
            doc.Editor.WriteMessage("\n  " + Describe(s) + (s.Id == settings.CurrentSectionId ? "  (corrente)" : ""));
    }

    private static void ManageSections(Document doc)
    {
        Editor ed = doc.Editor;
        ListSections(doc);

        PromptKeywordOptions options = new(
            "\nSecoes [Corrente/Nova/Editar/eXcluir] <Corrente>: ",
            "Corrente Nova Editar eXcluir")
        {
            AllowNone = true
        };

        PromptResult result = ed.GetKeywords(options);
        if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
            return;

        switch (result.Status == PromptStatus.OK ? result.StringResult : "Corrente")
        {
            case "Nova": NewSection(doc); break;
            case "Editar": EditSection(doc); break;
            case "eXcluir": DeleteSection(doc); break;
            default: ChooseCurrentSection(doc); break;
        }
    }

    private static RoadSection? PickSection(Document doc, string label)
    {
        List<RoadSection> sections = ViarioStore.LoadSections(doc.Database);
        ViarioStore.Settings settings = ViarioStore.LoadSettings(doc.Database);
        RoadSection current = sections.FirstOrDefault(p => p.Id == settings.CurrentSectionId) ?? sections[0];

        ListSections(doc);
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

        RoadSection? found = sections.FirstOrDefault(p => p.Id.Equals(text, StringComparison.OrdinalIgnoreCase)) ??
                             sections.FirstOrDefault(p => p.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (found is null)
            doc.Editor.WriteMessage("\nSecao nao encontrada.");
        return found;
    }

    private static void ChooseCurrentSection(Document doc)
    {
        RoadSection? section = PickSection(doc, "Secao corrente");
        if (section is null)
            return;

        AcadDb.Database db = doc.Database;
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        settings.CurrentSectionId = section.Id;

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            EnsureSectionsSaved(tr, db);
            ViarioStore.SaveSettings(tr, db, settings);
            tr.Commit();
        }

        doc.Editor.WriteMessage("\n  Secao corrente: " + Describe(section));
    }

    /// <summary>Grava no DWG as secoes padrao/antigas na primeira alteracao.</summary>
    private static void EnsureSectionsSaved(AcadDb.Transaction tr, AcadDb.Database db)
    {
        foreach (RoadSection s in ViarioStore.LoadSections(db))
            ViarioStore.SaveSection(tr, db, s);
    }

    private static bool AskSectionValues(Editor ed, RoadSection s)
    {
        PromptStringOptions name = new("\nNome da secao <" + s.Name + ">: ") { AllowSpaces = true };
        PromptResult nameResult = ed.GetString(name);
        if (nameResult.Status != PromptStatus.OK)
            return false;
        if (nameResult.StringResult.Trim().Length > 0)
            s.Name = nameResult.StringResult.Trim();

        double? pista = AskDouble(ed, "Largura total de rolamento, os dois sentidos (m)", s.Pista, allowZero: false);
        if (pista is null) return false;
        double? canteiro = AskDouble(ed, "Canteiro central (m, 0 = sem canteiro)", s.Canteiro, allowZero: true);
        if (canteiro is null) return false;
        double? sarjeta = AskDouble(ed, "Sarjeta de cada lado (m, 0 = sem sarjeta)", s.Sarjeta, allowZero: true);
        if (sarjeta is null) return false;
        double? meioFio = AskDouble(ed, "Meio-fio de cada lado, face as costas (m, 0 = sem)", s.MeioFio, allowZero: true);
        if (meioFio is null) return false;
        double? calcada = AskDouble(ed, "Calcada de cada lado (m, 0 = sem calcada)", s.Calcada, allowZero: true);
        if (calcada is null) return false;
        double? raio = AskDouble(ed, "Raio de esquina na face do meio-fio (m, 0 = canto vivo)", s.Raio, allowZero: true);
        if (raio is null) return false;

        s.Pista = pista.Value;
        s.Canteiro = canteiro.Value;
        s.Sarjeta = sarjeta.Value;
        s.MeioFio = meioFio.Value;
        s.Calcada = calcada.Value;
        s.Raio = raio.Value;
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

    private static void NewSection(Document doc)
    {
        AcadDb.Database db = doc.Database;
        List<RoadSection> sections = ViarioStore.LoadSections(db);
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        RoadSection basis = sections.FirstOrDefault(p => p.Id == settings.CurrentSectionId) ?? sections[0];

        RoadSection section = basis.Clone();
        section.Id = ViarioStore.NextSectionId(sections);
        section.Name = "Secao " + section.Id;

        if (!AskSectionValues(doc.Editor, section))
            return;

        settings.CurrentSectionId = section.Id;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            EnsureSectionsSaved(tr, db);
            ViarioStore.SaveSection(tr, db, section);
            ViarioStore.SaveSettings(tr, db, settings);
            tr.Commit();
        }

        doc.Editor.WriteMessage("\n  Secao criada e definida como corrente: " + Describe(section));
    }

    private static void EditSection(Document doc)
    {
        RoadSection? section = PickSection(doc, "Secao a editar");
        if (section is null || !AskSectionValues(doc.Editor, section))
            return;

        AcadDb.Database db = doc.Database;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            EnsureSectionsSaved(tr, db);
            ViarioStore.SaveSection(tr, db, section);
            tr.Commit();
        }

        doc.Editor.WriteMessage("\n  Secao atualizada: " + Describe(section));
        ViarioSync.Regenerate(doc, quiet: false);
    }

    private static void DeleteSection(Document doc)
    {
        AcadDb.Database db = doc.Database;
        List<RoadSection> sections = ViarioStore.LoadSections(db);
        if (sections.Count <= 1)
        {
            doc.Editor.WriteMessage("\nE preciso manter pelo menos uma secao.");
            return;
        }

        RoadSection? section = PickSection(doc, "Secao a excluir");
        if (section is null)
            return;

        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        RoadSection replacement = sections.First(p => p.Id != section.Id);

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            EnsureSectionsSaved(tr, db);
            ViarioStore.DeleteSection(tr, db, section.Id);
            if (settings.CurrentSectionId == section.Id)
            {
                settings.CurrentSectionId = replacement.Id;
                ViarioStore.SaveSettings(tr, db, settings);
            }

            tr.Commit();
        }

        doc.Editor.WriteMessage(
            "\n  Secao excluida. Eixos que usavam " + section.Name + " passam a usar " + replacement.Name + ".");
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

        double? snap = AskDouble(ed, "Tolerancia de encontro entre eixos (ponta encostada em outro eixo)", settings.SnapTolerance, allowZero: false);
        if (snap is null) return;
        double? capture = AskDouble(ed, "Captura das pontas ao lancar eixos (0 = desligada)", settings.Capture, allowZero: true);
        if (capture is null) return;
        double? setback = AskDouble(ed, "Recuo do nariz do canteiro em relacao a pista transversal", settings.MedianSetback, allowZero: true);
        if (setback is null) return;

        settings.SnapTolerance = snap.Value;
        settings.Capture = capture.Value;
        settings.MedianSetback = setback.Value;

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.SaveSettings(tr, db, settings);
            tr.Commit();
        }

        ed.WriteMessage("\n  Configuracao salva no desenho.");
        ViarioSync.Regenerate(doc, quiet: false);
    }
}

/// <summary>Desenho comum dos jigs: linhas da rede que mudam em relacao ao desenho atual.</summary>
internal static class ViarioPreview
{
    public static void DrawChanges(WorldDraw draw, ViarioResult result, HashSet<string> existingKeys)
    {
        foreach (OutputLine line in result.Lines)
        {
            if (existingKeys.Contains(ViarioStore.GeometryKey(line)))
                continue;

            using AcadDb.Polyline pl = ViarioStore.ToPolyline(line.Segs, line.Closed);
            pl.ColorIndex = line.Level switch
            {
                EdgeLevel.FaceMF => 4,
                EdgeLevel.Canteiro => 30,
                EdgeLevel.Ilha => 6,
                _ => 9
            };
            draw.Geometry.Draw(pl);
        }
    }
}

/// <summary>
/// Jig do eixo: a cada movimento do mouse recalcula a rede com o eixo em
/// andamento (reta ou arco tangente) e desenha o que vai mudar.
/// </summary>
internal sealed class AxisJig : DrawJig
{
    private readonly List<V2> _points;
    private readonly List<double> _bulges;
    private readonly bool _arcMode;
    private readonly RoadSection _section;
    private readonly List<AxisInput> _existing;
    private readonly HashSet<string> _existingKeys;
    private readonly EngineSettings _engine;
    private readonly double _capture;

    public AxisJig(List<V2> points, List<double> bulges, bool arcMode, RoadSection section,
        List<AxisInput> existing, HashSet<string> existingKeys, EngineSettings engine, double capture)
    {
        _points = points;
        _bulges = bulges;
        _arcMode = arcMode;
        _section = section;
        _existing = existing;
        _existingKeys = existingKeys;
        _engine = engine;
        _capture = capture;
        Current = points[^1];
    }

    public V2 Current { get; private set; }
    public double CurrentBulge { get; private set; }
    public bool Finished { get; private set; }
    public string? Keyword { get; private set; }

    private bool CanArc => _bulges.Count > 0;

    protected override SamplerStatus Sampler(JigPrompts prompts)
    {
        V2 last = _points[^1];
        string mode = _arcMode && CanArc ? "arco tangente" : "reta";
        JigPromptPointOptions options = new(
            "\nProximo ponto (" + mode + ") ou [" + (_arcMode ? "Linha" : "Arco") +
            (_points.Count > 1 ? "/Desfazer" : "") + "] <terminar>: ")
        {
            UserInputControls = UserInputControls.Accept3dCoordinates |
                                UserInputControls.NullResponseAccepted |
                                UserInputControls.NoNegativeResponseAccepted,
            BasePoint = new Point3d(last.X, last.Y, 0),
            UseBasePoint = true,
            Cursor = CursorType.RubberBand
        };

        options.Keywords.Add(_arcMode ? "Linha" : "Arco");
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
        CurrentBulge = _arcMode && CanArc ? TangentBulge(EndTangent(), last, p) : 0.0;
        return SamplerStatus.OK;
    }

    private V2 EndTangent() =>
        new Seg(_points[^2], _points[^1], _bulges[^1]).TangentAt(1.0);

    /// <summary>Bulge do arco tangente a t que sai de a e chega em b.</summary>
    public static double TangentBulge(V2 tangent, V2 a, V2 b)
    {
        V2 chord = b - a;
        double alpha = Math.Atan2(V2.Cross(tangent, chord), V2.Dot(tangent, chord));
        alpha = Math.Clamp(alpha, -Math.PI + 0.02, Math.PI - 0.02);
        return Math.Tan(alpha / 2.0);
    }

    protected override bool WorldDraw(WorldDraw draw)
    {
        try
        {
            List<V2> pts = new(_points);
            List<double> bulges = new(_bulges);
            if (V2.Dist(pts[^1], Current) > 1e-6)
            {
                pts.Add(Current);
                bulges.Add(CurrentBulge);
            }

            if (pts.Count < 2)
                return true;

            pts = Capture(pts, _existing, _capture);
            GPath path = PathOf(pts, bulges);
            if (path.IsEmpty)
                return true;

            using (AcadDb.Polyline axis = ViarioStore.ToPolyline(path.Segs, closed: false))
            {
                axis.ColorIndex = 1;
                draw.Geometry.Draw(axis);
            }

            List<AxisInput> axes = new(PreviewSubset(path));
            axes.Add(new AxisInput("__NOVO__", path, _section));
            ViarioResult result = ViarioEngine.Compute(axes, _engine);
            ViarioPreview.DrawChanges(draw, result, _existingKeys);

            double size = Math.Max(_section.Pista * 0.25, 0.5);
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

    private IEnumerable<AxisInput> PreviewSubset(GPath path)
    {
        if (_existing.Count <= 150)
            return _existing;

        Box2 box = path.Bounds().Inflate(200.0);
        return _existing.Where(a => a.Path.Bounds().Intersects(box));
    }

    public static GPath PathOf(List<V2> pts, List<double> bulges)
    {
        List<Seg> segs = new();
        for (int i = 1; i < pts.Count; i++)
            segs.Add(new Seg(pts[i - 1], pts[i], i - 1 < bulges.Count ? bulges[i - 1] : 0.0));
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

/// <summary>
/// Jig de valor (raio, comprimento...): a distancia do mouse ao ponto base e o
/// valor; a rede e recalculada a cada movimento e o que muda aparece na hora.
/// </summary>
internal sealed class ValueJig : DrawJig
{
    private readonly string _label;
    private readonly HashSet<string> _existingKeys;
    private readonly V2 _base;
    private readonly Func<double, (List<AxisInput> Axes, EngineSettings Settings)> _apply;
    private readonly double _initial;

    public ValueJig(string label, HashSet<string> existingKeys, V2 basePoint, double current,
        Func<double, (List<AxisInput>, EngineSettings)> apply)
    {
        _label = label;
        _existingKeys = existingKeys;
        _base = basePoint;
        _apply = apply;
        _initial = current;
        Value = current;
    }

    public double Value { get; private set; }
    public bool ToDefault { get; private set; }

    protected override SamplerStatus Sampler(JigPrompts prompts)
    {
        JigPromptDistanceOptions options = new(
            "\n" + _label + " <" + _initial.ToString("0.##", CultureInfo.InvariantCulture) + "> (arraste ou digite) ou [Padrao]: ")
        {
            BasePoint = new Point3d(_base.X, _base.Y, 0),
            UseBasePoint = true,
            UserInputControls = UserInputControls.Accept3dCoordinates |
                                UserInputControls.NullResponseAccepted |
                                UserInputControls.NoNegativeResponseAccepted,
            Cursor = CursorType.RubberBand
        };
        options.Keywords.Add("Padrao");

        PromptDoubleResult result = prompts.AcquireDistance(options);

        switch (result.Status)
        {
            case PromptStatus.Keyword:
                ToDefault = true;
                return SamplerStatus.Cancel;
            case PromptStatus.None:
                return SamplerStatus.Cancel;
            case PromptStatus.OK:
                break;
            default:
                return SamplerStatus.Cancel;
        }

        if (Math.Abs(result.Value - Value) < 1e-6)
            return SamplerStatus.NoChange;

        Value = result.Value;
        return SamplerStatus.OK;
    }

    protected override bool WorldDraw(WorldDraw draw)
    {
        try
        {
            (List<AxisInput> axes, EngineSettings settings) = _apply(Value);
            ViarioResult result = ViarioEngine.Compute(axes, settings);
            ViarioPreview.DrawChanges(draw, result, _existingKeys);
        }
        catch
        {
            // Preview nunca derruba o comando.
        }

        return true;
    }
}
