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
internal sealed partial class LayoutPanel : SW.Window
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
    private readonly TextFilter _filtro = new();
    private List<SheetText2> _textos = new();
    private readonly HashSet<ObjectId> _textosMarcados = new();
    private readonly NumberingRule _regra = new();
    private Dictionary<ObjectId, ViewFrameLink> _vinculos = new();
    private readonly SheetBlockOptions _prancha = SheetBlockOptions.Carregar();
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
    private bool _mostrarTextos = true;
    private double _zoom = 1;
    private SW.Vector _pan;
    private SW.Point? _dragFrom;
    private SheetGraphics.Mapa? _mapa;
    private readonly Dictionary<ObjectId, List<WS.Path>> _vpShapes = new();
    private readonly Dictionary<SheetData, WC.Border> _cards = new();
    private double CardW = 250;
    private double ThumbH => CardW * 0.7;
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
    private readonly WM.Brush _vpTravada = Hachura(WM.Color.FromRgb(110, 200, 120), 9, 20);
    private readonly WM.Brush _vpDesligada = Hachura(WM.Color.FromRgb(170, 170, 170), 7, 28);
    private readonly WM.Brush _vp3d = Hachura(WM.Color.FromRgb(230, 165, 20), 8, 26);
    private readonly WM.Brush _vp3dTraco = ToolTheme.Brush(230, 165, 20);

    /// <summary>Hachura leve (linhas a 45 graus, em pixels) com um fundo quase transparente.</summary>
    private static WM.Brush Hachura(WM.Color cor, double passo, byte fundo)
    {
        var grupo = new WM.DrawingGroup();
        using (WM.DrawingContext dc = grupo.Open())
        {
            dc.DrawRectangle(new WM.SolidColorBrush(WM.Color.FromArgb(fundo, cor.R, cor.G, cor.B)), null, new SW.Rect(0, 0, passo, passo));
            var caneta = new WM.Pen(new WM.SolidColorBrush(WM.Color.FromArgb(150, cor.R, cor.G, cor.B)), 0.8);
            dc.DrawLine(caneta, new SW.Point(0, passo), new SW.Point(passo, 0));
            dc.DrawLine(caneta, new SW.Point(-passo / 2, passo / 2), new SW.Point(passo / 2, -passo / 2));
            dc.DrawLine(caneta, new SW.Point(passo / 2, passo * 1.5), new SW.Point(passo * 1.5, passo / 2));
        }
        var brush = new WM.DrawingBrush(grupo)
        {
            TileMode = WM.TileMode.Tile,
            Viewport = new SW.Rect(0, 0, passo, passo),
            ViewportUnits = WM.BrushMappingMode.Absolute,
            Viewbox = new SW.Rect(0, 0, passo, passo),
            ViewboxUnits = WM.BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Hachura (preenchimento) pelo estado: desligada > 3D > travada > livre.</summary>
    private WM.Brush FillEstado(SheetViewport v) =>
        !v.Ligada ? _vpDesligada : !v.Planta ? _vp3d : v.Travada ? _vpTravada : WM.Brushes.Transparent;

    // ------------------------------------------------------------------
    // Categorias e comandos
    // ------------------------------------------------------------------

    private static readonly (string Id, string Icon, string Title)[] Categories =
    {
        ("LAYOUTS", "layouts", "Layouts"),
        ("PRANCHAS", "prancha", "Pranchas do eixo"),
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
        new("VPLEVE", "desligar", "Modo leve (desligar/religar viewports)", "Viewports",
            "Desliga as viewports de todos os layouts (marcando-as) para trocar de layout sem regenerar, e religa so essas depois.",
            new[] { "Aba Modo leve: desligar todas, montar as pranchas, religar." }, p => p.SelectCategory("VIEWPORTS", "Leve")),
        new("ESCALA", "escala", "Escala das viewports", "Viewports",
            "Aplica a escala 1:N nas viewports marcadas (e a escala de anotacao, se existir no desenho).",
            new[] { "Marque as viewports.", "Escolha a escala na aba Escala." }, p => p.SelectCategory("VIEWPORTS", "Escala")),
        new("NUMERAR", "numero", "Numerar pranchas", "Numeracao",
            "Escreve numero e total nos textos marcados (busca na aba Numeracao), na ordem das abas, com prefixo, sufixo, digitos e total configuraveis.",
            new[] { "Busque e marque os textos do numero e do total (uma vez).", "Clique para renumerar depois de criar, apagar ou reordenar layouts." }, p => p.Numerar()),
        new("PRANCHA", "prancha", "Inserir prancha", "Layouts",
            "Insere um bloco de prancha (A0 a A4 ou tamanho livre) com estendimentos, margens e marcas de dobra, na origem ou num ponto clicado.",
            new[] { "Configure na aba Prancha.", "Inserir na origem ou clicar o ponto." }, p => p.SelectCategory("LAYOUTS", "Prancha")),
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
        new("PPGERAR", "prancha", "Pranchas do eixo", "Pranchas do eixo",
            "Gera frames ao longo de um eixo no tamanho da viewport de planta do layout modelo, uma prancha por frame e um perfil por folha no tamanho da viewport de perfil.",
            new[] { "1. Escolha o layout modelo e as viewports de planta e perfil.", "2. Escolha o eixo.", "3. Estilo do perfil.", "4. Nome das pranchas e Gerar." }, p => p.SelectCategory("PRANCHAS", "Gerar")),
        new("PPATUALIZAR", "atualizar", "Atualizar pranchas do eixo", "Pranchas do eixo",
            "Realinha todas as series: estacas pelos frames, viewport de planta, intervalo e cotas do perfil, viewport de perfil e linhas de corte.",
            new[] { "Mova, gire ou estique os frames no Model e clique aqui (ou deixe o automatico ligado)." }, p => p.AtualizarTodasSeries()),
        new("PPZOOM", "viewframe", "Zoom no frame da folha", "Pranchas do eixo",
            "Mostra no Model o frame da folha selecionada.", new[] { "Selecione a folha na categoria Pranchas do eixo." }, p => p.ZoomFrame(null)),
        new("VFZOOM", "viewframe", "Zoom no view frame", "View frames",
            "Mostra no Model o view frame ou match line selecionado.", new[] { "Selecione na categoria View frames." }, p => p.ZoomViewFrame()),
        new("VFROTULOS", "ligar", "Rotulos dos view frames", "View frames",
            "Liga os rotulos dos frames/match lines marcados (Shift: desliga).",
            new[] { "Marque na categoria View frames." }, p => p.RotulosVf(!(WI.Keyboard.Modifiers.HasFlag(WI.ModifierKeys.Shift)))),
        new("VFATUALIZAR", "atualizar", "Alinhar viewports aos view frames", "View frames",
            "Planta: centro, giro e tamanho pelo frame na escala atual. Perfil: intervalo de estacas do frame no profile view, alinhado pelo inicio.",
            new[] { "Vincule as viewports (aba View frames).", "Mova os frames no Model e clique aqui." }, p => p.AtualizarViewFrames()),
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
                SheetSeriesSync.Changed -= OnKeyMapChanged;
                foreach (Document d in fechado._docsLigados)
                    d.CommandEnded -= OnCommandEnded;
                fechado._docsLigados.Clear();
            };
            LayoutKeyMap.Changed += OnKeyMapChanged;
            SheetSeriesSync.Changed += OnKeyMapChanged;
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
            nome.StartsWith("C3DMAPACHAVE", StringComparison.OrdinalIgnoreCase) ||
            nome.StartsWith("C3DPRANCHA", StringComparison.OrdinalIgnoreCase))
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
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(LayoutSettings.GetDouble("vista_proporcao", 0.7), SW.GridUnitType.Star), MinHeight = 120 });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(6) });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1, SW.GridUnitType.Star), MinHeight = 200 });
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
        _gradeScroll.PreviewMouseWheel += (_, e) =>
        {
            if ((WI.Keyboard.Modifiers & WI.ModifierKeys.Control) == 0)
                return;
            CardW = Math.Clamp(CardW * (e.Delta > 0 ? 1.2 : 1 / 1.2), 140, 900);
            RenderGraphic();
            e.Handled = true;
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
            IsChecked = _mostrarTextos,
            VerticalAlignment = SW.VerticalAlignment.Center,
            Margin = new SW.Thickness(8, 0, 8, 0),
            ToolTip = "Mostra os textos e atributos do papel na miniatura"
        };
        textos.Click += (_, _) =>
        {
            _mostrarTextos = textos.IsChecked == true;
            RenderGraphic();
        };
        WC.DockPanel.SetDock(textos, WC.Dock.Right);
        graphicBar.Children.Add(textos);
        graphicBar.Children.Add(_graphicInfo);
        // Vista + barra na mesma linha: o GridSplitter redimensiona as duas linhas estrela vizinhas.
        WC.DockPanel graphicRow = new();
        WC.DockPanel.SetDock(graphicBar, WC.Dock.Bottom);
        graphicRow.Children.Add(graphicBar);
        graphicRow.Children.Add(graphic);
        WC.Grid.SetRow(graphicRow, 2);
        root.Children.Add(graphicRow);

        WC.GridSplitter splitH = new()
        {
            Height = 6,
            HorizontalAlignment = SW.HorizontalAlignment.Stretch,
            VerticalAlignment = SW.VerticalAlignment.Stretch,
            Background = T.Back,
            ResizeDirection = WC.GridResizeDirection.Rows,
            ResizeBehavior = WC.GridResizeBehavior.PreviousAndNext,
            Cursor = WI.Cursors.SizeNS
        };
        WC.Grid.SetRow(splitH, 3);
        splitH.DragCompleted += (_, _) =>
        {
            double a = root.RowDefinitions[2].ActualHeight, b = root.RowDefinitions[4].ActualHeight;
            if (a > 0 && b > 0)
                LayoutSettings.Set("vista_proporcao", a / b);
        };
        root.Children.Add(splitH);

        WC.Grid bottom = new() { Margin = new SW.Thickness(4, 0, 4, 0) };
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star), MinWidth = 260 });
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(5) });
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1.15, SW.GridUnitType.Star), MinWidth = 300 });
        _list.SelectionChanged += (_, _) => OnRowSelected();
        _list.MouseDoubleClick += (_, _) => OnRowDoubleClick();
        bottom.Children.Add(_list);
        WC.GridSplitter splitV = new() { Width = 5, HorizontalAlignment = SW.HorizontalAlignment.Stretch, VerticalAlignment = SW.VerticalAlignment.Stretch, Background = T.Back, ResizeDirection = WC.GridResizeDirection.Columns, ResizeBehavior = WC.GridResizeBehavior.PreviousAndNext, Cursor = WI.Cursors.SizeWE };
        WC.Grid.SetColumn(splitV, 1);
        bottom.Children.Add(splitV);

        WC.DockPanel right = new();
        WC.Border strip = new() { Child = _tabStrip, BorderBrush = T.Line, BorderThickness = new SW.Thickness(0, 0, 0, 1) };
        WC.DockPanel.SetDock(strip, WC.Dock.Top);
        right.Children.Add(strip);
        right.Children.Add(new WC.Border { BorderBrush = T.Line, BorderThickness = new SW.Thickness(1, 0, 1, 1), Background = T.Pane, Child = _tabContent });
        WC.Grid.SetColumn(right, 2);
        bottom.Children.Add(right);
        WC.Grid.SetRow(bottom, 4);
        root.Children.Add(bottom);

        WC.DockPanel footer = new() { Margin = new SW.Thickness(4, 4, 4, 4) };
        WC.StackPanel leftButtons = new() { Orientation = WC.Orientation.Horizontal };
        leftButtons.Children.Add(FooterButton("Marcar todos", () => MarcarLayouts(true)));
        leftButtons.Children.Add(FooterButton("Desmarcar", () => MarcarLayouts(false)));
        leftButtons.Children.Add(FooterButton("Atualizar", Refresh));
        WC.DockPanel.SetDock(leftButtons, WC.Dock.Left);
        footer.Children.Add(leftButtons);
        footer.Children.Add(_status);
        WC.Grid.SetRow(footer, 5);
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
        combo.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Input));
        combo.Setters.Add(new SW.Setter(WC.Control.BorderBrushProperty, T.Line));
        Resources[typeof(WC.ComboBox)] = combo;

        // A lista suspensa do ComboBox usa as cores de sistema "Window": redefinidas aqui para o tema.
        Resources[SW.SystemColors.WindowBrushKey] = T.Pane;
        Resources[SW.SystemColors.WindowFrameBrushKey] = T.Line;
        Resources[SW.SystemColors.WindowTextBrushKey] = T.Text;
        Resources[SW.SystemColors.HighlightBrushKey] = T.Selected;
        Resources[SW.SystemColors.HighlightTextBrushKey] = T.Text;
        Resources[SW.SystemColors.ControlBrushKey] = T.Back;
        Resources[SW.SystemColors.ControlTextBrushKey] = T.Text;

        SW.Style comboItem = new(typeof(WC.ComboBoxItem));
        comboItem.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        comboItem.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Pane));
        comboItem.Setters.Add(new SW.Setter(WC.Control.PaddingProperty, new SW.Thickness(6, 3, 6, 3)));
        SW.FrameworkElementFactory cbd = new(typeof(WC.Border), "Bd");
        cbd.SetValue(WC.Border.BackgroundProperty, new SW.TemplateBindingExtension(WC.Control.BackgroundProperty));
        cbd.SetValue(WC.Border.PaddingProperty, new SW.TemplateBindingExtension(WC.Control.PaddingProperty));
        cbd.SetValue(WC.Border.SnapsToDevicePixelsProperty, true);
        cbd.AppendChild(new SW.FrameworkElementFactory(typeof(WC.ContentPresenter)));
        WC.ControlTemplate comboItemTemplate = new(typeof(WC.ComboBoxItem)) { VisualTree = cbd };
        SW.Trigger ciHover = new() { Property = SW.UIElement.IsMouseOverProperty, Value = true };
        ciHover.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, T.Hover, "Bd"));
        comboItemTemplate.Triggers.Add(ciHover);
        SW.Trigger ciSel = new() { Property = WC.ComboBoxItem.IsSelectedProperty, Value = true };
        ciSel.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, T.Selected, "Bd"));
        comboItemTemplate.Triggers.Add(ciSel);
        comboItem.Setters.Add(new SW.Setter(WC.Control.TemplateProperty, comboItemTemplate));
        Resources[typeof(WC.ComboBoxItem)] = comboItem;

        SW.Style radio = new(typeof(WC.RadioButton));
        radio.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        Resources[typeof(WC.RadioButton)] = radio;
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
            _vinculos = LayoutViewFrameSync.LerVinculos(db);
            _textos = LayoutNumbering.Buscar(db, _filtro);
        }
        catch (System.Exception ex)
        {
            Say("Erro ao ler os layouts: " + ex.Message);
            return;
        }

        try
        {
            LerPranchas(doc.Database);
        }
        catch (System.Exception ex)
        {
            // Sem Civil ou serie ilegivel: o resto do painel funciona.
            Say("Pranchas do eixo: " + ex.Message);
        }

        LigarDocumento(doc);
        _vfsMarcados.RemoveWhere(id => !_vfs.Any(v => v.Id == id));
        _textosMarcados.RemoveWhere(id => !_textos.Any(t => t.Id == id));
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
        "NUMERACAO" => _textos.Count(t => t.Papel.Length > 0),
        "LAYERSTATE" => _states.Count,
        "CARIMBO" => _blocos.Count,
        "PAGESETUP" => _pageSetups.Count,
        "MAPACHAVE" => _kmResumo.Count(r => r.Regioes > 0),
        "VIEWFRAMES" => _vfs.Count,
        "PRANCHAS" => _framesPP.Count,
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
        _tab = tab ?? "";
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
                        EscalaTexto(v.EscalaCustom) + (v.Escala.Length > 0 ? $"  {v.Escala}" : ""),
                        $"{v.Largura:0.#} x {v.Altura:0.#}",
                        EstadoTexto(v),
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

            case "NUMERACAO":
                headers = new[] { "Texto", "Layout", "Tipo", "Layer", "Altura", "Papel" };
                widths = new[] { 220.0, 120, 70, 130, 60, 70 };
                comCheck = true;
                rows = _textos.Where(t => Match(t.Texto) || Match(t.Layout) || Match(t.Layer) || Match(t.Bloco) || Match(t.Tag)).Select(t => new Row
                {
                    Icon = t.Papel == "Numero" ? "numero" : t.Papel == "Total" ? "ordenar" : t.Tipo == "Atributo" ? "bloco" : "renomear",
                    Data = t,
                    Checked = _textosMarcados.Contains(t.Id),
                    Cells = new[] { t.Texto.Length > 60 ? t.Texto[..60] + "..." : t.Texto, t.Layout, t.Tipo == "Atributo" ? $"Atrib. {t.Tag}" : t.Tipo, t.Layer, t.Altura.ToString("0.##", CultureInfo.InvariantCulture), t.Papel.Length > 0 ? t.Papel.ToUpperInvariant() : "" }
                }).ToList();
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

            case "PRANCHAS":
                headers = new[] { "Folha", "Eixo", "Estaca inicial", "Estaca final", "Extensao", "Perfil", "Situacao" };
                widths = new[] { 110.0, 120, 85, 85, 70, 90, 220 };
                comCheck = false;
                rows = _framesPP.Where(f => Match(f.LayoutNome) || Match(f.EixoNome) || Match(f.Situacao)).Select(f => new Row
                {
                    Icon = LayoutSheetSeries.Valido(f.Layout) ? "prancha" : "viewframe",
                    Data = f,
                    Cells = new[]
                    {
                        f.LayoutNome.Length > 0 ? f.LayoutNome : "(sem prancha)",
                        f.EixoNome,
                        LayoutViewFrames.Estaca(f.E0),
                        LayoutViewFrames.Estaca(f.E1),
                        f.Extensao.ToString("0.##", CultureInfo.InvariantCulture),
                        f.PerfilNome.Length > 0 ? f.PerfilNome : "-",
                        f.Situacao
                    }
                }).ToList();
                break;

            case "VIEWFRAMES":
                headers = new[] { "View frame", "Eixo", "Estaca inicial", "Estaca final", "Layout", "Viewports vinculadas" };
                widths = new[] { 170.0, 130, 85, 85, 120, 140 };
                comCheck = true;
                rows = _vfs.Where(v => Match(v.Nome) || Match(v.Eixo) || Match(v.Grupo) || Match(v.Layout)).Select(v => new Row
                {
                    Icon = v.Tipo == "Frame" ? "viewframe" : "quebrar",
                    Data = v,
                    Checked = _vfsMarcados.Contains(v.Id),
                    Cells = new[] { v.Nome, v.Eixo, LayoutViewFrames.Estaca(v.EstacaInicio), LayoutViewFrames.Estaca(v.EstacaFim), v.Layout, VinculosTexto(v) }
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
            C5 = c.Length > 5 ? c[5] : "";
            C6 = c.Length > 6 ? c[6] : "";
        }

        public bool Checked { get; set; }
        public WM.ImageSource IconImage { get; }
        public string C0 { get; }
        public string C1 { get; }
        public string C2 { get; }
        public string C3 { get; }
        public string C4 { get; }
        public string C5 { get; }
        public string C6 { get; }
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
            case SheetText2 tx:
                if (marcar) _textosMarcados.Add(tx.Id); else _textosMarcados.Remove(tx.Id);
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
            case SheetText2 tx:
                MostrarFolha(_folhas.FirstOrDefault(f => f.Nome == tx.Layout));
                break;
            case SheetFrameInfo fr:
                _frameSel = fr;
                _tab = "Folha";
                if (fr.LayoutNome.Length > 0)
                    MostrarFolha(_folhas.FirstOrDefault(f => f.Nome.Equals(fr.LayoutNome, StringComparison.OrdinalIgnoreCase)));
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
            case SheetFrameInfo fr:
                ZoomFrame(fr);
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

        _mapa = SheetGraphics.Desenhar(_graphic, folha, w, h, _zoom, _pan, _mostrarTextos, soTextosGrandes: false);
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
            SheetGraphics.Mapa mapa = SheetGraphics.Desenhar(canvas, folha, CardW - 2, ThumbH, 1, new SW.Vector(), _mostrarTextos, soTextosGrandes: true, margem: 6);
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
                ToolTip = DicaViewport(folha, vp),
                ContextMenu = MenuViewport(folha, vp)
            };
            SheetViewport v = vp;
            forma.MouseEnter += (_, _) => { if (!_vpsMarcadas.Contains(v.Id)) forma.Fill = _vpHover; };
            forma.MouseLeave += (_, _) => { if (!_vpsMarcadas.Contains(v.Id)) forma.Fill = FillEstado(v); };
            forma.MouseRightButtonDown += (_, _) => MostrarFolha(folha);
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
                string rot = vp.Aplicado ?? (vp.EscalaCustom > 0 ? vp.EscalaCustom.ToString("0.####", CultureInfo.InvariantCulture) : vp.Escala);
                string? papel = PapelNoModelo(folha, vp);
                var rotulo = new WC.TextBlock
                {
                    Text = papel is null ? $"#{i}  {rot}" : $"{papel}  #{i}  {rot}",
                    Foreground = papel is null ? ToolTheme.GraphicText : ToolTheme.Brush(120, 220, 140),
                    FontSize = papel is null ? 10 : 12,
                    FontWeight = vp.Aplicado is null && papel is null ? SW.FontWeights.Normal : SW.FontWeights.Bold,
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
                SheetViewport? vpi = _vpPorId.GetValueOrDefault(id);
                forma.Fill = marcada ? _vpSel : vpi is null ? WM.Brushes.Transparent : FillEstado(vpi);
                forma.Stroke = marcada ? _vpSelTraco : vpi is { Planta: false } ? _vp3dTraco : _vpTraco;
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
            ? $"{_folhas.Count} layout(s); {total} viewport(s) marcada(s) em {layouts} layout(s). Clique no cartao para escolher; duplo clique abre; Ctrl+roda muda o tamanho."
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

    private string EscalaTexto(double custom) => LayoutOps.EscalaTexto(custom, Fator);

    private double Fator => _folhaAtual?.FatorUnidade ?? _folhas.FirstOrDefault()?.FatorUnidade ?? 1000;

    private static string EstadoTexto(SheetViewport v) =>
        (v.Travada ? "travada" : "livre") + (v.Ligada ? "" : ", desligada") + (v.Planta ? "" : ", vista 3D/inclinada");

    private string DicaViewport(SheetData folha, SheetViewport vp) =>
        $"{folha.Nome}  viewport #{vp.Indice}\nEscala: {(vp.Escala.Length > 0 ? vp.Escala + "  |  " : "")}{EscalaTexto(vp.EscalaCustom)}\n{vp.Largura:0.##} x {vp.Altura:0.##} no papel\n{EstadoTexto(vp)}" +
        (vp.Aplicado is null ? "" : $"\nLayer state: {vp.Aplicado}") + "\nClique: marcar/desmarcar   Botao direito: acoes";

    /// <summary>Botao direito na viewport: informacoes e edicoes rapidas.</summary>
    private WC.ContextMenu MenuViewport(SheetData folha, SheetViewport vp)
    {
        var menu = new WC.ContextMenu();
        menu.Items.Add(new WC.MenuItem { Header = $"{folha.Nome}  #{vp.Indice}   {EscalaTexto(vp.EscalaCustom)}   {EstadoTexto(vp)}", IsEnabled = false, FontWeight = SW.FontWeights.Bold });
        menu.Items.Add(new WC.Separator());

        void Item(string texto, Action acao, string? icone = null)
        {
            var mi = new WC.MenuItem { Header = texto };
            if (icone is not null)
                mi.Icon = new WC.Image { Source = IconOf(icone), Width = 16, Height = 16 };
            mi.Click += (_, _) => acao();
            menu.Items.Add(mi);
        }
        var so = new[] { vp.Id };

        Item(_vpsMarcadas.Contains(vp.Id) ? "Desmarcar" : "Marcar", () =>
        {
            if (!_vpsMarcadas.Remove(vp.Id)) _vpsMarcadas.Add(vp.Id);
            AtualizarViewports();
            if (_category == "VIEWPORTS") FillList();
            Status();
        });
        Item("Repetir esta posicao nos demais layouts", () =>
        {
            _vpsMarcadas.Add(vp.Id);
            MostrarFolha(folha);
            RepetirNosDemais();
        });
        menu.Items.Add(new WC.Separator());
        Item(vp.Travada ? "Destravar" : "Travar", () => Executar(() => LayoutOps.Travar(Doc!, so, !vp.Travada)), vp.Travada ? "destravar" : "travar");
        Item(vp.Ligada ? "Desligar" : "Ligar", () => Executar(() => LayoutOps.Ligar(Doc!, so, !vp.Ligada)), vp.Ligada ? "desligar" : "ligar");
        Item("Escala...", () =>
        {
            _vpsMarcadas.Add(vp.Id);
            AtualizarViewports();
            SelectCategory("VIEWPORTS", "Escala");
        }, "escala");
        if (_layerState is not null)
            Item($"Aplicar layer state \"{_layerState}\"", () =>
            {
                Executar(() => LayoutOps.AplicarLayerState(Doc!, _layerState, so));
                vp.Aplicado = _layerState;
            }, "camadas");
        menu.Items.Add(new WC.Separator());
        if (vp.Planta)
        {
            Item("Pranchas do eixo: modelo com esta viewport de PLANTA", () => DefinirModelo(folha, vp, planta: true), "prancha");
            Item("Pranchas do eixo: modelo com esta viewport de PERFIL", () => DefinirModelo(folha, vp, planta: false), "prancha");
            menu.Items.Add(new WC.Separator());
        }
        Item("Abrir layout e dar zoom nesta viewport", () => ZoomViewport(folha, vp), "abrir");
        Item("Selecionar no desenho", () => SelecionarViewport(folha, vp));
        Item("Propriedades", () =>
        {
            MostrarFolha(folha);
            SelectCategory("VIEWPORTS");
            foreach (WC.ListViewItem item in _list.Items)
                if (item.Tag is Row { Data: SheetViewport r } && r.Id == vp.Id)
                    _list.SelectedItem = item;
        });
        return menu;
    }

    private void ZoomViewport(SheetData folha, SheetViewport vp)
    {
        if (Doc is not Document doc)
            return;
        try
        {
            LayoutOps.Abrir(doc, folha.Nome);
        }
        catch (System.Exception ex)
        {
            Say("Erro ao abrir: " + ex.Message);
            return;
        }
        double w = vp.Largura * 0.55, h = vp.Altura * 0.55;
        string P(double x, double y) => $"{x.ToString("0.###", CultureInfo.InvariantCulture)},{y.ToString("0.###", CultureInfo.InvariantCulture)}";
        Enviar($"_.PSPACE _.ZOOM _W {P(vp.Centro.X - w, vp.Centro.Y - h)} {P(vp.Centro.X + w, vp.Centro.Y + h)}");
    }

    private void SelecionarViewport(SheetData folha, SheetViewport vp)
    {
        if (Doc is not Document doc)
            return;
        try
        {
            LayoutOps.Abrir(doc, folha.Nome);
            doc.Editor.SwitchToPaperSpace();
            doc.Editor.SetImpliedSelection(new[] { vp.Id });
            Say($"Viewport #{vp.Indice} de {folha.Nome} selecionada no desenho.");
        }
        catch (System.Exception ex)
        {
            Say("Erro ao selecionar: " + ex.Message);
        }
    }

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
                tabs.Add(("Prancha", "Inserir prancha", BuildSheetBlock));
                break;
            case "VIEWPORTS":
                tabs.Add(("Escala", "Escala", BuildScale));
                tabs.Add(("Leve", "Modo leve", BuildViewportPower));
                break;
            case "NUMERACAO":
                tabs.Insert(0, ("Busca", "Buscar textos", BuildTextSearch));
                tabs.Insert(1, ("Numeracao", "Numerar", BuildNumbering));
                tabs.Add(("Editar", "Editar texto", BuildTextEdit));
                break;
            case "LAYERSTATE":
                tabs.Insert(0, ("Aplicar", "Aplicar", BuildLayerState));
                break;
            case "CARIMBO":
                tabs.Insert(0, ("Carimbo", "Carimbo", BuildStamp));
                tabs.Add(("Editar", "Editar texto", BuildTextEdit));
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
            case "PRANCHAS":
                tabs.Clear();
                tabs.Add(("Gerar", "Gerar pranchas", BuildSheetSeries));
                tabs.Add(("Folha", "Folha selecionada", BuildSheetFrame));
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
                if (f.Diagnostico.Length > 0)
                    p.Children.Add(Prop("Miniatura", f.Diagnostico));
                p.Children.Add(ActionButton("Abrir layout", () => Abrir(f.Nome)));
                break;
            case SheetViewport v:
                SheetData? dono = _folhas.FirstOrDefault(f => f.Viewports.Contains(v));
                p.Children.Add(Label($"Viewport de {dono?.Nome}", true));
                p.Children.Add(Prop("Escala de anotacao", v.Escala.Length > 0 ? v.Escala : "(nenhuma)"));
                p.Children.Add(Prop("Custom scale", EscalaTexto(v.EscalaCustom)));
                p.Children.Add(Prop("Vista", v.Planta ? "planta" : "3D / inclinada"));
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
            case SheetText2 tx:
                p.Children.Add(Label(tx.Tipo == "Atributo" ? $"Atributo {tx.Tag} do bloco {tx.Bloco}" : tx.Tipo, true));
                p.Children.Add(Prop("Layout", tx.Layout));
                p.Children.Add(Prop("Texto", tx.Texto));
                p.Children.Add(Prop("Layer", tx.Layer));
                p.Children.Add(Prop("Altura", tx.Altura.ToString("0.###", CultureInfo.InvariantCulture)));
                p.Children.Add(Prop("Caracteres", tx.Caracteres.ToString()));
                p.Children.Add(Prop("Papel na numeracao", tx.Papel.Length > 0 ? tx.Papel : "(nenhum)"));
                var acoesTx = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
                acoesTx.Children.Add(ActionButton("Marcar como numero", () => MarcarTextos("Numero", new[] { tx.Id })));
                acoesTx.Children.Add(ActionButton("Marcar como total", () => MarcarTextos("Total", new[] { tx.Id })));
                if (tx.Papel.Length > 0)
                    acoesTx.Children.Add(ActionButton("Desmarcar", () => MarcarTextos("", new[] { tx.Id })));
                p.Children.Add(acoesTx);
                p.Children.Add(ActionButton("Editar texto", () => { _tab = "Editar"; BuildTabs(); }));
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
        p.Children.Add(Label("No formato da propriedade \"Custom scale\" do AutoCAD (unidades de papel por unidade de desenho). Tambem aceita 1:N, 1/N ou N:1, convertido pelo fator de unidade abaixo."));

        var fator = new WC.TextBox { Width = 90, HorizontalAlignment = SW.HorizontalAlignment.Left, Text = Fator.ToString("0.###", CultureInfo.InvariantCulture) };
        double FatorAtual() => double.TryParse(fator.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double f) && f > 0 ? f : Fator;

        var combo = new WC.ComboBox { Width = 260, HorizontalAlignment = SW.HorizontalAlignment.Left, IsEditable = true, Margin = new SW.Thickness(0, 4, 0, 0) };
        void Preencher()
        {
            combo.Items.Clear();
            double f = FatorAtual();
            foreach ((string nome, double den) in _escalas)
                combo.Items.Add($"{(f / den).ToString("0.####", CultureInfo.InvariantCulture)}   ({nome})");
            foreach (int den in new[] { 50, 100, 200, 250, 500, 1000, 2000, 5000 })
                if (!_escalas.Any(e => Math.Abs(e.Denominador - den) < 1e-6))
                    combo.Items.Add($"{(f / den).ToString("0.####", CultureInfo.InvariantCulture)}   (1:{den})");
            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }
        Preencher();
        fator.TextChanged += (_, _) => Preencher();

        var previa = new WC.TextBlock { Foreground = T.Muted, Margin = new SW.Thickness(0, 4, 0, 0) };
        void Previa()
        {
            string t = (combo.Text ?? "").Trim();
            int par = t.IndexOf("  (", StringComparison.Ordinal);
            if (par > 0) t = t[..par];
            previa.Text = LayoutOps.TentarEscala(t, FatorAtual(), out double c) ? "Vai aplicar custom scale " + LayoutOps.EscalaTexto(c, FatorAtual()) : "Escala invalida.";
        }
        combo.AddHandler(WC.Primitives.TextBoxBase.TextChangedEvent, new WC.TextChangedEventHandler((_, _) => Previa()));
        combo.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Previa), SW.Threading.DispatcherPriority.Background);
        fator.TextChanged += (_, _) => Previa();

        p.Children.Add(Label("Escala (escolha ou digite: 2, 0.5, 1:500, 4:1)"));
        p.Children.Add(combo);
        p.Children.Add(previa);
        p.Children.Add(Label("Fator de unidade: mm de papel por unidade de desenho (1000 = desenho em metros; 1 = em milimetros). Lido do INSUNITS."));
        p.Children.Add(fator);
        p.Children.Add(Label("As viewports travadas sao destravadas, escaladas e travadas de novo. A escala de anotacao 1:N equivalente so muda se existir no desenho."));
        p.Children.Add(ActionButton("Aplicar escala", () =>
        {
            if (_vpsMarcadas.Count == 0) { Say("Marque as viewports."); return; }
            string t = (combo.Text ?? "").Trim();
            int par = t.IndexOf("  (", StringComparison.Ordinal);
            if (par > 0) t = t[..par];
            if (!LayoutOps.TentarEscala(t, FatorAtual(), out double custom)) { Say("Escala invalida. Use 2, 0.5, 1:500 ou 4:1."); return; }
            Executar(() => LayoutOps.Escalar(Doc!, VpsMarcadas(), custom, FatorAtual()));
        }));
        Dispatcher.BeginInvoke(new Action(Previa), SW.Threading.DispatcherPriority.Background);
        return p;
    }

    private SW.UIElement BuildNumbering()
    {
        WC.StackPanel p = Form();
        int nNum = _textos.Count(t => t.Papel == "Numero"), nTot = _textos.Count(t => t.Papel == "Total");
        var layoutsComNum = _textos.Where(t => t.Papel == "Numero").Select(t => t.Layout).Distinct().Count();
        p.Children.Add(Label("Numerar pranchas", true));
        p.Children.Add(Label($"{nNum} texto(s) marcado(s) como numero em {layoutsComNum} layout(s); {nTot} como total. Layouts sem texto marcado contam no total, mas nao recebem numero. Use a aba Buscar textos para marcar."));

        var prefixo = new WC.TextBox { Width = 120, HorizontalAlignment = SW.HorizontalAlignment.Left, Text = _regra.Prefixo };
        var sufixo = new WC.TextBox { Width = 120, HorizontalAlignment = SW.HorizontalAlignment.Left, Text = _regra.Sufixo };
        var inicio = new WC.TextBox { Width = 60, HorizontalAlignment = SW.HorizontalAlignment.Left, Text = _regra.Inicio.ToString() };
        var digitos = new WC.TextBox { Width = 60, HorizontalAlignment = SW.HorizontalAlignment.Left, Text = _regra.Digitos.ToString() };
        int totalPadrao = _regra.SoLayoutsMarcados ? Marcados().Count : _folhas.Count;
        var total = new WC.TextBox { Width = 60, HorizontalAlignment = SW.HorizontalAlignment.Left, Text = (_regra.Total ?? totalPadrao).ToString(), ToolTip = "Sugerido: quantidade de layouts. Edite se o conjunto tiver mais folhas." };
        var fmtTotal = new WC.ComboBox { Width = 160, HorizontalAlignment = SW.HorizontalAlignment.Left };
        foreach (string f in new[] { "/{0}", "{0}", "de {0}", "/ {0}" })
            fmtTotal.Items.Add(f);
        fmtTotal.SelectedItem = _regra.FormatoTotal;
        var soMarcados = new WC.CheckBox { Content = "Numerar so os layouts marcados (os outros nao mudam)", IsChecked = _regra.SoLayoutsMarcados, Margin = new SW.Thickness(0, 8, 0, 0) };
        var previa = new WC.TextBlock { Foreground = T.Muted, Margin = new SW.Thickness(0, 8, 0, 0), TextWrapping = SW.TextWrapping.Wrap };

        void Ler()
        {
            _regra.Prefixo = prefixo.Text;
            _regra.Sufixo = sufixo.Text;
            _regra.Inicio = int.TryParse(inicio.Text, out int i0) ? i0 : 1;
            _regra.Digitos = int.TryParse(digitos.Text, out int d0) ? Math.Clamp(d0, 1, 6) : 2;
            _regra.Total = int.TryParse(total.Text, out int t0) && t0 > 0 ? t0 : null;
            _regra.FormatoTotal = fmtTotal.SelectedItem as string ?? "/{0}";
            _regra.SoLayoutsMarcados = soMarcados.IsChecked == true;
            _regra.Marcados = Marcados();
            int tot = _regra.Total ?? (_regra.SoLayoutsMarcados ? _regra.Marcados.Count : _folhas.Count);
            previa.Text = $"Previa: {_regra.Numero(_regra.Inicio)}{_regra.TotalTexto(tot)}, {_regra.Numero(_regra.Inicio + 1)}{_regra.TotalTexto(tot)} ... (total {_regra.TotalTexto(tot).Trim()})";
        }
        foreach (WC.TextBox tb in new[] { prefixo, sufixo, inicio, digitos, total })
            tb.TextChanged += (_, _) => Ler();
        fmtTotal.SelectionChanged += (_, _) => Ler();
        soMarcados.Click += (_, _) => { total.Text = (soMarcados.IsChecked == true ? Marcados().Count : _folhas.Count).ToString(); Ler(); };

        var g = new WC.Grid();
        for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
        g.RowDefinitions.Add(new WC.RowDefinition()); g.RowDefinitions.Add(new WC.RowDefinition());
        void Campo(int col, string rotulo, SW.UIElement c)
        {
            var sp = new WC.StackPanel { Margin = new SW.Thickness(0, 0, 14, 0) };
            sp.Children.Add(Label(rotulo));
            sp.Children.Add(c);
            WC.Grid.SetColumn(sp, col);
            g.Children.Add(sp);
        }
        Campo(0, "Prefixo", prefixo); Campo(1, "Inicio", inicio); Campo(2, "Digitos (zeros a esquerda)", digitos);
        p.Children.Add(g);
        var g2 = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        var c1 = new WC.StackPanel { Margin = new SW.Thickness(0, 0, 14, 0) }; c1.Children.Add(Label("Sufixo")); c1.Children.Add(sufixo);
        var c2 = new WC.StackPanel { Margin = new SW.Thickness(0, 0, 14, 0) }; c2.Children.Add(Label("Total de folhas")); c2.Children.Add(total);
        var c3 = new WC.StackPanel(); c3.Children.Add(Label("Formato do total")); c3.Children.Add(fmtTotal);
        g2.Children.Add(c1); g2.Children.Add(c2); g2.Children.Add(c3);
        p.Children.Add(g2);
        p.Children.Add(soMarcados);
        p.Children.Add(previa);
        Ler();
        p.Children.Add(ActionButton("Numerar agora", Numerar));
        p.Children.Add(Label("O NP da linha de comando continua funcionando: [Configurar/Atualizar] usa os mesmos textos marcados."));
        return p;
    }

    private SW.UIElement BuildTextSearch()
    {
        WC.StackPanel p = Form();
        p.Children.Add(Label("Buscar textos nos layouts (TEXT, MTEXT e atributos de bloco, inclusive dentro do carimbo)", true));
        p.Children.Add(Label("Filtre ate sobrar o texto que numera a prancha, marque-o como numero (e o do total como total). O plugin grava a marca no proprio texto; nos outros layouts, o texto com a mesma layer/posicao e encontrado com os mesmos filtros."));

        var layer = new WC.TextBox { Width = 160, Text = _filtro.Layer, ToolTip = "Curinga: PR-*,CARIMBO" };
        var contem = new WC.TextBox { Width = 160, Text = _filtro.Contem };
        var regex = new WC.CheckBox { Content = "regex", IsChecked = _filtro.Regex, VerticalAlignment = SW.VerticalAlignment.Bottom, Margin = new SW.Thickness(6, 0, 0, 3) };
        var altMin = new WC.TextBox { Width = 60, Text = _filtro.AlturaMin?.ToString(CultureInfo.InvariantCulture) ?? "" };
        var altMax = new WC.TextBox { Width = 60, Text = _filtro.AlturaMax?.ToString(CultureInfo.InvariantCulture) ?? "" };
        var carMin = new WC.TextBox { Width = 50, Text = _filtro.CaracteresMin?.ToString() ?? "" };
        var carMax = new WC.TextBox { Width = 50, Text = _filtro.CaracteresMax?.ToString() ?? "" };
        var numericos = new WC.CheckBox { Content = "So textos com cara de numero (01, 12/30, 3 de 10)", IsChecked = _filtro.SoNumericos, Margin = new SW.Thickness(0, 6, 0, 0) };
        var tipos = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(0, 6, 0, 0) };
        var cbT = new WC.CheckBox { Content = "TEXT", IsChecked = _filtro.Textos, Margin = new SW.Thickness(0, 0, 12, 0) };
        var cbM = new WC.CheckBox { Content = "MTEXT", IsChecked = _filtro.MTextos, Margin = new SW.Thickness(0, 0, 12, 0) };
        var cbA = new WC.CheckBox { Content = "Atributos", IsChecked = _filtro.Atributos, Margin = new SW.Thickness(0, 0, 12, 0) };
        var cbMarc = new WC.CheckBox { Content = "So os ja marcados", IsChecked = _filtro.SoMarcados };
        tipos.Children.Add(cbT); tipos.Children.Add(cbM); tipos.Children.Add(cbA); tipos.Children.Add(cbMarc);

        double? D(string t) => double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        int? I(string t) => int.TryParse(t, out int v) ? v : null;
        void Buscar()
        {
            _filtro.Layer = layer.Text.Trim(); _filtro.Contem = contem.Text; _filtro.Regex = regex.IsChecked == true;
            _filtro.AlturaMin = D(altMin.Text); _filtro.AlturaMax = D(altMax.Text);
            _filtro.CaracteresMin = I(carMin.Text); _filtro.CaracteresMax = I(carMax.Text);
            _filtro.SoNumericos = numericos.IsChecked == true;
            _filtro.Textos = cbT.IsChecked == true; _filtro.MTextos = cbM.IsChecked == true; _filtro.Atributos = cbA.IsChecked == true; _filtro.SoMarcados = cbMarc.IsChecked == true;
            if (Doc is not Document doc) return;
            try
            {
                _textos = LayoutNumbering.Buscar(doc.Database, _filtro);
                _textosMarcados.RemoveWhere(id => !_textos.Any(t => t.Id == id));
                FillList();
                Say($"{_textos.Count} texto(s) encontrado(s).");
            }
            catch (System.Exception ex)
            {
                Say("Erro na busca: " + ex.Message);
            }
        }

        var l1 = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        void Campo(WC.Panel pai, string rotulo, SW.UIElement c)
        {
            var sp = new WC.StackPanel { Margin = new SW.Thickness(0, 0, 12, 0) };
            sp.Children.Add(Label(rotulo));
            sp.Children.Add(c);
            pai.Children.Add(sp);
        }
        Campo(l1, "Layer (curinga)", layer);
        var contemSp = new WC.StackPanel { Orientation = WC.Orientation.Horizontal }; contemSp.Children.Add(contem); contemSp.Children.Add(regex);
        Campo(l1, "Contem", contemSp);
        p.Children.Add(l1);
        var l2 = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        Campo(l2, "Altura min", altMin); Campo(l2, "Altura max", altMax); Campo(l2, "Caracteres min", carMin); Campo(l2, "Caracteres max", carMax);
        p.Children.Add(l2);
        p.Children.Add(numericos);
        p.Children.Add(tipos);

        var botoes = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        botoes.Children.Add(ActionButton("Buscar", Buscar));
        botoes.Children.Add(ActionButton("Limpar filtros", () =>
        {
            layer.Text = ""; contem.Text = ""; regex.IsChecked = false; altMin.Text = ""; altMax.Text = ""; carMin.Text = ""; carMax.Text = "";
            numericos.IsChecked = false; cbT.IsChecked = cbM.IsChecked = cbA.IsChecked = true; cbMarc.IsChecked = false;
            Buscar();
        }));
        p.Children.Add(botoes);

        p.Children.Add(Label($"Com os textos marcados na lista ({_textosMarcados.Count}):", true));
        var moverLayer = new WC.CheckBox { Content = $"Tambem mover para a layer {LayoutOps.LayerNumero} / {LayoutOps.LayerTotal} (compativel com o NP antigo)", IsChecked = true };
        p.Children.Add(moverLayer);
        var acoes = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        acoes.Children.Add(ActionButton("Marcar como NUMERO", () => MarcarTextos("Numero", _textosMarcados.ToList(), moverLayer.IsChecked == true)));
        acoes.Children.Add(ActionButton("Marcar como TOTAL", () => MarcarTextos("Total", _textosMarcados.ToList(), moverLayer.IsChecked == true)));
        acoes.Children.Add(ActionButton("Desmarcar", () => MarcarTextos("", _textosMarcados.ToList())));
        p.Children.Add(acoes);
        p.Children.Add(ActionButton("Marcar todos da lista", () =>
        {
            foreach (SheetText2 t in _textos) _textosMarcados.Add(t.Id);
            FillList();
            BuildTabs();
        }));
        return p;
    }

    private SW.UIElement BuildTextEdit()
    {
        WC.StackPanel p = Form();
        SheetText2? tx = (_list.SelectedItem as WC.ListViewItem)?.Tag is Row { Data: SheetText2 t } ? t : null;
        if (tx is null)
        {
            p.Children.Add(Label("Selecione um texto na lista (categoria Numeracao > Buscar textos) para editar.", true));
            return p;
        }
        p.Children.Add(Label(tx.Tipo == "Atributo" ? $"Atributo {tx.Tag} do bloco {tx.Bloco} em {tx.Layout}" : $"{tx.Tipo} em {tx.Layout}", true));
        var caixa = new WC.TextBox { Text = tx.Texto, AcceptsReturn = tx.Tipo == "MTexto", TextWrapping = SW.TextWrapping.Wrap, MinHeight = 60, Margin = new SW.Thickness(0, 4, 0, 0) };
        p.Children.Add(caixa);
        var botoes = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        botoes.Children.Add(ActionButton("Salvar texto", () => Executar(() => LayoutNumbering.EditarTexto(Doc!, tx.Id, caixa.Text))));
        botoes.Children.Add(ActionButton("Aplicar nos layouts marcados", () =>
        {
            // Mesmo texto (mesma layer, tipo e tag/bloco) nos layouts marcados.
            List<string> marcados = Marcados();
            var alvos = _textos.Where(o => marcados.Contains(o.Layout, StringComparer.OrdinalIgnoreCase) && o.Tipo == tx.Tipo && o.Layer == tx.Layer && o.Tag == tx.Tag && o.Bloco == tx.Bloco && o.Texto == tx.Texto).Select(o => o.Id).ToList();
            if (alvos.Count == 0) { Say("Nenhum texto igual nos layouts marcados."); return; }
            Executar(() =>
            {
                int n = 0;
                foreach (ObjectId id in alvos)
                    n += LayoutNumbering.EditarTexto(Doc!, id, caixa.Text).Ok;
                return new OpResult(n, 0, $"{n} texto(s) alterado(s).");
            });
        }));
        p.Children.Add(botoes);
        p.Children.Add(Label("\"Aplicar nos layouts marcados\" altera os textos iguais a este (mesma layer, tipo e conteudo) nos layouts marcados: serve para trocar um campo do carimbo em varias pranchas."));
        return p;
    }

    private SW.UIElement BuildSheetBlock()
    {
        WC.StackPanel p = Form();
        SheetBlockOptions o = _prancha;
        p.Children.Add(Label("Inserir bloco de prancha", true));
        p.Children.Add(Label("Contorno do papel, quadro com margens (esquerda maior para encadernacao) e marcas de dobra a cada 210 mm. O bloco e criado/atualizado com o nome PRANCHA_<formato>_E<n>. A quantidade e o modulo de estendimento ficam guardados por formato."));

        var formato = new WC.ComboBox { Width = 160, HorizontalAlignment = SW.HorizontalAlignment.Left };
        foreach (SheetFormat f in SheetFormat.Padrao) formato.Items.Add($"{f.Nome}  ({f.Largura:0} x {f.Altura:0})");
        formato.Items.Add("Personalizado");
        formato.SelectedIndex = Math.Max(0, Array.FindIndex(SheetFormat.Padrao, f => f.Nome == o.Formato));
        if (o.Formato == "Personalizado") formato.SelectedIndex = formato.Items.Count - 1;
        var larg = new WC.TextBox { Width = 70, Text = o.Largura.ToString("0.##", CultureInfo.InvariantCulture) };
        var alt = new WC.TextBox { Width = 70, Text = o.Altura.ToString("0.##", CultureInfo.InvariantCulture) };
        var nEst = new WC.TextBox { Width = 50, Text = o.Estendimentos.ToString(), FontWeight = SW.FontWeights.Bold };
        var tamEst = new WC.TextBox { Width = 70, Text = o.Estendimento.ToString("0.##", CultureInfo.InvariantCulture) };
        var mEsq = new WC.TextBox { Width = 60, Text = o.MargemEsquerda.ToString("0.##", CultureInfo.InvariantCulture) };
        var mOut = new WC.TextBox { Width = 60, Text = o.MargemOutras.ToString("0.##", CultureInfo.InvariantCulture) };
        var layer = new WC.TextBox { Width = 140, Text = o.Layer };
        var dobras = new WC.CheckBox { Content = "Marcas de dobra (modulos de 210 mm)", IsChecked = o.MarcasDeDobra, Margin = new SW.Thickness(0, 8, 0, 0) };
        var retrato = new WC.CheckBox { Content = "Retrato", IsChecked = o.Retrato, Margin = new SW.Thickness(0, 4, 0, 0) };
        var previa = new WC.TextBlock { Foreground = T.Muted, Margin = new SW.Thickness(0, 8, 0, 0), TextWrapping = SW.TextWrapping.Wrap };
        var resumo = new WC.TextBlock { Foreground = T.Muted, Margin = new SW.Thickness(0, 2, 0, 0), TextWrapping = SW.TextWrapping.Wrap };

        double D(WC.TextBox tb, double padrao) => double.TryParse(tb.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : padrao;
        bool carregando = false;
        string FormatoEscolhido() => formato.SelectedIndex >= 0 && formato.SelectedIndex < SheetFormat.Padrao.Length ? SheetFormat.Padrao[formato.SelectedIndex].Nome : "Personalizado";
        void Ler()
        {
            if (carregando)
                return;
            o.Formato = FormatoEscolhido();
            o.Largura = D(larg, 841); o.Altura = D(alt, 594);
            o.Estendimentos = int.TryParse(nEst.Text, out int n) ? Math.Clamp(n, 0, 20) : 0;
            o.Estendimento = D(tamEst, 210);
            o.MargemEsquerda = D(mEsq, 25); o.MargemOutras = D(mOut, 10);
            o.Layer = layer.Text.Trim().Length > 0 ? layer.Text.Trim() : "PRANCHA";
            o.MarcasDeDobra = dobras.IsChecked == true; o.Retrato = retrato.IsChecked == true;
            o.GuardarFormato();
            larg.IsEnabled = alt.IsEnabled = o.Formato == "Personalizado";
            previa.Text = $"{o.NomeBloco()}: {(o.Retrato ? o.AlturaTotal : o.LarguraTotal):0} x {(o.Retrato ? o.LarguraTotal : o.AlturaTotal):0} mm";
            resumo.Text = "Guardado por formato: " + string.Join("   ", SheetFormat.Padrao.Select(f => f.Nome).Append("Personalizado")
                .Where(f => o.PorFormato.ContainsKey(f))
                .Select(f => $"{f} = {o.PorFormato[f].N} x {o.PorFormato[f].Modulo:0}"));
            o.Salvar();
        }
        formato.SelectionChanged += (_, _) =>
        {
            // Troca de formato: recupera os estendimentos guardados daquele formato.
            carregando = true;
            o.AplicarFormato(FormatoEscolhido());
            nEst.Text = o.Estendimentos.ToString();
            tamEst.Text = o.Estendimento.ToString("0.##", CultureInfo.InvariantCulture);
            carregando = false;
            Ler();
        };
        foreach (WC.TextBox tb in new[] { larg, alt, nEst, tamEst, mEsq, mOut, layer }) tb.TextChanged += (_, _) => Ler();
        dobras.Click += (_, _) => Ler(); retrato.Click += (_, _) => Ler();

        void Campo(WC.Panel pai, string rotulo, SW.UIElement c)
        {
            var sp = new WC.StackPanel { Margin = new SW.Thickness(0, 0, 12, 0) };
            sp.Children.Add(Label(rotulo));
            sp.Children.Add(c);
            pai.Children.Add(sp);
        }
        var l1 = new WC.WrapPanel();
        Campo(l1, "Formato", formato); Campo(l1, "Largura", larg); Campo(l1, "Altura", alt);
        Campo(l1, "Estendimentos (qtd.)", nEst); Campo(l1, "Modulo do estendimento (mm)", tamEst);
        p.Children.Add(l1);
        var l2 = new WC.WrapPanel();
        Campo(l2, "Margem esquerda", mEsq); Campo(l2, "Demais margens", mOut); Campo(l2, "Layer", layer);
        p.Children.Add(l2);
        p.Children.Add(dobras);
        p.Children.Add(retrato);
        p.Children.Add(previa);
        p.Children.Add(resumo);
        Ler();

        // Onde inserir: sem precisar abrir os layouts.
        p.Children.Add(Label("Onde inserir", true));
        var emMarcados = new WC.RadioButton { Content = "Nos layouts marcados (sem abrir cada um)", IsChecked = LayoutSettings.GetBool("prancha_em_marcados", true), Margin = new SW.Thickness(0, 2, 0, 0) };
        var emAtual = new WC.RadioButton { Content = "No layout aberto (espaco atual)", IsChecked = !(emMarcados.IsChecked == true), Margin = new SW.Thickness(0, 2, 0, 0) };
        emMarcados.Checked += (_, _) => LayoutSettings.Set("prancha_em_marcados", true);
        emAtual.Checked += (_, _) => LayoutSettings.Set("prancha_em_marcados", false);
        p.Children.Add(emMarcados);
        p.Children.Add(emAtual);
        var px = new WC.TextBox { Width = 80, Text = LayoutSettings.Get("prancha_x", "0") };
        var py = new WC.TextBox { Width = 80, Text = LayoutSettings.Get("prancha_y", "0") };
        px.TextChanged += (_, _) => LayoutSettings.Set("prancha_x", px.Text);
        py.TextChanged += (_, _) => LayoutSettings.Set("prancha_y", py.Text);
        var l3 = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        Campo(l3, "Ponto de insercao X (mm)", px); Campo(l3, "Y (mm)", py);
        p.Children.Add(l3);

        Autodesk.AutoCAD.Geometry.Point3d Ponto()
        {
            double x = double.TryParse(px.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double vx) ? vx : 0;
            double y = double.TryParse(py.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double vy) ? vy : 0;
            return new Autodesk.AutoCAD.Geometry.Point3d(x, y, 0);
        }
        void Inserir(Autodesk.AutoCAD.Geometry.Point3d ponto)
        {
            if (emMarcados.IsChecked == true)
            {
                if (Marcados().Count == 0)
                {
                    Say("Marque os layouts que recebem a prancha (caixa na lista ou no cartao).");
                    return;
                }
                Executar(() => LayoutSheetBlock.InserirEmLayouts(Doc!, o, ponto, Marcados()));
            }
            else
            {
                Executar(() => LayoutSheetBlock.Inserir(Doc!, o, ponto));
            }
        }

        var botoes = new WC.WrapPanel { Margin = new SW.Thickness(0, 6, 0, 0) };
        botoes.Children.Add(ActionButton("Inserir na origem", () => Inserir(Autodesk.AutoCAD.Geometry.Point3d.Origin)));
        botoes.Children.Add(ActionButton("Inserir em X,Y", () => Inserir(Ponto())));
        botoes.Children.Add(ActionButton("Clicar o ponto no layout aberto...", () => Enviar(C3DCommands.Layouts.Prancha)));
        p.Children.Add(botoes);
        return p;
    }

    private void MarcarTextos(string papel, IReadOnlyList<ObjectId> ids, bool moverLayer = true)
    {
        if (ids.Count == 0)
        {
            Say("Marque os textos na lista.");
            return;
        }
        Executar(() => LayoutNumbering.Marcar(Doc!, ids, papel, moverLayer));
    }

    private void Numerar()
    {
        if (_textos.Count(t => t.Papel.Length > 0) == 0)
        {
            SelectCategory("NUMERACAO", "Busca");
            Say("Nenhum texto marcado como NUMERO/TOTAL: busque os textos abaixo, marque o do numero (e o do total) e clique em Numerar de novo.");
            SW.MessageBox.Show(
                "Ainda nao ha texto marcado como numero da prancha.\n\n" +
                "1. Na aba Busca, filtre e localize o texto que numera a prancha (pode estar dentro do bloco do carimbo).\n" +
                "2. Marque-o como NUMERO (e o do total como TOTAL). Isso fica gravado no desenho.\n" +
                "3. Confira prefixo, digitos e total na aba Numeracao e clique em Numerar.",
                "Numerar pranchas", SW.MessageBoxButton.OK, SW.MessageBoxImage.Information);
            return;
        }
        _regra.Marcados = Marcados();
        if (_regra.SoLayoutsMarcados && _regra.Marcados.Count == 0)
        {
            Say("Marque os layouts que devem ser numerados.");
            return;
        }
        Executar(() => LayoutNumbering.Numerar(Doc!, _regra));
    }

    /// <summary>Opcoes de prancha atuais (para o comando C3DPRANCHA).</summary>
    internal static SheetBlockOptions? PranchaAtual => _instance?._prancha;

    private SW.UIElement BuildViewportPower()
    {
        WC.StackPanel p = Form();
        int desligadas = 0;
        try { if (Doc is not null) desligadas = LayoutViewportPower.Desligadas(Doc.Database); } catch (System.Exception) { }
        p.Children.Add(Label("Modo leve: viewports desligadas enquanto se monta a prancha", true));
        p.Children.Add(Label("Trocar de layout com viewports ligadas obriga o AutoCAD a regenerar o Model em cada uma (corredores, superficies). " +
            "Aqui as viewports sao desligadas e marcadas (XData C3D_VPOFF); \"Religar\" liga de volta exatamente essas, sem mexer nas que voce mesmo desligou. " +
            "Quase tudo deste painel funciona com as viewports desligadas (numeracao, prancha, page setup, escala, vinculo com view frames)."));
        p.Children.Add(Prop("Desligadas pelo plugin", desligadas.ToString()));

        var botoes = new WC.WrapPanel();
        botoes.Children.Add(ActionButton("Desligar todas (menos o layout aberto)", () => Executar(() => LayoutViewportPower.Desligar(Doc!, null, poupaAtual: true))));
        botoes.Children.Add(ActionButton("Desligar nos layouts marcados", () => Executar(() => LayoutViewportPower.Desligar(Doc!, Marcados(), poupaAtual: false))));
        botoes.Children.Add(ActionButton("Religar todas", () => Executar(() => LayoutViewportPower.Religar(Doc!, null))));
        botoes.Children.Add(ActionButton("Religar nos layouts marcados", () => Executar(() => LayoutViewportPower.Religar(Doc!, Marcados()))));
        p.Children.Add(botoes);

        var auto = new WC.CheckBox
        {
            Content = "Automatico: ao sair de um layout desliga as viewports dele; ao entrar, religa as dele",
            IsChecked = LayoutViewportPower.Automatico,
            Margin = new SW.Thickness(0, 8, 0, 0)
        };
        auto.Click += (_, _) => { LayoutViewportPower.Automatico = auto.IsChecked == true; Say(auto.IsChecked == true ? "Modo leve automatico ligado." : "Modo leve automatico desligado."); };
        p.Children.Add(auto);
        p.Children.Add(Label("Observacao: no automatico a regeneracao das viewports acontece ao entrar no layout (no religar), entao a troca em si fica rapida mas o custo nao some. " +
            "O ganho real vem de manter as viewports desligadas durante a montagem e religar no fim. Alternativa do proprio AutoCAD: LAYOUTREGENCTL = 2 guarda a lista de exibicao de todos os layouts (mais memoria, troca sem regenerar)."));
        var regen = new WC.WrapPanel { Margin = new SW.Thickness(0, 4, 0, 0) };
        regen.Children.Add(ActionButton("LAYOUTREGENCTL = 2 (guardar todos)", () => Sysvar("LAYOUTREGENCTL", 2)));
        regen.Children.Add(ActionButton("LAYOUTREGENCTL = 1 (padrao)", () => Sysvar("LAYOUTREGENCTL", 1)));
        p.Children.Add(regen);
        return p;
    }

    private void Sysvar(string nome, int valor)
    {
        try
        {
            AcApp.SetSystemVariable(nome, valor);
            Say($"{nome} = {valor}.");
        }
        catch (System.Exception ex)
        {
            Say($"Nao foi possivel definir {nome}: {ex.Message}");
        }
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
            p.Children.Add(Label("Sao listados os blocos com atributos encontrados nos layouts, com a quantidade de layouts em que aparecem. Se a lista esta vazia, o carimbo deste desenho nao usa atributos (e desenho e textos soltos, ou um bloco so com textos dentro)."));
            p.Children.Add(Label("Nesse caso, edite os textos pela categoria Numeracao > Buscar textos: filtre pela layer ou pelo conteudo, selecione o texto e use a aba Editar texto, que tambem aplica a mesma alteracao nos layouts marcados."));
            p.Children.Add(ActionButton("Ir para Buscar textos", () => SelectCategory("NUMERACAO", "Busca")));
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

        var padrao = new WC.ComboBox { Width = 200, HorizontalAlignment = SW.HorizontalAlignment.Left, IsEditable = true, Text = _kmCfg.Padrao };
        foreach (string pat in new[] { "SOLID", "ANSI31", "ANSI32", "ANSI37", "DOTS", "NET", "LINE", "CROSS", "GRASS" })
            padrao.Items.Add(pat);
        padrao.Text = _kmCfg.Padrao;
        var escH = new WC.TextBox { Width = 60, Text = _kmCfg.EscalaHachura.ToString("0.##", CultureInfo.InvariantCulture) };
        var angH = new WC.TextBox { Width = 60, Text = _kmCfg.AnguloHachura.ToString("0.#", CultureInfo.InvariantCulture) };
        var fundo = new WC.ComboBox { Width = 160, HorizontalAlignment = SW.HorizontalAlignment.Left };
        fundo.Items.Add(new WC.ComboBoxItem { Content = "Sem fundo", Tag = (short)-1 });
        foreach ((short aci, string nome) in cores)
            fundo.Items.Add(new WC.ComboBoxItem { Content = $"{nome} ({aci})", Tag = aci });
        fundo.Items.Add(new WC.ComboBoxItem { Content = "Branco (7)", Tag = (short)7 });
        fundo.Items.Add(new WC.ComboBoxItem { Content = "Cinza claro (254)", Tag = (short)254 });
        fundo.SelectedItem = fundo.Items.Cast<WC.ComboBoxItem>().FirstOrDefault(i => (short)i.Tag! == _kmCfg.CorFundo) ?? fundo.Items[0];
        var lh = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        void CampoH(string rotulo, SW.UIElement c)
        {
            var sp = new WC.StackPanel { Margin = new SW.Thickness(0, 0, 12, 0) };
            sp.Children.Add(Label(rotulo));
            sp.Children.Add(c);
            lh.Children.Add(sp);
        }
        CampoH("Padrao da hachura", padrao); CampoH("Escala", escH); CampoH("Angulo (graus)", angH); CampoH("Cor de fundo", fundo);
        p.Children.Add(lh);
        p.Children.Add(Label("Com SOLID nao ha escala, angulo nem fundo. O angulo e somado ao giro da folha no mapa (como na LISP)."));

        var fatorTxt = new WC.TextBox { Width = 60, Text = _kmCfg.FatorTexto.ToString("0.##", CultureInfo.InvariantCulture) };
        var totalMan = new WC.TextBox { Width = 60, Text = _kmCfg.TotalManual > 0 ? _kmCfg.TotalManual.ToString() : "", ToolTip = "Vazio = quantidade de layouts com folha sobre a base" };
        var usarCarimbo = new WC.CheckBox { Content = "Numero/total dos textos marcados do carimbo (senao ordem das abas)", IsChecked = _kmCfg.UsarCarimbo, Margin = new SW.Thickness(0, 6, 0, 0) };
        var titulo = new WC.CheckBox { Content = "Titulo MAPA CHAVE na caixa", IsChecked = _kmCfg.Titulo, Margin = new SW.Thickness(0, 4, 0, 0) };
        var lt = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        var sp1 = new WC.StackPanel { Margin = new SW.Thickness(0, 0, 12, 0) }; sp1.Children.Add(Label("Fator da altura do texto (1 = como a LISP)")); sp1.Children.Add(fatorTxt);
        var sp2 = new WC.StackPanel(); sp2.Children.Add(Label("Total de folhas (vazio = contar)")); sp2.Children.Add(totalMan);
        lt.Children.Add(sp1); lt.Children.Add(sp2);
        p.Children.Add(lt);
        p.Children.Add(usarCarimbo);
        p.Children.Add(titulo);

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
            _kmCfg.Padrao = (padrao.Text ?? "SOLID").Trim().ToUpperInvariant();
            if (_kmCfg.Padrao.Length == 0) _kmCfg.Padrao = "SOLID";
            _kmCfg.EscalaHachura = double.TryParse(escH.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double eh) && eh > 0 ? eh : 1;
            _kmCfg.AnguloHachura = double.TryParse(angH.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double ah) ? ah : 0;
            _kmCfg.CorFundo = (fundo.SelectedItem as WC.ComboBoxItem)?.Tag is short cf ? cf : (short)-1;
            _kmCfg.FatorTexto = double.TryParse(fatorTxt.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double ft) && ft > 0 ? ft : 1;
            _kmCfg.TotalManual = int.TryParse(totalMan.Text, out int tm) && tm > 0 ? tm : 0;
            _kmCfg.UsarCarimbo = usarCarimbo.IsChecked == true;
            _kmCfg.Titulo = titulo.IsChecked == true;
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
        botoes.Children.Add(ActionButton("Inserir no layout atual...", () => Enviar(C3DCommands.Layouts.MapaChave + " Inserir")));
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
            : $"{frames} frame(s) e {linhas} match line(s) em {_vfs.Select(v => v.Grupo).Distinct().Count()} grupo(s); {_vinculos.Count} viewport(s) vinculada(s). Selecione um para ver o layout ligado; duplo clique faz zoom no Model."));

        p.Children.Add(Label("Vincular e alinhar", true));
        p.Children.Add(Label("O vinculo fica gravado na viewport. Ao atualizar, a viewport de planta e centrada no frame, girada pela tangente do eixo e redimensionada para o frame na escala atual; " +
            "a viewport de perfil recebe o intervalo de estacas do frame no profile view e e alinhada pelo inicio (estaca inicial na borda esquerda)."));
        var girar = new WC.CheckBox { Content = "Girar a viewport de planta pela tangente do eixo", IsChecked = LayoutSettings.GetBool("vf_girar", true), Margin = new SW.Thickness(0, 4, 0, 0) };
        girar.Click += (_, _) => LayoutSettings.Set("vf_girar", girar.IsChecked == true);
        p.Children.Add(girar);
        var botoes = new WC.WrapPanel();
        botoes.Children.Add(ActionButton("Vincular automaticamente", () => Executar(() => LayoutViewFrameSync.VincularAutomatico(Doc!, _vfs, _folhas))));
        botoes.Children.Add(ActionButton("Vincular marcados", () => VincularMarcados(girar.IsChecked == true)));
        botoes.Children.Add(ActionButton("Desvincular", () => Executar(() => LayoutViewFrameSync.Desvincular(Doc!, VpsMarcadas()))));
        botoes.Children.Add(ActionButton("Atualizar viewports", AtualizarViewFrames));
        p.Children.Add(botoes);
        p.Children.Add(Label("Automatico: usa a folha ligada ao frame pelo Civil. Marcados: 1 frame nesta lista + viewports na miniatura; cada viewport vira perfil se mostra um profile view do eixo do frame, senao planta. " +
            "Atualizar: as viewports marcadas ou, sem marcacao, todas as vinculadas. Rotulos e zoom ficam na barra de comandos."));
        return p;
    }

    private void AtualizarViewFrames()
    {
        List<ObjectId> marcadas = VpsMarcadas().ToList();
        Executar(() => LayoutViewFrameSync.Atualizar(Doc!, marcadas.Count > 0 ? marcadas : null, _vfs));
    }

    private string VinculosTexto(ViewFrameItem v)
    {
        var lista = new List<string>();
        foreach ((ObjectId vpId, ViewFrameLink l) in _vinculos)
        {
            if (l.Frame != v.Id || !_vpPorId.TryGetValue(vpId, out SheetViewport? vp))
                continue;
            SheetData? f = _folhas.FirstOrDefault(x => x.Viewports.Contains(vp));
            lista.Add($"{f?.Nome} #{vp.Indice} ({(l.Perfil ? "perfil" : "planta")})");
        }
        return string.Join("; ", lista);
    }

    private void VincularMarcados(bool girar)
    {
        List<ViewFrameItem> frames = _vfs.Where(v => v.Tipo == "Frame" && _vfsMarcados.Contains(v.Id)).ToList();
        if (frames.Count != 1)
        {
            Say("Marque exatamente um view frame na lista.");
            return;
        }
        if (_vpsMarcadas.Count == 0)
        {
            Say("Marque as viewports na miniatura.");
            return;
        }
        if (Doc is not Document doc)
            return;
        Executar(() =>
        {
            int planta = 0, perfil = 0;
            foreach (ObjectId vpId in _vpsMarcadas)
            {
                ObjectId pv = ProfileViewNaViewport(doc, vpId);
                if (pv.IsNull)
                {
                    planta += LayoutViewFrameSync.Vincular(doc, vpId, frames[0].Id, false, ObjectId.Null, girar).Ok;
                }
                else
                {
                    perfil += LayoutViewFrameSync.Vincular(doc, vpId, frames[0].Id, true, pv, false).Ok;
                }
            }
            return new OpResult(planta + perfil, 0, $"{planta} viewport(s) de planta e {perfil} de perfil vinculada(s) a {frames[0].Nome}.");
        });
    }

    /// <summary>Profile view cujo centro cai dentro da viewport (pela regiao da viewport no Model).</summary>
    private static ObjectId ProfileViewNaViewport(Document doc, ObjectId vpId)
    {
        try
        {
            Database db = doc.Database;
            var civ = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(db);
            if (civ.GetType().GetMethod("GetProfileViewIds", Type.EmptyTypes)?.Invoke(civ, null) is not ObjectIdCollection ids)
                return ObjectId.Null;
            using Transaction tr = db.TransactionManager.StartTransaction();
            if (tr.GetObject(vpId, OpenMode.ForRead) is not Viewport vp || vp.CustomScale <= 0)
                return ObjectId.Null;
            Autodesk.AutoCAD.Geometry.Matrix3d dcs2wcs =
                Autodesk.AutoCAD.Geometry.Matrix3d.Rotation(-vp.TwistAngle, vp.ViewDirection, vp.ViewTarget) *
                Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(vp.ViewTarget - Autodesk.AutoCAD.Geometry.Point3d.Origin) *
                Autodesk.AutoCAD.Geometry.Matrix3d.PlaneToWorld(vp.ViewDirection);
            Autodesk.AutoCAD.Geometry.Matrix3d wcs2dcs = dcs2wcs.Inverse();
            double hw = vp.Width / vp.CustomScale / 2, hh = vp.Height / vp.CustomScale / 2;
            foreach (ObjectId id in ids)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity e)
                    continue;
                Extents3d ext = e.GeometricExtents;
                var c = new Autodesk.AutoCAD.Geometry.Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0).TransformBy(wcs2dcs);
                if (Math.Abs(c.X - vp.ViewCenter.X) <= hw && Math.Abs(c.Y - vp.ViewCenter.Y) <= hh)
                    return id;
            }
            tr.Commit();
        }
        catch (System.Exception)
        {
        }
        return ObjectId.Null;
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
