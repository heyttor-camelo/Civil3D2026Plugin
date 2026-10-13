using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using SW = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WS = System.Windows.Shapes;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Janela de modelos de intersecao: lista de modelos, parametros, secoes e
/// pre-visualizacao desenhada pelo proprio motor da rede.
/// </summary>
internal sealed class ViarioTemplateWindow : SW.Window
{
    private readonly List<IntersectionTemplate> _templates = ViarioTemplates.All();
    private readonly List<RoadSection> _sections;
    private readonly WC.ListBox _list = new();
    private readonly WC.TextBlock _description = new();
    private readonly WC.Canvas _canvas = new();
    private readonly WC.StackPanel _fields = new();
    private readonly WC.ComboBox _principal = new();
    private readonly WC.ComboBox _secundaria = new();
    private readonly WC.ComboBox _anel = new();
    private readonly WC.TextBlock _notes = new();
    private bool _building;

    public IntersectionTemplate? Selected { get; private set; }

    public TemplateSections Sections => new()
    {
        Principal = SectionOf(_principal),
        Secundaria = SectionOf(_secundaria),
        Anel = SectionOf(_anel)
    };

    public ViarioTemplateWindow(List<RoadSection> sections, RoadSection current)
    {
        _sections = sections;

        Title = "Modelos de intersecao - C3DVIARIO";
        Width = 1000;
        Height = 680;
        MinWidth = 800;
        MinHeight = 520;
        WindowStartupLocation = SW.WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;

        WC.Grid root = new() { Margin = new SW.Thickness(10) };
        root.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(250) });
        root.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1, SW.GridUnitType.Star) });
        root.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });

        // Lista de modelos.
        foreach (IntersectionTemplate t in _templates)
            _list.Items.Add(t.Name);
        _list.Margin = new SW.Thickness(0, 0, 10, 0);
        _list.SelectionChanged += (_, _) => OnTemplateChanged();
        WC.Grid.SetColumn(_list, 0);
        WC.Grid.SetRow(_list, 0);
        root.Children.Add(_list);

        // Lado direito: descricao, preview, campos.
        WC.Grid right = new();
        right.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        right.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(1, SW.GridUnitType.Star) });
        right.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        right.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        right.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(330) });

        _description.TextWrapping = SW.TextWrapping.Wrap;
        _description.Margin = new SW.Thickness(0, 0, 0, 6);
        _description.FontWeight = SW.FontWeights.SemiBold;
        WC.Grid.SetRow(_description, 0);
        WC.Grid.SetColumnSpan(_description, 2);
        right.Children.Add(_description);

        WC.Border previewBorder = new()
        {
            BorderBrush = WM.Brushes.Gray,
            BorderThickness = new SW.Thickness(1),
            Background = new WM.SolidColorBrush(WM.Color.FromRgb(33, 40, 48)),
            Child = _canvas,
            ClipToBounds = true
        };
        _canvas.SizeChanged += (_, _) => RenderPreview();
        WC.Grid.SetRow(previewBorder, 1);
        WC.Grid.SetColumn(previewBorder, 0);
        right.Children.Add(previewBorder);

        WC.StackPanel side = new() { Margin = new SW.Thickness(10, 0, 0, 0) };
        side.Children.Add(Caption("Secoes"));
        side.Children.Add(Row("Principal", _principal));
        side.Children.Add(Row("Secundaria", _secundaria));
        side.Children.Add(Row("Anel (rotatoria)", _anel));
        side.Children.Add(Caption("Parametros"));
        side.Children.Add(_fields);
        _notes.TextWrapping = SW.TextWrapping.Wrap;
        _notes.Foreground = WM.Brushes.DarkOrange;
        _notes.Margin = new SW.Thickness(0, 8, 0, 0);
        side.Children.Add(_notes);

        WC.ScrollViewer scroll = new()
        {
            Content = side,
            VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto
        };
        WC.Grid.SetRow(scroll, 1);
        WC.Grid.SetColumn(scroll, 1);
        right.Children.Add(scroll);

        WC.TextBlock hint = new()
        {
            Text = "Depois de Inserir: clique o ponto da intersecao e gire com o mouse (ou digite o angulo). " +
                   "Os eixos entram na rede e podem ser editados como qualquer outro.",
            TextWrapping = SW.TextWrapping.Wrap,
            Foreground = WM.Brushes.DimGray,
            Margin = new SW.Thickness(0, 6, 0, 0)
        };
        WC.Grid.SetRow(hint, 2);
        WC.Grid.SetColumnSpan(hint, 2);
        right.Children.Add(hint);

        WC.Grid.SetColumn(right, 1);
        WC.Grid.SetRow(right, 0);
        root.Children.Add(right);

        // Botoes.
        WC.StackPanel buttons = new()
        {
            Orientation = WC.Orientation.Horizontal,
            HorizontalAlignment = SW.HorizontalAlignment.Right,
            Margin = new SW.Thickness(0, 10, 0, 0)
        };
        WC.Button insert = new() { Content = "Inserir", Width = 100, Height = 28, IsDefault = true, Margin = new SW.Thickness(0, 0, 8, 0) };
        WC.Button cancel = new() { Content = "Cancelar", Width = 100, Height = 28, IsCancel = true };
        insert.Click += (_, _) =>
        {
            Selected = _list.SelectedIndex >= 0 ? _templates[_list.SelectedIndex] : null;
            DialogResult = Selected is not null;
        };
        buttons.Children.Add(insert);
        buttons.Children.Add(cancel);
        WC.Grid.SetRow(buttons, 1);
        WC.Grid.SetColumnSpan(buttons, 2);
        root.Children.Add(buttons);

        Content = root;

        foreach (WC.ComboBox combo in new[] { _principal, _secundaria, _anel })
        {
            foreach (RoadSection s in _sections)
                combo.Items.Add($"{s.Id} {s.Name} (pista {N(s.Pista)}" + (s.Canteiro > 0 ? $", canteiro {N(s.Canteiro)}" : "") + ")");
            combo.SelectedIndex = Math.Max(0, _sections.FindIndex(s => s.Id == current.Id));
            combo.SelectionChanged += (_, _) => RenderPreview();
        }

        _list.SelectedIndex = 0;
    }

    private static WC.TextBlock Caption(string text) => new()
    {
        Text = text,
        FontWeight = SW.FontWeights.Bold,
        Margin = new SW.Thickness(0, 8, 0, 4)
    };

    private static WC.Grid Row(string label, SW.UIElement control)
    {
        WC.Grid row = new() { Margin = new SW.Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(150) });
        WC.TextBlock text = new() { Text = label, VerticalAlignment = SW.VerticalAlignment.Center, TextWrapping = SW.TextWrapping.Wrap };
        row.Children.Add(text);
        WC.Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private RoadSection SectionOf(WC.ComboBox combo) =>
        _sections[Math.Clamp(combo.SelectedIndex, 0, _sections.Count - 1)];

    private void OnTemplateChanged()
    {
        if (_list.SelectedIndex < 0)
            return;

        IntersectionTemplate t = _templates[_list.SelectedIndex];
        _building = true;
        _description.Text = t.Name + " - " + t.Description;
        _anel.IsEnabled = t.UsesRing;
        _fields.Children.Clear();

        foreach (TemplateParam p in t.Params)
        {
            WC.TextBox box = new() { Text = N(p.Value), VerticalContentAlignment = SW.VerticalAlignment.Center };
            TemplateParam param = p;
            box.TextChanged += (_, _) =>
            {
                string text = box.Text.Replace(',', '.');
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    param.Value = v;
                    box.Background = WM.Brushes.White;
                    RenderPreview();
                }
                else
                {
                    box.Background = WM.Brushes.MistyRose;
                }
            };
            _fields.Children.Add(Row(p.Label, box));
        }

        _building = false;
        RenderPreview();
    }

    private void RenderPreview()
    {
        if (_building || _list.SelectedIndex < 0)
            return;

        _canvas.Children.Clear();
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        if (w < 20 || h < 20)
            return;

        IntersectionTemplate t = _templates[_list.SelectedIndex];
        TemplateSections sections = Sections;
        TemplateBuild build;
        ViarioResult result;
        List<AxisInput> placed;

        try
        {
            build = t.Build(sections);
            result = ViarioTemplates.Compute(build, sections, new List<AxisInput>(), new EngineSettings(), new V2(0, 0), 0.0, out placed);
        }
        catch (Exception ex)
        {
            _notes.Text = "Erro no modelo: " + ex.Message;
            return;
        }

        _notes.Text = string.Join("\n", build.Notes.Concat(result.Warnings.Distinct().Take(4)));

        List<(List<V2> Points, WM.Brush Brush, double Thickness, bool Dashed)> shapes = new();
        foreach (AxisInput axis in placed)
            shapes.Add((Sample(axis.Path.Segs), WM.Brushes.IndianRed, 1.0, true));
        foreach (OutputLine line in result.Lines)
            shapes.Add((Sample(line.Segs), BrushFor(line.Level), line.Level == EdgeLevel.FaceMF ? 1.6 : 1.0, false));

        List<V2> all = shapes.SelectMany(sh => sh.Points).ToList();
        if (all.Count == 0)
            return;

        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X);
        double minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
        double scale = 0.92 * Math.Min(w / Math.Max(maxX - minX, 1e-6), h / Math.Max(maxY - minY, 1e-6));
        double cx = (minX + maxX) / 2.0, cy = (minY + maxY) / 2.0;

        foreach ((List<V2> pts, WM.Brush brush, double thickness, bool dashed) in shapes)
        {
            WS.Polyline poly = new() { Stroke = brush, StrokeThickness = thickness };
            if (dashed)
                poly.StrokeDashArray = new WM.DoubleCollection { 6, 4 };
            foreach (V2 p in pts)
                poly.Points.Add(new SW.Point((w / 2.0) + ((p.X - cx) * scale), (h / 2.0) - ((p.Y - cy) * scale)));
            _canvas.Children.Add(poly);
        }
    }

    private static WM.Brush BrushFor(EdgeLevel level) => level switch
    {
        EdgeLevel.Pista => WM.Brushes.WhiteSmoke,
        EdgeLevel.FaceMF => WM.Brushes.Cyan,
        EdgeLevel.CostasMF => WM.Brushes.LimeGreen,
        EdgeLevel.Alinhamento => WM.Brushes.Gray,
        EdgeLevel.Canteiro => WM.Brushes.Orange,
        _ => WM.Brushes.Magenta
    };

    private static List<V2> Sample(IEnumerable<Seg> segs)
    {
        List<V2> pts = new();
        foreach (Seg s in segs)
        {
            int n = s.IsArc ? 12 : 1;
            for (int k = pts.Count == 0 ? 0 : 1; k <= n; k++)
                pts.Add(s.PointAt(k / (double)n));
        }

        return pts;
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
