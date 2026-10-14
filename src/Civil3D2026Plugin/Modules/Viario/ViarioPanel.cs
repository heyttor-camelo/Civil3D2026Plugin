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
/// Painel flutuante da rede viaria (C3DVIARIOPAINEL): comandos com icone e
/// ilustracao, secoes com corte transversal, lista de eixos com mapa,
/// sinalizacao horizontal previa, objetos do Civil e configuracao.
/// </summary>
internal sealed class ViarioPanel : SW.Window
{
    private static ViarioPanel? _instance;

    // ---------------- dados ----------------
    private Document? _doc;
    private List<AxisInput> _axes = new();
    private ViarioResult _result = new();
    private List<RoadSection> _sections = new();
    private ViarioStore.Settings _settings = new();
    private Dictionary<string, string> _names = new();
    private SignageOptions _signage = new();
    private bool _refreshQueued;
    private bool _loading;

    // ---------------- controles ----------------
    private readonly WC.TabControl _tabs = new();
    private readonly WC.TextBlock _status = new();
    private readonly WC.TextBlock _message = new();

    private readonly WC.Image _cmdImage = new() { Width = 120, Height = 120 };
    private readonly WC.TextBlock _cmdTitle = new();
    private readonly WC.TextBlock _cmdText = new();

    private readonly WC.ListBox _sectionList = new();
    private readonly Dictionary<string, WC.TextBox> _sectionFields = new();
    private readonly WC.ComboBox _sectionTipo = new() { IsEditable = true };
    private readonly WC.Canvas _sectionCanvas = new();
    private readonly WC.TextBlock _sectionInfo = new();

    private readonly WC.ListView _axisList = new();
    private readonly WC.Canvas _mapCanvas = new();
    private readonly WC.ComboBox _axisSection = new();
    private readonly WC.ListBox _eventList = new();

    private readonly WC.Canvas _signCanvas = new();
    private readonly WC.TextBlock _signInfo = new();
    private readonly Dictionary<string, SW.FrameworkElement> _signFields = new();

    private readonly WC.TextBlock _civilInfo = new();
    private readonly Dictionary<string, WC.TextBox> _configFields = new();

    // ------------------------------------------------------------------
    // Comandos
    // ------------------------------------------------------------------

    private sealed record Cmd(string Key, string Send, string Title, string Group, string Description, string[] Steps);

    private static readonly Cmd[] Commands =
    {
        new("EIXO", "C3DVIARIOEIXO ", "Lancar eixo", "Desenhar",
            "Desenha eixos (retas e arcos) com a secao corrente; bordos, meio-fio, calcada e esquinas aparecem enquanto desenha.",
            new[] { "Clique os pontos do eixo ([Arco] alterna reta/arco, [Desfazer] volta um ponto).", "Enter termina o eixo; o proximo clique comeca outro.", "[Secao] no primeiro ponto troca a secao.", "Ponta encostada em outro eixo forma intersecao." }),
        new("ADICIONAR", "C3DVIARIO AD ", "Adicionar eixos", "Desenhar",
            "Transforma polilinhas, linhas, arcos ou circulos ja desenhados em eixos da rede (secao corrente).",
            new[] { "Selecione as curvas.", "Enter: entram na rede e a rede e refeita." }),
        new("ROTATORIA", "C3DVIARIO RO ", "Rotatoria", "Desenhar",
            "Anel circular (eixo fechado) com a secao corrente; os eixos que chegam nele viram ramos.",
            new[] { "Clique o centro.", "Digite ou arraste o raio da ilha central (face do meio-fio)." }),
        new("MODELOS", "C3DVIARIOMODELOS ", "Modelos de intersecao", "Desenhar",
            "Janela de intersecoes prontas (cruz, T, Y, gotas, faixas livres, conversao, rotatorias) com preview.",
            new[] { "Escolha o modelo, as secoes e os parametros.", "Inserir: clique o ponto da intersecao e gire." }),
        new("REMOVEREIXO", "C3DVIARIO RE ", "Tirar eixo da rede", "Desenhar",
            "A polilinha continua no desenho, mas deixa de ser eixo (bordos e esquinas somem).",
            new[] { "Selecione os eixos.", "Enter." }),

        new("RAIO", "C3DVIARIORAIO ", "Raio", "Intersecoes",
            "Raio de esquina, de vertice do eixo (canto de quadra), dos cantos de uma ilha ou de todas as esquinas de um eixo.",
            new[] { "Clique perto da esquina (ou dentro de uma ilha rosa).", "Arraste o mouse ou digite o raio; [Padrao] volta ao raio da secao.", "[Eixo] = raio de todas as esquinas de um eixo; [Limpar] apaga os especificos." }),
        new("ALARGAR", "C3DVIARIOALARGAR ", "Alargamento / taper", "Intersecoes",
            "Faixa adicional na aproximacao (conversao a direita ou esquerda) com armazenamento e taper.",
            new[] { "Clique no ramo, perto da intersecao.", "Lado (Direita/Esquerda/Ambos), largura e taper.", "Formato do taper (Suave/Reto).", "Arraste ou digite o comprimento do armazenamento." }),
        new("GOTA", "C3DVIARIOGOTA ", "Gota", "Intersecoes",
            "Ilha em gota na aproximacao de uma via sem canteiro (Tipo II); a pista abre em volta dela.",
            new[] { "Clique no ramo que recebe a gota, perto da intersecao.", "Largura maxima, recuo da ponta e taper.", "Arraste ou digite o comprimento.", "Raio dos cantos: C3DVIARIORAIO clicando dentro da gota." }),
        new("CONVERSAO", "C3DVIARIOCONVERSAO ", "Conversao no canteiro", "Intersecoes",
            "Faixa de conversao a esquerda tirada do canteiro central, junto da pista de quem chega (Tipo IV).",
            new[] { "Clique na aproximacao (via com canteiro), perto da intersecao.", "Esquerda (padrao), largura e taper.", "Arraste ou digite o armazenamento." }),
        new("ABERTURA", "C3DVIARIOABERTURA ", "Abertura de canteiro", "Intersecoes",
            "Abre o canteiro central para retorno no meio da quadra.",
            new[] { "Clique no eixo com canteiro, no centro da abertura.", "Arraste ou digite metade do comprimento." }),
        new("LIVRE", "C3DVIARIOLIVRE ", "Faixa livre a direita", "Intersecoes",
            "Esquina com faixa livre de conversao a direita e ilha triangular (Tipo III).",
            new[] { "Clique perto da esquina.", "Largura da faixa e afastamento da ilha.", "Arraste ou digite o raio do meio-fio externo." }),
        new("ELEMREMOVER", "C3DVIARIOELEMREMOVER ", "Remover elemento", "Intersecoes",
            "Remove alargamento, gota, conversao, abertura ou faixa livre (tambem pela aba Rede).",
            new[] { "Clique perto do elemento.", "Confirme." }),

        new("ILHA", "C3DVIARIOILHA ", "Eliminar ilha", "Ilhas e linhas",
            "Clique dentro de uma ilha (meio-fio fechado, canteiro ou gota) e ela vira pavimento.",
            new[] { "Clique dentro da ilha.", "[Restaurar] traz uma de volta; [Todas] restaura todas." }),
        new("SOLTAR", "C3DVIARIO SO ", "Soltar linhas", "Ilhas e linhas",
            "Linhas geradas viram polilinhas comuns para edicao manual; o plugin nao as recria nem apaga.",
            new[] { "Selecione as linhas geradas.", "Soltar > Restaurar devolve ao plugin." }),
        new("ATUALIZAR", "C3DVIARIO ATU ", "Atualizar rede", "Ilhas e linhas",
            "Recalcula a rede inteira (normalmente ela acompanha sozinha).",
            new[] { "Sem perguntas." }),

        new("CIVIL", "C3DVIARIOCIVIL G ", "Objetos do Civil", "Saidas",
            "Alinhamentos do Civil: eixos, bordos LD/LE (offset dinamico, um por lado) e curb returns conectados.",
            new[] { "Gera ou atualiza; depois acompanham a rede sozinhos.", "Remover pela aba Civil." }),
        new("SINAL", "C3DVIARIOSINAL G ", "Sinalizacao horizontal", "Saidas",
            "Sinalizacao previa num bloco unico: divisoria amarela, divisao de faixas, faixas de pedestres e linhas de retencao.",
            new[] { "Opcoes na aba Sinalizacao.", "O bloco acompanha a rede depois de criado." }),
        new("TESTE", "C3DVIARIOTESTE  ", "Autoteste", "Saidas",
            "Confere linhas desenhadas e objetos do Civil contra a rede e grava relatorio em %TEMP%.",
            new[] { "Sem perguntas (etapa 'manual')." }),
        new("SECAO", "", "Secoes", "Ajustes",
            "Tipos de via: pista, faixas, canteiro, sarjeta, meio-fio, calcada, raio e velocidade (aba Secoes).",
            new[] { "Abre a aba Secoes." }),
        new("CONFIG", "", "Configuracao", "Ajustes",
            "Tolerancias de encontro e captura, recuo do nariz do canteiro (aba Config).",
            new[] { "Abre a aba Config." }),
        new("DIAG", "C3DVIARIODIAG ", "Diagnostico API", "Ajustes",
            "Lista por reflexao o que a API do Civil expoe (somente leitura, para desenvolvimento).",
            new[] { "Relatorio em %TEMP%\\C3DVIARIODIAG.txt." })
    };

