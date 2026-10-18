using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.UI.Common;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WS = System.Windows.Shapes;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>
/// Explorador dos rebaixos de meio-fio (MFREBAIXOPAINEL), no molde do explorador da rede
/// viaria: categorias com contagem, barra de comandos, vista grafica (planta geral com os
/// rebaixos identificados por codigo/nome ou planta do trecho), lista em colunas e abas de
/// detalhe (propriedades editaveis, nome, secoes, avisos).
/// Segue o tema claro/escuro do painel viario.
/// </summary>
internal sealed class MFRebaixoPanel : SW.Window
{
    private static MFRebaixoPanel? _instance;

    private readonly ToolTheme T = ToolTheme.Current;
    private static readonly WM.FontFamily UiFont = new("Arial");
    private static readonly WM.FontFamily MonoFont = new("Consolas");

    // ------------------------------------------------------------------
    // Dados
    // ------------------------------------------------------------------

    private List<RebaixoInfo> _items = new();
    private List<CorridorSummary> _corridors = new();
    private List<RebaixoWarning> _warnings = new();
    private MFRebaixoSettings _settings = MFRebaixoSettingsStore.Load();

    private string _category = "REBAIXOS";
    private string _tab = "Propriedades";
    private RebaixoInfo? _current;
    private CorridorSummary? _currentCorridor;
    private RebaixoModel? _model;
    private List<(AcadDb.ObjectId Corridor, string Name, List<Point3d> Points)> _overview = new();

    // Rotulos ja colocados na planta (para nao sobrepor textos).
    private readonly List<SW.Rect> _labelRects = new();

    private string Mode => _settings.PainelModo is "PLANTA" or "3D" ? "PLANTA" : "GERAL";
    private string? _modelKey;
    private bool _refreshQueued;
    private Document? _watched;
    private (AcadDb.ObjectId Corridor, string Baseline, double S1)? _pending;

    // ------------------------------------------------------------------
    // Controles
    // ------------------------------------------------------------------

    private WC.WrapPanel _categoryBar = new();
    private WC.StackPanel _commandBar = new();
    private WC.Grid _graphicHost = new();
    private WC.Canvas _plan = new();
    private WC.TextBlock _graphicInfo = new();
    private WC.TextBlock _coords = new();
    private WC.StackPanel _viewToggles = new();
    private WC.ListView _list = new();
    private WC.TextBox _find = new();
    private WC.StackPanel _tabStrip = new();
    private WC.ContentControl _tabContent = new();
    private WC.TextBlock _status = new();
    private readonly Dictionary<string, WM.ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WC.TextBox> _fields = new();

    // Transformacao da planta (zoom e arrasto).
    private double _scale, _cx, _cy, _ox, _oy, _zoom = 1, _panX, _panY;
    private SW.Point? _dragFrom;

    // ------------------------------------------------------------------
    // Categorias e comandos
    // ------------------------------------------------------------------

    private static readonly (string Id, string Icon, string Title)[] Categories =
    {
        ("REBAIXOS", "rebaixo", "Rebaixos"),
        ("CORREDORES", "corredor", "Corredores"),
        ("AVISOS", "aviso", "Avisos (NBR 9050)"),
        ("CONFIG", "config", "Configuracao")
    };

    private sealed record Cmd(string Key, string Icon, string Title, string Group, string Description, string[] Steps, Action<MFRebaixoPanel> Run);

    private static readonly Cmd[] Commands =
    {
        new("NOVO", "rebaixo", "Novo rebaixo", "Rebaixo",
            "Lanca um rebaixo por dois cliques no trecho que fica totalmente rebaixado (MFREBAIXO). As rampas e as secoes saem calculadas.",
            new[] { "Selecione o corredor.", "Clique o inicio e o fim do trecho rebaixado.", "Informe a altura livre final ou a reducao." },
            p => p.Send(C3DCommands.Corridor.MfRebaixo + " ", "Novo rebaixo: siga a linha de comando.")),
        new("EDITAR", "renomear", "Editar rebaixo", "Rebaixo",
            "Muda o inicio, o fim ou a altura final do rebaixo selecionado e reaplica (Transitions e secoes refeitas).",
            new[] { "Selecione o rebaixo.", "Altere os valores na aba Propriedades.", "Aplicar." }, p => p.SelectTab("Propriedades")),
        new("REMOVER", "excluir", "Remover rebaixo", "Rebaixo",
            "Tira os Transition Sets do rebaixo (geometria e destaque) e as secoes que o comando criou. Pede confirmacao.",
            new[] { "Selecione o rebaixo e clique." }, p => p.RemoveCurrent()),
        new("ZOOM", "zoom", "Zoom no desenho", "Rebaixo",
            "Aproxima o desenho no rebaixo selecionado (duplo clique na lista faz o mesmo).", new[] { "Selecione o rebaixo." }, p => p.ZoomCurrent()),
        new("SECOES", "secoes", "Secoes do rebaixo", "Rebaixo",
            "Valores do meio-fio em cada secao do corredor no trecho: REB, DESTAQUE, altura exposta, rampa e faixa livre da calcada.",
            new[] { "Selecione o rebaixo." }, p => p.SelectTab("Secoes")),
        new("SELECIONAR", "corredor", "Selecionar corredor", "Corredor",
            "Seleciona no desenho o corredor do item escolhido (para Propriedades, Corridor Transitions, Section Editor).",
            new[] { "Selecione um rebaixo ou corredor." }, p => p.SelectCorridor()),
        new("REBUILD", "reconstruir", "Rebuild do corredor", "Corredor",
            "Reconstroi o corredor do item escolhido e le tudo de novo.", new[] { "Selecione um rebaixo ou corredor." }, p => p.RebuildCurrent()),
        new("ATUALIZAR", "atualizar", "Atualizar", "Geral",
            "Le os rebaixos do desenho de novo.", new[] { "Sem perguntas." }, p => p.Refresh()),
        new("AJUDA", "ajuda", "Ajuda do MFREBAIXO", "Geral",
            "Mostra na linha de comando como o rebaixo e calculado.", new[] { "Sem perguntas." },
            p => p.Send(C3DCommands.Corridor.MfRebaixoHelp + " ", "Ajuda na linha de comando."))
    };

    // ------------------------------------------------------------------
    // Abrir / atualizar
    // ------------------------------------------------------------------

    public static void ShowPanel(string? category = null)
    {
        if (_instance is null)
        {
            _instance = new MFRebaixoPanel();
            _instance.Closed += (_, _) =>
            {
                _instance?.Unwatch();
                _instance = null;
                AcApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
            };
            AcApp.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApp.ShowModelessWindow(_instance);
        }
        else
        {
            _instance.Show();
            _instance.Activate();
        }

        if (category is not null)
            _instance._category = category;
        _instance.Refresh();
    }

