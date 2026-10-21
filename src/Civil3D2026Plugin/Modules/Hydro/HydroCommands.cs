using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>Comandos do painel HYDRO (abrir em cada categoria) e HYAPLICAR (edicoes pedidas pelo painel).</summary>
public sealed class HydroCommands
{
    private const string Module = "Hydro";

    [CommandMethod(C3DCommands.Hydro.Painel, CommandFlags.Modal)]
    public void Painel() => Open(null);

    [CommandMethod(C3DCommands.Hydro.Redes, CommandFlags.Modal)]
    public void Redes() => Open("REDES");

    [CommandMethod(C3DCommands.Hydro.Estruturas, CommandFlags.Modal)]
    public void Estruturas() => Open("ESTRUTURAS");

    [CommandMethod(C3DCommands.Hydro.Tubos, CommandFlags.Modal)]
    public void Tubos() => Open("TUBOS");

    [CommandMethod(C3DCommands.Hydro.Bacias, CommandFlags.Modal)]
    public void Bacias() => Open("BACIAS");

    [CommandMethod(C3DCommands.Hydro.Calculo, CommandFlags.Modal)]
    public void Calculo() => Open("CALCULO");

    [CommandMethod(C3DCommands.Hydro.Verificacao, CommandFlags.Modal)]
    public void Verificacao() => Open("VERIFICACAO");

    [CommandMethod(C3DCommands.Hydro.Saidas, CommandFlags.Modal)]
    public void Saidas() => Open("SAIDAS");

    private static void Open(string? category)
    {
        try
        {
            HydroPanel.ShowPanel(category);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Module, "Nao foi possivel abrir o painel", ex);
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n[HYDRO] Erro ao abrir o painel: " + ex.Message);
        }
    }

    /// <summary>Executa as edicoes enfileiradas pelo painel: uma transacao por acao, um UNDO por comando.</summary>
    [CommandMethod(C3DCommands.Hydro.Aplicar, CommandFlags.Modal | CommandFlags.NoHistory)]
    public void Aplicar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        int ok = 0, failed = 0;
        while (HydroApply.TryDequeue(out string what, out Action<AcadDb.Database> action))
        {
            try
            {
                using (PluginLog.Medir(Module, what))
                    action(doc.Database);
                ok++;
                ed.WriteMessage($"\n[HYDRO] {what}: ok.");
            }
            catch (System.Exception ex)
            {
                failed++;
                PluginLog.Erro(Module, what + " falhou", ex);
                ed.WriteMessage($"\n[HYDRO] {what} falhou: {ex.Message}");
            }
        }

        if (ok + failed == 0)
            ed.WriteMessage("\n[HYDRO] Nada a aplicar.");
        HydroPanel.NotifyChanged();
    }
}
