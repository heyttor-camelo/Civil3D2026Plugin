using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Civil3D2026Plugin.UI.Common;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>
/// Ficha do PED (PEDFICHA): janela flutuante que acompanha a selecao do desenho.
/// Clique num bloco PED (sem comando ativo) e a ficha mostra os dados no arranjo da aba
/// "Ficha para desenhos": cabecalho, larguras atual x proposta, informacoes complementares,
/// embarques, distancias, obs, foto do local e mapa de localizacao.
/// A lista a esquerda tem todos os PEDs da planilha; duplo clique faz zoom no bloco.
/// </summary>
internal sealed class PedPanel : SW.Window
{
    private static PedPanel? _instance;

    private readonly ToolTheme T = ToolTheme.Current;
    private static readonly WM.FontFamily UiFont = new("Arial");

    private readonly WM.Brush _ok = ToolTheme.Brush(46, 160, 67);
    private readonly WM.Brush _bad = ToolTheme.Brush(215, 58, 73);

    // Dados
    private Document? _doc;
    private PedMeta? _meta;
    private List<PedColumn> _cols = new();
    private Dictionary<string, PedStored> _peds = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<AcadDb.ObjectId>> _blocks = new(StringComparer.OrdinalIgnoreCase);
    private string? _current;
    private string _tab = "Ficha";
    private bool _suppressList;
    private bool _suppressDrawing;

    // Controles
    private WC.ListView _list = new();
    private WC.TextBox _find = new();
    private WC.CheckBox _follow = new();
    private WC.StackPanel _tabStrip = new();
    private WC.ContentControl _tabContent = new();
    private WC.TextBlock _status = new();
    private WC.TextBlock _subtitle = new();

    private sealed class Row
    {
        public string Code { get; init; } = string.Empty;
        public string Via { get; init; } = string.Empty;
        public string Abrigo { get; init; } = string.Empty;
        public string Desenho { get; init; } = string.Empty;
    }

    // ------------------------------------------------------------------
    // Abrir / fechar
    // ------------------------------------------------------------------

    public static void ShowPanel()
    {
        if (_instance is null)
        {
            _instance = new PedPanel();
            _instance.Closed += (_, _) =>
            {
                _instance?.Detach();
                AcApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
                _instance = null;
            };
            AcApp.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApp.ShowModelessWindow(_instance);
        }
        else
        {
            _instance.Show();
            _instance.Activate();
        }

        _instance.Reload();
        _instance.ShowFromPickfirst();
    }

    public static void RefreshIfOpen() => _instance?.Reload();

