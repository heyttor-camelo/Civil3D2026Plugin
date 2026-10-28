using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.Modules.Hydro.Calc;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Painel C3DHYDRO: apoio a Pipe Network no molde do painel viario. Barra de categorias com
/// contagem, barra de comandos por categoria, monitor (planta da rede com setas do fluxo ou
/// perfil do caminho), lista (planilha) e abas do item. Le o desenho sem LockDocument; escreve
/// so por comandos (SendStringToExecute) ou por HydroApply (uma transacao, um UNDO).
/// </summary>
internal sealed partial class HydroPanel : SW.Window
{
    private static HydroPanel? _instance;
    private const string Module = "Hydro";

    // ------------------------------------------------------------------
    // Tema
    // ------------------------------------------------------------------

    private sealed class Theme
    {
        public WM.Brush Back = null!, Pane = null!, Header = null!, Line = null!, Text = null!, Muted = null!,
            Input = null!, Accent = null!, Warn = null!, Selected = null!, Error = null!;
    }

    private static readonly Theme Light = new()
    {
        Back = Brush(240, 240, 240), Pane = Brush(255, 255, 255), Header = Brush(229, 229, 229), Line = Brush(200, 200, 200),
        Text = Brush(20, 20, 20), Muted = Brush(105, 105, 105), Input = Brush(255, 255, 240), Accent = Brush(0, 95, 170),
        Warn = Brush(190, 110, 0), Selected = Brush(204, 228, 247), Error = Brush(190, 30, 30)
    };

    private static readonly Theme Dark = new()
    {
        Back = Brush(45, 45, 48), Pane = Brush(37, 37, 38), Header = Brush(58, 58, 62), Line = Brush(70, 70, 74),
        Text = Brush(222, 222, 222), Muted = Brush(150, 150, 150), Input = Brush(30, 30, 30), Accent = Brush(80, 160, 255),
        Warn = Brush(230, 165, 20), Selected = Brush(38, 79, 120), Error = Brush(240, 80, 80)
    };

    private static readonly WM.FontFamily UiFont = new("Tahoma");
    private static readonly WM.FontFamily MonoFont = new("Consolas");

    private static bool _dark = LoadDarkPreference();
    private Theme T => _dark ? Dark : Light;

    // ------------------------------------------------------------------
    // Dados
    // ------------------------------------------------------------------

    private HydroSnapshot _snap = new();
    private string? _networkHandle;
    private NetworkInfo? Net => _snap.Find(_networkHandle) ?? _snap.Networks.FirstOrDefault();
    private HydroSettings _settings = new();
    private List<HydroViolation> _violations = new();
    private ILookup<string, HydroViolation> _violationsByHandle = Array.Empty<HydroViolation>().ToLookup(v => v.Handle);
    private Dictionary<string, HydroResult> _results = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Medidas da peca mostradas como colunas na lista de estruturas (nesta sessao).</summary>
    private readonly HashSet<string> _paramColumns = new(StringComparer.OrdinalIgnoreCase);
    private List<Row> _rows = new();
    private bool _refreshQueued;
    private int _refreshCount;

    private string _category = "REDES";
    private string? _selectedRow;
    private string _tab = "Propriedades";

    // ------------------------------------------------------------------
    // Controles
    // ------------------------------------------------------------------

    private WC.WrapPanel _categoryBar = new();
    private WC.StackPanel _commandBar = new();
    private HydroMonitorSurface _graphic = new();
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
    private readonly Dictionary<string, WC.TextBlock> _fieldLabels = new();
    private static readonly WM.Brush FieldBackLight = Brush(255, 255, 255), FieldBackDark = Brush(24, 24, 26);
    private static readonly WM.Brush FieldBorderLight = Brush(150, 150, 150), FieldBorderDark = Brush(100, 100, 106);

    private bool _showGrid = true;
    private bool _showNames = true;
    private bool _showArrows = true;
    private bool _showCatchments = true;
    private bool _profileView;

    private Action? _render;
    private Row? _current;

    // ------------------------------------------------------------------
    // Comandos da barra (por categoria)
    // ------------------------------------------------------------------

    private sealed record Cmd(string Key, string Category, string Send, string Title, string Group, string Description, string[] Steps);

    private static readonly Cmd[] Commands =
    {
        new("NOVAREDE", "REDES", "", "Nova rede", "Rede", "Cria uma Pipe Network vazia com a parts list e a superficie escolhidas (menu).", new[] { "O botao abre o menu com as parts lists do desenho." }),
        new("CATALOGO", "REDES", "HYCATALOGO ", "Catalogo CSBIM", "Rede", "Mostra os catalogos de pecas ativos, aponta para os CSBIM saneados (SETPIPENETWORKCATALOG), reindexa e importa familias/tamanhos para uma parts list por capitulo.", new[] { "Definir: dialogo nativo com os catalogos CSBIM.", "Regenerar: PARTCATALOGREGEN.", "Importar: escolha estruturas/tubos, a parts list e os capitulos." }),
        new("LANCAR", "REDES", "HYLANCAR ", "Lancar PV > tubo > PV", "Rede", "Lanca estruturas e tubos em cadeia, com previa, na rede ativa.", new[] { "Clique o primeiro PV.", "Clique os proximos: cada clique cria o PV e o tubo.", "Enter termina." }),
        new("IMPORTAR", "REDES", "DRENEXCEL ", "Importar planilha", "Rede", "Aplica o dimensionamento da planilha Excel (DRENEXCEL) na rede.", new[] { "Escolha o .xlsx e siga a linha de comando." }),
        new("NUMERAR", "REDES", "DRENNUM ", "Numerar pelo fluxo", "Rede", "Numera estruturas e tubos pela hierarquia hidraulica (DRENNUM).", new[] { "Siga a janela de previa." }),
        new("CALCULAR", "REDES", "HYCALCULAR ", "Calcular tudo", "Calculo", "Metodo racional acumulado e Manning em toda a rede ativa; grava vazao, HGL/EGL e o Property Set HYDRO.", new[] { "Confira a categoria Calculo antes (IDF, Tr, criterios)." }),
        new("LOTE", "ESTRUTURAS", "", "Editar em lote", "Edicao", "Fundo, tampa, familia/tamanho, nome: vale para as estruturas marcadas.", new[] { "Marque as linhas (caixinha ou Espaco).", "O botao abre o menu." }),
        new("TROCAR", "ESTRUTURAS", "HYTROCAR ", "Trocar tipo", "Edicao", "Troca familia/tamanho mantendo as conexoes.", new[] { "Clique as estruturas ou tubos.", "Escolha a familia e o tamanho." }),
        new("CONECTAR", "ESTRUTURAS", "HYCONECTAR ", "Conectar / desconectar", "Edicao", "Liga a ponta de um tubo a uma estrutura, ou desliga.", new[] { "Clique o tubo perto da ponta.", "Clique a estrutura (Enter desliga)." }),
        new("REGRAS", "ESTRUTURAS", "", "Aplicar regras", "Edicao", "Aplica o rule set do Civil nos itens marcados.", new[] { "Marque as linhas e clique." }),
        new("MEDIDAS", "ESTRUTURAS", "", "Atualizar regras das medidas", "Medidas", "Recalcula as regras das medidas (=regra nos campos: SIL, SIW, tampa, fundo...) das estruturas marcadas, ou de todas da rede, e refaz as que mudaram.", new[] { "Escreva =regra nas medidas (aba Propriedades) ou use HYREGRA no desenho.", "Marque as estruturas (nenhuma = todas) e clique." }),
        new("AUTO", "ESTRUTURAS", "", "Regras automaticas (liga/desliga)", "Medidas", "Liga/desliga o recalculo das regras no fim de cada comando, como o Auto do Viario. Desligado, as pecas so mudam no Atualizar regras.", new[] { "Clique para alternar; o estado aparece na barra de status." }),
        new("DENTRO", "ESTRUTURAS", "", "Tubos dentro da boca de lobo (liga/desliga)", "Medidas", "Nas pecas com o ponto de insercao fora do centro (bocas de lobo de calcada, base point na face da guia), leva a ponta do tubo para o centro da caixa no fim de cada comando. Ligado por padrao.", new[] { "Clique para alternar; vale para o desenho (fica salvo nele)." }),
        new("ZOOM", "ESTRUTURAS", "", "Zoom", "Ver", "Zoom no desenho para o item selecionado.", new[] { "Selecione na lista e clique (ou duplo clique)." }),
        new("SELECIONAR", "ESTRUTURAS", "", "Selecionar no desenho", "Ver", "Seleciona no desenho os itens marcados (ou o selecionado).", new[] { "Marque e clique." }),
        new("LOTE", "TUBOS", "", "Editar em lote", "Edicao", "Fundo de montante/jusante, declividade, familia/tamanho, nome: vale para os tubos marcados.", new[] { "Marque as linhas (caixinha ou Espaco).", "O botao abre o menu." }),
        new("TROCAR", "TUBOS", "HYTROCAR ", "Trocar tamanho", "Edicao", "Troca familia/tamanho do tubo mantendo as conexoes.", new[] { "Clique os tubos.", "Escolha a familia e o tamanho." }),
        new("INVERTER", "TUBOS", "", "Inverter sentido", "Edicao", "Inverte o sentido do fluxo dos tubos marcados.", new[] { "Marque e clique." }),
        new("CONECTAR", "TUBOS", "HYCONECTAR ", "Conectar / desconectar", "Edicao", "Liga a ponta de um tubo a uma estrutura, ou desliga.", new[] { "Clique o tubo perto da ponta.", "Clique a estrutura (Enter desliga)." }),
        new("REGRAS", "TUBOS", "", "Aplicar regras", "Edicao", "Aplica o rule set do Civil nos tubos marcados.", new[] { "Marque as linhas e clique." }),
        new("ZOOM", "TUBOS", "", "Zoom", "Ver", "Zoom no desenho para o item selecionado.", new[] { "Selecione na lista e clique." }),
        new("SELECIONAR", "TUBOS", "", "Selecionar no desenho", "Ver", "Seleciona no desenho os itens marcados (ou o selecionado).", new[] { "Marque e clique." }),
        new("NOVABACIA", "BACIAS", "HYBACIA ", "Nova bacia por polilinha", "Bacias", "Cria um catchment a partir de uma polilinha fechada.", new[] { "Clique a polilinha.", "Grupo, C e Tc (ou depois, nas propriedades)." }),
        new("LIGARBACIA", "BACIAS", "HYLIGARBACIA ", "Ligar a estrutura", "Bacias", "Liga a bacia a estrutura de engolimento.", new[] { "Clique a bacia.", "Clique a estrutura." }),
        new("TC", "BACIAS", "", "Calcular Tc", "Bacias", "Tempo de concentracao pelas formulas usuais (Kirpich, California, FAA...) nas bacias marcadas.", new[] { "Marque as bacias.", "O botao abre o menu com as formulas." }),
        new("ZOOM", "BACIAS", "", "Zoom", "Ver", "Zoom no desenho para a bacia.", new[] { "Selecione e clique." }),
        new("SELECIONAR", "BACIAS", "", "Selecionar no desenho", "Ver", "Seleciona as bacias marcadas no desenho.", new[] { "Marque e clique." }),
        new("CALCULAR", "CALCULO", "HYCALCULAR ", "Calcular", "Calculo", "Metodo racional acumulado e Manning em toda a rede ativa.", new[] { "Clique; o resultado aparece em Tubos, Estruturas e Verificacao." }),
        new("DIMENSIONAR", "CALCULO", "HYDIMENSIONAR ", "Dimensionar (menor diametro)", "Calculo", "Menor diametro da parts list que atende aos criterios, trecho a trecho; aplica se confirmar.", new[] { "Calcule antes.", "Confira a previa e confirme." }),
        new("MATRIZ", "CALCULO", "", "Matriz diametro x declividade", "Calculo", "Mostra, para o tubo selecionado, a lamina e a velocidade para cada diametro e declividade.", new[] { "Selecione um tubo em Tubos e abra a aba Hidraulica." }),
        new("VERIFICACAO", "VERIFICACAO", "HYVERIFICAR ", "Verificar", "Verificacao", "Confere lamina, velocidade, recobrimento, degrau e declividade com os criterios da categoria Calculo e as regras do Civil.", new[] { "Clique; as violacoes aparecem na lista." }),
        new("IRPARA", "VERIFICACAO", "", "Ir para", "Verificacao", "Zoom no item da violacao selecionada.", new[] { "Selecione e clique." }),
        new("CORRIGIR", "VERIFICACAO", "", "Corrigir", "Verificacao", "Correcoes sugeridas para a violacao (menu).", new[] { "Selecione a violacao.", "O botao abre o menu." }),
        new("PLANILHA", "SAIDAS", "", "Planilha de dimensionamento", "Saidas", "Mostra a planilha de dimensionamento (trechos, areas, Q, diametro, lamina, V).", new[] { "Calcule antes." }),
        new("EXPORTAR", "SAIDAS", "", "Exportar", "Saidas", "Exporta a planilha para XLSX ou HTML (menu).", new[] { "O botao abre o menu." }),
        new("ROTULOS", "SAIDAS", "", "Rotulos nativos", "Saidas", "Adiciona os rotulos de planta nativos do Civil nos tubos/estruturas da rede (menu).", new[] { "O botao abre o menu." }),
        new("TABELA", "SAIDAS", "HYTABELA ", "Tabela no desenho", "Saidas", "Insere no desenho uma tabela AutoCAD com a planilha de dimensionamento.", new[] { "Clique o ponto de insercao." })
    };

    private static readonly (string Id, string Icon, string Title)[] Categories =
    {
        ("REDES", "REDES", "Redes"),
        ("ESTRUTURAS", "ESTRUTURAS", "Estruturas"),
        ("TUBOS", "TUBOS", "Tubos"),
        ("BACIAS", "BACIAS", "Bacias"),
        ("CALCULO", "CALCULO", "Calculo"),
        ("VERIFICACAO", "VERIFICACAO", "Verificacao"),
        ("SAIDAS", "SAIDAS", "Saidas")
    };

    // ------------------------------------------------------------------
    // Abrir / atualizar
    // ------------------------------------------------------------------

