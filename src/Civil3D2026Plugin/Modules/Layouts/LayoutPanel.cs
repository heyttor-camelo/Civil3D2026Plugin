using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Civil3D2026Plugin.Infrastructure;
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

    /// <summary>Viewport copiada pelo "Copiar layer state" (botao direito), e o nome dela para os menus.</summary>
    private (ObjectId Id, string Nome)? _camadasCopiadas;
    private string? _layerState;
    private string? _bloco;
    private List<PageSetupItem> _pageSetups = new();
    private string? _pageSetup;
    private List<string>? _dispositivos;
    private List<string>? _estilos;
    private KeyMapConfig _kmCfg = new();
    private List<KeyMapSheet> _kmResumo = new();
    private readonly TextFilter _filtro = new();
    private List<SheetText2> _textos = new();
    private readonly HashSet<ObjectId> _textosMarcados = new();
    private readonly NumberingRule _regra = new();
    private Database? _regraDb;
    private readonly SheetBlockOptions _prancha = SheetBlockOptions.Carregar();
    private readonly HashSet<Document> _docsLigados = new();
    private readonly Dictionary<string, string> _valoresCarimbo = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshQueued;

    // ------------------------------------------------------------------
    // Controles
    // ------------------------------------------------------------------

    /// <summary>Navegacao das categorias, na coluna da esquerda (como a arvore do Project Explorer).</summary>
    private WC.StackPanel _categoryBar = new();

    /// <summary>Cabecalho da categoria aberta: icone, nome e quantidade.</summary>
    private WC.StackPanel _categoryHeader = new();
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
    /// <summary>A lista atual tem a coluna de caixinhas (a barra de espaco so marca nessas).</summary>
    private bool _listaComCheck;
    /// <summary>Contagem de textos escolhidos na aba Busca (atualizada sem reconstruir a aba).</summary>
    private WC.TextBlock? _lbEscolhidos;
    private WC.TextBox _find = new();
    private WC.StackPanel _tabStrip = new();
    private WC.ContentControl _tabContent = new();
    private WC.TextBlock _status = new();
    private readonly Dictionary<string, WM.ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);

    private readonly WM.Brush _vpTraco = ToolTheme.Brush(200, 200, 200);
    private readonly WM.Brush _vpTracoPapel = ToolTheme.Brush(90, 96, 110); // sobre a folha branca
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
        ("PRANCHAS", "pranchas", "Pranchas do eixo"),
        ("VIEWPORTS", "ligar", "Viewports"),
        ("PAGESETUP", "imprimir", "Page setup"),
        ("NUMERACAO", "numero", "Numeracao"),
        ("LAYERSTATE", "camadas", "Layer state"),
        ("CARIMBO", "bloco", "Carimbo"),
        ("MAPACHAVE", "mapa", "Mapa-chave")
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
            "Reordena TODAS as abas do desenho pelo nome (PR-2 antes de PR-10). A ordem atual (montada a mao, por exemplo) e substituida; pede confirmacao e guarda um ponto de restauracao.", new[] { "Confirme na janela.", "Para voltar: Desfazer (seta para tras na barra)." },
            p => p.Executar(() => LayoutOps.OrdenarPorNome(p.Doc!), $"Reordenar TODAS as {p._folhas.Count} abas de layout pelo nome (ex.: PR-2 antes de PR-10).\n\nA ordem atual das abas sera substituida pela ordem alfabetica/numerica dos nomes. Nenhum layout e alterado por dentro.")),
        new("COPIAR", "copiar", "Copiar layouts", "Layouts",
            "Copia os layouts marcados; a copia fica logo depois do original (as abas seguintes andam uma posicao).", new[] { "Marque os layouts." },
            p => p.ExecutarLongo("Copiando layouts", () => LayoutOps.Copiar(p.Doc!, p.Marcados()), confirmar: p.ConfLayouts("Criar uma copia de cada layout marcado. Cada copia fica logo depois do original, com a mesma vista nas viewports (e uma copia: a vista dela e igual a do original ate voce mudar)."))),
        new("EXCLUIR", "excluir", "Excluir layouts", "Layouts",
            "Exclui os layouts marcados (pede confirmacao).", new[] { "Marque os layouts." }, p => p.Excluir()),
        new("TRAVAR", "travar", "Travar viewports", "Viewports",
            "Trava a exibicao das viewports marcadas.", new[] { "Marque as viewports na miniatura ou na lista." }, p => p.Executar(() => LayoutOps.Travar(p.Doc!, p.VpsMarcadas(), true), p.ConfVps("Travar a exibicao das viewports marcadas (zoom e pan dentro delas ficam bloqueados). A vista nao muda.", 2))),
        new("DESTRAVAR", "destravar", "Destravar viewports", "Viewports",
            "Destrava a exibicao das viewports marcadas.", new[] { "Marque as viewports." }, p => p.Executar(() => LayoutOps.Travar(p.Doc!, p.VpsMarcadas(), false), p.ConfVps("Destravar a exibicao das viewports marcadas: um zoom ou pan dentro delas passa a mudar a vista da prancha.", 2))),
        new("LIGAR", "ligar", "Ligar viewports", "Viewports",
            "Liga as viewports marcadas.", new[] { "Marque as viewports." }, p => p.Executar(() => LayoutOps.Ligar(p.Doc!, p.VpsMarcadas(), true), p.ConfVps("Ligar as viewports marcadas (voltam a mostrar o Model).", 2))),
        new("DESLIGAR", "desligar", "Desligar viewports", "Viewports",
            "Desliga as viewports marcadas.", new[] { "Marque as viewports." }, p => p.Executar(() => LayoutOps.Ligar(p.Doc!, p.VpsMarcadas(), false), p.ConfVps("Desligar as viewports marcadas (ficam em branco e nao plotam ate religar). A vista nao muda.", 2))),
        new("VPLEVE", "desligar", "Modo leve (desligar/religar viewports)", "Viewports",
            "Desliga as viewports de todos os layouts (marcando-as) para trocar de layout sem regenerar, e religa so essas depois.",
            new[] { "Aba Modo leve: desligar todas, montar as pranchas, religar." }, p => p.SelectCategory("VIEWPORTS", "Leve")),
        new("ESCALA", "escala", "Escala das viewports", "Viewports",
            "Aplica a escala 1:N nas viewports marcadas (e a escala de anotacao, se existir no desenho).",
            new[] { "Marque as viewports.", "Escolha a escala na aba Escala." }, p => p.SelectCategory("VIEWPORTS", "Escala")),
        new("NORTE", "norte", "Seta do norte", "Viewports",
            "Insere a seta do norte (Arrow10 do Civil) nas viewports marcadas ou no ponto clicado, girada pela viewport; Girar acerta todos os nortes (as sheets do Plan & Profile acertam sozinhas ao atualizar).",
            new[] { "Marque as viewports de planta.", "Aba Norte e escala grafica: tamanho e canto." }, p => p.SelectCategory("VIEWPORTS", "Norte")),
        new("ESCALAGRAF", "escalagrafica", "Escala grafica", "Viewports",
            "Escala grafica pela escala da viewport (ex-ESCALAGRAF_V2.LSP): nas viewports marcadas ou clicando o ponto (comando ESCALAGRAF).",
            new[] { "Marque as viewports.", "Aba Norte e escala grafica: ate quantos metros e altura do texto." }, p => p.SelectCategory("VIEWPORTS", "Norte")),
        new("MNE", "malha", "Malha, norte e escala (padrao)", "Viewports",
            "Insere de uma vez a malha de coordenadas, o norte e a escala grafica nas viewports marcadas, pelo padrao salvo (troca os que ja existiam nelas). Para criar o padrao: lance os tres numa viewport, ajuste a mao e use \"Salvar padrao\" com so ela marcada (ou C3DMNE > Padrao).",
            new[] { "Marque as viewports.", "Clique: insere pelo padrao.", "Botao direito na viewport: salvar padrao / remover." }, p => p.Executar(() => LayoutConjunto.Inserir(p.Doc!, p.VpsMarcadas(), PadraoConjunto.Ler()), p.ConfVps("Inserir malha de coordenadas, norte e escala grafica nas viewports marcadas, pelo padrao salvo. Malha/norte/escala inseridos antes pelo plugin nessas viewports sao apagados e refeitos. A vista das viewports nao muda.", 1))),
        new("MNECFG", "config", "Configurar malha, norte e escala", "Viewports",
            "Janela do padrao: intervalo da malha e total da escala grafica (fixos em metros ou automaticos pela escala), altura do norte e dos textos, canto e deslocamento, layers (C3D_MALHA, C3D_NORTE, C3D_ESCALAGRAF).",
            new[] { "Ajuste os valores e OK.", "Depois use Malha, norte e escala nas viewports marcadas." }, p => p.ConfigurarConjunto()),
        new("NUMERAR", "numero", "Numerar pranchas", "Numeracao",
            "Escreve numero e total nos textos marcados (busca na aba Numeracao), na ordem das abas, com prefixo, sufixo, digitos e total configuraveis.",
            new[] { "Busque e marque os textos do numero e do total (uma vez).", "Clique para renumerar depois de criar, apagar ou reordenar layouts." }, p => p.Numerar()),
        new("PRANCHA", "prancha", "Inserir prancha", "Layouts",
            "Insere um bloco de prancha (A0 a A4 ou tamanho livre) com estendimentos, margens e marcas de dobra, na origem ou num ponto clicado.",
            new[] { "Configure na aba Prancha.", "Inserir na origem ou clicar o ponto." }, p => p.SelectCategory("LAYOUTS", "Prancha")),
        new("LAYERSTATE", "camadas", "Aplicar layer state", "Layer state",
            "Restaura o layer state escolhido como override nas viewports marcadas.",
            new[] { "Escolha o layer state na lista.", "Marque as viewports nas miniaturas.", "Aplicar." }, p => p.AplicarLayerState()),
        new("LSIMPORTAR", "abrir", "Importar layer state de outro DWG", "Layer state",
            "Traz o VP freeze e os overrides de layer de uma viewport de outro DWG/DWT e aplica nas viewports marcadas.",
            new[] { "Marque as viewports que recebem.", "Escolha o arquivo e a viewport de origem." }, p => p.ImportarCamadas(p.VpsMarcadas())),
        new("COPIARLAYERS", "camadas", "Estado das layers de outro DWG", "Layer state",
            "Copia on/off, freeze, lock, cor, linetype, lineweight e transparencia das layers de outro DWG/DWT; as layers de xref casam pelo arquivo do xref (comando C3DCOPIARLAYERS).",
            new[] { "Escolha o arquivo.", "Modo [Xrefs/Todas] na linha de comando." }, p => p.Enviar(C3DCommands.Utilitarios.CopiarLayers)),
        new("ORDEMDES", "ordenar", "Ordem de desenho de outro DWG", "Geral",
            "Copia a ordem de desenho (draw order) de outro DWG/DWT para este desenho, por layer ou por objeto (comando C3DORDEMDESENHO).",
            new[] { "Escolha o arquivo.", "Modo [Layers/Objetos] e espacos [Model/Layouts/Todos] na linha de comando." }, p => p.Enviar(C3DCommands.Utilitarios.OrdemDesenho)),
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
            "Refaz o mapa-chave em todos os layouts: base, contorno e numero de cada folha e hachura da folha atual (pede confirmacao).",
            new[] { "Confirme na janela." }, p => p.ExecutarLongo("Mapa-chave", () => LayoutKeyMap.Gerar(p.Doc!, true),
                confirmar: "Refazer o mapa-chave em TODOS os layouts que tem a area do mapa-chave: o desenho do mapa-chave em cada um e apagado e gerado de novo. As viewports das pranchas nao mudam.")),
        new("PPGERAR", "prancha", "Pranchas do eixo", "Pranchas do eixo",
            "Gera frames ao longo de um eixo no tamanho da viewport de planta do layout modelo, uma prancha por frame e um perfil por folha no tamanho da viewport de perfil.",
            new[] { "1. Escolha o layout modelo e as viewports de planta e perfil.", "2. Escolha o eixo.", "3. Estilo do perfil.", "4. Nome das pranchas e Gerar." }, p => p.SelectCategory("PRANCHAS", "Gerar")),
        new("PPATUALIZAR", "viewframe", "Realinhar as pranchas do eixo pelos frames", "Pranchas do eixo",
            "ATENCAO: MUDA A VISTA das viewports. Em todas as series de pranchas do eixo, recentraliza e gira a viewport de planta de cada sheet no seu frame do Model, e refaz estacas, perfil e linhas de corte. Ajustes de vista feitos a mao nessas viewports se perdem. Pede confirmacao e guarda um ponto de restauracao.",
            new[] { "Mova, gire ou estique os frames no Model e clique aqui.", "Para voltar: Desfazer (seta para tras na barra)." }, p => p.AtualizarTodasSeries()),
        new("PPZOOM", "viewframe", "Zoom no frame da folha", "Pranchas do eixo",
            "Mostra no Model o frame da folha selecionada.", new[] { "Selecione a folha na categoria Pranchas do eixo." }, p => p.ZoomFrame(null)),
        new("DESFAZER", "desfazer", "Desfazer / pontos de restauracao", "Geral",
            "Volta a vista das viewports, a ordem e o nome das abas para como estavam antes de uma acao do painel (cada acao guarda um ponto antes de mexer). Mostra o que muda antes de restaurar; layouts criados depois podem ser excluidos. Nao mexe no resto do desenho.",
            new[] { "Escolha o ponto (o mais recente e o de antes da ultima acao).", "Confira o resumo e Restaurar." }, p => p.Restaurar()),
        new("ATUALIZAR", "atualizar", "Reler o painel", "Geral",
            "So le os layouts do desenho de novo para o painel. NAO muda nada no desenho.", new[] { "Sem perguntas." }, p => p.Refresh())
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
        Width = 1280;
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
            // Esc nao fecha a janela (decisao do David: nenhum plugin fecha com Esc).
            if (e.Key == WI.Key.L && WI.Keyboard.Modifiers == (WI.ModifierKeys.Control | WI.ModifierKeys.Shift))
            {
                // Aba escondida com o log (para copiar e mandar).
                _mostrarLog = !_mostrarLog;
                if (_mostrarLog)
                    _tab = "Log";
                BuildTabs();
                e.Handled = true;
            }
        };
    }

    /// <summary>Aba Log visivel (Ctrl+Shift+L).</summary>
    private bool _mostrarLog;

    /// <summary>Modo leve ativo (viewports desligadas ou Model aliviado): aparece no rodape.</summary>
    private string _avisoLeve = "";

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

        _categoryBar = new WC.StackPanel { Margin = new SW.Thickness(0, 2, 0, 4) };
        _categoryHeader = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(8, 6, 8, 2) };
        _graphic = new WC.Canvas { Background = ToolTheme.GraphicBack, ClipToBounds = true };
        _graphicInfo = new WC.TextBlock { Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(8, 0, 8, 0) };
        _list = new WC.ListView { BorderBrush = T.Line, Background = T.Pane, Foreground = T.Text, AlternationCount = 2 };
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

        root.Children.Add(_categoryHeader);

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
        SheetGraphics.MostrarPapel = LayoutSettings.GetBool("vista_papel", true);
        var folhaPapel = new WC.CheckBox
        {
            Content = "Folha",
            IsChecked = SheetGraphics.MostrarPapel,
            VerticalAlignment = SW.VerticalAlignment.Center,
            Margin = new SW.Thickness(8, 0, 0, 0),
            ToolTip = "Mostra a folha do page setup: papel branco no tamanho do papel e a area de impressao (margens do dispositivo) tracejada"
        };
        folhaPapel.Click += (_, _) =>
        {
            SheetGraphics.MostrarPapel = folhaPapel.IsChecked == true;
            LayoutSettings.Set("vista_papel", SheetGraphics.MostrarPapel);
            RenderGraphic();
        };
        WC.DockPanel.SetDock(folhaPapel, WC.Dock.Right);
        graphicBar.Children.Add(folhaPapel);
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
        _list.SelectionChanged += (_, e) =>
        {
            SelecaoDaLista(e);
            OnRowSelected();
        };
        _list.SelectionMode = WC.SelectionMode.Extended;
        _list.ToolTip = "Clique: mostra a linha (nao marca).\nCtrl+clique e Shift+clique: selecionam e MARCAM varias linhas (Ctrl+clique de novo desmarca).\nCaixinha ou barra de espaco: marca/desmarca as selecionadas.\nBotao direito: acoes nas linhas selecionadas.";
        WC.ToolTipService.SetInitialShowDelay(_list, 1200);
        _list.PreviewKeyDown += (_, e) =>
        {
            // Barra de espaco: marca/desmarca as linhas selecionadas (pela primeira).
            if (e.Key != WI.Key.Space || _list.SelectedItems.Count == 0 || (_list.SelectedItems[0] as WC.ListViewItem)?.Content is not RowView rv0)
                return;
            if (WI.Keyboard.FocusedElement is WC.TextBox || !_listaComCheck)
                return;
            e.Handled = true;
            MarcarSelecionados(!rv0.Checked);
        };
        _list.MouseDoubleClick += (_, _) => OnRowDoubleClick();
        // O menu e montado na hora, pelas linhas selecionadas; precisa de um ContextMenu para o evento disparar.
        _list.ContextMenu = new WC.ContextMenu();
        _list.ContextMenuOpening += (_, e) => MenuLista(e);
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

        // Coluna da esquerda com as categorias (navegacao) | divisor | area de trabalho.
        WC.Grid outer = new();
        outer.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(LayoutSettings.GetDouble("nav_largura", 190)), MinWidth = 140, MaxWidth = 360 });
        outer.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(5) });
        outer.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });

        WC.DockPanel nav = new() { Background = T.Pane };
        var navTitulo = new WC.TextBlock
        {
            Text = "EXPLORADOR DE LAYOUTS",
            Foreground = T.Muted,
            FontSize = 10.5,
            FontWeight = SW.FontWeights.SemiBold,
            Margin = new SW.Thickness(10, 8, 8, 4)
        };
        WC.DockPanel.SetDock(navTitulo, WC.Dock.Top);
        nav.Children.Add(navTitulo);
        nav.Children.Add(new WC.ScrollViewer
        {
            Content = _categoryBar,
            VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled
        });
        outer.Children.Add(new WC.Border { Child = nav, BorderBrush = T.Line, BorderThickness = new SW.Thickness(0, 0, 1, 0) });

        WC.GridSplitter splitNav = new()
        {
            Width = 5,
            HorizontalAlignment = SW.HorizontalAlignment.Stretch,
            VerticalAlignment = SW.VerticalAlignment.Stretch,
            Background = T.Back,
            ResizeDirection = WC.GridResizeDirection.Columns,
            ResizeBehavior = WC.GridResizeBehavior.PreviousAndNext,
            Cursor = WI.Cursors.SizeWE
        };
        splitNav.DragCompleted += (_, _) => LayoutSettings.Set("nav_largura", outer.ColumnDefinitions[0].ActualWidth);
        WC.Grid.SetColumn(splitNav, 1);
        outer.Children.Add(splitNav);
        WC.Grid.SetColumn(root, 2);
        outer.Children.Add(root);
        return outer;
    }

    private void AddStyles()
    {
        SW.Style header = new(typeof(WC.GridViewColumnHeader));
        header.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Hover));
        header.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        header.Setters.Add(new SW.Setter(WC.Control.BorderBrushProperty, T.Line));
        header.Setters.Add(new SW.Setter(WC.Control.PaddingProperty, new SW.Thickness(6, 4, 6, 4)));
        header.Setters.Add(new SW.Setter(WC.Control.BorderThicknessProperty, new SW.Thickness(0, 0, 1, 1)));
        header.Setters.Add(new SW.Setter(WC.Control.FontWeightProperty, SW.FontWeights.SemiBold));
        header.Setters.Add(new SW.Setter(WC.Control.HorizontalContentAlignmentProperty, SW.HorizontalAlignment.Left));
        Resources[typeof(WC.GridViewColumnHeader)] = header;

        // Linha da lista com selecao nas cores do tema.
        SW.FrameworkElementFactory bd = new(typeof(WC.Border), "Bd");
        bd.SetValue(WC.Border.BackgroundProperty, new SW.TemplateBindingExtension(WC.Control.BackgroundProperty));
        bd.SetValue(WC.Border.PaddingProperty, new SW.Thickness(0, 3, 0, 3));
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
        // Linhas alternadas, como as grades do Project Explorer.
        SW.Trigger alternada = new() { Property = WC.ItemsControl.AlternationIndexProperty, Value = 1 };
        alternada.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, Alternada()));
        item.Triggers.Add(alternada);
        Resources[typeof(WC.ListViewItem)] = item;

        // Botoes, caixas de texto, checkboxes e ComboBox no tema (comum a todas as janelas do plugin).
        ToolStyles.Aplicar(Resources, T);
    }

    /// <summary>Fundo das linhas alternadas: o tom de hover do tema bem suave.</summary>
    private WM.Brush Alternada()
    {
        if (T.Hover is WM.SolidColorBrush s)
        {
            var b = new WM.SolidColorBrush(s.Color) { Opacity = 0.45 };
            b.Freeze();
            return b;
        }
        return WM.Brushes.Transparent;
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
                button.Click += (_, _) =>
                {
                    PluginLog.Info("Layouts", $"Botao: {c.Title} (categoria {_category})");
                    _acaoAtual = c.Title;
                    c.Run(this);
                };
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

    private void Say(string text)
    {
        MostrarStatus(text);
        PluginLog.Info("Layouts", text);
    }

    private void MostrarStatus(string text) => _status.Text = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "  " + text;

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
            using (PluginLog.Medir("Layouts", "Leitura dos layouts para o painel"))
                _folhas = LayoutReader.LerLayouts(db);
            _states = LayoutReader.LerLayerStates(db);
            _blocos = LayoutStamp.LerBlocos(db);
            _escalas = LayoutOps.EscalasDoDesenho(db);
            _pageSetups = LayoutPageSetup.Ler(db);
            _kmCfg = LayoutKeyMap.Ler(db);
            _kmResumo = LayoutKeyMap.Resumo(db);
            _textos = LayoutNumbering.Buscar(db, _filtro);
        }
        catch (System.Exception ex)
        {
            Say("Erro ao ler os layouts: " + ex.Message);
            PluginLog.Erro("Layouts", "Leitura do painel", ex);
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

        try
        {
            _avisoLeve = LayoutViewportPower.AvisoPlot(doc.Database);
        }
        catch (System.Exception)
        {
            _avisoLeve = "";
        }

        LigarDocumento(doc);
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
        "PRANCHAS" => _framesPP.Count,
        _ => -1
    };

    private void FillCategories()
    {
        _categoryBar.Children.Clear();
        foreach ((string id, string icon, string title) in Categories)
        {
            int count = CountFor(id);
            bool selected = id == _category;

            // Linha da arvore: marca de selecao, icone, nome e quantidade alinhada a direita.
            WC.DockPanel linha = new() { LastChildFill = true };
            var marca = new WC.Border { Width = 3, Background = selected ? T.Accent : WM.Brushes.Transparent, Margin = new SW.Thickness(0, 0, 8, 0) };
            WC.DockPanel.SetDock(marca, WC.Dock.Left);
            linha.Children.Add(marca);
            var img = new WC.Image { Source = IconOf(icon), Width = 16, Height = 16, VerticalAlignment = SW.VerticalAlignment.Center };
            WC.DockPanel.SetDock(img, WC.Dock.Left);
            linha.Children.Add(img);
            var qtd = new WC.TextBlock
            {
                Text = count >= 0 ? count.ToString(CultureInfo.InvariantCulture) : "",
                Foreground = T.Muted,
                VerticalAlignment = SW.VerticalAlignment.Center,
                Margin = new SW.Thickness(6, 0, 10, 0)
            };
            WC.DockPanel.SetDock(qtd, WC.Dock.Right);
            linha.Children.Add(qtd);
            linha.Children.Add(new WC.TextBlock
            {
                Text = title,
                Foreground = T.Text,
                FontWeight = selected ? SW.FontWeights.SemiBold : SW.FontWeights.Normal,
                VerticalAlignment = SW.VerticalAlignment.Center,
                Margin = new SW.Thickness(8, 0, 0, 0),
                TextTrimming = SW.TextTrimming.CharacterEllipsis
            });

            WC.Button button = new()
            {
                Content = linha,
                ToolTip = title + (count >= 0 ? $" ({count})" : ""),
                Padding = new SW.Thickness(0, 5, 0, 5),
                HorizontalContentAlignment = SW.HorizontalAlignment.Stretch,
                Background = selected ? T.Selected : WM.Brushes.Transparent,
                BorderBrush = WM.Brushes.Transparent,
                BorderThickness = new SW.Thickness(0)
            };
            string cat = id;
            button.Click += (_, _) => SelectCategory(cat);
            _categoryBar.Children.Add(button);

            if (selected)
            {
                _categoryHeader.Children.Clear();
                _categoryHeader.Children.Add(new WC.Image { Source = IconOf(icon), Width = 20, Height = 20, VerticalAlignment = SW.VerticalAlignment.Center });
                _categoryHeader.Children.Add(new WC.TextBlock
                {
                    Text = title,
                    Foreground = T.Text,
                    FontSize = 15,
                    FontWeight = SW.FontWeights.SemiBold,
                    VerticalAlignment = SW.VerticalAlignment.Center,
                    Margin = new SW.Thickness(8, 0, 0, 0)
                });
                if (count >= 0)
                    _categoryHeader.Children.Add(new WC.TextBlock
                    {
                        Text = $"{count} {(count == 1 ? "item" : "itens")}",
                        Foreground = T.Muted,
                        VerticalAlignment = SW.VerticalAlignment.Bottom,
                        Margin = new SW.Thickness(10, 0, 0, 2)
                    });
            }
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
                headers = new[] { "Sheet", "Alignment", "Estaca inicial", "Estaca final", "Extensao (m)", "Profile view", "Situacao" };
                widths = new[] { 110.0, 120, 85, 85, 70, 90, 220 };
                comCheck = false;
                rows = _framesPP.Where(f => Match(f.LayoutNome) || Match(f.EixoNome) || Match(f.Situacao)).Select(f => new Row
                {
                    Icon = LayoutSheetSeries.Valido(f.Layout) ? "prancha" : "viewframe",
                    Data = f,
                    Cells = new[]
                    {
                        f.LayoutNome.Length > 0 ? f.LayoutNome : "(sem layout)",
                        f.EixoNome,
                        LayoutSheetSeries.Estaca(f.E0),
                        LayoutSheetSeries.Estaca(f.E1),
                        f.Extensao.ToString("0.##", CultureInfo.InvariantCulture),
                        f.PerfilNome.Length > 0 ? f.PerfilNome : "-",
                        f.Situacao
                    }
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

        _linhasBase = rows;
        _listaComCheck = comCheck;
        rows = AplicarFiltrosColuna(rows);

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
            var col = new WC.GridViewColumn { Header = CabecalhoFiltravel(headers[i], i), Width = widths[i] };
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
    private sealed class RowView : System.ComponentModel.INotifyPropertyChanged
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

        private bool _checked;

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value)
                    return;
                _checked = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Checked)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
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
        if (sender is not WC.CheckBox cb || ItemDe(cb) is not { Tag: Row row } item)
            return;
        bool marcar = cb.IsChecked == true;
        // Caixinha de uma linha selecionada, com varias selecionadas (Shift/Ctrl): vale para todas.
        if (item.IsSelected && _list.SelectedItems.Count > 1)
        {
            MarcarSelecionados(marcar);
            return;
        }
        Marcar(row.Data, marcar);
        if (row.Data is SheetText2)
            EscolhidosMudaram();
        Status();
    }

    /// <summary>Marca (ou desmarca) as linhas selecionadas na lista.</summary>
    private void MarcarSelecionados(bool marcar)
    {
        var itens = _list.SelectedItems.OfType<WC.ListViewItem>().ToList();
        if (itens.Count == 0)
        {
            Say("Selecione linhas na lista (Shift e Ctrl para varias).");
            return;
        }
        bool texto = false;
        foreach (WC.ListViewItem it in itens)
        {
            if (it.Tag is not Row r)
                continue;
            Marcar(r.Data, marcar);
            if (it.Content is RowView rv)
                rv.Checked = marcar;
            texto |= r.Data is SheetText2;
        }
        if (texto)
            EscolhidosMudaram();
        Status();
    }

    /// <summary>Contagem na aba Busca; a aba Numerar (se aberta) e refeita. A Busca nao, para nao perder filtros digitados.</summary>
    private void EscolhidosMudaram()
    {
        if (_tab == "Numeracao")
        {
            BuildTabs();
            return;
        }
        if (_lbEscolhidos is not null)
            _lbEscolhidos.Text = TextoEscolhidos();
    }

    private string TextoEscolhidos() =>
        $"Escolhidos na lista: {_textosMarcados.Count} texto(s) em {TextosEscolhidos().Select(t => t.Layout).Distinct().Count()} layout(s)";

    private void Marcar(object? dado, bool marcar)
    {
        switch (dado)
        {
            case SheetData f:
                if (marcar) _layoutsMarcados.Add(f.Nome); else _layoutsMarcados.Remove(f.Nome);
                break;
            case SheetViewport v:
                if (marcar) _vpsMarcadas.Add(v.Id); else _vpsMarcadas.Remove(v.Id);
                AtualizarViewports();
                break;
            case SheetText2 tx:
                if (marcar) _textosMarcados.Add(tx.Id); else _textosMarcados.Remove(tx.Id);
                break;
        }
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
        _marcasCartao.Clear();
        string filtro = _find.Text.Trim();

        foreach (SheetData folha in _folhas.Where(f => filtro.Length == 0 || f.Nome.Contains(filtro, StringComparison.OrdinalIgnoreCase)))
        {
            var sp = new WC.StackPanel();
            var cab = new WC.DockPanel { Margin = new SW.Thickness(4, 3, 6, 2) };
            var marca = new WC.CheckBox
            {
                IsChecked = _layoutsMarcados.Contains(folha.Nome),
                VerticalAlignment = SW.VerticalAlignment.Center,
                ToolTip = "Marcar o layout para os comandos da barra (Ctrl+clique no cartao tambem marca; Shift+clique marca do ultimo clicado ate este)"
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
            _marcasCartao[folha] = marca;
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
                ToolTip = $"{folha.Nome}\n{Papel(folha)}\n{folha.Dispositivo}\nClique: mostra o layout   Ctrl+clique: marca/desmarca   Shift+clique: marca do ultimo clicado ate este\nDuplo clique abre na vista grande"
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
                if (e.ClickCount == 1 && ClicarCartao(f0))
                {
                    e.Handled = true;
                    return;
                }
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
                // Montado na hora de abrir: depois de "Copiar layer state" as outras viewports ja mostram "Colar".
                ContextMenu = new WC.ContextMenu()
            };
            SheetViewport v = vp;
            forma.ContextMenuOpening += (_, _) => forma.ContextMenu = MenuViewport(folha, v);
            forma.MouseEnter += (_, _) => { if (!_vpsMarcadas.Contains(v.Id)) forma.Fill = _vpHover; };
            forma.MouseLeave += (_, _) => { if (!_vpsMarcadas.Contains(v.Id)) forma.Fill = FillEstado(v); };
            forma.MouseRightButtonDown += (_, _) => MostrarFolha(folha);
            forma.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount > 1)
                    return;
                ClicarViewport(v);
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
                    Foreground = papel is null ? SheetGraphics.TextoSobre(folha) : SheetGraphics.ComPapel(folha) ? ToolTheme.Brush(20, 140, 60) : ToolTheme.Brush(120, 220, 140),
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
                forma.Stroke = marcada ? _vpSelTraco : vpi is { Planta: false } ? _vp3dTraco : SheetGraphics.MostrarPapel ? _vpTracoPapel : _vpTraco;
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
        (vp.Aplicado is null ? "" : $"\nLayer state: {vp.Aplicado}") + "\nClique (ou Ctrl+clique): marcar/desmarcar   Shift+clique: marca todas entre a ultima clicada e esta   Botao direito: acoes";

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
            mi.Click += (_, _) =>
            {
                _acaoAtual = texto;
                acao();
            };
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
        foreach (WC.MenuItem mi in ItensCamadas(new[] { vp }, $"{folha.Nome} #{vp.Indice}"))
            menu.Items.Add(mi);
        menu.Items.Add(new WC.Separator());
        Item("Malha, norte e escala pelo padrao", () => Executar(() => LayoutConjunto.Inserir(Doc!, so, PadraoConjunto.Ler())), "malha");
        Item("Salvar o padrao de malha, norte e escala desta viewport", () => Executar(() => LayoutConjunto.LerPadrao(Doc!, vp.Id, out _)));
        Item("Configurar malha, norte e escala...", ConfigurarConjunto);
        Item("Remover malha, norte e escala desta viewport", () => Executar(() => LayoutConjunto.Remover(Doc!, so)));
        menu.Items.Add(new WC.Separator());
        if (vp.Planta)
        {
            Item("Plan & Profile: viewport de PLANTA do layout base", () => DefinirModelo(folha, vp, planta: true), "prancha");
            Item("Plan & Profile: viewport de PERFIL do layout base", () => DefinirModelo(folha, vp, planta: false), "prancha");
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

    /// <summary>
    /// Copiar/colar layer state entre viewports, como o copiar/colar targets do Civil:
    /// copiar guarda a viewport; colar leva o VP freeze e os overrides dela para as escolhidas.
    /// <paramref name="alvo"/> sao as viewports clicadas (ou as linhas selecionadas na lista).
    /// </summary>
    private List<WC.MenuItem> ItensCamadas(IReadOnlyList<SheetViewport> alvo, string nomeAlvo)
    {
        var itens = new List<WC.MenuItem>();
        WC.MenuItem Novo(string texto, string? icone, Action acao)
        {
            var mi = new WC.MenuItem { Header = texto };
            if (icone is not null)
                mi.Icon = new WC.Image { Source = IconOf(icone), Width = 16, Height = 16 };
            mi.Click += (_, _) =>
            {
                _acaoAtual = texto;
                acao();
            };
            itens.Add(mi);
            return mi;
        }

        if (alvo.Count == 1)
        {
            SheetViewport vp = alvo[0];
            Novo("Copiar layer state desta viewport", "copiar", () =>
            {
                _camadasCopiadas = (vp.Id, nomeAlvo);
                MostrarStatus($"Layer state de {nomeAlvo} copiado. Botao direito em outra viewport (ou nas linhas selecionadas): Colar layer state.");
            }).ToolTip = "Guarda o VP freeze e os overrides de layer (cor, linetype, lineweight, plot style, transparencia) desta viewport para colar em outras.";
        }

        List<ObjectId> clicadas = alvo.Select(v => v.Id).ToList();
        Novo(alvo.Count == 1 ? "Importar layer state de viewport de outro DWG..." : $"Importar layer state de outro DWG nas {alvo.Count} viewport(s)...", "abrir",
            () => ImportarCamadas(clicadas))
            .ToolTip = "Escolha o DWG/DWT e a viewport dele: o VP freeze e os overrides de layer dela sao aplicados aqui.";

        if (_camadasCopiadas is null)
            return itens;
        (ObjectId origem, string nomeOrigem) = _camadasCopiadas.Value;

        List<ObjectId> destino = alvo.Select(v => v.Id).Where(id => id != origem).ToList();
        if (destino.Count > 0)
        {
            string onde = destino.Count == 1 && alvo.Count == 1 ? "nesta viewport" : $"nas {destino.Count} viewport(s) selecionada(s)";
            Novo($"Colar layer state de {nomeOrigem} {onde}", "camadas", () => Colar(origem, nomeOrigem, destino));
        }

        List<ObjectId> marcadas = _vpsMarcadas.Where(id => id != origem).ToList();
        if (marcadas.Count > 0 && !marcadas.SequenceEqual(destino))
            Novo($"Colar layer state de {nomeOrigem} nas {marcadas.Count} viewport(s) marcada(s)", "camadas", () => Colar(origem, nomeOrigem, marcadas));

        return itens;
    }

    private void Colar(ObjectId origem, string nomeOrigem, List<ObjectId> destino)
    {
        // O Executar rele o desenho: a coluna Aplicado vem do que ficou gravado em cada viewport.
        Executar(() => LayoutSheetSeries.ColarCamadas(Doc!, origem, destino, nomeOrigem),
            destino.Count > 1 ? ConfVps($"Colar o layer state de {nomeOrigem} em {destino.Count} viewport(s): o VP freeze e os overrides de layer delas sao substituidos pelos da origem. A vista nao muda.", 2, destino) : null);
        if (_category == "VIEWPORTS")
            FillList();
    }

    /// <summary>Botao direito nas linhas da lista de viewports: copiar/colar layer state.</summary>
    private void MenuLista(WC.ContextMenuEventArgs e)
    {
        int n = _list.SelectedItems.Count;
        var menu = new WC.ContextMenu();
        if (_listaComCheck && n > 0)
        {
            var marcar = new WC.MenuItem { Header = $"Marcar as {n} linha(s) selecionada(s)" };
            marcar.Click += (_, _) => MarcarSelecionados(true);
            var desmarcar = new WC.MenuItem { Header = $"Desmarcar as {n} linha(s) selecionada(s)" };
            desmarcar.Click += (_, _) => MarcarSelecionados(false);
            menu.Items.Add(marcar);
            menu.Items.Add(desmarcar);
        }

        List<SheetViewport> sel = _category != "VIEWPORTS" ? new List<SheetViewport>() : _list.SelectedItems.OfType<WC.ListViewItem>()
            .Select(i => (i.Tag as Row)?.Data).OfType<SheetViewport>().ToList();
        if (sel.Count > 0)
        {
            string nome = sel.Count == 1 ? NomeViewport(sel[0]) : $"{sel.Count} viewports";
            List<WC.MenuItem> itens = ItensCamadas(sel, nome);
            if (itens.Count > 0 && menu.Items.Count > 0)
                menu.Items.Add(new WC.Separator());
            foreach (WC.MenuItem mi in itens)
                menu.Items.Add(mi);
        }
        if (menu.Items.Count == 0)
        {
            e.Handled = true;
            return;
        }
        _list.ContextMenu = menu;
    }

    private string NomeViewport(SheetViewport vp)
    {
        SheetData? f = _folhas.FirstOrDefault(x => x.Viewports.Contains(vp));
        return f is null ? $"#{vp.Indice}" : $"{f.Nome} #{vp.Indice}";
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
                tabs.Add(("Norte", "Norte e escala grafica", BuildNorteEscala));
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
            case "PRANCHAS":
                tabs.Clear();
                tabs.Add(("Gerar", "Plan & Profile", BuildSheetSeries));
                tabs.Add(("Folha", "Sheet", BuildSheetFrame));
                break;
        }

        if (_mostrarLog)
            tabs.Add(("Log", "Log", () => LogView.Criar(T, "Layouts")));
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
                _tabContent.Content = key == "Log"
                    ? build()
                    : new WC.ScrollViewer { Content = build(), VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
        }
    }

    private WC.StackPanel Form() => new() { Margin = new SW.Thickness(12, 10, 12, 10) };

    /// <summary>Texto da aba; o negrito vira a faixa de titulo de secao (como na grade de propriedades).</summary>
    private WC.TextBlock Label(string t, bool bold = false) => new()
    {
        Text = t,
        Foreground = bold ? T.Text : T.Muted,
        FontWeight = bold ? SW.FontWeights.SemiBold : SW.FontWeights.Normal,
        Background = bold ? T.Hover : WM.Brushes.Transparent,
        Padding = bold ? new SW.Thickness(6, 3, 6, 3) : new SW.Thickness(0),
        Margin = new SW.Thickness(0, bold ? 8 : 6, 0, 2),
        TextWrapping = SW.TextWrapping.Wrap
    };

    /// <summary>Linha da grade de propriedades: nome a esquerda (coluna fixa), valor a direita, linha fina embaixo.</summary>
    private SW.FrameworkElement Prop(string nome, string valor)
    {
        var g = new WC.Grid();
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(150) });
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        g.Children.Add(new WC.TextBlock { Text = nome, Foreground = T.Muted, TextWrapping = SW.TextWrapping.Wrap, Margin = new SW.Thickness(6, 0, 8, 0) });
        var v = new WC.TextBlock { Text = valor, Foreground = T.Text, TextWrapping = SW.TextWrapping.Wrap };
        WC.Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return new WC.Border
        {
            Child = g,
            BorderBrush = T.Line,
            BorderThickness = new SW.Thickness(0, 0, 0, 1),
            Padding = new SW.Thickness(0, 3, 0, 3)
        };
    }

    private WC.Button ActionButton(string text, Action action)
    {
        var b = new WC.Button { Content = text, MinWidth = 110, Padding = new SW.Thickness(12, 4, 12, 4), Margin = new SW.Thickness(0, 10, 6, 0), HorizontalAlignment = SW.HorizontalAlignment.Left };
        b.Click += (_, _) =>
        {
            _acaoAtual = text + (_tab.Length > 0 ? $" (aba {_tab})" : "");
            action();
        };
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
                var diag = ActionButton("Diagnosticar os blocos da miniatura", () => DiagnosticarMiniatura(f));
                diag.ToolTip = "Escreve no log o que a miniatura le de cada bloco deste layout (tipo, definicao, pontos). Depois: Ctrl+Shift+L > Copiar e mande.";
                p.Children.Add(diag);
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
                p.Children.Add(Prop("Layer state aplicado", v.Aplicado ?? "(nenhum registrado)"));
                if (Doc is Document docVp)
                {
                    try
                    {
                        (int congeladas, int overrides) = LayoutOps.ResumoCamadas(docVp.Database, v.Id);
                        p.Children.Add(Prop("Layers congeladas nesta viewport", congeladas.ToString()));
                        p.Children.Add(Prop("Layers com override nesta viewport", overrides.ToString()));
                    }
                    catch (System.Exception ex)
                    {
                        PluginLog.Diag("Layouts", "Resumo das camadas da viewport: " + ex.Message);
                    }
                }
                p.Children.Add(Label("O layer state fica registrado na viewport quando e aplicado pelo plugin (aplicar, colar, importar ou layout base). Se depois as layers forem mudadas a mao, o registro continua com o ultimo aplicado; as contagens acima sao as atuais."));
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
                : "Previa (na ordem das abas): " + string.Join(", ", OrdemAbas(marcados).Take(4).Select((n, i) => $"{n} -> {r.Nome(i, n)}")) + (marcados.Count > 4 ? ", ..." : "");
        }
        foreach (WC.TextBox tb in new[] { prefixo, baseNome, sufixo, inicio, digitos })
            tb.TextChanged += (_, _) => Previa();
        numerar.Click += (_, _) => Previa();
        Previa();

        p.Children.Add(ActionButton("Renomear", () =>
        {
            RenameRule regra = Regra();
            List<string> nomes = Marcados();
            Executar(() =>
            {
                OpResult r = LayoutOps.Renomear(Doc!, nomes, regra);
                // Os layouts renomeados continuam marcados (o Refresh tiraria os nomes que sumiram).
                foreach (KeyValuePair<string, string> kv in LayoutOps.UltimasRenomeacoes)
                {
                    if (_layoutsMarcados.Remove(kv.Key))
                        _layoutsMarcados.Add(kv.Value);
                }

                return r;
            }, nomes.Count == 0 ? null :
                $"Renomear {nomes.Count} layout(s):\n\n" + string.Join("\n", nomes.Take(12).Select((n, i) => $"   {n}  ->  {regra.Nome(i, n)}")) +
                (nomes.Count > 12 ? $"\n   ... e mais {nomes.Count - 12}" : ""));
        }));
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
            previa.Text = LerEscala(combo.Text, FatorAtual(), out double c) ? "Vai aplicar custom scale " + LayoutOps.EscalaTexto(c, FatorAtual()) : "Escala invalida.";
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
            if (!LerEscala(combo.Text, FatorAtual(), out double custom)) { Say("Escala invalida. Use 2, 0.5, 1:500 ou 4:1."); return; }
            Executar(() => LayoutOps.Escalar(Doc!, VpsMarcadas(), custom, FatorAtual()),
                ConfVps($"Mudar a escala das viewports marcadas para {EscalaTexto(custom)}. O que aparece em cada uma muda (o zoom e refeito pelo centro da vista).", 1));
        }));
        Dispatcher.BeginInvoke(new Action(Previa), SW.Threading.DispatcherPriority.Background);
        return p;
    }

    /// <summary>Textos escolhidos na Busca (caixinha marcada), na ordem das abas.</summary>
    private List<SheetText2> TextosEscolhidos() => _textos.Where(t => _textosMarcados.Contains(t.Id)).OrderBy(t => t.Ordem).ToList();

    private void DiagnosticarMiniatura(SheetData f)
    {
        if (Doc is not Document doc)
            return;
        try
        {
            List<string> linhas = LayoutReader.DiagnosticarBlocos(doc.Database, f.Id);
            PluginLog.Info("Layouts", $"Diagnostico da miniatura de {f.Nome} ({linhas.Count} linha(s)): {f.Diagnostico}");
            foreach (string l in linhas)
                PluginLog.Info("Layouts", "Miniatura " + f.Nome + " " + l);
            Say($"Diagnostico de {f.Nome} no log ({linhas.Count} bloco(s)/linha(s)): Ctrl+Shift+L > Copiar e mande.");
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("Layouts", "Diagnostico da miniatura", ex);
            Say("Diagnostico falhou: " + ex.Message);
        }
    }

    /// <summary>Aba Norte e escala grafica (categoria Viewports).</summary>
    private SW.UIElement BuildNorteEscala()
    {
        WC.StackPanel p = Form();
        int n = _vpsMarcadas.Count;
        double D(WC.TextBox tb, double padrao) =>
            double.TryParse(tb.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : padrao;
        WC.StackPanel Campo(string rotulo, SW.UIElement c)
        {
            var sp = new WC.StackPanel { Margin = new SW.Thickness(0, 0, 14, 0) };
            sp.Children.Add(Label(rotulo));
            sp.Children.Add(c);
            return sp;
        }

        // ---- Norte ----
        p.Children.Add(Label("Seta do norte", true));
        p.Children.Add(Label("Rosa dos ventos Arrow10 do Civil 3D (bloco C3D_NORTE). Fica girada pela viewport em que esta: o norte do Model (+Y) como aparece no papel."));
        var tam = new WC.TextBox { Width = 60, Text = LayoutSettings.GetDouble("norte_mm", 20).ToString("0.##", CultureInfo.InvariantCulture), ToolTip = "Altura total da seta no papel (do S ao N), em mm." };
        var canto = new WC.ComboBox { Width = 170 };
        foreach (string c in new[] { "superior direito", "superior esquerdo", "inferior direito", "inferior esquerdo" })
            canto.Items.Add(c);
        canto.SelectedItem = LayoutSettings.Get("norte_canto", "superior direito");
        if (canto.SelectedIndex < 0)
            canto.SelectedIndex = 0;
        canto.ToolTip = "Canto da viewport (por dentro) onde a seta entra.";
        var afast = new WC.TextBox { Width = 60, Text = LayoutSettings.GetDouble("norte_afast", 8).ToString("0.##", CultureInfo.InvariantCulture), ToolTip = "Distancia da seta ate as bordas da viewport, em mm." };
        var l1 = new WC.WrapPanel();
        l1.Children.Add(Campo("Altura (mm)", tam));
        l1.Children.Add(Campo("Canto", canto));
        l1.Children.Add(Campo("Afastamento (mm)", afast));
        p.Children.Add(l1);
        void LerNorte()
        {
            LayoutSettings.Set("norte_mm", D(tam, 20));
            LayoutSettings.Set("norte_canto", canto.SelectedItem as string ?? "superior direito");
            LayoutSettings.Set("norte_afast", D(afast, 8));
        }
        var b1 = new WC.WrapPanel();
        b1.Children.Add(ActionButton($"Inserir nas viewports marcadas ({n})", () =>
        {
            LerNorte();
            Executar(() => LayoutNorte.InserirNasViewports(Doc!, VpsMarcadas(), D(tam, 20), canto.SelectedItem as string ?? "superior direito", D(afast, 8)),
                ConfVps("Inserir uma seta do norte em cada viewport marcada. A vista nao muda.", 2));
        }));
        b1.Children.Add(ActionButton("Clicar o ponto...", () => { LerNorte(); Enviar(C3DCommands.Layouts.Norte); }));
        b1.Children.Add(ActionButton("Girar todos pelas viewports", () => Executar(() => LayoutNorte.Girar(Doc!),
            "Girar TODAS as setas do norte do plugin, em todos os layouts, pelo giro da viewport embaixo de cada uma. As viewports nao mudam.")));
        p.Children.Add(b1);
        p.Children.Add(Label("Nas sheets do Plan & Profile: ponha a seta no layout base, dentro da viewport de planta. Cada sheet copia a seta e o Atualizar gira pela viewport dela."));

        // ---- Escala grafica ----
        p.Children.Add(Label("Escala grafica", true));
        p.Children.Add(Label("Mesmo desenho da ESCALAGRAF (barra em duas faixas, metragem a cada 10 m, \"ESCALA GRAFICA\" e 1:N), pela escala da viewport. Sai como bloco."));
        var max = new WC.TextBox { Width = 60, Text = LayoutEscalaGrafica.UltimoMaximo.ToString("0.##", CultureInfo.InvariantCulture), ToolTip = "Ate quantos metros vai a barra." };
        var txt = new WC.TextBox { Width = 60, Text = LayoutEscalaGrafica.UltimaAltura.ToString("0.##", CultureInfo.InvariantCulture), ToolTip = "Altura do texto no papel, em mm (a altura da barra acompanha)." };
        var afastE = new WC.TextBox { Width = 60, Text = LayoutSettings.GetDouble("eg_afast", 8).ToString("0.##", CultureInfo.InvariantCulture), ToolTip = "Distancia ate as bordas da viewport (canto inferior esquerdo), em mm." };
        var l2 = new WC.WrapPanel();
        l2.Children.Add(Campo("Ate (m)", max));
        l2.Children.Add(Campo("Texto (mm)", txt));
        l2.Children.Add(Campo("Afastamento (mm)", afastE));
        p.Children.Add(l2);
        void LerEscala()
        {
            LayoutEscalaGrafica.UltimoMaximo = D(max, 25);
            LayoutEscalaGrafica.UltimaAltura = D(txt, 4);
            LayoutSettings.Set("eg_afast", D(afastE, 8));
        }
        var b2 = new WC.WrapPanel();
        b2.Children.Add(ActionButton($"Inserir nas viewports marcadas ({n})", () =>
        {
            LerEscala();
            Executar(() => LayoutEscalaGrafica.InserirNasViewports(Doc!, VpsMarcadas(), D(max, 25), D(txt, 4), D(afastE, 8)),
                ConfVps("Inserir uma escala grafica em cada viewport marcada. A vista nao muda.", 2));
        }));
        b2.Children.Add(ActionButton("Clicar o ponto... (ESCALAGRAF)", () => { LerEscala(); Enviar(C3DCommands.Layouts.EscalaGrafica); }));
        p.Children.Add(b2);
        return p;
    }

    private SW.UIElement BuildNumbering()
    {
        WC.StackPanel p = Form();
        if (Doc?.Database is Database dbAtual && !ReferenceEquals(_regraDb, dbAtual))
        {
            // Formato da ultima numeracao deste desenho.
            _regraDb = dbAtual;
            if (LayoutNumbering.FormatoGravado(dbAtual) is NumberingRule gravada)
            {
                _regra.Modelo = gravada.Modelo;
                _regra.ModeloTotal = gravada.ModeloTotal;
            }
        }
        p.Children.Add(Label("Numerar pranchas", true));

        List<SheetText2> escolhidos = TextosEscolhidos();
        bool daBusca = escolhidos.Count > 0;
        List<SheetText2> fonte = daBusca ? escolhidos : _textos.Where(t => t.Papel.Length > 0).OrderBy(t => t.Ordem).ToList();
        List<string> layouts = fonte.GroupBy(t => t.Layout).OrderBy(g => g.First().Ordem).Select(g => g.Key).ToList();
        int nTotal = fonte.Count(t => t.Papel == "Total");
        string origem = daBusca
            ? $"{escolhidos.Count} texto(s) escolhido(s) na Busca, em {layouts.Count} layout(s)" + (layouts.Count > 0 ? $": {layouts.First()} a {layouts.Last()}, na ordem das abas." : ".")
            : fonte.Count > 0
                ? $"Nenhum texto escolhido na Busca: numera os {fonte.Count} texto(s) ja gravados como NUMERO/TOTAL ({layouts.Count} layout(s))."
                : "Nenhum texto escolhido: na aba Busca, busque os textos da prancha, selecione os que valem (Shift/Ctrl) e marque a caixinha.";
        p.Children.Add(Destaque(origem));
        if (daBusca && escolhidos.Count > layouts.Count)
            p.Children.Add(Label($"Ha layouts com mais de um texto escolhido: todos os textos do mesmo layout recebem o mesmo numero."));

        WC.ComboBox Modelo(string atual, string[] sugestoes, double largura)
        {
            var cb = new WC.ComboBox { Width = largura, IsEditable = true, IsTextSearchEnabled = false, HorizontalAlignment = SW.HorizontalAlignment.Left };
            foreach (string s in sugestoes)
                cb.Items.Add(s);
            cb.Text = atual;
            return cb;
        }
        WC.ComboBox modelo = Modelo(_regra.Modelo, new[] { "{00}", "{000}", "PRANCHA: {000}", "{00}/{T00}", "{000}/{T000}", "PRANCHA: {000}/{T000}", "{000} de {T000}" }, 220);
        modelo.ToolTip = Dica("Texto que vai no lugar do numero. {000} = numero com 3 algarismos (008); {T000} = total com 3 algarismos (012); {0} e {T} sem zeros a esquerda.",
            "Ex.: PRANCHA: {000}/{T000} vira PRANCHA: 008/012.");
        WC.ComboBox modeloTotal = Modelo(_regra.ModeloTotal, new[] { "/{T00}", "{T00}", "/{T000}", "{T000}", "de {T000}" }, 140);
        modeloTotal.ToolTip = "So para textos gravados como TOTAL (o total num texto separado do numero).";
        var inicio = new WC.TextBox { Width = 60, HorizontalAlignment = SW.HorizontalAlignment.Left, Text = _regra.Inicio.ToString(CultureInfo.InvariantCulture) };
        // Layouts que vao receber numero, na ordem: com escolhidos, os deles; sem, todos (ou os marcados), como o Numerar faz.
        List<string> Alvo(bool soMarcados)
        {
            List<string> marcados = Marcados();
            List<string> baseLista = daBusca ? layouts : _folhas.OrderBy(f => f.Ordem).Select(f => f.Nome).ToList();
            return soMarcados ? baseLista.Where(l => marcados.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList() : baseLista;
        }
        int sugerido = Alvo(_regra.SoLayoutsMarcados).Count;
        var total = new WC.TextBox
        {
            Width = 60,
            HorizontalAlignment = SW.HorizontalAlignment.Left,
            Text = (_regra.Total ?? sugerido).ToString(CultureInfo.InvariantCulture),
            ToolTip = "Sugerido: quantidade de layouts que vao ser numerados. Edite se o conjunto tiver mais folhas (ex.: outras disciplinas)."
        };
        var soMarcados = new WC.CheckBox { Content = "Numerar so os layouts marcados (os outros nao mudam)", IsChecked = _regra.SoLayoutsMarcados, Margin = new SW.Thickness(0, 8, 0, 0) };
        var previa = new WC.TextBlock { Foreground = T.Text, Margin = new SW.Thickness(0, 8, 0, 0), TextWrapping = SW.TextWrapping.Wrap };
        var aviso = new WC.TextBlock { Foreground = T.Muted, TextWrapping = SW.TextWrapping.Wrap };

        void Ler()
        {
            _regra.Modelo = modelo.Text;
            _regra.ModeloTotal = modeloTotal.Text;
            _regra.Inicio = int.TryParse(inicio.Text, out int i0) ? i0 : 1;
            _regra.SoLayoutsMarcados = soMarcados.IsChecked == true;
            _regra.Marcados = Marcados();
            List<string> alvo = Alvo(_regra.SoLayoutsMarcados);
            int auto = alvo.Count;
            // Total igual ao sugerido = automatico (acompanha a selecao).
            _regra.Total = int.TryParse(total.Text, out int t0) && t0 > 0 && t0 != auto ? t0 : null;
            int tot = _regra.Total ?? auto;
            var linhas = alvo.Take(3).Select((l, i) => $"{l}:  {_regra.Numero(_regra.Inicio + i, tot)}").ToList();
            if (alvo.Count > 3)
                linhas.Add($"...  {alvo.Last()}:  {_regra.Numero(_regra.Inicio + alvo.Count - 1, tot)}");
            previa.Text = linhas.Count > 0 ? "Previa:  " + string.Join("     ", linhas) : $"Previa:  {_regra.Numero(_regra.Inicio, tot)}, {_regra.Numero(_regra.Inicio + 1, tot)} ...";
            if (nTotal > 0)
                previa.Text += $"     (total separado: {_regra.TotalTexto(_regra.Inicio, tot)})";
            aviso.Text = NumberingRule.TemCampo(_regra.Modelo) ? "" : "O texto do numero nao tem {0}/{000}: todas as pranchas recebem o mesmo texto.";
        }
        foreach (WC.TextBox tb in new[] { inicio, total })
            tb.TextChanged += (_, _) => Ler();
        foreach (WC.ComboBox cb in new[] { modelo, modeloTotal })
        {
            cb.AddHandler(WC.Primitives.TextBoxBase.TextChangedEvent, new WC.TextChangedEventHandler((_, _) => Ler()));
            cb.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Ler));
        }
        soMarcados.Click += (_, _) => { _regra.Total = null; total.Text = Alvo(soMarcados.IsChecked == true).Count.ToString(CultureInfo.InvariantCulture); Ler(); };

        var g = new WC.WrapPanel();
        void Campo(string rotulo, SW.UIElement c)
        {
            var sp = new WC.StackPanel { Margin = new SW.Thickness(0, 0, 14, 0) };
            sp.Children.Add(Label(rotulo));
            sp.Children.Add(c);
            g.Children.Add(sp);
        }
        Campo("Texto do numero", modelo);
        Campo("Primeiro numero", inicio);
        Campo("Total de folhas", total);
        Campo("Texto do total separado", modeloTotal);
        p.Children.Add(g);
        p.Children.Add(Label("{000} = numero com 3 algarismos, {T000} = total com 3 algarismos ({0} e {T}: sem zeros a esquerda)."));
        p.Children.Add(soMarcados);
        p.Children.Add(previa);
        p.Children.Add(aviso);
        Ler();
        p.Children.Add(ActionButton("Numerar agora", Numerar));
        p.Children.Add(Label("Os textos escolhidos ficam gravados como NUMERO, com este formato: o NP e a numeracao automatica das sheets (Plan & Profile) usam os mesmos."));
        return p;
    }

    private SW.UIElement BuildTextSearch()
    {
        WC.StackPanel p = Form();
        p.Children.Add(Label("Buscar textos nos layouts (TEXT, MTEXT e atributos de bloco, inclusive dentro do carimbo)", true));
        p.Children.Add(Label("Filtre ate aparecer o texto que numera a prancha em cada layout. Na lista, selecione os que valem (Shift e Ctrl para varios) e marque a caixinha de um deles: vale para todos os selecionados (barra de espaco tambem). A aba Numerar usa os textos marcados, na ordem das abas, e ja sabe o total."));

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

        _lbEscolhidos = Label(TextoEscolhidos(), true);
        p.Children.Add(_lbEscolhidos);
        var escolha = new WC.WrapPanel();
        escolha.Children.Add(ActionButton("Marcar os selecionados", () => MarcarSelecionados(true)));
        escolha.Children.Add(ActionButton("Marcar todos da lista", () =>
        {
            foreach (SheetText2 t in _textos) _textosMarcados.Add(t.Id);
            FillList();
            BuildTabs();
        }));
        escolha.Children.Add(ActionButton("Limpar", () =>
        {
            _textosMarcados.Clear();
            FillList();
            BuildTabs();
        }));
        p.Children.Add(escolha);
        p.Children.Add(ActionButton("Ir para Numerar", () => SelectCategory("NUMERACAO", "Numeracao")));

        p.Children.Add(Label("Gravar o papel no desenho (opcional: Numerar ja grava os escolhidos como NUMERO; use TOTAL so se o total fica num texto separado):", true));
        var moverLayer = new WC.CheckBox { Content = $"Tambem mover para a layer {LayoutOps.LayerNumero} / {LayoutOps.LayerTotal} (compativel com o NP antigo)", IsChecked = false };
        p.Children.Add(moverLayer);
        var acoes = new WC.WrapPanel();
        acoes.Children.Add(ActionButton("Gravar como NUMERO", () => MarcarTextos("Numero", _textosMarcados.ToList(), moverLayer.IsChecked == true)));
        acoes.Children.Add(ActionButton("Gravar como TOTAL", () => MarcarTextos("Total", _textosMarcados.ToList(), moverLayer.IsChecked == true)));
        acoes.Children.Add(ActionButton("Tirar o papel", () => MarcarTextos("", _textosMarcados.ToList())));
        p.Children.Add(acoes);
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
                Executar(() => LayoutSheetBlock.InserirEmLayouts(Doc!, o, ponto, Marcados()),
                    ConfLayouts("Inserir o bloco de prancha em cada layout marcado."));
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
        List<SheetText2> escolhidos = TextosEscolhidos();
        if (escolhidos.Count == 0 && _textos.Count(t => t.Papel.Length > 0) == 0)
        {
            SelectCategory("NUMERACAO", "Busca");
            Say("Nenhum texto escolhido: busque os textos da prancha, selecione os que valem (Shift/Ctrl) e marque a caixinha.");
            SW.MessageBox.Show(
                "Ainda nao ha texto escolhido para o numero da prancha.\n\n" +
                "1. Na aba Busca, filtre ate aparecer o texto que numera a prancha em cada layout (pode estar dentro do bloco do carimbo).\n" +
                "2. Selecione na lista os que valem (Shift e Ctrl para varios) e marque a caixinha de um deles: vale para todos os selecionados.\n" +
                "3. Na aba Numerar, escreva o texto (ex.: PRANCHA: {000}/{T000}) e clique em Numerar agora.",
                "Numerar pranchas", SW.MessageBoxButton.OK, SW.MessageBoxImage.Information);
            return;
        }
        if (!NumberingRule.TemCampo(_regra.Modelo) && escolhidos.Count > 1)
        {
            Say("O texto do numero precisa de {0}, {00} ou {000} (o lugar do numero).");
            return;
        }
        _regra.Textos = escolhidos.Count > 0 ? escolhidos.Select(t => t.Id).ToList() : null;
        _regra.Marcados = Marcados();
        if (_regra.SoLayoutsMarcados && _regra.Marcados.Count == 0)
        {
            Say("Marque os layouts que devem ser numerados.");
            return;
        }
        ExecutarLongo("Numerando as pranchas", confirmar: $"Reescrever o numero e o total nos textos marcados ({(_regra.SoLayoutsMarcados ? $"so nos {_regra.Marcados?.Count ?? 0} layout(s) marcado(s)" : "em todos os layouts")}), na ordem das abas. O texto atual desses campos e substituido.", op: () =>
        {
            OpResult r = LayoutNumbering.Numerar(Doc!, _regra);
            // A lista mostra o texto novo e o papel gravado.
            if (Doc is Document d)
                _textos = LayoutNumbering.Buscar(d.Database, _filtro);
            return r;
        });
    }

    /// <summary>Opcoes de prancha atuais (para o comando C3DPRANCHA).</summary>
    internal static SheetBlockOptions? PranchaAtual => _instance?._prancha;

    private SW.UIElement BuildViewportPower()
    {
        WC.StackPanel p = Form();
        int desligadas = 0, layersLeve = 0, labelsLeve = 0;
        try
        {
            if (Doc is not null)
            {
                desligadas = LayoutViewportPower.Desligadas(Doc.Database);
                (layersLeve, labelsLeve) = LayoutViewportPower.Aliviados(Doc.Database);
            }
        }
        catch (System.Exception)
        {
        }
        var titulo = Label("Modo leve: viewports desligadas enquanto se monta a prancha", true);
        titulo.ToolTip = "Trocar de layout com viewports ligadas obriga o AutoCAD a regenerar o Model em cada uma (corredores, superficies). " +
                         "As viewports sao desligadas e marcadas (XData C3D_VPOFF); Religar liga de volta exatamente essas, sem mexer nas que voce mesmo desligou. " +
                         "Quase tudo deste painel funciona com as viewports desligadas (numeracao, prancha, page setup, escala, pranchas do eixo).";
        p.Children.Add(titulo);
        p.Children.Add(Prop("Desligadas pelo plugin", desligadas.ToString(CultureInfo.InvariantCulture) +
                                                      (layersLeve + labelsLeve > 0 ? $"   |   Model: {layersLeve} layer(s) congelada(s), {labelsLeve} label(s) oculta(s)" : "")));

        var botoes = new WC.WrapPanel();
        WC.Button Botao(string texto, string dica, Func<OpResult> op)
        {
            WC.Button b = ActionButton(texto, () => ExecutarLongo("Modo leve", op));
            b.ToolTip = dica;
            botoes.Children.Add(b);
            return b;
        }
        Botao("Desligar todas (menos o layout aberto)", "Desliga as viewports de todos os layouts menos o aberto; com as opcoes abaixo, tambem congela as layers e oculta as labels do Civil.",
            () => LayoutViewportPower.Desligar(Doc!, null, poupaAtual: true));
        Botao("Desligar nos layouts marcados", "Desliga as viewports so dos layouts marcados (e aplica as opcoes abaixo).",
            () => LayoutViewportPower.Desligar(Doc!, Marcados(), poupaAtual: false));
        Botao("Religar todas", "Religa as viewports que o plugin desligou e desfaz as opcoes abaixo (descongela as layers e mostra as labels que o plugin escondeu).",
            () => LayoutViewportPower.Religar(Doc!, null));
        Botao("Religar nos layouts marcados", "Religa as viewports dos layouts marcados; layers e labels voltam quando nao sobrar viewport desligada pelo plugin.",
            () => LayoutViewportPower.Religar(Doc!, Marcados()));
        p.Children.Add(botoes);

        // Model mais leve (junto com o desligar).
        var lbModel = Label("Ao desligar, deixar o Model mais leve", true);
        lbModel.ToolTip = "Vale para os botoes Desligar; Religar todas desfaz. O plugin marca o que mudou (XData C3D_LEVE) e so desfaz isso: o que voce congelou ou ocultou continua como estava.";
        p.Children.Add(lbModel);
        var chLayers = new WC.CheckBox { Content = "Congelar as layers:", IsChecked = LayoutViewportPower.CongelarLayers, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(0, 0, 8, 0) };
        chLayers.ToolTip = "Freeze das layers da lista (a layer atual nao pode ser congelada). Ex.: F-VT-NOTAVEL.";
        var tbLayers = new WC.TextBox { Width = 240, Text = LayoutViewportPower.Layers, VerticalContentAlignment = SW.VerticalAlignment.Center };
        tbLayers.ToolTip = "Nomes separados por virgula; aceita * e ? (ex.: F-VT-NOTAVEL, C-TOPO-*).";
        chLayers.Click += (_, _) => LayoutViewportPower.CongelarLayers = chLayers.IsChecked == true;
        tbLayers.TextChanged += (_, _) => LayoutViewportPower.Layers = tbLayers.Text;
        var linhaLayers = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(0, 4, 0, 0) };
        linhaLayers.Children.Add(chLayers);
        linhaLayers.Children.Add(tbLayers);
        p.Children.Add(linhaLayers);
        var chLabels = new WC.CheckBox { Content = "Ocultar as labels do Civil 3D (todas, no Model)", IsChecked = LayoutViewportPower.OcultarLabels, Margin = new SW.Thickness(0, 6, 0, 0) };
        chLabels.ToolTip = "Labels de alignment, profile, superficie, pipe, parcel, etc. (objetos AECC_*LABEL*) ficam invisiveis. Labels dentro do estilo dos pontos (COGO points) nao sao objetos separados e ficam.";
        chLabels.Click += (_, _) => LayoutViewportPower.OcultarLabels = chLabels.IsChecked == true;
        p.Children.Add(chLabels);

        var auto = new WC.CheckBox
        {
            Content = "Automatico: ao sair de um layout desliga as viewports dele; ao entrar, religa as dele",
            IsChecked = LayoutViewportPower.Automatico,
            Margin = new SW.Thickness(0, 12, 0, 0),
            ToolTip = "A regeneracao das viewports acontece ao entrar no layout (no religar): a troca fica rapida mas o custo nao some. " +
                      "O ganho real vem de manter as viewports desligadas durante a montagem e religar no fim. O automatico nao mexe nas layers nem nas labels."
        };
        auto.Click += (_, _) => { LayoutViewportPower.Automatico = auto.IsChecked == true; Say(auto.IsChecked == true ? "Modo leve automatico ligado." : "Modo leve automatico desligado."); };
        p.Children.Add(auto);
        var lbRegen = Label("Alternativa do proprio AutoCAD: LAYOUTREGENCTL = 2 guarda a lista de exibicao de todos os layouts (mais memoria, troca sem regenerar).");
        p.Children.Add(lbRegen);
        var regen = new WC.WrapPanel { Margin = new SW.Thickness(0, 4, 0, 0) };
        regen.Children.Add(ActionButton("LAYOUTREGENCTL = 2 (guardar todos)", () => Sysvar("LAYOUTREGENCTL", 2)));
        regen.Children.Add(ActionButton("LAYOUTREGENCTL = 1 (padrao)", () => Sysvar("LAYOUTREGENCTL", 1)));
        p.Children.Add(regen);
        return p;
    }

    /// <summary>
    /// Custom scale do texto do combo: "1.3333  (1:750)" usa o 1:750 exato (o numero da frente e
    /// arredondado); "2", "0,5", "1:500" e "4:1" como sempre.
    /// </summary>
    private static bool LerEscala(string? texto, double fator, out double custom)
    {
        string t = (texto ?? "").Trim();
        int a = t.IndexOf('('), b = t.LastIndexOf(')');
        if (a >= 0 && b > a + 1 && LayoutOps.TentarEscala(t[(a + 1)..b].Trim(), fator, out custom))
            return true;
        int par = t.IndexOf("  (", StringComparison.Ordinal);
        if (par > 0)
            t = t[..par];
        return LayoutOps.TentarEscala(t.Trim(), fator, out custom);
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

    /// <summary>Os nomes na ordem das abas do desenho (a lista do painel pode estar ordenada por outra coluna).</summary>
    private List<string> OrdemAbas(IReadOnlyCollection<string> nomes)
    {
        if (Doc is null)
            return nomes.ToList();
        var set = new HashSet<string>(nomes, StringComparer.OrdinalIgnoreCase);
        List<string> r = LayoutOps.OrdemAtual(Doc.Database).Where(set.Contains).ToList();
        r.AddRange(nomes.Where(n => !r.Contains(n, StringComparer.OrdinalIgnoreCase)));
        return r;
    }

    private List<string> Marcados() => _folhas.Where(f => _layoutsMarcados.Contains(f.Nome)).Select(f => f.Nome).ToList();

    private List<ObjectId> VpsMarcadas() => _vpsMarcadas.ToList();

    /// <summary>
    /// Roda uma acao que mexe no desenho: com <paramref name="confirmar"/>, mostra antes o que vai
    /// acontecer; sempre guarda um ponto de restauracao (botao Desfazer) antes de mexer.
    /// </summary>
    private void Executar(Func<OpResult> op, string? confirmar = null)
    {
        if (Doc is null)
        {
            Say("Nenhum desenho aberto.");
            return;
        }
        if (confirmar is not null && !Confirmar(confirmar))
        {
            MostrarStatus("Cancelado: nada foi alterado.");
            return;
        }
        LayoutRestauracao.Capturar(Doc.Database, _acaoAtual ?? "acao do painel");
        _acaoAtual = null;
        try
        {
            OpResult r = op();
            MostrarStatus(r.Mensagem);
            if (r.Falhas > 0)
                PluginLog.Aviso("Layouts", r.Mensagem);
            else
                PluginLog.Info("Layouts", r.Mensagem);
        }
        catch (System.Exception ex)
        {
            MostrarStatus("Erro: " + ex.Message + "  (detalhes: Ctrl+Shift+L)");
            PluginLog.Erro("Layouts", "Erro: " + ex.Message, ex);
        }
        Progresso.Mostrar("Relendo os layouts para o painel...", double.NaN);
        Refresh();
    }

    /// <summary>
    /// Operacao demorada: janela de progresso e cursor de espera enquanto roda. Cancelar so aparece
    /// nas operacoes que param entre etapas (gerar, atualizar, opcoes dos profiles).
    /// </summary>
    private void ExecutarLongo(string titulo, Func<OpResult> op, bool cancelavel = false, string? confirmar = null)
    {
        if (Doc is not null && confirmar is not null && !Confirmar(confirmar))
        {
            MostrarStatus("Cancelado: nada foi alterado.");
            return;
        }
        _acaoAtual ??= titulo;
        WI.Mouse.OverrideCursor = WI.Cursors.Wait;
        try
        {
            using (ProgressoJanela.Abrir(titulo, this, cancelavel))
                Executar(op);
        }
        finally
        {
            WI.Mouse.OverrideCursor = null;
        }
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
        ExecutarLongo($"Excluindo {nomes.Count} layout(s)", () => LayoutOps.Excluir(Doc!, nomes));
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
        if (_vpsMarcadas.Count > 1 && ConfVps($"Aplicar o layer state \"{_layerState}\" nas viewports marcadas: VP freeze e overrides de layer delas sao substituidos. A vista nao muda.", 2) is string texto && !Confirmar(texto))
        {
            MostrarStatus("Cancelado: nada foi alterado.");
            return;
        }
        LayoutRestauracao.Capturar(doc.Database, $"aplicar o layer state {_layerState}");

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
        Executar(() => LayoutStamp.Aplicar(Doc!, _bloco, nomes, valores),
            ConfLayouts($"Escrever no carimbo {_bloco} os valores preenchidos ({string.Join(", ", valores.Keys.Take(6))}{(valores.Count > 6 ? ", ..." : "")}) em cada layout marcado. O valor atual dessas tags e substituido."));
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

    /// <summary>Marcar todos / Desmarcar: age nas linhas visiveis da lista (respeita Localizar e filtros).</summary>
    private void MarcarLayouts(bool marcar)
    {
        bool algum = false;
        foreach (WC.ListViewItem item in _list.Items)
        {
            switch ((item.Tag as Row)?.Data)
            {
                case SheetData f:
                    if (marcar) _layoutsMarcados.Add(f.Nome); else _layoutsMarcados.Remove(f.Nome);
                    algum = true;
                    break;
                case SheetViewport v:
                    if (marcar) _vpsMarcadas.Add(v.Id); else _vpsMarcadas.Remove(v.Id);
                    algum = true;
                    break;
                case SheetText2 tx:
                    if (marcar) _textosMarcados.Add(tx.Id); else _textosMarcados.Remove(tx.Id);
                    algum = true;
                    break;
            }
        }
        if (!algum)
        {
            // Lista sem caixas (layer states, pranchas do eixo...): vale para os layouts.
            _layoutsMarcados.Clear();
            if (marcar)
                foreach (SheetData f in _folhas)
                    _layoutsMarcados.Add(f.Nome);
            else
                _vpsMarcadas.Clear();
        }
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
            Executar(() => LayoutPageSetup.AplicarNomeado(Doc!, _pageSetup, Marcados()),
                ConfLayouts($"Aplicar o page setup \"{_pageSetup}\" (impressora, papel, area e escala de plotagem) em cada layout marcado."));
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
                d == manter ? null : d, canon, st == manter ? null : st, layout11.IsChecked == true),
                ConfLayouts("Trocar impressora/papel/estilo de plotagem (os campos escolhidos) em cada layout marcado."));
        }));
        return p;
    }

    /// <summary>Pasta do desenho atual, para os navegadores de arquivo abrirem nela ("" se nao foi salvo).</summary>
    private string PastaDoDesenho()
    {
        try
        {
            string? nome = Doc?.Database.Filename;
            string? pasta = string.IsNullOrEmpty(nome) ? null : System.IO.Path.GetDirectoryName(nome);
            return pasta is not null && System.IO.Directory.Exists(pasta) ? pasta : "";
        }
        catch (System.Exception)
        {
            return "";
        }
    }

    /// <summary>Janela do padrao de malha, norte e escala.</summary>
    private void ConfigurarConjunto()
    {
        PadraoConjunto p = PadraoConjunto.Ler();
        if (LayoutConjuntoJanela.Editar(p, Doc?.Database, this))
            Doc?.Editor.WriteMessage("\nPadrao de malha, norte e escala salvo: " + p.Resumo() + ".\n");
    }

    /// <summary>Importar layer state de uma viewport de outro DWG para as viewports dadas.</summary>
    private void ImportarCamadas(IReadOnlyList<ObjectId> destinos)
    {
        if (Doc is not Document doc)
            return;
        if (destinos.Count == 0)
        {
            Say("Marque (ou selecione na lista) as viewports que recebem o layer state.");
            return;
        }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Layer state de uma viewport de outro desenho",
            Filter = "Desenhos e templates (*.dwg;*.dwt)|*.dwg;*.dwt|Todos os arquivos (*.*)|*.*",
            InitialDirectory = PastaDoDesenho()
        };
        if (dlg.ShowDialog(this) != true)
            return;
        if (string.Equals(System.IO.Path.GetFullPath(dlg.FileName), System.IO.Path.GetFullPath(doc.Database.Filename), StringComparison.OrdinalIgnoreCase))
        {
            Say("Esse e o proprio desenho atual; use Copiar/Colar layer state no botao direito da viewport.");
            return;
        }

        List<ViewportExterna> vps;
        try
        {
            vps = LayoutCamadasExternas.Viewports(dlg.FileName);
        }
        catch (System.Exception ex)
        {
            Say("Nao foi possivel ler o arquivo: " + ex.Message);
            return;
        }
        if (vps.Count == 0)
        {
            Say("O arquivo nao tem viewports nos layouts.");
            return;
        }
        string? rotulo = EscolherDaLista("Viewport de origem", $"Viewports de {System.IO.Path.GetFileName(dlg.FileName)} (layout, numero, escala, tamanho):", vps.Select(v => v.Rotulo).ToList());
        ViewportExterna? origem = vps.FirstOrDefault(v => v.Rotulo == rotulo);
        if (origem is null)
            return;

        string arquivo = dlg.FileName;
        List<ObjectId> alvo = destinos.ToList();
        Executar(() => LayoutCamadasExternas.Importar(doc, arquivo, origem, alvo),
            alvo.Count > 1 ? ConfVps($"Aplicar o layer state da viewport {origem.Rotulo.Split("   ")[0]} de {System.IO.Path.GetFileName(arquivo)} em {alvo.Count} viewport(s): VP freeze e overrides delas sao substituidos. A vista nao muda.", 2, alvo) : null);
        if (_category == "VIEWPORTS")
            FillList();
    }

    private void ImportarPageSetups()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importar page setups",
            Filter = "Desenhos e templates (*.dwg;*.dwt)|*.dwg;*.dwt|Todos os arquivos (*.*)|*.*",
            InitialDirectory = PastaDoDesenho()
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
            Executar(() => LayoutKeyMap.Gerar(doc, true), "Refazer o mapa-chave em TODOS os layouts que tem a area do mapa-chave (o desenho antigo do mapa-chave e apagado). As viewports nao mudam.");
        }));
        botoes.Children.Add(ActionButton("Atualizar", () => Executar(() => LayoutKeyMap.Gerar(Doc!, false))));
        botoes.Children.Add(ActionButton("Remover", () => Executar(() => LayoutKeyMap.Remover(Doc!), "Apagar o mapa-chave de TODOS os layouts.")));
        botoes.Children.Add(ActionButton("Inserir no layout atual...", () => Enviar(C3DCommands.Layouts.MapaChave + " Inserir")));
        p.Children.Add(botoes);
        p.Children.Add(Label("Tudo o que o mapa-chave cria fica nas layers MAPA_CHAVE_* e e refeito a cada atualizacao; nao edite esses objetos a mao. A base fica no bloco C3D_MAPACHAVE: \"Salvar e gerar\" refaz o bloco a partir dos objetos do Model."));
        return p;
    }

    private void Status()
    {
        int layoutsVp = _folhas.Count(f => f.Viewports.Any(v => _vpsMarcadas.Contains(v.Id)));
        string ls = _layerState is not null ? $"; layer state \"{_layerState}\"" : "";
        string bl = _bloco is not null ? $"; carimbo \"{_bloco}\"" : "";
        int filtros = FiltrosAtivos();
        string fl = filtros > 0 ? $"; {_list.Items.Count} de {_linhasBase.Count} linha(s) ({filtros} filtro(s) de coluna)" : "";
        _status.Text = $"{_layoutsMarcados.Count} layout(s) marcado(s); {_vpsMarcadas.Count} viewport(s) em {layoutsVp} layout(s){ls}{bl}{fl}." +
                       (_avisoLeve.Length > 0 ? "   |   MODO LEVE: " + _avisoLeve : "");
    }
}