    private static void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
    {
        PedPanel? p = _instance;
        p?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_instance == p)
                p.Reload();
        }), SW.Threading.DispatcherPriority.Background);
    }

    private PedPanel()
    {
        Civil3D2026Plugin.UI.Common.LogView.Atalho(this, "PEDs");
        Title = "Ficha do PED";
        Width = 1120;
        Height = 760;
        MinWidth = 760;
        MinHeight = 480;
        ShowInTaskbar = false;
        WindowStartupLocation = SW.WindowStartupLocation.CenterScreen;
        FontFamily = UiFont;
        FontSize = 12;
        Content = BuildUi();
        // Esc nao fecha a janela (decisao do David: nenhum plugin fecha com Esc).
    }

    private void Attach(Document? doc)
    {
        if (ReferenceEquals(doc, _doc))
            return;
        Detach();
        _doc = doc;
        if (_doc is not null)
            _doc.ImpliedSelectionChanged += OnImpliedSelectionChanged;
    }

    private void Detach()
    {
        if (_doc is null)
            return;
        try
        {
            _doc.ImpliedSelectionChanged -= OnImpliedSelectionChanged;
        }
        catch
        {
            // Documento ja fechado.
        }
        _doc = null;
    }

    // ------------------------------------------------------------------
    // Tela
    // ------------------------------------------------------------------

    private SW.UIElement BuildUi()
    {
        Background = T.Back;
        Foreground = T.Text;
        AddStyles();

        WC.Grid root = new();
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1, SW.GridUnitType.Star) });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });

        // Barra superior
        WC.DockPanel top = new() { Margin = new SW.Thickness(6, 6, 6, 4) };
        WC.StackPanel buttons = new() { Orientation = WC.Orientation.Horizontal };
        buttons.Children.Add(Button("Zoom", ZoomCurrent, "Zoom no bloco do PED mostrado (duplo clique na lista faz o mesmo)."));
        buttons.Children.Add(Button("Todos", ZoomAll, "Zoom em todos os PEDs do desenho."));
        buttons.Children.Add(Button("Foto", () => OpenImage(PedFicha.ColFoto), "Abre a foto do local no visualizador do Windows."));
        buttons.Children.Add(Button("Mapa", () => OpenImage(PedFicha.ColMapa), "Abre o mapa de localizacao no visualizador do Windows."));
        buttons.Children.Add(Button("Atualizar", Reload, "Le de novo os dados gravados no desenho."));
        _follow = new WC.CheckBox
        {
            Content = "Seguir seleção do desenho",
            IsChecked = true,
            VerticalAlignment = SW.VerticalAlignment.Center,
            Margin = new SW.Thickness(10, 0, 0, 0),
            ToolTip = "Ao clicar num bloco PED no desenho (sem comando ativo), mostra a ficha dele."
        };
        buttons.Children.Add(_follow);
        WC.DockPanel.SetDock(buttons, WC.Dock.Right);
        top.Children.Add(buttons);
        _subtitle = new WC.TextBlock { Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, TextTrimming = SW.TextTrimming.CharacterEllipsis };
        top.Children.Add(_subtitle);
        root.Children.Add(top);

        // Lista | ficha
        WC.Grid body = new() { Margin = new SW.Thickness(6, 0, 6, 0) };
        body.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(330), MinWidth = 220 });
        body.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(5) });
        body.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star), MinWidth = 380 });

        WC.DockPanel left = new();
        WC.DockPanel findRow = new() { Margin = new SW.Thickness(0, 0, 0, 4) };
        WC.TextBlock findLabel = new() { Text = "Localizar:", VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(0, 0, 4, 0) };
        WC.DockPanel.SetDock(findLabel, WC.Dock.Left);
        findRow.Children.Add(findLabel);
        _find = new WC.TextBox { VerticalContentAlignment = SW.VerticalAlignment.Center };
        _find.TextChanged += (_, _) => FillList();
        findRow.Children.Add(_find);
        WC.DockPanel.SetDock(findRow, WC.Dock.Top);
        left.Children.Add(findRow);

        _list = new WC.ListView { BorderBrush = T.Line, Background = T.Pane, Foreground = T.Text };
        WC.GridView gv = new();
        gv.Columns.Add(Column("PED", nameof(Row.Code), 70));
        gv.Columns.Add(Column("Via", nameof(Row.Via), 140));
        gv.Columns.Add(Column("Abrigo", nameof(Row.Abrigo), 80));
        gv.Columns.Add(Column("DWG", nameof(Row.Desenho), 36));
        _list.View = gv;
        _list.SelectionChanged += (_, _) => OnListSelection();
        _list.MouseDoubleClick += (_, _) => ZoomCurrent();
        left.Children.Add(_list);
        body.Children.Add(left);

        WC.GridSplitter split = new() { Width = 5, HorizontalAlignment = SW.HorizontalAlignment.Stretch, Background = T.Back };
        WC.Grid.SetColumn(split, 1);
        body.Children.Add(split);

        WC.DockPanel right = new();
        _tabStrip = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        WC.Border strip = new() { Child = _tabStrip, BorderBrush = T.Line, BorderThickness = new SW.Thickness(0, 0, 0, 1) };
        WC.DockPanel.SetDock(strip, WC.Dock.Top);
        right.Children.Add(strip);
        _tabContent = new WC.ContentControl();
        right.Children.Add(new WC.Border { BorderBrush = T.Line, BorderThickness = new SW.Thickness(1, 0, 1, 1), Background = T.Pane, Child = _tabContent });
        WC.Grid.SetColumn(right, 2);
        body.Children.Add(right);

        WC.Grid.SetRow(body, 1);
        root.Children.Add(body);

        _status = new WC.TextBlock { Foreground = T.Muted, Margin = new SW.Thickness(8, 4, 8, 6), TextTrimming = SW.TextTrimming.CharacterEllipsis };
        WC.Grid.SetRow(_status, 2);
        root.Children.Add(_status);

        BuildTabs();
        return root;
    }

    private void BuildTabs()
    {
        _tabStrip.Children.Clear();
        foreach (string name in new[] { "Ficha", "Todos os campos" })
        {
            bool active = name == _tab;
            WC.Border tab = new()
            {
                Child = new WC.TextBlock { Text = name, Foreground = active ? T.Text : T.Muted, FontWeight = active ? SW.FontWeights.Bold : SW.FontWeights.Normal },
                Padding = new SW.Thickness(12, 5, 12, 5),
                Background = active ? T.Pane : WM.Brushes.Transparent,
                BorderBrush = active ? T.Line : WM.Brushes.Transparent,
                BorderThickness = new SW.Thickness(1, 1, 1, 0),
                Cursor = WI.Cursors.Hand
            };
            string n = name;
            tab.MouseLeftButtonDown += (_, _) =>
            {
                _tab = n;
                BuildTabs();
                ShowCurrent();
            };
            _tabStrip.Children.Add(tab);
        }
    }

    private static WC.GridViewColumn Column(string header, string path, double width) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new WD.Binding(path) };

    private WC.Button Button(string text, Action action, string tip)
    {
        WC.Button b = new() { Content = text, MinWidth = 64, Padding = new SW.Thickness(10, 3, 10, 3), Margin = new SW.Thickness(0, 0, 6, 0), ToolTip = tip };
        b.Click += (_, _) => action();
        return b;
    }

    private void AddStyles()
    {
        SW.Style header = new(typeof(WC.GridViewColumnHeader));
        header.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Hover));
        header.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        header.Setters.Add(new SW.Setter(WC.Control.BorderBrushProperty, T.Line));
        header.Setters.Add(new SW.Setter(WC.Control.HorizontalContentAlignmentProperty, SW.HorizontalAlignment.Left));
        Resources[typeof(WC.GridViewColumnHeader)] = header;

        SW.FrameworkElementFactory bd = new(typeof(WC.Border), "Bd");
        bd.SetValue(WC.Border.BackgroundProperty, new SW.TemplateBindingExtension(WC.Control.BackgroundProperty));
        bd.SetValue(WC.Border.PaddingProperty, new SW.Thickness(0, 2, 0, 2));
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
        SW.FrameworkElementFactory bbd = new(typeof(WC.Border), "Bd");
        bbd.SetValue(WC.Border.BackgroundProperty, new SW.TemplateBindingExtension(WC.Control.BackgroundProperty));
        bbd.SetValue(WC.Border.BorderBrushProperty, new SW.TemplateBindingExtension(WC.Control.BorderBrushProperty));
        bbd.SetValue(WC.Border.BorderThicknessProperty, new SW.TemplateBindingExtension(WC.Control.BorderThicknessProperty));
        bbd.SetValue(WC.Border.PaddingProperty, new SW.TemplateBindingExtension(WC.Control.PaddingProperty));
        SW.FrameworkElementFactory bcp = new(typeof(WC.ContentPresenter));
        bcp.SetValue(SW.FrameworkElement.HorizontalAlignmentProperty, SW.HorizontalAlignment.Center);
        bcp.SetValue(SW.FrameworkElement.VerticalAlignmentProperty, SW.VerticalAlignment.Center);
        bbd.AppendChild(bcp);
        WC.ControlTemplate buttonTemplate = new(typeof(WC.Button)) { VisualTree = bbd };
        SW.Trigger bHover = new() { Property = SW.UIElement.IsMouseOverProperty, Value = true };
        bHover.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, T.Selected, "Bd"));
        buttonTemplate.Triggers.Add(bHover);
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
    }

    // ------------------------------------------------------------------
    // Dados
    // ------------------------------------------------------------------

    private void Reload()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        Attach(doc);

        _peds = new Dictionary<string, PedStored>(StringComparer.OrdinalIgnoreCase);
        _blocks = new Dictionary<string, List<AcadDb.ObjectId>>(StringComparer.OrdinalIgnoreCase);
        _meta = null;
        _cols = new List<PedColumn>();

        if (doc is not null)
        {
            try
            {
                _meta = PedStore.LoadMeta(doc.Database);
                _cols = PedStore.LoadColumns(doc.Database);
                foreach (PedStored p in PedStore.LoadAll(doc.Database))
                    _peds[p.Code] = p;
                using AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
                _blocks = PedStore.FindBlocks(tr, doc.Database);
            }
            catch (Exception ex)
            {
                Say("Erro ao ler os dados do desenho: " + ex.Message);
            }
        }

        _subtitle.Text = _meta is null
            ? string.Empty
            : $"{Path.GetFileName(_meta.XlsxPath)}  ·  {_meta.SheetName}  ·  importado em {_meta.ImportedAt}" +
              (_meta.Title.Length > 0 ? "  ·  " + _meta.Title : string.Empty);

        if (_peds.Count == 0)
        {
            _list.ItemsSource = null;
            _current = null;
            _tabContent.Content = Message($"Este desenho ainda nao tem PEDs importados.\n\nUse {C3DCommands.Peds.Importar} para ler a planilha e o KMZ.");
            Say(doc is null ? "Sem desenho aberto." : "Sem PEDs neste desenho.");
            return;
        }

        if (_current is not null && !_peds.ContainsKey(_current))
            _current = null;

        FillList();
        int placed = _peds.Keys.Count(k => _blocks.ContainsKey(k));
        Say($"{_peds.Count} PED(s) na base, {placed} com bloco no desenho." +
            (placed < _peds.Count ? $" Os sem bloco: {C3DCommands.Peds.Locar}." : string.Empty));
        ShowCurrent();
    }

    private void FillList()
    {
        string q = PedFormat.Normalize(_find.Text);
        List<Row> rows = _peds.Values
            .OrderBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
            .Where(p => q.Length == 0 ||
                        PedFormat.Normalize(p.Code).Contains(q, StringComparison.Ordinal) ||
                        PedFormat.Normalize(p.Get("B")).Contains(q, StringComparison.Ordinal) ||
                        PedFormat.Normalize(p.Get("C")).Contains(q, StringComparison.Ordinal))
            .Select(p => new Row
            {
                Code = p.Code,
                Via = p.Get("B"),
                Abrigo = p.Get("AX"),
                Desenho = _blocks.ContainsKey(p.Code) ? "●" : "–"
            })
            .ToList();

        _suppressList = true;
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(r => string.Equals(r.Code, _current, StringComparison.OrdinalIgnoreCase));
        _suppressList = false;
    }

    // ------------------------------------------------------------------
    // Selecao
    // ------------------------------------------------------------------

    private void OnListSelection()
    {
        if (_suppressList || _list.SelectedItem is not Row row)
            return;
        _current = row.Code;
        ShowCurrent();
        HighlightInDrawing(row.Code);
    }

    private void OnImpliedSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressDrawing || _follow.IsChecked != true || !IsVisible)
            return;
        ShowFromPickfirst();
    }

    /// <summary>Se ha um bloco PED na selecao atual do desenho, mostra a ficha dele.</summary>
    private void ShowFromPickfirst()
    {
        Document? doc = _doc;
        if (doc is null || _peds.Count == 0)
            return;

        string? code = null;
        try
        {
            PromptSelectionResult res = doc.Editor.SelectImplied();
            if (res.Status != PromptStatus.OK || res.Value is null)
                return;

            using AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
            foreach (AcadDb.ObjectId id in res.Value.GetObjectIds())
            {
                if (id.IsErased)
                    continue;
                code = PedStore.CodeOf(tr.GetObject(id, AcadDb.OpenMode.ForRead));
                if (code is not null)
                    break;
            }
        }
        catch
        {
            return;
        }

        if (code is null || string.Equals(code, _current, StringComparison.OrdinalIgnoreCase))
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _current = code;
            if (!_peds.ContainsKey(code))
            {
                _tabContent.Content = Message($"O bloco selecionado e do PED {code}, que nao esta na base importada.\n\nRode {C3DCommands.Peds.Atualizar} se a planilha foi revisada.");
                return;
            }
            _suppressList = true;
            if (_list.ItemsSource is IEnumerable<Row> rows)
            {
                Row? row = rows.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
                _list.SelectedItem = row;
                if (row is not null)
                    _list.ScrollIntoView(row);
            }
            _suppressList = false;
            ShowCurrent();
        }));
    }

    private void HighlightInDrawing(string code)
    {
        Document? doc = _doc;
        if (doc is null || !_blocks.TryGetValue(code, out List<AcadDb.ObjectId>? ids))
            return;
        try
        {
            _suppressDrawing = true;
            using (doc.LockDocument())
                doc.Editor.SetImpliedSelection(ids.Where(i => !i.IsErased).ToArray());
        }
        catch
        {
            // Com comando ativo nao da para selecionar: so mostra a ficha.
        }
        finally
        {
            _suppressDrawing = false;
        }
    }

    // ------------------------------------------------------------------
    // Ficha
    // ------------------------------------------------------------------

    private void ShowCurrent()
    {
        if (_current is null || !_peds.TryGetValue(_current, out PedStored? ped))
        {
            if (_peds.Count > 0)
                _tabContent.Content = Message("Clique num bloco PED no desenho ou escolha um na lista.");
            return;
        }

        _tabContent.Content = _tab == "Ficha" ? BuildFicha(ped) : BuildAllFields(ped);
    }

    private SW.UIElement BuildFicha(PedStored ped)
    {
        PedFicha.Sheet f = PedFicha.Build(ped.Get);

        WC.Grid grid = new() { Margin = new SW.Thickness(12) };
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star), MinWidth = 340 });
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(14) });
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star), MinWidth = 260 });

        // ---------- coluna de dados ----------
        WC.StackPanel data = new();

        WC.DockPanel title = new() { Margin = new SW.Thickness(0, 0, 0, 8) };
        if (f.Verdict.Length > 0)
        {
            bool ok = f.Verdict == "viavel";
            WC.Border badge = new()
            {
                Background = ok ? _ok : _bad,
                CornerRadius = new SW.CornerRadius(3),
                Padding = new SW.Thickness(8, 2, 8, 2),
                VerticalAlignment = SW.VerticalAlignment.Center,
                Child = new WC.TextBlock { Text = ok ? "viável" : ped.Get("AP"), Foreground = WM.Brushes.White, FontWeight = SW.FontWeights.Bold },
                ToolTip = "Avaliação da faixa de tráfego geral (coluna AP)"
            };
            WC.DockPanel.SetDock(badge, WC.Dock.Right);
            title.Children.Add(badge);
        }
        title.Children.Add(new WC.TextBlock { Text = "PED " + ped.Code, FontSize = 20, FontWeight = SW.FontWeights.Bold });
        data.Children.Add(title);

        data.Children.Add(InfoGrid(f.Header.Skip(1)));

        data.Children.Add(Section("Larguras dos componentes viários (m)"));
        data.Children.Add(WidthTable(f));

        data.Children.Add(Section("Informações complementares da proposta"));
        data.Children.Add(InfoGrid(f.Complement));
        data.Children.Add(Spacer());
        data.Children.Add(InfoGrid(f.Boardings));
        data.Children.Add(Spacer());
        data.Children.Add(DistanceGrid(f.Distances));

        if (f.Obs.Length > 0)
        {
            data.Children.Add(Section("Obs."));
            data.Children.Add(new WC.TextBlock { Text = f.Obs, TextWrapping = SW.TextWrapping.Wrap, Margin = new SW.Thickness(2, 0, 2, 0) });
        }

        List<PedFicha.Info> eval = f.Evaluation.Where(i => i.Value.Length > 0).ToList();
        if (eval.Count > 0)
        {
            data.Children.Add(Section("Avaliação (planilha)"));
            data.Children.Add(InfoGrid(eval));
        }

        data.Children.Add(new WC.TextBlock
        {
            Text = LocationText(ped),
            Foreground = T.Muted,
            FontSize = 11,
            Margin = new SW.Thickness(0, 12, 0, 0),
            TextWrapping = SW.TextWrapping.Wrap
        });

        grid.Children.Add(data);

        // ---------- coluna das imagens ----------
        WC.StackPanel imgs = new();
        imgs.Children.Add(ImageBox("Foto do local", ped, PedFicha.ColFoto));
        imgs.Children.Add(ImageBox("Localização", ped, PedFicha.ColMapa));
        foreach (string extra in ped.Images.Keys.Where(k => k != PedFicha.ColFoto && k != PedFicha.ColMapa))
            imgs.Children.Add(ImageBox(Label(extra), ped, extra));
        WC.Grid.SetColumn(imgs, 2);
        grid.Children.Add(imgs);

        return new WC.ScrollViewer { Content = grid, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
    }

    private SW.UIElement BuildAllFields(PedStored ped)
    {
        WC.Grid g = new() { Margin = new SW.Thickness(12) };
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(40) });
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star), MaxWidth = 340 });
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1.4, SW.GridUnitType.Star) });

        IEnumerable<PedColumn> cols = _cols.Count > 0 ? _cols : ped.Values.Keys.Select(k => new PedColumn(k, k));
        int r = 0;
        foreach (PedColumn c in cols)
        {
            string value = ped.Get(c.Letter);
            if (ped.Images.TryGetValue(c.Letter, out string? img))
                value = "[imagem] " + img;

            g.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
            WM.Brush back = r % 2 == 0 ? WM.Brushes.Transparent : T.Hover;
            AddCell(g, r, 0, c.Letter, T.Muted, back);
            AddCell(g, r, 1, c.Label, T.Text, back);
            AddCell(g, r, 2, value, T.Text, back, bold: value.Length > 0);
            r++;
        }

        return new WC.ScrollViewer { Content = g, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
    }

    private void AddCell(WC.Grid g, int row, int col, string text, WM.Brush fg, WM.Brush back, bool bold = false)
    {
        WC.Border b = new()
        {
            Background = back,
            Padding = new SW.Thickness(4, 2, 4, 2),
            Child = new WC.TextBlock { Text = text, Foreground = fg, TextWrapping = SW.TextWrapping.Wrap, FontWeight = bold ? SW.FontWeights.SemiBold : SW.FontWeights.Normal }
        };
        WC.Grid.SetRow(b, row);
        WC.Grid.SetColumn(b, col);
        g.Children.Add(b);
    }

    private SW.UIElement Section(string text) => new WC.Border
    {
        Background = T.Hover,
        BorderBrush = T.Line,
        BorderThickness = new SW.Thickness(0, 0, 0, 1),
        Padding = new SW.Thickness(6, 3, 6, 3),
        Margin = new SW.Thickness(0, 12, 0, 4),
        Child = new WC.TextBlock { Text = text, FontWeight = SW.FontWeights.Bold }
    };

    private static SW.UIElement Spacer() => new WC.Border { Height = 6 };

    private SW.UIElement InfoGrid(IEnumerable<PedFicha.Info> items)
    {
        WC.Grid g = new();
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(190) });
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        int r = 0;
        foreach (PedFicha.Info i in items)
        {
            g.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
            WC.TextBlock l = new() { Text = i.Label, Foreground = T.Muted, Margin = new SW.Thickness(2, 2, 6, 2), TextWrapping = SW.TextWrapping.Wrap };
            WC.TextBlock v = new() { Text = i.Value.Length == 0 ? "—" : i.Value, Margin = new SW.Thickness(2), TextWrapping = SW.TextWrapping.Wrap, FontWeight = SW.FontWeights.SemiBold };
            WC.Grid.SetRow(l, r);
            WC.Grid.SetRow(v, r);
            WC.Grid.SetColumn(v, 1);
            g.Children.Add(l);
            g.Children.Add(v);
            r++;
        }
        return g;
    }

    private SW.UIElement DistanceGrid(IEnumerable<PedFicha.Distance> items)
    {
        WC.Grid g = new();
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(190) });
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(80) });
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        int r = 0;
        foreach (PedFicha.Distance d in items)
        {
            g.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
            WC.TextBlock l = new() { Text = d.Label, Foreground = T.Muted, Margin = new SW.Thickness(2, 2, 6, 2) };
            bool linkable = _peds.ContainsKey(d.Ped);
            WC.TextBlock p = new()
            {
                Text = d.Ped.Length == 0 ? "—" : d.Ped,
                Margin = new SW.Thickness(2),
                FontWeight = SW.FontWeights.SemiBold,
                Foreground = linkable ? T.Accent : T.Text,
                Cursor = linkable ? WI.Cursors.Hand : null,
                ToolTip = linkable ? "Mostrar a ficha deste PED" : null
            };
            if (linkable)
            {
                string code = d.Ped;
                p.MouseLeftButtonDown += (_, _) => Select(code);
            }
            WC.TextBlock m = new() { Text = d.Meters.Length == 0 ? "—" : d.Meters, Margin = new SW.Thickness(2), FontWeight = SW.FontWeights.SemiBold };
            WC.Grid.SetRow(l, r);
            WC.Grid.SetRow(p, r);
            WC.Grid.SetRow(m, r);
            WC.Grid.SetColumn(p, 1);
            WC.Grid.SetColumn(m, 2);
            g.Children.Add(l);
            g.Children.Add(p);
            g.Children.Add(m);
            r++;
        }
        return g;
    }

    private SW.UIElement WidthTable(PedFicha.Sheet f)
    {
        WC.Grid g = new();
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(130) });
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(70) });
        g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(70) });

        int r = 0;
        void AddRow(string a, string b, string atual, string prop, bool head = false, bool total = false)
        {
            g.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
            WM.Brush back = head || total ? T.Hover : WM.Brushes.Transparent;
            bool changed = !head && !total && atual != prop && atual.Length + prop.Length > 0;
            string[] texts = { a, b, atual, prop };
            for (int c = 0; c < 4; c++)
            {
                WC.Border cell = new()
                {
                    Background = back,
                    BorderBrush = T.Line,
                    BorderThickness = new SW.Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                    Padding = new SW.Thickness(5, 2, 5, 2),
                    Child = new WC.TextBlock
                    {
                        Text = texts[c],
                        TextWrapping = SW.TextWrapping.Wrap,
                        FontWeight = head || total || (c == 3 && changed) ? SW.FontWeights.Bold : SW.FontWeights.Normal,
                        Foreground = c == 3 && changed ? T.Accent : c == 0 && !head && !total ? T.Muted : T.Text,
                        HorizontalAlignment = c >= 2 ? SW.HorizontalAlignment.Right : SW.HorizontalAlignment.Left
                    }
                };
                WC.Grid.SetRow(cell, r);
                WC.Grid.SetColumn(cell, c);
                g.Children.Add(cell);
            }
            r++;
        }

        AddRow("Componente", string.Empty, "Atual", "Proposta", head: true);
        string lastGroup = string.Empty;
        foreach (PedFicha.WidthLine w in f.Widths)
        {
            AddRow(w.Group == lastGroup ? string.Empty : w.Group, w.Item, w.Atual, w.Proposta);
            lastGroup = w.Group;
        }
        AddRow("Seção viária total", string.Empty, f.TotalAtual, f.TotalProposta, total: true);

        return g;
    }

    private SW.UIElement ImageBox(string caption, PedStored ped, string letter)
    {
        WC.StackPanel box = new() { Margin = new SW.Thickness(0, 0, 0, 12) };
        box.Children.Add(new WC.TextBlock { Text = caption, FontWeight = SW.FontWeights.Bold, Margin = new SW.Thickness(0, 0, 0, 4) });

        string? path = ped.Images.TryGetValue(letter, out string? file) && _doc is not null
            ? PedStore.ImagePath(_doc.Database, _meta, file)
            : null;
        WM.Imaging.BitmapImage? img = path is null ? null : PedImages.Load(path, 900);

        if (img is null)
        {
            box.Children.Add(new WC.Border
            {
                Height = 120,
                Background = ToolTheme.GraphicBack,
                Child = new WC.TextBlock
                {
                    Text = file is null ? "Sem imagem na planilha." : $"Imagem nao encontrada ({file}).\nRode {C3DCommands.Peds.Atualizar}.",
                    Foreground = ToolTheme.GraphicText,
                    TextAlignment = SW.TextAlignment.Center,
                    HorizontalAlignment = SW.HorizontalAlignment.Center,
                    VerticalAlignment = SW.VerticalAlignment.Center
                }
            });
            return box;
        }

        WC.Image image = new()
        {
            Source = img,
            Stretch = WM.Stretch.Uniform,
            MaxHeight = 360,
            HorizontalAlignment = SW.HorizontalAlignment.Left,
            Cursor = WI.Cursors.Hand,
            ToolTip = "Clique para abrir no visualizador do Windows"
        };
        string p = path!;
        image.MouseLeftButtonDown += (_, _) => Open(p);
        box.Children.Add(new WC.Border { BorderBrush = T.Line, BorderThickness = new SW.Thickness(1), Child = image, HorizontalAlignment = SW.HorizontalAlignment.Left });
        return box;
    }

    private string Label(string letter) => _cols.FirstOrDefault(c => string.Equals(c.Letter, letter, StringComparison.OrdinalIgnoreCase))?.Label ?? letter;

    private string LocationText(PedStored ped)
    {
        string where = _blocks.TryGetValue(ped.Code, out List<AcadDb.ObjectId>? ids)
            ? $"Bloco no desenho{(ids.Count > 1 ? $" ({ids.Count} copias)" : string.Empty)}."
            : $"Sem bloco no desenho ({C3DCommands.Peds.Locar}).";
        if (ped.Latitude is double lat && ped.Longitude is double lon)
            where += string.Create(CultureInfo.InvariantCulture, $"  KMZ: {lat:0.000000}, {lon:0.000000}");
        return where;
    }

    private WC.TextBlock Message(string text) => new()
    {
        Text = text,
        Foreground = T.Muted,
        Margin = new SW.Thickness(16),
        TextWrapping = SW.TextWrapping.Wrap
    };

    // ------------------------------------------------------------------
    // Acoes
    // ------------------------------------------------------------------

    private void Select(string code)
    {
        _current = code;
        _find.Text = string.Empty;
        FillList();
        if (_list.SelectedItem is Row row)
            _list.ScrollIntoView(row);
        ShowCurrent();
        HighlightInDrawing(code);
    }

    private void ZoomCurrent()
    {
        Document? doc = _doc;
        if (doc is null || _current is null)
            return;
        if (!_blocks.TryGetValue(_current, out List<AcadDb.ObjectId>? ids) || ids.Count == 0)
        {
            Say($"{_current} nao tem bloco no desenho. Use {C3DCommands.Peds.Locar}.");
            return;
        }

        try
        {
            using AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
            if (tr.GetObject(ids[0], AcadDb.OpenMode.ForRead) is AcadDb.BlockReference br)
            {
                double size = Math.Max(60.0, 12.0 * br.ScaleFactors.X * 8.0);
                PedView.ZoomAround(doc, br.Position, size);
                HighlightInDrawing(_current);
            }
        }
        catch (Exception ex)
        {
            Say("Zoom falhou: " + ex.Message);
        }
    }

    private void ZoomAll()
    {
        if (_doc is not null)
            PedCommands.ZoomToPeds(_doc);
    }

    private void OpenImage(string letter)
    {
        if (_current is null || _doc is null || !_peds.TryGetValue(_current, out PedStored? ped))
            return;
        if (!ped.Images.TryGetValue(letter, out string? file) || PedStore.ImagePath(_doc.Database, _meta, file) is not string path)
        {
            Say("Imagem nao encontrada.");
            return;
        }
        Open(path);
    }

    private void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Say("Nao foi possivel abrir: " + ex.Message);
        }
    }

    private void Say(string text) => _status.Text = text;
}
