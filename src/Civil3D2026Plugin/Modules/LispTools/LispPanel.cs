using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Civil3D2026Plugin.Modules.Utilitarios;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.Modules.LispTools;

/// <summary>
/// Lista das ferramentas (C3DLISP): utilitarios nativos e LISPs embutidas numa lista so,
/// cada linha com icone pequeno, nome em destaque, comando e uma linha de descricao.
/// Duplo clique (ou Enter) executa; a dica mostra o passo a passo.
/// </summary>
internal sealed class LispPanel : SW.Window
{
    private static LispPanel? _instance;

    private sealed record Theme(WM.Brush Back, WM.Brush Pane, WM.Brush Hover, WM.Brush Line,
        WM.Brush Text, WM.Brush Muted, WM.Brush Input, WM.Brush Accent, WM.Brush Selected);

    private static readonly Theme Light = new(
        Brush(240, 240, 240), Brush(255, 255, 255), Brush(236, 241, 247), Brush(200, 200, 200),
        Brush(20, 20, 20), Brush(110, 110, 110), Brush(255, 255, 255), Brush(0, 95, 170), Brush(204, 228, 247));

    private static readonly Theme Dark = new(
        Brush(45, 45, 48), Brush(37, 37, 38), Brush(50, 54, 60), Brush(70, 70, 74),
        Brush(222, 222, 222), Brush(150, 150, 150), Brush(30, 30, 30), Brush(80, 160, 255), Brush(38, 79, 120));

    /// <summary>Uma linha da lista (ferramenta nativa ou LISP).</summary>
    private sealed record Entry(string Titulo, string Icone, string Descricao, IReadOnlyList<string> Comandos,
        IReadOnlyList<string> Uso, bool Lisp, string Origem);

    private readonly Theme T = ViarioDarkPreference() ? Dark : Light;
    private readonly List<Entry> _entries;
    private readonly Dictionary<string, WM.ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly WC.TextBox _find = new();
    private readonly WC.ListBox _list = new();

