using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>
/// Rebuild do corredor automatico ou manual (2.126.0, pedido do Heyvid em 18/11/2026: "o comando pode ter um modo de
/// rebuild automatico ... ou outro manual, onde eu lanco os rebaixos e so quando eu quiser dou rebuild").
///
/// - Automatico (padrao): como sempre, cada rebaixo faz o Rebuild, acerta a altura final e desenha a hachura.
/// - Manual: o rebaixo grava so os Transition Sets e as secoes e fica "pendente" (lista guardada no desenho, entra no
///   U). A altura normal das pontas e lida das secoes ja calculadas (a ultima reconstrucao).
/// - MFREBAIXORECONSTRUIR: um Rebuild por corredor dos pendentes, depois altura final, hachuras e aviso das travessias.
/// </summary>
public sealed partial class MFRebaixoCommands
{
    [ThreadStatic]
    private static bool forceRebuild;

    /// <summary>Rebaixos lancados agora ficam sem Rebuild (modo manual), a nao ser durante a finalizacao.</summary>
    internal static bool DeferRebuild => !forceRebuild && (ManualOverride ?? MFRebaixoSettingsStore.Load().RebuildManual);

    /// <summary>So para testes (accoreconsole): modo sem mexer no config.json do usuario.</summary>
    internal static bool? ManualOverride { get; set; }

    internal static string RebuildModeLabel(MFRebaixoSettings s) => s.RebuildManual ? "Manual" : "Automatico";

    /// <summary>Trecho que precisa do corredor pronto (travessias, acerto da altura final): Rebuild mesmo no modo manual.</summary>
    internal static IDisposable ForceRebuildScope() => new RebuildScope();

    private sealed class RebuildScope : IDisposable
    {
        private readonly bool old = forceRebuild;

        public RebuildScope() => forceRebuild = true;

        public void Dispose() => forceRebuild = old;
    }

