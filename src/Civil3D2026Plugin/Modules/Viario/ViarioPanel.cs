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
using WM = System.Windows.Media;
using WS = System.Windows.Shapes;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Explorador da rede viaria (C3DVIARIOPAINEL), no estilo do Project Explorer:
/// barra de ferramentas com os comandos, arvore de objetos (secoes, eixos e
/// elementos, intersecoes e esquinas, ilhas, sinalizacao, Civil), grafico do
/// objeto, propriedades editaveis e recomendacoes dos manuais do DNIT.
/// </summary>
internal sealed class ViarioPanel : SW.Window
{
    private static ViarioPanel? _instance;

    // Paleta discreta (tons do AutoCAD claro).
    private static readonly WM.Brush Back = Brush(243, 243, 243);
    private static readonly WM.Brush Pane = Brush(255, 255, 255);
    private static readonly WM.Brush Line = Brush(204, 204, 204);
    private static readonly WM.Brush Muted = Brush(110, 110, 110);
    private static readonly WM.Brush Accent = Brush(0, 102, 180);
    private static readonly WM.Brush Warn = Brush(196, 120, 0);

    // ---------------- dados ----------------
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
    private string? _selectedId;

    // ---------------- controles ----------------
    private readonly WC.TreeView _tree = new();
    private readonly WC.TextBlock _title = new();
    private readonly WC.TextBlock _subtitle = new();
    private readonly WC.Canvas _graphic = new();
    private readonly WC.StackPanel _props = new();
    private readonly WC.WrapPanel _actions = new();
    private readonly WC.ListView _hintList = new();
    private readonly WC.CheckBox _hintFilter = new() { Content = "So do item selecionado", IsChecked = true };
    private readonly WC.TextBlock _hintCount = new();
    private readonly WC.TextBlock _status = new();
    private readonly WC.TextBlock _message = new();
    private readonly Dictionary<string, SW.FrameworkElement> _fields = new();

    /// <summary>O que o grafico desenha para o item atual.</summary>
    private Action? _render;

    // ------------------------------------------------------------------
    // Comandos (barra de ferramentas)
    // ------------------------------------------------------------------

    private sealed record Cmd(string Key, string Send, string Title, string Group, string Description, string[] Steps);

    private static readonly Cmd[] Commands =
    {
        new("EIXO", "C3DVIARIOEIXO ", "Lancar eixo", "Desenho",
            "Desenha eixos (retas e arcos) com a secao corrente; bordos e esquinas aparecem enquanto desenha.",
            new[] { "Clique os pontos ([Arco] alterna, [Desfazer] volta um ponto).", "Enter termina o eixo; o proximo clique comeca outro.", "[Secao] no primeiro ponto troca a secao." }),
        new("ADICIONAR", "C3DVIARIO AD ", "Adicionar eixos", "Desenho",
            "Transforma polilinhas, linhas, arcos ou circulos existentes em eixos da rede.",
            new[] { "Selecione as curvas e confirme." }),
        new("ROTATORIA", "C3DVIARIO RO ", "Rotatoria", "Desenho",
            "Anel circular com a secao corrente; eixos que chegam viram ramos.",
            new[] { "Clique o centro.", "Raio da ilha central (face do meio-fio)." }),
        new("MODELOS", "C3DVIARIOMODELOS ", "Modelos de intersecao", "Desenho",
            "Intersecoes prontas (cruz, T, Y, gotas, faixas livres, conversao, rotatorias).",
            new[] { "Escolha modelo, secoes e parametros.", "Clique o ponto e gire." }),
        new("REMOVEREIXO", "C3DVIARIO RE ", "Tirar eixo da rede", "Desenho",
            "A polilinha fica no desenho, mas deixa de ser eixo.",
            new[] { "Selecione os eixos e confirme." }),

        new("RAIO", "C3DVIARIORAIO ", "Raio", "Intersecoes",
            "Raio de esquina, de vertice do eixo, dos cantos de uma ilha ou de todas as esquinas de um eixo.",
            new[] { "Clique perto da esquina (ou dentro de uma ilha rosa).", "Arraste ou digite; [Padrao] volta ao da secao." }),
        new("ALARGAR", "C3DVIARIOALARGAR ", "Alargamento / taper", "Intersecoes",
            "Faixa adicional na aproximacao com armazenamento e taper.",
            new[] { "Clique no ramo, perto da intersecao.", "Lado, largura, taper e formato.", "Arraste o armazenamento." }),
        new("GOTA", "C3DVIARIOGOTA ", "Gota", "Intersecoes",
            "Ilha em gota na aproximacao de via sem canteiro (Tipo II).",
            new[] { "Clique no ramo, perto da intersecao.", "Largura, recuo da ponta, taper.", "Arraste o comprimento." }),
        new("CONVERSAO", "C3DVIARIOCONVERSAO ", "Conversao no canteiro", "Intersecoes",
            "Faixa de conversao a esquerda tirada do canteiro, junto da pista de quem chega (Tipo IV).",
            new[] { "Clique na aproximacao, perto da intersecao.", "Largura e taper.", "Arraste o armazenamento." }),
        new("ABERTURA", "C3DVIARIOABERTURA ", "Abertura de canteiro", "Intersecoes",
            "Abertura do canteiro para retorno no meio da quadra.",
            new[] { "Clique no eixo, no centro da abertura.", "Arraste metade do comprimento." }),
        new("LIVRE", "C3DVIARIOLIVRE ", "Faixa livre a direita", "Intersecoes",
            "Esquina com faixa livre de conversao e ilha triangular (Tipo III).",
            new[] { "Clique perto da esquina.", "Largura e afastamento.", "Arraste o raio externo." }),
        new("ELEMREMOVER", "C3DVIARIOELEMREMOVER ", "Remover elemento", "Intersecoes",
            "Remove alargamento, gota, conversao, abertura ou faixa livre.",
            new[] { "Clique perto do elemento e confirme." }),

        new("ILHA", "C3DVIARIOILHA ", "Eliminar ilha", "Linhas",
            "A ilha clicada vira pavimento; [Restaurar] ou [Todas] desfaz.",
            new[] { "Clique dentro da ilha." }),
        new("SOLTAR", "C3DVIARIO SO ", "Soltar linhas", "Linhas",
            "Linhas geradas viram polilinhas comuns para edicao manual.",
            new[] { "Selecione as linhas; Soltar > Restaurar devolve." }),
        new("ATUALIZAR", "C3DVIARIO ATU ", "Atualizar rede", "Linhas",
            "Recalcula a rede inteira.", new[] { "Sem perguntas." }),

        new("CIVIL", "C3DVIARIOCIVIL G ", "Objetos do Civil", "Saidas",
            "Alinhamentos do Civil: eixos, bordos LD/LE e curb returns conectados.",
            new[] { "Gera ou atualiza; depois acompanham a rede." }),
        new("SINAL", "C3DVIARIOSINAL G ", "Sinalizacao horizontal", "Saidas",
            "Sinalizacao previa num bloco unico que acompanha a rede.",
            new[] { "Opcoes no item Sinalizacao da arvore." }),
        new("TESTE", "C3DVIARIOTESTE  ", "Autoteste", "Saidas",
            "Confere linhas, sinalizacao e objetos do Civil contra a rede.",
            new[] { "Relatorio em %TEMP%\\C3DVIARIOTESTE.txt." })
    };

    // ------------------------------------------------------------------
    // Abrir / atualizar
    // ------------------------------------------------------------------

    public static void ShowPanel(string? item = null)
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

