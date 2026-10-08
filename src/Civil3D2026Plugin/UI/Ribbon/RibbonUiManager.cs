using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Windows;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.UI.Ribbon;

/// <summary>
/// Cria a aba "C3D Tools" a partir do CommandCatalog.
///
/// A Ribbon e somente uma camada de interface:
/// - nomes continuam centralizados em CommandNames;
/// - descricoes continuam centralizadas em CommandCatalog;
/// - clique executa o mesmo CommandMethod da linha de comando.
///
/// Assim, uma futura GUI/palette pode reutilizar o mesmo catalogo.
/// </summary>
public static class RibbonUiManager
{
    public const string TabId = "CIVIL3D2026PLUGIN_C3DTOOLS";
    public const string TabTitle = "C3D Tools";

    private static readonly RibbonCommandHandler CommandHandler = new();
    private static bool _idleHooked;

    /// <summary>
    /// Tenta criar a Ribbon imediatamente. Se o controle ainda nao existir,
    /// agenda nova tentativa no primeiro Idle do AutoCAD/Civil 3D.
    /// </summary>
    public static void Initialize()
    {
        if (!EnsureRibbon(activate: true))
            HookIdle();
    }

    public static void Terminate()
    {
        UnhookIdle();
    }

    /// <summary>
    /// Garante que a aba exista. Retorna false apenas quando a Ribbon ainda
    /// nao esta disponivel no host.
    /// </summary>
    public static bool EnsureRibbon(bool activate)
    {
        RibbonControl? ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            HookIdle();
            return false;
        }

        RibbonTab? existing = FindTab(ribbon);
        if (existing is null)
        {
            existing = BuildTab();
            ribbon.Tabs.Add(existing);
        }

        if (activate)
            existing.IsActive = true;

        return true;
    }

    /// <summary>
    /// Remove e monta novamente a aba. Util para desenvolvimento e para
    /// restaurar a UI apos alteracoes de workspace/CUI.
    /// </summary>
    public static bool RebuildRibbon(bool activate)
    {
        RibbonControl? ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            HookIdle();
            return false;
        }

        RibbonTab? current = FindTab(ribbon);
        if (current is not null)
            ribbon.Tabs.Remove(current);

        RibbonTab rebuilt = BuildTab();
        ribbon.Tabs.Add(rebuilt);

        if (activate)
            rebuilt.IsActive = true;

        return true;
    }

    private static RibbonTab BuildTab()
    {
        var tab = new RibbonTab
        {
            Id = TabId,
            Title = TabTitle
        };

        IReadOnlyList<CommandCatalog.ModuleDefinition> modules =
            CommandCatalog.RibbonModules.ToList();

        foreach (CommandCatalog.ModuleDefinition module in modules)
        {
            RibbonPanel? panel = BuildModulePanel(module, modules);
            if (panel is not null)
                tab.Panels.Add(panel);
        }

        return tab;
    }

    private static RibbonPanel? BuildModulePanel(
        CommandCatalog.ModuleDefinition module,
        IReadOnlyList<CommandCatalog.ModuleDefinition> allModules)
    {
        CommandCatalog.CommandDefinition[] commands =
            module.Commands.Where(command => command.ShowInRibbon).ToArray();

        if (commands.Length == 0)
            return null;

        var source = new RibbonPanelSource
        {
            Title = GetPanelTitle(module, allModules)
        };

        // Primeiro comando do modulo em destaque.
        source.Items.Add(CreateButton(module, commands[0], primary: true));

        // Demais comandos em colunas de ate 3 linhas, seguindo o padrao visual
        // comum da Ribbon do AutoCAD/Civil 3D.
        const int rowsPerColumn = 3;
        for (int start = 1; start < commands.Length; start += rowsPerColumn)
        {
            var column = new RibbonRowPanel();
            int end = Math.Min(start + rowsPerColumn, commands.Length);

            for (int index = start; index < end; index++)
            {
                column.Items.Add(CreateButton(module, commands[index], primary: false));

                if (index < end - 1)
                    column.Items.Add(new RibbonRowBreak());
            }

            source.Items.Add(column);
        }

        return new RibbonPanel
        {
            Source = source
        };
    }

    private static RibbonButton CreateButton(
        CommandCatalog.ModuleDefinition module,
        CommandCatalog.CommandDefinition command,
        bool primary)
    {
        var button = new RibbonButton
        {
            Id = $"C3D_{SanitizeId(command.Name)}",
            Text = command.Title,
            ShowText = true,
            ShowImage = true,
            Size = primary ? RibbonItemSize.Large : RibbonItemSize.Standard,
            CommandParameter = command.Name,
            CommandHandler = CommandHandler,
            IsToolTipEnabled = true,
            Image = RibbonIconFactory.Create(module.Category, command.Name, 16),
            LargeImage = RibbonIconFactory.Create(module.Category, command.Name, 32)
        };

        button.ToolTip = CreateToolTip(module, command);
        return button;
    }

    private static RibbonToolTip CreateToolTip(
        CommandCatalog.ModuleDefinition module,
        CommandCatalog.CommandDefinition command)
    {
        string version = string.IsNullOrWhiteSpace(module.Version)
            ? string.Empty
            : $" v{module.Version}";

        return new RibbonToolTip
        {
            Title = command.Title,
            Content = command.Description,
            ExpandedContent =
                $"{command.Description}\n\n" +
                $"Comando: {command.Name}\n" +
                $"Grupo: {module.Category}\n" +
                $"Modulo: {module.Name}{version}\n\n" +
                "Clique para executar o comando.",
            Command = command.Name
        };
    }

    private static string GetPanelTitle(
        CommandCatalog.ModuleDefinition module,
        IReadOnlyList<CommandCatalog.ModuleDefinition> allModules)
    {
        int countInCategory = allModules.Count(item =>
            string.Equals(item.Category, module.Category, StringComparison.OrdinalIgnoreCase));

        if (countInCategory <= 1 ||
            string.Equals(module.Category, module.Name, StringComparison.OrdinalIgnoreCase))
        {
            return module.Name;
        }

        return $"{module.Category} · {module.Name}";
    }

    private static RibbonTab? FindTab(RibbonControl ribbon)
    {
        foreach (RibbonTab tab in ribbon.Tabs)
        {
            if (string.Equals(tab.Id, TabId, StringComparison.OrdinalIgnoreCase))
                return tab;
        }

        return null;
    }

    private static string SanitizeId(string text)
    {
        char[] chars = text
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_')
            .ToArray();

        return new string(chars);
    }

    private static void HookIdle()
    {
        if (_idleHooked)
            return;

        AcApp.Idle += OnIdle;
        _idleHooked = true;
    }

    private static void UnhookIdle()
    {
        if (!_idleHooked)
            return;

        AcApp.Idle -= OnIdle;
        _idleHooked = false;
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        if (EnsureRibbon(activate: true))
            UnhookIdle();
    }

    public static void WriteStatus(Editor ed, bool rebuilt)
    {
        ed.WriteMessage(
            rebuilt
                ? $"\nRibbon '{TabTitle}' reconstruida a partir do catalogo central."
                : $"\nRibbon '{TabTitle}' criada/ativada.");
    }
}
