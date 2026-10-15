using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WS = System.Windows.Shapes;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Explorador da rede viaria (C3DVIARIOPAINEL), no molde do Project Explorer do
/// Civil 3D: barra de categorias com contagem, barra de comandos, vista grafica
/// escura (planta ou corte) com grade e coordenadas, lista de objetos e abas de
/// detalhe (propriedades, recomendacoes do DNIT e itens relacionados).
/// Tema claro ou escuro (Preferencias).
/// </summary>
internal sealed class ViarioPanel : SW.Window
{
    private static ViarioPanel? _instance;

    // ------------------------------------------------------------------
    // Tema
    // ------------------------------------------------------------------

    private sealed class Theme
    {
        public WM.Brush Back = null!, Pane = null!, Header = null!, Line = null!, Text = null!, Muted = null!,
            Input = null!, Accent = null!, Warn = null!, Selected = null!;
    }

    private static readonly Theme Light = new()
    {
        Back = Brush(240, 240, 240), Pane = Brush(255, 255, 255), Header = Brush(229, 229, 229), Line = Brush(200, 200, 200),
        Text = Brush(20, 20, 20), Muted = Brush(105, 105, 105), Input = Brush(255, 255, 240), Accent = Brush(0, 95, 170),
        Warn = Brush(190, 110, 0), Selected = Brush(204, 228, 247)
    };

    private static readonly Theme Dark = new()
    {
        Back = Brush(45, 45, 48), Pane = Brush(37, 37, 38), Header = Brush(58, 58, 62), Line = Brush(70, 70, 74),
        Text = Brush(222, 222, 222), Muted = Brush(150, 150, 150), Input = Brush(30, 30, 30), Accent = Brush(80, 160, 255),
        Warn = Brush(230, 165, 20), Selected = Brush(38, 79, 120)
    };

    // A vista grafica e escura nos dois temas, como no Project Explorer.
    private static readonly WM.Brush GraphicBack = Brush(56, 58, 62);
    private static readonly WM.Brush GraphicGrid = Brush(66, 68, 73);
    private static readonly WM.Brush GraphicGridMajor = Brush(86, 88, 94);
    private static readonly WM.Brush GraphicRuler = Brush(44, 46, 50);
    private static readonly WM.Brush GraphicText = Brush(205, 205, 205);
    private static readonly WM.Brush HighlightBrush = Brush(255, 176, 0);

    private static readonly WM.FontFamily UiFont = new("Tahoma");
    private static readonly WM.FontFamily MonoFont = new("Consolas");

    private static bool _dark = LoadDarkPreference();
    private Theme T => _dark ? Dark : Light;

    // ------------------------------------------------------------------
    // Dados
    // ------------------------------------------------------------------

    private List<AxisInput> _axes = new();
    private ViarioResult _result = new();
    private List<RoadSection> _sections = new();
    private ViarioStore.Settings _settings = new();
    private EngineSettings _engine = new();
    private Dictionary<string, string> _names = new();
    private SignageOptions _signage = new();
    private List<Hint> _hints = new();
    private bool _civilActive;
    private bool _signExists;
    private bool _refreshQueued;

    private string _category = "EIXOS";
    private string? _selectedRow;
    private string _tab = "Propriedades";

    // ------------------------------------------------------------------
    // Controles (refeitos ao trocar o tema)
    // ------------------------------------------------------------------

    private WC.WrapPanel _categoryBar = new();
    private WC.Canvas _graphic = new();
    private WC.TextBlock _graphicInfo = new();
    private WC.TextBlock _coords = new();
    private WC.ListView _list = new();
    private WC.TextBox _find = new();
    private WC.StackPanel _tabStrip = new();
    private WC.ContentControl _tabContent = new();
    private WC.TextBlock _status = new();
    private WC.StackPanel _props = new();
    private WC.WrapPanel _actions = new();
    private readonly Dictionary<string, SW.FrameworkElement> _fields = new();

    private bool _showGrid = true;
    private bool _showAxes = true;
    private bool _showText = true;

    // Transformacao da vista grafica (para coordenadas, zoom e pan).
    private double _vScale, _vCx, _vCy, _vW, _vH, _vOx, _vOy;
    private double _zoom = 1;
    private V2 _pan = new(0, 0);
    private SW.Point? _dragFrom;

    private Action? _render;
    private Row? _current;

    // ------------------------------------------------------------------
    // Comandos
    // ------------------------------------------------------------------

    private sealed record Cmd(string Key, string Send, string Title, string Group, string Description, string[] Steps);

    private static readonly Cmd[] Commands =
    {
        new("EIXO", "C3DVIARIOEIXO ", "Lancar eixo", "Desenho",
            "Desenha eixos (retas e arcos) com a secao corrente; bordos e esquinas aparecem enquanto desenha.",
            new[] { "Clique os pontos ([Arco] alterna, [Desfazer] volta um ponto).", "Enter termina o eixo; o proximo clique comeca outro.", "[Secao] no primeiro ponto troca a secao." }),
        new("ADICIONAR", "C3DVIARIO AD ", "Adicionar eixos", "Desenho",
            "Transforma polilinhas, linhas, arcos ou circulos existentes em eixos da rede.", new[] { "Selecione as curvas e confirme." }),
        new("ROTATORIA", "C3DVIARIO RO ", "Rotatoria", "Desenho",
            "Anel circular com a secao corrente; eixos que chegam viram ramos.", new[] { "Clique o centro.", "Raio da ilha central (face do meio-fio)." }),
        new("MODELOS", "C3DVIARIOMODELOS ", "Modelos de intersecao", "Desenho",
            "Intersecoes prontas (cruz, T, Y, gotas, faixas livres, conversao, rotatorias).", new[] { "Escolha modelo, secoes e parametros.", "Clique o ponto e gire." }),
        new("REMOVEREIXO", "C3DVIARIO RE ", "Tirar eixo da rede", "Desenho",
            "A polilinha fica no desenho, mas deixa de ser eixo.", new[] { "Selecione os eixos e confirme." }),
        new("RAIO", "C3DVIARIORAIO ", "Raio", "Intersecoes",
            "Raio de esquina, de vertice do eixo, dos cantos de uma ilha ou de todas as esquinas de um eixo.",
            new[] { "Clique perto da esquina (ou dentro de uma ilha rosa).", "Arraste ou digite; [Padrao] volta ao da secao." }),
        new("ALARGAR", "C3DVIARIOALARGAR ", "Alargamento / taper", "Intersecoes",
            "Faixa adicional na aproximacao com armazenamento e taper.", new[] { "Clique no ramo, perto da intersecao.", "Lado, largura, taper e formato.", "Arraste o armazenamento." }),
        new("GOTA", "C3DVIARIOGOTA ", "Gota", "Intersecoes",
            "Ilha em gota na aproximacao de via sem canteiro (Tipo II).", new[] { "Clique no ramo, perto da intersecao.", "Largura, recuo da ponta, taper.", "Arraste o comprimento." }),
        new("CONVERSAO", "C3DVIARIOCONVERSAO ", "Conversao no canteiro", "Intersecoes",
            "Faixa de conversao a esquerda tirada do canteiro, junto da pista de quem chega (Tipo IV).", new[] { "Clique na aproximacao.", "Largura e taper.", "Arraste o armazenamento." }),
        new("ABERTURA", "C3DVIARIOABERTURA ", "Abertura de canteiro", "Intersecoes",
            "Abertura do canteiro para retorno no meio da quadra.", new[] { "Clique no eixo, no centro da abertura.", "Arraste metade do comprimento." }),
        new("LIVRE", "C3DVIARIOLIVRE ", "Faixa livre a direita", "Intersecoes",
            "Esquina com faixa livre de conversao e ilha triangular (Tipo III).", new[] { "Clique perto da esquina.", "Largura e afastamento.", "Arraste o raio externo." }),
        new("ELEMREMOVER", "C3DVIARIOELEMREMOVER ", "Remover elemento", "Intersecoes",
            "Remove alargamento, gota, conversao, abertura ou faixa livre.", new[] { "Clique perto do elemento e confirme." }),
        new("ILHA", "C3DVIARIOILHA ", "Eliminar ilha", "Linhas",
            "A ilha clicada vira pavimento; [Restaurar] ou [Todas] desfaz.", new[] { "Clique dentro da ilha." }),
        new("DESVINCULAR", "C3DVIARIO DE ", "Desvincular linhas", "Linhas",
            "Linhas geradas viram polilinhas comuns para edicao manual; o plugin nao as recria nem apaga.",
            new[] { "Selecione as linhas.", "Desvincular > Revincular devolve ao plugin." }),
        new("ATUALIZAR", "C3DVIARIO ATU ", "Atualizar rede", "Linhas", "Recalcula a rede inteira.", new[] { "Sem perguntas." }),
        new("CIVIL", "C3DVIARIOCIVIL G ", "Objetos do Civil", "Saidas",
            "Alinhamentos do Civil: eixos, bordos LD/LE e curb returns conectados.", new[] { "Gera ou atualiza; depois acompanham a rede." }),
        new("SINAL", "C3DVIARIOSINAL G ", "Sinalizacao horizontal", "Saidas",
            "Sinalizacao previa num bloco unico que acompanha a rede.", new[] { "Opcoes na categoria Sinalizacao." }),
        new("COTAR", "C3DVIARIOCOTAR ", "Cotar faixas", "Saidas",
            "Cadeia de cotas da secao em planta (faixas, canteiro, passeio) e largura total, na estaca clicada.",
            new[] { "Clique sobre o eixo onde quer a cota.", "[Detalhe] separa sarjeta e meio-fio; [Remover] apaga todas." }),
        new("TESTE", "C3DVIARIOTESTE  ", "Autoteste", "Saidas",
            "Confere linhas, sinalizacao e objetos do Civil contra a rede.", new[] { "Relatorio em %TEMP%\\C3DVIARIOTESTE.txt." })
    };

    private static readonly (string Id, string Icon, string Title)[] Categories =
    {
        ("SECOES", "SECAO", "Secoes"),
        ("EIXOS", "EIXO", "Eixos"),
        ("ELEMENTOS", "ALARGAR", "Elementos"),
        ("NOS", "MODELOS", "Intersecoes"),
        ("ESQUINAS", "RAIO", "Esquinas"),
        ("ILHAS", "GOTA", "Ilhas canalizadoras"),
        ("SINAL", "SINAL", "Sinalizacao horizontal"),
        ("CIVIL", "CIVIL", "Objetos do Civil"),
        ("AVISOS", "AVISOS", "Recomendacoes DNIT"),
        ("CONSULTA", "CONSULTA", "Consulta (tabelas dos manuais)"),
        ("CONFIG", "CONFIG", "Configuracao")
    };

    // ------------------------------------------------------------------
    // Abrir / atualizar
    // ------------------------------------------------------------------

    public static void ShowPanel(string? category = null)
    {
        if (_instance is null)
        {
            _instance = new ViarioPanel();
            _instance.Closed += (_, _) => _instance = null;
            AcApp.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApp.ShowModelessWindow(_instance);
        }
        else
        {
            _instance.Show();
            _instance.Activate();
        }

        if (category is not null)
        {
            _instance._category = category switch
            {
                "Sinalizacao" => "SINAL",
                "Secoes" => "SECOES",
                "Config" => "CONFIG",
                _ => category
            };
        }

        _instance.Refresh();
    }

    public static void Command(Document doc) => ShowPanel();

    public static void NotifyChanged(Document doc)
    {
        ViarioPanel? panel = _instance;
        if (panel is null || panel._refreshQueued)
            return;

        panel._refreshQueued = true;
        panel.Dispatcher.BeginInvoke(new Action(() =>
        {
            panel._refreshQueued = false;
            if (_instance == panel)
                panel.Refresh();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
    {
        if (_instance is null)
        {
            AcApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
            return;
        }

        NotifyChanged(e.Document);
    }

    private ViarioPanel()
    {
        Title = "Rede viaria - Explorador";
        Width = 1100;
        Height = 760;
        MinWidth = 760;
        MinHeight = 520;
        ShowInTaskbar = false;
        WindowStartupLocation = SW.WindowStartupLocation.CenterScreen;
        Icon = ViarioIcons.Image("PAINEL");
        FontFamily = UiFont;
        FontSize = 12;
        Content = BuildUi();
    }

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
        _graphic = new WC.Canvas { Background = GraphicBack, ClipToBounds = true };
        _graphicInfo = new WC.TextBlock { Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(8, 0, 8, 0) };
        _coords = new WC.TextBlock { Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(8, 0, 8, 0), FontFamily = MonoFont };
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
        _find.TextChanged += (_, _) => FillList();
        WC.DockPanel.SetDock(findPanel, WC.Dock.Right);
        commandRow.Children.Add(findPanel);
        commandRow.Children.Add(BuildCommandBar());
        WC.Grid.SetRow(commandRow, 1);
        root.Children.Add(commandRow);

        WC.Border graphic = new() { BorderBrush = T.Line, BorderThickness = new SW.Thickness(1), Child = _graphic, Margin = new SW.Thickness(4, 0, 4, 0) };
        _graphic.SizeChanged += (_, _) => _render?.Invoke();
        _graphic.MouseMove += OnGraphicMouseMove;
        _graphic.MouseWheel += OnGraphicWheel;
        _graphic.MouseDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                _zoom = 1;
                _pan = new V2(0, 0);
                _render?.Invoke();
                return;
            }

            _dragFrom = e.GetPosition(_graphic);
            _graphic.CaptureMouse();
        };
        _graphic.MouseUp += (_, _) =>
        {
            _dragFrom = null;
            _graphic.ReleaseMouseCapture();
        };
        WC.Grid.SetRow(graphic, 2);
        root.Children.Add(graphic);

        WC.DockPanel graphicBar = new() { Margin = new SW.Thickness(4, 0, 4, 0), Background = T.Header, Height = 24 };
        WC.StackPanel toggles = new() { Orientation = WC.Orientation.Horizontal };
        toggles.Children.Add(Toggle("Grade", () => _showGrid, v => _showGrid = v));
        toggles.Children.Add(Toggle("Eixos", () => _showAxes, v => _showAxes = v));
        toggles.Children.Add(Toggle("Textos", () => _showText, v => _showText = v));
        WC.DockPanel.SetDock(toggles, WC.Dock.Left);
        graphicBar.Children.Add(toggles);
        WC.DockPanel.SetDock(_coords, WC.Dock.Right);
        graphicBar.Children.Add(_coords);
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
        leftButtons.Children.Add(FooterButton("Preferencias...", ShowPreferences));
        leftButtons.Children.Add(FooterButton("Atualizar", Refresh));
        WC.DockPanel.SetDock(leftButtons, WC.Dock.Left);
        footer.Children.Add(leftButtons);
        WC.StackPanel rightButtons = new() { Orientation = WC.Orientation.Horizontal };
        rightButtons.Children.Add(FooterButton("Consulta DNIT", () => SelectCategory("CONSULTA")));
        WC.DockPanel.SetDock(rightButtons, WC.Dock.Right);
        footer.Children.Add(rightButtons);
        footer.Children.Add(_status);
        WC.Grid.SetRow(footer, 6);
        root.Children.Add(footer);

        return root;
    }

    private void AddStyles()
    {
        SW.Style header = new(typeof(WC.GridViewColumnHeader));
        header.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Header));
        header.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        header.Setters.Add(new SW.Setter(WC.Control.BorderBrushProperty, T.Line));
        header.Setters.Add(new SW.Setter(WC.Control.PaddingProperty, new SW.Thickness(4, 2, 4, 2)));
        header.Setters.Add(new SW.Setter(WC.Control.HorizontalContentAlignmentProperty, SW.HorizontalAlignment.Left));
        Resources[typeof(WC.GridViewColumnHeader)] = header;

