using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.Modules.LispTools;

/// <summary>
/// Lista das LISPs embutidas (C3DLISP): a esquerda as rotinas por categoria,
/// a direita a explicacao, o passo a passo e um botao para cada comando.
/// Janela flutuante; segue o tema (claro/escuro) escolhido no painel viario.
/// </summary>
internal sealed class LispPanel : SW.Window
{
    private static LispPanel? _instance;

    private sealed record Theme(WM.Brush Back, WM.Brush Pane, WM.Brush Header, WM.Brush Line,
        WM.Brush Text, WM.Brush Muted, WM.Brush Input, WM.Brush Accent, WM.Brush Selected);

    private static readonly Theme Light = new(
        Brush(240, 240, 240), Brush(255, 255, 255), Brush(229, 229, 229), Brush(200, 200, 200),
        Brush(20, 20, 20), Brush(105, 105, 105), Brush(255, 255, 255), Brush(0, 95, 170), Brush(204, 228, 247));

    private static readonly Theme Dark = new(
        Brush(45, 45, 48), Brush(37, 37, 38), Brush(58, 58, 62), Brush(70, 70, 74),
        Brush(222, 222, 222), Brush(150, 150, 150), Brush(30, 30, 30), Brush(80, 160, 255), Brush(38, 79, 120));

    private static readonly WM.FontFamily UiFont = new("Arial");

    private readonly Theme T = ViarioDarkPreference() ? Dark : Light;

    private readonly WC.TextBox _find = new();
    private readonly WC.ListBox _list = new();
    private readonly WC.StackPanel _detail = new();
    private readonly WC.TextBlock _status = new();

