using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace Civil3D2026Plugin.Diagnostics
{
    public class PluginDiagnosticsCommands
    {
        [CommandMethod(C3DCommands.Diagnostico.TesteDll)]
        public void TesteDll()
        {
            Document doc =
                AcApp.DocumentManager.MdiActiveDocument;

            Editor ed = doc.Editor;

            ed.WriteMessage(
                "\nTESTE OK - DLL reconhecida pelo AutoCAD!");
        }

        /// <summary>Janela com o log de todos os plugins (copiar e mandar para a correcao).</summary>
        [CommandMethod(C3DCommands.Core.Log, CommandFlags.Modal)]
        public void Log()
        {
            Civil3D2026Plugin.Infrastructure.PluginLog.Info("Plugin", "Janela de log aberta (C3DLOG).");
            Civil3D2026Plugin.UI.Common.LogView.AbrirJanela();
        }
    }
}