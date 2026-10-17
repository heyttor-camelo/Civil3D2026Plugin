using System;
using System.Collections.Generic;
using System.Linq;

using SW = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using W3 = System.Windows.Media.Media3D;

namespace Civil3D2026Plugin.Modules.Secao;

/// <summary>
/// Vista 3D do conteudo da caixa de corte, dentro da janela do plugin, no espirito do
/// Model Viewer com section box: tudo ja vem recortado exatamente na caixa. Barra propria
/// com vistas prontas (iso, topo, frente = a secao, lado), estilos (sombreado, com arestas,
/// so arestas, raio-x), perspectiva e imagem (copiar/salvar). Objetos podem ser ocultados
/// pela lista da janela. Arrastar orbita, botao direito/meio move, roda aproxima, duplo
/// clique volta a isometrica. As malhas sao montadas uma vez por cena e congeladas.
/// </summary>
internal sealed class SecaoView3D : WC.Border
{
    public enum Style3D
    {
        Sombreado,
        SombreadoArestas,
        Arestas,
        RaioX
    }

    private readonly WC.Grid _root = new();
    private readonly WC.Viewport3D _viewport = new();
    private readonly W3.OrthographicCamera _ortho = new();
    private readonly W3.PerspectiveCamera _persp = new() { FieldOfView = 45 };
    private readonly W3.ModelVisual3D _visual = new();
    private readonly WC.StackPanel _toolbar = new() { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(6), HorizontalAlignment = SW.HorizontalAlignment.Left, VerticalAlignment = SW.VerticalAlignment.Top };
    private Scene3D? _scene;
    private double _exagero = 1;
    private double _size = 10;
    private double _width = 14;
    private double _yaw, _pitch = 35;
    private double _defaultYaw;
    private W3.Point3D _target = new(0, 0, 0);
    private SW.Point _last;
    private bool _orbit, _pan;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Scene3D.Mesh, List<(P3 A, P3 B)>> _featureEdges = new();

    public event Action<string>? Info;

    /// <summary>Pedido de imagem (o painel decide copiar ou salvar).</summary>
    public event Action<bool>? ImageRequested;

    public Style3D ViewStyle { get; private set; } = Style3D.SombreadoArestas;

    public bool Perspective { get; private set; }

    public bool ShowCutPlane { get; private set; } = true;

    /// <summary>Chaves (tipo|nome) dos objetos ocultos.</summary>
    public HashSet<string> Hidden { get; } = new(StringComparer.Ordinal);

    public SecaoView3D()
    {
        Background = new WM.SolidColorBrush(WM.Color.FromRgb(56, 58, 62));
        _viewport.Camera = _ortho;
        _viewport.Children.Add(_visual);
        _ortho.UpDirection = new W3.Vector3D(0, 0, 1);
        _persp.UpDirection = new W3.Vector3D(0, 0, 1);
        _root.Children.Add(_viewport);
        _root.Children.Add(_toolbar);
        Child = _root;
        ClipToBounds = true;
        BuildToolbar();

        MouseDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ResetView();
                return;
            }

