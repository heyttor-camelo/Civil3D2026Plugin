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
/// Vista 3D (isometrica, camera ortografica) do conteudo da caixa de corte, dentro da
/// janela do plugin: superficies, corredor, tubos, linhas e pontos, com o plano do corte
/// translucido e o contorno da caixa. Arrastar orbita, botao direito move, roda aproxima,
/// duplo clique volta a isometrica. As malhas sao montadas uma vez por cena e congeladas.
/// </summary>
internal sealed class SecaoView3D : WC.Border
{
    private readonly WC.Viewport3D _viewport = new();
    private readonly W3.OrthographicCamera _camera = new();
    private readonly W3.ModelVisual3D _visual = new();
    private Scene3D? _scene;
    private double _exagero = 1;
    private double _size = 10;
    private double _yaw, _pitch = 35;
    private double _defaultYaw;
    private W3.Point3D _target = new(0, 0, 0);
    private SW.Point _last;
    private bool _orbit, _pan;

    public event Action<string>? Info;

    public SecaoView3D()
    {
        Background = new WM.SolidColorBrush(WM.Color.FromRgb(56, 58, 62));
        _viewport.Camera = _camera;
        _viewport.Children.Add(_visual);
        _camera.UpDirection = new W3.Vector3D(0, 0, 1);
        Child = _viewport;
        ClipToBounds = true;

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
                _pitch = Math.Clamp(_pitch + (dy * 0.4), 2, 89);
            }
            else
            {
                double k = _camera.Width / Math.Max(ActualWidth, 1);
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
            _camera.Width = Math.Clamp(_camera.Width * (e.Delta > 0 ? 0.85 : 1 / 0.85), _size * 0.02, _size * 20);
            e.Handled = true;
        };
    }

    public Scene3D? Scene => _scene;

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

    // ------------------------------------------------------------------
    // Montagem
    // ------------------------------------------------------------------

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

        W3.Model3DGroup group = new();
        group.Children.Add(new W3.AmbientLight(WM.Color.FromRgb(90, 90, 95)));
        group.Children.Add(new W3.DirectionalLight(WM.Color.FromRgb(210, 210, 205), new W3.Vector3D(-0.6, -0.4, -1)));
        group.Children.Add(new W3.DirectionalLight(WM.Color.FromRgb(70, 70, 80), new W3.Vector3D(0.7, 0.5, -0.3)));

        foreach (Scene3D.Mesh m in scene.Meshes)
        {
            W3.MeshGeometry3D geo = new();
            foreach (P3 p in m.Pos)
                geo.Positions.Add(Map(p));
            foreach (int i in m.Tri)
                geo.TriangleIndices.Add(i);
            geo.Freeze();
            WM.Color c = ColorOf(m.Kind, m.Aci);
            W3.DiffuseMaterial mat = new(new WM.SolidColorBrush(c));
            mat.Freeze();
            group.Children.Add(new W3.GeometryModel3D(geo, mat) { BackMaterial = mat });
        }

        // Tubos e linhas agrupados por cor numa malha so (menos objetos para o WPF).
        double thin = Math.Max(_size * 0.0018, 0.01);
        foreach (IGrouping<WM.Color, Scene3D.Tube> byColor in scene.Tubes.GroupBy(t => ColorOf(t.Kind, t.Aci)))
        {
            W3.MeshGeometry3D geo = new();
            foreach (Scene3D.Tube tube in byColor)
            {
                double r = tube.Radius > 1e-6 ? tube.Radius : thin;
                for (int i = 0; i + 1 < tube.Pts.Count; i++)
                    AddPrism(geo, Map(tube.Pts[i]), Map(tube.Pts[i + 1]), r, tube.Radius > 1e-6 ? 10 : 4);
            }

            geo.Freeze();
            W3.DiffuseMaterial mat = new(new WM.SolidColorBrush(byColor.Key));
            mat.Freeze();
            group.Children.Add(new W3.GeometryModel3D(geo, mat) { BackMaterial = mat });
        }

        if (scene.Points.Count > 0)
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
            W3.EmissiveMaterial mat = new(new WM.SolidColorBrush(WM.Color.FromRgb(150, 150, 155)));
            mat.Freeze();
            group.Children.Add(new W3.GeometryModel3D(geo, new W3.DiffuseMaterial(new WM.SolidColorBrush(WM.Color.FromRgb(120, 120, 125)))) { BackMaterial = mat });
        }

        // Plano do corte (translucido, por ultimo para a transparencia funcionar).
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

        // Isometrica: observador do lado "atras" (direita da linha), olhando para a frente, a 35 graus.
        P2 first = (box.Line[1] - box.Line[0]).Unit();
        P2 look = first.Left;
        _defaultYaw = (Math.Atan2(look.Y, look.X) * 180 / Math.PI) + 30;
    }

    /// <summary>Prisma de n lados entre dois pontos (tubo).</summary>
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

    private static WM.Color ColorOf(ItemKind kind, int aci)
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
            _ => WM.Color.FromRgb(230, 230, 230)
        };
    }

    // ------------------------------------------------------------------
    // Camera
    // ------------------------------------------------------------------

    private W3.Vector3D Look()
    {
        double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        W3.Vector3D d = new(Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), -Math.Sin(pitch));
        d.Normalize();
        return d;
    }

    private void ResetView()
    {
        _yaw = _defaultYaw;
        _pitch = 35;
        _target = new W3.Point3D(0, 0, 0);
        _camera.Width = _size * 1.4;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        W3.Vector3D look = Look();
        _camera.LookDirection = look;
        _camera.Position = _target - (look * (_size * 4));
        _camera.NearPlaneDistance = 0.01;
        _camera.FarPlaneDistance = _size * 20;
    }
}
