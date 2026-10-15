using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcColors = Autodesk.AutoCAD.Colors;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Hachura do pavimento (C3DVIARIOHACHURA): uma HATCH unica, na layer
/// VIARIO-PAVIMENTO, com o contorno do pavimento e os furos (quadras, canteiros,
/// ilhas) calculados pelo <see cref="ViarioHatch"/>. Acompanha a rede: a cada
/// regeneracao e refeita enquanto existir. Padrao, escala e "solido com
/// transparencia" ficam gravados no desenho (Config).
/// </summary>
internal static class ViarioHatchSync
{
    public const string Layer = "VIARIO-PAVIMENTO";
    public const string TagHatch = "HACHURA";

    public static readonly string[] Patterns = { "ANSI31", "ANSI32", "ANSI37", "AR-CONC", "AR-SAND", "DOTS", "NET", "GRAVEL" };
    public static readonly double[] Scales = { 0.5, 1, 2, 5, 10, 20, 50, 100, 200 };

    public static bool Exists(AcadDb.Database db) => Find(db).Count > 0;

    private static List<AcadDb.ObjectId> Find(AcadDb.Database db)
    {
        List<AcadDb.ObjectId> ids = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (!id.ObjectClass.DxfName.Equals("HATCH", StringComparison.OrdinalIgnoreCase))
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.Hatch h && ViarioStore.ReadTag(h, out _) == TagHatch)
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>Gera/atualiza a hachura. Devolve o numero de lacos (ou -1 se nao existe e create=false).</summary>
    public static int Sync(Document doc, IReadOnlyList<AxisInput> axes, ViarioResult result, bool create, List<string>? messages = null)
    {
        AcadDb.Database db = doc.Database;
        List<AcadDb.ObjectId> existing = Find(db);
        if (existing.Count == 0 && !create)
            return -1;

        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        ViarioHatch.Plan plan = ViarioHatch.Build(axes, result);
        messages?.AddRange(plan.Warnings);

        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        ViarioStore.EnsureRegApp(tr, db);
        ViarioStore.EnsureLayer(tr, db, Layer, 8);

        foreach (AcadDb.ObjectId id in existing)
        {
            if (!id.IsErased)
                tr.GetObject(id, AcadDb.OpenMode.ForWrite).Erase();
        }

        if (plan.Loops.Count == 0)
        {
            tr.Commit();
            return 0;
        }

        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

        AcadDb.Hatch hatch = new();
        hatch.SetDatabaseDefaults(db);
        hatch.Layer = Layer;
        hatch.Normal = Vector3d.ZAxis;
        hatch.Elevation = 0.0;
        ms.AppendEntity(hatch);
        tr.AddNewlyCreatedDBObject(hatch, true);

        bool solid = settings.HachuraSolida;
        if (!solid)
        {
            hatch.PatternScale = settings.HachuraEscala > 0 ? settings.HachuraEscala : 20.0;
            hatch.PatternAngle = settings.HachuraAngulo * Math.PI / 180.0;
        }

        hatch.SetHatchPattern(AcadDb.HatchPatternType.PreDefined, solid ? "SOLID" : (string.IsNullOrWhiteSpace(settings.HachuraPadrao) ? "ANSI31" : settings.HachuraPadrao));
        hatch.Associative = false;
        hatch.HatchStyle = AcadDb.HatchStyle.Normal;

        int loops = 0;
        foreach (ViarioHatch.Loop loop in plan.Loops)
        {
            Point2dCollection pts = new();
            AcadDb.DoubleCollection bulges = new();
            foreach (Seg sg in loop.Segs)
            {
                if (sg.Length < 1e-6)
                    continue;
                pts.Add(new Point2d(sg.A.X, sg.A.Y));
                bulges.Add(sg.IsArc ? sg.Bulge : 0.0);
            }

            if (pts.Count < 3)
                continue;

            try
            {
                hatch.AppendLoop(AcadDb.HatchLoopTypes.Polyline | (loop.Outer ? AcadDb.HatchLoopTypes.External : AcadDb.HatchLoopTypes.Default), pts, bulges);
                loops++;
            }
            catch (System.Exception ex)
            {
                messages?.Add($"Laco da hachura ignorado perto de {loop.Segs[0].A}: {ex.Message}");
            }
        }

        try
        {
            hatch.EvaluateHatch(true);
        }
        catch (System.Exception ex)
        {
            messages?.Add("Hachura nao avaliada: " + ex.Message);
        }

        if (solid)
            hatch.Transparency = new AcColors.Transparency((byte)Math.Round(255.0 * (100 - Math.Clamp(settings.HachuraTransparencia, 0, 90)) / 100.0));

        ViarioStore.SetTag(hatch, TagHatch, loops.ToString(CultureInfo.InvariantCulture));
        tr.Commit();
        return loops;
    }

    public static bool Remove(Document doc)
    {
        AcadDb.Database db = doc.Database;
        List<AcadDb.ObjectId> ids = Find(db);
        if (ids.Count == 0)
            return false;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (AcadDb.ObjectId id in ids)
            tr.GetObject(id, AcadDb.OpenMode.ForWrite).Erase();
        tr.Commit();
        return true;
    }

