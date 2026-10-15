using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Civil3D2026Plugin.Modules.LispTools;

using SW = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WS = System.Windows.Shapes;

namespace Civil3D2026Plugin.Modules.Utilitarios;

/// <summary>
/// LSLAYOUTS: a esquerda os layer states do desenho; a direita uma miniatura de cada
/// layout com as viewports. Clique nas viewports para escolher, "repetir nos demais"
/// marca as viewports na mesma posicao dos outros layouts, Aplicar restaura o layer
/// state como override nas viewports marcadas.
/// </summary>
internal sealed class LayerStateWindow : SW.Window
{
    private const double CardW = 250, ThumbH = 176;

    private readonly ToolTheme T = ToolTheme.Current;
    private readonly Document _doc;
    private readonly List<SheetData> _folhas;
    private readonly HashSet<ObjectId> _sel = new();
    private readonly Dictionary<ObjectId, (WS.Path Forma, WC.TextBlock Rotulo, SheetViewport Vp)> _vps = new();
    private readonly Dictionary<SheetData, (WC.Border Card, WC.TextBlock Contagem)> _cards = new();

    private readonly WC.ListBox _states = new();
    private readonly WC.TextBox _find = new();
    private readonly WC.WrapPanel _grade = new();
    private readonly WC.TextBlock _status = new();

    private readonly WM.Brush _vpTraco = ToolTheme.Brush(200, 200, 200);
    private readonly WM.Brush _vpHover = new WM.SolidColorBrush(WM.Color.FromArgb(45, 80, 160, 255));
    private readonly WM.Brush _vpSel = new WM.SolidColorBrush(WM.Color.FromArgb(110, 80, 160, 255));
    private readonly WM.Brush _vpSelTraco = new WM.SolidColorBrush(ToolTheme.AccentColor);

    /// <summary>Algum layer state foi aplicado (o comando faz REGENALL ao fechar).</summary>
    public bool Aplicou { get; private set; }

