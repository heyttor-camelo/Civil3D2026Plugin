using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Modules.CorridorTools.PlanVisibility;
using Civil3D2026Plugin.Modules.CorridorTools.SolidArray;
using Civil3D2026Plugin.Modules.LispTools;
using Civil3D2026Plugin.Modules.Viario;
using Civil3D2026Plugin.UI;
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
        // Log comum (C3DLOG / Ctrl+Shift+L): primeiro de tudo, para registrar o resto da carga.
        PluginLog.Iniciar();

        // A Ribbon pode ainda nao estar pronta durante o Initialize.
        // O gerenciador tenta imediatamente e, se necessario, repete no Idle.
        RibbonUiManager.Initialize();

        // Servicos de fundo so dos modulos marcados em C3DMODULOS (os comandos ficam registrados sempre).

        // Tooltip dos estais (C3DSOLIDLIGAR) via Editor.PointMonitor.
        if (ModuleSettings.IsCommandEnabled(C3DCommands.Corridor.SolidLigar))
            SolidLinkTooltip.Initialize();

        // Solidos so no 3D (C3DOCULTARPLANTA): reativa a configuracao salva do usuario.
        if (ModuleSettings.IsCommandEnabled(C3DCommands.Corridor.OcultarPlanta))
            PlanVisibilityManager.Initialize();

        // Rede viaria (C3DVIARIO): bordos e intersecoes associativos aos eixos.
        if (ModuleSettings.IsCategoryEnabled("Viario"))
            ViarioSync.Initialize();

        // HYDRO (C3DHYDRO): dica no cursor e menu de contexto em tubos e estruturas da Pipe Network.
        if (ModuleSettings.IsCategoryEnabled("Hydro"))
        {
            Civil3D2026Plugin.Modules.Hydro.HydroTooltip.Initialize();
            Civil3D2026Plugin.Modules.Hydro.HydroContextMenu.Initialize();
            Civil3D2026Plugin.Modules.Hydro.HydroSync.Initialize();
        }

        // Visualizador de secao (C3DSECAO): contorno das caixas de corte em planta e atualizacao da janela.
        if (ModuleSettings.IsCommandEnabled(C3DCommands.Secao.Painel))
            Civil3D2026Plugin.Modules.Secao.SecaoCommands.Initialize();

        // Mapa-chave automatico (C3DMAPACHAVE): refaz o mapa quando viewports ou layouts mudam.
        if (ModuleSettings.IsCategoryEnabled("Layouts"))
            Civil3D2026Plugin.Modules.Layouts.KeyMapSync.Initialize();

        // Modo leve das viewports (C3DLAYOUT > Viewports > Modo leve): religa/desliga ao trocar de layout, se ativado.
        if (ModuleSettings.IsCategoryEnabled("Layouts"))
            Civil3D2026Plugin.Modules.Layouts.LayoutViewportPower.Initialize();

        // Pranchas do eixo (C3DPRANCHAS): mover um frame ou editar o eixo realinha as folhas.
        if (ModuleSettings.IsCategoryEnabled("Layouts"))
            Civil3D2026Plugin.Modules.Layouts.SheetSeriesSync.Initialize();

        // LISPs embutidas (C3DLISP): extrai e registra os comandos por autoload em cada desenho.
        if (ModuleSettings.IsCategoryEnabled("LISPs"))
            LispModule.Initialize();

        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is not null)
            WriteBanner(doc.Editor, includeDescriptions: false);
    }

    public void Terminate()
    {
        PluginLog.Info("Plugin", "Fim da sessao.");
        LispModule.Terminate();
        Civil3D2026Plugin.Modules.Layouts.SheetSeriesSync.Terminate();
        Civil3D2026Plugin.Modules.Layouts.LayoutViewportPower.Terminate();
        Civil3D2026Plugin.Modules.Layouts.KeyMapSync.Terminate();
        Civil3D2026Plugin.Modules.Secao.SecaoCommands.Terminate();
        Civil3D2026Plugin.Modules.Hydro.HydroContextMenu.Terminate();
        Civil3D2026Plugin.Modules.Hydro.HydroTooltip.Terminate();
        Civil3D2026Plugin.Modules.Hydro.HydroSync.Terminate();
        ViarioSync.Terminate();
        PlanVisibilityManager.Terminate();
        SolidLinkTooltip.Terminate();
        RibbonUiManager.Terminate();
        PluginLog.Encerrar();
    }

    [CommandMethod(C3DCommands.Core.Help, CommandFlags.Modal)]
    public void ShowHelp()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        WriteBanner(doc.Editor, includeDescriptions: true);
    }

    [CommandMethod(C3DCommands.Core.Modulos, CommandFlags.Modal)]
    public void ShowModules() => ModulesWindow.Open();

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
        LispModule.WriteTree(ed, includeDescriptions);

        ed.WriteMessage("\n============================================================");
        ed.WriteMessage($"\n Use {C3DCommands.Core.Help} para exibir esta arvore novamente.");
        ed.WriteMessage($"\n Use {C3DCommands.Core.Ribbon} para abrir a Ribbon.");
        ed.WriteMessage($"\n Use {C3DCommands.Core.RibbonReset} para reconstruir a Ribbon.");
        ed.WriteMessage($"\n Use {C3DCommands.Core.Modulos} para escolher quais modulos aparecem na Ribbon" +
                        (ModuleSettings.DisabledCount > 0 ? $" ({ModuleSettings.DisabledCount} desativado(s))." : "."));
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