    private static void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e) => _instance?.QueueRefresh();

    private void QueueRefresh()
    {
        if (_refreshQueued)
            return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _refreshQueued = false;
            if (_instance == this)
                Refresh();
        }), SW.Threading.DispatcherPriority.Background);
    }

    /// <summary>Atualiza sozinho quando um comando do rebaixo, UNDO ou REDO termina.</summary>
    private void Watch(Document? doc)
    {
        if (ReferenceEquals(doc, _watched))
            return;
        Unwatch();
        _watched = doc;
        if (doc is not null)
            doc.CommandEnded += OnCommandEnded;
    }

    private void Unwatch()
    {
        if (_watched is not null)
        {
            try
            {
                _watched.CommandEnded -= OnCommandEnded;
            }
            catch
            {
                // Desenho ja fechado.
            }
        }

        _watched = null;
    }

    private void OnCommandEnded(object sender, CommandEventArgs e)
    {
        string name = e.GlobalCommandName ?? "";
        if ((name.StartsWith("MFREBAIXO", StringComparison.OrdinalIgnoreCase) && !name.Equals(C3DCommands.Corridor.MfRebaixoPainel, StringComparison.OrdinalIgnoreCase)) ||
            name is "U" or "UNDO" or "REDO" or "MREDO" ||
            name.Contains("CORRIDOR", StringComparison.OrdinalIgnoreCase))
        {
            _modelKey = null;
            QueueRefresh();
        }
    }

    private MFRebaixoPanel()
    {
        Civil3D2026Plugin.UI.Common.LogView.Atalho(this, "MFRebaixo");
        Title = "Rebaixos de meio-fio - Explorador";
        Width = 1120;
        Height = 780;
        MinWidth = 780;
        MinHeight = 540;
        ShowInTaskbar = false;
        WindowStartupLocation = SW.WindowStartupLocation.CenterScreen;
        FontFamily = UiFont;
        FontSize = 12;
        Content = BuildUi();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == WI.Key.Escape)
                Close();
        };
    }

    private static Document? Doc => AcApp.DocumentManager.MdiActiveDocument;

    // ------------------------------------------------------------------
    // Montagem da tela
    // ------------------------------------------------------------------

    private SW.UIElement BuildUi()
    {
        Background = T.Back;
        Foreground = T.Text;
        Resources.Clear();
        AddStyles();

        _categoryBar = new WC.WrapPanel { Margin = new SW.Thickness(4, 4, 4, 0) };
        _plan = new WC.Canvas { Background = ToolTheme.GraphicBack, ClipToBounds = true };
        _graphicHost = new WC.Grid();
        _graphicHost.Children.Add(_plan);
        _graphicInfo = new WC.TextBlock { Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(8, 0, 8, 0), TextTrimming = SW.TextTrimming.CharacterEllipsis };
        _coords = new WC.TextBlock { Foreground = T.Text, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(8, 0, 8, 0), MaxWidth = 520, TextTrimming = SW.TextTrimming.CharacterEllipsis };
        _list = new WC.ListView { BorderBrush = T.Line, Background = T.Pane, Foreground = T.Text };
        _find = new WC.TextBox { Width = 160, VerticalContentAlignment = SW.VerticalAlignment.Center };
        _tabStrip = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        _tabContent = new WC.ContentControl();
        _status = new WC.TextBlock { Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(6, 0, 6, 0) };

        WC.Grid root = new();
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1.0, SW.GridUnitType.Star), MinHeight = 160 });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(0) });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(5) });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1, SW.GridUnitType.Star), MinHeight = 160 });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });

        root.Children.Add(_categoryBar);

        WC.DockPanel commandRow = new() { Margin = new SW.Thickness(4, 2, 4, 2) };
        WC.StackPanel findPanel = new() { Orientation = WC.Orientation.Horizontal };
        findPanel.Children.Add(new WC.TextBlock { Text = "Localizar:", Foreground = T.Text, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(8, 0, 4, 0) });
        findPanel.Children.Add(_find);
        _find.TextChanged += (_, _) => FillList();
        WC.DockPanel.SetDock(findPanel, WC.Dock.Right);
        commandRow.Children.Add(findPanel);
        _commandBar = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        commandRow.Children.Add(_commandBar);
        WC.Grid.SetRow(commandRow, 1);
        root.Children.Add(commandRow);

        WC.Border graphic = new() { BorderBrush = T.Line, BorderThickness = new SW.Thickness(1), Child = _graphicHost, Margin = new SW.Thickness(4, 0, 4, 0) };
        _plan.SizeChanged += (_, _) => RenderPlan();
        _plan.MouseWheel += OnPlanWheel;
        _plan.MouseMove += OnPlanMove;
        _plan.MouseDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                _zoom = 1;
                _panX = _panY = 0;
                RenderPlan();
                return;
            }

            _dragFrom = e.GetPosition(_plan);
            _plan.CaptureMouse();
        };
        _plan.MouseUp += (_, _) =>
        {
            _dragFrom = null;
            _plan.ReleaseMouseCapture();
        };
        WC.DockPanel graphicBar = new() { Margin = new SW.Thickness(4, 0, 4, 0), Background = T.Hover, Height = 24 };
        _viewToggles = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        WC.DockPanel.SetDock(_viewToggles, WC.Dock.Left);
        graphicBar.Children.Add(_viewToggles);
        WC.DockPanel.SetDock(_coords, WC.Dock.Right);
        graphicBar.Children.Add(_coords);
        graphicBar.Children.Add(_graphicInfo);

        // Grafico e barra juntos na mesma linha: o divisor fica entre duas linhas proporcionais.
        WC.DockPanel graphicPane = new();
        WC.DockPanel.SetDock(graphicBar, WC.Dock.Bottom);
        graphicPane.Children.Add(graphicBar);
        graphicPane.Children.Add(graphic);
        WC.Grid.SetRow(graphicPane, 2);
        root.Children.Add(graphicPane);

        WC.GridSplitter splitH = new() { Height = 5, HorizontalAlignment = SW.HorizontalAlignment.Stretch, Background = T.Back };
        WC.Grid.SetRow(splitH, 4);
        root.Children.Add(splitH);

        WC.Grid bottom = new() { Margin = new SW.Thickness(4, 0, 4, 0) };
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1.1, SW.GridUnitType.Star), MinWidth = 280 });
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(5) });
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star), MinWidth = 300 });
        _list.SelectionChanged += (_, _) => OnRowSelected();
        _list.MouseDoubleClick += (_, _) => ZoomCurrent();
        bottom.Children.Add(_list);
        WC.GridSplitter splitV = new() { Width = 5, HorizontalAlignment = SW.HorizontalAlignment.Stretch, Background = T.Back };
        WC.Grid.SetColumn(splitV, 1);
        bottom.Children.Add(splitV);

        WC.DockPanel right = new();
        WC.Border strip = new() { Child = _tabStrip, BorderBrush = T.Line, BorderThickness = new SW.Thickness(0, 0, 0, 1) };
        WC.DockPanel.SetDock(strip, WC.Dock.Top);
        right.Children.Add(strip);
        right.Children.Add(new WC.Border { BorderBrush = T.Line, BorderThickness = new SW.Thickness(1, 0, 1, 1), Background = T.Pane, Child = _tabContent });
        WC.Grid.SetColumn(right, 2);
        bottom.Children.Add(right);
        WC.Grid.SetRow(bottom, 5);
        root.Children.Add(bottom);

        WC.DockPanel footer = new() { Margin = new SW.Thickness(4, 4, 4, 4) };
        WC.StackPanel leftButtons = new() { Orientation = WC.Orientation.Horizontal };
        leftButtons.Children.Add(FooterButton("Novo rebaixo", () => Send(C3DCommands.Corridor.MfRebaixo + " ", "Novo rebaixo: siga a linha de comando.")));
        leftButtons.Children.Add(FooterButton("Atualizar", Refresh));
        WC.DockPanel.SetDock(leftButtons, WC.Dock.Left);
        footer.Children.Add(leftButtons);
        footer.Children.Add(_status);
        WC.Grid.SetRow(footer, 6);
        root.Children.Add(footer);

        BuildCommandBar();
        BuildViewToggles();
        ShowView();
        return root;
    }

    private void AddStyles()
    {
        SW.Style header = new(typeof(WC.GridViewColumnHeader));
        header.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Hover));
        header.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        header.Setters.Add(new SW.Setter(WC.Control.BorderBrushProperty, T.Line));
        header.Setters.Add(new SW.Setter(WC.Control.PaddingProperty, new SW.Thickness(4, 2, 4, 2)));
        header.Setters.Add(new SW.Setter(WC.Control.HorizontalContentAlignmentProperty, SW.HorizontalAlignment.Left));
        Resources[typeof(WC.GridViewColumnHeader)] = header;

        SW.FrameworkElementFactory bd = new(typeof(WC.Border), "Bd");
        bd.SetValue(WC.Border.BackgroundProperty, new SW.TemplateBindingExtension(WC.Control.BackgroundProperty));
        bd.SetValue(WC.Border.PaddingProperty, new SW.Thickness(0, 1, 0, 1));
        SW.FrameworkElementFactory presenter = new(typeof(WC.GridViewRowPresenter));
        presenter.SetValue(WC.GridViewRowPresenter.ColumnsProperty, new SW.TemplateBindingExtension(WC.GridView.ColumnCollectionProperty));
        presenter.SetValue(WC.ContentPresenter.ContentProperty, new SW.TemplateBindingExtension(WC.ContentControl.ContentProperty));
        bd.AppendChild(presenter);
        WC.ControlTemplate rowTemplate = new(typeof(WC.ListViewItem)) { VisualTree = bd };
        SW.Trigger hover = new() { Property = SW.UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, T.Hover, "Bd"));
        rowTemplate.Triggers.Add(hover);
        SW.Trigger sel = new() { Property = WC.Primitives.Selector.IsSelectedProperty, Value = true };
        sel.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, T.Selected, "Bd"));
        rowTemplate.Triggers.Add(sel);
        SW.Style item = new(typeof(WC.ListViewItem));
        item.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        item.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, WM.Brushes.Transparent));
        item.Setters.Add(new SW.Setter(WC.Control.TemplateProperty, rowTemplate));
        Resources[typeof(WC.ListViewItem)] = item;

        SW.Style button = new(typeof(WC.Button));
        button.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Hover));
        button.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        button.Setters.Add(new SW.Setter(WC.Control.BorderBrushProperty, T.Line));
        button.Setters.Add(new SW.Setter(WC.Control.BorderThicknessProperty, new SW.Thickness(1)));
        button.Setters.Add(new SW.Setter(WC.Control.HorizontalContentAlignmentProperty, SW.HorizontalAlignment.Center));
        button.Setters.Add(new SW.Setter(WC.Control.VerticalContentAlignmentProperty, SW.VerticalAlignment.Center));
        SW.FrameworkElementFactory bbd = new(typeof(WC.Border), "Bd");
        bbd.SetValue(WC.Border.BackgroundProperty, new SW.TemplateBindingExtension(WC.Control.BackgroundProperty));
        bbd.SetValue(WC.Border.BorderBrushProperty, new SW.TemplateBindingExtension(WC.Control.BorderBrushProperty));
        bbd.SetValue(WC.Border.BorderThicknessProperty, new SW.TemplateBindingExtension(WC.Control.BorderThicknessProperty));
        bbd.SetValue(WC.Border.PaddingProperty, new SW.TemplateBindingExtension(WC.Control.PaddingProperty));
        bbd.SetValue(WC.Border.SnapsToDevicePixelsProperty, true);
        SW.FrameworkElementFactory bcp = new(typeof(WC.ContentPresenter));
        bcp.SetValue(SW.FrameworkElement.HorizontalAlignmentProperty, new SW.TemplateBindingExtension(WC.Control.HorizontalContentAlignmentProperty));
        bcp.SetValue(SW.FrameworkElement.VerticalAlignmentProperty, new SW.TemplateBindingExtension(WC.Control.VerticalContentAlignmentProperty));
        bbd.AppendChild(bcp);
        WC.ControlTemplate buttonTemplate = new(typeof(WC.Button)) { VisualTree = bbd };
        SW.Trigger bHover = new() { Property = SW.UIElement.IsMouseOverProperty, Value = true };
        bHover.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, T.Selected, "Bd"));
        buttonTemplate.Triggers.Add(bHover);
        SW.Trigger bPressed = new() { Property = WC.Primitives.ButtonBase.IsPressedProperty, Value = true };
        bPressed.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, T.Line, "Bd"));
        buttonTemplate.Triggers.Add(bPressed);
        SW.Trigger bDisabled = new() { Property = SW.UIElement.IsEnabledProperty, Value = false };
        bDisabled.Setters.Add(new SW.Setter(SW.UIElement.OpacityProperty, 0.45));
        buttonTemplate.Triggers.Add(bDisabled);
        button.Setters.Add(new SW.Setter(WC.Control.TemplateProperty, buttonTemplate));
        Resources[typeof(WC.Button)] = button;

        SW.Style check = new(typeof(WC.CheckBox));
        check.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        Resources[typeof(WC.CheckBox)] = check;

        SW.Style text = new(typeof(WC.TextBox));
        text.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Input));
        text.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        text.Setters.Add(new SW.Setter(WC.Control.BorderBrushProperty, T.Line));
        text.Setters.Add(new SW.Setter(WC.TextBox.CaretBrushProperty, T.Text));
        text.Setters.Add(new SW.Setter(WC.Control.PaddingProperty, new SW.Thickness(3, 1, 3, 1)));
        Resources[typeof(WC.TextBox)] = text;
    }

    private void BuildCommandBar()
    {
        _commandBar.Children.Clear();
        bool first = true;
        foreach (IGrouping<string, Cmd> group in Commands.GroupBy(c => c.Group))
        {
            if (!first)
                _commandBar.Children.Add(new WC.Border { Width = 1, Background = T.Line, Margin = new SW.Thickness(5, 3, 5, 3) });
            first = false;
            foreach (Cmd cmd in group)
            {
                WC.Button button = new()
                {
                    Content = new WC.Image { Source = IconOf(cmd.Icon), Width = 18, Height = 18 },
                    Padding = new SW.Thickness(3),
                    Background = WM.Brushes.Transparent,
                    BorderBrush = WM.Brushes.Transparent,
                    ToolTip = Tip(cmd)
                };
                WC.ToolTipService.SetInitialShowDelay(button, 250);
                WC.ToolTipService.SetShowDuration(button, 30000);
                Cmd c = cmd;
                button.Click += (_, _) => c.Run(this);
                _commandBar.Children.Add(button);
            }
        }
    }

    private WC.ToolTip Tip(Cmd cmd)
    {
        WC.StackPanel panel = new() { MaxWidth = 360 };
        panel.Children.Add(new WC.TextBlock { Text = cmd.Title, FontWeight = SW.FontWeights.Bold });
        panel.Children.Add(new WC.TextBlock
        {
            Text = cmd.Description + "\n\n" + string.Join("\n", cmd.Steps.Select((s, i) => $"{i + 1}. {s}")),
            TextWrapping = SW.TextWrapping.Wrap,
            Margin = new SW.Thickness(0, 4, 0, 0)
        });
        return new WC.ToolTip { Content = panel };
    }

    private WC.Button FooterButton(string text, Action action)
    {
        WC.Button b = new() { Content = text, MinWidth = 96, Padding = new SW.Thickness(10, 3, 10, 3), Margin = new SW.Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => action();
        return b;
    }

    private WC.TextBlock Link(string text, Action action, string tip, bool active = false)
    {
        var t = new WC.TextBlock
        {
            Text = text,
            Foreground = active ? T.Text : T.Accent,
            FontWeight = active ? SW.FontWeights.Bold : SW.FontWeights.Normal,
            FontSize = 11,
            Cursor = WI.Cursors.Hand,
            ToolTip = tip,
            VerticalAlignment = SW.VerticalAlignment.Center,
            Margin = new SW.Thickness(8, 0, 0, 0)
        };
        t.MouseEnter += (_, _) => t.TextDecorations = SW.TextDecorations.Underline;
        t.MouseLeave += (_, _) => t.TextDecorations = null;
        t.MouseLeftButtonDown += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return t;
    }

    private WC.TextBlock Sep() => new() { Text = "  |", Foreground = T.Muted, FontSize = 11, VerticalAlignment = SW.VerticalAlignment.Center };

    private WM.ImageSource IconOf(string key)
    {
        if (!_icons.TryGetValue(key, out WM.ImageSource? img))
            _icons[key] = img = ToolIcons.Create(key, T.Text, T.Accent);
        return img;
    }

    private void Say(string text) => _status.Text = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "  " + text;

    private void Send(string command, string message)
    {
        if (Doc is not Document doc)
            return;
        Say(message);
        doc.SendStringToExecute("\x03\x03" + command, true, false, false);
        try
        {
            Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
        }
        catch
        {
            // O comando roda mesmo sem foco.
        }
    }

    private static string N(double v, string fmt = "0.000") => v.ToString(fmt, CultureInfo.InvariantCulture);

    private static string N(double? v, string fmt = "0.000") => v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "-";

    private static string Cm(double? v) => v.HasValue ? (v.Value * 100).ToString("0.#", CultureInfo.InvariantCulture) + " cm" : "-";

    private static string Pct(double? v) => v.HasValue ? (Math.Abs(v.Value) * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%" : "-";

    // ------------------------------------------------------------------
    // Dados
    // ------------------------------------------------------------------

    private void Refresh()
    {
        Document? doc = Doc;
        Watch(doc);
        if (doc is null)
        {
            _items.Clear();
            _corridors.Clear();
            _warnings.Clear();
            Say("Nenhum desenho aberto.");
            FillCategories();
            FillList();
            BuildTabs();
            RenderGraphic();
            return;
        }

        try
        {
            using (doc.LockDocument())
            {
                _items = MFRebaixoData.ReadAll(doc.Database);
                _corridors = MFRebaixoData.Corridors(doc.Database);
                _overview = MFRebaixoData.Overview(doc.Database);
            }
        }
        catch (System.Exception ex)
        {
            Say("Erro ao ler os rebaixos: " + ex.Message);
            return;
        }

        foreach (CorridorSummary c in _corridors)
            c.Rebaixos = _items.Count(r => r.CorridorId == c.Id);
        _warnings = MFRebaixoData.Check(_items);
        if (MFRebaixoData.RemovedSets.Count > 0)
        {
            _warnings.Add(new RebaixoWarning
            {
                Serious = false,
                Text = $"{MFRebaixoData.RemovedSets.Count} Transition Set(s) de rebaixos removidos (desligados, sem efeito): " +
                       string.Join("; ", MFRebaixoData.RemovedSets.Take(6)) + (MFRebaixoData.RemovedSets.Count > 6 ? "; ..." : ""),
                Reference = "A API do Civil nao apaga Transition Set. Para limpar a lista: Edit Corridor Transition > apagar os sets 'REMOVIDO'"
            });
        }

        if (_pending is { } pend)
        {
            _current = _items.FirstOrDefault(r => r.CorridorId == pend.Corridor && r.BaselineKey == pend.Baseline && Math.Abs(r.S1 - pend.S1) < 0.01) ?? _current;
            _pending = null;
        }

        if (_current is not null)
            _current = _items.FirstOrDefault(r => r.CorridorId == _current.CorridorId && r.SetName == _current.SetName)
                       ?? _items.FirstOrDefault(r => r.CorridorId == _current.CorridorId && r.BaselineKey == _current.BaselineKey && Math.Abs(r.S1 - _current.S1) < 0.01);
        _current ??= _items.FirstOrDefault();
        if (_currentCorridor is not null)
            _currentCorridor = _corridors.FirstOrDefault(c => c.Id == _currentCorridor.Id);

        Title = $"Rebaixos de meio-fio - Explorador [{System.IO.Path.GetFileName(doc.Name)}]";
        FillCategories();
        FillList();
        BuildTabs();
        RenderGraphic();
        Say($"{_items.Count} rebaixo(s) em {_corridors.Count} corredor(es); {_warnings.Count(w => w.Serious)} aviso(s) importante(s).");
    }

    private int CountFor(string category) => category switch
    {
        "REBAIXOS" => _items.Count,
        "CORREDORES" => _corridors.Count,
        "AVISOS" => _warnings.Count,
        _ => -1
    };

    private void FillCategories()
    {
        _categoryBar.Children.Clear();
        foreach ((string id, string icon, string title) in Categories)
        {
            int count = CountFor(id);
            WC.StackPanel content = new() { Orientation = WC.Orientation.Horizontal };
            content.Children.Add(new WC.Image { Source = IconOf(icon), Width = 18, Height = 18 });
            content.Children.Add(new WC.TextBlock
            {
                Text = " " + title + (count >= 0 ? $" ({count})" : ""),
                Foreground = id == "AVISOS" && _warnings.Any(w => w.Serious) ? ToolTheme.Brush(230, 150, 30) : T.Text,
                VerticalAlignment = SW.VerticalAlignment.Center
            });

            bool selected = id == _category;
            WC.Button button = new()
            {
                Content = content,
                ToolTip = title,
                Padding = new SW.Thickness(5, 2, 7, 2),
                Margin = new SW.Thickness(0, 0, 2, 0),
                Background = selected ? T.Selected : WM.Brushes.Transparent,
                BorderBrush = selected ? T.Accent : WM.Brushes.Transparent,
                BorderThickness = new SW.Thickness(0, 0, 0, 2)
            };
            string cat = id;
            button.Click += (_, _) => SelectCategory(cat);
            _categoryBar.Children.Add(button);
        }
    }

    private void SelectCategory(string id)
    {
        _category = id;
        _tab = "Propriedades";
        FillCategories();
        FillList();
        BuildTabs();
    }

    private void SelectTab(string tab)
    {
        if (_category is not ("REBAIXOS" or "AVISOS"))
            SelectCategory("REBAIXOS");
        _tab = tab;
        BuildTabs();
    }

    // ------------------------------------------------------------------
    // Lista
    // ------------------------------------------------------------------

    private sealed class Row
    {
        public string Icon { get; init; } = "generico";
        public string[] Cells { get; init; } = Array.Empty<string>();
        public object? Data { get; init; }
    }

    private sealed class RowView
    {
        public RowView(Row row, WM.ImageSource icon)
        {
            IconImage = icon;
            string[] c = row.Cells;
            string At(int i) => c.Length > i ? c[i] : "";
            C0 = At(0);
            C1 = At(1);
            C2 = At(2);
            C3 = At(3);
            C4 = At(4);
            C5 = At(5);
            C6 = At(6);
            C7 = At(7);
        }

        public WM.ImageSource IconImage { get; }
        public string C0 { get; }
        public string C1 { get; }
        public string C2 { get; }
        public string C3 { get; }
        public string C4 { get; }
        public string C5 { get; }
        public string C6 { get; }
        public string C7 { get; }
    }

    private void FillList()
    {
        string filter = _find.Text.Trim();
        bool Match(params string[] s) => filter.Length == 0 || s.Any(x => x.Contains(filter, StringComparison.OrdinalIgnoreCase));

        string[] headers;
        double[] widths;
        List<Row> rows;

        switch (_category)
        {
            case "CORREDORES":
                headers = new[] { "Corredor", "Baselines", "Regions", "Regions com MF", "Rebaixos" };
                widths = new[] { 200.0, 70, 70, 100, 70 };
                rows = _corridors.Where(c => Match(c.Name)).Select(c => new Row
                {
                    Icon = "corredor",
                    Data = c,
                    Cells = new[] { c.Name, c.Baselines.ToString(), c.Regions.ToString(), c.MfRegions.ToString(), c.Rebaixos.ToString() }
                }).ToList();
                break;

            case "AVISOS":
                headers = new[] { "Rebaixo", "Aviso", "Referencia" };
                widths = new[] { 150.0, 330, 260 };
                rows = _warnings.Where(w => Match(w.Text, w.Reference, w.Item?.Label ?? "", w.Item?.CorridorName ?? "")).Select(w => new Row
                {
                    Icon = w.Serious ? "aviso" : "ajuda",
                    Data = w,
                    Cells = new[] { w.Item is null ? "(desenho)" : $"{w.Item.Label} ({w.Item.CorridorName})", w.Text, w.Reference }
                }).ToList();
                break;

            case "CONFIG":
                headers = new[] { "Opcao", "Valor" };
                widths = new[] { 240.0, 160 };
                rows = new List<Row>
                {
                    new() { Icon = "config", Data = "margem", Cells = new[] { "Trecho mostrado antes/depois do rebaixo", N(_settings.PainelMargem, "0.#") + " m" } },
                    new() { Icon = "config", Data = "altura", Cells = new[] { "Ultima altura livre final (MFREBAIXO)", Cm(_settings.UltimaAlturaFinal) } },
                    new() { Icon = "config", Data = "reducao", Cells = new[] { "Ultima reducao (MFREBAIXO)", Cm(_settings.UltimaReducao) } }
                };
                break;

            default:
                headers = new[] { "Rebaixo", "Estacas (S1 - S2)", "Corredor", "Lado", "Rebaixado", "Altura final", "Rampa", "Avisos" };
                widths = new[] { 160.0, 115, 140, 65, 70, 75, 55, 50 };
                rows = _items.Where(r => Match(r.Label, r.Stations, r.CorridorName, r.BaselineName, r.RegionName)).Select(r =>
                {
                    int w = _warnings.Count(x => ReferenceEquals(x.Item, r));
                    bool serious = _warnings.Any(x => ReferenceEquals(x.Item, r) && x.Serious);
                    return new Row
                    {
                        Icon = serious ? "aviso" : "rebaixo",
                        Data = r,
                        Cells = new[]
                        {
                            r.Label, r.Stations, r.CorridorName, r.SideLabel, N(r.LoweredLength, "0.00") + " m",
                            Cm(r.FinalHeight), Pct(r.RampSlope), w == 0 ? "" : w.ToString()
                        }
                    };
                }).ToList();
                break;
        }

        var view = new WC.GridView();
        for (int i = 0; i < headers.Length; i++)
        {
            var col = new WC.GridViewColumn { Header = headers[i], Width = widths[i] };
            if (i == 0)
            {
                var sp = new SW.FrameworkElementFactory(typeof(WC.StackPanel));
                sp.SetValue(WC.StackPanel.OrientationProperty, WC.Orientation.Horizontal);
                var img = new SW.FrameworkElementFactory(typeof(WC.Image));
                img.SetBinding(WC.Image.SourceProperty, new SW.Data.Binding("IconImage"));
                img.SetValue(SW.FrameworkElement.WidthProperty, 16.0);
                img.SetValue(SW.FrameworkElement.HeightProperty, 16.0);
                img.SetValue(SW.FrameworkElement.MarginProperty, new SW.Thickness(0, 0, 5, 0));
                sp.AppendChild(img);
                var tb = new SW.FrameworkElementFactory(typeof(WC.TextBlock));
                tb.SetBinding(WC.TextBlock.TextProperty, new SW.Data.Binding("C0"));
                tb.SetValue(SW.FrameworkElement.VerticalAlignmentProperty, SW.VerticalAlignment.Center);
                sp.AppendChild(tb);
                col.CellTemplate = new SW.DataTemplate { VisualTree = sp };
            }
            else
            {
                col.DisplayMemberBinding = new SW.Data.Binding($"C{i}");
            }

            view.Columns.Add(col);
        }

        _list.View = view;
        _list.Items.Clear();
        WC.ListViewItem? keep = null;
        foreach (Row row in rows)
        {
            var item = new WC.ListViewItem { Content = new RowView(row, IconOf(row.Icon)), Tag = row };
            _list.Items.Add(item);
            if ((row.Data is RebaixoInfo r && ReferenceEquals(r, _current) && _category == "REBAIXOS") ||
                (row.Data is CorridorSummary c && ReferenceEquals(c, _currentCorridor)))
            {
                keep = item;
            }
        }

        if (keep is not null)
        {
            _list.SelectedItem = keep;
            _list.ScrollIntoView(keep);
        }
    }

    private object? SelectedData => (_list.SelectedItem as WC.ListViewItem)?.Tag is Row r ? r.Data : null;

    private void OnRowSelected()
    {
        switch (SelectedData)
        {
            case RebaixoInfo r:
                _current = r;
                break;
            case RebaixoWarning { Item: not null } w:
                _current = w.Item;
                break;
            case CorridorSummary c:
                _currentCorridor = c;
                break;
        }

        BuildTabs();
        RenderGraphic();
    }

    // ------------------------------------------------------------------
    // Vista grafica: planta geral ou planta do rebaixo
    // ------------------------------------------------------------------

    private WC.Button ModeButton(string mode, string icon, string text, string tip)
    {
        bool active = Mode == mode;
        WC.StackPanel content = new() { Orientation = WC.Orientation.Horizontal };
        content.Children.Add(new WC.Image { Source = IconOf(icon), Width = 14, Height = 14 });
        content.Children.Add(new WC.TextBlock { Text = " " + text, Foreground = T.Text, FontSize = 11, VerticalAlignment = SW.VerticalAlignment.Center });
        WC.Button b = new()
        {
            Content = content,
            ToolTip = tip,
            Padding = new SW.Thickness(6, 1, 8, 1),
            Margin = new SW.Thickness(2, 1, 0, 1),
            Background = active ? T.Selected : WM.Brushes.Transparent,
            BorderBrush = active ? T.Accent : WM.Brushes.Transparent,
            BorderThickness = new SW.Thickness(0, 0, 0, 2)
        };
        b.Click += (_, _) => SetMode(mode);
        return b;
    }

    private void BuildViewToggles()
    {
        _viewToggles.Children.Clear();
        _viewToggles.Children.Add(ModeButton("GERAL", "mapa", "Planta geral", "Todos os corredores e rebaixos do desenho; clique num rebaixo para selecionar, duplo clique para abrir"));
        _viewToggles.Children.Add(ModeButton("PLANTA", "planta", "Planta do rebaixo", "Trecho do corredor do rebaixo selecionado, colorido pelos codes"));

        if (Mode == "PLANTA")
        {
            _viewToggles.Children.Add(Sep());
            foreach (double m in new[] { 3.0, 6.0, 15.0 })
            {
                double mm = m;
                _viewToggles.Children.Add(Link($"+{m:0} m", () =>
                {
                    _settings.PainelMargem = mm;
                    SaveSettings();
                    _modelKey = null;
                    BuildViewToggles();
                    RenderGraphic();
                }, "Trecho mostrado antes e depois do rebaixo", Math.Abs(_settings.PainelMargem - m) < 1e-6));
            }
        }

        _viewToggles.Children.Add(Sep());
        // Abre no SOLTAR do botao: aberto no MouseDown, o MouseUp que vem em seguida caia
        // fora do popup (StaysOpen = false) e o fechava na hora.
        WC.TextBlock textos = Link("Textos \u25BE", () => { }, "Escolher quais textos aparecem na vista e na barra de baixo");
        textos.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_textPopup?.IsOpen == true)
            {
                _textPopup.IsOpen = false;
                return;
            }

            // Clique no proprio link com o menu aberto: o MouseDown ja fechou; nao reabre.
            if ((DateTime.Now - _textPopupClosed).TotalMilliseconds < 250)
                return;
            ShowTextOptions(textos);
        };
        _viewToggles.Children.Add(textos);
    }

    // ------------------------------------------------------------------
    // Textos da vista (liga/desliga)
    // ------------------------------------------------------------------

    /// <summary>Chave, grupo, texto e dica de cada texto que pode ser ligado/desligado.</summary>
    private static readonly (string Key, string Group, string Text, string Tip)[] TextOptions =
    {
        ("CODIGO", "Etiqueta do rebaixo", "Codigo (R01, R02...)", "Codigo automatico do rebaixo"),
        ("NOME", "Etiqueta do rebaixo", "Nome", "Nome dado na aba Propriedades"),
        ("ESTACAS", "Etiqueta do rebaixo", "Estacas", "Estacas inicial e final do trecho rebaixado"),
        ("TODOS", "Etiqueta do rebaixo", "Em todos os rebaixos", "Desligado: etiqueta so no rebaixo selecionado"),
        ("CHAMADA", "Etiqueta do rebaixo", "Linha de chamada", "Linha ligando a etiqueta ao rebaixo"),
        ("CORREDOR", "Planta", "Nome do corredor", "Nome do corredor ao longo do eixo (planta geral)"),
        ("BASELINE", "Planta", "Nome das baselines", "Nome de cada baseline ao longo do eixo (planta geral)"),
        ("MARCOS", "Planta", "Marcos S0-S3", "Inicio/fim das rampas e do trecho rebaixado (planta do rebaixo)"),
        ("LEGENDA", "Planta", "Legenda", "Cores da vista"),
        ("ESCALA", "Planta", "Escala grafica", "Barra de escala no canto"),
        ("RESUMO", "Barra de baixo", "Resumo", "Quantidade de rebaixos / dados do rebaixo selecionado"),
        ("CURSOR", "Barra de baixo", "O que esta sob o cursor", "Rebaixo, corredor ou secao sob o mouse")
    };

    private WC.Primitives.Popup? _textPopup;
    private DateTime _textPopupClosed = DateTime.MinValue;

    private bool Show(string key) => !_settings.PainelTextosOcultos.Contains(key);

    private void SetShow(string key, bool on)
    {
        _settings.PainelTextosOcultos.RemoveAll(x => x == key);
        if (!on)
            _settings.PainelTextosOcultos.Add(key);
        SaveSettings();
        if (!Show("CURSOR"))
            _coords.Text = "";
        RenderGraphic();
    }

    private void ShowTextOptions(SW.UIElement target)
    {
        WC.StackPanel list = new() { Margin = new SW.Thickness(10, 6, 10, 8) };
        List<WC.CheckBox> boxes = new();
        string? group = null;
        foreach ((string key, string g, string text, string tip) in TextOptions)
        {
            if (g != group)
            {
                group = g;
                list.Children.Add(new WC.TextBlock { Text = g, Foreground = T.Muted, FontSize = 11, FontWeight = SW.FontWeights.Bold, Margin = new SW.Thickness(0, list.Children.Count == 0 ? 0 : 8, 0, 2) });
            }

            WC.CheckBox box = new() { Content = text, IsChecked = Show(key), ToolTip = tip, Foreground = T.Text, Margin = new SW.Thickness(4, 2, 0, 2), Tag = key };
            string k = key;
            box.Click += (_, _) => SetShow(k, box.IsChecked == true);
            boxes.Add(box);
            list.Children.Add(box);
        }

        void SetAll(Func<string, bool> on)
        {
            _settings.PainelTextosOcultos = TextOptions.Select(o => o.Key).Where(k => !on(k)).ToList();
            foreach (WC.CheckBox b in boxes)
                b.IsChecked = on((string)b.Tag);
            SaveSettings();
            if (!Show("CURSOR"))
                _coords.Text = "";
            RenderGraphic();
        }

        WC.StackPanel quick = new() { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(0, 10, 0, 0) };
        quick.Children.Add(Link("Tudo", () => SetAll(_ => true), "Ligar todos os textos"));
        quick.Children.Add(Link("Nada", () => SetAll(_ => false), "Desligar todos os textos"));
        quick.Children.Add(Link("Padrao", () => SetAll(k => k is not ("ESTACAS" or "BASELINE")), "Codigo, nome, corredor, marcos, legenda, escala e barra"));
        list.Children.Add(quick);

        if (_textPopup is not null)
            _textPopup.IsOpen = false;
        var popup = new WC.Primitives.Popup
        {
            PlacementTarget = target,
            Placement = WC.Primitives.PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new WC.Border { Background = T.Pane, BorderBrush = T.Line, BorderThickness = new SW.Thickness(1), Child = list }
        };
        popup.Closed += (_, _) => _textPopupClosed = DateTime.Now;
        _textPopup = popup;
        popup.IsOpen = true;
    }

    private void SetMode(string mode)
    {
        _settings.PainelModo = mode;
        SaveSettings();
        _zoom = 1;
        _panX = _panY = 0;
        BuildViewToggles();
        ShowView();
        RenderGraphic();
    }

    private void ShowView()
    {
        _plan.Visibility = SW.Visibility.Visible;
        _coords.Text = "";
    }

    private void SaveSettings()
    {
        try
        {
            MFRebaixoSettingsStore.Save(_settings);
        }
        catch
        {
            // Preferencia nao gravada: segue so nesta sessao.
        }
    }

    private RebaixoModel? CurrentModel()
    {
        RebaixoInfo? r = _current;
        if (r is null || Doc is not Document doc)
            return null;

        string key = $"{r.CorridorId.Handle.Value}|{r.SetName}|{N(r.S0)}|{N(r.S3)}|{N(_settings.PainelMargem)}";
        if (_model is not null && key == _modelKey)
            return _model;

        try
        {
            using (doc.LockDocument())
                _model = MFRebaixoData.Model(doc.Database, r, _settings.PainelMargem);
            _modelKey = key;
        }
        catch (System.Exception ex)
        {
            _model = null;
            _modelKey = null;
            Say("Erro ao ler o corredor: " + ex.Message);
        }

        return _model;
    }

    private void RenderGraphic()
    {
        RebaixoModel? model = Mode == "GERAL" ? null : CurrentModel();
        RenderPlan();

        RebaixoInfo? r = _current;
        _graphicInfo.Text = !Show("RESUMO") ? "" : Mode == "GERAL"
            ? $"{_items.Count} rebaixo(s) em {_overview.Count} baseline(s)" + (r is null ? "" : $"   |   selecionado: {r.Label}")
            : r is null
                ? "Nenhum rebaixo selecionado."
                : $"{r.Label}   {r.CorridorName} / {r.BaselineName}   lado {r.SideLabel.ToLowerInvariant()}   rampa {N(r.S0, "0.00")}-{N(r.S1, "0.00")}   rebaixado {N(r.S1, "0.00")}-{N(r.S2, "0.00")}   rampa {N(r.S2, "0.00")}-{N(r.S3, "0.00")}" +
                  (model is null ? "" : $"   ({model.Sections.Count} secoes)");
    }

    private SW.Point ToScreen(double x, double y) => new(_ox + ((x - _cx) * _scale), _oy - ((y - _cy) * _scale));

    private (double X, double Y) ToWorld(SW.Point p) => (_cx + ((p.X - _ox) / _scale), _cy - ((p.Y - _oy) / _scale));

    private void RenderPlan()
    {
        if (Mode == "GERAL")
            RenderOverview();
        else
            RenderDetail();
    }

    // ------------------------------------------------------------------
    // Planta geral
    // ------------------------------------------------------------------

    private static readonly WM.Brush HaloBrush = ToolTheme.Brush(40, 42, 46, 215);
    private static readonly WM.Brush RebaixoBrush = ToolTheme.Brush(225, 45, 60);
    private static readonly WM.Brush WarnBrush = ToolTheme.Brush(240, 150, 30);
    private static readonly WM.Brush SelectedBrush = ToolTheme.Brush(255, 205, 60);
    private static readonly WM.Brush DarkText = ToolTheme.Brush(25, 25, 25);

    private static (string Corridor, string Baseline) SplitName(string name)
    {
        int k = name.IndexOf(" / ", StringComparison.Ordinal);
        return k < 0 ? (name, "") : (name[..k], name[(k + 3)..]);
    }

    private bool Inside(SW.Rect r) => r.Left >= 0 && r.Top >= 0 && r.Right <= _plan.ActualWidth && r.Bottom <= _plan.ActualHeight;

    /// <summary>Coloca o elemento perto de anchor na primeira posicao livre (sem sobrepor outros textos).</summary>
    private SW.Rect? Place(SW.FrameworkElement el, SW.Point anchor, bool force)
    {
        el.Measure(new SW.Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = el.DesiredSize.Width, h = el.DesiredSize.Height;
        SW.Vector[] offsets =
        {
            new(10, -h - 6), new(10, 6), new(-w - 10, -h - 6), new(-w - 10, 6),
            new(-w / 2, -h - 12), new(-w / 2, 12), new(18, -h / 2), new(-w - 18, -h / 2),
            new(14, -h - 22), new(-w - 14, -h - 22), new(14, 22), new(-w - 14, 22)
        };
        foreach (SW.Vector o in offsets)
        {
            SW.Rect r = new(anchor.X + o.X, anchor.Y + o.Y, w, h);
            if (Inside(r) && !_labelRects.Any(x => x.IntersectsWith(r)))
                return Put(el, r);
        }

        return force ? Put(el, new SW.Rect(anchor.X + offsets[0].X, anchor.Y + offsets[0].Y, w, h)) : null;
    }

    private SW.Rect Put(SW.FrameworkElement el, SW.Rect r)
    {
        WC.Canvas.SetLeft(el, r.Left);
        WC.Canvas.SetTop(el, r.Top);
        _plan.Children.Add(el);
        _labelRects.Add(r);
        return r;
    }

    private static SW.Point Nearest(SW.Rect r, SW.Point p) =>
        new(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));

    /// <summary>Texto ao longo da linha (girado, sempre de pe), com fundo escuro.</summary>
    private void AlongLabel(string text, List<SW.Point> line, double size, WM.Brush fg)
    {
        if (line.Count < 2)
            return;

        // Ponto no meio do comprimento visivel.
        double total = 0;
        for (int k = 1; k < line.Count; k++)
            total += (line[k] - line[k - 1]).Length;
        double half = total / 2, run = 0;
        SW.Point at = line[0];
        SW.Vector dir = line[1] - line[0];
        for (int k = 1; k < line.Count; k++)
        {
            double seg = (line[k] - line[k - 1]).Length;
            if (run + seg >= half && seg > 1e-6)
            {
                dir = line[k] - line[k - 1];
                at = line[k - 1] + (dir * ((half - run) / seg));
                break;
            }

            run += seg;
        }

        double angle = Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI;
        if (angle > 90) angle -= 180;
        if (angle < -90) angle += 180;

        var t = new WC.TextBlock
        {
            Text = text,
            Foreground = fg,
            FontSize = size,
            Background = HaloBrush,
            Padding = new SW.Thickness(3, 0, 3, 0),
            IsHitTestVisible = false,
            RenderTransformOrigin = new SW.Point(0.5, 0.5),
            RenderTransform = new WM.RotateTransform(angle)
        };
        t.Measure(new SW.Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = t.DesiredSize.Width, h = t.DesiredSize.Height;
        if (w > total * 0.9)
            return; // nao cabe ao longo da linha visivel

        // Caixa que envolve o texto girado.
        double a = Math.Abs(angle * Math.PI / 180.0);
        double bw = (w * Math.Cos(a)) + (h * Math.Sin(a)), bh = (w * Math.Sin(a)) + (h * Math.Cos(a));
        SW.Rect box = new(at.X - (bw / 2), at.Y - (bh / 2), bw, bh);
        if (!Inside(box) || _labelRects.Any(x => x.IntersectsWith(box)))
            return;

        WC.Canvas.SetLeft(t, at.X - (w / 2));
        WC.Canvas.SetTop(t, at.Y - (h / 2));
        _plan.Children.Add(t);
        _labelRects.Add(box);
    }

    /// <summary>Parte da polilinha que aparece na tela (pontos de tela).</summary>
    private List<SW.Point> VisiblePart(List<Point3d> pts, double w, double h)
    {
        List<SW.Point> best = new(), cur = new();
        foreach (Point3d p in pts)
        {
            SW.Point s = ToScreen(p.X, p.Y);
            if (s.X >= 0 && s.Y >= 0 && s.X <= w && s.Y <= h)
            {
                cur.Add(s);
            }
            else
            {
                if (cur.Count > best.Count)
                    best = cur;
                cur = new List<SW.Point>();
            }
        }

        return cur.Count > best.Count ? cur : best;
    }

    /// <summary>Planta geral: baselines dos corredores (com nomes) e os rebaixos identificados por codigo e nome.</summary>
    private void RenderOverview()
    {
        _plan.Children.Clear();
        _labelRects.Clear();
        double w = _plan.ActualWidth, h = _plan.ActualHeight;
        if (w < 20 || h < 20 || !_plan.IsVisible)
            return;

        List<Point3d> all = _overview.SelectMany(o => o.Points).Concat(_items.SelectMany(r => r.Path)).ToList();
        if (all.Count == 0)
        {
            _scale = 0;
            PlanLabel("Nenhum corredor no desenho.", 10, 10, 12);
            return;
        }

        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
        _scale = 0.9 * Math.Min(w / Math.Max(maxX - minX, 1e-6), h / Math.Max(maxY - minY, 1e-6)) * _zoom;
        _cx = ((minX + maxX) / 2.0) + _panX;
        _cy = ((minY + maxY) / 2.0) + _panY;
        _ox = w / 2.0;
        _oy = h / 2.0;

        // Legenda e escala primeiro: reservam o espaco dos cantos.
        double ly = 8;
        foreach ((string text, WM.Brush b) in Show("LEGENDA") ? new[] { ("rebaixo", RebaixoBrush), ("com aviso", WarnBrush), ("selecionado", SelectedBrush) } : Array.Empty<(string, WM.Brush)>())
        {
            var sw = new WS.Rectangle { Width = 14, Height = 6, Fill = b, IsHitTestVisible = false };
            WC.Canvas.SetLeft(sw, 8);
            WC.Canvas.SetTop(sw, ly + 5);
            _plan.Children.Add(sw);
            PlanLabel(text, 26, ly, 11);
            ly += 15;
        }

        if (Show("LEGENDA"))
            _labelRects.Add(new SW.Rect(0, 0, 110, ly + 4));
        if (Show("ESCALA"))
        {
            double step = NiceStep(Math.Max(w, h) / _scale / 8.0);
            double len = step * _scale;
            _plan.Children.Add(new WS.Line { X1 = w - len - 14, Y1 = h - 12, X2 = w - 14, Y2 = h - 12, Stroke = ToolTheme.GraphicText, StrokeThickness = 2, IsHitTestVisible = false });
            PlanLabel($"{step:0.##} m", w - len - 14, h - 28, 10);
            _labelRects.Add(new SW.Rect(w - len - 20, h - 30, len + 20, 30));
        }

        // Baselines: as do corredor do rebaixo selecionado mais claras.
        AcadDb.ObjectId? selCorridor = _current?.CorridorId;
        WM.Brush baseBrush = ToolTheme.Brush(120, 123, 128), baseSel = ToolTheme.Brush(185, 188, 194);
        foreach ((AcadDb.ObjectId id, string _, List<Point3d> pts) in _overview)
        {
            bool mine = selCorridor is AcadDb.ObjectId sid && sid == id;
            var line = new WS.Polyline { Stroke = mine ? baseSel : baseBrush, StrokeThickness = mine ? 1.6 : 1.1, IsHitTestVisible = false };
            foreach (Point3d p in pts)
                line.Points.Add(ToScreen(p.X, p.Y));
            _plan.Children.Add(line);
        }

        // Rebaixos: traco grosso ao longo da baseline.
        foreach (RebaixoInfo r in _items.OrderBy(x => ReferenceEquals(x, _current)))
        {
            if (r.Path.Count == 0)
                continue;
            bool selected = ReferenceEquals(r, _current);
            bool serious = _warnings.Any(x => ReferenceEquals(x.Item, r) && x.Serious);
            var path = new WS.Polyline
            {
                Stroke = selected ? SelectedBrush : serious ? WarnBrush : RebaixoBrush,
                StrokeThickness = selected ? 7 : 5,
                StrokeStartLineCap = WM.PenLineCap.Round,
                StrokeEndLineCap = WM.PenLineCap.Round,
                IsHitTestVisible = false
            };
            foreach (Point3d p in r.Path)
                path.Points.Add(ToScreen(p.X, p.Y));
            _plan.Children.Add(path);
        }

        // Etiquetas dos rebaixos: o selecionado primeiro (sempre aparece), depois os demais na ordem.
        bool showCode = Show("CODIGO"), showName = Show("NOME"), showStations = Show("ESTACAS");
        foreach (RebaixoInfo r in _items.OrderBy(x => ReferenceEquals(x, _current) ? 0 : 1).ThenBy(x => x.Code))
        {
            if (r.Path.Count == 0)
                continue;
            bool selected = ReferenceEquals(r, _current);
            if (!selected && !Show("TODOS"))
                continue;
            List<string> parts = new();
            if (showCode)
                parts.Add(r.Code);
            if (showName && r.CustomName.Length > 0)
                parts.Add(r.CustomName);
            if (showStations)
                parts.Add(r.Stations);
            if (parts.Count == 0)
                continue;
            bool serious = _warnings.Any(x => ReferenceEquals(x.Item, r) && x.Serious);
            Point3d mid = r.Path[r.Path.Count / 2];
            SW.Point anchor = ToScreen(mid.X, mid.Y);
            if (anchor.X < 0 || anchor.Y < 0 || anchor.X > w || anchor.Y > h)
                continue;

            WC.StackPanel content = new() { Orientation = WC.Orientation.Horizontal };
            for (int k = 0; k < parts.Count; k++)
            {
                bool bold = k == 0 && showCode;
                content.Children.Add(new WC.TextBlock { Text = (k == 0 ? "" : "  ") + parts[k], FontWeight = bold ? SW.FontWeights.Bold : SW.FontWeights.Normal, FontSize = selected ? 12 : bold ? 11 : 10.5, Foreground = DarkText });
            }

            var tag = new WC.Border
            {
                Child = content,
                Background = selected ? SelectedBrush : serious ? WarnBrush : RebaixoBrush,
                BorderBrush = DarkText,
                BorderThickness = new SW.Thickness(selected ? 1.5 : 0.8),
                CornerRadius = new SW.CornerRadius(3),
                Padding = new SW.Thickness(4, 1, 4, 1),
                Cursor = WI.Cursors.Hand,
                ToolTip = $"{r.Label}\n{r.CorridorName} / {r.BaselineName}, lado {r.SideLabel.ToLowerInvariant()}\n" +
                          $"estacas {r.Stations} (rebaixado {N(r.LoweredLength, "0.00")} m, total {N(r.TotalLength, "0.00")} m)\n" +
                          $"altura final {Cm(r.FinalHeight)}\nclique: selecionar   duplo clique: planta do rebaixo"
            };
            RebaixoInfo item = r;
            tag.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                SelectRebaixo(item);
                if (e.ClickCount == 2)
                    SetMode("PLANTA");
            };

            if (Place(tag, anchor, force: selected) is SW.Rect box && Show("CHAMADA"))
            {
                SW.Point end = Nearest(box, anchor);
                var leader = new WS.Line { X1 = anchor.X, Y1 = anchor.Y, X2 = end.X, Y2 = end.Y, Stroke = tag.Background, StrokeThickness = 1.2, IsHitTestVisible = false };
                _plan.Children.Insert(Math.Max(0, _plan.Children.Count - 1), leader);
            }
        }

        // Nomes dos corredores ao longo da baseline mais comprida de cada um (por ultimo: so onde sobra espaco).
        foreach (IGrouping<AcadDb.ObjectId, (AcadDb.ObjectId Corridor, string Name, List<Point3d> Points)> g in _overview.GroupBy(o => o.Corridor))
        {
            var visible = g.Select(o => (o.Name, Line: VisiblePart(o.Points, w, h))).Where(x => x.Line.Count > 1).ToList();
            if (visible.Count == 0)
                continue;
            var longest = visible.OrderByDescending(x => x.Line.Count).First();
            bool mine = selCorridor is AcadDb.ObjectId sid && sid == g.Key;
            if (Show("CORREDOR"))
                AlongLabel(SplitName(longest.Name).Corridor, longest.Line, mine ? 12 : 11, mine ? ToolTheme.GraphicText : ToolTheme.GraphicLine);
            if (Show("BASELINE"))
            {
                foreach (var (name, line) in visible)
                    AlongLabel(SplitName(name).Baseline, line, 9, ToolTheme.GraphicLine);
            }
        }
    }

    private void SelectRebaixo(RebaixoInfo r)
    {
        _current = r;
        if (_category != "REBAIXOS")
        {
            _category = "REBAIXOS";
            FillCategories();
        }

        FillList();
        BuildTabs();
        RenderGraphic();
    }

    private void RenderDetail()
    {
        _plan.Children.Clear();
        double w = _plan.ActualWidth, h = _plan.ActualHeight;
        if (w < 20 || h < 20 || !_plan.IsVisible)
            return;

        RebaixoModel? model = CurrentModel();
        if (model is null || model.Sections.Count < 2)
        {
            _scale = 0;
            PlanLabel(model is null ? "Selecione um rebaixo." : "Sem secoes calculadas neste trecho (Rebuild no corredor?).", 10, 10, 12);
            return;
        }

        List<Point3d> all = model.Sections.SelectMany(s => s.Subassemblies.SelectMany(a => a.Links.SelectMany(l => l.Points))).ToList();
        if (all.Count == 0)
            return;

        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
        _scale = 0.9 * Math.Min(w / Math.Max(maxX - minX, 1e-6), h / Math.Max(maxY - minY, 1e-6)) * _zoom;
        _cx = ((minX + maxX) / 2.0) + _panX;
        _cy = ((minY + maxY) / 2.0) + _panY;
        _ox = w / 2.0;
        _oy = h / 2.0;

        // Faixas entre secoes vizinhas (como a hachura do Code Set), na ordem dos codes.
        List<(int Order, WM.Color Color, SW.Point[] Poly)> polys = new();
        for (int k = 0; k + 1 < model.Sections.Count; k++)
        {
            ModelSection a = model.Sections[k], b = model.Sections[k + 1];
            foreach ((long key, List<ModelLink> linksA) in a.Subassemblies)
            {
                List<ModelLink>? linksB = b.Subassemblies.FirstOrDefault(x => x.Key == key).Links;
                if (linksB is null)
                    continue;
                int n = Math.Min(linksA.Count, linksB.Count);
                for (int j = 0; j < n; j++)
                {
                    ModelLink la = linksA[j], lb = linksB[j];
                    if (la.Points.Length < 2 || la.Points.Length != lb.Points.Length)
                        continue;
                    RebaixoStyle.Look look = RebaixoStyle.Of(la.Codes);
                    if (!look.Plan)
                        continue;
                    for (int p = 0; p + 1 < la.Points.Length; p++)
                    {
                        polys.Add((look.Order, look.Color, new[]
                        {
                            ToScreen(la.Points[p].X, la.Points[p].Y), ToScreen(la.Points[p + 1].X, la.Points[p + 1].Y),
                            ToScreen(lb.Points[p + 1].X, lb.Points[p + 1].Y), ToScreen(lb.Points[p].X, lb.Points[p].Y)
                        }));
                    }
                }
            }
        }

        Dictionary<WM.Color, WM.SolidColorBrush> brushes = new();
        foreach (var group in polys.GroupBy(x => (x.Order, x.Color)).OrderBy(x => x.Key.Order))
        {
            var geo = new WM.StreamGeometry { FillRule = WM.FillRule.Nonzero };
            using (WM.StreamGeometryContext ctx = geo.Open())
            {
                foreach (var item in group)
                {
                    ctx.BeginFigure(item.Poly[0], true, true);
                    ctx.PolyLineTo(item.Poly.Skip(1).ToList(), true, false);
                }
            }

            geo.Freeze();
            WM.Color color = group.Key.Color;
            if (!brushes.TryGetValue(color, out WM.SolidColorBrush? brush))
            {
                brush = new WM.SolidColorBrush(color);
                brush.Freeze();
                brushes[color] = brush;
            }

            _plan.Children.Add(new WS.Path { Data = geo, Fill = brush, Stroke = brush, StrokeThickness = 0.5, IsHitTestVisible = false });
        }

        // Linhas das secoes.
        WM.Brush sectionBrush = ToolTheme.Brush(255, 255, 255, 70);
        foreach (ModelSection s in model.Sections)
        {
            List<Point3d> pts = s.Subassemblies.SelectMany(a => a.Links.SelectMany(l => l.Points)).ToList();
            if (pts.Count < 2)
                continue;
            Point3d p0 = pts[0];
            Point3d far = pts.OrderByDescending(p => p.DistanceTo(p0)).First();
            Point3d near = pts.OrderByDescending(p => p.DistanceTo(far)).First();
            SW.Point a = ToScreen(near.X, near.Y), b = ToScreen(far.X, far.Y);
            _plan.Children.Add(new WS.Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = sectionBrush, StrokeThickness = 0.6, IsHitTestVisible = false });
        }

        // Baseline e marcos S0..S3.
        if (model.Baseline.Count > 1)
        {
            var bl = new WS.Polyline { Stroke = ToolTheme.Brush(220, 80, 80), StrokeThickness = 1, StrokeDashArray = new WM.DoubleCollection { 6, 3 }, IsHitTestVisible = false };
            foreach (Point3d p in model.Baseline)
                bl.Points.Add(ToScreen(p.X, p.Y));
            _plan.Children.Add(bl);
        }

        for (int i = 0; i < model.Marks.Length; i++)
        {
            Point3d m = model.Marks[i];
            if (m == Point3d.Origin)
                continue;
            SW.Point s = ToScreen(m.X, m.Y);
            var dot = new WS.Ellipse { Width = 7, Height = 7, Fill = ToolTheme.Brush(255, 190, 30), IsHitTestVisible = false };
            WC.Canvas.SetLeft(dot, s.X - 3.5);
            WC.Canvas.SetTop(dot, s.Y - 3.5);
            _plan.Children.Add(dot);
            if (Show("MARCOS"))
                PlanLabel(model.MarkNames.Length > i ? model.MarkNames[i] : "", s.X + 5, s.Y - 16, 11, ToolTheme.Brush(255, 205, 80));
        }

        // Legenda.
        double ly = 8;
        foreach ((string text, WM.Color color) in !Show("LEGENDA") ? Array.Empty<(string, WM.Color)>() : new[]
                 {
                     ("REBAIXO", RebaixoStyle.Of(new[] { "REBAIXO" }).Color),
                     ("Meio-fio", RebaixoStyle.Of(new[] { "Curb", "Top" }).Color),
                     ("Sarjeta", RebaixoStyle.Of(new[] { "Sarjeta", "Top" }).Color),
                     ("Calcada", RebaixoStyle.Of(new[] { "Calcada" }).Color),
                     ("Pista", RebaixoStyle.Of(new[] { "Top" }).Color)
                 })
        {
            var sw = new WS.Rectangle { Width = 12, Height = 10, Fill = new WM.SolidColorBrush(color), Stroke = ToolTheme.GraphicLine, StrokeThickness = 0.5, IsHitTestVisible = false };
            WC.Canvas.SetLeft(sw, 8);
            WC.Canvas.SetTop(sw, ly + 2);
            _plan.Children.Add(sw);
            PlanLabel(text, 24, ly, 11);
            ly += 15;
        }

        // Escala grafica.
        if (!Show("ESCALA"))
            return;
        double step = NiceStep(Math.Max(w, h) / _scale / 8.0);
        double len = step * _scale;
        var bar = new WS.Line { X1 = w - len - 14, Y1 = h - 12, X2 = w - 14, Y2 = h - 12, Stroke = ToolTheme.GraphicText, StrokeThickness = 2, IsHitTestVisible = false };
        _plan.Children.Add(bar);
        PlanLabel($"{step:0.##} m", w - len - 14, h - 28, 10);
    }

    private static double NiceStep(double raw)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
        return new[] { 1.0, 2.0, 5.0, 10.0 }.Select(k => k * mag).First(v => v >= raw);
    }

    private void PlanLabel(string text, double x, double y, double size, WM.Brush? brush = null)
    {
        var t = new WC.TextBlock { Text = text, Foreground = brush ?? ToolTheme.GraphicText, FontSize = size, IsHitTestVisible = false };
        WC.Canvas.SetLeft(t, x);
        WC.Canvas.SetTop(t, y);
        _plan.Children.Add(t);
    }

    private void OnPlanMove(object sender, WI.MouseEventArgs e)
    {
        if (_scale <= 0)
            return;
        SW.Point p = e.GetPosition(_plan);
        (double x, double y) = ToWorld(p);
        _coords.Text = !Show("CURSOR") ? "" : Mode == "GERAL" ? UnderCursorOverview(p) : UnderCursorDetail(x, y);
        if (_dragFrom is SW.Point f && (e.LeftButton == WI.MouseButtonState.Pressed || e.MiddleButton == WI.MouseButtonState.Pressed))
        {
            _panX -= (p.X - f.X) / _scale;
            _panY += (p.Y - f.Y) / _scale;
            _dragFrom = p;
            RenderPlan();
        }
    }

    /// <summary>O que esta sob o cursor na planta geral: rebaixo (nome) ou corredor/baseline.</summary>
    private string UnderCursorOverview(SW.Point p)
    {
        double best = 14;
        RebaixoInfo? hit = null;
        foreach (RebaixoInfo r in _items)
        {
            foreach (Point3d q in r.Path)
            {
                double d = (ToScreen(q.X, q.Y) - p).Length;
                if (d < best)
                {
                    best = d;
                    hit = r;
                }
            }
        }

        if (hit is not null)
            return $"{hit.Label}  -  {hit.CorridorName} / {hit.BaselineName}" + (Show("ESTACAS") ? $"  -  estacas {hit.Stations}" : "") + $"  -  lado {hit.SideLabel.ToLowerInvariant()}";

        best = 10;
        string? name = null;
        foreach ((AcadDb.ObjectId _, string n, List<Point3d> pts) in _overview)
        {
            for (int k = 1; k < pts.Count; k++)
            {
                double d = SegDist(p, ToScreen(pts[k - 1].X, pts[k - 1].Y), ToScreen(pts[k].X, pts[k].Y));
                if (d < best)
                {
                    best = d;
                    name = n;
                }
            }
        }

        return name ?? "";
    }

    /// <summary>Na planta do rebaixo: estaca da secao mais perto do cursor e o trecho.</summary>
    private string UnderCursorDetail(double x, double y)
    {
        RebaixoInfo? r = _current;
        if (r is null || _model is null)
            return "";
        double best = double.MaxValue, station = 0;
        foreach (ModelSection s in _model.Sections)
        {
            foreach ((long _, List<ModelLink> links) in s.Subassemblies)
            {
                foreach (ModelLink l in links)
                {
                    foreach (Point3d q in l.Points)
                    {
                        double d = ((q.X - x) * (q.X - x)) + ((q.Y - y) * (q.Y - y));
                        if (d < best)
                        {
                            best = d;
                            station = s.Station;
                        }
                    }
                }
            }
        }

        if (best == double.MaxValue)
            return "";
        string where = station < r.S0 - 1e-4 ? "antes do rebaixo"
            : station < r.S1 - 1e-4 ? "rampa de entrada"
            : station <= r.S2 + 1e-4 ? "trecho rebaixado"
            : station <= r.S3 + 1e-4 ? "rampa de saida"
            : "depois do rebaixo";
        return $"{r.Label}  -  secao {N(station, "0.00")}  -  {where}";
    }

    private static double SegDist(SW.Point p, SW.Point a, SW.Point b)
    {
        SW.Vector ab = b - a;
        double t = ab.LengthSquared < 1e-9 ? 0 : Math.Clamp(((p - a) * ab) / ab.LengthSquared, 0, 1);
        return (p - (a + (ab * t))).Length;
    }

    private void OnPlanWheel(object sender, WI.MouseWheelEventArgs e)
    {
        if (_scale <= 0)
            return;
        SW.Point p = e.GetPosition(_plan);
        (double bx, double by) = ToWorld(p);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25 : 0.8), 0.2, 400);
        RenderPlan();
        (double ax, double ay) = ToWorld(p);
        _panX += bx - ax;
        _panY += by - ay;
        RenderPlan();
    }

    // ------------------------------------------------------------------
    // Abas de detalhe
    // ------------------------------------------------------------------

    private void BuildTabs()
    {
        var tabs = new List<(string Key, Func<SW.UIElement> Build)> { ("Propriedades", BuildProperties) };
        if (_category is "REBAIXOS" or "AVISOS")
        {
            tabs.Add(("Secoes", BuildSections));
            tabs.Add(("Avisos", BuildWarnings));
        }

        if (!tabs.Any(t => t.Key == _tab))
            _tab = tabs[0].Key;

        _tabStrip.Children.Clear();
        foreach ((string key, Func<SW.UIElement> build) in tabs)
        {
            bool active = key == _tab;
            WC.Button tab = new()
            {
                Content = key,
                Padding = new SW.Thickness(10, 3, 10, 3),
                Background = active ? T.Pane : T.Back,
                Foreground = active ? T.Text : T.Muted,
                BorderBrush = active ? T.Accent : T.Line,
                BorderThickness = new SW.Thickness(0, active ? 2 : 0, 0, 0),
                FontWeight = active ? SW.FontWeights.Bold : SW.FontWeights.Normal
            };
            string tabKey = key;
            tab.Click += (_, _) =>
            {
                _tab = tabKey;
                BuildTabs();
            };
            _tabStrip.Children.Add(tab);
            if (active)
                _tabContent.Content = new WC.ScrollViewer { Content = build(), VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
        }
    }

    private WC.StackPanel Form() => new() { Margin = new SW.Thickness(12, 10, 12, 10) };

    private WC.TextBlock Label(string t, bool bold = false) => new()
    {
        Text = t,
        Foreground = bold ? T.Text : T.Muted,
        FontWeight = bold ? SW.FontWeights.Bold : SW.FontWeights.Normal,
        Margin = new SW.Thickness(0, 6, 0, 2),
        TextWrapping = SW.TextWrapping.Wrap
    };

    private WC.TextBlock Prop(string name, string value)
    {
        var tb = new WC.TextBlock { Margin = new SW.Thickness(0, 2, 0, 2), TextWrapping = SW.TextWrapping.Wrap, Foreground = T.Text };
        tb.Inlines.Add(new SW.Documents.Run(name + ": ") { Foreground = T.Muted });
        tb.Inlines.Add(new SW.Documents.Run(value));
        return tb;
    }

    private WC.Grid EditRow(string key, string label, string value, string unit)
    {
        WC.Grid g = new() { Margin = new SW.Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(190) });
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(110) });
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
        g.Children.Add(new WC.TextBlock { Text = label, Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center });
        var box = new WC.TextBox { Text = value, VerticalContentAlignment = SW.VerticalAlignment.Center };
        WC.Grid.SetColumn(box, 1);
        g.Children.Add(box);
        var u = new WC.TextBlock { Text = unit, Foreground = T.Muted, Margin = new SW.Thickness(6, 0, 0, 0), VerticalAlignment = SW.VerticalAlignment.Center };
        WC.Grid.SetColumn(u, 2);
        g.Children.Add(u);
        _fields[key] = box;
        return g;
    }

    private double? Number(string key)
    {
        if (!_fields.TryGetValue(key, out WC.TextBox? box))
            return null;
        string t = box.Text.Trim().Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
    }

    private WC.Button ActionButton(string text, Action action, bool primary = false)
    {
        var b = new WC.Button
        {
            Content = text,
            MinWidth = 110,
            Padding = new SW.Thickness(12, 4, 12, 4),
            Margin = new SW.Thickness(0, 10, 6, 0),
            HorizontalAlignment = SW.HorizontalAlignment.Left,
            FontWeight = primary ? SW.FontWeights.Bold : SW.FontWeights.Normal
        };
        b.Click += (_, _) => action();
        return b;
    }

    private SW.UIElement BuildProperties()
    {
        _fields.Clear();
        WC.StackPanel p = Form();

        if (_category == "CORREDORES")
        {
            CorridorSummary? c = _currentCorridor;
            if (c is null)
            {
                p.Children.Add(Label("Selecione um corredor na lista.", true));
                return p;
            }

            p.Children.Add(Label(c.Name, true));
            p.Children.Add(Prop("Baselines", c.Baselines.ToString()));
            p.Children.Add(Prop("Regions", c.Regions.ToString()));
            p.Children.Add(Prop("Regions com meio-fio (MF/MFSARJETA)", c.MfRegions.ToString()));
            p.Children.Add(Prop("Rebaixos", c.Rebaixos.ToString()));
            WC.WrapPanel acts = new();
            acts.Children.Add(ActionButton("Novo rebaixo", () => Send(C3DCommands.Corridor.MfRebaixo + " ", $"Novo rebaixo: selecione o corredor {c.Name}."), true));
            acts.Children.Add(ActionButton("Selecionar", () => SelectCorridor(c.Id)));
            acts.Children.Add(ActionButton("Rebuild", () => Rebuild(c.Id)));
            acts.Children.Add(ActionButton("Ver rebaixos", () =>
            {
                _find.Text = c.Name;
                SelectCategory("REBAIXOS");
            }));
            p.Children.Add(acts);
            return p;
        }

        if (_category == "CONFIG")
            return BuildConfig();

        RebaixoInfo? r = _current;
        if (r is null)
        {
            p.Children.Add(Label(_items.Count == 0
                ? "Nenhum rebaixo no desenho. Use Novo rebaixo (MFREBAIXO): selecione o corredor e clique o inicio e o fim do trecho rebaixado."
                : "Selecione um rebaixo na lista.", true));
            return p;
        }

        p.Children.Add(Label($"Rebaixo {r.Label}", true));
        WC.Grid nameRow = EditRow("nome", "Nome", r.CustomName, "");
        nameRow.ColumnDefinitions[1].Width = new SW.GridLength(220);
        WC.Button saveName = new() { Content = "Salvar nome", Padding = new SW.Thickness(8, 1, 8, 1), Margin = new SW.Thickness(8, 0, 0, 0) };
        saveName.Click += (_, _) => RenameCurrent(r);
        WC.Grid.SetColumn(saveName, 2);
        nameRow.Children.Add(saveName);
        p.Children.Add(nameRow);
        p.Children.Add(Prop("Codigo / estacas", $"{r.Code} / {r.Stations}"));
        p.Children.Add(Prop("Corredor / baseline / region", $"{r.CorridorName} / {r.BaselineName} / {r.RegionName}"));
        p.Children.Add(Prop("Lado / subassembly", $"{r.SideLabel} / {r.SubassemblyName}"));
        p.Children.Add(Prop("Rampa de entrada", $"{N(r.S0)} -> {N(r.S1)}  ({N(r.EntryLength, "0.00")} m)"));
        p.Children.Add(Prop("Trecho rebaixado", $"{N(r.S1)} -> {N(r.S2)}  ({N(r.LoweredLength, "0.00")} m)"));
        p.Children.Add(Prop("Rampa de saida", $"{N(r.S2)} -> {N(r.S3)}  ({N(r.ExitLength, "0.00")} m)"));
        p.Children.Add(Prop("Altura livre normal / final", $"{Cm(r.NormalHeight)} / {Cm(r.FinalHeight)}"));
        p.Children.Add(Prop("Transition", r.ByDepth
            ? $"REB (profundidade): {Cm(r.ValueIn)} na entrada, {Cm(r.ValueOut)} na saida"
            : $"AMF_LIVRE (altura livre): {Cm(r.ValueStart)} -> {Cm(r.ValueIn)} -> {Cm(r.ValueEnd)}"));
        p.Children.Add(Prop("Destaque (code REBAIXO)", r.HighlightSetName ?? (r.HasHighlightParameter ? "falta o Transition" : "subassembly sem DESTAQUE")));
        p.Children.Add(Prop("Declividade da rampa do MF", Pct(r.RampSlope)));
        if (r.RampOk.HasValue || r.FreeWidth.HasValue)
            p.Children.Add(Prop("Calcada", $"rampa {(r.RampOk is double ok && ok > 0.5 ? "ok" : "nao cabe")}, faixa livre {N(r.FreeWidth, "0.00")} m"));

        p.Children.Add(Label("Editar e reaplicar", true));
        p.Children.Add(EditRow("s1", "Inicio do trecho rebaixado (S1)", N(r.S1), "m"));
        p.Children.Add(EditRow("s2", "Fim do trecho rebaixado (S2)", N(r.S2), "m"));
        p.Children.Add(EditRow("h", "Altura livre final", N(r.FinalHeight ?? 0, "0.000"), "m"));
        p.Children.Add(Label("As rampas (S0 e S3) saem de novo da altura normal das secoes vizinhas e da declividade iRampa. O rebaixo antigo e removido e o novo e aplicado; se der erro, o antigo volta."));

        WC.WrapPanel actions = new();
        actions.Children.Add(ActionButton("Aplicar", () => ApplyEdit(r), true));
        actions.Children.Add(ActionButton("Zoom no desenho", ZoomCurrent));
        actions.Children.Add(ActionButton("Selecionar corredor", SelectCorridor));
        actions.Children.Add(ActionButton("Remover", RemoveCurrent));
        p.Children.Add(actions);
        return p;
    }

    private SW.UIElement BuildConfig()
    {
        WC.StackPanel p = Form();
        p.Children.Add(Label("Painel", true));
        p.Children.Add(EditRow("margem", "Trecho antes/depois do rebaixo", N(_settings.PainelMargem, "0.##"), "m"));
        p.Children.Add(Label("Valores padrao do MFREBAIXO", true));
        p.Children.Add(EditRow("altura", "Altura livre final", N(_settings.UltimaAlturaFinal, "0.000"), "m"));
        p.Children.Add(EditRow("reducao", "Reducao", N(_settings.UltimaReducao, "0.000"), "m"));
        p.Children.Add(ActionButton("Salvar", () =>
        {
            if (Number("margem") is double m && m >= 0)
                _settings.PainelMargem = Math.Min(m, 100);
            if (Number("altura") is double h && h >= 0)
                _settings.UltimaAlturaFinal = h;
            if (Number("reducao") is double d && d >= 0)
                _settings.UltimaReducao = d;
            SaveSettings();
            _modelKey = null;
            BuildViewToggles();
            FillList();
            RenderGraphic();
            Say("Configuracao salva.");
        }, true));
        p.Children.Add(Label("O tema (claro/escuro) segue as Preferencias do painel viario (C3DVIARIOPAINEL)."));
        return p;
    }

    private SW.UIElement BuildSections()
    {
        RebaixoInfo? r = _current;
        if (r is null || Doc is not Document doc)
            return Label("Selecione um rebaixo.", true);

        List<RebaixoSection> sections;
        try
        {
            using (doc.LockDocument())
                sections = MFRebaixoData.Sections(doc.Database, r, 1.0);
        }
        catch (System.Exception ex)
        {
            return Label("Erro ao ler as secoes: " + ex.Message, true);
        }

        var lv = new WC.ListView { BorderThickness = new SW.Thickness(0), Background = T.Pane, Foreground = T.Text, Margin = new SW.Thickness(4) };
        string[] headers = { "Estaca", "Posicao", "REB", "DESTAQUE", "Exposto", "AMF_LIVRE", "Rampa OK", "Faixa livre" };
        double[] widths = { 80, 130, 60, 75, 65, 75, 65, 75 };
        var view = new WC.GridView();
        for (int i = 0; i < headers.Length; i++)
            view.Columns.Add(new WC.GridViewColumn { Header = headers[i], Width = widths[i], DisplayMemberBinding = new SW.Data.Binding($"C{i}") });
        lv.View = view;
        foreach (RebaixoSection s in sections)
        {
            var row = new Row
            {
                Data = s,
                Cells = new[]
                {
                    N(s.Station), s.Mark, N(s.Reb, "0.000"), N(s.Highlight, "0.###"), N(s.Exposed, "0.000"), N(s.FreeHeight, "0.000"),
                    N(s.RampOk, "0"), N(s.FreeWidth, "0.00")
                }
            };
            lv.Items.Add(new WC.ListViewItem { Content = new RowView(row, IconOf("secoes")), Tag = row });
        }

        WC.StackPanel p = new();
        p.Children.Add(new WC.TextBlock
        {
            Text = $"{sections.Count} secao(oes) do corredor de {N(r.S0 - 1)} a {N(r.S3 + 1)}. DESTAQUE em [1,4) da o code REBAIXO; REB e a profundidade do rebaixo.",
            Foreground = T.Muted,
            TextWrapping = SW.TextWrapping.Wrap,
            Margin = new SW.Thickness(8, 6, 8, 2)
        });
        p.Children.Add(lv);
        return p;
    }

    private SW.UIElement BuildWarnings()
    {
        WC.StackPanel p = Form();
        RebaixoInfo? r = _current;
        if (r is null)
        {
            p.Children.Add(Label("Selecione um rebaixo.", true));
            return p;
        }

        List<RebaixoWarning> mine = _warnings.Where(w => ReferenceEquals(w.Item, r)).ToList();
        p.Children.Add(Label(mine.Count == 0 ? "Nenhum aviso para este rebaixo." : $"{mine.Count} aviso(s)", true));
        foreach (RebaixoWarning w in mine)
        {
            var tb = new WC.TextBlock { TextWrapping = SW.TextWrapping.Wrap, Margin = new SW.Thickness(0, 4, 0, 4), Foreground = T.Text };
            tb.Inlines.Add(new SW.Documents.Run(w.Serious ? "! " : "i ") { Foreground = w.Serious ? ToolTheme.Brush(230, 150, 30) : T.Accent, FontWeight = SW.FontWeights.Bold });
            tb.Inlines.Add(new SW.Documents.Run(w.Text));
            tb.Inlines.Add(new SW.Documents.LineBreak());
            tb.Inlines.Add(new SW.Documents.Run(w.Reference) { Foreground = T.Muted, FontSize = 11 });
            p.Children.Add(tb);
        }

        p.Children.Add(Label("Os avisos da NBR 9050 sao recomendacoes para rebaixo de travessia de pedestres; confira na norma vigente. Rebaixo de garagem nao precisa zerar a altura."));
        return p;
    }

    // ------------------------------------------------------------------
    // Acoes
    // ------------------------------------------------------------------

    private void Edit(string done, Action<AcadDb.Database, Editor> action)
    {
        if (Doc is not Document doc)
        {
            Say("Nenhum desenho aberto.");
            return;
        }

        try
        {
            using (doc.LockDocument())
                action(doc.Database, doc.Editor);
            doc.Editor.UpdateScreen();
            AcApp.UpdateScreen();
            _modelKey = null;
            Refresh();
            Say(done);
        }
        catch (System.Exception ex)
        {
            _modelKey = null;
            Refresh();
            Say("Erro: " + ex.Message);
            doc.Editor.WriteMessage($"\n[{C3DCommands.Corridor.MfRebaixoPainel}] {ex.Message}\n");
        }
    }

    private void RenameCurrent(RebaixoInfo r)
    {
        string name = _fields.TryGetValue("nome", out WC.TextBox? box) ? box.Text.Trim() : "";
        _pending = (r.CorridorId, r.BaselineKey, r.S1);
        Edit(name.Length > 0 ? $"{r.Code} agora se chama \"{name}\"." : $"{r.Code} ficou sem nome.", (db, _) => MFRebaixoData.Rename(db, r, name));
    }

    private void ApplyEdit(RebaixoInfo r)
    {
        double? s1 = Number("s1"), s2 = Number("s2"), h = Number("h");
        if (s1 is null || s2 is null || h is null)
        {
            Say("Valores invalidos (use ponto ou virgula).");
            return;
        }

        if (s2 <= s1 + 0.01)
        {
            Say("O fim do trecho rebaixado tem que vir depois do inicio.");
            return;
        }

        if (h < 0)
        {
            Say("A altura final nao pode ser negativa.");
            return;
        }

        if (r.FinalHeight is not double oldH)
        {
            Say("Nao deu para ler a altura final atual deste rebaixo (Rebuild no corredor e Atualizar).");
            return;
        }

        _pending = (r.CorridorId, r.BaselineKey, s1.Value);
        Edit($"Rebaixo reaplicado: {N(s1.Value)} - {N(s2.Value)}, altura final {Cm(h)}.", (db, ed) =>
        {
            MFRebaixoData.Remove(db, r, rebuild: false);
            try
            {
                MFRebaixoCommands.ApplyFromPanel(db, ed, r.CorridorId, r.BaselineKey, r.RegionName, r.Left, s1.Value, s2.Value, h.Value, MFRebaixoData.CommentFor(r.CustomName));
            }
            catch (System.Exception ex)
            {
                // Volta o rebaixo antigo.
                try
                {
                    MFRebaixoCommands.ApplyFromPanel(db, ed, r.CorridorId, r.BaselineKey, r.RegionName, r.Left, r.S1, r.S2, oldH, MFRebaixoData.CommentFor(r.CustomName));
                }
                catch
                {
                    throw new InvalidOperationException(ex.Message + " (o rebaixo antigo tambem nao voltou; use Novo rebaixo)");
                }

                throw new InvalidOperationException(ex.Message + " (o rebaixo antigo foi mantido)");
            }
        });
    }

    private void RemoveCurrent()
    {
        RebaixoInfo? r = _current;
        if (r is null)
        {
            Say("Selecione um rebaixo.");
            return;
        }

        SW.MessageBoxResult answer = SW.MessageBox.Show(this,
            $"Remover o rebaixo {r.Label} de {r.CorridorName}?\n\nSaem os Transition Sets ({r.SetName}{(r.HighlightSetName is null ? "" : " e " + r.HighlightSetName)}) e as secoes que o comando criou.",
            "Remover rebaixo", SW.MessageBoxButton.YesNo, SW.MessageBoxImage.Warning);
        if (answer != SW.MessageBoxResult.Yes)
            return;

        string summary = "";
        Edit($"Rebaixo {r.Label} removido.", (db, _) => summary = MFRebaixoData.Remove(db, r));
        if (summary.Length > 0)
            Say($"Rebaixo {r.Label} removido: {summary}.");
    }

    private void RebuildCurrent()
    {
        AcadDb.ObjectId? id = _category == "CORREDORES" ? _currentCorridor?.Id : _current?.CorridorId;
        if (id is null)
        {
            Say("Selecione um rebaixo ou corredor.");
            return;
        }

        Rebuild(id.Value);
    }

    private void Rebuild(AcadDb.ObjectId id) => Edit("Corredor reconstruido.", (db, _) => MFRebaixoData.Rebuild(db, id));

    private void SelectCorridor()
    {
        AcadDb.ObjectId? id = _category == "CORREDORES" ? _currentCorridor?.Id : _current?.CorridorId;
        if (id is null)
        {
            Say("Selecione um rebaixo ou corredor.");
            return;
        }

        SelectCorridor(id.Value);
    }

    private void SelectCorridor(AcadDb.ObjectId id)
    {
        if (Doc is not Document doc)
            return;
        try
        {
            using (doc.LockDocument())
                doc.Editor.SetImpliedSelection(new[] { id });
            Say("Corredor selecionado no desenho.");
        }
        catch (System.Exception ex)
        {
            Say("Nao deu para selecionar: " + ex.Message);
        }
    }

    private void ZoomCurrent()
    {
        if (_category == "CORREDORES" || _category == "CONFIG")
            return;
        RebaixoInfo? r = _current;
        if (r is null || Doc is not Document doc)
            return;

        Point3d[] pts = new[] { r.P0, r.P1, r.P2, r.P3 }.Where(p => p != Point3d.Origin).ToArray();
        if (pts.Length == 0)
        {
            Say("Sem coordenadas para o zoom.");
            return;
        }

        double pad = Math.Max(6, r.TotalLength * 0.25);
        double minX = pts.Min(p => p.X) - pad, maxX = pts.Max(p => p.X) + pad, minY = pts.Min(p => p.Y) - pad, maxY = pts.Max(p => p.Y) + pad;

        try
        {
            using (doc.LockDocument())
            {
                using AcadDb.ViewTableRecord view = doc.Editor.GetCurrentView();
                Matrix3d dcsToWcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                                    Matrix3d.Displacement(view.Target - Point3d.Origin) *
                                    Matrix3d.PlaneToWorld(view.ViewDirection);
                Matrix3d wcsToDcs = dcsToWcs.Inverse();
                Point3d a = new Point3d(minX, minY, 0).TransformBy(wcsToDcs);
                Point3d b = new Point3d(maxX, maxY, 0).TransformBy(wcsToDcs);
                double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
                double ratio = view.Width / Math.Max(view.Height, 1e-9);
                double width = Math.Max(x1 - x0, 5.0), height = Math.Max(y1 - y0, 5.0);
                if (width / height > ratio)
                    height = width / ratio;
                else
                    width = height * ratio;
                view.CenterPoint = new Point2d((x0 + x1) / 2.0, (y0 + y1) / 2.0);
                view.Width = width * 1.1;
                view.Height = height * 1.1;
                doc.Editor.SetCurrentView(view);
                AcApp.UpdateScreen();
            }

            Say($"Zoom no rebaixo {r.Label}.");
        }
        catch (System.Exception ex)
        {
            Say("Zoom falhou: " + ex.Message);
        }
    }
}
