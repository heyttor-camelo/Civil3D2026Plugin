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
/// distancia e elevacao, zoom e pan), lista em colunas e abas de detalhe. Janela
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

    /// <summary>Secao mostrada agora (usada por C3DSECAOCORTE para o diagnostico).</summary>
    public static SectionData? CurrentData => _instance?._data;

    public static SecaoStore.Prefs? CurrentPrefs => _instance?._prefs;

    public static double CurrentExagero => _instance is null ? 1.0 : _instance.Ex;

    private static readonly int[] ColorsAci = { 30, 4, 6, 2, 3, 1, 5, 7 };

    /// <summary>O que C3DSECAODESENHAR leva ao model: a secao mostrada, ou as fixadas visiveis na comparacao.</summary>
    public static List<DrawLayer>? ExportLayers()
    {
        SecaoPanel? p = _instance;
        if (p is null)
            return null;
        if (p.CompareMode)
        {
            return p._compare.Where(c => c.Visible).Select(c => new DrawLayer(c.Data, p.CompareOffset(c.Data),
                ColorsAci[Math.Max(0, Array.IndexOf(Colors, c.Color)) % ColorsAci.Length], c.Label, p._prefs.CompararFantasmas)).ToList();
        }

        return p._data is null || p._data.Items.Count == 0 ? null : new List<DrawLayer> { new(p._data, 0, null, p._data.Box.Name, true) };
    }

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

    // Comparacao: secoes fixadas (copias, nao sao recalculadas) sobrepostas na vista.
    private sealed class CompareEntry
    {
        public string Label = "";
        public SectionData Data = null!;
        public bool Visible = true;
        public WM.Color Color;
    }

    private readonly List<CompareEntry> _compare = new();
    private CompareEntry? _compareSel;
    private string _compareAlign = "Eixo";
    private double _compareEx = 1;

    private static readonly WM.Color[] Colors =
    {
        WM.Color.FromRgb(255, 160, 60), WM.Color.FromRgb(80, 200, 230), WM.Color.FromRgb(230, 110, 230), WM.Color.FromRgb(230, 220, 60),
        WM.Color.FromRgb(90, 210, 110), WM.Color.FromRgb(235, 80, 80), WM.Color.FromRgb(110, 150, 255), WM.Color.FromRgb(225, 225, 225)
    };

    // Vista 3D: montada so quando a categoria 3D esta aberta (e refeita quando a secao muda).
    private readonly SecaoView3D _view3d = new();
    private bool _scene3dDirty = true;

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
    private WC.Button _textButton = new();
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
        ("COMPARAR", "copiar", "Comparar secoes"),
        ("VISTA3D", "solido3d", "Vista 3D"),
        ("CONFIG", "config", "Configuracao")
    };

    private sealed record Cmd(string Key, string Icon, string Title, string Group, string Description, string[] Steps, Action<SecaoPanel> Run);

    private static readonly Cmd[] Commands =
    {
        new("NOVO", "corte", "Nova caixa de corte", "Corte",
            "Linha de corte por pontos (ou polilinha existente) e profundidades; a caixa fica numa polilinha na layer SECAO-CORTE e a janela acompanha os grips.",
            new[] { "Clique os pontos da linha (Enter termina).", "Profundidade a frente e atras.", "Elevacoes e exagero na aba Propriedades." },
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
        new("FIXAR", "copiar", "Fixar na comparacao", "Comparar",
            "Guarda a secao mostrada (corte ou estaca do corredor) na categoria Comparar secoes, para sobrepor com outras.",
            new[] { "Mostre a secao (corte ou estaca).", "Clique para fixar; repita com outras.", "Abra Comparar secoes." }, p => p.PinCurrent()),
        new("DESENHAR", "polilinha", "Desenhar secao no model", "Saidas",
            "Leva a secao mostrada para o desenho como polilinhas 2D (moldura, elevacoes, distancias), no ponto clicado.",
            new[] { "Clique o canto inferior esquerdo.", "Confirme o exagero." }, p => p.Send("C3DSECAODESENHAR ", "Clique onde a secao deve ser desenhada.")),
        new("ATUALIZAR", "atualizar", "Atualizar", "Geral", "Le o desenho de novo (cortes, corredores e conteudo).", new[] { "Sem perguntas." }, p => p.FullRefresh())
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
            panel.UpdateCuts(ids);
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
        Civil3D2026Plugin.UI.Common.LogView.Atalho(this, "Secao");
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

        WC.Grid graphicHost = new();
        graphicHost.Children.Add(_graphic);
        _view3d.Visibility = SW.Visibility.Collapsed;
        _view3d.Info += t => _graphicInfo.Text = t;
        _view3d.ImageRequested += save => Image3D(save);
        graphicHost.Children.Add(_view3d);
        WC.Border graphic = new() { BorderBrush = T.Line, BorderThickness = new SW.Thickness(1), Child = graphicHost, Margin = new SW.Thickness(4, 0, 4, 0) };
        _graphic.SizeChanged += (_, _) => RenderGraphic();
        _graphic.MouseWheel += OnWheel;
        _graphic.MouseDown += OnMouseDown;
        _graphic.MouseMove += OnMouseMove;
        _graphic.MouseUp += (_, _) => { _dragging = false; EndLabelDrag(); _graphic.ReleaseMouseCapture(); };
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
        _textButton = new WC.Button { Content = "Textos  ▾", Padding = new SW.Thickness(8, 0, 8, 0), Margin = new SW.Thickness(4, 2, 2, 2), ToolTip = "O que aparece escrito na vista e no desenho. Ctrl + arrastar move um rotulo." };
        _textButton.Click += (_, _) => OpenTextMenu();
        WC.DockPanel.SetDock(_textButton, WC.Dock.Right);
        graphicBar.Children.Add(_textButton);
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
        _list.MouseRightButtonUp += (_, e) =>
        {
            if (ItemUnder(e.OriginalSource as SW.DependencyObject)?.Tag is Row row)
            {
                _list.SelectedItem = ItemUnder(e.OriginalSource as SW.DependencyObject);
                OpenRowMenu(row);
                e.Handled = true;
            }
        };
        _list.MouseDoubleClick += (_, _) =>
        {
            if ((_list.SelectedItem as WC.ListViewItem)?.Tag is Row { Data: CompareEntry ce })
            {
                ce.Visible = !ce.Visible;
                _fitPending = true;
                FillList();
                RenderGraphic();
            }
            else if ((_list.SelectedItem as WC.ListViewItem)?.Tag is Row { Data: Obj3D o })
            {
                Toggle3D(o.Key);
            }
        };
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
        leftButtons.Children.Add(FooterButton("Atualizar", FullRefresh));
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

    /// <summary>Atualiza so as caixas que mudaram (sem varrer o model space).</summary>
    private void UpdateCuts(HashSet<AcadDb.ObjectId> ids)
    {
        Document? doc = Doc;
        if (doc is null)
            return;
        using AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
        foreach (AcadDb.ObjectId id in ids)
        {
            _cuts.RemoveAll(c => c.Id == id);
            if (id.IsNull || id.IsErased)
                continue;
            try
            {
                if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.Polyline pl && SecaoStore.ReadCut(pl) is CutBox box && box.IsValid)
                    _cuts.Add((id, box));
            }
            catch
            {
                // apagada no meio do caminho
            }
        }

        if (!_cutId.IsNull && !_cuts.Any(c => c.Id == _cutId))
            _cutId = AcadDb.ObjectId.Null;
        if (_cutId.IsNull && _cuts.Count > 0 && !_stationMode)
            _cutId = _cuts[0].Id;
    }

    /// <summary>O corredor mudou (rebuild): sera lido de novo na proxima secao.</summary>
    public static void InvalidateCorridor(AcadDb.ObjectId id)
    {
        SecaoPanel? panel = _instance;
        if (panel is null)
            return;
        panel._corridors.RemoveAll(c => c.Id == id);
        panel._corridorsLoaded = false;
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

    /// <summary>Atualizar: esquece os caches (extents, malhas, blocos, corredores) e le tudo de novo.</summary>
    private void FullRefresh()
    {
        if (Doc is Document doc)
            SecaoReader.InvalidateAll(doc.Database);
        _corridorsLoaded = false;
        _scene3dDirty = true;
        Refresh();
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
                _data = SecaoReader.Build(doc.Database, box, _prefs, _corridors);
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
        _scene3dDirty = true;
        if (_category == "VISTA3D")
            Update3D(keepCamera: true);
        else
            RenderGraphic();
        Status();
        if (_category is "CONTEUDO" or "VISTA3D")
        {
            FillList();
            BuildTabs();
        }
    }

    /// <summary>Monta a cena 3D da caixa corrente (so na categoria Vista 3D).</summary>
    private void Update3D(bool keepCamera)
    {
        Document? doc = Doc;
        if (doc is null || _data is null)
        {
            _view3d.Clear();
            _graphicInfo.Text = "3D: sem secao (selecione um corte ou uma estaca).";
            return;
        }

        if (!_scene3dDirty && _view3d.Scene is not null)
            return;
        try
        {
            (double zmin, double zmax) = _data.ViewZ();
            Scene3D scene = SecaoReader.Build3D(doc.Database, _data.Box, _prefs, _corridors, zmin, zmax);
            _view3d.SetScene(scene, _data.Box.Exagero, keepCamera && _view3d.Scene is not null && _view3d.Scene.Box.Name == scene.Box.Name);
            _scene3dDirty = false;
        }
        catch (System.Exception ex)
        {
            Say("Erro ao montar a vista 3D: " + ex.Message);
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
        "VISTA3D" => _view3d.Scene?.Objects().Count ?? -1,
        "CORTES" => _cuts.Count,
        "CORREDOR" => _corridorsLoaded ? _corridors.Sum(c => c.Regions.Count) : -1,
        "CONTEUDO" => _data?.Items.Count ?? 0,
        "COMPARAR" => _compare.Count,
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
        bool wasCompare = _category == "COMPARAR";
        _category = id;
        _tab = "Propriedades";
        if (id == "CORREDOR")
            EnsureCorridors();
        bool show3d = id == "VISTA3D";
        _view3d.Visibility = show3d ? SW.Visibility.Visible : SW.Visibility.Collapsed;
        _graphic.Visibility = show3d ? SW.Visibility.Collapsed : SW.Visibility.Visible;
        _textButton.Visibility = show3d ? SW.Visibility.Collapsed : SW.Visibility.Visible;
        if (show3d)
            Update3D(keepCamera: true);
        else if (wasCompare != (id == "COMPARAR"))
        {
            _fitPending = true;
            RenderGraphic();
        }

        FillCategories();
        FillList();
        BuildTabs();
    }

    /// <summary>Fixa a secao mostrada na comparacao (copia, nao e recalculada depois).</summary>
    private void PinCurrent()
    {
        if (_data is null || CompareMode)
        {
            Say("Mostre uma secao (Cortes ou Corredor por estaca) para fixar.");
            return;
        }

        string label = _data.Box.Name;
        int n = _compare.Count(c => c.Label.StartsWith(label, StringComparison.Ordinal));
        if (n > 0)
            label += $" ({n + 1})";
        _compare.Add(new CompareEntry { Label = label, Data = _data, Color = Colors[_compare.Count % Colors.Length] });
        FillCategories();
        Say($"Fixada na comparacao: {label} ({_compare.Count} no total). Abra Comparar secoes.");
    }

    // ------------------------------------------------------------------
    // Lista
    // ------------------------------------------------------------------

    /// <summary>Objeto da vista 3D na lista (chave tipo|nome).</summary>
    private sealed record Obj3D(string Key);

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
                headers = new[] { "Tipo", "Nome", "Layer", "Posicao", "Afastamento", "Estilo" };
                widths = new[] { 100.0, 220, 110, 90, 80, 80 };
                rows = (_data?.Items ?? new List<SectionItem>()).Select(i => new Row
                {
                    Icon = IconFor(i.Kind),
                    Data = i,
                    Cells = new[] { KindName(i.Kind), i.Name, i.Layer, i.Ghost ? "na profundidade" : "cortado", i.Ghost ? N(i.Depth) + " m" : "", StyleName(_prefs.StyleOf(i)) }
                }).Where(r => Match(r.Cells[0]) || Match(r.Cells[1]) || Match(r.Cells[2])).ToList();
                current = _selectedItem;
                break;

            case "COMPARAR":
                headers = new[] { "Secao", "Comp. [m]", "Elev. min", "Elev. max", "Itens", "Mostrar" };
                widths = new[] { 220.0, 80, 80, 80, 60, 60 };
                rows = _compare.Where(c => Match(c.Label)).Select(c =>
                {
                    (double z0, double z1) = c.Data.DataZ();
                    return new Row
                    {
                        Icon = "copiar",
                        Data = c,
                        Cells = new[] { c.Label, N(c.Data.Length), N(z0), N(z1), c.Data.Items.Count(i => !i.Ghost).ToString(CultureInfo.InvariantCulture), c.Visible ? "sim" : "nao" }
                    };
                }).ToList();
                current = _compareSel;
                break;

            case "VISTA3D":
                headers = new[] { "Tipo", "Nome", "Elementos", "Mostrar" };
                widths = new[] { 110.0, 240, 120, 60 };
                rows = _view3d.Scene is Scene3D sc
                    ? sc.Objects().Where(o => Match(o.Name) || Match(KindName(o.Kind))).Select(o => new Row
                    {
                        Icon = IconFor(o.Kind),
                        Data = new Obj3D(o.Key),
                        Cells = new[] { KindName(o.Kind), o.Name, o.Info, _view3d.Hidden.Contains(o.Key) ? "nao" : "sim" }
                    }).ToList()
                    : new List<Row>();
                break;

            case "CONFIG":
                headers = new[] { "Superficie", "Mostrar" };
                widths = new[] { 260.0, 90 };
                rows = (_data?.Items.Where(i => i.Kind == ItemKind.Superficie).Select(i => i.Name) ?? Enumerable.Empty<string>())
                    .Concat(_prefs.SuperficiesOcultas).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s)
                    .Select(s => new Row { Icon = "malha", Data = s, Cells = new[] { s, _prefs.SuperficiesOcultas.Contains(s) ? "nao" : "sim" } }).ToList();
                break;

            default:
                headers = new[] { "Corte", "Comp. [m]", "Frente [m]", "Atras [m]", "Elev. min", "Elev. max", "Exagero" };
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
            case CompareEntry ce:
                _compareSel = ce;
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

    private static WC.ListViewItem? ItemUnder(SW.DependencyObject? d)
    {
        while (d is not null and not WC.ListViewItem)
            d = d is SW.Media.Visual or SW.Media.Media3D.Visual3D ? WM.VisualTreeHelper.GetParent(d) : SW.LogicalTreeHelper.GetParent(d);
        return d as WC.ListViewItem;
    }

    /// <summary>Menu do botao direito na lista: estilo do item ou acoes da comparacao.</summary>
    private void OpenRowMenu(Row row)
    {
        switch (row.Data)
        {
            case SectionItem it:
                OpenStyleMenu(it);
                break;
            case Obj3D o:
            {
                WC.ContextMenu menu = new();
                bool hidden = _view3d.Hidden.Contains(o.Key);
                WC.MenuItem vis = new() { Header = hidden ? "Mostrar" : "Ocultar" };
                vis.Click += (_, _) => Toggle3D(o.Key);
                menu.Items.Add(vis);
                WC.MenuItem only = new() { Header = "Mostrar so este (isolar)" };
                only.Click += (_, _) =>
                {
                    if (_view3d.Scene is Scene3D sc)
                        _view3d.SetHidden(sc.Objects().Select(x => x.Key).Where(k => k != o.Key).ToList());
                    FillList();
                };
                menu.Items.Add(only);
                WC.MenuItem all = new() { Header = "Mostrar todos" };
                all.Click += (_, _) => { _view3d.SetHidden(Array.Empty<string>()); FillList(); };
                menu.Items.Add(all);
                menu.IsOpen = true;
                break;
            }

            case CompareEntry ce:
            {
                WC.ContextMenu menu = new();
                WC.MenuItem vis = new() { Header = ce.Visible ? "Ocultar na vista" : "Mostrar na vista" };
                vis.Click += (_, _) => { ce.Visible = !ce.Visible; _fitPending = true; FillList(); RenderGraphic(); };
                menu.Items.Add(vis);
                WC.MenuItem only = new() { Header = "Mostrar so esta" };
                only.Click += (_, _) => { foreach (CompareEntry c in _compare) c.Visible = ReferenceEquals(c, ce); _fitPending = true; FillList(); RenderGraphic(); };
                menu.Items.Add(only);
                WC.MenuItem all = new() { Header = "Mostrar todas" };
                all.Click += (_, _) => { foreach (CompareEntry c in _compare) c.Visible = true; _fitPending = true; FillList(); RenderGraphic(); };
                menu.Items.Add(all);
                menu.Items.Add(new WC.Separator());
                WC.MenuItem color = new() { Header = "Cor" };
                foreach (WM.Color c in Colors)
                {
                    WC.MenuItem ci = new() { Header = new WS.Rectangle { Width = 40, Height = 10, Fill = Solid(c) }, IsCheckable = true, IsChecked = c == ce.Color };
                    WM.Color chosen = c;
                    ci.Click += (_, _) => { ce.Color = chosen; RenderGraphic(); };
                    color.Items.Add(ci);
                }

                menu.Items.Add(color);
                menu.Items.Add(new WC.Separator());
                WC.MenuItem remove = new() { Header = "Remover da comparacao" };
                remove.Click += (_, _) => { _compare.Remove(ce); if (ReferenceEquals(_compareSel, ce)) _compareSel = null; _fitPending = true; FillCategories(); FillList(); BuildTabs(); RenderGraphic(); };
                menu.Items.Add(remove);
                WC.MenuItem clear = new() { Header = "Remover todas" };
                clear.Click += (_, _) => { _compare.Clear(); _compareSel = null; FillCategories(); FillList(); BuildTabs(); RenderGraphic(); };
                menu.Items.Add(clear);
                menu.IsOpen = true;
                break;
            }
        }
    }

    private static string KindName(ItemKind k) => k switch
    {
        ItemKind.Superficie => "Superficie",
        ItemKind.Corredor => "Corredor",
        ItemKind.Tubo => "Tubo",
        ItemKind.Estrutura => "Estrutura",
        ItemKind.Solido => "Solido/malha",
        ItemKind.Bloco => "Bloco",
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
        ItemKind.Bloco => "bloco",
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
            WC.MenuItem mi = new() { Header = ex.ToString("0.#", CultureInfo.InvariantCulture) + "x", IsCheckable = true, IsChecked = Math.Abs((CompareMode ? _compareEx : _data?.Box.Exagero ?? _prefs.ExageroPadrao) - ex) < 1e-9 };
            double v = ex;
            mi.Click += (_, _) => SetExagero(v);
            menu.Items.Add(mi);
        }

        menu.IsOpen = true;
    }

    private void SetExagero(double ex)
    {
        if (CompareMode)
        {
            _compareEx = ex;
            _fitPending = true;
            RenderGraphic();
            BuildTabs();
            return;
        }

        _prefs.ExageroPadrao = ex;
        if (_category == "VISTA3D")
            _view3d.SetExagero(ex);
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

        // Sem REGEN: a polilinha alterada ja e redesenhada pelo AutoCAD (e o overrule com ela).
        UpdateCuts(new HashSet<AcadDb.ObjectId> { _cutId });
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
        if (_category == "COMPARAR")
            return BuildCompareProperties(form);
        if (_category == "VISTA3D")
            return Build3DProperties(form);
        if (_selectedItem is SectionItem it && _category is "CONTEUDO" or "CORTES" or "CORREDOR")
        {
            form.Children.Add(Label(KindName(it.Kind) + ": " + it.Name, true));
            WC.StackPanel styles = new() { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(0, 2, 0, 6) };
            foreach ((ItemStyle st, string text) in new[] { (ItemStyle.Normal, "Normal"), (ItemStyle.Destaque, "Destacar"), (ItemStyle.Hachura, "Hachurar"), (ItemStyle.Oculto, "Ocultar") })
            {
                bool on = _prefs.StyleOf(it) == st;
                WC.Button sb = new()
                {
                    Content = text,
                    Padding = new SW.Thickness(8, 2, 8, 2),
                    Margin = new SW.Thickness(0, 0, 4, 0),
                    Background = on ? T.Selected : T.Hover,
                    BorderBrush = on ? T.Accent : T.Line,
                    IsEnabled = st != ItemStyle.Hachura || it.Fills.Count > 0,
                    ToolTip = st == ItemStyle.Hachura && it.Fills.Count == 0 ? "Este item nao forma regiao fechada." : null
                };
                ItemStyle chosen = st;
                sb.Click += (_, _) => SetStyle(it, chosen);
                styles.Children.Add(sb);
            }

            form.Children.Add(styles);
            WC.Grid g = PropGrid();
            PropText(g, "Layer", it.Layer);
            PropText(g, "Posicao", it.Ghost ? $"na profundidade ({N(it.Depth)} m do plano)" : "cortado pelo plano");
            if (it.ZRange() is (double a, double b))
                PropText(g, "Elevacoes", $"{N(a, "0.000")} a {N(b, "0.000")}");
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
        Prop(grid, "Faixa de elevacao", autoZ);
        Prop(grid, "Elevacao minima", zmin);
        Prop(grid, "Elevacao maxima", zmax);
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
        WC.Button fromView = new() { Content = "Elevacoes da vista atual", Width = 150, Margin = new SW.Thickness(0, 0, 6, 0), ToolTip = "Fixa a faixa de elevacao no que esta visivel agora (depois do zoom)." };
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

    private SW.UIElement BuildCompareProperties(WC.StackPanel form)
    {
        form.Children.Add(Label("Comparacao de secoes", true));
        form.Children.Add(Label("Fixe secoes com o botao Fixar na comparacao (em Cortes ou Corredor por estaca). Clique duplo na linha mostra/oculta; botao direito: cor, remover."));

        WC.StackPanel align = new() { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(0, 4, 0, 4) };
        align.Children.Add(new WC.TextBlock { Text = "Alinhar pelo:", Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(0, 0, 8, 0) });
        foreach ((string key, string text) in new[] { ("Eixo", "eixo"), ("Centro", "centro"), ("Inicio", "inicio") })
        {
            bool on = _compareAlign == key;
            WC.Button b = new() { Content = text, Padding = new SW.Thickness(8, 2, 8, 2), Margin = new SW.Thickness(0, 0, 4, 0), Background = on ? T.Selected : T.Hover, BorderBrush = on ? T.Accent : T.Line };
            string k = key;
            b.Click += (_, _) => { _compareAlign = k; _fitPending = true; RenderGraphic(); BuildTabs(); };
            align.Children.Add(b);
        }

        form.Children.Add(align);
        WC.CheckBox ghosts = new() { Content = "Incluir o que esta na profundidade (fantasmas)", IsChecked = _prefs.CompararFantasmas, Foreground = T.Text, Margin = new SW.Thickness(0, 3, 0, 3) };
        ghosts.Click += (_, _) => { _prefs.CompararFantasmas = ghosts.IsChecked == true; SecaoStore.SavePrefs(_prefs); RenderGraphic(); };
        form.Children.Add(ghosts);
        form.Children.Add(Label($"Exagero vertical: {_compareEx:0.##}x (botao Exagero)"));

        if (_compareSel is CompareEntry sel)
        {
            form.Children.Add(Label("Selecionada", true));
            WC.Grid g = PropGrid();
            WC.TextBox name = new() { Text = sel.Label };
            name.LostFocus += (_, _) => { if (name.Text.Trim().Length > 0 && name.Text.Trim() != sel.Label) { sel.Label = name.Text.Trim(); FillList(); RenderGraphic(); } };
            Prop(g, "Nome", name);
            (double z0, double z1) = sel.Data.DataZ();
            PropText(g, "Comprimento", N(sel.Data.Length) + " m");
            PropText(g, "Elevacoes", $"{N(z0, "0.000")} a {N(z1, "0.000")}");
            PropText(g, "Eixo", double.IsNaN(sel.Data.AxisS) ? "(corte livre: alinha pelo centro)" : $"S = {N(sel.Data.AxisS)} m");
            form.Children.Add(g);
        }

        // Diferenca de topo entre as duas primeiras visiveis.
        List<CompareEntry> vis = _compare.Where(c => c.Visible).ToList();
        if (vis.Count >= 2)
        {
            (string text, _) = TopDifference(vis[0], vis[1]);
            form.Children.Add(Label("Diferenca de topo", true));
            form.Children.Add(new WC.TextBlock { Text = text, Foreground = T.Text, FontFamily = MonoFont, TextWrapping = SW.TextWrapping.Wrap, Margin = new SW.Thickness(0, 2, 0, 2) });
        }

        return form;
    }

    /// <summary>Compara o topo (maior elevacao) de duas secoes alinhadas, a cada 5 cm.</summary>
    private (string Text, double Max) TopDifference(CompareEntry a, CompareEntry b)
    {
        double oa = CompareOffset(a.Data), ob = CompareOffset(b.Data);
        double lo = Math.Max(oa, ob), hi = Math.Min(oa + a.Data.Length, ob + b.Data.Length);
        if (hi - lo < 0.05)
            return ("As duas secoes nao se sobrepoem no alinhamento escolhido.", 0);
        List<double> xs = new();
        for (double x = lo; x <= hi + 1e-9; x += 0.05)
            xs.Add(x);
        IEnumerable<List<(double S, double Z)>> Lines(CompareEntry e, double off) =>
            e.Data.Items.Where(i => !i.Ghost && _prefs.StyleOf(i) != ItemStyle.Oculto && i.Kind is ItemKind.Superficie or ItemKind.Corredor)
                .SelectMany(i => i.Polylines).Select(pl => pl.Select(p => (p.S + off, p.Z)).ToList());
        double[] ta = SecaoGeometry.TopAt(Lines(a, oa), xs), tb = SecaoGeometry.TopAt(Lines(b, ob), xs);
        double maxAbs = 0, at = 0, sum = 0;
        int n = 0;
        for (int i = 0; i < xs.Count; i++)
        {
            if (double.IsNaN(ta[i]) || double.IsNaN(tb[i]))
                continue;
            double d = tb[i] - ta[i];
            sum += d;
            n++;
            if (Math.Abs(d) > Math.Abs(maxAbs))
            {
                maxAbs = d;
                at = xs[i];
            }
        }

        if (n == 0)
            return ("Sem superficie/corredor em comum para comparar.", 0);
        return ($"{b.Label} - {a.Label}:\n  maior diferenca {maxAbs:+0.000;-0.000} m em S = {at:0.00}\n  media {sum / n:+0.000;-0.000} m em {n} pontos ({(n - 1) * 0.05:0.00} m)", maxAbs);
    }

    private SW.UIElement Build3DProperties(WC.StackPanel form)
    {
        form.Children.Add(Label("Vista 3D da caixa", true));
        form.Children.Add(Label("O conteudo da caixa corrente (corte ou estaca) recortado exatamente nela, como a section box do Model Viewer: superficies, corredor, solidos, corpos, malhas e blocos, tubos, estruturas, linhas e pontos. Com a faixa de elevacao fixada, recorta tambem em cima e embaixo."));
        form.Children.Add(Label("Arrastar: orbitar. Botao direito ou do meio: mover. Roda: zoom. Duplo clique: isometrica. Barra no canto: vistas prontas, estilo, perspectiva e imagem. Na lista: clique duplo mostra/oculta; botao direito isola."));

        form.Children.Add(Label("Estilo", true));
        WC.WrapPanel styles = new() { Margin = new SW.Thickness(0, 2, 0, 4) };
        foreach ((SecaoView3D.Style3D st, string text) in new[] { (SecaoView3D.Style3D.Sombreado, "Sombreado"), (SecaoView3D.Style3D.SombreadoArestas, "Com arestas"), (SecaoView3D.Style3D.Arestas, "So arestas"), (SecaoView3D.Style3D.RaioX, "Raio-X") })
        {
            bool on = _view3d.ViewStyle == st;
            WC.Button b = new() { Content = text, Padding = new SW.Thickness(8, 2, 8, 2), Margin = new SW.Thickness(0, 0, 4, 4), Background = on ? T.Selected : T.Hover, BorderBrush = on ? T.Accent : T.Line };
            SecaoView3D.Style3D chosen = st;
            b.Click += (_, _) => { _view3d.SetStyle(chosen); BuildTabs(); };
            styles.Children.Add(b);
        }

        form.Children.Add(styles);
        WC.CheckBox persp = new() { Content = "Perspectiva (desligado: ortogonal)", IsChecked = _view3d.Perspective, Foreground = T.Text, Margin = new SW.Thickness(0, 3, 0, 3) };
        persp.Click += (_, _) => _view3d.SetPerspective(persp.IsChecked == true);
        form.Children.Add(persp);

        WC.StackPanel buttons = new() { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(0, 8, 0, 0) };
        WC.Button copy = new() { Content = "Copiar imagem", Width = 110, Margin = new SW.Thickness(0, 0, 6, 0) };
        copy.Click += (_, _) => Image3D(false);
        WC.Button save = new() { Content = "Salvar PNG...", Width = 110, Margin = new SW.Thickness(0, 0, 6, 0) };
        save.Click += (_, _) => Image3D(true);
        WC.Button rebuild = new() { Content = "Refazer a cena", Width = 120 };
        rebuild.Click += (_, _) => { _scene3dDirty = true; Update3D(keepCamera: true); FillList(); BuildTabs(); };
        buttons.Children.Add(copy);
        buttons.Children.Add(save);
        buttons.Children.Add(rebuild);
        form.Children.Add(buttons);

        if (_view3d.Scene is Scene3D sc)
        {
            form.Children.Add(Label(sc.Summary(), true));
            if (_view3d.Hidden.Count > 0)
                form.Children.Add(Label($"{_view3d.Hidden.Count} objeto(s) oculto(s) (lista: botao direito > Mostrar todos)."));
            foreach (string w in sc.Warnings.Take(10))
                form.Children.Add(Label("- " + w));
        }

        return form;
    }

    private void Toggle3D(string key)
    {
        List<string> hidden = _view3d.Hidden.ToList();
        if (!hidden.Remove(key))
            hidden.Add(key);
        _view3d.SetHidden(hidden);
        FillList();
        BuildTabs();
    }

    /// <summary>Imagem da vista 3D: copia para a area de transferencia ou salva em PNG.</summary>
    private void Image3D(bool save)
    {
        try
        {
            WM.Imaging.BitmapSource? img = _view3d.Snapshot();
            if (img is null)
            {
                Say("Abra a Vista 3D para gerar a imagem.");
                return;
            }

            if (!save)
            {
                SW.Clipboard.SetImage(img);
                Say("Imagem da vista 3D copiada.");
                return;
            }

            string name = string.Concat((_data?.Box.Name ?? "secao").Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            Microsoft.Win32.SaveFileDialog dlg = new() { Filter = "Imagem PNG (*.png)|*.png", FileName = name + "-3d.png" };
            if (dlg.ShowDialog(this) != true)
                return;
            WM.Imaging.PngBitmapEncoder enc = new();
            enc.Frames.Add(WM.Imaging.BitmapFrame.Create(img));
            using (System.IO.FileStream fs = System.IO.File.Create(dlg.FileName))
                enc.Save(fs);
            Say("Imagem salva: " + dlg.FileName);
        }
        catch (System.Exception ex)
        {
            Say("Nao foi possivel gerar a imagem: " + ex.Message);
        }
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
        Add("Solidos 3D, corpos, superficies e malhas do AutoCAD", () => _prefs.Solidos, v => _prefs.Solidos = v);
        Add("Blocos com solidos/malhas (xrefs ficam de fora)", () => _prefs.Blocos, v => _prefs.Blocos = v);
        Add("Feature lines", () => _prefs.FeatureLines, v => _prefs.FeatureLines = v);
        Add("Polilinhas 3D", () => _prefs.Polilinhas3d, v => _prefs.Polilinhas3d = v);
        Add("Pontos COGO", () => _prefs.Pontos, v => _prefs.Pontos = v);

        // Faixa de vista por classe: so o corte / faixa da caixa / tudo.
        form.Children.Add(Label("Faixa de vista por elemento", true));
        form.Children.Add(Label("Corte: so o que cruza o plano. Faixa: o corte e o que esta na profundidade da caixa (projetado, apagado). Tudo: ignora a profundidade e projeta o que cai na largura da linha."));
        WC.Grid fg = new() { Margin = new SW.Thickness(0, 2, 0, 4) };
        fg.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(200) });
        for (int c = 0; c < 3; c++)
            fg.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(70) });
        int row = 0;
        foreach ((string cls, string title) in SecaoStore.Prefs.FaixaClasses)
        {
            fg.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
            WC.TextBlock name = new() { Text = title, Foreground = T.Text, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(0, 2, 6, 2) };
            WC.Grid.SetRow(name, row);
            fg.Children.Add(name);
            int col = 1;
            foreach ((string mode, string text) in new[] { ("corte", "Corte"), ("faixa", "Faixa"), ("tudo", "Tudo") })
            {
                WC.RadioButton rb = new() { Content = text, GroupName = "faixa-" + cls, IsChecked = _prefs.Faixa(cls) == mode, Foreground = T.Text, VerticalAlignment = SW.VerticalAlignment.Center };
                string c2 = cls, m2 = mode;
                rb.Checked += (_, _) =>
                {
                    if (_prefs.Faixa(c2) == m2)
                        return;
                    _prefs.Faixas[c2] = m2;
                    SecaoStore.SavePrefs(_prefs);
                    Recompute();
                };
                WC.Grid.SetRow(rb, row);
                WC.Grid.SetColumn(rb, col++);
                fg.Children.Add(rb);
            }

            row++;
        }

        form.Children.Add(fg);
        form.Children.Add(Label("A vista 3D fica sempre limitada a caixa."));

        form.Children.Add(Label("Vista", true));
        WC.CheckBox grade = new() { Content = "Grade", IsChecked = _prefs.Grade, Foreground = T.Text, Margin = new SW.Thickness(0, 3, 0, 3) };
        grade.Click += (_, _) => { _prefs.Grade = grade.IsChecked == true; SecaoStore.SavePrefs(_prefs); RenderGraphic(); };
        form.Children.Add(grade);
        form.Children.Add(Label("Textos: botao Textos, na barra abaixo da vista. Ctrl + arrastar move um rotulo; botao direito sobre ele volta a posicao automatica."));
        form.Children.Add(Label("Superficies individuais: categoria Configuracao (clique na linha para mostrar/ocultar)."));

        form.Children.Add(Label("Corredor", true));
        WC.StackPanel modes = new() { Margin = new SW.Thickness(0, 2, 0, 2) };
        WC.TextBox codes = new() { Text = _prefs.CorredorCodigos, Width = 220, HorizontalAlignment = SW.HorizontalAlignment.Left, Margin = new SW.Thickness(20, 2, 0, 4), ToolTip = "Codigos de link separados por virgula (ex.: TOP, DATUM, Pave1)." };
        foreach ((string key, string text) in new[] { ("links", "Todos os links"), ("contorno", "So o contorno (boundary)"), ("codigos", "So os links com os codigos:") })
        {
            WC.RadioButton rb = new() { Content = text, GroupName = "corredor", IsChecked = _prefs.CorredorModo == key, Foreground = T.Text, Margin = new SW.Thickness(0, 2, 0, 2) };
            string k = key;
            rb.Checked += (_, _) =>
            {
                if (_prefs.CorredorModo == k)
                    return;
                _prefs.CorredorModo = k;
                SecaoStore.SavePrefs(_prefs);
                Recompute();
            };
            modes.Children.Add(rb);
        }

        modes.Children.Add(codes);
        codes.LostFocus += (_, _) =>
        {
            if (codes.Text.Trim() == _prefs.CorredorCodigos)
                return;
            _prefs.CorredorCodigos = codes.Text.Trim();
            SecaoStore.SavePrefs(_prefs);
            if (_prefs.CorredorModo == "codigos")
                Recompute();
        };
        form.Children.Add(modes);
        form.Children.Add(Label("O contorno e sempre calculado: com ele o corredor pode ser hachurado (botao direito no corredor > Hachurar), mesmo mostrando os links."));

        form.Children.Add(Label("Desenhar no model", true));
        WC.Grid g = PropGrid();
        WC.TextBox scale = new() { Text = N(_prefs.EscalaDesenho, "0"), Width = 90, HorizontalAlignment = SW.HorizontalAlignment.Left };
        WC.TextBox mm = new() { Text = N(_prefs.TextoMm, "0.0#"), Width = 90, HorizontalAlignment = SW.HorizontalAlignment.Left };
        Prop(g, "Escala do desenho 1:", scale);
        Prop(g, "Texto no papel [mm]", mm);
        form.Children.Add(g);
        void SaveExport()
        {
            if (TryD(scale.Text, out double n) && n >= 1) _prefs.EscalaDesenho = n;
            if (TryD(mm.Text, out double h) && h >= 0.5) _prefs.TextoMm = h;
            SecaoStore.SavePrefs(_prefs);
        }

        scale.LostFocus += (_, _) => SaveExport();
        mm.LostFocus += (_, _) => SaveExport();
        form.Children.Add(Label($"Altura do texto no desenho = mm x escala / 1000 (hoje {_prefs.TextoMm * _prefs.EscalaDesenho / 1000:0.###} m). O passo das elevacoes e das distancias se ajusta para os textos nao se sobreporem."));
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

    /// <summary>Uma secao desenhada na vista (normal: a corrente; comparacao: as fixadas, sobrepostas).</summary>
    private sealed class ViewLayer
    {
        public SectionData Data = null!;
        public double Offset;
        public WM.Color? Color;
        public string Label = "";
        public bool Ghosts = true;
        public CompareEntry? Entry;
    }

    private bool CompareMode => _category == "COMPARAR";

    private List<ViewLayer> Layers()
    {
        if (CompareMode)
        {
            return _compare.Where(c => c.Visible).Select(c => new ViewLayer
            {
                Data = c.Data,
                Offset = CompareOffset(c.Data),
                Color = c.Color,
                Label = c.Label,
                Ghosts = _prefs.CompararFantasmas,
                Entry = c
            }).ToList();
        }

        return _data is null ? new List<ViewLayer>() : new List<ViewLayer> { new() { Data = _data } };
    }

    private double CompareOffset(SectionData d) => _compareAlign switch
    {
        "Inicio" => 0,
        "Centro" => -d.Length / 2,
        _ => double.IsNaN(d.AxisS) ? -d.Length / 2 : -d.AxisS
    };

    private double Ex => CompareMode ? _compareEx : _data?.Box.Exagero ?? 1.0;

    private static (double Min, double Max) LayersZ(List<ViewLayer> layers)
    {
        if (layers.Count == 1)
            return layers[0].Data.ViewZ();
        double min = double.MaxValue, max = double.MinValue;
        foreach (ViewLayer l in layers)
        {
            (double a, double b) = l.Data.ViewZ();
            min = Math.Min(min, a);
            max = Math.Max(max, b);
        }

        return min <= max ? (min, max) : (0, 10);
    }

    private static (double Min, double Max) LayersS(List<ViewLayer> layers)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (ViewLayer l in layers)
        {
            min = Math.Min(min, l.Offset);
            max = Math.Max(max, l.Offset + l.Data.Length);
        }

        return min <= max ? (min, max) : (0, 1);
    }

    private SW.Point ToScreen(double s, double z) => new(_ox + (s * _scale), _oy - (z * _scale * Ex));

    private (double S, double Z) ToWorld(SW.Point p) => ((p.X - _ox) / _scale, (_oy - p.Y) / (_scale * Ex));

    private void Fit()
    {
        List<ViewLayer> layers = Layers();
        if (layers.Count == 0)
            return;
        double w = _graphic.ActualWidth - MarginLeft - MarginRight, h = _graphic.ActualHeight - MarginTop - MarginBottom;
        if (w < 20 || h < 20)
            return;
        (double zmin, double zmax) = LayersZ(layers);
        (double smin, double smax) = LayersS(layers);
        double length = Math.Max(smax - smin, 1e-6);
        double sx = w / length, sy = h / Math.Max((zmax - zmin) * Ex, 1e-6);
        _scale = Math.Min(sx, sy) * 0.97;
        double cw = length * _scale, ch = (zmax - zmin) * Ex * _scale;
        _ox = MarginLeft + ((w - cw) / 2) - (smin * _scale);
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
        if (Layers().Count == 0)
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

        SW.Point p = e.GetPosition(_graphic);

        // Ctrl + arrastar sobre um rotulo: move o rotulo (deslocamento salvo por secao).
        if (e.ChangedButton == WI.MouseButton.Left && (WI.Keyboard.Modifiers & WI.ModifierKeys.Control) != 0 && LabelAt(p) is LabelHit lh)
        {
            _dragLabel = lh;
            _dragLabelStart = p;
            if (_prefs.Deslocamentos.TryGetValue(lh.Key, out (double DS, double DZ) d0))
                _dragLabelOffset0 = d0;
            else
            {
                (double ws, double wz) = ToWorld(new SW.Point(lh.Rect.Left, lh.Rect.Bottom));
                _dragLabelOffset0 = (ws - lh.S, wz - lh.Z);
            }

            _graphic.CaptureMouse();
            return;
        }

        (ViewLayer Layer, SectionItem Item)? hit = HitTest(p);

        if (e.ChangedButton == WI.MouseButton.Right)
        {
            if (LabelAt(p) is LabelHit rl)
                OpenLabelMenu(rl);
            else if (hit is not null)
                OpenStyleMenu(hit.Value.Item);
            return;
        }

        if (e.ChangedButton == WI.MouseButton.Left && hit is not null)
        {
            _selectedItem = hit.Value.Item;
            if (hit.Value.Layer.Entry is CompareEntry ce)
                _compareSel = ce;
            if (_category is "CONTEUDO" or "COMPARAR")
                FillList();
            BuildTabs();
            RenderGraphic();
        }

        _dragging = true;
        _dragStart = p;
        _dragOrigin = (_ox, _oy);
        _graphic.CaptureMouse();
    }

    private void OnMouseMove(object sender, WI.MouseEventArgs e)
    {
        SW.Point p = e.GetPosition(_graphic);
        if (_dragLabel is LabelHit dl)
        {
            if (e.LeftButton != WI.MouseButtonState.Pressed)
            {
                EndLabelDrag();
                return;
            }

            double ds = (p.X - _dragLabelStart.X) / _scale, dz = -(p.Y - _dragLabelStart.Y) / (_scale * Ex);
            _prefs.Deslocamentos[dl.Key] = (_dragLabelOffset0.DS + ds, _dragLabelOffset0.DZ + dz);
            RenderGraphic();
            return;
        }

        bool ctrl = (WI.Keyboard.Modifiers & WI.ModifierKeys.Control) != 0;
        _graphic.Cursor = ctrl && !_dragging && LabelAt(p) is not null ? WI.Cursors.SizeAll : null;
        if (_dragging && (e.LeftButton == WI.MouseButtonState.Pressed || e.MiddleButton == WI.MouseButtonState.Pressed))
        {
            _ox = _dragOrigin.ox + (p.X - _dragStart.X);
            _oy = _dragOrigin.oy + (p.Y - _dragStart.Y);
            RenderGraphic();
            return;
        }

        if (Layers().Count == 0)
            return;
        (double s, double z) = ToWorld(p);
        (ViewLayer Layer, SectionItem Item)? hit = HitTest(p);
        string info = $"S {s,8:0.00} m   Z {z,9:0.000}";
        if (hit is (ViewLayer hl, SectionItem hi))
            info += $"   |  {(hl.Label.Length > 0 ? hl.Label + ": " : "")}{KindName(hi.Kind)}: {hi.Name}" + (hi.Ghost ? $" (a {N(hi.Depth)} m do plano)" : "") + "   (botao direito: destacar/hachurar)";
        _graphicInfo.Text = info;
    }

    private (ViewLayer Layer, SectionItem Item)? HitTest(SW.Point p)
    {
        (ViewLayer, SectionItem)? best = null;
        double bestD = 7.0;
        (double ws, double wz) = ToWorld(p);
        double tolS = 8.0 / Math.Max(_scale, 1e-9), tolZ = 8.0 / Math.Max(_scale * Ex, 1e-9);
        foreach (ViewLayer layer in Layers())
        {
            double off = layer.Offset;
            foreach (SectionItem it in layer.Data.Items)
            {
                if ((it.Ghost && !layer.Ghosts) || _prefs.StyleOf(it) == ItemStyle.Oculto)
                    continue;
                SW.Rect bb = GeomOf(it).Bounds;
                if (bb.IsEmpty || ws < bb.Left + off - tolS || ws > bb.Right + off + tolS || wz < bb.Top - tolZ || wz > bb.Bottom + tolZ)
                    continue;
                foreach (List<(double S, double Z)> pl in it.Polylines)
                {
                    for (int i = 0; i + 1 < pl.Count; i++)
                    {
                        double d = DistToSegment(p, ToScreen(pl[i].S + off, pl[i].Z), ToScreen(pl[i + 1].S + off, pl[i + 1].Z));
                        if (d < bestD)
                        {
                            bestD = d;
                            best = (layer, it);
                        }
                    }
                }

                foreach ((double s, double z, double r) in it.Circles)
                {
                    SW.Point c = ToScreen(s + off, z);
                    double rx = r * _scale, ry = r * _scale * Ex;
                    double d = Math.Abs(Math.Sqrt(Math.Pow((p.X - c.X) / Math.Max(rx, 1), 2) + Math.Pow((p.Y - c.Y) / Math.Max(ry, 1), 2)) - 1) * Math.Min(rx, ry);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = (layer, it);
                    }
                }

                foreach ((double s, double z, _) in it.Points)
                {
                    SW.Point c = ToScreen(s + off, z);
                    double d = Math.Sqrt(Math.Pow(p.X - c.X, 2) + Math.Pow(p.Y - c.Y, 2));
                    if (d < bestD)
                    {
                        bestD = d;
                        best = (layer, it);
                    }
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
            ItemKind.Bloco => 6,
            ItemKind.FeatureLine => 6,
            ItemKind.Polilinha3d => 5,
            _ => 7
        };
    }

    private static WM.Color RgbFor(SectionItem it) => ColorFor(it) switch
    {
        1 => WM.Color.FromRgb(235, 80, 80),
        2 => WM.Color.FromRgb(230, 220, 60),
        3 => WM.Color.FromRgb(90, 210, 110),
        4 => WM.Color.FromRgb(80, 200, 230),
        5 => WM.Color.FromRgb(110, 150, 255),
        6 => WM.Color.FromRgb(230, 110, 230),
        30 => WM.Color.FromRgb(255, 160, 60),
        _ => WM.Color.FromRgb(225, 225, 225)
    };

    private static readonly WM.Color GhostColor = WM.Color.FromRgb(120, 124, 130);
    private static readonly WM.Color SelectColor = WM.Color.FromRgb(255, 176, 0);
    private readonly Dictionary<uint, WM.Brush> _brushes = new();
    private readonly Dictionary<uint, WM.Brush> _hatches = new();

    private WM.Brush Solid(WM.Color c, byte a = 255)
    {
        uint key = ((uint)a << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        if (!_brushes.TryGetValue(key, out WM.Brush? b))
            _brushes[key] = b = ToolTheme.Brush(c.R, c.G, c.B, a);
        return b;
    }

    /// <summary>Hachura diagonal (tela) na cor dada, com fundo translucido.</summary>
    private WM.Brush Hatch(WM.Color c)
    {
        uint key = ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        if (_hatches.TryGetValue(key, out WM.Brush? cached))
            return cached;
        WM.DrawingGroup dg = new();
        dg.Children.Add(new WM.GeometryDrawing(new WM.SolidColorBrush(WM.Color.FromArgb(45, c.R, c.G, c.B)), null, new WM.RectangleGeometry(new SW.Rect(0, 0, 8, 8))));
        WM.GeometryGroup lines = new();
        lines.Children.Add(new WM.LineGeometry(new SW.Point(0, 8), new SW.Point(8, 0)));
        lines.Children.Add(new WM.LineGeometry(new SW.Point(-2, 2), new SW.Point(2, -2)));
        lines.Children.Add(new WM.LineGeometry(new SW.Point(6, 10), new SW.Point(10, 6)));
        dg.Children.Add(new WM.GeometryDrawing(null, new WM.Pen(new WM.SolidColorBrush(WM.Color.FromArgb(210, c.R, c.G, c.B)), 1.0), lines));
        WM.DrawingBrush brush = new(dg)
        {
            TileMode = WM.TileMode.Tile,
            Viewport = new SW.Rect(0, 0, 8, 8),
            ViewportUnits = WM.BrushMappingMode.Absolute,
            Viewbox = new SW.Rect(0, 0, 8, 8),
            ViewboxUnits = WM.BrushMappingMode.Absolute
        };
        brush.Freeze();
        _hatches[key] = brush;
        return brush;
    }

    /// <summary>Geometria de um item em coordenadas do mundo (S, Z), montada uma vez e congelada.</summary>
    private sealed class ItemGeom
    {
        public WM.Geometry? Lines;
        public WM.Geometry? Circles;
        public WM.Geometry? Fills;
        public bool FillsBuilt;
        /// <summary>Envolvente em (S, Z) do mundo, para descartar rapido no teste do cursor.</summary>
        public SW.Rect Bounds = SW.Rect.Empty;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SectionItem, ItemGeom> _geom = new();

    private ItemGeom GeomOf(SectionItem it)
    {
        if (_geom.TryGetValue(it, out ItemGeom? g))
            return g;
        g = new ItemGeom();
        if (it.Polylines.Any(p => p.Count > 1))
            g.Lines = Stream(it.Polylines, closed: false);
        foreach (List<(double S, double Z)> pl in it.Polylines)
            foreach ((double s, double z) in pl)
                g.Bounds.Union(new SW.Point(s, z));
        foreach ((double s, double z, double r) in it.Circles)
            g.Bounds.Union(new SW.Rect(s - r, z - r, 2 * r, 2 * r));
        foreach ((double s, double z, _) in it.Points)
            g.Bounds.Union(new SW.Point(s, z));
        if (it.Circles.Count > 0)
        {
            WM.GeometryGroup cg = new();
            foreach ((double s, double z, double r) in it.Circles)
                cg.Children.Add(new WM.EllipseGeometry(new SW.Point(s, z), r, r));
            cg.Freeze();
            g.Circles = cg;
        }

        _geom.Add(it, g);
        return g;
    }

    private static WM.Geometry Stream(List<List<(double S, double Z)>> polylines, bool closed)
    {
        WM.StreamGeometry sg = new() { FillRule = WM.FillRule.EvenOdd };
        using (WM.StreamGeometryContext ctx = sg.Open())
        {
            foreach (List<(double S, double Z)> pl in polylines)
            {
                if (pl.Count < 2)
                    continue;
                ctx.BeginFigure(new SW.Point(pl[0].S, pl[0].Z), closed, closed);
                for (int i = 1; i < pl.Count; i++)
                    ctx.LineTo(new SW.Point(pl[i].S, pl[i].Z), true, true);
            }
        }

        sg.Freeze();
        return sg;
    }

    private static WM.Geometry Placed(WM.Geometry g, WM.Transform tf)
    {
        WM.GeometryGroup group = new() { Transform = tf };
        group.Children.Add(g);
        return group;
    }

    private void RenderGraphic()
    {
        _graphic.Children.Clear();
        _labels.Clear();
        double W = _graphic.ActualWidth, H = _graphic.ActualHeight;
        if (W < 10 || H < 10)
            return;

        List<ViewLayer> layers = Layers();
        if (layers.Count == 0)
        {
            string msg = CompareMode
                ? "Nada para comparar: em Cortes ou Corredor por estaca, use Fixar na comparacao para guardar a secao mostrada."
                : _cuts.Count == 0 ? "Sem caixa de corte. Use Nova caixa de corte: clique os pontos em planta e informe as profundidades." : "Selecione um corte na lista.";
            _graphic.Children.Add(Text(msg, 12, H / 2 - 8, ToolTheme.GraphicText, 12));
            return;
        }

        if (_fitPending)
            Fit();

        (double zmin, double zmax) = LayersZ(layers);
        (double smin, double smax) = LayersS(layers);

        WC.Canvas plot = new() { ClipToBounds = true, Width = Math.Max(1, W - MarginLeft - MarginRight), Height = Math.Max(1, H - MarginTop - MarginBottom) };
        WC.Canvas.SetLeft(plot, MarginLeft);
        WC.Canvas.SetTop(plot, MarginTop);
        _graphic.Children.Add(plot);
        SW.Point P(double s, double z)
        {
            SW.Point q = ToScreen(s, z);
            return new SW.Point(q.X - MarginLeft, q.Y - MarginTop);
        }

        // Faixa util (elevacao e comprimento) levemente mais clara.
        SW.Point c0 = P(smin, zmax), c1 = P(smax, zmin);
        plot.Children.Add(new WS.Rectangle
        {
            Width = Math.Max(0, c1.X - c0.X), Height = Math.Max(0, c1.Y - c0.Y),
            Fill = ToolTheme.Brush(64, 66, 71), Stroke = ToolTheme.Brush(110, 112, 118), StrokeThickness = 1
        }.At(c0.X, c0.Y));

        // Grade num unico Path.
        double zStep = SecaoGeometry.NiceStep(60.0 / Math.Max(_scale * Ex, 1e-9));
        double sStep = SecaoGeometry.NiceStep(80.0 / Math.Max(_scale, 1e-9));
        (double vzLo, double vzHi) = VisibleZ();
        (double vsLo, _) = ToWorld(new SW.Point(MarginLeft, 0));
        (double vsHi, _) = ToWorld(new SW.Point(W - MarginRight, 0));
        if (_prefs.Grade)
        {
            WM.StreamGeometry grid = new();
            using (WM.StreamGeometryContext ctx = grid.Open())
            {
                for (double z = Math.Floor(vzLo / zStep) * zStep; z <= vzHi; z += zStep)
                {
                    ctx.BeginFigure(P(vsLo, z), false, false);
                    ctx.LineTo(P(vsHi, z), true, false);
                }

                for (double s = Math.Floor(vsLo / sStep) * sStep; s <= vsHi; s += sStep)
                {
                    ctx.BeginFigure(P(s, vzLo), false, false);
                    ctx.LineTo(P(s, vzHi), true, false);
                }
            }

            grid.Freeze();
            plot.Children.Add(new WS.Path { Data = grid, Stroke = ToolTheme.Brush(78, 80, 86), StrokeThickness = 1 });
        }

        // Eixo (secoes na estaca / comparacao alinhada pelo eixo).
        if (CompareMode && _compareAlign == "Eixo" || (!CompareMode && layers.Count == 1 && !double.IsNaN(layers[0].Data.AxisS)))
        {
            double ax = CompareMode ? 0 : layers[0].Data.AxisS;
            SW.Point a = P(ax, vzLo), b = P(ax, vzHi);
            plot.Children.Add(new WS.Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = ToolTheme.Brush(200, 90, 90), StrokeThickness = 1, StrokeDashArray = new WM.DoubleCollection { 8, 3, 2, 3 } });
        }

        // Itens: por camada; fantasmas primeiro (ja ordenados), selecionado por ultimo.
        List<SW.Rect> placed = new();
        foreach (ViewLayer layer in layers)
        {
            WM.MatrixTransform tf = new(new WM.Matrix(_scale, 0, 0, -_scale * Ex, _ox + (layer.Offset * _scale) - MarginLeft, _oy - MarginTop));
            tf.Freeze();
            IEnumerable<SectionItem> items = layer.Data.Items.Where(i => layer.Ghosts || !i.Ghost);
            foreach (SectionItem it in items.Where(i => !ReferenceEquals(i, _selectedItem)))
                DrawItem(plot, layer, it, false, tf, P, placed);
            if (_selectedItem is not null && layer.Data.Items.Contains(_selectedItem))
                DrawItem(plot, layer, _selectedItem, true, tf, P, placed);
        }

        // Reguas: elevacoes a esquerda, distancias embaixo.
        WM.Brush ruler = ToolTheme.Brush(44, 46, 50);
        _graphic.Children.Add(new WS.Rectangle { Width = MarginLeft - 2, Height = H, Fill = ruler }.At(0, 0));
        _graphic.Children.Add(new WS.Rectangle { Width = W, Height = MarginBottom - 2, Fill = ruler }.At(0, H - MarginBottom + 2));
        bool reguas = _prefs.Texto("reguas");
        for (double z = Math.Floor(vzLo / zStep) * zStep; reguas && z <= vzHi; z += zStep)
        {
            SW.Point a = ToScreen(0, z);
            if (a.Y < MarginTop || a.Y > H - MarginBottom)
                continue;
            _graphic.Children.Add(new WS.Line { X1 = MarginLeft - 6, Y1 = a.Y, X2 = MarginLeft - 2, Y2 = a.Y, Stroke = ToolTheme.GraphicText, StrokeThickness = 1 });
            _graphic.Children.Add(Text(z.ToString(zStep < 1 ? "0.00" : "0.0", CultureInfo.InvariantCulture), 4, a.Y - 7, ToolTheme.GraphicText, 10, MarginLeft - 12, SW.TextAlignment.Right));
        }

        for (double s = Math.Floor(vsLo / sStep) * sStep; reguas && s <= vsHi; s += sStep)
        {
            SW.Point a = ToScreen(s, 0);
            if (a.X < MarginLeft || a.X > W - MarginRight)
                continue;
            _graphic.Children.Add(new WS.Line { X1 = a.X, Y1 = H - MarginBottom + 2, X2 = a.X, Y2 = H - MarginBottom + 6, Stroke = ToolTheme.GraphicText, StrokeThickness = 1 });
            _graphic.Children.Add(Text(s.ToString(sStep < 1 ? "0.0" : "0", CultureInfo.InvariantCulture), a.X - 30, H - MarginBottom + 8, ToolTheme.GraphicText, 10, 60, SW.TextAlignment.Center));
        }

        if (CompareMode)
        {
            string align = _compareAlign == "Inicio" ? "pelo inicio" : _compareAlign == "Centro" ? "pelo centro" : "pelo eixo";
            if (_prefs.Texto("titulo"))
                _graphic.Children.Add(Text($"Comparacao: {layers.Count} secao(oes), alinhadas {align}   exagero {Ex:0.##}x", MarginLeft + 6, 2, ToolTheme.GraphicText, 10));
            double ly = MarginTop + 8;
            foreach (ViewLayer l in _prefs.Texto("legenda") ? layers : new List<ViewLayer>())
            {
                WM.Color c = l.Color ?? Colors[0];
                _graphic.Children.Add(new WS.Rectangle { Width = 14, Height = 4, Fill = Solid(c), Stroke = ReferenceEquals(l.Entry, _compareSel) ? Solid(SelectColor) : null }.At(MarginLeft + 10, ly + 5));
                _graphic.Children.Add(Text(l.Label, MarginLeft + 30, ly, Solid(c), 10));
                ly += 14;
            }
        }
        else if (_prefs.Texto("titulo"))
        {
            _graphic.Children.Add(Text($"{layers[0].Data.Box.Name}   exagero {Ex:0.##}x   1 m = {_scale:0.#} px", MarginLeft + 6, 2, ToolTheme.GraphicText, 10));
            _graphic.Children.Add(Text("esq. (inicio da linha)", MarginLeft + 6, H - MarginBottom - 16, ToolTheme.Brush(150, 152, 156), 9));
            _graphic.Children.Add(Text("dir. (fim da linha)", W - MarginRight - 110, H - MarginBottom - 16, ToolTheme.Brush(150, 152, 156), 9, 104, SW.TextAlignment.Right));
        }
    }

    private void DrawItem(WC.Canvas plot, ViewLayer layer, SectionItem it, bool selected, WM.Transform tf, Func<double, double, SW.Point> P0, List<SW.Rect> placed)
    {
        ItemStyle style = _prefs.StyleOf(it);
        if (style == ItemStyle.Oculto && !selected)
            return;

        double off = layer.Offset;
        SW.Point P(double s, double z) => P0(s + off, z);
        bool strong = style == ItemStyle.Destaque;
        WM.Color baseColor = layer.Color ?? (it.Ghost && !strong ? GhostColor : RgbFor(it));
        WM.Color color = selected ? SelectColor : baseColor;
        WM.Brush brush = Solid(color, (byte)(layer.Color is not null && it.Ghost && !strong ? 120 : 255));
        double thick = selected ? 2.6 : strong ? 3.0 : it.Ghost ? 1.0 : it.Kind == ItemKind.Superficie ? 1.8 : 1.4;
        bool dashed = it.Ghost && !strong;
        ItemGeom g = GeomOf(it);

        if (style == ItemStyle.Hachura && !g.FillsBuilt)
        {
            // Regioes so quando a hachura e pedida (o contorno de fantasmas e calculado aqui).
            g.FillsBuilt = true;
            g.Fills = it.Fills.Count > 0 ? Stream(it.Fills, closed: true) : null;
        }

        if (style == ItemStyle.Hachura && g.Fills is not null)
            plot.Children.Add(new WS.Path { Data = Placed(g.Fills, tf), Fill = Hatch(layer.Color ?? RgbFor(it)), Stroke = Solid(layer.Color ?? RgbFor(it), 160), StrokeThickness = 0.8 });

        foreach (WM.Geometry? geom in new[] { g.Lines, g.Circles })
        {
            if (geom is null)
                continue;
            if (strong && !selected)
                plot.Children.Add(new WS.Path { Data = Placed(geom, tf), Stroke = Solid(color, 70), StrokeThickness = thick + 5, StrokeLineJoin = WM.PenLineJoin.Round });
            WS.Path path = new() { Data = Placed(geom, tf), Stroke = brush, StrokeThickness = thick, StrokeLineJoin = WM.PenLineJoin.Round };
            if (dashed)
                path.StrokeDashArray = new WM.DoubleCollection { 4, 3 };
            plot.Children.Add(path);
        }

        foreach ((double s, double z, _) in it.Points)
        {
            SW.Point c = P(s, z);
            const double m = 4;
            plot.Children.Add(new WS.Line { X1 = c.X - m, Y1 = c.Y - m, X2 = c.X + m, Y2 = c.Y + m, Stroke = brush, StrokeThickness = thick });
            plot.Children.Add(new WS.Line { X1 = c.X - m, Y1 = c.Y + m, X2 = c.X + m, Y2 = c.Y - m, Stroke = brush, StrokeThickness = thick });
        }

        // Rotulos conforme o menu Textos (mesma regra do desenho).
        foreach (LabelSpec spec in SecaoLabels.For(it, layer.Data.Box.Name, _prefs.Texto))
        {
            bool atPoint = SecaoLabelsPointSuffix(spec.Key).Length > 0;
            PlaceLabel(plot, placed, spec, off, brush, atPoint ? 5 : 4, atPoint ? -14 : -7);
        }
    }

    /// <summary>Sufixo "|pN" das chaves de rotulo de ponto ("" se nao for de ponto).</summary>
    private static string SecaoLabelsPointSuffix(string key)
    {
        int bar = key.LastIndexOf('|');
        return bar >= 0 && bar + 1 < key.Length && key[bar + 1] == 'p' ? key[bar..] : "";
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

    // ------------------------------------------------------------------
    // Textos (menu) e rotulos arrastaveis (Ctrl)
    // ------------------------------------------------------------------

    /// <summary>Rotulo desenhado na vista: retangulo (coordenadas da vista), chave e ancoragem no mundo.</summary>
    private sealed class LabelHit
    {
        public SW.Rect Rect;
        public string Key = "";
        public double S, Z;
        public bool Moved;
    }

    private readonly List<LabelHit> _labels = new();
    private LabelHit? _dragLabel;
    private SW.Point _dragLabelStart;
    private (double DS, double DZ) _dragLabelOffset0;

    private void OpenTextMenu()
    {
        WC.ContextMenu menu = new() { PlacementTarget = _textButton, Placement = WC.Primitives.PlacementMode.Top };
        foreach ((string key, string title) in SecaoStore.Prefs.TextKinds)
        {
            WC.MenuItem mi = new() { Header = title, IsCheckable = true, IsChecked = _prefs.Texto(key), StaysOpenOnClick = true };
            string k = key;
            mi.Click += (_, _) =>
            {
                if (mi.IsChecked)
                    _prefs.Textos.Add(k);
                else
                    _prefs.Textos.Remove(k);
                SecaoStore.SavePrefs(_prefs);
                RenderGraphic();
            };
            menu.Items.Add(mi);
        }

        menu.Items.Add(new WC.Separator());
        WC.MenuItem all = new() { Header = "Todos" };
        all.Click += (_, _) => { foreach ((string key, _) in SecaoStore.Prefs.TextKinds) _prefs.Textos.Add(key); SecaoStore.SavePrefs(_prefs); RenderGraphic(); };
        menu.Items.Add(all);
        WC.MenuItem none = new() { Header = "Nenhum" };
        none.Click += (_, _) => { _prefs.Textos.Clear(); SecaoStore.SavePrefs(_prefs); RenderGraphic(); };
        menu.Items.Add(none);
        menu.Items.Add(new WC.Separator());
        string? section = CompareMode ? null : _data?.Box.Name;
        int moved = section is null ? 0 : _prefs.Deslocamentos.Keys.Count(k => k.StartsWith(section + "|", StringComparison.Ordinal));
        WC.MenuItem reset = new() { Header = moved > 0 ? $"Voltar os rotulos movidos desta secao ({moved})" : "Voltar os rotulos movidos desta secao", IsEnabled = moved > 0 };
        reset.Click += (_, _) => ResetLabels(section);
        menu.Items.Add(reset);
        WC.MenuItem hint = new() { Header = "Ctrl + arrastar move um rotulo", IsEnabled = false };
        menu.Items.Add(hint);
        menu.IsOpen = true;
    }

    private void ResetLabels(string? section)
    {
        if (section is null)
            return;
        foreach (string k in _prefs.Deslocamentos.Keys.Where(k => k.StartsWith(section + "|", StringComparison.Ordinal)).ToList())
            _prefs.Deslocamentos.Remove(k);
        SecaoStore.SavePrefs(_prefs);
        RenderGraphic();
    }

    private LabelHit? LabelAt(SW.Point p)
    {
        for (int i = _labels.Count - 1; i >= 0; i--)
        {
            SW.Rect r = _labels[i].Rect;
            r.Inflate(2, 2);
            if (r.Contains(p))
                return _labels[i];
        }

        return null;
    }

    private void EndLabelDrag()
    {
        if (_dragLabel is null)
            return;
        _dragLabel = null;
        SecaoStore.SavePrefs(_prefs);
    }

    private void OpenLabelMenu(LabelHit hit)
    {
        WC.ContextMenu menu = new();
        WC.MenuItem back = new() { Header = "Voltar o rotulo para a posicao automatica", IsEnabled = hit.Moved };
        back.Click += (_, _) => { _prefs.Deslocamentos.Remove(hit.Key); SecaoStore.SavePrefs(_prefs); RenderGraphic(); };
        menu.Items.Add(back);
        string section = hit.Key.Split('|')[0];
        WC.MenuItem all = new() { Header = "Voltar todos os rotulos desta secao" };
        all.Click += (_, _) => ResetLabels(section);
        menu.Items.Add(all);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Coloca um rotulo: no deslocamento salvo (com linha de chamada ate a ancoragem) ou,
    /// sem ele, ao lado da ancoragem subindo uma linha por vez ate nao sobrepor (ate 6).
    /// </summary>
    private void PlaceLabel(WC.Canvas plot, List<SW.Rect> placed, LabelSpec spec, double off, WM.Brush brush, double autoDx, double autoDy)
    {
        double w = (spec.Text.Length * 5.6) + 2, h = 11;
        SW.Point anchor = ToScreen(spec.S + off, spec.Z);
        SW.Rect r;
        bool moved = _prefs.Deslocamentos.TryGetValue(spec.Key, out (double DS, double DZ) d);
        if (moved)
        {
            SW.Point bl = ToScreen(spec.S + off + d.DS, spec.Z + d.DZ);
            r = new SW.Rect(bl.X, bl.Y - h, w, h);
        }
        else
        {
            r = new SW.Rect(anchor.X + autoDx, anchor.Y + autoDy, w, h);
            for (int i = 0; i < 6 && placed.Any(q => q.IntersectsWith(r)); i++)
                r.Y -= h + 1;
        }

        placed.Add(r);
        _labels.Add(new LabelHit { Rect = r, Key = spec.Key, S = spec.S + off, Z = spec.Z, Moved = moved });
        if (moved)
        {
            // Linha de chamada: da ancoragem ao lado mais perto do texto.
            double lx = Math.Abs(anchor.X - r.Left) < Math.Abs(anchor.X - r.Right) ? r.Left : r.Right;
            double ly = Math.Clamp(anchor.Y, r.Top, r.Bottom);
            if (Math.Abs(lx - anchor.X) + Math.Abs(ly - anchor.Y) > 6)
                plot.Children.Add(new WS.Line { X1 = anchor.X - MarginLeft, Y1 = anchor.Y - MarginTop, X2 = lx - MarginLeft, Y2 = (r.Top + (h / 2)) - MarginTop, Stroke = brush, StrokeThickness = 0.7, Opacity = 0.8 });
        }

        plot.Children.Add(Text(spec.Text, r.X - MarginLeft, r.Y - MarginTop, brush, 9));
    }

    // ------------------------------------------------------------------
    // Estilo por item (destacar / hachurar / ocultar)
    // ------------------------------------------------------------------

    private void OpenStyleMenu(SectionItem it)
    {
        WC.ContextMenu menu = new();
        menu.Items.Add(new WC.MenuItem { Header = KindName(it.Kind) + ": " + it.Name, IsEnabled = false });
        menu.Items.Add(new WC.Separator());
        ItemStyle current = _prefs.StyleOf(it);
        foreach ((ItemStyle st, string text) in new[] { (ItemStyle.Normal, "Normal"), (ItemStyle.Destaque, "Destacar"), (ItemStyle.Hachura, it.Fills.Count > 0 ? "Hachurar" : "Hachurar (sem regiao fechada)"), (ItemStyle.Oculto, "Ocultar") })
        {
            WC.MenuItem mi = new() { Header = text, IsCheckable = true, IsChecked = current == st, IsEnabled = st != ItemStyle.Hachura || it.Fills.Count > 0 };
            ItemStyle chosen = st;
            mi.Click += (_, _) => SetStyle(it, chosen);
            menu.Items.Add(mi);
        }

        if (_prefs.Estilos.Count > 0)
        {
            menu.Items.Add(new WC.Separator());
            WC.MenuItem reset = new() { Header = "Limpar destaques e hachuras de todos" };
            reset.Click += (_, _) =>
            {
                _prefs.Estilos.Clear();
                SecaoStore.SavePrefs(_prefs);
                RenderGraphic();
                FillList();
            };
            menu.Items.Add(reset);
        }

        menu.IsOpen = true;
    }

    private void SetStyle(SectionItem it, ItemStyle st)
    {
        if (st == ItemStyle.Normal)
            _prefs.Estilos.Remove(it.Key);
        else
            _prefs.Estilos[it.Key] = st;
        SecaoStore.SavePrefs(_prefs);
        RenderGraphic();
        if (_category is "CONTEUDO")
            FillList();
        BuildTabs();
    }

    private static string StyleName(ItemStyle st) => st switch
    {
        ItemStyle.Destaque => "destacado",
        ItemStyle.Hachura => "hachurado",
        ItemStyle.Oculto => "oculto",
        _ => ""
    };
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
