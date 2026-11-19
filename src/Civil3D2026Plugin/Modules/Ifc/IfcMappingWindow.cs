using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SW = System.Windows;
using SWD = System.Windows.Data;
using WC = System.Windows.Controls;
using Civil3D2026Plugin.UI.Common;

namespace Civil3D2026Plugin.Modules.Ifc;

/// <summary>
/// Classes IFC por layer/estilo, so pela janela (pedido do Heyvid em 19/11/2026: "nao quero editar classes
/// via documento de texto algum"). Tipo e nome vem de listas do proprio desenho; a classe de uma lista com
/// a explicacao em portugues (pode digitar outra). Guarda no JSON do projeto (e, se pedir, no padrao).
/// </summary>
internal sealed class IfcMappingWindow : SW.Window
{
    internal static readonly (string Key, string Label)[] Kinds =
    {
        ("LayerName", "Layer"),
        ("ShapeStyleName", "Estilo de shape"),
        ("LinkStyleName", "Estilo de link"),
        ("AlignmentStyleName", "Estilo de alinhamento"),
        ("SurfaceStyleName", "Estilo de superfície")
    };

    internal sealed class IfcClass
    {
        public string Cls { get; init; } = "";
        public string Desc { get; init; } = "";
        public string Display => Cls + "  ·  " + Desc;
        public override string ToString() => Cls;
    }

    internal static readonly List<IfcClass> Classes = new()
    {
        new() { Cls = "IfcKerb", Desc = "Meio-fio / sarjeta" },
        new() { Cls = "IfcPavement", Desc = "Pavimento" },
        new() { Cls = "IfcPavement.FLEXIBLE", Desc = "Pavimento flexível" },
        new() { Cls = "IfcPavement.RIGID", Desc = "Pavimento rígido" },
        new() { Cls = "IfcCourse.PAVEMENT", Desc = "Camada de pavimento" },
        new() { Cls = "IfcEarthworksFill.SUBGRADE", Desc = "Subleito" },
        new() { Cls = "IfcEarthworksFill.EMBANKMENT", Desc = "Aterro" },
        new() { Cls = "IfcEarthworksCut", Desc = "Corte" },
        new() { Cls = "IfcSlab", Desc = "Laje / calçada" },
        new() { Cls = "IfcPlate.COVER_PLATE", Desc = "Grelha / tampa" },
        new() { Cls = "IfcFooting.FOOTING_BEAM", Desc = "Viga baldrame" },
        new() { Cls = "IfcSurfaceFeature.PAVEMENTSURFACEMARKING", Desc = "Sinalização horizontal" },
        new() { Cls = "IfcWall", Desc = "Muro" },
        new() { Cls = "IfcRailing", Desc = "Guarda-corpo / defensa" },
        new() { Cls = "IfcGeographicElement.TERRAIN", Desc = "Terreno" },
        new() { Cls = "IfcPipeSegment", Desc = "Tubo" },
        new() { Cls = "IfcDistributionChamberElement", Desc = "Caixa / poço de visita" },
        new() { Cls = "IfcBuildingElementProxy", Desc = "Genérico" }
    };

