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
/// C3DCOPIARLAYERS: copia o estado das layers de outro DWG (on/off, freeze, lock, cor,
/// linetype, lineweight, transparencia e plot). As layers de xref casam pelo ARQUIVO do xref,
/// nao pelo nome: "BASE|EIXO" na origem vale para "BASE_v2|EIXO" aqui se os dois xrefs
/// apontam para o mesmo arquivo. Modo Todas tambem copia as layers do proprio desenho, pelo nome.
/// </summary>
public sealed class LayerCopyCommands
{
    private const string Modulo = "C3DCOPIARLAYERS";

    private static string _modo = "Xrefs";

    /// <summary>Estado de uma layer da origem, com o linetype pelo nome.</summary>
    private sealed record EstadoLayer(
        bool Off, bool Frozen, bool Locked, bool Plot,
        Autodesk.AutoCAD.Colors.Color Cor, string Linetype, LineWeight Peso, Autodesk.AutoCAD.Colors.Transparency Transparencia);

    [CommandMethod(C3DCommands.Utilitarios.CopiarLayers, CommandFlags.Modal)]
    public void CopiarLayers()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        Database destDb = doc.Database;

        var pfo = new PromptOpenFileOptions("\nDesenho com o estado das layers desejado")
        {
            Filter = "Desenho ou template (*.dwg;*.dwt)|*.dwg;*.dwt",
            PreferCommandLine = false
        };
        if (!string.IsNullOrEmpty(destDb.Filename) && Path.GetDirectoryName(destDb.Filename) is string pasta && Directory.Exists(pasta))
            pfo.InitialDirectory = pasta;
        PromptFileNameResult pfr = ed.GetFileNameForOpen(pfo);
        if (pfr.Status != PromptStatus.OK)
            return;
        string origem = pfr.StringResult;

        if (string.Equals(Path.GetFullPath(origem), Path.GetFullPath(destDb.Filename), StringComparison.OrdinalIgnoreCase))
        {
            ed.WriteMessage("\nO arquivo de origem e o proprio desenho atual.");
            return;
        }

        var pk = new PromptKeywordOptions($"\nCopiar o estado das layers [Xrefs/Todas] <{_modo}>: ", "Xrefs Todas") { AllowNone = true };
        PromptResult rk = ed.GetKeywords(pk);
        if (rk.Status == PromptStatus.OK)
            _modo = rk.StringResult;
        else if (rk.Status != PromptStatus.None)
            return;
        bool todas = _modo == "Todas";

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

            Dictionary<string, string> xrefsOrigem = XrefsPorArquivo(srcDb);
            Dictionary<string, string> xrefsDestino = XrefsPorArquivo(destDb);

