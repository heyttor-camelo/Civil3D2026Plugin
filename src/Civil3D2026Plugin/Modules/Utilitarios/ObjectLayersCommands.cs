using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.Settings;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Utilitarios;

/// <summary>
/// C3DOBJLAYERS: copia so a aba "Object Layers" das Drawing Settings de outro DWG
/// (layer, modifier, value e locked de cada tipo de objeto, mais a opcao
/// "Immediate and independent layer on/off control"). Unidades, zona,
/// transformacao, abreviacoes e ambient settings nao sao tocados.
/// </summary>
public sealed class ObjectLayersCommands
{
    private const string Modulo = "C3DOBJLAYERS";

    [CommandMethod(C3DCommands.Utilitarios.ObjLayers, CommandFlags.Modal)]
    public void CopiarObjectLayers()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        Database destDb = doc.Database;

        var pfo = new PromptOpenFileOptions("\nDWG/DWT de origem das Object Layers")
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

            using (doc.LockDocument())
                Copiar(srcDb, destDb, ed, Path.GetFileName(origem));
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nNao foi possivel copiar as Object Layers: {ex.Message}");
            PluginLog.Erro(Modulo, $"Falha ao copiar de {origem}", ex);
        }
        finally
        {
            if (srcPropria)
                srcDb?.Dispose();
        }
    }

    private static void Copiar(Database srcDb, Database destDb, Editor ed, string nomeOrigem)
    {
        SettingsObjectLayers srcOL = CivilDocument.GetCivilDocument(srcDb).Settings.DrawingSettings.ObjectLayerSettings;
        SettingsObjectLayers dstOL = CivilDocument.GetCivilDocument(destDb).Settings.DrawingSettings.ObjectLayerSettings;

        var tipos = Enum.GetValues(typeof(SettingsObjectLayerType)).Cast<SettingsObjectLayerType>().Distinct().ToList();

        // 1) Layers da origem usadas na aba (nome -> id na origem).
        var layersOrigem = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        var nomePorTipo = new Dictionary<SettingsObjectLayerType, string>();
        using (Transaction tr = srcDb.TransactionManager.StartOpenCloseTransaction())
        {
            foreach (SettingsObjectLayerType t in tipos)
            {
                try
                {
                    SettingsObjectLayer s = srcOL.GetObjectLayerSetting(t);
                    if (s.LayerId.IsNull || s.LayerId.IsErased)
                        continue;
                    var ltr = (LayerTableRecord)tr.GetObject(s.LayerId, OpenMode.ForRead);
                    nomePorTipo[t] = ltr.Name;
                    layersOrigem[ltr.Name] = s.LayerId;
                }
                catch
                {
                    // tipo que esta versao do Civil 3D nao expoe
                }
            }
            tr.Commit();
        }

        if (nomePorTipo.Count == 0)
        {
            ed.WriteMessage($"\nNenhuma Object Layer lida de {nomeOrigem}.");
            return;
        }

        // 2) Clona para o destino as layers que ainda nao existem (as existentes ficam como estao).
        var faltando = new ObjectIdCollection();
        using (Transaction tr = destDb.TransactionManager.StartOpenCloseTransaction())
        {
            var lt = (LayerTable)tr.GetObject(destDb.LayerTableId, OpenMode.ForRead);
            foreach (KeyValuePair<string, ObjectId> kv in layersOrigem)
                if (!lt.Has(kv.Key))
                    faltando.Add(kv.Value);
            tr.Commit();
        }
        if (faltando.Count > 0)
            srcDb.WblockCloneObjects(faltando, destDb.LayerTableId, new IdMapping(), DuplicateRecordCloning.Ignore, false);

        // 3) Aplica layer, modifier, value e locked.
        int alterados = 0, iguais = 0, falhas = 0;
        using (Transaction tr = destDb.TransactionManager.StartTransaction())
        {
            var lt = (LayerTable)tr.GetObject(destDb.LayerTableId, OpenMode.ForRead);

            foreach (KeyValuePair<SettingsObjectLayerType, string> kv in nomePorTipo)
            {
                try
                {
                    SettingsObjectLayer src = srcOL.GetObjectLayerSetting(kv.Key);
                    SettingsObjectLayer dst = dstOL.GetObjectLayerSetting(kv.Key);
                    ObjectId layerDestino = lt[kv.Value];

                    bool igual = dst.LayerId == layerDestino
                                 && dst.Modifier == src.Modifier
                                 && string.Equals(dst.ModifierValue ?? string.Empty, src.ModifierValue ?? string.Empty, StringComparison.Ordinal)
                                 && dst.Locked == src.Locked;
                    if (igual)
                    {
                        iguais++;
                        continue;
                    }

                    // Destravar antes de escrever; o Locked da origem e aplicado por ultimo.
                    if (dst.Locked)
                        dst.Locked = false;
                    dst.LayerId = layerDestino;
                    dst.Modifier = src.Modifier;
                    dst.ModifierValue = src.ModifierValue ?? string.Empty;
                    dst.Locked = src.Locked;
                    alterados++;
                    PluginLog.Diag(Modulo, $"{kv.Key}: {kv.Value} / {src.Modifier} '{src.ModifierValue}' / locked={src.Locked}");
                }
                catch (System.Exception ex)
                {
                    falhas++;
                    ed.WriteMessage($"\n  {kv.Key}: {ex.Message}");
                    PluginLog.Aviso(Modulo, $"{kv.Key}: {ex.Message}");
                }
            }

            // Checkbox "Immediate and independent layer on/off control of display components".
            try
            {
                dstOL.ObjectControlledByLayer = srcOL.ObjectControlledByLayer;
            }
            catch (System.Exception ex)
            {
                PluginLog.Aviso(Modulo, $"ObjectControlledByLayer: {ex.Message}");
            }

            tr.Commit();
        }

        string resumo = $"Object Layers de {nomeOrigem}: {alterados} alterada(s), {iguais} ja iguais, " +
                        $"{faltando.Count} layer(s) criada(s)" + (falhas > 0 ? $", {falhas} falha(s)" : string.Empty) + ".";
        ed.WriteMessage($"\n{resumo}");
        PluginLog.Info(Modulo, resumo);

        if (alterados > 0)
            ed.WriteMessage("\nObjetos ja existentes continuam nas layers atuais; a configuracao vale para os objetos criados daqui em diante.");
    }
}