            _last = e.GetPosition(this);
            _orbit = e.ChangedButton == WI.MouseButton.Left;
            _pan = e.ChangedButton is WI.MouseButton.Right or WI.MouseButton.Middle;
            CaptureMouse();
        };
        MouseUp += (_, _) =>
        {
            _orbit = _pan = false;
            ReleaseMouseCapture();
        };
        MouseMove += (_, e) =>
        {
            if (!_orbit && !_pan)
                return;
            SW.Point p = e.GetPosition(this);
            double dx = p.X - _last.X, dy = p.Y - _last.Y;
            _last = p;
            if (_orbit)
            {
                _yaw -= dx * 0.4;
                _pitch = Math.Clamp(_pitch + (dy * 0.4), -15, 89.5);
            }
            else
            {
                double k = _width / Math.Max(ActualWidth, 1);
                W3.Vector3D look = Look();
                W3.Vector3D right = W3.Vector3D.CrossProduct(look, new W3.Vector3D(0, 0, 1));
                right.Normalize();
                W3.Vector3D up = W3.Vector3D.CrossProduct(right, look);
                up.Normalize();
                _target -= (right * dx * k) - (up * dy * k);
            }

            UpdateCamera();
        };
        MouseWheel += (_, e) =>
        {
            _width = Math.Clamp(_width * (e.Delta > 0 ? 0.85 : 1 / 0.85), _size * 0.01, _size * 20);
            UpdateCamera();
            e.Handled = true;
        };
    }

    public Scene3D? Scene => _scene;

    // ------------------------------------------------------------------
    // Barra
    // ------------------------------------------------------------------

    private WC.Button ToolButton(string text, string tip, Action click)
    {
        WC.Button b = new()
        {
            Content = text,
            Padding = new SW.Thickness(7, 1, 7, 1),
            Margin = new SW.Thickness(0, 0, 3, 0),
            ToolTip = tip,
            Opacity = 0.92
        };
        b.Click += (_, _) => click();
        return b;
    }

    private void BuildToolbar()
    {
        _toolbar.Children.Clear();
        _toolbar.Children.Add(ToolButton("Iso", "Isometrica (duplo clique na vista faz o mesmo)", ResetView));
        _toolbar.Children.Add(ToolButton("Topo", "Vista de cima, com a frente da caixa para cima", () => SetView(89.5, ViewYaw())));
        _toolbar.Children.Add(ToolButton("Frente", "Olhando como a secao: do lado de tras para a frente", () => SetView(0.5, ViewYaw())));
        _toolbar.Children.Add(ToolButton("Lado", "Olhando ao longo da linha de corte", () => SetView(5, LineYaw())));
        _toolbar.Children.Add(new WC.Border { Width = 8 });
        WC.Button style = ToolButton("Estilo ▾", "Sombreado, com arestas, so arestas ou raio-x", () => { });
        style.Click += (_, _) => OpenStyleMenu(style);
        _toolbar.Children.Add(style);
        WC.Button persp = ToolButton(Perspective ? "Perspectiva" : "Ortogonal", "Alterna camera ortogonal / perspectiva", () => SetPerspective(!Perspective));
        _toolbar.Children.Add(persp);
        WC.Button img = ToolButton("Imagem ▾", "Copia a vista para a area de transferencia ou salva em PNG", () => { });
        img.Click += (_, _) =>
        {
            WC.ContextMenu m = new() { PlacementTarget = img, Placement = WC.Primitives.PlacementMode.Bottom };
            WC.MenuItem copy = new() { Header = "Copiar imagem" };
            copy.Click += (_, _) => ImageRequested?.Invoke(false);
            WC.MenuItem save = new() { Header = "Salvar PNG..." };
            save.Click += (_, _) => ImageRequested?.Invoke(true);
            m.Items.Add(copy);
            m.Items.Add(save);
            m.IsOpen = true;
        };
        _toolbar.Children.Add(img);
    }

    private void OpenStyleMenu(WC.Button anchor)
    {
        WC.ContextMenu m = new() { PlacementTarget = anchor, Placement = WC.Primitives.PlacementMode.Bottom };
        foreach ((Style3D st, string text) in new[] { (Style3D.Sombreado, "Sombreado"), (Style3D.SombreadoArestas, "Sombreado com arestas"), (Style3D.Arestas, "So arestas (wireframe)"), (Style3D.RaioX, "Raio-X (translucido)") })
        {
            WC.MenuItem mi = new() { Header = text, IsCheckable = true, IsChecked = ViewStyle == st };
            Style3D chosen = st;
            mi.Click += (_, _) => SetStyle(chosen);
            m.Items.Add(mi);
        }

        m.Items.Add(new WC.Separator());
        WC.MenuItem plane = new() { Header = "Plano do corte", IsCheckable = true, IsChecked = ShowCutPlane };
        plane.Click += (_, _) => { ShowCutPlane = !ShowCutPlane; Rebuild(); };
        m.Items.Add(plane);
        m.IsOpen = true;
    }

    public void SetStyle(Style3D style)
    {
        ViewStyle = style;
        Rebuild();
    }

    public void SetPerspective(bool on)
    {
        Perspective = on;
        _viewport.Camera = on ? _persp : _ortho;
        BuildToolbar();
        UpdateCamera();
    }

    public void SetHidden(IEnumerable<string> keys)
    {
        Hidden.Clear();
        foreach (string k in keys)
            Hidden.Add(k);
        Rebuild();
    }

    /// <summary>Imagem da vista (2x), com o fundo.</summary>
    public WM.Imaging.BitmapSource? Snapshot()
    {
        if (ActualWidth < 2 || ActualHeight < 2)
            return null;
        _toolbar.Visibility = SW.Visibility.Hidden;
        try
        {
            UpdateLayout();
            WM.Imaging.RenderTargetBitmap rtb = new((int)(ActualWidth * 2), (int)(ActualHeight * 2), 192, 192, WM.PixelFormats.Pbgra32);
            rtb.Render(this);
            rtb.Freeze();
            return rtb;
        }
        finally
        {
            _toolbar.Visibility = SW.Visibility.Visible;
        }
    }

    // ------------------------------------------------------------------
    // Cena
    // ------------------------------------------------------------------

    /// <summary>Troca a cena (nova caixa/novo conteudo). Mantem a camera se a caixa e a mesma.</summary>
    public void SetScene(Scene3D scene, double exagero, bool keepCamera)
    {
        bool reset = !keepCamera || _scene is null;
        _scene = scene;
        _exagero = Math.Max(0.1, exagero);
        Rebuild();
        if (reset)
            ResetView();
        else
            UpdateCamera();
        Info?.Invoke($"3D: {scene.Summary()}   |  arrastar: orbitar   botao direito: mover   roda: zoom   duplo clique: isometrica");
    }

    public void SetExagero(double ex)
    {
        _exagero = Math.Max(0.1, ex);
        if (_scene is not null)
            Rebuild();
    }

    public void Clear()
    {
        _scene = null;
        _visual.Content = null;
    }

    private double _cx, _cy, _cz;

    private W3.Point3D Map(P3 p) => new(p.X - _cx, p.Y - _cy, (p.Z - _cz) * _exagero);

    private void Rebuild()
    {
        Scene3D? scene = _scene;
        if (scene is null || !scene.Box.IsValid)
        {
            _visual.Content = null;
            return;
        }

        CutBox box = scene.Box;
        List<P2> outline = box.Outline();
        _cx = outline.Average(p => p.X);
        _cy = outline.Average(p => p.Y);
        _cz = (scene.ZMin + scene.ZMax) / 2;
        double span = Math.Max(outline.Max(p => p.X) - outline.Min(p => p.X), outline.Max(p => p.Y) - outline.Min(p => p.Y));
        _size = Math.Max(Math.Max(span, (scene.ZMax - scene.ZMin) * _exagero), 1);

        bool faces = ViewStyle != Style3D.Arestas;
        bool edges = ViewStyle != Style3D.Sombreado;
        byte alpha = ViewStyle == Style3D.RaioX ? (byte)70 : (byte)255;

        W3.Model3DGroup group = new();
        group.Children.Add(new W3.AmbientLight(WM.Color.FromRgb(90, 90, 95)));
        group.Children.Add(new W3.DirectionalLight(WM.Color.FromRgb(210, 210, 205), new W3.Vector3D(-0.6, -0.4, -1)));
        group.Children.Add(new W3.DirectionalLight(WM.Color.FromRgb(70, 70, 80), new W3.Vector3D(0.7, 0.5, -0.3)));

        List<W3.GeometryModel3D> translucent = new();
        if (faces)
        {
            foreach (Scene3D.Mesh m in scene.Meshes)
            {
                if (Hidden.Contains(m.Key))
                    continue;
                W3.MeshGeometry3D geo = new();
                foreach (P3 p in m.Pos)
                    geo.Positions.Add(Map(p));
                foreach (int i in m.Tri)
                    geo.TriangleIndices.Add(i);
                geo.Freeze();
                WM.Color c = ColorOf(m.Kind, m.Aci);
                W3.DiffuseMaterial mat = new(new WM.SolidColorBrush(WM.Color.FromArgb(alpha, c.R, c.G, c.B)));
                mat.Freeze();
                W3.GeometryModel3D model = new(geo, mat) { BackMaterial = mat };
                if (alpha < 255)
                    translucent.Add(model);
                else
                    group.Children.Add(model);
            }
        }

        // Tubos e linhas agrupados por cor numa malha so (menos objetos para o WPF).
        double thin = Math.Max(_size * 0.0018, 0.01);
        foreach (IGrouping<WM.Color, Scene3D.Tube> byColor in scene.Tubes.Where(t => !Hidden.Contains(t.Key)).GroupBy(t => ColorOf(t.Kind, t.Aci)))
        {
            W3.MeshGeometry3D geo = new();
            foreach (Scene3D.Tube tube in byColor)
            {
                bool round = tube.Radius > 1e-6 && faces;
                double r = round ? tube.Radius : thin;
                for (int i = 0; i + 1 < tube.Pts.Count; i++)
                    AddPrism(geo, Map(tube.Pts[i]), Map(tube.Pts[i + 1]), r, round ? 12 : 4);
            }

            geo.Freeze();
            W3.DiffuseMaterial mat = new(new WM.SolidColorBrush(byColor.Key));
            mat.Freeze();
            group.Children.Add(new W3.GeometryModel3D(geo, mat) { BackMaterial = mat });
        }

        // Arestas: as do Brep (solidos/blocos) e as vivas das malhas (superficie, corredor).
        if (edges)
        {
            double r = Math.Max(_size * 0.0007, 0.004);
            int budget = 80000;
            Dictionary<WM.Color, W3.MeshGeometry3D> byColor = new();
            W3.MeshGeometry3D GeoFor(WM.Color c)
            {
                if (!byColor.TryGetValue(c, out W3.MeshGeometry3D? g))
                    byColor[c] = g = new W3.MeshGeometry3D();
                return g;
            }

            WM.Color dark = WM.Color.FromRgb(30, 30, 32);
            foreach (Scene3D.Wire w in scene.Wires)
            {
                if (Hidden.Contains(w.Key) || budget <= 0)
                    continue;
                WM.Color c = faces ? dark : ColorOf(w.Kind, w.Aci);
                W3.MeshGeometry3D g = GeoFor(c);
                for (int i = 0; i + 1 < w.Pts.Count && budget-- > 0; i++)
                    AddPrism(g, Map(w.Pts[i]), Map(w.Pts[i + 1]), r, 3);
            }

            foreach (Scene3D.Mesh m in scene.Meshes)
            {
                if (Hidden.Contains(m.Key) || budget <= 0 || m.Kind is ItemKind.Solido or ItemKind.Bloco)
                    continue;
                WM.Color c = faces ? WM.Color.FromArgb(255, 45, 46, 50) : ColorOf(m.Kind, m.Aci);
                W3.MeshGeometry3D g = GeoFor(c);
                foreach ((P3 a, P3 b) in FeatureEdges(m))
                {
                    if (budget-- <= 0)
                        break;
                    AddPrism(g, Map(a), Map(b), r * 0.8, 3);
                }
            }

            foreach ((WM.Color c, W3.MeshGeometry3D g) in byColor)
            {
                g.Freeze();
                W3.DiffuseMaterial mat = new(new WM.SolidColorBrush(c));
                mat.Freeze();
                group.Children.Add(new W3.GeometryModel3D(g, mat) { BackMaterial = mat });
            }
        }

        if (scene.Points.Count > 0 && !Hidden.Contains(Scene3D.PointsKey))
        {
            W3.MeshGeometry3D geo = new();
            double r = Math.Max(_size * 0.004, 0.03);
            foreach ((P3 p, _) in scene.Points)
                AddPrism(geo, Map(new P3(p.X, p.Y, p.Z - r)), Map(new P3(p.X, p.Y, p.Z + r)), r, 4);
            geo.Freeze();
            W3.DiffuseMaterial mat = new(new WM.SolidColorBrush(WM.Color.FromRgb(240, 240, 240)));
            mat.Freeze();
            group.Children.Add(new W3.GeometryModel3D(geo, mat));
        }

        // Contorno da caixa (em cima e embaixo) e arestas verticais.
        {
            W3.MeshGeometry3D geo = new();
            double r = Math.Max(_size * 0.0012, 0.008);
            for (int i = 0; i < outline.Count; i++)
            {
                P2 a = outline[i], b = outline[(i + 1) % outline.Count];
                AddPrism(geo, Map(new P3(a.X, a.Y, scene.ZMin)), Map(new P3(b.X, b.Y, scene.ZMin)), r, 4);
                AddPrism(geo, Map(new P3(a.X, a.Y, scene.ZMax)), Map(new P3(b.X, b.Y, scene.ZMax)), r, 4);
                AddPrism(geo, Map(new P3(a.X, a.Y, scene.ZMin)), Map(new P3(a.X, a.Y, scene.ZMax)), r, 4);
            }

            geo.Freeze();
            W3.DiffuseMaterial mat = new(new WM.SolidColorBrush(WM.Color.FromRgb(120, 120, 125)));
            mat.Freeze();
            group.Children.Add(new W3.GeometryModel3D(geo, mat) { BackMaterial = mat });
        }

        // Transparentes por ultimo (o WPF nao ordena): raio-x e o plano do corte.
        foreach (W3.GeometryModel3D model in translucent)
            group.Children.Add(model);
        if (ShowCutPlane)
        {
            W3.MeshGeometry3D geo = new();
            for (int i = 0; i + 1 < box.Line.Count; i++)
            {
                P2 a = box.Line[i], b = box.Line[i + 1];
                int baseIndex = geo.Positions.Count;
                geo.Positions.Add(Map(new P3(a.X, a.Y, scene.ZMin)));
                geo.Positions.Add(Map(new P3(b.X, b.Y, scene.ZMin)));
                geo.Positions.Add(Map(new P3(b.X, b.Y, scene.ZMax)));
                geo.Positions.Add(Map(new P3(a.X, a.Y, scene.ZMax)));
                foreach (int k in new[] { 0, 1, 2, 0, 2, 3 })
                    geo.TriangleIndices.Add(baseIndex + k);
            }

            geo.Freeze();
            W3.DiffuseMaterial mat = new(new WM.SolidColorBrush(WM.Color.FromArgb(55, 80, 160, 255)));
            mat.Freeze();
            group.Children.Add(new W3.GeometryModel3D(geo, mat) { BackMaterial = mat });
        }

        group.Freeze();
        _visual.Content = group;
        _defaultYaw = ViewYaw() + 30;
    }

    /// <summary>
    /// Arestas vivas de uma malha (vertices soldados): bordas e dobras acima de ~25 graus.
    /// Para superficies e corredor, que nao tem Brep.
    /// </summary>
    private List<(P3 A, P3 B)> FeatureEdges(Scene3D.Mesh m)
    {
        if (_featureEdges.TryGetValue(m, out List<(P3 A, P3 B)>? cached))
            return cached;
        double q = 1e-4;
        Dictionary<(long, long, long), int> ids = new();
        int[] weld = new int[m.Pos.Count];
        List<P3> verts = new();
        for (int i = 0; i < m.Pos.Count; i++)
        {
            P3 p = m.Pos[i];
            (long, long, long) k = ((long)Math.Round(p.X / q), (long)Math.Round(p.Y / q), (long)Math.Round(p.Z / q));
            if (!ids.TryGetValue(k, out int id))
            {
                id = verts.Count;
                ids[k] = id;
                verts.Add(p);
            }

            weld[i] = id;
        }

        Dictionary<(int, int), List<W3.Vector3D>> edges = new();
        for (int t = 0; t + 2 < m.Tri.Count; t += 3)
        {
            int a = weld[m.Tri[t]], b = weld[m.Tri[t + 1]], c = weld[m.Tri[t + 2]];
            if (a == b || b == c || a == c)
                continue;
            P3 pa = verts[a], pb = verts[b], pc = verts[c];
            W3.Vector3D n = W3.Vector3D.CrossProduct(new W3.Vector3D(pb.X - pa.X, pb.Y - pa.Y, pb.Z - pa.Z), new W3.Vector3D(pc.X - pa.X, pc.Y - pa.Y, pc.Z - pa.Z));
            if (n.Length < 1e-14)
                continue;
            n.Normalize();
            foreach ((int u, int v) in new[] { (a, b), (b, c), (c, a) })
            {
                (int, int) key = u < v ? (u, v) : (v, u);
                if (!edges.TryGetValue(key, out List<W3.Vector3D>? list))
                    edges[key] = list = new List<W3.Vector3D>(2);
                list.Add(n);
            }
        }

        List<(P3, P3)> result = new();
        foreach (((int u, int v), List<W3.Vector3D> normals) in edges)
        {
            bool feature = normals.Count == 1 || (normals.Count == 2 && Math.Abs(W3.Vector3D.DotProduct(normals[0], normals[1])) < 0.906);
            if (feature)
                result.Add((verts[u], verts[v]));
        }

        _featureEdges.Add(m, result);
        return result;
    }

    /// <summary>Prisma de n lados entre dois pontos (tubo, linha, aresta).</summary>
    private static void AddPrism(W3.MeshGeometry3D geo, W3.Point3D a, W3.Point3D b, double r, int sides)
    {
        W3.Vector3D axis = b - a;
        if (axis.Length < 1e-9)
            return;
        axis.Normalize();
        W3.Vector3D refv = Math.Abs(axis.Z) < 0.9 ? new W3.Vector3D(0, 0, 1) : new W3.Vector3D(1, 0, 0);
        W3.Vector3D u = W3.Vector3D.CrossProduct(axis, refv);
        u.Normalize();
        W3.Vector3D v = W3.Vector3D.CrossProduct(axis, u);
        int baseIndex = geo.Positions.Count;
        for (int i = 0; i < sides; i++)
        {
            double ang = 2 * Math.PI * i / sides;
            W3.Vector3D off = ((u * Math.Cos(ang)) + (v * Math.Sin(ang))) * r;
            geo.Positions.Add(a + off);
            geo.Positions.Add(b + off);
        }

        for (int i = 0; i < sides; i++)
        {
            int i0 = baseIndex + (2 * i), i1 = baseIndex + (2 * ((i + 1) % sides));
            foreach (int k in new[] { i0, i1, i0 + 1, i1, i1 + 1, i0 + 1 })
                geo.TriangleIndices.Add(k);
        }
    }

    public static WM.Color ColorOf(ItemKind kind, int aci)
    {
        if (aci is >= 1 and <= 6 && kind != ItemKind.Superficie)
        {
            return aci switch
            {
                1 => WM.Color.FromRgb(235, 80, 80),
                2 => WM.Color.FromRgb(230, 220, 60),
                3 => WM.Color.FromRgb(90, 210, 110),
                4 => WM.Color.FromRgb(80, 200, 230),
                5 => WM.Color.FromRgb(110, 150, 255),
                _ => WM.Color.FromRgb(230, 110, 230)
            };
        }

        return kind switch
        {
            ItemKind.Superficie => WM.Color.FromRgb(110, 170, 100),
            ItemKind.Corredor => WM.Color.FromRgb(200, 200, 205),
            ItemKind.Tubo or ItemKind.Estrutura => WM.Color.FromRgb(80, 200, 230),
            ItemKind.FeatureLine => WM.Color.FromRgb(230, 110, 230),
            ItemKind.Polilinha3d => WM.Color.FromRgb(110, 150, 255),
            ItemKind.Solido => WM.Color.FromRgb(215, 205, 150),
            ItemKind.Bloco => WM.Color.FromRgb(190, 170, 220),
            _ => WM.Color.FromRgb(230, 230, 230)
        };
    }

    // ------------------------------------------------------------------
    // Camera
    // ------------------------------------------------------------------

    /// <summary>Direcao (graus) da frente da caixa no primeiro trecho: para onde a secao olha.</summary>
    private double ViewYaw()
    {
        if (_scene is not { Box.Line.Count: >= 2 } s)
            return 0;
        P2 n = (s.Box.Line[1] - s.Box.Line[0]).Unit().Left;
        return Math.Atan2(n.Y, n.X) * 180 / Math.PI;
    }

    private double LineYaw()
    {
        if (_scene is not { Box.Line.Count: >= 2 } s)
            return 0;
        P2 t = (s.Box.Line[1] - s.Box.Line[0]).Unit();
        return Math.Atan2(t.Y, t.X) * 180 / Math.PI;
    }

    private W3.Vector3D Look()
    {
        double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        W3.Vector3D d = new(Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), -Math.Sin(pitch));
        d.Normalize();
        return d;
    }

    private void SetView(double pitch, double yaw)
    {
        _pitch = pitch;
        _yaw = yaw;
        _target = new W3.Point3D(0, 0, 0);
        _width = _size * 1.25;
        UpdateCamera();
    }

    private void ResetView()
    {
        _yaw = _defaultYaw;
        _pitch = 35;
        _target = new W3.Point3D(0, 0, 0);
        _width = _size * 1.4;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        W3.Vector3D look = Look();
        if (Perspective)
        {
            double dist = _width / (2 * Math.Tan(_persp.FieldOfView * Math.PI / 360));
            _persp.Position = _target - (look * dist);
            _persp.LookDirection = look * dist;
            _persp.NearPlaneDistance = Math.Max(dist * 0.01, 0.01);
            _persp.FarPlaneDistance = dist + (_size * 20);
        }
        else
        {
            _ortho.Width = _width;
            _ortho.LookDirection = look;
            _ortho.Position = _target - (look * (_size * 4));
            _ortho.NearPlaneDistance = 0.01;
            _ortho.FarPlaneDistance = _size * 20;
        }
    }
}
