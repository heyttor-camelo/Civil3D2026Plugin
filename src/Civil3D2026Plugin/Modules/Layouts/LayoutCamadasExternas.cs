using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Viewport de outro desenho, para a lista de escolha.</summary>
internal sealed record ViewportExterna(string Rotulo, long Handle);

/// <summary>
/// Importar layer state de uma viewport de outro DWG: o VP freeze e os overrides dela viram
/// um layer state temporario na origem, que e trazido para o desenho atual, restaurado nas
/// viewports escolhidas e apagado dos dois desenhos.
/// </summary>
internal static class LayoutCamadasExternas
{
    private const string Mod = "Layouts";

    /// <summary>Viewports de modelo de todos os layouts do arquivo, na ordem das abas.</summary>
    public static List<ViewportExterna> Viewports(string arquivo)
    {
        var lista = new List<(int Tab, ViewportExterna Vp)>();
        Abrir(arquivo, (db, _) =>
        {
            double fator = LayoutReader.FatorUnidade(db);
            using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dic)
            {
                var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                if (lay.ModelType)
                    continue;
                var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
                HashSet<ObjectId> papel = LayoutReader.ViewportsDoPapel(tr, lay, btr);
                int n = 0;
                foreach (ObjectId id in btr)
                {
                    if (papel.Contains(id) || tr.GetObject(id, OpenMode.ForRead) is not Viewport vp)
                        continue;
                    n++;
                    string escala = LayoutOps.EscalaTexto(vp.CustomScale, fator);
                    lista.Add((lay.TabOrder, new ViewportExterna(
                        $"{lay.LayoutName}  #{n}   {escala}   {vp.Width:0.#} x {vp.Height:0.#}", id.Handle.Value)));
                }
            }
            tr.Commit();
        });
        return lista.OrderBy(x => x.Tab).Select(x => x.Vp).ToList();
    }

    public static OpResult Importar(Document doc, string arquivo, ViewportExterna origem, IReadOnlyList<ObjectId> destinos)
    {
        if (destinos.Count == 0)
            throw new InvalidOperationException("Escolha as viewports que recebem o layer state.");

        string tmp = "C3D_TMP_IMPORT_" + origem.Handle.ToString("X");
        Database db = doc.Database;
        var avisos = new List<string>();

        using (doc.LockDocument())
        {
            LayerStateManager destino = db.LayerStateManager;
            try
            {
                Abrir(arquivo, (src, srcDoc) =>
                {
                    using DocumentLock? trava = srcDoc?.LockDocument();
                    if (!src.TryGetObjectId(new Handle(origem.Handle), out ObjectId vpId) || vpId.IsErased)
                        throw new InvalidOperationException("A viewport nao existe mais no arquivo de origem.");

                    LayerStateManager lsm = src.LayerStateManager;
                    if (lsm.HasLayerState(tmp))
                        lsm.DeleteLayerState(tmp);
                    lsm.SaveLayerState(tmp, LayerStateMasks.CurrentViewport | LayerStateMasks.Color | LayerStateMasks.LineType |
                                            LayerStateMasks.LineWeight | LayerStateMasks.PlotStyle | LayerStateMasks.Transparency, vpId);
                    try
                    {
                        if (destino.HasLayerState(tmp))
                            destino.DeleteLayerState(tmp);
                        Trazer(destino, lsm, tmp, src);
                    }
                    finally
                    {
                        lsm.DeleteLayerState(tmp);
                    }
                });

                LayoutSheetSeries.Camadas(db, tmp, ObjectId.Null, destinos, "importada", avisos);
            }
            finally
            {
                try
                {
                    if (destino.HasLayerState(tmp))
                        destino.DeleteLayerState(tmp);
                }
                catch (System.Exception)
                {
                }
            }
        }

        foreach (string a in avisos)
            PluginLog.Aviso(Mod, "Importar layer state: " + a);
        return new OpResult(destinos.Count - avisos.Count, avisos.Count,
            $"Layer state de {Path.GetFileName(arquivo)} ({origem.Rotulo.Split("   ")[0]}) aplicado em {destinos.Count} viewport(s)" +
            (avisos.Count > 0 ? $"; {avisos.Count} aviso(s) no log (Ctrl+Shift+L)." : "."));
    }

    /// <summary>Traz o layer state da origem; se o import direto falhar, passa por um arquivo .las temporario.</summary>
    private static void Trazer(LayerStateManager destino, LayerStateManager origem, string nome, Database src)
    {
        try
        {
            destino.ImportLayerStateFromDb(nome, src);
            if (destino.HasLayerState(nome))
                return;
        }
        catch (System.Exception ex)
        {
            PluginLog.Diag(Mod, $"ImportLayerStateFromDb falhou ({ex.Message}); usando arquivo .las");
        }

        string las = Path.Combine(Path.GetTempPath(), nome + ".las");
        try
        {
            origem.ExportLayerState(nome, las);
            destino.ImportLayerState(las);
        }
        finally
        {
            try
            {
                File.Delete(las);
            }
            catch (System.Exception)
            {
            }
        }
        if (!destino.HasLayerState(nome))
            throw new InvalidOperationException("O layer state da viewport nao pode ser trazido do outro desenho.");
    }

    /// <summary>
    /// Database do arquivo: a do documento, se ele estiver aberto no editor (le o que esta na tela),
    /// senao uma copia lida do disco, descartada no fim.
    /// </summary>
    private static void Abrir(string arquivo, Action<Database, Document?> acao)
    {
        foreach (Document d in AcApp.DocumentManager)
        {
            if (string.Equals(Path.GetFullPath(d.Name), Path.GetFullPath(arquivo), StringComparison.OrdinalIgnoreCase))
            {
                acao(d.Database, d);
                return;
            }
        }
        using var db = new Database(false, true);
        db.ReadDwgFile(arquivo, FileOpenMode.OpenForReadAndAllShare, true, null);
        db.CloseInput(true);
        acao(db, null);
    }
}