    private LispPanel()
    {
        Title = "LISPs embutidas - Civil3D2026Plugin";
        Width = 820;
        Height = 560;
        MinWidth = 560;
        MinHeight = 360;
        FontFamily = UiFont;
        FontSize = 12;
        Background = T.Back;
        Foreground = T.Text;
        ShowInTaskbar = false;
        WindowStartupLocation = SW.WindowStartupLocation.CenterScreen;

        Content = Build();
        FillList();
        KeyDown += (_, e) =>
        {
            if (e.Key == WI.Key.Escape)
                Close();
        };
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
    // Montagem
    // ------------------------------------------------------------------

    private SW.UIElement Build()
    {
        var root = new WC.DockPanel { LastChildFill = true };

        // Barra superior: localizar.
        var top = new WC.DockPanel { Margin = new SW.Thickness(8, 8, 8, 6) };
        var label = new WC.TextBlock
        {
            Text = "Localizar:",
            VerticalAlignment = SW.VerticalAlignment.Center,
            Margin = new SW.Thickness(0, 0, 6, 0),
            Foreground = T.Text
        };
        _find.Width = 220;
        _find.Height = 22;
        _find.Background = T.Input;
        _find.Foreground = T.Text;
        _find.BorderBrush = T.Line;
        _find.CaretBrush = T.Text;
        _find.VerticalContentAlignment = SW.VerticalAlignment.Center;
        _find.TextChanged += (_, _) => FillList();
        var count = new WC.TextBlock
        {
            Text = $"{LispModule.Catalogo.Count} rotinas",
            Foreground = T.Muted,
            VerticalAlignment = SW.VerticalAlignment.Center,
            HorizontalAlignment = SW.HorizontalAlignment.Right
        };
        WC.DockPanel.SetDock(label, WC.Dock.Left);
        WC.DockPanel.SetDock(_find, WC.Dock.Left);
        top.Children.Add(label);
        top.Children.Add(_find);
        top.Children.Add(count);
        WC.DockPanel.SetDock(top, WC.Dock.Top);
        root.Children.Add(top);

        // Barra inferior: pasta, recarregar, status.
        var bottom = new WC.DockPanel { Margin = new SW.Thickness(8, 6, 8, 8) };
        var reload = Button("Recarregar", (_, _) => Recarregar());
        var folder = Button("Abrir pasta", (_, _) => AbrirPasta());
        folder.Margin = new SW.Thickness(6, 0, 0, 0);
        WC.DockPanel.SetDock(folder, WC.Dock.Right);
        WC.DockPanel.SetDock(reload, WC.Dock.Right);
        bottom.Children.Add(folder);
        bottom.Children.Add(reload);
        _status.Foreground = T.Muted;
        _status.VerticalAlignment = SW.VerticalAlignment.Center;
        _status.TextTrimming = SW.TextTrimming.CharacterEllipsis;
        _status.Text = "Cada rotina e carregada no primeiro uso do comando.";
        bottom.Children.Add(_status);
        WC.DockPanel.SetDock(bottom, WC.Dock.Bottom);
        root.Children.Add(bottom);

        // Centro: lista | detalhe.
        var grid = new WC.Grid { Margin = new SW.Thickness(8, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(270) });
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(5) });
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });

        _list.Background = T.Pane;
        _list.Foreground = T.Text;
        _list.BorderBrush = T.Line;
        _list.SelectionChanged += (_, _) => ShowDetail(_list.SelectedItem is WC.ListBoxItem { Tag: LispInfo l } ? l : null);
        _list.MouseDoubleClick += (_, _) =>
        {
            if (_list.SelectedItem is WC.ListBoxItem { Tag: LispInfo l } && l.Comandos.Count > 0)
                Run(l.Comandos[0]);
        };
        WC.Grid.SetColumn(_list, 0);
        grid.Children.Add(_list);

        var splitter = new WC.GridSplitter
        {
            Width = 5,
            Background = T.Back,
            HorizontalAlignment = SW.HorizontalAlignment.Stretch
        };
        WC.Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);

        _detail.Margin = new SW.Thickness(14, 10, 14, 10);
        var scroll = new WC.ScrollViewer
        {
            Content = _detail,
            VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled
        };
        var detailBorder = new WC.Border
        {
            Child = scroll,
            Background = T.Pane,
            BorderBrush = T.Line,
            BorderThickness = new SW.Thickness(1)
        };
        WC.Grid.SetColumn(detailBorder, 2);
        grid.Children.Add(detailBorder);

        root.Children.Add(grid);
        return root;
    }

    private void FillList()
    {
        string filtro = _find.Text.Trim();
        LispInfo? atual = _list.SelectedItem is WC.ListBoxItem { Tag: LispInfo s } ? s : null;

        _list.Items.Clear();
        WC.ListBoxItem? primeiro = null, manter = null;

        foreach (IGrouping<string, LispInfo> grupo in LispModule.Catalogo.Where(l => Combina(l, filtro)).GroupBy(l => l.Categoria))
        {
            _list.Items.Add(new WC.ListBoxItem
            {
                Content = grupo.Key.ToUpperInvariant(),
                FontWeight = SW.FontWeights.Bold,
                FontSize = 10.5,
                Foreground = T.Muted,
                Background = T.Header,
                IsEnabled = false,
                Padding = new SW.Thickness(6, 3, 6, 3)
            });

            foreach (LispInfo l in grupo)
            {
                var linha = new WC.StackPanel { Orientation = WC.Orientation.Vertical };
                linha.Children.Add(new WC.TextBlock { Text = l.Titulo, Foreground = T.Text });
                linha.Children.Add(new WC.TextBlock
                {
                    Text = string.Join(", ", l.Comandos),
                    Foreground = T.Muted,
                    FontSize = 10.5,
                    TextTrimming = SW.TextTrimming.CharacterEllipsis
                });

                var item = new WC.ListBoxItem { Content = linha, Tag = l, Padding = new SW.Thickness(14, 3, 6, 3) };
                _list.Items.Add(item);
                primeiro ??= item;
                if (ReferenceEquals(l, atual))
                    manter = item;
            }
        }

        _list.SelectedItem = manter ?? primeiro;
        if (_list.SelectedItem is null)
            ShowDetail(null);
    }

    private static bool Combina(LispInfo l, string filtro)
    {
        if (filtro.Length == 0)
            return true;
        bool Tem(string s) => s.Contains(filtro, StringComparison.OrdinalIgnoreCase);
        return Tem(l.Titulo) || Tem(l.Descricao) || Tem(l.Categoria) || Tem(l.Arquivo) || l.Comandos.Any(Tem);
    }

    private void ShowDetail(LispInfo? l)
    {
        _detail.Children.Clear();
        if (l is null)
        {
            _detail.Children.Add(new WC.TextBlock { Text = "Nenhuma rotina encontrada.", Foreground = T.Muted });
            return;
        }

        _detail.Children.Add(new WC.TextBlock
        {
            Text = l.Titulo,
            FontSize = 16,
            FontWeight = SW.FontWeights.Bold,
            Foreground = T.Text,
            TextWrapping = SW.TextWrapping.Wrap
        });

        string versao = l.Versao.Length > 0 ? $"  |  v{l.Versao}" : "";
        string modo = l.CarregarInteiro ? "carregada ao abrir o desenho" : "carregada no primeiro uso";
        _detail.Children.Add(new WC.TextBlock
        {
            Text = $"{l.Categoria}  |  {l.Arquivo}{versao}  |  {modo}",
            Foreground = T.Muted,
            FontSize = 11,
            Margin = new SW.Thickness(0, 2, 0, 10),
            TextWrapping = SW.TextWrapping.Wrap
        });

        _detail.Children.Add(new WC.TextBlock
        {
            Text = l.Descricao,
            Foreground = T.Text,
            TextWrapping = SW.TextWrapping.Wrap,
            LineHeight = 17
        });

        if (l.Uso.Count > 0)
        {
            _detail.Children.Add(Section("Como usar"));
            int n = 0;
            foreach (string passo in l.Uso)
            {
                n++;
                _detail.Children.Add(new WC.TextBlock
                {
                    Text = $"{n}.  {passo}",
                    Foreground = T.Text,
                    TextWrapping = SW.TextWrapping.Wrap,
                    Margin = new SW.Thickness(4, 0, 0, 3)
                });
            }
        }

        if (l.Comandos.Count > 0)
        {
            _detail.Children.Add(Section("Comandos"));
            var wrap = new WC.WrapPanel();
            foreach (string cmd in l.Comandos)
            {
                WC.Button b = Button(cmd, (_, _) => Run(cmd));
                b.Margin = new SW.Thickness(0, 0, 6, 6);
                b.ToolTip = $"Executar {cmd}";
                wrap.Children.Add(b);
            }
            _detail.Children.Add(wrap);
        }
    }

    private WC.TextBlock Section(string text) => new()
    {
        Text = text,
        FontWeight = SW.FontWeights.Bold,
        Foreground = T.Accent,
        Margin = new SW.Thickness(0, 14, 0, 6)
    };

    private WC.Button Button(string text, SW.RoutedEventHandler click)
    {
        var b = new WC.Button
        {
            Content = text,
            MinWidth = 80,
            Height = 24,
            Padding = new SW.Thickness(10, 0, 10, 0),
            Background = T.Header,
            Foreground = T.Text,
            BorderBrush = T.Line
        };

        // Modelo proprio: o hover padrao do Windows apaga o texto no tema escuro.
        var border = new SW.FrameworkElementFactory(typeof(WC.Border));
        border.SetValue(WC.Border.BackgroundProperty, new SW.TemplateBindingExtension(WC.Control.BackgroundProperty));
        border.SetValue(WC.Border.BorderBrushProperty, new SW.TemplateBindingExtension(WC.Control.BorderBrushProperty));
        border.SetValue(WC.Border.BorderThicknessProperty, new SW.Thickness(1));
        border.SetValue(WC.Border.PaddingProperty, new SW.TemplateBindingExtension(WC.Control.PaddingProperty));
        var presenter = new SW.FrameworkElementFactory(typeof(WC.ContentPresenter));
        presenter.SetValue(SW.FrameworkElement.HorizontalAlignmentProperty, SW.HorizontalAlignment.Center);
        presenter.SetValue(SW.FrameworkElement.VerticalAlignmentProperty, SW.VerticalAlignment.Center);
        border.AppendChild(presenter);
        b.Template = new WC.ControlTemplate(typeof(WC.Button)) { VisualTree = border };
        b.MouseEnter += (_, _) => b.Background = T.Selected;
        b.MouseLeave += (_, _) => b.Background = T.Header;

        b.Click += click;
        return b;
    }

    // ------------------------------------------------------------------
    // Acoes
    // ------------------------------------------------------------------

    private void Run(string command)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        // Garante os stubs neste desenho (ex.: aberto antes do plugin ficar ocioso).
        LispModule.CarregarEm(doc, forcar: false);

        _status.Text = $"Executando {command}...";
        doc.SendStringToExecute("\x03\x03" + command + " ", true, false, true);
        try
        {
            Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
        }
        catch
        {
            // O comando roda mesmo sem foco.
        }
    }

    private void Recarregar()
    {
        try
        {
            LispModule.ExtrairArquivos();
            bool ok = LispModule.CarregarEm(AcApp.DocumentManager.MdiActiveDocument, forcar: true);
            _status.Text = ok
                ? $"{LispModule.Catalogo.Count} rotinas registradas de novo no desenho atual."
                : "Nenhum desenho ativo para recarregar.";
        }
        catch (System.Exception ex)
        {
            _status.Text = $"Falha ao recarregar: {ex.Message}";
        }
    }

    private void AbrirPasta()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LispModule.Pasta}\"") { UseShellExecute = true });
        }
        catch (System.Exception ex)
        {
            _status.Text = $"Nao foi possivel abrir a pasta: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------
    // Utilitarios
    // ------------------------------------------------------------------

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
