using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.UI.Ribbon;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(Civil3D2026Plugin.Infrastructure.PluginApplication))]

namespace Civil3D2026Plugin.Infrastructure;

/// <summary>
/// Unico ponto de entrada/saida da extensao.
/// Banner, versao geral, catalogo e inicializacao da UI ficam centralizados aqui.
/// </summary>
public sealed class PluginApplication : IExtensionApplication
{
    public void Initialize()
    {
        // A Ribbon pode ainda nao estar pronta durante o Initialize.
        // O gerenciador tenta imediatamente e, se necessario, repete no Idle.
        RibbonUiManager.Initialize();

        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is not null)
            WriteBanner(doc.Editor, includeDescriptions: false);
    }

    public void Terminate()
    {
        RibbonUiManager.Terminate();
    }

    [CommandMethod(C3DCommands.Core.Help, CommandFlags.Modal)]
    public void ShowHelp()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        WriteBanner(doc.Editor, includeDescriptions: true);
    }

    [CommandMethod(C3DCommands.Core.Ribbon, CommandFlags.Modal)]
    public void ShowRibbon()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        bool created = RibbonUiManager.EnsureRibbon(activate: true);
        if (created)
            RibbonUiManager.WriteStatus(doc.Editor, rebuilt: false);
        else
            doc.Editor.WriteMessage("\nA Ribbon ainda nao esta disponivel. A criacao foi reagendada para o proximo Idle.");
    }

    [CommandMethod(C3DCommands.Core.RibbonReset, CommandFlags.Modal)]
    public void RebuildRibbon()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        bool rebuilt = RibbonUiManager.RebuildRibbon(activate: true);
        if (rebuilt)
            RibbonUiManager.WriteStatus(doc.Editor, rebuilt: true);
        else
            doc.Editor.WriteMessage("\nA Ribbon ainda nao esta disponivel. Tente novamente apos a interface terminar de carregar.");
    }

    private static void WriteBanner(Editor ed, bool includeDescriptions)
    {
        string dllPath = GetAssemblyPath();

        ed.WriteMessage("\n");
        ed.WriteMessage("\n============================================================");
        ed.WriteMessage($"\n Civil3D2026Plugin v{C3DVersions.Plugin}");
        ed.WriteMessage("\n============================================================");
        ed.WriteMessage($"\n DLL carregada: {dllPath}");
        ed.WriteMessage($"\n Ribbon: {RibbonUiManager.TabTitle}");
        ed.WriteMessage("\n");

        CommandCatalog.WriteTree(ed, includeDescriptions);

        ed.WriteMessage("\n============================================================");
        ed.WriteMessage($"\n Use {C3DCommands.Core.Help} para exibir esta arvore novamente.");
        ed.WriteMessage($"\n Use {C3DCommands.Core.Ribbon} para abrir a Ribbon.");
        ed.WriteMessage($"\n Use {C3DCommands.Core.RibbonReset} para reconstruir a Ribbon.");
        ed.WriteMessage("\n============================================================");
        ed.WriteMessage("\n");
    }

    private static string GetAssemblyPath()
    {
        try
        {
            string location = Assembly.GetExecutingAssembly().Location;
            return string.IsNullOrWhiteSpace(location)
                ? "(caminho nao informado pelo runtime)"
                : Path.GetFullPath(location);
        }
        catch (System.Exception)
        {
            return "(caminho indisponivel)";
        }
    }
}
