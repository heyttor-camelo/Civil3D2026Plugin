using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.FeatureLines;

/// <summary>
/// FLCOPIARGEOMETRIA (18/11/2026, pedido do Heyvid): a feature line existente passa a ter a geometria em planta de uma
/// polilinha, sem ser apagada nem recriada (os corredores que a usam como baseline continuam ligados a ela).
/// </summary>
public sealed class FeatureLineGeometryCommands
{
    private const string Mod = "FLGEOMETRIA";

    [CommandMethod(C3DCommands.FeatureLines.CopiarGeometria, CommandFlags.Modal)]
    public void CopiarGeometria()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        try
        {
            var o1 = new PromptEntityOptions("\nSelecione a feature line que vai mudar de geometria: ");
            o1.SetRejectMessage("\nTem de ser uma feature line.");
            o1.AddAllowedClass(typeof(CivilDb.FeatureLine), false);
            PromptEntityResult r1 = ed.GetEntity(o1);
            if (r1.Status != PromptStatus.OK)
                return;

            var o2 = new PromptEntityOptions("\nSelecione a polilinha com a geometria nova: ");
            o2.SetRejectMessage("\nTem de ser uma polilinha (LWPOLYLINE, 2D ou 3D).");
            o2.AddAllowedClass(typeof(AcadDb.Polyline), true);
            o2.AddAllowedClass(typeof(AcadDb.Polyline2d), true);
            o2.AddAllowedClass(typeof(AcadDb.Polyline3d), true);
            PromptEntityResult r2 = ed.GetEntity(o2);
            if (r2.Status != PromptStatus.OK)
                return;

            bool usePolyZ = false;
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (tr.GetObject(r2.ObjectId, AcadDb.OpenMode.ForRead) is AcadDb.Polyline3d p3)
                {
                    (_, _, _, bool has3d) = FeatureLineGeometry.ReadPolyline(tr, p3);
                    if (has3d)
                    {
                        var oz = new PromptKeywordOptions("\nA polilinha 3D tem elevacoes. Usar as elevacoes dela? [Polilinha/Manter] <Manter>: ", "Polilinha Manter")
                        {
                            AllowNone = true
                        };
                        oz.Keywords.Default = "Manter";
                        PromptResult rz = ed.GetKeywords(oz);
                        if (rz.Status == PromptStatus.Cancel)
                            return;
                        usePolyZ = rz.Status == PromptStatus.OK && rz.StringResult == "Polilinha";
                    }
                }
                tr.Commit();
            }

            var otol = new PromptDoubleOptions("\nTolerancia para enxugar os pontos de elevacao, em m (sai o ponto que muda o perfil menos que isso; 0 = manter todos) <0.001>: ")
            {
                AllowNone = true,
                AllowNegative = false,
                DefaultValue = 0.001,
                UseDefaultValue = true
            };
            PromptDoubleResult rtol = ed.GetDouble(otol);
            if (rtol.Status == PromptStatus.Cancel)
                return;
            double weedTol = rtol.Status == PromptStatus.OK ? rtol.Value : 0.001;

