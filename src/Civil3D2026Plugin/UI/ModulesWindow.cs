using System;
using System.Collections.Generic;
using System.Linq;
using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.UI.Ribbon;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.UI;

/// <summary>
/// C3DMODULOS: janela para marcar quais modulos aparecem na Ribbon (e iniciam
/// seus servicos na proxima carga). Aplicar grava e reconstroi a Ribbon.
/// </summary>
public sealed class ModulesWindow : SW.Window
{
    private readonly List<(WC.CheckBox Box, CommandCatalog.ModuleDefinition Module)> _boxes = new();

    public static void Open()
    {
        ModulesWindow window = new();
        AcApp.ShowModalWindow(window);
    }

    private ModulesWindow()
    {
        Civil3D2026Plugin.UI.Common.LogView.Atalho(this, "Plugin");
        Title = "Modulos do plugin - C3D Tools";
        Width = 520;
        Height = 620;
        MinWidth = 420;
        MinHeight = 360;
        WindowStartupLocation = SW.WindowStartupLocation.CenterOwner;
        FontFamily = new WM.FontFamily("Tahoma");
        FontSize = 12;
        Background = new WM.SolidColorBrush(WM.Color.FromRgb(45, 47, 51));
        Foreground = WM.Brushes.Gainsboro;

        WC.DockPanel root = new() { Margin = new SW.Thickness(10) };

        WC.TextBlock head = new()
        {
            Text = "Marque os modulos que devem aparecer na Ribbon e na lista de comandos. " +
                   "Os desmarcados nao iniciam seus servicos na proxima vez que a DLL carregar; " +
                   "digitar o comando continua funcionando.",
            TextWrapping = SW.TextWrapping.Wrap,
            Margin = new SW.Thickness(0, 0, 0, 8),
            Foreground = WM.Brushes.Silver
        };
        WC.DockPanel.SetDock(head, WC.Dock.Top);
        root.Children.Add(head);

        WC.DockPanel footer = new() { Margin = new SW.Thickness(0, 8, 0, 0) };
        WC.StackPanel left = new() { Orientation = WC.Orientation.Horizontal };
        left.Children.Add(Button("Todos", () => SetAll(true)));
        left.Children.Add(Button("Nenhum", () => SetAll(false)));
        WC.DockPanel.SetDock(left, WC.Dock.Left);
        footer.Children.Add(left);
        WC.StackPanel right = new() { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = SW.HorizontalAlignment.Right };
        right.Children.Add(Button("Aplicar", Apply, primary: true));
        right.Children.Add(Button("Fechar", Close));
        footer.Children.Add(right);
        WC.DockPanel.SetDock(footer, WC.Dock.Bottom);
        root.Children.Add(footer);

        WC.TextBlock note = new()
        {
            Text = "Arquivo: " + ModuleSettings.Path,
            FontSize = 10,
            Foreground = WM.Brushes.Gray,
            Margin = new SW.Thickness(0, 6, 0, 0),
            TextWrapping = SW.TextWrapping.Wrap
        };
        WC.DockPanel.SetDock(note, WC.Dock.Bottom);
        root.Children.Add(note);

        WC.StackPanel list = new();
        foreach (IGrouping<string, CommandCatalog.ModuleDefinition> category in CommandCatalog.Modules.GroupBy(m => m.Category))
        {
            list.Children.Add(new WC.TextBlock
            {
                Text = category.Key.ToUpperInvariant(),
                FontWeight = SW.FontWeights.Bold,
                Foreground = new WM.SolidColorBrush(WM.Color.FromRgb(120, 200, 235)),
                Margin = new SW.Thickness(0, 8, 0, 2)
            });

            foreach (CommandCatalog.ModuleDefinition module in category)
            {
                bool fixedOn = string.Equals(module.Category, "Geral", StringComparison.OrdinalIgnoreCase);
                string version = string.IsNullOrWhiteSpace(module.Version) ? "" : "  v" + module.Version;
                int commands = module.Commands.Count;
                WC.CheckBox box = new()
                {
                    Content = $"{module.Name}{version}   ({commands} comando{(commands == 1 ? "" : "s")}{(module.ShowInRibbon ? "" : ", so linha de comando")})",
                    IsChecked = ModuleSettings.IsEnabled(module),
                    IsEnabled = !fixedOn,
                    Margin = new SW.Thickness(12, 2, 0, 2),
                    Foreground = WM.Brushes.Gainsboro,
                    ToolTip = string.Join("\n", module.Commands.Select(c => c.Name + " - " + c.Title))
                };
                _boxes.Add((box, module));
                list.Children.Add(box);
            }
        }

        WC.ScrollViewer scroll = new()
        {
            Content = list,
            VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto,
            BorderBrush = WM.Brushes.DimGray,
            BorderThickness = new SW.Thickness(1),
            Padding = new SW.Thickness(6)
        };
        root.Children.Add(scroll);
        Content = root;
    }

    private static WC.Button Button(string text, Action action, bool primary = false)
    {
        WC.Button b = new()
        {
            Content = text,
            MinWidth = 90,
            Margin = new SW.Thickness(0, 0, 6, 0),
            Padding = new SW.Thickness(10, 4, 10, 4),
            Background = primary ? new WM.SolidColorBrush(WM.Color.FromRgb(38, 79, 120)) : new WM.SolidColorBrush(WM.Color.FromRgb(66, 68, 73)),
            Foreground = WM.Brushes.Gainsboro,
            BorderBrush = WM.Brushes.DimGray
        };
        b.Click += (_, _) => action();
        return b;
    }

    private void SetAll(bool on)
    {
        foreach ((WC.CheckBox box, CommandCatalog.ModuleDefinition module) in _boxes)
        {
            if (box.IsEnabled)
                box.IsChecked = on;
        }
    }

    private void Apply()
    {
        foreach ((WC.CheckBox box, CommandCatalog.ModuleDefinition module) in _boxes)
            ModuleSettings.SetEnabled(module, box.IsChecked == true);
        ModuleSettings.Save();

        bool rebuilt = RibbonUiManager.RebuildRibbon(activate: true);
        try
        {
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                $"\n[C3DMODULOS] {_boxes.Count - ModuleSettings.DisabledCount} de {_boxes.Count} modulo(s) ativos; " +
                (rebuilt ? "Ribbon reconstruida." : "a Ribbon sera refeita quando estiver disponivel.") +
                (ModuleSettings.DisabledCount > 0 ? " Servicos dos modulos desativados param na proxima carga da DLL." : ""));
        }
        catch
        {
            // Sem documento ativo.
        }

        Close();
    }
}
