using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Civil3D2026Plugin.UI.Common;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WS = System.Windows.Shapes;

namespace Civil3D2026Plugin.Modules.Secao;

/// <summary>
/// Visualizador de secao (C3DSECAO), no molde do explorador do viario: categorias,
/// barra de comandos com icones, vista grafica escura (a secao, com reguas de
/// distancia e cota, zoom e pan), lista em colunas e abas de detalhe. Janela
/// flutuante; pode ficar em outro monitor enquanto o desenho segue livre.
/// </summary>
internal sealed class SecaoPanel : SW.Window
{
    private static SecaoPanel? _instance;
    private static readonly HashSet<AcadDb.ObjectId> Pending = new();
    private static bool _idleHooked;

    private readonly ToolTheme T = ToolTheme.Current;
    private static readonly WM.FontFamily UiFont = new("Tahoma");
    private static readonly WM.FontFamily MonoFont = new("Consolas");

    /// <summary>Secao mostrada agora (usada por C3DSECAODESENHAR).</summary>
    public static SectionData? CurrentData => _instance?._data;

    // ------------------------------------------------------------------
    // Dados
    // ------------------------------------------------------------------

    private List<(AcadDb.ObjectId Id, CutBox Box)> _cuts = new();
    private List<SecaoReader.CorridorCache> _corridors = new();
    private bool _corridorsLoaded;
    private SecaoStore.Prefs _prefs = SecaoStore.LoadPrefs();
    private SectionData? _data;

    private string _category = "CORTES";
    private string _tab = "Propriedades";
    private AcadDb.ObjectId _cutId;
    private (SecaoReader.CorridorCache Corridor, SecaoReader.RegionCache Region)? _region;
    private int _stationIndex;
    private bool _stationMode;
    private SectionItem? _selectedItem;
    private bool _refreshQueued;

    // Vista: escala (px por metro em S) e origem; exagero vertical vem da caixa.
    private double _scale = 1;
    private double _ox, _oy;
    private bool _fitPending = true;
    private SW.Point _dragStart;
    private (double ox, double oy) _dragOrigin;
    private bool _dragging;

    // ------------------------------------------------------------------
    // Controles
    // ------------------------------------------------------------------

    private WC.WrapPanel _categoryBar = new();
    private WC.StackPanel _commandBar = new();
    private WC.Canvas _graphic = new();
    private WC.TextBlock _graphicInfo = new();
    private WC.StackPanel _stationBar = new();
    private WC.Slider _stationSlider = new();
    private WC.TextBlock _stationText = new();
    private WC.ListView _list = new();
    private WC.TextBox _find = new();
    private WC.StackPanel _tabStrip = new();
    private WC.ContentControl _tabContent = new();
    private WC.TextBlock _status = new();
    private readonly Dictionary<string, WM.ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------
    // Categorias e comandos
    // ------------------------------------------------------------------

    private static readonly (string Id, string Icon, string Title)[] Categories =
    {
        ("CORTES", "corte", "Cortes"),
        ("CORREDOR", "corredor", "Corredor por estaca"),
        ("CONTEUDO", "camadas", "Conteudo da secao"),
        ("CONFIG", "config", "Configuracao")
    };

    private sealed record Cmd(string Key, string Icon, string Title, string Group, string Description, string[] Steps, Action<SecaoPanel> Run);

    private static readonly Cmd[] Commands =
    {
        new("NOVO", "corte", "Nova caixa de corte", "Corte",
            "Linha de corte por pontos (ou polilinha existente) e profundidades; a caixa fica numa polilinha na layer SECAO-CORTE e a janela acompanha os grips.",
            new[] { "Clique os pontos da linha (Enter termina).", "Profundidade a frente e atras.", "Cotas e exagero na aba Propriedades." },
            p => p.Send("C3DSECAOCORTE ", "Clique os pontos da linha de corte.")),
        new("SELECIONAR", "abrir", "Usar polilinha existente", "Corte",
            "Transforma uma polilinha do desenho em caixa de corte.", new[] { "Selecione a polilinha." },
            p => p.Send("C3DSECAOCORTE S ", "Selecione a polilinha que sera a linha de corte.")),
        new("INVERTER", "inverter", "Inverter sentido do corte", "Corte",
            "Inverte a linha (o que estava a esquerda da tela passa para a direita) e troca frente/atras.", new[] { "Com um corte selecionado." },
            p => p.InvertCut()),
        new("EXCLUIR", "excluir", "Excluir caixa de corte", "Corte",
            "Apaga a polilinha de corte selecionada.", new[] { "Com um corte selecionado (pede confirmacao)." }, p => p.DeleteCut()),
        new("ZOOM", "zoom", "Enquadrar", "Vista", "Enquadra a secao inteira na vista.", new[] { "Duplo clique na vista faz o mesmo." }, p => { p._fitPending = true; p.RenderGraphic(); }),
        new("EXAGERO", "escala", "Exagero vertical", "Vista", "Escolhe o exagero vertical (1x, 2x, 5x, 10x...).", new[] { "Abre um menu para clicar." }, p => p.OpenExageroMenu()),
        new("ANTERIOR", "esquerda", "Estaca anterior", "Corredor", "Na categoria Corredor por estaca: volta uma estaca.", new[] { "Selecione uma regiao do corredor." }, p => p.StepStation(-1)),
        new("PROXIMA", "direita", "Proxima estaca", "Corredor", "Na categoria Corredor por estaca: avanca uma estaca.", new[] { "Selecione uma regiao do corredor." }, p => p.StepStation(+1)),
        new("DESENHAR", "polilinha", "Desenhar secao no model", "Saidas",
            "Leva a secao mostrada para o desenho como polilinhas 2D (moldura, cotas, distancias), no ponto clicado.",
            new[] { "Clique o canto inferior esquerdo.", "Confirme o exagero." }, p => p.Send("C3DSECAODESENHAR ", "Clique onde a secao deve ser desenhada.")),
        new("ATUALIZAR", "atualizar", "Atualizar", "Geral", "Le o desenho de novo (cortes, corredores e conteudo).", new[] { "Sem perguntas." }, p => { p._corridorsLoaded = false; p.Refresh(); })
    };

    // ------------------------------------------------------------------
    // Abrir / atualizar
    // ------------------------------------------------------------------

