using System;
using System.Collections.Generic;
using System.Linq;
using Civil3D2026Plugin.UI.Common;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>Janela do PEDCAMPOS: marca quais linhas da ficha (e foto/mapa) aparecem nos multileaders.</summary>
internal sealed class PedFieldsWindow : SW.Window
{
    private readonly ToolTheme T = ToolTheme.Current;
    private readonly Dictionary<string, WC.CheckBox> _checks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mostra a janela; retorna as chaves escondidas, ou null se cancelou.</summary>
    public static HashSet<string>? Ask(ISet<string> hidden)
    {
        PedFieldsWindow w = new(hidden);
        bool? ok = AcApp.ShowModalWindow(w);
        if (ok != true)
            return null;
        return w._checks.Where(kv => kv.Value.IsChecked != true).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private PedFieldsWindow(ISet<string> hidden)
    {
        Title = "Itens da ficha do PED";
        Width = 420;
        Height = 640;
        MinWidth = 340;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = SW.WindowStartupLocation.CenterScreen;
        FontFamily = new System.Windows.Media.FontFamily("Arial");
        FontSize = 12;
        Background = T.Back;
        Foreground = T.Text;

        WC.StackPanel list = new() { Margin = new SW.Thickness(12, 8, 12, 8) };
        foreach (IGrouping<string, (string Key, string Group, string Label)> g in PedFichaBlock.Rows.GroupBy(r => r.Group))
        {
            list.Children.Add(new WC.TextBlock
            {
                Text = g.Key,
                FontWeight = SW.FontWeights.Bold,
                Foreground = T.Text,
                Margin = new SW.Thickness(0, 10, 0, 4)
            });
            foreach ((string key, _, string label) in g)
            {
                WC.CheckBox c = new()
                {
                    Content = label,
                    IsChecked = !hidden.Contains(key),
                    Foreground = T.Text,
                    Margin = new SW.Thickness(10, 2, 0, 2)
                };
                _checks[key] = c;
                list.Children.Add(c);
            }
        }

        WC.DockPanel root = new();
        WC.TextBlock hint = new()
        {
            Text = "Marque o que aparece na ficha (multileader) de todos os PEDs. As fichas sao refeitas com os dados ja gravados no desenho; a posicao delas nao muda.",
            TextWrapping = SW.TextWrapping.Wrap,
            Foreground = T.Muted,
            Margin = new SW.Thickness(12, 10, 12, 0)
        };
        WC.DockPanel.SetDock(hint, WC.Dock.Top);
        root.Children.Add(hint);

        WC.DockPanel buttons = new() { Margin = new SW.Thickness(12, 6, 12, 12), LastChildFill = false };
        WC.Button all = Btn("Todos", () => Set(true));
        WC.Button none = Btn("Nenhum", () => Set(false));
        WC.Button ok = Btn("OK", () => { DialogResult = true; });
        ok.IsDefault = true;
        WC.Button cancel = Btn("Cancelar", () => { DialogResult = false; });
        cancel.IsCancel = true;
        WC.DockPanel.SetDock(all, WC.Dock.Left);
        WC.DockPanel.SetDock(none, WC.Dock.Left);
        WC.DockPanel.SetDock(cancel, WC.Dock.Right);
        WC.DockPanel.SetDock(ok, WC.Dock.Right);
        buttons.Children.Add(all);
        buttons.Children.Add(none);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        WC.DockPanel.SetDock(buttons, WC.Dock.Bottom);
        root.Children.Add(buttons);

        root.Children.Add(new WC.ScrollViewer { Content = list, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto });
        Content = root;
    }

    private void Set(bool value)
    {
        foreach (WC.CheckBox c in _checks.Values)
            c.IsChecked = value;
    }

    private WC.Button Btn(string text, Action action)
    {
        WC.Button b = new()
        {
            Content = text,
            MinWidth = 76,
            Padding = new SW.Thickness(10, 3, 10, 3),
            Margin = new SW.Thickness(0, 0, 6, 0),
            Background = T.Hover,
            Foreground = T.Text,
            BorderBrush = T.Line
        };
        b.Click += (_, _) => action();
        return b;
    }
}
