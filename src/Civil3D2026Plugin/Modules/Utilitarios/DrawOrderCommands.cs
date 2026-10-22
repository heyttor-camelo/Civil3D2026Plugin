using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Utilitarios;

/// <summary>
/// C3DORDEMDESENHO: copia a ordem de desenho (DRAWORDER) de outro DWG para o desenho atual.
/// Modo Layers: cada layer vai para a posicao que tem na origem (vale entre quaisquer
/// desenhos com os mesmos nomes de layer). Modo Objetos: cada objeto vai para a posicao
/// exata do objeto de mesmo handle (so faz sentido se um desenho for copia do outro).
/// Objetos sem correspondencia na origem ficam onde estao.
/// </summary>
public sealed class DrawOrderCommands
{
    private const string Modulo = "C3DORDEMDESENHO";

    private static string _modo = "Layers";
    private static string _espacos = "Model";

    /// <summary>Um objeto na ordem de desenho da origem: handle, classe DXF e layer.</summary>
    private sealed record ItemOrigem(long Handle, string Classe, string Layer);

    [CommandMethod(C3DCommands.Utilitarios.OrdemDesenho, CommandFlags.Modal)]
    public void CopiarOrdemDesenho()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        Database destDb = doc.Database;

        var pfo = new PromptOpenFileOptions("\nDesenho com a ordem de desenho desejada")
        {
            Filter = "Desenho ou template (*.dwg;*.dwt)|*.dwg;*.dwt",
            PreferCommandLine = false
        };
        PromptFileNameResult pfr = ed.GetFileNameForOpen(pfo);
        if (pfr.Status != PromptStatus.OK)
            return;
        string origem = pfr.StringResult;

        if (string.Equals(Path.GetFullPath(origem), Path.GetFullPath(destDb.Filename), StringComparison.OrdinalIgnoreCase))
        {
            ed.WriteMessage("\nO arquivo de origem e o proprio desenho atual.");
            return;
        }

        var pkModo = new PromptKeywordOptions(
            $"\nCopiar a ordem por [Layers/Objetos] <{_modo}>: ", "Layers Objetos") { AllowNone = true };
        PromptResult rModo = ed.GetKeywords(pkModo);
        if (rModo.Status == PromptStatus.OK)
            _modo = rModo.StringResult;
        else if (rModo.Status != PromptStatus.None)
            return;

        var pkEsp = new PromptKeywordOptions(
            $"\nEm quais espacos [Model/Layouts/Todos] <{_espacos}>: ", "Model Layouts Todos") { AllowNone = true };
        PromptResult rEsp = ed.GetKeywords(pkEsp);
        if (rEsp.Status == PromptStatus.OK)
            _espacos = rEsp.StringResult;
        else if (rEsp.Status != PromptStatus.None)
            return;

        bool porLayer = _modo == "Layers";
        bool model = _espacos != "Layouts";
        bool layouts = _espacos != "Model";

        // Se a origem estiver aberta no editor, usa a database dela (le o que
        // esta na tela, inclusive o que nao foi salvo, e evita o arquivo travado).
        Database? srcDb = null;
        bool srcPropria = false;
        foreach (Document d in AcApp.DocumentManager)
        {
            if (string.Equals(Path.GetFullPath(d.Name), Path.GetFullPath(origem), StringComparison.OrdinalIgnoreCase))
            {
                srcDb = d.Database;
                break;
            }
        }