    internal static string LabelOf(string key) => Kinds.FirstOrDefault(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Label ?? key;

    internal static string KeyOf(string label) => Kinds.FirstOrDefault(k => k.Label == label).Key ?? label;

    private sealed class RowVm : INotifyPropertyChanged
    {
        private readonly Dictionary<string, List<string>> _names;
        private string _tipo = "Layer";
        private string _classe = "";

        public RowVm(Dictionary<string, List<string>> names) => _names = names;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Tipo
        {
            get => _tipo;
            set
            {
                _tipo = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tipo)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NomeOpcoes)));
            }
        }

        public string Nome { get; set; } = "";

        public bool Exportar { get; set; } = true;

        public string Classe
        {
            get => _classe;
            set
            {
                _classe = (value ?? "").Trim();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Classe)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OQueE)));
            }
        }

        public string Observacao { get; set; } = "";

        public List<string> NomeOpcoes => _names.TryGetValue(KeyOf(Tipo), out List<string>? l) ? l : new List<string>();

        public string OQueE => string.IsNullOrWhiteSpace(Classe)
            ? "Automática"
            : Classes.FirstOrDefault(c => c.Cls.Equals(Classe, StringComparison.OrdinalIgnoreCase))?.Desc ?? "";
    }

    public List<IfcMapRow> Result { get; private set; } = new();

    /// <summary>Marcado: grava tambem no padrao (projetos novos comecam com estas classes).</summary>
    public bool SaveAsDefault { get; private set; }

    private readonly ObservableCollection<RowVm> _rows = new();
    private readonly Dictionary<string, List<string>> _names;
    private readonly WC.DataGrid _grid = new();
    private readonly WC.CheckBox _asDefault = new();
    private readonly ToolTheme T = ToolTheme.Current;

    public IfcMappingWindow(Database db, IEnumerable<IfcMapRow> rows)
    {
        _names = DrawingNames(db);
        Load(rows);
        Title = "Classes IFC";
        Width = 1050;
        Height = 620;
        MinWidth = 760;
        MinHeight = 400;
        Background = T.Back;
        Foreground = T.Text;
        WindowStartupLocation = SW.WindowStartupLocation.CenterOwner;
        ToolStyles.Aplicar(Resources, T);
        Content = BuildUi();
    }

    private void Load(IEnumerable<IfcMapRow> rows)
    {
        _rows.Clear();
        foreach (IfcMapRow r in rows)
            _rows.Add(new RowVm(_names) { Tipo = LabelOf(r.Mapeamento), Nome = r.Nome, Exportar = r.Exportar, Classe = r.ClasseIfc, Observacao = r.Observacao });
    }

    private SW.UIElement BuildUi()
    {
        var root = new WC.DockPanel { Margin = new SW.Thickness(10) };
        var help = new WC.TextBlock
        {
            Text = "Classe IFC atribuída a cada layer ou estilo. Itens com Exportar desmarcado não são gravados no arquivo.",
            Foreground = T.Muted,
            TextWrapping = SW.TextWrapping.Wrap,
            Margin = new SW.Thickness(0, 0, 0, 8)
        };
        WC.DockPanel.SetDock(help, WC.Dock.Top);
        root.Children.Add(help);

        var bottom = new WC.DockPanel { Margin = new SW.Thickness(0, 8, 0, 0) };
        var left = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        left.Children.Add(Btn("Adicionar", () =>
        {
            var r = new RowVm(_names) { Tipo = "Layer" };
            _rows.Add(r);
            _grid.SelectedItem = r;
            _grid.ScrollIntoView(r);
        }));
        left.Children.Add(Btn("Remover", () =>
        {
            foreach (RowVm r in _grid.SelectedItems.OfType<RowVm>().ToList())
                _rows.Remove(r);
        }));
        left.Children.Add(Btn("Restaurar padrão", () =>
        {
            if (SW.MessageBox.Show(this, "Substituir a tabela pelas classes padrão?", "Classes IFC",
                    SW.MessageBoxButton.YesNo, SW.MessageBoxImage.Question) == SW.MessageBoxResult.Yes)
                Load(IfcCriteria.DefaultRows());
        }));
        _asDefault.Content = "Definir como padrão";
        _asDefault.Foreground = T.Text;
        _asDefault.VerticalAlignment = SW.VerticalAlignment.Center;
        _asDefault.Margin = new SW.Thickness(8, 0, 0, 0);
        _asDefault.ToolTip = "Desenhos que ainda não foram exportados passam a usar esta tabela.";
        left.Children.Add(_asDefault);
        WC.DockPanel.SetDock(left, WC.Dock.Left);
        bottom.Children.Add(left);
        var right = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = SW.HorizontalAlignment.Right };
        WC.Button ok = Btn("OK", Save);
        ok.FontWeight = SW.FontWeights.Bold;
        right.Children.Add(ok);
        right.Children.Add(Btn("Cancelar", () => DialogResult = false));
        bottom.Children.Add(right);
        WC.DockPanel.SetDock(bottom, WC.Dock.Bottom);
        root.Children.Add(bottom);

        _grid.ItemsSource = _rows;
        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.CanUserDeleteRows = true;
        _grid.SelectionMode = WC.DataGridSelectionMode.Extended;
        _grid.Background = T.Pane;
        _grid.RowBackground = T.Pane;
        _grid.AlternatingRowBackground = T.Back;
        _grid.Foreground = T.Text;
        _grid.BorderBrush = T.Line;
        _grid.HorizontalGridLinesBrush = T.Line;
        _grid.VerticalGridLinesBrush = T.Line;
        _grid.HeadersVisibility = WC.DataGridHeadersVisibility.Column;
        var header = new SW.Style(typeof(WC.Primitives.DataGridColumnHeader));
        header.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, T.Back));
        header.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, T.Text));
        header.Setters.Add(new SW.Setter(WC.Control.BorderBrushProperty, T.Line));
        header.Setters.Add(new SW.Setter(WC.Control.BorderThicknessProperty, new SW.Thickness(0, 0, 1, 1)));
        header.Setters.Add(new SW.Setter(WC.Control.PaddingProperty, new SW.Thickness(6, 3, 6, 3)));
        _grid.ColumnHeaderStyle = header;

        _grid.Columns.Add(new WC.DataGridCheckBoxColumn
        {
            Header = "Exportar",
            Binding = new SWD.Binding(nameof(RowVm.Exportar)) { UpdateSourceTrigger = SWD.UpdateSourceTrigger.PropertyChanged }
        });
        _grid.Columns.Add(new WC.DataGridComboBoxColumn
        {
            Header = "Tipo",
            ItemsSource = Kinds.Select(k => k.Label).ToList(),
            SelectedItemBinding = new SWD.Binding(nameof(RowVm.Tipo)) { UpdateSourceTrigger = SWD.UpdateSourceTrigger.PropertyChanged },
            Width = 190
        });
        _grid.Columns.Add(ComboColumn("Nome", nameof(RowVm.Nome), new SWD.Binding(nameof(RowVm.NomeOpcoes)), null, 220));
        _grid.Columns.Add(ComboColumn("Classe IFC", nameof(RowVm.Classe), null, Classes, 250));
        _grid.Columns.Add(new WC.DataGridTextColumn { Header = "Descrição", Binding = new SWD.Binding(nameof(RowVm.OQueE)) { Mode = SWD.BindingMode.OneWay }, IsReadOnly = true, Width = 190 });
        _grid.Columns.Add(new WC.DataGridTextColumn { Header = "Observação", Binding = new SWD.Binding(nameof(RowVm.Observacao)), Width = new WC.DataGridLength(1, WC.DataGridLengthUnitType.Star) });
        root.Children.Add(_grid);
        return root;
    }

    /// <summary>Coluna com texto livre e lista: mostra o texto; editando, ComboBox editavel.</summary>
    private static WC.DataGridTemplateColumn ComboColumn(string header, string path, SWD.Binding? itemsBinding, IEnumerable? items, double width)
    {
        var view = new SW.FrameworkElementFactory(typeof(WC.TextBlock));
        view.SetBinding(WC.TextBlock.TextProperty, new SWD.Binding(path));
        view.SetValue(WC.TextBlock.MarginProperty, new SW.Thickness(4, 1, 4, 1));
        var edit = new SW.FrameworkElementFactory(typeof(WC.ComboBox));
        edit.SetValue(WC.ComboBox.IsEditableProperty, true);
        edit.SetValue(WC.ComboBox.MaxDropDownHeightProperty, 360.0);
        if (itemsBinding is not null)
            edit.SetBinding(WC.ItemsControl.ItemsSourceProperty, itemsBinding);
        else if (items is not null)
        {
            edit.SetValue(WC.ItemsControl.ItemsSourceProperty, items);
            edit.SetValue(WC.ItemsControl.DisplayMemberPathProperty, nameof(IfcClass.Display));
            edit.SetValue(WC.TextSearch.TextPathProperty, nameof(IfcClass.Cls));
        }
        edit.SetBinding(WC.ComboBox.TextProperty, new SWD.Binding(path) { UpdateSourceTrigger = SWD.UpdateSourceTrigger.PropertyChanged });
        return new WC.DataGridTemplateColumn
        {
            Header = header,
            Width = width,
            CellTemplate = new SW.DataTemplate { VisualTree = view },
            CellEditingTemplate = new SW.DataTemplate { VisualTree = edit }
        };
    }

    private WC.Button Btn(string text, Action a)
    {
        var b = new WC.Button { Content = text, Padding = new SW.Thickness(10, 3, 10, 3), Margin = new SW.Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => a();
        return b;
    }

    private void Save()
    {
        _grid.CommitEdit(WC.DataGridEditingUnit.Row, true);
        Result = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Nome))
            .Select(r => new IfcMapRow { Mapeamento = KeyOf(r.Tipo), Nome = r.Nome.Trim(), Exportar = r.Exportar, ClasseIfc = r.Exportar ? r.Classe : "", Observacao = r.Observacao })
            .ToList();
        SaveAsDefault = _asDefault.IsChecked == true;
        DialogResult = true;
    }

    /// <summary>Layers e estilos do desenho, para as listas de nome.</summary>
    private static Dictionary<string, List<string>> DrawingNames(Database db)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using Transaction tr = db.TransactionManager.StartTransaction();
        try
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            d["LayerName"] = lt.Cast<ObjectId>().Select(id => ((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            d["LayerName"] = new List<string>();
        }
        object? styles = null;
        try
        {
            styles = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(db).Styles;
        }
        catch
        {
            // Sem Civil: so layers.
        }
        foreach ((string key, string prop) in new[] { ("ShapeStyleName", "ShapeStyles"), ("LinkStyleName", "LinkStyles"), ("AlignmentStyleName", "AlignmentStyles"), ("SurfaceStyleName", "SurfaceStyles") })
        {
            var names = new List<string>();
            try
            {
                // Por reflexao: as colecoes de estilo variam entre builds do Civil 2026.
                if (styles?.GetType().GetProperty(prop)?.GetValue(styles) is IEnumerable col)
                {
                    foreach (object o in col)
                    {
                        if (o is ObjectId id && tr.GetObject(id, OpenMode.ForRead) is DBObject s && s.GetType().GetProperty("Name")?.GetValue(s) is string n)
                            names.Add(n);
                    }
                }
            }
            catch
            {
                // Lista vazia: digita o nome.
            }
            d[key] = names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
        tr.Commit();
        return d;
    }
}