        if (item is not null)
        {
            _instance._selectedId = item switch
            {
                "Sinalizacao" => "SINAL",
                "Secoes" => "SECOES",
                "Config" => "CONFIG",
                _ => item
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
        Width = 980;
        Height = 660;
        MinWidth = 700;
        MinHeight = 480;
        ShowInTaskbar = false;
        Background = Back;
        FontSize = 12;
        WindowStartupLocation = SW.WindowStartupLocation.CenterScreen;
        Icon = ViarioIcons.Image("PAINEL");

        WC.DockPanel root = new();

        WC.ToolBarTray tray = BuildToolbar();
        WC.DockPanel.SetDock(tray, WC.Dock.Top);
        root.Children.Add(tray);

        WC.Primitives.StatusBar status = new() { Background = Back, BorderBrush = Line, BorderThickness = new SW.Thickness(0, 1, 0, 0) };
        status.Items.Add(new WC.Primitives.StatusBarItem { Content = _status });
        status.Items.Add(new WC.Separator());
        status.Items.Add(new WC.Primitives.StatusBarItem { Content = _message });
        _message.Foreground = Muted;
        WC.DockPanel.SetDock(status, WC.Dock.Bottom);
        root.Children.Add(status);

        // Arvore | conteudo.
        WC.Grid main = new() { Margin = new SW.Thickness(4) };
        main.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(250), MinWidth = 160 });
        main.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(5) });
        main.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });

        _tree.BorderBrush = Line;
        _tree.Background = Pane;
        _tree.SelectedItemChanged += (_, _) => OnSelect();
        main.Children.Add(_tree);

        WC.GridSplitter split = new() { Width = 5, HorizontalAlignment = SW.HorizontalAlignment.Stretch, Background = Back };
        WC.Grid.SetColumn(split, 1);
        main.Children.Add(split);

        WC.Grid content = new();
        content.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        content.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1, SW.GridUnitType.Star), MinHeight = 150 });
        content.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(5) });
        content.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(150), MinHeight = 60 });
        WC.Grid.SetColumn(content, 2);
        main.Children.Add(content);

        // Cabecalho do item.
        WC.StackPanel head = new() { Margin = new SW.Thickness(2, 0, 2, 4) };
        _title.FontSize = 14;
        _title.FontWeight = SW.FontWeights.SemiBold;
        _subtitle.Foreground = Muted;
        _subtitle.TextWrapping = SW.TextWrapping.Wrap;
        head.Children.Add(_title);
        head.Children.Add(_subtitle);
        content.Children.Add(head);

        // Grafico | propriedades.
        WC.Grid middle = new();
        middle.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        middle.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(5) });
        middle.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(320), MinWidth = 220 });
        WC.Grid.SetRow(middle, 1);
        content.Children.Add(middle);

        WC.Border graphic = new()
        {
            BorderBrush = Line,
            BorderThickness = new SW.Thickness(1),
            Background = Pane,
            Child = _graphic,
            ClipToBounds = true
        };
        _graphic.SizeChanged += (_, _) => _render?.Invoke();
        middle.Children.Add(graphic);

        WC.GridSplitter split2 = new() { Width = 5, HorizontalAlignment = SW.HorizontalAlignment.Stretch, Background = Back };
        WC.Grid.SetColumn(split2, 1);
        middle.Children.Add(split2);

        WC.DockPanel propsDock = new();
        WC.Border propsHead = Header("Propriedades");
        WC.DockPanel.SetDock(propsHead, WC.Dock.Top);
        propsDock.Children.Add(propsHead);
        _actions.Margin = new SW.Thickness(0, 4, 0, 0);
        WC.DockPanel.SetDock(_actions, WC.Dock.Bottom);
        propsDock.Children.Add(_actions);
        propsDock.Children.Add(new WC.Border
        {
            BorderBrush = Line,
            BorderThickness = new SW.Thickness(1),
            Background = Pane,
            Child = new WC.ScrollViewer { Content = _props, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto }
        });
        WC.Grid.SetColumn(propsDock, 2);
        middle.Children.Add(propsDock);

        WC.GridSplitter split3 = new() { Height = 5, HorizontalAlignment = SW.HorizontalAlignment.Stretch, Background = Back };
        WC.Grid.SetRow(split3, 2);
        content.Children.Add(split3);

        // Recomendacoes.
        WC.DockPanel hints = new();
        WC.DockPanel hintHead = new() { Background = Brush(232, 232, 232) };
        WC.TextBlock hintTitle = new() { Text = "Recomendacoes (DNIT IPR-718 / IPR-740)", FontWeight = SW.FontWeights.SemiBold, Margin = new SW.Thickness(6, 3, 6, 3) };
        WC.DockPanel.SetDock(hintTitle, WC.Dock.Left);
        hintHead.Children.Add(hintTitle);
        _hintFilter.Margin = new SW.Thickness(10, 3, 6, 3);
        _hintFilter.Click += (_, _) => FillHints();
        WC.DockPanel.SetDock(_hintFilter, WC.Dock.Left);
        hintHead.Children.Add(_hintFilter);
        _hintCount.Foreground = Muted;
        _hintCount.Margin = new SW.Thickness(10, 3, 6, 3);
        hintHead.Children.Add(_hintCount);
        WC.DockPanel.SetDock(hintHead, WC.Dock.Top);
        hints.Children.Add(hintHead);

        WC.GridView view = new();
        view.Columns.Add(new WC.GridViewColumn { Header = "", Width = 26, CellTemplate = GlyphTemplate() });
        view.Columns.Add(new WC.GridViewColumn { Header = "Recomendacao", Width = 520, DisplayMemberBinding = new WD.Binding("Message") });
        view.Columns.Add(new WC.GridViewColumn { Header = "Fonte", Width = 140, DisplayMemberBinding = new WD.Binding("Source") });
        _hintList.View = view;
        _hintList.BorderBrush = Line;
        _hintList.MouseDoubleClick += (_, _) =>
        {
            if (_hintList.SelectedItem is Hint h && h.Where is V2 p)
                ZoomTo(new Box2(p.X - 25, p.Y - 25, p.X + 25, p.Y + 25));
        };
        _hintList.ToolTip = "Duplo clique: zoom no local. Valores de referencia para conferencia; nao substituem o projeto.";
        hints.Children.Add(_hintList);
        WC.Grid.SetRow(hints, 3);
        content.Children.Add(hints);

        root.Children.Add(main);
        Content = root;
    }

    private static WM.SolidColorBrush Brush(byte r, byte g, byte b)
    {
        WM.SolidColorBrush brush = new(WM.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static WC.Border Header(string text) => new()
    {
        Background = Brush(232, 232, 232),
        BorderBrush = Line,
        BorderThickness = new SW.Thickness(1, 1, 1, 0),
        Child = new WC.TextBlock { Text = text, FontWeight = SW.FontWeights.SemiBold, Margin = new SW.Thickness(6, 3, 6, 3) }
    };

    /// <summary>Icone do aviso: triangulo ambar (atencao) ou "i" azul (informacao).</summary>
    private static SW.DataTemplate GlyphTemplate()
    {
        SW.FrameworkElementFactory text = new(typeof(WC.TextBlock));
        text.SetBinding(WC.TextBlock.TextProperty, new WD.Binding("Level") { Converter = new GlyphConverter() });
        text.SetBinding(WC.TextBlock.ForegroundProperty, new WD.Binding("Level") { Converter = new GlyphBrushConverter() });
        text.SetValue(WC.TextBlock.FontWeightProperty, SW.FontWeights.Bold);
        return new SW.DataTemplate { VisualTree = text };
    }

    private sealed class GlyphConverter : WD.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is HintLevel.Atencao ? "⚠" : "ℹ";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => WD.Binding.DoNothing;
    }

    private sealed class GlyphBrushConverter : WD.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is HintLevel.Atencao ? Warn : Accent;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => WD.Binding.DoNothing;
    }

    private WC.ToolBarTray BuildToolbar()
    {
        WC.ToolBarTray tray = new() { Background = Back };
        int band = 0;
        foreach (IGrouping<string, Cmd> group in Commands.GroupBy(c => c.Group))
        {
            WC.ToolBar bar = new() { Band = 0, BandIndex = band++, Background = Back };
            foreach (Cmd cmd in group)
            {
                WC.Button button = new()
                {
                    Content = new WC.Image { Source = ViarioIcons.Image(cmd.Key), Width = 22, Height = 22 },
                    Padding = new SW.Thickness(2),
                    ToolTip = Tip(cmd)
                };
                WC.ToolTipService.SetInitialShowDelay(button, 250);
                WC.ToolTipService.SetShowDuration(button, 30000);
                Cmd c = cmd;
                button.Click += (_, _) => RunCommand(c);
                bar.Items.Add(button);
            }

            tray.ToolBars.Add(bar);
        }

        return tray;
    }

    /// <summary>Dica do botao: titulo, ilustracao, descricao e passos (como as dicas estendidas do AutoCAD).</summary>
    private static WC.ToolTip Tip(Cmd cmd)
    {
        WC.StackPanel panel = new() { MaxWidth = 380 };
        panel.Children.Add(new WC.TextBlock { Text = cmd.Title, FontWeight = SW.FontWeights.SemiBold });
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

    private void RunCommand(Cmd cmd) => Send(cmd.Send, cmd.Title + ": siga a linha de comando.");

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

    private void Say(string text) => _message.Text = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "  " + text;

    // ------------------------------------------------------------------
    // Dados e arvore
    // ------------------------------------------------------------------

    private void Refresh()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            _status.Text = "Nenhum desenho aberto.";
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
            _status.Text = "Erro ao ler a rede: " + ex.Message;
            return;
        }

        int warn = _hints.Count(h => h.Level == HintLevel.Atencao);
        _status.Text = $"{_axes.Count} eixo(s)   {_result.Nodes.Count} no(s)   {_result.Corners.Count} esquina(s)   " +
                       $"{warn} atencao / {_hints.Count - warn} informacao   Civil: {(_civilActive ? "ativo" : "-")}   " +
                       $"Sinalizacao: {(_signExists ? "sim" : "-")}   v{C3DVersions.Viario}";
        BuildTree();
    }

    private sealed class Node
    {
        public string Id = "";
        public string Kind = "";
        public string Label = "";
        public string Icon = "PAINEL";
        public object? Data;
        public readonly List<Node> Children = new();
    }

    private void BuildTree()
    {
        string? keep = _selectedId;
        HashSet<string> expanded = new();
        foreach (WC.TreeViewItem item in AllItems(_tree.Items))
        {
            if (item.IsExpanded && item.Tag is Node n)
                expanded.Add(n.Id);
        }

        bool first = _tree.Items.Count == 0;
        _tree.Items.Clear();

        Node root = new() { Id = "REDE", Kind = "Rede", Label = "Rede viaria", Icon = "PAINEL" };

        Node secs = new() { Id = "SECOES", Kind = "Secoes", Label = $"Secoes ({_sections.Count})", Icon = "SECAO" };
        foreach (RoadSection s in _sections)
        {
            secs.Children.Add(new Node
            {
                Id = "SEC:" + s.Id, Kind = "Secao", Data = s, Icon = "SECAO",
                Label = s.Name + (s.Id == _settings.CurrentSectionId ? "  (corrente)" : "")
            });
        }

        root.Children.Add(secs);

        Node axes = new() { Id = "EIXOS", Kind = "Eixos", Label = $"Eixos ({_axes.Count})", Icon = "EIXO" };
        for (int i = 0; i < _axes.Count; i++)
        {
            AxisInput a = _axes[i];
            Node an = new() { Id = "EIXO:" + a.Key, Kind = "Eixo", Data = a, Icon = a.Path.Closed ? "ROTATORIA" : "EIXO", Label = AxisName(a, i) };
            for (int k = 0; k < a.Events.Count; k++)
            {
                AxisEvent ev = a.Events[k];
                an.Children.Add(new Node
                {
                    Id = $"EV:{a.Key}:{k}", Kind = "Elemento", Data = (a, ev), Label = KindName(ev.Kind),
                    Icon = ev.Kind switch
                    {
                        AxisEventKind.Gota => "GOTA",
                        AxisEventKind.Baia => "CONVERSAO",
                        AxisEventKind.Abertura => "ABERTURA",
                        _ => "ALARGAR"
                    }
                });
            }

            axes.Children.Add(an);
        }

        root.Children.Add(axes);

        Node nodes = new() { Id = "NOS", Kind = "Nos", Label = $"Intersecoes ({_result.Nodes.Count})", Icon = "MODELOS" };
        for (int i = 0; i < _result.Nodes.Count; i++)
        {
            NodeInfo nd = _result.Nodes[i];
            Node nn = new()
            {
                Id = $"NO:{nd.Point.X:0.##},{nd.Point.Y:0.##}", Kind = "No", Data = nd, Icon = "MODELOS",
                Label = $"No {i + 1} - {nd.LegCount} ramo(s)"
            };
            foreach (CornerInfo c in _result.Corners.Where(c => V2.Dist(c.Node, nd.Point) < 0.5))
            {
                bool slip = _engine.SlipLanes.ContainsKey(c.Key);
                nn.Children.Add(new Node
                {
                    Id = "ESQ:" + c.Key, Kind = "Esquina", Data = c, Icon = slip ? "LIVRE" : "RAIO",
                    Label = (slip ? "Faixa livre" : "Esquina") + $" R {N(c.Radius)}" + (_engine.CornerRadius.ContainsKey(c.Key) ? " *" : "")
                });
            }

            nodes.Children.Add(nn);
        }

        root.Children.Add(nodes);

        List<OutputLine> islands = _result.Lines.Where(l => l.Level == EdgeLevel.Ilha && l.Closed).ToList();
        Node isl = new() { Id = "ILHAS", Kind = "Ilhas", Label = $"Ilhas canalizadoras ({islands.Count})", Icon = "ILHA" };
        for (int i = 0; i < islands.Count; i++)
        {
            isl.Children.Add(new Node
            {
                Id = $"ILHA:{i}", Kind = "Ilha", Data = islands[i], Icon = "GOTA",
                Label = $"Ilha {i + 1} - {N(ViarioEngine.RingArea(islands[i].Segs))} m2"
            });
        }

        root.Children.Add(isl);
        root.Children.Add(new Node { Id = "SINAL", Kind = "Sinalizacao", Label = "Sinalizacao horizontal" + (_signExists ? "" : " (nao gerada)"), Icon = "SINAL" });
        root.Children.Add(new Node { Id = "CIVIL", Kind = "Civil", Label = "Objetos do Civil" + (_civilActive ? "" : " (nao gerados)"), Icon = "CIVIL" });
        root.Children.Add(new Node { Id = "CONFIG", Kind = "Config", Label = "Configuracao", Icon = "CONFIG" });

        WC.TreeViewItem rootItem = Item(root, expanded, first);
        _tree.Items.Add(rootItem);

        WC.TreeViewItem target = AllItems(_tree.Items).FirstOrDefault(i => i.Tag is Node n && n.Id == keep) ?? rootItem;
        for (WC.TreeViewItem? parent = target.Parent as WC.TreeViewItem; parent is not null; parent = parent.Parent as WC.TreeViewItem)
            parent.IsExpanded = true;

        target.IsSelected = true;
        target.BringIntoView();
        OnSelect();
    }

    private WC.TreeViewItem Item(Node node, HashSet<string> expanded, bool first)
    {
        int warn = node.Kind is "Rede" or "Eixos" or "Nos" or "Ilhas" or "Secoes" ? 0 : HintsFor(node).Count(h => h.Level == HintLevel.Atencao);
        WC.StackPanel header = new() { Orientation = WC.Orientation.Horizontal };
        header.Children.Add(new WC.Image { Source = ViarioIcons.Image(node.Icon), Width = 16, Height = 16, Margin = new SW.Thickness(0, 0, 5, 0) });
        header.Children.Add(new WC.TextBlock { Text = node.Label, VerticalAlignment = SW.VerticalAlignment.Center });
        if (warn > 0)
            header.Children.Add(new WC.TextBlock { Text = "  ⚠", Foreground = Warn, ToolTip = $"{warn} recomendacao(oes) de atencao" });

        WC.TreeViewItem item = new()
        {
            Header = header,
            Tag = node,
            IsExpanded = expanded.Contains(node.Id) || (first && node.Kind is "Rede" or "Eixos" or "Secoes")
        };
        foreach (Node child in node.Children)
            item.Items.Add(Item(child, expanded, first));
        return item;
    }

    private static IEnumerable<WC.TreeViewItem> AllItems(WC.ItemCollection items)
    {
        foreach (object o in items)
        {
            if (o is not WC.TreeViewItem item)
                continue;
            yield return item;
            foreach (WC.TreeViewItem child in AllItems(item.Items))
                yield return child;
        }
    }

    private string AxisName(AxisInput axis, int index) =>
        _names.TryGetValue(axis.Key, out string? name) ? name : (axis.Path.Closed ? $"Rotatoria {index + 1}" : $"Eixo {index + 1}");

    private static string KindName(AxisEventKind kind) => kind switch
    {
        AxisEventKind.Alargamento => "Alargamento",
        AxisEventKind.Gota => "Gota",
        AxisEventKind.Baia => "Conversao no canteiro",
        AxisEventKind.Abertura => "Abertura de canteiro",
        _ => kind.ToString()
    };

    private IEnumerable<Hint> HintsFor(Node node)
    {
        switch (node.Kind)
        {
            case "Secao":
                string sid = ((RoadSection)node.Data!).Id;
                return _hints.Where(h => h.Topic == "Secao" && h.Target == sid);
            case "Eixo":
                AxisInput a = (AxisInput)node.Data!;
                return _hints.Where(h => h.Target == a.Key || (h.Topic == "Secao" && h.Target == a.Section.Id));
            case "Elemento":
                (AxisInput ea, AxisEvent ev) = ((AxisInput, AxisEvent))node.Data!;
                string prefix = KindName(ev.Kind).Split(' ')[0];
                return _hints.Where(h => h.Topic == "Elemento" && h.Target == ea.Key && h.Message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            case "Esquina":
                string ck = ((CornerInfo)node.Data!).Key;
                return _hints.Where(h => h.Target == ck);
            case "No":
                V2 np = ((NodeInfo)node.Data!).Point;
                return _hints.Where(h => h.Topic is "No" or "Esquina" or "Elemento" && h.Where is V2 p && V2.Dist(p, np) < 30.0);
            case "Ilha":
                OutputLine ring = (OutputLine)node.Data!;
                return _hints.Where(h => h.Topic == "Ilha" && h.Where is V2 p && ViarioEngine.RingContains(ring.Segs, p));
            case "Secoes":
                return _hints.Where(h => h.Topic == "Secao");
            case "Ilhas":
                return _hints.Where(h => h.Topic == "Ilha");
            case "Nos":
                return _hints.Where(h => h.Topic is "Esquina" or "No" or "Rotatoria");
            case "Eixos":
                return _hints.Where(h => h.Topic is "Eixo" or "Elemento");
            case "Sinalizacao":
            case "Civil":
            case "Config":
                return Enumerable.Empty<Hint>();
            default:
                return _hints;
        }
    }

    // ------------------------------------------------------------------
    // Selecao
    // ------------------------------------------------------------------

    private Node? Current => (_tree.SelectedItem as WC.TreeViewItem)?.Tag as Node;

    private void OnSelect()
    {
        Node? node = Current;
        if (node is null)
            return;

        _selectedId = node.Id;
        _props.Children.Clear();
        _actions.Children.Clear();
        _fields.Clear();
        _title.Text = node.Label;
        _subtitle.Text = "";
        _render = null;

        switch (node.Kind)
        {
            case "Secao": ShowSection((RoadSection)node.Data!); break;
            case "Secoes": ShowSections(); break;
            case "Eixo": ShowAxis((AxisInput)node.Data!); break;
            case "Elemento": ShowEvent(((AxisInput, AxisEvent))node.Data!); break;
            case "No": ShowNode((NodeInfo)node.Data!); break;
            case "Esquina": ShowCorner((CornerInfo)node.Data!); break;
            case "Ilha": ShowIsland((OutputLine)node.Data!); break;
            case "Sinalizacao": ShowSignage(); break;
            case "Civil": ShowCivil(); break;
            case "Config": ShowConfig(); break;
            default: ShowNetwork(); break;
        }

        FillHints();
        _render?.Invoke();
    }

    private void FillHints()
    {
        Node? node = Current;
        List<Hint> list = (node is not null && _hintFilter.IsChecked == true ? HintsFor(node) : _hints)
            .OrderByDescending(h => h.Level)
            .ThenBy(h => h.Topic)
            .ToList();
        _hintList.ItemsSource = list;
        int warn = list.Count(h => h.Level == HintLevel.Atencao);
        _hintCount.Text = list.Count == 0 ? "nenhuma" : $"{warn} atencao, {list.Count - warn} informacao";
    }

    // ------------------------------------------------------------------
    // Propriedades
    // ------------------------------------------------------------------

    private void Prop(string label, string value)
    {
        WC.Grid row = PropRow(label);
        WC.TextBlock text = new() { Text = value, Margin = new SW.Thickness(4, 2, 4, 2), TextWrapping = SW.TextWrapping.Wrap };
        WC.Grid.SetColumn(text, 1);
        row.Children.Add(text);
        _props.Children.Add(row);
    }

    private WC.TextBox EditProp(string key, string label, string value)
    {
        WC.Grid row = PropRow(label);
        WC.TextBox box = new() { Text = value, BorderThickness = new SW.Thickness(0), Padding = new SW.Thickness(3, 2, 3, 2), Background = Brush(250, 250, 236) };
        WC.Grid.SetColumn(box, 1);
        row.Children.Add(box);
        _props.Children.Add(row);
        _fields[key] = box;
        return box;
    }

    private WC.ComboBox ComboProp(string key, string label, IEnumerable<(string Text, string Value)> items, string selected, bool editable = false)
    {
        WC.Grid row = PropRow(label);
        WC.ComboBox combo = new() { IsEditable = editable, BorderThickness = new SW.Thickness(0), Margin = new SW.Thickness(1) };
        foreach ((string text, string value) in items)
        {
            WC.ComboBoxItem item = new() { Content = text, Tag = value };
            combo.Items.Add(item);
            if (value == selected)
                combo.SelectedItem = item;
        }

        if (editable)
            combo.Text = selected;
        WC.Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        _props.Children.Add(row);
        _fields[key] = combo;
        return combo;
    }

    private WC.CheckBox CheckProp(string key, string label, bool value)
    {
        WC.Grid row = PropRow(label);
        WC.CheckBox box = new() { IsChecked = value, Margin = new SW.Thickness(4, 3, 4, 3) };
        WC.Grid.SetColumn(box, 1);
        row.Children.Add(box);
        _props.Children.Add(row);
        _fields[key] = box;
        return box;
    }

    private void Category(string text) => _props.Children.Add(new WC.TextBlock
    {
        Text = text,
        FontWeight = SW.FontWeights.SemiBold,
        Background = Brush(238, 238, 238),
        Padding = new SW.Thickness(4, 2, 4, 2)
    });

    private static WC.Grid PropRow(string label)
    {
        WC.Grid row = new();
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        WC.Border cell = new()
        {
            BorderBrush = Brush(232, 232, 232),
            BorderThickness = new SW.Thickness(0, 0, 1, 1),
            Child = new WC.TextBlock { Text = label, Margin = new SW.Thickness(4, 2, 4, 2), Foreground = Brush(60, 60, 60), TextWrapping = SW.TextWrapping.Wrap }
        };
        row.Children.Add(cell);
        WC.Border line = new() { BorderBrush = Brush(232, 232, 232), BorderThickness = new SW.Thickness(0, 0, 0, 1) };
        WC.Grid.SetColumn(line, 1);
        row.Children.Add(line);
        return row;
    }

    private void Action(string text, Action action, bool primary = false)
    {
        WC.Button b = new()
        {
            Content = text,
            Margin = new SW.Thickness(0, 0, 4, 4),
            Padding = new SW.Thickness(8, 2, 8, 2),
            FontWeight = primary ? SW.FontWeights.SemiBold : SW.FontWeights.Normal
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
            box.ClearValue(WC.Control.ForegroundProperty);
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
            WC.ComboBox { IsEditable: true } c => c.Text.Trim(),
            WC.ComboBox c => (c.SelectedItem as WC.ComboBoxItem)?.Tag as string ?? "",
            _ => ""
        };
    }

    private bool Checked(string key) => _fields.TryGetValue(key, out SW.FrameworkElement? e) && e is WC.CheckBox { IsChecked: true };

    // ---------------- Rede ----------------

    private void ShowNetwork()
    {
        _subtitle.Text = "Clique em um item da arvore para ver o grafico, as propriedades e as recomendacoes.";
        Category("Resumo");
        Prop("Eixos", _axes.Count.ToString(CultureInfo.InvariantCulture));
        Prop("Extensao de eixos", N(_axes.Sum(a => a.Path.Length)) + " m");
        Prop("Intersecoes", _result.Nodes.Count.ToString(CultureInfo.InvariantCulture));
        Prop("Esquinas", _result.Corners.Count.ToString(CultureInfo.InvariantCulture));
        Prop("Linhas geradas", _result.Lines.Count.ToString(CultureInfo.InvariantCulture));
        Prop("Secao corrente", CurrentSection().Name);
        List<string> warnings = _result.Warnings.Distinct().ToList();
        if (warnings.Count > 0)
        {
            Category("Avisos do motor");
            foreach (string w in warnings.Take(8))
                Prop("", w);
        }

        _render = () => RenderMap(null, null);
    }

    // ---------------- Secoes ----------------

    private RoadSection CurrentSection() =>
        _sections.FirstOrDefault(s => s.Id == _settings.CurrentSectionId) ?? _sections.FirstOrDefault() ?? ViarioStore.DefaultSection();

    private void ShowSections()
    {
        _subtitle.Text = "Tipos de via do desenho. Selecione uma secao para editar.";
        Category("Secoes");
        foreach (RoadSection s in _sections)
            Prop(s.Name, $"{s.Tipo}, pista {N(s.Pista)}" + (s.Canteiro > 0 ? $" + canteiro {N(s.Canteiro)}" : "") + $", {_axes.Count(a => a.Section.Id == s.Id)} eixo(s)");
        Action("Nova secao", () => NewSection(null), primary: true);
        RoadSection cur = CurrentSection();
        _render = () => RenderSection(cur);
    }

    private void ShowSection(RoadSection s)
    {
        int uses = _axes.Count(a => a.Section.Id == s.Id);
        _subtitle.Text = $"{s.Tipo} - {uses} eixo(s) - codigo interno {s.Id}" + (s.Id == _settings.CurrentSectionId ? " - secao corrente" : "");

        Category("Identificacao");
        EditProp("Name", "Nome", s.Name);
        ComboProp("Tipo", "Tipo de via", ViarioStore.Tipos.Select(t => (t, t)), s.Tipo, editable: true);
        EditProp("Velocidade", "Velocidade (km/h)", N(s.Velocidade));
        Category("Pista");
        EditProp("Pista", "Largura de rolamento (m)", N(s.Pista));
        EditProp("Faixas", "Faixas, dois sentidos (0 = auto)", s.Faixas.ToString(CultureInfo.InvariantCulture));
        EditProp("Canteiro", "Canteiro central (m)", N(s.Canteiro));
        Category("Bordos");
        EditProp("Sarjeta", "Sarjeta (m)", N(s.Sarjeta));
        EditProp("MeioFio", "Meio-fio (m)", N(s.MeioFio));
        EditProp("Calcada", "Calcada (m)", N(s.Calcada));
        EditProp("Raio", "Raio de esquina (m)", N(s.Raio));
        Category("Calculado");
        Prop("Faixa de rolamento", N((s.Pista / 2.0) / s.FaixasPorSentido) + " m");
        Prop("Largura total", N(s.TotalWidth) + " m");

        foreach (SW.FrameworkElement f in _fields.Values)
        {
            if (f is WC.TextBox box)
                box.TextChanged += (_, _) => _render?.Invoke();
        }

        Action("Salvar", () => SaveSection(s), primary: true);
        Action("Tornar corrente", () => MakeCurrent(s));
        Action("Aplicar aos selecionados", () => ApplyToSelected(s));
        Action("Duplicar", () => NewSection(s));
        Action("Excluir", () => DeleteSection(s));
        _render = () => RenderSection(EditedSection(s) ?? s);
    }

    private RoadSection? EditedSection(RoadSection basis)
    {
        RoadSection s = basis.Clone();
        string name = Text("Name");
        if (name.Length > 0)
            s.Name = name;
        string tipo = Text("Tipo");
        if (tipo.Length > 0)
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
        _selectedId = "SEC:" + s.Id;
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

        _selectedId = "SECOES";
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

    // ---------------- Eixos ----------------

    private void ShowAxis(AxisInput a)
    {
        _subtitle.Text = (a.Path.Closed ? "Anel de rotatoria" : "Eixo") + $" - handle {a.Key}";
        Category("Geometria");
        Prop("Comprimento", N(a.Path.Length) + " m");
        Prop("Trechos", $"{a.Path.Segs.Count} ({a.Path.Segs.Count(s => s.IsArc)} arco(s))");
        Prop("Intersecoes", _result.Nodes.Count(n => n.AxisKeys.Contains(a.Key)).ToString(CultureInfo.InvariantCulture));
        Category("Secao");
        ComboProp("Secao", "Secao", _sections.Select(s => (s.Name, s.Id)), a.Section.Id);
        Prop("Raio de esquina", a.Radius is double r ? N(r) + " m (do eixo)" : N(a.Section.Raio) + " m (da secao)");
        Prop("Elementos", a.Events.Count == 0 ? "-" : string.Join(", ", a.Events.Select(e => KindName(e.Kind))));
        if (_names.TryGetValue(a.Key, out string? civil))
            Prop("Alinhamento do Civil", civil);

        Action("Zoom", () => ZoomAxis(a), primary: true);
        Action("Selecionar", () => SelectAxis(a));
        Action("Aplicar secao", () => AssignSection(a));
        Action("Tirar da rede", () => RemoveAxis(a));
        _render = () => RenderMap(a.Path.Bounds().Inflate(a.Section.TotalWidth + 10), sh => sh.Highlight(a.Path.Segs));
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
        _selectedId = "EIXOS";
        Edit("Eixo tirado da rede (a polilinha continua no desenho).", db =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            if (AxisEntity(tr, db, a.Key, AcadDb.OpenMode.ForWrite) is AcadDb.Entity e)
                ViarioStore.ClearTag(e);
            tr.Commit();
        });
    }

    private void ShowEvent((AxisInput Axis, AxisEvent Event) data)
    {
        (AxisInput a, AxisEvent ev) = data;
        _subtitle.Text = ev.Describe();
        Category("Elemento");
        Prop("Tipo", KindName(ev.Kind));
        if (ev.Kind is AxisEventKind.Alargamento or AxisEventKind.Baia)
            Prop("Lado", ev.Side == 0 ? "ambos" : ev.Side > 0 ? "direita de quem chega" : "esquerda de quem chega");
        Prop(ev.Kind == AxisEventKind.Gota ? "Largura maxima" : "Largura", N(ev.Width) + " m");
        Prop(ev.Kind is AxisEventKind.Abertura or AxisEventKind.Gota ? "Comprimento" : "Armazenamento", N(ev.Length) + " m");
        if (ev.Kind != AxisEventKind.Abertura)
            Prop("Taper", N(ev.Taper) + " m" + (ev.Smooth ? " (suave)" : " (reto)"));
        if (ev.Kind == AxisEventKind.Gota)
            Prop("Recuo da ponta", N(ev.Gap) + " m");
        Prop("Eixo", AxisName(a, _axes.IndexOf(a)));

        if (ev.Kind is AxisEventKind.Alargamento or AxisEventKind.Baia)
        {
            Category("Referencia (IPR-718)");
            Prop("Largura da faixa", ">= 3,00 m (2,70 excepcional)");
            Prop("Taper urbano", "30 - 54 m (8:1 ate 50 km/h, 15:1 ate 80)");
            Prop("Armazenamento minimo", "2 carros (~15 m), Tab.53");
        }

        V2 at = a.Path.PointAt(Math.Clamp(ev.S + (ev.Dir * (ev.Gap + (ev.Length / 2.0))), 0, a.Path.Length));
        double size = ev.Length + ev.Taper + 30;
        Box2 box = new(at.X - size, at.Y - size, at.X + size, at.Y + size);
        Action("Zoom", () => ZoomTo(box), primary: true);
        Action("Remover elemento", () =>
        {
            string serialized = ev.Serialize();
            _selectedId = "EIXO:" + a.Key;
            Edit("Elemento removido.", _ =>
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                ViarioElementCommands.ModifyAxisEvents(doc, a.Key, events => events.RemoveAll(e => e.Serialize() == serialized));
            });
        });
        _render = () => RenderMap(box, sh => sh.Highlight(a.Path.Segs));
    }

    // ---------------- Nos e esquinas ----------------

    private void ShowNode(NodeInfo nd)
    {
        _subtitle.Text = $"Intersecao em ({N(nd.Point.X)}; {N(nd.Point.Y)})";
        Category("Intersecao");
        Prop("Ramos", nd.LegCount.ToString(CultureInfo.InvariantCulture));
        Prop("Eixos", string.Join(", ", nd.AxisKeys.Select(k => _axes.FindIndex(a => a.Key == k)).Where(i => i >= 0).Select(i => AxisName(_axes[i], i))));
        List<CornerInfo> corners = _result.Corners.Where(c => V2.Dist(c.Node, nd.Point) < 0.5).ToList();
        Prop("Esquinas", corners.Count == 0 ? "-" : string.Join("; ", corners.Select(c => "R " + N(c.Radius))));
        Category("Referencia (IPR-718 8.4.1)");
        Prop("Angulo entre vias", "ideal 75 - 90 graus; abaixo de 60, realinhar");
        Prop("Greide na chegada", "evitar > 3%, max. 6%");
        Box2 box = new(nd.Point.X - 40, nd.Point.Y - 40, nd.Point.X + 40, nd.Point.Y + 40);
        Action("Zoom", () => ZoomTo(box), primary: true);
        _render = () => RenderMap(box, sh =>
        {
            foreach (CornerInfo c in corners.Where(c => c.Arc is not null))
                sh.Highlight(new[] { c.Arc! });
        });
    }

    private void ShowCorner(CornerInfo c)
    {
        bool slip = _engine.SlipLanes.TryGetValue(c.Key, out SlipLane? lane);
        double turn = c.Arc is null ? 0 : Math.Abs(c.Arc.Sweep) * 180.0 / Math.PI;
        (double vp, double co) = ViarioNorms.MinCornerRadius(turn);
        _subtitle.Text = slip ? "Esquina com faixa livre a direita" : "Esquina (raio na face do meio-fio)";
        Category("Esquina");
        Prop("Giro", N(turn) + " graus");
        if (!slip || lane is null)
        {
            EditProp("Raio", "Raio (m)", N(c.Radius));
            Prop("Origem", _engine.CornerRadius.ContainsKey(c.Key) ? "especifico da esquina" : "secao ou eixo");
        }
        else
        {
            Prop("Raio externo", N(lane.Radius) + " m");
            Prop("Largura da faixa", N(lane.Width) + " m");
            Prop("Afastamento da ilha", N(lane.Setback) + " m");
            Prop("Largura recomendada (VP)", N(ViarioNorms.SlipWidth(Math.Max(lane.Radius - lane.Width, 1))) + " m (Tab.45)");
        }

        Category("Referencia (IPR-718 Tab.39)");
        Prop("Carro (VP), curva simples", ">= " + N(vp) + " m");
        Prop("Onibus/caminhao (CO)", ">= " + N(co) + " m");

        Box2 box = c.Arc is null ? new Box2(c.Corner.X - 30, c.Corner.Y - 30, c.Corner.X + 30, c.Corner.Y + 30) : c.Arc.Bounds().Inflate(25);
        if (!slip)
        {
            Action("Aplicar raio", () => SetCornerRadius(c, Number("Raio")), primary: true);
            Action("Padrao", () => SetCornerRadius(c, -1));
        }

        Action("Zoom", () => ZoomTo(box));
        _render = () => RenderMap(box, sh =>
        {
            if (c.Arc is not null)
                sh.Highlight(new[] { c.Arc });
        });
    }

    /// <summary>Raio especifico da esquina; negativo = volta ao padrao; null = valor invalido.</summary>
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

    // ---------------- Ilhas ----------------

    private void ShowIsland(OutputLine island)
    {
        double area = ViarioEngine.RingArea(island.Segs);
        Box2 box = Bounds(island.Segs).Inflate(20);
        _subtitle.Text = "Ilha canalizadora (gota ou ilha da faixa livre)";
        Category("Ilha");
        Prop("Area", N(area) + " m2");
        Prop("Perimetro", N(island.Segs.Sum(s => s.Length)) + " m");
        Prop("Cantos arredondados", island.Segs.Count(s => s.IsArc).ToString(CultureInfo.InvariantCulture));
        Category("Referencia (IPR-718 p.354)");
        Prop("Area minima", "5 m2 urbana / 7 m2 rural");
        Prop("Lado minimo (triangular)", "3,50 m (pref. 4,50)");
        Prop("Raio do nariz", "0,60 - 1,00 m");
        Prop("Afastamento do bordo", "0,60 - 1,00 m");
        Action("Zoom", () => ZoomTo(box), primary: true);
        Action("Raio dos cantos...", () => RunCommand(Commands.First(c => c.Key == "RAIO")));
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

    private void ShowSignage()
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
            SW.FrameworkElement control = value switch
            {
                bool b => CheckProp(prop, label, b),
                string str => ComboProp(prop, label, new[] { ("Seccionada (LFO-2)", "Seccionada"), ("Continua (LFO-1)", "Continua"), ("Dupla (LFO-3)", "Dupla") }, str),
                double d => EditProp(prop, label, N(d)),
                _ => EditProp(prop, label, value?.ToString() ?? "")
            };
            switch (control)
            {
                case WC.CheckBox cb: cb.Click += (_, _) => _render?.Invoke(); break;
                case WC.ComboBox co: co.SelectionChanged += (_, _) => _render?.Invoke(); break;
                case WC.TextBox tb: tb.TextChanged += (_, _) => _render?.Invoke(); break;
            }
        }

        Category("Referencia (MBST Vol. IV)");
        Prop("Cadencia v < 60 km/h", "traco 2 / espaco 4 m, linha 0,10");
        Prop("Faixa de pedestres", "listras 0,30-0,40 / espaco 0,30-0,80, extensao >= 3 m (rec. 4)");
        Prop("Retencao", "0,30-0,60 m, >= 1,60 m antes da faixa");

        Action(_signExists ? "Atualizar bloco" : "Gerar bloco", GenerateSignage, primary: true);
        if (_signExists)
            Action("Remover bloco", () => Edit("Sinalizacao removida.", _ => ViarioSignageSync.Remove(AcApp.DocumentManager.MdiActiveDocument), regenerate: false));
        _render = () => RenderSignage(EditedSignage() ?? _signage);
    }

    private SignageOptions? EditedSignage()
    {
        SignageOptions o = _signage.Clone();
        foreach ((string prop, _, _) in SignageLabels)
        {
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

    private void GenerateSignage()
    {
        SignageOptions? o = EditedSignage();
        if (o is null)
        {
            Say("Corrija os valores em vermelho.");
            return;
        }

        Edit("Sinalizacao gerada.", db =>
        {
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                ViarioStore.SaveSignage(tr, db, o);
                tr.Commit();
            }

            ViarioSignageSync.Sync(AcApp.DocumentManager.MdiActiveDocument, _axes, _result, create: true);
        }, regenerate: false);
    }

    // ---------------- Civil / Config ----------------

    private void ShowCivil()
    {
        _subtitle.Text = _civilActive
            ? "Eixos, bordos LD/LE (offset dinamico) e curb returns conectados, sem site; acompanham a rede."
            : "Ainda nao gerados neste desenho.";
        Category("Objetos");
        Prop("Alinhamentos de eixo", _names.Count.ToString(CultureInfo.InvariantCulture));
        Prop("Situacao", _civilActive ? "ativo" : "nao gerado");
        Category("Proxima etapa");
        Prop("Altimetria", "perfil dos eixos, perfis offset dos bordos, curb returns e amarracao de greides");
        Prop("Greide nas intersecoes", "evitar > 3% na chegada, max. 6% (IPR-718 p.206)");
        Action(_civilActive ? "Atualizar" : "Gerar", () => RunCommand(Commands.First(c => c.Key == "CIVIL")), primary: true);
        Action("Remover", () => Send("C3DVIARIOCIVIL R ", "Remover objetos do Civil: confirme na linha de comando."));
        Action("Autoteste", () => RunCommand(Commands.First(c => c.Key == "TESTE")));
        _render = () => RenderMap(null, null);
    }

    private void ShowConfig()
    {
        _subtitle.Text = "Valores gravados no desenho.";
        Category("Encontro de eixos");
        EditProp("SnapTolerance", "Tolerancia de encontro (m)", N(_settings.SnapTolerance));
        EditProp("Capture", "Captura das pontas (m, 0 = nao)", N(_settings.Capture));
        Category("Canteiro");
        EditProp("MedianSetback", "Recuo do nariz do canteiro (m)", N(_settings.MedianSetback));
        Action("Salvar", () =>
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
        }, primary: true);
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

            Say(what);
        }
        catch (Exception ex)
        {
            Say("Erro: " + ex.Message);
        }

        Refresh();
    }

    private static AcadDb.Entity? AxisEntity(AcadDb.Transaction tr, AcadDb.Database db, string key, AcadDb.OpenMode mode)
    {
        if (!long.TryParse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
            return null;
        if (!db.TryGetObjectId(new AcadDb.Handle(value), out AcadDb.ObjectId id) || id.IsErased)
            return null;
        return tr.GetObject(id, mode, false) as AcadDb.Entity;
    }

    private void ZoomAxis(AxisInput a)
    {
        ZoomTo(a.Path.Bounds().Inflate(a.Section.TotalWidth));
        SelectAxis(a);
    }

    private void ZoomTo(Box2 box)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        using (doc.LockDocument())
        {
            using AcadDb.ViewTableRecord view = doc.Editor.GetCurrentView();
            double ratio = view.Width / Math.Max(view.Height, 1e-9);
            double width = Math.Max(box.MaxX - box.MinX, 10.0);
            double height = Math.Max(box.MaxY - box.MinY, 10.0);
            if (width / height > ratio)
                height = width / ratio;
            else
                width = height * ratio;

            view.CenterPoint = new Point2d((box.MinX + box.MaxX) / 2.0, (box.MinY + box.MaxY) / 2.0);
            view.Width = width * 1.1;
            view.Height = height * 1.1;
            doc.Editor.SetCurrentView(view);
            AcApp.UpdateScreen();
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

        Say("Eixo selecionado no desenho.");
    }

    // ------------------------------------------------------------------
    // Graficos (fundo branco com grade, como as vistas do Civil)
    // ------------------------------------------------------------------

    private sealed class Shapes
    {
        public readonly List<(List<V2> Points, WM.Brush Brush, double Thickness, bool Closed, WM.Brush? Fill)> Items = new();

        public void Add(IEnumerable<Seg> segs, WM.Brush brush, double thickness, bool closed = false, WM.Brush? fill = null) =>
            Items.Add((Sample(segs), brush, thickness, closed, fill));

        public void Highlight(IEnumerable<Seg> segs) => Add(segs, Brush(230, 120, 0), 2.5);
    }

    private (double W, double H) GraphicSize() => (Math.Max(_graphic.ActualWidth, 0), Math.Max(_graphic.ActualHeight, 0));

    private Shapes NetworkShapes()
    {
        Shapes sh = new();
        foreach (OutputLine line in _result.Lines)
        {
            (WM.Brush brush, double t) = line.Level switch
            {
                EdgeLevel.Pista => (Brush(150, 150, 150), 0.8),
                EdgeLevel.FaceMF => (Brush(40, 40, 40), 1.2),
                EdgeLevel.CostasMF => (Brush(90, 90, 90), 0.8),
                EdgeLevel.Alinhamento => (Brush(170, 170, 170), 0.8),
                EdgeLevel.Canteiro => (Brush(70, 140, 70), 1.0),
                _ => (Brush(180, 60, 160), 1.2)
            };
            sh.Add(line.Segs, brush, t, line.Closed, line.Level == EdgeLevel.Ilha ? Brush(240, 220, 236) : null);
        }

        foreach (AxisInput a in _axes)
            sh.Add(a.Path.Segs, Brush(200, 70, 70), 0.6);
        return sh;
    }

    private void RenderMap(Box2? focus, Action<Shapes>? extra)
    {
        Shapes sh = NetworkShapes();
        extra?.Invoke(sh);
        Draw(sh, focus, grid: true);
    }

    private void RenderSignage(SignageOptions o)
    {
        Shapes sh = new();
        foreach (OutputLine line in _result.Lines.Where(l => l.Level is EdgeLevel.FaceMF or EdgeLevel.Canteiro or EdgeLevel.Ilha))
            sh.Add(line.Segs, Brush(120, 120, 120), 0.8, line.Closed);

        List<MarkLine> marks;
        try
        {
            marks = ViarioSignage.Build(_axes, _result, o);
        }
        catch (Exception ex)
        {
            Say("Erro na previa: " + ex.Message);
            return;
        }

        foreach (MarkLine m in marks)
        {
            if (o.Vazada)
                sh.Add(ViarioSignage.Outline(m), Brush(130, 130, 130), 0.8, true);
            else
                sh.Add(m.Segs, m.Yellow ? Brush(214, 160, 0) : Brush(60, 60, 60), -m.Width);
        }

        Draw(sh, null, grid: true);
        _subtitle.Text = (_signExists ? "Bloco " + ViarioSignageSync.BlockName + " no desenho (acompanha a rede). " : "Ainda nao gerada. ") +
                         $"Previa: {marks.Count} marca(s) - {ViarioSignageSync.Summary(marks)}.";
    }

    private void Draw(Shapes sh, Box2? focus, bool grid)
    {
        _graphic.Children.Clear();
        (double w, double h) = GraphicSize();
        List<V2> all = sh.Items.SelectMany(i => i.Points).ToList();
        if (w < 20 || h < 20)
            return;
        if (all.Count == 0)
        {
            Label("Sem eixos na rede.", 10, 10, Muted);
            return;
        }

        Box2 box = focus ?? new Box2(all.Min(p => p.X), all.Min(p => p.Y), all.Max(p => p.X), all.Max(p => p.Y));
        double scale = 0.92 * Math.Min(w / Math.Max(box.MaxX - box.MinX, 1e-6), h / Math.Max(box.MaxY - box.MinY, 1e-6));
        double cx = (box.MinX + box.MaxX) / 2.0, cy = (box.MinY + box.MaxY) / 2.0;
        SW.Point P(V2 p) => new((w / 2.0) + ((p.X - cx) * scale), (h / 2.0) - ((p.Y - cy) * scale));

        if (grid)
        {
            // Grade de 1, 2 ou 5 x 10^n metros, com cerca de 8 divisoes.
            double raw = Math.Max(w, h) / scale / 8.0;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-6))));
            double step = new[] { 1.0, 2.0, 5.0, 10.0 }.Select(k => k * mag).First(v => v >= raw);
            double x0 = cx - (w / 2.0 / scale), x1 = cx + (w / 2.0 / scale);
            double y0 = cy - (h / 2.0 / scale), y1 = cy + (h / 2.0 / scale);
            WM.Brush gridBrush = Brush(236, 236, 236);
            for (double x = Math.Ceiling(x0 / step) * step; x <= x1; x += step)
            {
                double px = P(new V2(x, y0)).X;
                _graphic.Children.Add(new WS.Line { X1 = px, X2 = px, Y1 = 0, Y2 = h, Stroke = gridBrush, StrokeThickness = 1 });
            }

            for (double y = Math.Ceiling(y0 / step) * step; y <= y1; y += step)
            {
                double py = P(new V2(x0, y)).Y;
                _graphic.Children.Add(new WS.Line { X1 = 0, X2 = w, Y1 = py, Y2 = py, Stroke = gridBrush, StrokeThickness = 1 });
            }

            Label($"grade {N(step)} m", 6, h - 18, Muted);
        }

        foreach ((List<V2> pts, WM.Brush brush, double thickness, bool closed, WM.Brush? fill) in sh.Items)
        {
            if (pts.Count < 2)
                continue;
            WM.PointCollection points = new(pts.Select(P));
            WS.Shape shape = closed
                ? new WS.Polygon { Points = points, Fill = fill }
                : new WS.Polyline { Points = points };
            shape.Stroke = brush;
            shape.StrokeThickness = thickness < 0 ? Math.Max(0.6, -thickness * scale) : thickness;
            shape.StrokeLineJoin = WM.PenLineJoin.Round;
            _graphic.Children.Add(shape);
        }
    }

    private void Label(string text, double x, double y, WM.Brush brush, double size = 11)
    {
        WC.TextBlock t = new() { Text = text, Foreground = brush, FontSize = size };
        WC.Canvas.SetLeft(t, x);
        WC.Canvas.SetTop(t, y);
        _graphic.Children.Add(t);
    }

    /// <summary>Corte transversal com cotas, como uma vista de secao do Civil.</summary>
    private void RenderSection(RoadSection s)
    {
        _graphic.Children.Clear();
        (double w, double h) = GraphicSize();
        if (w < 60 || h < 60)
            return;

        double total = s.TotalWidth;
        double k = (w - 60) / Math.Max(total, 1.0);
        double X(double x) => (w / 2.0) + (x * k);
        double road = h * 0.45, curbTop = road - 12, baseY = road + 10;

        for (double y = 20; y < h; y += 20)
            _graphic.Children.Add(new WS.Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = Brush(242, 242, 242), StrokeThickness = 1 });

        void Rect(double x0, double x1, double top, double bottom, WM.Brush fill)
        {
            if (Math.Abs(x1 - x0) < 1e-6)
                return;
            WS.Rectangle r = new()
            {
                Width = Math.Max(1, Math.Abs(X(x1) - X(x0))),
                Height = Math.Max(1, bottom - top),
                Fill = fill,
                Stroke = Brush(60, 60, 60),
                StrokeThickness = 0.6
            };
            WC.Canvas.SetLeft(r, Math.Min(X(x0), X(x1)));
            WC.Canvas.SetTop(r, top);
            _graphic.Children.Add(r);
        }

        void Dim(double a, double b, double y, string text)
        {
            if (Math.Abs(b - a) < 1e-3)
                return;
            WM.Brush ink = Brush(70, 70, 70);
            _graphic.Children.Add(new WS.Line { X1 = X(a), X2 = X(b), Y1 = y, Y2 = y, Stroke = ink, StrokeThickness = 0.8 });
            foreach (double v in new[] { a, b })
                _graphic.Children.Add(new WS.Line { X1 = X(v), X2 = X(v), Y1 = y - 4, Y2 = y + 4, Stroke = ink, StrokeThickness = 0.8 });
            WC.TextBlock t = new() { Text = text, FontSize = 10, Foreground = ink };
            t.Measure(new SW.Size(double.PositiveInfinity, double.PositiveInfinity));
            WC.Canvas.SetLeft(t, ((X(a) + X(b)) / 2.0) - (t.DesiredSize.Width / 2.0));
            WC.Canvas.SetTop(t, y - 15);
            _graphic.Children.Add(t);
        }

        double c = s.Canteiro / 2.0, p = c + (s.Pista / 2.0), f = p + s.Sarjeta, b = f + s.MeioFio, a = b + s.Calcada;
        foreach (int side in new[] { -1, 1 })
        {
            Rect(side * c, side * p, road, baseY, Brush(120, 120, 120));
            Rect(side * p, side * f, road + 2, baseY, Brush(190, 190, 190));
            Rect(side * f, side * b, curbTop, baseY, Brush(215, 215, 215));
            Rect(side * b, side * a, curbTop, curbTop + 5, Brush(226, 214, 190));

            int n = s.FaixasPorSentido;
            for (int i = 1; i < n; i++)
            {
                double x = side * (c + (i * (s.Pista / 2.0) / n));
                _graphic.Children.Add(new WS.Line { X1 = X(x), X2 = X(x), Y1 = road - 5, Y2 = road, Stroke = Brush(40, 40, 40), StrokeThickness = 1.5 });
            }
        }

        if (c > 1e-3)
            Rect(-c, c, curbTop, baseY, Brush(170, 205, 160));

        _graphic.Children.Add(new WS.Line
        {
            X1 = X(0), X2 = X(0), Y1 = 26, Y2 = h - 8, Stroke = Brush(200, 70, 70), StrokeThickness = 0.8,
            StrokeDashArray = new WM.DoubleCollection { 8, 3, 1, 3 }
        });

        // Cotas: lado direito detalhado, total embaixo.
        double y1 = baseY + 24;
        if (c > 1e-3)
            Dim(-c, c, y1, N(s.Canteiro));
        Dim(c, p, y1, N(s.Pista / 2.0));
        if (s.Sarjeta > 0)
            Dim(p, f, y1 + 18, N(s.Sarjeta));
        if (s.MeioFio > 0)
            Dim(f, b, y1 + 36, N(s.MeioFio));
        if (s.Calcada > 0)
            Dim(b, a, y1, N(s.Calcada));
        Dim(-a, a, Math.Min(h - 10, y1 + 58), "total " + N(total));

        Label($"{s.Name}  |  {s.Tipo}  |  {s.FaixasPorSentido * 2} faixas de {N((s.Pista / 2.0) / s.FaixasPorSentido)} m  |  {N(s.Velocidade)} km/h  |  raio {N(s.Raio)} m", 8, 6, Brush(50, 50, 50));
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
}