        // Linha da lista com selecao nas cores do tema (o modelo padrao do Windows ignora o tema escuro).
        SW.FrameworkElementFactory bd = new(typeof(WC.Border), "Bd");
        bd.SetValue(WC.Border.BackgroundProperty, new SW.TemplateBindingExtension(WC.Control.BackgroundProperty));
        bd.SetValue(WC.Border.PaddingProperty, new SW.Thickness(0, 1, 0, 1));
        SW.FrameworkElementFactory presenter = new(typeof(WC.GridViewRowPresenter));
        presenter.SetValue(WC.GridViewRowPresenter.ColumnsProperty, new SW.TemplateBindingExtension(WC.GridView.ColumnCollectionProperty));
        presenter.SetValue(WC.ContentPresenter.ContentProperty, new SW.TemplateBindingExtension(WC.ContentControl.ContentProperty));
        bd.AppendChild(presenter);
        WC.ControlTemplate rowTemplate = new(typeof(WC.ListViewItem)) { VisualTree = bd };
        SW.Trigger hover = new() { Property = SW.UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, T.Header, "Bd"));
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
        button.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Header));
        button.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        button.Setters.Add(new SW.Setter(WC.Control.BorderBrushProperty, T.Line));
        button.Setters.Add(new SW.Setter(WC.Control.BorderThicknessProperty, new SW.Thickness(1)));
        button.Setters.Add(new SW.Setter(WC.Control.HorizontalContentAlignmentProperty, SW.HorizontalAlignment.Center));
        button.Setters.Add(new SW.Setter(WC.Control.VerticalContentAlignmentProperty, SW.VerticalAlignment.Center));
        // Modelo proprio: o cromado padrao pinta o fundo de azul claro no hover e o texto claro
        // do tema escuro some.
        SW.FrameworkElementFactory bbd = new(typeof(WC.Border), "Bd");
        bbd.SetValue(WC.Border.BackgroundProperty, new SW.TemplateBindingExtension(WC.Control.BackgroundProperty));
        bbd.SetValue(WC.Border.BorderBrushProperty, new SW.TemplateBindingExtension(WC.Control.BorderBrushProperty));
        bbd.SetValue(WC.Border.BorderThicknessProperty, new SW.TemplateBindingExtension(WC.Control.BorderThicknessProperty));
        bbd.SetValue(WC.Border.PaddingProperty, new SW.TemplateBindingExtension(WC.Control.PaddingProperty));
        bbd.SetValue(WC.Border.SnapsToDevicePixelsProperty, true);
        SW.FrameworkElementFactory bcp = new(typeof(WC.ContentPresenter));
        bcp.SetValue(SW.FrameworkElement.HorizontalAlignmentProperty, new SW.TemplateBindingExtension(WC.Control.HorizontalContentAlignmentProperty));
        bcp.SetValue(SW.FrameworkElement.VerticalAlignmentProperty, new SW.TemplateBindingExtension(WC.Control.VerticalContentAlignmentProperty));
        bcp.SetValue(WC.ContentPresenter.RecognizesAccessKeyProperty, true);
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

        SW.Style menu = new(typeof(WC.ContextMenu));
        menu.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Pane));
        menu.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        Resources[typeof(WC.ContextMenu)] = menu;
    }

    private WC.StackPanel BuildCommandBar()
    {
        WC.StackPanel tray = new() { Orientation = WC.Orientation.Horizontal };
        bool first = true;
        foreach (IGrouping<string, Cmd> group in Commands.GroupBy(c => c.Group))
        {
            if (!first)
                tray.Children.Add(new WC.Border { Width = 1, Background = T.Line, Margin = new SW.Thickness(5, 3, 5, 3) });
            first = false;
            WC.StackPanel bar = tray;
            foreach (Cmd cmd in group)
            {
                WC.Button button = new()
                {
                    Content = new WC.Image { Source = ViarioIcons.Image(cmd.Key), Width = 20, Height = 20 },
                    Padding = new SW.Thickness(2),
                    Background = WM.Brushes.Transparent,
                    BorderBrush = WM.Brushes.Transparent,
                    ToolTip = Tip(cmd)
                };
                WC.ToolTipService.SetInitialShowDelay(button, 250);
                WC.ToolTipService.SetShowDuration(button, 30000);
                Cmd c = cmd;
                button.Click += (_, _) => Send(c.Send, c.Title + ": siga a linha de comando.");
                bar.Children.Add(button);
            }
        }

        return tray;
    }

    private static WC.ToolTip Tip(Cmd cmd)
    {
        WC.StackPanel panel = new() { MaxWidth = 380 };
        panel.Children.Add(new WC.TextBlock { Text = cmd.Title, FontWeight = SW.FontWeights.Bold });
        WC.DockPanel row = new() { Margin = new SW.Thickness(0, 4, 0, 0) };
        WC.Image image = new() { Source = ViarioIcons.Image(cmd.Key), Width = 96, Height = 96, Margin = new SW.Thickness(0, 0, 8, 0), VerticalAlignment = SW.VerticalAlignment.Top };
        WC.DockPanel.SetDock(image, WC.Dock.Left);
        row.Children.Add(image);
        string command = cmd.Send.Trim().Split(' ')[0];
        row.Children.Add(new WC.TextBlock
        {
            Text = cmd.Description + "\n\n" + string.Join("\n", cmd.Steps.Select((s, i) => $"{i + 1}. {s}")) + $"\n\nComando: {command}",
            TextWrapping = SW.TextWrapping.Wrap
        });
        panel.Children.Add(row);
        return new WC.ToolTip { Content = panel };
    }

    private WC.CheckBox Toggle(string text, Func<bool> get, Action<bool> set)
    {
        WC.CheckBox box = new()
        {
            Content = text,
            IsChecked = get(),
            Foreground = T.Text,
            VerticalAlignment = SW.VerticalAlignment.Center,
            Margin = new SW.Thickness(8, 0, 4, 0)
        };
        box.Click += (_, _) =>
        {
            set(box.IsChecked == true);
            _render?.Invoke();
        };
        return box;
    }

    private static WC.Button FooterButton(string text, Action action)
    {
        WC.Button b = new() { Content = text, MinWidth = 96, Padding = new SW.Thickness(10, 3, 10, 3), Margin = new SW.Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => action();
        return b;
    }

    private void ShowPreferences()
    {
        WC.ContextMenu menu = new();
        WC.MenuItem light = new() { Header = "Tema claro", IsCheckable = true, IsChecked = !_dark };
        WC.MenuItem dark = new() { Header = "Tema escuro", IsCheckable = true, IsChecked = _dark };
        light.Click += (_, _) => SetTheme(false);
        dark.Click += (_, _) => SetTheme(true);
        menu.Items.Add(light);
        menu.Items.Add(dark);
        menu.IsOpen = true;
    }

    private void SetTheme(bool dark)
    {
        if (_dark == dark)
            return;
        _dark = dark;
        SaveDarkPreference(dark);
        Content = BuildUi();
        Refresh();
    }

    private static string PreferencesPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Civil3D2026Plugin", "viario_painel.txt");

    private static bool LoadDarkPreference()
    {
        try
        {
            string path = PreferencesPath();
            return System.IO.File.Exists(path) && System.IO.File.ReadAllText(path).Contains("tema=escuro", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void SaveDarkPreference(bool dark)
    {
        try
        {
            string path = PreferencesPath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, "tema=" + (dark ? "escuro" : "claro"));
        }
        catch
        {
            // Preferencia e conveniencia.
        }
    }

    private static WM.SolidColorBrush Brush(byte r, byte g, byte b)
    {
        WM.SolidColorBrush brush = new(WM.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void Send(string command, string message)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
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

    private void Say(string text) => _status.Text = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "  " + text;

    // ------------------------------------------------------------------
    // Dados
    // ------------------------------------------------------------------

    private void Refresh()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            Say("Nenhum desenho aberto.");
            return;
        }

        AcadDb.Database db = doc.Database;
        try
        {
            ViarioSync.Initialize();
            (_axes, _result, _) = ViarioSync.ComputeCurrent(db);
            _sections = ViarioStore.LoadSections(db);
            _settings = ViarioStore.LoadSettings(db);
            _engine = ViarioSync.EngineSettingsFor(db);
            _signage = ViarioStore.LoadSignage(db);
            _names = ViarioCivil.AxisNames(db);
            _civilActive = ViarioCivil.IsActive(db);
            _signExists = ViarioSignageSync.Exists(db);
            _hints = ViarioNorms.Check(_axes, _result, _engine, _sections);
        }
        catch (Exception ex)
        {
            Say("Erro ao ler a rede: " + ex.Message);
            return;
        }

        Title = $"Rede viaria - Explorador [{System.IO.Path.GetFileName(doc.Name)}]";
        FillCategories();
        FillList();
    }

    private int CountFor(string category) => category switch
    {
        "SECOES" => _sections.Count,
        "EIXOS" => _axes.Count,
        "ELEMENTOS" => _axes.Sum(a => a.Events.Count) + _engine.SlipLanes.Count,
        "NOS" => _result.Nodes.Count,
        "ESQUINAS" => _result.Corners.Count,
        "ILHAS" => Islands().Count,
        "SINAL" => _signExists ? 1 : 0,
        "CIVIL" => _names.Count,
        "AVISOS" => _hints.Count,
        "CONSULTA" => ViarioReference.All.Length,
        _ => -1
    };

    private void FillCategories()
    {
        _categoryBar.Children.Clear();
        foreach ((string id, string icon, string title) in Categories)
        {
            int count = CountFor(id);
            int warn = id == "AVISOS" ? _hints.Count(h => h.Level == HintLevel.Atencao) : 0;
            WC.StackPanel content = new() { Orientation = WC.Orientation.Horizontal };
            content.Children.Add(new WC.Image { Source = ViarioIcons.Image(icon), Width = 18, Height = 18 });
            if (count >= 0)
            {
                content.Children.Add(new WC.TextBlock
                {
                    Text = $" ({count})",
                    Foreground = warn > 0 ? T.Warn : T.Text,
                    VerticalAlignment = SW.VerticalAlignment.Center
                });
            }

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
        _selectedRow = null;
        _tab = "Propriedades";
        _zoom = 1;
        _pan = new V2(0, 0);
        FillCategories();
        FillList();
    }

    private List<OutputLine> Islands() => _result.Lines.Where(l => l.Level == EdgeLevel.Ilha && l.Closed).ToList();

    // ------------------------------------------------------------------
    // Lista de objetos (colunas por categoria)
    // ------------------------------------------------------------------

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string Icon { get; init; } = "PAINEL";
        public string[] Cells { get; init; } = Array.Empty<string>();
        public object? Data { get; init; }
        public string Flag { get; set; } = "";
        public WM.Brush? FlagBrush { get; set; }
    }

    private (string[] Columns, double[] Widths, List<Row> Rows) BuildRows(string category)
    {
        List<Row> rows = new();
        switch (category)
        {
            case "SECOES":
                foreach (RoadSection s in _sections)
                {
                    rows.Add(new Row
                    {
                        Id = "SEC:" + s.Id, Icon = "SECAO", Data = s,
                        Cells = new[]
                        {
                            s.Name + (s.Id == _settings.CurrentSectionId ? " (corrente)" : ""), s.Tipo, N(s.Pista, 2), (s.FaixasPorSentido * 2).ToString(CultureInfo.InvariantCulture),
                            N(s.Canteiro, 2), N(s.Sarjeta, 2), N(s.MeioFio, 2), N(s.Calcada, 2),
                            s.Simetrica ? "ambos" : $"{ViarioStore.LadosNome(s.LadosSarjeta)}/{ViarioStore.LadosNome(s.LadosMeioFio)}/{ViarioStore.LadosNome(s.LadosCalcada)}",
                            N(s.Raio, 2), N(s.Velocidade), _axes.Count(a => a.Section.Id == s.Id).ToString(CultureInfo.InvariantCulture)
                        }
                    });
                }

                return (new[] { "Nome", "Tipo", "Pista [m]", "Faixas", "Canteiro [m]", "Sarjeta [m]", "Meio-fio [m]", "Calcada [m]", "Lados", "Raio [m]", "km/h", "Eixos" },
                    new double[] { 170, 90, 65, 50, 80, 70, 80, 75, 110, 60, 45, 45 }, rows);

            case "EIXOS":
                for (int i = 0; i < _axes.Count; i++)
                {
                    AxisInput a = _axes[i];
                    rows.Add(new Row
                    {
                        Id = "EIXO:" + a.Key, Icon = a.Path.Closed ? "ROTATORIA" : "EIXO", Data = a,
                        Cells = new[]
                        {
                            AxisName(a, i), a.Section.Name, N(a.Path.Length, 2), Estaca(a.Path.Length, true),
                            a.Path.Segs.Count.ToString(CultureInfo.InvariantCulture), a.Path.Segs.Count(s => s.IsArc).ToString(CultureInfo.InvariantCulture),
                            a.Path.Segs.Any(s => s.IsArc) ? N(a.Path.Segs.Where(s => s.IsArc).Min(s => s.Radius), 2) : "-",
                            a.Path.Closed ? "anel" : a.Chained ? "mantida" : "livre",
                            a.Radius is double r ? N(r) : "secao", _result.Nodes.Count(n => n.AxisKeys.Contains(a.Key)).ToString(CultureInfo.InvariantCulture),
                            a.Events.Count == 0 ? "-" : string.Join(", ", a.Events.Select(e => KindName(e.Kind)))
                        }
                    });
                }

                return (new[] { "Nome", "Secao", "Comp. [m]", "Est. final", "Trechos", "Arcos", "R min [m]", "Concord.", "R esq.", "Nos", "Elementos" },
                    new double[] { 150, 120, 80, 80, 55, 50, 70, 70, 55, 40, 200 }, rows);

            case "ELEMENTOS":
                for (int i = 0; i < _axes.Count; i++)
                {
                    AxisInput a = _axes[i];
                    for (int k = 0; k < a.Events.Count; k++)
                    {
                        AxisEvent ev = a.Events[k];
                        rows.Add(new Row
                        {
                            Id = $"EV:{a.Key}:{k}", Icon = EventIcon(ev.Kind), Data = (a, ev),
                            Cells = new[]
                            {
                                KindName(ev.Kind), AxisName(a, i), N(ev.Width), N(ev.Length),
                                ev.Kind == AxisEventKind.Abertura ? "-" : N(ev.Taper) + (ev.Smooth ? " suave" : " reto"),
                                ev.Kind is AxisEventKind.Alargamento or AxisEventKind.Baia ? (ev.Side == 0 ? "ambos" : ev.Side > 0 ? "direita" : "esquerda") : "-"
                            }
                        });
                    }
                }

                foreach ((string key, SlipLane slip) in _engine.SlipLanes)
                {
                    rows.Add(new Row
                    {
                        Id = "SLIP:" + key, Icon = "LIVRE", Data = key,
                        Cells = new[] { "Faixa livre a direita", "-", N(slip.Width), "R " + N(slip.Radius), "-", "afast. " + N(slip.Setback) }
                    });
                }

                return (new[] { "Tipo", "Eixo", "Largura", "Comprimento", "Taper", "Lado" },
                    new double[] { 150, 140, 60, 85, 80, 80 }, rows);

            case "NOS":
                for (int i = 0; i < _result.Nodes.Count; i++)
                {
                    NodeInfo nd = _result.Nodes[i];
                    List<CornerInfo> corners = CornersAt(nd);
                    rows.Add(new Row
                    {
                        Id = $"NO:{nd.Point.X:0.##},{nd.Point.Y:0.##}", Icon = "MODELOS", Data = nd,
                        Cells = new[]
                        {
                            $"No {i + 1}", nd.LegCount.ToString(CultureInfo.InvariantCulture),
                            string.Join(", ", nd.AxisKeys.Select(AxisLabel)),
                            corners.Count == 0 ? "-" : string.Join(" ", corners.Select(c => N(c.Radius))), N(nd.Point.X), N(nd.Point.Y)
                        }
                    });
                }

                return (new[] { "No", "Ramos", "Eixos", "Raios [m]", "E", "N" }, new double[] { 70, 50, 220, 120, 95, 95 }, rows);

            case "ESQUINAS":
                for (int i = 0; i < _result.Corners.Count; i++)
                {
                    CornerInfo c = _result.Corners[i];
                    double turn = c.Arc is null ? 0 : Math.Abs(c.Arc.Sweep) * 180.0 / Math.PI;
                    (double vp, double co) = ViarioNorms.MinCornerRadius(turn);
                    bool slip = _engine.SlipLanes.ContainsKey(c.Key);
                    rows.Add(new Row
                    {
                        Id = "ESQ:" + c.Key, Icon = slip ? "LIVRE" : "RAIO", Data = c,
                        Cells = new[]
                        {
                            $"Esquina {i + 1}", AxisLabel(c.AxisA) + " x " + AxisLabel(c.AxisB), N(turn, 0), N(c.Radius),
                            slip ? "faixa livre" : _engine.CornerRadius.ContainsKey(c.Key) ? "especifico" : "secao/eixo", N(vp), N(co)
                        }
                    });
                }

                return (new[] { "Esquina", "Eixos", "Giro [graus]", "Raio [m]", "Origem", "VP min [m]", "CO min [m]" },
                    new double[] { 100, 200, 75, 60, 85, 70, 70 }, rows);

            case "ILHAS":
                List<OutputLine> islands = Islands();
                for (int i = 0; i < islands.Count; i++)
                {
                    OutputLine isl = islands[i];
                    rows.Add(new Row
                    {
                        Id = $"ILHA:{i}", Icon = "GOTA", Data = isl,
                        Cells = new[]
                        {
                            $"Ilha {i + 1}", N(ViarioEngine.RingArea(isl.Segs)), N(isl.Segs.Sum(s => s.Length)),
                            isl.Segs.Count(s => s.IsArc).ToString(CultureInfo.InvariantCulture)
                        }
                    });
                }

                return (new[] { "Ilha", "Area (m2)", "Perimetro (m)", "Arcos" }, new double[] { 90, 80, 90, 60 }, rows);

            case "SINAL":
                List<MarkLine> marks = SafeMarks(_signage);
                foreach ((string kind, string desc) in new[]
                         {
                             ("LFO", "Divisoria de fluxos opostos (amarela)"), ("LMS", "Divisao de faixas do mesmo sentido"),
                             ("LBO", "Linha de bordo"), ("FTP", "Faixa de pedestres zebrada (listras)"),
                             ("LRE", "Linha de retencao"), ("LDP", "Linha de 'De a preferencia' (rotatorias)")
                         })
                {
                    rows.Add(new Row
                    {
                        Id = "SH:" + kind, Icon = "SINAL", Data = kind,
                        Cells = new[] { kind, desc, marks.Count(m => m.Kind == kind).ToString(CultureInfo.InvariantCulture) }
                    });
                }

                return (new[] { "Marca", "Descricao", "Qtde" }, new double[] { 60, 280, 60 }, rows);

            case "CIVIL":
                for (int i = 0; i < _axes.Count; i++)
                {
                    AxisInput a = _axes[i];
                    rows.Add(new Row
                    {
                        Id = "CIV:" + a.Key, Icon = "CIVIL", Data = a,
                        Cells = new[] { AxisName(a, i), _names.TryGetValue(a.Key, out string? n) ? n : "(nao gerado)", a.Key }
                    });
                }

                return (new[] { "Eixo", "Alinhamento do Civil", "Handle" }, new double[] { 160, 220, 80 }, rows);

            case "AVISOS":
                foreach (Hint h in _hints.OrderByDescending(h => h.Level).ThenBy(h => h.Topic))
                {
                    rows.Add(new Row
                    {
                        Id = "AV:" + h.Key, Icon = h.Level == HintLevel.Atencao ? "AVISOS" : "INFO", Data = h,
                        Cells = new[] { h.Level == HintLevel.Atencao ? "atencao" : "info", h.Topic, h.Message, h.Source }
                    });
                }

                return (new[] { "Nivel", "Tema", "Recomendacao", "Fonte" }, new double[] { 70, 70, 460, 130 }, rows);

            case "CONSULTA":
                foreach (RefTable t in ViarioReference.All)
                    rows.Add(new Row { Id = "REF:" + t.Id, Icon = "CONSULTA", Data = t, Cells = new[] { t.Title, t.Source } });

                return (new[] { "Tabela", "Fonte" }, new double[] { 300, 280 }, rows);

            default:
                rows.Add(new Row { Id = "CFG", Icon = "CONFIG", Data = null, Cells = new[] { "Configuracao do desenho" } });
                return (new[] { "Item" }, new double[] { 260 }, rows);
        }
    }

    private void FillList()
    {
        (string[] columns, double[] widths, List<Row> rows) = BuildRows(_category);
        string find = _find.Text.Trim();
        if (find.Length > 0)
            rows = rows.Where(r => r.Cells.Any(c => c.Contains(find, StringComparison.OrdinalIgnoreCase))).ToList();

        foreach (Row r in rows)
        {
            List<Hint> hs = HintsFor(r).ToList();
            if (hs.Any(h => h.Level == HintLevel.Atencao))
            {
                r.Flag = "⚠";
                r.FlagBrush = T.Warn;
            }
            else if (hs.Count > 0)
            {
                r.Flag = "i";
                r.FlagBrush = T.Accent;
            }
        }

        _list.View = ListViewFor(columns, widths, rows);
        _list.ItemsSource = rows;

        Row? keep = rows.FirstOrDefault(r => r.Id == _selectedRow) ?? rows.FirstOrDefault();
        _list.SelectedItem = keep;
        if (keep is not null)
            _list.ScrollIntoView(keep);
        OnRowSelected();

        int warn = _hints.Count(h => h.Level == HintLevel.Atencao);
        Say($"{_axes.Count} eixo(s), {_result.Nodes.Count} no(s), {_result.Corners.Count} esquina(s) | " +
            $"DNIT: {warn} atencao, {_hints.Count - warn} info | Civil: {(_civilActive ? "ativo" : "-")} | " +
            $"Sinalizacao: {(_signExists ? "sim" : "-")} | v{C3DVersions.Viario}");
    }

    /// <summary>GridView com coluna de aviso, icone + primeira coluna e as demais por indice.</summary>
    private static readonly System.Text.RegularExpressions.Regex NumberLike =
        new(@"^[-+]?\d+([.,]\d+)?( ?(m|m2|km/h|graus|%))?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Coluna numerica: todos os valores preenchidos sao numeros (alinhada a direita, fonte mono).</summary>
    private static bool IsNumeric(List<Row> rows, int column)
    {
        bool any = false;
        foreach (Row r in rows)
        {
            if (column >= r.Cells.Length)
                continue;
            string c = r.Cells[column].Trim();
            if (c.Length == 0 || c == "-")
                continue;
            if (!NumberLike.IsMatch(c))
                return false;
            any = true;
        }

        return any;
    }

    /// <summary>GridView com coluna de aviso, icone + primeira coluna e as demais por indice; numeros a direita.</summary>
    private static WC.GridView ListViewFor(string[] columns, double[] widths, List<Row>? rows = null)
    {
        WC.GridView view = new();

        SW.FrameworkElementFactory flag = new(typeof(WC.TextBlock));
        flag.SetBinding(WC.TextBlock.TextProperty, new WD.Binding("Flag"));
        flag.SetBinding(WC.TextBlock.ForegroundProperty, new WD.Binding("FlagBrush"));
        flag.SetValue(WC.TextBlock.FontWeightProperty, SW.FontWeights.Bold);
        flag.SetValue(WC.TextBlock.TextAlignmentProperty, SW.TextAlignment.Center);
        view.Columns.Add(new WC.GridViewColumn { Header = "", Width = 20, CellTemplate = new SW.DataTemplate { VisualTree = flag } });

        for (int i = 0; i < columns.Length; i++)
        {
            if (i == 0)
            {
                SW.FrameworkElementFactory panel = new(typeof(WC.StackPanel));
                panel.SetValue(WC.StackPanel.OrientationProperty, WC.Orientation.Horizontal);
                SW.FrameworkElementFactory img = new(typeof(WC.Image));
                img.SetBinding(WC.Image.SourceProperty, new WD.Binding("Icon") { Converter = new IconConverter() });
                img.SetValue(SW.FrameworkElement.WidthProperty, 16.0);
                img.SetValue(SW.FrameworkElement.HeightProperty, 16.0);
                img.SetValue(SW.FrameworkElement.MarginProperty, new SW.Thickness(0, 0, 4, 0));
                panel.AppendChild(img);
                SW.FrameworkElementFactory txt = new(typeof(WC.TextBlock));
                txt.SetBinding(WC.TextBlock.TextProperty, new WD.Binding("Cells[0]"));
                txt.SetValue(WC.TextBlock.VerticalAlignmentProperty, SW.VerticalAlignment.Center);
                panel.AppendChild(txt);
                view.Columns.Add(new WC.GridViewColumn { Header = columns[0], Width = widths[0], CellTemplate = new SW.DataTemplate { VisualTree = panel } });
                continue;
            }

            bool numeric = rows is not null && IsNumeric(rows, i);
            SW.FrameworkElementFactory cell = new(typeof(WC.TextBlock));
            cell.SetBinding(WC.TextBlock.TextProperty, new WD.Binding($"Cells[{i}]"));
            if (numeric)
            {
                cell.SetValue(WC.TextBlock.TextAlignmentProperty, SW.TextAlignment.Right);
                cell.SetValue(WC.TextBlock.FontFamilyProperty, MonoFont);
                cell.SetValue(SW.FrameworkElement.MarginProperty, new SW.Thickness(0, 0, 6, 0));
            }

            WC.GridViewColumnHeader header = new() { Content = columns[i] };
            if (numeric)
                header.HorizontalContentAlignment = SW.HorizontalAlignment.Right;
            view.Columns.Add(new WC.GridViewColumn { Header = header, Width = i < widths.Length ? widths[i] : 80, CellTemplate = new SW.DataTemplate { VisualTree = cell } });
        }

        return view;
    }

    private sealed class IconConverter : WD.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            ViarioIcons.Image(value as string ?? "PAINEL");

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => WD.Binding.DoNothing;
    }

    private string AxisName(AxisInput axis, int index) =>
        _names.TryGetValue(axis.Key, out string? name) ? name : (axis.Path.Closed ? $"Rotatoria {index + 1}" : $"Eixo {index + 1}");

    private string AxisLabel(string key)
    {
        int i = _axes.FindIndex(a => a.Key == key);
        return i >= 0 ? AxisName(_axes[i], i) : key;
    }

    private List<CornerInfo> CornersAt(NodeInfo nd) => _result.Corners.Where(c => V2.Dist(c.Node, nd.Point) < 0.5).ToList();

    private static string EventIcon(AxisEventKind kind) => kind switch
    {
        AxisEventKind.Gota => "GOTA",
        AxisEventKind.Baia => "CONVERSAO",
        AxisEventKind.Abertura => "ABERTURA",
        _ => "ALARGAR"
    };

    private static string KindName(AxisEventKind kind) => kind switch
    {
        AxisEventKind.Alargamento => "Alargamento",
        AxisEventKind.Gota => "Gota",
        AxisEventKind.Baia => "Conversao no canteiro",
        AxisEventKind.Abertura => "Abertura de canteiro",
        _ => kind.ToString()
    };

    private IEnumerable<Hint> HintsFor(Row row)
    {
        switch (row.Data)
        {
            case RoadSection s:
                return _hints.Where(h => h.Topic == "Secao" && h.Target == s.Id);
            case AxisInput a when _category == "EIXOS":
                return _hints.Where(h => h.Target == a.Key || (h.Topic == "Secao" && h.Target == a.Section.Id));
            case ValueTuple<AxisInput, AxisEvent> pair:
                string prefix = KindName(pair.Item2.Kind).Split(' ')[0];
                return _hints.Where(h => h.Topic == "Elemento" && h.Target == pair.Item1.Key && h.Message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            case string slipKey when _category == "ELEMENTOS":
                return _hints.Where(h => h.Topic == "Elemento" && h.Target == slipKey);
            case NodeInfo nd:
                return _hints.Where(h => h.Topic is "No" or "Esquina" or "Rotatoria" && h.Where is V2 p && V2.Dist(p, nd.Point) < 30.0);
            case CornerInfo c:
                return _hints.Where(h => h.Target == c.Key);
            case OutputLine isl:
                return _hints.Where(h => h.Topic == "Ilha" && h.Where is V2 p && ViarioEngine.RingContains(isl.Segs, p));
            case Hint h:
                return new[] { h };
            default:
                return Enumerable.Empty<Hint>();
        }
    }

    // ------------------------------------------------------------------
    // Selecao e abas
    // ------------------------------------------------------------------

    private void OnRowSelected()
    {
        _current = _list.SelectedItem as Row;
        _selectedRow = _current?.Id;
        _zoom = 1;
        _pan = new V2(0, 0);
        BuildTabs();
    }

    private void BuildTabs()
    {
        List<(string Key, string Name, Func<SW.UIElement> Build)> tabs = new() { ("Propriedades", "Propriedades", BuildProperties) };
        Row? row = _current;

        switch (row?.Data)
        {
            case RoadSection s:
                List<Row> uses = _axes.Select((a, i) => (a, i)).Where(x => x.a.Section.Id == s.Id)
                    .Select(x => new Row { Icon = "EIXO", Data = x.a, Cells = new[] { AxisName(x.a, x.i), N(x.a.Path.Length) } }).ToList();
                tabs.Add(("Eixos", $"Eixos ({uses.Count})", () => SubList(new[] { "Eixo", "Comprimento" }, new double[] { 200, 100 }, uses)));
                break;
            case AxisInput a when _category == "EIXOS":
                List<Row> geo = ViarioChain.Elements(a.Path).Select(e => new Row
                {
                    Icon = e.Tipo == "Arco" ? "RAIO" : "EIXO", Data = a,
                    Cells = new[]
                    {
                        e.Index.ToString(CultureInfo.InvariantCulture), e.Tipo, N(e.Length, 2), e.Tipo == "Arco" ? N(e.Radius, 2) : "-",
                        Estaca(e.S0, true), Estaca(e.S1, true), N(e.A.X, 3), N(e.A.Y, 3), N(e.B.X, 3), N(e.B.Y, 3)
                    }
                }).ToList();
                tabs.Add(("Geometria", $"Geometria ({geo.Count})", () => SubList(
                    new[] { "#", "Tipo", "Comp. [m]", "Raio [m]", "Est. inicial", "Est. final", "E inicial", "N inicial", "E final", "N final" },
                    new double[] { 40, 60, 75, 75, 85, 85, 95, 95, 95, 95 }, geo)));
                List<Row> evs = a.Events.Select(ev => new Row { Icon = EventIcon(ev.Kind), Data = (a, ev), Cells = new[] { KindName(ev.Kind), N(ev.Width), N(ev.Length) } }).ToList();
                tabs.Add(("Elementos", $"Elementos ({evs.Count})", () => SubList(new[] { "Tipo", "Largura [m]", "Comp. [m]" }, new double[] { 180, 80, 80 }, evs)));
                break;
            case NodeInfo nd:
                List<Row> cs = CornersAt(nd).Select(c => new Row { Icon = "RAIO", Data = c, Cells = new[] { AxisLabel(c.AxisA) + " x " + AxisLabel(c.AxisB), N(c.Radius) } }).ToList();
                tabs.Add(("Esquinas", $"Esquinas ({cs.Count})", () => SubList(new[] { "Eixos", "Raio" }, new double[] { 240, 60 }, cs)));
                break;
            case Hint h when ViarioReference.Find(h.Source) is RefTable table:
                tabs.Add(("Consulta", "Consulta: " + h.Source, () => ReferenceView(table)));
                break;
            case RefTable t:
                tabs.Insert(0, ("Tabela", "Tabela", () => ReferenceView(t)));
                break;
        }

        if (row is not null && row.Data is not RefTable && row.Data is not Hint && _category is not "SINAL" and not "CONFIG")
            tabs.Add(("Recomendacoes", $"Recomendacoes ({HintsFor(row).Count()})", BuildHints));

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
        }

        // Propriedades sempre montam o grafico do item; as outras abas so o conteudo.
        SW.UIElement props = BuildProperties();
        _tabContent.Content = _tab == "Propriedades" ? props : tabs.First(t => t.Key == _tab).Build();
        _render?.Invoke();
    }

    private SW.UIElement SubList(string[] columns, double[] widths, List<Row> rows)
    {
        WC.ListView list = new() { BorderThickness = new SW.Thickness(0), Background = T.Pane, Foreground = T.Text };
        list.View = ListViewFor(columns, widths, rows);
        list.ItemsSource = rows;
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is Row r)
                ZoomRow(r);
        };
        return list;
    }

    // ---------------- Recomendacoes ----------------

    private SW.UIElement BuildHints()
    {
        WC.DockPanel dock = new();
        WC.TextBlock help = new()
        {
            Text = "Clique: mostra o local na vista. Duplo clique: zoom no desenho. Clique na fonte: abre a tabela de consulta. " +
                   "Valores de referencia; nao substituem o projeto.",
            Foreground = T.Muted, TextWrapping = SW.TextWrapping.Wrap, Margin = new SW.Thickness(6, 4, 6, 4), FontSize = 11
        };
        WC.DockPanel.SetDock(help, WC.Dock.Bottom);
        dock.Children.Add(help);

        List<Hint> hints = _current is null ? new List<Hint>() : HintsFor(_current).OrderByDescending(h => h.Level).ToList();
        if (hints.Count == 0)
        {
            dock.Children.Add(new WC.TextBlock { Text = "Nenhuma recomendacao para este item.", Foreground = T.Muted, Margin = new SW.Thickness(8) });
            return dock;
        }

        WC.StackPanel stack = new() { Margin = new SW.Thickness(4) };
        foreach (Hint h in hints)
            stack.Children.Add(HintLine(h));
        dock.Children.Add(new WC.ScrollViewer { Content = stack, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto });
        return dock;
    }

    private SW.UIElement HintLine(Hint h)
    {
        WC.DockPanel line = new() { Margin = new SW.Thickness(0, 2, 0, 2), Background = WM.Brushes.Transparent, Cursor = WI.Cursors.Hand };
        WC.TextBlock glyph = new()
        {
            Text = h.Level == HintLevel.Atencao ? "⚠ " : "i  ",
            Foreground = h.Level == HintLevel.Atencao ? T.Warn : T.Accent,
            FontWeight = SW.FontWeights.Bold,
            VerticalAlignment = SW.VerticalAlignment.Top
        };
        WC.DockPanel.SetDock(glyph, WC.Dock.Left);
        line.Children.Add(glyph);

        WC.TextBlock source = new()
        {
            Text = h.Source,
            Foreground = T.Accent,
            TextDecorations = SW.TextDecorations.Underline,
            Margin = new SW.Thickness(8, 0, 0, 0),
            ToolTip = "Abrir a tabela de consulta"
        };
        source.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenReference(h.Source);
        };
        WC.DockPanel.SetDock(source, WC.Dock.Right);
        line.Children.Add(source);
        line.Children.Add(new WC.TextBlock { Text = h.Message, TextWrapping = SW.TextWrapping.Wrap, Foreground = T.Text });
        line.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ZoomHint(h);
                return;
            }

            if (h.Where is V2 p)
            {
                _render = () => RenderMap(new Box2(p.X - 40, p.Y - 40, p.X + 40, p.Y + 40), sh => sh.Marker(p));
                _render();
            }
        };
        return line;
    }

    private void ZoomHint(Hint h)
    {
        if (h.Where is V2 p)
        {
            ZoomTo(new Box2(p.X - 30, p.Y - 30, p.X + 30, p.Y + 30));
            Say("Zoom no local da recomendacao.");
            return;
        }

        AxisInput? axis = h.Topic == "Secao" ? _axes.FirstOrDefault(a => a.Section.Id == h.Target) : _axes.FirstOrDefault(a => a.Key == h.Target);
        if (axis is not null)
        {
            ZoomTo(axis.Path.Bounds().Inflate(axis.Section.TotalWidth));
            Say("Zoom no eixo da recomendacao.");
        }
        else
        {
            Say("Esta recomendacao nao tem local no desenho.");
        }
    }

    /// <summary>Abre a tabela de consulta da fonte numa janela pequena.</summary>
    private void OpenReference(string source)
    {
        RefTable? table = ViarioReference.Find(source);
        if (table is null)
        {
            Say("Sem tabela de consulta para " + source + ".");
            return;
        }

        SW.Window window = new()
        {
            Title = "Consulta - " + table.Title,
            Width = 680,
            Height = 440,
            Owner = this,
            WindowStartupLocation = SW.WindowStartupLocation.CenterOwner,
            Background = T.Pane,
            Foreground = T.Text,
            FontFamily = UiFont,
            FontSize = 12,
            ShowInTaskbar = false
        };
        foreach (object key in Resources.Keys)
            window.Resources[key] = Resources[key];
        window.Content = ReferenceView(table);
        window.Show();
    }

    private SW.UIElement ReferenceView(RefTable table)
    {
        WC.DockPanel dock = new() { Margin = new SW.Thickness(6) };
        WC.StackPanel head = new();
        head.Children.Add(new WC.TextBlock { Text = table.Title, FontWeight = SW.FontWeights.Bold, FontSize = 13, Foreground = T.Text });
        head.Children.Add(new WC.TextBlock { Text = table.Source + "   (no PDF: pagina impressa + 2)", Foreground = T.Muted, Margin = new SW.Thickness(0, 2, 0, 4) });
        WC.DockPanel.SetDock(head, WC.Dock.Top);
        dock.Children.Add(head);

        if (table.Note.Length > 0)
        {
            WC.TextBlock note = new() { Text = table.Note, TextWrapping = SW.TextWrapping.Wrap, Foreground = T.Muted, Margin = new SW.Thickness(0, 6, 0, 0) };
            WC.DockPanel.SetDock(note, WC.Dock.Bottom);
            dock.Children.Add(note);
        }

        WC.ListView list = new() { Background = T.Pane, Foreground = T.Text, BorderBrush = T.Line };
        WC.GridView view = new();
        for (int i = 0; i < table.Columns.Length; i++)
        {
            view.Columns.Add(new WC.GridViewColumn
            {
                Header = table.Columns[i],
                Width = i == 0 ? 200 : Math.Max(110, 420.0 / Math.Max(1, table.Columns.Length - 1)),
                DisplayMemberBinding = new WD.Binding($"Cells[{i}]")
            });
        }

        list.View = view;
        list.ItemsSource = table.Rows.Select(r => new Row { Cells = r }).ToList();
        dock.Children.Add(list);
        return dock;
    }

    // ------------------------------------------------------------------
    // Propriedades
    // ------------------------------------------------------------------

    private SW.UIElement BuildProperties()
    {
        _props = new WC.StackPanel();
        _actions = new WC.WrapPanel { Margin = new SW.Thickness(4) };
        _fields.Clear();
        _render = null;

        switch (_current?.Data)
        {
            case RoadSection s: ShowSection(s); break;
            case AxisInput a when _category == "CIVIL": ShowCivil(a); break;
            case AxisInput a: ShowAxis(a); break;
            case ValueTuple<AxisInput, AxisEvent> pair: ShowEvent(pair.Item1, pair.Item2); break;
            case string slip when _category == "ELEMENTOS": ShowSlip(slip); break;
            case NodeInfo nd: ShowNode(nd); break;
            case CornerInfo c: ShowCorner(c); break;
            case OutputLine isl: ShowIsland(isl); break;
            case string kind when _category == "SINAL": ShowSignage(kind); break;
            case Hint h: ShowHint(h); break;
            case RefTable t:
                Prop("Fonte", t.Source);
                Act("Abrir em janela", () => OpenReference(t.Keys.FirstOrDefault() ?? t.Source), true);
                _render = () => RenderMap(null, null);
                break;
            default:
                if (_category == "CONFIG")
                    ShowConfig();
                else if (_category == "SINAL")
                    ShowSignage(null);
                else
                {
                    Prop("Rede", $"{_axes.Count} eixo(s), {_result.Nodes.Count} no(s)");
                    if (_category == "SECOES")
                        Act("Nova secao", () => NewSection(null), true);
                    _render = () => RenderMap(null, null);
                }

                break;
        }

        WC.DockPanel dock = new();
        WC.DockPanel.SetDock(_actions, WC.Dock.Bottom);
        dock.Children.Add(_actions);
        dock.Children.Add(new WC.ScrollViewer { Content = _props, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto });
        return dock;
    }

    private void Category(string text) => _props.Children.Add(new WC.TextBlock
    {
        Text = text,
        FontWeight = SW.FontWeights.Bold,
        Background = T.Header,
        Foreground = T.Text,
        Padding = new SW.Thickness(4, 2, 4, 2)
    });

    private WC.Grid PropRow(string label)
    {
        WC.Grid row = new();
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        row.Children.Add(new WC.Border
        {
            BorderBrush = T.Line,
            BorderThickness = new SW.Thickness(0, 0, 1, 1),
            Child = new WC.TextBlock { Text = label, Margin = new SW.Thickness(4, 2, 4, 2), Foreground = T.Muted, TextWrapping = SW.TextWrapping.Wrap }
        });
        WC.Border line = new() { BorderBrush = T.Line, BorderThickness = new SW.Thickness(0, 0, 0, 1) };
        WC.Grid.SetColumn(line, 1);
        row.Children.Add(line);
        return row;
    }

    private void Prop(string label, string value)
    {
        WC.Grid row = PropRow(label);
        WC.TextBlock text = new() { Text = value, Margin = new SW.Thickness(4, 2, 4, 2), TextWrapping = SW.TextWrapping.Wrap, Foreground = T.Text };
        WC.Grid.SetColumn(text, 1);
        row.Children.Add(text);
        _props.Children.Add(row);
    }

    private WC.TextBox EditProp(string key, string label, string value)
    {
        WC.Grid row = PropRow(label);
        WC.TextBox box = new()
        {
            Text = value,
            BorderThickness = new SW.Thickness(0),
            Padding = new SW.Thickness(3, 2, 3, 2),
            Background = T.Input,
            Foreground = T.Text,
            CaretBrush = T.Text
        };
        WC.Grid.SetColumn(box, 1);
        row.Children.Add(box);
        _props.Children.Add(row);
        _fields[key] = box;
        return box;
    }

    /// <summary>Lista de escolha (botao + menu), legivel nos dois temas.</summary>
    private WC.Button ChoiceProp(string key, string label, IEnumerable<(string Text, string Value)> items, string selected, Action? changed = null)
    {
        List<(string Text, string Value)> options = items.ToList();
        WC.Grid row = PropRow(label);
        string shown = options.Where(o => o.Value == selected).Select(o => o.Text).FirstOrDefault() ?? selected;
        WC.Button button = new()
        {
            HorizontalContentAlignment = SW.HorizontalAlignment.Left,
            Background = T.Input,
            Foreground = T.Text,
            BorderThickness = new SW.Thickness(0),
            Padding = new SW.Thickness(3, 2, 3, 2),
            Tag = selected,
            Content = shown + "  ▾"
        };
        button.Click += (_, _) =>
        {
            WC.ContextMenu menu = new() { PlacementTarget = button };
            foreach ((string text, string value) in options)
            {
                WC.MenuItem mi = new() { Header = text, IsCheckable = true, IsChecked = (string)button.Tag == value };
                mi.Click += (_, _) =>
                {
                    button.Tag = value;
                    button.Content = text + "  ▾";
                    changed?.Invoke();
                };
                menu.Items.Add(mi);
            }

            menu.IsOpen = true;
        };
        WC.Grid.SetColumn(button, 1);
        row.Children.Add(button);
        _props.Children.Add(row);
        _fields[key] = button;
        return button;
    }

    private WC.CheckBox CheckProp(string key, string label, bool value, Action? changed = null)
    {
        WC.Grid row = PropRow(label);
        WC.CheckBox box = new() { IsChecked = value, Margin = new SW.Thickness(4, 3, 4, 3) };
        box.Click += (_, _) => changed?.Invoke();
        WC.Grid.SetColumn(box, 1);
        row.Children.Add(box);
        _props.Children.Add(row);
        _fields[key] = box;
        return box;
    }

    private void Act(string text, Action action, bool primary = false)
    {
        WC.Button b = new()
        {
            Content = text,
            Margin = new SW.Thickness(0, 0, 4, 4),
            Padding = new SW.Thickness(8, 2, 8, 2),
            FontWeight = primary ? SW.FontWeights.Bold : SW.FontWeights.Normal
        };
        b.Click += (_, _) => action();
        _actions.Children.Add(b);
    }

    private double? Number(string key, double min = 0)
    {
        if (!_fields.TryGetValue(key, out SW.FrameworkElement? element) || element is not WC.TextBox box)
            return null;
        if (double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= min)
        {
            box.Foreground = T.Text;
            return v;
        }

        box.Foreground = WM.Brushes.Red;
        return null;
    }

    private string Text(string key)
    {
        if (!_fields.TryGetValue(key, out SW.FrameworkElement? e))
            return "";
        return e switch
        {
            WC.TextBox t => t.Text.Trim(),
            WC.Button b => b.Tag as string ?? "",
            _ => ""
        };
    }

    private bool Checked(string key) => _fields.TryGetValue(key, out SW.FrameworkElement? e) && e is WC.CheckBox { IsChecked: true };

    // ---------------- Secoes ----------------

    private void ShowSection(RoadSection s)
    {
        Category("Identificacao");
        EditProp("Name", "Nome", s.Name);
        ChoiceProp("Tipo", "Tipo de via", ViarioStore.Tipos.Select(t => (t, t)), s.Tipo, () => _render?.Invoke());
        EditProp("Velocidade", "Velocidade (km/h)", N(s.Velocidade));
        Category("Pista");
        EditProp("Pista", "Largura de rolamento (m)", N(s.Pista));
        EditProp("Faixas", "Faixas, dois sentidos (0 = auto)", s.Faixas.ToString(CultureInfo.InvariantCulture));
        EditProp("Canteiro", "Canteiro central (m)", N(s.Canteiro));
        Category("Bordos (lados no sentido do eixo)");
        IEnumerable<(string, string)> lados = ViarioStore.LadosOpcoes.Select(o => (o.Text, o.Text));
        EditProp("Sarjeta", "Sarjeta (m)", N(s.Sarjeta));
        ChoiceProp("LadosSarjeta", "Sarjeta em", lados, ViarioStore.LadosNome(s.LadosSarjeta), () => _render?.Invoke());
        EditProp("MeioFio", "Meio-fio (m)", N(s.MeioFio));
        ChoiceProp("LadosMeioFio", "Meio-fio em", lados, ViarioStore.LadosNome(s.LadosMeioFio), () => _render?.Invoke());
        EditProp("Calcada", "Calcada (m)", N(s.Calcada));
        ChoiceProp("LadosCalcada", "Calcada em", lados, ViarioStore.LadosNome(s.LadosCalcada), () => _render?.Invoke());
        EditProp("Raio", "Raio de esquina (m)", N(s.Raio));
        Category("Calculado");
        Prop("Faixa de rolamento", N((s.Pista / 2.0) / s.FaixasPorSentido) + " m");
        Prop("Largura total", N(s.TotalWidth) + " m");
        Prop("Codigo interno", s.Id + (s.Id == _settings.CurrentSectionId ? " (secao corrente)" : ""));

        foreach (SW.FrameworkElement f in _fields.Values)
        {
            if (f is WC.TextBox box)
                box.TextChanged += (_, _) => _render?.Invoke();
        }

        Act("Salvar", () => SaveSection(s), true);
        Act("Tornar corrente", () => MakeCurrent(s));
        Act("Aplicar aos selecionados", () => ApplyToSelected(s));
        Act("Nova", () => NewSection(null));
        Act("Duplicar", () => NewSection(s));
        Act("Excluir", () => DeleteSection(s));
        _render = () => RenderSection(EditedSection(s) ?? s);
    }

    private RoadSection? EditedSection(RoadSection basis)
    {
        RoadSection s = basis.Clone();
        if (Text("Name") is { Length: > 0 } name)
            s.Name = name;
        if (Text("Tipo") is { Length: > 0 } tipo)
            s.Tipo = tipo;
        double? pista = Number("Pista", 0.5), faixas = Number("Faixas"), canteiro = Number("Canteiro"), sarjeta = Number("Sarjeta"),
            meio = Number("MeioFio"), calcada = Number("Calcada"), raio = Number("Raio"), vel = Number("Velocidade");
        if (pista is null || faixas is null || canteiro is null || sarjeta is null || meio is null || calcada is null || raio is null || vel is null)
            return null;

        s.Pista = pista.Value;
        s.Faixas = (int)Math.Round(faixas.Value);
        s.Canteiro = canteiro.Value;
        s.Sarjeta = sarjeta.Value;
        s.MeioFio = meio.Value;
        s.Calcada = calcada.Value;
        s.Raio = raio.Value;
        s.Velocidade = vel.Value;
        if (Text("LadosSarjeta") is { Length: > 0 } ls)
            s.LadosSarjeta = ViarioStore.LadosDe(ls);
        if (Text("LadosMeioFio") is { Length: > 0 } lm)
            s.LadosMeioFio = ViarioStore.LadosDe(lm);
        if (Text("LadosCalcada") is { Length: > 0 } lc)
            s.LadosCalcada = ViarioStore.LadosDe(lc);
        return s;
    }

    private void SaveSection(RoadSection basis)
    {
        RoadSection? s = EditedSection(basis);
        if (s is null)
        {
            Say("Corrija os valores em vermelho.");
            return;
        }

        Edit("Secao salva: " + s.Name, db =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            foreach (RoadSection other in _sections.Where(o => o.Id != s.Id))
                ViarioStore.SaveSection(tr, db, other);
            ViarioStore.SaveSection(tr, db, s);
            tr.Commit();
        });
    }

    private void NewSection(RoadSection? copy)
    {
        RoadSection s = (copy ?? ViarioStore.DefaultSection()).Clone();
        s.Id = ViarioStore.NextSectionId(_sections);
        s.Name = copy is null ? "Nova secao " + s.Id : copy.Name + " (copia)";
        _selectedRow = "SEC:" + s.Id;
        Edit("Secao criada: " + s.Name, db =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            foreach (RoadSection other in _sections)
                ViarioStore.SaveSection(tr, db, other);
            ViarioStore.SaveSection(tr, db, s);
            tr.Commit();
        }, regenerate: false);
    }

    private void MakeCurrent(RoadSection s)
    {
        Edit("Secao corrente: " + s.Name, db =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            foreach (RoadSection other in _sections)
                ViarioStore.SaveSection(tr, db, other);
            _settings.CurrentSectionId = s.Id;
            ViarioStore.SaveSettings(tr, db, _settings);
            tr.Commit();
        }, regenerate: false);
    }

    private void DeleteSection(RoadSection s)
    {
        if (_sections.Count <= 1)
        {
            Say("E preciso manter pelo menos uma secao.");
            return;
        }

        RoadSection replacement = _sections.First(o => o.Id != s.Id);
        int uses = _axes.Count(a => a.Section.Id == s.Id);
        if (SW.MessageBox.Show(this,
                $"Excluir a secao '{s.Name}'?" + (uses > 0 ? $"\n{uses} eixo(s) passam a usar '{replacement.Name}'." : ""),
                "Excluir secao", SW.MessageBoxButton.YesNo, SW.MessageBoxImage.Question) != SW.MessageBoxResult.Yes)
            return;

        _selectedRow = null;
        Edit("Secao excluida: " + s.Name, db =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            foreach (RoadSection other in _sections.Where(o => o.Id != s.Id))
                ViarioStore.SaveSection(tr, db, other);
            ViarioStore.DeleteSection(tr, db, s.Id);
            if (_settings.CurrentSectionId == s.Id)
            {
                _settings.CurrentSectionId = replacement.Id;
                ViarioStore.SaveSettings(tr, db, _settings);
            }

            foreach (AxisInput axis in _axes.Where(a => a.Section.Id == s.Id))
            {
                if (AxisEntity(tr, db, axis.Key, AcadDb.OpenMode.ForWrite) is AcadDb.Entity e)
                    ViarioStore.UpdateAxis(e, sectionId: replacement.Id);
            }

            tr.Commit();
        });
    }

    private void ApplyToSelected(RoadSection s)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        PromptSelectionResult implied = doc.Editor.SelectImplied();
        if (implied.Status != PromptStatus.OK || implied.Value.Count == 0)
        {
            Say("Selecione os eixos no desenho (sem comando ativo) e clique de novo.");
            return;
        }

        int count = 0;
        Edit("Secao aplicada: " + s.Name, db =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            foreach (RoadSection other in _sections)
                ViarioStore.SaveSection(tr, db, other);
            foreach (AcadDb.ObjectId id in implied.Value.GetObjectIds())
            {
                if (tr.GetObject(id, AcadDb.OpenMode.ForWrite) is AcadDb.Curve curve && ViarioStore.IsAxis(curve, out string _))
                {
                    ViarioStore.UpdateAxis(curve, sectionId: s.Id);
                    count++;
                }
            }

            tr.Commit();
        });
        Say($"Secao {s.Name} aplicada a {count} eixo(s).");
    }

    // ---------------- Eixos e elementos ----------------

    private void ShowAxis(AxisInput a)
    {
        Category("Geometria");
        Prop("Comprimento", N(a.Path.Length) + " m");
        Prop("Trechos", $"{a.Path.Segs.Count} ({a.Path.Segs.Count(s => s.IsArc)} arco(s))");
        Prop("Tipo", a.Path.Closed ? "anel de rotatoria" : "eixo");
        if (!a.Path.Closed)
        {
            ChoiceProp("Concordancia", "Concordancia dos trechos",
                new[] { ("mantida (refeita ao editar)", "1"), ("livre", "0") }, a.Chained ? "1" : "0",
                () => SetChained(a, Text("Concordancia") == "1"));
        }
        Prop("Handle", a.Key);
        Category("Secao");
        ChoiceProp("Secao", "Secao", _sections.Select(s => (s.Name, s.Id)), a.Section.Id);
        Prop("Raio de esquina", a.Radius is double r ? N(r) + " m (do eixo)" : N(a.Section.Raio) + " m (da secao)");
        if (_names.TryGetValue(a.Key, out string? civil))
            Prop("Alinhamento do Civil", civil);

        Act("Zoom", ZoomCurrent, true);
        Act("Selecionar", () => SelectAxis(a));
        Act("Aplicar secao", () => AssignSection(a));
        Act("Remover da rede", () => RemoveAxis(a));
        _render = () => RenderMap(a.Path.Bounds().Inflate(a.Section.TotalWidth + 10), sh => sh.Highlight(a.Path.Segs));
    }

    private void SetChained(AxisInput a, bool chained)
    {
        if (chained == a.Chained)
            return;
        Edit(chained ? "Concordancia mantida: " + AxisLabel(a.Key) : "Concordancia liberada: " + AxisLabel(a.Key), db =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            if (AxisEntity(tr, db, a.Key, AcadDb.OpenMode.ForWrite) is AcadDb.Entity e)
                ViarioStore.SetChained(e, chained);
            tr.Commit();
        });
    }

    private void AssignSection(AxisInput a)
    {
        string id = Text("Secao");
        if (id.Length == 0 || id == a.Section.Id)
            return;
        Edit("Secao do eixo alterada.", db =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            foreach (RoadSection other in _sections)
                ViarioStore.SaveSection(tr, db, other);
            if (AxisEntity(tr, db, a.Key, AcadDb.OpenMode.ForWrite) is AcadDb.Entity e)
                ViarioStore.UpdateAxis(e, sectionId: id);
            tr.Commit();
        });
    }

    private void RemoveAxis(AxisInput a)
    {
        _selectedRow = null;
        Edit("Eixo tirado da rede (a polilinha continua no desenho).", db =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            if (AxisEntity(tr, db, a.Key, AcadDb.OpenMode.ForWrite) is AcadDb.Entity e)
                ViarioStore.ClearTag(e);
            tr.Commit();
        });
    }

    private static V2 EventPoint(AxisInput a, AxisEvent ev) =>
        a.Path.PointAt(Math.Clamp(ev.S + (ev.Dir * (ev.Gap + (ev.Length / 2.0))), 0, a.Path.Length));

    private static Box2 EventBox(AxisInput a, AxisEvent ev)
    {
        V2 at = EventPoint(a, ev);
        double size = (ev.Length + ev.Taper + 30) / 1.5;
        return new Box2(at.X - size, at.Y - size, at.X + size, at.Y + size);
    }

    private void ShowEvent(AxisInput a, AxisEvent ev)
    {
        Category("Elemento");
        Prop("Tipo", KindName(ev.Kind));
        Prop("Eixo", AxisLabel(a.Key));
        if (ev.Kind is AxisEventKind.Alargamento or AxisEventKind.Baia)
            Prop("Lado", ev.Side == 0 ? "ambos" : ev.Side > 0 ? "direita de quem chega" : "esquerda de quem chega");
        Prop(ev.Kind == AxisEventKind.Gota ? "Largura maxima" : "Largura", N(ev.Width) + " m");
        Prop(ev.Kind is AxisEventKind.Abertura or AxisEventKind.Gota ? "Comprimento" : "Armazenamento", N(ev.Length) + " m");
        if (ev.Kind != AxisEventKind.Abertura)
            Prop("Taper", N(ev.Taper) + " m" + (ev.Smooth ? " (suave)" : " (reto)"));
        if (ev.Kind == AxisEventKind.Gota)
            Prop("Recuo da ponta", N(ev.Gap) + " m");
        Prop("Descricao", ev.Describe());

        Box2 box = EventBox(a, ev);
        Act("Zoom", () => ZoomTo(box), true);
        Act("Remover elemento", () =>
        {
            string serialized = ev.Serialize();
            _selectedRow = null;
            Edit("Elemento removido.", _ =>
                ViarioElementCommands.ModifyAxisEvents(AcApp.DocumentManager.MdiActiveDocument, a.Key, events => events.RemoveAll(e => e.Serialize() == serialized)));
        });
        _render = () => RenderMap(box, sh => sh.Highlight(a.Path.Segs));
    }

    private void ShowSlip(string key)
    {
        if (!_engine.SlipLanes.TryGetValue(key, out SlipLane? slip))
            return;
        CornerInfo? corner = _result.Corners.FirstOrDefault(c => c.Key == key);
        Category("Faixa livre a direita");
        Prop("Raio externo", N(slip.Radius) + " m");
        Prop("Largura", N(slip.Width) + " m");
        Prop("Afastamento da ilha", N(slip.Setback) + " m");
        Prop("Largura recomendada (VP, Tab.45)", N(ViarioNorms.SlipWidth(Math.Max(slip.Radius - slip.Width, 1))) + " m");
        Box2 box = corner?.Arc is Seg arc ? arc.Bounds().Inflate(30) : corner is not null
            ? new Box2(corner.Corner.X - 40, corner.Corner.Y - 40, corner.Corner.X + 40, corner.Corner.Y + 40)
            : new Box2(-50, -50, 50, 50);
        Act("Zoom", () => ZoomTo(box), true);
        _render = () => RenderMap(box, sh =>
        {
            if (corner?.Arc is Seg c)
                sh.Highlight(new[] { c });
        });
    }

    // ---------------- Nos, esquinas, ilhas ----------------

    private void ShowNode(NodeInfo nd)
    {
        List<CornerInfo> corners = CornersAt(nd);
        Category("Intersecao");
        Prop("Ramos", nd.LegCount.ToString(CultureInfo.InvariantCulture));
        Prop("Eixos", string.Join(", ", nd.AxisKeys.Select(AxisLabel)));
        Prop("Raios", corners.Count == 0 ? "-" : string.Join("; ", corners.Select(c => N(c.Radius))));
        Prop("Coordenadas", $"{N(nd.Point.X)} ; {N(nd.Point.Y)}");
        Category("Referencia (IPR-718 8.4.1)");
        Prop("Angulo entre vias", "ideal 75 - 90 graus; abaixo de 60, realinhar");
        Prop("Greide na chegada", "evitar > 3%, max. 6%");
        Box2 box = new(nd.Point.X - 40, nd.Point.Y - 40, nd.Point.X + 40, nd.Point.Y + 40);
        Act("Zoom", () => ZoomTo(box), true);
        Act("Tabela", () => OpenReference("IPR-718 8.4.1"));
        _render = () => RenderMap(box, sh =>
        {
            foreach (CornerInfo c in corners.Where(c => c.Arc is not null))
                sh.Highlight(new[] { c.Arc! });
        });
    }

    private void ShowCorner(CornerInfo c)
    {
        bool slip = _engine.SlipLanes.ContainsKey(c.Key);
        double turn = c.Arc is null ? 0 : Math.Abs(c.Arc.Sweep) * 180.0 / Math.PI;
        (double vp, double co) = ViarioNorms.MinCornerRadius(turn);
        Category("Esquina");
        Prop("Eixos", AxisLabel(c.AxisA) + " x " + AxisLabel(c.AxisB));
        Prop("Giro", N(turn, 0) + " graus");
        if (slip)
        {
            Prop("Tipo", "faixa livre a direita (ver Elementos)");
        }
        else
        {
            EditProp("Raio", "Raio na face do meio-fio (m)", N(c.Radius));
            Prop("Origem", _engine.CornerRadius.ContainsKey(c.Key) ? "especifico da esquina" : "secao ou eixo");
        }

        Category("Referencia (IPR-718 Tab.39)");
        Prop("Carro (VP), curva simples", ">= " + N(vp) + " m");
        Prop("Onibus/caminhao (CO)", ">= " + N(co) + " m");

        Box2 box = c.Arc is null ? new Box2(c.Corner.X - 30, c.Corner.Y - 30, c.Corner.X + 30, c.Corner.Y + 30) : c.Arc.Bounds().Inflate(25);
        if (!slip)
        {
            Act("Aplicar raio", () => SetCornerRadius(c, Number("Raio")), true);
            Act("Padrao", () => SetCornerRadius(c, -1));
        }

        Act("Zoom", () => ZoomTo(box));
        Act("Tabela", () => OpenReference("IPR-718 Tab.39"));
        _render = () => RenderMap(box, sh =>
        {
            if (c.Arc is not null)
                sh.Highlight(new[] { c.Arc });
        });
    }

    private void SetCornerRadius(CornerInfo c, double? radius)
    {
        if (radius is null)
        {
            Say("Raio invalido.");
            return;
        }

        bool reset = radius.Value < 0;
        Edit(reset ? "Esquina volta ao raio padrao." : $"Raio da esquina: {N(radius.Value)} m.", db =>
        {
            Dictionary<string, double> radii = ViarioStore.LoadCornerRadii(db);
            if (reset)
                radii.Remove(c.Key);
            else
                radii[c.Key] = radius.Value;
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            ViarioStore.SaveCornerRadii(tr, db, radii);
            tr.Commit();
        });
    }

    private void ShowIsland(OutputLine island)
    {
        Box2 box = Bounds(island.Segs).Inflate(20);
        Category("Ilha");
        Prop("Area", N(ViarioEngine.RingArea(island.Segs)) + " m2");
        Prop("Perimetro", N(island.Segs.Sum(s => s.Length)) + " m");
        Prop("Cantos arredondados", island.Segs.Count(s => s.IsArc).ToString(CultureInfo.InvariantCulture));
        Category("Referencia (IPR-718 p.354)");
        Prop("Area minima", "5 m2 urbana / 7 m2 rural");
        Prop("Lado minimo (triangular)", "3,50 m (pref. 4,50)");
        Prop("Raio do nariz", "0,60 - 1,00 m");
        Act("Zoom", () => ZoomTo(box), true);
        Act("Raio dos cantos...", () => Send("C3DVIARIORAIO ", "Clique dentro da ilha."));
        Act("Tabela", () => OpenReference("IPR-718 p.354"));
        _render = () => RenderMap(box, sh => sh.Highlight(island.Segs));
    }

    // ---------------- Sinalizacao ----------------

    private static readonly (string Prop, string Label, string Group)[] SignageLabels =
    {
        ("Vazada", "Vazada (so contornos cinza)", "Desenho"),
        ("Central", "Divisoria amarela (LFO)", "Longitudinais"),
        ("CentralTipo", "Tipo da divisoria", "Longitudinais"),
        ("ContinuaAntes", "Divisoria continua antes da LRE (m)", "Longitudinais"),
        ("Faixas", "Divisao de faixas (LMS)", "Longitudinais"),
        ("FaixasContinuasAntes", "Faixas continuas antes da LRE (m)", "Longitudinais"),
        ("Bordo", "Linha de bordo (LBO)", "Longitudinais"),
        ("CadenciaAuto", "Cadencia pela velocidade", "Longitudinais"),
        ("Largura", "Largura da linha (m)", "Longitudinais"),
        ("Traco", "Traco (m)", "Longitudinais"),
        ("Espaco", "Espaco (m)", "Longitudinais"),
        ("Travessia", "Faixa de pedestres (FTP-1)", "Transversais"),
        ("TravessiaLargura", "Extensao das listras (m)", "Transversais"),
        ("ListraLargura", "Largura da listra (m)", "Transversais"),
        ("ListraEspaco", "Espaco entre listras (m)", "Transversais"),
        ("TravessiaRecuo", "Recuo ao fim da curva (m)", "Transversais"),
        ("Retencao", "Linha de retencao (LRE)", "Transversais"),
        ("RetencaoLargura", "Largura da LRE (m)", "Transversais"),
        ("RetencaoDistancia", "LRE ate a faixa de pedestres (m)", "Transversais")
    };

    private void ShowSignage(string? kind)
    {
        string group = "";
        foreach ((string prop, string label, string g) in SignageLabels)
        {
            if (g != group)
            {
                Category(g);
                group = g;
            }

            object? value = typeof(SignageOptions).GetProperty(prop)!.GetValue(_signage);
            switch (value)
            {
                case bool b:
                    CheckProp(prop, label, b, () => _render?.Invoke());
                    break;
                case string str:
                    ChoiceProp(prop, label, new[] { ("Seccionada (LFO-2)", "Seccionada"), ("Continua (LFO-1)", "Continua"), ("Dupla (LFO-3)", "Dupla") }, str, () => _render?.Invoke());
                    break;
                case double d:
                    EditProp(prop, label, N(d)).TextChanged += (_, _) => _render?.Invoke();
                    break;
            }
        }

        Category("Bloco");
        Prop("Situacao", _signExists ? ViarioSignageSync.BlockName + " no desenho (acompanha a rede)" : "ainda nao gerado");
        Act(_signExists ? "Atualizar bloco" : "Gerar bloco", GenerateSignage, true);
        if (_signExists)
            Act("Remover bloco", () => Edit("Sinalizacao removida.", _ => ViarioSignageSync.Remove(AcApp.DocumentManager.MdiActiveDocument), regenerate: false));
        Act("Tabela MBST", () => OpenReference("MBST"));
        _render = () => RenderSignage(EditedSignage() ?? _signage, kind);
    }

    private SignageOptions? EditedSignage()
    {
        SignageOptions o = _signage.Clone();
        foreach ((string prop, _, _) in SignageLabels)
        {
            if (!_fields.ContainsKey(prop))
                return o;
            System.Reflection.PropertyInfo info = typeof(SignageOptions).GetProperty(prop)!;
            if (info.PropertyType == typeof(bool))
                info.SetValue(o, Checked(prop));
            else if (info.PropertyType == typeof(string))
                info.SetValue(o, Text(prop) is { Length: > 0 } t ? t : "Seccionada");
            else if (Number(prop) is double v)
                info.SetValue(o, v);
            else
                return null;
        }

        foreach (string prop in new[] { "Largura", "Traco", "Espaco" })
        {
            if (_fields.TryGetValue(prop, out SW.FrameworkElement? f))
                f.IsEnabled = !o.CadenciaAuto;
        }

        return o;
    }

    private List<MarkLine> SafeMarks(SignageOptions o)
    {
        try
        {
            return ViarioSignage.Build(_axes, _result, o);
        }
        catch (Exception ex)
        {
            Say("Erro na sinalizacao: " + ex.Message);
            return new List<MarkLine>();
        }
    }

    private void GenerateSignage()
    {
        SignageOptions? o = EditedSignage();
        if (o is null)
        {
            Say("Corrija os valores em vermelho.");
            return;
        }

        int count = -1;
        Edit("Sinalizacao gerada.", db =>
        {
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                ViarioStore.SaveSignage(tr, db, o);
                tr.Commit();
            }

            count = ViarioSignageSync.Sync(AcApp.DocumentManager.MdiActiveDocument, _axes, _result, create: true);
        }, regenerate: false);
        if (count >= 0)
            Say($"Sinalizacao: {count} marca(s) no bloco {ViarioSignageSync.BlockName}.");
    }

    // ---------------- Civil, avisos, config ----------------

    private void ShowCivil(AxisInput a)
    {
        Category("Objetos do Civil");
        Prop("Situacao", _civilActive ? "ativo (acompanham a rede)" : "nao gerados");
        Prop("Eixo", AxisLabel(a.Key));
        Prop("Alinhamento", _names.TryGetValue(a.Key, out string? n) ? n : "-");
        Prop("Bordos", _civilActive ? "LD e LE (offset dinamico, um por lado)" : "-");
        Prop("Curb returns", _civilActive ? "conectados aos bordos, sem site" : "-");
        Category("Proxima etapa");
        Prop("Altimetria", "perfil dos eixos, perfis offset dos bordos, curb returns e amarracao de greides");
        Act(_civilActive ? "Atualizar" : "Gerar", () => Send("C3DVIARIOCIVIL G ", "Objetos do Civil: siga a linha de comando."), true);
        Act("Remover", () => Send("C3DVIARIOCIVIL R ", "Remover objetos do Civil: confirme na linha de comando."));
        Act("Autoteste", () => Send("C3DVIARIOTESTE  ", "Autoteste: relatorio em %TEMP%."));
        Act("Zoom", ZoomCurrent);
        _render = () => RenderMap(a.Path.Bounds().Inflate(a.Section.TotalWidth + 10), sh => sh.Highlight(a.Path.Segs));
    }

    private void ShowHint(Hint h)
    {
        Category(h.Level == HintLevel.Atencao ? "Atencao" : "Informacao");
        Prop("Recomendacao", h.Message);
        Prop("Tema", h.Topic);
        Prop("Fonte", h.Source);
        if (h.Where is V2 p)
            Prop("Local", $"{N(p.X)} ; {N(p.Y)}");
        Act("Zoom no desenho", () => ZoomHint(h), true);
        if (ViarioReference.Find(h.Source) is not null)
            Act("Abrir tabela", () => OpenReference(h.Source));
        _render = h.Where is V2 w
            ? () => RenderMap(new Box2(w.X - 40, w.Y - 40, w.X + 40, w.Y + 40), sh => sh.Marker(w))
            : () => RenderMap(null, null);
    }

    private void ShowConfig()
    {
        Category("Encontro de eixos");
        EditProp("SnapTolerance", "Tolerancia de encontro (m)", N(_settings.SnapTolerance));
        EditProp("Capture", "Captura das pontas (m, 0 = nao)", N(_settings.Capture));
        Category("Canteiro");
        EditProp("MedianSetback", "Recuo do nariz do canteiro (m)", N(_settings.MedianSetback));
        Category("Painel");
        Prop("Tema", (_dark ? "escuro" : "claro") + " (Preferencias...)");
        Act("Salvar", () =>
        {
            double? snap = Number("SnapTolerance", 1e-6), capture = Number("Capture"), setback = Number("MedianSetback");
            if (snap is null || capture is null || setback is null)
            {
                Say("Corrija os valores em vermelho.");
                return;
            }

            Edit("Configuracao salva.", db =>
            {
                _settings.SnapTolerance = snap.Value;
                _settings.Capture = capture.Value;
                _settings.MedianSetback = setback.Value;
                using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
                ViarioStore.SaveSettings(tr, db, _settings);
                tr.Commit();
            });
        }, true);
        _render = () => RenderMap(null, null);
    }

    // ------------------------------------------------------------------
    // Execucao no desenho
    // ------------------------------------------------------------------

    private void Edit(string what, Action<AcadDb.Database> action, bool regenerate = true)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        try
        {
            using (doc.LockDocument())
            {
                ViarioSync.Initialize();
                ViarioSync.Suspended = true;
                try
                {
                    action(doc.Database);
                }
                finally
                {
                    ViarioSync.Suspended = false;
                }

                if (regenerate)
                    ViarioSync.Regenerate(doc, quiet: true);
                doc.Editor.UpdateScreen();
                AcApp.UpdateScreen();
            }

            Refresh();
            Say(what);
        }
        catch (Exception ex)
        {
            Refresh();
            Say("Erro: " + ex.Message);
            doc.Editor.WriteMessage("\n[C3DVIARIOPAINEL] " + what + " falhou: " + ex);
        }
    }

    private static AcadDb.Entity? AxisEntity(AcadDb.Transaction tr, AcadDb.Database db, string key, AcadDb.OpenMode mode)
    {
        if (!long.TryParse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
            return null;
        if (!db.TryGetObjectId(new AcadDb.Handle(value), out AcadDb.ObjectId id) || id.IsErased)
            return null;
        return tr.GetObject(id, mode, false) as AcadDb.Entity;
    }

    private void ZoomCurrent()
    {
        if (_current is not null)
            ZoomRow(_current);
    }

    private void ZoomRow(Row row)
    {
        switch (row.Data)
        {
            case AxisInput a:
                ZoomTo(a.Path.Bounds().Inflate(a.Section.TotalWidth));
                SelectAxis(a);
                break;
            case ValueTuple<AxisInput, AxisEvent> pair:
                ZoomTo(EventBox(pair.Item1, pair.Item2));
                break;
            case NodeInfo nd:
                ZoomTo(new Box2(nd.Point.X - 40, nd.Point.Y - 40, nd.Point.X + 40, nd.Point.Y + 40));
                break;
            case CornerInfo c:
                ZoomTo(c.Arc is null ? new Box2(c.Corner.X - 30, c.Corner.Y - 30, c.Corner.X + 30, c.Corner.Y + 30) : c.Arc.Bounds().Inflate(25));
                break;
            case OutputLine isl:
                ZoomTo(Bounds(isl.Segs).Inflate(20));
                break;
            case Hint h:
                ZoomHint(h);
                break;
            case RefTable t:
                OpenReference(t.Keys.FirstOrDefault() ?? t.Source);
                break;
            case string key when _category == "ELEMENTOS":
                CornerInfo? corner = _result.Corners.FirstOrDefault(c => c.Key == key);
                if (corner is not null)
                    ZoomTo(new Box2(corner.Corner.X - 40, corner.Corner.Y - 40, corner.Corner.X + 40, corner.Corner.Y + 40));
                break;
            case RoadSection s when _axes.FirstOrDefault(a => a.Section.Id == s.Id) is AxisInput first:
                ZoomTo(first.Path.Bounds().Inflate(first.Section.TotalWidth));
                break;
        }
    }

    /// <summary>Zoom no retangulo (WCS), convertido para as coordenadas da vista (DCS).</summary>
    private void ZoomTo(Box2 box)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        try
        {
            using (doc.LockDocument())
            {
                using AcadDb.ViewTableRecord view = doc.Editor.GetCurrentView();
                Matrix3d dcsToWcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                                    Matrix3d.Displacement(view.Target - Point3d.Origin) *
                                    Matrix3d.PlaneToWorld(view.ViewDirection);
                Matrix3d wcsToDcs = dcsToWcs.Inverse();
                Point3d a = new Point3d(box.MinX, box.MinY, 0).TransformBy(wcsToDcs);
                Point3d b = new Point3d(box.MaxX, box.MaxY, 0).TransformBy(wcsToDcs);
                double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X), minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);

                double ratio = view.Width / Math.Max(view.Height, 1e-9);
                double width = Math.Max(maxX - minX, 10.0);
                double height = Math.Max(maxY - minY, 10.0);
                if (width / height > ratio)
                    height = width / ratio;
                else
                    width = height * ratio;

                view.CenterPoint = new Point2d((minX + maxX) / 2.0, (minY + maxY) / 2.0);
                view.Width = width * 1.1;
                view.Height = height * 1.1;
                doc.Editor.SetCurrentView(view);
                AcApp.UpdateScreen();
            }
        }
        catch (Exception ex)
        {
            Say("Zoom falhou: " + ex.Message);
        }
    }

    private void SelectAxis(AxisInput a)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        using (doc.LockDocument())
        {
            using AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
            if (AxisEntity(tr, doc.Database, a.Key, AcadDb.OpenMode.ForRead) is AcadDb.Entity e)
                doc.Editor.SetImpliedSelection(new[] { e.ObjectId });
        }
    }

    // ------------------------------------------------------------------
    // Vista grafica
    // ------------------------------------------------------------------

    private sealed class Shapes
    {
        public readonly List<(List<V2> Points, WM.Brush Brush, double Thickness, bool Closed, WM.Brush? Fill, bool Dashed)> Items = new();
        public readonly List<V2> Markers = new();

        public void Add(IEnumerable<Seg> segs, WM.Brush brush, double thickness, bool closed = false, WM.Brush? fill = null, bool dashed = false) =>
            Items.Add((Sample(segs), brush, thickness, closed, fill, dashed));

        public void Highlight(IEnumerable<Seg> segs) => Add(segs, HighlightBrush, 2.5);

        public void Marker(V2 p) => Markers.Add(p);
    }

    private Shapes NetworkShapes()
    {
        Shapes sh = new();
        foreach (OutputLine line in _result.Lines)
        {
            (WM.Brush brush, double t) = line.Level switch
            {
                EdgeLevel.Pista => (Brush(150, 150, 150), 0.8),
                EdgeLevel.FaceMF => (Brush(235, 235, 235), 1.1),
                EdgeLevel.CostasMF => (Brush(110, 200, 110), 0.8),
                EdgeLevel.Alinhamento => (Brush(130, 130, 130), 0.8),
                EdgeLevel.Canteiro => (Brush(230, 150, 60), 1.0),
                _ => (Brush(220, 90, 220), 1.1)
            };
            sh.Add(line.Segs, brush, t, line.Closed, line.Level == EdgeLevel.Ilha ? Brush(90, 60, 90) : null);
        }

        if (_showAxes)
        {
            foreach (AxisInput a in _axes)
                sh.Add(a.Path.Segs, Brush(220, 80, 80), 0.7, dashed: true);
        }

        return sh;
    }

    private void RenderMap(Box2? focus, Action<Shapes>? extra)
    {
        Shapes sh = NetworkShapes();
        extra?.Invoke(sh);
        Draw(sh, focus);
        if (_showText)
            DrawLabels();
    }

    private void RenderSignage(SignageOptions o, string? kind)
    {
        Shapes sh = new();
        foreach (OutputLine line in _result.Lines.Where(l => l.Level is EdgeLevel.FaceMF or EdgeLevel.Canteiro or EdgeLevel.Ilha))
            sh.Add(line.Segs, Brush(140, 140, 140), 0.8, line.Closed);

        List<MarkLine> marks = SafeMarks(o);
        foreach (MarkLine m in marks)
        {
            bool dim = kind is not null && m.Kind != kind;
            if (o.Vazada)
                sh.Add(ViarioSignage.Outline(m), dim ? Brush(90, 90, 90) : Brush(175, 175, 175), 0.8, true);
            else
                sh.Add(m.Segs, dim ? Brush(95, 95, 95) : m.Yellow ? Brush(240, 190, 30) : Brush(245, 245, 245), -m.Width);
        }

        Draw(sh, null);
        _graphicInfo.Text += $"   |   {marks.Count} marca(s): {ViarioSignageSync.Summary(marks)}";
    }

    private V2 ToWorld(SW.Point p) => new(_vCx + ((p.X - _vOx) / _vScale), _vCy - ((p.Y - _vOy) / _vScale));

    private SW.Point ToScreen(V2 p) => new(_vOx + ((p.X - _vCx) * _vScale), _vOy - ((p.Y - _vCy) * _vScale));

    private void OnGraphicMouseMove(object sender, WI.MouseEventArgs e)
    {
        if (_vScale <= 0)
            return;
        SW.Point p = e.GetPosition(_graphic);
        V2 w = ToWorld(p);
        _coords.Text = $"E {w.X,12:0.000}   N {w.Y,12:0.000}";
        if (_dragFrom is SW.Point f && (e.LeftButton == WI.MouseButtonState.Pressed || e.MiddleButton == WI.MouseButtonState.Pressed))
        {
            _pan = new V2(_pan.X - ((p.X - f.X) / _vScale), _pan.Y + ((p.Y - f.Y) / _vScale));
            _dragFrom = p;
            _render?.Invoke();
        }
    }

    private void OnGraphicWheel(object sender, WI.MouseWheelEventArgs e)
    {
        if (_vScale <= 0)
            return;
        SW.Point p = e.GetPosition(_graphic);
        V2 before = ToWorld(p);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25 : 0.8), 0.2, 200);
        _render?.Invoke();
        V2 after = ToWorld(p);
        _pan = new V2(_pan.X + (before.X - after.X), _pan.Y + (before.Y - after.Y));
        _render?.Invoke();
    }

    // Reguas da vista em planta (px).
    private const double RulerLeft = 44;
    private const double RulerBottom = 16;

    /// <summary>Passo "redondo" (1-2-5) maior ou igual a raw.</summary>
    private static double NiceStep(double raw)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
        return new[] { 1.0, 2.0, 5.0, 10.0 }.Select(k => k * mag).First(v => v >= raw);
    }

    /// <summary>Estaca de 20 m: 14+15.00 (ou so "14" quando cai na estaca inteira).</summary>
    private static string Estaca(double s, bool full = false)
    {
        int e = (int)Math.Floor((s / 20.0) + 1e-9);
        double r = s - (20.0 * e);
        if (r < 0.005 && !full)
            return e.ToString(CultureInfo.InvariantCulture);
        return $"{e}+{r.ToString("0.00", CultureInfo.InvariantCulture)}";
    }

    private void Draw(Shapes sh, Box2? focus)
    {
        _graphic.Children.Clear();
        double w = _graphic.ActualWidth, h = _graphic.ActualHeight;
        List<V2> all = sh.Items.SelectMany(i => i.Points).ToList();
        if (w < 20 || h < 20)
            return;
        if (all.Count == 0)
        {
            _vScale = 0;
            Label("Sem eixos na rede.", 10, 10);
            return;
        }

        double dw = w - RulerLeft, dh = h - RulerBottom;
        Box2 box = focus ?? new Box2(all.Min(p => p.X), all.Min(p => p.Y), all.Max(p => p.X), all.Max(p => p.Y));
        _vScale = 0.90 * Math.Min(dw / Math.Max(box.MaxX - box.MinX, 1e-6), dh / Math.Max(box.MaxY - box.MinY, 1e-6)) * _zoom;
        _vCx = ((box.MinX + box.MaxX) / 2.0) + _pan.X;
        _vCy = ((box.MinY + box.MaxY) / 2.0) + _pan.Y;
        _vOx = RulerLeft + (dw / 2.0);
        _vOy = dh / 2.0;
        _vW = w;
        _vH = h;
        double scale = _vScale;
        SW.Point P(V2 p) => ToScreen(p);

        // Grade: passo maior (1-2-5) e menor (1/5) quando ha espaco.
        double step = NiceStep(Math.Max(dw, dh) / scale / 8.0);
        double minor = step / 5.0;
        bool showMinor = minor * scale >= 9;
        double x0 = _vCx - ((_vOx - RulerLeft) / scale), x1 = _vCx + ((w - _vOx) / scale);
        double y0 = _vCy - ((dh - _vOy) / scale), y1 = _vCy + (_vOy / scale);

        void GridLine(double a, double b, double c, double d, WM.Brush brush) =>
            _graphic.Children.Add(new WS.Line { X1 = a, Y1 = b, X2 = c, Y2 = d, Stroke = brush, StrokeThickness = 1, SnapsToDevicePixels = true });

        if (_showGrid)
        {
            double g = showMinor ? minor : step;
            for (double x = Math.Ceiling(x0 / g) * g; x <= x1; x += g)
            {
                bool major = Math.Abs((x / step) - Math.Round(x / step)) < 1e-6;
                double px = Math.Round(P(new V2(x, 0)).X) + 0.5;
                if (px < RulerLeft)
                    continue;
                GridLine(px, 0, px, dh, major ? GraphicGridMajor : GraphicGrid);
            }

            for (double y = Math.Ceiling(y0 / g) * g; y <= y1; y += g)
            {
                bool major = Math.Abs((y / step) - Math.Round(y / step)) < 1e-6;
                double py = Math.Round(P(new V2(0, y)).Y) + 0.5;
                if (py > dh)
                    continue;
                GridLine(RulerLeft, py, w, py, major ? GraphicGridMajor : GraphicGrid);
            }
        }

        // Linhas da rede.
        foreach ((List<V2> pts, WM.Brush brush, double thickness, bool closed, WM.Brush? fill, bool dashed) in sh.Items)
        {
            if (pts.Count < 2)
                continue;
            WM.PointCollection points = new(pts.Select(P));
            WS.Shape shape = closed ? new WS.Polygon { Points = points, Fill = fill } : new WS.Polyline { Points = points };
            shape.Stroke = brush;
            shape.StrokeThickness = thickness < 0 ? Math.Max(0.6, -thickness * scale) : thickness;
            shape.StrokeLineJoin = WM.PenLineJoin.Miter;
            if (dashed)
                shape.StrokeDashArray = new WM.DoubleCollection { 12, 4, 2, 4 };
            _graphic.Children.Add(shape);
        }

        // Estacas ao longo dos eixos.
        double stStep = new[] { 20.0, 100.0, 200.0, 500.0, 1000.0, 2000.0, 5000.0 }.First(v => v * scale >= 40 || v >= 5000);
        if (_showAxes && _showText)
            DrawStations(stStep);

        // Marcadores (local de uma recomendacao).
        foreach (V2 m in sh.Markers)
        {
            SW.Point c = P(m);
            WS.Ellipse ring = new() { Width = 22, Height = 22, Stroke = Brush(255, 80, 60), StrokeThickness = 1.5 };
            WC.Canvas.SetLeft(ring, c.X - 11);
            WC.Canvas.SetTop(ring, c.Y - 11);
            _graphic.Children.Add(ring);
            GridLine(c.X - 16, c.Y, c.X + 16, c.Y, Brush(255, 80, 60));
            GridLine(c.X, c.Y - 16, c.X, c.Y + 16, Brush(255, 80, 60));
        }

        DrawRulers(step, dw, dh, x0, x1, y0, y1);
        DrawScaleBar(step, dh);
        DrawNorth();

        // 1 px = 25.4/96 mm no monitor.
        double den = 1000.0 / (scale * (25.4 / 96.0));
        string denText = den >= 100 ? Math.Round(den / 10.0) * 10 + "" : Math.Round(den) + "";
        _graphicInfo.Text = $"Grade {N(step)} m   Estacas {N(stStep)} m   Esc. 1:{denText}      roda: zoom   arrastar: mover   duplo clique: enquadrar";
    }

    private void DrawRulers(double step, double dw, double dh, double x0, double x1, double y0, double y1)
    {
        double w = _vW;
        WS.Rectangle left = new() { Width = RulerLeft, Height = dh, Fill = GraphicRuler };
        _graphic.Children.Add(left);
        WS.Rectangle bottom = new() { Width = w, Height = RulerBottom, Fill = GraphicRuler };
        WC.Canvas.SetTop(bottom, dh);
        _graphic.Children.Add(bottom);
        _graphic.Children.Add(new WS.Line { X1 = RulerLeft + 0.5, Y1 = 0, X2 = RulerLeft + 0.5, Y2 = dh, Stroke = GraphicGridMajor, StrokeThickness = 1 });
        _graphic.Children.Add(new WS.Line { X1 = 0, Y1 = dh + 0.5, X2 = w, Y2 = dh + 0.5, Stroke = GraphicGridMajor, StrokeThickness = 1 });

        string Fmt(double v) => Math.Abs(v) >= 1e5 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString(step < 1 ? "0.0" : "0", CultureInfo.InvariantCulture);

        double lastX = double.MinValue;
        for (double x = Math.Ceiling(x0 / step) * step; x <= x1; x += step)
        {
            double px = ToScreen(new V2(x, 0)).X;
            if (px < RulerLeft + 2)
                continue;
            _graphic.Children.Add(new WS.Line { X1 = Math.Round(px) + 0.5, Y1 = dh, X2 = Math.Round(px) + 0.5, Y2 = dh + 4, Stroke = GraphicText, StrokeThickness = 1 });
            if (px - lastX < 52)
                continue;
            lastX = px;
            WC.TextBlock t = Mono(Fmt(x), 9.5);
            t.Measure(new SW.Size(double.PositiveInfinity, double.PositiveInfinity));
            WC.Canvas.SetLeft(t, px - (t.DesiredSize.Width / 2.0));
            WC.Canvas.SetTop(t, dh + 2);
            _graphic.Children.Add(t);
        }

        double lastY = double.MaxValue;
        for (double y = Math.Ceiling(y0 / step) * step; y <= y1; y += step)
        {
            double py = ToScreen(new V2(0, y)).Y;
            if (py > dh - 2)
                continue;
            _graphic.Children.Add(new WS.Line { X1 = RulerLeft - 4, Y1 = Math.Round(py) + 0.5, X2 = RulerLeft, Y2 = Math.Round(py) + 0.5, Stroke = GraphicText, StrokeThickness = 1 });
            if (lastY - py < 14)
                continue;
            lastY = py;
            WC.TextBlock t = Mono(Fmt(y), 9.5);
            t.Width = RulerLeft - 7;
            t.TextAlignment = SW.TextAlignment.Right;
            WC.Canvas.SetLeft(t, 1);
            WC.Canvas.SetTop(t, py - 7);
            _graphic.Children.Add(t);
        }
    }

    private void DrawStations(double stStep)
    {
        double dh = _vH - RulerBottom;
        int budget = 400;
        for (int i = 0; i < _axes.Count && budget > 0; i++)
        {
            AxisInput a = _axes[i];
            double len = a.Path.Length;
            for (double s = 0; s <= len + 1e-6 && budget > 0; s += stStep)
            {
                double ss = Math.Min(s, len);
                V2 p = a.Path.PointAt(ss);
                SW.Point c = ToScreen(p);
                if (c.X < RulerLeft || c.Y < 0 || c.X > _vW || c.Y > dh)
                    continue;
                V2 n = a.Path.TangentAt(ss).Left;
                double tick = 4;
                _graphic.Children.Add(new WS.Line
                {
                    X1 = c.X - (n.X * tick), Y1 = c.Y + (n.Y * tick), X2 = c.X + (n.X * tick), Y2 = c.Y - (n.Y * tick),
                    Stroke = GraphicText, StrokeThickness = 1
                });
                WC.TextBlock t = Mono(Estaca(ss), 9);
                WC.Canvas.SetLeft(t, c.X + (n.X * 7) + 1);
                WC.Canvas.SetTop(t, c.Y - (n.Y * 7) - 6);
                _graphic.Children.Add(t);
                budget--;
            }

            // Ponta final com a estaca completa.
            if (!a.Path.Closed && len % stStep > 1e-6)
            {
                SW.Point c = ToScreen(a.Path.PointAt(len));
                if (c.X >= RulerLeft && c.Y >= 0 && c.X <= _vW && c.Y <= dh)
                {
                    WC.TextBlock t = Mono("PF " + Estaca(len, true), 9);
                    WC.Canvas.SetLeft(t, c.X + 5);
                    WC.Canvas.SetTop(t, c.Y + 4);
                    _graphic.Children.Add(t);
                }
            }
        }

        DrawLabels();
    }

    private void DrawScaleBar(double step, double dh)
    {
        double px = step * _vScale;
        if (px < 30 || px > _vW * 0.5)
            return;
        double x1 = _vW - 14, x0 = x1 - px, y = dh - 16;
        WM.Brush ink = GraphicText;
        _graphic.Children.Add(new WS.Rectangle { Width = px / 2.0, Height = 4, Fill = ink, Stroke = ink, StrokeThickness = 0.8 }.At(x0, y));
        _graphic.Children.Add(new WS.Rectangle { Width = px / 2.0, Height = 4, Fill = GraphicBack, Stroke = ink, StrokeThickness = 0.8 }.At(x0 + (px / 2.0), y));
        WC.TextBlock t0 = Mono("0", 9);
        WC.Canvas.SetLeft(t0, x0 - 3);
        WC.Canvas.SetTop(t0, y - 14);
        _graphic.Children.Add(t0);
        WC.TextBlock t1 = Mono(N(step) + " m", 9);
        t1.Measure(new SW.Size(double.PositiveInfinity, double.PositiveInfinity));
        WC.Canvas.SetLeft(t1, x1 - t1.DesiredSize.Width + 2);
        WC.Canvas.SetTop(t1, y - 14);
        _graphic.Children.Add(t1);
    }

    private void DrawNorth()
    {
        double x = _vW - 20, y0 = 36, y1 = 12;
        _graphic.Children.Add(new WS.Line { X1 = x, Y1 = y0, X2 = x, Y2 = y1, Stroke = GraphicText, StrokeThickness = 1 });
        _graphic.Children.Add(new WS.Polygon
        {
            Points = new WM.PointCollection { new(x, y1), new(x - 3.5, y1 + 9), new(x + 3.5, y1 + 9) },
            Fill = GraphicText
        });
        WC.TextBlock n = Mono("N", 9);
        WC.Canvas.SetLeft(n, x - 4);
        WC.Canvas.SetTop(n, y0 + 1);
        _graphic.Children.Add(n);
    }

    private void DrawLabels()
    {
        if (_vScale <= 0.8)
            return;
        double w = _vW, h = _vH - RulerBottom;
        for (int i = 0; i < _axes.Count; i++)
        {
            AxisInput a = _axes[i];
            // Rotulo deslocado ao longo do eixo para nao sobrepor os vizinhos.
            double s = a.Path.Length * (0.25 + (0.15 * (i % 3)));
            SW.Point p = ToScreen(a.Path.PointAt(s));
            if (p.X < RulerLeft || p.Y < 0 || p.X > w || p.Y > h)
                continue;
            V2 n = a.Path.TangentAt(s).Left;
            Label(AxisName(a, i), p.X - (n.X * 12) + 3, p.Y + (n.Y * 12) - 14, 10);
        }
    }

    private void Label(string text, double x, double y, double size = 11)
    {
        WC.TextBlock t = new() { Text = text, Foreground = GraphicText, FontSize = size, FontFamily = UiFont };
        WC.Canvas.SetLeft(t, x);
        WC.Canvas.SetTop(t, y);
        _graphic.Children.Add(t);
    }

    private static WC.TextBlock Mono(string text, double size, WM.Brush? brush = null) =>
        new() { Text = text, Foreground = brush ?? GraphicText, FontSize = size, FontFamily = MonoFont };

    // ------------------------------------------------------------------
    // Corte transversal da secao tipo (desenho cotado)
    // ------------------------------------------------------------------

    private void RenderSection(RoadSection s)
    {
        _graphic.Children.Clear();
        double w = _graphic.ActualWidth, h = _graphic.ActualHeight;
        if (w < 60 || h < 60)
            return;

        _vScale = 0;
        double aL = s.EdgeOffset(EdgeLevel.Alinhamento, RoadSection.Esquerda);
        double aR = s.EdgeOffset(EdgeLevel.Alinhamento, RoadSection.Direita);
        double margin = Math.Max(1.5, (aL + aR) * 0.08);
        double k = (w - 60) / (aL + aR + (2 * margin));
        double X(double x) => 30 + ((x + aL + margin) * k);

        double road = h * 0.40;
        double curbH = 10;                 // altura do meio-fio em px (sem escala vertical)
        double curbTop = road - curbH;
        double ground = road + 14;
        WM.Brush ink = Brush(225, 225, 225);
        WM.Brush dim = Brush(190, 190, 190);
        WM.Brush soft = Brush(140, 140, 140);

        WS.Line L(double xa, double ya, double xb, double yb, WM.Brush b, double t = 1.0, WM.DoubleCollection? dash = null)
        {
            WS.Line line = new() { X1 = xa, Y1 = ya, X2 = xb, Y2 = yb, Stroke = b, StrokeThickness = t };
            if (dash is not null)
                line.StrokeDashArray = dash;
            _graphic.Children.Add(line);
            return line;
        }

        void Txt(string text, double x, double y, double size, WM.Brush b, bool center = true, bool right = false)
        {
            WC.TextBlock t = Mono(text, size, b);
            t.Measure(new SW.Size(double.PositiveInfinity, double.PositiveInfinity));
            double dx = center ? t.DesiredSize.Width / 2.0 : right ? t.DesiredSize.Width : 0;
            WC.Canvas.SetLeft(t, x - dx);
            WC.Canvas.SetTop(t, y);
            _graphic.Children.Add(t);
        }

        // Regua de offsets (embaixo) e grade vertical a cada metro.
        double rulerY = h - RulerBottom;
        _graphic.Children.Add(new WS.Rectangle { Width = w, Height = RulerBottom, Fill = GraphicRuler }.At(0, rulerY));
        L(0, rulerY + 0.5, w, rulerY + 0.5, GraphicGridMajor);
        double labStep = k >= 40 ? 1 : k >= 18 ? 2 : k >= 8 ? 5 : 10;
        for (double x = Math.Ceiling((-aL - margin)); x <= aR + margin; x += 1)
        {
            double px = Math.Round(X(x)) + 0.5;
            bool lab = Math.Abs((x / labStep) - Math.Round(x / labStep)) < 1e-9;
            if (_showGrid)
                L(px, 0, px, rulerY, lab ? GraphicGridMajor : GraphicGrid);
            L(px, rulerY, px, rulerY + (lab ? 5 : 3), GraphicText);
            if (lab)
                Txt(x.ToString("+0;-0;0", CultureInfo.InvariantCulture), px, rulerY + 2, 9.5, GraphicText);
        }

        // Terreno/base e eixo.
        L(X(-aL - (margin * 0.7)), ground + 0.5, X(aR + (margin * 0.7)), ground + 0.5, soft, 1, new WM.DoubleCollection { 6, 3 });
        if (_showAxes)
        {
            L(X(0), 22, X(0), ground + 8, Brush(220, 80, 80), 0.9, new WM.DoubleCollection { 14, 4, 2, 4 });
            Txt("EIXO", X(0), 8, 9, Brush(220, 80, 80));
        }

        double c = s.Canteiro / 2.0;
        List<double> breaks = new();
        List<(double A, double B, string T)> rowA = new(), rowB = new();
        List<(string T, double A, double B)> names = new();

        foreach (int side in new[] { RoadSection.Esquerda, RoadSection.Direita })
        {
            double sg = side == RoadSection.Esquerda ? -1 : 1;
            double p = c + (s.Pista / 2.0);
            double f = p + s.SarjetaEm(side);
            double b = f + s.MeioFioEm(side);
            double a = b + s.CalcadaEm(side);
            bool hasCurb = s.MeioFioEm(side) > 1e-3;
            bool hasWalk = s.CalcadaEm(side) > 1e-3;
            bool hasGutter = s.SarjetaEm(side) > 1e-3;

            // Pista.
            L(X(sg * c), road + 0.5, X(sg * p), road + 0.5, ink, 1.4);
            // Sarjeta (leve caimento para a guia).
            if (hasGutter)
                L(X(sg * p), road + 0.5, X(sg * f), road + 3.5, ink, 1.2);
            double edgeY = hasGutter ? road + 3.5 : road + 0.5;
            if (hasCurb)
            {
                // Face, topo e costas do meio-fio; costas descem ate a base.
                L(X(sg * f), edgeY, X(sg * f), curbTop, ink, 1.4);
                L(X(sg * f), curbTop + 0.5, X(sg * b), curbTop + 0.5, ink, 1.4);
                L(X(sg * b), curbTop, X(sg * b), ground, dim, 0.8);
                L(X(sg * f), edgeY, X(sg * f), ground, dim, 0.8);
            }
            else
            {
                // Bordo livre: pequena saia ate a base.
                L(X(sg * f), edgeY, X(sg * (f + 0.6)), ground, dim, 0.9);
            }

            if (hasWalk)
            {
                double top = hasCurb ? curbTop : edgeY - 2;
                L(X(sg * b), top + 0.5, X(sg * a), top + 0.5, ink, 1.2);
                L(X(sg * a), top, X(sg * a), ground, dim, 0.8);
                // Hachura de calcada (pontilhado de concreto).
                double x0 = Math.Min(X(sg * b), X(sg * a)), x1 = Math.Max(X(sg * b), X(sg * a));
                for (double hx = x0 + 6; hx < x1 - 3; hx += 9)
                    L(hx, top + 3, hx + 1.5, top + 4.5, soft, 0.8);
            }

            // Faixas de rolamento (LMS) e nomes.
            int n = s.FaixasPorSentido;
            for (int i = 1; i < n; i++)
            {
                double x = sg * (c + (i * (s.Pista / 2.0) / n));
                L(X(x), road - 6, X(x), road, Brush(245, 245, 245), 1.2, new WM.DoubleCollection { 2, 2 });
            }

            names.Add(($"PISTA {N(s.Pista / 2.0)} ({n} x {N((s.Pista / 2.0) / n)})", sg * c, sg * p));
            if (hasWalk)
                names.Add(($"CALCADA {N(s.CalcadaEm(side))}", sg * b, sg * a));

            // Cotas: componentes (linha A) e meia pista (linha B).
            if (hasGutter)
                rowA.Add((sg * p, sg * f, N(s.Sarjeta)));
            if (hasCurb)
                rowA.Add((sg * f, sg * b, N(s.MeioFio)));
            if (hasWalk)
                rowA.Add((sg * b, sg * a, N(s.Calcada)));
            rowB.Add((sg * c, sg * p, N(s.Pista / 2.0)));
            breaks.AddRange(new[] { sg * c, sg * p, sg * f, sg * b, sg * a });
        }

        // Canteiro central ou linha de eixo (LFO).
        if (c > 1e-3)
        {
            L(X(-c), road + 0.5, X(-c), curbTop, ink, 1.4);
            L(X(c), road + 0.5, X(c), curbTop, ink, 1.4);
            L(X(-c), curbTop + 0.5, X(c), curbTop + 0.5, ink, 1.4);
            for (double hx = X(-c) + 5; hx < X(c) - 3; hx += 8)
                L(hx, curbTop - 3, hx + 1, curbTop, Brush(110, 170, 110), 0.9);
            names.Add(($"CANTEIRO {N(s.Canteiro)}", -c, c));
            rowB.Add((-c, c, N(s.Canteiro)));
        }
        else
        {
            L(X(0), road - 6, X(0), road, Brush(240, 190, 30), 1.6);
        }

        // Nomes dos componentes acima do corte.
        foreach ((string text, double a, double b) in names)
        {
            double px = Math.Abs(X(b) - X(a));
            if (px < 44)
                continue;
            Txt(text, (X(a) + X(b)) / 2.0, curbTop - 18, 9, dim);
        }

        // Cotas estilo CAD: linhas de chamada, linha de cota continua, tracos obliquos, texto acima.
        double yA = ground + 26, yB = yA + 18, yC = yB + 18;
        if (yC > rulerY - 6)
        {
            yA = ground + 20;
            yB = yA + 15;
            yC = yB + 15;
        }

        void Tick(double x, double y)
        {
            L(x - 3, y + 3, x + 3, y - 3, ink, 1.0);
        }

        void Dim(double a, double b, double y, string text)
        {
            if (Math.Abs(b - a) < 1e-4)
                return;
            double xa = X(Math.Min(a, b)), xb = X(Math.Max(a, b));
            L(xa, y + 0.5, xb, y + 0.5, ink, 0.9);
            Tick(xa, y + 0.5);
            Tick(xb, y + 0.5);
            if (xb - xa >= 18)
                Txt(text, (xa + xb) / 2.0, y - 13, 9.5, ink);
        }

        foreach (double bx in breaks.Distinct().OrderBy(v => v))
            L(X(bx), ground + 4, X(bx), yC + 4, dim, 0.6);

        foreach ((double a, double b, string t) in rowA)
            Dim(a, b, yA, t);
        foreach ((double a, double b, string t) in rowB)
            Dim(a, b, yB, t);
        Dim(-aL, aR, yC, N(aL + aR));

        // Altura do meio-fio (cota vertical, no lado direito se houver).
        int curbSide = s.MeioFioEm(RoadSection.Direita) > 1e-3 ? RoadSection.Direita : s.MeioFioEm(RoadSection.Esquerda) > 1e-3 ? RoadSection.Esquerda : 0;
        if (curbSide != 0)
        {
            double sg = curbSide == RoadSection.Direita ? 1 : -1;
            double b = c + (s.Pista / 2.0) + s.SarjetaEm(curbSide) + s.MeioFioEm(curbSide);
            double x = X(sg * b) + (sg * 14);
            L(x, curbTop, x, road + 4, dim, 0.7);
            L(x - 3, curbTop + 0.5, x + 3, curbTop + 0.5, dim, 0.7);
            L(x - 3, road + 4.5, x + 3, road + 4.5, dim, 0.7);
            Txt("h 0.15", x + (sg > 0 ? 5 : -5), road - 14, 8.5, dim, center: false, right: sg < 0);
        }

        if (_showText)
        {
            Txt($"SECAO TIPO  {s.Name.ToUpperInvariant()} [{s.Id}]   {s.Tipo.ToUpperInvariant()}   " +
                 $"{s.FaixasPorSentido * 2} FAIXAS x {N((s.Pista / 2.0) / s.FaixasPorSentido)} m   {N(s.Velocidade)} km/h   R ESQ. {N(s.Raio)} m   LARGURA {N(aL + aR)} m",
                8, 6, 9.5, GraphicText, center: false);
            Txt("LE", X(-aL - (margin * 0.6)), road - 7, 9, soft);
            Txt("LD", X(aR + (margin * 0.6)), road - 7, 9, soft);
        }

        _graphicInfo.Text = "Corte transversal, offsets em m (esquerda negativa); sem escala vertical";
    }

    // ------------------------------------------------------------------
    // Utilitarios
    // ------------------------------------------------------------------

    private static Box2 Bounds(IEnumerable<Seg> segs)
    {
        Box2 box = Box2.Empty;
        foreach (Seg s in segs)
            box = box.Union(s.Bounds());
        return box;
    }

    private static List<V2> Sample(IEnumerable<Seg> segs)
    {
        List<V2> pts = new();
        foreach (Seg s in segs)
        {
            int n = s.IsArc ? 10 : 1;
            for (int k = pts.Count == 0 ? 0 : 1; k <= n; k++)
                pts.Add(s.PointAt(k / (double)n));
        }

        return pts;
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string N(double v, int decimals) => v.ToString("F" + decimals, CultureInfo.InvariantCulture);
}

internal static class CanvasExtensions
{
    /// <summary>Posiciona o elemento no Canvas e o devolve (para encadear).</summary>
    public static T At<T>(this T element, double left, double top) where T : SW.UIElement
    {
        WC.Canvas.SetLeft(element, left);
        WC.Canvas.SetTop(element, top);
        return element;
    }
}
