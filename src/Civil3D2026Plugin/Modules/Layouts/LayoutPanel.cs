using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Civil3D2026Plugin.UI.Common;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WS = System.Windows.Shapes;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// Explorador de layouts (C3DLAYOUT), no mesmo molde do explorador da rede viaria:
/// barra de categorias com contagem, barra de comandos com icones, vista grafica escura
/// (miniatura do layout com as viewports clicaveis), lista em colunas e abas de detalhe.
/// Janela flutuante; segue o tema claro/escuro do painel viario.
/// </summary>
internal sealed class LayoutPanel : SW.Window
{
    private static LayoutPanel? _instance;

    private readonly ToolTheme T = ToolTheme.Current;
    private static readonly WM.FontFamily UiFont = new("Arial");

    // ------------------------------------------------------------------
    // Dados
    // ------------------------------------------------------------------

    private List<SheetData> _folhas = new();
    private List<LayerStateItem> _states = new();
    private List<StampBlock> _blocos = new();
    private List<(string Nome, double Denominador)> _escalas = new();
    private readonly Dictionary<ObjectId, SheetViewport> _vpPorId = new();

    private string _category = "LAYOUTS";
    private string _tab = "Propriedades";
    private SheetData? _folhaAtual;
    private readonly HashSet<string> _layoutsMarcados = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ObjectId> _vpsMarcadas = new();
    private string? _layerState;
    private string? _bloco;
    private List<PageSetupItem> _pageSetups = new();
    private string? _pageSetup;
    private List<string>? _dispositivos;
    private List<string>? _estilos;
    private KeyMapConfig _kmCfg = new();
    private List<KeyMapSheet> _kmResumo = new();
    private List<ViewFrameItem> _vfs = new();
    private readonly HashSet<ObjectId> _vfsMarcados = new();
    private readonly HashSet<Document> _docsLigados = new();
    private readonly Dictionary<string, string> _valoresCarimbo = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshQueued;

    // ------------------------------------------------------------------
    // Controles
    // ------------------------------------------------------------------

    private WC.WrapPanel _categoryBar = new();
    private WC.StackPanel _commandBar = new();
    private WC.Canvas _graphic = new();
    private WC.TextBlock _graphicInfo = new();
    private WC.ScrollViewer _gradeScroll = new();
    private WC.WrapPanel _grade = new();
    private WC.StackPanel _modoBar = new();

