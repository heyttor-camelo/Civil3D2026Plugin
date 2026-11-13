using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>
/// Apagar clicando no desenho (pedido do Heyvid em 13/11/2026): MFREBAIXOAPAGAR (clique na hachura ou
/// no meio-fio do rebaixo) e MFTRAVESSIAAPAGAR (clique num solido da lombofaixa). Mostra o que sai,
/// confirma e remove pelo mesmo caminho do painel. U desfaz.
/// </summary>
public sealed partial class MFRebaixoCommands
{
    [CommandMethod(C3DCommands.Corridor.MfRebaixoApagar, CommandFlags.Modal)]
    public void ApagarClicando()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        Database db = doc.Database;
        try
        {
            List<RebaixoInfo> items = MFRebaixoData.ReadAll(db);
            if (items.Count == 0)
            {
                ed.WriteMessage("\nNenhum rebaixo no desenho.\n");
                return;
            }

            var chosen = new List<RebaixoInfo>();
            while (true)
            {
                var po = new PromptPointOptions($"\nClique em cima do rebaixo a remover (hachura ou meio-fio; clicar de novo tira) - {chosen.Count} marcado(s) [Pronto] <Pronto>: ")
                {
                    AllowNone = true,
                    AppendKeywordsToMessage = false
                };
                po.Keywords.Add("Pronto");
                PromptPointResult pr = ed.GetPoint(po);
                if (pr.Status == PromptStatus.Cancel)
                    return;
                if (pr.Status != PromptStatus.OK)
                    break;
                Point3d p = pr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                RebaixoInfo? hit = HitRebaixo(items, p);
                if (hit is null)
                {
                    ed.WriteMessage("\nNenhum rebaixo neste ponto (clique na hachura do rebaixo ou no meio-fio dentro dele).");
                    continue;
                }
                if (chosen.Contains(hit))
                {
                    chosen.Remove(hit);
                    ed.WriteMessage($"\n{hit.Label} tirado da lista.");
                }
                else
                {
                    chosen.Add(hit);
                    ed.WriteMessage($"\n{hit.Label} ({hit.CorridorName}, lado {hit.SideLabel.ToLowerInvariant()}, {hit.LoweredLength:0.00} m) marcado.");
                }
            }
            if (chosen.Count == 0)
                return;

            List<MFTravessia.Stored> travs = MFTravessia.FindAll(db).Select(x => x.Data)
                .Where(d => chosen.Any(r => Uses(d, r))).GroupBy(d => d.Id).Select(g => g.First()).ToList();
            ed.WriteMessage($"\nVao sair {chosen.Count} rebaixo(s): {string.Join(", ", chosen.Select(r => r.Label))} (Transition Sets, secoes que o comando criou, hachura e os pedacos em regions/baselines vizinhas).");
            string answer;
            if (travs.Count > 0)
            {
                ed.WriteMessage($"\nTravessia(s) que usam esses rebaixos: {string.Join(", ", travs.Select(d => $"{d.LabelA}-{d.LabelB}"))}. Se ficarem, ficam sem o rebaixo (o meio-fio volta a altura normal embaixo delas).");
                var ko = new PromptKeywordOptions("\nRemover os rebaixos? Sim = so os rebaixos; Tambem = rebaixos e travessias [Sim/Tambem/Nao] <Nao>: ") { AppendKeywordsToMessage = false };
                ko.Keywords.Add("Sim");
                ko.Keywords.Add("Tambem");
                ko.Keywords.Add("Nao");
                ko.Keywords.Default = "Nao";
                PromptResult kr = ed.GetKeywords(ko);
                answer = kr.Status == PromptStatus.OK ? kr.StringResult : "Nao";
            }
            else
            {
                var ko = new PromptKeywordOptions("\nRemover? U desfaz [Sim/Nao] <Nao>: "); ko.AppendKeywordsToMessage = false;
                ko.Keywords.Add("Sim");
                ko.Keywords.Add("Nao");
                ko.Keywords.Default = "Nao";
                PromptResult kr = ed.GetKeywords(ko);
                answer = kr.Status == PromptStatus.OK ? kr.StringResult : "Nao";
            }
            if (answer is not ("Sim" or "Tambem"))
            {
                ed.WriteMessage("\nNada foi removido.\n");
                return;
            }

            var done = new List<string>();
            for (int i = 0; i < chosen.Count; i++)
            {
                RebaixoInfo r = chosen[i];
                bool last = !chosen.Skip(i + 1).Any(x => x.CorridorId == r.CorridorId);
                try
                {
                    MFRebaixoData.Remove(db, r, rebuild: last);
                    done.Add(r.Label);
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\n{r.Label}: nao removido ({ex.Message}).");
                    PluginLog.Erro("MFREBAIXO", $"MFREBAIXOAPAGAR {r.SetName}: {ex.Message}", ex);
                }
            }
            if (answer == "Tambem")
            {
                foreach (MFTravessia.Stored d in travs)
                {
                    try
                    {
                        MFTravessia.EraseTravessia(db, ed, d.Id);
                        done.Add($"travessia {d.LabelA}-{d.LabelB}");
                    }
                    catch (System.Exception ex)
                    {
                        ed.WriteMessage($"\nTravessia {d.LabelA}-{d.LabelB}: nao apagada ({ex.Message}).");
                    }
                }
            }
            string msg = $"Removido(s): {string.Join(", ", done)}. U desfaz.";
            ed.WriteMessage("\n" + msg + "\n");
            PluginLog.Info("MFREBAIXO", "MFREBAIXOAPAGAR: " + msg);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("MFREBAIXO", "MFREBAIXOAPAGAR: " + ex.Message, ex);
            ed.WriteMessage("\nErro: " + ex.Message + "\n");
        }
    }

    [CommandMethod(C3DCommands.Corridor.MfTravessiaApagar, CommandFlags.Modal)]
    public void ApagarTravessiaClicando()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        Database db = doc.Database;
        try
        {
            var chosen = new List<MFTravessia.Stored>();
            while (true)
            {
                // Clique em ponto (dentro da lombofaixa em planta): funciona com a planta oculta tambem.
                var eo = new PromptPointOptions($"\nClique em cima da lombofaixa a apagar (plato ou rampa, em planta) - {chosen.Count} marcada(s) [Pronto] <Pronto>: ")
                {
                    AllowNone = true,
                    AppendKeywordsToMessage = false
                };
                eo.Keywords.Add("Pronto");
                PromptPointResult er = ed.GetPoint(eo);
                if (er.Status == PromptStatus.Cancel)
                    return;
                if (er.Status != PromptStatus.OK)
                    break;
                string? id = MFTravessia.TravessiaAt(db, er.Value.TransformBy(ed.CurrentUserCoordinateSystem));
                MFTravessia.Stored? d = id is null ? null : MFTravessia.FindAll(db).Select(x => x.Data).FirstOrDefault(x => x.Id == id);
                if (d is null)
                {
                    ed.WriteMessage("\nNenhuma travessia neste ponto (clique dentro do plato ou da rampa; unioes de cruzamento: apague pelo painel).");
                    continue;
                }
                if (chosen.Any(x => x.Id == d.Id))
                {
                    chosen.RemoveAll(x => x.Id == d.Id);
                    ed.WriteMessage($"\nTravessia {d.LabelA}-{d.LabelB} tirada da lista.");
                }
                else
                {
                    chosen.Add(d);
                    ed.WriteMessage($"\nTravessia {d.LabelA}-{d.LabelB} marcada.");
                }
            }
            if (chosen.Count == 0)
                return;
            var ko = new PromptKeywordOptions($"\nApagar {chosen.Count} travessia(s) ({string.Join(", ", chosen.Select(d => d.LabelA + "-" + d.LabelB))})? Os rebaixos ficam. U desfaz [Sim/Nao] <Nao>: "); ko.AppendKeywordsToMessage = false;
            ko.Keywords.Add("Sim");
            ko.Keywords.Add("Nao");
            ko.Keywords.Default = "Nao";
            PromptResult kr = ed.GetKeywords(ko);
            if (kr.Status != PromptStatus.OK || kr.StringResult != "Sim")
            {
                ed.WriteMessage("\nNada foi apagado.\n");
                return;
            }
            int n = 0;
            foreach (MFTravessia.Stored d in chosen)
                n += MFTravessia.EraseTravessia(db, ed, d.Id);
            ed.WriteMessage($"\n{chosen.Count} travessia(s) apagada(s) ({n} objeto(s)). U desfaz.\n");
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("MFTRAVESSIA", "MFTRAVESSIAAPAGAR: " + ex.Message, ex);
            ed.WriteMessage("\nErro: " + ex.Message + "\n");
        }
    }

    /// <summary>
    /// Seleciona no desenho (pickfirst, com grips) as pecas das travessias: todas ou clicando, com filtro.
    /// Depois e so usar qualquer comando do Civil (IFCInfraExportSelected, LAYMCH, ISOLATEOBJECTS...).
    /// </summary>
    [CommandMethod(C3DCommands.Corridor.MfTravessiaSelecionar, CommandFlags.Modal | CommandFlags.Redraw)]
    public void TravessiaSelecionar()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        Database db = doc.Database;
        var ko = new PromptKeywordOptions("\nQuais travessias [Todas/Clicar] <Todas>: ") { AppendKeywordsToMessage = false, AllowNone = true };
        ko.Keywords.Add("Todas");
        ko.Keywords.Add("Clicar");
        PromptResult kr = ed.GetKeywords(ko);
        if (kr.Status == PromptStatus.Cancel)
            return;
        var ids = new HashSet<string>();
        if (kr.Status == PromptStatus.OK && kr.StringResult == "Clicar")
        {
            while (true)
            {
                var po = new PromptPointOptions($"\nClique dentro da lombofaixa (plato ou rampa) - {ids.Count} marcada(s) [Pronto] <Pronto>: ") { AllowNone = true, AppendKeywordsToMessage = false };
                po.Keywords.Add("Pronto");
                PromptPointResult pr = ed.GetPoint(po);
                if (pr.Status == PromptStatus.Cancel)
                    return;
                if (pr.Status != PromptStatus.OK)
                    break;
                if (MFTravessia.TravessiaAt(db, pr.Value.TransformBy(ed.CurrentUserCoordinateSystem)) is string id)
                {
                    if (!ids.Add(id))
                        ids.Remove(id);
                    ed.WriteMessage($"\n{ids.Count} travessia(s) marcada(s).");
                }
                else
                {
                    ed.WriteMessage("\nNenhuma travessia neste ponto.");
                }
            }
            if (ids.Count == 0)
                return;
        }
        var fo = new PromptKeywordOptions("\nQuais pecas [Tudo/Solidos/Plato/Grelhas/Vigas/Sinalizacao/Planta] <Tudo>: ") { AppendKeywordsToMessage = false, AllowNone = true };
        foreach (string f in MFTravessia.PartFilters)
            fo.Keywords.Add(f);
        PromptResult fr = ed.GetKeywords(fo);
        if (fr.Status == PromptStatus.Cancel)
            return;
        string filter = fr.Status == PromptStatus.OK ? fr.StringResult : "Tudo";
        List<ObjectId> objs = MFTravessia.Objects(db, ids, filter);
        if (objs.Count == 0)
        {
            ed.WriteMessage("\nNenhuma peca de travessia com esse filtro.\n");
            return;
        }
        ed.SetImpliedSelection(objs.ToArray());
        ed.WriteMessage($"\n{objs.Count} objeto(s) selecionado(s) ({filter}). Agora use o comando que quiser (ex. IFCInfraExportSelected para IFC 4.3).\n");
    }

    /// <summary>Objetos que o painel pediu para selecionar (botao Selecionar no desenho).</summary>
    internal static ObjectId[] PendingSelection = Array.Empty<ObjectId>();

    /// <summary>Faz, dentro de um comando (jeito seguro do AutoCAD), a selecao pedida pelo painel.</summary>
    [CommandMethod(C3DCommands.Corridor.MfRebaixoSelecionarPainel, CommandFlags.Modal | CommandFlags.Redraw)]
    public void SelecionarDoPainel()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        ObjectId[] ids = PendingSelection.Where(x => !x.IsNull && !x.IsErased).ToArray();
        PendingSelection = Array.Empty<ObjectId>();
        if (ids.Length == 0)
        {
            doc.Editor.WriteMessage("\nNada marcado no painel para selecionar.\n");
            return;
        }
        doc.Editor.SetImpliedSelection(ids);
        doc.Editor.WriteMessage($"\n{ids.Length} objeto(s) selecionado(s) no desenho. Use o comando que quiser (ex. IFCInfraExportSelected para IFC 4.3).\n");
    }

    [CommandMethod(C3DCommands.Corridor.MfTravessiaPlanta, CommandFlags.Modal)]
    public void TravessiaPlanta()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        MFRebaixoSettings s = MFRebaixoSettingsStore.Load();
        bool hide = !s.TravessiaPlantaOculta;
        int n = MFTravessia.SetPlanVisible(doc.Database, !hide);
        s.TravessiaPlantaOculta = hide;
        MFRebaixoSettingsStore.Save(s);
        doc.Editor.Regen();
        doc.Editor.WriteMessage(hide
            ? $"\nLombofaixas ocultas em planta ({n} hachura(s)); os solidos 3D ficam. Rode de novo para mostrar.\n"
            : $"\nLombofaixas visiveis em planta de novo ({n} hachura(s)).\n");
    }

    /// <summary>A travessia usa este rebaixo (mesma baseline e lado, e o eixo dela perto do rebaixo).</summary>
    private static bool Uses(MFTravessia.Stored d, RebaixoInfo r)
    {
        bool sideA = d.BaselineA == r.BaselineKey && d.LeftA == r.Left;
        bool sideB = d.BaselineB == r.BaselineKey && d.LeftB == r.Left;
        if (!sideA && !sideB)
            return false;
        Point3d mid = r.Path.Count > 0 ? r.Path[r.Path.Count / 2] : r.P1;
        return Math.Sqrt(Math.Pow(mid.X - d.Origin.X, 2) + Math.Pow(mid.Y - d.Origin.Y, 2)) < 25.0;
    }

    /// <summary>Rebaixo debaixo do ponto: dentro da faixa (borda da pista ao fundo do meio-fio) com 0,6 m de folga.</summary>
    internal static RebaixoInfo? HitRebaixo(IEnumerable<RebaixoInfo> items, Point3d p)
    {
        RebaixoInfo? best = null;
        double bestD = double.MaxValue;
        foreach (RebaixoInfo r in items)
        {
            double d;
            if (r.Band.Count >= 2)
            {
                var poly = r.Band.Select(b => b.Inner).Concat(r.Band.AsEnumerable().Reverse().Select(b => b.Outer)).ToList();
                d = Inside(poly, p) ? 0 : poly.Select((a, i) => SegDist(a, poly[(i + 1) % poly.Count], p)).Min();
            }
            else if (r.Path.Count >= 2)
            {
                d = Math.Max(0, r.Path.Select((a, i) => i + 1 < r.Path.Count ? SegDist(a, r.Path[i + 1], p) : double.MaxValue).Min() - 1.0);
            }
            else
            {
                continue;
            }
            if (d < 0.6 && d < bestD)
            {
                bestD = d;
                best = r;
            }
        }
        return best;
    }

    private static bool Inside(List<Point3d> poly, Point3d p)
    {
        bool c = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if (((poly[i].Y > p.Y) != (poly[j].Y > p.Y)) &&
                p.X < ((poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y)) + poly[i].X)
                c = !c;
        }
        return c;
    }

    private static double SegDist(Point3d a, Point3d b, Point3d p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = (dx * dx) + (dy * dy);
        double t = l2 < 1e-12 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / l2, 0, 1);
        double x = a.X + (t * dx) - p.X, y = a.Y + (t * dy) - p.Y;
        return Math.Sqrt((x * x) + (y * y));
    }
}