    [CommandMethod(C3DCommands.Corridor.MfRebaixoReconstruir, CommandFlags.Modal)]
    public void ReconstruirPendentes()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        try
        {
            FinalizePending(doc, ask: true);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("MFREBAIXO", "MFREBAIXORECONSTRUIR falhou: " + ex.Message, ex);
            doc.Editor.WriteMessage("\nErro ao reconstruir: " + ex.Message + "\n");
        }
    }

    [CommandMethod(C3DCommands.Corridor.MfRebaixoModoRebuild, CommandFlags.Modal)]
    public void TrocarModoRebuild()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        MFRebaixoPending.EnsureHooks();
        MFRebaixoSettings s = MFRebaixoSettingsStore.Load();
        var o = new PromptKeywordOptions($"\nRebuild do corredor (hoje {RebuildModeLabel(s)}) [Automatico/Manual] <{(s.RebuildManual ? "Automatico" : "Manual")}>: ", "Automatico Manual")
        {
            AllowNone = true
        };
        o.Keywords.Default = s.RebuildManual ? "Automatico" : "Manual";
        PromptResult r = doc.Editor.GetKeywords(o);
        if (r.Status == PromptStatus.Cancel)
            return;
        bool manual = r.Status == PromptStatus.OK ? r.StringResult == "Manual" : !s.RebuildManual;
        SetRebuildMode(doc, manual);
    }

    /// <summary>
    /// Troca o modo (prompt do MFREBAIXO e botao do painel). No manual, corredor com "Rebuild automatico" do Civil
    /// ligado reconstroi sozinho a cada mudanca: avisa e oferece desligar (com confirmacao).
    /// </summary>
    internal static void SetRebuildMode(Document doc, bool manual, bool ask = true)
    {
        MFRebaixoSettings s = MFRebaixoSettingsStore.Load();
        s.RebuildManual = manual;
        MFRebaixoSettingsStore.Save(s);
        Editor ed = doc.Editor;
        ed.WriteMessage($"\nRebuild do corredor: {RebuildModeLabel(s)}." + (manual
            ? " Os rebaixos ficam pendentes (sem Rebuild, hachura nem acerto da altura final) ate o MFREBAIXORECONSTRUIR."
            : " Cada rebaixo faz o Rebuild e fica pronto na hora.") + "\n");
        PluginLog.Info("MFREBAIXO", "Rebuild do corredor: " + RebuildModeLabel(s));
        if (manual && ask)
            OfferAutoRebuildOff(doc);
        if (!manual)
        {
            int n = MFRebaixoPending.Count(doc.Database);
            if (n > 0)
                ed.WriteMessage($"\nAinda ha {n} rebaixo(s) pendente(s) do modo manual: rode MFREBAIXORECONSTRUIR para finaliza-los.\n");
        }
    }

    /// <summary>Corredores com o "Rebuild automatico" do Civil ligado (propriedade lida por reflexao).</summary>
    internal static List<(ObjectId Id, string Name)> AutoRebuildCorridors(Database db)
    {
        var list = new List<(ObjectId, string)>();
        using Transaction tr = db.TransactionManager.StartTransaction();
        foreach (ObjectId id in MFRebaixoData.CorridorIds(db, tr))
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not CivilCorridor c)
                continue;
            if (c.GetType().GetProperty("RebuildAutomatic")?.GetValue(c) is true)
                list.Add((id, c.Name));
        }
        tr.Commit();
        return list;
    }

    private static void OfferAutoRebuildOff(Document doc)
    {
        Editor ed = doc.Editor;
        List<(ObjectId Id, string Name)> auto;
        try
        {
            auto = AutoRebuildCorridors(doc.Database);
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso("MFREBAIXO", "Nao deu para ler o Rebuild automatico dos corredores: " + ex.Message);
            return;
        }
        if (auto.Count == 0)
            return;
        ed.WriteMessage($"\nAtencao: {auto.Count} corredor(es) com o \"Rebuild automatico\" do Civil ligado ({string.Join(", ", auto.Select(a => a.Name))}): " +
                        "o Civil reconstroi sozinho a cada rebaixo e o modo manual nao ganha tempo.");
        var o = new PromptKeywordOptions("\nDesligar o Rebuild automatico desses corredores? [Desligar/Manter] <Manter>: ", "Desligar Manter")
        {
            AllowNone = true
        };
        o.Keywords.Default = "Manter";
        PromptResult r = ed.GetKeywords(o);
        if (r.Status != PromptStatus.OK || r.StringResult != "Desligar")
        {
            ed.WriteMessage("\nMantido como esta.\n");
            return;
        }
        using (doc.LockDocument())
        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            foreach ((ObjectId id, _) in auto)
            {
                var c = (CivilCorridor)tr.GetObject(id, OpenMode.ForWrite);
                c.GetType().GetProperty("RebuildAutomatic")?.SetValue(c, false);
            }
            tr.Commit();
        }
        ed.WriteMessage($"\nRebuild automatico desligado em {auto.Count} corredor(es). U desfaz.\n");
        PluginLog.Info("MFREBAIXO", "Rebuild automatico desligado: " + string.Join(", ", auto.Select(a => a.Name)));
    }

    /// <summary>
    /// Travessias e outros comandos que leem o corredor pronto: com rebaixos pendentes, oferece reconstruir antes.
    /// Devolve false se o usuario cancelou.
    /// </summary>
    internal static bool OfferFinalizeBefore(Document doc, string what)
    {
        int n = MFRebaixoPending.Read(doc.Database).Count;
        if (n == 0)
            return true;
        Editor ed = doc.Editor;
        var o = new PromptKeywordOptions($"\nHa {n} rebaixo(s) pendente(s) (lancados sem Rebuild). {what} le o corredor pronto. Reconstruir antes? [Reconstruir/Seguir/Cancelar] <Reconstruir>: ",
            "Reconstruir Seguir Cancelar")
        {
            AllowNone = true
        };
        o.Keywords.Default = "Reconstruir";
        PromptResult r = ed.GetKeywords(o);
        if (r.Status == PromptStatus.Cancel || (r.Status == PromptStatus.OK && r.StringResult == "Cancelar"))
            return false;
        if (r.Status == PromptStatus.OK && r.StringResult == "Seguir")
            return true;
        FinalizePending(doc, ask: false);
        return true;
    }

    /// <summary>
    /// Finaliza os rebaixos pendentes: um Rebuild por corredor, acerto da altura final dos desviados (peca sem
    /// REB_K), hachuras e aviso das travessias que usam esses rebaixos. Devolve quantos foram finalizados.
    /// </summary>
    internal static int FinalizePending(Document doc, bool ask)
    {
        Database db = doc.Database;
        Editor ed = doc.Editor;
        List<MFRebaixoPending.Item> pend = MFRebaixoPending.Read(db);
        if (pend.Count == 0)
        {
            ed.WriteMessage("\nNenhum rebaixo pendente: nada a reconstruir.\n");
            return 0;
        }
        var corridorIds = new List<ObjectId>();
        foreach (MFRebaixoPending.Item p in pend)
        {
            foreach (ObjectId id in p.Corridors)
            {
                if (!id.IsNull && !id.IsErased && !corridorIds.Contains(id))
                    corridorIds.Add(id);
            }
        }
        var names = new List<string>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in corridorIds)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is CivilCorridor c)
                    names.Add(c.Name);
            }
            tr.Commit();
        }

        if (ask)
        {
            ed.WriteMessage($"\n{pend.Count} rebaixo(s) pendente(s) em {corridorIds.Count} corredor(es) ({string.Join(", ", names)}):");
            foreach (MFRebaixoPending.Item p in pend.Take(30))
                ed.WriteMessage("\n  " + p.SetName);
            if (pend.Count > 30)
                ed.WriteMessage($"\n  ... e mais {pend.Count - 30}");
            ed.WriteMessage("\nVai: 1 Rebuild por corredor, acerto da altura final dos que sairem diferentes da pedida, hachuras em planta. U desfaz tudo.");
            var o = new PromptKeywordOptions("\nReconstruir e finalizar? [Reconstruir/Cancelar] <Reconstruir>: ", "Reconstruir Cancelar")
            {
                AllowNone = true
            };
            o.Keywords.Default = "Reconstruir";
            PromptResult r = ed.GetKeywords(o);
            if (r.Status == PromptStatus.Cancel || (r.Status == PromptStatus.OK && r.StringResult == "Cancelar"))
            {
                ed.WriteMessage("\nCancelado. Os rebaixos continuam pendentes.\n");
                return 0;
            }
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool old = forceRebuild;
        forceRebuild = true;
        try
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in corridorIds)
                {
                    var c = (CivilCorridor)tr.GetObject(id, OpenMode.ForWrite);
                    c.Rebuild();
                }
                tr.Commit();
            }
            long rebuildMs = clock.ElapsedMilliseconds;

            var setNames = new HashSet<string>(pend.Select(p => p.SetName), StringComparer.OrdinalIgnoreCase);
            List<RebaixoInfo> mine = MFRebaixoData.ReadAll(db).Where(r => setNames.Contains(r.SetName)).ToList();
            List<RebaixoInfo> off = mine.Where(r => r.FinalOff && r.TargetFinal is not null).ToList();
            int fixedN = 0;
            var errors = new List<string>();
            if (off.Count > 0)
            {
                ed.WriteMessage($"\n{off.Count} rebaixo(s) com a altura obtida diferente da pedida: acertando (peca sem REB_K).");
                (fixedN, errors) = FixFinalHeights(db, ed, off);
                mine = MFRebaixoData.ReadAll(db).Where(r => setNames.Contains(r.SetName)).ToList();
            }
            (_, int hatches) = MFRebaixoHatch.RedrawAll(db, mine);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                MFRebaixoPending.Clear(tr, db);
                tr.Commit();
            }

            var labels = new HashSet<string>(mine.Select(r => r.Label), StringComparer.OrdinalIgnoreCase);
            int trav = MFTravessia.FindAll(db).Select(x => x.Data).Where(d => labels.Contains(d.LabelA) || labels.Contains(d.LabelB))
                .Select(d => d.Id).Distinct().Count();

            string fim = $"{mine.Count} rebaixo(s) finalizado(s): {corridorIds.Count} Rebuild ({rebuildMs / 1000.0:0.0} s), " +
                         (off.Count > 0 ? $"altura final acertada em {fixedN} de {off.Count}, " : "altura final ok, ") +
                         $"{hatches} hachura(s); total {clock.ElapsedMilliseconds / 1000.0:0.0} s. U desfaz.";
            ed.WriteMessage("\n" + fim);
            if (errors.Count > 0)
                ed.WriteMessage("\nNao deu: " + string.Join("; ", errors));
            if (trav > 0)
                ed.WriteMessage($"\n{trav} travessia(s) usam estes rebaixos: confira o nivel com o meio-fio no painel (aba Travessias) ou use Refazer.");
            ed.WriteMessage("\n");
            PluginLog.Info("MFREBAIXO", "MFREBAIXORECONSTRUIR: " + fim + (trav > 0 ? $" Travessias ligadas: {trav}." : ""));
            return mine.Count;
        }
        finally
        {
            forceRebuild = old;
        }
    }
}

