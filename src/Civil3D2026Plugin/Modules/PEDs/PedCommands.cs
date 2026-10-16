using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>
/// Locacao de PEDs (pontos de embarque e desembarque) a partir da planilha de proposta
/// e do KMZ com a localizacao:
///
/// - PEDIMPORTAR: le a planilha (.xlsx) e o KMZ, grava os dados no DWG, extrai as
///   imagens para PEDs_img e insere um bloco PED em cada ponto.
/// - PEDATUALIZAR: rele a planilha (revisao nova) sem mexer na posicao dos blocos.
/// - PEDLOCAR: insere ou move um PED clicando (para quem nao esta no KMZ).
/// - PEDFICHA: paleta que mostra a ficha do PED selecionado no desenho.
/// </summary>
public sealed class PedCommands
{
    [CommandMethod(C3DCommands.Peds.Ficha, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void Ficha() => PedPanel.ShowPanel();

    [CommandMethod(C3DCommands.Peds.Importar, CommandFlags.Modal)]
    public void Importar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        PedMeta? meta = PedStore.LoadMeta(doc.Database);

        string? xlsx = AskFile(ed, "\nPlanilha de PEDs (.xlsx): ", "Planilha Excel (*.xlsx)|*.xlsx", meta?.XlsxPath);
        if (xlsx is null)
            return;

        string? kmz = null;
        PromptKeywordOptions kOpt = new("\nLocalizacao dos PEDs [Kmz/Nenhum] <Kmz>: ", "Kmz Nenhum") { AllowNone = true };
        PromptResult kRes = ed.GetKeywords(kOpt);
        if (kRes.Status == PromptStatus.Cancel)
            return;
        if (kRes.Status == PromptStatus.None || kRes.StringResult == "Kmz")
        {
            kmz = AskFile(ed, "\nArquivo KMZ/KML com os pontos: ", "Google Earth (*.kmz;*.kml)|*.kmz;*.kml", meta?.KmzPath);
            if (kmz is null)
                return;
        }

        PromptDoubleOptions sOpt = new($"\nEscala do bloco PED (8 x 3 na escala 1) <{(meta?.BlockScale ?? 1.0).ToString("0.###", CultureInfo.InvariantCulture)}>: ")
        {
            AllowNone = true,
            AllowZero = false,
            AllowNegative = false
        };
        PromptDoubleResult sRes = ed.GetDouble(sOpt);
        if (sRes.Status == PromptStatus.Cancel)
            return;
        double scale = sRes.Status == PromptStatus.OK ? sRes.Value : meta?.BlockScale ?? 1.0;

        bool reposition = false;
        int existing;
        using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            existing = PedStore.FindBlocks(tr, doc.Database).Count;
        if (existing > 0 && kmz is not null)
        {
            PromptKeywordOptions rOpt = new($"\nJa existem {existing} PED(s) no desenho [Manter/Reposicionar] <Manter>: ", "Manter Reposicionar") { AllowNone = true };
            PromptResult rRes = ed.GetKeywords(rOpt);
            if (rRes.Status == PromptStatus.Cancel)
                return;
            reposition = rRes.Status == PromptStatus.OK && rRes.StringResult == "Reposicionar";
        }

        if (Run(doc, xlsx, kmz, scale, reposition))
            PedPanel.ShowPanel();
    }

    [CommandMethod(C3DCommands.Peds.Atualizar, CommandFlags.Modal)]
    public void Atualizar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;

        PedMeta? meta = PedStore.LoadMeta(doc.Database);
        if (meta is null)
        {
            ed.WriteMessage($"\nEste desenho ainda nao tem PEDs importados. Use {C3DCommands.Peds.Importar}.");
            return;
        }

        string? xlsx = File.Exists(meta.XlsxPath) ? meta.XlsxPath : null;
        if (xlsx is null)
        {
            ed.WriteMessage($"\nA planilha gravada nao foi encontrada: {meta.XlsxPath}");
            xlsx = AskFile(ed, "\nPlanilha de PEDs (.xlsx): ", "Planilha Excel (*.xlsx)|*.xlsx", meta.XlsxPath);
            if (xlsx is null)
                return;
        }
        else
        {
            ed.WriteMessage($"\nRelendo {Path.GetFileName(xlsx)} (para trocar de arquivo use {C3DCommands.Peds.Importar}).");
        }

        string? kmz = File.Exists(meta.KmzPath) ? meta.KmzPath : null;
        if (Run(doc, xlsx, kmz, meta.BlockScale, reposition: false))
            PedPanel.RefreshIfOpen();
    }

    [CommandMethod(C3DCommands.Peds.Locar, CommandFlags.Modal)]
    public void Locar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        List<PedStored> peds = PedStore.LoadAll(db);
        if (peds.Count == 0)
        {
            ed.WriteMessage($"\nEste desenho ainda nao tem PEDs importados. Use {C3DCommands.Peds.Importar}.");
            return;
        }
        double scale = PedStore.LoadMeta(db)?.BlockScale ?? 1.0;

        while (true)
        {
            HashSet<string> placed;
            using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                placed = PedStore.FindBlocks(tr, db).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<string> pending = peds.Select(p => p.Code).Where(c => !placed.Contains(c)).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            if (pending.Count > 0)
                ed.WriteMessage($"\nSem bloco no desenho ({pending.Count}): {string.Join(", ", pending.Take(20))}{(pending.Count > 20 ? "..." : string.Empty)}");

            string def = pending.FirstOrDefault() ?? string.Empty;
            PromptStringOptions cOpt = new($"\nCodigo do PED para locar ou mover{(def.Length > 0 ? $" <{def}>" : string.Empty)}: ") { AllowSpaces = false };
            PromptResult cRes = ed.GetString(cOpt);
            if (cRes.Status != PromptStatus.OK)
                return;
            string typed = string.IsNullOrWhiteSpace(cRes.StringResult) ? def : cRes.StringResult.Trim();
            if (typed.Length == 0)
                return;

            PedStored? ped = peds.FirstOrDefault(p => string.Equals(p.Code, typed, StringComparison.OrdinalIgnoreCase));
            if (ped is null)
            {
                ed.WriteMessage($"\n{typed} nao esta na planilha importada.");
                continue;
            }

            PromptPointResult pRes = ed.GetPoint(new PromptPointOptions($"\nPonto do {ped.Code}: "));
            if (pRes.Status != PromptStatus.OK)
                return;
            Point3d pt = pRes.Value.TransformBy(ed.CurrentUserCoordinateSystem);

            AcadDb.ObjectId brId;
            using (doc.LockDocument())
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                PedStore.EnsureRegApp(tr, db);
                PedBlock.EnsureLayer(tr, db);
                Dictionary<string, List<AcadDb.ObjectId>> blocks = PedStore.FindBlocks(tr, db);
                if (blocks.TryGetValue(ped.Code, out List<AcadDb.ObjectId>? ids))
                {
                    brId = ids[0];
                    PedBlock.MoveTo((AcadDb.BlockReference)tr.GetObject(brId, AcadDb.OpenMode.ForWrite), pt);
                }
                else
                {
                    AcadDb.ObjectId blockId = PedBlock.EnsureBlock(tr, db, out _);
                    brId = PedBlock.Insert(tr, db, blockId, pt, scale, ped.Code);
                }
                tr.Commit();
            }

            PromptAngleOptions aOpt = new($"\nRotacao do {ped.Code} <manter>: ") { AllowNone = true, UseBasePoint = true, BasePoint = pRes.Value };
            PromptDoubleResult aRes = ed.GetAngle(aOpt);
            if (aRes.Status == PromptStatus.OK)
            {
                using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
                AcadDb.BlockReference br = (AcadDb.BlockReference)tr.GetObject(brId, AcadDb.OpenMode.ForWrite);
                double target = aRes.Value + UcsAngle(ed);
                br.TransformBy(Matrix3d.Rotation(target - br.Rotation, br.Normal, br.Position));
                tr.Commit();
            }
            else if (aRes.Status == PromptStatus.Cancel)
            {
                return;
            }

            ed.WriteMessage($"\n{ped.Code} locado.");
        }
    }

    // ------------------------------------------------------------------

    /// <summary>Importacao/atualizacao comum. Retorna false se nada foi gravado.</summary>
    private static bool Run(Document doc, string xlsx, string? kmz, double scale, bool reposition)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        // ---------- Planilha ----------
        PedSheet sheet;
        try
        {
            sheet = PedXlsxReader.Read(xlsx);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nNao foi possivel ler a planilha: {ex.Message}" +
                            "\n(Se ela estiver aberta no Excel com alteracoes nao salvas, salve antes.)");
            return false;
        }

        ed.WriteMessage($"\nPlanilha: aba \"{sheet.SheetName}\", {sheet.Records.Count} PED(s), {sheet.Columns.Count} coluna(s).");
        foreach (string w in sheet.Warnings)
            ed.WriteMessage("\n  Aviso: " + w);
        List<string> layout = PedFicha.CheckLayout(sheet.Columns);
        if (layout.Count > 0)
        {
            ed.WriteMessage("\n  Aviso: o layout da planilha mudou; a ficha pode mostrar campos trocados nas colunas:");
            foreach (string l in layout.Take(8))
                ed.WriteMessage("\n    " + l);
            ed.WriteMessage("\n  (A aba \"Todos os campos\" da ficha continua certa. Ajuste as letras em PedFicha.cs.)");
        }

        if (sheet.Records.Count == 0)
        {
            ed.WriteMessage("\nNenhum PED na planilha.");
            return false;
        }

        // ---------- KMZ ----------
        Dictionary<string, PedPoint> points = new(StringComparer.OrdinalIgnoreCase);
        PedGeo? geo = null;
        if (kmz is not null)
        {
            try
            {
                foreach (PedPoint p in PedKml.Read(kmz))
                    points[p.Name] = p;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nNao foi possivel ler o KMZ: {ex.Message}");
                return false;
            }

            List<string> semPonto = sheet.Records.Select(r => r.Code).Where(c => !points.ContainsKey(c)).ToList();
            List<string> foraDaPlanilha = points.Keys.Where(k => sheet.Records.All(r => !string.Equals(r.Code, k, StringComparison.OrdinalIgnoreCase))).ToList();
            ed.WriteMessage($"\nKMZ: {points.Count} ponto(s); {sheet.Records.Count - semPonto.Count} batem com a planilha.");
            if (semPonto.Count > 0)
                ed.WriteMessage($"\n  Sem ponto no KMZ (use {C3DCommands.Peds.Locar}): {string.Join(", ", semPonto)}");
            if (foraDaPlanilha.Count > 0)
                ed.WriteMessage($"\n  No KMZ mas nao na planilha (ignorados): {string.Join(", ", foraDaPlanilha)}");

            if (points.Count > 0)
            {
                geo = PedGeo.Create(doc, ed);
                if (geo is null)
                    return false;
                ed.WriteMessage($"\nConversao de coordenadas: {geo.Description}.");
            }
        }

        // ---------- Imagens ----------
        string baseDir = PedStore.DwgFolder(db);
        if (baseDir.Length == 0)
        {
            baseDir = Path.GetDirectoryName(Path.GetFullPath(xlsx)) ?? string.Empty;
            ed.WriteMessage("\nO desenho ainda nao foi salvo: as imagens vao para a pasta da planilha." +
                            $"\n(Depois de salvar o DWG, rode {C3DCommands.Peds.Atualizar} para leva-las para junto dele.)");
        }
        string imgFolder = Path.Combine(baseDir, PedImages.FolderName);

        List<string> imgErrors = new();
        Dictionary<string, Dictionary<string, string>> images;
        int totalImages = sheet.Records.Sum(r => r.ImageEntries.Count);
        try
        {
            using ProgressMeter pm = new();
            pm.SetLimit(Math.Max(1, sheet.Records.Count));
            pm.Start($"PEDs: extraindo {totalImages} imagem(ns)");
            images = PedImages.ExtractAll(xlsx, sheet.Records, imgFolder, imgErrors, _ =>
            {
                pm.MeterProgress();
                System.Windows.Forms.Application.DoEvents();
            });
            pm.Stop();
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nNao foi possivel gravar as imagens em {imgFolder}: {ex.Message}");
            images = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }

        int okImages = images.Values.Sum(d => d.Count);
        ed.WriteMessage($"\nImagens: {okImages} de {totalImages} gravada(s) em {imgFolder}");
        foreach (string e in imgErrors.Take(5))
            ed.WriteMessage("\n  Erro: " + e);

        // ---------- DWG ----------
        int inserted = 0, moved = 0, updated = 0, removed;
        List<string> withoutBlock = new();

        using (doc.LockDocument())
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            PedStore.SaveMeta(tr, db, new PedMeta
            {
                XlsxPath = Path.GetFullPath(xlsx),
                KmzPath = kmz is null ? string.Empty : Path.GetFullPath(kmz),
                ImageFolder = imgFolder,
                SheetName = sheet.SheetName,
                Title = sheet.Title,
                ImportedAt = DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                BlockScale = scale
            });
            PedStore.SaveColumns(tr, db, sheet.Columns);

            foreach (PedRecord rec in sheet.Records)
            {
                PedStored s = new() { Code = rec.Code };
                foreach ((string k, string v) in rec.Values)
                    s.Values[k] = v;
                if (images.TryGetValue(rec.Code, out Dictionary<string, string>? files))
                {
                    foreach ((string k, string v) in files)
                        s.Images[k] = v;
                }
                if (points.TryGetValue(rec.Code, out PedPoint? pt))
                {
                    s.Longitude = pt.Longitude;
                    s.Latitude = pt.Latitude;
                }
                PedStore.Save(tr, db, s);
            }

            removed = PedStore.RemoveMissing(tr, db, sheet.Records.Select(r => r.Code).ToList());

            PedStore.EnsureRegApp(tr, db);
            PedBlock.EnsureLayer(tr, db);
            AcadDb.ObjectId blockId = PedBlock.EnsureBlock(tr, db, out bool created);
            if (created)
                ed.WriteMessage($"\nBloco {PedBlock.BlockName} criado (layer {PedBlock.LayerName}).");

            Dictionary<string, List<AcadDb.ObjectId>> blocks = PedStore.FindBlocks(tr, db);

            foreach (PedRecord rec in sheet.Records)
            {
                bool hasPoint = points.TryGetValue(rec.Code, out PedPoint? pt) && geo is not null;
                Point3d pos = hasPoint ? geo!.ToDrawing(pt!.Longitude, pt.Latitude) : Point3d.Origin;

                if (blocks.TryGetValue(rec.Code, out List<AcadDb.ObjectId>? ids))
                {
                    for (int i = 0; i < ids.Count; i++)
                    {
                        AcadDb.BlockReference br = (AcadDb.BlockReference)tr.GetObject(ids[i], AcadDb.OpenMode.ForWrite);
                        if (i == 0 && reposition && hasPoint)
                        {
                            PedBlock.MoveTo(br, pos);
                            moved++;
                        }
                        PedBlock.UpdateAttributes(tr, db, br, rec.Code);
                    }
                    updated++;
                }
                else if (hasPoint)
                {
                    PedBlock.Insert(tr, db, blockId, pos, scale, rec.Code);
                    inserted++;
                }
                else
                {
                    withoutBlock.Add(rec.Code);
                }
            }

            tr.Commit();
        }

        ed.WriteMessage($"\nPEDs: {inserted} inserido(s), {updated} ja existente(s) atualizado(s)" +
                        (moved > 0 ? $", {moved} reposicionado(s)" : string.Empty) +
                        (removed > 0 ? $", {removed} removido(s) da base (nao estao mais na planilha; os blocos ficam sem ficha)" : string.Empty) + ".");
        if (withoutBlock.Count > 0)
            ed.WriteMessage($"\nSem bloco no desenho ({withoutBlock.Count}): {string.Join(", ", withoutBlock)} -> use {C3DCommands.Peds.Locar}.");
        ed.WriteMessage($"\nClique num PED com a ficha aberta ({C3DCommands.Peds.Ficha}) para ver os dados.");

        if (inserted > 0)
            ZoomToPeds(doc);
        return true;
    }

    private static string? AskFile(Editor ed, string message, string filter, string? previous)
    {
        PromptOpenFileOptions opt = new(message) { Filter = filter };
        if (!string.IsNullOrEmpty(previous))
        {
            try
            {
                string? dir = Path.GetDirectoryName(previous);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    opt.InitialDirectory = dir;
                opt.InitialFileName = Path.GetFileName(previous);
            }
            catch
            {
                // Caminho anterior invalido: abre na pasta padrao.
            }
        }

        PromptFileNameResult r = ed.GetFileNameForOpen(opt);
        return r.Status == PromptStatus.OK ? r.StringResult : null;
    }

    private static double UcsAngle(Editor ed)
    {
        Matrix3d ucs = ed.CurrentUserCoordinateSystem;
        Vector3d x = Vector3d.XAxis.TransformBy(ucs);
        return Math.Atan2(x.Y, x.X);
    }

    /// <summary>Zoom em todos os blocos PED (extensao dos pontos).</summary>
    internal static void ZoomToPeds(Document doc)
    {
        try
        {
            AcadDb.Database db = doc.Database;
            AcadDb.Extents3d? ext = null;
            using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (List<AcadDb.ObjectId> ids in PedStore.FindBlocks(tr, db).Values)
                {
                    foreach (AcadDb.ObjectId id in ids)
                    {
                        if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is AcadDb.Entity e)
                        {
                            try
                            {
                                AcadDb.Extents3d b = e.GeometricExtents;
                                if (ext is AcadDb.Extents3d cur)
                                {
                                    cur.AddExtents(b);
                                    ext = cur;
                                }
                                else
                                {
                                    ext = b;
                                }
                            }
                            catch
                            {
                                // Sem extensao.
                            }
                        }
                    }
                }
            }

            if (ext is AcadDb.Extents3d x)
                PedView.ZoomTo(doc, x.MinPoint, x.MaxPoint, margin: 0.1);
        }
        catch
        {
            // Zoom e conveniencia.
        }
    }
}