    public static void ShowPanel()
    {
        if (_instance is null)
        {
            _instance = new SecaoPanel();
            _instance.Closed += (_, _) =>
            {
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

        _instance.Refresh();
    }

    /// <summary>Mostra a caixa de corte dada (depois de C3DSECAOCORTE).</summary>
    public static void SelectCut(AcadDb.ObjectId id)
    {
        if (_instance is null)
            return;
        _instance._cutId = id;
        _instance._stationMode = false;
        _instance._category = "CORTES";
        _instance._fitPending = true;
        _instance.Refresh();
    }

    /// <summary>Uma polilinha de corte mudou (grip, move, apagada): refaz quando o AutoCAD ficar livre.</summary>
    public static void NotifyChanged(AcadDb.ObjectId id)
    {
        if (_instance is null)
            return;
        lock (Pending)
        {
            Pending.Add(id);
            if (_idleHooked)
                return;
            _idleHooked = true;
        }

        AcApp.Idle += OnIdle;
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        AcApp.Idle -= OnIdle;
        HashSet<AcadDb.ObjectId> ids;
        lock (Pending)
        {
            _idleHooked = false;
            ids = new HashSet<AcadDb.ObjectId>(Pending);
            Pending.Clear();
        }

        SecaoPanel? panel = _instance;
        if (panel is null)
            return;
        try
        {
            panel.ReadCuts();
            if (!panel._stationMode && (ids.Contains(panel._cutId) || panel._cutId.IsNull || panel._cutId.IsErased))
                panel.Recompute();
            panel.FillCategories();
            panel.FillList();
            panel.BuildTabs();
        }
        catch (System.Exception ex)
        {
            panel.Say("Erro ao atualizar: " + ex.Message);
        }
    }

    private static void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
    {
        SecaoPanel? panel = _instance;
        if (panel is null || panel._refreshQueued)
            return;
        panel._refreshQueued = true;
        panel.Dispatcher.BeginInvoke(new Action(() =>
        {
            panel._refreshQueued = false;
            if (_instance == panel)
            {
                panel._corridorsLoaded = false;
                panel._cutId = AcadDb.ObjectId.Null;
                panel._region = null;
                panel.Refresh();
            }
        }), SW.Threading.DispatcherPriority.Background);
    }

    private SecaoPanel()
    {
        Title = "Secao - Visualizador";
        Width = 1100;
        Height = 760;
        MinWidth = 760;
        MinHeight = 520;
        ShowInTaskbar = false;
        WindowStartupLocation = SW.WindowStartupLocation.CenterScreen;
        FontFamily = UiFont;
        FontSize = 12;
        Content = BuildUi();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == WI.Key.Escape)
                Close();
            else if (e.Key == WI.Key.Left && _stationMode) { StepStation(-1); e.Handled = true; }
            else if (e.Key == WI.Key.Right && _stationMode) { StepStation(+1); e.Handled = true; }
        };
    }

    private Document? Doc => AcApp.DocumentManager.MdiActiveDocument;

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
        _graphic = new WC.Canvas { Background = ToolTheme.GraphicBack, ClipToBounds = true };
        _graphicInfo = new WC.TextBlock { Foreground = T.Muted, FontFamily = MonoFont, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(8, 0, 8, 0) };
        _list = new WC.ListView { BorderBrush = T.Line, Background = T.Pane, Foreground = T.Text };
        _find = new WC.TextBox { Width = 160, Background = T.Input, Foreground = T.Text, BorderBrush = T.Line, CaretBrush = T.Text, VerticalContentAlignment = SW.VerticalAlignment.Center };
        _tabStrip = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        _tabContent = new WC.ContentControl();
        _status = new WC.TextBlock { Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(6, 0, 6, 0) };

