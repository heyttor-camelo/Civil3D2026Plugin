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
/// Hachura do pavimento (C3DVIARIOHACHURA): uma HATCH por contorno externo, na
/// layer VIARIO-PAVIMENTO, com o contorno do pavimento e os seus furos (quadras,
/// canteiros, ilhas) calculados pelo <see cref="ViarioHatch"/>. Acompanha a rede: a cada
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

    /// <summary>Hachura do modulo no desenho: bloco da grade e assinaturas gravadas nela.</summary>
    private readonly record struct Existing(AcadDb.ObjectId Id, string Tile, string Geometry, string Style, int Loops, int InTile);

    private static List<Existing> Find(AcadDb.Database db)
    {
        List<Existing> found = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (!id.ObjectClass.DxfName.Equals("HATCH", StringComparison.OrdinalIgnoreCase))
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Hatch h || ViarioStore.ReadValues(h) is not { } v || v[0] != TagHatch)
                continue;
            // Hachuras antigas (sem bloco) ficam com bloco vazio e sao sempre refeitas.
            found.Add(new Existing(id, v.Count > 2 ? v[2] : "", v.Count > 3 ? v[3] : "", v.Count > 4 ? v[4] : "",
                v.Count > 1 && int.TryParse(v[1], out int n) ? n : 0, v.Count > 5 && int.TryParse(v[5], out int t) ? t : -1));
        }

        return found;
    }

    /// <summary>Estilo gravado na hachura: muda o padrao sem refazer o contorno.</summary>
    private static string StyleKey(ViarioStore.Settings s) => s.HachuraSolida
        ? FormattableString.Invariant($"SOLID|{Math.Clamp(s.HachuraTransparencia, 0, 90)}")
        : FormattableString.Invariant($"{PatternName(s)}|{ScaleOf(s):0.######}|{s.HachuraAngulo:0.######}");

    private static string PatternName(ViarioStore.Settings s) =>
        s.HachuraSolida ? "SOLID" : (string.IsNullOrWhiteSpace(s.HachuraPadrao) ? "ANSI31" : s.HachuraPadrao);

    private static double ScaleOf(ViarioStore.Settings s) => s.HachuraEscala > 0 ? s.HachuraEscala : 20.0;

    private static void ApplyStyle(AcadDb.Hatch hatch, ViarioStore.Settings settings)
    {
        bool solid = settings.HachuraSolida;
        if (!solid)
        {
            hatch.PatternScale = ScaleOf(settings);
            hatch.PatternAngle = settings.HachuraAngulo * Math.PI / 180.0;
        }

        hatch.SetHatchPattern(AcadDb.HatchPatternType.PreDefined, PatternName(settings));
        hatch.Transparency = solid
            ? new AcColors.Transparency((byte)Math.Round(255.0 * (100 - Math.Clamp(settings.HachuraTransparencia, 0, 90)) / 100.0))
            : new AcColors.Transparency(AcColors.TransparencyMethod.ByLayer);
    }

    /// <summary>
    /// Gera/atualiza a hachura. Devolve o numero de lacos (ou -1 se nao existe e create=false).
    /// A hachura e recortada em blocos (Config: HachuraBloco) e so os blocos cujo contorno
    /// mudou sao refeitos; os demais so trocam o estilo, se ele mudou. rebuild refaz tudo.
    /// </summary>
    public static int Sync(Document doc, IReadOnlyList<AxisInput> axes, ViarioResult result, bool create, List<string>? messages = null,
        bool rebuild = false)
    {
        AcadDb.Database db = doc.Database;
        List<Existing> existing = Find(db);
        if (existing.Count == 0 && !create)
            return -1;

        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        ViarioHatch.Plan plan = ViarioHatch.Build(axes, result, settings.HachuraBloco);
        messages?.AddRange(plan.Warnings);
        string style = StyleKey(settings);

        Dictionary<string, (string Geometry, List<ViarioHatch.Loop> Loops)> wanted = new(StringComparer.Ordinal);
        foreach (IGrouping<(int X, int Y), ViarioHatch.Loop> tile in plan.ByTile())
        {
            List<ViarioHatch.Loop> inTile = tile.ToList();
            wanted[ViarioHatch.TileKey(tile.Key)] = (ViarioHatch.Signature(inTile), inTile);
        }

        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        ViarioStore.EnsureRegApp(tr, db);
        ViarioStore.EnsureLayer(tr, db, Layer, 8);

        // Blocos iguais ficam (com o estilo atualizado); os demais sao apagados e refeitos.
        HashSet<string> keep = new(StringComparer.Ordinal);
        int loops = 0;
        if (!rebuild)
        {
            foreach (IGrouping<string, Existing> group in existing.GroupBy(e => e.Tile))
            {
                if (group.Key.Length > 0 && wanted.TryGetValue(group.Key, out var want) && group.All(e => e.Geometry == want.Geometry && e.InTile == group.Count()))
                    keep.Add(group.Key);
            }
        }

        foreach (Existing e in existing)
        {
            if (e.Id.IsErased)
                continue;
            if (!keep.Contains(e.Tile))
            {
                tr.GetObject(e.Id, AcadDb.OpenMode.ForWrite).Erase();
                continue;
            }

            loops += e.Loops;
            if (e.Style == style)
                continue;
            AcadDb.Hatch h = (AcadDb.Hatch)tr.GetObject(e.Id, AcadDb.OpenMode.ForWrite);
            ApplyStyle(h, settings);
            h.EvaluateHatch(true);
            Tag(h, e, style);
        }

        if (plan.Loops.Count == 0)
        {
            tr.Commit();
            return 0;
        }

        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

        // Uma HATCH por contorno externo, com os seus furos. Num unico objeto com
        // varios "externos" o AutoCAD se perde na paridade quando dois contornos se
        // tocam; separados, um laco ruim derruba so a sua regiao, e a area de cada
        // hachura pode ser conferida com a esperada.
        foreach ((string tileKey, (string geometry, List<ViarioHatch.Loop> tileLoops)) in wanted)
        {
            if (keep.Contains(tileKey))
                continue;

            List<(AcadDb.Hatch Hatch, int Loops)> made = new();
            foreach (ViarioHatch.Loop outer in tileLoops.Where(l => l.Outer))
            {
                AcadDb.Hatch hatch = new();
                hatch.SetDatabaseDefaults(db);
                hatch.Layer = Layer;
                hatch.Normal = Vector3d.ZAxis;
                hatch.Elevation = 0.0;
                ms.AppendEntity(hatch);
                tr.AddNewlyCreatedDBObject(hatch, true);
                ApplyStyle(hatch, settings);
                hatch.Associative = false;
                hatch.HatchStyle = AcadDb.HatchStyle.Normal;

                int added = 0;
                if (AppendLoop(hatch, outer, AcadDb.HatchLoopTypes.External, messages))
                    added++;
                foreach (ViarioHatch.Loop hole in outer.Holes)
                {
                    if (AppendLoop(hatch, hole, AcadDb.HatchLoopTypes.Default, messages))
                        added++;
                }

                if (added == 0)
                {
                    hatch.Erase();
                    continue;
                }

                try
                {
                    hatch.EvaluateHatch(true);
                    double expected = ViarioHatch.NetArea(outer);
                    double got = hatch.Area;
                    if (expected > 1.0 && Math.Abs(got - expected) > Math.Max(1.0, 0.01 * expected))
                    {
                        string where = outer.Crossing is V2 x ? $"perto de {x}" : $"com inicio em {outer.Segs[0].A}";
                        messages?.Add($"Hachura {where}: area {got:0.#} m2, esperada {expected:0.#} m2 (contorno com problema; confira as faces de meio-fio nesse trecho).");
                    }
                }
                catch (System.Exception ex)
                {
                    messages?.Add($"Hachura com inicio em {outer.Segs[0].A} nao avaliada: {ex.Message}");
                }

                made.Add((hatch, added));
                loops += added;
            }

            // Cada hachura sabe quantas o bloco tem: uma apagada a mao faz o bloco ser refeito.
            foreach ((AcadDb.Hatch hatch, int added) in made)
                ViarioStore.SetTag(hatch, TagHatch, added.ToString(CultureInfo.InvariantCulture), tileKey, geometry, style, made.Count.ToString(CultureInfo.InvariantCulture));
        }

        tr.Commit();
        return loops;
    }

    private static void Tag(AcadDb.Hatch h, Existing e, string style) =>
        ViarioStore.SetTag(h, TagHatch, e.Loops.ToString(CultureInfo.InvariantCulture), e.Tile, e.Geometry, style, e.InTile.ToString(CultureInfo.InvariantCulture));

    /// <summary>Troca so o estilo das hachuras existentes (padrao, escala, solido), sem refazer o contorno.</summary>
    public static int Restyle(Document doc)
    {
        AcadDb.Database db = doc.Database;
        List<Existing> existing = Find(db);
        if (existing.Count == 0)
            return 0;
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        string style = StyleKey(settings);
        int count = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (Existing e in existing.Where(e => !e.Id.IsErased && e.Style != style))
        {
            AcadDb.Hatch h = (AcadDb.Hatch)tr.GetObject(e.Id, AcadDb.OpenMode.ForWrite);
            ApplyStyle(h, settings);
            h.EvaluateHatch(true);
            Tag(h, e, style);
            count++;
        }

        tr.Commit();
        return count;
    }

    private static bool AppendLoop(AcadDb.Hatch hatch, ViarioHatch.Loop loop, AcadDb.HatchLoopTypes type, List<string>? messages)
    {
        Point2dCollection pts = new();
        DoubleCollection bulges = new();
        foreach (Seg sg in loop.Segs)
        {
            if (sg.Length < 1e-6)
                continue;
            pts.Add(new Point2d(sg.A.X, sg.A.Y));
            bulges.Add(sg.IsArc ? sg.Bulge : 0.0);
        }

        if (pts.Count < 3)
            return false;

        try
        {
            hatch.AppendLoop(AcadDb.HatchLoopTypes.Polyline | type, pts, bulges);
            return true;
        }
        catch (System.Exception ex)
        {
            messages?.Add($"Laco da hachura ignorado perto de {loop.Segs[0].A}: {ex.Message}");
            return false;
        }
    }

    public static bool Remove(Document doc)
    {
        AcadDb.Database db = doc.Database;
        List<Existing> found = Find(db);
        if (found.Count == 0)
            return false;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (Existing e in found)
        {
            if (!e.Id.IsErased)
                tr.GetObject(e.Id, AcadDb.OpenMode.ForWrite).Erase();
        }

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
            $"\nHachura do pavimento [Gerar/Remover/PAdrao/Escala/SOlido/Blocos/Ocultar/Mostrar/PAInel] <Gerar> " +
            $"(atual: {(settings.HachuraSolida ? $"SOLID {settings.HachuraTransparencia}% transparente" : $"{settings.HachuraPadrao} escala {settings.HachuraEscala:0.##} angulo {settings.HachuraAngulo:0.#}")}, " +
            $"{(settings.HachuraBloco > 0 ? $"blocos de {settings.HachuraBloco:0.#} m" : "sem blocos")}): ",
            "Gerar Remover PAdrao Escala SOlido Blocos Ocultar Mostrar PAInel")
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
            case "Blocos":
            {
                PromptDoubleOptions bo = new($"\nLado dos blocos da hachura em m (0 = sem recorte; minimo {ViarioHatch.MinTile:0}) <{settings.HachuraBloco:0.#}>: ")
                {
                    AllowNegative = false, AllowZero = true, AllowNone = true
                };
                PromptDoubleResult br = ed.GetDouble(bo);
                if (br.Status == PromptStatus.OK)
                    settings.HachuraBloco = br.Value <= 0 ? 0 : Math.Max(br.Value, ViarioHatch.MinTile);
                else if (br.Status != PromptStatus.None)
                    return;
                Save(db, settings);
                break;
            }
        }

        // So o estilo mudou e a hachura ja existe: troca o padrao sem recalcular o contorno.
        if (exists && choice is "PAdrao" or "Escala" or "SOlido")
        {
            int restyled = Restyle(doc);
            ed.WriteMessage($"\n  Estilo aplicado em {restyled} hachura(s); contorno mantido.");
            return;
        }

        (List<AxisInput> axes, ViarioResult result, _) = ViarioSync.ComputeCurrent(db);
        List<string> messages = new();
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        int loops = Sync(doc, axes, result, create: true, messages, rebuild: choice == "Blocos");
        int hatches = Find(db).Count;
        ed.WriteMessage($"\n  Hachura do pavimento {(exists ? "atualizada" : "criada")}: {hatches} hachura(s), {loops} laco(s) (contorno + furos) na layer {Layer}" +
                        $"{(settings.HachuraBloco > 0 ? $", em blocos de {settings.HachuraBloco:0.#} m" : "")}; acompanha a rede ({clock.Elapsed.TotalSeconds:0.0} s).");
        if (ViarioSync.IsPending(db))
            ed.WriteMessage("\n  A rede esta pendente (atualizacao manual): a hachura segue os eixos atuais; \"Atualizar rede\" refaz as linhas.");
        foreach (string m in messages.Distinct().Take(12))
            ed.WriteMessage("\n  [HACHURA] " + m);
        if (messages.Count == 0)
            ed.WriteMessage("\n  Contornos fechados e areas conferidas: sem problemas de boundary.");
    }

    private static void Save(AcadDb.Database db, ViarioStore.Settings settings)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        ViarioStore.SaveSettings(tr, db, settings);
        tr.Commit();
    }
}
