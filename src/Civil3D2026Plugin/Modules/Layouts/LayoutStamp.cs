using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Um bloco com atributos presente nos layouts (candidato a carimbo).</summary>
internal sealed record StampBlock(string Nome, int Layouts, int Referencias, IReadOnlyList<string> Tags);

/// <summary>Valor atual de uma tag num layout (para a tabela do painel).</summary>
internal sealed record StampValue(string Layout, string Tag, string Valor);

/// <summary>
/// Carimbo em lote: le os blocos com atributos dos layouts e escreve valores nas tags
/// escolhidas em varios layouts de uma vez (ex-JOBEPLACE, sem valores fixos no codigo).
/// </summary>
internal static class LayoutStamp
{
    /// <summary>Blocos com atributos encontrados nos layouts, com as tags de cada um.</summary>
    public static List<StampBlock> LerBlocos(Database db)
    {
        var porNome = new Dictionary<string, (HashSet<string> Layouts, int Refs, List<string> Tags)>(StringComparer.OrdinalIgnoreCase);

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach ((string layout, BlockTableRecord btr) in Layouts(tr, db))
            {
                foreach (ObjectId id in btr)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference br || br.AttributeCollection.Count == 0)
                        continue;

                    string nome = NomeEfetivo(tr, br);
                    if (!porNome.TryGetValue(nome, out var info))
                        porNome[nome] = info = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, new List<string>());

                    info.Layouts.Add(layout);
                    info.Refs++;
                    foreach (ObjectId aid in br.AttributeCollection)
                    {
                        if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference att &&
                            !att.IsConstant &&
                            !info.Tags.Contains(att.Tag, StringComparer.OrdinalIgnoreCase))
                            info.Tags.Add(att.Tag);
                    }
                    porNome[nome] = info;
                }
            }
            tr.Commit();
        }

        return porNome
            .Select(kv => new StampBlock(kv.Key, kv.Value.Layouts.Count, kv.Value.Refs, kv.Value.Tags))
            .OrderByDescending(b => b.Layouts)
            .ThenBy(b => b.Nome, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Valores atuais das tags do bloco em cada layout (primeira referencia de cada layout).</summary>
    public static List<StampValue> LerValores(Database db, string bloco, IEnumerable<string> layouts)
    {
        var filtro = new HashSet<string>(layouts, StringComparer.OrdinalIgnoreCase);
        var lista = new List<StampValue>();

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach ((string layout, BlockTableRecord btr) in Layouts(tr, db))
            {
                if (!filtro.Contains(layout))
                    continue;

                foreach (ObjectId id in btr)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference br || br.AttributeCollection.Count == 0)
                        continue;
                    if (!NomeEfetivo(tr, br).Equals(bloco, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (ObjectId aid in br.AttributeCollection)
                        if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference att && !att.IsConstant)
                            lista.Add(new StampValue(layout, att.Tag, att.TextString));
                    break;
                }
            }
            tr.Commit();
        }

        return lista;
    }

    /// <summary>
    /// Escreve os valores nas tags do bloco em cada layout escolhido. Todas as referencias
    /// do bloco no layout sao atualizadas. Tags ausentes no bloco sao ignoradas.
    /// </summary>
    public static OpResult Aplicar(Document doc, string bloco, IEnumerable<string> layouts, IReadOnlyDictionary<string, string> valores)
    {
        if (valores.Count == 0)
            return new OpResult(0, 0, "Nenhuma tag com valor para escrever.");

        int atributos = 0, blocos = 0;
        var layoutsFeitos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filtro = new HashSet<string>(layouts, StringComparer.OrdinalIgnoreCase);

        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach ((string layout, BlockTableRecord btr) in Layouts(tr, db))
            {
                if (!filtro.Contains(layout))
                    continue;

                foreach (ObjectId id in btr)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference br || br.AttributeCollection.Count == 0)
                        continue;
                    if (!NomeEfetivo(tr, br).Equals(bloco, StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool mudou = false;
                    foreach (ObjectId aid in br.AttributeCollection)
                    {
                        if (tr.GetObject(aid, OpenMode.ForRead) is not AttributeReference att || att.IsConstant)
                            continue;
                        if (!valores.TryGetValue(att.Tag, out string? novo) || novo is null)
                            continue;
                        if (att.TextString == novo)
                            continue;

                        att.UpgradeOpen();
                        att.TextString = novo;
                        atributos++;
                        mudou = true;
                    }

                    if (mudou)
                    {
                        blocos++;
                        layoutsFeitos.Add(layout);
                    }
                }
            }
            tr.Commit();
        }

        return new OpResult(atributos, 0,
            atributos > 0
                ? $"{atributos} atributo(s) em {blocos} bloco(s) de {layoutsFeitos.Count} layout(s)."
                : "Nada a alterar: os valores ja eram esses ou o bloco nao esta nos layouts marcados.");
    }

    // ------------------------------------------------------------------

    private static IEnumerable<(string Nome, BlockTableRecord Btr)> Layouts(Transaction tr, Database db)
    {
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dic)
        {
            var layout = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (layout.ModelType)
                continue;
            yield return (layout.LayoutName, (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead));
        }
    }

    /// <summary>Nome do bloco; em bloco dinamico, o nome da definicao original.</summary>
    private static string NomeEfetivo(Transaction tr, BlockReference br)
    {
        ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
        return ((BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead)).Name;
    }
}