    private LispPanel()
    {
        _entries = Entradas();

        Title = "Ferramentas e LISPs";
        Width = 470;
        Height = 620;
        MinWidth = 340;
        MinHeight = 300;
        FontFamily = new WM.FontFamily("Arial");
        FontSize = 12;
        Background = T.Back;
        Foreground = T.Text;
        ShowInTaskbar = false;
        WindowStartupLocation = SW.WindowStartupLocation.CenterScreen;

        Content = Build();
        Preencher();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == WI.Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == WI.Key.Enter && Selecionada() is Entry sel)
            {
                Executar(sel.Comandos[0]);
                e.Handled = true;
            }
            else if (e.Key == WI.Key.Down && _find.IsKeyboardFocused && _list.Items.Count > 0)
            {
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex);
                (_list.ItemContainerGenerator.ContainerFromIndex(_list.SelectedIndex) as WC.ListBoxItem)?.Focus();
                e.Handled = true;
            }
        };
        Loaded += (_, _) => _find.Focus();
    }

    public static void ShowPanel()
    {
        if (_instance is null)
        {
            _instance = new LispPanel();
            _instance.Closed += (_, _) => _instance = null;
            AcApp.ShowModelessWindow(_instance);
        }
        else
        {
            _instance.Show();
            _instance.Activate();
        }
    }

    // ------------------------------------------------------------------
    // Dados
    // ------------------------------------------------------------------

    private static List<Entry> Entradas()
    {
        var lista = new List<Entry>();
        foreach (UtilTool u in UtilCatalog.Tools)
            lista.Add(new Entry(u.Titulo, u.Icone, u.Descricao, u.Comandos, u.Uso, false, u.Origem));
        foreach (LispInfo l in LispModule.Catalogo.Where(l => l.Comandos.Count > 0))
            lista.Add(new Entry(l.Titulo, l.Icone, l.Descricao, l.Comandos, l.Uso, true, l.Arquivo));
        return lista.OrderBy(e => e.Titulo, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool Combina(Entry e, string filtro)
    {
        if (filtro.Length == 0)
            return true;
        bool Tem(string s) => s.Contains(filtro, StringComparison.OrdinalIgnoreCase);
        return Tem(e.Titulo) || Tem(e.Descricao) || e.Comandos.Any(Tem);
    }

    // ------------------------------------------------------------------
    // Montagem
    // ------------------------------------------------------------------

    private SW.UIElement Build()
    {
        var root = new WC.DockPanel();

        // Localizar.
        var top = new WC.DockPanel { Margin = new SW.Thickness(8, 8, 8, 6) };
        var count = new WC.TextBlock
        {
            Text = $"{_entries.Count} ferramentas",
            Foreground = T.Muted,
            FontSize = 11,
            VerticalAlignment = SW.VerticalAlignment.Center,
            Margin = new SW.Thickness(8, 0, 0, 0)
        };
        WC.DockPanel.SetDock(count, WC.Dock.Right);
        top.Children.Add(count);
        _find.Height = 22;
        _find.Background = T.Input;
        _find.Foreground = T.Text;
        _find.BorderBrush = T.Line;
        _find.CaretBrush = T.Text;
        _find.VerticalContentAlignment = SW.VerticalAlignment.Center;
        _find.ToolTip = "Localizar por nome, comando ou descricao";
        _find.TextChanged += (_, _) => Preencher();
        top.Children.Add(_find);
        WC.DockPanel.SetDock(top, WC.Dock.Top);
        root.Children.Add(top);

        // Lista.
        _list.Background = T.Pane;
        _list.Foreground = T.Text;
        _list.BorderBrush = T.Line;
        _list.Margin = new SW.Thickness(8, 0, 8, 8);
        _list.HorizontalContentAlignment = SW.HorizontalAlignment.Stretch;
        WC.ScrollViewer.SetHorizontalScrollBarVisibility(_list, WC.ScrollBarVisibility.Disabled);
        _list.ItemContainerStyle = EstiloLinha();
        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is SW.DependencyObject src && ItemDe(src) is WC.ListBoxItem { Tag: Entry sel })
                Executar(sel.Comandos[0]);
        };
        root.Children.Add(_list);
        return root;
    }

    private void Preencher()
    {
        string filtro = _find.Text.Trim();
        Entry? atual = Selecionada();
        _list.Items.Clear();

        foreach (Entry e in _entries.Where(e => Combina(e, filtro)))
        {
            var item = new WC.ListBoxItem
            {
                Tag = e,
                Content = Linha(e),
                ToolTip = Dica(e),
                ContextMenu = e.Comandos.Count > 1 ? MenuComandos(e) : null
            };
            WC.ToolTipService.SetInitialShowDelay(item, 600);
            WC.ToolTipService.SetShowDuration(item, 30000);
            _list.Items.Add(item);
            if (ReferenceEquals(e, atual))
                _list.SelectedItem = item;
        }
    }

    private SW.UIElement Linha(Entry e)
    {
        var grid = new WC.Grid { Margin = new SW.Thickness(4, 4, 6, 4) };
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });

        var icone = new WC.Image
        {
            Source = Icone(e.Icone),
            Width = 16,
            Height = 16,
            Margin = new SW.Thickness(2, 1, 9, 0),
            VerticalAlignment = SW.VerticalAlignment.Top
        };
        WC.Grid.SetColumn(icone, 0);
        grid.Children.Add(icone);

        var textos = new WC.StackPanel();
        var nome = new WC.TextBlock { TextTrimming = SW.TextTrimming.CharacterEllipsis };
        nome.Inlines.Add(new WD.Run(e.Titulo) { FontWeight = SW.FontWeights.Bold, FontSize = 12.5, Foreground = T.Text });
        // Ate dois comandos na linha; os outros ficam na dica e no botao direito.
        string comandos = string.Join("  ", e.Comandos.Take(2)) + (e.Comandos.Count > 2 ? $"  +{e.Comandos.Count - 2}" : "");
        nome.Inlines.Add(new WD.Run("   " + comandos) { FontSize = 11, Foreground = T.Accent });
        textos.Children.Add(nome);
        textos.Children.Add(new WC.TextBlock
        {
            Text = PrimeiraFrase(e.Descricao),
            Foreground = T.Muted,
            FontSize = 11,
            Margin = new SW.Thickness(0, 1, 0, 0),
            TextTrimming = SW.TextTrimming.CharacterEllipsis
        });
        WC.Grid.SetColumn(textos, 1);
        grid.Children.Add(textos);

        if (e.Lisp)
        {
            var tag = new WC.Border
            {
                BorderBrush = T.Line,
                BorderThickness = new SW.Thickness(1),
                CornerRadius = new SW.CornerRadius(2),
                Padding = new SW.Thickness(3, 0, 3, 0),
                Margin = new SW.Thickness(8, 1, 0, 0),
                VerticalAlignment = SW.VerticalAlignment.Top,
                Child = new WC.TextBlock { Text = "LISP", FontSize = 9.5, Foreground = T.Muted },
                ToolTip = "Ainda em LISP (carregada no primeiro uso)"
            };
            WC.Grid.SetColumn(tag, 2);
            grid.Children.Add(tag);
        }

        return grid;
    }

    private object Dica(Entry e)
    {
        var painel = new WC.StackPanel { MaxWidth = 380 };
        painel.Children.Add(new WC.TextBlock { Text = e.Titulo, FontWeight = SW.FontWeights.Bold, Margin = new SW.Thickness(0, 0, 0, 4) });
        painel.Children.Add(new WC.TextBlock { Text = e.Descricao, TextWrapping = SW.TextWrapping.Wrap });
        int n = 0;
        foreach (string passo in e.Uso)
            painel.Children.Add(new WC.TextBlock
            {
                Text = $"{++n}. {passo}",
                TextWrapping = SW.TextWrapping.Wrap,
                Margin = new SW.Thickness(0, n == 1 ? 6 : 1, 0, 0)
            });
        if (e.Comandos.Count > 1)
            painel.Children.Add(new WC.TextBlock
            {
                Text = "Comandos: " + string.Join(", ", e.Comandos) + " (botao direito para escolher)",
                TextWrapping = SW.TextWrapping.Wrap,
                Margin = new SW.Thickness(0, 6, 0, 0)
            });
        painel.Children.Add(new WC.TextBlock
        {
            Text = (e.Lisp ? "LISP: " : "C#, antes: ") + e.Origem,
            FontSize = 10.5,
            Opacity = 0.7,
            Margin = new SW.Thickness(0, 6, 0, 0)
        });
        return painel;
    }

    private WC.ContextMenu MenuComandos(Entry e)
    {
        var menu = new WC.ContextMenu();
        foreach (string cmd in e.Comandos)
        {
            var mi = new WC.MenuItem { Header = cmd };
            mi.Click += (_, _) => Executar(cmd);
            menu.Items.Add(mi);
        }
        return menu;
    }

    /// <summary>Linha com hover e selecao nas cores do tema (o modelo padrao ignora o tema escuro).</summary>
    private SW.Style EstiloLinha()
    {
        var bd = new SW.FrameworkElementFactory(typeof(WC.Border), "Bd");
        bd.SetValue(WC.Border.BackgroundProperty, new SW.TemplateBindingExtension(WC.Control.BackgroundProperty));
        bd.SetValue(WC.Border.BorderBrushProperty, T.Line);
        bd.SetValue(WC.Border.BorderThicknessProperty, new SW.Thickness(0, 0, 0, 1));
        var cp = new SW.FrameworkElementFactory(typeof(WC.ContentPresenter));
        bd.AppendChild(cp);

        var template = new WC.ControlTemplate(typeof(WC.ListBoxItem)) { VisualTree = bd };
        var hover = new SW.Trigger { Property = SW.UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, T.Hover, "Bd"));
        template.Triggers.Add(hover);
        var sel = new SW.Trigger { Property = WC.Primitives.Selector.IsSelectedProperty, Value = true };
        sel.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, T.Selected, "Bd"));
        template.Triggers.Add(sel);

        var style = new SW.Style(typeof(WC.ListBoxItem));
        style.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, WM.Brushes.Transparent));
        style.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        style.Setters.Add(new SW.Setter(WC.Control.TemplateProperty, template));
        style.Setters.Add(new SW.Setter(WC.Control.HorizontalContentAlignmentProperty, SW.HorizontalAlignment.Stretch));
        return style;
    }

    // ------------------------------------------------------------------
    // Acoes e utilitarios
    // ------------------------------------------------------------------

    private void Executar(string comando)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        // Garante o registro das LISPs neste desenho (ex.: aberto antes do plugin ficar ocioso).
        LispModule.CarregarEm(doc, forcar: false);

        doc.SendStringToExecute("\x03\x03" + comando + " ", true, false, true);
        try
        {
            Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
        }
        catch
        {
            // O comando roda mesmo sem foco.
        }
    }

    private Entry? Selecionada() => (_list.SelectedItem as WC.ListBoxItem)?.Tag as Entry;

    private static WC.ListBoxItem? ItemDe(SW.DependencyObject? d)
    {
        while (d is not null and not WC.ListBoxItem)
            d = d is SW.Media.Visual or SW.Media.Media3D.Visual3D ? WM.VisualTreeHelper.GetParent(d) : SW.LogicalTreeHelper.GetParent(d);
        return d as WC.ListBoxItem;
    }

    private WM.ImageSource Icone(string key)
    {
        if (!_icons.TryGetValue(key, out WM.ImageSource? img))
            _icons[key] = img = ToolIcons.Create(key, T.Text, T.Accent);
        return img;
    }

    private static string PrimeiraFrase(string texto)
    {
        int p = texto.IndexOf(". ", StringComparison.Ordinal);
        string s = p > 0 ? texto[..(p + 1)] : texto;
        return s.Length > 0 && s.EndsWith('.') ? s[..^1] : s;
    }

    private static bool ViarioDarkPreference()
    {
        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Civil3D2026Plugin", "viario_painel.txt");
            return System.IO.File.Exists(path) &&
                   System.IO.File.ReadAllText(path).Contains("tema=escuro", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static WM.SolidColorBrush Brush(byte r, byte g, byte b)
    {
        WM.SolidColorBrush brush = new(WM.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
