using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Civil3D2026Plugin.Modules.Hydro.Calc;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using M3 = System.Windows.Media.Media3D;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WS = System.Windows.Shapes;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Editor da peca em 3D: desenha a estrutura a partir das medidas do PartData (corpo, paredes,
/// fundo, laje, cone, chamine, tampa e os tubos ligados nas cotas reais) e cota cada medida no
/// modelo. Clicar numa cota leva ao campo; focar um campo destaca a cota. Digitar mostra a
/// previa na hora; "Aplicar no desenho" grava pelo mesmo caminho da aba Propriedades (um UNDO).
/// Girar: arrastar. Zoom: roda. Duplo clique: vista inicial.
/// </summary>
internal sealed class HydroPart3DWindow : SW.Window
{
    private static HydroPart3DWindow? _current;

    private StructureInfo _s;
    private NetworkInfo _net;
    private Action<StructureInfo, List<(string Target, string Text)>> _apply;
    private int _batch = 1;
    private List<M3.Point3D>? _real;
    private string _realKey = "";
    private readonly Action<string> _zoom;

    private readonly Dictionary<string, WC.TextBox> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _preview = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Dim> _dims = new();
    private string? _focus;

    private readonly WC.Viewport3D _view = new() { ClipToBounds = true };
    private readonly WC.Canvas _overlay = new() { IsHitTestVisible = true, ClipToBounds = true };
    private readonly M3.ModelVisual3D _model = new();
    private readonly M3.PerspectiveCamera _camera = new() { FieldOfView = 40 };
    private readonly WC.StackPanel _fields = new();
    private readonly WC.TextBlock _status = new() { Margin = new SW.Thickness(6, 2, 6, 2), TextWrapping = SW.TextWrapping.Wrap };
    private double _yaw = -55, _pitch = 22, _dist = 8, _height = 2;
    private SW.Point? _drag;

    private static readonly WM.Brush Back = Frozen(new WM.SolidColorBrush(WM.Color.FromRgb(46, 48, 52)));
    private static readonly WM.Brush DimBrush = Frozen(new WM.SolidColorBrush(WM.Color.FromRgb(240, 240, 240)));
    private static readonly WM.Brush FocusBrush = Frozen(new WM.SolidColorBrush(WM.Color.FromRgb(255, 176, 0)));
    private static readonly WM.Brush LabelBack = Frozen(new WM.SolidColorBrush(WM.Color.FromArgb(200, 30, 30, 34)));
    private static readonly WM.Brush PipeLabel = Frozen(new WM.SolidColorBrush(WM.Color.FromRgb(120, 220, 235)));

    /// <summary>Uma cota no modelo: medida (chave do campo), pontas em coordenadas locais (m) e texto.</summary>
    private sealed record Dim(string Key, M3.Point3D A, M3.Point3D B, string Text, bool Editable);

    private static WM.Brush Frozen(WM.Brush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Abre (ou reaproveita) a janela. batch &gt; 1: edicao em lote, a peca mostrada e a primeira
    /// marcada e "Aplicar" grava em todas (o apply recebido ja aponta para o lote).
    /// </summary>
    public static void Open(StructureInfo s, NetworkInfo net, Action<StructureInfo, List<(string Target, string Text)>> apply, Action<string> zoom, int batch = 1)
    {
        if (_current is not null)
        {
            _current._apply = apply;
            _current._batch = batch;
            _current.Load(s, net);
            _current.Activate();
            return;
        }

        _current = new HydroPart3DWindow(s, net, apply, zoom, batch);
        AcApp.ShowModelessWindow(_current);
    }

    /// <summary>O painel releu o desenho: atualiza a peca aberta (mesmo handle) com os valores gravados.</summary>
    public static void Refreshed(Func<string, (StructureInfo? S, NetworkInfo? Net)> find)
    {
        HydroPart3DWindow? w = _current;
        if (w is null)
            return;
        (StructureInfo? s, NetworkInfo? net) = find(w._s.Handle);
        if (s is not null && net is not null)
            w.Load(s, net);
    }

    private HydroPart3DWindow(StructureInfo s, NetworkInfo net, Action<StructureInfo, List<(string Target, string Text)>> apply, Action<string> zoom, int batch)
    {
        _s = s;
        _net = net;
        _apply = apply;
        _batch = batch;
        _zoom = zoom;
        Title = "HYDRO - Peca 3D";
        Width = 1000;
        Height = 660;
        Background = Back;
        Foreground = WM.Brushes.Gainsboro;
        Closed += (_, _) => _current = null;
        Content = BuildUi();
        Load(s, net);
    }

    // ------------------------------------------------------------------
    // Interface
    // ------------------------------------------------------------------

    private SW.UIElement BuildUi()
    {
        _camera.UpDirection = new M3.Vector3D(0, 0, 1);
        _view.Camera = _camera;
        _view.Children.Add(_model);

        WC.Grid stage = new() { Background = Back };
        stage.Children.Add(_view);
        stage.Children.Add(_overlay);
        stage.Children.Add(new WC.TextBlock
        {
            Text = "arrastar: girar   roda: zoom   duplo clique: vista inicial   clique na cota: editar",
            Foreground = WM.Brushes.Gray,
            Margin = new SW.Thickness(8, 6, 8, 6),
            VerticalAlignment = SW.VerticalAlignment.Bottom,
            IsHitTestVisible = false
        });
        stage.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ResetView();
                return;
            }

            _drag = e.GetPosition(stage);
            stage.CaptureMouse();
        };
        stage.MouseLeftButtonUp += (_, _) =>
        {
            _drag = null;
            stage.ReleaseMouseCapture();
        };
        stage.MouseMove += (_, e) =>
        {
            if (_drag is not SW.Point from)
                return;
            SW.Point p = e.GetPosition(stage);
            _yaw -= (p.X - from.X) * 0.4;
            _pitch = Math.Clamp(_pitch + ((p.Y - from.Y) * 0.3), -15, 88);
            _drag = p;
            PlaceCamera();
        };
        stage.MouseWheel += (_, e) =>
        {
            _dist = Math.Clamp(_dist * (e.Delta > 0 ? 0.88 : 1.14), 0.5, 200);
            PlaceCamera();
        };
        stage.SizeChanged += (_, _) => QueueOverlay();

        WC.DockPanel right = new() { Width = 360, Background = Frozen(new WM.SolidColorBrush(WM.Color.FromRgb(37, 37, 38))) };
        WC.WrapPanel buttons = new() { Margin = new SW.Thickness(6) };
        buttons.Children.Add(Button("Aplicar no desenho", ApplyNow, true));
        buttons.Children.Add(Button("Desfazer previa", () => Load(_s, _net)));
        buttons.Children.Add(Button("Zoom no desenho", () => _zoom(_s.Handle)));
        WC.DockPanel.SetDock(buttons, WC.Dock.Bottom);
        right.Children.Add(buttons);
        WC.DockPanel.SetDock(_status, WC.Dock.Bottom);
        right.Children.Add(_status);
        right.Children.Add(new WC.ScrollViewer { Content = _fields, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto });

        WC.DockPanel root = new();
        WC.DockPanel.SetDock(right, WC.Dock.Right);
        root.Children.Add(right);
        root.Children.Add(stage);
        return root;
    }

    private static WC.Button Button(string text, Action action, bool primary = false)
    {
        WC.Button b = new()
        {
            Content = text,
            Margin = new SW.Thickness(0, 0, 6, 4),
            Padding = new SW.Thickness(10, 3, 10, 3),
            FontWeight = primary ? SW.FontWeights.Bold : SW.FontWeights.Normal
        };
        b.Click += (_, _) => action();
        return b;
    }

    private void Load(StructureInfo s, NetworkInfo net)
    {
        bool same = _s.Handle == s.Handle && _boxes.Count > 0;
        _s = s;
        _net = net;
        _preview.Clear();
        Title = _batch > 1
            ? $"HYDRO - Peca 3D (lote: {_batch} estruturas, mostrando {s.Name})"
            : $"HYDRO - Peca 3D: {s.Name} ({s.Family} / {s.Size})";
        LoadReal();
        BuildFields();
        Rebuild();
        if (!same)
            ResetView();
        _status.Text = (_real is null ? "Modelo simplificado (a peca do desenho nao tem solido legivel). " : "Peca real do desenho. ")
            + (_batch > 1 ? $"Digite as medidas (m): a previa aparece na hora. Aplicar grava nas {_batch} marcadas." : "Digite as medidas (m): a previa aparece na hora. Aplicar grava no desenho.");
    }

    /// <summary>
    /// Le o solido real da peca (geometria do Part Builder) quando a peca ou suas medidas mudaram;
    /// fica null se nao der (aí vale o modelo simplificado pelas medidas).
    /// </summary>
    private void LoadReal()
    {
        string key = _s.Handle + "|" + string.Join(";", _s.Parameters.Select(p => p.Name + "=" + p.Value.ToString("R", CultureInfo.InvariantCulture))) + "|" + _s.Rim.ToString("R", CultureInfo.InvariantCulture) + "|" + _s.Sump.ToString("R", CultureInfo.InvariantCulture);
        if (key == _realKey)
            return;
        _realKey = key;
        _real = null;
        try
        {
            if (AcApp.DocumentManager.MdiActiveDocument?.Database is Autodesk.AutoCAD.DatabaseServices.Database db
                && HydroPartMesh.Read(db, _s.Handle) is List<M3.Point3D> tris && tris.Count >= 3)
            {
                double zMin = tris.Min(p => p.Z), zMax = tris.Max(p => p.Z);
                if (zMax - zMin > 0.05)
                    _real = tris;
            }
        }
        catch
        {
            _real = null;
        }
    }

    private void BuildFields()
    {
        _fields.Children.Clear();
        _boxes.Clear();
        Header(_batch > 1 ? $"Lote: {_batch} estruturas (valores de {_s.Name}; vale o que voce digitar)" : $"{_s.Name}  -  {_s.Family} / {_s.Size}");
        Field(HydroRules.Rim, "Tampa (rim) [m]", Rule(HydroRules.Rim) ?? N(_s.Rim, 3));
        Field(HydroRules.Sump, "Fundo (sump) [m]", Rule(HydroRules.Sump) ?? N(_s.Sump, 3));
        Field("prof", "Profundidade (tampa - fundo) [m]", N(_s.Rim - _s.Sump, 3));

        List<PartParam> editable = _s.Parameters.Where(p => p.Editable).ToList();
        if (editable.Count > 0)
        {
            Header($"Medidas da peca ({editable.Count})");
            foreach (PartParam p in editable.OrderBy(p => p.Fixed ? 1 : 0))
            {
                string range = p.Fixed ? "fixo"
                    : p.IsRange && !double.IsNaN(p.Min) ? $"{N(p.Min * p.Factor, 2)}..{N(p.Max * p.Factor, 2)}"
                    : p.List.Count > 0 ? string.Join("/", p.List.Take(6).Select(x => N(x * p.Factor, 2))) : "";
                Field(p.Name, $"{p.Description} [{p.Name}] {range}", Rule(p.Name) ?? N(p.Meters, 3));
            }
        }

        List<PartParam> locked = _s.Parameters.Where(p => !p.Editable).ToList();
        if (locked.Count > 0)
        {
            WC.Expander exp = new() { Header = $"Medidas internas do modelo ({locked.Count}, so leitura)", Foreground = WM.Brushes.Gray, Margin = new SW.Thickness(4, 6, 4, 2) };
            WC.StackPanel inner = new();
            foreach (PartParam p in locked)
                inner.Children.Add(new WC.TextBlock { Text = $"{p.Name} = {N(p.Value, 3)} {p.Units}  ({p.Context})", Foreground = WM.Brushes.Gray, FontFamily = new WM.FontFamily("Consolas"), Margin = new SW.Thickness(6, 1, 2, 1) });
            exp.Content = inner;
            _fields.Children.Add(exp);
        }

        // Tubos ligados: so informacao (cota do fundo do tubo em relacao ao fundo da estrutura).
        List<(PipeInfo P, bool AtStart)> pipes = Pipes().ToList();
        if (pipes.Count > 0)
        {
            Header($"Tubos ligados ({pipes.Count})");
            foreach ((PipeInfo p, bool atStart) in pipes)
            {
                double inv = atStart ? p.StartInvert : p.EndInvert;
                _fields.Children.Add(new WC.TextBlock
                {
                    Text = $"{p.Name}: D {Math.Round(p.InnerDiameter * 1000)} mm, fundo {N(inv, 3)} ({N(inv - _s.Sump, 2)} m acima do fundo)",
                    Foreground = PipeLabel,
                    Margin = new SW.Thickness(6, 1, 4, 1),
                    TextWrapping = SW.TextWrapping.Wrap
                });
            }
        }
    }

    private string? Rule(string target) => _s.Rules.TryGetValue(target, out string? r) ? "=" + r : null;

    private void Header(string text) => _fields.Children.Add(new WC.TextBlock
    {
        Text = text,
        FontWeight = SW.FontWeights.Bold,
        Foreground = WM.Brushes.White,
        Background = Frozen(new WM.SolidColorBrush(WM.Color.FromRgb(58, 58, 62))),
        Padding = new SW.Thickness(4, 2, 4, 2),
        Margin = new SW.Thickness(0, 4, 0, 2),
        TextWrapping = SW.TextWrapping.Wrap
    });

    private void Field(string key, string label, string value)
    {
        WC.Grid row = new() { Margin = new SW.Thickness(4, 1, 4, 1) };
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(110) });
        row.Children.Add(new WC.TextBlock { Text = label, Foreground = WM.Brushes.LightGray, TextWrapping = SW.TextWrapping.Wrap, VerticalAlignment = SW.VerticalAlignment.Center });
        WC.TextBox box = new()
        {
            Text = value,
            Tag = value,
            Background = Frozen(new WM.SolidColorBrush(WM.Color.FromRgb(30, 30, 30))),
            Foreground = value.StartsWith('=') ? PipeLabel : WM.Brushes.White,
            CaretBrush = WM.Brushes.White,
            BorderThickness = new SW.Thickness(1),
            BorderBrush = Frozen(new WM.SolidColorBrush(WM.Color.FromRgb(110, 110, 116))),
            Padding = new SW.Thickness(2, 1, 2, 1),
            FontFamily = new WM.FontFamily("Consolas"),
            TextAlignment = SW.TextAlignment.Right,
            ToolTip = "Valor em m, +0,10 / -0,10 (relativo) ou =regra. Enter aplica no desenho."
        };
        box.GotKeyboardFocus += (_, _) =>
        {
            _focus = key;
            box.SelectAll();
            QueueOverlay();
        };
        box.TextChanged += (_, _) => OnFieldChanged(key, box);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == WI.Key.Enter)
            {
                ApplyNow();
                e.Handled = true;
            }
        };
        WC.Grid.SetColumn(box, 1);
        row.Children.Add(box);
        _fields.Children.Add(row);
        _boxes[key] = box;
    }

    private void OnFieldChanged(string key, WC.TextBox box)
    {
        string t = box.Text.Trim();
        double current = Current(key);
        _preview.Remove(key);
        if (t.Length > 0 && t != (box.Tag as string ?? "") && !t.StartsWith('='))
        {
            try
            {
                if (HydroRuleEngine.EditValue(t, current) is double v)
                    _preview[key] = v;
                box.Foreground = WM.Brushes.White;
            }
            catch (HydroExprException)
            {
                box.Foreground = WM.Brushes.OrangeRed;
            }
        }

        Rebuild();
    }

    /// <summary>Valor gravado (m) da medida, antes da previa.</summary>
    private double Current(string key)
    {
        if (key == HydroRules.Rim)
            return _s.Rim;
        if (key == HydroRules.Sump)
            return _s.Sump;
        if (key == "prof")
            return _s.Rim - _s.Sump;
        return _s.Parameters.FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase))?.Meters ?? double.NaN;
    }

    private void ApplyNow()
    {
        List<(string Target, string Text)> edits = new();
        foreach ((string key, WC.TextBox box) in _boxes)
        {
            string t = box.Text.Trim();
            if (t.Length == 0 || t == (box.Tag as string ?? ""))
                continue;
            edits.Add((key, t));
        }

        if (edits.Count == 0)
        {
            _status.Text = "Nada mudou.";
            return;
        }

        _status.Text = $"Gravando {edits.Count} medida(s)...";
        _apply(_s, edits);
    }

    // ------------------------------------------------------------------
    // Medidas -> geometria
    // ------------------------------------------------------------------

    /// <summary>Medida pelo contexto do catalogo (StructInnerWidth...), com a previa por cima; NaN se a peca nao tem.</summary>
    private (double Value, string Key) ByContext(params string[] contexts)
    {
        foreach (string ctx in contexts)
        {
            PartParam? p = _s.Parameters.FirstOrDefault(x => x.Context.Equals(ctx, StringComparison.OrdinalIgnoreCase) && !x.Internal);
            if (p is null)
                continue;
            return (_preview.TryGetValue(p.Name, out double v) ? v : p.Meters, p.Name);
        }

        return (double.NaN, "");
    }

    private double Pv(string key, double recorded) => _preview.TryGetValue(key, out double v) ? v : recorded;

    private void Rebuild()
    {
        _dims.Clear();
        M3.Model3DGroup opaque = new(), glass = new();

        (double diam, string diamKey) = ByContext("StructInnerDiameter", "StructDiameter");
        (double width, string widthKey) = ByContext("StructInnerWidth", "StructWidth");
        (double length, string lengthKey) = ByContext("StructInnerLength", "StructLength");
        bool round = !double.IsNaN(diam) && double.IsNaN(width);
        double w = round ? diam : !double.IsNaN(width) ? width : _s.InnerDiameter > 0 ? _s.InnerDiameter : 1.0;
        double l = round ? w : !double.IsNaN(length) ? length : w;
        string wKey = round ? diamKey : widthKey;
        (double wall, string wallKey) = ByContext("WallThickness", "StructWallThickness");
        (double floor, string floorKey) = ByContext("FloorThickness", "StructFloorThickness");
        (double cone, string coneKey) = ByContext("StructConeHeight");
        (double frameD, string frameDKey) = ByContext("StructFrameDiameter", "StructFrameWidth");
        (double frameH, string frameHKey) = ByContext("StructFrameHeight");
        (double slab, string slabKey) = ByContext("StructSlabThickness");
        wall = double.IsNaN(wall) ? 0.1 : wall;
        floor = double.IsNaN(floor) ? 0.1 : floor;
        cone = double.IsNaN(cone) ? 0 : cone;
        frameH = double.IsNaN(frameH) ? 0 : frameH;
        slab = double.IsNaN(slab) ? 0 : slab;
        if (double.IsNaN(frameD) || frameD <= 0)
            frameD = Math.Min(0.6, Math.Min(w, l));

        double rim = Pv(HydroRules.Rim, _s.Rim), sump = Pv(HydroRules.Sump, _s.Sump);
        if (_preview.TryGetValue("prof", out double prof))
            sump = rim - prof;
        double h = Math.Max(0.2, rim - sump);
        double body = Math.Max(0.15, h - cone - frameH - slab);
        _height = h;

        WM.Color concrete = WM.Color.FromRgb(150, 150, 150), inside = WM.Color.FromRgb(200, 196, 186), cap = WM.Color.FromRgb(70, 70, 75);
        double ox = (w / 2) + wall, oy = (l / 2) + wall;

        // Peca real do desenho quando nao ha previa pendente; com previa, o modelo pelas medidas.
        bool real = _real is not null && _preview.Count == 0;
        if (real)
        {
            M3.MeshGeometry3D rm = new();
            foreach (M3.Point3D p in _real!)
                rm.Positions.Add(p);
            glass.Children.Add(Geo(rm, WM.Color.FromRgb(165, 165, 160), 0.62));
        }

        // Com a peca real na tela, o modelo pelas medidas so serve para as cotas (nao e desenhado).
        M3.Model3DGroup shapeOpaque = real ? new() : opaque, shapeGlass = real ? new() : glass;

        // Fundo (laje de fundo), sempre opaco.
        shapeOpaque.Children.Add(Geo(round ? Frustum(ox, ox, -floor, 0) : Box(-ox, -oy, -floor, ox, oy, 0), concrete, 1));

        // Corpo: casca interna opaca (sem tampa) e externa translucida.
        shapeOpaque.Children.Add(Geo(round ? Frustum(w / 2, w / 2, 0, body, caps: false) : Box(-w / 2, -l / 2, 0, w / 2, l / 2, body, top: false, bottom: false), inside, 0.55));
        shapeGlass.Children.Add(Geo(round ? Frustum(ox, ox, 0, body, caps: false) : Box(-ox, -oy, 0, ox, oy, body, top: false, bottom: false), concrete, 0.28));

        double z = body;
        if (slab > 0)
        {
            shapeGlass.Children.Add(Geo(round ? Frustum(ox, ox, z, z + slab) : Box(-ox, -oy, z, ox, oy, z + slab), concrete, 0.45));
            z += slab;
        }

        double fr = (frameD / 2) + Math.Min(wall, 0.1);
        if (cone > 0)
        {
            double r0 = round ? ox : Math.Min(ox, oy);
            shapeGlass.Children.Add(Geo(Frustum(r0, fr, z, z + cone, caps: false), concrete, 0.35));
            z += cone;
        }

        if (frameH > 0)
        {
            shapeGlass.Children.Add(Geo(Frustum(fr, fr, z, z + frameH, caps: false), concrete, 0.4));
            z += frameH;
        }

        // Tampa no topo (cota da tampa).
        shapeOpaque.Children.Add(Geo(Frustum(frameD / 2, frameD / 2, h, h + 0.04), cap, 1));

        // Tubos ligados na cota real (eixo = fundo + altura/2), na direcao da outra ponta.
        foreach ((PipeInfo p, bool atStart) in Pipes())
        {
            P2 other = atStart ? p.End : p.Start;
            double ang = Math.Atan2(other.Y - _s.Point.Y, other.X - _s.Point.X) - _s.Rotation;
            double inv = (atStart ? p.StartInvert : p.EndInvert) - _s.Sump;
            double r = Math.Max(p.OuterDiameter, p.InnerDiameter) / 2;
            if (r <= 0)
                r = 0.15;
            double zc = inv + (p.InnerHeight > 0 ? p.InnerHeight / 2 : r);
            double from = (round ? w / 2 : Math.Min(w, l) / 2) * 0.8;
            double to = Math.Max(ox, oy) + 1.2;
            bool incoming = p.ToHandle == _s.Handle;
            WM.Color color = incoming ? WM.Color.FromRgb(70, 160, 230) : WM.Color.FromRgb(90, 200, 120);
            M3.GeometryModel3D tube = Geo(Frustum(r, r, from, to, 20), color, 1);
            M3.Transform3DGroup tf = new();
            tf.Children.Add(new M3.RotateTransform3D(new M3.AxisAngleRotation3D(new M3.Vector3D(0, 1, 0), 90)));
            tf.Children.Add(new M3.TranslateTransform3D(0, 0, zc));
            tf.Children.Add(new M3.RotateTransform3D(new M3.AxisAngleRotation3D(new M3.Vector3D(0, 0, 1), ang * 180 / Math.PI)));
            tube.Transform = tf;
            opaque.Children.Add(tube);
            M3.Point3D tip = new(Math.Cos(ang) * to, Math.Sin(ang) * to, zc + r + 0.1);
            _dims.Add(new Dim("", tip, tip, $"{p.Name} D{Math.Round(p.InnerDiameter * 1000)}  fundo +{N(inv, 2)}", false));
        }

        // Cotas: cada medida que a peca tem, no lugar dela.
        double gap = 0.35;
        if (wKey.Length > 0)
            _dims.Add(new Dim(wKey, new(-w / 2, -oy - gap, body), new(w / 2, -oy - gap, body), $"{wKey} {N(w, 2)}", true));
        if (!round && lengthKey.Length > 0)
            _dims.Add(new Dim(lengthKey, new(ox + gap, -l / 2, body), new(ox + gap, l / 2, body), $"{lengthKey} {N(l, 2)}", true));
        if (wallKey.Length > 0)
            _dims.Add(new Dim(wallKey, new(w / 2, 0, body + 0.05), new(ox, 0, body + 0.05), $"{wallKey} {N(wall, 2)}", true));
        if (floorKey.Length > 0)
            _dims.Add(new Dim(floorKey, new(-ox - gap, -oy, -floor), new(-ox - gap, -oy, 0), $"{floorKey} {N(floor, 2)}", true));
        _dims.Add(new Dim("prof", new(-ox - (2 * gap), -oy, 0), new(-ox - (2 * gap), -oy, h), $"prof {N(h, 2)}  (tampa {N(rim, 2)} / fundo {N(sump, 2)})", true));
        if (slabKey.Length > 0 && slab > 0)
            _dims.Add(new Dim(slabKey, new(ox + gap, oy, body), new(ox + gap, oy, body + slab), $"{slabKey} {N(slab, 2)}", true));
        if (coneKey.Length > 0 && cone > 0)
            _dims.Add(new Dim(coneKey, new(fr + gap, 0, body + slab), new(fr + gap, 0, body + slab + cone), $"{coneKey} {N(cone, 2)}", true));
        if (frameHKey.Length > 0 && frameH > 0)
            _dims.Add(new Dim(frameHKey, new(-fr - gap, 0, h - frameH), new(-fr - gap, 0, h), $"{frameHKey} {N(frameH, 2)}", true));
        if (frameDKey.Length > 0)
            _dims.Add(new Dim(frameDKey, new(-frameD / 2, 0, h + 0.25), new(frameD / 2, 0, h + 0.25), $"{frameDKey} {N(frameD, 2)}", true));

        M3.Model3DGroup all = new();
        all.Children.Add(new M3.AmbientLight(WM.Color.FromRgb(95, 95, 95)));
        all.Children.Add(new M3.DirectionalLight(WM.Color.FromRgb(200, 200, 200), new M3.Vector3D(-0.6, -0.8, -1)));
        all.Children.Add(new M3.DirectionalLight(WM.Color.FromRgb(90, 90, 100), new M3.Vector3D(0.7, 0.5, -0.3)));
        all.Children.Add(opaque);
        all.Children.Add(glass);
        _model.Content = all;
        PlaceCamera();
    }

    private IEnumerable<(PipeInfo P, bool AtStart)> Pipes()
    {
        foreach (string h in _s.PipesIn.Concat(_s.PipesOut).Concat(_s.PipesUndefined).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_net.PipeByHandle.TryGetValue(h, out PipeInfo? p))
                yield return (p, p.StartHandle.Equals(_s.Handle, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ------------------------------------------------------------------
    // Camera e cotas (sobreposicao 2D)
    // ------------------------------------------------------------------

    private void ResetView()
    {
        double size = 3;
        foreach (Dim d in _dims.Where(d => d.Editable))
            size = Math.Max(size, Math.Max(Math.Abs(d.A.X), Math.Abs(d.A.Y)) * 2);
        _dist = Math.Max(size, _height) * 2.3;
        _yaw = -55;
        _pitch = 22;
        PlaceCamera();
    }

    private void PlaceCamera()
    {
        double y = _yaw * Math.PI / 180, p = _pitch * Math.PI / 180;
        M3.Point3D target = new(0, 0, _height / 2);
        M3.Vector3D dir = new(Math.Cos(p) * Math.Cos(y), Math.Cos(p) * Math.Sin(y), Math.Sin(p));
        _camera.Position = target + (dir * _dist);
        _camera.LookDirection = -dir * _dist;
        QueueOverlay();
    }

    private bool _overlayQueued;

    private void QueueOverlay()
    {
        if (_overlayQueued)
            return;
        _overlayQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _overlayQueued = false;
            DrawOverlay();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void DrawOverlay()
    {
        _overlay.Children.Clear();
        M3.GeneralTransform3DTo2D? tf;
        try
        {
            tf = _model.TransformToAncestor(_view);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (tf is null)
            return;
        foreach (Dim d in _dims)
        {
            if (!tf.TryTransform(d.A, out SW.Point a) || !tf.TryTransform(d.B, out SW.Point b))
                continue;
            bool focus = d.Key.Length > 0 && d.Key.Equals(_focus, StringComparison.OrdinalIgnoreCase);
            WM.Brush brush = !d.Editable ? PipeLabel : focus ? FocusBrush : DimBrush;
            if (d.A != d.B)
            {
                _overlay.Children.Add(new WS.Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = brush, StrokeThickness = focus ? 2.5 : 1.2, IsHitTestVisible = false });
                foreach (SW.Point end in new[] { a, b })
                {
                    WS.Ellipse dot = new() { Width = 5, Height = 5, Fill = brush, IsHitTestVisible = false };
                    WC.Canvas.SetLeft(dot, end.X - 2.5);
                    WC.Canvas.SetTop(dot, end.Y - 2.5);
                    _overlay.Children.Add(dot);
                }
            }

            WC.Border label = new()
            {
                Background = LabelBack,
                BorderBrush = brush,
                BorderThickness = new SW.Thickness(focus ? 2 : 1),
                CornerRadius = new SW.CornerRadius(3),
                Padding = new SW.Thickness(4, 1, 4, 1),
                Child = new WC.TextBlock { Text = d.Text, Foreground = brush, FontFamily = new WM.FontFamily("Consolas"), FontSize = 11 },
                ToolTip = d.Editable ? "Clique para editar esta medida" : null
            };
            if (d.Editable)
                label.Cursor = WI.Cursors.Hand;
            if (d.Editable && _boxes.TryGetValue(d.Key, out WC.TextBox? box))
            {
                label.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    box.Focus();
                    box.BringIntoView();
                };
            }

            WC.Canvas.SetLeft(label, ((a.X + b.X) / 2) + 4);
            WC.Canvas.SetTop(label, ((a.Y + b.Y) / 2) - 9);
            _overlay.Children.Add(label);
        }
    }

    // ------------------------------------------------------------------
    // Malhas
    // ------------------------------------------------------------------

    private static M3.GeometryModel3D Geo(M3.MeshGeometry3D mesh, WM.Color color, double opacity)
    {
        WM.SolidColorBrush brush = new(color) { Opacity = opacity };
        brush.Freeze();
        M3.DiffuseMaterial mat = new(brush);
        mat.Freeze();
        mesh.Freeze();
        return new M3.GeometryModel3D(mesh, mat) { BackMaterial = mat };
    }

    private static void Quad(M3.MeshGeometry3D m, M3.Point3D a, M3.Point3D b, M3.Point3D c, M3.Point3D d)
    {
        int i = m.Positions.Count;
        m.Positions.Add(a);
        m.Positions.Add(b);
        m.Positions.Add(c);
        m.Positions.Add(d);
        m.TriangleIndices.Add(i);
        m.TriangleIndices.Add(i + 1);
        m.TriangleIndices.Add(i + 2);
        m.TriangleIndices.Add(i);
        m.TriangleIndices.Add(i + 2);
        m.TriangleIndices.Add(i + 3);
    }

    private static M3.MeshGeometry3D Box(double x0, double y0, double z0, double x1, double y1, double z1, bool top = true, bool bottom = true)
    {
        M3.MeshGeometry3D m = new();
        M3.Point3D p000 = new(x0, y0, z0), p100 = new(x1, y0, z0), p110 = new(x1, y1, z0), p010 = new(x0, y1, z0);
        M3.Point3D p001 = new(x0, y0, z1), p101 = new(x1, y0, z1), p111 = new(x1, y1, z1), p011 = new(x0, y1, z1);
        Quad(m, p000, p100, p101, p001);
        Quad(m, p100, p110, p111, p101);
        Quad(m, p110, p010, p011, p111);
        Quad(m, p010, p000, p001, p011);
        if (top)
            Quad(m, p001, p101, p111, p011);
        if (bottom)
            Quad(m, p010, p110, p100, p000);
        return m;
    }

    /// <summary>Tronco de cone (ou cilindro com r0 = r1) no eixo Z, de z0 a z1.</summary>
    private static M3.MeshGeometry3D Frustum(double r0, double r1, double z0, double z1, int n = 40, bool caps = true)
    {
        M3.MeshGeometry3D m = new();
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n, b = 2 * Math.PI * (i + 1) / n;
            M3.Point3D a0 = new(r0 * Math.Cos(a), r0 * Math.Sin(a), z0), b0 = new(r0 * Math.Cos(b), r0 * Math.Sin(b), z0);
            M3.Point3D a1 = new(r1 * Math.Cos(a), r1 * Math.Sin(a), z1), b1 = new(r1 * Math.Cos(b), r1 * Math.Sin(b), z1);
            Quad(m, a0, b0, b1, a1);
            if (!caps)
                continue;
            int c = m.Positions.Count;
            m.Positions.Add(new M3.Point3D(0, 0, z0));
            m.Positions.Add(b0);
            m.Positions.Add(a0);
            m.Positions.Add(new M3.Point3D(0, 0, z1));
            m.Positions.Add(a1);
            m.Positions.Add(b1);
            m.TriangleIndices.Add(c);
            m.TriangleIndices.Add(c + 1);
            m.TriangleIndices.Add(c + 2);
            m.TriangleIndices.Add(c + 3);
            m.TriangleIndices.Add(c + 4);
            m.TriangleIndices.Add(c + 5);
        }

        return m;
    }

    private static string N(double v, int d) => double.IsNaN(v) ? "-" : v.ToString("F" + d, CultureInfo.InvariantCulture);
}