    public static void ShowPanel(string? category = null)
    {
        if (_instance is null)
        {
            _instance = new HydroPanel();
            _instance.Closed += (_, _) =>
            {
                HighlightInDrawing(Array.Empty<string>());
                _instance = null;
                AcApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
                DetachAll();
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
            _instance._category = category.ToUpperInvariant();

        _instance.Attach(AcApp.DocumentManager.MdiActiveDocument);
        _instance.Refresh();
    }

    public static bool IsOpen => _instance is not null;

    /// <summary>Rede ativa no painel (handle), para os comandos que precisam dela.</summary>
    public static string? ActiveNetworkHandle => _instance?.Net?.Handle;

    public static void NotifyChanged()
    {
        HydroPanel? panel = _instance;
        if (panel is null || panel._refreshQueued)
            return;

        panel._refreshQueued = true;
        int seen = panel._refreshCount;
        panel.Dispatcher.BeginInvoke(new Action(() =>
        {
            panel._refreshQueued = false;
            if (_instance != panel || panel._refreshCount != seen)
                return;

            // Com comando ativo (ex. Network Layout Tools no meio de um lancamento) nao rele a rede:
            // espera o AutoCAD ficar livre.
            if (CadBusy())
            {
                if (!_refreshOnIdle)
                {
                    _refreshOnIdle = true;
                    AcApp.Idle += OnIdleRefresh;
                }

                return;
            }

            panel.Refresh();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static bool _refreshOnIdle;

    private static bool CadBusy()
    {
        try
        {
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            return doc is not null && (!string.IsNullOrEmpty(doc.CommandInProgress) || !doc.Editor.IsQuiescent);
        }
        catch
        {
            return false;
        }
    }

    private static void OnIdleRefresh(object? sender, EventArgs e)
    {
        if (CadBusy())
            return;
        AcApp.Idle -= OnIdleRefresh;
        _refreshOnIdle = false;
        NotifyChanged();
    }

    private static readonly HashSet<Document> Attached = new();

    private void Attach(Document? doc)
    {
        if (doc is null || !Attached.Add(doc))
            return;
        doc.CommandEnded += OnCommandEnded;
        doc.Database.ObjectModified += OnDbChanged;
        doc.Database.ObjectAppended += OnDbChanged;
        doc.Database.ObjectErased += OnDbErased;
        doc.BeginDocumentClose += (_, _) => Detach(doc);
    }

    /// <summary>
    /// Houve mudanca em objeto da rede (ou nos dados do HYDRO) desde a ultima leitura. Sem isso o
    /// painel relia todas as redes no fim de QUALQUER comando (zoom, pan, selecao...), o que
    /// travava em rede grande.
    /// </summary>
    private static bool _dirty = true;

    private static void OnDbChanged(object? sender, AcadDb.ObjectEventArgs e) => MarkDirty(e.DBObject);

    private static void OnDbErased(object? sender, AcadDb.ObjectErasedEventArgs e) => MarkDirty(e.DBObject);

    private static void MarkDirty(AcadDb.DBObject? obj)
    {
        if (_dirty || obj is null)
            return;
        if (obj is CivilDb.Part or CivilDb.Network or CivilDb.Catchment or AcadDb.Xrecord or AcadDb.DBDictionary)
            _dirty = true;
    }

    private static void Detach(Document doc)
    {
        if (!Attached.Remove(doc))
            return;
        try
        {
            doc.CommandEnded -= OnCommandEnded;
            doc.Database.ObjectModified -= OnDbChanged;
            doc.Database.ObjectAppended -= OnDbChanged;
            doc.Database.ObjectErased -= OnDbErased;
        }
        catch
        {
            // Documento em fechamento.
        }
    }

    private static void DetachAll()
    {
        foreach (Document d in Attached.ToList())
            Detach(d);
    }

    private static void OnCommandEnded(object? sender, CommandEventArgs e)
    {
        // Comandos de so leitura do proprio painel nao precisam reler.
        if (e.GlobalCommandName.StartsWith("C3DHYDRO", StringComparison.OrdinalIgnoreCase) || !_dirty)
            return;
        NotifyChanged();
    }

    private static void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
    {
        if (_instance is null)
            return;
        _instance.Attach(e.Document);
        _dirty = true;
        NotifyChanged();
    }

    private HydroPanel()
    {
        Civil3D2026Plugin.UI.Common.LogView.Atalho(this, Module);
        Title = "HYDRO - Pipe Network";
        Width = 1140;
        Height = 780;
        MinWidth = 800;
        MinHeight = 540;
        ShowInTaskbar = false;
        WindowStartupLocation = SW.WindowStartupLocation.CenterScreen;
        Icon = HydroIcons.Image("PAINEL");
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
        _commandBar = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        _graphic = new HydroMonitorSurface { Background = GraphicBack, ClipToBounds = true };
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
        commandRow.Children.Add(_commandBar);
        WC.Grid.SetRow(commandRow, 1);
        root.Children.Add(commandRow);

        WC.Border graphic = new() { BorderBrush = T.Line, BorderThickness = new SW.Thickness(1), Child = _graphic, Margin = new SW.Thickness(4, 0, 4, 0) };
        _graphic.SizeChanged += (_, _) => RequestRender();
        _graphic.MouseMove += OnGraphicMouseMove;
        _graphic.MouseWheel += OnGraphicWheel;
        _graphic.MouseDown += OnGraphicMouseDown;
        _graphic.MouseUp += OnGraphicMouseUp;
        _graphic.MouseLeave += (_, _) =>
        {
            if (_hover is null)
                return;
            _hover = null;
            RequestRender();
        };
        WC.Grid.SetRow(graphic, 2);
        root.Children.Add(graphic);

        WC.DockPanel graphicBar = new() { Margin = new SW.Thickness(4, 0, 4, 0), Background = T.Header, Height = 24 };
        WC.StackPanel toggles = new() { Orientation = WC.Orientation.Horizontal };
        toggles.Children.Add(Toggle("Grade", () => _showGrid, v => _showGrid = v));
        toggles.Children.Add(Toggle("Nomes", () => _showNames, v => _showNames = v));
        toggles.Children.Add(Toggle("Setas", () => _showArrows, v => _showArrows = v));
        toggles.Children.Add(Toggle("Bacias", () => _showCatchments, v => _showCatchments = v));
        toggles.Children.Add(Toggle("Perfil do caminho", () => _profileView, v => _profileView = v));
        WC.Button colorButton = new() { Content = "Cor \u25BE", Margin = new SW.Thickness(6, 1, 2, 1), Padding = new SW.Thickness(6, 0, 6, 0), ToolTip = "Colorir os tubos por lamina, velocidade, recobrimento ou declividade" };
        colorButton.Click += (_, _) => OpenColorMenu(colorButton);
        toggles.Children.Add(colorButton);
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
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1.3, SW.GridUnitType.Star), MinWidth = 300 });
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(5) });
        bottom.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star), MinWidth = 280 });
        _list.SelectionMode = WC.SelectionMode.Extended;
        _list.SelectionChanged += (_, _) => OnRowSelected();
        _list.MouseDoubleClick += (_, e) =>
        {
            if (!InCellEditor(e.OriginalSource))
                ZoomCurrent();
        };
        _list.PreviewKeyDown += (_, e) =>
        {
            // Ctrl+A seleciona todas as linhas mesmo com o cursor numa celula editavel.
            if (e.Key == WI.Key.A && WI.Keyboard.Modifiers == WI.ModifierKeys.Control && InCellEditor(e.OriginalSource))
            {
                _list.SelectAll();
                _list.Focus();
                e.Handled = true;
                return;
            }

            if (e.Key == WI.Key.Space && Checkable && _list.SelectedItems.Count > 0 && !InCellEditor(e.OriginalSource))
            {
                List<Row> sel = _list.SelectedItems.OfType<Row>().ToList();
                bool mark = sel.Any(r => !r.Checked);
                foreach (Row r in sel)
                    r.Checked = mark;
                OnChecksChanged();
                e.Handled = true;
            }
        };
        _list.PreviewMouseLeftButtonDown += OnListMouseDown;
        _list.MouseRightButtonUp += (_, e) =>
        {
            OpenListMenu();
            e.Handled = true;
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
        rightButtons.Children.Add(FooterButton("Log (Ctrl+Shift+L)", () => Civil3D2026Plugin.UI.Common.LogView.AbrirJanela(Module)));
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

        Civil3D2026Plugin.UI.Common.ToolStyles.Aplicar(Resources, _dark ? Civil3D2026Plugin.UI.Common.ToolTheme.Dark : Civil3D2026Plugin.UI.Common.ToolTheme.Light);
    }

    private void BuildCommandBar()
    {
        _commandBar.Children.Clear();
        bool first = true;
        foreach (IGrouping<string, Cmd> group in Commands.Where(c => c.Category == _category).GroupBy(c => c.Group))
        {
            if (!first)
                _commandBar.Children.Add(new WC.Border { Width = 1, Background = T.Line, Margin = new SW.Thickness(5, 3, 5, 3) });
            first = false;
            foreach (Cmd cmd in group)
            {
                bool menu = cmd.Send.Length == 0;
                WC.Button button = new()
                {
                    Padding = new SW.Thickness(2),
                    Background = WM.Brushes.Transparent,
                    BorderBrush = WM.Brushes.Transparent,
                    ToolTip = Tip(cmd)
                };
                WC.StackPanel content = new() { Orientation = WC.Orientation.Horizontal };
                content.Children.Add(new WC.Image { Source = HydroIcons.Image(cmd.Key), Width = 20, Height = 20 });
                if (menu)
                    content.Children.Add(new WC.TextBlock { Text = "▾", Foreground = T.Muted, FontSize = 9, VerticalAlignment = SW.VerticalAlignment.Bottom });
                button.Content = content;
                WC.ToolTipService.SetInitialShowDelay(button, 250);
                WC.ToolTipService.SetShowDuration(button, 30000);
                Cmd c = cmd;
                button.Click += (_, _) => RunCommand(c, button);
                _commandBar.Children.Add(button);
            }
        }
    }

    private static WC.ToolTip Tip(Cmd cmd)
    {
        WC.StackPanel panel = new() { MaxWidth = 380 };
        panel.Children.Add(new WC.TextBlock { Text = cmd.Title, FontWeight = SW.FontWeights.Bold });
        WC.DockPanel row = new() { Margin = new SW.Thickness(0, 4, 0, 0) };
        WC.Image image = new() { Source = HydroIcons.Image(cmd.Key), Width = 96, Height = 96, Margin = new SW.Thickness(0, 0, 8, 0), VerticalAlignment = SW.VerticalAlignment.Top };
        WC.DockPanel.SetDock(image, WC.Dock.Left);
        row.Children.Add(image);
        string command = cmd.Send.Trim().Split(' ')[0];
        row.Children.Add(new WC.TextBlock
        {
            Text = cmd.Description + "\n\n" + string.Join("\n", cmd.Steps.Select((s, i) => $"{i + 1}. {s}")) + (command.Length > 0 ? $"\n\nComando: {command}" : ""),
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
        WC.ContextMenu menu = new() { PlacementTarget = this };
        WC.MenuItem dark = new() { Header = "Tema escuro", IsCheckable = true, IsChecked = _dark };
        dark.Click += (_, _) => SetTheme(dark.IsChecked);
        menu.Items.Add(dark);
        menu.IsOpen = true;
    }

    private void SetTheme(bool dark)
    {
        _dark = dark;
        SaveDarkPreference(dark);
        Content = BuildUi();
        Refresh();
    }

    private static string PreferencesPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Civil3D2026Plugin", "hydro_painel.txt");

    private static bool LoadDarkPreference()
    {
        try
        {
            string path = PreferencesPath();
            if (System.IO.File.Exists(path))
                return System.IO.File.ReadAllText(path).Contains("tema=escuro", StringComparison.OrdinalIgnoreCase);
            return Civil3D2026Plugin.UI.Common.ToolTheme.Current.IsDarkTheme;
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
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PreferencesPath())!);
            System.IO.File.WriteAllText(PreferencesPath(), "tema=" + (dark ? "escuro" : "claro"));
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
        _refreshCount++;
        _dirty = false;
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            Say("Nenhum desenho aberto.");
            return;
        }

        try
        {
            using (PluginLog.Medir(Module, "Leitura da rede para o painel"))
            {
                _snap = HydroReader.Read(doc.Database);
                _settings = HydroStore.Load(doc.Database);
                _results = HydroStore.LoadResults(doc.Database);
            }

            if (Net is NetworkInfo n)
                _violations = HydroChecker.Check(n, _snap, _settings, _results);
            else
                _violations = new List<HydroViolation>();
            _violationsByHandle = _violations.ToLookup(v => v.Handle, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            PluginLog.Erro(Module, "Erro ao ler a rede para o painel", ex);
            Say("Erro ao ler a rede: " + ex.Message);
            return;
        }

        Title = $"HYDRO - Pipe Network [{System.IO.Path.GetFileName(doc.Name)}]";
        FillCategories();
        BuildCommandBar();
        FillList();
        HydroPart3DWindow.Refreshed(h =>
        {
            foreach (NetworkInfo n in _snap.Networks)
            {
                if (n.StructureByHandle.TryGetValue(h, out StructureInfo? x))
                    return (x, n);
            }

            return (null, null);
        });
    }

    private int CountFor(string category) => category switch
    {
        "REDES" => _snap.Networks.Count,
        "ESTRUTURAS" => Net?.Structures.Count ?? 0,
        "TUBOS" => Net?.Pipes.Count ?? 0,
        "BACIAS" => _snap.Catchments.Count,
        "VERIFICACAO" => _violations.Count,
        _ => -1
    };

    private void FillCategories()
    {
        _categoryBar.Children.Clear();
        foreach ((string id, string icon, string title) in Categories)
        {
            int count = CountFor(id);
            int warn = id switch
            {
                "VERIFICACAO" => _violations.Count(v => v.Severity == HydroSeverity.Erro),
                "ESTRUTURAS" => Net?.Structures.Count(s => s.Warnings.Count > 0) ?? 0,
                "TUBOS" => Net?.Pipes.Count(p => p.Warnings.Count > 0) ?? 0,
                _ => 0
            };
            WC.StackPanel content = new() { Orientation = WC.Orientation.Horizontal };
            content.Children.Add(new WC.Image { Source = HydroIcons.Image(icon), Width = 18, Height = 18 });
            content.Children.Add(new WC.TextBlock { Text = " " + title, Foreground = T.Text, VerticalAlignment = SW.VerticalAlignment.Center });
            if (count >= 0)
            {
                content.Children.Add(new WC.TextBlock
                {
                    Text = $" ({count})",
                    Foreground = warn > 0 ? T.Warn : T.Muted,
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
        _pan = new P2(0, 0);
        FillCategories();
        BuildCommandBar();
        FillList();
    }

    /// <summary>Se o evento veio de dentro de uma celula editavel (TextBox) da lista.</summary>
    private static bool InCellEditor(object? source)
    {
        SW.DependencyObject? d = source as SW.DependencyObject;
        while (d is not null)
        {
            if (d is WC.TextBox)
                return true;
            if (d is WC.ListViewItem)
                return false;
            d = d is SW.Media.Visual or SW.Media.Media3D.Visual3D ? WM.VisualTreeHelper.GetParent(d) : SW.LogicalTreeHelper.GetParent(d);
        }

        return false;
    }

    private bool Checkable => _category is "ESTRUTURAS" or "TUBOS" or "BACIAS";

    // ------------------------------------------------------------------
    // Lista
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

    private static string N(double v, int decimals = 2) => double.IsNaN(v) ? "-" : v.ToString("F" + decimals, CultureInfo.InvariantCulture);

    private static string Pct(double slope) => double.IsNaN(slope) ? "-" : (slope * 100).ToString("0.00", CultureInfo.InvariantCulture) + " %";

    private static string Mm(double m) => double.IsNaN(m) ? "-" : Math.Round(m * 1000).ToString(CultureInfo.InvariantCulture);

    private HydroResult? ResultOf(string handle) => _results.GetValueOrDefault(handle);

    /// <summary>Soma das bacias a montante da estrutura (inclusive as dela), sem precisar do calculo.</summary>
    private double UpstreamAreaHa(NetworkInfo net, string? structureHandle)
    {
        if (structureHandle is null)
            return 0;
        HashSet<string> up = net.Upstream(structureHandle);
        return _snap.Catchments.Where(c => up.Contains(c.StructureHandle)).Sum(c => c.AreaHa);
    }

    private (string[] Columns, double[] Widths, List<Row> Rows) BuildRows(string category)
    {
        List<Row> rows = new();
        NetworkInfo? net = Net;
        switch (category)
        {
            case "REDES":
                foreach (NetworkInfo n in _snap.Networks)
                {
                    rows.Add(new Row
                    {
                        Id = "REDE:" + n.Handle, Icon = "REDES", Data = n,
                        Cells = new[]
                        {
                            n.Name + (n.Handle == net?.Handle ? "  (ativa)" : ""), n.Structures.Count.ToString(CultureInfo.InvariantCulture),
                            n.Pipes.Count.ToString(CultureInfo.InvariantCulture), N(n.Length2D, 1), n.PartsList, n.ReferenceSurface,
                            n.Pipes.Count(p => !p.Defined) > 0 ? $"{n.Pipes.Count(p => !p.Defined)} tubo(s) sem sentido" : "ok"
                        }
                    });
                }

                return (new[] { "Rede", "Estruturas", "Tubos", "Extensao [m]", "Parts list", "Superficie", "Status" }, new double[] { 200, 80, 60, 90, 160, 140, 160 }, rows);

            case "ESTRUTURAS":
                if (net is null)
                    break;
                foreach (StructureInfo s in net.Ordered())
                {
                    HydroResult? r = ResultOf(s.Handle);
                    rows.Add(new Row
                    {
                        Id = "EST:" + s.Handle, Icon = "ESTRUTURAS", Data = s,
                        Cells = new[]
                        {
                            s.Name, s.Family, s.Size, N(s.Rim, 3), N(s.Sump, 3), N(s.Depth, 2),
                            $"{s.PipesIn.Count}>{s.PipesOut.Count}" + (s.PipesUndefined.Count > 0 ? "?" : ""),
                            s.Catchments.Count > 0 ? string.Join(", ", s.Catchments) : "-",
                            N(r?.AreaHa ?? UpstreamAreaHa(net, s.Handle), 3), r is null ? "-" : N(r.Flow, 4),
                            string.Join(" | ", s.Warnings.Concat(_violationsByHandle[s.Handle].Select(v => v.Text)))
                        }
                    });
                }

                {
                    // Colunas escolhidas das medidas da peca (lista > botao direito > Colunas da peca), antes de Avisos.
                    string[] extra = _paramColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray();
                    for (int r = 0; r < rows.Count; r++)
                    {
                        Row row = rows[r];
                        StructureInfo s = (StructureInfo)row.Data!;
                        List<string> cells = row.Cells.ToList();
                        List<string> add = extra.Select(c => s.Parameters.FirstOrDefault(x => x.Name.Equals(c, StringComparison.OrdinalIgnoreCase)) is PartParam pp
                            ? N(pp.Meters, 3) : "-").ToList();
                        add.Add(s.Rules.Count == 0 ? "-" : (s.OwnRules.Template.Length > 0 ? s.OwnRules.Template + " " : "") + $"({s.Rules.Count})");
                        cells.InsertRange(cells.Count - 1, add);
                        rows[r] = new Row { Id = row.Id, Icon = row.Icon, Data = row.Data, Cells = cells.ToArray() };
                    }

                    List<string> cols = new() { "Estrutura", "Familia", "Tamanho", "Tampa", "Fundo", "Prof. [m]", "Tubos", "Bacias", "A acum. [ha]", "Q [m3/s]" };
                    List<double> widths = new() { 110, 130, 100, 75, 75, 65, 50, 110, 80, 75 };
                    cols.AddRange(extra.Select(c => c + " [m]"));
                    widths.AddRange(extra.Select(_ => 70.0));
                    cols.Add("Regras");
                    widths.Add(90);
                    cols.Add("Avisos");
                    widths.Add(260);
                    return (cols.ToArray(), widths.ToArray(), rows);
                }

            case "TUBOS":
                if (net is null)
                    break;
                foreach (PipeInfo p in net.OrderedPipes())
                {
                    HydroResult? r = ResultOf(p.Handle);
                    string from = p.FromHandle is string f && net.StructureByHandle.TryGetValue(f, out StructureInfo? fs) ? fs.Name : "?";
                    string to = p.ToHandle is string t && net.StructureByHandle.TryGetValue(t, out StructureInfo? ts) ? ts.Name : "?";
                    rows.Add(new Row
                    {
                        Id = "TUBO:" + p.Handle, Icon = "TUBOS", Data = p,
                        Cells = new[]
                        {
                            p.Name, from + " > " + to, p.Family, Mm(p.InnerDiameter), N(p.Length2D, 2), Pct(p.Slope),
                            N(p.UpInvert, 3), N(p.DownInvert, 3), N(p.MinCoverActual, 2),
                            N(r?.AreaHa ?? UpstreamAreaHa(net, p.FromHandle), 3),
                            r is null ? N(p.FlowRate, 4) : N(r.Flow, 4), r is null ? "-" : N(r.DepthRatio * 100, 0) + " %", r is null ? "-" : N(r.Velocity, 2),
                            string.Join(" | ", p.Warnings.Concat(_violationsByHandle[p.Handle].Select(v => v.Text)))
                        }
                    });
                }

                return (new[] { "Tubo", "De > para", "Familia", "D [mm]", "L [m]", "i", "Fundo mont.", "Fundo jus.", "Recob. min [m]", "A acum. [ha]", "Q [m3/s]", "y/D", "V [m/s]", "Avisos" },
                    new double[] { 90, 130, 120, 55, 65, 65, 80, 80, 80, 80, 75, 50, 60, 240 }, rows);

            case "BACIAS":
                foreach (CatchmentInfo c in _snap.Catchments.OrderBy(c => c.Group, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    rows.Add(new Row
                    {
                        Id = "BACIA:" + c.Handle, Icon = "BACIAS", Data = c,
                        Cells = new[] { c.Name, c.Group, N(c.AreaHa, 4), N(c.C, 2), N(c.Tc, 1), c.StructureName.Length > 0 ? c.StructureName : "-", c.NetworkName }
                    });
                }

                return (new[] { "Bacia", "Grupo", "Area [ha]", "C", "Tc [min]", "Estrutura", "Rede" }, new double[] { 140, 120, 80, 50, 70, 120, 140 }, rows);

            case "CALCULO":
                foreach ((string key, string label, string value) in HydroStore.Describe(_settings))
                    rows.Add(new Row { Id = "CFG:" + key, Icon = key.StartsWith("idf", StringComparison.Ordinal) ? "IDF" : "CONFIG", Data = key, Cells = new[] { label, value } });
                return (new[] { "Parametro", "Valor" }, new double[] { 300, 260 }, rows);

            case "VERIFICACAO":
                foreach (HydroViolation v in _violations)
                {
                    rows.Add(new Row
                    {
                        Id = "VIOL:" + v.Handle + ":" + v.Rule, Icon = v.Severity == HydroSeverity.Erro ? "ERRO" : "VERIFICACAO", Data = v,
                        Cells = new[] { v.Item, v.Rule, v.Value, v.Limit, v.Severity.ToString(), v.Text }
                    });
                }

                return (new[] { "Item", "Regra", "Valor", "Limite", "Severidade", "Descricao" }, new double[] { 110, 150, 80, 80, 80, 300 }, rows);

            case "SAIDAS":
                if (net is not null)
                {
                    foreach (HydroSizingRow r in HydroSizing.Rows(net, _snap, _results, _settings))
                    {
                        rows.Add(new Row
                        {
                            Id = "DIM:" + r.PipeHandle, Icon = "PLANILHA", Data = r,
                            Cells = new[] { r.Trecho, N(r.AreaLocalHa, 4), N(r.AreaAcumHa, 4), N(r.C, 2), N(r.Tc, 1), N(r.Intensity, 1), N(r.Flow, 4), N(r.Length, 2), Pct(r.Slope), Mm(r.Diameter), N(r.DepthRatio * 100, 0) + " %", N(r.Velocity, 2), Mm(r.MinDiameter) }
                        });
                    }
                }

                return (new[] { "Trecho", "A local [ha]", "A acum. [ha]", "C", "Tc [min]", "i [mm/h]", "Q [m3/s]", "L [m]", "Decliv.", "D [mm]", "y/D", "V [m/s]", "D min [mm]" },
                    new double[] { 130, 80, 80, 45, 65, 70, 75, 65, 65, 60, 50, 60, 70 }, rows);
        }

        return (new[] { "Item" }, new double[] { 300 }, rows);
    }

    private void FillList()
    {
        (string[] columns, double[] widths, List<Row> rows) = BuildRows(_category);
        string find = _find.Text.Trim();
        if (find.Length > 0)
            rows = rows.Where(r => r.Cells.Any(c => c.Contains(find, StringComparison.OrdinalIgnoreCase))).ToList();

        foreach (Row r in rows)
        {
            string warn = r.Cells.Length > 0 ? r.Cells[^1] : "";
            bool hasViolation = r.Data is StructureInfo s ? _violationsByHandle[s.Handle].Any(v => v.Severity == HydroSeverity.Erro)
                : r.Data is PipeInfo p && _violationsByHandle[p.Handle].Any(v => v.Severity == HydroSeverity.Erro);
            if (r.Data is HydroViolation hv)
            {
                r.Flag = hv.Severity == HydroSeverity.Erro ? "✖" : "⚠";
                r.FlagBrush = hv.Severity == HydroSeverity.Erro ? T.Error : T.Warn;
            }
            else if (hasViolation)
            {
                r.Flag = "✖";
                r.FlagBrush = T.Error;
            }
            else if ((r.Data is StructureInfo or PipeInfo) && warn.Length > 0)
            {
                r.Flag = "⚠";
                r.FlagBrush = T.Warn;
            }
        }

        if (Checkable)
        {
            _checked.IntersectWith(rows.Select(r => r.Id));
            foreach (Row r in rows)
                r.Checked = _checked.Contains(r.Id);
        }

        _rows = rows;
        _columns = columns;
        Func<int, bool> editable = i => i < columns.Length && EditableColumn(_category, columns[i]);
        _list.View = Checkable
            ? ListViewFor(columns, widths, rows, OnChecksChanged, all => { foreach (Row r in _rows) r.Checked = all; OnChecksChanged(); }, rows.Count > 0 && rows.All(r => r.Checked), editable, CommitCell)
            : ListViewFor(columns, widths, rows, editable: editable, commit: CommitCell);
        _list.ItemsSource = rows;

        Row? keep = rows.FirstOrDefault(r => r.Id == _selectedRow) ?? rows.FirstOrDefault();
        _list.SelectedItem = keep;
        if (keep is not null)
            _list.ScrollIntoView(keep);
        OnRowSelected();

        NetworkInfo? net = Net;
        int errs = _violations.Count(v => v.Severity == HydroSeverity.Erro);
        string catalogWarn = net is null ? "" : CatalogNote(net);
        if (catalogWarn.Length > 0)
            catalogWarn = "CATALOGO: " + catalogWarn + " | ";
        Say(net is null
            ? $"Nenhuma Pipe Network no desenho | {_snap.Catchments.Count} bacia(s) | v{C3DVersions.Hydro}"
            : catalogWarn + $"Rede ativa: {net.Name} | {net.Structures.Count} estrutura(s), {net.Pipes.Count} tubo(s), {N(net.Length2D, 0)} m | " +
              $"{_snap.Catchments.Count} bacia(s) | Verificacao: {errs} erro(s), {_violations.Count - errs} aviso(s) | " +
              $"{(_checked.Count > 0 ? _checked.Count + " marcado(s) | " : "")}v{C3DVersions.Hydro}");
    }

    private string[] _columns = Array.Empty<string>();

    /// <summary>Colunas da lista que aceitam edicao direto na celula.</summary>
    private bool EditableColumn(string category, string column) => category switch
    {
        "ESTRUTURAS" => column is "Estrutura" or "Tampa" or "Fundo" or "Prof. [m]" ||
                        (column.EndsWith(" [m]", StringComparison.Ordinal) && _paramColumns.Contains(column[..^4])),
        "TUBOS" => column is "Tubo" or "D [mm]" or "i" or "Fundo mont." or "Fundo jus.",
        "BACIAS" => column is "C" or "Tc [min]",
        "CALCULO" => column == "Valor",
        _ => false
    };

    /// <summary>
    /// Grava a celula editada. Vale para a linha, ou para todas as marcadas/selecionadas quando a
    /// linha faz parte delas (edicao em lote direto na lista). Mesmas regras da aba Propriedades.
    /// </summary>
    private void CommitCell(Row row, int col, string text)
    {
        if (col >= _columns.Length)
            return;
        string column = _columns[col];
        List<Row> targets = BatchTargets(row);
        _selectedRow = row.Id;
        switch (row.Data)
        {
            case StructureInfo s:
            {
                List<StructureInfo> list = targets.Select(r => (StructureInfo)r.Data!).ToList();
                if (column == "Estrutura")
                {
                    ApplyStructureEdits(new[] { s }, new List<(string, string)>(), text, "", false);
                    return;
                }

                string target = column switch
                {
                    "Tampa" => HydroRules.Rim,
                    "Fundo" => HydroRules.Sump,
                    "Prof. [m]" => DepthTarget,
                    _ => column[..^4]
                };
                ApplyStructureEdits(list, new List<(string, string)> { (target, text) }, "", "", list.Count > 1);
                return;
            }

            case PipeInfo p:
            {
                List<PipeInfo> list = targets.Select(r => (PipeInfo)r.Data!).ToList();
                if (column == "Tubo")
                {
                    RunApply("Renomear " + p.Name, db => HydroEditor.EditPipe(db, p.Handle, text, null, null, null));
                    return;
                }

                string t = text.Replace("%", "").Trim();
                RunApply(list.Count > 1 ? $"{column} em {list.Count} tubo(s)" : $"{column} de {p.Name}", db =>
                {
                    foreach (PipeInfo x in list)
                    {
                        switch (column)
                        {
                            case "D [mm]":
                                if (Relative(t, x.InnerDiameter * 1000) is double mm && mm > 0)
                                    HydroEditor.ResizePipe(db, x.Handle, mm / 1000);
                                break;
                            case "i":
                                if (Relative(t, x.Slope * 100) is double pct)
                                    HydroEditor.EditPipe(db, x.Handle, null, null, null, pct / 100);
                                break;
                            case "Fundo mont.":
                                if (Relative(t, x.UpInvert) is double up)
                                    HydroEditor.EditPipe(db, x.Handle, null, up, null, null);
                                break;
                            case "Fundo jus.":
                                if (Relative(t, x.DownInvert) is double down)
                                    HydroEditor.EditPipe(db, x.Handle, null, null, down, null);
                                break;
                        }
                    }
                });
                return;
            }

            case CatchmentInfo c:
            {
                List<CatchmentInfo> list = targets.Select(r => (CatchmentInfo)r.Data!).ToList();
                RunApply(list.Count > 1 ? $"{column} em {list.Count} bacia(s)" : $"{column} de {c.Name}", db =>
                {
                    foreach (CatchmentInfo x in list)
                    {
                        if (column == "C" && Relative(text, x.C) is double cc && cc >= 0)
                            HydroEditor.EditCatchment(db, x.Handle, cc, null, null);
                        else if (column == "Tc [min]" && Relative(text, x.Tc) is double tc && tc >= 0)
                            HydroEditor.EditCatchment(db, x.Handle, null, tc, null);
                    }
                });
                return;
            }

            case string key when _category == "CALCULO":
            {
                Dictionary<string, string> values = HydroStore.Describe(_settings).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                values[key] = text;
                HydroSettings settings = HydroStore.FromFields(_settings, k => values.GetValueOrDefault(k, ""));
                RunApply("Parametro " + key, db => HydroStore.Save(db, settings));
                return;
            }
        }
    }

    private void OnChecksChanged()
    {
        _checked.Clear();
        foreach (Row r in _rows.Where(r => r.Checked))
            _checked.Add(r.Id);
        BuildTabs();
        FillCategoriesStatus();
    }

    private void FillCategoriesStatus()
    {
        string text = _status.Text;
        int i = text.IndexOf("| v", StringComparison.Ordinal);
        if (i > 0)
            _status.Text = text[..i] + (_checked.Count > 0 ? $"| {_checked.Count} marcado(s) " : "") + text[i..];
    }

    /// <summary>Itens alvo das acoes em lote: marcados, senao os selecionados.</summary>
    private List<Row> Targets()
    {
        List<Row> marked = _rows.Where(r => r.Checked).ToList();
        if (marked.Count > 0)
            return marked;
        return _list.SelectedItems.OfType<Row>().ToList();
    }

    /// <summary>
    /// Lote da edicao a partir de uma linha (aba Propriedades e celulas da lista), do mesmo tipo
    /// dela: os marcados se ela esta marcada, senao os selecionados se ela esta na selecao
    /// multipla; fora disso so ela.
    /// </summary>
    private List<Row> BatchTargets(Row row)
    {
        Type? type = row.Data?.GetType();
        List<Row> marked = _rows.Where(r => r.Checked && r.Data?.GetType() == type).ToList();
        if (marked.Count > 1 && marked.Contains(row))
            return marked;
        List<Row> selected = _list.SelectedItems.OfType<Row>().Where(r => r.Data?.GetType() == type).ToList();
        if (selected.Count > 1 && selected.Contains(row))
            return selected;
        return new List<Row> { row };
    }

    private void SayBatch(int count, string what) =>
        Say($"EDICAO EM LOTE: {count} {what} - o que for gravado (aba ou celula da lista) vale para todos. Ctrl/Shift+clique soma, Ctrl+A todos, Espaco marca.");

    private Row? _anchor;

    /// <summary>
    /// Ctrl/Shift+clique numa celula editavel: a caixa de texto engoliria o clique, entao a
    /// selecao e estendida aqui (Ctrl soma/tira a linha, Shift seleciona do ultimo clique ate ela).
    /// </summary>
    private void OnListMouseDown(object sender, WI.MouseButtonEventArgs e)
    {
        SW.DependencyObject? up = e.OriginalSource as SW.DependencyObject;
        while (up is not null and not WC.ListViewItem)
            up = up is WM.Visual ? WM.VisualTreeHelper.GetParent(up) : SW.LogicalTreeHelper.GetParent(up);
        if (up is not WC.ListViewItem item || item.DataContext is not Row row)
            return;
        WI.ModifierKeys mods = WI.Keyboard.Modifiers;
        bool ctrl = (mods & WI.ModifierKeys.Control) != 0, shift = (mods & WI.ModifierKeys.Shift) != 0;
        if (!ctrl && !shift)
        {
            _anchor = row;
            return;
        }

        if (!InCellEditor(e.OriginalSource))
        {
            if (!shift)
                _anchor = row;
            return;
        }

        e.Handled = true;
        if (shift && _anchor is not null && _list.Items.IndexOf(_anchor) is int a and >= 0)
        {
            int b = _list.Items.IndexOf(row);
            if (!ctrl)
                _list.SelectedItems.Clear();
            for (int k = Math.Min(a, b); k <= Math.Max(a, b); k++)
            {
                if (!_list.SelectedItems.Contains(_list.Items[k]))
                    _list.SelectedItems.Add(_list.Items[k]);
            }
        }
        else
        {
            item.IsSelected = !item.IsSelected;
            _anchor = row;
        }

        _list.Focus();
    }

    private static readonly System.Text.RegularExpressions.Regex NumberLike =
        new(@"^[-+]?\d+([.,]\d+)?( ?(m|m2|m3/s|m/s|mm|ha|min|%|mm/h))?$", System.Text.RegularExpressions.RegexOptions.Compiled);

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

    private static WC.GridView ListViewFor(string[] columns, double[] widths, List<Row>? rows = null,
        Action? checkChanged = null, Action<bool>? checkAll = null, bool allChecked = false,
        Func<int, bool>? editable = null, Action<Row, int, string>? commit = null)
    {
        WC.GridView view = new();

        if (checkChanged is not null)
        {
            SW.FrameworkElementFactory box = new(typeof(WC.CheckBox));
            box.SetBinding(WC.Primitives.ToggleButton.IsCheckedProperty, new WD.Binding("Checked") { Mode = WD.BindingMode.TwoWay, UpdateSourceTrigger = WD.UpdateSourceTrigger.PropertyChanged });
            box.SetValue(SW.FrameworkElement.HorizontalAlignmentProperty, SW.HorizontalAlignment.Center);
            box.SetValue(SW.FrameworkElement.ToolTipProperty, "Marcar: as acoes em lote valem para os itens marcados (Espaco marca os selecionados).");
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
                view.Columns.Add(new WC.GridViewColumn { Header = columns[0], Width = widths[0], CellTemplate = new SW.DataTemplate { VisualTree = panel } });
                continue;
            }

            bool numeric = rows is not null && IsNumeric(rows, i);
            bool canEdit = commit is not null && editable?.Invoke(i) == true;
            SW.FrameworkElementFactory cell = canEdit ? EditableCell(i, numeric, commit!) : new(typeof(WC.TextBlock));
            if (!canEdit)
            {
                cell.SetBinding(WC.TextBlock.TextProperty, new WD.Binding($"Cells[{i}]"));
                if (numeric)
                {
                    cell.SetValue(WC.TextBlock.TextAlignmentProperty, SW.TextAlignment.Right);
                    cell.SetValue(WC.TextBlock.FontFamilyProperty, MonoFont);
                    cell.SetValue(SW.FrameworkElement.MarginProperty, new SW.Thickness(0, 0, 6, 0));
                }
            }

            WC.GridViewColumnHeader header = new()
            {
                Content = canEdit ? "✎ " + columns[i] : columns[i],
                ToolTip = canEdit ? "Coluna editavel: clique na celula, digite e Enter (Esc desfaz). Aceita valor, +0,10 / -0,30 e =regra nas medidas.\nSe a linha estiver marcada (ou selecionada junto com outras), grava em todas." : null
            };
            if (numeric)
                header.HorizontalContentAlignment = SW.HorizontalAlignment.Right;
            view.Columns.Add(new WC.GridViewColumn { Header = header, Width = i < widths.Length ? widths[i] : 80, CellTemplate = new SW.DataTemplate { VisualTree = cell } });
        }

        return view;
    }

    /// <summary>
    /// Celula editavel da lista: TextBox sem borda que parece texto. Enter (ou sair da celula)
    /// grava se mudou; Esc volta ao valor. Ao entrar, a linha vira a selecionada.
    /// </summary>
    private static SW.FrameworkElementFactory EditableCell(int column, bool numeric, Action<Row, int, string> commit)
    {
        SW.FrameworkElementFactory box = new(typeof(WC.TextBox));
        box.SetBinding(WC.TextBox.TextProperty, new WD.Binding($"Cells[{column}]") { Mode = WD.BindingMode.OneWay });
        box.SetValue(WC.Control.BorderThicknessProperty, new SW.Thickness(0, 0, 0, 1));
        box.SetValue(WC.Control.BorderBrushProperty, (_dark ? Dark : Light).Line);
        box.SetValue(WC.Control.BackgroundProperty, WM.Brushes.Transparent);
        box.SetValue(WC.Control.ForegroundProperty, (_dark ? Dark : Light).Text);
        box.SetValue(WC.TextBox.CaretBrushProperty, (_dark ? Dark : Light).Text);
        box.SetValue(WC.Control.PaddingProperty, new SW.Thickness(0));
        box.SetValue(SW.FrameworkElement.TagProperty, column);
        if (numeric)
        {
            box.SetValue(WC.TextBox.TextAlignmentProperty, SW.TextAlignment.Right);
            box.SetValue(WC.Control.FontFamilyProperty, MonoFont);
            box.SetValue(SW.FrameworkElement.MarginProperty, new SW.Thickness(0, 0, 6, 0));
        }

        void Commit(WC.TextBox b)
        {
            if (b.DataContext is not Row row || b.Tag is not int col || col >= row.Cells.Length)
                return;
            string text = b.Text.Trim();
            if (text == row.Cells[col].Trim() || text.Length == 0 || b.Uid == "done")
                return;
            b.Uid = "done";
            commit(row, col, text);
        }

        box.AddHandler(SW.UIElement.KeyDownEvent, new WI.KeyEventHandler((o, e) =>
        {
            if (o is not WC.TextBox b)
                return;
            if (e.Key == WI.Key.Enter)
            {
                Commit(b);
                e.Handled = true;
            }
            else if (e.Key == WI.Key.Escape && b.DataContext is Row row && b.Tag is int col && col < row.Cells.Length)
            {
                b.Text = row.Cells[col];
                b.SelectAll();
                e.Handled = true;
            }
        }));
        box.AddHandler(SW.UIElement.LostKeyboardFocusEvent, new WI.KeyboardFocusChangedEventHandler((o, _) =>
        {
            if (o is WC.TextBox b)
                Commit(b);
        }));
        box.AddHandler(SW.UIElement.GotKeyboardFocusEvent, new WI.KeyboardFocusChangedEventHandler((o, _) =>
        {
            if (o is not WC.TextBox b)
                return;
            b.Uid = "";
            b.SelectAll();
            // Com Ctrl/Shift a linha entra na selecao (OnListMouseDown); nao zera a selecao multipla.
            if ((WI.Keyboard.Modifiers & (WI.ModifierKeys.Control | WI.ModifierKeys.Shift)) != 0)
                return;
            SW.DependencyObject? up = b;
            while (up is not null and not WC.ListViewItem)
                up = WM.VisualTreeHelper.GetParent(up);
            if (up is WC.ListViewItem item && !item.IsSelected && WC.ItemsControl.ItemsControlFromItemContainer(item) is WC.ListView lv)
                lv.SelectedItem = item.DataContext;
        }));
        return box;
    }

    private sealed class IconConverter : WD.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            HydroIcons.Image(value as string ?? "PAINEL");

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => WD.Binding.DoNothing;
    }

    private void OnRowSelected()
    {
        _current = _list.SelectedItem as Row;
        _selectedRow = _current?.Id;
        BuildTabs();
        HighlightSelection();
    }

    // ------------------------------------------------------------------
    // Abas
    // ------------------------------------------------------------------

    private void BuildTabs()
    {
        List<(string Key, string Name, Func<SW.UIElement> Build)> tabs = new() { ("Propriedades", "Propriedades", BuildProperties) };
        Row? row = _current;
        NetworkInfo? net = Net;

        switch (row?.Data)
        {
            case NetworkInfo n:
                tabs.Add(("Pecas", "Lista de pecas", () => PartsView(n)));
                break;
            case StructureInfo s when net is not null:
                tabs.Add(("Conexoes", $"Conexoes ({s.PipeCount + s.Catchments.Count})", () => ConnectionsView(s, net)));
                tabs.Add(("Hidraulica", "Hidraulica", () => StructureHydraulicsView(s, net)));
                tabs.Add(("Regras", "Regras", () => RulesView(s.Handle, s.RuleSet)));
                break;
            case PipeInfo p when net is not null:
                tabs.Add(("Hidraulica", "Hidraulica", () => PipeHydraulicsView(p, net)));
                tabs.Add(("Regras", "Regras", () => RulesView(p.Handle, p.RuleSet)));
                break;
            case CatchmentInfo c when net is not null:
                tabs.Add(("Contribuicao", "Contribuicao", () => ContributionView(c, net)));
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
        }

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

    private SW.UIElement BuildProperties()
    {
        _props = new WC.StackPanel();
        _actions = new WC.WrapPanel { Margin = new SW.Thickness(4) };
        _fields.Clear();
        _fieldLabels.Clear();
        _render = () => RenderPlan(null);

        switch (_current?.Data)
        {
            case NetworkInfo n: ShowNetwork(n); break;
            case StructureInfo s: ShowStructure(s); break;
            case PipeInfo p: ShowPipe(p); break;
            case CatchmentInfo c: ShowCatchment(c); break;
            case HydroViolation v: ShowViolation(v); break;
            case HydroSizingRow r: ShowSizingRow(r); break;
            case string key when _category == "CALCULO": ShowSetting(key); break;
            default:
                Prop("Redes", _snap.Networks.Count.ToString(CultureInfo.InvariantCulture));
                Prop("Bacias", _snap.Catchments.Count.ToString(CultureInfo.InvariantCulture));
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

    private void Prop(string label, string value, WM.Brush? brush = null)
    {
        WC.Grid row = PropRow(label);
        WC.TextBlock text = new() { Text = value, Margin = new SW.Thickness(4, 2, 4, 2), TextWrapping = SW.TextWrapping.Wrap, Foreground = brush ?? T.Text };
        WC.Grid.SetColumn(text, 1);
        row.Children.Add(text);
        _props.Children.Add(row);
    }

    private WC.TextBox EditProp(string key, string label, string value, Action? enter = null)
    {
        WC.Grid row = PropRow(label);
        WC.TextBox box = new()
        {
            Text = value,
            BorderThickness = new SW.Thickness(1),
            BorderBrush = _dark ? FieldBorderDark : FieldBorderLight,
            Margin = new SW.Thickness(3, 1, 3, 1),
            Padding = new SW.Thickness(3, 1, 3, 1),
            Background = _dark ? FieldBackDark : FieldBackLight,
            Foreground = T.Text,
            CaretBrush = T.Text,
            VerticalContentAlignment = SW.VerticalAlignment.Center
        };

        // Cara de campo: borda, fundo proprio, dica quando vazio e borda azul no foco.
        WC.TextBlock hint = new()
        {
            Text = "digite aqui",
            Foreground = T.Muted,
            FontStyle = SW.FontStyles.Italic,
            Margin = new SW.Thickness(9, 0, 0, 0),
            VerticalAlignment = SW.VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = value.Length == 0 ? SW.Visibility.Visible : SW.Visibility.Collapsed
        };
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 && !box.IsKeyboardFocused ? SW.Visibility.Visible : SW.Visibility.Collapsed;
        box.GotKeyboardFocus += (_, _) =>
        {
            box.BorderBrush = T.Accent;
            hint.Visibility = SW.Visibility.Collapsed;
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            box.BorderBrush = _dark ? FieldBorderDark : FieldBorderLight;
            hint.Visibility = box.Text.Length == 0 ? SW.Visibility.Visible : SW.Visibility.Collapsed;
        };
        if (row.Children.OfType<WC.Border>().FirstOrDefault()?.Child is WC.TextBlock caption)
            _fieldLabels[key] = caption;
        if (enter is not null)
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == WI.Key.Enter)
                {
                    enter();
                    e.Handled = true;
                }
            };
        }

        WC.Grid cell = new();
        cell.Children.Add(box);
        cell.Children.Add(hint);
        WC.Grid.SetColumn(cell, 1);
        row.Children.Add(cell);
        _props.Children.Add(row);
        _fields[key] = box;
        return box;
    }

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

    private double? Number(string key, double min = double.NegativeInfinity)
    {
        if (!_fields.TryGetValue(key, out SW.FrameworkElement? element) || element is not WC.TextBox box)
            return null;
        string t = box.Text.Trim().Replace(',', '.').Replace("%", "").Trim();
        if (t.Length == 0 || t == "-")
            return null;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= min)
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

    // ---------------- Propriedades por tipo ----------------

    private void ShowNetwork(NetworkInfo n)
    {
        Category("Rede");
        Prop("Nome", n.Name);
        Prop("Parts list", n.PartsList);
        Prop("Superficie de referencia", n.ReferenceSurface.Length > 0 ? n.ReferenceSurface : "-");
        Prop("Alinhamento de referencia", n.ReferenceAlignment.Length > 0 ? n.ReferenceAlignment : "-");
        Prop("Estruturas / tubos", $"{n.Structures.Count} / {n.Pipes.Count}");
        Prop("Extensao", N(n.Length2D, 1) + " m");
        Prop("Tubos sem sentido", n.Pipes.Count(p => !p.Defined).ToString(CultureInfo.InvariantCulture));
        Prop("Familias de tubos", string.Join(", ", n.Pipes.Select(p => p.Family).Distinct().OrderBy(f => f)));
        Prop("Familias de estruturas", string.Join(", ", n.Structures.Select(s => s.Family).Distinct().OrderBy(f => f)));
        string catalogNote = CatalogNote(n);
        if (catalogNote.Length > 0)
        {
            Prop("Catalogo de pecas", catalogNote, T.Warn);
            Act("Catalogo CSBIM (HYCATALOGO)", () => Send("HYCATALOGO ", "HYCATALOGO: siga a linha de comando."));
        }
        else
        {
            Prop("Catalogo de pecas", "familias da parts list vem do catalogo CSBIM saneado");
        }

        if (n.Handle != Net?.Handle)
            Act("Usar esta rede no painel", () => { _networkHandle = n.Handle; Refresh(); }, true);
        Act("Zoom", () => ZoomTo(n.Bounds));
        _render = () => RenderPlan(n);
    }

    private List<HydroCatalog.CatalogInfo>? _catalogs;

    /// <summary>Aviso quando a parts list da rede nao vem do catalogo CSBIM saneado (vazio = tudo certo).</summary>
    private string CatalogNote(NetworkInfo n)
    {
        try
        {
            _catalogs ??= HydroCatalog.Scan();
            if (n.PartsList.Length == 0)
                return "a rede nao tem parts list";
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc is null)
                return "";
            (int total, int csbim) = HydroCatalog.CountCsbimFamilies(doc.Database, n.PartsList, _catalogs);
            bool sanitized = _catalogs.Any(c => c.IsCsbim && c.Sanitized);
            if (total == 0)
                return $"parts list \"{n.PartsList}\" sem familias: importe do catalogo CSBIM";
            if (csbim < total)
                return $"{total - csbim} de {total} familia(s) da parts list \"{n.PartsList}\" nao vem do catalogo CSBIM";
            if (!sanitized)
                return "familias CSBIM, mas o catalogo nesta maquina nao esta saneado (tools/Sanitize-CsbimCatalog.ps1 -Apply + PARTCATALOGREGEN)";
            return "";
        }
        catch (Exception ex)
        {
            PluginLog.Diag(Module, "Verificacao do catalogo: " + ex.Message);
            return "";
        }
    }

    private void ShowStructure(StructureInfo s)
    {
        NetworkInfo? net = Net;
        List<Row> targets = _current is null ? new List<Row>() : BatchTargets(_current);
        if (targets.Count > 1)
        {
            ShowStructuresBatch(targets.Select(r => (StructureInfo)r.Data!).ToList());
            return;
        }

        Category("Estrutura " + s.Name);
        EditProp("nome", "Nome", s.Name, () => ApplyStructures(new[] { s }, false));
        Prop("Familia / tamanho", $"{s.Family} / {s.Size}");
        Prop("Tipo hidraulico", s.Type);
        Prop("Tipo no catalogo", s.CatalogPartType.Length > 0 ? s.CatalogPartType : "-");
        MeasureProp(s, HydroRules.Rim, "Tampa (rim) [m]", s.Rim, () => ApplyStructures(new[] { s }, false));
        MeasureProp(s, HydroRules.Sump, "Fundo (sump) [m]", s.Sump, () => ApplyStructures(new[] { s }, false));
        Prop("Profundidade", N(s.Depth, 2) + " m");
        Prop("Controle do fundo", s.ControlSumpBy);
        Prop("Superficie no ponto", double.IsNaN(s.SurfaceElevation) ? "-" : N(s.SurfaceElevation, 3) + " m" + (s.RefSurface.Length > 0 ? " (" + s.RefSurface + ")" : ""));
        Prop("Diametro interno", Mm(s.InnerDiameter) + " mm");
        Prop("Posicao", $"E {N(s.Point.X, 3)}  N {N(s.Point.Y, 3)}  rot {N(s.Rotation * 180 / Math.PI, 1)}°");
        Prop("Tubos", $"{s.PipesIn.Count} entrando, {s.PipesOut.Count} saindo" + (s.PipesUndefined.Count > 0 ? $", {s.PipesUndefined.Count} sem sentido" : ""));
        Prop("Bacias ligadas", s.Catchments.Count > 0 ? string.Join(", ", s.Catchments) : "-");
        Prop("Area a montante (bacias)", N(UpstreamAreaHa(net!, s.Handle), 4) + " ha");
        Prop("Rule set", s.RuleSet.Length > 0 ? s.RuleSet : "-");
        if (s.Warnings.Count > 0)
            Prop("Avisos", string.Join("\n", s.Warnings), T.Warn);

        Act("Aplicar (Enter)", () => ApplyStructures(new[] { s }, false), true);
        Act("Zoom", () => ZoomRow(_current!));
        Act("Selecionar", () => SelectHandles(new[] { s.Handle }));
        Act("Fundo no desenho (HYFUNDO)", () => Send("HYFUNDO ", "HYFUNDO: clique a estrutura."));
        Act("Peca 3D", () => OpenPart3D(s));

        // Medidas da peca (PartData) em metros: valor fixo, relativo ou "=regra" associativa.
        MeasuresSection(s.Parameters, s, s.Family, () => ApplyStructures(new[] { s }, false));
        RulesSection(new[] { s });
        AllPropertiesSection(new[] { s.Handle });
        _render = () => RenderPlan(net, s.Handle);
    }

    /// <summary>Abre (ou troca) o editor da peca em 3D; grava pelo mesmo caminho da aba Propriedades.</summary>
    private void OpenPart3D(StructureInfo s)
    {
        if (Net is not NetworkInfo net)
            return;
        HydroPart3DWindow.Open(s, net, (x, edits) => ApplyStructureEdits(new[] { x }, edits, "", "", false), GoTo);
    }

    /// <summary>Editor 3D com a primeira marcada como previa; "Aplicar" grava em todas as marcadas.</summary>
    private void OpenPart3DBatch(List<StructureInfo> list)
    {
        if (Net is not NetworkInfo net || list.Count == 0)
            return;
        // Por handle: depois de cada gravacao o painel rele e as medidas atuais valem (ex. +0,10).
        List<string> handles = list.Select(s => s.Handle).ToList();
        HydroPart3DWindow.Open(list[0], net, (_, edits) =>
        {
            List<StructureInfo> targets = handles
                .Select(h => _snap.Networks.Select(n => n.StructureByHandle.TryGetValue(h, out StructureInfo? x) ? x : null).FirstOrDefault(x => x is not null))
                .OfType<StructureInfo>().ToList();
            if (targets.Count > 0)
                ApplyStructureEdits(targets, edits, "", "", true);
        }, GoTo, handles.Count);
    }

    private void ShowStructuresBatch(List<StructureInfo> list)
    {
        Category($"Edicao em lote: {list.Count} estrutura(s)");
        SayBatch(list.Count, "estrutura(s)");
        Prop("Itens", string.Join(", ", list.Select(s => s.Name)));
        MeasureProp(null, HydroRules.Rim, "Tampa [m] (valor, +0,10 / -0,30 ou =regra)", 0, () => ApplyStructures(list, true));
        MeasureProp(null, HydroRules.Sump, "Fundo [m] (valor, +0,10 / -0,30 ou =regra)", 0, () => ApplyStructures(list, true));
        EditProp("prefixo", "Renomear: prefixo + numero (ex. PV-)", "", () => ApplyStructures(list, true));
        Prop("Familia / tamanho", "use Trocar tipo (barra de comandos) ou o menu Editar em lote");

        // Medidas comuns a todas as marcadas (mesmo nome): vazio = mantem cada uma como esta.
        List<PartParam> common = list.Select(s => s.Parameters.Where(p => p.Editable).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Aggregate((a, b) => { a.IntersectWith(b); return a; })
            .Select(name => list[0].Parameters.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();
        if (common.Count > 0)
        {
            MeasuresSection(common, null, "comuns aos marcados: digite e Enter (ou Aplicar nos marcados)", () => ApplyStructures(list, true));
            BatchMeasureValues(list, common.Select(p => p.Name));
        }

        RulesSection(list);
        AllPropertiesSection(list.Select(s => s.Handle).ToList());
        Act("Aplicar nos marcados", () => ApplyStructures(list, true), true);
        Act("Peca 3D (lote)", () => OpenPart3DBatch(list));
        Act("Selecionar no desenho", () => SelectHandles(list.Select(s => s.Handle)));
        _render = () => RenderPlan(Net, null, list.Select(s => s.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Mostra nos campos do lote o valor atual quando todas as marcadas tem o mesmo (ex. SIL 2,400);
    /// se variam, o campo fica vazio e o rotulo mostra a faixa. So o que for digitado e gravado.
    /// </summary>
    private void BatchMeasureValues(IReadOnlyList<StructureInfo> list, IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            if (!_fields.TryGetValue("med:" + name, out SW.FrameworkElement? el) || el is not WC.TextBox box)
                continue;
            List<double> values = list.Select(s => s.Parameters.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .Where(p => p is not null).Select(p => Math.Round(p!.Meters, 4)).ToList();
            if (values.Count == 0)
                continue;
            double min = values.Min(), max = values.Max();
            if (max - min < 1e-6)
            {
                box.Text = N(min, 3);
                box.Tag = box.Text;
            }
            else if (_fieldLabels.TryGetValue("med:" + name, out WC.TextBlock? label))
            {
                label.Text += $"  (varia {N(min, 3)} a {N(max, 3)})";
            }
        }
    }

    private bool _showAllProps;

    /// <summary>
    /// Todas as propriedades do objeto nativo (lidas por reflexao, so quando aberto). Cada campo
    /// grava sozinho: Enter na caixa ou escolha na lista. Com varias pecas, mostra os valores da
    /// primeira e grava em todas.
    /// </summary>
    private void AllPropertiesSection(IReadOnlyList<string> handles)
    {
        if (handles.Count == 0)
            return;
        WC.Expander exp = new()
        {
            Header = handles.Count > 1 ? $"Todas as propriedades do objeto ({handles.Count} pecas; valores da primeira)" : "Todas as propriedades do objeto (avancado)",
            Foreground = T.Text,
            FontWeight = SW.FontWeights.Bold,
            IsExpanded = false,
            Margin = new SW.Thickness(0, 4, 0, 2)
        };
        WC.StackPanel host = new();
        SW.Documents.TextElement.SetFontWeight(host, SW.FontWeights.Normal);
        exp.Content = host;
        _props.Children.Add(exp);
        bool filled = false;
        void Fill()
        {
            if (filled)
                return;
            filled = true;
            AcadDb.Database? db = AcApp.DocumentManager.MdiActiveDocument?.Database;
            if (db is null)
                return;
            List<ObjProp> props;
            try
            {
                props = HydroObjectProps.Read(db, handles[0]);
            }
            catch (Exception ex)
            {
                host.Children.Add(new WC.TextBlock { Text = "Nao foi possivel ler: " + ex.Message, Foreground = T.Warn });
                return;
            }

            WC.StackPanel keep = _props;
            _props = host;
            try
            {
                foreach (IGrouping<string, ObjProp> g in props.GroupBy(x => x.Group))
                {
                    Category($"{g.Key} ({g.Count()})");
                    foreach (ObjProp op in g)
                    {
                        string key = "obj:" + op.Key;
                        string prop = op.Key;
                        switch (op.Kind)
                        {
                            case "ro":
                                Prop(op.Label, op.Value, T.Muted);
                                break;
                            case "bool":
                            case "enum":
                            case "ref":
                                ChoiceProp(key, op.Label, op.Choices.Select(c => (c, c)), op.Value, () => ApplyObjProp(handles, prop, Text(key)));
                                break;
                            default:
                                WC.TextBox box = EditProp(key, op.Label, op.Value);
                                box.Tag = op.Value;
                                box.ToolTip = op.Kind switch
                                {
                                    "num" => "Numero, +0,10 / -0,30 (relativo) ou expressao. Enter grava.",
                                    "ang" => "Angulo em graus (ou +15 / -15). Enter grava.",
                                    "point" => "X; Y; Z (ou X; Y para manter o Z). Enter grava.",
                                    _ => "Enter grava."
                                };
                                box.KeyDown += (_, e) =>
                                {
                                    if (e.Key != WI.Key.Enter)
                                        return;
                                    e.Handled = true;
                                    if (box.Text.Trim() != (box.Tag as string ?? ""))
                                        ApplyObjProp(handles, prop, box.Text);
                                };
                                break;
                        }
                    }
                }
            }
            finally
            {
                _props = keep;
            }
        }

        exp.Expanded += (_, _) =>
        {
            _showAllProps = true;
            Fill();
        };
        exp.Collapsed += (_, _) => _showAllProps = false;
        if (_showAllProps)
        {
            exp.IsExpanded = true;
            Fill();
        }
    }

    private void ApplyObjProp(IReadOnlyList<string> handles, string key, string text)
    {
        List<string> list = handles.ToList();
        RunApply(list.Count > 1 ? $"{key} em {list.Count} peca(s)" : "Editar " + key, db =>
        {
            string msg = HydroObjectProps.Write(db, list, key, text);
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n[HYDRO] " + msg);
        });
    }

    /// <summary>
    /// Medidas da peca em tres grupos: as do catalogo (faixa/lista), as fixas da familia (gravacao
    /// direta, o Civil pode recusar) e as internas do desenho do modelo (so leitura, recolhidas).
    /// </summary>
    private void MeasuresSection(IReadOnlyList<PartParam> parameters, StructureInfo? s, string title, Action enter)
    {
        List<PartParam> catalog = parameters.Where(p => p.Editable && !p.Fixed).ToList();
        List<PartParam> fixedOnes = parameters.Where(p => p.Editable && p.Fixed).ToList();
        List<PartParam> locked = parameters.Where(p => !p.Editable).ToList();
        if (catalog.Count > 0)
        {
            Category($"Medidas da peca ({catalog.Count}) - {title}");
            foreach (PartParam p in catalog)
                MeasureProp(s, p.Name, $"{p.Description} [{p.Name}] {RangeM(p)}", s is null ? 0 : p.Meters, enter);
        }

        if (fixedOnes.Count > 0)
        {
            Category($"Medidas fixas da familia ({fixedOnes.Count})");
            foreach (PartParam p in fixedOnes)
                MeasureProp(s, p.Name, $"{p.Description} [{p.Name}] fixo no catalogo", s is null ? 0 : p.Meters, enter);
        }

        if (locked.Count > 0 && s is not null)
        {
            WC.Expander exp = new()
            {
                Header = $"Medidas internas do desenho do modelo ({locked.Count}, so leitura)",
                Foreground = T.Muted,
                IsExpanded = false,
                Margin = new SW.Thickness(0, 2, 0, 2)
            };
            WC.StackPanel inner = new();
            foreach (PartParam p in locked)
            {
                inner.Children.Add(new WC.TextBlock
                {
                    Text = $"{p.Name}  {N(p.Value, 3)} {p.Units}   {p.Description} ({p.Context})",
                    Foreground = T.Muted,
                    FontFamily = MonoFont,
                    Margin = new SW.Thickness(8, 1, 4, 1)
                });
            }

            exp.Content = inner;
            _props.Children.Add(exp);
        }
    }

    /// <summary>
    /// Campo de uma medida da estrutura (tampa, fundo ou medida da peca, em m). Mostra "=regra"
    /// quando a medida e associativa; numero digitado vira valor fixo, "+0,10"/"-0,30" e relativo.
    /// </summary>
    private void MeasureProp(StructureInfo? s, string target, string label, double meters, Action enter)
    {
        string text = "";
        if (s is not null)
        {
            if (s.Rules.TryGetValue(target, out string? rule))
            {
                text = "=" + rule;
                label += $"  = {N(meters, 3)}" + (s.OwnRules.Own.ContainsKey(target) ? "" : " (modelo)");
            }
            else
            {
                text = N(meters, 3);
            }
        }

        WC.TextBox box = EditProp("med:" + target, label, text, enter);
        box.Tag = text;
        box.ToolTip = "Numero = valor fixo; +0,10 / -0,30 = relativo ao atual; =expressao = regra associativa (ex. =dmax + 2*0,15).\n" + HydroRules.Help;
        if (text.StartsWith('='))
            box.Foreground = T.Accent;
    }

    /// <summary>Modelo de regras, salvar como modelo e atualizar (uma ou varias estruturas).</summary>
    private void RulesSection(IReadOnlyList<StructureInfo> list)
    {
        Category("Regras das medidas (associativas)");
        string family = list[0].Family;
        bool same = list.Select(x => x.OwnRules.Template).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        string current = same ? list[0].OwnRules.Template : "*";
        List<(string Text, string Value)> items = new();
        if (!same)
            items.Add(("(varios - manter)", "*"));
        items.Add(("(sem modelo)", ""));
        items.AddRange(_snap.Templates.Values
            .OrderBy(t => t.Family.Equals(family, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => ($"{t.Name}  [{t.Family}, {t.Rules.Count} regra(s)]", t.Name)));
        ChoiceProp("modelo", "Modelo de regras", items, current, () => ApplyTemplateChoice(list));
        if (list.Count == 1)
        {
            EditProp("novomodelo", "Salvar as regras como modelo (nome + Enter)", "", () => SaveTemplateFrom(list[0]));
            if (list[0].Rules.Count > 0)
                Prop("Regras que valem", string.Join("\n", list[0].Rules.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key} = {kv.Value}")));
        }

        Prop("Atualizacao", _settings.RulesAuto ? "automatica, no fim de cada comando" : "manual: use Atualizar regras (HYREGRAS)");
        Prop("Tubos nas bocas de lobo", _settings.PipesInside ? "entram ate o centro da caixa (botao Tubos dentro)" : "ficam onde o Civil ligar (desligado)");
        Prop("Como escrever", HydroRules.Help);
        Act("Atualizar regras", () => UpdateRules(list.Select(x => x.Handle).ToList()));
    }

    private void ApplyTemplateChoice(IReadOnlyList<StructureInfo> list)
    {
        string name = Text("modelo");
        if (name == "*")
            return;
        List<string> handles = list.Select(x => x.Handle).ToList();

        // Adotar o modelo tira as regras proprias das medidas que ele define.
        Dictionary<string, string?> clear = new(StringComparer.OrdinalIgnoreCase);
        if (name.Length > 0 && _snap.Templates.TryGetValue(name, out HydroRuleTemplate? t))
        {
            foreach (string k in t.Rules.Keys)
                clear[k] = null;
        }

        HydroStore.HoldMode hold = _settings.HoldMode;
        RunApply(name.Length > 0 ? $"Modelo '{name}' em {handles.Count} estrutura(s)" : $"Sem modelo em {handles.Count} estrutura(s)", db =>
        {
            HydroRules.Edit(db, handles, clear, name);
            ApplyRulesNow(db, handles, hold);
        });
    }

    private void SaveTemplateFrom(StructureInfo s)
    {
        string name = Text("novomodelo");
        if (name.Length == 0)
        {
            Say("Digite o nome do modelo (ex. PV DNIT padrao).");
            return;
        }

        if (s.Rules.Count == 0)
        {
            Say($"{s.Name} nao tem regras: escreva =regra nas medidas primeiro.");
            return;
        }

        name = HydroRules.CleanName(name);
        Dictionary<string, string> rules = new(s.Rules, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> clear = rules.Keys.ToDictionary(k => k, _ => (string?)null, StringComparer.OrdinalIgnoreCase);
        string family = s.Family;
        RunApply($"Modelo '{name}' salvo ({rules.Count} regra(s))", db =>
        {
            HydroRules.SaveTemplate(db, name, family, rules);
            HydroRules.Edit(db, new[] { s.Handle }, clear, name);
        });
    }

    private void UpdateRules(List<string> handles)
    {
        HydroStore.HoldMode hold = _settings.HoldMode;
        RunApply($"Atualizar regras em {handles.Count} estrutura(s)", db => ApplyRulesNow(db, handles, hold));
    }

    /// <summary>Recalcula as regras ja (dentro do HYAPLICAR) e escreve o resumo na linha de comando.</summary>
    private static void ApplyRulesNow(AcadDb.Database db, IEnumerable<string> handles, HydroStore.HoldMode hold)
    {
        List<AcadDb.ObjectId> ids = handles.Select(h => HydroReader.Id(db, h)).OfType<AcadDb.ObjectId>().ToList();
        HydroRules.Report rep;
        using (HydroSync.Applying())
            rep = HydroRules.Apply(db, ids, hold);
        Autodesk.AutoCAD.EditorInput.Editor? ed = AcApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed is null)
            return;
        ed.WriteMessage("\n[HYDRO] " + rep.Summary + ".");
        foreach (string line in rep.Lines.Take(20))
            ed.WriteMessage("\n  " + line);
        foreach (string err in rep.Errors.Take(20))
            ed.WriteMessage("\n  aviso: " + err);
    }

    private void ShowPipe(PipeInfo p)
    {
        NetworkInfo? net = Net;
        List<Row> targets = _current is null ? new List<Row>() : BatchTargets(_current);
        if (targets.Count > 1)
        {
            ShowPipesBatch(targets.Select(r => (PipeInfo)r.Data!).ToList());
            return;
        }

        string from = p.FromHandle is string f && net?.StructureByHandle.GetValueOrDefault(f) is StructureInfo fs ? fs.Name : "?";
        string to = p.ToHandle is string t && net?.StructureByHandle.GetValueOrDefault(t) is StructureInfo ts ? ts.Name : "?";
        Category("Tubo " + p.Name);
        EditProp("nome", "Nome", p.Name, () => ApplyField(p));
        Prop("De > para (fluxo)", from + " > " + to + "  [" + p.FlowDirection + "]");
        Prop("Familia / tamanho", $"{p.Family} / {p.Size}");
        Prop("Diametro interno / externo", $"{Mm(p.InnerDiameter)} / {Mm(p.OuterDiameter)} mm");
        Prop("Comprimento 2D / 3D", $"{N(p.Length2D, 2)} / {N(p.Length3D, 2)} m");
        EditProp("fundomont", "Fundo de montante [m]", N(p.UpInvert, 3), () => ApplyField(p));
        EditProp("fundojus", "Fundo de jusante [m]", N(p.DownInvert, 3), () => ApplyField(p));
        EditProp("decliv", "Declividade [%] (segura montante)", N(p.Slope * 100, 3), () => ApplyField(p));
        Prop("Recobrimento mont. / jus.", $"{N(p.CoverUp, 2)} / {N(p.CoverDown, 2)} m  (min. regra {N(p.MinimumCover, 2)})");
        Prop("Manning (parts list)", N(p.Manning, 3));
        Prop("Vazao gravada", N(p.FlowRate, 4) + " m3/s");
        Prop("HGL mont. / jus.", $"{N(p.HglUp, 3)} / {N(p.HglDown, 3)}");
        Prop("Rule set", p.RuleSet.Length > 0 ? p.RuleSet : "-");
        if (p.Warnings.Count > 0)
            Prop("Avisos", string.Join("\n", p.Warnings), T.Warn);

        Act("Aplicar (Enter)", () => ApplyField(p), true);
        Act("Zoom", () => ZoomRow(_current!));
        Act("Selecionar", () => SelectHandles(new[] { p.Handle }));
        Act("Inverter sentido", () => RunApply("Inverter " + p.Name, db => HydroEditor.Reverse(db, new[] { p.Handle })));
        PipeMeasuresSection(new[] { p.Handle });
        AllPropertiesSection(new[] { p.Handle });
        _render = () => RenderPlan(net, p.Handle);
    }

    /// <summary>
    /// Medidas da peca do tubo (PartData: diametro, espessura, etc.), lidas na hora. Enter grava a
    /// medida em todos os tubos (valor em m, ou +0,05 / -0,05 relativo a cada um).
    /// </summary>
    private void PipeMeasuresSection(IReadOnlyList<string> handles)
    {
        AcadDb.Database? db = AcApp.DocumentManager.MdiActiveDocument?.Database;
        if (db is null || handles.Count == 0)
            return;
        List<PartParam> parameters;
        try
        {
            parameters = HydroObjectProps.ReadPartParams(db, handles[0]);
        }
        catch (Exception ex)
        {
            Prop("Medidas da peca", "nao lidas: " + ex.Message, T.Warn);
            return;
        }

        List<PartParam> editable = parameters.Where(x => x.Editable).ToList();
        if (editable.Count == 0)
            return;
        Category(handles.Count > 1 ? $"Medidas da peca ({editable.Count}; valores do primeiro)" : $"Medidas da peca ({editable.Count})");
        foreach (PartParam pp in editable)
        {
            string text = handles.Count > 1 ? "" : N(pp.Meters, 4);
            WC.TextBox box = EditProp("pmed:" + pp.Name, $"{pp.Description} [{pp.Name}] {(pp.Fixed ? "fixo no catalogo" : RangeM(pp))}", text);
            box.Tag = text;
            box.ToolTip = "Valor em m, ou +0,05 / -0,05 relativo. Enter grava.";
            string name = pp.Name;
            box.KeyDown += (_, e) =>
            {
                if (e.Key != WI.Key.Enter)
                    return;
                e.Handled = true;
                string t = box.Text.Trim();
                if (t.Length > 0 && t != (box.Tag as string ?? ""))
                    ApplyPartParam(handles, name, t);
            };
        }
    }

    /// <summary>Grava uma medida de PartData (texto em m, absoluto ou relativo) em cada peca.</summary>
    private void ApplyPartParam(IReadOnlyList<string> handles, string name, string text)
    {
        List<string> list = handles.ToList();
        RunApply(list.Count > 1 ? $"{name} em {list.Count} peca(s)" : "Editar " + name, db =>
        {
            List<string> msgs = new();
            foreach (string h in list)
            {
                PartParam? pp = HydroObjectProps.ReadPartParams(db, h).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (pp is null)
                {
                    msgs.Add($"{h}: sem {name}");
                    continue;
                }

                double? v;
                try
                {
                    v = HydroRuleEngine.EditValue(text, pp.Meters);
                }
                catch (HydroExprException ex)
                {
                    msgs.Add(ex.Message);
                    continue;
                }

                if (v is null)
                    continue;
                double catalog = v.Value / pp.Factor;
                if (!pp.IsRange && pp.List.Count > 0)
                    catalog = HydroRuleEngine.SnapUp(catalog, pp.List, out _);
                msgs.Add(HydroEditor.SetParameters(db, h, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [pp.Name] = catalog }));
            }

            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n[HYDRO] " + name + ": " + string.Join("; ", msgs.Distinct().Take(6)));
        });
    }

    private void ShowPipesBatch(List<PipeInfo> list)
    {
        Category($"Edicao em lote: {list.Count} tubo(s)");
        SayBatch(list.Count, "tubo(s)");
        Prop("Itens", string.Join(", ", list.Select(p => p.Name)));
        EditProp("decliv", "Declividade [%] (segura montante)", "", () => ApplyBatchPipes(list));
        EditProp("fundomont", "Fundo de montante [m] (valor ou +/-)", "", () => ApplyBatchPipes(list));
        EditProp("fundojus", "Fundo de jusante [m] (valor ou +/-)", "", () => ApplyBatchPipes(list));
        EditProp("prefixo", "Renomear: prefixo + numero (ex. T-)", "", () => ApplyBatchPipes(list));
        Act("Aplicar nos marcados", () => ApplyBatchPipes(list), true);
        Act("Inverter sentido", () => RunApply($"Inverter {list.Count} tubo(s)", db => HydroEditor.Reverse(db, list.Select(p => p.Handle))));
        Act("Aplicar regras", () => RunApply($"Regras em {list.Count} tubo(s)", db => HydroEditor.ApplyRules(db, list.Select(p => p.Handle))));
        Act("Selecionar no desenho", () => SelectHandles(list.Select(p => p.Handle)));
        PipeMeasuresSection(list.Select(p => p.Handle).ToList());
        AllPropertiesSection(list.Select(p => p.Handle).ToList());
        _render = () => RenderPlan(Net, null, list.Select(p => p.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private void ShowCatchment(CatchmentInfo c)
    {
        NetworkInfo? net = Net;
        Category("Bacia " + c.Name);
        Prop("Grupo", c.Group);
        Prop("Area", $"{N(c.AreaHa, 4)} ha  ({N(c.Area, 0)} m2)");
        EditProp("c", "Coeficiente de escoamento C", N(c.C, 2), () => ApplyField(c));
        EditProp("tc", "Tempo de concentracao [min]", N(c.Tc, 1), () => ApplyField(c));
        Prop("Metodo do Tc (Civil)", c.TcMethod);
        Prop("Comprimento do talvegue", N(c.FlowLength, 1) + " m");
        List<(string, string)> structures = new() { ("(nenhuma)", "") };
        if (net is not null)
            structures.AddRange(net.Structures.OrderBy(s => s.Name).Select(s => (s.Name, s.Handle)));
        ChoiceProp("estrutura", "Estrutura de destino", structures, c.StructureHandle, () => ApplyField(c));
        Act("Aplicar (Enter)", () => ApplyField(c), true);
        Act("Zoom", () => ZoomRow(_current!));
        Act("Selecionar", () => SelectHandles(new[] { c.Handle }));
        _render = () => RenderPlan(net, c.Handle);
    }

    private void ShowViolation(HydroViolation v)
    {
        Category("Violacao");
        Prop("Item", v.Item);
        Prop("Regra", v.Rule);
        Prop("Valor / limite", v.Value + " / " + v.Limit);
        Prop("Severidade", v.Severity.ToString(), v.Severity == HydroSeverity.Erro ? T.Error : T.Warn);
        Prop("Descricao", v.Text);
        if (v.Fix.Length > 0)
            Prop("Correcao sugerida", v.Fix);
        Act("Ir para", () => GoTo(v.Handle), true);
        Act("Mostrar no painel", () => ShowInPanel(v.Handle));
        _render = () => RenderPlan(Net, v.Handle);
    }

    private void ShowSizingRow(HydroSizingRow r)
    {
        Category("Trecho " + r.Trecho);
        Prop("Area local / acumulada", $"{N(r.AreaLocalHa, 4)} / {N(r.AreaAcumHa, 4)} ha");
        Prop("C medio / Tc", $"{N(r.C, 2)} / {N(r.Tc, 1)} min");
        Prop("Intensidade", N(r.Intensity, 1) + " mm/h");
        Prop("Vazao", N(r.Flow, 4) + " m3/s");
        Prop("Diametro / declividade", $"{Mm(r.Diameter)} mm / {Pct(r.Slope)}");
        Prop("Lamina / velocidade", $"{N(r.DepthRatio * 100, 0)} % / {N(r.Velocity, 2)} m/s");
        Prop("Menor diametro que atende", Mm(r.MinDiameter) + " mm");
        Act("Ir para", () => GoTo(r.PipeHandle), true);
        _render = () => RenderPlan(Net, r.PipeHandle);
    }

    private void ShowSetting(string key)
    {
        Category("Parametros de calculo");
        foreach ((string k, string label, string value) in HydroStore.Describe(_settings))
        {
            if (k == "idf.nome")
                ChoiceProp(k, label, HydroIdf.Library.Select(i => (i.Name, i.Name)).Append(("(personalizada)", "custom")), _settings.IdfName, () => { });
            else
                EditProp(k, label, value);
        }

        Act("Salvar no desenho", SaveSettings, true);
        Act("Calcular agora", () => Send("HYCALCULAR ", "Calculando..."));
        _render = () => RenderIdf();
    }

    // ---------------- Abas de contexto ----------------

    private SW.UIElement PartsView(NetworkInfo n)
    {
        List<Row> rows = new();
        foreach (IGrouping<string, PipeInfo> g in n.Pipes.GroupBy(p => p.Family + " / " + p.Size).OrderBy(g => g.Key))
            rows.Add(new Row { Icon = "TUBOS", Cells = new[] { g.Key, g.Count().ToString(CultureInfo.InvariantCulture), N(g.Sum(p => p.Length2D), 1) } });
        foreach (IGrouping<string, StructureInfo> g in n.Structures.GroupBy(s => s.Family + " / " + s.Size).OrderBy(g => g.Key))
            rows.Add(new Row { Icon = "ESTRUTURAS", Cells = new[] { g.Key, g.Count().ToString(CultureInfo.InvariantCulture), "-" } });
        return SubList(new[] { "Peca", "Qtde", "Extensao [m]" }, new double[] { 260, 60, 90 }, rows);
    }

    private SW.UIElement ConnectionsView(StructureInfo s, NetworkInfo net)
    {
        List<Row> rows = new();
        foreach (string h in s.PipesIn)
        {
            if (net.PipeByHandle.TryGetValue(h, out PipeInfo? p))
                rows.Add(new Row { Id = "TUBO:" + h, Icon = "TUBOS", Data = p, Cells = new[] { p.Name, "entra", Mm(p.InnerDiameter), N(p.DownInvert, 3), N(s.Sump - p.DownInvert, 2) } });
        }

        foreach (string h in s.PipesOut)
        {
            if (net.PipeByHandle.TryGetValue(h, out PipeInfo? p))
                rows.Add(new Row { Id = "TUBO:" + h, Icon = "TUBOS", Data = p, Cells = new[] { p.Name, "sai", Mm(p.InnerDiameter), N(p.UpInvert, 3), N(s.Sump - p.UpInvert, 2) } });
        }

        foreach (string h in s.PipesUndefined)
        {
            if (net.PipeByHandle.TryGetValue(h, out PipeInfo? p))
                rows.Add(new Row { Id = "TUBO:" + h, Icon = "TUBOS", Data = p, Cells = new[] { p.Name, "sem sentido", Mm(p.InnerDiameter), "-", "-" } });
        }

        foreach (CatchmentInfo c in _snap.Catchments.Where(c => c.StructureHandle == s.Handle))
            rows.Add(new Row { Id = "BACIA:" + c.Handle, Icon = "BACIAS", Data = c, Cells = new[] { c.Name, "bacia", N(c.AreaHa, 4) + " ha", "C " + N(c.C, 2), "Tc " + N(c.Tc, 1) } });
        return SubList(new[] { "Item", "Papel", "D [mm]", "Fundo no PV", "Fundo PV - fundo tubo [m]" }, new double[] { 120, 80, 60, 90, 150 }, rows);
    }

    private SW.UIElement StructureHydraulicsView(StructureInfo s, NetworkInfo net)
    {
        _props = new WC.StackPanel();
        _actions = new WC.WrapPanel();
        HydroResult? r = ResultOf(s.Handle);
        Category("Hidraulica de " + s.Name);
        Prop("Area acumulada", r is null ? "-" : N(r.AreaHa, 4) + " ha");
        Prop("C medio acumulado", r is null ? "-" : N(r.C, 3));
        Prop("Tc no no", r is null ? "-" : N(r.Tc, 1) + " min");
        Prop("Vazao de saida", r is null ? "-" : N(r.Flow, 4) + " m3/s");
        Prop("HGL / EGL (Civil)", $"{N(s.Hgl, 3)} / {N(s.Egl, 3)}");
        double? inMin = s.PipesIn.Select(h => net.PipeByHandle.GetValueOrDefault(h)?.DownInvert).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Min();
        double? outMax = s.PipesOut.Select(h => net.PipeByHandle.GetValueOrDefault(h)?.UpInvert).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Max();
        Prop("Degrau (menor entrada - maior saida)", inMin.HasValue && outMax.HasValue && !double.IsNaN(inMin.Value) && !double.IsNaN(outMax.Value) ? N(inMin.Value - outMax.Value, 3) + " m" : "-");
        Prop("Fundo do PV - menor fundo de tubo", N(s.Sump - Math.Min(inMin ?? double.NaN, outMax ?? double.NaN), 3) + " m");
        return new WC.ScrollViewer { Content = _props, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
    }

    private SW.UIElement PipeHydraulicsView(PipeInfo p, NetworkInfo net)
    {
        _props = new WC.StackPanel();
        _actions = new WC.WrapPanel();
        HydroResult? r = ResultOf(p.Handle);
        Category("Hidraulica de " + p.Name);
        Prop("Vazao de projeto", r is null ? N(p.FlowRate, 4) + " m3/s (gravada)" : N(r.Flow, 4) + " m3/s");
        Prop("Area acumulada / C / Tc", r is null ? "-" : $"{N(r.AreaHa, 4)} ha / {N(r.C, 3)} / {N(r.Tc, 1)} min");
        Prop("Intensidade", r is null ? "-" : N(r.Intensity, 1) + " mm/h");
        double n = double.IsNaN(p.Manning) ? _settings.Manning : p.Manning;
        double slope = Math.Max(p.Slope, 1e-6);
        double qFull = Calc.Manning.FullFlow(p.InnerDiameter, slope, n);
        Prop("Q plena (D, i, n)", N(qFull, 4) + " m3/s  (n = " + N(n, 3) + ")");
        if (r is not null)
        {
            Prop("Lamina y/D", N(r.DepthRatio * 100, 1) + " %  (y = " + N(r.DepthRatio * p.InnerDiameter, 3) + " m)");
            Prop("Velocidade", N(r.Velocity, 2) + " m/s");
            Prop("Tempo de percurso", N(r.TravelTime, 2) + " min");
        }

        Prop("HGL / EGL mont. (Civil)", $"{N(p.HglUp, 3)} / {N(p.EglUp, 3)}");
        Prop("HGL / EGL jus. (Civil)", $"{N(p.HglDown, 3)} / {N(p.EglDown, 3)}");

        Category("Matriz diametro x declividade (y/D e V para a vazao de projeto)");
        double flow = r?.Flow ?? p.FlowRate;
        if (double.IsNaN(flow) || flow <= 0)
        {
            Prop("Matriz", "Calcule a rede antes (HYCALCULAR).");
        }
        else
        {
            List<double> diameters = net.Pipes.Select(x => x.InnerDiameter).Concat(_settings.Diameters).Where(d => d > 0).Distinct().OrderBy(d => d).ToList();
            double[] slopes = { 0.002, 0.003, 0.005, 0.0075, 0.01, 0.015, 0.02, 0.03, 0.05 };
            WC.Grid grid = new() { Margin = new SW.Thickness(4) };
            grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
            foreach (double _ in slopes)
                grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
            grid.RowDefinitions.Add(new WC.RowDefinition());
            Cell(grid, 0, 0, "D \\ i", T.Muted);
            for (int j = 0; j < slopes.Length; j++)
                Cell(grid, 0, j + 1, Pct(slopes[j]), T.Muted);
            for (int i = 0; i < diameters.Count; i++)
            {
                grid.RowDefinitions.Add(new WC.RowDefinition());
                Cell(grid, i + 1, 0, Mm(diameters[i]) + " mm", T.Muted);
                for (int j = 0; j < slopes.Length; j++)
                {
                    Calc.ManningResult m = Calc.Manning.Solve(flow, diameters[i], slopes[j], n);
                    bool ok = m.Flows && m.DepthRatio <= _settings.MaxDepthRatio && m.Velocity >= _settings.MinVelocity && m.Velocity <= _settings.MaxVelocity;
                    string text = m.Flows ? $"{N(m.DepthRatio * 100, 0)}% {N(m.Velocity, 1)}" : "cheio";
                    Cell(grid, i + 1, j + 1, text, ok ? Brush(60, 170, 75) : m.Flows ? T.Warn : T.Error);
                }
            }

            _props.Children.Add(new WC.ScrollViewer { Content = grid, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Auto });
        }

        return new WC.ScrollViewer { Content = _props, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
    }

    private void Cell(WC.Grid grid, int row, int col, string text, WM.Brush brush)
    {
        WC.Border b = new() { BorderBrush = T.Line, BorderThickness = new SW.Thickness(0, 0, 1, 1), Padding = new SW.Thickness(4, 1, 4, 1) };
        b.Child = new WC.TextBlock { Text = text, Foreground = brush, FontFamily = MonoFont, FontSize = 11 };
        WC.Grid.SetRow(b, row);
        WC.Grid.SetColumn(b, col);
        grid.Children.Add(b);
    }

    private SW.UIElement RulesView(string handle, string ruleSet)
    {
        _props = new WC.StackPanel();
        _actions = new WC.WrapPanel();
        Category("Regras do Civil");
        Prop("Rule set", ruleSet.Length > 0 ? ruleSet : "-");
        List<HydroViolation> own = _violations.Where(v => v.Handle == handle).ToList();
        if (own.Count == 0)
            Prop("Verificacao", "sem violacoes registradas");
        foreach (HydroViolation v in own)
            Prop(v.Rule, $"{v.Value} (limite {v.Limit}): {v.Text}", v.Severity == HydroSeverity.Erro ? T.Error : T.Warn);
        WC.Button apply = new() { Content = "Aplicar regras do Civil neste item", Margin = new SW.Thickness(4), Padding = new SW.Thickness(8, 2, 8, 2) };
        apply.Click += (_, _) => RunApply("Aplicar regras", db => HydroEditor.ApplyRules(db, new[] { handle }));
        _props.Children.Add(apply);
        return new WC.ScrollViewer { Content = _props, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
    }

    private SW.UIElement ContributionView(CatchmentInfo c, NetworkInfo net)
    {
        List<Row> rows = new();
        if (c.StructureHandle.Length > 0)
        {
            foreach (PipeInfo p in net.PathDownstream(c.StructureHandle))
            {
                HydroResult? r = ResultOf(p.Handle);
                rows.Add(new Row { Id = "TUBO:" + p.Handle, Icon = "TUBOS", Data = p, Cells = new[] { p.Name, Mm(p.InnerDiameter), r is null ? "-" : N(r.AreaHa, 4), r is null ? "-" : N(r.Flow, 4) } });
            }
        }

        return SubList(new[] { "Trecho a jusante", "D [mm]", "A acum. [ha]", "Q [m3/s]" }, new double[] { 160, 60, 90, 80 }, rows);
    }

    // ------------------------------------------------------------------
    // Acoes
    // ------------------------------------------------------------------

    private void RunCommand(Cmd c, WC.Button anchor)
    {
        switch (c.Key)
        {
            case "NOVAREDE": OpenNewNetworkMenu(anchor); return;
            case "LOTE": OpenBatchMenu(anchor); return;
            case "REGRAS":
                {
                    List<string> handles = TargetHandles();
                    if (handles.Count == 0)
                    {
                        Say("Marque ou selecione os itens.");
                        return;
                    }

                    RunApply($"Regras em {handles.Count} item(ns)", db => HydroEditor.ApplyRules(db, handles));
                    return;
                }

            case "INVERTER":
                {
                    List<string> handles = TargetHandles();
                    if (handles.Count == 0)
                    {
                        Say("Marque ou selecione os tubos.");
                        return;
                    }

                    RunApply($"Inverter {handles.Count} tubo(s)", db => HydroEditor.Reverse(db, handles));
                    return;
                }

            case "MEDIDAS":
                {
                    List<string> handles = Targets().Select(r => r.Data).OfType<StructureInfo>().Select(x => x.Handle).ToList();
                    if (handles.Count == 0)
                        handles = Net?.Structures.Select(x => x.Handle).ToList() ?? new List<string>();
                    if (handles.Count == 0)
                    {
                        Say("Nenhuma estrutura na rede ativa.");
                        return;
                    }

                    UpdateRules(handles);
                    return;
                }

            case "AUTO":
                {
                    bool on = !_settings.RulesAuto;
                    RunApply(on ? "Regras automaticas ligadas" : "Regras automaticas desligadas", db =>
                    {
                        HydroSettings st = HydroStore.Load(db);
                        st.RulesAuto = on;
                        HydroStore.Save(db, st);
                    });
                    return;
                }

            case "DENTRO":
                {
                    bool on = !_settings.PipesInside;
                    RunApply(on ? "Tubos dentro da boca de lobo: ligado" : "Tubos dentro da boca de lobo: desligado", db =>
                    {
                        HydroSettings st = HydroStore.Load(db);
                        st.PipesInside = on;
                        HydroStore.Save(db, st);
                    });
                    return;
                }

            case "ZOOM": ZoomCurrent(); return;
            case "SELECIONAR": SelectHandles(TargetHandles()); return;
            case "TC": OpenTcMenu(anchor); return;
            case "MATRIZ":
                if (_current?.Data is PipeInfo)
                {
                    _tab = "Hidraulica";
                    BuildTabs();
                }
                else
                {
                    Say("Selecione um tubo na categoria Tubos.");
                }

                return;
            case "IRPARA":
                if (_current?.Data is HydroViolation v)
                    GoTo(v.Handle);
                return;
            case "CORRIGIR": OpenFixMenu(anchor); return;
            case "PLANILHA": SelectCategory("SAIDAS"); return;
            case "EXPORTAR": OpenExportMenu(anchor); return;
            case "ROTULOS": OpenLabelsMenu(anchor); return;
        }

        if (c.Send.Length > 0)
        {
            if (c.Send.StartsWith("HY", StringComparison.Ordinal) || c.Send.StartsWith("DREN", StringComparison.Ordinal))
                HydroApply.ActiveNetwork = Net?.Handle;
            Send(c.Send, c.Title + ": siga a linha de comando.");
        }
    }

    private List<string> TargetHandles() => Targets().Select(r => r.Data switch
    {
        StructureInfo s => s.Handle,
        PipeInfo p => p.Handle,
        CatchmentInfo c => c.Handle,
        _ => ""
    }).Where(h => h.Length > 0).ToList();

    /// <summary>Executa uma edicao no desenho por HYAPLICAR (uma transacao, um UNDO) e atualiza o painel.</summary>
    private void RunApply(string what, Action<AcadDb.Database> action)
    {
        HydroApply.Queue(what, action);
        Send("HYAPLICAR ", what + "...");
    }

    private void ApplyField(PipeInfo p)
    {
        string name = Text("nome");
        double? up = Number("fundomont");
        double? down = Number("fundojus");
        double? slope = Number("decliv");
        bool slopeChanged = slope.HasValue && Math.Abs((slope.Value / 100) - p.Slope) > 1e-6;
        bool upChanged = up.HasValue && Math.Abs(up.Value - p.UpInvert) > 5e-4;
        bool downChanged = down.HasValue && Math.Abs(down.Value - p.DownInvert) > 5e-4;
        RunApply("Editar " + p.Name, db => HydroEditor.EditPipe(db, p.Handle,
            name.Length > 0 && name != p.Name ? name : null,
            upChanged ? up : null,
            downChanged && !slopeChanged ? down : null,
            slopeChanged ? slope!.Value / 100 : null));
    }

    private void ApplyField(CatchmentInfo c)
    {
        double? cc = Number("c", 0);
        double? tc = Number("tc", 0);
        string structure = Text("estrutura");
        RunApply("Editar bacia " + c.Name, db => HydroEditor.EditCatchment(db, c.Handle, cc, tc, structure == c.StructureHandle ? null : structure));
    }

    private static string RangeM(PartParam p)
    {
        if (!p.IsRange && p.List.Count > 0)
        {
            string vals = string.Join(" / ", p.List.Take(8).Select(x => N(x * p.Factor, 2)));
            return $"lista {vals}{(p.List.Count > 8 ? " ..." : "")} m";
        }

        return double.IsNaN(p.Min) || double.IsNaN(p.Max) ? "m" : $"{N(p.Min * p.Factor, 3)}..{N(p.Max * p.Factor, 3)} m";
    }

    /// <summary>
    /// Grava os campos alterados das estruturas (uma ou o lote): nome/prefixo, tampa, fundo e as
    /// medidas da peca. "=expr" grava a regra; numero ou +/- grava valor fixo e tira a regra da
    /// medida. Tudo e validado antes de mexer no desenho; um UNDO por operacao.
    /// </summary>
    private void ApplyStructures(IReadOnlyList<StructureInfo> list, bool batch)
    {
        string name = batch ? "" : Text("nome");
        string prefix = batch ? Text("prefixo") : "";
        List<(string Target, string Text)> edits = new();
        foreach ((string key, SW.FrameworkElement el) in _fields.Where(kv => kv.Key.StartsWith("med:", StringComparison.Ordinal)))
        {
            if (el is not WC.TextBox box)
                continue;
            string t = box.Text.Trim();
            if (t.Length == 0 || t == (box.Tag as string ?? ""))
                continue;
            edits.Add((key[4..], t));
        }

        ApplyStructureEdits(list, edits, name, prefix, batch);
    }

    /// <summary>
    /// Nucleo da edicao das estruturas (aba e celulas da lista): alvo = tampa, fundo, "prof"
    /// (profundidade, grava o fundo) ou nome de medida da peca; texto = valor, +/- ou =regra.
    /// </summary>
    private const string DepthTarget = "prof";

    private void ApplyStructureEdits(IReadOnlyList<StructureInfo> list, List<(string Target, string Text)> edits, string name, string prefix, bool batch)
    {
        bool rename = (!batch && name.Length > 0 && name != list[0].Name) || prefix.Length > 0;
        if (edits.Count == 0 && !rename)
        {
            Say(batch ? "Preencha ao menos um campo." : "Nada mudou.");
            return;
        }

        List<string> errors = new();
        List<(string Handle, string? Name, Dictionary<string, string?> Rules, Dictionary<string, double> Part, double? Rim, double? Sump, double? Depth)> jobs = new();
        int i = 1;
        foreach (StructureInfo s in list)
        {
            Dictionary<string, string?> rules = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> part = new(StringComparer.OrdinalIgnoreCase);
            double? rim = null, sump = null, depth = null;
            foreach ((string target, string text) in edits)
            {
                bool isRim = target == HydroRules.Rim, isSump = target == HydroRules.Sump, isDepth = target == DepthTarget;
                if (isDepth && text.StartsWith('='))
                {
                    errors.Add("Profundidade: escreva a regra no fundo (ex. =tampa - 1,50)");
                    continue;
                }

                if (isDepth)
                {
                    double? d;
                    try
                    {
                        d = HydroRuleEngine.EditValue(text, s.Depth);
                    }
                    catch (HydroExprException ex)
                    {
                        errors.Add($"Profundidade: {ex.Message}");
                        continue;
                    }

                    if (d is null)
                        continue;
                    if (s.Rules.ContainsKey(HydroRules.Sump))
                    {
                        bool fromTpl = s.OwnRules.Template.Length > 0 && _snap.Templates.TryGetValue(s.OwnRules.Template, out HydroRuleTemplate? tp) && tp.Rules.ContainsKey(HydroRules.Sump);
                        rules[HydroRules.Sump] = fromTpl ? "" : null;
                    }
                    // Medida na tampa do desenho na hora de gravar (a do painel pode estar velha).
                    depth = d.Value;
                    continue;
                }

                PartParam? p = isRim || isSump ? null : s.Parameters.FirstOrDefault(x => x.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (!isRim && !isSump && p is null)
                {
                    errors.Add($"{s.Name}: a peca nao tem {target}");
                    continue;
                }

                if (text.StartsWith('='))
                {
                    if (!HydroExpr.TryParse(text, out HydroExpr? e, out string err))
                    {
                        errors.Add($"{target}: {err}");
                        continue;
                    }

                    rules[target] = e!.Text;
                    continue;
                }

                double current = isRim ? s.Rim : isSump ? s.Sump : p!.Meters;
                double? v;
                try
                {
                    v = HydroRuleEngine.EditValue(text, current);
                }
                catch (HydroExprException ex)
                {
                    errors.Add($"{target}: {ex.Message}");
                    continue;
                }

                if (v is null)
                    continue;

                // Valor digitado vira medida fixa: tira a regra propria, ou fixa por cima da do modelo.
                if (s.Rules.ContainsKey(target))
                {
                    bool fromTemplate = s.OwnRules.Template.Length > 0 && _snap.Templates.TryGetValue(s.OwnRules.Template, out HydroRuleTemplate? tpl) && tpl.Rules.ContainsKey(target);
                    rules[target] = fromTemplate ? "" : null;
                }

                if (isRim)
                {
                    rim = v;
                }
                else if (isSump)
                {
                    sump = v;
                }
                else
                {
                    double catalog = v.Value / p!.Factor;
                    if (p.IsRange && !double.IsNaN(p.Min) && !double.IsNaN(p.Max) && (catalog < p.Min - 1e-9 || catalog > p.Max + 1e-9))
                    {
                        errors.Add($"{s.Name}: {p.Name} = {N(v.Value, 3)} m fora da faixa {RangeM(p)}");
                        continue;
                    }

                    if (!p.IsRange && p.List.Count > 0 && !p.List.Any(x => Math.Abs(x - catalog) < 1e-6))
                    {
                        errors.Add($"{s.Name}: {p.Name} = {N(v.Value, 3)} m nao esta na lista do catalogo ({RangeM(p)})");
                        continue;
                    }

                    if (Math.Abs(catalog - p.Value) > 1e-9)
                        part[p.Name] = catalog;
                }
            }

            string? newName = !batch && name.Length > 0 && name != s.Name ? name
                : prefix.Length > 0 ? prefix + (i++).ToString("00", CultureInfo.InvariantCulture) : null;
            jobs.Add((s.Handle, newName, rules, part, rim, sump, depth));
        }

        if (errors.Count > 0)
        {
            Say(string.Join(" | ", errors.Distinct().Take(6)));
            return;
        }

        HydroStore.HoldMode hold = _settings.HoldMode;
        RunApply(batch ? $"Lote em {list.Count} estrutura(s)" : "Editar " + list[0].Name, db =>
        {
            List<string> ruled = new();
            foreach ((string h, string? newName, Dictionary<string, string?> rules, Dictionary<string, double> part, double? rim, double? sump, double? depth) in jobs)
            {
                if (rules.Count > 0)
                    HydroRules.Edit(db, new[] { h }, rules, null);
                if (part.Count > 0)
                    HydroEditor.SetParameters(db, h, part);
                if (newName is not null || rim.HasValue || sump.HasValue || depth.HasValue)
                    HydroEditor.EditStructure(db, h, newName, rim, sump, hold, depth: depth);
                if (rules.Values.Any(v => !string.IsNullOrEmpty(v)))
                    ruled.Add(h);
            }

            if (ruled.Count > 0)
                ApplyRulesNow(db, ruled, hold);
        });
    }

    private void ApplyBatchPipes(List<PipeInfo> list)
    {
        string slope = Text("decliv"), up = Text("fundomont"), down = Text("fundojus"), prefix = Text("prefixo");
        if (slope.Length == 0 && up.Length == 0 && down.Length == 0 && prefix.Length == 0)
        {
            Say("Preencha ao menos um campo.");
            return;
        }

        double? slopeValue = Number("decliv");
        RunApply($"Lote em {list.Count} tubo(s)", db =>
        {
            int i = 1;
            foreach (PipeInfo p in list)
            {
                HydroEditor.EditPipe(db, p.Handle,
                    prefix.Length > 0 ? prefix + (i++).ToString("00", CultureInfo.InvariantCulture) : null,
                    Relative(up, p.UpInvert), slopeValue.HasValue ? null : Relative(down, p.DownInvert), slopeValue / 100);
            }
        });
    }

    /// <summary>"+0.10" soma, "-0.30" subtrai, "12.345" e absoluto, vazio = nulo.</summary>
    private static double? Relative(string text, double current)
    {
        string t = text.Trim().Replace(',', '.');
        if (t.Length == 0)
            return null;
        // Com sinal na frente o valor e relativo ao atual (+0.10 sobe, -0.30 desce); sem sinal e absoluto.
        bool rel = t[0] == '+' || t[0] == '-';
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return null;
        return rel ? current + v : v;
    }

    private void SaveSettings()
    {
        HydroSettings s = HydroStore.FromFields(_settings, k => Text(k));
        RunApply("Parametros salvos", db => HydroStore.Save(db, s));
    }

    private void OpenNewNetworkMenu(WC.Button anchor)
    {
        WC.ContextMenu menu = new() { PlacementTarget = anchor };
        List<string> lists = HydroEditor.PartsListNames(AcApp.DocumentManager.MdiActiveDocument?.Database);
        if (lists.Count == 0)
            menu.Items.Add(new WC.MenuItem { Header = "(nenhuma parts list no desenho)", IsEnabled = false });
        foreach (string name in lists)
        {
            WC.MenuItem mi = new() { Header = "Nova rede com parts list " + name };
            string n = name;
            mi.Click += (_, _) => RunApply("Nova rede", db => _networkHandle = HydroEditor.CreateNetwork(db, n));
            menu.Items.Add(mi);
        }

        menu.IsOpen = true;
    }

    private void OpenBatchMenu(WC.Button anchor)
    {
        List<Row> targets = Targets();
        WC.ContextMenu menu = new() { PlacementTarget = anchor };
        if (targets.Count == 0)
        {
            menu.Items.Add(new WC.MenuItem { Header = "Marque os itens na lista (caixinha ou Espaco)", IsEnabled = false });
        }
        else
        {
            WC.MenuItem edit = new() { Header = $"Editar os {targets.Count} marcado(s) nas Propriedades" };
            edit.Click += (_, _) =>
            {
                foreach (Row r in targets)
                    r.Checked = true;
                OnChecksChanged();
                _tab = "Propriedades";
                BuildTabs();
            };
            menu.Items.Add(edit);
            WC.MenuItem swap = new() { Header = "Trocar familia / tamanho (HYTROCAR nos marcados)" };
            swap.Click += (_, _) =>
            {
                HydroApply.Preselect = TargetHandles();
                HydroApply.ActiveNetwork = Net?.Handle;
                Send("HYTROCAR ", "HYTROCAR: escolha a familia e o tamanho.");
            };
            menu.Items.Add(swap);
            WC.MenuItem rules = new() { Header = "Aplicar regras do Civil" };
            rules.Click += (_, _) => RunApply("Aplicar regras", db => HydroEditor.ApplyRules(db, TargetHandles()));
            menu.Items.Add(rules);
            if (_category == "TUBOS")
            {
                WC.MenuItem rev = new() { Header = "Inverter sentido" };
                rev.Click += (_, _) => RunApply("Inverter", db => HydroEditor.Reverse(db, TargetHandles()));
                menu.Items.Add(rev);
            }
        }

        menu.Items.Add(new WC.Separator());
        WC.MenuItem all = new() { Header = "Marcar todos" };
        all.Click += (_, _) => { foreach (Row r in _rows) r.Checked = true; OnChecksChanged(); };
        menu.Items.Add(all);
        WC.MenuItem none = new() { Header = "Desmarcar todos" };
        none.Click += (_, _) => { foreach (Row r in _rows) r.Checked = false; OnChecksChanged(); };
        menu.Items.Add(none);
        menu.IsOpen = true;
    }

    private void OpenTcMenu(WC.Button anchor)
    {
        WC.ContextMenu menu = new() { PlacementTarget = anchor };
        List<CatchmentInfo> targets = Targets().Select(r => r.Data).OfType<CatchmentInfo>().ToList();
        if (targets.Count == 0)
            menu.Items.Add(new WC.MenuItem { Header = "Marque as bacias", IsEnabled = false });
        foreach (Calc.TcMethod method in Enum.GetValues<Calc.TcMethod>())
        {
            WC.MenuItem mi = new() { Header = Calc.TimeOfConcentration.Name(method) + " (usa comprimento do talvegue e desnivel)", IsEnabled = targets.Count > 0 };
            Calc.TcMethod m = method;
            mi.Click += (_, _) => Send("HYTC " + Calc.TimeOfConcentration.Key(m) + " ", "HYTC: informe o desnivel de cada bacia.");
            menu.Items.Add(mi);
        }

        menu.IsOpen = true;
        if (targets.Count > 0)
            HydroApply.Preselect = targets.Select(c => c.Handle).ToList();
    }

    private void OpenFixMenu(WC.Button anchor)
    {
        WC.ContextMenu menu = new() { PlacementTarget = anchor };
        if (_current?.Data is not HydroViolation v)
        {
            menu.Items.Add(new WC.MenuItem { Header = "Selecione uma violacao", IsEnabled = false });
        }
        else
        {
            foreach ((string title, Action<AcadDb.Database>? action) in HydroChecker.Fixes(v, Net, _settings))
            {
                WC.MenuItem mi = new() { Header = title, IsEnabled = action is not null };
                Action<AcadDb.Database>? a = action;
                string t = title;
                mi.Click += (_, _) => RunApply(t, a!);
                menu.Items.Add(mi);
            }
        }

        menu.IsOpen = true;
    }

    private void OpenExportMenu(WC.Button anchor)
    {
        WC.ContextMenu menu = new() { PlacementTarget = anchor };
        WC.MenuItem xlsx = new() { Header = "Planilha de dimensionamento (.xlsx)" };
        xlsx.Click += (_, _) => Export("xlsx");
        menu.Items.Add(xlsx);
        WC.MenuItem html = new() { Header = "Relatorio (.html)" };
        html.Click += (_, _) => Export("html");
        menu.Items.Add(html);
        menu.IsOpen = true;
    }

    private void Export(string format)
    {
        NetworkInfo? net = Net;
        if (net is null)
            return;
        Microsoft.Win32.SaveFileDialog dlg = new()
        {
            Filter = format == "xlsx" ? "Planilha Excel (*.xlsx)|*.xlsx" : "Pagina HTML (*.html)|*.html",
            FileName = "HYDRO_" + net.Name + "." + format
        };
        if (dlg.ShowDialog(this) != true)
            return;
        try
        {
            List<HydroSizingRow> rows = HydroSizing.Rows(net, _snap, _results, _settings);
            if (format == "xlsx")
                HydroExport.Xlsx(dlg.FileName, net, rows, _violations, _settings);
            else
                HydroExport.Html(dlg.FileName, net, rows, _violations, _settings);
            Say("Exportado: " + dlg.FileName);
            PluginLog.Info(Module, "Exportado " + dlg.FileName);
        }
        catch (Exception ex)
        {
            PluginLog.Erro(Module, "Exportacao falhou", ex);
            Say("Exportacao falhou: " + ex.Message);
        }
    }

    private void OpenLabelsMenu(WC.Button anchor)
    {
        WC.ContextMenu menu = new() { PlacementTarget = anchor };
        WC.MenuItem pipes = new() { Header = "Rotulos de planta nos tubos da rede (estilo padrao)" };
        pipes.Click += (_, _) => RunApply("Rotulos nos tubos", db => HydroEditor.AddLabels(db, Net?.Handle, pipes: true, structures: false));
        menu.Items.Add(pipes);
        WC.MenuItem structs = new() { Header = "Rotulos de planta nas estruturas da rede (estilo padrao)" };
        structs.Click += (_, _) => RunApply("Rotulos nas estruturas", db => HydroEditor.AddLabels(db, Net?.Handle, pipes: false, structures: true));
        menu.Items.Add(structs);
        menu.IsOpen = true;
    }

    private void OpenListMenu()
    {
        WC.ContextMenu menu = new() { PlacementTarget = _list };
        if (Checkable)
        {
            WC.MenuItem mark = new() { Header = "Marcar selecionados" };
            mark.Click += (_, _) => { foreach (Row r in _list.SelectedItems.OfType<Row>()) r.Checked = true; OnChecksChanged(); };
            menu.Items.Add(mark);
            WC.MenuItem unmark = new() { Header = "Desmarcar selecionados" };
            unmark.Click += (_, _) => { foreach (Row r in _list.SelectedItems.OfType<Row>()) r.Checked = false; OnChecksChanged(); };
            menu.Items.Add(unmark);
            WC.MenuItem invert = new() { Header = "Inverter marcacao" };
            invert.Click += (_, _) => { foreach (Row r in _rows) r.Checked = !r.Checked; OnChecksChanged(); };
            menu.Items.Add(invert);
            menu.Items.Add(new WC.Separator());
        }

        if (_category == "ESTRUTURAS" && Net is NetworkInfo cn)
        {
            WC.MenuItem cols = new() { Header = "Colunas da peca" };
            foreach (IGrouping<string, PartParam> g in cn.Structures.SelectMany(x => x.Parameters).Where(p => p.Editable)
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                string key = g.Key;
                WC.MenuItem mi = new() { Header = $"{key} - {g.First().Description}", IsCheckable = true, IsChecked = _paramColumns.Contains(key) };
                mi.Click += (_, _) =>
                {
                    if (!_paramColumns.Remove(key))
                        _paramColumns.Add(key);
                    FillList();
                };
                cols.Items.Add(mi);
            }

            if (cols.Items.Count == 0)
                cols.Items.Add(new WC.MenuItem { Header = "(as pecas desta rede nao tem medidas editaveis)", IsEnabled = false });
            menu.Items.Add(cols);
            menu.Items.Add(new WC.Separator());
        }

        WC.MenuItem zoom = new() { Header = "Zoom" };
        zoom.Click += (_, _) => ZoomCurrent();
        menu.Items.Add(zoom);
        WC.MenuItem select = new() { Header = "Selecionar no desenho" };
        select.Click += (_, _) => SelectHandles(TargetHandles());
        menu.Items.Add(select);
        if (_current?.Data is NetworkInfo n)
        {
            WC.MenuItem use = new() { Header = "Usar esta rede no painel" };
            use.Click += (_, _) => { _networkHandle = n.Handle; Refresh(); };
            menu.Items.Add(use);
        }

        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------
    // Zoom / selecao
    // ------------------------------------------------------------------

    private void ZoomCurrent()
    {
        if (_current is not null)
            ZoomRow(_current);
    }

    private void ZoomRow(Row row)
    {
        switch (row.Data)
        {
            case NetworkInfo n: ZoomTo(n.Bounds); break;
            case StructureInfo s: ZoomTo(Bounds.Empty.Add(s.Point).Inflate(15)); break;
            case PipeInfo p: ZoomTo(Bounds.Empty.Add(p.Start).Add(p.End).Inflate(10)); break;
            case CatchmentInfo c:
                Bounds b = Bounds.Empty;
                foreach (P2 pt in c.Boundary)
                    b = b.Add(pt);
                if (b.IsEmpty)
                    b = b.Add(c.Centroid).Inflate(20);
                ZoomTo(b.Inflate(10));
                break;
            case HydroViolation v: GoTo(v.Handle); break;
            case HydroSizingRow r: GoTo(r.PipeHandle); break;
        }
    }

    private void GoTo(string handle)
    {
        NetworkInfo? net = Net;
        if (net?.StructureByHandle.GetValueOrDefault(handle) is StructureInfo s)
            ZoomTo(Bounds.Empty.Add(s.Point).Inflate(15));
        else if (net?.PipeByHandle.GetValueOrDefault(handle) is PipeInfo p)
            ZoomTo(Bounds.Empty.Add(p.Start).Add(p.End).Inflate(10));
        else if (_snap.Catchments.FirstOrDefault(c => c.Handle == handle) is CatchmentInfo c)
            ZoomTo(Bounds.Empty.Add(c.Centroid).Inflate(30));
        SelectHandles(new[] { handle });
    }

    private void ShowInPanel(string handle)
    {
        NetworkInfo? net = Net;
        if (net?.StructureByHandle.ContainsKey(handle) == true)
        {
            _category = "ESTRUTURAS";
            _selectedRow = "EST:" + handle;
        }
        else if (net?.PipeByHandle.ContainsKey(handle) == true)
        {
            _category = "TUBOS";
            _selectedRow = "TUBO:" + handle;
        }
        else
        {
            return;
        }

        FillCategories();
        BuildCommandBar();
        FillList();
    }

    private void ZoomTo(Bounds box)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || box.IsEmpty)
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

    private void SelectHandles(IEnumerable<string> handles)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        try
        {
            using (doc.LockDocument())
            {
                List<AcadDb.ObjectId> ids = handles.Select(h => HydroReader.Id(doc.Database, h)).Where(id => id.HasValue).Select(id => id!.Value).ToList();
                if (ids.Count > 0)
                    doc.Editor.SetImpliedSelection(ids.ToArray());
                Say($"{ids.Count} item(ns) selecionado(s) no desenho.");
            }
        }
        catch (Exception ex)
        {
            Say("Selecao falhou: " + ex.Message);
        }
    }
}