    // ------------------------------------------------------------------
    // Abrir / atualizar
    // ------------------------------------------------------------------

    public static void ShowPanel(string? tab = null)
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

        if (tab is not null)
            _instance.SelectTab(tab);
        _instance.Refresh();
    }

    public static void Command(Document doc) => ShowPanel();

    /// <summary>Chamado pela regeneracao: atualiza o painel quando ele estiver aberto.</summary>
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
        Title = "Rede viaria - C3DVIARIO " + C3DVersions.Viario;
        Width = 560;
        Height = 760;
        MinWidth = 460;
        MinHeight = 520;
        ShowInTaskbar = false;
        WindowStartupLocation = SW.WindowStartupLocation.Manual;
        Left = Math.Max(0, SW.SystemParameters.WorkArea.Right - Width - 60);
        Top = 80;
        Icon = ViarioIcons.Image("PAINEL");

        WC.DockPanel root = new();

        // Cabecalho.
        WC.Border header = new()
        {
            Background = new WM.SolidColorBrush(WM.Color.FromRgb(28, 43, 60)),
            Padding = new SW.Thickness(10, 8, 10, 8)
        };
        WC.DockPanel headerRow = new();
        WC.Image logo = new() { Source = ViarioIcons.Image("PAINEL", plate: false), Width = 34, Height = 34, Margin = new SW.Thickness(0, 0, 10, 0) };
        WC.DockPanel.SetDock(logo, WC.Dock.Left);
        headerRow.Children.Add(logo);
        WC.Button refresh = new() { Content = "Atualizar", Padding = new SW.Thickness(10, 2, 10, 2), VerticalAlignment = SW.VerticalAlignment.Center };
        refresh.Click += (_, _) => Refresh();
        WC.DockPanel.SetDock(refresh, WC.Dock.Right);
        headerRow.Children.Add(refresh);
        WC.StackPanel titles = new();
        titles.Children.Add(new WC.TextBlock { Text = "Rede viaria", FontSize = 16, FontWeight = SW.FontWeights.SemiBold, Foreground = WM.Brushes.White });
        _status.Foreground = new WM.SolidColorBrush(WM.Color.FromRgb(180, 200, 215));
        _status.TextWrapping = SW.TextWrapping.Wrap;
        titles.Children.Add(_status);
        headerRow.Children.Add(titles);
        header.Child = headerRow;
        WC.DockPanel.SetDock(header, WC.Dock.Top);
        root.Children.Add(header);

        _message.Margin = new SW.Thickness(8, 4, 8, 4);
        _message.TextWrapping = SW.TextWrapping.Wrap;
        _message.Foreground = WM.Brushes.DimGray;
        WC.DockPanel.SetDock(_message, WC.Dock.Bottom);
        root.Children.Add(_message);

        _tabs.Items.Add(Tab("Comandos", BuildCommandsTab()));
        _tabs.Items.Add(Tab("Secoes", BuildSectionsTab()));
        _tabs.Items.Add(Tab("Rede", BuildNetworkTab()));
        _tabs.Items.Add(Tab("Sinalizacao", BuildSignageTab()));
        _tabs.Items.Add(Tab("Civil", BuildCivilTab()));
        _tabs.Items.Add(Tab("Config", BuildConfigTab()));
        _tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == _tabs)
                RenderAll();
        };
        root.Children.Add(_tabs);

        Content = root;
    }

    private static WC.TabItem Tab(string name, SW.UIElement content) => new() { Header = name, Content = content, Tag = name };

    private void SelectTab(string name)
    {
        foreach (WC.TabItem item in _tabs.Items)
        {
            if (string.Equals(item.Tag as string, name, StringComparison.OrdinalIgnoreCase))
                _tabs.SelectedItem = item;
        }
    }

    private void Refresh()
    {
        _doc = AcApp.DocumentManager.MdiActiveDocument;
        if (_doc is null)
        {
            _status.Text = "Nenhum desenho aberto.";
            return;
        }

        AcadDb.Database db = _doc.Database;
        try
        {
            ViarioSync.Initialize();
            (_axes, _result, _) = ViarioSync.ComputeCurrent(db);
            _sections = ViarioStore.LoadSections(db);
            _settings = ViarioStore.LoadSettings(db);
            _signage = ViarioStore.LoadSignage(db);
            _names = ViarioCivil.AxisNames(db);
        }
        catch (Exception ex)
        {
            _status.Text = "Erro ao ler a rede: " + ex.Message;
            return;
        }

        bool civil = ViarioCivil.IsActive(db);
        bool sign = ViarioSignageSync.Exists(db);
        RoadSection current = CurrentSection();
        _status.Text =
            $"{_axes.Count} eixo(s), {_result.Nodes.Count} no(s), {_result.Corners.Count} esquina(s), {_result.Warnings.Distinct().Count()} aviso(s)\n" +
            $"Secao corrente: {current.Name}  |  Civil: {(civil ? "ativo" : "nao gerado")}  |  Sinalizacao: {(sign ? "sim" : "nao")}";

        _loading = true;
        try
        {
            FillSections();
            FillAxes();
            FillSignage();
            FillCivil(civil, sign);
            FillConfig();
        }
        finally
        {
            _loading = false;
        }

        RenderAll();
    }

    private void RenderAll()
    {
        RenderSection();
        RenderMap();
        RenderSignage();
    }

    private RoadSection CurrentSection() =>
        _sections.FirstOrDefault(s => s.Id == _settings.CurrentSectionId) ?? _sections.FirstOrDefault() ?? ViarioStore.DefaultSection();

    private void Say(string text) => _message.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text;

    // ------------------------------------------------------------------
    // Execucao no desenho
    // ------------------------------------------------------------------

    private void Send(string command)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || command.Length == 0)
            return;

        doc.SendStringToExecute("\x03\x03" + command, true, false, false);
        try
        {
            Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
        }
        catch
        {
            // Foco fica no painel; o comando roda mesmo assim.
        }
    }

    /// <summary>Altera o DWG com o documento travado e regenera a rede.</summary>
    private void Edit(string what, Action<Document, AcadDb.Database> action, bool regenerate = true)
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
                    action(doc, doc.Database);
                }
                finally
                {
                    ViarioSync.Suspended = false;
                }

                if (regenerate)
                    ViarioSync.Regenerate(doc, quiet: true);
            }

            Say(what);
        }
        catch (Exception ex)
        {
            Say("Erro: " + ex.Message);
        }

        Refresh();
    }

    // ------------------------------------------------------------------
    // Aba Comandos
    // ------------------------------------------------------------------

    private SW.UIElement BuildCommandsTab()
    {
        WC.DockPanel dock = new() { Margin = new SW.Thickness(6) };

        // Detalhe do comando (ilustracao grande + passos).
        WC.Border detail = new()
        {
            BorderBrush = WM.Brushes.LightGray,
            BorderThickness = new SW.Thickness(1),
            Padding = new SW.Thickness(8),
            Margin = new SW.Thickness(0, 6, 0, 0)
        };
        WC.DockPanel detailRow = new();
        _cmdImage.Margin = new SW.Thickness(0, 0, 10, 0);
        _cmdImage.VerticalAlignment = SW.VerticalAlignment.Top;
        WC.DockPanel.SetDock(_cmdImage, WC.Dock.Left);
        detailRow.Children.Add(_cmdImage);
        WC.StackPanel texts = new();
        _cmdTitle.FontWeight = SW.FontWeights.SemiBold;
        _cmdTitle.FontSize = 14;
        texts.Children.Add(_cmdTitle);
        _cmdText.TextWrapping = SW.TextWrapping.Wrap;
        _cmdText.Margin = new SW.Thickness(0, 4, 0, 0);
        texts.Children.Add(_cmdText);
        detailRow.Children.Add(texts);
        detail.Child = detailRow;
        WC.DockPanel.SetDock(detail, WC.Dock.Bottom);
        dock.Children.Add(detail);

        WC.StackPanel groups = new();
        foreach (IGrouping<string, Cmd> group in Commands.GroupBy(c => c.Group))
        {
            groups.Children.Add(new WC.TextBlock
            {
                Text = group.Key,
                FontWeight = SW.FontWeights.SemiBold,
                Margin = new SW.Thickness(2, 6, 0, 2),
                Foreground = WM.Brushes.SlateGray
            });

            WC.WrapPanel wrap = new();
            foreach (Cmd cmd in group)
            {
                WC.StackPanel content = new() { Width = 78 };
                content.Children.Add(new WC.Image { Source = ViarioIcons.Image(cmd.Key), Width = 34, Height = 34 });
                content.Children.Add(new WC.TextBlock
                {
                    Text = cmd.Title,
                    TextWrapping = SW.TextWrapping.Wrap,
                    TextAlignment = SW.TextAlignment.Center,
                    FontSize = 11,
                    Margin = new SW.Thickness(0, 2, 0, 0)
                });

                WC.Button button = new()
                {
                    Content = content,
                    Margin = new SW.Thickness(2),
                    Padding = new SW.Thickness(2, 4, 2, 4),
                    Background = WM.Brushes.Transparent,
                    BorderBrush = WM.Brushes.Gainsboro,
                    ToolTip = cmd.Description,
                    VerticalContentAlignment = SW.VerticalAlignment.Top
                };
                Cmd c = cmd;
                button.MouseEnter += (_, _) => ShowCommand(c);
                button.GotFocus += (_, _) => ShowCommand(c);
                button.Click += (_, _) => RunCommand(c);
                wrap.Children.Add(button);
            }

            groups.Children.Add(wrap);
        }

        dock.Children.Add(new WC.ScrollViewer { Content = groups, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto });
        ShowCommand(Commands[0]);
        return dock;
    }

    private void ShowCommand(Cmd cmd)
    {
        _cmdImage.Source = ViarioIcons.Image(cmd.Key);
        _cmdTitle.Text = cmd.Title;
        string command = cmd.Send.Trim().Split(' ')[0];
        _cmdText.Text = cmd.Description + "\n\n" +
                        string.Join("\n", cmd.Steps.Select((s, i) => $"{i + 1}. {s}")) +
                        (command.Length > 0 ? $"\n\nLinha de comando: {command}" : "");
    }

    private void RunCommand(Cmd cmd)
    {
        switch (cmd.Key)
        {
            case "SECAO":
                SelectTab("Secoes");
                return;
            case "CONFIG":
                SelectTab("Config");
                return;
            default:
                Say("Rodando " + cmd.Title + " - siga a linha de comando.");
                Send(cmd.Send);
                return;
        }
    }

    // ------------------------------------------------------------------
    // Aba Secoes
    // ------------------------------------------------------------------

    private static readonly (string Key, string Label)[] SectionNumbers =
    {
        ("Pista", "Pista - largura total de rolamento (m)"),
        ("Faixas", "Faixas, somando os dois sentidos (0 = auto)"),
        ("Canteiro", "Canteiro central (m, 0 = sem)"),
        ("Sarjeta", "Sarjeta de cada lado (m)"),
        ("MeioFio", "Meio-fio, face as costas (m)"),
        ("Calcada", "Calcada de cada lado (m)"),
        ("Raio", "Raio de esquina na face do meio-fio (m)"),
        ("Velocidade", "Velocidade (km/h) - sinalizacao")
    };

    private SW.UIElement BuildSectionsTab()
    {
        WC.Grid grid = new() { Margin = new SW.Thickness(6) };
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(170) });
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        grid.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1, SW.GridUnitType.Star) });
        grid.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(170) });

        // Lista + botoes.
        WC.DockPanel left = new() { Margin = new SW.Thickness(0, 0, 6, 0) };
        WC.Primitives.UniformGrid buttons = new() { Columns = 2, Margin = new SW.Thickness(0, 4, 0, 0) };
        buttons.Children.Add(Button("Nova", () => NewSection(false)));
        buttons.Children.Add(Button("Duplicar", () => NewSection(true)));
        buttons.Children.Add(Button("Excluir", DeleteSection));
        buttons.Children.Add(Button("Corrente", MakeCurrent));
        WC.DockPanel.SetDock(buttons, WC.Dock.Bottom);
        left.Children.Add(buttons);
        _sectionList.SelectionChanged += (_, _) =>
        {
            if (!_loading)
                ShowSection();
        };
        left.Children.Add(_sectionList);
        grid.Children.Add(left);

        // Campos.
        WC.StackPanel form = new();
        WC.TextBox name = new();
        _sectionFields["Name"] = name;
        form.Children.Add(Row("Nome", name));
        foreach (string tipo in ViarioStore.Tipos)
            _sectionTipo.Items.Add(tipo);
        form.Children.Add(Row("Tipo de via", _sectionTipo));
        foreach ((string key, string label) in SectionNumbers)
        {
            WC.TextBox box = new();
            box.TextChanged += (_, _) =>
            {
                if (!_loading)
                    RenderSection();
            };
            _sectionFields[key] = box;
            form.Children.Add(Row(label, box));
        }

        WC.WrapPanel actions = new() { Margin = new SW.Thickness(0, 6, 0, 0) };
        actions.Children.Add(Button("Salvar secao", SaveSection, primary: true));
        actions.Children.Add(Button("Aplicar aos eixos selecionados", ApplyToSelected));
        form.Children.Add(actions);
        _sectionInfo.TextWrapping = SW.TextWrapping.Wrap;
        _sectionInfo.Foreground = WM.Brushes.DimGray;
        _sectionInfo.Margin = new SW.Thickness(0, 6, 0, 0);
        form.Children.Add(_sectionInfo);

        WC.ScrollViewer scroll = new() { Content = form, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
        WC.Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);

        // Corte transversal.
        WC.Border preview = Preview(_sectionCanvas);
        _sectionCanvas.SizeChanged += (_, _) => RenderSection();
        WC.Grid.SetRow(preview, 1);
        WC.Grid.SetColumnSpan(preview, 2);
        preview.Margin = new SW.Thickness(0, 6, 0, 0);
        grid.Children.Add(preview);
        return grid;
    }

    private void FillSections()
    {
        string? selected = (_sectionList.SelectedItem as WC.ListBoxItem)?.Tag as string ?? _settings.CurrentSectionId;
        _sectionList.Items.Clear();
        foreach (RoadSection s in _sections)
        {
            int uses = _axes.Count(a => a.Section.Id == s.Id);
            WC.ListBoxItem item = new()
            {
                Content = $"{s.Name}\n  {s.Tipo}, pista {N(s.Pista)}" + (s.Canteiro > 0 ? $" + canteiro {N(s.Canteiro)}" : "") +
                          $" | {uses} eixo(s)" + (s.Id == _settings.CurrentSectionId ? "  (corrente)" : ""),
                Tag = s.Id,
                FontWeight = s.Id == _settings.CurrentSectionId ? SW.FontWeights.SemiBold : SW.FontWeights.Normal
            };
            _sectionList.Items.Add(item);
            if (s.Id == selected)
                _sectionList.SelectedItem = item;
        }

        if (_sectionList.SelectedIndex < 0 && _sectionList.Items.Count > 0)
            _sectionList.SelectedIndex = 0;
        ShowSection();
    }

    private RoadSection? SelectedSection()
    {
        string? id = (_sectionList.SelectedItem as WC.ListBoxItem)?.Tag as string;
        return _sections.FirstOrDefault(s => s.Id == id);
    }

    private void ShowSection()
    {
        RoadSection? s = SelectedSection();
        if (s is null)
            return;

        bool was = _loading;
        _loading = true;
        _sectionFields["Name"].Text = s.Name;
        _sectionTipo.Text = s.Tipo;
        _sectionFields["Pista"].Text = N(s.Pista);
        _sectionFields["Faixas"].Text = s.Faixas.ToString(CultureInfo.InvariantCulture);
        _sectionFields["Canteiro"].Text = N(s.Canteiro);
        _sectionFields["Sarjeta"].Text = N(s.Sarjeta);
        _sectionFields["MeioFio"].Text = N(s.MeioFio);
        _sectionFields["Calcada"].Text = N(s.Calcada);
        _sectionFields["Raio"].Text = N(s.Raio);
        _sectionFields["Velocidade"].Text = N(s.Velocidade);
        _sectionInfo.Text = $"Codigo interno {s.Id}. Eixos com esta secao: {_axes.Count(a => a.Section.Id == s.Id)}. " +
                            "Para aplicar a eixos: selecione-os no desenho e clique em 'Aplicar aos eixos selecionados'.";
        _loading = was;
        RenderSection();
    }

    /// <summary>Secao com os valores digitados (sem gravar). Null se algum numero for invalido.</summary>
    private RoadSection? EditedSection()
    {
        RoadSection? basis = SelectedSection();
        if (basis is null)
            return null;

        RoadSection s = basis.Clone();
        s.Name = _sectionFields["Name"].Text.Trim().Length > 0 ? _sectionFields["Name"].Text.Trim() : basis.Name;
        s.Tipo = _sectionTipo.Text.Trim().Length > 0 ? _sectionTipo.Text.Trim() : basis.Tipo;
        bool ok = true;
        double Get(string key, double min)
        {
            if (Parse(_sectionFields[key].Text, out double v) && v >= min)
            {
                _sectionFields[key].ClearValue(WC.Control.BackgroundProperty);
                return v;
            }

            _sectionFields[key].Background = WM.Brushes.MistyRose;
            ok = false;
            return 0;
        }

        s.Pista = Get("Pista", 0.5);
        s.Faixas = (int)Math.Round(Get("Faixas", 0));
        s.Canteiro = Get("Canteiro", 0);
        s.Sarjeta = Get("Sarjeta", 0);
        s.MeioFio = Get("MeioFio", 0);
        s.Calcada = Get("Calcada", 0);
        s.Raio = Get("Raio", 0);
        s.Velocidade = Get("Velocidade", 0);
        return ok ? s : null;
    }

    private void SaveSection()
    {
        RoadSection? s = EditedSection();
        if (s is null)
        {
            Say("Corrija os campos em vermelho.");
            return;
        }

        Edit("Secao " + s.Name + " salva.", (_, db) =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            foreach (RoadSection other in _sections.Where(o => o.Id != s.Id))
                ViarioStore.SaveSection(tr, db, other);
            ViarioStore.SaveSection(tr, db, s);
            tr.Commit();
        });
    }

    private void NewSection(bool duplicate)
    {
        RoadSection basis = (duplicate ? SelectedSection() : null) ?? ViarioStore.DefaultSection();
        RoadSection s = basis.Clone();
        s.Id = ViarioStore.NextSectionId(_sections);
        s.Name = duplicate ? basis.Name + " (copia)" : "Nova secao " + s.Id;

        Edit("Secao criada: " + s.Name + ".", (_, db) =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            foreach (RoadSection other in _sections)
                ViarioStore.SaveSection(tr, db, other);
            ViarioStore.SaveSection(tr, db, s);
            tr.Commit();
        }, regenerate: false);

        foreach (WC.ListBoxItem item in _sectionList.Items)
        {
            if (item.Tag as string == s.Id)
                _sectionList.SelectedItem = item;
        }
    }

    private void DeleteSection()
    {
        RoadSection? s = SelectedSection();
        if (s is null)
            return;
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

        Edit("Secao excluida: " + s.Name + ".", (_, db) =>
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

            // Eixos da secao excluida passam para a substituta.
            foreach (AxisInput axis in _axes.Where(a => a.Section.Id == s.Id))
            {
                if (AxisEntity(tr, db, axis.Key, AcadDb.OpenMode.ForWrite) is AcadDb.Entity e)
                    ViarioStore.UpdateAxis(e, sectionId: replacement.Id);
            }

            tr.Commit();
        });
    }

    private void MakeCurrent()
    {
        RoadSection? s = SelectedSection();
        if (s is null)
            return;

        Edit("Secao corrente: " + s.Name + ".", (_, db) =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            foreach (RoadSection other in _sections)
                ViarioStore.SaveSection(tr, db, other);
            _settings.CurrentSectionId = s.Id;
            ViarioStore.SaveSettings(tr, db, _settings);
            tr.Commit();
        }, regenerate: false);
    }

    private void ApplyToSelected()
    {
        RoadSection? s = SelectedSection();
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (s is null || doc is null)
            return;

        PromptSelectionResult implied = doc.Editor.SelectImplied();
        if (implied.Status != PromptStatus.OK || implied.Value.Count == 0)
        {
            Say("Selecione os eixos no desenho (sem comando ativo) e clique de novo.");
            return;
        }

        int count = 0;
        Edit($"Secao {s.Name} aplicada.", (d, db) =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
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

    /// <summary>Corte transversal da secao (sem escala vertical).</summary>
    private void RenderSection()
    {
        _sectionCanvas.Children.Clear();
        double w = _sectionCanvas.ActualWidth, h = _sectionCanvas.ActualHeight;
        RoadSection? s = EditedSection() ?? SelectedSection();
        if (s is null || w < 40 || h < 40)
            return;

        double total = s.TotalWidth;
        double k = (w - 30) / Math.Max(total, 1.0);
        double X(double x) => (w / 2.0) + (x * k);
        double road = h * 0.55, curbTop = road - 14;

        void Rect(double x0, double x1, double top, double bottom, WM.Color fill)
        {
            if (x1 - x0 < 1e-6)
                return;
            WS.Rectangle r = new()
            {
                Width = Math.Max(1, X(x1) - X(x0)),
                Height = Math.Max(1, bottom - top),
                Fill = new WM.SolidColorBrush(fill)
            };
            WC.Canvas.SetLeft(r, X(x0));
            WC.Canvas.SetTop(r, top);
            _sectionCanvas.Children.Add(r);
        }

        void Text(string text, double x, double y, WM.Brush brush)
        {
            WC.TextBlock t = new() { Text = text, Foreground = brush, FontSize = 10 };
            t.Measure(new SW.Size(double.PositiveInfinity, double.PositiveInfinity));
            WC.Canvas.SetLeft(t, x - (t.DesiredSize.Width / 2.0));
            WC.Canvas.SetTop(t, y);
            _sectionCanvas.Children.Add(t);
        }

        double c = s.Canteiro / 2.0, p = c + (s.Pista / 2.0), f = p + s.Sarjeta, b = f + s.MeioFio, a = b + s.Calcada;
        foreach (int side in new[] { -1, 1 })
        {
            double Lo(double u, double v) => Math.Min(side * u, side * v);
            double Hi(double u, double v) => Math.Max(side * u, side * v);
            Rect(Lo(c, p), Hi(c, p), road, road + 10, WM.Color.FromRgb(70, 76, 86));
            Rect(Lo(p, f), Hi(p, f), road + 2, road + 10, WM.Color.FromRgb(150, 150, 150));
            Rect(Lo(f, b), Hi(f, b), curbTop, road + 10, WM.Color.FromRgb(200, 200, 200));
            Rect(Lo(b, a), Hi(b, a), curbTop, curbTop + 6, WM.Color.FromRgb(190, 170, 140));

            int n = s.FaixasPorSentido;
            for (int i = 1; i < n; i++)
            {
                double x = side * (c + (i * (s.Pista / 2.0) / n));
                WS.Line dash = new() { X1 = X(x), X2 = X(x), Y1 = road - 6, Y2 = road, Stroke = WM.Brushes.White, StrokeThickness = 2 };
                _sectionCanvas.Children.Add(dash);
            }
        }

        if (c > 1e-3)
            Rect(-c, c, curbTop, road + 10, WM.Color.FromRgb(90, 160, 80));
        else
            _sectionCanvas.Children.Add(new WS.Line { X1 = X(0), X2 = X(0), Y1 = road - 6, Y2 = road, Stroke = WM.Brushes.Gold, StrokeThickness = 2 });

        _sectionCanvas.Children.Add(new WS.Line
        {
            X1 = X(0), X2 = X(0), Y1 = 8, Y2 = h - 8, Stroke = WM.Brushes.IndianRed, StrokeThickness = 1,
            StrokeDashArray = new WM.DoubleCollection { 6, 3, 1, 3 }
        });

        WM.Brush label = WM.Brushes.Gainsboro;
        Text($"{s.Name} - {s.Tipo} - {s.FaixasPorSentido * 2} faixas - {N(s.Velocidade)} km/h", w / 2.0, 6, WM.Brushes.White);
        Text($"pista {N(s.Pista)}", X(c + (s.Pista / 4.0)), road + 14, label);
        if (c > 1e-3)
            Text($"canteiro {N(s.Canteiro)}", X(0), road + 28, label);
        if (s.Calcada > 0)
            Text($"calcada {N(s.Calcada)}", X(-(b + (s.Calcada / 2.0))), curbTop - 16, label);
        Text($"total {N(total)} m | raio {N(s.Raio)} m", w / 2.0, h - 18, label);
    }

    // ------------------------------------------------------------------
    // Aba Rede
    // ------------------------------------------------------------------

    private sealed class AxisRow
    {
        public string Key { get; init; } = "";
        public string Nome { get; init; } = "";
        public string Secao { get; init; } = "";
        public string Comprimento { get; init; } = "";
        public string Raio { get; init; } = "";
        public string Elementos { get; init; } = "";
    }

    private SW.UIElement BuildNetworkTab()
    {
        WC.Grid grid = new() { Margin = new SW.Thickness(6) };
        grid.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1, SW.GridUnitType.Star) });
        grid.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1, SW.GridUnitType.Star) });
        grid.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });

        WC.Border map = Preview(_mapCanvas);
        _mapCanvas.SizeChanged += (_, _) => RenderMap();
        grid.Children.Add(map);

        WC.GridView view = new();
        foreach ((string header, string prop, double width) in new[]
                 {
                     ("Eixo", "Nome", 120.0), ("Secao", "Secao", 120.0), ("Comp. (m)", "Comprimento", 70.0),
                     ("Raio", "Raio", 60.0), ("Elementos", "Elementos", 160.0)
                 })
        {
            view.Columns.Add(new WC.GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new WD.Binding(prop) });
        }

        _axisList.View = view;
        _axisList.Margin = new SW.Thickness(0, 6, 0, 0);
        _axisList.SelectionChanged += (_, _) =>
        {
            if (_loading)
                return;
            FillEvents();
            RenderMap();
        };
        _axisList.MouseDoubleClick += (_, _) => ZoomSelected();
        WC.Grid.SetRow(_axisList, 1);
        grid.Children.Add(_axisList);

        WC.StackPanel bottom = new() { Margin = new SW.Thickness(0, 6, 0, 0) };
        WC.WrapPanel row1 = new();
        row1.Children.Add(Button("Zoom", ZoomSelected));
        row1.Children.Add(Button("Selecionar no desenho", SelectSelected));
        row1.Children.Add(new WC.TextBlock { Text = "Secao:", VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(8, 0, 4, 0) });
        _axisSection.MinWidth = 140;
        row1.Children.Add(_axisSection);
        row1.Children.Add(Button("Aplicar", AssignSelected));
        row1.Children.Add(Button("Tirar da rede", RemoveSelected));
        bottom.Children.Add(row1);

        WC.DockPanel events = new() { Margin = new SW.Thickness(0, 6, 0, 0) };
        WC.Button removeEvent = Button("Remover elemento", RemoveEvent);
        removeEvent.VerticalAlignment = SW.VerticalAlignment.Top;
        WC.DockPanel.SetDock(removeEvent, WC.Dock.Right);
        events.Children.Add(removeEvent);
        _eventList.Height = 70;
        events.Children.Add(_eventList);
        bottom.Children.Add(new WC.TextBlock { Text = "Elementos do eixo selecionado", Foreground = WM.Brushes.SlateGray });
        bottom.Children.Add(events);
        WC.Grid.SetRow(bottom, 2);
        grid.Children.Add(bottom);
        return grid;
    }

    private string AxisName(AxisInput axis, int index) =>
        _names.TryGetValue(axis.Key, out string? name) ? name : $"Eixo {index + 1} ({axis.Key})";

    private void FillAxes()
    {
        string? selected = (_axisList.SelectedItem as AxisRow)?.Key;
        _axisList.Items.Clear();
        for (int i = 0; i < _axes.Count; i++)
        {
            AxisInput a = _axes[i];
            AxisRow row = new()
            {
                Key = a.Key,
                Nome = AxisName(a, i),
                Secao = a.Section.Name,
                Comprimento = N(a.Path.Length),
                Raio = a.Radius is double r ? N(r) : "secao",
                Elementos = a.Events.Count == 0 ? "-" : string.Join(", ", a.Events.GroupBy(e => e.Kind).Select(g => $"{g.Key} {g.Count()}"))
            };
            _axisList.Items.Add(row);
            if (row.Key == selected)
                _axisList.SelectedItem = row;
        }

        _axisSection.Items.Clear();
        foreach (RoadSection s in _sections)
            _axisSection.Items.Add(new WC.ComboBoxItem { Content = s.Name, Tag = s.Id });
        _axisSection.SelectedIndex = Math.Max(0, _sections.FindIndex(s => s.Id == _settings.CurrentSectionId));
        FillEvents();
    }

    private AxisInput? SelectedAxis()
    {
        string? key = (_axisList.SelectedItem as AxisRow)?.Key;
        return _axes.FirstOrDefault(a => a.Key == key);
    }

    private void FillEvents()
    {
        _eventList.Items.Clear();
        AxisInput? axis = SelectedAxis();
        if (axis is null)
            return;

        foreach (AxisEvent ev in axis.Events)
            _eventList.Items.Add(new WC.ListBoxItem { Content = ev.Describe(), Tag = ev.Serialize() });

        int index = _sections.FindIndex(s => s.Id == axis.Section.Id);
        if (index >= 0)
            _axisSection.SelectedIndex = index;
    }

    private static AcadDb.Entity? AxisEntity(AcadDb.Transaction tr, AcadDb.Database db, string key, AcadDb.OpenMode mode)
    {
        if (!long.TryParse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
            return null;
        if (!db.TryGetObjectId(new AcadDb.Handle(value), out AcadDb.ObjectId id) || id.IsErased)
            return null;
        return tr.GetObject(id, mode, false) as AcadDb.Entity;
    }

    private void ZoomSelected()
    {
        AxisInput? axis = SelectedAxis();
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (axis is null || doc is null)
            return;

        Box2 box = axis.Path.Bounds().Inflate(axis.Section.TotalWidth);
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
        }

        SelectSelected();
    }

    private void SelectSelected()
    {
        AxisInput? axis = SelectedAxis();
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (axis is null || doc is null)
            return;

        using (doc.LockDocument())
        {
            using AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
            if (AxisEntity(tr, doc.Database, axis.Key, AcadDb.OpenMode.ForRead) is AcadDb.Entity e)
                doc.Editor.SetImpliedSelection(new[] { e.ObjectId });
        }

        Say("Eixo selecionado no desenho.");
    }

    private void AssignSelected()
    {
        AxisInput? axis = SelectedAxis();
        string? id = (_axisSection.SelectedItem as WC.ComboBoxItem)?.Tag as string;
        if (axis is null || id is null)
            return;

        Edit("Secao do eixo alterada.", (_, db) =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            foreach (RoadSection other in _sections)
                ViarioStore.SaveSection(tr, db, other);
            if (AxisEntity(tr, db, axis.Key, AcadDb.OpenMode.ForWrite) is AcadDb.Entity e)
                ViarioStore.UpdateAxis(e, sectionId: id);
            tr.Commit();
        });
    }

    private void RemoveSelected()
    {
        AxisInput? axis = SelectedAxis();
        if (axis is null)
            return;

        Edit("Eixo tirado da rede (a polilinha continua no desenho).", (_, db) =>
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            if (AxisEntity(tr, db, axis.Key, AcadDb.OpenMode.ForWrite) is AcadDb.Entity e)
                ViarioStore.ClearTag(e);
            tr.Commit();
        });
    }

    private void RemoveEvent()
    {
        AxisInput? axis = SelectedAxis();
        string? serialized = (_eventList.SelectedItem as WC.ListBoxItem)?.Tag as string;
        if (axis is null || serialized is null)
        {
            Say("Escolha um elemento na lista.");
            return;
        }

        Edit("Elemento removido.", (doc, _) =>
            ViarioElementCommands.ModifyAxisEvents(doc, axis.Key, events => events.RemoveAll(e => e.Serialize() == serialized)));
    }

    private void RenderMap()
    {
        AxisInput? selected = SelectedAxis();
        List<(List<V2> Points, WM.Brush Brush, double Thickness)> shapes = new();
        foreach (OutputLine line in _result.Lines)
            shapes.Add((Sample(line.Segs), LineBrush(line.Level), 1.0));
        foreach (AxisInput a in _axes)
            shapes.Add((Sample(a.Path.Segs), a == selected ? WM.Brushes.Gold : WM.Brushes.IndianRed, a == selected ? 3.0 : 0.8));
        Draw(_mapCanvas, shapes, null);
    }

    // ------------------------------------------------------------------
    // Aba Sinalizacao
    // ------------------------------------------------------------------

    private static readonly (string Prop, string Label)[] SignageLabels =
    {
        ("Central", "Divisoria amarela (vias sem canteiro)"),
        ("CentralTipo", "Tipo da divisoria"),
        ("ContinuaAntes", "Continua antes da retencao (m)"),
        ("Faixas", "Divisao de faixas do mesmo sentido"),
        ("Bordo", "Linha de bordo"),
        ("Travessia", "Faixa de pedestres (zebrada)"),
        ("Retencao", "Linha de retencao"),
        ("CadenciaAuto", "Cadencia pela velocidade da secao"),
        ("Largura", "Largura da linha (m)"),
        ("Traco", "Traco (m)"),
        ("Espaco", "Espaco (m)"),
        ("TravessiaLargura", "Extensao das listras (m)"),
        ("ListraLargura", "Largura da listra (m)"),
        ("ListraEspaco", "Espaco entre listras (m)"),
        ("TravessiaRecuo", "Recuo da faixa ao fim da curva (m)"),
        ("RetencaoLargura", "Largura da retencao (m)"),
        ("RetencaoDistancia", "Retencao ate a faixa (m)")
    };

    private SW.UIElement BuildSignageTab()
    {
        WC.Grid grid = new() { Margin = new SW.Thickness(6) };
        grid.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1, SW.GridUnitType.Star) });
        grid.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1.1, SW.GridUnitType.Star) });

        WC.Border preview = Preview(_signCanvas);
        _signCanvas.SizeChanged += (_, _) => RenderSignage();
        grid.Children.Add(preview);

        WC.StackPanel form = new() { Margin = new SW.Thickness(0, 6, 0, 0) };
        foreach ((string prop, string label) in SignageLabels)
        {
            System.Reflection.PropertyInfo info = typeof(SignageOptions).GetProperty(prop)!;
            SW.FrameworkElement control;
            if (info.PropertyType == typeof(bool))
            {
                WC.CheckBox box = new() { Content = label, Margin = new SW.Thickness(0, 2, 0, 2) };
                box.Click += (_, _) => SignageChanged();
                control = box;
                form.Children.Add(box);
            }
            else if (prop == "CentralTipo")
            {
                WC.ComboBox combo = new();
                foreach (string t in new[] { "Seccionada", "Continua", "Dupla" })
                    combo.Items.Add(t);
                combo.SelectionChanged += (_, _) => SignageChanged();
                control = combo;
                form.Children.Add(Row(label, combo));
            }
            else
            {
                WC.TextBox box = new();
                box.TextChanged += (_, _) => SignageChanged();
                control = box;
                form.Children.Add(Row(label, box));
            }

            _signFields[prop] = control;
        }

        WC.WrapPanel actions = new() { Margin = new SW.Thickness(0, 6, 0, 0) };
        actions.Children.Add(Button("Gerar / atualizar bloco", GenerateSignage, primary: true));
        actions.Children.Add(Button("Remover bloco", RemoveSignage));
        form.Children.Add(actions);
        _signInfo.TextWrapping = SW.TextWrapping.Wrap;
        _signInfo.Foreground = WM.Brushes.DimGray;
        _signInfo.Margin = new SW.Thickness(0, 6, 0, 0);
        form.Children.Add(_signInfo);

        WC.ScrollViewer scroll = new() { Content = form, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
        WC.Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        return grid;
    }

    private void FillSignage()
    {
        foreach ((string prop, _) in SignageLabels)
        {
            object? value = typeof(SignageOptions).GetProperty(prop)!.GetValue(_signage);
            switch (_signFields[prop])
            {
                case WC.CheckBox box:
                    box.IsChecked = value is true;
                    break;
                case WC.ComboBox combo:
                    combo.SelectedItem = value as string;
                    break;
                case WC.TextBox text:
                    text.Text = value is double d ? N(d) : value?.ToString() ?? "";
                    break;
            }
        }
    }

    /// <summary>Opcoes da tela (null se algum numero for invalido).</summary>
    private SignageOptions? EditedSignage()
    {
        SignageOptions o = _signage.Clone();
        bool ok = true;
        foreach ((string prop, _) in SignageLabels)
        {
            System.Reflection.PropertyInfo info = typeof(SignageOptions).GetProperty(prop)!;
            switch (_signFields[prop])
            {
                case WC.CheckBox box:
                    info.SetValue(o, box.IsChecked == true);
                    break;
                case WC.ComboBox combo:
                    info.SetValue(o, combo.SelectedItem as string ?? "Seccionada");
                    break;
                case WC.TextBox text:
                    if (Parse(text.Text, out double v) && v >= 0)
                    {
                        info.SetValue(o, v);
                        text.ClearValue(WC.Control.BackgroundProperty);
                    }
                    else
                    {
                        text.Background = WM.Brushes.MistyRose;
                        ok = false;
                    }

                    break;
            }
        }

        bool manual = !o.CadenciaAuto;
        foreach (string prop in new[] { "Largura", "Traco", "Espaco" })
            _signFields[prop].IsEnabled = manual;
        return ok ? o : null;
    }

    private void SignageChanged()
    {
        if (!_loading)
            RenderSignage();
    }

    private void RenderSignage()
    {
        SignageOptions? o = EditedSignage();
        if (o is null)
            return;

        List<MarkLine> marks;
        try
        {
            marks = ViarioSignage.Build(_axes, _result, o);
        }
        catch (Exception ex)
        {
            _signInfo.Text = "Erro: " + ex.Message;
            return;
        }

        List<(List<V2> Points, WM.Brush Brush, double Thickness)> shapes = new();
        foreach (OutputLine line in _result.Lines)
            shapes.Add((Sample(line.Segs), LineBrush(line.Level), 0.8));
        foreach (MarkLine m in marks)
            shapes.Add((Sample(m.Segs), m.Yellow ? WM.Brushes.Gold : WM.Brushes.White, -m.Width));
        Draw(_signCanvas, shapes, null);

        _signInfo.Text = $"Previa: {marks.Count} marca(s) - {ViarioSignageSync.Summary(marks)}.\n" +
                         "LFO divisoria amarela, LMS divisao de faixas, FTP faixa de pedestres, LRE retencao, LBO bordo. " +
                         "Valores do Manual Brasileiro de Sinalizacao de Transito, Vol. IV (conferir no projeto executivo). " +
                         "O bloco " + ViarioSignageSync.BlockName + " fica na origem e acompanha a rede.";
    }

    private void GenerateSignage()
    {
        SignageOptions? o = EditedSignage();
        if (o is null)
        {
            Say("Corrija os campos em vermelho.");
            return;
        }

        int count = 0;
        Edit("Sinalizacao gerada.", (doc, db) =>
        {
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                ViarioStore.SaveSignage(tr, db, o);
                tr.Commit();
            }

            count = ViarioSignageSync.Sync(doc, _axes, _result, create: true);
        }, regenerate: false);
        Say($"Sinalizacao gerada: {count} marca(s) no bloco {ViarioSignageSync.BlockName}.");
    }

    private void RemoveSignage()
    {
        Edit("Sinalizacao removida.", (doc, _) => ViarioSignageSync.Remove(doc), regenerate: false);
    }

    // ------------------------------------------------------------------
    // Aba Civil
    // ------------------------------------------------------------------

    private SW.UIElement BuildCivilTab()
    {
        WC.StackPanel panel = new() { Margin = new SW.Thickness(8) };
        WC.StackPanel title = new() { Orientation = WC.Orientation.Horizontal };
        title.Children.Add(new WC.Image { Source = ViarioIcons.Image("CIVIL"), Width = 48, Height = 48, Margin = new SW.Thickness(0, 0, 10, 0) });
        title.Children.Add(new WC.TextBlock
        {
            Text = "Objetos do Civil 3D",
            FontSize = 14,
            FontWeight = SW.FontWeights.SemiBold,
            VerticalAlignment = SW.VerticalAlignment.Center
        });
        panel.Children.Add(title);
        _civilInfo.TextWrapping = SW.TextWrapping.Wrap;
        _civilInfo.Margin = new SW.Thickness(0, 8, 0, 8);
        panel.Children.Add(_civilInfo);

        WC.WrapPanel actions = new();
        actions.Children.Add(Button("Gerar / atualizar", () => Send("C3DVIARIOCIVIL G "), primary: true));
        actions.Children.Add(Button("Remover", () => Send("C3DVIARIOCIVIL R ")));
        actions.Children.Add(Button("Autoteste", () => Send("C3DVIARIOTESTE  ")));
        panel.Children.Add(actions);

        panel.Children.Add(new WC.TextBlock
        {
            Text = "Altimetria (proxima etapa): perfil do eixo, perfis offset dos bordos pela declividade transversal, " +
                   "perfis dos curb returns e amarracao de greides nos nos.",
            TextWrapping = SW.TextWrapping.Wrap,
            Foreground = WM.Brushes.SlateGray,
            Margin = new SW.Thickness(0, 14, 0, 0)
        });
        return panel;
    }

    private void FillCivil(bool civil, bool sign)
    {
        _civilInfo.Text = civil
            ? "Gerados e acompanhando a rede: eixos (alinhamentos), bordos LD/LE (offset dinamico, um por lado, " +
              "com alargamentos) e curb returns conectados aos bordos. Sem site (nao formam lotes)."
            : "Ainda nao gerados neste desenho. 'Gerar' cria eixos, bordos LD/LE e curb returns; depois eles acompanham a rede.";
        _civilInfo.Text += sign ? "\nSinalizacao previa: bloco presente." : "";
    }

    // ------------------------------------------------------------------
    // Aba Config
    // ------------------------------------------------------------------

    private SW.UIElement BuildConfigTab()
    {
        WC.StackPanel panel = new() { Margin = new SW.Thickness(8) };
        foreach ((string key, string label) in new[]
                 {
                     ("SnapTolerance", "Tolerancia de encontro entre eixos (m)"),
                     ("Capture", "Captura das pontas ao lancar eixos (m, 0 = desligada)"),
                     ("MedianSetback", "Recuo do nariz do canteiro (m)")
                 })
        {
            WC.TextBox box = new();
            _configFields[key] = box;
            panel.Children.Add(Row(label, box));
        }

        panel.Children.Add(Button("Salvar configuracao", SaveConfig, primary: true));
        panel.Children.Add(new WC.TextBlock
        {
            Text = "Os valores ficam no desenho. A rede e refeita ao salvar.",
            Foreground = WM.Brushes.DimGray,
            Margin = new SW.Thickness(0, 6, 0, 0)
        });
        return panel;
    }

    private void FillConfig()
    {
        _configFields["SnapTolerance"].Text = N(_settings.SnapTolerance);
        _configFields["Capture"].Text = N(_settings.Capture);
        _configFields["MedianSetback"].Text = N(_settings.MedianSetback);
    }

    private void SaveConfig()
    {
        if (!Parse(_configFields["SnapTolerance"].Text, out double snap) || snap <= 0 ||
            !Parse(_configFields["Capture"].Text, out double capture) || capture < 0 ||
            !Parse(_configFields["MedianSetback"].Text, out double setback) || setback < 0)
        {
            Say("Valores invalidos.");
            return;
        }

        Edit("Configuracao salva.", (_, db) =>
        {
            _settings.SnapTolerance = snap;
            _settings.Capture = capture;
            _settings.MedianSetback = setback;
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            ViarioStore.SaveSettings(tr, db, _settings);
            tr.Commit();
        });
    }

    // ------------------------------------------------------------------
    // Utilitarios de tela
    // ------------------------------------------------------------------

    private static WC.Button Button(string text, Action action, bool primary = false)
    {
        WC.Button b = new()
        {
            Content = text,
            Margin = new SW.Thickness(2),
            Padding = new SW.Thickness(8, 3, 8, 3),
            FontWeight = primary ? SW.FontWeights.SemiBold : SW.FontWeights.Normal
        };
        b.Click += (_, _) => action();
        return b;
    }

    private static WC.Grid Row(string label, SW.UIElement control)
    {
        WC.Grid row = new() { Margin = new SW.Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(120) });
        row.Children.Add(new WC.TextBlock { Text = label, VerticalAlignment = SW.VerticalAlignment.Center, TextWrapping = SW.TextWrapping.Wrap });
        WC.Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static WC.Border Preview(WC.Canvas canvas) => new()
    {
        BorderBrush = WM.Brushes.Gray,
        BorderThickness = new SW.Thickness(1),
        Background = new WM.SolidColorBrush(WM.Color.FromRgb(33, 40, 48)),
        Child = canvas,
        ClipToBounds = true
    };

    private static WM.Brush LineBrush(EdgeLevel level) => level switch
    {
        EdgeLevel.Pista => WM.Brushes.WhiteSmoke,
        EdgeLevel.FaceMF => WM.Brushes.Cyan,
        EdgeLevel.CostasMF => WM.Brushes.LimeGreen,
        EdgeLevel.Alinhamento => WM.Brushes.Gray,
        EdgeLevel.Canteiro => WM.Brushes.Orange,
        _ => WM.Brushes.Magenta
    };

    /// <summary>
    /// Desenha formas no canvas ajustando a escala. Espessura negativa = largura
    /// real em metros (marcas de sinalizacao).
    /// </summary>
    private static void Draw(WC.Canvas canvas, List<(List<V2> Points, WM.Brush Brush, double Thickness)> shapes, Box2? focus)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        List<V2> all = shapes.SelectMany(s => s.Points).ToList();
        if (w < 20 || h < 20 || all.Count == 0)
        {
            if (w >= 20 && h >= 20)
            {
                WC.TextBlock empty = new() { Text = "Sem eixos na rede.", Foreground = WM.Brushes.Gray };
                WC.Canvas.SetLeft(empty, 10);
                WC.Canvas.SetTop(empty, 10);
                canvas.Children.Add(empty);
            }

            return;
        }

        Box2 box = focus ?? new Box2(all.Min(p => p.X), all.Min(p => p.Y), all.Max(p => p.X), all.Max(p => p.Y));
        double scale = 0.94 * Math.Min(w / Math.Max(box.MaxX - box.MinX, 1e-6), h / Math.Max(box.MaxY - box.MinY, 1e-6));
        double cx = (box.MinX + box.MaxX) / 2.0, cy = (box.MinY + box.MaxY) / 2.0;

        foreach ((List<V2> pts, WM.Brush brush, double thickness) in shapes)
        {
            WS.Polyline poly = new()
            {
                Stroke = brush,
                StrokeThickness = thickness < 0 ? Math.Max(0.6, -thickness * scale) : thickness,
                StrokeLineJoin = WM.PenLineJoin.Round
            };
            foreach (V2 p in pts)
                poly.Points.Add(new SW.Point((w / 2.0) + ((p.X - cx) * scale), (h / 2.0) - ((p.Y - cy) * scale)));
            canvas.Children.Add(poly);
        }
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

    private static bool Parse(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
