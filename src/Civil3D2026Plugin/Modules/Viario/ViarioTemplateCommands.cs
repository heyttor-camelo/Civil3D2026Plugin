using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>Insercao de modelos de intersecao (janela + ponto + rotacao com preview).</summary>
internal static class ViarioTemplateCommands
{
    public static void InsertTemplate(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        List<RoadSection> sections = ViarioStore.LoadSections(db);
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        RoadSection current = sections.FirstOrDefault(s => s.Id == settings.CurrentSectionId) ?? sections[0];

        ViarioTemplateWindow window = new(sections, current);
        bool? ok = AcApp.ShowModalWindow(window);
        if (ok != true || window.Selected is null)
            return;

        TemplateSections chosen = window.Sections;
        TemplateBuild build = window.Selected.Build(chosen);
        foreach (string note in build.Notes)
            ed.WriteMessage("\n  " + note);

        (List<AxisInput> existing, HashSet<string> keys) = ViarioSync.CurrentAxes(db);
        EngineSettings engine = ViarioSync.EngineSettingsFor(db);

        // 1) Ponto da intersecao.
        TemplateJig jig = new(build, chosen, existing, keys, engine);
        PromptResult r1 = ed.Drag(jig);
        if (r1.Status != PromptStatus.OK)
            return;

        // 2) Rotacao.
        jig.Phase = 1;
        PromptResult r2 = ed.Drag(jig);
        if (r2.Status != PromptStatus.OK && r2.Status != PromptStatus.None)
            return;

        V2 origin = jig.Origin;
        double angle = jig.Angle;

        // 3) Cria os eixos.
        List<string> handles = new();
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.EnsureRegApp(tr, db);
            ViarioStore.EnsureLayers(tr, db);
            foreach (RoadSection s in new[] { chosen.Principal, chosen.Secundaria, chosen.Anel })
            {
                if (sections.Any(x => x.Id == s.Id))
                    ViarioStore.SaveSection(tr, db, s);
            }

            AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
            AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

            foreach (TemplateAxis local in build.Axes)
            {
                TemplateAxis axis = local.Transform(origin, angle);
                AcadDb.Curve entity;

                if (axis.IsCircle)
                {
                    entity = new AcadDb.Circle(new Point3d(axis.Center.X, axis.Center.Y, 0), Vector3d.ZAxis, axis.Radius);
                }
                else
                {
                    AcadDb.Polyline pl = new();
                    for (int i = 0; i < axis.Points.Count; i++)
                        pl.AddVertexAt(i, new Point2d(axis.Points[i].X, axis.Points[i].Y), i < axis.Bulges.Count ? axis.Bulges[i] : 0.0, 0, 0);
                    entity = pl;
                }

                entity.SetDatabaseDefaults(db);
                entity.Layer = ViarioStore.LayerAxis;
                ms.AppendEntity(entity);
                tr.AddNewlyCreatedDBObject(entity, true);
                ViarioStore.SetAxis(entity, chosen.For(axis.Role).Id, null, axis.Events);
                handles.Add(entity.Handle.ToString() ?? string.Empty);
            }

            tr.Commit();
        }

        // 4) Faixas livres: associa cada uma a esquina real mais proxima.
        if (build.Slips.Count > 0)
        {
            (_, ViarioResult placed, _) = ViarioSync.ComputeCurrent(db);
            Dictionary<string, SlipLane> slips = ViarioStore.LoadSlipLanes(db);
            foreach (TemplateSlip slip in build.Slips)
            {
                string? key = ViarioTemplates.NearestCorner(placed, ViarioTemplates.Place(slip.Hint, origin, angle), 40.0);
                if (key is not null)
                    slips[key] = slip.Slip;
            }

            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            ViarioStore.SaveSlipLanes(tr, db, slips);
            tr.Commit();
        }

        ViarioResult? result = ViarioSync.Regenerate(doc, quiet: true);
        ed.WriteMessage($"\n  Modelo inserido: {window.Selected.Name} ({handles.Count} eixo(s)).");
        if (result is not null)
            ViarioSync.WriteWarnings(doc, result);
    }
}

/// <summary>Jig do modelo: fase 0 posiciona, fase 1 gira; a rede e recalculada ao vivo.</summary>
internal sealed class TemplateJig : DrawJig
{
    private readonly TemplateBuild _build;
    private readonly TemplateSections _sections;
    private readonly List<AxisInput> _existing;
    private readonly HashSet<string> _keys;
    private readonly EngineSettings _engine;

    public TemplateJig(TemplateBuild build, TemplateSections sections, List<AxisInput> existing, HashSet<string> keys, EngineSettings engine)
    {
        _build = build;
        _sections = sections;
        _existing = existing;
        _keys = keys;
        _engine = engine;
    }

    public int Phase { get; set; }
    public V2 Origin { get; private set; }
    public double Angle { get; private set; }

    protected override SamplerStatus Sampler(JigPrompts prompts)
    {
        if (Phase == 0)
        {
            JigPromptPointOptions options = new("\nPonto da intersecao: ")
            {
                UserInputControls = UserInputControls.Accept3dCoordinates
            };

            PromptPointResult result = prompts.AcquirePoint(options);
            if (result.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            V2 p = new(result.Value.X, result.Value.Y);
            if (V2.Dist(p, Origin) < 1e-6)
                return SamplerStatus.NoChange;
            Origin = p;
            return SamplerStatus.OK;
        }

        JigPromptAngleOptions angleOptions = new("\nRotacao do modelo <0>: ")
        {
            BasePoint = new Point3d(Origin.X, Origin.Y, 0),
            UseBasePoint = true,
            UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.NullResponseAccepted,
            Cursor = CursorType.RubberBand
        };

        PromptDoubleResult angle = prompts.AcquireAngle(angleOptions);
        if (angle.Status == PromptStatus.None)
            return SamplerStatus.OK;
        if (angle.Status != PromptStatus.OK)
            return SamplerStatus.Cancel;

        if (Math.Abs(angle.Value - Angle) < 1e-9)
            return SamplerStatus.NoChange;
        Angle = angle.Value;
        return SamplerStatus.OK;
    }

    protected override bool WorldDraw(WorldDraw draw)
    {
        try
        {
            ViarioResult result = ViarioTemplates.Compute(_build, _sections, PreviewSubset().ToList(), _engine, Origin, Angle, out List<AxisInput> placed);

            foreach (AxisInput axis in placed)
            {
                using AcadDb.Polyline pl = ViarioStore.ToPolyline(axis.Path.Segs, axis.Path.Closed);
                pl.ColorIndex = 1;
                draw.Geometry.Draw(pl);
            }

            ViarioPreview.DrawChanges(draw, result, _keys);
        }
        catch
        {
            // Preview nunca derruba o comando.
        }

        return true;
    }

    private IEnumerable<AxisInput> PreviewSubset()
    {
        if (_existing.Count <= 150)
            return _existing;

        Box2 box = Box2.Empty.Add(Origin).Inflate(300.0);
        return _existing.Where(a => a.Path.Bounds().Intersects(box));
    }
}
