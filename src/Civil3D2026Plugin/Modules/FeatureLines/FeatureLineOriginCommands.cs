using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.FeatureLines;

/// <summary>
/// FLORIGEM (18/11/2026, pedido do Heyvid: "setar novo ponto de origem de feature line, mas sem f* todo o corredor"):
/// numa feature line FECHADA, a estaca 0 passa para o ponto escolhido. A geometria, as elevacoes e o sentido nao mudam;
/// a feature line continua o mesmo objeto (troca de identidade, como no FLCOPIARGEOMETRIA). No corredor, tudo que e por
/// estaca vai junto: regioes (regravadas), transicoes/rebaixos (recriados na estaca nova, com o token dos rebaixos) e,
/// com elas, os overrides; as secoes extras o Civil leva sozinho. Teste no KM_01 (SARJETA 12, 16, 17): tudo no mesmo
/// lugar fisico (0,0 mm), overrides mantidos, Rebuild sem erro.
///
/// FLFECHADAS: lista as feature lines que parecem fechadas mas estao abertas (pontas coladas) e oferece fechar.
/// </summary>
public sealed class FeatureLineOriginCommands
{
    private const string Mod = "FLORIGEM";

    [CommandMethod(C3DCommands.FeatureLines.Origem, CommandFlags.Modal)]
    public void MudarOrigem()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        try
        {
            var o1 = new PromptEntityOptions("\nSelecione a feature line fechada: ");
            o1.SetRejectMessage("\nTem de ser uma feature line.");
            o1.AddAllowedClass(typeof(CivilDb.FeatureLine), false);
            PromptEntityResult r1 = ed.GetEntity(o1);
            if (r1.Status != PromptStatus.OK)
                return;
            AcadDb.ObjectId flId = r1.ObjectId;

            string name;
            bool closed;
            (double Gap, double Dz) gap;
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                var fl = (CivilDb.FeatureLine)tr.GetObject(flId, AcadDb.OpenMode.ForRead);
                if (fl.IsReferenceObject)
                {
                    ed.WriteMessage("\nEssa feature line vem de referencia (data shortcut/xref): mude no desenho de origem.\n");
                    return;
                }
                name = fl.Name;
                closed = fl.Closed;
                gap = closed ? (0, 0) : FeatureLineGeometry.EndGap(fl);
                tr.Commit();
            }

            // Fechada de verdade? (pedido do Heyvid: as vezes parecem fechadas mas nao estao)
            if (!closed)
            {
                double tol = AskTolerance(ed);
                if (double.IsNaN(tol))
                    return;
                if (gap.Gap > tol)
                {
                    ed.WriteMessage($"\n'{name}' e ABERTA: as pontas estao a {gap.Gap * 100:0.0} cm uma da outra em planta (elevacao {gap.Dz * 100:0.0} cm). " +
                                    "A origem de uma aberta so pode ser uma das pontas. Nada mudou.\n");
                    return;
                }
                ed.WriteMessage($"\n'{name}' parece fechada mas esta ABERTA: as pontas estao a {gap.Gap * 1000:0.0} mm em planta e {gap.Dz * 1000:0.0} mm em elevacao.");
                if (!Ask(ed, "\nFechar de verdade antes de continuar (mesmo objeto, mesmo handle; U desfaz)? [Fechar/Cancelar] <Fechar>: ", "Fechar", "Cancelar", "Fechar"))
                {
                    ed.WriteMessage("\nCancelado. Nada mudou.\n");
                    return;
                }
                if (!Close(db, ed, flId, name))
                    return;
            }

            // Ponto da nova origem.
            bool snap = true;
            Point3d pick;
            while (true)
            {
                var op = new PromptPointOptions($"\nClique o novo ponto de origem em cima da feature line [{(snap ? "Livre" : "Vertice")}] <{(snap ? "vai para o vertice mais proximo" : "ponto livre: vira vertice")}>: ");
                op.Keywords.Add(snap ? "Livre" : "Vertice");
                PromptPointResult rp = ed.GetPoint(op);
                if (rp.Status == PromptStatus.Keyword)
                {
                    snap = !snap;
                    continue;
                }
                if (rp.Status != PromptStatus.OK)
                    return;
                pick = rp.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                break;
            }