/// <summary>
/// Lista dos rebaixos pendentes (lancados no modo manual, sem Rebuild), guardada no desenho num dicionario do NOD
/// (vai junto no U e no salvar).
/// </summary>
internal static class MFRebaixoPending
{
    private const string DictName = "C3D_MFREBAIXO_PENDENTES";

    internal sealed record Item(string SetName, List<ObjectId> Corridors);

    private static bool hooked;

    public static void Add(Transaction tr, Database db, string setName, IEnumerable<ObjectId> corridors)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        DBDictionary dict;
        if (nod.Contains(DictName))
        {
            dict = (DBDictionary)tr.GetObject(nod.GetAt(DictName), OpenMode.ForWrite);
        }
        else
        {
            nod.UpgradeOpen();
            dict = new DBDictionary();
            nod.SetAt(DictName, dict);
            tr.AddNewlyCreatedDBObject(dict, true);
        }
        var values = new List<TypedValue> { new((int)DxfCode.Text, setName) };
        foreach (ObjectId id in corridors.Distinct())
            values.Add(new TypedValue((int)DxfCode.SoftPointerId, id));
        string key = Key(setName);
        if (dict.Contains(key))
        {
            var old = (Xrecord)tr.GetObject(dict.GetAt(key), OpenMode.ForWrite);
            old.Data = new ResultBuffer(values.ToArray());
            return;
        }
        var xr = new Xrecord { Data = new ResultBuffer(values.ToArray()) };
        dict.SetAt(key, xr);
        tr.AddNewlyCreatedDBObject(xr, true);
        EnsureHooks();
    }

    /// <summary>Quantos pendentes (rapido, sem conferir se o rebaixo ainda existe).</summary>
    public static int Count(Database db) => Read(db, check: false).Count;

    public static List<Item> Read(Database db, bool check = true)
    {
        var list = new List<Item>();
        using Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (nod.Contains(DictName))
        {
            var dict = (DBDictionary)tr.GetObject(nod.GetAt(DictName), OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
            {
                if (tr.GetObject(e.Value, OpenMode.ForRead) is not Xrecord xr || xr.Data is null)
                    continue;
                string name = "";
                var ids = new List<ObjectId>();
                foreach (TypedValue v in xr.Data)
                {
                    if (v.TypeCode == (int)DxfCode.Text)
                        name = v.Value as string ?? "";
                    else if (v.TypeCode == (int)DxfCode.SoftPointerId && v.Value is ObjectId id && !id.IsNull && !id.IsErased)
                        ids.Add(id);
                }
                if (name.Length > 0)
                    list.Add(new Item(name, ids));
            }
        }
        tr.Commit();
        // Rebaixo pendente que foi removido depois: sai da conta.
        if (check && list.Count > 0)
        {
            var existing = new HashSet<string>(MFRebaixoData.ReadAll(db).Select(r => r.SetName), StringComparer.OrdinalIgnoreCase);
            list = list.Where(i => existing.Contains(i.SetName)).ToList();
        }
        return list;
    }

    public static void Clear(Transaction tr, Database db)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(DictName))
            return;
        var dict = (DBDictionary)tr.GetObject(nod.GetAt(DictName), OpenMode.ForWrite);
        var ids = new List<ObjectId>();
        foreach (DBDictionaryEntry e in dict)
            ids.Add(e.Value);
        foreach (ObjectId id in ids)
            tr.GetObject(id, OpenMode.ForWrite).Erase();
    }

    private static string Key(string setName)
    {
        var chars = setName.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray();
        return new string(chars);
    }

    /// <summary>Aviso na linha de comando ao salvar ou fechar um desenho com rebaixos pendentes.</summary>
    public static void EnsureHooks()
    {
        if (hooked)
            return;
        hooked = true;
        DocumentCollection dm = AcApp.DocumentManager;
        foreach (Document d in dm)
            Hook(d);
        dm.DocumentCreated += (_, e) => Hook(e.Document);
        dm.DocumentToBeDestroyed += (_, e) => Warn(e.Document, "fechando");
    }

    private static void Hook(Document? d)
    {
        if (d is null)
            return;
        d.Database.BeginSave += (_, _) => Warn(d, "salvando");
    }

    private static void Warn(Document? d, string when)
    {
        try
        {
            if (d is null)
                return;
            int n = Count(d.Database);
            if (n == 0)
                return;
            string msg = $"{n} rebaixo(s) pendente(s) (lancados sem Rebuild) neste desenho ({when}). O corredor ainda nao mostra esses rebaixos: rode MFREBAIXORECONSTRUIR.";
            d.Editor.WriteMessage("\n[MFREBAIXO] " + msg + "\n");
            PluginLog.Aviso("MFREBAIXO", msg);
        }
        catch
        {
            // Desenho fechando: nada a fazer.
        }
    }
}