            FeatureLineGeometry.Plan plan;
            string flName, handle;
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                var fl = (CivilDb.FeatureLine)tr.GetObject(r1.ObjectId, AcadDb.OpenMode.ForRead);
                if (fl.IsReferenceObject)
                {
                    ed.WriteMessage("\nEssa feature line vem de referencia (data shortcut/xref): mude a geometria no desenho de origem.\n");
                    return;
                }
                flName = fl.Name;
                handle = fl.Handle.ToString();
                var polyEnt = (AcadDb.Entity)tr.GetObject(r2.ObjectId, AcadDb.OpenMode.ForRead);
                plan = FeatureLineGeometry.Prepare(tr, db, fl, polyEnt, usePolyZ, weedTol);
                tr.Commit();
            }

            // Resumo e confirmacao.
            ed.WriteMessage($"\n\nFeature line '{flName}' (handle {handle}, continua o mesmo objeto):");
            ed.WriteMessage($"\n  Vertices: {plan.OldPis} -> {plan.V.Count}   Comprimento: {plan.OldLength:0.000} -> {plan.NewLength:0.000} m   {(plan.Closed ? "fechada" : "aberta")}");
            ed.WriteMessage($"\n  Sentido: o da feature line{(plan.Reversed ? " (a polilinha estava ao contrario e foi invertida)" : "")}" +
                            (plan.Closed && plan.Rotated != 0 ? "; o inicio fica no vertice mais perto do inicio antigo" : "") + ".");
            ed.WriteMessage($"\n  Elevacao dos vertices: {plan.ZSource} (maior diferenca para a feature line antiga no mesmo lugar: {plan.ZMaxChange * 100:0.0} cm).");
            ed.WriteMessage($"\n  Pontos de elevacao: havia {plan.OldElevationPoints}; {plan.ElevationBeforeWeed} cabem na geometria nova; ficam {plan.ElevationPoints.Count}" +
                            (plan.ElevationWeeded > 0 ? $" ({plan.ElevationWeeded} enxugados, mudavam o perfil menos de {weedTol * 1000:0.#} mm)" : "") +
                            (plan.ElevationLost > 0 ? $", {plan.ElevationLost} perdido(s) (mais de {FeatureLineGeometry.ElevationReach:0.0} m da geometria nova)" : "") + ".");
            foreach (string note in plan.Notes)
                ed.WriteMessage("\n  Aviso: " + note);
            if (plan.Baselines.Count == 0)
            {
                ed.WriteMessage("\n  Nenhum corredor usa esta feature line como baseline.");
            }
            foreach (FeatureLineGeometry.BaselineUse b in plan.Baselines)
            {
                var last = b.Regions.LastOrDefault();
                if (b.Regions.Count > 1)
                    ed.WriteMessage($"\n  Regioes de hoje (o comando as regrava depois da troca; se o Civil recusar, acerte com estas estacas): " + string.Join("; ", b.Regions.Select(r => $"\u0027{r.Name}\u0027 {r.Start:0.000}-{r.End:0.000}")));
                ed.WriteMessage($"\n  Corredor '{b.Corridor}', baseline '{b.Baseline}'{(b.Reference ? " (referencia: nao muda)" : "")}: {b.Regions.Count} regiao(oes)" +
                                (b.Regions.Count > 0 ? $"; a ultima ('{last.Name}', {last.Start:0.000}-{last.End:0.000}) vai ate o novo fim, as outras ficam nas estacas de hoje." : "."));
                foreach (var r in b.Regions.Take(b.Regions.Count - 1).Where(r => r.End > plan.NewLength + 0.001))
                    ed.WriteMessage($"\n    Atencao: a regiao '{r.Name}' ({r.Start:0.000}-{r.End:0.000}) passa do novo comprimento.");
            }
            ed.WriteMessage("\n  A feature line e refeita por baixo e assume o mesmo handle (corredores, alvos e site continuam ligados). Com muitos pontos de elevacao, dentro de site, pode levar alguns minutos.");
            ed.WriteMessage("\n  A polilinha fica no desenho (pergunto no fim se apaga). U desfaz tudo.");
            var ok = new PromptKeywordOptions("\nTrocar a geometria da feature line? [Trocar/Cancelar] <Cancelar>: ", "Trocar Cancelar")
            {
                AllowNone = true
            };
            ok.Keywords.Default = "Cancelar";
            PromptResult rok = ed.GetKeywords(ok);
            if (rok.Status != PromptStatus.OK || rok.StringResult != "Trocar")
            {
                ed.WriteMessage("\nCancelado. Nada mudou.\n");
                return;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            // 1) Feature line nova com a geometria certa (nome provisorio), no site da antiga; conferida contra a polilinha.
            AcadDb.ObjectId newId;
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                var fl = (CivilDb.FeatureLine)tr.GetObject(r1.ObjectId, AcadDb.OpenMode.ForRead);
                try
                {
                    newId = FeatureLineGeometry.CreateNew(tr, db, fl, plan);
                    var nf = (CivilDb.FeatureLine)tr.GetObject(newId, AcadDb.OpenMode.ForRead);
                    string? bad = FeatureLineGeometry.Check(nf, plan);
                    if (bad is not null)
                        throw new InvalidOperationException(bad);
                }
                catch (System.Exception ex)
                {
                    tr.Abort();
                    PluginLog.Erro(Mod, $"{flName}: geometria nao trocada: {ex.Message}", ex);
                    ed.WriteMessage($"\nNao deu para trocar a geometria: {ex.Message}\nNada mudou.\n");
                    return;
                }
                tr.Commit();
            }

            // 2) Troca de identidade: o handle original fica com a geometria nova.
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    FeatureLineGeometry.SwapIn(tr, r1.ObjectId, newId, flName);
                }
                catch (System.Exception ex)
                {
                    tr.Abort();
                    using (AcadDb.Transaction tr2 = db.TransactionManager.StartTransaction())
                    {
                        if (tr2.GetObject(newId, AcadDb.OpenMode.ForWrite) is AcadDb.DBObject o && !o.IsErased)
                            o.Erase();
                        tr2.Commit();
                    }
                    PluginLog.Erro(Mod, $"{flName}: troca de identidade falhou: {ex.Message}", ex);
                    ed.WriteMessage($"\nNao deu para trocar a geometria: {ex.Message}\nNada mudou.\n");
                    return;
                }
                tr.Commit();
            }
            // Regioes depois do commit (o Civil remapeia as estacas ao fechar a feature line).
            List<string> regionMsgs;
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                regionMsgs = FeatureLineGeometry.FitLastRegions(tr, plan);
                tr.Commit();
            }

            ed.WriteMessage($"\n[OK] '{flName}' com a geometria da polilinha ({plan.V.Count} vertices, {plan.NewLength:0.000} m; {clock.ElapsedMilliseconds / 1000.0:0.0} s). Handle {handle} mantido.");
            foreach (string m in regionMsgs)
                ed.WriteMessage("\n  " + m);
            PluginLog.Info(Mod, $"{flName}: geometria trocada ({plan.OldPis} -> {plan.V.Count} vertices, {plan.OldLength:0.000} -> {plan.NewLength:0.000} m). " + string.Join(" | ", regionMsgs.Concat(plan.Notes)));

            // Rebuild dos corredores (pergunta).
            var corridors = plan.Baselines.Where(b => !b.Reference).Select(b => b.CorridorId).Distinct().ToList();
            if (corridors.Count > 0)
            {
                var orb = new PromptKeywordOptions($"\nReconstruir agora o(s) {corridors.Count} corredor(es) que usam a feature line? [Reconstruir/Depois] <Reconstruir>: ", "Reconstruir Depois")
                {
                    AllowNone = true
                };
                orb.Keywords.Default = "Reconstruir";
                PromptResult rrb = ed.GetKeywords(orb);
                if (rrb.Status != PromptStatus.Cancel && !(rrb.Status == PromptStatus.OK && rrb.StringResult == "Depois"))
                {
                    using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
                    foreach (AcadDb.ObjectId id in corridors)
                    {
                        var c = (CivilDb.Corridor)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                        try
                        {
                            c.Rebuild();
                            ed.WriteMessage($"\n  Corredor '{c.Name}' reconstruido.");
                        }
                        catch (System.Exception ex)
                        {
                            ed.WriteMessage($"\n  Corredor '{c.Name}': Rebuild falhou ({ex.Message}).");
                            PluginLog.Aviso(Mod, $"Rebuild de {c.Name} falhou: {ex.Message}");
                        }
                    }
                    tr.Commit();
                }
            }

            var oe = new PromptKeywordOptions("\nApagar a polilinha? [Apagar/Manter] <Manter>: ", "Apagar Manter")
            {
                AllowNone = true
            };
            oe.Keywords.Default = "Manter";
            PromptResult re = ed.GetKeywords(oe);
            if (re.Status == PromptStatus.OK && re.StringResult == "Apagar")
            {
                using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
                tr.GetObject(r2.ObjectId, AcadDb.OpenMode.ForWrite).Erase();
                tr.Commit();
                ed.WriteMessage("\nPolilinha apagada.");
            }
            ed.WriteMessage("\n");
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Mod, "FLCOPIARGEOMETRIA falhou: " + ex.Message, ex);
            ed.WriteMessage("\nErro: " + ex.Message + "\n");
        }
    }
}