            FeatureLineGeometry.Plan plan;
            double shift;
            bool newVertex;
            List<string> crossing;
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                var fl = (CivilDb.FeatureLine)tr.GetObject(flId, AcadDb.OpenMode.ForRead);
                plan = FeatureLineGeometry.PrepareRotation(tr, db, fl, pick, snap, out shift, out newVertex);
                crossing = FeatureLineGeometry.CrossingTransitions(tr, plan, shift);
                tr.Commit();
            }
            if (shift < 0.001 || shift > plan.OldLength - 0.001)
            {
                ed.WriteMessage("\nEsse ponto ja e a origem. Nada mudou.\n");
                return;
            }
            if (crossing.Count > 0)
            {
                ed.WriteMessage($"\nA nova origem (estaca {shift:0.000}) cairia no meio de: {string.Join("; ", crossing)}.");
                ed.WriteMessage("\nO Civil nao deixa uma regiao/transicao dar a volta pela estaca 0. Escolha um ponto fora delas (de preferencia na divisa entre regioes). Nada mudou.\n");
                return;
            }

            // Resumo.
            ed.WriteMessage($"\n\nFeature line '{name}': a estaca 0 passa para a estaca {shift:0.000} de hoje{(newVertex ? " (entra um vertice novo ali)" : " (vertice existente)")}.");
            ed.WriteMessage($"\n  Geometria, elevacoes ({plan.ElevationPoints.Count} pontos de elevacao), sentido e handle nao mudam. Comprimento {plan.OldLength:0.000} m.");
            foreach (FeatureLineGeometry.BaselineUse b in plan.Baselines)
            {
                ed.WriteMessage($"\n  Corredor '{b.Corridor}', baseline '{b.Baseline}'{(b.Reference ? " (referencia: nao muda!)" : "")}:");
                foreach ((string rn, double s0, double e0) in b.Regions)
                {
                    bool full = s0 < 0.01 && e0 > plan.OldLength - 0.01;
                    double a = full ? 0 : FeatureLineGeometry.Rotate(s0, shift, plan.OldLength);
                    double z = full ? plan.OldLength : FeatureLineGeometry.Rotate(e0, shift, plan.OldLength);
                    if (!full && z <= a)
                        z = plan.OldLength;
                    ed.WriteMessage($"\n    regiao '{rn}': {s0:0.000}-{e0:0.000} -> {a:0.000}-{z:0.000}{(full ? " (volta inteira)" : "")}");
                }
            }
            ed.WriteMessage("\n  Transicoes (rebaixos do MFREBAIXO inclusive) vao para a estaca nova; secoes extras e overrides acompanham. Tudo fica no mesmo lugar fisico.");
            if (!Ask(ed, "\nMudar a origem? [Mudar/Cancelar] <Cancelar>: ", "Mudar", "Cancelar", "Cancelar"))
            {
                ed.WriteMessage("\nCancelado. Nada mudou.\n");
                return;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (!Rebuild(db, ed, flId, name, plan))
                return;
            var msgs = new List<string>();
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                msgs.AddRange(FeatureLineGeometry.RotateRegions(tr, plan, shift));
                tr.Commit();
            }
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                msgs.AddRange(FeatureLineGeometry.ShiftTransitions(tr, plan, shift));
                tr.Commit();
            }

            string state;
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                var fl = (CivilDb.FeatureLine)tr.GetObject(flId, AcadDb.OpenMode.ForRead);
                Point3d p0 = FeatureLineGeometry.UniquePis(fl)[0];
                state = $"{(fl.Closed ? "fechada" : "ABERTA (confira!)")}, handle {fl.Handle}, inicio em {p0.X:0.000},{p0.Y:0.000}";
                tr.Commit();
            }
            ed.WriteMessage($"\n[OK] Origem de '{name}' mudada ({clock.ElapsedMilliseconds / 1000.0:0.0} s): {state}.");
            foreach (string m in msgs)
                ed.WriteMessage("\n  " + m);
            PluginLog.Info(Mod, $"{name}: origem na estaca {shift:0.000} (novo vertice {newVertex}); {state}. " + string.Join(" | ", msgs));
            AskRebuild(db, ed, plan);
            ed.WriteMessage("\n");
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Mod, "FLORIGEM falhou: " + ex.Message, ex);
            ed.WriteMessage("\nErro: " + ex.Message + "\n");
        }
    }

    [CommandMethod(C3DCommands.FeatureLines.Fechadas, CommandFlags.Modal)]
    public void ListarQuaseFechadas()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        try
        {
            double tol = AskTolerance(ed);
            if (double.IsNaN(tol))
                return;
            var found = new List<(AcadDb.ObjectId Id, string Name, double Gap, double Dz)>();
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                var ms = (AcadDb.BlockTableRecord)tr.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db), AcadDb.OpenMode.ForRead);
                foreach (AcadDb.ObjectId id in ms)
                {
                    if (!id.ObjectClass.DxfName.Contains("FEATURE", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not CivilDb.FeatureLine fl || fl.Closed || fl.IsReferenceObject || fl.PIPointsCount < 3)
                        continue;
                    (double g, double dz) = FeatureLineGeometry.EndGap(fl);
                    if (g <= tol)
                        found.Add((id, fl.Name, g, dz));
                }
                tr.Commit();
            }
            if (found.Count == 0)
            {
                ed.WriteMessage($"\nNenhuma feature line aberta com as pontas a menos de {tol * 100:0.##} cm. Nada a fazer.\n");
                return;
            }
            ed.WriteMessage($"\n{found.Count} feature line(s) parecem fechadas mas estao ABERTAS (pontas a menos de {tol * 100:0.##} cm):");
            foreach (var f in found)
                ed.WriteMessage($"\n  {f.Name}: pontas a {f.Gap * 1000:0.0} mm em planta, {f.Dz * 1000:0.0} mm em elevacao");
            if (!Ask(ed, $"\nFechar de verdade todas as {found.Count} (cada uma continua o mesmo objeto; U desfaz)? [Fechar/Nao] <Nao>: ", "Fechar", "Nao", "Nao"))
            {
                ed.WriteMessage("\nNada mudou.\n");
                return;
            }
            int ok = 0;
            foreach (var f in found)
            {
                if (Close(db, ed, f.Id, f.Name))
                    ok++;
            }
            ed.WriteMessage($"\n{ok} de {found.Count} fechada(s). U desfaz.\n");
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Mod, "FLFECHADAS falhou: " + ex.Message, ex);
            ed.WriteMessage("\nErro: " + ex.Message + "\n");
        }
    }

    private static double AskTolerance(Editor ed)
    {
        var o = new PromptDoubleOptions("\nDistancia maxima entre as pontas para considerar \"parece fechada\", em m <0.01>: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = false,
            DefaultValue = 0.01,
            UseDefaultValue = true
        };
        PromptDoubleResult r = ed.GetDouble(o);
        if (r.Status == PromptStatus.Cancel)
            return double.NaN;
        return r.Status == PromptStatus.OK ? r.Value : 0.01;
    }

    private static bool Ask(Editor ed, string prompt, string yes, string no, string def)
    {
        var o = new PromptKeywordOptions(prompt, yes + " " + no)
        {
            AllowNone = true
        };
        o.Keywords.Default = def;
        PromptResult r = ed.GetKeywords(o);
        if (r.Status == PromptStatus.Cancel)
            return false;
        string ans = r.Status == PromptStatus.OK ? r.StringResult : def;
        return ans == yes;
    }

    /// <summary>Fecha de verdade (mesmo objeto) e regrava as regioes nas estacas de antes.</summary>
    private static bool Close(AcadDb.Database db, Editor ed, AcadDb.ObjectId flId, string name)
    {
        FeatureLineGeometry.Plan plan;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            var fl = (CivilDb.FeatureLine)tr.GetObject(flId, AcadDb.OpenMode.ForRead);
            plan = FeatureLineGeometry.PrepareClose(tr, db, fl);
            tr.Commit();
        }
        if (!Rebuild(db, ed, flId, name, plan))
            return false;
        List<string> msgs;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            msgs = FeatureLineGeometry.RestoreRegions(tr, plan, (k, count, s0, e0) => (s0, e0));
            tr.Commit();
        }
        bool isClosed;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            isClosed = ((CivilDb.FeatureLine)tr.GetObject(flId, AcadDb.OpenMode.ForRead)).Closed;
            tr.Commit();
        }
        ed.WriteMessage($"\n'{name}': {(isClosed ? "agora FECHADA de verdade" : "NAO ficou fechada (confira)")}; comprimento {plan.OldLength:0.000} -> {plan.NewLength:0.000} m.");
        foreach (string m in msgs)
            ed.WriteMessage("\n  " + m);
        PluginLog.Info(Mod, $"{name}: fechada (pontas coladas); fechada={isClosed}. " + string.Join(" | ", msgs));
        return isClosed;
    }

    /// <summary>Feature line nova com o plano + troca de identidade (o handle fica). Falhou: nada muda.</summary>
    private static bool Rebuild(AcadDb.Database db, Editor ed, AcadDb.ObjectId flId, string name, FeatureLineGeometry.Plan plan)
    {
        AcadDb.ObjectId newId;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            var fl = (CivilDb.FeatureLine)tr.GetObject(flId, AcadDb.OpenMode.ForRead);
            try
            {
                newId = FeatureLineGeometry.CreateNew(tr, db, fl, plan);
                string? bad = FeatureLineGeometry.Check((CivilDb.FeatureLine)tr.GetObject(newId, AcadDb.OpenMode.ForRead), plan);
                if (bad is not null)
                    throw new InvalidOperationException(bad);
            }
            catch (System.Exception ex)
            {
                tr.Abort();
                PluginLog.Erro(Mod, $"{name}: nao refeita: {ex.Message}", ex);
                ed.WriteMessage($"\nNao deu: {ex.Message}\nNada mudou.\n");
                return false;
            }
            tr.Commit();
        }
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            try
            {
                FeatureLineGeometry.SwapIn(tr, flId, newId, name);
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
                PluginLog.Erro(Mod, $"{name}: troca de identidade falhou: {ex.Message}", ex);
                ed.WriteMessage($"\nNao deu: {ex.Message}\nNada mudou.\n");
                return false;
            }
            tr.Commit();
        }
        return true;
    }

    private static void AskRebuild(AcadDb.Database db, Editor ed, FeatureLineGeometry.Plan plan)
    {
        var corridors = plan.Baselines.Where(b => !b.Reference).Select(b => b.CorridorId).Distinct().ToList();
        if (corridors.Count == 0)
            return;
        if (!Ask(ed, $"\nReconstruir agora o(s) {corridors.Count} corredor(es)? [Reconstruir/Depois] <Reconstruir>: ", "Reconstruir", "Depois", "Reconstruir"))
            return;
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
            }
        }
        tr.Commit();
    }
}