            // Prefixo do xref aqui -> prefixo do mesmo arquivo na origem.
            var prefixos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in xrefsDestino)
                if (xrefsOrigem.TryGetValue(kv.Key, out string? nomeOrigem))
                    prefixos[kv.Value] = nomeOrigem;

            if (prefixos.Count == 0 && !todas)
            {
                ed.WriteMessage($"\nNenhum xref deste desenho aponta para um arquivo que tambem esteja em {Path.GetFileName(origem)}.");
                return;
            }

            Dictionary<string, EstadoLayer> estados = LerEstados(srcDb);
            using (doc.LockDocument())
                Aplicar(destDb, ed, estados, prefixos, todas, Path.GetFileName(origem));
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nNao foi possivel copiar as layers: {ex.Message}");
            PluginLog.Erro(Modulo, $"Falha ao copiar de {origem}", ex);
        }
        finally
        {
            if (srcPropria)
                srcDb?.Dispose();
        }
    }

    /// <summary>Arquivo do xref (so o nome, sem pasta) -> nome do xref no desenho.</summary>
    private static Dictionary<string, string> XrefsPorArquivo(Database db)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId id in bt)
        {
            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!btr.IsFromExternalReference || string.IsNullOrWhiteSpace(btr.PathName))
                continue;
            string arquivo = Path.GetFileName(btr.PathName.Trim());
            result.TryAdd(arquivo, btr.Name);
        }
        tr.Commit();
        return result;
    }

    private static Dictionary<string, EstadoLayer> LerEstados(Database db)
    {
        var result = new Dictionary<string, EstadoLayer>(StringComparer.OrdinalIgnoreCase);
        using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId id in lt)
        {
            var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            string linetype = "";
            if (!ltr.LinetypeObjectId.IsNull && tr.GetObject(ltr.LinetypeObjectId, OpenMode.ForRead) is LinetypeTableRecord ltt)
                linetype = ltt.Name;
            result[ltr.Name] = new EstadoLayer(ltr.IsOff, ltr.IsFrozen, ltr.IsLocked, ltr.IsPlottable,
                ltr.Color, linetype, ltr.LineWeight, ltr.Transparency);
        }
        tr.Commit();
        return result;
    }

    /// <summary>Nome na origem da layer daqui: troca o prefixo do xref; null se nao tem par.</summary>
    private static string? NomeNaOrigem(string nome, Dictionary<string, string> prefixos, bool todas)
    {
        int bar = nome.IndexOf('|');
        if (bar < 0)
            return todas ? nome : null;
        string prefixo = nome.Substring(0, bar);
        return prefixos.TryGetValue(prefixo, out string? p) ? p + nome.Substring(bar) : null;
    }

    /// <summary>Linetype da origem com o prefixo do xref trocado pelo daqui.</summary>
    private static string LinetypeAqui(string linetype, Dictionary<string, string> prefixos)
    {
        int bar = linetype.IndexOf('|');
        if (bar < 0)
            return linetype;
        string prefixo = linetype.Substring(0, bar);
        foreach (KeyValuePair<string, string> kv in prefixos)
            if (string.Equals(kv.Value, prefixo, StringComparison.OrdinalIgnoreCase))
                return kv.Key + linetype.Substring(bar);
        return linetype;
    }

    private static void Aplicar(Database db, Editor ed, Dictionary<string, EstadoLayer> estados,
        Dictionary<string, string> prefixos, bool todas, string nomeOrigem)
    {
        int alteradas = 0, iguais = 0, semPar = 0, falhas = 0;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                string? nome = NomeNaOrigem(ltr.Name, prefixos, todas);
                if (nome is null)
                    continue;
                if (!estados.TryGetValue(nome, out EstadoLayer? e))
                {
                    semPar++;
                    continue;
                }

                ObjectId linetype = ltr.LinetypeObjectId;
                string ltNome = LinetypeAqui(e.Linetype, prefixos);
                if (ltNome.Length > 0 && ltt.Has(ltNome))
                    linetype = ltt[ltNome];
                bool podeCongelar = id != db.Clayer;

                bool igual = ltr.IsOff == e.Off && (ltr.IsFrozen == e.Frozen || !podeCongelar) && ltr.IsLocked == e.Locked &&
                             ltr.IsPlottable == e.Plot && ltr.Color == e.Cor && ltr.LinetypeObjectId == linetype &&
                             ltr.LineWeight == e.Peso && ltr.Transparency == e.Transparencia;
                if (igual)
                {
                    iguais++;
                    continue;
                }

                try
                {
                    ltr.UpgradeOpen();
                    ltr.IsOff = e.Off;
                    if (podeCongelar)
                        ltr.IsFrozen = e.Frozen;
                    ltr.IsLocked = e.Locked;
                    ltr.IsPlottable = e.Plot;
                    ltr.Color = e.Cor;
                    ltr.LinetypeObjectId = linetype;
                    ltr.LineWeight = e.Peso;
                    ltr.Transparency = e.Transparencia;
                    alteradas++;
                }
                catch (System.Exception ex)
                {
                    falhas++;
                    PluginLog.Aviso(Modulo, $"{ltr.Name}: {ex.Message}");
                }
            }
            tr.Commit();
        }

        string xrefs = prefixos.Count == 0 ? "nenhum xref em comum"
            : string.Join(", ", prefixos.Select(kv => string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase) ? kv.Key : $"{kv.Key} = {kv.Value}"));
        string resumo = $"Layers de {nomeOrigem}: {alteradas} alterada(s), {iguais} ja iguais, {semPar} sem par na origem" +
                        (falhas > 0 ? $", {falhas} falha(s) (log: Ctrl+Shift+L)" : "") + $". Xrefs: {xrefs}.";
        ed.WriteMessage($"\n{resumo}");
        PluginLog.Info(Modulo, resumo);

        if (alteradas > 0 && Convert.ToInt32(AcApp.GetSystemVariable("VISRETAIN")) == 0)
        {
            AcApp.SetSystemVariable("VISRETAIN", 1);
            ed.WriteMessage("\nVISRETAIN estava em 0 (as layers dos xrefs voltariam ao recarregar); passou para 1.");
        }
        ed.Regen();
    }
}