        try
        {
            if (srcDb is null)
            {
                srcDb = new Database(false, true);
                srcPropria = true;
                srcDb.ReadDwgFile(origem, FileOpenMode.OpenForReadAndAllShare, true, null);
                srcDb.CloseInput(true);
            }

            Dictionary<string, List<ItemOrigem>> ordemOrigem = LerOrdens(srcDb, model, layouts);
            using (doc.LockDocument())
                Aplicar(ordemOrigem, destDb, ed, porLayer, model, layouts, Path.GetFileName(origem));
            ed.Regen();
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nNao foi possivel copiar a ordem de desenho: {ex.Message}");
            PluginLog.Erro(Modulo, $"Falha ao copiar de {origem}", ex);
        }
        finally
        {
            if (srcPropria)
                srcDb?.Dispose();
        }
    }

    /// <summary>Nome do espaco usado para casar origem e destino: "Model" ou o nome do layout.</summary>
    private static Dictionary<string, ObjectId> Espacos(Transaction tr, Database db, bool model, bool layouts)
    {
        var result = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dic)
        {
            var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (lay.ModelType ? model : layouts)
                result[lay.ModelType ? "Model" : lay.LayoutName] = lay.BlockTableRecordId;
        }
        return result;
    }

    private static Dictionary<string, List<ItemOrigem>> LerOrdens(Database srcDb, bool model, bool layouts)
    {
        var result = new Dictionary<string, List<ItemOrigem>>(StringComparer.OrdinalIgnoreCase);
        using Transaction tr = srcDb.TransactionManager.StartOpenCloseTransaction();
        foreach (KeyValuePair<string, ObjectId> esp in Espacos(tr, srcDb, model, layouts))
        {
            var btr = (BlockTableRecord)tr.GetObject(esp.Value, OpenMode.ForRead);
            var itens = new List<ItemOrigem>();
            foreach (ObjectId id in OrdemAtual(tr, btr))
            {
                if (tr.GetObject(id, OpenMode.ForRead) is Entity ent)
                    itens.Add(new ItemOrigem(id.Handle.Value, id.ObjectClass.DxfName, ent.Layer));
            }
            result[esp.Key] = itens;
        }
        tr.Commit();
        return result;
    }

    /// <summary>
    /// Ordem de desenho do espaco, de baixo para cima. Sem tabela de ordem
    /// (ou se ela nao puder ser lida), vale a ordem em que os objetos foram criados.
    /// </summary>
    private static List<ObjectId> OrdemAtual(Transaction tr, BlockTableRecord btr)
    {
        try
        {
            var dot = (DrawOrderTable)tr.GetObject(btr.DrawOrderTableId, OpenMode.ForRead);
            return dot.GetFullDrawOrder(0).Cast<ObjectId>().Where(i => !i.IsNull && !i.IsErased).ToList();
        }
        catch (System.Exception ex)
        {
            PluginLog.Diag(Modulo, $"{btr.Name}: tabela de ordem nao lida ({ex.Message}); usando a ordem de criacao");
            return btr.Cast<ObjectId>().Where(i => !i.IsErased).ToList();
        }
    }

    /// <summary>
    /// Posicao de cada layer na origem: a mediana das posicoes dos objetos dela,
    /// para uma layer espalhada nao ser puxada por um objeto solto.
    /// </summary>
    private static Dictionary<string, double> PosicaoLayers(List<ItemOrigem> itens)
    {
        return itens
            .Select((it, i) => (it.Layer, i))
            .GroupBy(x => x.Layer, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    int[] pos = g.Select(x => x.i).ToArray();
                    return pos.Length % 2 == 1
                        ? pos[pos.Length / 2]
                        : (pos[pos.Length / 2 - 1] + pos[pos.Length / 2]) / 2.0;
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static void Aplicar(
        Dictionary<string, List<ItemOrigem>> ordemOrigem,
        Database destDb, Editor ed, bool porLayer, bool model, bool layouts, string nomeOrigem)
    {
        int espacosAlterados = 0, movidos = 0, casados = 0, total = 0;
        var semOrigem = new List<string>();
        var poucoCasados = new List<string>();

        using (Transaction tr = destDb.TransactionManager.StartTransaction())
        {
            foreach (KeyValuePair<string, ObjectId> esp in Espacos(tr, destDb, model, layouts))
            {
                // Por layer, um layout que nao existe na origem usa a ordem do Model dela.
                if (!ordemOrigem.TryGetValue(esp.Key, out List<ItemOrigem>? itens))
                {
                    if (!porLayer || !ordemOrigem.TryGetValue("Model", out itens))
                    {
                        semOrigem.Add(esp.Key);
                        continue;
                    }
                }

                var btr = (BlockTableRecord)tr.GetObject(esp.Value, OpenMode.ForWrite);
                var dot = (DrawOrderTable)tr.GetObject(btr.DrawOrderTableId, OpenMode.ForWrite);
                List<ObjectId> atual = dot.GetFullDrawOrder(0).Cast<ObjectId>().Where(i => !i.IsNull && !i.IsErased).ToList();
                total += atual.Count;

                // Chave de cada objeto do destino na ordem da origem; null = sem correspondencia.
                var chave = new double?[atual.Count];
                if (porLayer)
                {
                    Dictionary<string, double> posLayer = PosicaoLayers(itens);
                    for (int i = 0; i < atual.Count; i++)
                        if (tr.GetObject(atual[i], OpenMode.ForRead) is Entity ent && posLayer.TryGetValue(ent.Layer, out double p))
                            chave[i] = p;
                }
                else
                {
                    var posHandle = new Dictionary<long, (int Pos, string Classe)>();
                    for (int i = 0; i < itens.Count; i++)
                        posHandle[itens[i].Handle] = (i, itens[i].Classe);
                    for (int i = 0; i < atual.Count; i++)
                        if (posHandle.TryGetValue(atual[i].Handle.Value, out var h)
                            && string.Equals(h.Classe, atual[i].ObjectClass.DxfName, StringComparison.OrdinalIgnoreCase))
                            chave[i] = h.Pos;
                }

                // Os objetos com correspondencia sao reordenados entre si, nas mesmas
                // posicoes que ja ocupavam; os sem correspondencia nao saem do lugar.
                List<int> slots = Enumerable.Range(0, atual.Count).Where(i => chave[i].HasValue).ToList();
                casados += slots.Count;
                if (!porLayer && atual.Count > 0 && slots.Count < atual.Count / 2)
                    poucoCasados.Add($"{esp.Key} ({slots.Count} de {atual.Count})");

                List<ObjectId> reordenados = slots
                    .OrderBy(i => chave[i]!.Value)
                    .ThenBy(i => i)
                    .Select(i => atual[i])
                    .ToList();
                var nova = new List<ObjectId>(atual);
                for (int k = 0; k < slots.Count; k++)
                    nova[slots[k]] = reordenados[k];

                int mudou = Enumerable.Range(0, nova.Count).Count(i => nova[i] != atual[i]);
                if (mudou == 0)
                    continue;

                var col = new ObjectIdCollection();
                foreach (ObjectId id in nova)
                    col.Add(id);
                dot.SetRelativeDrawOrder(col);
                espacosAlterados++;
                movidos += mudou;
                PluginLog.Diag(Modulo, $"{esp.Key}: {slots.Count} com correspondencia, {mudou} mudaram de posicao");
            }
            tr.Commit();
        }

        string modoTxt = porLayer ? "por layer" : "por objeto";
        string resumo = $"Ordem de desenho de {nomeOrigem} ({modoTxt}): {movidos} objeto(s) mudaram de posicao em {espacosAlterados} espaco(s); " +
                        $"{casados} de {total} objeto(s) tinham correspondencia na origem.";
        ed.WriteMessage($"\n{resumo}");
        PluginLog.Info(Modulo, resumo);

        if (semOrigem.Count > 0)
            ed.WriteMessage($"\nSem espaco de mesmo nome na origem: {string.Join(", ", semOrigem)}.");
        if (poucoCasados.Count > 0)
            ed.WriteMessage($"\nPoucos objetos com o mesmo handle em {string.Join(", ", poucoCasados)}: " +
                            "o modo Objetos so serve quando um desenho e copia do outro; tente o modo Layers.");
        if (porLayer && casados < total)
            ed.WriteMessage("\nObjetos em layers que nao existem na origem ficaram onde estavam.");
    }
}