        WC.Grid root = new();
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1.3, SW.GridUnitType.Star), MinHeight = 160 });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(5) });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1, SW.GridUnitType.Star), MinHeight = 140 });
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

        WC.Border graphic = new() { BorderBrush = T.Line, BorderThickness = new SW.Thickness(1), Child = _graphic, Margin = new SW.Thickness(4, 0, 4, 0) };
        _graphic.SizeChanged += (_, _) => RenderGraphic();
        _graphic.MouseWheel += OnWheel;
        _graphic.MouseDown += OnMouseDown;
        _graphic.MouseMove += OnMouseMove;
        _graphic.MouseUp += (_, _) => { _dragging = false; _graphic.ReleaseMouseCapture(); };
        _graphic.MouseLeave += (_, _) => { _dragging = false; };
        WC.Grid.SetRow(graphic, 2);
        root.Children.Add(graphic);

        WC.DockPanel graphicBar = new() { Margin = new SW.Thickness(4, 0, 4, 0), Background = T.Hover, Height = 26 };
        _stationBar = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, Visibility = SW.Visibility.Collapsed };
        _stationBar.Children.Add(Link("<", () => StepStation(-1), "Estaca anterior (seta esquerda)"));
        _stationSlider = new WC.Slider { Width = 220, Minimum = 0, Maximum = 1, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(6, 0, 6, 0), IsSnapToTickEnabled = true, TickFrequency = 1 };
        _stationSlider.ValueChanged += (_, e) =>
        {
            int i = (int)Math.Round(e.NewValue);
            if (_stationMode && i != _stationIndex)
            {
                _stationIndex = i;
                Recompute();
            }
        };
        _stationBar.Children.Add(_stationSlider);
        _stationBar.Children.Add(Link(">", () => StepStation(+1), "Proxima estaca (seta direita)"));
        _stationText = new WC.TextBlock { Foreground = T.Text, FontFamily = MonoFont, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(8, 0, 0, 0) };
        _stationBar.Children.Add(_stationText);
        WC.DockPanel.SetDock(_stationBar, WC.Dock.Left);
        graphicBar.Children.Add(_stationBar);
        graphicBar.Children.Add(_graphicInfo);
        WC.Grid.SetRow(graphicBar, 3);
        root.Children.Add(graphicBar);

        WC.GridSplitter splitH = new() { Height = 5, HorizontalAlignment = SW.HorizontalAlignment.Stretch, Background = T.Back };
        WC.Grid.SetRow(splitH, 4);
        root.Children.Add(splitH);

        WC.Grid bottom = new() { Margin = new SW.Thickness(4, 0, 4, 0) };
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star), MinWidth = 260 });
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(5) });
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star), MinWidth = 300 });
        _list.SelectionChanged += (_, _) => OnRowSelected();
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
        leftButtons.Children.Add(FooterButton("Nova caixa...", () => Send("C3DSECAOCORTE ", "Clique os pontos da linha de corte.")));
        leftButtons.Children.Add(FooterButton("Atualizar", () => { _corridorsLoaded = false; Refresh(); }));
        WC.DockPanel.SetDock(leftButtons, WC.Dock.Left);
        footer.Children.Add(leftButtons);
        footer.Children.Add(_status);
        WC.Grid.SetRow(footer, 6);
        root.Children.Add(footer);

        BuildCommandBar();
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

        // Menus com modelo proprio (o cromado padrao e claro e fixo).
        SW.Style menu = new(typeof(WC.ContextMenu));
        menu.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Pane));
        menu.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        menu.Setters.Add(new SW.Setter(WC.Control.BorderBrushProperty, T.Line));
        Resources[typeof(WC.ContextMenu)] = menu;
        SW.Style mi = new(typeof(WC.MenuItem));
        mi.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        mi.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Pane));
        Resources[typeof(WC.MenuItem)] = mi;
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

    private WC.TextBlock Link(string texto, Action acao, string dica)
    {
        WC.TextBlock t = new()
        {
            Text = texto,
            Foreground = T.Accent,
            FontSize = 12,
            FontWeight = SW.FontWeights.Bold,
            Cursor = WI.Cursors.Hand,
            ToolTip = dica,
            VerticalAlignment = SW.VerticalAlignment.Center,
            Margin = new SW.Thickness(8, 0, 0, 0)
        };
        t.MouseLeftButtonDown += (_, e) =>
        {
            acao();
            e.Handled = true;
        };
        return t;
    }

    private WM.ImageSource IconOf(string key)
    {
        if (!_icons.TryGetValue(key, out WM.ImageSource? img))
            _icons[key] = img = ToolIcons.Create(key, T.Text, T.Accent);
        return img;
    }

    private void Say(string text) => _status.Text = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "  " + text;

    private void Send(string command, string hint)
    {
        Document? doc = Doc;
        if (doc is null)
            return;
        Say(hint);
        try
        {
            Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
        }
        catch
        {
            // Sem foco: o comando roda igual.
        }

        doc.SendStringToExecute(command, true, false, true);
    }

    // ------------------------------------------------------------------
    // Dados
    // ------------------------------------------------------------------

    private void ReadCuts()
    {
        Document? doc = Doc;
        _cuts = doc is null ? new() : SecaoStore.ReadCuts(doc.Database);
        if (!_cutId.IsNull && !_cuts.Any(c => c.Id == _cutId))
            _cutId = AcadDb.ObjectId.Null;
        if (_cutId.IsNull && _cuts.Count > 0 && !_stationMode)
            _cutId = _cuts[0].Id;
    }

    private void EnsureCorridors()
    {
        if (_corridorsLoaded)
            return;
        Document? doc = Doc;
        _corridors = doc is null ? new() : SecaoReader.ReadCorridors(doc.Database);
        _corridorsLoaded = true;
        if (_region is not null)
        {
            SecaoReader.CorridorCache? c = _corridors.FirstOrDefault(x => x.Id == _region.Value.Corridor.Id);
            SecaoReader.RegionCache? r = c?.Regions.FirstOrDefault(x => x.Baseline == _region.Value.Region.Baseline && x.Region == _region.Value.Region.Region);
            _region = c is not null && r is not null ? (c, r) : null;
        }
    }

    private void Refresh()
    {
        Document? doc = Doc;
        if (doc is null)
        {
            Say("Nenhum desenho aberto.");
            return;
        }

        try
        {
            ReadCuts();
            if (_category == "CORREDOR" || _stationMode)
                EnsureCorridors();
            Recompute();
        }
        catch (System.Exception ex)
        {
            Say("Erro ao ler o desenho: " + ex.Message);
            return;
        }

        Title = $"Secao - Visualizador [{System.IO.Path.GetFileName(doc.Name)}]";
        FillCategories();
        FillList();
        BuildTabs();
    }

    /// <summary>Refaz a secao mostrada (corte selecionado ou estaca do corredor).</summary>
    private void Recompute()
    {
        Document? doc = Doc;
        if (doc is null)
            return;
        try
        {
            if (_stationMode && _region is (SecaoReader.CorridorCache c, SecaoReader.RegionCache r) && r.Assemblies.Count > 0)
            {
                _stationIndex = Math.Clamp(_stationIndex, 0, r.Assemblies.Count - 1);
                _data = SecaoReader.AtStation(doc.Database, c, r, _stationIndex, _prefs, _prefs.FrentePadrao, _prefs.AtrasPadrao, _corridors);
                _stationBar.Visibility = SW.Visibility.Visible;
                _stationSlider.Maximum = r.Assemblies.Count - 1;
                if ((int)Math.Round(_stationSlider.Value) != _stationIndex)
                    _stationSlider.Value = _stationIndex;
                _stationText.Text = $"est. {SecaoReader.Station(r.Assemblies[_stationIndex].Station)}  ({_stationIndex + 1}/{r.Assemblies.Count})";
            }
            else if (!_cutId.IsNull && _cuts.FirstOrDefault(x => x.Id == _cutId).Box is CutBox box)
            {
                _stationBar.Visibility = SW.Visibility.Collapsed;
                _data = SecaoReader.Build(doc.Database, box, _prefs, _corridorsLoaded ? _corridors : null);
            }
            else
            {
                _stationBar.Visibility = SW.Visibility.Collapsed;
                _data = null;
            }
        }
        catch (System.Exception ex)
        {
            Say("Erro ao montar a secao: " + ex.Message);
            _data = null;
        }

        _selectedItem = null;
        RenderGraphic();
        Status();
        if (_category == "CONTEUDO")
        {
            FillList();
            BuildTabs();
        }
    }

    private void Status()
    {
        if (_data is null)
        {
            Say(_cuts.Count == 0 ? "Sem caixa de corte: use Nova caixa... (ou C3DSECAOCORTE)." : "Selecione um corte na lista.");
            return;
        }

        Say($"{_data.Box.Name}: {_data.Length:0.00} m | {_data.Summary()} | {_data.Elapsed.TotalMilliseconds:0} ms | v{C3DVersions.Secao}");
    }

    private int CountFor(string category) => category switch
    {
        "CORTES" => _cuts.Count,
        "CORREDOR" => _corridorsLoaded ? _corridors.Sum(c => c.Regions.Count) : -1,
        "CONTEUDO" => _data?.Items.Count ?? 0,
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
                Foreground = T.Text,
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
        if (id == "CORREDOR")
            EnsureCorridors();
        FillCategories();
        FillList();
        BuildTabs();
    }

    // ------------------------------------------------------------------
    // Lista
    // ------------------------------------------------------------------

    private sealed class Row
    {
        public string Icon { get; init; } = "camadas";
        public string[] Cells { get; init; } = Array.Empty<string>();
        public object? Data { get; init; }
    }

    private static string N(double d, string f = "0.00") => d.ToString(f, CultureInfo.InvariantCulture);

    private void FillList()
    {
        string filtro = _find.Text.Trim();
        bool Match(string s) => filtro.Length == 0 || s.Contains(filtro, StringComparison.OrdinalIgnoreCase);

        List<Row> rows;
        string[] headers;
        double[] widths;
        object? current = null;

        switch (_category)
        {
            case "CORREDOR":
                headers = new[] { "Corredor", "Baseline", "Regiao", "Est. inicial", "Est. final", "Estacas" };
                widths = new[] { 150.0, 130, 120, 90, 90, 60 };
                rows = _corridors.SelectMany(c => c.Regions.Select(r => new Row
                {
                    Icon = "corredor",
                    Data = (c, r),
                    Cells = new[] { c.Name, r.Baseline, r.Region, SecaoReader.Station(r.StartStation), SecaoReader.Station(r.EndStation), r.Assemblies.Count.ToString(CultureInfo.InvariantCulture) }
                })).Where(r => Match(r.Cells[0]) || Match(r.Cells[1]) || Match(r.Cells[2])).ToList();
                current = _region is (SecaoReader.CorridorCache cc, SecaoReader.RegionCache rr) ? rows.Select(r => r.Data).FirstOrDefault(d => d is (SecaoReader.CorridorCache c2, SecaoReader.RegionCache r2) && c2 == cc && r2 == rr) : null;
                break;

            case "CONTEUDO":
                headers = new[] { "Tipo", "Nome", "Layer", "Posicao", "Afastamento" };
                widths = new[] { 100.0, 220, 120, 90, 90 };
                rows = (_data?.Items ?? new List<SectionItem>()).Select(i => new Row
                {
                    Icon = IconFor(i.Kind),
                    Data = i,
                    Cells = new[] { KindName(i.Kind), i.Name, i.Layer, i.Ghost ? "na profundidade" : "cortado", i.Ghost ? N(i.Depth) + " m" : "" }
                }).Where(r => Match(r.Cells[0]) || Match(r.Cells[1]) || Match(r.Cells[2])).ToList();
                current = _selectedItem;
                break;

            case "CONFIG":
                headers = new[] { "Superficie", "Mostrar" };
                widths = new[] { 260.0, 90 };
                rows = (_data?.Items.Where(i => i.Kind == ItemKind.Superficie).Select(i => i.Name) ?? Enumerable.Empty<string>())
                    .Concat(_prefs.SuperficiesOcultas).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s)
                    .Select(s => new Row { Icon = "malha", Data = s, Cells = new[] { s, _prefs.SuperficiesOcultas.Contains(s) ? "nao" : "sim" } }).ToList();
                break;

            default:
                headers = new[] { "Corte", "Comp. [m]", "Frente [m]", "Atras [m]", "Cota min", "Cota max", "Exagero" };
                widths = new[] { 150.0, 80, 80, 80, 80, 80, 60 };
                rows = _cuts.Where(c => Match(c.Box.Name)).Select(c => new Row
                {
                    Icon = "corte",
                    Data = c.Id,
                    Cells = new[]
                    {
                        c.Box.Name, N(c.Box.Length), N(c.Box.Frente), N(c.Box.Atras),
                        c.Box.ZMin is double a ? N(a) : "auto", c.Box.ZMax is double b ? N(b) : "auto", N(c.Box.Exagero, "0.#") + "x"
                    }
                }).ToList();
                current = _stationMode ? null : _cutId;
                break;
        }

        WC.GridView view = new();
        for (int i = 0; i < headers.Length; i++)
        {
            WC.GridViewColumn col = new() { Header = headers[i], Width = widths[i] };
            if (i == 0)
            {
                SW.FrameworkElementFactory sp = new(typeof(WC.StackPanel));
                sp.SetValue(WC.StackPanel.OrientationProperty, WC.Orientation.Horizontal);
                SW.FrameworkElementFactory img = new(typeof(WC.Image));
                img.SetBinding(WC.Image.SourceProperty, new SW.Data.Binding("IconImage"));
                img.SetValue(SW.FrameworkElement.WidthProperty, 16.0);
                img.SetValue(SW.FrameworkElement.HeightProperty, 16.0);
                img.SetValue(SW.FrameworkElement.MarginProperty, new SW.Thickness(0, 0, 5, 0));
                sp.AppendChild(img);
                SW.FrameworkElementFactory tb = new(typeof(WC.TextBlock));
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
        _list.SelectionChanged -= OnSelectionChanged;
        _list.Items.Clear();
        WC.ListViewItem? keep = null;
        foreach (Row row in rows)
        {
            WC.ListViewItem item = new() { Content = new RowView(row, IconOf(row.Icon)), Tag = row };
            _list.Items.Add(item);
            if (current is not null && Equals(row.Data, current))
                keep = item;
        }

        if (keep is not null)
            _list.SelectedItem = keep;
        _list.SelectionChanged += OnSelectionChanged;
    }

    private void OnSelectionChanged(object sender, WC.SelectionChangedEventArgs e) => OnRowSelected();

    private sealed class RowView
    {
        public RowView(Row row, WM.ImageSource icon)
        {
            IconImage = icon;
            string[] c = row.Cells;
            C0 = c.Length > 0 ? c[0] : "";
            C1 = c.Length > 1 ? c[1] : "";
            C2 = c.Length > 2 ? c[2] : "";
            C3 = c.Length > 3 ? c[3] : "";
            C4 = c.Length > 4 ? c[4] : "";
            C5 = c.Length > 5 ? c[5] : "";
            C6 = c.Length > 6 ? c[6] : "";
        }

        public WM.ImageSource IconImage { get; }
        public string C0 { get; }
        public string C1 { get; }
        public string C2 { get; }
        public string C3 { get; }
        public string C4 { get; }
        public string C5 { get; }
        public string C6 { get; }
    }

    private void OnRowSelected()
    {
        if ((_list.SelectedItem as WC.ListViewItem)?.Tag is not Row row)
            return;

        switch (row.Data)
        {
            case AcadDb.ObjectId id:
                if (id != _cutId || _stationMode)
                {
                    _cutId = id;
                    _stationMode = false;
                    _fitPending = true;
                    Recompute();
                }

                break;
            case (SecaoReader.CorridorCache c, SecaoReader.RegionCache r):
                _region = (c, r);
                _stationMode = true;
                _stationIndex = Math.Clamp(_stationIndex, 0, r.Assemblies.Count - 1);
                _fitPending = true;
                Recompute();
                break;
            case SectionItem it:
                _selectedItem = it;
                RenderGraphic();
                break;
            case string surface:
                if (_prefs.SuperficiesOcultas.Contains(surface))
                    _prefs.SuperficiesOcultas.Remove(surface);
                else
                    _prefs.SuperficiesOcultas.Add(surface);
                SecaoStore.SavePrefs(_prefs);
                Recompute();
                FillList();
                return;
        }

        BuildTabs();
    }

    private static string KindName(ItemKind k) => k switch
    {
        ItemKind.Superficie => "Superficie",
        ItemKind.Corredor => "Corredor",
        ItemKind.Tubo => "Tubo",
        ItemKind.Estrutura => "Estrutura",
        ItemKind.Solido => "Solido 3D",
        ItemKind.FeatureLine => "Feature line",
        ItemKind.Polilinha3d => "Polilinha 3D",
        _ => "Ponto"
    };

    private static string IconFor(ItemKind k) => k switch
    {
        ItemKind.Superficie => "malha",
        ItemKind.Corredor => "corredor",
        ItemKind.Tubo or ItemKind.Estrutura => "curva",
        ItemKind.Solido => "solido3d",
        ItemKind.FeatureLine or ItemKind.Polilinha3d => "polilinha",
        _ => "numero"
    };

    // ------------------------------------------------------------------
    // Acoes
    // ------------------------------------------------------------------

    private void StepStation(int delta)
    {
        if (!_stationMode || _region is not (_, SecaoReader.RegionCache r))
        {
            Say("Selecione uma regiao na categoria Corredor por estaca.");
            return;
        }

        _stationIndex = Math.Clamp(_stationIndex + delta, 0, r.Assemblies.Count - 1);
        Recompute();
    }

    private void OpenExageroMenu()
    {
        WC.ContextMenu menu = new();
        foreach (double ex in new[] { 0.5, 1, 2, 3, 5, 10 })
        {
            WC.MenuItem mi = new() { Header = ex.ToString("0.#", CultureInfo.InvariantCulture) + "x", IsCheckable = true, IsChecked = Math.Abs((_data?.Box.Exagero ?? _prefs.ExageroPadrao) - ex) < 1e-9 };
            double v = ex;
            mi.Click += (_, _) => SetExagero(v);
            menu.Items.Add(mi);
        }

        menu.IsOpen = true;
    }

    private void SetExagero(double ex)
    {
        _prefs.ExageroPadrao = ex;
        SecaoStore.SavePrefs(_prefs);
        if (!_stationMode && !_cutId.IsNull)
        {
            EditCut(b => b.Exagero = ex);
            return;
        }

        if (_data is not null)
            _data.Box.Exagero = ex;
        _fitPending = true;
        RenderGraphic();
    }

    /// <summary>Altera a caixa corrente na polilinha (XData) e refaz.</summary>
    private void EditCut(Action<CutBox> change)
    {
        Document? doc = Doc;
        if (doc is null || _cutId.IsNull)
            return;
        try
        {
            using DocumentLock _ = doc.LockDocument();
            using AcadDb.Transaction tr = doc.Database.TransactionManager.StartTransaction();
            if (tr.GetObject(_cutId, AcadDb.OpenMode.ForWrite) is not AcadDb.Polyline pl || SecaoStore.ReadCut(pl) is not CutBox box)
                return;
            change(box);
            SecaoStore.EnsureRegApp(tr, doc.Database);
            if (box.Line.Count != pl.NumberOfVertices)
            {
                // Linha trocada (inversao): reescreve os vertices.
                while (pl.NumberOfVertices > 0)
                    pl.RemoveVertexAt(0);
                for (int i = 0; i < box.Line.Count; i++)
                    pl.AddVertexAt(i, new Autodesk.AutoCAD.Geometry.Point2d(box.Line[i].X, box.Line[i].Y), 0, 0, 0);
            }
            else
            {
                for (int i = 0; i < box.Line.Count; i++)
                    pl.SetPointAt(i, new Autodesk.AutoCAD.Geometry.Point2d(box.Line[i].X, box.Line[i].Y));
            }

            SecaoStore.WriteCut(pl, box);
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            Say("Nao foi possivel alterar a caixa: " + ex.Message);
            return;
        }

        try
        {
            doc.Editor.Regen();
        }
        catch
        {
            // sem regen
        }

        ReadCuts();
        _fitPending = true;
        Recompute();
        FillList();
        BuildTabs();
    }

    private void InvertCut()
    {
        if (_stationMode || _cutId.IsNull)
        {
            Say("Selecione um corte na lista.");
            return;
        }

        EditCut(b =>
        {
            b.Line.Reverse();
            (b.Frente, b.Atras) = (b.Atras, b.Frente);
        });
    }

    private void DeleteCut()
    {
        Document? doc = Doc;
        if (doc is null || _stationMode || _cutId.IsNull)
        {
            Say("Selecione um corte na lista.");
            return;
        }

        if (SW.MessageBox.Show(this, "Apagar a polilinha da caixa de corte selecionada?", "Secao", SW.MessageBoxButton.YesNo, SW.MessageBoxImage.Question) != SW.MessageBoxResult.Yes)
            return;
        try
        {
            using DocumentLock _ = doc.LockDocument();
            using AcadDb.Transaction tr = doc.Database.TransactionManager.StartTransaction();
            tr.GetObject(_cutId, AcadDb.OpenMode.ForWrite).Erase();
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            Say("Nao foi possivel apagar: " + ex.Message);
            return;
        }

        _cutId = AcadDb.ObjectId.Null;
        Refresh();
    }

    // ------------------------------------------------------------------
    // Abas
    // ------------------------------------------------------------------

    private void BuildTabs()
    {
        List<(string Key, string Name, Func<SW.UIElement> Build)> tabs = new()
        {
            ("Propriedades", "Propriedades", BuildProperties),
            ("Conteudo", "Conteudo", BuildContent),
            ("Avisos", _data is { Warnings.Count: > 0 } ? $"Diagnostico ({_data.Warnings.Count})" : "Diagnostico", BuildWarnings)
        };
        if (!tabs.Any(t => t.Key == _tab))
            _tab = tabs[0].Key;

        _tabStrip.Children.Clear();
        foreach ((string key, string name, Func<SW.UIElement> build) in tabs)
        {
            bool active = key == _tab;
            WC.Button tab = new()
            {
                Content = name,
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

    private WC.Grid PropGrid()
    {
        WC.Grid g = new();
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(150) });
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        return g;
    }

    private void Prop(WC.Grid g, string name, SW.UIElement value)
    {
        int r = g.RowDefinitions.Count;
        g.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        WC.TextBlock n = new() { Text = name, Foreground = T.Muted, Margin = new SW.Thickness(0, 3, 8, 3), VerticalAlignment = SW.VerticalAlignment.Center };
        WC.Grid.SetRow(n, r);
        g.Children.Add(n);
        WC.Grid.SetRow(value, r);
        WC.Grid.SetColumn(value, 1);
        g.Children.Add(value);
    }

    private void PropText(WC.Grid g, string name, string value) =>
        Prop(g, name, new WC.TextBlock { Text = value, Foreground = T.Text, FontFamily = MonoFont, Margin = new SW.Thickness(0, 3, 0, 3), TextWrapping = SW.TextWrapping.Wrap });

    private SW.UIElement BuildProperties()
    {
        WC.StackPanel form = Form();
        if (_category == "CONTEUDO" && _selectedItem is SectionItem it)
        {
            form.Children.Add(Label(KindName(it.Kind) + ": " + it.Name, true));
            WC.Grid g = PropGrid();
            PropText(g, "Layer", it.Layer);
            PropText(g, "Posicao", it.Ghost ? $"na profundidade ({N(it.Depth)} m do plano)" : "cortado pelo plano");
            if (it.ZRange() is (double a, double b))
                PropText(g, "Cotas", $"{N(a, "0.000")} a {N(b, "0.000")}");
            PropText(g, "Elementos", $"{it.Polylines.Count} linha(s), {it.Circles.Count} circulo(s), {it.Points.Count} ponto(s)");
            foreach (KeyValuePair<string, string> kv in it.Info)
                PropText(g, kv.Key, kv.Value);
            form.Children.Add(g);
            return form;
        }

        if (_stationMode && _region is (SecaoReader.CorridorCache c, SecaoReader.RegionCache r))
        {
            form.Children.Add(Label($"{c.Name} / {r.Baseline} / {r.Region}", true));
            WC.Grid g = PropGrid();
            PropText(g, "Estacas", $"{SecaoReader.Station(r.StartStation)} a {SecaoReader.Station(r.EndStation)} ({r.Assemblies.Count})");
            if (r.Assemblies.Count > 0)
            {
                SecaoReader.AssemblyCache ac = r.Assemblies[Math.Clamp(_stationIndex, 0, r.Assemblies.Count - 1)];
                PropText(g, "Estaca mostrada", SecaoReader.Station(ac.Station) + $"  ({N(ac.Station, "0.000")} m)");
                PropText(g, "Subassemblies", string.Join(", ", ac.SubassemblyNames.Where(n => n.Length > 0).Distinct()));
                PropText(g, "Links", ac.Subassemblies.Sum(s => s.Count).ToString(CultureInfo.InvariantCulture));
            }

            form.Children.Add(g);
            form.Children.Add(Label("Profundidade para as estacas vizinhas (fantasmas)"));
            WC.Grid g2 = PropGrid();
            WC.TextBox fr = new() { Text = N(_prefs.FrentePadrao), Width = 90, HorizontalAlignment = SW.HorizontalAlignment.Left };
            WC.TextBox at = new() { Text = N(_prefs.AtrasPadrao), Width = 90, HorizontalAlignment = SW.HorizontalAlignment.Left };
            Prop(g2, "A frente [m]", fr);
            Prop(g2, "Atras [m]", at);
            form.Children.Add(g2);
            WC.Button apply = new() { Content = "Aplicar", Width = 110, HorizontalAlignment = SW.HorizontalAlignment.Left, Margin = new SW.Thickness(0, 8, 0, 0) };
            apply.Click += (_, _) =>
            {
                if (TryD(fr.Text, out double f)) _prefs.FrentePadrao = Math.Max(0, f);
                if (TryD(at.Text, out double a)) _prefs.AtrasPadrao = Math.Max(0, a);
                SecaoStore.SavePrefs(_prefs);
                Recompute();
            };
            form.Children.Add(apply);
            form.Children.Add(Label("Setas esquerda/direita trocam de estaca. A linha de corte e a perpendicular ao eixo na estaca; o lado esquerdo do eixo fica a esquerda da tela."));
            return form;
        }

        (AcadDb.ObjectId Id, CutBox Box) cut = _cuts.FirstOrDefault(x => x.Id == _cutId);
        if (cut.Box is null)
        {
            form.Children.Add(Label("Nenhum corte selecionado.", true));
            form.Children.Add(Label("Use Nova caixa de corte (ou o comando C3DSECAOCORTE): clique os pontos da linha em planta e informe as profundidades. A polilinha fica na layer SECAO-CORTE; mova os grips e a secao acompanha."));
            return form;
        }

        CutBox box = cut.Box;
        form.Children.Add(Label(box.Name, true));
        WC.Grid grid = PropGrid();
        WC.TextBox name = new() { Text = box.Name };
        WC.TextBox frente = new() { Text = N(box.Frente), Width = 90, HorizontalAlignment = SW.HorizontalAlignment.Left };
        WC.TextBox atras = new() { Text = N(box.Atras), Width = 90, HorizontalAlignment = SW.HorizontalAlignment.Left };
        WC.CheckBox autoZ = new() { Content = "automatica (pelos dados)", IsChecked = box.ZMin is null && box.ZMax is null, Foreground = T.Text, Margin = new SW.Thickness(0, 4, 0, 2) };
        (double dmin, double dmax) = _data?.ViewZ() ?? (0, 10);
        WC.TextBox zmin = new() { Text = N(box.ZMin ?? dmin, "0.00"), Width = 90, HorizontalAlignment = SW.HorizontalAlignment.Left, IsEnabled = autoZ.IsChecked != true };
        WC.TextBox zmax = new() { Text = N(box.ZMax ?? dmax, "0.00"), Width = 90, HorizontalAlignment = SW.HorizontalAlignment.Left, IsEnabled = autoZ.IsChecked != true };
        autoZ.Checked += (_, _) => { zmin.IsEnabled = zmax.IsEnabled = false; };
        autoZ.Unchecked += (_, _) => { zmin.IsEnabled = zmax.IsEnabled = true; };
        WC.TextBox exag = new() { Text = N(box.Exagero, "0.##"), Width = 90, HorizontalAlignment = SW.HorizontalAlignment.Left };
        Prop(grid, "Nome", name);
        PropText(grid, "Comprimento", N(box.Length) + " m, " + (box.Line.Count - 1) + " trecho(s)");
        Prop(grid, "Profundidade a frente [m]", frente);
        Prop(grid, "Profundidade atras [m]", atras);
        Prop(grid, "Faixa de cotas", autoZ);
        Prop(grid, "Cota minima", zmin);
        Prop(grid, "Cota maxima", zmax);
        Prop(grid, "Exagero vertical", exag);
        form.Children.Add(grid);

        WC.StackPanel buttons = new() { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(0, 8, 0, 0) };
        WC.Button apply2 = new() { Content = "Aplicar", Width = 110, Margin = new SW.Thickness(0, 0, 6, 0) };
        apply2.Click += (_, _) =>
        {
            EditCut(b =>
            {
                if (name.Text.Trim().Length > 0) b.Name = name.Text.Trim();
                if (TryD(frente.Text, out double f)) b.Frente = Math.Max(0, f);
                if (TryD(atras.Text, out double a)) b.Atras = Math.Max(0, a);
                if (TryD(exag.Text, out double e) && e > 0) b.Exagero = e;
                if (autoZ.IsChecked == true)
                {
                    b.ZMin = null;
                    b.ZMax = null;
                }
                else
                {
                    if (TryD(zmin.Text, out double z0)) b.ZMin = z0;
                    if (TryD(zmax.Text, out double z1)) b.ZMax = z1;
                    if (b.ZMin is double lo && b.ZMax is double hi && hi <= lo)
                        b.ZMax = lo + 1;
                }
            });
        };
        buttons.Children.Add(apply2);
        WC.Button fromView = new() { Content = "Cotas da vista atual", Width = 150, Margin = new SW.Thickness(0, 0, 6, 0), ToolTip = "Fixa a faixa de cotas no que esta visivel agora (depois do zoom)." };
        fromView.Click += (_, _) =>
        {
            (double lo, double hi) = VisibleZ();
            EditCut(b => { b.ZMin = Math.Round(lo, 2); b.ZMax = Math.Round(hi, 2); });
        };
        buttons.Children.Add(fromView);
        form.Children.Add(buttons);
        form.Children.Add(Label("Frente = lado esquerdo da linha (alem do plano, visto pelo observador); atras = lado direito. O inicio da linha fica a esquerda da tela. Inverter sentido troca os dois."));
        return form;
    }

    private SW.UIElement BuildContent()
    {
        WC.StackPanel form = Form();
        form.Children.Add(Label("O que entra na secao", true));
        void Add(string text, Func<bool> get, Action<bool> set)
        {
            WC.CheckBox cb = new() { Content = text, IsChecked = get(), Foreground = T.Text, Margin = new SW.Thickness(0, 3, 0, 3) };
            cb.Click += (_, _) =>
            {
                set(cb.IsChecked == true);
                SecaoStore.SavePrefs(_prefs);
                Recompute();
            };
            form.Children.Add(cb);
        }

        Add("Superficies (amostradas ao longo da linha)", () => _prefs.Superficies, v => _prefs.Superficies = v);
        Add("Corredores (links interpolados entre estacas)", () => _prefs.Corredores, v => _prefs.Corredores = v);
        Add("Tubos e estruturas", () => _prefs.Tubos, v => _prefs.Tubos = v);
        Add("Solidos 3D (secao pelo plano)", () => _prefs.Solidos, v => _prefs.Solidos = v);
        Add("Feature lines", () => _prefs.FeatureLines, v => _prefs.FeatureLines = v);
        Add("Polilinhas 3D", () => _prefs.Polilinhas3d, v => _prefs.Polilinhas3d = v);
        Add("Pontos COGO", () => _prefs.Pontos, v => _prefs.Pontos = v);
        form.Children.Add(Label("Projetado da profundidade (fantasmas, apagados)", true));
        Add("Estacas do corredor dentro da caixa", () => _prefs.FantasmaCorredor, v => _prefs.FantasmaCorredor = v);
        Add("Tubos e estruturas dentro da caixa", () => _prefs.FantasmaTubos, v => _prefs.FantasmaTubos = v);
        Add("Feature lines e polilinhas 3D dentro da caixa", () => _prefs.FantasmaLinhas, v => _prefs.FantasmaLinhas = v);
        Add("Pontos COGO dentro da caixa", () => _prefs.FantasmaPontos, v => _prefs.FantasmaPontos = v);
        form.Children.Add(Label("Vista", true));
        WC.CheckBox grade = new() { Content = "Grade", IsChecked = _prefs.Grade, Foreground = T.Text, Margin = new SW.Thickness(0, 3, 0, 3) };
        grade.Click += (_, _) => { _prefs.Grade = grade.IsChecked == true; SecaoStore.SavePrefs(_prefs); RenderGraphic(); };
        form.Children.Add(grade);
        WC.CheckBox rot = new() { Content = "Rotulos (nome no fim de cada linha)", IsChecked = _prefs.Rotulos, Foreground = T.Text, Margin = new SW.Thickness(0, 3, 0, 3) };
        rot.Click += (_, _) => { _prefs.Rotulos = rot.IsChecked == true; SecaoStore.SavePrefs(_prefs); RenderGraphic(); };
        form.Children.Add(rot);
        form.Children.Add(Label("Superficies individuais: categoria Configuracao (clique na linha para mostrar/ocultar)."));
        return form;
    }

    private SW.UIElement BuildWarnings()
    {
        WC.StackPanel form = Form();
        if (_data is not null)
        {
            form.Children.Add(Label("Diagnostico", true));
            form.Children.Add(new WC.TextBlock { Text = _data.Summary(), Foreground = T.Text, TextWrapping = SW.TextWrapping.Wrap, Margin = new SW.Thickness(0, 2, 0, 8) });
        }

        if (_data is null || _data.Warnings.Count == 0)
        {
            form.Children.Add(Label("Sem avisos."));
            return form;
        }

        foreach (string w in _data.Warnings.Take(60))
            form.Children.Add(new WC.TextBlock { Text = "- " + w, Foreground = T.Text, TextWrapping = SW.TextWrapping.Wrap, Margin = new SW.Thickness(0, 2, 0, 2) });
        return form;
    }

    private static bool TryD(string s, out double d) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d);

    // ------------------------------------------------------------------
    // Vista grafica
    // ------------------------------------------------------------------

    private const double MarginLeft = 60, MarginBottom = 26, MarginTop = 12, MarginRight = 14;

    private double Ex => _data?.Box.Exagero ?? 1.0;

    private SW.Point ToScreen(double s, double z) => new(_ox + (s * _scale), _oy - (z * _scale * Ex));

    private (double S, double Z) ToWorld(SW.Point p) => ((p.X - _ox) / _scale, (_oy - p.Y) / (_scale * Ex));

    private void Fit()
    {
        if (_data is null)
            return;
        double w = _graphic.ActualWidth - MarginLeft - MarginRight, h = _graphic.ActualHeight - MarginTop - MarginBottom;
        if (w < 20 || h < 20)
            return;
        (double zmin, double zmax) = _data.ViewZ();
        double length = Math.Max(_data.Length, 1e-6);
        double sx = w / length, sy = h / Math.Max((zmax - zmin) * Ex, 1e-6);
        _scale = Math.Min(sx, sy) * 0.97;
        double cw = length * _scale, ch = (zmax - zmin) * Ex * _scale;
        _ox = MarginLeft + ((w - cw) / 2);
        _oy = MarginTop + ((h - ch) / 2) + ch + (zmin * _scale * Ex);
        _fitPending = false;
    }

    private (double Min, double Max) VisibleZ()
    {
        (_, double top) = ToWorld(new SW.Point(0, MarginTop));
        (_, double bottom) = ToWorld(new SW.Point(0, _graphic.ActualHeight - MarginBottom));
        return (Math.Min(top, bottom), Math.Max(top, bottom));
    }

    private void OnWheel(object sender, WI.MouseWheelEventArgs e)
    {
        if (_data is null)
            return;
        SW.Point p = e.GetPosition(_graphic);
        (double s, double z) = ToWorld(p);
        double k = e.Delta > 0 ? 1.2 : 1 / 1.2;
        _scale *= k;
        SW.Point q = ToScreen(s, z);
        _ox += p.X - q.X;
        _oy += p.Y - q.Y;
        RenderGraphic();
        e.Handled = true;
    }

    private void OnMouseDown(object sender, WI.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _fitPending = true;
            RenderGraphic();
            return;
        }

        if (e.ChangedButton == WI.MouseButton.Left && _data is not null && _category == "CONTEUDO")
        {
            SectionItem? hit = HitTest(e.GetPosition(_graphic));
            if (hit is not null)
            {
                _selectedItem = hit;
                FillList();
                BuildTabs();
                RenderGraphic();
            }
        }

        _dragging = true;
        _dragStart = e.GetPosition(_graphic);
        _dragOrigin = (_ox, _oy);
        _graphic.CaptureMouse();
    }

    private void OnMouseMove(object sender, WI.MouseEventArgs e)
    {
        SW.Point p = e.GetPosition(_graphic);
        if (_dragging && (e.LeftButton == WI.MouseButtonState.Pressed || e.MiddleButton == WI.MouseButtonState.Pressed))
        {
            _ox = _dragOrigin.ox + (p.X - _dragStart.X);
            _oy = _dragOrigin.oy + (p.Y - _dragStart.Y);
            RenderGraphic();
            return;
        }

        if (_data is null)
            return;
        (double s, double z) = ToWorld(p);
        SectionItem? hit = HitTest(p);
        string info = $"S {s,8:0.00} m   Z {z,9:0.000}";
        if (hit is not null)
            info += $"   |  {KindName(hit.Kind)}: {hit.Name}" + (hit.Ghost ? $" (a {N(hit.Depth)} m do plano)" : "");
        _graphicInfo.Text = info;
    }

    private SectionItem? HitTest(SW.Point p)
    {
        if (_data is null)
            return null;
        SectionItem? best = null;
        double bestD = 7.0;
        foreach (SectionItem it in _data.Items)
        {
            foreach (List<(double S, double Z)> pl in it.Polylines)
            {
                for (int i = 0; i + 1 < pl.Count; i++)
                {
                    double d = DistToSegment(p, ToScreen(pl[i].S, pl[i].Z), ToScreen(pl[i + 1].S, pl[i + 1].Z));
                    if (d < bestD)
                    {
                        bestD = d;
                        best = it;
                    }
                }
            }

            foreach ((double s, double z, double r) in it.Circles)
            {
                SW.Point c = ToScreen(s, z);
                double rx = r * _scale, ry = r * _scale * Ex;
                double d = Math.Abs(Math.Sqrt(Math.Pow((p.X - c.X) / Math.Max(rx, 1), 2) + Math.Pow((p.Y - c.Y) / Math.Max(ry, 1), 2)) - 1) * Math.Min(rx, ry);
                if (d < bestD)
                {
                    bestD = d;
                    best = it;
                }
            }

            foreach ((double s, double z, _) in it.Points)
            {
                SW.Point c = ToScreen(s, z);
                double d = Math.Sqrt(Math.Pow(p.X - c.X, 2) + Math.Pow(p.Y - c.Y, 2));
                if (d < bestD)
                {
                    bestD = d;
                    best = it;
                }
            }
        }

        return best;
    }

    private static double DistToSegment(SW.Point p, SW.Point a, SW.Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = (dx * dx) + (dy * dy);
        double t = len2 < 1e-9 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / len2, 0, 1);
        double x = a.X + (t * dx), y = a.Y + (t * dy);
        return Math.Sqrt(Math.Pow(p.X - x, 2) + Math.Pow(p.Y - y, 2));
    }

    /// <summary>Cor (ACI) de um item: a do objeto quando e uma cor "forte", senao a da classe.</summary>
    public static int ColorFor(SectionItem it)
    {
        if (it.ColorIndex is >= 1 and <= 6)
            return it.ColorIndex;
        return it.Kind switch
        {
            ItemKind.Superficie => 3,
            ItemKind.Corredor => 30,
            ItemKind.Tubo or ItemKind.Estrutura => 4,
            ItemKind.Solido => 2,
            ItemKind.FeatureLine => 6,
            ItemKind.Polilinha3d => 5,
            _ => 7
        };
    }

    private static WM.Brush BrushFor(SectionItem it, bool selected)
    {
        if (selected)
            return ToolTheme.Brush(255, 176, 0);
        if (it.Ghost)
            return ToolTheme.Brush(120, 124, 130);
        return ColorFor(it) switch
        {
            1 => ToolTheme.Brush(235, 80, 80),
            2 => ToolTheme.Brush(230, 220, 60),
            3 => ToolTheme.Brush(90, 210, 110),
            4 => ToolTheme.Brush(80, 200, 230),
            5 => ToolTheme.Brush(110, 150, 255),
            6 => ToolTheme.Brush(230, 110, 230),
            30 => ToolTheme.Brush(255, 160, 60),
            _ => ToolTheme.Brush(225, 225, 225)
        };
    }

    private void RenderGraphic()
    {
        _graphic.Children.Clear();
        double W = _graphic.ActualWidth, H = _graphic.ActualHeight;
        if (W < 10 || H < 10)
            return;

        if (_data is null)
        {
            _graphic.Children.Add(Text(_cuts.Count == 0 ? "Sem caixa de corte. Use Nova caixa de corte: clique os pontos em planta e informe as profundidades." : "Selecione um corte na lista.",
                12, H / 2 - 8, ToolTheme.GraphicText, 12));
            return;
        }

        if (_fitPending)
            Fit();

        (double zmin, double zmax) = _data.ViewZ();
        double length = _data.Length;

        // Area util (clip): a secao fica dentro da moldura; reguas fora.
        WC.Canvas plot = new() { ClipToBounds = true, Width = Math.Max(1, W - MarginLeft - MarginRight), Height = Math.Max(1, H - MarginTop - MarginBottom) };
        WC.Canvas.SetLeft(plot, MarginLeft);
        WC.Canvas.SetTop(plot, MarginTop);
        _graphic.Children.Add(plot);
        SW.Point P(double s, double z)
        {
            SW.Point q = ToScreen(s, z);
            return new SW.Point(q.X - MarginLeft, q.Y - MarginTop);
        }

        // Faixa da caixa (cotas min/max) e comprimento: fundo levemente mais claro.
        SW.Point c0 = P(0, zmax), c1 = P(length, zmin);
        plot.Children.Add(new WS.Rectangle
        {
            Width = Math.Max(0, c1.X - c0.X), Height = Math.Max(0, c1.Y - c0.Y),
            Fill = ToolTheme.Brush(64, 66, 71), Stroke = ToolTheme.Brush(110, 112, 118), StrokeThickness = 1
        }.At(c0.X, c0.Y));

        // Grade.
        double zStep = SecaoGeometry.NiceStep(60.0 / Math.Max(_scale * Ex, 1e-9));
        double sStep = SecaoGeometry.NiceStep(80.0 / Math.Max(_scale, 1e-9));
        (double vzLo, double vzHi) = VisibleZ();
        (double vsLo, _) = ToWorld(new SW.Point(MarginLeft, 0));
        (double vsHi, _) = ToWorld(new SW.Point(W - MarginRight, 0));
        WM.Brush grid = ToolTheme.Brush(78, 80, 86), gridMajor = ToolTheme.Brush(96, 98, 104);
        if (_prefs.Grade)
        {
            for (double z = Math.Floor(vzLo / zStep) * zStep; z <= vzHi; z += zStep)
            {
                SW.Point a = P(vsLo, z), b = P(vsHi, z);
                bool major = Math.Abs(Math.Round(z / (zStep * 5)) * zStep * 5 - z) < zStep * 0.01;
                plot.Children.Add(new WS.Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = major ? gridMajor : grid, StrokeThickness = 1 });
            }

            for (double s = Math.Floor(vsLo / sStep) * sStep; s <= vsHi; s += sStep)
            {
                SW.Point a = P(s, vzLo), b = P(s, vzHi);
                plot.Children.Add(new WS.Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = grid, StrokeThickness = 1 });
            }
        }

        // Itens: fantasmas primeiro (ja ordenados), cortados por cima; selecionado por ultimo.
        foreach (SectionItem it in _data.Items.Where(i => !ReferenceEquals(i, _selectedItem)).Concat(_selectedItem is null ? Enumerable.Empty<SectionItem>() : new[] { _selectedItem }))
            DrawItem(plot, it, ReferenceEquals(it, _selectedItem), P);

        // Reguas: cotas a esquerda, distancias embaixo.
        WM.Brush ruler = ToolTheme.Brush(44, 46, 50);
        _graphic.Children.Add(new WS.Rectangle { Width = MarginLeft - 2, Height = H, Fill = ruler }.At(0, 0));
        _graphic.Children.Add(new WS.Rectangle { Width = W, Height = MarginBottom - 2, Fill = ruler }.At(0, H - MarginBottom + 2));
        for (double z = Math.Floor(vzLo / zStep) * zStep; z <= vzHi; z += zStep)
        {
            SW.Point a = ToScreen(0, z);
            if (a.Y < MarginTop || a.Y > H - MarginBottom)
                continue;
            _graphic.Children.Add(new WS.Line { X1 = MarginLeft - 6, Y1 = a.Y, X2 = MarginLeft - 2, Y2 = a.Y, Stroke = ToolTheme.GraphicText, StrokeThickness = 1 });
            _graphic.Children.Add(Text(z.ToString(zStep < 1 ? "0.00" : "0.0", CultureInfo.InvariantCulture), 4, a.Y - 7, ToolTheme.GraphicText, 10, MarginLeft - 12, SW.TextAlignment.Right));
        }

        for (double s = Math.Floor(vsLo / sStep) * sStep; s <= vsHi; s += sStep)
        {
            SW.Point a = ToScreen(s, 0);
            if (a.X < MarginLeft || a.X > W - MarginRight)
                continue;
            _graphic.Children.Add(new WS.Line { X1 = a.X, Y1 = H - MarginBottom + 2, X2 = a.X, Y2 = H - MarginBottom + 6, Stroke = ToolTheme.GraphicText, StrokeThickness = 1 });
            _graphic.Children.Add(Text(s.ToString(sStep < 1 ? "0.0" : "0", CultureInfo.InvariantCulture), a.X - 30, H - MarginBottom + 8, ToolTheme.GraphicText, 10, 60, SW.TextAlignment.Center));
        }

        // Cabecalho: nome, exagero, escala.
        _graphic.Children.Add(Text($"{_data.Box.Name}   exagero {Ex:0.##}x   1 m = {_scale:0.#} px", MarginLeft + 6, 2, ToolTheme.GraphicText, 10));
        _graphic.Children.Add(Text("esq. (inicio da linha)", MarginLeft + 6, H - MarginBottom - 16, ToolTheme.Brush(150, 152, 156), 9));
        _graphic.Children.Add(Text("dir. (fim da linha)", W - MarginRight - 110, H - MarginBottom - 16, ToolTheme.Brush(150, 152, 156), 9, 104, SW.TextAlignment.Right));
    }

    private void DrawItem(WC.Canvas plot, SectionItem it, bool selected, Func<double, double, SW.Point> P)
    {
        WM.Brush brush = BrushFor(it, selected);
        double thick = selected ? 2.4 : it.Ghost ? 1.0 : it.Kind == ItemKind.Superficie ? 1.8 : 1.4;
        WM.DoubleCollection? dash = it.Ghost ? new WM.DoubleCollection { 4, 3 } : null;

        foreach (List<(double S, double Z)> pl in it.Polylines)
        {
            if (pl.Count < 2)
                continue;
            WS.Polyline line = new() { Stroke = brush, StrokeThickness = thick, StrokeLineJoin = WM.PenLineJoin.Round };
            if (dash is not null)
                line.StrokeDashArray = dash;
            foreach ((double s, double z) in pl)
                line.Points.Add(P(s, z));
            plot.Children.Add(line);
            if (_prefs.Rotulos && !it.Ghost && (it.Kind == ItemKind.Superficie || it.Kind == ItemKind.Corredor) && ReferenceEquals(pl, it.Polylines[0]))
            {
                (double s, double z) = pl[^1];
                SW.Point e = P(s, z);
                plot.Children.Add(Text(it.Name, e.X + 4, e.Y - 7, brush, 9));
            }
        }

        foreach ((double s, double z, double r) in it.Circles)
        {
            double rx = r * _scale, ry = r * _scale * Ex;
            SW.Point c = P(s, z);
            WS.Ellipse el = new() { Width = rx * 2, Height = ry * 2, Stroke = brush, StrokeThickness = thick };
            if (dash is not null)
                el.StrokeDashArray = dash;
            plot.Children.Add(el.At(c.X - rx, c.Y - ry));
        }

        foreach ((double s, double z, string label) in it.Points)
        {
            SW.Point c = P(s, z);
            const double m = 4;
            plot.Children.Add(new WS.Line { X1 = c.X - m, Y1 = c.Y - m, X2 = c.X + m, Y2 = c.Y + m, Stroke = brush, StrokeThickness = thick });
            plot.Children.Add(new WS.Line { X1 = c.X - m, Y1 = c.Y + m, X2 = c.X + m, Y2 = c.Y - m, Stroke = brush, StrokeThickness = thick });
            if (_prefs.Rotulos && label.Length > 0)
                plot.Children.Add(Text(label, c.X + 5, c.Y - 14, brush, 9));
        }
    }

    private static WC.TextBlock Text(string t, double x, double y, WM.Brush brush, double size, double? width = null, SW.TextAlignment align = SW.TextAlignment.Left)
    {
        WC.TextBlock tb = new() { Text = t, Foreground = brush, FontSize = size, FontFamily = MonoFont, TextAlignment = align };
        if (width is double w)
            tb.Width = w;
        WC.Canvas.SetLeft(tb, x);
        WC.Canvas.SetTop(tb, y);
        return tb;
    }
}

internal static class SecaoCanvasExtensions
{
    public static T At<T>(this T element, double x, double y) where T : SW.UIElement
    {
        WC.Canvas.SetLeft(element, x);
        WC.Canvas.SetTop(element, y);
        return element;
    }
}