    public static void SetVisible(Document doc, bool visible)
    {
        AcadDb.Database db = doc.Database;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.LayerTable table = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        if (table.Has(Layer))
        {
            AcadDb.LayerTableRecord layer = (AcadDb.LayerTableRecord)tr.GetObject(table[Layer], AcadDb.OpenMode.ForWrite);
            if (layer.ObjectId != db.Clayer || visible)
                layer.IsOff = !visible;
        }

        tr.Commit();
        doc.Editor.Regen();
    }

    public static bool IsVisible(AcadDb.Database db)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.LayerTable table = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        if (!table.Has(Layer))
            return true;
        return tr.GetObject(table[Layer], AcadDb.OpenMode.ForRead) is AcadDb.LayerTableRecord l && !l.IsOff;
    }

    public static void Command(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        bool exists = Exists(db);
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);

        PromptKeywordOptions options = new(
            $"\nHachura do pavimento [Gerar/Remover/PAdrao/Escala/SOlido/Ocultar/Mostrar/PAInel] <Gerar> " +
            $"(atual: {(settings.HachuraSolida ? $"SOLID {settings.HachuraTransparencia}% transparente" : $"{settings.HachuraPadrao} escala {settings.HachuraEscala:0.##} angulo {settings.HachuraAngulo:0.#}")}): ",
            "Gerar Remover PAdrao Escala SOlido Ocultar Mostrar PAInel")
        {
            AllowNone = true
        };

        PromptResult r = ed.GetKeywords(options);
        if (r.Status == PromptStatus.Cancel || r.Status == PromptStatus.Error)
            return;

        string choice = r.Status == PromptStatus.OK ? r.StringResult : "Gerar";
        switch (choice)
        {
            case "Remover":
                ed.WriteMessage(Remove(doc) ? "\n  Hachura removida." : "\n  Nao ha hachura do viario.");
                return;
            case "Ocultar":
            case "Mostrar":
                SetVisible(doc, choice == "Mostrar");
                ed.WriteMessage(choice == "Mostrar" ? "\n  Hachura visivel." : "\n  Hachura oculta (layer VIARIO-PAVIMENTO desligada).");
                return;
            case "PAInel":
                ViarioPanel.ShowPanel("CONFIG");
                return;
            case "PAdrao":
            {
                ed.WriteMessage("\n  Padroes sugeridos: " + string.Join(", ", Patterns) + " (ou qualquer padrao do acad.pat).");
                PromptStringOptions so = new("\nPadrao da hachura <" + settings.HachuraPadrao + ">: ");
                PromptResult sr = ed.GetString(so);
                if (sr.Status != PromptStatus.OK)
                    return;
                if (sr.StringResult.Trim().Length > 0)
                    settings.HachuraPadrao = sr.StringResult.Trim().ToUpperInvariant();
                settings.HachuraSolida = false;
                Save(db, settings);
                break;
            }
            case "Escala":
            {
                PromptDoubleOptions eo = new("\nEscala do padrao <" + settings.HachuraEscala.ToString("0.##", CultureInfo.InvariantCulture) + ">: ")
                {
                    AllowNegative = false, AllowZero = false, AllowNone = true
                };
                PromptDoubleResult er = ed.GetDouble(eo);
                if (er.Status == PromptStatus.OK)
                    settings.HachuraEscala = er.Value;
                else if (er.Status != PromptStatus.None)
                    return;
                PromptDoubleOptions ao = new("\nAngulo do padrao (graus) <" + settings.HachuraAngulo.ToString("0.#", CultureInfo.InvariantCulture) + ">: ") { AllowNone = true };
                PromptDoubleResult ar = ed.GetDouble(ao);
                if (ar.Status == PromptStatus.OK)
                    settings.HachuraAngulo = ar.Value;
                Save(db, settings);
                break;
            }
            case "SOlido":
            {
                PromptIntegerOptions to = new("\nSolido com transparencia de (%) <" + settings.HachuraTransparencia + "> (0 = padrao em vez de solido): ")
                {
                    AllowNegative = false, AllowNone = true, LowerLimit = 0, UpperLimit = 90
                };
                PromptIntegerResult tres = ed.GetInteger(to);
                if (tres.Status == PromptStatus.OK)
                {
                    settings.HachuraSolida = tres.Value > 0;
                    if (tres.Value > 0)
                        settings.HachuraTransparencia = tres.Value;
                }
                else if (tres.Status != PromptStatus.None)
                {
                    return;
                }
                else
                {
                    settings.HachuraSolida = true;
                }

                Save(db, settings);
                break;
            }
        }

        (List<AxisInput> axes, ViarioResult result, _) = ViarioSync.ComputeCurrent(db);
        List<string> messages = new();
        int loops = Sync(doc, axes, result, create: true, messages);
        ed.WriteMessage($"\n  Hachura do pavimento {(exists ? "atualizada" : "criada")}: {loops} laco(s) (contorno + furos) na layer {Layer}; acompanha a rede.");
        foreach (string m in messages.Distinct().Take(8))
            ed.WriteMessage("\n  [HACHURA] " + m);
    }

    private static void Save(AcadDb.Database db, ViarioStore.Settings settings)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        ViarioStore.SaveSettings(tr, db, settings);
        tr.Commit();
    }
}
