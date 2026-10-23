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
using WMI = System.Windows.Media.Imaging;
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

    /// <summary>Atualizacao automatica da rede neste desenho; desligada, a rede pode estar pendente (_stale).</summary>
    private bool _auto = true;
    private bool _stale;
    private bool _missing;
    private WC.Button? _autoButton;
    private List<RoadSection> _sections = new();
    private ViarioStore.Settings _settings = new();
    private EngineSettings _engine = new();
    private Dictionary<string, string> _names = new();

    /// <summary>Nome dado a cada eixo (XData proprio; chave = handle).</summary>
    private Dictionary<string, string> _ownNames = new();

    /// <summary>Linhas marcadas na lista de eixos (Id da linha); os comandos em lote valem para elas.</summary>
    private readonly HashSet<string> _checked = new(StringComparer.Ordinal);

    private List<Row> _rows = new();
    private SignageOptions _signage = new();
    private List<Hint> _hints = new();

    // Recomendacoes guardadas enquanto a rede, os eixos e os registros nao mudam.
    private object? _hintsResult;
    private ulong _hintsAxes;
    private string _hintsRecords = "";

    // Indices montados a cada leitura (listas de centenas de eixos sem varreduras repetidas).
    private Dictionary<string, int> _axisIndex = new(StringComparer.Ordinal);
    private Dictionary<string, int> _nodeCount = new(StringComparer.Ordinal);
    private Dictionary<(long, long), List<CornerInfo>> _cornerCells = new();
    private Dictionary<CornerInfo, int> _cornerOrder = new();
    private Dictionary<string, List<Hint>> _hintsByTarget = new(StringComparer.Ordinal);
    private Dictionary<(long, long), List<Hint>> _hintCells = new();
    private Dictionary<Hint, int> _hintOrder = new();
    private const double HintCell = 30.0;
    private bool _civilActive;
    private bool _signExists;
    private List<PlacaItem> _placas = new();
    private (string Cat, int Col, bool Desc)? _sort;
    private SW.UIElement[] _fullParts = Array.Empty<SW.UIElement>();
    private bool _fullMonitor;
    private SW.WindowState _stateBefore;
    private double _monitorRowBefore;

    /// <summary>Monitor em tela cheia: esconde categorias, comandos, lista e propriedades e maximiza a janela.</summary>
    private void ToggleFullMonitor()
    {
        _fullMonitor = !_fullMonitor;
        foreach (SW.UIElement e in _fullParts)
            e.Visibility = _fullMonitor ? SW.Visibility.Collapsed : SW.Visibility.Visible;
        if (Content is WC.Grid root && root.RowDefinitions.Count > 5)
        {
            if (_fullMonitor)
            {
                _monitorRowBefore = root.RowDefinitions[2].Height.Value;
                root.RowDefinitions[2].Height = new SW.GridLength(1, SW.GridUnitType.Star);
                root.RowDefinitions[5].Height = new SW.GridLength(0);
                root.RowDefinitions[5].MinHeight = 0;
            }
            else
            {
                root.RowDefinitions[2].Height = new SW.GridLength(_monitorRowBefore > 0 ? _monitorRowBefore : 0.85, SW.GridUnitType.Star);
                root.RowDefinitions[5].Height = new SW.GridLength(1, SW.GridUnitType.Star);
                root.RowDefinitions[5].MinHeight = 160;
            }
        }

        if (_fullMonitor)
        {
            _stateBefore = WindowState;
            WindowState = SW.WindowState.Maximized;
        }
        else
        {
            WindowState = _stateBefore;
        }

        Dispatcher.BeginInvoke(new Action(() => _render?.Invoke()), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private readonly Dictionary<string, string> _colFilter = new(StringComparer.Ordinal);
    private List<AtrativoItem> _atrativos = new();
    private bool _refreshQueued;

    /// <summary>Quantas vezes o painel foi atualizado (o aviso na fila pula se ja houve uma depois dele).</summary>
    private int _refreshCount;

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

    /// <summary>Textos da vista: quais aparecem (menu Textos).</summary>
    private static readonly (string Key, string Title)[] TextKinds =
    {
        ("eixos", "Nomes dos eixos"),
        ("estacas", "Estacas ao longo dos eixos"),
        ("pf", "Estaca final (PF)"),
        ("reguas", "Reguas de coordenadas"),
        ("escala", "Escala grafica e norte"),
        ("titulo", "Titulo e LE/LD (secao)"),
        ("componentes", "Nomes dos componentes (secao)"),
        ("cotas", "Textos das cotas (secao)")
    };

    private static HashSet<string> _texts = LoadTextPreference();
    private static bool Show(string key) => _texts.Contains(key);
    private bool _sectionView;

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
        new("RECONCORDAR", "C3DVIARIOEIXO CO V ", "Concordancia", "Eixos",
            "Vincular/desvincular a concordancia de eixos existentes, modo dos proximos eixos e reconcordar dois trechos ([Arco] refaz o arco pela reta, [Tangente] move a reta ate o arco). Com a concordancia vinculada, arrastar o meio do arco muda o raio e as tangentes acompanham.",
            new[] { "O botao abre o menu.", "Vincular/Desvincular: selecione os eixos.", "Reconcordar: clique perto do vertice entre os dois trechos." }),
        new("INVERTER", "C3DVIARIOEIXO I ", "Inverter sentido do eixo", "Eixos",
            "Inverte o sentido (estacas e esquerda/direita); elementos e raios acompanham.", new[] { "Selecione os eixos." }),
        new("DESVINCULAR", "C3DVIARIO DE ", "Desvincular linhas", "Linhas",
            "Linhas geradas viram polilinhas comuns para edicao manual; o plugin nao as recria nem apaga.",
            new[] { "Selecione as linhas.", "Desvincular > Revincular devolve ao plugin." }),
        new("ATUALIZAR", "C3DVIARIO ATU ", "Atualizar rede", "Linhas", "Recalcula a rede inteira.", new[] { "Sem perguntas." }),
        new("CIVIL", "C3DVIARIOCIVIL G ", "Objetos do Civil", "Saidas",
            "Alinhamentos do Civil: eixos, bordos LD/LE e curb returns conectados.", new[] { "Gera ou atualiza; depois acompanham a rede." }),
        new("SINAL", "C3DVIARIOSINAL G ", "Sinalizacao horizontal", "Saidas",
            "Sinalizacao previa num bloco unico que acompanha a rede.", new[] { "O botao abre o menu: gerar/atualizar, ocultar, mostrar, remover.", "Opcoes na categoria Sinalizacao." }),
        new("PLACAS", "C3DVIARIOPLACAS ", "Placas verticais (SinC)", "Saidas",
            "Importa as placas de um projeto e insere cada uma no padrao do SinC (bloco T-xxx_SV com atributos e linha de chamada), amarrada ao eixo mais proximo.",
            new[] { "Importar: catalogo JSON/CSV e pasta dos T-xxx_SV.dwg.", "ATualizar: recalcula eixo, estaca e lado depois de editar eixos.", "NOmear: da aos eixos o nome da rota/trecho das placas." }),
        new("ATRATIVOS", "C3DVIARIOATRATIVOS ", "Atrativos turisticos (KMZ)", "Saidas",
            "Importa os pontos de um KML/KMZ como circulos com hachura transparente (bloco com nome e categoria em atributos; a escala do bloco e o raio).",
            new[] { "Importar: escolha o KMZ e o raio.", "Raio: muda o raio de todos.", "Os atrativos aparecem na categoria Atrativos e perto de cada placa." }),
        new("HACHURA", "C3DVIARIOHACHURA G ", "Hachura do pavimento", "Saidas",
            "Uma hachura so da regiao de pavimento: contorno pelas faces de meio-fio, quadras, canteiros e ilhas como furos; acompanha a rede.",
            new[] { "O botao abre o menu: gerar/atualizar, padrao, escala, solido com transparencia, ocultar, remover." }),
        new("COTAR", "C3DVIARIOCOTAR ", "Cotar faixas", "Saidas",
            "Cadeia de cotas da secao em planta (faixas, canteiro, passeio) e largura total, na estaca clicada.",
            new[] { "O botao abre o menu: estilo de cota, escala (DIMSCALE), detalhe, cotar e remover.", "Cotar: passe sobre o eixo (a cadeia segue o cursor) e clique." }),
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
        ("PLACAS", "PLACAS", "Placas verticais"),
        ("ATRATIVOS", "ATRATIVOS", "Atrativos turisticos"),
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
        int seen = panel._refreshCount;
        panel.Dispatcher.BeginInvoke(new Action(() =>
        {
            panel._refreshQueued = false;
            if (_instance == panel && panel._refreshCount == seen)
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
        Civil3D2026Plugin.UI.Common.LogView.Atalho(this, "Viario");
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
        // So as linhas visiveis sao montadas (listas de centenas de eixos).
        WC.VirtualizingPanel.SetIsVirtualizing(_list, true);
        WC.VirtualizingPanel.SetVirtualizationMode(_list, WC.VirtualizationMode.Recycling);
        WC.ScrollViewer.SetCanContentScroll(_list, true);
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
        // Localizar: espera a digitacao parar (listas de centenas de eixos).
        System.Windows.Threading.DispatcherTimer findTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        findTimer.Tick += (_, _) =>
        {
            findTimer.Stop();
            FillList();
        };
        _find.TextChanged += (_, _) =>
        {
            findTimer.Stop();
            findTimer.Start();
        };
        WC.DockPanel.SetDock(findPanel, WC.Dock.Right);
        commandRow.Children.Add(findPanel);
        commandRow.Children.Add(BuildCommandBar());
        WC.Grid.SetRow(commandRow, 1);
        root.Children.Add(commandRow);

        WC.Border graphic = new() { BorderBrush = T.Line, BorderThickness = new SW.Thickness(1), Child = _graphic, Margin = new SW.Thickness(4, 0, 4, 0) };
        _graphic.SizeChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => _render?.Invoke()), System.Windows.Threading.DispatcherPriority.Render);
        graphic.SizeChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => _render?.Invoke()), System.Windows.Threading.DispatcherPriority.Loaded);
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
        toggles.Children.Add(TextMenu());
        WC.DockPanel.SetDock(toggles, WC.Dock.Left);
        graphicBar.Children.Add(toggles);
        WC.DockPanel.SetDock(_coords, WC.Dock.Right);
        graphicBar.Children.Add(_coords);
        WC.Button full = new()
        {
            Content = "⛶", ToolTip = "Monitor em tela cheia (clique de novo para voltar)", Width = 24, Height = 20, Padding = new SW.Thickness(0),
            Background = WM.Brushes.Transparent, BorderBrush = WM.Brushes.Transparent, Foreground = T.Text, Margin = new SW.Thickness(4, 0, 4, 0)
        };
        full.Click += (_, _) => ToggleFullMonitor();
        WC.DockPanel.SetDock(full, WC.Dock.Right);
        graphicBar.Children.Add(full);
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
        _list.SelectionMode = WC.SelectionMode.Extended;
        _list.SelectionChanged += (_, _) => OnRowSelected();
        _list.MouseDoubleClick += (_, _) => ZoomCurrent();
        _list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == WI.Key.Space && _category == "EIXOS" && _list.SelectedItems.Count > 0)
            {
                List<Row> sel = _list.SelectedItems.OfType<Row>().ToList();
                bool mark = sel.Any(r => !r.Checked);
                foreach (Row r in sel)
                    r.Checked = mark;
                OnChecksChanged();
                e.Handled = true;
            }
        };
        _list.MouseRightButtonUp += (_, e) =>
        {
            if (_category == "EIXOS")
            {
                OpenAxisListMenu();
                e.Handled = true;
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
        _fullParts = new SW.UIElement[] { _categoryBar, commandRow, splitH, bottom, footer };

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

        // Menus: o cromado padrao (Aero2) traz cores fixas - coluna de icones branca,
        // submenus em #F0F0F0, texto desabilitado #707070 - e no tema escuro os
        // submenus ficavam ilegiveis. Modelos proprios para ContextMenu, MenuItem
        // e Separator, pintados com o tema.
        Resources.MergedDictionaries.Clear();
        try
        {
            Resources.MergedDictionaries.Add(MenuStyles());
        }
        catch
        {
            SW.Style menu = new(typeof(WC.ContextMenu));
            menu.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Pane));
            menu.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
            Resources[typeof(WC.ContextMenu)] = menu;
        }
    }

    private static string Hex(WM.Brush brush) =>
        brush is WM.SolidColorBrush s ? $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : "#808080";

    /// <summary>Dicionario com os estilos de menu (ContextMenu, MenuItem com submenu e marca, Separator) no tema corrente.</summary>
    private SW.ResourceDictionary MenuStyles()
    {
        string xaml = $@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <SolidColorBrush x:Key='MBg' Color='{Hex(T.Pane)}'/>
  <SolidColorBrush x:Key='MFg' Color='{Hex(T.Text)}'/>
  <SolidColorBrush x:Key='MMuted' Color='{Hex(T.Muted)}'/>
  <SolidColorBrush x:Key='MLine' Color='{Hex(T.Line)}'/>
  <SolidColorBrush x:Key='MSel' Color='{Hex(T.Selected)}'/>
  <SolidColorBrush x:Key='MGutter' Color='{Hex(T.Header)}'/>

  <Style TargetType='ContextMenu'>
    <Setter Property='Background' Value='{{StaticResource MBg}}'/>
    <Setter Property='Foreground' Value='{{StaticResource MFg}}'/>
    <Setter Property='FontFamily' Value='Tahoma'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='HasDropShadow' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ContextMenu'>
          <Border Background='{{TemplateBinding Background}}' BorderBrush='{{StaticResource MLine}}' BorderThickness='1' Padding='2' SnapsToDevicePixels='True'>
            <Grid>
              <Rectangle Width='24' HorizontalAlignment='Left' Fill='{{StaticResource MGutter}}' Margin='0,-2,0,-2'/>
              <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Cycle'/>
            </Grid>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='{{x:Static MenuItem.SeparatorStyleKey}}' TargetType='Separator'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Separator'>
          <Border Height='1' Background='{{StaticResource MLine}}' Margin='28,3,4,3' SnapsToDevicePixels='True'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='MenuItem'>
    <Setter Property='Foreground' Value='{{StaticResource MFg}}'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='MenuItem'>
          <Border x:Name='Bd' Background='{{TemplateBinding Background}}' SnapsToDevicePixels='True'>
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width='24'/>
                <ColumnDefinition Width='*'/>
                <ColumnDefinition Width='18'/>
              </Grid.ColumnDefinitions>
              <Path x:Name='Check' Data='M0,4 L3.5,7.5 L9.5,0.5' Stroke='{{TemplateBinding Foreground}}' StrokeThickness='1.8'
                    Width='10' Height='8' Stretch='None' HorizontalAlignment='Center' VerticalAlignment='Center' Visibility='Collapsed'/>
              <ContentPresenter Grid.Column='1' ContentSource='Header' Margin='6,4,12,4' VerticalAlignment='Center' RecognizesAccessKey='True'
                                SnapsToDevicePixels='{{TemplateBinding SnapsToDevicePixels}}'/>
              <Path x:Name='Arrow' Grid.Column='2' Data='M0,0 L4,4 L0,8 Z' Fill='{{TemplateBinding Foreground}}'
                    HorizontalAlignment='Center' VerticalAlignment='Center' Visibility='Collapsed'/>
              <Popup x:Name='PART_Popup' Grid.Column='0' Grid.ColumnSpan='3' Placement='Right' HorizontalOffset='-2' VerticalOffset='-3'
                     IsOpen='{{Binding IsSubmenuOpen, RelativeSource={{RelativeSource TemplatedParent}}}}'
                     AllowsTransparency='True' Focusable='False' PopupAnimation='None'>
                <Border Background='{{StaticResource MBg}}' BorderBrush='{{StaticResource MLine}}' BorderThickness='1' Padding='2' SnapsToDevicePixels='True'>
                  <Grid>
                    <Rectangle Width='24' HorizontalAlignment='Left' Fill='{{StaticResource MGutter}}' Margin='0,-2,0,-2'/>
                    <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Cycle' Grid.IsSharedSizeScope='True'/>
                  </Grid>
                </Border>
              </Popup>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{{StaticResource MSel}}'/>
            </Trigger>
            <Trigger Property='IsSubmenuOpen' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{{StaticResource MSel}}'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Check' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='HasItems' Value='True'>
              <Setter TargetName='Arrow' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Foreground' Value='{{StaticResource MMuted}}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
        return (SW.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
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
                if (c.Key == "SINAL")
                {
                    button.Content = new WC.StackPanel
                    {
                        Orientation = WC.Orientation.Horizontal,
                        Children =
                        {
                            new WC.Image { Source = ViarioIcons.Image(c.Key), Width = 20, Height = 20 },
                            new WC.TextBlock { Text = "▾", Foreground = T.Muted, FontSize = 9, VerticalAlignment = SW.VerticalAlignment.Bottom }
                        }
                    };
                    button.Click += (_, _) => OpenSignageMenu(button);
                }
                else if (c.Key == "HACHURA")
                {
                    button.Content = new WC.StackPanel
                    {
                        Orientation = WC.Orientation.Horizontal,
                        Children =
                        {
                            new WC.Image { Source = ViarioIcons.Image(c.Key), Width = 20, Height = 20 },
                            new WC.TextBlock { Text = "▾", Foreground = T.Muted, FontSize = 9, VerticalAlignment = SW.VerticalAlignment.Bottom }
                        }
                    };
                    button.Click += (_, _) => OpenHatchMenu(button);
                }
                else if (c.Key == "PLACAS")
                {
                    button.Content = new WC.StackPanel
                    {
                        Orientation = WC.Orientation.Horizontal,
                        Children =
                        {
                            new WC.Image { Source = ViarioIcons.Image(c.Key), Width = 20, Height = 20 },
                            new WC.TextBlock { Text = "▾", Foreground = T.Muted, FontSize = 9, VerticalAlignment = SW.VerticalAlignment.Bottom }
                        }
                    };
                    button.Click += (_, _) => OpenPlacasMenu(button);
                }
                else if (c.Key == "RECONCORDAR")
                {
                    button.Content = new WC.StackPanel
                    {
                        Orientation = WC.Orientation.Horizontal,
                        Children =
                        {
                            new WC.Image { Source = ViarioIcons.Image(c.Key), Width = 20, Height = 20 },
                            new WC.TextBlock { Text = "▾", Foreground = T.Muted, FontSize = 9, VerticalAlignment = SW.VerticalAlignment.Bottom }
                        }
                    };
                    button.Click += (_, _) => OpenChainMenu(button);
                }
                else if (c.Key == "COTAR")
                {
                    button.Content = new WC.StackPanel
                    {
                        Orientation = WC.Orientation.Horizontal,
                        Children =
                        {
                            new WC.Image { Source = ViarioIcons.Image(c.Key), Width = 20, Height = 20 },
                            new WC.TextBlock { Text = "▾", Foreground = T.Muted, FontSize = 9, VerticalAlignment = SW.VerticalAlignment.Bottom }
                        }
                    };
                    button.Click += (_, _) => OpenDimensionMenu(button);
                }
                else
                {
                    button.Click += (_, _) => Send(c.Send, c.Title + ": siga a linha de comando.");
                }

                bar.Children.Add(button);
                if (c.Key == "ATUALIZAR")
                    bar.Children.Add(BuildAutoButton());
            }
        }

        return tray;
    }

    /// <summary>Botao liga/desliga da atualizacao automatica (ao lado do Atualizar rede).</summary>
    private WC.Button BuildAutoButton()
    {
        _autoButton = new WC.Button
        {
            Padding = new SW.Thickness(4, 1, 4, 1),
            Margin = new SW.Thickness(2, 0, 0, 0),
            Background = WM.Brushes.Transparent,
            BorderBrush = T.Line,
            VerticalAlignment = SW.VerticalAlignment.Center
        };
        WC.ToolTipService.SetInitialShowDelay(_autoButton, 250);
        WC.ToolTipService.SetShowDuration(_autoButton, 30000);
        _autoButton.Click += (_, _) => ToggleAuto();
        UpdateAutoButton();
        return _autoButton;
    }

    private void UpdateAutoButton()
    {
        if (_autoButton is null)
            return;

        WM.Brush dot = _auto ? Brush(60, 170, 75) : _stale ? T.Warn : T.Muted;
        _autoButton.Content = new WC.StackPanel
        {
            Orientation = WC.Orientation.Horizontal,
            Children =
            {
                new WC.TextBlock { Text = "\u25CF", Foreground = dot, FontSize = 11, Margin = new SW.Thickness(0, 0, 3, 0), VerticalAlignment = SW.VerticalAlignment.Center },
                new WC.TextBlock { Text = _auto ? "Auto" : _stale ? "Manual (pendente)" : "Manual", Foreground = T.Text, VerticalAlignment = SW.VerticalAlignment.Center }
            }
        };
        _autoButton.ToolTip = new WC.ToolTip
        {
            Content = new WC.TextBlock
            {
                MaxWidth = 380,
                TextWrapping = SW.TextWrapping.Wrap,
                Text = _auto
                    ? "Atualizacao automatica: LIGADA.\n\nA rede e refeita ao fim de cada comando que mexe nos eixos.\n\n" +
                      "Clique para desligar em redes grandes (centenas de vias): os eixos mudam a vontade e a rede so e refeita no \"Atualizar rede\". Fica salvo no desenho."
                    : "Atualizacao automatica: DESLIGADA" + (_stale ? " - ha mudancas nos eixos ainda nao aplicadas" : "") + ".\n\n" +
                      "Os eixos mudam sem refazer a rede (sem previa da rede no arraste de grips; a concordancia dos eixos continua). " +
                      "Use \"Atualizar rede\" (botao ao lado) para ver o resultado. Clique para religar (com mudancas pendentes, a rede e refeita na hora)."
            }
        };
    }

    private void ToggleAuto()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        bool on = !ViarioSync.IsAuto(doc.Database);
        try
        {
            if (on)
                Say("Atualizando a rede...");
            using (doc.LockDocument())
            {
                ViarioSync.Initialize();
                ViarioSync.SetAuto(doc, on);
                doc.Editor.UpdateScreen();
                AcApp.UpdateScreen();
            }

            Refresh();
            Say(on
                ? "Atualizacao automatica ligada."
                : "Atualizacao automatica desligada: use Atualizar rede quando quiser ver o resultado.");
        }
        catch (Exception ex)
        {
            Refresh();
            Say("Erro: " + ex.Message);
        }
    }

    /// <summary>Escalas (DIMSCALE) oferecidas no menu das cotas; 0 = a do estilo/desenho.</summary>
    private static readonly double[] DimScales = { 0, 0.25, 0.5, 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000 };

    /// <summary>Menu do botao Cotar faixas: estilo e escala para clicar, detalhe, cotar e remover.</summary>
    private void OpenDimensionMenu(WC.Button anchor)
    {
        WC.ContextMenu menu = new() { PlacementTarget = anchor, Placement = WC.Primitives.PlacementMode.Bottom };

        WC.MenuItem run = new() { Header = "Cotar faixas... (passe sobre o eixo e clique)", FontWeight = SW.FontWeights.Bold };
        run.Click += (_, _) => Send("C3DVIARIOCOTAR ", "Passe sobre o eixo e clique onde quer a cota.");
        menu.Items.Add(run);
        menu.Items.Add(new WC.Separator());

        // Estilo de cota.
        WC.MenuItem styles = new() { Header = "Estilo de cota: " + (_settings.CotaEstilo.Length > 0 ? _settings.CotaEstilo : "corrente do desenho") };
        List<string> names = new();
        try
        {
            if (AcApp.DocumentManager.MdiActiveDocument is Document d)
                names = ViarioDimensions.StyleNames(d.Database);
        }
        catch
        {
            // Sem desenho ativo.
        }

        foreach ((string text, string value) in new[] { ("corrente do desenho", "") }.Concat(names.Select(n => (n, n))))
        {
            WC.MenuItem mi = new() { Header = text, IsCheckable = true, IsChecked = value == _settings.CotaEstilo };
            string v = value;
            mi.Click += (_, _) => SaveDimensionSetting(st => st.CotaEstilo = v, "Estilo de cota: " + text);
            styles.Items.Add(mi);
        }

        menu.Items.Add(styles);

        // Escala (DIMSCALE).
        WC.MenuItem scales = new() { Header = "Escala (DIMSCALE): " + (_settings.CotaEscala > 0 ? N(_settings.CotaEscala) : "do estilo") };
        foreach (double sc in DimScales)
        {
            string text = sc <= 0 ? "do estilo / desenho" : N(sc);
            WC.MenuItem mi = new() { Header = text, IsCheckable = true, IsChecked = Math.Abs(sc - _settings.CotaEscala) < 1e-9 };
            double v = sc;
            mi.Click += (_, _) => SaveDimensionSetting(st => st.CotaEscala = v, "Escala das cotas: " + text);
            scales.Items.Add(mi);
        }

        if (_settings.CotaEscala > 0 && !DimScales.Any(v => Math.Abs(v - _settings.CotaEscala) < 1e-9))
            scales.Items.Add(new WC.MenuItem { Header = N(_settings.CotaEscala) + " (atual)", IsCheckable = true, IsChecked = true, IsEnabled = false });
        scales.Items.Add(new WC.Separator());
        WC.MenuItem other = new() { Header = "Outro valor... (Config > Cotas das faixas)" };
        other.Click += (_, _) => SelectCategory("CONFIG");
        scales.Items.Add(other);
        menu.Items.Add(scales);

        WC.MenuItem detail = new() { Header = "Sarjeta e meio-fio em cotas separadas", IsCheckable = true, IsChecked = ViarioDimensions.Detail };
        detail.Click += (_, _) => ViarioDimensions.Detail = detail.IsChecked;
        menu.Items.Add(detail);

        menu.Items.Add(new WC.Separator());
        WC.MenuItem removeNear = new() { Header = "Remover cota... (clique perto da cadeia)" };
        removeNear.Click += (_, _) => Send("C3DVIARIOCOTAR R ", "Clique perto da cadeia de cotas a remover.");
        menu.Items.Add(removeNear);
        WC.MenuItem remove = new() { Header = "Remover todas as cotas do viario" };
        remove.Click += (_, _) => Send("C3DVIARIOCOTAR R T ", "Cotas removidas; Enter para sair.");
        menu.Items.Add(remove);
        menu.Items.Add(new WC.MenuItem { Header = "Layer: " + ViarioStore.LayerDims, IsEnabled = false });

        menu.IsOpen = true;
    }

    /// <summary>Menu do botao Sinalizacao: gerar/atualizar, ocultar/mostrar, remover e opcoes.</summary>
    /// <summary>Menu do botao Concordancia: vincular/desvincular eixos, modo dos proximos e reconcordar.</summary>
    private void OpenChainMenu(WC.Button anchor)
    {
        WC.ContextMenu menu = new() { PlacementTarget = anchor, Placement = WC.Primitives.PlacementMode.Bottom };

        WC.MenuItem link = new() { Header = "Vincular concordancia... (selecione os eixos)", FontWeight = SW.FontWeights.Bold };
        link.Click += (_, _) => Send("C3DVIARIOEIXO CO VI ", "Selecione os eixos: retas e arcos ficam tangentes e sao refeitos ao editar.");
        menu.Items.Add(link);
        WC.MenuItem unlink = new() { Header = "Desvincular concordancia... (selecione os eixos)" };
        unlink.Click += (_, _) => Send("C3DVIARIOEIXO CO DE ", "Selecione os eixos: vertices e arcos passam a ser editados livremente.");
        menu.Items.Add(unlink);
        menu.Items.Add(new WC.Separator());
        WC.MenuItem next = new() { Header = "Proximos eixos lancados" };
        WC.MenuItem nextChain = new() { Header = "concordantes (T reta, A arco tangente, U desfaz)" };
        nextChain.Click += (_, _) => Send("C3DVIARIOEIXO CO M ", "Proximos eixos concordantes.");
        next.Items.Add(nextChain);
        WC.MenuItem nextFree = new() { Header = "livres" };
        nextFree.Click += (_, _) => Send("C3DVIARIOEIXO CO L ", "Proximos eixos livres.");
        next.Items.Add(nextFree);
        menu.Items.Add(next);
        menu.Items.Add(new WC.Separator());
        WC.MenuItem reconc = new() { Header = "Reconcordar trecho... (clique perto do vertice)" };
        reconc.Click += (_, _) => Send("C3DVIARIOEIXO CO V ", "Clique perto do vertice entre os dois trechos.");
        menu.Items.Add(reconc);
        menu.Items.Add(new WC.MenuItem
        {
            Header = "Vinculado: arrastar o meio do arco muda o raio; tangentes acompanham",
            IsEnabled = false
        });
        menu.IsOpen = true;
    }

    private void OpenSignageMenu(WC.Button anchor)
    {
        WC.ContextMenu menu = new() { PlacementTarget = anchor, Placement = WC.Primitives.PlacementMode.Bottom };
        bool visible = true;
        try
        {
            if (AcApp.DocumentManager.MdiActiveDocument is Document d)
                visible = ViarioSignageSync.IsVisible(d.Database);
        }
        catch
        {
            // Sem desenho ativo.
        }

        WC.MenuItem gen = new() { Header = _signExists ? "Atualizar sinalizacao" : "Gerar sinalizacao", FontWeight = SW.FontWeights.Bold };
        gen.Click += (_, _) => Send("C3DVIARIOSINAL G ", "Sinalizacao gerada.");
        menu.Items.Add(gen);
        menu.Items.Add(new WC.Separator());
        WC.MenuItem hide = new() { Header = "Ocultar (desliga as layers VIARIO-SH-*)", IsEnabled = _signExists && visible };
        hide.Click += (_, _) => Send("C3DVIARIOSINAL O ", "Sinalizacao oculta.");
        menu.Items.Add(hide);
        WC.MenuItem show = new() { Header = "Mostrar", IsEnabled = _signExists && !visible };
        show.Click += (_, _) => Send("C3DVIARIOSINAL M ", "Sinalizacao visivel.");
        menu.Items.Add(show);
        WC.MenuItem remove = new() { Header = "Remover bloco", IsEnabled = _signExists };
        remove.Click += (_, _) => Send("C3DVIARIOSINAL R ", "Sinalizacao removida.");
        menu.Items.Add(remove);
        menu.Items.Add(new WC.Separator());
        WC.MenuItem opts = new() { Header = "Opcoes (categoria Sinalizacao)" };
        opts.Click += (_, _) => SelectCategory("SINAL");
        menu.Items.Add(opts);
        menu.IsOpen = true;
    }

    /// <summary>Menu do botao Hachura: gerar/atualizar, padrao, escala, solido, ocultar/mostrar, remover.</summary>
    private void OpenPlacasMenu(WC.Button anchor)
    {
        WC.ContextMenu menu = new() { PlacementTarget = anchor, Placement = WC.Primitives.PlacementMode.Bottom };
        bool exists = _placas.Count > 0;
        WC.MenuItem import = new() { Header = "Importar placas (JSON/CSV + pasta do SinC)...", FontWeight = SW.FontWeights.Bold };
        import.Click += (_, _) => Send("C3DVIARIOPLACAS I ", "Importar placas: siga a linha de comando.");
        menu.Items.Add(import);
        menu.Items.Add(new WC.Separator());
        WC.MenuItem relink = new() { Header = "Atualizar eixo, estaca e lado", IsEnabled = exists };
        relink.Click += (_, _) => Send("C3DVIARIOPLACAS AT ", "Atualizar vinculos das placas: siga a linha de comando.");
        menu.Items.Add(relink);
        WC.MenuItem name = new() { Header = "Nomear eixos pela rota/trecho das placas", IsEnabled = exists };
        name.Click += (_, _) => Send("C3DVIARIOPLACAS NO ", "Nomear eixos pelas placas: siga a linha de comando.");
        menu.Items.Add(name);
        WC.MenuItem arrange = new() { Header = "Arrumar por clique (rotacionar, mover, alinhar, 180, lado)", IsEnabled = exists };
        arrange.Click += (_, _) => { ViarioPlacaSync.PendingIds = null; Send("C3DVIARIOPLACAS AR ", "Arrumar: clique perto de uma placa."); };
        menu.Items.Add(arrange);
        WC.MenuItem recompute = new() { Header = "Recalcular coordenadas pelo suporte/chamada", IsEnabled = exists };
        recompute.Click += (_, _) => { ViarioPlacaSync.PendingIds = null; Send("C3DVIARIOPLACAS CO ", "Recalcular coordenadas: siga a linha de comando."); };
        menu.Items.Add(recompute);
        WC.MenuItem replace = new() { Header = "Substituir blocos T-xxx_SV corrigidos...", IsEnabled = exists };
        replace.Click += (_, _) => { ViarioPlacaSync.PendingIds = null; Send("C3DVIARIOPLACAS SU ", "Substituir blocos: escolha a pasta."); };
        menu.Items.Add(replace);
        WC.MenuItem scale = new() { Header = "Escalar todas (placa, chamada e suporte)...", IsEnabled = exists };
        scale.Click += (_, _) => { ViarioPlacaSync.PendingIds = null; Send("C3DVIARIOPLACAS ES T ", "Escalar todas as placas: informe a escala na linha de comando."); };
        menu.Items.Add(scale);
        WC.MenuItem list = new() { Header = "Listar na linha de comando", IsEnabled = exists };
        list.Click += (_, _) => Send("C3DVIARIOPLACAS L ", "Lista das placas na linha de comando.");
        menu.Items.Add(list);
        WC.MenuItem show = new() { Header = "Ver no painel" };
        show.Click += (_, _) => SelectCategory("PLACAS");
        menu.Items.Add(show);
        menu.Items.Add(new WC.Separator());
        WC.MenuItem remove = new() { Header = "Remover todas as placas", IsEnabled = exists };
        remove.Click += (_, _) => Send("C3DVIARIOPLACAS R ", "Remover placas: confirme na linha de comando.");
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    private void OpenHatchMenu(WC.Button anchor)
    {
        WC.ContextMenu menu = new() { PlacementTarget = anchor, Placement = WC.Primitives.PlacementMode.Bottom };
        bool exists = false, visible = true;
        try
        {
            if (AcApp.DocumentManager.MdiActiveDocument is Document d)
            {
                exists = ViarioHatchSync.Exists(d.Database);
                visible = ViarioHatchSync.IsVisible(d.Database);
            }
        }
        catch
        {
            // Sem desenho ativo.
        }

        WC.MenuItem gen = new() { Header = exists ? "Atualizar hachura do pavimento" : "Gerar hachura do pavimento", FontWeight = SW.FontWeights.Bold };
        gen.Click += (_, _) => Send("C3DVIARIOHACHURA G ", "Hachura do pavimento gerada.");
        menu.Items.Add(gen);
        menu.Items.Add(new WC.Separator());

        WC.MenuItem pattern = new() { Header = "Padrao: " + (_settings.HachuraSolida ? "SOLID" : _settings.HachuraPadrao) };
        foreach (string pat in ViarioHatchSync.Patterns)
        {
            WC.MenuItem mi = new() { Header = pat, IsCheckable = true, IsChecked = !_settings.HachuraSolida && pat == _settings.HachuraPadrao };
            string v = pat;
            mi.Click += (_, _) => SaveDimensionSetting(st => { st.HachuraPadrao = v; st.HachuraSolida = false; }, "Hachura: " + v, regenerateHatch: true);
            pattern.Items.Add(mi);
        }

        pattern.Items.Add(new WC.Separator());
        WC.MenuItem solid = new() { Header = "SOLID com transparencia " + _settings.HachuraTransparencia + "%", IsCheckable = true, IsChecked = _settings.HachuraSolida };
        solid.Click += (_, _) => SaveDimensionSetting(st => st.HachuraSolida = true, "Hachura: solido", regenerateHatch: true);
        pattern.Items.Add(solid);
        menu.Items.Add(pattern);

        WC.MenuItem scale = new() { Header = "Escala do padrao: " + N(_settings.HachuraEscala) };
        foreach (double sc in ViarioHatchSync.Scales)
        {
            WC.MenuItem mi = new() { Header = N(sc), IsCheckable = true, IsChecked = Math.Abs(sc - _settings.HachuraEscala) < 1e-9 };
            double v = sc;
            mi.Click += (_, _) => SaveDimensionSetting(st => st.HachuraEscala = v, "Hachura: escala " + N(v), regenerateHatch: true);
            scale.Items.Add(mi);
        }

        menu.Items.Add(scale);

        WC.MenuItem transp = new() { Header = "Transparencia do solido: " + _settings.HachuraTransparencia + "%" };
        foreach (int t in new[] { 30, 50, 70, 80, 90 })
        {
            WC.MenuItem mi = new() { Header = t + "%", IsCheckable = true, IsChecked = t == _settings.HachuraTransparencia };
            int v = t;
            mi.Click += (_, _) => SaveDimensionSetting(st => { st.HachuraTransparencia = v; st.HachuraSolida = true; }, "Hachura: solido " + v + "%", regenerateHatch: true);
            transp.Items.Add(mi);
        }

        menu.Items.Add(transp);

        WC.MenuItem blocks = new() { Header = "Blocos (redes grandes): " + (_settings.HachuraBloco > 0 ? N(_settings.HachuraBloco) + " m" : "sem recorte") };
        foreach (double b in new[] { 250.0, 500.0, 1000.0, 2000.0, 0.0 })
        {
            WC.MenuItem mi = new() { Header = b > 0 ? N(b) + " m" : "sem recorte (uma hachura por contorno)", IsCheckable = true, IsChecked = Math.Abs(b - _settings.HachuraBloco) < 1e-9 };
            double v = b;
            mi.Click += (_, _) => Send($"C3DVIARIOHACHURA B {v.ToString(CultureInfo.InvariantCulture)} ", "Hachura refeita em blocos de " + (v > 0 ? N(v) + " m." : "contorno inteiro."));
            blocks.Items.Add(mi);
        }

        blocks.ToolTip = "A hachura e recortada numa grade de blocos: cada bloco vira hachuras pequenas e so os blocos que mudaram sao refeitos.";
        menu.Items.Add(blocks);
        menu.Items.Add(new WC.Separator());
        WC.MenuItem hide = new() { Header = "Ocultar (desliga a layer VIARIO-PAVIMENTO)", IsEnabled = exists && visible };
        hide.Click += (_, _) => Send("C3DVIARIOHACHURA O ", "Hachura oculta.");
        menu.Items.Add(hide);
        WC.MenuItem show = new() { Header = "Mostrar", IsEnabled = exists && !visible };
        show.Click += (_, _) => Send("C3DVIARIOHACHURA M ", "Hachura visivel.");
        menu.Items.Add(show);
        WC.MenuItem remove = new() { Header = "Remover hachura", IsEnabled = exists };
        remove.Click += (_, _) => Send("C3DVIARIOHACHURA R ", "Hachura removida.");
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    private void SaveDimensionSetting(Action<ViarioStore.Settings> change, string what, bool regenerateHatch = false)
    {
        change(_settings);
        Edit(what, db =>
        {
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                ViarioStore.SaveSettings(tr, db, _settings);
                tr.Commit();
            }

            // So o estilo: as hachuras existentes trocam o padrao sem recalcular a rede.
            if (regenerateHatch && AcApp.DocumentManager.MdiActiveDocument is Document doc)
                ViarioHatchSync.Restyle(doc);
        }, regenerate: false);
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

    /// <summary>Botao "Textos" com a lista do que mostrar na vista.</summary>
    private WC.Button TextMenu()
    {
        WC.Button b = new()
        {
            Content = "Textos  ▾",
            Padding = new SW.Thickness(8, 1, 8, 1),
            Margin = new SW.Thickness(8, 1, 4, 1),
            VerticalAlignment = SW.VerticalAlignment.Center,
            Background = T.Header,
            Foreground = T.Text,
            BorderBrush = T.Line
        };
        b.Click += (_, _) =>
        {
            WC.ContextMenu menu = new() { PlacementTarget = b };
            foreach ((string key, string title) in TextKinds)
            {
                WC.MenuItem mi = new() { Header = title, IsCheckable = true, IsChecked = Show(key), StaysOpenOnClick = true };
                mi.Click += (_, _) =>
                {
                    if (mi.IsChecked)
                        _texts.Add(key);
                    else
                        _texts.Remove(key);
                    SaveTextPreference();
                    _render?.Invoke();
                };
                menu.Items.Add(mi);
            }

            menu.Items.Add(new WC.Separator());
            WC.MenuItem all = new() { Header = "Todos" };
            all.Click += (_, _) =>
            {
                foreach ((string key, _) in TextKinds)
                    _texts.Add(key);
                SaveTextPreference();
                _render?.Invoke();
            };
            menu.Items.Add(all);
            WC.MenuItem none = new() { Header = "Nenhum" };
            none.Click += (_, _) =>
            {
                _texts.Clear();
                SaveTextPreference();
                _render?.Invoke();
            };
            menu.Items.Add(none);
            menu.IsOpen = true;
        };
        return b;
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

    private static Dictionary<string, string> ReadPreferences()
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = PreferencesPath();
            if (!System.IO.File.Exists(path))
                return map;
            foreach (string line in System.IO.File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                    map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch
        {
            // Preferencia e conveniencia.
        }

        return map;
    }

    private static void WritePreference(string key, string value)
    {
        try
        {
            Dictionary<string, string> map = ReadPreferences();
            map[key] = value;
            string path = PreferencesPath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllLines(path, map.Select(kv => kv.Key + "=" + kv.Value));
        }
        catch
        {
            // Preferencia e conveniencia.
        }
    }

    private static bool LoadDarkPreference() =>
        ReadPreferences().TryGetValue("tema", out string? tema) && tema.Equals("escuro", StringComparison.OrdinalIgnoreCase);

    private static void SaveDarkPreference(bool dark) => WritePreference("tema", dark ? "escuro" : "claro");

    private static HashSet<string> LoadTextPreference()
    {
        if (ReadPreferences().TryGetValue("textos", out string? list))
            return new HashSet<string>(list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(TextKinds.Select(t => t.Key), StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveTextPreference() => WritePreference("textos", string.Join(",", _texts));

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
        _refreshCount++;
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
            ViarioSync.NetworkView view = ViarioSync.View(db);
            _axes = view.Axes;
            _result = view.Result;
            _stale = view.Stale;
            _missing = view.Missing;
            _auto = ViarioSync.IsAuto(db);
            _sections = ViarioStore.LoadSections(db);
            _settings = ViarioStore.LoadSettings(db);
            _engine = ViarioSync.EngineSettingsFor(db);
            _signage = ViarioStore.LoadSignage(db);
            _ownNames = ViarioSync.AxisNames(db, _axes.Select(a => a.Key));
            _civilActive = ViarioCivil.IsActive(db);
            _names = _civilActive ? ViarioCivil.AxisNames(db) : new Dictionary<string, string>();
            _signExists = ViarioSignageSync.Exists(db);
            _placas = ViarioPlacaSync.Read(db);
            _atrativos = ViarioAtrativos.Read(db);

            // Recomendacoes so sao refeitas quando a rede, os eixos ou os registros mudam.
            ulong stamp = HintsStamp(_axes);
            string records = ViarioStore.RecordsSignature(db);
            if (!ReferenceEquals(_hintsResult, _result) || _hintsAxes != stamp || _hintsRecords != records)
            {
                _hints = ViarioNorms.Check(_axes, _result, _engine, _sections);
                _hintsResult = _result;
                _hintsAxes = stamp;
                _hintsRecords = records;
            }

            BuildIndexes();
        }
        catch (Exception ex)
        {
            Say("Erro ao ler a rede: " + ex.Message);
            return;
        }

        Title = $"Rede viaria - Explorador [{System.IO.Path.GetFileName(doc.Name)}]";
        UpdateAutoButton();
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
        "PLACAS" => _placas.Count,
        "ATRATIVOS" => _atrativos.Count,
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

    private sealed class Row : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isChecked;

        public string Id { get; init; } = "";
        public string Icon { get; init; } = "PAINEL";
        public string[] Cells { get; init; } = Array.Empty<string>();
        public object? Data { get; init; }
        public string Flag { get; set; } = "";
        public WM.Brush? FlagBrush { get; set; }

        /// <summary>Caixinha da linha (listas com marcacao).</summary>
        public bool Checked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                    return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Checked)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
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
                            a.Radius is double r ? N(r) : "secao", (_nodeCount.TryGetValue(a.Key, out int nodes) ? nodes : 0).ToString(CultureInfo.InvariantCulture),
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

            case "PLACAS":
                foreach (PlacaItem p in _placas)
                {
                    rows.Add(new Row
                    {
                        Id = "PL:" + p.Id, Icon = "PLACAS", Data = p,
                        Cells = new[]
                        {
                            p.Id, p.Codigo, p.Vinculada ? AxisLabel(p.AxisKey) : "(sem eixo)", p.EstacaTexto, p.LadoTexto,
                            p.Vinculada ? N(p.Offset, 1) : "", N(p.AreaSinC, 2), p.AreaProjetoM2 > 0 ? N(p.AreaProjetoM2, 2) : "",
                            p.SuporteTipo, p.Trecho, p.MensagemResumo
                        }
                    });
                }

                return (new[] { "Id", "SinC", "Eixo", "Estaca", "Lado", "Afast. [m]", "Area SinC [m2]", "Area proj. [m2]", "Suporte", "Trecho", "Mensagens" },
                    new double[] { 90, 60, 170, 80, 45, 70, 90, 90, 120, 55, 320 }, rows);

            case "ATRATIVOS":
                foreach (AtrativoItem a in _atrativos)
                {
                    rows.Add(new Row
                    {
                        Id = "ATR:" + a.Nome + "@" + N(a.E, 1) + "," + N(a.N, 1), Icon = "ATRATIVOS", Data = a,
                        Cells = new[] { a.Nome, a.Categoria, N(a.E, 2), N(a.N, 2), N(a.Raio, 1), NearestAxis(a.Ponto) }
                    });
                }

                return (new[] { "Atrativo", "Categoria", "E", "N", "Raio [m]", "Eixo mais proximo" },
                    new double[] { 240, 200, 95, 100, 60, 180 }, rows);

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

        // Filtro por coluna (menu do cabecalho) e ordenacao (clique no cabecalho).
        for (int ci = 0; ci < columns.Length; ci++)
        {
            if (_colFilter.TryGetValue(_category + "|" + ci, out string? f) && f.Length > 0)
            {
                int c = ci;
                rows = rows.Where(r => c < r.Cells.Length && r.Cells[c].Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }

        if (_sort is (string sc, int col, bool desc) && sc == _category && col < columns.Length)
        {
            Comparison<Row> cmp = (a, b) => CompareCells(a.Cells.ElementAtOrDefault(col) ?? "", b.Cells.ElementAtOrDefault(col) ?? "");
            rows.Sort(cmp);
            if (desc)
                rows.Reverse();
        }

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

        bool checkable = _category == "EIXOS";
        if (checkable)
        {
            // Marcacao sobrevive aos recalculos; eixos que sumiram saem dela.
            _checked.IntersectWith(_axes.Select(a => "EIXO:" + a.Key));
            foreach (Row r in rows)
                r.Checked = _checked.Contains(r.Id);
        }

        _rows = rows;
        string[] heads = new string[columns.Length];
        for (int ci = 0; ci < columns.Length; ci++)
        {
            string h = columns[ci];
            if (_sort is (string sc2, int col2, bool desc2) && sc2 == _category && col2 == ci)
                h += desc2 ? " ▼" : " ▲";
            if (_colFilter.TryGetValue(_category + "|" + ci, out string? f2) && f2.Length > 0)
                h += " ⌕";
            heads[ci] = h;
        }

        _list.View = checkable
            ? ListViewFor(heads, widths, rows, OnChecksChanged, all => { foreach (Row r in _rows) r.Checked = all; OnChecksChanged(); }, rows.Count > 0 && rows.All(r => r.Checked), HeaderClick, HeaderMenu)
            : ListViewFor(heads, widths, rows, null, null, false, HeaderClick, HeaderMenu);
        _list.ItemsSource = rows;

        Row? keep = rows.FirstOrDefault(r => r.Id == _selectedRow) ?? rows.FirstOrDefault();
        _list.SelectedItem = keep;
        if (keep is not null)
            _list.ScrollIntoView(keep);
        OnRowSelected();

        int warn = _hints.Count(h => h.Level == HintLevel.Atencao);
        Say((_stale ? "REDE PENDENTE (atualizacao manual: clique em Atualizar rede) | " :
             _missing ? "Rede ainda nao calculada nesta sessao (atualizacao manual): Atualizar rede mostra nos e esquinas | " : "") +
            $"{_axes.Count} eixo(s){(_checked.Count > 0 ? $" ({_checked.Count} marcado(s))" : "")}, {_result.Nodes.Count} no(s), {_result.Corners.Count} esquina(s) | " +
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

    /// <summary>Compara celulas: numeros (com virgula ou ponto, estaca "12+5.30") como numeros; o resto como texto.</summary>
    private static int CompareCells(string a, string b)
    {
        bool na = CellNumber(a, out double va), nb = CellNumber(b, out double vb);
        if (na && nb)
            return va.CompareTo(vb);
        if (na != nb)
            return na ? -1 : 1; // numeros antes de textos; vazios no fim
        if (a.Length == 0 || b.Length == 0)
            return a.Length == 0 ? (b.Length == 0 ? 0 : 1) : -1;
        return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool CellNumber(string s, out double v)
    {
        v = 0;
        s = s.Trim();
        if (s.Length == 0)
            return false;
        int plus = s.IndexOf('+');
        if (plus > 0 && double.TryParse(s[..plus], NumberStyles.Float, CultureInfo.InvariantCulture, out double est) &&
            double.TryParse(s[(plus + 1)..].Split(' ')[0].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double rest))
        {
            v = (est * 20.0) + rest; // estaca
            return true;
        }

        string t = s.Split(' ')[0].Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    private void HeaderClick(int col)
    {
        if (_sort is (string sc, int c, bool desc) && sc == _category && c == col)
            _sort = desc ? null : (sc, c, true);
        else
            _sort = (_category, col, false);
        FillList();
    }

    private void HeaderMenu(int col, WC.GridViewColumnHeader header)
    {
        string key = _category + "|" + col;
        WC.ContextMenu menu = new() { PlacementTarget = header, Placement = WC.Primitives.PlacementMode.Bottom };
        WC.MenuItem filter = new() { Header = "Filtrar esta coluna...", FontWeight = SW.FontWeights.Bold };
        filter.Click += (_, _) =>
        {
            string? value = AskText("Filtrar coluna", $"Mostrar so as linhas em que \"{header.Content?.ToString()?.TrimEnd(' ', '▲', '▼', '⌕')}\" contem:",
                _colFilter.TryGetValue(key, out string? cur) ? cur : "");
            if (value is null)
                return;
            if (value.Length == 0)
                _colFilter.Remove(key);
            else
                _colFilter[key] = value;
            FillList();
        };
        menu.Items.Add(filter);
        WC.MenuItem clear = new() { Header = "Limpar filtro desta coluna", IsEnabled = _colFilter.ContainsKey(key) };
        clear.Click += (_, _) => { _colFilter.Remove(key); FillList(); };
        menu.Items.Add(clear);
        WC.MenuItem clearAll = new() { Header = "Limpar todos os filtros e a ordenacao", IsEnabled = _colFilter.Keys.Any(k => k.StartsWith(_category + "|", StringComparison.Ordinal)) || _sort is not null };
        clearAll.Click += (_, _) =>
        {
            foreach (string k in _colFilter.Keys.Where(k => k.StartsWith(_category + "|", StringComparison.Ordinal)).ToList())
                _colFilter.Remove(k);
            _sort = null;
            FillList();
        };
        menu.Items.Add(clearAll);
        menu.Items.Add(new WC.Separator());
        WC.MenuItem asc = new() { Header = "Ordenar crescente" };
        asc.Click += (_, _) => { _sort = (_category, col, false); FillList(); };
        menu.Items.Add(asc);
        WC.MenuItem des = new() { Header = "Ordenar decrescente" };
        des.Click += (_, _) => { _sort = (_category, col, true); FillList(); };
        menu.Items.Add(des);
        menu.IsOpen = true;
    }

    /// <summary>Janela pequena com uma caixa de texto (OK/Limpar/Cancelar; Esc nao fecha). Null = cancelou; "" = limpar.</summary>
    private string? AskText(string title, string label, string initial)
    {
        SW.Window win = new()
        {
            Title = title, Owner = this, Width = 420, SizeToContent = SW.SizeToContent.Height,
            WindowStartupLocation = SW.WindowStartupLocation.CenterOwner, Background = T.Back, Foreground = T.Text, ResizeMode = SW.ResizeMode.NoResize
        };
        WC.StackPanel root = new() { Margin = new SW.Thickness(10) };
        root.Children.Add(new WC.TextBlock { Text = label, Foreground = T.Text, Margin = new SW.Thickness(0, 0, 0, 6), TextWrapping = SW.TextWrapping.Wrap });
        WC.TextBox box = new() { Text = initial, Background = T.Input, Foreground = T.Text, BorderBrush = T.Line, CaretBrush = T.Text, Padding = new SW.Thickness(4, 2, 4, 2) };
        root.Children.Add(box);
        WC.StackPanel buttons = new() { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = SW.HorizontalAlignment.Right, Margin = new SW.Thickness(0, 10, 0, 0) };
        string? result = null;
        WC.Button ok = new() { Content = "OK", Width = 80, Margin = new SW.Thickness(4, 0, 0, 0), IsDefault = true };
        ok.Click += (_, _) => { result = box.Text.Trim(); win.Close(); };
        WC.Button clear = new() { Content = "Limpar", Width = 80, Margin = new SW.Thickness(4, 0, 0, 0) };
        clear.Click += (_, _) => { result = ""; win.Close(); };
        WC.Button cancel = new() { Content = "Cancelar", Width = 80, Margin = new SW.Thickness(4, 0, 0, 0) };
        cancel.Click += (_, _) => win.Close();
        buttons.Children.Add(ok);
        buttons.Children.Add(clear);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        win.Content = root;
        box.Focus();
        box.SelectAll();
        win.ShowDialog();
        return result;
    }

    /// <summary>GridView com coluna de aviso, icone + primeira coluna e as demais por indice; numeros a direita.</summary>
    private static WC.GridView ListViewFor(string[] columns, double[] widths, List<Row>? rows = null,
        Action? checkChanged = null, Action<bool>? checkAll = null, bool allChecked = false,
        Action<int>? onSort = null, Action<int, WC.GridViewColumnHeader>? onMenu = null)
    {
        WC.GridViewColumnHeader Head(int i)
        {
            WC.GridViewColumnHeader h = new() { Content = columns[i] };
            if (onSort is not null)
            {
                int c = i;
                h.Click += (_, _) => onSort(c);
                h.ToolTip = "Clique: ordenar | botao direito: filtrar";
                h.MouseRightButtonUp += (_, e) => { onMenu?.Invoke(c, h); e.Handled = true; };
            }

            return h;
        }

        WC.GridView view = new();

        if (checkChanged is not null)
        {
            // Caixinha por linha + caixinha no cabecalho (marca/desmarca as linhas da lista, ja filtrada).
            SW.FrameworkElementFactory box = new(typeof(WC.CheckBox));
            box.SetBinding(WC.Primitives.ToggleButton.IsCheckedProperty, new WD.Binding("Checked") { Mode = WD.BindingMode.TwoWay, UpdateSourceTrigger = WD.UpdateSourceTrigger.PropertyChanged });
            box.SetValue(SW.FrameworkElement.HorizontalAlignmentProperty, SW.HorizontalAlignment.Center);
            box.SetValue(SW.FrameworkElement.ToolTipProperty, "Marcar: os comandos em lote valem para os eixos marcados (Espaco marca os selecionados).");
            box.AddHandler(WC.Primitives.ButtonBase.ClickEvent, new SW.RoutedEventHandler((_, _) => checkChanged()));
            WC.CheckBox header = new() { IsChecked = allChecked, ToolTip = "Marcar/desmarcar todos os da lista (respeita o Localizar)" };
            header.Click += (_, _) => checkAll?.Invoke(header.IsChecked == true);
            view.Columns.Add(new WC.GridViewColumn { Header = header, Width = 28, CellTemplate = new SW.DataTemplate { VisualTree = box } });
        }

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
                view.Columns.Add(new WC.GridViewColumn { Header = Head(0), Width = widths[0], CellTemplate = new SW.DataTemplate { VisualTree = panel } });
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

            WC.GridViewColumnHeader header = Head(i);
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
        _ownNames.TryGetValue(axis.Key, out string? own) ? own :
        _names.TryGetValue(axis.Key, out string? name) ? name : (axis.Path.Closed ? $"Rotatoria {index + 1}" : $"Eixo {index + 1}");

    private string AxisLabel(string key) =>
        _axisIndex.TryGetValue(key, out int i) ? AxisName(_axes[i], i) : key;

    private List<CornerInfo> CornersAt(NodeInfo nd)
    {
        // Mesma ordem da lista de esquinas da rede.
        List<CornerInfo> found = new();
        foreach ((long, long) cell in Cells(nd.Point, 0.5, 1.0))
        {
            if (_cornerCells.TryGetValue(cell, out List<CornerInfo>? list))
                found.AddRange(list.Where(c => V2.Dist(c.Node, nd.Point) < 0.5));
        }

        return found.Count < 2 ? found : found.Distinct().OrderBy(c => _cornerOrder[c]).ToList();
    }

    private static IEnumerable<(long, long)> Cells(V2 p, double radius, double size)
    {
        long x0 = (long)Math.Floor((p.X - radius) / size), x1 = (long)Math.Floor((p.X + radius) / size);
        long y0 = (long)Math.Floor((p.Y - radius) / size), y1 = (long)Math.Floor((p.Y + radius) / size);
        for (long x = x0; x <= x1; x++)
        {
            for (long y = y0; y <= y1; y++)
                yield return (x, y);
        }
    }

    private static (long, long) Cell(V2 p, double size) => ((long)Math.Floor(p.X / size), (long)Math.Floor(p.Y / size));

    /// <summary>Marca dos dados que mudam as recomendacoes (geometria, secao, raio e elementos de cada eixo).</summary>
    private static ulong HintsStamp(List<AxisInput> axes)
    {
        ulong hash = AxesStamp(axes);
        void Mix(long v)
        {
            hash ^= (ulong)v;
            hash *= 1099511628211UL;
        }

        foreach (AxisInput a in axes)
        {
            // O cache de leitura devolve o mesmo GPath enquanto o eixo nao muda.
            Mix(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a.Path));
            Mix(a.Section.Id.GetHashCode(StringComparison.Ordinal));
            Mix(a.Radius is double r ? BitConverter.DoubleToInt64Bits(r) : -1);
            Mix(a.Events.Count);
            Mix(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a.Events));
            Mix(a.Chained ? 1 : 0);
        }

        return hash;
    }

    /// <summary>Indices por chave e por posicao (eixos, nos, esquinas e recomendacoes).</summary>
    private void BuildIndexes()
    {
        _axisIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < _axes.Count; i++)
            _axisIndex.TryAdd(_axes[i].Key, i);

        _nodeCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (NodeInfo nd in _result.Nodes)
        {
            foreach (string key in nd.AxisKeys.Distinct())
                _nodeCount[key] = _nodeCount.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        _cornerCells = new Dictionary<(long, long), List<CornerInfo>>();
        _cornerOrder = new Dictionary<CornerInfo, int>(ReferenceEqualityComparer.Instance);
        foreach (CornerInfo c in _result.Corners)
        {
            _cornerOrder.TryAdd(c, _cornerOrder.Count);
            (long, long) cell = Cell(c.Node, 1.0);
            if (!_cornerCells.TryGetValue(cell, out List<CornerInfo>? list))
                _cornerCells[cell] = list = new List<CornerInfo>();
            list.Add(c);
        }

        _hintsByTarget = new Dictionary<string, List<Hint>>(StringComparer.Ordinal);
        _hintCells = new Dictionary<(long, long), List<Hint>>();
        _hintOrder = new Dictionary<Hint, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _hints.Count; i++)
        {
            Hint h = _hints[i];
            _hintOrder[h] = i;
            if (!_hintsByTarget.TryGetValue(h.Target, out List<Hint>? list))
                _hintsByTarget[h.Target] = list = new List<Hint>();
            list.Add(h);
            if (h.Where is V2 p)
            {
                (long, long) cell = Cell(p, HintCell);
                if (!_hintCells.TryGetValue(cell, out List<Hint>? near))
                    _hintCells[cell] = near = new List<Hint>();
                near.Add(h);
            }
        }
    }

    private IEnumerable<Hint> HintsOf(string target) =>
        _hintsByTarget.TryGetValue(target, out List<Hint>? list) ? list : Enumerable.Empty<Hint>();

    /// <summary>Recomendacoes a ate radius do ponto (radius ate HintCell), na ordem original.</summary>
    private IEnumerable<Hint> HintsNear(V2 p, double radius)
    {
        List<Hint> found = new();
        foreach ((long, long) cell in Cells(p, radius, HintCell))
        {
            if (_hintCells.TryGetValue(cell, out List<Hint>? list))
                found.AddRange(list.Where(h => h.Where is V2 q && V2.Dist(q, p) < radius));
        }

        return found.OrderBy(h => _hintOrder[h]);
    }

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
                return HintsOf(s.Id).Where(h => h.Topic == "Secao");
            case AxisInput a when _category == "EIXOS":
                return HintsOf(a.Key).Concat(a.Section.Id == a.Key ? Enumerable.Empty<Hint>() : HintsOf(a.Section.Id).Where(h => h.Topic == "Secao"))
                    .OrderBy(h => _hintOrder[h]);
            case ValueTuple<AxisInput, AxisEvent> pair:
                string prefix = KindName(pair.Item2.Kind).Split(' ')[0];
                return HintsOf(pair.Item1.Key).Where(h => h.Topic == "Elemento" && h.Message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            case string slipKey when _category == "ELEMENTOS":
                return HintsOf(slipKey).Where(h => h.Topic == "Elemento");
            case NodeInfo nd:
                return HintsNear(nd.Point, 30.0).Where(h => h.Topic is "No" or "Esquina" or "Rotatoria");
            case CornerInfo c:
                return HintsOf(c.Key);
            case OutputLine isl:
                Box2 ib = Bounds(isl.Segs);
                return _hints.Where(h => h.Topic == "Ilha" && h.Where is V2 p && p.X >= ib.MinX && p.X <= ib.MaxX && p.Y >= ib.MinY && p.Y <= ib.MaxY &&
                                         ViarioEngine.RingContains(isl.Segs, p));
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
            case AxisInput a when _category == "EIXOS" && (_checked.Count > 0 || TargetAxes().Count > 1): ShowAxes(TargetAxes(), a); break;
            case AxisInput a: ShowAxis(a); break;
            case ValueTuple<AxisInput, AxisEvent> pair: ShowEvent(pair.Item1, pair.Item2); break;
            case string slip when _category == "ELEMENTOS": ShowSlip(slip); break;
            case NodeInfo nd: ShowNode(nd); break;
            case CornerInfo c: ShowCorner(c); break;
            case OutputLine isl: ShowIsland(isl); break;
            case string kind when _category == "SINAL": ShowSignage(kind); break;
            case PlacaItem placa: ShowPlaca(placa); break;
            case AtrativoItem atrativo: ShowAtrativo(atrativo); break;
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
                    if (_category == "PLACAS")
                    {
                        Prop("Placas", $"{_placas.Count} no desenho, {_placas.Count(p => !p.Vinculada)} sem eixo, {N(_placas.Sum(p => p.AreaSinC), 2)} m2 (SinC)");
                        foreach (IGrouping<string, PlacaItem> g in _placas.Where(p => p.SuporteTipo.Length > 0).GroupBy(p => p.SuporteTipo).OrderByDescending(g => g.Count()))
                            Prop(g.Key, g.Count().ToString(CultureInfo.InvariantCulture));
                        Act("Importar", () => Send("C3DVIARIOPLACAS I ", "Importar placas: siga a linha de comando."), true);
                        Act("Atualizar vinculos", () => Send("C3DVIARIOPLACAS AT ", "Atualizar vinculos: siga a linha de comando."));
                        Act("Nomear eixos", () => Send("C3DVIARIOPLACAS NO ", "Nomear eixos pelas placas: siga a linha de comando."));
                        Act("Escalar todas...", () => { ViarioPlacaSync.PendingIds = null; Send("C3DVIARIOPLACAS ES T ", "Escalar todas as placas: informe a escala na linha de comando."); });
                        Act("Arrumar (clique)...", () => { ViarioPlacaSync.PendingIds = null; Send("C3DVIARIOPLACAS AR ", "Arrumar: clique perto de uma placa."); });
                        Act("Recalcular coordenadas de todas", () => { ViarioPlacaSync.PendingIds = null; Send("C3DVIARIOPLACAS CO ", "Recalcular coordenadas pelo suporte/linha de chamada: siga a linha de comando."); });
                        Act("Substituir blocos corrigidos...", () => { ViarioPlacaSync.PendingIds = null; Send("C3DVIARIOPLACAS SU ", "Substituir blocos T-xxx_SV: escolha a pasta."); });
                    }

                    if (_category == "ATRATIVOS")
                    {
                        Prop("Atrativos", $"{_atrativos.Count} no desenho");
                        foreach (IGrouping<string, AtrativoItem> g in _atrativos.GroupBy(a => a.Categoria).OrderByDescending(g => g.Count()))
                            Prop(g.Key.Length > 0 ? g.Key : "(sem categoria)", g.Count().ToString(CultureInfo.InvariantCulture));
                        Act("Importar KMZ", () => Send("C3DVIARIOATRATIVOS I ", "Importar atrativos: siga a linha de comando."), true);
                        Act("Novo ponto (clique)...", () => Send("C3DVIARIOATRATIVOS N ", "Novo atrativo: clique no desenho e informe o nome."));
                        Act("Raio de todos...", () => Send("C3DVIARIOATRATIVOS R ", "Raio dos atrativos: informe na linha de comando."));
                    }

                    if (_category is "PLACAS" or "ATRATIVOS")
                        _render = () => RenderOverview(_category);
                    else
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
        Category("Identificacao");
        WC.TextBox nameBox = EditProp("NomeEixo", "Nome do eixo", _ownNames.TryGetValue(a.Key, out string? own) ? own : "");
        nameBox.ToolTip = "Nome proprio do eixo (fica no desenho). Enter grava; vazio volta ao nome automatico. Vai para o alinhamento do Civil.";
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == WI.Key.Enter)
                SetNames(new[] { a }, nameBox.Text.Trim());
        };
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
        Act("Salvar nome", () => SetNames(new[] { a }, Text("NomeEixo")));
        Act("Nome pelo texto proximo...", () => OpenNameDialog(new List<AxisInput> { a }));
        Act("Aplicar secao", () => AssignSection(a));
        if (!a.Path.Closed)
        {
            Act("Inverter sentido", () => ReverseAxis(a));
            Act("Reconcordar trecho...", () => Send("C3DVIARIOEIXO CO V ", "Clique perto do vertice entre os dois trechos."));
        }
        Act("Cotar faixas...", () => Send("C3DVIARIOCOTAR ", "Passe sobre o eixo e clique onde quer a cota."));
        Act("Remover da rede", () => RemoveAxis(a));
        _render = () => RenderMap(a.Path.Bounds().Inflate(a.Section.TotalWidth + 10), sh => sh.Highlight(a.Path.Segs));
    }

    private void ReverseAxis(AxisInput a)
    {
        Edit("Sentido invertido: " + AxisLabel(a.Key), db =>
        {
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc is null)
                return;
            AcadDb.ObjectId id = db.GetObjectId(false, new AcadDb.Handle(Convert.ToInt64(a.Key, 16)), 0);
            (int count, List<string> notes) = ViarioCommands.ReverseAxes(doc, new[] { id });
            foreach (string n in notes)
                doc.Editor.WriteMessage("\n  [C3DVIARIO] " + n);
            if (count > 0 && notes.Count > 0)
                Say(notes[0]);
        });
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

    // ---------------- Eixos em lote (marcados ou selecionados) ----------------

    /// <summary>Eixos em que os comandos agem: os marcados; sem marca, os selecionados na lista.</summary>
    private List<AxisInput> TargetAxes()
    {
        if (_category != "EIXOS")
            return _current?.Data is AxisInput c ? new List<AxisInput> { c } : new List<AxisInput>();
        List<AxisInput> marked = _axes.Where(a => _checked.Contains("EIXO:" + a.Key)).ToList();
        if (marked.Count > 0)
            return marked;
        List<AxisInput> selected = _list.SelectedItems.OfType<Row>().Select(r => r.Data).OfType<AxisInput>().ToList();
        if (selected.Count > 0)
            return selected;
        return _current?.Data is AxisInput a ? new List<AxisInput> { a } : new List<AxisInput>();
    }

    private void OnChecksChanged()
    {
        foreach (Row r in _rows)
        {
            if (r.Checked)
                _checked.Add(r.Id);
            else
                _checked.Remove(r.Id);
        }

        BuildTabs();
        Say(_checked.Count == 0 ? "Nenhum eixo marcado: os comandos valem para a linha selecionada." : $"{_checked.Count} eixo(s) marcado(s): os comandos em lote valem para eles.");
    }

    private void SetChecks(IEnumerable<Row> rows, bool value)
    {
        foreach (Row r in rows)
            r.Checked = value;
        OnChecksChanged();
    }

    /// <summary>Botao direito na lista de eixos: marcar/desmarcar.</summary>
    private void OpenAxisListMenu()
    {
        WC.ContextMenu menu = new() { PlacementTarget = _list };
        List<Row> selected = _list.SelectedItems.OfType<Row>().ToList();
        void Item(string text, Action action, bool enabled = true)
        {
            WC.MenuItem mi = new() { Header = text, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        Item($"Marcar selecionados ({selected.Count})", () => SetChecks(selected, true), selected.Count > 0);
        Item($"Desmarcar selecionados ({selected.Count})", () => SetChecks(selected, false), selected.Count > 0);
        menu.Items.Add(new WC.Separator());
        Item($"Marcar todos da lista ({_rows.Count})", () => SetChecks(_rows, true), _rows.Count > 0);
        Item("Inverter marcacao da lista", () => { foreach (Row r in _rows) r.Checked = !r.Checked; OnChecksChanged(); }, _rows.Count > 0);
        Item($"Desmarcar todos ({_checked.Count})", () => { _checked.Clear(); foreach (Row r in _rows) r.Checked = false; OnChecksChanged(); }, _checked.Count > 0);
        menu.Items.Add(new WC.Separator());
        Item("Nomear pelos textos proximos...", () => OpenNameDialog(TargetAxes()));
        Item("Selecionar no desenho", () => SelectAxes(TargetAxes()));
        Item("Remover da rede...", () => RemoveAxes(TargetAxes()));
        menu.IsOpen = true;
    }

    private void ShowAxes(List<AxisInput> targets, AxisInput current)
    {
        bool marked = _checked.Count > 0;
        Category($"{targets.Count} eixo(s) {(marked ? "marcado(s)" : "selecionado(s)")}");
        Prop("Comprimento total", N(targets.Sum(t => t.Path.Length)) + " m");
        Prop("Secoes", string.Join(", ", targets.GroupBy(t => t.Section.Name).Select(g => $"{g.Key} ({g.Count()})")));
        Prop("Com nome", $"{targets.Count(t => _ownNames.ContainsKey(t.Key))} de {targets.Count}");
        Prop("Linha selecionada", AxisName(current, _axes.IndexOf(current)) + (targets.Contains(current) ? "" : " (fora do lote)"));

        Category("Alterar em lote (vazio = manter)");
        List<(string, string)> sections = new() { ("(manter)", "") };
        sections.AddRange(_sections.Select(x => (x.Name, x.Id)));
        ChoiceProp("LoteSecao", "Secao", sections, "");
        WC.TextBox raio = EditProp("LoteRaio", "Raio de esquina do eixo (m)", "");
        raio.ToolTip = "Numero = raio de todas as esquinas desses eixos; 'secao' = volta ao raio da secao; vazio = manter.";
        ChoiceProp("LoteConc", "Concordancia", new[] { ("(manter)", ""), ("mantida (refeita ao editar)", "1"), ("livre", "0") }, "");
        WC.TextBox nome = EditProp("LoteNome", "Nome (o mesmo para todos)", "");
        nome.ToolTip = "Ex.: trechos da mesma rua. Vazio = manter; para tirar os nomes use Limpar nomes.";

        Act("Aplicar", () => ApplyBatch(targets), true);
        Act("Zoom", () => ZoomTo(targets.Select(t => t.Path.Bounds().Inflate(t.Section.TotalWidth)).Aggregate(Box2.Empty, (acc, b) => acc.Union(b))));
        Act("Selecionar no desenho", () => SelectAxes(targets));
        Act("Nomear pelos textos...", () => OpenNameDialog(targets));
        Act("Limpar nomes", () => SetNames(targets, ""));
        Act("Inverter sentido", () => ReverseAxes(targets));
        Act("Remover da rede...", () => RemoveAxes(targets));
        if (marked)
            Act("Desmarcar todos", () => { _checked.Clear(); foreach (Row r in _rows) r.Checked = false; OnChecksChanged(); });

        Box2 box = targets.Select(t => t.Path.Bounds()).Aggregate(Box2.Empty, (acc, b) => acc.Union(b));
        _render = () => RenderMap(box.IsEmpty ? null : box.Inflate(20), sh =>
        {
            foreach (AxisInput t in targets)
                sh.Highlight(t.Path.Segs);
        });
    }

    private void ApplyBatch(List<AxisInput> targets)
    {
        string section = Text("LoteSecao");
        string raio = Text("LoteRaio");
        string conc = Text("LoteConc");
        string nome = Text("LoteNome");
        double? radius = null;
        bool clearRadius = raio.Equals("secao", StringComparison.OrdinalIgnoreCase) || raio.Equals("seção", StringComparison.OrdinalIgnoreCase);
        if (raio.Length > 0 && !clearRadius)
        {
            radius = Number("LoteRaio", 0.5);
            if (radius is null)
            {
                Say("Raio invalido (numero >= 0,5, 'secao' ou vazio).");
                return;
            }
        }

        if (section.Length == 0 && raio.Length == 0 && conc.Length == 0 && nome.Length == 0)
        {
            Say("Nada para alterar: escolha a secao, o raio, a concordancia ou o nome.");
            return;
        }

        int count = 0;
        Edit($"{targets.Count} eixo(s) alterado(s).", db =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            foreach (RoadSection other in _sections)
                ViarioStore.SaveSection(tr, db, other);
            Dictionary<string, (string, string)> renamed = new();
            foreach (AxisInput a in targets)
            {
                if (AxisEntity(tr, db, a.Key, AcadDb.OpenMode.ForWrite) is not AcadDb.Entity e)
                    continue;
                if (section.Length > 0 || radius is not null || clearRadius)
                    ViarioStore.UpdateAxis(e, sectionId: section.Length > 0 ? section : null, radius: radius, clearRadius: clearRadius);
                if (conc.Length > 0 && !a.Path.Closed)
                    ViarioStore.SetChained(e, conc == "1");
                if (nome.Length > 0)
                {
                    renamed[a.Key] = (_ownNames.TryGetValue(a.Key, out string? old) ? old : "", nome);
                    ViarioStore.SetAxisName(tr, db, e, nome);
                }

                count++;
            }

            if (renamed.Count > 0)
                ViarioCivil.RenameFromAxes(tr, db, renamed);
            tr.Commit();
        });
        Say($"{count} eixo(s) alterado(s).");
    }

    /// <summary>Grava o mesmo nome (ou nenhum) nos eixos; leva o nome aos alinhamentos do Civil.</summary>
    private void SetNames(IReadOnlyCollection<AxisInput> targets, string name)
    {
        name = name.Trim();
        Edit(name.Length == 0 ? $"Nome tirado de {targets.Count} eixo(s)." : $"Nome '{name}' em {targets.Count} eixo(s).", db =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            Dictionary<string, (string, string)> renamed = new();
            foreach (AxisInput a in targets)
            {
                if (AxisEntity(tr, db, a.Key, AcadDb.OpenMode.ForWrite) is not AcadDb.Entity e)
                    continue;
                if (name.Length > 0)
                    renamed[a.Key] = (_ownNames.TryGetValue(a.Key, out string? old) ? old : "", name);
                ViarioStore.SetAxisName(tr, db, e, name);
            }

            if (renamed.Count > 0)
                ViarioCivil.RenameFromAxes(tr, db, renamed);
            tr.Commit();
        }, regenerate: false);
    }

    private void SelectAxes(IReadOnlyCollection<AxisInput> targets)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || targets.Count == 0)
            return;
        using (doc.LockDocument())
        {
            using AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
            AcadDb.ObjectId[] ids = targets.Select(a => AxisEntity(tr, doc.Database, a.Key, AcadDb.OpenMode.ForRead)?.ObjectId ?? AcadDb.ObjectId.Null)
                .Where(id => !id.IsNull).ToArray();
            doc.Editor.SetImpliedSelection(ids);
            Say($"{ids.Length} eixo(s) selecionado(s) no desenho.");
        }
    }

    private void ReverseAxes(IReadOnlyCollection<AxisInput> targets)
    {
        List<AxisInput> open = targets.Where(a => !a.Path.Closed).ToList();
        if (open.Count == 0)
        {
            Say("Aneis de rotatoria nao tem sentido para inverter.");
            return;
        }

        Edit($"Sentido invertido em {open.Count} eixo(s).", db =>
        {
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc is null)
                return;
            List<AcadDb.ObjectId> ids = new();
            using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (AxisInput a in open)
                {
                    if (AxisEntity(tr, db, a.Key, AcadDb.OpenMode.ForRead) is AcadDb.Entity e)
                        ids.Add(e.ObjectId);
                }
            }

            (_, List<string> notes) = ViarioCommands.ReverseAxes(doc, ids);
            foreach (string n in notes)
                doc.Editor.WriteMessage("\n  [C3DVIARIO] " + n);
        });
    }

    private void RemoveAxes(IReadOnlyCollection<AxisInput> targets)
    {
        if (targets.Count == 0)
            return;
        if (targets.Count > 1 && SW.MessageBox.Show(this,
                $"Tirar {targets.Count} eixos da rede?\nAs polilinhas continuam no desenho (os nomes tambem); as linhas geradas deles somem.",
                "Remover da rede", SW.MessageBoxButton.YesNo, SW.MessageBoxImage.Question) != SW.MessageBoxResult.Yes)
            return;

        _selectedRow = null;
        Edit($"{targets.Count} eixo(s) tirado(s) da rede (as polilinhas continuam no desenho).", db =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            foreach (AxisInput a in targets)
            {
                if (AxisEntity(tr, db, a.Key, AcadDb.OpenMode.ForWrite) is AcadDb.Entity e)
                    ViarioStore.ClearTag(e);
                _checked.Remove("EIXO:" + a.Key);
            }

            tr.Commit();
        });
    }

    // ---------------- Nomes pelos textos proximos ----------------

    /// <summary>Textos (TEXT e MTEXT) do model space: texto, centro, angulo e layer.</summary>
    private static List<TextCandidate> CollectTexts(AcadDb.Database db)
    {
        List<TextCandidate> list = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            string dxf;
            try
            {
                dxf = id.ObjectClass.DxfName;
            }
            catch
            {
                continue;
            }

            if (dxf != "TEXT" && dxf != "MTEXT")
                continue;
            try
            {
                AcadDb.DBObject obj = tr.GetObject(id, AcadDb.OpenMode.ForRead, false);
                string text;
                double angle;
                Point3d fallback;
                switch (obj)
                {
                    case AcadDb.DBText t:
                        text = t.TextString;
                        angle = t.Rotation;
                        fallback = t.Position;
                        break;
                    case AcadDb.MText m:
                        text = m.Text;
                        angle = m.Rotation;
                        fallback = m.Location;
                        break;
                    default:
                        continue;
                }

                // Centro do texto (rotulo comprido ao longo da via: a ponta pode ficar longe dela).
                V2 at;
                try
                {
                    AcadDb.Extents3d ext = ((AcadDb.Entity)obj).GeometricExtents;
                    at = new V2((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2);
                }
                catch
                {
                    at = new V2(fallback.X, fallback.Y);
                }

                list.Add(new TextCandidate(text ?? "", at, angle, ((AcadDb.Entity)obj).Layer));
            }
            catch
            {
                // texto ilegivel
            }
        }

        return list;
    }

    /// <summary>
    /// Janela "Nomear eixos pelos textos": layers dos textos, distancia maxima, alinhamento com o
    /// eixo, numeros ignorados; previa com o nome proposto por eixo (marque o que aplicar).
    /// </summary>
    private void OpenNameDialog(List<AxisInput> initialTargets)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        List<TextCandidate> texts;
        try
        {
            texts = CollectTexts(doc.Database);
        }
        catch (Exception ex)
        {
            Say("Nao foi possivel ler os textos: " + ex.Message);
            return;
        }

        if (texts.Count == 0)
        {
            Say("Nao ha TEXT nem MTEXT no model space.");
            return;
        }

        Dictionary<string, string> prefs = ReadPreferences();
        HashSet<string> savedLayers = prefs.TryGetValue("nomes.layers", out string? sl)
            ? new HashSet<string>(sl.Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string savedDist = prefs.TryGetValue("nomes.dist", out string? sd) ? sd : "15";

        SW.Window win = new()
        {
            Title = "Nomear eixos pelos textos proximos - C3DVIARIO",
            Width = 860,
            Height = 620,
            MinWidth = 640,
            MinHeight = 420,
            Owner = this,
            WindowStartupLocation = SW.WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = T.Back,
            Foreground = T.Text,
            FontFamily = UiFont,
            FontSize = 12
        };
        foreach (object key in Resources.Keys)
            win.Resources[key] = Resources[key];
        try
        {
            win.Resources.MergedDictionaries.Add(MenuStyles());
        }
        catch
        {
            // menus no modelo padrao
        }

        WC.Grid root = new() { Margin = new SW.Thickness(10) };
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1, SW.GridUnitType.Star) });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });

        // ---- Opcoes ----
        WC.Grid top = new();
        top.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(260) });
        top.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });

        WC.StackPanel layerPane = new() { Margin = new SW.Thickness(0, 0, 10, 0) };
        layerPane.Children.Add(new WC.TextBlock { Text = "Layers dos textos", FontWeight = SW.FontWeights.Bold, Foreground = T.Text, Margin = new SW.Thickness(0, 0, 0, 3) });
        WC.StackPanel layerList = new();
        List<WC.CheckBox> layerBoxes = new();
        foreach (IGrouping<string, TextCandidate> g in texts.GroupBy(t => t.Layer, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
        {
            WC.CheckBox cb = new()
            {
                Content = $"{g.Key} ({g.Count()})",
                Tag = g.Key,
                IsChecked = savedLayers.Count == 0 || savedLayers.Contains(g.Key),
                Foreground = T.Text,
                Margin = new SW.Thickness(0, 1, 0, 1)
            };
            layerBoxes.Add(cb);
            layerList.Children.Add(cb);
        }

        layerPane.Children.Add(new WC.Border
        {
            BorderBrush = T.Line,
            BorderThickness = new SW.Thickness(1),
            Background = T.Pane,
            Height = 150,
            Child = new WC.ScrollViewer { Content = layerList, Padding = new SW.Thickness(4), VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto }
        });
        WC.StackPanel layerButtons = new() { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(0, 4, 0, 0) };
        WC.Button allLayers = new() { Content = "Todas", Padding = new SW.Thickness(8, 1, 8, 1), Margin = new SW.Thickness(0, 0, 4, 0) };
        allLayers.Click += (_, _) => layerBoxes.ForEach(b => b.IsChecked = true);
        WC.Button noLayers = new() { Content = "Nenhuma", Padding = new SW.Thickness(8, 1, 8, 1) };
        noLayers.Click += (_, _) => layerBoxes.ForEach(b => b.IsChecked = false);
        layerButtons.Children.Add(allLayers);
        layerButtons.Children.Add(noLayers);
        layerPane.Children.Add(layerButtons);
        top.Children.Add(layerPane);

        WC.StackPanel opts = new();
        WC.Grid.SetColumn(opts, 1);
        WC.StackPanel distRow = new() { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(0, 0, 0, 4) };
        distRow.Children.Add(new WC.TextBlock { Text = "Distancia maxima do texto ao eixo (m):", Foreground = T.Text, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(0, 0, 6, 0) });
        WC.TextBox dist = new() { Text = savedDist, Width = 70, Background = T.Input, Foreground = T.Text, CaretBrush = T.Text, BorderBrush = T.Line };
        distRow.Children.Add(dist);
        opts.Children.Add(distRow);
        WC.CheckBox useAngle = new() { Content = "Preferir textos alinhados com o eixo (rotulos de rua; resolve os cruzamentos)", IsChecked = true, Foreground = T.Text, Margin = new SW.Thickness(0, 2, 0, 2) };
        WC.CheckBox noNumbers = new() { Content = "Ignorar textos so com numeros (cotas, lotes, estacas)", IsChecked = true, Foreground = T.Text, Margin = new SW.Thickness(0, 2, 0, 2) };
        WC.CheckBox onlyUnnamed = new() { Content = "So eixos sem nome", IsChecked = true, Foreground = T.Text, Margin = new SW.Thickness(0, 2, 0, 2) };
        WC.RadioButton scopeTargets = new() { Content = $"Eixos marcados/selecionados ({initialTargets.Count})", GroupName = "escopo", IsChecked = initialTargets.Count > 0, IsEnabled = initialTargets.Count > 0, Foreground = T.Text, Margin = new SW.Thickness(0, 6, 0, 2) };
        WC.RadioButton scopeAll = new() { Content = $"Todos os eixos ({_axes.Count})", GroupName = "escopo", IsChecked = initialTargets.Count == 0, Foreground = T.Text, Margin = new SW.Thickness(0, 2, 0, 2) };
        WC.CheckBox civil = new() { Content = "Levar o nome aos alinhamentos do Civil ja gerados (os que tem nome automatico)", IsChecked = true, Foreground = T.Text, Margin = new SW.Thickness(0, 6, 0, 2) };
        foreach (SW.UIElement el in new SW.UIElement[] { useAngle, noNumbers, onlyUnnamed, scopeTargets, scopeAll, civil })
            opts.Children.Add(el);
        WC.Button search = new() { Content = "Procurar", Padding = new SW.Thickness(14, 3, 14, 3), Margin = new SW.Thickness(0, 8, 0, 0), HorizontalAlignment = SW.HorizontalAlignment.Left, FontWeight = SW.FontWeights.Bold };
        opts.Children.Add(search);
        top.Children.Add(opts);
        root.Children.Add(top);

        // ---- Previa ----
        WC.ListView preview = new() { Margin = new SW.Thickness(0, 10, 0, 6), Background = T.Pane, Foreground = T.Text, BorderBrush = T.Line, SelectionMode = WC.SelectionMode.Extended };
        WC.Grid.SetRow(preview, 1);
        root.Children.Add(preview);
        List<Row> rows = new();

        WC.DockPanel bottom = new();
        WC.Grid.SetRow(bottom, 2);
        WC.TextBlock info = new() { Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, TextWrapping = SW.TextWrapping.Wrap };
        WC.StackPanel buttons = new() { Orientation = WC.Orientation.Horizontal };
        WC.Button apply = new() { Content = "Aplicar marcados", Padding = new SW.Thickness(14, 3, 14, 3), Margin = new SW.Thickness(0, 0, 6, 0), FontWeight = SW.FontWeights.Bold, IsEnabled = false };
        WC.Button close = new() { Content = "Fechar", Padding = new SW.Thickness(14, 3, 14, 3) };
        close.Click += (_, _) => win.Close();
        buttons.Children.Add(apply);
        buttons.Children.Add(close);
        WC.DockPanel.SetDock(buttons, WC.Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(info);
        root.Children.Add(bottom);
        info.Text = $"{texts.Count} texto(s) no desenho. Escolha as layers e a distancia e clique em Procurar. Duplo clique numa linha da previa: zoom no eixo.";

        void Count() => apply.Content = $"Aplicar marcados ({rows.Count(r => r.Checked)})";

        search.Click += (_, _) =>
        {
            if (!double.TryParse(dist.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double maxDist) || maxDist <= 0)
            {
                info.Text = "Distancia invalida.";
                return;
            }

            HashSet<string> layers = new(layerBoxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag), StringComparer.OrdinalIgnoreCase);
            WritePreference("nomes.dist", maxDist.ToString(CultureInfo.InvariantCulture));
            WritePreference("nomes.layers", layers.Count == layerBoxes.Count ? "" : string.Join("|", layers));
            List<AxisInput> scope = scopeTargets.IsChecked == true ? initialTargets : _axes;
            if (onlyUnnamed.IsChecked == true)
                scope = scope.Where(a => !_ownNames.ContainsKey(a.Key)).ToList();
            List<TextCandidate> chosen = texts.Where(t => layers.Contains(t.Layer)).ToList();
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            // Todos os eixos disputam os textos (um rotulo de cruzamento fica com a via certa);
            // so os do escopo entram na previa.
            HashSet<string> inScope = new(scope.Select(a => a.Key), StringComparer.Ordinal);
            List<NameProposal> found = ViarioNames.Propose(_axes, chosen, maxDist, useAngle.IsChecked == true, noNumbers.IsChecked == true)
                .Where(p => inScope.Contains(p.AxisKey)).ToList();
            rows = found.Select(p =>
            {
                int i = _axes.FindIndex(a => a.Key == p.AxisKey);
                AxisInput a = _axes[i];
                string current = _ownNames.TryGetValue(a.Key, out string? own) ? own : "";
                return new Row
                {
                    Id = "NOME:" + a.Key,
                    Icon = a.Path.Closed ? "ROTATORIA" : "EIXO",
                    Data = (a, p),
                    Checked = !string.Equals(current, p.Name, StringComparison.Ordinal),
                    Cells = new[]
                    {
                        AxisName(a, i), p.Name, p.Hits.ToString(CultureInfo.InvariantCulture), N(p.Distance, 1),
                        current.Length == 0 ? "-" : current, p.Others.Count == 0 ? "-" : string.Join("; ", p.Others)
                    }
                };
            }).OrderBy(r => r.Cells[0], StringComparer.CurrentCultureIgnoreCase).ToList();
            preview.View = ListViewFor(new[] { "Eixo", "Nome proposto", "Vezes", "Dist. [m]", "Nome atual", "Outros textos perto" },
                new double[] { 150, 200, 50, 65, 120, 220 }, rows, Count, all => { rows.ForEach(r => r.Checked = all); Count(); }, rows.Count > 0 && rows.All(r => r.Checked));
            preview.ItemsSource = rows;
            int without = scope.Count - found.Count;
            info.Text = $"{found.Count} eixo(s) com nome encontrado, {without} sem texto perto ({chosen.Count} texto(s) nas layers escolhidas, {sw.ElapsedMilliseconds} ms). " +
                        "Desmarque o que nao quiser e clique em Aplicar.";
            apply.IsEnabled = rows.Count > 0;
            Count();
        };

        preview.MouseDoubleClick += (_, _) =>
        {
            if (preview.SelectedItem is Row { Data: ValueTuple<AxisInput, NameProposal> pair })
                ZoomTo(pair.Item1.Path.Bounds().Inflate(pair.Item1.Section.TotalWidth + 10));
        };

        apply.Click += (_, _) =>
        {
            List<(AxisInput Axis, NameProposal P)> chosen = rows.Where(r => r.Checked).Select(r => (ValueTuple<AxisInput, NameProposal>)r.Data!).ToList();
            if (chosen.Count == 0)
                return;
            bool toCivil = civil.IsChecked == true;
            Edit($"{chosen.Count} eixo(s) nomeado(s) pelos textos.", db =>
            {
                using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
                Dictionary<string, (string, string)> renamed = new();
                foreach ((AxisInput a, NameProposal p) in chosen)
                {
                    if (AxisEntity(tr, db, a.Key, AcadDb.OpenMode.ForWrite) is not AcadDb.Entity e)
                        continue;
                    renamed[a.Key] = (_ownNames.TryGetValue(a.Key, out string? old) ? old : "", p.Name);
                    ViarioStore.SetAxisName(tr, db, e, p.Name);
                }

                int civilCount = toCivil ? ViarioCivil.RenameFromAxes(tr, db, renamed) : 0;
                tr.Commit();
                if (civilCount > 0)
                    AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n  [C3DVIARIO] {civilCount} alinhamento(s) do Civil renomeado(s).");
            }, regenerate: false);
            win.Close();
        };

        win.Content = root;
        win.ShowDialog();
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
            Prop("Taper", N(Math.Max(ev.Taper, ev.TaperIn)) + " m" + (ev.Smooth ? " (suave)" : " (reto)"));
        if (ev.Kind is AxisEventKind.Alargamento or AxisEventKind.Baia)
        {
            ChoiceProp("TaperModo", "Taper fica",
                new[] { ("longe do no (saida)", "longe"), ("junto ao no (entrada)", "junto"), ("nas duas pontas", "ambos") }, ev.TaperMode,
                () => SaveTaperMode(a, ev, Text("TaperModo")));
        }
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

    private void SaveTaperMode(AxisInput a, AxisEvent ev, string mode)
    {
        if (mode == ev.TaperMode)
            return;
        string serialized = ev.Serialize();
        double taper = Math.Max(ev.Taper, ev.TaperIn);
        string where = mode == "junto" ? "Junto" : mode == "ambos" ? "Ambos" : "Longe";
        Edit("Taper: " + mode, _ => ViarioElementCommands.ModifyAxisEvents(AcApp.DocumentManager.MdiActiveDocument, a.Key, events =>
        {
            foreach (AxisEvent e in events.Where(e => e.Serialize() == serialized))
                ViarioElementCommands.ApplyTaperWhere(e, where, taper);
        }));
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
            if (c.Limit.Length > 0)
                Prop("Limite", $"pedido {N(c.Requested)} m; {c.Limit}");
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

            (List<AxisInput> axes, ViarioResult result, _) = ViarioSync.ComputeCurrent(db);
            count = ViarioSignageSync.Sync(AcApp.DocumentManager.MdiActiveDocument, axes, result, create: true);
        }, regenerate: false);
        if (count >= 0)
            Say($"Sinalizacao: {count} marca(s) no bloco {ViarioSignageSync.BlockName}.");
    }

    // ---------------- Civil, avisos, config ----------------

    private void ShowPlaca(PlacaItem p)
    {
        Category("Placa " + p.Id);
        Prop("Arquivo SinC", p.Codigo + "_SV.dwg");
        Prop("Trecho", $"{p.Trecho} {p.Rota}".Trim());
        if (p.TrechoDescricao.Length > 0)
            Prop("Descricao do trecho", p.TrechoDescricao);
        Prop("Sentido / item", $"{p.Sentido}-{p.Item:00}");
        Prop("Mensagens", p.MensagemResumo.Length > 0 ? p.MensagemResumo : "-");
        Prop("Dimensoes", $"SinC {p.DimensaoSinC}" + (p.DimensaoProjeto.Length > 0 ? $" | projeto {p.DimensaoProjeto}" : ""));
        Prop("Area", $"SinC {N(p.AreaSinC, 2)} m2" + (p.AreaProjetoM2 > 0 ? $" | projeto {N(p.AreaProjetoM2, 2)} m2" : ""));
        Prop("Suporte", (p.SuporteTipo.Length > 0 ? p.SuporteTipo + " (pela area)" : "nao inserido") + (p.Suporte.Length > 0 ? $" | projeto: {p.Suporte}" : "")
            + (p.AlturaSuporte.Length > 0 ? $" | altura {p.AlturaSuporte} m" : ""));
        Prop("E / N", $"{N(p.E)} ; {N(p.N)}");
        Category("Vinculo com a rede");
        if (p.Vinculada)
        {
            Prop("Eixo", AxisLabel(p.AxisKey) + (p.AxisName.Length == 0 ? " (sem nome)" : ""));
            Prop("Estaca", $"{p.EstacaTexto} (km {N(p.S / 1000.0, 3)})");
            Prop("Lado", p.Lado > 0 ? "esquerda (LE)" : "direita (LD)");
            Prop("Afastamento do eixo", N(p.Offset, 2) + " m");
            Prop("Rotacao do bloco", N(p.Rotacao, 1) + " graus");
        }
        else
        {
            Prop("Eixo", "nenhum a menos da distancia maxima (rode ATualizar com outra distancia)");
        }

        Act("Zoom", ZoomCurrent, true);
        Act("Google Maps", () => OpenMaps(p.E, p.N));
        Act(_list.SelectedItems.Count > 1 ? $"Escalar {_list.SelectedItems.Count} selecionadas..." : "Escalar esta...", () =>
        {
            HashSet<string> ids = new(_list.SelectedItems.OfType<Row>().Select(r => r.Data).OfType<PlacaItem>().Select(q => q.Id), StringComparer.Ordinal);
            if (ids.Count == 0)
                ids.Add(p.Id);
            ViarioPlacaSync.PendingIds = ids;
            Send("C3DVIARIOPLACAS ES ", $"Escalar {ids.Count} placa(s): informe a escala na linha de comando.");
        });
        Act("Arrumar esta...", () => { ViarioPlacaSync.PendingIds = new HashSet<string>(StringComparer.Ordinal) { p.Id }; Send("C3DVIARIOPLACAS AR ", "Arrumar: Rotacionar/Mover/Alinhar/Girar180/Lado na linha de comando."); });
        Act("Recalcular coordenadas", () =>
        {
            HashSet<string> ids = new(_list.SelectedItems.OfType<Row>().Select(r => r.Data).OfType<PlacaItem>().Select(q => q.Id), StringComparer.Ordinal);
            if (ids.Count == 0)
                ids.Add(p.Id);
            ViarioPlacaSync.PendingIds = ids;
            Send("C3DVIARIOPLACAS CO ", $"Recalcular coordenadas de {ids.Count} placa(s) pelo suporte/linha de chamada: siga a linha de comando.");
        });
        Act("Substituir bloco...", () => { ViarioPlacaSync.PendingIds = new HashSet<string>(StringComparer.Ordinal) { p.Id }; Send("C3DVIARIOPLACAS SU ", "Substituir o bloco desta placa: escolha a pasta."); });
        Act("Atualizar vinculos", () => Send("C3DVIARIOPLACAS AT ", "Atualizar vinculos das placas: siga a linha de comando."));
        Act("Nomear eixos", () => Send("C3DVIARIOPLACAS NO ", "Nomear eixos pelas placas: siga a linha de comando."));
        Act("Importar", () => Send("C3DVIARIOPLACAS I ", "Importar placas: siga a linha de comando."));
        Act("Remover todas", () => Send("C3DVIARIOPLACAS R ", "Remover placas: confirme na linha de comando."));
        List<(AtrativoItem Item, double Dist)> perto = ViarioAtrativos.Near(_atrativos, p.Ponto, 2000.0).Take(6).ToList();
        if (perto.Count > 0)
        {
            Category("Atrativos proximos");
            foreach ((AtrativoItem a, double d) in perto)
                Prop(a.Nome, $"{N(d / 1000.0, 2)} km" + (a.Categoria.Length > 0 ? " · " + a.Categoria : ""));
        }

        AxisInput? axis = p.Vinculada && _axisIndex.TryGetValue(p.AxisKey, out int ai) ? _axes[ai] : null;
        _render = () => RenderPlaca(p, axis);
    }

    private void ShowAtrativo(AtrativoItem a)
    {
        Category(a.Nome);
        Prop("Categoria", a.Categoria.Length > 0 ? a.Categoria : "-");
        Prop("Lat / Lon", $"{N(a.Lat, 6)} ; {N(a.Lon, 6)}");
        Prop("E / N", $"{N(a.E, 2)} ; {N(a.N, 2)}");
        Prop("Raio (escala do bloco)", N(a.Raio, 1) + " m");
        Prop("Eixo mais proximo", NearestAxis(a.Ponto));
        if (a.Fonte.Length > 0)
            Prop("Fonte", a.Fonte);
        List<PlacaItem> placas = _placas.Where(p => p.Mensagens.Any(m => Mention(m, a.Nome))).ToList();
        if (placas.Count > 0)
        {
            Category("Placas que indicam este atrativo");
            foreach (PlacaItem p in placas.Take(12))
                Prop(p.Id, $"{p.Codigo} · {(p.Vinculada ? AxisLabel(p.AxisKey) + " " + p.EstacaTexto : "sem eixo")}");
        }

        Act("Zoom", ZoomCurrent, true);
        Act("Google Maps", () => OpenUrl($"https://www.google.com/maps/search/?api=1&query={a.Lat.ToString("0.#######", CultureInfo.InvariantCulture)},{a.Lon.ToString("0.#######", CultureInfo.InvariantCulture)}"));
        Act("Novo ponto (clique)...", () => Send("C3DVIARIOATRATIVOS N ", "Novo atrativo: clique no desenho e informe o nome."));
        Act("Raio de todos...", () => Send("C3DVIARIOATRATIVOS R ", "Raio dos atrativos: informe na linha de comando."));
        Act("Importar KMZ", () => Send("C3DVIARIOATRATIVOS I ", "Importar atrativos: siga a linha de comando."));
        V2 w = a.Ponto;
        double r = Math.Max(a.Raio * 4, 80);
        _render = () =>
        {
            RenderMap(new Box2(w.X - r, w.Y - r, w.X + r, w.Y + r), sh =>
            {
                sh.Marker(w);
                foreach (PlacaItem p in placas)
                    sh.Marker(p.Ponto);
            });
            if (_vScale <= 0)
                return;
            DrawAxisLabelsNear(w, Math.Max(400, r * 2));
            DrawPointLabels(_placas.Select(q => (q.Ponto, q.Codigo, GraphicText)), 80);
            DrawPointLabels(_atrativos.Where(x => !ReferenceEquals(x, a)).Select(x => (x.Ponto, x.Nome, AtrativoBrush)), 40);
            DrawPointLabels(new[] { (w, a.Nome + (a.Categoria.Length > 0 ? "  (" + a.Categoria + ")" : ""), HighlightBrush) }, 1, true);
        };
    }

    /// <summary>
    /// Hachuras do bloco T-xxx_SV desenhadas na planta na posicao, escala e rotacao da placa
    /// (so quando a placa tem ao menos 8 px na tela; abaixo disso fica o retangulo).
    /// </summary>
    private void DrawSignInPlan(PlacaItem p)
    {
        if (_vScale <= 0)
            return;
        double wPx = p.LarguraSinC * ViarioPlacaSync.FatorSinC * p.Escala * _vScale;
        if (wPx < 8)
            return;
        (_, List<Seg> rect, _) = ViarioPlacaSync.Footprint(p);
        SW.Point o = ToScreen(rect[0].A);
        if (o.X < -wPx * 2 || o.Y < -wPx * 2 || o.X > _vW + (wPx * 2) || o.Y > _vH + (wPx * 2))
            return;

        // Imagem do SinC (leve): o retangulo do bloco recebe a imagem girada e escalada.
        V2 origem0 = rect[0].A;
        if (SignBitmap(p.Codigo) is WMI.BitmapSource bmp)
        {
            double bw = p.LarguraSinC * ViarioPlacaSync.FatorSinC, bh = p.AlturaSinC * ViarioPlacaSync.FatorSinC;
            WM.Matrix im = WM.Matrix.Identity;
            im.Scale(bw / bmp.PixelWidth, -bh / bmp.PixelHeight); // px -> unidades do bloco (y para baixo)
            im.Scale(p.Escala, p.Escala);
            im.Rotate(p.Rotacao);
            im.Translate(origem0.X, origem0.Y);
            im.Scale(_vScale, -_vScale);
            im.Translate(_vOx - (_vCx * _vScale), _vOy + (_vCy * _vScale));
            WM.MatrixTransform ixf = new(im);
            ixf.Freeze();
            WC.Image image = new() { Source = bmp, Width = bmp.PixelWidth, Height = bmp.PixelHeight, Stretch = WM.Stretch.Fill, RenderTransform = ixf };
            WM.RenderOptions.SetBitmapScalingMode(image, wPx < 200 ? WM.BitmapScalingMode.LowQuality : WM.BitmapScalingMode.Linear);
            _graphic.Children.Add(image);
            return;
        }

        ViarioPlacaSync.SignShape? shape = null;
        try
        {
            if (AcApp.DocumentManager.MdiActiveDocument is Document d)
                shape = ViarioPlacaSync.Shape(d.Database, p.Codigo);
        }
        catch
        {
            // sem bloco
        }

        if (shape is null)
            return;

        // Geometria congelada por codigo (coordenadas do bloco); por placa so muda a matriz
        // (escala, rotacao, posicao e a transformacao da vista). Detalhes (letras) so de perto.
        List<(WM.Geometry Geo, WM.Brush Brush, bool Detail)> parts = SignGeometry(p.Codigo, shape);
        V2 origem = rect[0].A;
        WM.Matrix m = WM.Matrix.Identity;
        m.Scale(p.Escala, p.Escala);
        m.Rotate(p.Rotacao);
        m.Translate(origem.X, origem.Y);
        m.Scale(_vScale, -_vScale);
        m.Translate(_vOx - (_vCx * _vScale), _vOy + (_vCy * _vScale));
        WM.MatrixTransform xf = new(m);
        xf.Freeze();
        bool details = wPx >= 90;
        foreach ((WM.Geometry geo, WM.Brush brush, bool detail) in parts)
        {
            if (detail && !details)
                continue;
            _graphic.Children.Add(new WS.Path { Data = geo, Fill = brush, RenderTransform = xf });
        }
    }

    private static readonly Dictionary<string, List<(WM.Geometry, WM.Brush, bool)>> SignGeoCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, WMI.BitmapSource?> SignBitmapCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Esquece imagem e geometria em cache desses codigos (blocos substituidos).</summary>
    public static void InvalidateSignCaches(IEnumerable<string> codigos)
    {
        foreach (string c in codigos)
        {
            SignBitmapCache.Remove(c);
            SignGeoCache.Remove(c);
        }
    }

    /// <summary>Imagem da placa (T-xxx.jpg/png ou IMG.png do .pla), decodificada a 480 px e congelada; null sem arquivo.</summary>
    private static WMI.BitmapSource? SignBitmap(string codigo)
    {
        if (SignBitmapCache.TryGetValue(codigo, out WMI.BitmapSource? cached))
            return cached;
        WMI.BitmapSource? result = null;
        try
        {
            string? file = ViarioPlacaSync.SignImagePath(codigo);
            if (file is not null)
            {
                WMI.BitmapImage bi = new();
                bi.BeginInit();
                bi.CacheOption = WMI.BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 480;
                bi.UriSource = new Uri(file, UriKind.Absolute);
                bi.EndInit();
                bi.Freeze();
                result = bi;
            }
        }
        catch
        {
            // imagem invalida: cai no desenho do bloco
        }

        SignBitmapCache[codigo] = result;
        return result;
    }

    /// <summary>
    /// Hachuras do bloco como geometrias congeladas em coordenadas do bloco, na ordem original de
    /// desenho (fundo, orla, fundo interno, textos...): hachuras seguidas de mesma cor viram uma so.
    /// </summary>
    private static List<(WM.Geometry, WM.Brush, bool)> SignGeometry(string codigo, ViarioPlacaSync.SignShape shape)
    {
        if (SignGeoCache.TryGetValue(codigo, out List<(WM.Geometry, WM.Brush, bool)>? cached))
            return cached;

        List<(WM.Geometry, WM.Brush, bool)> parts = new();
        WM.PathGeometry? geo = null;
        (byte, byte, byte, bool) key = default;
        void Flush()
        {
            if (geo is null)
                return;
            geo.Freeze();
            WM.SolidColorBrush brush = new(WM.Color.FromRgb(key.Item1, key.Item2, key.Item3));
            brush.Freeze();
            parts.Add((geo, brush, key.Item4));
            geo = null;
        }

        foreach ((List<List<V2>> loops, byte r, byte g, byte b, bool detail) in shape.Hatches)
        {
            (byte, byte, byte, bool) k = (r, g, b, detail);
            if (geo is null || !k.Equals(key))
            {
                Flush();
                key = k;
                geo = new WM.PathGeometry { FillRule = WM.FillRule.EvenOdd };
            }

            foreach (List<V2> loop in loops)
            {
                WM.PathFigure fig = new() { IsClosed = true, IsFilled = true, StartPoint = new SW.Point(loop[0].X, loop[0].Y) };
                WM.PolyLineSegment seg = new();
                for (int i = 1; i < loop.Count; i++)
                    seg.Points.Add(new SW.Point(loop[i].X, loop[i].Y));
                fig.Segments.Add(seg);
                geo.Figures.Add(fig);
            }
        }

        Flush();
        SignGeoCache[codigo] = parts;
        return parts;
    }

    /// <summary>Abre o Google Maps na coordenada do desenho (UTM -> lat/lon pelo fuso do Civil 3D; sem fuso, 21S).</summary>
    private void OpenMaps(double e, double n)
    {
        int zone = 21;
        bool south = true;
        try
        {
            string code = Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument.Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode ?? "";
            if (Civil3D2026Plugin.Modules.Quadros.GeoConversion.TryParseZone(code, out int z, out bool s))
            {
                zone = z;
                south = s;
            }
        }
        catch
        {
            // sem sistema de coordenadas no desenho: fuso padrao
        }

        (double lat, double lon) = Civil3D2026Plugin.Modules.Quadros.GeoConversion.UtmToGeographic(e, n, zone, south);
        OpenUrl($"https://www.google.com/maps/search/?api=1&query={lat.ToString("0.#######", CultureInfo.InvariantCulture)},{lon.ToString("0.#######", CultureInfo.InvariantCulture)}");
        Say($"Google Maps: {lat:0.######}, {lon:0.######} (fuso {zone}{(south ? "S" : "N")}).");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // sem navegador padrao
        }
    }

    private static readonly WM.Brush AtrativoBrush = Brush(230, 120, 230);
    private static readonly WM.Brush PlacaBrush = Brush(220, 150, 90);
    private static readonly WM.Brush PlacaFill = new WM.SolidColorBrush(WM.Color.FromArgb(150, 110, 70, 40));

    /// <summary>Nomes dos eixos que passam a menos de <paramref name="maxDist"/> m do ponto, no pe da perpendicular.</summary>
    private void DrawAxisLabelsNear(V2 w, double maxDist)
    {
        if (_vScale <= 0)
            return;
        double x0 = _vCx - ((_vOx - RulerLeft) / _vScale), x1 = _vCx + ((_vW - _vOx) / _vScale);
        double y0 = _vCy - ((_vH - RulerBottom - _vOy) / _vScale), y1 = _vCy + (_vOy / _vScale);
        Box2 visible = new(x0, y0, x1, y1);
        HashSet<(int, int)> used = new();
        for (int i = 0; i < _axes.Count; i++)
        {
            AxisInput a = _axes[i];
            if (!a.Path.Bounds().Intersects(visible))
                continue;
            (double s, V2 q, double d) = a.Path.Project(w);
            if (d > maxDist)
                continue;
            SW.Point sp = ToScreen(q);
            if (sp.X < RulerLeft || sp.Y < 0 || sp.X > _vW || sp.Y > _vH - RulerBottom)
                continue;
            V2 n = a.Path.TangentAt(s).Left;
            double lx = sp.X - (n.X * 14) + 3, ly = sp.Y + (n.Y * 14) - 14;
            if (!used.Add(((int)(lx / 60), (int)(ly / 16))))
                continue; // rotulo em cima de outro
            Label(AxisName(a, i), lx, ly, 10);
        }
    }

    /// <summary>Texto ao lado de cada ponto visivel (ate <paramref name="max"/>), com fundo para leitura.</summary>
    private void DrawPointLabels(IEnumerable<(V2 P, string Text, WM.Brush Brush)> items, int max, bool bold = false)
    {
        if (_vScale <= 0)
            return;
        int count = 0;
        foreach ((V2 p, string text, WM.Brush brush) in items)
        {
            SW.Point sp = ToScreen(p);
            if (sp.X < RulerLeft || sp.Y < 0 || sp.X > _vW || sp.Y > _vH - RulerBottom)
                continue;
            WC.Border b = new()
            {
                Background = Brush(32, 34, 38),
                BorderBrush = brush,
                BorderThickness = new SW.Thickness(bold ? 1 : 0.5),
                Padding = new SW.Thickness(3, 0, 3, 0),
                Child = new WC.TextBlock { Text = text, Foreground = brush, FontSize = bold ? 11 : 9.5, FontFamily = UiFont, FontWeight = bold ? SW.FontWeights.Bold : SW.FontWeights.Normal }
            };
            WC.Canvas.SetLeft(b, sp.X + 6);
            WC.Canvas.SetTop(b, sp.Y - 18);
            _graphic.Children.Add(b);
            if (++count >= max)
                break;
        }
    }

    /// <summary>Vista geral da categoria Placas/Atrativos: todos os pontos com rotulo.</summary>
    private void RenderOverview(string category)
    {
        List<V2> pts = category == "PLACAS" ? _placas.Select(p => p.Ponto).ToList() : _atrativos.Select(a => a.Ponto).ToList();
        Box2? focus = null;
        if (pts.Count > 0)
        {
            Box2 box = Box2.Empty;
            foreach (V2 p in pts)
                box = box.Add(p);
            focus = box.Inflate(Math.Max(50, (box.MaxX - box.MinX) * 0.05));
        }

        RenderMap(focus, sh =>
        {
            if (category == "PLACAS")
            {
                foreach (PlacaItem p in _placas)
                {
                    (List<Seg> ldc, List<Seg> placa, _) = ViarioPlacaSync.Footprint(p);
                    sh.Add(ldc, GraphicText, 1.0);
                    sh.Add(placa, PlacaBrush, 1.2, true, PlacaFill);
                }
            }

            foreach (V2 p in pts)
                sh.Marker(p);
        });
        if (_vScale <= 0)
            return;
        if (category == "PLACAS")
        {
            foreach (PlacaItem p in _placas)
                DrawSignInPlan(p);
            DrawPointLabels(_placas.Select(p => (p.Ponto, p.Codigo, GraphicText)), 250);
        }
        else
            DrawPointLabels(_atrativos.Select(a => (a.Ponto, a.Nome, AtrativoBrush)), 250);
    }

    /// <summary>Se a mensagem da placa cita o atrativo (compara sem acentos, ignorando "Cach.", "Baln." etc.).</summary>
    private static bool Mention(string mensagem, string nome)
    {
        string a = Simple(mensagem), b = Simple(nome);
        if (a.Length < 4 || b.Length < 4)
            return false;
        return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
    }

    private static string Simple(string s)
    {
        string t = s.Normalize(System.Text.NormalizationForm.FormD);
        System.Text.StringBuilder sb = new();
        foreach (char c in t)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString().Replace("cach. ", "cachoeira ").Replace("baln. ", "balneario ").Replace("pq. nac. ", "parque nacional ")
            .Replace("comunidade ", "").Replace(" da ", " ").Replace(" do ", " ").Replace(" de ", " ").Replace(" dos ", " ").Replace(" das ", " ").Trim();
    }

    private string NearestAxis(V2 p)
    {
        AxisInput? best = null;
        double bestDist = double.MaxValue, bestS = 0;
        foreach (AxisInput a in _axes)
        {
            Box2 box = a.Path.Bounds().Inflate(500.0);
            if (p.X < box.MinX || p.X > box.MaxX || p.Y < box.MinY || p.Y > box.MaxY)
                continue;
            (double s, _, double d) = a.Path.Project(p);
            if (d < bestDist)
            {
                best = a;
                bestDist = d;
                bestS = s;
            }
        }

        return best is null ? "-" : $"{AxisLabel(best.Key)} ({N(bestDist, 0)} m, est. {Estaca(bestS)})";
    }

    /// <summary>
    /// Monitor da placa: planta a direita (eixos com nome, marcador no ponto) e, a esquerda,
    /// o desenho da placa (hachuras do bloco T-xxx_SV) com os dados principais.
    /// </summary>
    private void RenderPlaca(PlacaItem p, AxisInput? axis)
    {
        V2 w = p.Ponto;
        // Foco deslocado: o ponto fica no terco direito, fora do cartao da placa.
        (List<Seg> ldc, List<Seg> placa, _) = ViarioPlacaSync.Footprint(p);
        RenderMap(new Box2(w.X - 110, w.Y - 45, w.X + 50, w.Y + 45), sh =>
        {
            if (axis is not null)
                sh.Highlight(axis.Path.Segs);
            // Placa como inserida: linha de chamada, retangulo do bloco (marrom) e o ponto do suporte.
            sh.Add(ldc, GraphicText, 1.2);
            sh.Add(placa, PlacaBrush, 1.5, true, PlacaFill);
            sh.Marker(w);
        });
        if (_vScale <= 0)
            return;

        // Desenho real da placa (hachuras do bloco) na planta, como inserida.
        DrawSignInPlan(p);
        foreach (PlacaItem q in _placas.Where(q => !ReferenceEquals(q, p) && V2.Dist(q.Ponto, w) < 400).Take(40))
            DrawSignInPlan(q);

        // Nomes dos eixos em volta do ponto, codigo desta e das placas vizinhas, atrativos.
        DrawAxisLabelsNear(w, 400);
        DrawPointLabels(_placas.Where(q => !ReferenceEquals(q, p)).Select(q => (q.Ponto, q.Codigo, GraphicText)), 60);
        DrawPointLabels(_atrativos.Select(a => (a.Ponto, a.Nome, AtrativoBrush)), 40);
        DrawPointLabels(new[] { (w, p.Codigo, HighlightBrush) }, 1, true);

        // Cartao da placa.
        double cw = Math.Max(160, Math.Min(_vW * 0.42, 420));
        ViarioPlacaSync.SignShape? shape = null;
        try
        {
            if (AcApp.DocumentManager.MdiActiveDocument is Document d)
                shape = ViarioPlacaSync.Shape(d.Database, p.Codigo);
        }
        catch
        {
            // Sem desenho ou bloco: cartao so com texto.
        }

        double pad = 8;
        double drawW = cw - (2 * pad);
        double drawH = shape is null ? 0 : drawW * shape.Height / shape.Width;
        if (shape is null && SignBitmap(p.Codigo) is WMI.BitmapSource b0)
            drawH = drawW * b0.PixelHeight / b0.PixelWidth;
        double textH = 58;
        double ch = drawH + textH + (2 * pad);
        WC.Canvas card = new() { Width = cw, Height = ch, Background = Brush(32, 34, 38), ClipToBounds = true };
        WC.Border frame = new()
        {
            Width = cw, Height = ch, BorderBrush = HighlightBrush, BorderThickness = new SW.Thickness(1), Child = card, Background = Brush(32, 34, 38)
        };
        WC.Canvas.SetLeft(frame, RulerLeft + 10);
        WC.Canvas.SetTop(frame, 10);

        WMI.BitmapSource? bmp = SignBitmap(p.Codigo);
        if (bmp is not null)
        {
            drawH = drawW * bmp.PixelHeight / bmp.PixelWidth;
            ch = drawH + textH + (2 * pad);
            card.Height = ch;
            frame.Height = ch;
            WC.Image im = new() { Source = bmp, Width = drawW, Height = drawH, Stretch = WM.Stretch.Fill };
            WM.RenderOptions.SetBitmapScalingMode(im, WM.BitmapScalingMode.HighQuality);
            WC.Canvas.SetLeft(im, pad);
            WC.Canvas.SetTop(im, pad);
            card.Children.Add(im);
        }
        else if (shape is not null)
        {
            double k = drawW / shape.Width;
            WM.Matrix cm = WM.Matrix.Identity;
            cm.Scale(k, -k);
            cm.Translate(pad, pad);
            WM.MatrixTransform cxf = new(cm);
            cxf.Freeze();
            foreach ((WM.Geometry geo, WM.Brush brush, _) in SignGeometry(p.Codigo, shape))
                card.Children.Add(new WS.Path { Data = geo, Fill = brush, RenderTransform = cxf });
        }
        else if (bmp is null)
        {
            WC.TextBlock none = new() { Text = "(bloco " + p.Codigo + "_SV nao esta no desenho)", Foreground = T.Muted, FontSize = 10, FontFamily = UiFont };
            WC.Canvas.SetLeft(none, pad);
            WC.Canvas.SetTop(none, pad);
            card.Children.Add(none);
            drawH = 14;
            ch = drawH + textH + (2 * pad);
            card.Height = ch;
            frame.Height = ch;
        }

        double ty = pad + drawH + 4;
        void Line(string text, double size, WM.Brush brush)
        {
            WC.TextBlock t = new() { Text = text, Foreground = brush, FontSize = size, FontFamily = UiFont, Width = drawW, TextTrimming = SW.TextTrimming.CharacterEllipsis };
            WC.Canvas.SetLeft(t, pad);
            WC.Canvas.SetTop(t, ty);
            card.Children.Add(t);
            ty += size + 4;
        }

        Line($"{p.Id}  ·  {p.Codigo}  ·  {p.DimensaoSinC}  ·  {N(p.AreaSinC, 2)} m²" + (p.SuporteTipo.Length > 0 ? "  ·  " + p.SuporteTipo : ""), 11, HighlightBrush);
        Line(p.Vinculada ? $"{AxisLabel(p.AxisKey)}  ·  est. {p.EstacaTexto}  ·  {p.LadoTexto}  ·  {N(p.Offset, 1)} m do eixo" : "sem eixo vinculado", 10, GraphicText);
        Line(p.MensagemResumo.Length > 0 ? p.MensagemResumo : "-", 10, GraphicText);
        _graphic.Children.Add(frame);
    }

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
        Category("Cotas das faixas (C3DVIARIOCOTAR)");
        List<(string, string)> styles = new() { ("corrente do desenho", "") };
        try
        {
            if (AcApp.DocumentManager.MdiActiveDocument is Document d)
                styles.AddRange(ViarioDimensions.StyleNames(d.Database).Select(n => (n, n)));
        }
        catch
        {
            // Sem desenho ativo.
        }
        ChoiceProp("CotaEstilo", "Estilo de cota", styles, _settings.CotaEstilo);
        EditProp("CotaEscala", "Escala (DIMSCALE, 0 = do estilo)", N(_settings.CotaEscala));
        Prop("Layer", ViarioStore.LayerDims);
        Category("Hachura do pavimento (C3DVIARIOHACHURA)");
        ChoiceProp("HachuraPadrao", "Padrao", ViarioHatchSync.Patterns.Select(x => (x, x)), _settings.HachuraPadrao);
        EditProp("HachuraEscala", "Escala do padrao", N(_settings.HachuraEscala));
        EditProp("HachuraAngulo", "Angulo (graus)", N(_settings.HachuraAngulo));
        CheckProp("HachuraSolida", "Solido com transparencia", _settings.HachuraSolida);
        EditProp("HachuraTransparencia", "Transparencia do solido (%)", _settings.HachuraTransparencia.ToString(CultureInfo.InvariantCulture));
        EditProp("HachuraBloco", "Blocos da hachura (m, 0 = sem recorte)", N(_settings.HachuraBloco))
            .ToolTip = "Redes grandes: a hachura e recortada numa grade de blocos deste tamanho; so os blocos que mudam sao refeitos.";
        Prop("Layer ", ViarioHatchSync.Layer);
        Category("Painel");
        Prop("Tema", (_dark ? "escuro" : "claro") + " (Preferencias...)");
        Act("Salvar", () =>
        {
            double? snap = Number("SnapTolerance", 1e-6), capture = Number("Capture"), setback = Number("MedianSetback"), cotaEscala = Number("CotaEscala");
            double? hEsc = Number("HachuraEscala", 1e-6), hAng = Number("HachuraAngulo", -360), hTr = Number("HachuraTransparencia"), hBl = Number("HachuraBloco");
            if (snap is null || capture is null || setback is null || cotaEscala is null || hEsc is null || hAng is null || hTr is null || hBl is null)
            {
                Say("Corrija os valores em vermelho.");
                return;
            }

            Edit("Configuracao salva.", db =>
            {
                _settings.SnapTolerance = snap.Value;
                _settings.Capture = capture.Value;
                _settings.MedianSetback = setback.Value;
                _settings.CotaEstilo = Text("CotaEstilo");
                _settings.CotaEscala = cotaEscala.Value;
                _settings.HachuraPadrao = Text("HachuraPadrao") is { Length: > 0 } hp ? hp : "ANSI31";
                _settings.HachuraEscala = hEsc.Value;
                _settings.HachuraAngulo = hAng.Value;
                _settings.HachuraSolida = Checked("HachuraSolida");
                _settings.HachuraTransparencia = (int)Math.Round(Math.Clamp(hTr.Value, 0, 90));
                _settings.HachuraBloco = hBl.Value <= 0 ? 0 : Math.Max(hBl.Value, ViarioHatch.MinTile);
                using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ViarioStore.SaveSettings(tr, db, _settings);
                    tr.Commit();
                }

                if (AcApp.DocumentManager.MdiActiveDocument is Document doc)
                    ViarioHatchSync.Restyle(doc);
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
            case PlacaItem placa:
                ZoomTo(ViarioPlacaSync.ZoomBox(placa));
                break;
            case AtrativoItem atrativo:
                ZoomTo(new Box2(atrativo.E - (3 * atrativo.Raio), atrativo.N - (3 * atrativo.Raio), atrativo.E + (3 * atrativo.Raio), atrativo.N + (3 * atrativo.Raio)));
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
        public readonly List<Box2> Boxes = new();
        public readonly List<Lod> Lods = new();
        public readonly List<V2> Markers = new();

        public Shapes()
        {
        }

        /// <summary>Copia (as listas de pontos e os niveis de detalhe sao compartilhados).</summary>
        public Shapes(Shapes from)
        {
            Items.AddRange(from.Items);
            Boxes.AddRange(from.Boxes);
            Lods.AddRange(from.Lods);
            Markers.AddRange(from.Markers);
        }

        public void Add(IEnumerable<Seg> segs, WM.Brush brush, double thickness, bool closed = false, WM.Brush? fill = null, bool dashed = false) =>
            Add(Lod.Of(segs), brush, thickness, closed, fill, dashed);

        public void Add(Lod lod, WM.Brush brush, double thickness, bool closed = false, WM.Brush? fill = null, bool dashed = false)
        {
            Items.Add((lod.Points, brush, thickness, closed, fill, dashed));
            Boxes.Add(lod.Box);
            Lods.Add(lod);
        }

        public void Highlight(IEnumerable<Seg> segs) => Add(segs, HighlightBrush, 2.5);

        public void Marker(V2 p) => Markers.Add(p);
    }

    /// <summary>
    /// Pontos de uma linha da vista com versoes simplificadas para os zooms afastados
    /// (pontos a menos de 0,25 / 1 / 4 / 16 / 64 / 256 m do anterior saem). Com a rede inteira
    /// na tela, uma via de quilometros vira poucas dezenas de pontos em vez de milhares.
    /// </summary>
    private sealed class Lod
    {
        private static readonly double[] Tolerances = { 0.25, 1.0, 4.0, 16.0, 64.0, 256.0 };
        private readonly List<V2>?[] _levels = new List<V2>?[Tolerances.Length];

        public List<V2> Points { get; }
        public Box2 Box { get; }

        private Lod(List<V2> points)
        {
            Points = points;
            Box2 box = Box2.Empty;
            foreach (V2 p in points)
                box = box.Add(p);
            Box = box;
        }

        public static Lod Of(IEnumerable<Seg> segs) => new(Sample(segs));

        /// <summary>Pontos para a escala (px por m): erro abaixo de 0,7 px.</summary>
        public List<V2> For(double scale)
        {
            double allowed = 0.7 / Math.Max(scale, 1e-12);
            int level = -1;
            for (int k = 0; k < Tolerances.Length && Tolerances[k] <= allowed; k++)
                level = k;
            if (level < 0 || Points.Count < 8)
                return Points;
            return _levels[level] ??= Decimate(Points, Tolerances[level]);
        }

        private static List<V2> Decimate(List<V2> pts, double tol)
        {
            List<V2> result = new() { pts[0] };
            V2 last = pts[0];
            for (int i = 1; i < pts.Count - 1; i++)
            {
                if (Math.Abs(pts[i].X - last.X) >= tol || Math.Abs(pts[i].Y - last.Y) >= tol)
                {
                    result.Add(pts[i]);
                    last = pts[i];
                }
            }

            result.Add(pts[^1]);
            return result;
        }
    }

    // Linhas da rede ja amostradas para a vista: refeitas so quando a rede muda (nao a cada
    // zoom/arraste). Os eixos sao amostrados uma vez por geometria (o cache de leitura devolve
    // o mesmo GPath enquanto o eixo nao muda): editar um eixo nao reamostra os outros.
    private Shapes? _lineShapes;
    private object? _lineShapesResult;
    private Dictionary<GPath, Lod> _axisLods = new(ReferenceEqualityComparer.Instance);

    private Shapes NetworkShapes()
    {
        if (_lineShapes is null || !ReferenceEquals(_lineShapesResult, _result))
        {
            _lineShapes = BuildLineShapes();
            _lineShapesResult = _result;
        }

        Shapes sh = new(_lineShapes);
        if (_showAxes)
        {
            Dictionary<GPath, Lod> lods = new(ReferenceEqualityComparer.Instance);
            WM.Brush axisBrush = Brush(220, 80, 80);
            foreach (AxisInput a in _axes)
            {
                if (!_axisLods.TryGetValue(a.Path, out Lod? lod))
                    lod = Lod.Of(a.Path.Segs);
                lods[a.Path] = lod;
                sh.Add(lod, axisBrush, 0.7, dashed: true);
            }

            _axisLods = lods;
        }

        return sh;
    }

    /// <summary>Marca da geometria dos eixos (chave, inicio, fim, comprimento e trechos de cada um).</summary>
    private static ulong AxesStamp(List<AxisInput> axes)
    {
        ulong hash = 14695981039346656037UL;
        void Mix(double v)
        {
            hash ^= (ulong)BitConverter.DoubleToInt64Bits(v);
            hash *= 1099511628211UL;
        }

        foreach (AxisInput a in axes)
        {
            foreach (char c in a.Key)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }

            Mix(a.Path.Length);
            Mix(a.Path.Segs.Count);
            if (!a.Path.IsEmpty)
            {
                Mix(a.Path.Start.X);
                Mix(a.Path.Start.Y);
                Mix(a.Path.End.X);
                Mix(a.Path.End.Y);
            }
        }

        return hash;
    }

    private Shapes BuildLineShapes()
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

        return sh;
    }

    private void RenderMap(Box2? focus, Action<Shapes>? extra)
    {
        Shapes sh = NetworkShapes();
        extra?.Invoke(sh);
        Draw(sh, focus);
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
        _coords.Text = _sectionView ? $"offset {w.X,8:0.000} m" : $"E {w.X,12:0.000}   N {w.Y,12:0.000}";
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
        _sectionView = false;
        _graphic.Children.Clear();
        double w = _graphic.ActualWidth, h = _graphic.ActualHeight;
        Box2 extents = Box2.Empty;
        for (int i = 0; i < sh.Items.Count; i++)
        {
            if (sh.Items[i].Points.Count > 0)
                extents = extents.Union(sh.Boxes[i]);
        }

        if (w < 20 || h < 20)
            return;
        if (extents.IsEmpty)
        {
            _vScale = 0;
            Label("Sem eixos na rede.", 10, 10);
            return;
        }

        double dw = w - RulerLeft, dh = h - RulerBottom;
        Box2 box = focus ?? extents;
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

        // Linhas da rede: itens seguidos do mesmo estilo (cor, espessura, tracejado) num Path
        // so, so o que aparece na tela e sem pontos a menos de 0,7 px um do outro - em redes de
        // centenas de vias, milhares de polilinhas WPF travavam a vista a cada zoom/arraste.
        // Itens preenchidos (ilhas) ficam um por Path; a ordem de desenho e a mesma de antes.
        Box2 visible = new Box2(x0, y0, x1, y1).Inflate(4.0 / scale);
        WM.StreamGeometry? runGeometry = null;
        WM.StreamGeometryContext? runContext = null;
        (WM.Color Color, double Thickness, bool Dashed) runKey = default;
        WM.Brush? runBrush = null;

        void AddPath(WM.StreamGeometry geometry, WM.Brush stroke, WM.Brush? fill, double thickness, bool dashed)
        {
            geometry.Freeze();
            WS.Path path = new()
            {
                Data = geometry,
                Stroke = stroke,
                Fill = fill,
                StrokeThickness = thickness,
                StrokeLineJoin = WM.PenLineJoin.Miter
            };
            if (dashed)
                path.StrokeDashArray = new WM.DoubleCollection { 12, 4, 2, 4 };
            _graphic.Children.Add(path);
        }

        void FlushRun()
        {
            if (runGeometry is null || runContext is null || runBrush is null)
                return;
            runContext.Close();
            AddPath(runGeometry, runBrush, null, runKey.Thickness, runKey.Dashed);
            runGeometry = null;
            runContext = null;
            runBrush = null;
        }

        void Figure(WM.StreamGeometryContext context, List<V2> pts, bool filled, bool closed)
        {
            SW.Point last = P(pts[0]);
            context.BeginFigure(last, filled, closed);
            for (int k = 1; k < pts.Count; k++)
            {
                SW.Point q = P(pts[k]);
                if (k < pts.Count - 1 && Math.Abs(q.X - last.X) < 0.7 && Math.Abs(q.Y - last.Y) < 0.7)
                    continue;
                context.LineTo(q, true, false);
                last = q;
            }
        }

        for (int i = 0; i < sh.Items.Count; i++)
        {
            (List<V2> full, WM.Brush brush, double thickness, bool closed, WM.Brush? fill, bool dashed) = sh.Items[i];
            Box2 itemBox = sh.Boxes[i];
            if (full.Count < 2 || !itemBox.Intersects(visible))
                continue;
            // Menor que um pixel: nao aparece.
            if ((itemBox.MaxX - itemBox.MinX) * scale < 0.6 && (itemBox.MaxY - itemBox.MinY) * scale < 0.6)
                continue;
            List<V2> pts = sh.Lods[i].For(scale);

            double th = thickness < 0 ? Math.Max(0.6, -thickness * scale) : thickness;
            if (fill is not null)
            {
                FlushRun();
                WM.StreamGeometry single = new();
                using (WM.StreamGeometryContext context = single.Open())
                    Figure(context, pts, true, closed);
                AddPath(single, brush, fill, th, dashed);
                continue;
            }

            (WM.Color, double, bool) key = (ColorOf(brush), Math.Round(th, 3), dashed);
            if (runGeometry is null || key != runKey)
            {
                FlushRun();
                runGeometry = new WM.StreamGeometry();
                runContext = runGeometry.Open();
                runKey = key;
                runBrush = brush;
            }

            Figure(runContext!, pts, false, closed);
        }

        FlushRun();

        // Estacas ao longo dos eixos.
        double stStep = new[] { 20.0, 100.0, 200.0, 500.0, 1000.0, 2000.0, 5000.0 }.First(v => v * scale >= 40 || v >= 5000);
        if (_showAxes)
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

        if (Show("reguas"))
            DrawRulers(step, dw, dh, x0, x1, y0, y1);
        if (Show("escala"))
        {
            DrawScaleBar(step, dh);
            DrawNorth();
        }

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
        int budget = Show("estacas") ? 400 : 0;
        V2 c0 = ToWorld(new SW.Point(RulerLeft, dh)), c1 = ToWorld(new SW.Point(_vW, 0));
        Box2 visible = new(Math.Min(c0.X, c1.X), Math.Min(c0.Y, c1.Y), Math.Max(c0.X, c1.X), Math.Max(c0.Y, c1.Y));
        for (int i = 0; i < _axes.Count; i++)
        {
            AxisInput a = _axes[i];
            // Eixo fora da tela: nem percorre as estacas.
            if (!a.Path.Bounds().Intersects(visible))
                continue;
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
            if (Show("pf") && !a.Path.Closed && len % stStep > 1e-6)
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

        if (Show("eixos"))
            DrawLabels();
    }

    /// <summary>Escala grafica cartografica: duas fileiras alternadas, primeiro trecho subdividido, valores nos tracos maiores.</summary>
    private void DrawScaleBar(double step, double dh)
    {
        double px = step * _vScale;
        if (px < 70 || px > _vW * 0.5)
            return;

        double major = NiceStep(step / 5.0);
        if (major >= step)
            major = step / 2.0;
        int majors = (int)Math.Round(step / major);
        int subs = 4;
        double x1 = _vW - 14, x0 = x1 - px, y = dh - 20, rowH = 3.5;
        WM.Brush ink = GraphicText;
        double Px(double v) => x0 + ((v / step) * px);

        void Block(double v0, double v1, double top, bool filled)
        {
            WS.Rectangle r = new()
            {
                Width = Math.Max(0.5, Px(v1) - Px(v0)), Height = rowH, Fill = filled ? ink : GraphicBack, Stroke = ink, StrokeThickness = 0.6
            };
            _graphic.Children.Add(r.At(Px(v0), top));
        }

        // Primeiro trecho subdividido; demais inteiros. Fileira de cima comeca cheia, a de baixo vazia.
        for (int k = 0; k < subs; k++)
        {
            double v0 = major * k / subs, v1 = major * (k + 1) / subs;
            Block(v0, v1, y, k % 2 == 0);
            Block(v0, v1, y + rowH, k % 2 != 0);
        }

        for (int m = 1; m < majors; m++)
        {
            double v0 = major * m, v1 = Math.Min(step, major * (m + 1));
            Block(v0, v1, y, m % 2 == 1);
            Block(v0, v1, y + rowH, m % 2 == 0);
        }

        // Valores: 0, cada traco maior e o fim com a unidade.
        for (int m = 0; m <= majors; m++)
        {
            double v = Math.Min(step, major * m);
            string text = m == majors ? N(v) + " m" : N(v);
            WC.TextBlock t = Mono(text, 9);
            t.Measure(new SW.Size(double.PositiveInfinity, double.PositiveInfinity));
            double tx = Px(v) - (m == majors ? t.DesiredSize.Width - 2 : m == 0 ? 2 : t.DesiredSize.Width / 2.0);
            WC.Canvas.SetLeft(t, tx);
            WC.Canvas.SetTop(t, y - 14);
            _graphic.Children.Add(t);
            _graphic.Children.Add(new WS.Line { X1 = Px(v), Y1 = y - 2, X2 = Px(v), Y2 = y, Stroke = ink, StrokeThickness = 1 });
        }
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

        double aL = s.EdgeOffset(EdgeLevel.Alinhamento, RoadSection.Esquerda);
        double aR = s.EdgeOffset(EdgeLevel.Alinhamento, RoadSection.Direita);
        double margin = Math.Max(1.5, (aL + aR) * 0.08);
        double k = (w - 60) / (aL + aR + (2 * margin)) * _zoom;
        double xMid = ((aR - aL) / 2.0) + _pan.X;
        // Roda e arrasto valem tambem aqui (so na horizontal): a vista guarda escala e centro.
        _vScale = k;
        _vCx = xMid;
        _vCy = 0;
        _vOx = w / 2.0;
        _vOy = h / 2.0;
        _vW = w;
        _vH = h;
        double X(double x) => (w / 2.0) + ((x - xMid) * k);
        double xVis0 = xMid - (w / 2.0 / k), xVis1 = xMid + (w / 2.0 / k);

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
        double tickStep = k >= 4 ? 1 : labStep;
        for (double x = Math.Ceiling(xVis0 / tickStep) * tickStep; x <= xVis1; x += tickStep)
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
            if (Show("titulo"))
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
            if (px < 44 || !Show("componentes"))
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
            if (xb - xa >= 18 && Show("cotas"))
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
            if (Show("cotas"))
                Txt("h " + N(s.MeioFio), x + (sg > 0 ? 5 : -5), road - 14, 8.5, dim, center: false, right: sg < 0);
        }

        if (Show("titulo"))
        {
            Txt($"SECAO TIPO  {s.Name.ToUpperInvariant()} [{s.Id}]   {s.Tipo.ToUpperInvariant()}   " +
                 $"{s.FaixasPorSentido * 2} FAIXAS x {N((s.Pista / 2.0) / s.FaixasPorSentido)} m   {N(s.Velocidade)} km/h   R ESQ. {N(s.Raio)} m   LARGURA {N(aL + aR)} m",
                8, 6, 9.5, GraphicText, center: false);
            Txt("LE", X(-aL - (margin * 0.6)), road - 7, 9, soft);
            Txt("LD", X(aR + (margin * 0.6)), road - 7, 9, soft);
        }

        _graphicInfo.Text = "Corte transversal, offsets em m (esquerda negativa); sem escala vertical   |   roda: zoom   arrastar: mover   duplo clique: enquadrar";
        _sectionView = true;
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

    private static WM.Color ColorOf(WM.Brush brush) => brush is WM.SolidColorBrush solid ? solid.Color : WM.Colors.Gray;

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
