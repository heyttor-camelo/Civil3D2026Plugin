using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.EditorInput;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Escolha de um item de lista na linha de comando sem pedir numero: cada item vira uma opcao
/// clicavel (menu do cursor e colchetes da linha de comando), com o atalho em maiusculas unico.
/// Listas longas abrem uma janela com a lista.
/// </summary>
internal static class HydroPrompt
{
    /// <summary>Acima disto a lista abre em janela (o menu do cursor fica grande demais).</summary>
    private const int MaxKeywords = 30;

    /// <summary>Indice escolhido, ou -1 se cancelou.</summary>
    public static int Choose(Editor ed, string prompt, IReadOnlyList<string> items, int def = 0)
    {
        if (items.Count == 0)
            return -1;
        def = Math.Clamp(def, 0, items.Count - 1);
        if (items.Count == 1)
        {
            ed.WriteMessage($"\n{prompt}: {items[0]}");
            return 0;
        }

        if (items.Count > MaxKeywords)
            return ChooseWindow(prompt, items, def);

        List<string> keys = Keywords(items);
        PromptKeywordOptions o = new("\n" + prompt) { AllowNone = true };
        foreach (string k in keys)
            o.Keywords.Add(k);
        o.Keywords.Default = keys[def];
        PromptResult r = ed.GetKeywords(o);
        if (r.Status == PromptStatus.None)
            return def;
        if (r.Status != PromptStatus.OK)
            return -1;
        int i = keys.FindIndex(k => string.Equals(k, r.StringResult, StringComparison.OrdinalIgnoreCase));
        return i;
    }

    /// <summary>
    /// Opcoes a partir dos nomes: sem espacos nem simbolos, unicas, e com o menor prefixo que
    /// distingue cada uma em maiusculas (o atalho), ex. Pv_dnit / POco_de_visita.
    /// </summary>
    internal static List<string> Keywords(IReadOnlyList<string> items)
    {
        List<string> tokens = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string item in items)
        {
            string t = Token(item);
            string u = t;
            for (int n = 2; !seen.Add(u); n++)
                u = t + "_" + n.ToString(CultureInfo.InvariantCulture);
            tokens.Add(u.ToLowerInvariant());
        }

        List<string> result = new();
        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i];
            int len = 1;
            while (len < t.Length && tokens.Where((o, j) => j != i).Any(o => o.StartsWith(t[..len], StringComparison.Ordinal)))
                len++;
            result.Add(t[..len].ToUpperInvariant() + t[len..]);
        }

        return result;
    }

    private static string Token(string text)
    {
        string plain = new string(text.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        StringBuilder sb = new();
        foreach (char c in plain)
        {
            if (char.IsLetterOrDigit(c) && c < 128)
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_')
                sb.Append('_');
        }

        string t = sb.ToString().Trim('_');
        if (t.Length == 0 || !char.IsLetter(t[0]))
            t = "T" + t;
        return t.Length > 40 ? t[..40].TrimEnd('_') : t;
    }

    private static int ChooseWindow(string prompt, IReadOnlyList<string> items, int def)
    {
        int chosen = -1;
        SW.Window w = new()
        {
            Title = "HYDRO - " + prompt,
            Width = 460,
            Height = 520,
            WindowStartupLocation = SW.WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ResizeMode = SW.ResizeMode.CanResizeWithGrip
        };
        WC.TextBox filter = new() { Margin = new SW.Thickness(8, 8, 8, 4) };
        WC.ListBox list = new() { Margin = new SW.Thickness(8, 0, 8, 4) };
        void Fill()
        {
            string f = filter.Text.Trim();
            list.Items.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                if (f.Length == 0 || items[i].Contains(f, StringComparison.OrdinalIgnoreCase))
                    list.Items.Add(new WC.ListBoxItem { Content = items[i], Tag = i });
            }

            foreach (WC.ListBoxItem it in list.Items)
            {
                if ((int)it.Tag == def)
                {
                    list.SelectedItem = it;
                    list.ScrollIntoView(it);
                }
            }

            if (list.SelectedItem is null && list.Items.Count > 0)
                list.SelectedIndex = 0;
        }

        void Accept()
        {
            if (list.SelectedItem is WC.ListBoxItem it)
            {
                chosen = (int)it.Tag;
                w.DialogResult = true;
            }
        }

        filter.TextChanged += (_, _) => Fill();
        list.MouseDoubleClick += (_, _) => Accept();
        WC.Button ok = new() { Content = "OK", Width = 90, Margin = new SW.Thickness(4), IsDefault = true };
        WC.Button cancel = new() { Content = "Cancelar", Width = 90, Margin = new SW.Thickness(4), IsCancel = true };
        ok.Click += (_, _) => Accept();
        WC.StackPanel buttons = new() { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = SW.HorizontalAlignment.Right, Margin = new SW.Thickness(4) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        WC.DockPanel root = new();
        WC.TextBlock hint = new() { Text = "Filtrar:", Margin = new SW.Thickness(8, 8, 8, 0) };
        WC.DockPanel.SetDock(hint, WC.Dock.Top);
        WC.DockPanel.SetDock(filter, WC.Dock.Top);
        WC.DockPanel.SetDock(buttons, WC.Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(filter);
        root.Children.Add(buttons);
        root.Children.Add(list);
        w.Content = root;
        Fill();
        w.Loaded += (_, _) => filter.Focus();
        return AcApp.ShowModalWindow(w) == true ? chosen : -1;
    }
}