    // Vista grafica: um layout (com zoom/pan) ou todos (cartoes).
    private bool _todos;
    private bool _textos = true;
    private double _zoom = 1;
    private SW.Vector _pan;
    private SW.Point? _dragFrom;
    private SheetGraphics.Mapa? _mapa;
    private readonly Dictionary<ObjectId, List<WS.Path>> _vpShapes = new();
    private readonly Dictionary<SheetData, WC.Border> _cards = new();
    private const double CardW = 250, ThumbH = 176;
    private WC.ListView _list = new();
    private WC.TextBox _find = new();
    private WC.StackPanel _tabStrip = new();
    private WC.ContentControl _tabContent = new();
    private WC.TextBlock _status = new();
    private readonly Dictionary<string, WM.ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);

    private readonly WM.Brush _vpTraco = ToolTheme.Brush(200, 200, 200);
    private readonly WM.Brush _vpHover = ToolTheme.Brush(80, 160, 255, 45);
    private readonly WM.Brush _vpSel = ToolTheme.Brush(80, 160, 255, 110);
    private readonly WM.Brush _vpSelTraco = ToolTheme.Brush(80, 160, 255);

    // ------------------------------------------------------------------
    // Categorias e comandos
    // ------------------------------------------------------------------

    private static readonly (string Id, string Icon, string Title)[] Categories =
    {
        ("LAYOUTS", "layouts", "Layouts"),
        ("VIEWPORTS", "ligar", "Viewports"),
        ("PAGESETUP", "imprimir", "Page setup"),
        ("NUMERACAO", "numero", "Numeracao"),
        ("LAYERSTATE", "camadas", "Layer state"),
        ("CARIMBO", "bloco", "Carimbo"),
        ("MAPACHAVE", "mapa", "Mapa-chave"),
        ("VIEWFRAMES", "viewframe", "View frames")
    };

    private sealed record Cmd(string Key, string Icon, string Title, string Group, string Description, string[] Steps, Action<LayoutPanel> Run);

    private static readonly Cmd[] Commands =
    {
        new("ABRIR", "abrir", "Abrir layout", "Layouts",
            "Torna o layout selecionado o atual.", new[] { "Selecione um layout e clique (ou duplo clique na lista)." }, p => p.Abrir()),
        new("RENOMEAR", "renomear", "Renomear em lote", "Layouts",
            "Renomeia os layouts marcados com prefixo, base, numero sequencial e sufixo.",
            new[] { "Marque os layouts.", "Preencha a regra na aba Renomear." }, p => p.SelectCategory("LAYOUTS", "Renomear")),
        new("ESQ", "esquerda", "Mover para a esquerda", "Layouts",
            "Move os layouts marcados uma aba para a esquerda.", new[] { "Marque os layouts." }, p => p.Executar(() => LayoutOps.Mover(p.Doc!, p.Marcados(), -1))),
        new("DIR", "direita", "Mover para a direita", "Layouts",
            "Move os layouts marcados uma aba para a direita.", new[] { "Marque os layouts." }, p => p.Executar(() => LayoutOps.Mover(p.Doc!, p.Marcados(), +1))),
        new("ORDENAR", "ordenar", "Ordenar pelo nome", "Layouts",
            "Ordena todas as abas pelo nome (PR-2 antes de PR-10).", new[] { "Sem perguntas." }, p => p.Executar(() => LayoutOps.OrdenarPorNome(p.Doc!))),
        new("COPIAR", "copiar", "Copiar layouts", "Layouts",
            "Copia os layouts marcados; a copia fica logo depois do original.", new[] { "Marque os layouts." }, p => p.Executar(() => LayoutOps.Copiar(p.Doc!, p.Marcados()))),
        new("EXCLUIR", "excluir", "Excluir layouts", "Layouts",
            "Exclui os layouts marcados (pede confirmacao).", new[] { "Marque os layouts." }, p => p.Excluir()),
        new("TRAVAR", "travar", "Travar viewports", "Viewports",
            "Trava a exibicao das viewports marcadas.", new[] { "Marque as viewports na miniatura ou na lista." }, p => p.Executar(() => LayoutOps.Travar(p.Doc!, p.VpsMarcadas(), true))),
        new("DESTRAVAR", "destravar", "Destravar viewports", "Viewports",
            "Destrava a exibicao das viewports marcadas.", new[] { "Marque as viewports." }, p => p.Executar(() => LayoutOps.Travar(p.Doc!, p.VpsMarcadas(), false))),
        new("LIGAR", "ligar", "Ligar viewports", "Viewports",
            "Liga as viewports marcadas.", new[] { "Marque as viewports." }, p => p.Executar(() => LayoutOps.Ligar(p.Doc!, p.VpsMarcadas(), true))),
        new("DESLIGAR", "desligar", "Desligar viewports", "Viewports",
            "Desliga as viewports marcadas.", new[] { "Marque as viewports." }, p => p.Executar(() => LayoutOps.Ligar(p.Doc!, p.VpsMarcadas(), false))),
        new("ESCALA", "escala", "Escala das viewports", "Viewports",
            "Aplica a escala 1:N nas viewports marcadas (e a escala de anotacao, se existir no desenho).",
            new[] { "Marque as viewports.", "Escolha a escala na aba Escala." }, p => p.SelectCategory("VIEWPORTS", "Escala")),
        new("NUMERAR", "numero", "Numerar pranchas", "Numeracao",
            "Escreve o numero (01, 02...) e o total (/NN) nos textos das layers PRANCHA_NUMERO e PRANCHA_TOTAL, na ordem das abas.",
            new[] { "Configure os textos uma vez (aba Numeracao).", "Clique para renumerar depois de criar, apagar ou reordenar layouts." }, p => p.Executar(() => LayoutOps.Numerar(p.Doc!))),
        new("LAYERSTATE", "camadas", "Aplicar layer state", "Layer state",
            "Restaura o layer state escolhido como override nas viewports marcadas.",
            new[] { "Escolha o layer state na lista.", "Marque as viewports nas miniaturas.", "Aplicar." }, p => p.AplicarLayerState()),
        new("CARIMBO", "bloco", "Aplicar carimbo", "Carimbo",
            "Escreve os valores preenchidos nas tags do bloco de carimbo, nos layouts marcados.",
            new[] { "Escolha o bloco na lista.", "Preencha as tags na aba Carimbo.", "Marque os layouts e aplique." }, p => p.AplicarCarimbo()),
        new("PSAPLICAR", "imprimir", "Aplicar page setup", "Page setup",
            "Aplica o page setup escolhido (aba Nomeado) nos layouts marcados.",
            new[] { "Escolha o page setup na aba Nomeado.", "Marque os layouts e aplique." }, p => p.SelectCategory("PAGESETUP", "Nomeado")),
        new("PSIMPORTAR", "abrir", "Importar page setups", "Page setup",
            "Importa os page setups de papel de outro DWG ou DWT (os de mesmo nome sao substituidos).",
            new[] { "Escolha o arquivo." }, p => p.ImportarPageSetups()),
        new("MCBASE", "mapa", "Base do mapa-chave", "Mapa-chave",
            "Escolhe no Model os objetos que aparecem no mapa-chave (eixos, quadras, limites).",
            new[] { "Selecione os objetos e confirme; o layout atual volta sozinho." }, p => p.Enviar(C3DCommands.Layouts.MapaChave + " Base")),
        new("MCAREA", "offset", "Area do mapa-chave", "Mapa-chave",
            "Escolhe no layout a caixa onde o mapa-chave fica (a mesma em todos os layouts).",
            new[] { "Clique os dois cantos no papel." }, p => p.Enviar(C3DCommands.Layouts.MapaChave + " Area")),
        new("MCGERAR", "atualizar", "Gerar mapa-chave", "Mapa-chave",
            "Refaz o mapa-chave em todos os layouts: base, contorno e numero de cada folha e hachura da folha atual.",
            new[] { "Sem perguntas." }, p => p.Executar(() => LayoutKeyMap.Gerar(p.Doc!, true))),
        new("VFZOOM", "viewframe", "Zoom no view frame", "View frames",
            "Mostra no Model o view frame ou match line selecionado.", new[] { "Selecione na categoria View frames." }, p => p.ZoomViewFrame()),
        new("ATUALIZAR", "atualizar", "Atualizar", "Geral",
            "Le os layouts do desenho de novo.", new[] { "Sem perguntas." }, p => p.Refresh())
    };

    // ------------------------------------------------------------------
    // Abrir / atualizar
    // ------------------------------------------------------------------

    public static void ShowPanel(string? category = null)
    {
        if (_instance is null)
        {
            _instance = new LayoutPanel();
            _instance.Closed += (_, _) =>
            {
                LayoutPanel fechado = _instance!;
                _instance = null;
                AcApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
                LayoutKeyMap.Changed -= OnKeyMapChanged;
                foreach (Document d in fechado._docsLigados)
                    d.CommandEnded -= OnCommandEnded;
                fechado._docsLigados.Clear();
            };
            LayoutKeyMap.Changed += OnKeyMapChanged;
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

    private static void OnKeyMapChanged(object? sender, EventArgs e) => _instance?.AgendarRefresh();

    // Comandos que mudam layouts, viewports ou textos das pranchas: o painel se relê ao terminar.
    private static readonly HashSet<string> ComandosQueMudam = new(StringComparer.OrdinalIgnoreCase)
    {
        "NP", "LAYOUT", "-LAYOUT", "MVIEW", "-VPORTS", "VPORTS", "PAGESETUP", "PSETUPIN", "U", "UNDO", "REDO", "MREDO",
        "ERASE", "COPY", "MOVE", "ROTATE", "SCALE", "PASTECLIP", "INSERT", "ATTEDIT", "EATTEDIT", "DDEDIT", "TEXTEDIT"
    };

    private static void OnCommandEnded(object? sender, CommandEventArgs e)
    {
        string nome = e.GlobalCommandName ?? "";
        if (ComandosQueMudam.Contains(nome) || nome.StartsWith("C3DLAYOUT", StringComparison.OrdinalIgnoreCase) ||
            nome.StartsWith("C3DMAPACHAVE", StringComparison.OrdinalIgnoreCase))
            _instance?.AgendarRefresh();
    }

    private void LigarDocumento(Document? doc)
    {
        if (doc is not null && _docsLigados.Add(doc))
            doc.CommandEnded += OnCommandEnded;
    }

    private void AgendarRefresh()
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

    private static void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
    {
        LayoutPanel? panel = _instance;
        if (panel is null || panel._refreshQueued)
            return;
        panel._refreshQueued = true;
        panel.Dispatcher.BeginInvoke(new Action(() =>
        {
            panel._refreshQueued = false;
            if (_instance == panel)
                panel.Refresh();
        }), SW.Threading.DispatcherPriority.Background);
    }

    private LayoutPanel()
    {
        Title = "Layouts - Explorador";
        Width = 1100;
        Height = 740;
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
        _graphicInfo = new WC.TextBlock { Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(8, 0, 8, 0) };
        _list = new WC.ListView { BorderBrush = T.Line, Background = T.Pane, Foreground = T.Text };
        _find = new WC.TextBox { Width = 160, Background = T.Input, Foreground = T.Text, BorderBrush = T.Line, CaretBrush = T.Text, VerticalContentAlignment = SW.VerticalAlignment.Center };
        _tabStrip = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        _tabContent = new WC.ContentControl();
        _status = new WC.TextBlock { Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(6, 0, 6, 0) };

        WC.Grid root = new();
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(0.85, SW.GridUnitType.Star), MinHeight = 120 });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(5) });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1, SW.GridUnitType.Star), MinHeight = 160 });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });

        root.Children.Add(_categoryBar);

        WC.DockPanel commandRow = new() { Margin = new SW.Thickness(4, 2, 4, 2) };
        WC.StackPanel findPanel = new() { Orientation = WC.Orientation.Horizontal };
        findPanel.Children.Add(new WC.TextBlock { Text = "Localizar:", Foreground = T.Text, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(8, 0, 4, 0) });
        findPanel.Children.Add(_find);
        _find.TextChanged += (_, _) =>
        {
            FillList();
            if (_todos)
                RenderGraphic();
        };
        WC.DockPanel.SetDock(findPanel, WC.Dock.Right);
        commandRow.Children.Add(findPanel);
        _commandBar = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        commandRow.Children.Add(_commandBar);
        WC.Grid.SetRow(commandRow, 1);
        root.Children.Add(commandRow);

        _grade = new WC.WrapPanel { Margin = new SW.Thickness(4) };
        _gradeScroll = new WC.ScrollViewer
        {
            Content = _grade,
            VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled,
            Background = ToolTheme.GraphicBack,
            Visibility = SW.Visibility.Collapsed
        };
        WC.Grid graphicHost = new();
        graphicHost.Children.Add(_graphic);
        graphicHost.Children.Add(_gradeScroll);
        WC.Border graphic = new() { BorderBrush = T.Line, BorderThickness = new SW.Thickness(1), Child = graphicHost, Margin = new SW.Thickness(4, 0, 4, 0) };
        _graphic.SizeChanged += (_, _) => { if (!_todos) RenderGraphic(); };
        _graphic.MouseWheel += OnGraphicWheel;
        _graphic.MouseDown += OnGraphicDown;
        _graphic.MouseMove += OnGraphicMove;
        _graphic.MouseUp += (_, _) =>
        {
            _dragFrom = null;
            _graphic.ReleaseMouseCapture();
        };
        WC.Grid.SetRow(graphic, 2);
        root.Children.Add(graphic);

        WC.DockPanel graphicBar = new() { Margin = new SW.Thickness(4, 0, 4, 0), Background = T.Hover, Height = 24 };
        WC.StackPanel links = new() { Orientation = WC.Orientation.Horizontal };
        _modoBar = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        links.Children.Add(_modoBar);
        links.Children.Add(Sep());
        links.Children.Add(Link("todas as viewports", () => MarcarViewportsDaFolha(true), "Marca todas as viewports do layout mostrado"));
        links.Children.Add(Sep());
        links.Children.Add(Link("nenhuma", () => MarcarViewportsDaFolha(false), "Desmarca as viewports do layout mostrado"));
        links.Children.Add(Sep());
        links.Children.Add(Link("repetir nos demais", RepetirNosDemais, "Marca, nos outros layouts, as viewports na mesma posicao e tamanho das marcadas aqui"));
        WC.DockPanel.SetDock(links, WC.Dock.Left);
        graphicBar.Children.Add(links);
        var textos = new WC.CheckBox
        {
            Content = "Textos",
            IsChecked = _textos,
            VerticalAlignment = SW.VerticalAlignment.Center,
            Margin = new SW.Thickness(8, 0, 8, 0),
            ToolTip = "Mostra os textos e atributos do papel na miniatura"
        };
        textos.Click += (_, _) =>
        {
            _textos = textos.IsChecked == true;
            RenderGraphic();
        };
        WC.DockPanel.SetDock(textos, WC.Dock.Right);
        graphicBar.Children.Add(textos);
        graphicBar.Children.Add(_graphicInfo);
        WC.Grid.SetRow(graphicBar, 3);
        root.Children.Add(graphicBar);

        WC.GridSplitter splitH = new() { Height = 5, HorizontalAlignment = SW.HorizontalAlignment.Stretch, Background = T.Back };
        WC.Grid.SetRow(splitH, 4);
        root.Children.Add(splitH);

        WC.Grid bottom = new() { Margin = new SW.Thickness(4, 0, 4, 0) };
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star), MinWidth = 260 });
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(5) });
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1.15, SW.GridUnitType.Star), MinWidth = 300 });
        _list.SelectionChanged += (_, _) => OnRowSelected();
        _list.MouseDoubleClick += (_, _) => OnRowDoubleClick();
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
        leftButtons.Children.Add(FooterButton("Marcar todos", () => MarcarLayouts(true)));
        leftButtons.Children.Add(FooterButton("Desmarcar", () => MarcarLayouts(false)));
        leftButtons.Children.Add(FooterButton("Atualizar", Refresh));
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

        // Linha da lista com selecao nas cores do tema.
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

        // Botao com modelo proprio (o hover padrao apaga o texto no tema escuro).
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

        SW.Style combo = new(typeof(WC.ComboBox));
        combo.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        Resources[typeof(WC.ComboBox)] = combo;
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
        var t = new WC.TextBlock
        {
            Text = texto,
            Foreground = T.Accent,
            FontSize = 11,
            Cursor = WI.Cursors.Hand,
            ToolTip = dica,
            VerticalAlignment = SW.VerticalAlignment.Center,
            Margin = new SW.Thickness(8, 0, 0, 0)
        };
        t.MouseEnter += (_, _) => t.TextDecorations = SW.TextDecorations.Underline;
        t.MouseLeave += (_, _) => t.TextDecorations = null;
        t.MouseLeftButtonDown += (_, e) =>
        {
            acao();
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

    // ------------------------------------------------------------------
    // Dados
    // ------------------------------------------------------------------

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
            Database db = doc.Database;
            _folhas = LayoutReader.LerLayouts(db);
            _states = LayoutReader.LerLayerStates(db);
            _blocos = LayoutStamp.LerBlocos(db);
            _escalas = LayoutOps.EscalasDoDesenho(db);
            _pageSetups = LayoutPageSetup.Ler(db);
            _kmCfg = LayoutKeyMap.Ler(db);
            _kmResumo = LayoutKeyMap.Resumo(db);
            _vfs = LayoutViewFrames.Ler(db, _folhas.Select(f => f.Nome).ToList());
        }
        catch (System.Exception ex)
        {
            Say("Erro ao ler os layouts: " + ex.Message);
            return;
        }

        LigarDocumento(doc);
        _vfsMarcados.RemoveWhere(id => !_vfs.Any(v => v.Id == id));
        if (_pageSetup is not null && !_pageSetups.Any(p => p.Nome == _pageSetup))
            _pageSetup = null;

        _vpPorId.Clear();
        foreach (SheetData f in _folhas)
            foreach (SheetViewport v in f.Viewports)
                _vpPorId[v.Id] = v;

        // Descarta marcacoes de layouts/viewports que nao existem mais.
        _layoutsMarcados.RemoveWhere(n => !_folhas.Any(f => f.Nome.Equals(n, StringComparison.OrdinalIgnoreCase)));
        _vpsMarcadas.RemoveWhere(id => !_vpPorId.ContainsKey(id));
        if (_layerState is not null && !_states.Any(s => s.Nome == _layerState))
            _layerState = null;
        if (_bloco is not null && !_blocos.Any(b => b.Nome.Equals(_bloco, StringComparison.OrdinalIgnoreCase)))
            _bloco = null;
        if (_folhaAtual is not null)
            _folhaAtual = _folhas.FirstOrDefault(f => f.Nome.Equals(_folhaAtual.Nome, StringComparison.OrdinalIgnoreCase));
        _folhaAtual ??= _folhas.FirstOrDefault(f => f.Atual) ?? _folhas.FirstOrDefault();

        Title = $"Layouts - Explorador [{System.IO.Path.GetFileName(doc.Name)}]";
        FillCategories();
        FillList();
        RenderGraphic();
        BuildTabs();
        Status();
    }

    private int CountFor(string category) => category switch
    {
        "LAYOUTS" => _folhas.Count,
        "VIEWPORTS" => _folhas.Sum(f => f.Viewports.Count),
        "NUMERACAO" => -1,
        "LAYERSTATE" => _states.Count,
        "CARIMBO" => _blocos.Count,
        "PAGESETUP" => _pageSetups.Count,
        "MAPACHAVE" => _kmResumo.Count(r => r.Regioes > 0),
        "VIEWFRAMES" => _vfs.Count,
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

    private void SelectCategory(string id, string? tab = null)
    {
        _category = id;
        _tab = tab ?? "Propriedades";
        FillCategories();
        FillList();
        BuildTabs();
    }

    // ------------------------------------------------------------------
    // Lista (colunas por categoria)
    // ------------------------------------------------------------------

    private sealed class Row
    {
        public string Icon { get; init; } = "generico";
        public string[] Cells { get; init; } = Array.Empty<string>();
        public object? Data { get; init; }
        public bool Checked { get; init; }
    }

    private void FillList()
    {
        string filtro = _find.Text.Trim();
        bool Match(string s) => filtro.Length == 0 || s.Contains(filtro, StringComparison.OrdinalIgnoreCase);
        object? atual = (_list.SelectedItem as WC.ListViewItem)?.Tag is Row r ? r.Data : null;

        List<Row> rows;
        string[] headers;
        double[] widths;
        bool comCheck;

        switch (_category)
        {
            case "VIEWPORTS":
                headers = new[] { "Layout", "Escala", "Tamanho", "Estado", "Aplicado" };
                widths = new[] { 150.0, 90, 100, 110, 110 };
                comCheck = true;
                rows = _folhas.SelectMany(f => f.Viewports.Select((v, i) => new Row
                {
                    Icon = v.Ligada ? "ligar" : "desligar",
                    Data = v,
                    Checked = _vpsMarcadas.Contains(v.Id),
                    Cells = new[]
                    {
                        $"{f.Nome}  #{i + 1}",
                        v.Escala.Length > 0 ? v.Escala : EscalaTexto(v.EscalaCustom),
                        $"{v.Largura:0.#} x {v.Altura:0.#}",
                        (v.Travada ? "travada" : "livre") + (v.Ligada ? "" : ", desligada"),
                        v.Aplicado ?? ""
                    }
                })).Where(r => Match(r.Cells[0]) || Match(r.Cells[1])).ToList();
                break;

            case "LAYERSTATE":
                headers = new[] { "Layer state", "Descricao" };
                widths = new[] { 180.0, 300 };
                comCheck = false;
                rows = _states.Where(s => Match(s.Nome) || Match(s.Descricao))
                    .Select(s => new Row { Icon = "camadas", Data = s, Cells = new[] { s.Nome, s.Descricao }, Checked = s.Nome == _layerState }).ToList();
                break;

            case "CARIMBO":
                headers = new[] { "Bloco", "Layouts", "Referencias", "Tags" };
                widths = new[] { 200.0, 60, 80, 260 };
                comCheck = false;
                rows = _blocos.Where(b => Match(b.Nome))
                    .Select(b => new Row { Icon = "bloco", Data = b, Cells = new[] { b.Nome, b.Layouts.ToString(), b.Referencias.ToString(), string.Join(", ", b.Tags) }, Checked = b.Nome.Equals(_bloco, StringComparison.OrdinalIgnoreCase) }).ToList();
                break;

            case "PAGESETUP":
                headers = new[] { "Layout", "Dispositivo", "Papel", "Estilo" };
                widths = new[] { 170.0, 150, 170, 120 };
                comCheck = true;
                rows = _folhas.Where(f => Match(f.Nome)).Select(f => new Row
                {
                    Icon = "imprimir",
                    Data = f,
                    Checked = _layoutsMarcados.Contains(f.Nome),
                    Cells = new[] { f.Nome, f.Dispositivo, f.Papel, f.EstiloPlot }
                }).ToList();
                break;

            case "MAPACHAVE":
                headers = new[] { "Layout", "Areas de folha", "Mapa-chave" };
                widths = new[] { 200.0, 100, 100 };
                comCheck = false;
                rows = _kmResumo.Where(r => Match(r.Layout)).Select(r => new Row
                {
                    Icon = r.Regioes > 0 ? "mapa" : "prancha",
                    Data = _folhas.FirstOrDefault(f => f.Nome == r.Layout),
                    Cells = new[] { $"{r.Ordem}  {r.Layout}", r.Regioes > 0 ? r.Regioes.ToString() : "-", r.TemMapa ? "sim" : "" }
                }).ToList();
                break;

            case "VIEWFRAMES":
                headers = new[] { "View frame", "Eixo", "Estaca inicial", "Estaca final", "Layout" };
                widths = new[] { 170.0, 140, 90, 90, 130 };
                comCheck = true;
                rows = _vfs.Where(v => Match(v.Nome) || Match(v.Eixo) || Match(v.Grupo) || Match(v.Layout)).Select(v => new Row
                {
                    Icon = v.Tipo == "Frame" ? "viewframe" : "quebrar",
                    Data = v,
                    Checked = _vfsMarcados.Contains(v.Id),
                    Cells = new[] { v.Nome, v.Eixo, LayoutViewFrames.Estaca(v.EstacaInicio), LayoutViewFrames.Estaca(v.EstacaFim), v.Layout }
                }).ToList();
                break;

            default: // LAYOUTS, NUMERACAO
                headers = new[] { "#", "Layout", "Viewports", "Papel", "Dispositivo" };
                widths = new[] { 36.0, 170, 70, 150, 150 };
                comCheck = true;
                rows = _folhas.Where(f => Match(f.Nome)).Select((f, i) => new Row
                {
                    Icon = f.Atual ? "abrir" : "prancha",
                    Data = f,
                    Checked = _layoutsMarcados.Contains(f.Nome),
                    Cells = new[] { (f.Ordem).ToString(), f.Nome, f.Viewports.Count.ToString(), Papel(f), f.Dispositivo }
                }).ToList();
                break;
        }

        var view = new WC.GridView();
        if (comCheck)
        {
            var col = new WC.GridViewColumn { Width = 28, Header = "" };
            var factory = new SW.FrameworkElementFactory(typeof(WC.CheckBox));
            factory.SetBinding(WC.CheckBox.IsCheckedProperty, new SW.Data.Binding("Checked"));
            factory.SetValue(SW.FrameworkElement.HorizontalAlignmentProperty, SW.HorizontalAlignment.Center);
            factory.AddHandler(WC.Primitives.ButtonBase.ClickEvent, new SW.RoutedEventHandler(OnCheckClick));
            col.CellTemplate = new SW.DataTemplate { VisualTree = factory };
            view.Columns.Add(col);
        }
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
        WC.ListViewItem? manter = null;
        foreach (Row row in rows)
        {
            var item = new WC.ListViewItem { Content = new RowView(row, IconOf(row.Icon)), Tag = row };
            _list.Items.Add(item);
            if (atual is not null && ReferenceEquals(row.Data, atual))
                manter = item;
        }
        if (manter is not null)
            _list.SelectedItem = manter;
    }

    /// <summary>Objeto ligado as colunas (binding por nome C0..C4, Checked e IconImage).</summary>
    private sealed class RowView
    {
        public RowView(Row row, WM.ImageSource icon)
        {
            Checked = row.Checked;
            IconImage = icon;
            string[] c = row.Cells;
            C0 = c.Length > 0 ? c[0] : "";
            C1 = c.Length > 1 ? c[1] : "";
            C2 = c.Length > 2 ? c[2] : "";
            C3 = c.Length > 3 ? c[3] : "";
            C4 = c.Length > 4 ? c[4] : "";
        }

        public bool Checked { get; set; }
        public WM.ImageSource IconImage { get; }
        public string C0 { get; }
        public string C1 { get; }
        public string C2 { get; }
        public string C3 { get; }
        public string C4 { get; }
    }

    private void OnCheckClick(object sender, SW.RoutedEventArgs e)
    {
        if (sender is not WC.CheckBox cb || ItemDe(cb)?.Tag is not Row row)
            return;
        bool marcar = cb.IsChecked == true;
        switch (row.Data)
        {
            case SheetData f:
                if (marcar) _layoutsMarcados.Add(f.Nome); else _layoutsMarcados.Remove(f.Nome);
                break;
            case SheetViewport v:
                if (marcar) _vpsMarcadas.Add(v.Id); else _vpsMarcadas.Remove(v.Id);
                AtualizarViewports();
                break;
            case ViewFrameItem vf:
                if (marcar) _vfsMarcados.Add(vf.Id); else _vfsMarcados.Remove(vf.Id);
                break;
        }
        Status();
    }

    private static WC.ListViewItem? ItemDe(SW.DependencyObject? d)
    {
        while (d is not null and not WC.ListViewItem)
            d = WM.VisualTreeHelper.GetParent(d);
        return d as WC.ListViewItem;
    }

    private void OnRowSelected()
    {
        if ((_list.SelectedItem as WC.ListViewItem)?.Tag is not Row row)
            return;

        switch (row.Data)
        {
            case SheetData f:
                MostrarFolha(f);
                break;
            case SheetViewport v:
                MostrarFolha(_folhas.FirstOrDefault(f => f.Viewports.Contains(v)));
                break;
            case LayerStateItem s:
                _layerState = s.Nome;
                Status();
                break;
            case StampBlock b:
                _bloco = b.Nome;
                _valoresCarimbo.Clear();
                break;
            case ViewFrameItem vf when vf.Layout.Length > 0:
                MostrarFolha(_folhas.FirstOrDefault(f => f.Nome == vf.Layout));
                break;
        }
        BuildTabs();
    }

    private void OnRowDoubleClick()
    {
        if ((_list.SelectedItem as WC.ListViewItem)?.Tag is not Row row)
            return;
        switch (row.Data)
        {
            case SheetData f:
                Abrir(f.Nome);
                break;
            case ViewFrameItem:
                ZoomViewFrame();
                break;
            case SheetViewport v:
                if (!_vpsMarcadas.Remove(v.Id))
                    _vpsMarcadas.Add(v.Id);
                FillList();
                AtualizarViewports();
                Status();
                break;
        }
    }

    // ------------------------------------------------------------------
    // Vista grafica: miniatura do layout atual com viewports clicaveis
    // ------------------------------------------------------------------

    private void RenderGraphic()
    {
        AtualizarModoBar();
        _vpShapes.Clear();
        if (_todos)
        {
            _graphic.Visibility = SW.Visibility.Collapsed;
            _gradeScroll.Visibility = SW.Visibility.Visible;
            RenderGrade();
        }
        else
        {
            _gradeScroll.Visibility = SW.Visibility.Collapsed;
            _graphic.Visibility = SW.Visibility.Visible;
            _grade.Children.Clear();
            _cards.Clear();
            RenderUm();
        }
    }

    private void AtualizarModoBar()
    {
        _modoBar.Children.Clear();
        _modoBar.Children.Add(Modo("Um layout", !_todos, () => { _todos = false; RenderGraphic(); }));
        _modoBar.Children.Add(Modo("Todos", _todos, () => { _todos = true; RenderGraphic(); }));
    }

    private WC.TextBlock Modo(string texto, bool ativo, Action acao)
    {
        WC.TextBlock t = Link(texto, acao, ativo ? "Vista atual" : "Trocar a vista");
        t.FontWeight = ativo ? SW.FontWeights.Bold : SW.FontWeights.Normal;
        t.Foreground = ativo ? T.Text : T.Accent;
        return t;
    }

    /// <summary>Um layout, com zoom pela roda e arrastar (botao do meio ou fundo).</summary>
    private void RenderUm()
    {
        SheetData? folha = _folhaAtual;
        double w = _graphic.ActualWidth, h = _graphic.ActualHeight;
        if (folha is null || w < 10 || h < 10)
        {
            _graphic.Children.Clear();
            _graphicInfo.Text = "Nenhum layout.";
            return;
        }

        _mapa = SheetGraphics.Desenhar(_graphic, folha, w, h, _zoom, _pan, _textos, soTextosGrandes: false);
        DesenharViewports(_graphic, folha, _mapa, rotulos: true);

        var nome = new WC.TextBlock
        {
            Text = $"{folha.Ordem}  {folha.Nome}   {Papel(folha)}   {folha.Viewports.Count} viewport(s)",
            Foreground = ToolTheme.GraphicText,
            FontSize = 11,
            IsHitTestVisible = false
        };
        WC.Canvas.SetLeft(nome, 8);
        WC.Canvas.SetTop(nome, 6);
        _graphic.Children.Add(nome);
        InfoViewports();
    }

    /// <summary>Todos os layouts lado a lado, como cartoes (cada um vira bitmap em cache).</summary>
    private void RenderGrade()
    {
        _grade.Children.Clear();
        _cards.Clear();
        string filtro = _find.Text.Trim();

        foreach (SheetData folha in _folhas.Where(f => filtro.Length == 0 || f.Nome.Contains(filtro, StringComparison.OrdinalIgnoreCase)))
        {
            var sp = new WC.StackPanel();
            var cab = new WC.DockPanel { Margin = new SW.Thickness(4, 3, 6, 2) };
            var marca = new WC.CheckBox
            {
                IsChecked = _layoutsMarcados.Contains(folha.Nome),
                VerticalAlignment = SW.VerticalAlignment.Center,
                ToolTip = "Marcar o layout para os comandos da barra"
            };
            SheetData f0 = folha;
            marca.Click += (_, _) =>
            {
                if (marca.IsChecked == true) _layoutsMarcados.Add(f0.Nome); else _layoutsMarcados.Remove(f0.Nome);
                if (_category is "LAYOUTS" or "NUMERACAO" or "PAGESETUP" or "CARIMBO")
                    FillList();
                Status();
            };
            WC.DockPanel.SetDock(marca, WC.Dock.Left);
            cab.Children.Add(marca);
            var cont = new WC.TextBlock { Foreground = ToolTheme.GraphicText, FontSize = 10.5, VerticalAlignment = SW.VerticalAlignment.Center, Tag = "contagem" };
            WC.DockPanel.SetDock(cont, WC.Dock.Right);
            cab.Children.Add(cont);
            cab.Children.Add(new WC.TextBlock
            {
                Text = $"{folha.Ordem}  {folha.Nome}",
                FontWeight = SW.FontWeights.Bold,
                Foreground = ToolTheme.GraphicText,
                Margin = new SW.Thickness(4, 0, 0, 0),
                TextTrimming = SW.TextTrimming.CharacterEllipsis,
                ToolTip = $"{folha.Nome}\n{Papel(folha)}\n{folha.Dispositivo}\nDuplo clique abre na vista grande"
            });
            sp.Children.Add(cab);

            var canvas = new WC.Canvas { Width = CardW - 2, Height = ThumbH, Background = ToolTheme.GraphicBack, ClipToBounds = true };
            SheetGraphics.Mapa mapa = SheetGraphics.Desenhar(canvas, folha, CardW - 2, ThumbH, 1, new SW.Vector(), _textos, soTextosGrandes: true, margem: 6);
            DesenharViewports(canvas, folha, mapa, rotulos: false);
            sp.Children.Add(canvas);

            var card = new WC.Border
            {
                Width = CardW,
                Margin = new SW.Thickness(4),
                Background = ToolTheme.Brush(48, 50, 54),
                BorderThickness = new SW.Thickness(1.5),
                Child = sp,
                CacheMode = new WM.BitmapCache { SnapsToDevicePixels = true }
            };
            card.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    _folhaAtual = f0;
                    _todos = false;
                    _zoom = 1;
                    _pan = new SW.Vector();
                    RenderGraphic();
                    BuildTabs();
                }
                else
                {
                    _folhaAtual = f0;
                    AtualizarCards();
                    BuildTabs();
                }
            };
            _cards[folha] = card;
            _grade.Children.Add(card);
        }

        AtualizarCards();
        InfoViewports();
    }

    /// <summary>Viewports clicaveis por cima do desenho do papel.</summary>
    private void DesenharViewports(WC.Canvas canvas, SheetData folha, SheetGraphics.Mapa mapa, bool rotulos)
    {
        int i = 0;
        foreach (SheetViewport vp in folha.Viewports)
        {
            i++;
            var vg = new WM.StreamGeometry();
            using (WM.StreamGeometryContext ctx = vg.Open())
            {
                ctx.BeginFigure(mapa.Tela(vp.Contorno[0]), true, true);
                ctx.PolyLineTo(vp.Contorno.Skip(1).Select(mapa.Tela).ToList(), true, false);
            }
            vg.Freeze();

            var forma = new WS.Path
            {
                Data = vg,
                StrokeDashArray = vp.Ligada ? null : new WM.DoubleCollection { 3, 2 },
                Cursor = WI.Cursors.Hand,
                ToolTip = $"Viewport #{i}\n{(vp.Escala.Length > 0 ? vp.Escala : EscalaTexto(vp.EscalaCustom))}\n{vp.Largura:0.##} x {vp.Altura:0.##}\n{(vp.Travada ? "travada" : "livre")}{(vp.Ligada ? "" : ", desligada")}" +
                          (vp.Aplicado is null ? "" : $"\nLayer state: {vp.Aplicado}") + "\nClique para marcar/desmarcar"
            };
            SheetViewport v = vp;
            forma.MouseEnter += (_, _) => { if (!_vpsMarcadas.Contains(v.Id)) forma.Fill = _vpHover; };
            forma.MouseLeave += (_, _) => { if (!_vpsMarcadas.Contains(v.Id)) forma.Fill = WM.Brushes.Transparent; };
            forma.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount > 1)
                    return;
                if (!_vpsMarcadas.Remove(v.Id))
                    _vpsMarcadas.Add(v.Id);
                AtualizarViewports();
                if (_category == "VIEWPORTS")
                    FillList();
                Status();
                e.Handled = true;
            };
            canvas.Children.Add(forma);
            if (!_vpShapes.TryGetValue(vp.Id, out List<WS.Path>? lista))
                _vpShapes[vp.Id] = lista = new List<WS.Path>();
            lista.Add(forma);

            if (rotulos)
            {
                SW.Point c = mapa.Tela(vp.Centro);
                string rot = vp.Aplicado ?? (vp.Escala.Length > 0 ? vp.Escala : EscalaTexto(vp.EscalaCustom));
                var rotulo = new WC.TextBlock
                {
                    Text = $"#{i}  {rot}",
                    Foreground = ToolTheme.GraphicText,
                    FontSize = 10,
                    FontWeight = vp.Aplicado is null ? SW.FontWeights.Normal : SW.FontWeights.Bold,
                    IsHitTestVisible = false,
                    TextAlignment = SW.TextAlignment.Center,
                    Width = Math.Max(40, vp.Largura * mapa.Escala),
                    TextTrimming = SW.TextTrimming.CharacterEllipsis
                };
                WC.Canvas.SetLeft(rotulo, c.X - rotulo.Width / 2.0);
                WC.Canvas.SetTop(rotulo, c.Y - 7);
                canvas.Children.Add(rotulo);
            }
        }
        AtualizarViewports();
    }

    /// <summary>So muda as cores das viewports (sem redesenhar os papeis).</summary>
    private void AtualizarViewports()
    {
        foreach ((ObjectId id, List<WS.Path> formas) in _vpShapes)
        {
            bool marcada = _vpsMarcadas.Contains(id);
            foreach (WS.Path forma in formas)
            {
                forma.Fill = marcada ? _vpSel : WM.Brushes.Transparent;
                forma.Stroke = marcada ? _vpSelTraco : _vpTraco;
                forma.StrokeThickness = marcada ? 2.0 : 1.2;
            }
        }
        if (_todos)
            AtualizarCards();
        InfoViewports();
    }

    private void AtualizarCards()
    {
        foreach ((SheetData f, WC.Border card) in _cards)
        {
            int n = f.Viewports.Count(v => _vpsMarcadas.Contains(v.Id));
            card.BorderBrush = f == _folhaAtual ? ToolTheme.Brush(255, 176, 0) : n > 0 ? _vpSelTraco : ToolTheme.Brush(70, 72, 77);
            if (card.Child is WC.StackPanel sp && sp.Children[0] is WC.DockPanel cab)
                foreach (object o in cab.Children)
                    if (o is WC.TextBlock { Tag: "contagem" } t)
                        t.Text = $"{n}/{f.Viewports.Count}";
        }
    }

    private void InfoViewports()
    {
        int total = _vpsMarcadas.Count;
        int layouts = _folhas.Count(f => f.Viewports.Any(v => _vpsMarcadas.Contains(v.Id)));
        _graphicInfo.Text = _todos
            ? $"{_folhas.Count} layout(s); {total} viewport(s) marcada(s) em {layouts} layout(s). Clique no cartao para escolher; duplo clique abre."
            : _folhaAtual is null
                ? ""
                : $"{_folhaAtual.Viewports.Count(v => _vpsMarcadas.Contains(v.Id))}/{_folhaAtual.Viewports.Count} viewport(s) marcada(s) neste layout; {total} no total. Roda: zoom; arrastar: mover; duplo clique: enquadrar.";
    }

    private void OnGraphicWheel(object sender, WI.MouseWheelEventArgs e)
    {
        if (_mapa is null || _folhaAtual is null)
            return;
        SW.Point c = e.GetPosition(_graphic);
        double novo = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25 : 0.8), 0.5, 60);
        double s0 = _mapa.Escala / _zoom, s1 = s0 * novo;
        double w = _graphic.ActualWidth, h = _graphic.ActualHeight;
        double bw = Math.Max(_folhaAtual.Largura, 1e-6), bh = Math.Max(_folhaAtual.Altura, 1e-6);
        double px = (c.X - _mapa.Ox) / _mapa.Escala, py = (c.Y - _mapa.Oy) / _mapa.Escala;
        _pan = new SW.Vector(c.X - px * s1 - (w - bw * s1) / 2.0, c.Y - py * s1 - (h - bh * s1) / 2.0);
        _zoom = novo;
        RenderGraphic();
        e.Handled = true;
    }

    private void OnGraphicDown(object sender, WI.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _zoom = 1;
            _pan = new SW.Vector();
            RenderGraphic();
            return;
        }
        if (e.ChangedButton is WI.MouseButton.Middle or WI.MouseButton.Left)
        {
            _dragFrom = e.GetPosition(_graphic);
            _graphic.CaptureMouse();
        }
    }

    private void OnGraphicMove(object sender, WI.MouseEventArgs e)
    {
        if (_dragFrom is not SW.Point de)
            return;
        SW.Point p = e.GetPosition(_graphic);
        SW.Vector d = p - de;
        if (d.Length < 2)
            return;
        _pan += d;
        _dragFrom = p;
        RenderGraphic();
    }

    private void MostrarFolha(SheetData? f)
    {
        if (f is null || f == _folhaAtual)
            return;
        _folhaAtual = f;
        _zoom = 1;
        _pan = new SW.Vector();
        if (_todos)
            AtualizarCards();
        else
            RenderGraphic();
    }

    private static string EscalaTexto(double custom) => custom > 0 ? $"1:{1.0 / custom:0.##}" : "";

    private static string Papel(SheetData f) =>
        f.PapelLargura > 0 && f.PapelAltura > 0 ? $"{f.PapelLargura:0} x {f.PapelAltura:0} mm" : f.Papel;

    // ------------------------------------------------------------------
    // Abas de detalhe
    // ------------------------------------------------------------------

    private void BuildTabs()
    {
        var tabs = new List<(string Key, string Name, Func<SW.UIElement> Build)> { ("Propriedades", "Propriedades", BuildProperties) };
        switch (_category)
        {
            case "LAYOUTS":
                tabs.Add(("Renomear", "Renomear", BuildRename));
                break;
            case "VIEWPORTS":
                tabs.Add(("Escala", "Escala", BuildScale));
                break;
            case "NUMERACAO":
                tabs.Insert(0, ("Numeracao", "Numeracao", BuildNumbering));
                break;
            case "LAYERSTATE":
                tabs.Insert(0, ("Aplicar", "Aplicar", BuildLayerState));
                break;
            case "CARIMBO":
                tabs.Insert(0, ("Carimbo", "Carimbo", BuildStamp));
                break;
            case "PAGESETUP":
                tabs.Insert(0, ("Nomeado", "Page setup nomeado", BuildPageSetupNamed));
                tabs.Insert(1, ("Direto", "Configuracao direta", BuildPageSetupDirect));
                break;
            case "MAPACHAVE":
                tabs.Insert(0, ("MapaChave", "Mapa-chave", BuildKeyMap));
                break;
            case "VIEWFRAMES":
                tabs.Insert(0, ("ViewFrames", "View frames", BuildViewFrames));
                break;
        }

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

    private WC.TextBlock Prop(string nome, string valor)
    {
        var tb = new WC.TextBlock { Margin = new SW.Thickness(0, 2, 0, 2), TextWrapping = SW.TextWrapping.Wrap, Foreground = T.Text };
        tb.Inlines.Add(new SW.Documents.Run(nome + ": ") { Foreground = T.Muted });
        tb.Inlines.Add(new SW.Documents.Run(valor));
        return tb;
    }

    private WC.Button ActionButton(string text, Action action)
    {
        var b = new WC.Button { Content = text, MinWidth = 110, Padding = new SW.Thickness(12, 4, 12, 4), Margin = new SW.Thickness(0, 10, 6, 0), HorizontalAlignment = SW.HorizontalAlignment.Left };
        b.Click += (_, _) => action();
        return b;
    }

    private SW.UIElement BuildProperties()
    {
        WC.StackPanel p = Form();
        object? data = (_list.SelectedItem as WC.ListViewItem)?.Tag is Row r ? r.Data : null;
        switch (data)
        {
            case SheetData f:
                p.Children.Add(Label(f.Nome, true));
                p.Children.Add(Prop("Ordem da aba", f.Ordem.ToString()));
                p.Children.Add(Prop("Papel", f.Papel.Length > 0 ? $"{f.Papel} ({Papel(f)})" : Papel(f)));
                p.Children.Add(Prop("Dispositivo", f.Dispositivo.Length > 0 ? f.Dispositivo : "(nenhum)"));
                p.Children.Add(Prop("Estilo de plotagem", f.EstiloPlot.Length > 0 ? f.EstiloPlot : "(nenhum)"));
                p.Children.Add(Prop("Viewports", f.Viewports.Count.ToString()));
                p.Children.Add(Prop("Atual", f.Atual ? "sim" : "nao"));
                p.Children.Add(ActionButton("Abrir layout", () => Abrir(f.Nome)));
                break;
            case SheetViewport v:
                SheetData? dono = _folhas.FirstOrDefault(f => f.Viewports.Contains(v));
                p.Children.Add(Label($"Viewport de {dono?.Nome}", true));
                p.Children.Add(Prop("Escala de anotacao", v.Escala.Length > 0 ? v.Escala : "(nenhuma)"));
                p.Children.Add(Prop("Escala", EscalaTexto(v.EscalaCustom)));
                p.Children.Add(Prop("Tamanho no papel", $"{v.Largura:0.##} x {v.Altura:0.##}"));
                p.Children.Add(Prop("Centro", $"{v.Centro.X:0.##}, {v.Centro.Y:0.##}"));
                p.Children.Add(Prop("Travada", v.Travada ? "sim" : "nao"));
                p.Children.Add(Prop("Ligada", v.Ligada ? "sim" : "nao"));
                if (v.Aplicado is not null)
                    p.Children.Add(Prop("Layer state aplicado", v.Aplicado));
                break;
            case LayerStateItem s:
                p.Children.Add(Label(s.Nome, true));
                p.Children.Add(Prop("Descricao", s.Descricao.Length > 0 ? s.Descricao : "(sem descricao)"));
                p.Children.Add(Label("Para incluir VP Freeze e cores de viewport, o layer state precisa ter sido salvo de dentro de uma viewport (MSPACE)."));
                break;
            case ViewFrameItem vf:
                p.Children.Add(Label(vf.Nome, true));
                p.Children.Add(Prop("Tipo", vf.Tipo));
                p.Children.Add(Prop("Grupo", vf.Grupo));
                p.Children.Add(Prop("Eixo", vf.Eixo));
                p.Children.Add(Prop(vf.Tipo == "Frame" ? "Estacas" : "Estaca",
                    vf.Tipo == "Frame" ? $"{LayoutViewFrames.Estaca(vf.EstacaInicio)} a {LayoutViewFrames.Estaca(vf.EstacaFim)} ({vf.EstacaInicio:0.00} a {vf.EstacaFim:0.00} m)"
                                       : $"{LayoutViewFrames.Estaca(vf.EstacaInicio)} ({vf.EstacaInicio:0.00} m)"));
                p.Children.Add(Prop("Rotulo", vf.RotuloVisivel ? "visivel" : "oculto"));
                if (vf.Folha.Length > 0)
                    p.Children.Add(Prop("Folha (Civil)", vf.Folha));
                if (vf.Layout.Length > 0)
                    p.Children.Add(ActionButton($"Abrir layout {vf.Layout}", () => Abrir(vf.Layout)));
                p.Children.Add(ActionButton("Zoom no Model", ZoomViewFrame));
                break;
            case StampBlock b:
                p.Children.Add(Label(b.Nome, true));
                p.Children.Add(Prop("Em layouts", b.Layouts.ToString()));
                p.Children.Add(Prop("Referencias", b.Referencias.ToString()));
                p.Children.Add(Prop("Tags", string.Join(", ", b.Tags)));
                break;
            default:
                p.Children.Add(Label($"{_folhas.Count} layout(s), {_folhas.Sum(f => f.Viewports.Count)} viewport(s), {_states.Count} layer state(s), {_blocos.Count} bloco(s) com atributos.", true));
                p.Children.Add(Label("Selecione um item na lista. A caixa de marcacao escolhe os layouts/viewports para os comandos da barra."));
                break;
        }
        return p;
    }

    private SW.UIElement BuildRename()
    {
        WC.StackPanel p = Form();
        List<string> marcados = Marcados();
        p.Children.Add(Label($"Renomear {marcados.Count} layout(s) marcado(s), na ordem das abas", true));

        var prefixo = new WC.TextBox { Width = 200, HorizontalAlignment = SW.HorizontalAlignment.Left };
        var baseNome = new WC.TextBox { Width = 200, HorizontalAlignment = SW.HorizontalAlignment.Left };
        var sufixo = new WC.TextBox { Width = 200, HorizontalAlignment = SW.HorizontalAlignment.Left };
        var inicio = new WC.TextBox { Width = 60, HorizontalAlignment = SW.HorizontalAlignment.Left, Text = "1" };
        var digitos = new WC.TextBox { Width = 60, HorizontalAlignment = SW.HorizontalAlignment.Left, Text = "2" };
        var numerar = new WC.CheckBox { Content = "Numerar em sequencia", IsChecked = true, Margin = new SW.Thickness(0, 8, 0, 0) };
        var previa = new WC.TextBlock { Foreground = T.Muted, Margin = new SW.Thickness(0, 8, 0, 0), TextWrapping = SW.TextWrapping.Wrap };

        p.Children.Add(Label("Prefixo"));
        p.Children.Add(prefixo);
        p.Children.Add(Label("Base (vazio = mantem o nome atual)"));
        p.Children.Add(baseNome);
        p.Children.Add(numerar);
        var linha = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        var c1 = new WC.StackPanel { Margin = new SW.Thickness(0, 0, 12, 0) };
        c1.Children.Add(Label("Inicio"));
        c1.Children.Add(inicio);
        var c2 = new WC.StackPanel();
        c2.Children.Add(Label("Digitos"));
        c2.Children.Add(digitos);
        linha.Children.Add(c1);
        linha.Children.Add(c2);
        p.Children.Add(linha);
        p.Children.Add(Label("Sufixo"));
        p.Children.Add(sufixo);
        p.Children.Add(previa);

        RenameRule Regra() => new(prefixo.Text, baseNome.Text, sufixo.Text,
            int.TryParse(inicio.Text, out int i0) ? i0 : 1,
            int.TryParse(digitos.Text, out int d0) ? d0 : 2,
            numerar.IsChecked == true);

        void Previa()
        {
            RenameRule r = Regra();
            previa.Text = marcados.Count == 0
                ? "Marque os layouts na lista."
                : "Previa: " + string.Join(", ", marcados.Take(4).Select((n, i) => $"{n} -> {r.Nome(i, n)}")) + (marcados.Count > 4 ? ", ..." : "");
        }
        foreach (WC.TextBox tb in new[] { prefixo, baseNome, sufixo, inicio, digitos })
            tb.TextChanged += (_, _) => Previa();
        numerar.Click += (_, _) => Previa();
        Previa();

        p.Children.Add(ActionButton("Renomear", () => Executar(() => LayoutOps.Renomear(Doc!, Marcados(), Regra()))));
        return p;
    }

    private SW.UIElement BuildScale()
    {
        WC.StackPanel p = Form();
        p.Children.Add(Label($"Escala para {_vpsMarcadas.Count} viewport(s) marcada(s)", true));
        var combo = new WC.ComboBox { Width = 220, HorizontalAlignment = SW.HorizontalAlignment.Left, IsEditable = true, Margin = new SW.Thickness(0, 4, 0, 0) };
        foreach ((string nome, double den) in _escalas)
            combo.Items.Add($"{nome}   (1:{den:0.##})");
        foreach (int den in new[] { 50, 100, 200, 250, 500, 1000, 2000, 5000 })
            if (!_escalas.Any(e => Math.Abs(e.Denominador - den) < 1e-6))
                combo.Items.Add($"1:{den}");
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
        p.Children.Add(Label("Escala (escolha ou digite 1:N ou so N)"));
        p.Children.Add(combo);
        p.Children.Add(Label("As viewports travadas sao destravadas, escaladas e travadas de novo. A escala de anotacao so muda se existir no desenho."));
        p.Children.Add(ActionButton("Aplicar escala", () =>
        {
            string texto = (combo.Text ?? "").Trim();
            int p1 = texto.IndexOf("(1:", StringComparison.Ordinal);
            if (p1 >= 0)
                texto = texto[(p1 + 1)..].TrimEnd(')');
            texto = texto.Replace("1:", "").Replace(",", ".").Trim();
            if (!double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out double den) || den <= 0)
            {
                Say("Escala invalida. Use 1:N ou N.");
                return;
            }
            Executar(() => LayoutOps.Escalar(Doc!, VpsMarcadas(), den));
        }));
        return p;
    }

    private SW.UIElement BuildNumbering()
    {
        WC.StackPanel p = Form();
        p.Children.Add(Label("Numeracao automatica de pranchas", true));
        p.Children.Add(Label($"Os textos (ou atributos de bloco) da layer {LayoutOps.LayerNumero} recebem o numero (01, 02...) e os da layer {LayoutOps.LayerTotal} recebem o total (/NN), na ordem das abas. O Model nao conta."));
        p.Children.Add(Label("Configurar (uma vez por modelo de prancha)", true));
        p.Children.Add(Label("Clique no desenho o texto do numero e depois o do total; eles vao para as layers acima e a numeracao e feita em seguida. Se o carimbo e um bloco, clique no atributo dentro dele."));
        var botoes = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        botoes.Children.Add(ActionButton("Escolher os textos (NP > Configurar)", () => Enviar(C3DCommands.Layouts.Numerar + " Configurar")));
        p.Children.Add(botoes);
        p.Children.Add(Label("Atualizar", true));
        p.Children.Add(Label($"Renumera os {_folhas.Count} layouts na ordem atual das abas."));
        p.Children.Add(ActionButton("Numerar agora", () => Executar(() => LayoutOps.Numerar(Doc!))));
        return p;
    }

    private SW.UIElement BuildLayerState()
    {
        WC.StackPanel p = Form();
        p.Children.Add(Label("Aplicar layer state como override de viewport", true));
        p.Children.Add(Prop("Layer state", _layerState ?? "(escolha na lista)"));
        p.Children.Add(Prop("Viewports marcadas", $"{_vpsMarcadas.Count} em {_folhas.Count(f => f.Viewports.Any(v => _vpsMarcadas.Contains(v.Id)))} layout(s)"));
        p.Children.Add(Label("Marque as viewports clicando nelas na miniatura (ou na categoria Viewports). \"repetir nos demais\" marca as viewports na mesma posicao nos outros layouts. Depois de aplicar, a janela continua aberta para aplicar outro layer state em outras viewports."));
        p.Children.Add(ActionButton("Aplicar", AplicarLayerState));
        return p;
    }

    private SW.UIElement BuildStamp()
    {
        WC.StackPanel p = Form();
        StampBlock? bloco = _blocos.FirstOrDefault(b => b.Nome.Equals(_bloco, StringComparison.OrdinalIgnoreCase));
        if (bloco is null)
        {
            p.Children.Add(Label("Escolha o bloco de carimbo na lista.", true));
            p.Children.Add(Label("Sao listados os blocos com atributos encontrados nos layouts, com a quantidade de layouts em que aparecem."));
            return p;
        }

        List<string> marcados = Marcados();
        p.Children.Add(Label($"{bloco.Nome}: tags em {marcados.Count} layout(s) marcado(s)", true));
        p.Children.Add(Label("Preencha so as tags que devem mudar; as vazias ficam como estao. O valor atual mostrado e o do primeiro layout marcado."));

        Dictionary<string, string> atuais = new(StringComparer.OrdinalIgnoreCase);
        if (marcados.Count > 0 && Doc is Document doc)
        {
            try
            {
                foreach (StampValue v in LayoutStamp.LerValores(doc.Database, bloco.Nome, marcados.Take(1)))
                    atuais[v.Tag] = v.Valor;
            }
            catch (System.Exception)
            {
            }
        }

        var grid = new WC.Grid { Margin = new SW.Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        int r = 0;
        foreach (string tag in bloco.Tags)
        {
            grid.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
            var lbl = new WC.TextBlock { Text = tag, Foreground = T.Text, Margin = new SW.Thickness(0, 3, 10, 3), VerticalAlignment = SW.VerticalAlignment.Center };
            WC.Grid.SetRow(lbl, r);
            grid.Children.Add(lbl);
            var tb = new WC.TextBox
            {
                Margin = new SW.Thickness(0, 2, 0, 2),
                Text = _valoresCarimbo.TryGetValue(tag, out string? v0) ? v0 : "",
                ToolTip = atuais.TryGetValue(tag, out string? atual) && atual.Length > 0 ? $"Atual: {atual}" : "Atual: (vazio)"
            };
            string t = tag;
            tb.TextChanged += (_, _) => _valoresCarimbo[t] = tb.Text;
            WC.Grid.SetRow(tb, r);
            WC.Grid.SetColumn(tb, 1);
            grid.Children.Add(tb);
            r++;
        }
        p.Children.Add(grid);

        var botoes = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        botoes.Children.Add(ActionButton("Aplicar nos marcados", AplicarCarimbo));
        botoes.Children.Add(ActionButton("Limpar campos", () =>
        {
            _valoresCarimbo.Clear();
            BuildTabs();
        }));
        p.Children.Add(botoes);
        return p;
    }

    // ------------------------------------------------------------------
    // Acoes
    // ------------------------------------------------------------------

    private List<string> Marcados() => _folhas.Where(f => _layoutsMarcados.Contains(f.Nome)).Select(f => f.Nome).ToList();

    private List<ObjectId> VpsMarcadas() => _vpsMarcadas.ToList();

    private void Executar(Func<OpResult> op)
    {
        if (Doc is null)
        {
            Say("Nenhum desenho aberto.");
            return;
        }
        try
        {
            OpResult r = op();
            Say(r.Mensagem);
        }
        catch (System.Exception ex)
        {
            Say("Erro: " + ex.Message);
        }
        Refresh();
    }

    private void Abrir()
    {
        if ((_list.SelectedItem as WC.ListViewItem)?.Tag is Row { Data: SheetData f })
            Abrir(f.Nome);
        else if (_folhaAtual is not null)
            Abrir(_folhaAtual.Nome);
    }

    private void Abrir(string nome)
    {
        if (Doc is not Document doc)
            return;
        try
        {
            LayoutOps.Abrir(doc, nome);
            Say($"Layout \"{nome}\" aberto.");
            Refresh();
        }
        catch (System.Exception ex)
        {
            Say("Erro ao abrir: " + ex.Message);
        }
    }

    private void Excluir()
    {
        List<string> nomes = Marcados();
        if (nomes.Count == 0)
        {
            Say("Marque os layouts a excluir.");
            return;
        }
        SW.MessageBoxResult r = SW.MessageBox.Show(this,
            $"Excluir {nomes.Count} layout(s)?\n\n{string.Join(", ", nomes.Take(8))}{(nomes.Count > 8 ? ", ..." : "")}",
            "Excluir layouts", SW.MessageBoxButton.YesNo, SW.MessageBoxImage.Warning);
        if (r != SW.MessageBoxResult.Yes)
            return;
        _layoutsMarcados.Clear();
        Executar(() => LayoutOps.Excluir(Doc!, nomes));
    }

    private void AplicarLayerState()
    {
        if (_layerState is null)
        {
            SelectCategory("LAYERSTATE", "Aplicar");
            Say("Escolha um layer state na lista.");
            return;
        }
        if (_vpsMarcadas.Count == 0)
        {
            Say("Marque as viewports clicando nelas na miniatura.");
            return;
        }
        if (Doc is not Document doc)
            return;

        try
        {
            OpResult r = LayoutOps.AplicarLayerState(doc, _layerState, _vpsMarcadas.ToList());
            foreach (ObjectId id in _vpsMarcadas)
                if (_vpPorId.TryGetValue(id, out SheetViewport? v))
                    v.Aplicado = _layerState;
            _vpsMarcadas.Clear();
            Say(r.Mensagem + " Marque outras viewports para aplicar outro layer state.");
            doc.Editor.Regen();
            FillList();
            RenderGraphic();
            BuildTabs();
        }
        catch (System.Exception ex)
        {
            Say("Erro ao aplicar: " + ex.Message);
        }
    }

    private void AplicarCarimbo()
    {
        if (_bloco is null)
        {
            SelectCategory("CARIMBO", "Carimbo");
            Say("Escolha o bloco de carimbo na lista.");
            return;
        }
        List<string> nomes = Marcados();
        if (nomes.Count == 0)
        {
            Say("Marque os layouts que recebem o carimbo.");
            return;
        }
        var valores = _valoresCarimbo.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        if (valores.Count == 0)
        {
            Say("Preencha pelo menos uma tag.");
            return;
        }
        Executar(() => LayoutStamp.Aplicar(Doc!, _bloco, nomes, valores));
    }

    /// <summary>Roda um comando na linha de comando (prompts precisam de contexto de comando).</summary>
    private void Enviar(string comando)
    {
        if (Doc is not Document doc)
            return;
        LigarDocumento(doc);
        doc.SendStringToExecute("\x03\x03" + comando + " ", true, false, true);
        try
        {
            Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
        }
        catch
        {
            // O comando roda mesmo sem foco.
        }
        Say("Siga a linha de comando.");
    }

    private void MarcarLayouts(bool marcar)
    {
        _layoutsMarcados.Clear();
        if (marcar)
            foreach (SheetData f in _folhas)
                _layoutsMarcados.Add(f.Nome);
        if (!marcar)
            _vpsMarcadas.Clear();
        FillList();
        RenderGraphic();
        Status();
    }

    private void MarcarViewportsDaFolha(bool marcar)
    {
        if (_folhaAtual is null)
            return;
        foreach (SheetViewport v in _folhaAtual.Viewports)
        {
            if (marcar) _vpsMarcadas.Add(v.Id); else _vpsMarcadas.Remove(v.Id);
        }
        AtualizarViewports();
        if (_category == "VIEWPORTS")
            FillList();
        Status();
    }

    /// <summary>
    /// Para cada viewport marcada no layout mostrado, marca nos outros layouts a viewport
    /// com o mesmo centro relativo e tamanho parecido (pranchas do mesmo modelo).
    /// </summary>
    private void RepetirNosDemais()
    {
        SheetData? origem = _folhaAtual;
        if (origem is null)
            return;
        List<SheetViewport> marcadas = origem.Viewports.Where(v => _vpsMarcadas.Contains(v.Id)).ToList();
        if (marcadas.Count == 0)
        {
            Say($"Marque primeiro as viewports em \"{origem.Nome}\".");
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
                if (par is not null && _vpsMarcadas.Add(par.Id))
                {
                    novas++;
                    algum = true;
                }
            }
            if (algum)
                layouts++;
        }

        if (_category == "VIEWPORTS")
            FillList();
        AtualizarViewports();
        Status();
        Say(novas > 0
            ? $"{novas} viewport(s) marcada(s) em {layouts} outro(s) layout(s) pela posicao em \"{origem.Nome}\"."
            : "Nenhuma viewport na mesma posicao nos outros layouts.");
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

    // ------------------------------------------------------------------
    // Page setup
    // ------------------------------------------------------------------

    private SW.UIElement BuildPageSetupNamed()
    {
        WC.StackPanel p = Form();
        List<string> marcados = Marcados();
        p.Children.Add(Label($"Aplicar page setup em {marcados.Count} layout(s) marcado(s)", true));

        var combo = new WC.ComboBox { Width = 320, HorizontalAlignment = SW.HorizontalAlignment.Left, Margin = new SW.Thickness(0, 4, 0, 0) };
        foreach (PageSetupItem ps in _pageSetups)
            combo.Items.Add(new WC.ComboBoxItem { Content = ps.Nome, Tag = ps, ToolTip = $"{ps.Dispositivo}\n{ps.Papel}\n{ps.Estilo}" });
        combo.SelectedItem = combo.Items.Cast<WC.ComboBoxItem>().FirstOrDefault(i => (i.Tag as PageSetupItem)?.Nome == _pageSetup)
                             ?? combo.Items.Cast<WC.ComboBoxItem>().FirstOrDefault();
        var detalhe = new WC.TextBlock { Foreground = T.Muted, Margin = new SW.Thickness(0, 4, 0, 0), TextWrapping = SW.TextWrapping.Wrap };
        void Detalhe()
        {
            var ps = (combo.SelectedItem as WC.ComboBoxItem)?.Tag as PageSetupItem;
            _pageSetup = ps?.Nome;
            detalhe.Text = ps is null ? "O desenho nao tem page setups de papel. Importe de um DWG/DWT ou salve um a partir de um layout."
                                      : $"{ps.Dispositivo}  |  {ps.Papel}  |  {(ps.Estilo.Length > 0 ? ps.Estilo : "sem estilo")}";
        }
        combo.SelectionChanged += (_, _) => Detalhe();
        Detalhe();
        p.Children.Add(Label("Page setup do desenho"));
        p.Children.Add(combo);
        p.Children.Add(detalhe);
        p.Children.Add(ActionButton("Aplicar nos marcados", () =>
        {
            if (_pageSetup is null) { Say("Escolha um page setup."); return; }
            if (Marcados().Count == 0) { Say("Marque os layouts na lista (ou na vista Todos)."); return; }
            Executar(() => LayoutPageSetup.AplicarNomeado(Doc!, _pageSetup, Marcados()));
        }));

        p.Children.Add(Label("Trazer de outro arquivo", true));
        p.Children.Add(Label("Importa todos os page setups de papel de um DWG ou DWT (o template da empresa, por exemplo)."));
        p.Children.Add(ActionButton("Importar de DWG/DWT...", ImportarPageSetups));

        p.Children.Add(Label("Guardar a configuracao de um layout", true));
        p.Children.Add(Label($"Salva a configuracao de plotagem de \"{_folhaAtual?.Nome}\" (layout mostrado na vista) como page setup nomeado."));
        var nome = new WC.TextBox { Width = 240, HorizontalAlignment = SW.HorizontalAlignment.Left, Text = _folhaAtual is null ? "" : "PS " + _folhaAtual.Nome };
        p.Children.Add(nome);
        p.Children.Add(ActionButton("Salvar page setup", () =>
        {
            if (_folhaAtual is null) return;
            _pageSetup = nome.Text.Trim();
            Executar(() => LayoutPageSetup.SalvarDoLayout(Doc!, _folhaAtual.Nome, nome.Text));
        }));
        return p;
    }

    private SW.UIElement BuildPageSetupDirect()
    {
        WC.StackPanel p = Form();
        p.Children.Add(Label($"Configuracao direta em {Marcados().Count} layout(s) marcado(s)", true));
        p.Children.Add(Label("Os campos em \"(manter)\" ficam como estao em cada layout."));

        _dispositivos ??= LayoutPageSetup.Dispositivos();
        _estilos ??= LayoutPageSetup.Estilos();
        const string manter = "(manter)";

        var dev = new WC.ComboBox { Width = 320, HorizontalAlignment = SW.HorizontalAlignment.Left };
        dev.Items.Add(manter);
        foreach (string d in _dispositivos) dev.Items.Add(d);
        dev.SelectedIndex = 0;

        var papel = new WC.ComboBox { Width = 320, HorizontalAlignment = SW.HorizontalAlignment.Left };
        void Papeis()
        {
            papel.Items.Clear();
            papel.Items.Add(new WC.ComboBoxItem { Content = manter, Tag = null });
            string? d = dev.SelectedItem as string;
            if (d is not null && d != manter)
                foreach ((string canon, string nomeP) in LayoutPageSetup.Papeis(d))
                    papel.Items.Add(new WC.ComboBoxItem { Content = nomeP, Tag = canon });
            papel.SelectedIndex = 0;
        }
        dev.SelectionChanged += (_, _) => Papeis();
        Papeis();

        var estilo = new WC.ComboBox { Width = 320, HorizontalAlignment = SW.HorizontalAlignment.Left };
        estilo.Items.Add(manter);
        foreach (string e in _estilos) estilo.Items.Add(e);
        estilo.SelectedIndex = 0;

        var layout11 = new WC.CheckBox { Content = "Area de plotagem Layout, escala 1:1, origem 0,0", Margin = new SW.Thickness(0, 10, 0, 0) };

        p.Children.Add(Label("Plotter / PC3"));
        p.Children.Add(dev);
        p.Children.Add(Label("Papel (do plotter escolhido)"));
        p.Children.Add(papel);
        p.Children.Add(Label("Estilo de plotagem (CTB/STB)"));
        p.Children.Add(estilo);
        p.Children.Add(layout11);
        p.Children.Add(ActionButton("Aplicar nos marcados", () =>
        {
            if (Marcados().Count == 0) { Say("Marque os layouts na lista (ou na vista Todos)."); return; }
            string? d = dev.SelectedItem as string;
            string? canon = (papel.SelectedItem as WC.ComboBoxItem)?.Tag as string;
            string? st = estilo.SelectedItem as string;
            Executar(() => LayoutPageSetup.AplicarDireto(Doc!, Marcados(),
                d == manter ? null : d, canon, st == manter ? null : st, layout11.IsChecked == true));
        }));
        return p;
    }

    private void ImportarPageSetups()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importar page setups",
            Filter = "Desenhos e templates (*.dwg;*.dwt)|*.dwg;*.dwt|Todos os arquivos (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true)
            return;
        Executar(() => LayoutPageSetup.Importar(Doc!, dlg.FileName));
        SelectCategory("PAGESETUP", "Nomeado");
    }

    // ------------------------------------------------------------------
    // Mapa-chave
    // ------------------------------------------------------------------

    private SW.UIElement BuildKeyMap()
    {
        WC.StackPanel p = Form();
        int comArea = _kmResumo.Count(r => r.Regioes > 0), comMapa = _kmResumo.Count(r => r.TemMapa);
        p.Children.Add(Label("Mapa-chave automatico", true));
        p.Children.Add(Label("Em cada layout com viewport sobre a base, o plugin insere a base na area escolhida, desenha o contorno e o numero de todas as folhas e hachura a area da propria folha. Viewports de perfil ou detalhe que nao mostram a base ficam de fora."));

        p.Children.Add(Prop("Base", _kmCfg.Base.Count > 0 ? $"{_kmCfg.Base.Count} objeto(s) do Model" : "nao escolhida"));
        p.Children.Add(Prop("Area", _kmCfg.TemCaixa ? $"{_kmCfg.CaixaMax!.Value.X - _kmCfg.CaixaMin!.Value.X:0.#} x {_kmCfg.CaixaMax!.Value.Y - _kmCfg.CaixaMin!.Value.Y:0.#} no papel" : "nao escolhida"));
        p.Children.Add(Prop("Folhas", $"{comArea} layout(s) com area sobre a base; mapa em {comMapa}"));

        var passos = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        passos.Children.Add(ActionButton("1. Escolher base no Model", () => Enviar(C3DCommands.Layouts.MapaChave + " Base")));
        passos.Children.Add(ActionButton("2. Escolher area no layout", () => Enviar(C3DCommands.Layouts.MapaChave + " Area")));
        p.Children.Add(passos);

        p.Children.Add(Label("Opcoes", true));
        var rotulo = new WC.ComboBox { Width = 200, HorizontalAlignment = SW.HorizontalAlignment.Left };
        rotulo.Items.Add("Numero da prancha (01, 02...)");
        rotulo.Items.Add("Nome do layout");
        rotulo.Items.Add("Numero e nome");
        rotulo.SelectedIndex = Math.Clamp(_kmCfg.Rotulo, 0, 2);
        p.Children.Add(Label("Rotulo de cada folha"));
        p.Children.Add(rotulo);

        var cores = new (short Aci, string Nome)[] { (1, "Vermelho"), (2, "Amarelo"), (3, "Verde"), (4, "Ciano"), (5, "Azul"), (6, "Magenta"), (8, "Cinza"), (30, "Laranja") };
        var cor = new WC.ComboBox { Width = 200, HorizontalAlignment = SW.HorizontalAlignment.Left };
        foreach ((short aci, string nome) in cores)
            cor.Items.Add(new WC.ComboBoxItem { Content = $"{nome} ({aci})", Tag = aci });
        cor.SelectedItem = cor.Items.Cast<WC.ComboBoxItem>().FirstOrDefault(i => (short)i.Tag! == _kmCfg.CorDestaque) ?? cor.Items[0];
        p.Children.Add(Label("Cor da hachura da folha"));
        p.Children.Add(cor);

        var transp = new WC.TextBox { Width = 60, HorizontalAlignment = SW.HorizontalAlignment.Left, Text = _kmCfg.Transparencia.ToString() };
        p.Children.Add(Label("Transparencia da hachura (0 a 90 %)"));
        p.Children.Add(transp);

        var auto = new WC.CheckBox
        {
            Content = "Atualizar sozinho quando viewports ou layouts mudarem",
            IsChecked = _kmCfg.Automatico,
            Margin = new SW.Thickness(0, 10, 0, 0)
        };
        p.Children.Add(auto);

        var botoes = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        botoes.Children.Add(ActionButton("Salvar e gerar", () =>
        {
            if (Doc is not Document doc) return;
            _kmCfg.Rotulo = rotulo.SelectedIndex;
            _kmCfg.CorDestaque = (cor.SelectedItem as WC.ComboBoxItem)?.Tag is short c ? c : (short)1;
            _kmCfg.Transparencia = int.TryParse(transp.Text, out int t) ? Math.Clamp(t, 0, 90) : 50;
            _kmCfg.Automatico = auto.IsChecked == true;
            try
            {
                LayoutKeyMap.Gravar(doc, _kmCfg);
            }
            catch (System.Exception ex)
            {
                Say("Erro ao gravar: " + ex.Message);
                return;
            }
            Executar(() => LayoutKeyMap.Gerar(doc, true));
        }));
        botoes.Children.Add(ActionButton("Atualizar", () => Executar(() => LayoutKeyMap.Gerar(Doc!, false))));
        botoes.Children.Add(ActionButton("Remover", () => Executar(() => LayoutKeyMap.Remover(Doc!))));
        p.Children.Add(botoes);
        p.Children.Add(Label("Tudo o que o mapa-chave cria fica nas layers MAPA_CHAVE_* e e refeito a cada atualizacao; nao edite esses objetos a mao. A base fica no bloco C3D_MAPACHAVE: \"Salvar e gerar\" refaz o bloco a partir dos objetos do Model."));
        return p;
    }

    // ------------------------------------------------------------------
    // View frames
    // ------------------------------------------------------------------

    private SW.UIElement BuildViewFrames()
    {
        WC.StackPanel p = Form();
        int frames = _vfs.Count(v => v.Tipo == "Frame"), linhas = _vfs.Count - frames;
        p.Children.Add(Label("View frames do Civil 3D", true));
        p.Children.Add(Label(_vfs.Count == 0
            ? "O desenho nao tem view frames. Crie pelo assistente do Civil (Output > Create View Frames); eles aparecem aqui."
            : $"{frames} frame(s) e {linhas} match line(s) em {_vfs.Select(v => v.Grupo).Distinct().Count()} grupo(s). Selecione um para ver o layout ligado (pela folha do Civil); duplo clique faz zoom no Model."));
        p.Children.Add(Label("O que da para controlar daqui", true));
        p.Children.Add(Label("A API do Civil le estacas, eixo, grupo e folha de cada frame e deixa ligar/desligar e posicionar os rotulos. Criar ou mover view frames e criar folhas a partir deles nao e exposto pela API (so pelo assistente do Civil)."));

        var botoes = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        botoes.Children.Add(ActionButton("Ligar rotulos dos marcados", () => RotulosVf(true)));
        botoes.Children.Add(ActionButton("Desligar rotulos", () => RotulosVf(false)));
        p.Children.Add(botoes);
        p.Children.Add(ActionButton("Zoom no selecionado", ZoomViewFrame));
        return p;
    }

    private void RotulosVf(bool ligar)
    {
        if (_vfsMarcados.Count == 0)
        {
            Say("Marque os view frames ou match lines na lista.");
            return;
        }
        Executar(() => LayoutViewFrames.Rotulos(Doc!, _vfsMarcados.ToList(), ligar));
    }

    private void ZoomViewFrame()
    {
        ViewFrameItem? vf = (_list.SelectedItem as WC.ListViewItem)?.Tag is Row { Data: ViewFrameItem v } ? v : null;
        vf ??= _vfs.FirstOrDefault(v => _vfsMarcados.Contains(v.Id));
        if (vf?.Extensao is not Autodesk.AutoCAD.DatabaseServices.Extents3d e)
        {
            Say(_category == "VIEWFRAMES" ? "Selecione um view frame com extensao valida." : "Abra a categoria View frames e selecione um.");
            if (_category != "VIEWFRAMES")
                SelectCategory("VIEWFRAMES");
            return;
        }
        double mx = (e.MaxPoint.X - e.MinPoint.X) * 0.1, my = (e.MaxPoint.Y - e.MinPoint.Y) * 0.1;
        string P(double x, double y) => $"{x.ToString("0.###", CultureInfo.InvariantCulture)},{y.ToString("0.###", CultureInfo.InvariantCulture)}";
        Enviar($"_.TILEMODE 1 _.ZOOM _W {P(e.MinPoint.X - mx, e.MinPoint.Y - my)} {P(e.MaxPoint.X + mx, e.MaxPoint.Y + my)}");
    }

    private void Status()
    {
        int layoutsVp = _folhas.Count(f => f.Viewports.Any(v => _vpsMarcadas.Contains(v.Id)));
        string ls = _layerState is not null ? $"; layer state \"{_layerState}\"" : "";
        string bl = _bloco is not null ? $"; carimbo \"{_bloco}\"" : "";
        _status.Text = $"{_layoutsMarcados.Count} layout(s) marcado(s); {_vpsMarcadas.Count} viewport(s) em {layoutsVp} layout(s){ls}{bl}.";
    }
}