    public LayerStateWindow(Document doc, List<LayerStateItem> states, List<SheetData> folhas)
    {
        _doc = doc;
        _folhas = folhas;

        Title = "Layer state em viewports";
        Width = 1100;
        Height = 720;
        MinWidth = 700;
        MinHeight = 420;
        FontFamily = new WM.FontFamily("Arial");
        FontSize = 12;
        Background = T.Back;
        Foreground = T.Text;
        ShowInTaskbar = false;
        WindowStartupLocation = SW.WindowStartupLocation.CenterScreen;

        Content = Build(states);
        MontarCards();
        AtualizarStatus();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == WI.Key.Escape)
                Close();
        };
    }

    // ------------------------------------------------------------------
    // Montagem
    // ------------------------------------------------------------------

    private SW.UIElement Build(List<LayerStateItem> states)
    {
        var root = new WC.DockPanel();

        // Rodape: status e acoes.
        var rodape = new WC.DockPanel { Margin = new SW.Thickness(10, 6, 10, 10) };
        WC.Button fechar = Botao("Fechar", (_, _) => Close());
        WC.Button aplicar = Botao("Aplicar", (_, _) => Aplicar());
        aplicar.FontWeight = SW.FontWeights.Bold;
        aplicar.Margin = new SW.Thickness(0, 0, 6, 0);
        WC.DockPanel.SetDock(fechar, WC.Dock.Right);
        WC.DockPanel.SetDock(aplicar, WC.Dock.Right);
        rodape.Children.Add(fechar);
        rodape.Children.Add(aplicar);
        _status.Foreground = T.Muted;
        _status.VerticalAlignment = SW.VerticalAlignment.Center;
        _status.TextTrimming = SW.TextTrimming.CharacterEllipsis;
        rodape.Children.Add(_status);
        WC.DockPanel.SetDock(rodape, WC.Dock.Bottom);
        root.Children.Add(rodape);

        var grid = new WC.Grid { Margin = new SW.Thickness(10, 10, 10, 0) };
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(240) });
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(10) });
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });

        // Esquerda: layer states.
        var esquerda = new WC.DockPanel();
        var tituloStates = Rotulo($"Layer states ({states.Count})");
        WC.DockPanel.SetDock(tituloStates, WC.Dock.Top);
        esquerda.Children.Add(tituloStates);
        _states.Background = T.Pane;
        _states.Foreground = T.Text;
        _states.BorderBrush = T.Line;
        _states.ItemContainerStyle = EstiloLinha();
        WC.ScrollViewer.SetHorizontalScrollBarVisibility(_states, WC.ScrollBarVisibility.Disabled);
        foreach (LayerStateItem s in states)
        {
            var sp = new WC.StackPanel { Margin = new SW.Thickness(8, 5, 8, 5) };
            sp.Children.Add(new WC.TextBlock { Text = s.Nome, FontWeight = SW.FontWeights.Bold, TextTrimming = SW.TextTrimming.CharacterEllipsis });
            if (s.Descricao.Length > 0)
                sp.Children.Add(new WC.TextBlock { Text = s.Descricao, Foreground = T.Muted, FontSize = 11, TextTrimming = SW.TextTrimming.CharacterEllipsis });
            _states.Items.Add(new WC.ListBoxItem { Content = sp, Tag = s, ToolTip = s.Descricao.Length > 0 ? s.Descricao : null });
        }
        _states.SelectionChanged += (_, _) => AtualizarStatus();
        esquerda.Children.Add(_states);
        WC.Grid.SetColumn(esquerda, 0);
        grid.Children.Add(esquerda);

        // Direita: barra + miniaturas.
        var direita = new WC.DockPanel();
        var barra = new WC.DockPanel { Margin = new SW.Thickness(0, 0, 0, 6) };
        var lblFind = Rotulo("Layouts");
        lblFind.Margin = new SW.Thickness(0, 0, 8, 0);
        WC.DockPanel.SetDock(lblFind, WC.Dock.Left);
        barra.Children.Add(lblFind);
        WC.Button nenhuma = Botao("Nenhuma", (_, _) => MarcarVisiveis(false));
        WC.Button todas = Botao("Todas", (_, _) => MarcarVisiveis(true));
        todas.Margin = new SW.Thickness(6, 0, 0, 0);
        nenhuma.Margin = new SW.Thickness(6, 0, 0, 0);
        WC.DockPanel.SetDock(nenhuma, WC.Dock.Right);
        WC.DockPanel.SetDock(todas, WC.Dock.Right);
        barra.Children.Add(nenhuma);
        barra.Children.Add(todas);
        _find.Height = 22;
        _find.MaxWidth = 260;
        _find.HorizontalAlignment = SW.HorizontalAlignment.Left;
        _find.Background = T.Input;
        _find.Foreground = T.Text;
        _find.BorderBrush = T.Line;
        _find.CaretBrush = T.Text;
        _find.VerticalContentAlignment = SW.VerticalAlignment.Center;
        _find.ToolTip = "Filtrar layouts pelo nome (aceita curinga, ex.: PR-*)";
        _find.TextChanged += (_, _) => Filtrar();
        barra.Children.Add(_find);
        WC.DockPanel.SetDock(barra, WC.Dock.Top);
        direita.Children.Add(barra);

        var scroll = new WC.ScrollViewer
        {
            Content = _grade,
            VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled,
            Background = T.Pane,
            BorderBrush = T.Line
        };
        _grade.Margin = new SW.Thickness(4);
        direita.Children.Add(new WC.Border { Child = scroll, BorderBrush = T.Line, BorderThickness = new SW.Thickness(1) });
        WC.Grid.SetColumn(direita, 2);
        grid.Children.Add(direita);

        root.Children.Add(grid);
        return root;
    }

    private void MontarCards()
    {
        foreach (SheetData folha in _folhas)
        {
            var card = new WC.Border
            {
                Width = CardW,
                Margin = new SW.Thickness(4),
                Background = T.Back,
                BorderBrush = T.Line,
                BorderThickness = new SW.Thickness(1)
            };
            var sp = new WC.StackPanel();

            // Cabecalho: nome, contagem e atalhos.
            var cab = new WC.DockPanel { Margin = new SW.Thickness(6, 4, 6, 2) };
            var contagem = new WC.TextBlock { Foreground = T.Muted, FontSize = 11, VerticalAlignment = SW.VerticalAlignment.Center };
            WC.DockPanel.SetDock(contagem, WC.Dock.Right);
            cab.Children.Add(contagem);
            cab.Children.Add(new WC.TextBlock
            {
                Text = folha.Nome,
                FontWeight = SW.FontWeights.Bold,
                TextTrimming = SW.TextTrimming.CharacterEllipsis,
                ToolTip = folha.Nome
            });
            sp.Children.Add(cab);

            var links = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(6, 0, 6, 4) };
            links.Children.Add(Link("todas", () => MarcarFolha(folha, !folha.Viewports.All(v => _sel.Contains(v.Id))),
                "Marca (ou desmarca) todas as viewports deste layout"));
            links.Children.Add(new WC.TextBlock { Text = "  |  ", Foreground = T.Muted, FontSize = 11 });
            links.Children.Add(Link("repetir nos demais", () => Repetir(folha),
                "Marca, nos outros layouts, as viewports na mesma posicao das marcadas aqui"));
            sp.Children.Add(links);

            sp.Children.Add(Miniatura(folha));
            card.Child = sp;

            _cards[folha] = (card, contagem);
            _grade.Children.Add(card);
            AtualizarCard(folha);
        }
    }

    /// <summary>Desenho simplificado do papel e as viewports clicaveis.</summary>
    private SW.UIElement Miniatura(SheetData folha)
    {
        double w = CardW - 2, h = ThumbH, m = 8;
        var canvas = new WC.Canvas { Width = w, Height = h, Background = ToolTheme.GraphicBack, ClipToBounds = true };

        double bw = Math.Max(folha.Largura, 1e-6), bh = Math.Max(folha.Altura, 1e-6);
        double s = Math.Min((w - 2 * m) / bw, (h - 2 * m) / bh);
        double ox = (w - bw * s) / 2.0, oy = (h - bh * s) / 2.0;
        SW.Point Map(SW.Point p) => new(ox + (p.X - folha.MinX) * s, h - (oy + (p.Y - folha.MinY) * s));

        // Papel: tudo numa geometria so (rapido mesmo com carimbos detalhados).
        var geo = new WM.StreamGeometry();
        using (WM.StreamGeometryContext ctx = geo.Open())
        {
            foreach (SW.Point[] linha in folha.Linhas)
            {
                ctx.BeginFigure(Map(linha[0]), false, false);
                ctx.PolyLineTo(linha.Skip(1).Select(Map).ToList(), true, false);
            }
        }
        geo.Freeze();
        canvas.Children.Add(new WS.Path { Data = geo, Stroke = ToolTheme.GraphicLine, StrokeThickness = 0.6, IsHitTestVisible = false });

        foreach (SheetViewport vp in folha.Viewports)
        {
            var vg = new WM.StreamGeometry();
            using (WM.StreamGeometryContext ctx = vg.Open())
            {
                ctx.BeginFigure(Map(vp.Contorno[0]), true, true);
                ctx.PolyLineTo(vp.Contorno.Skip(1).Select(Map).ToList(), true, false);
            }
            vg.Freeze();

            var forma = new WS.Path
            {
                Data = vg,
                Fill = WM.Brushes.Transparent,
                Stroke = _vpTraco,
                StrokeThickness = 1.2,
                StrokeDashArray = vp.Ligada ? null : new WM.DoubleCollection { 3, 2 },
                Cursor = WI.Cursors.Hand,
                ToolTip = DicaViewport(vp)
            };
            forma.MouseEnter += (_, _) => { if (!_sel.Contains(vp.Id)) forma.Fill = _vpHover; };
            forma.MouseLeave += (_, _) => { if (!_sel.Contains(vp.Id)) forma.Fill = WM.Brushes.Transparent; };
            forma.MouseLeftButtonDown += (_, e) =>
            {
                Alternar(vp.Id);
                AtualizarCard(folha);
                AtualizarStatus();
                e.Handled = true;
            };
            canvas.Children.Add(forma);

            SW.Point c = Map(vp.Centro);
            var rotulo = new WC.TextBlock
            {
                Foreground = ToolTheme.GraphicText,
                FontSize = 9.5,
                IsHitTestVisible = false,
                TextAlignment = SW.TextAlignment.Center,
                Width = Math.Max(30, vp.Largura * s),
                TextTrimming = SW.TextTrimming.CharacterEllipsis
            };
            WC.Canvas.SetLeft(rotulo, c.X - rotulo.Width / 2.0);
            WC.Canvas.SetTop(rotulo, c.Y - 7);
            canvas.Children.Add(rotulo);

            _vps[vp.Id] = (forma, rotulo, vp);
        }

        return canvas;
    }

    private static object DicaViewport(SheetViewport vp)
    {
        var linhas = new List<string>();
        if (vp.Escala.Length > 0)
            linhas.Add($"Escala de anotacao: {vp.Escala}");
        linhas.Add($"{vp.Largura:0.##} x {vp.Altura:0.##}");
        if (!vp.Ligada)
            linhas.Add("Viewport desligada");
        linhas.Add("Clique para marcar/desmarcar");
        return string.Join("\n", linhas);
    }

    // ------------------------------------------------------------------
    // Selecao
    // ------------------------------------------------------------------

    private void Alternar(ObjectId id)
    {
        if (!_sel.Remove(id))
            _sel.Add(id);
    }

    private void MarcarFolha(SheetData folha, bool marcar)
    {
        foreach (SheetViewport v in folha.Viewports)
        {
            if (marcar)
                _sel.Add(v.Id);
            else
                _sel.Remove(v.Id);
        }
        AtualizarCard(folha);
        AtualizarStatus();
    }

    private void MarcarVisiveis(bool marcar)
    {
        foreach (SheetData f in _folhas.Where(f => _cards[f].Card.Visibility == SW.Visibility.Visible))
            MarcarFolha(f, marcar);
    }

    /// <summary>
    /// Para cada viewport marcada neste layout, marca nos outros layouts a viewport com o
    /// mesmo centro relativo na folha e tamanho parecido (layouts do mesmo modelo de prancha).
    /// </summary>
    private void Repetir(SheetData origem)
    {
        List<SheetViewport> marcadas = origem.Viewports.Where(v => _sel.Contains(v.Id)).ToList();
        if (marcadas.Count == 0)
        {
            _status.Text = $"Marque primeiro as viewports em \"{origem.Nome}\".";
            return;
        }

        int novas = 0, layouts = 0;
        foreach (SheetData f in _folhas.Where(f => f != origem))
        {
            bool algum = false;
            foreach (SheetViewport ref0 in marcadas)
            {
                SheetViewport? par = f.Viewports
                    .Where(v => Parecida(origem, ref0, f, v))
                    .OrderBy(v => Distancia(origem, ref0, f, v))
                    .FirstOrDefault();
                if (par is not null && _sel.Add(par.Id))
                {
                    novas++;
                    algum = true;
                }
            }
            if (algum)
            {
                layouts++;
                AtualizarCard(f);
            }
        }

        AtualizarStatus();
        _status.Text = novas > 0
            ? $"{novas} viewport(s) marcada(s) em {layouts} outro(s) layout(s) pela posicao em \"{origem.Nome}\". " + _status.Text
            : "Nenhuma viewport na mesma posicao nos outros layouts.";
    }

    private static bool Parecida(SheetData fa, SheetViewport a, SheetData fb, SheetViewport b)
    {
        double tamA = a.Largura / Math.Max(fa.Largura, 1e-9), tamB = b.Largura / Math.Max(fb.Largura, 1e-9);
        double altA = a.Altura / Math.Max(fa.Altura, 1e-9), altB = b.Altura / Math.Max(fb.Altura, 1e-9);
        return Distancia(fa, a, fb, b) < 0.04 &&
               Math.Abs(tamA - tamB) <= 0.15 * Math.Max(tamA, tamB) &&
               Math.Abs(altA - altB) <= 0.15 * Math.Max(altA, altB);
    }

    private static double Distancia(SheetData fa, SheetViewport a, SheetData fb, SheetViewport b)
    {
        double ua = (a.Centro.X - fa.MinX) / Math.Max(fa.Largura, 1e-9), va = (a.Centro.Y - fa.MinY) / Math.Max(fa.Altura, 1e-9);
        double ub = (b.Centro.X - fb.MinX) / Math.Max(fb.Largura, 1e-9), vb = (b.Centro.Y - fb.MinY) / Math.Max(fb.Altura, 1e-9);
        return Math.Sqrt((ua - ub) * (ua - ub) + (va - vb) * (va - vb));
    }

    private void Filtrar()
    {
        string f = _find.Text.Trim();
        foreach (SheetData folha in _folhas)
        {
            bool ok = f.Length == 0 ||
                      (f.IndexOfAny(new[] { '*', '?', ',' }) >= 0
                          ? UtilHelpers.WildcardMatch(folha.Nome, f)
                          : folha.Nome.Contains(f, StringComparison.OrdinalIgnoreCase));
            _cards[folha].Card.Visibility = ok ? SW.Visibility.Visible : SW.Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------
    // Aparencia
    // ------------------------------------------------------------------

    private void AtualizarCard(SheetData folha)
    {
        int n = 0;
        foreach (SheetViewport v in folha.Viewports)
        {
            (WS.Path forma, WC.TextBlock rotulo, _) = _vps[v.Id];
            bool marcada = _sel.Contains(v.Id);
            if (marcada)
                n++;
            forma.Fill = marcada ? _vpSel : WM.Brushes.Transparent;
            forma.Stroke = marcada ? _vpSelTraco : _vpTraco;
            forma.StrokeThickness = marcada ? 2.0 : 1.2;
            rotulo.Text = v.Aplicado ?? v.Escala;
            rotulo.FontWeight = v.Aplicado is null ? SW.FontWeights.Normal : SW.FontWeights.Bold;
        }

        (WC.Border card, WC.TextBlock contagem) = _cards[folha];
        contagem.Text = $"{n}/{folha.Viewports.Count}";
        card.BorderBrush = n > 0 ? _vpSelTraco : T.Line;
    }

    private void AtualizarStatus()
    {
        int layouts = _folhas.Count(f => f.Viewports.Any(v => _sel.Contains(v.Id)));
        string state = (_states.SelectedItem as WC.ListBoxItem)?.Tag is LayerStateItem s ? $"\"{s.Nome}\"" : "nenhum layer state escolhido";
        _status.Text = $"{_sel.Count} viewport(s) marcada(s) em {layouts} layout(s); {state}.";
    }

    // ------------------------------------------------------------------
    // Aplicar
    // ------------------------------------------------------------------

    private void Aplicar()
    {
        if ((_states.SelectedItem as WC.ListBoxItem)?.Tag is not LayerStateItem state)
        {
            _status.Text = "Escolha um layer state na lista da esquerda.";
            return;
        }
        if (_sel.Count == 0)
        {
            _status.Text = "Marque as viewports clicando nelas nas miniaturas.";
            return;
        }

        try
        {
            (int ok, int falhas) = LayerStateSheets.Aplicar(_doc, state.Nome, _sel.ToList());
            foreach (ObjectId id in _sel)
                _vps[id].Vp.Aplicado = state.Nome;
            Aplicou |= ok > 0;

            _sel.Clear();
            foreach (SheetData f in _folhas)
                AtualizarCard(f);

            _status.Text = $"\"{state.Nome}\" aplicado em {ok} viewport(s)" +
                           (falhas > 0 ? $"; {falhas} falha(s)." : ".") +
                           " Marque outras viewports para aplicar outro layer state.";
        }
        catch (System.Exception ex)
        {
            _status.Text = $"Falha ao aplicar: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------
    // Controles
    // ------------------------------------------------------------------

    private WC.TextBlock Rotulo(string texto) => new()
    {
        Text = texto,
        FontWeight = SW.FontWeights.Bold,
        Foreground = T.Text,
        Margin = new SW.Thickness(0, 0, 0, 6),
        VerticalAlignment = SW.VerticalAlignment.Center
    };

    private WC.TextBlock Link(string texto, Action acao, string dica)
    {
        var t = new WC.TextBlock { Text = texto, Foreground = T.Accent, FontSize = 11, Cursor = WI.Cursors.Hand, ToolTip = dica };
        t.MouseEnter += (_, _) => t.TextDecorations = SW.TextDecorations.Underline;
        t.MouseLeave += (_, _) => t.TextDecorations = null;
        t.MouseLeftButtonDown += (_, e) =>
        {
            acao();
            e.Handled = true;
        };
        return t;
    }

    private WC.Button Botao(string texto, SW.RoutedEventHandler click)
    {
        var b = new WC.Button
        {
            Content = texto,
            MinWidth = 80,
            Height = 26,
            Padding = new SW.Thickness(12, 0, 12, 0),
            Background = T.Hover,
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
        b.MouseLeave += (_, _) => b.Background = T.Hover;
        b.Click += click;
        return b;
    }

    private SW.Style EstiloLinha()
    {
        var bd = new SW.FrameworkElementFactory(typeof(WC.Border), "Bd");
        bd.SetValue(WC.Border.BackgroundProperty, new SW.TemplateBindingExtension(WC.Control.BackgroundProperty));
        bd.SetValue(WC.Border.BorderBrushProperty, T.Line);
        bd.SetValue(WC.Border.BorderThicknessProperty, new SW.Thickness(0, 0, 0, 1));
        bd.AppendChild(new SW.FrameworkElementFactory(typeof(WC.ContentPresenter)));

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
        return style;
    }
}
