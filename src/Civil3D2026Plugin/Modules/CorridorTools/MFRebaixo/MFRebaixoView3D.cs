using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.UI.Common;

using SW = System.Windows;
using W3 = System.Windows.Media.Media3D;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>Cor e ordem de desenho de um link do corredor pelos codes (planta e 3D).</summary>
internal static class RebaixoStyle
{
    public readonly record struct Look(WM.Color Color, int Order, bool Plan, bool Overlay);

    public static Look Of(string[] codes)
    {
        if (codes.Length == 0)
            return new Look(WM.Colors.Transparent, -1, false, false);

        bool Has(string c) => codes.Any(x => x.Equals(c, StringComparison.OrdinalIgnoreCase));
        bool top = Has("Top") || Has("TOPO");

        if (Has("REBAIXO"))
            return new Look(WM.Color.FromRgb(225, 45, 60), 100, true, codes.Length == 1);
        if (Has("Curb"))
            return new Look(WM.Color.FromRgb(228, 228, 218), 60, top, false);
        if (Has("Sarjeta"))
            return new Look(WM.Color.FromRgb(172, 172, 172), 55, top, false);
        if (Has("Calcada"))
            return new Look(WM.Color.FromRgb(206, 176, 118), 50, true, false);
        if (top || Has("Pave") || Has("Pave1") || Has("Pave2"))
            return new Look(WM.Color.FromRgb(92, 94, 100), 10, true, false);
        if (Has("Calcada_Fundo") || Has("CalcadaBase"))
            return new Look(WM.Color.FromRgb(150, 128, 92), 5, false, false);
        if (Has("Base"))
            return new Look(WM.Color.FromRgb(146, 116, 82), 4, false, false);
        if (Has("SubBase"))
            return new Look(WM.Color.FromRgb(128, 104, 78), 3, false, false);
        if (Has("SubGrade"))
            return new Look(WM.Color.FromRgb(108, 90, 70), 2, false, false);
        return new Look(WM.Color.FromRgb(132, 134, 140), 1, false, false);
    }
}

/// <summary>
/// Vista 3D do trecho do corredor em volta do rebaixo: faixas entre seções vizinhas,
/// coloridas pelos codes. Botão esquerdo gira, direito/meio arrasta, roda aproxima,
/// duplo clique volta ao enquadramento.
/// </summary>
internal sealed class MFRebaixoView3D : WC.Grid
{
    private readonly WC.Viewport3D _viewport = new() { ClipToBounds = true };
    private readonly W3.PerspectiveCamera _camera = new() { FieldOfView = 40, NearPlaneDistance = 0.02, FarPlaneDistance = 20000, UpDirection = new W3.Vector3D(0, 0, 1) };
    private readonly W3.ModelVisual3D _content = new();
    private readonly WC.TextBlock _empty = new() { Foreground = ToolTheme.GraphicText, Margin = new SW.Thickness(10), Text = "Selecione um rebaixo." };

    private double _azimuth = -60, _elevation = 32, _distance = 30, _homeDistance = 30;
    private W3.Point3D _target, _homeTarget;
    private SW.Point? _last;
    private bool _pan;

    public MFRebaixoView3D()
    {
        Background = ToolTheme.GraphicBack;
        ClipToBounds = true;
        _viewport.Camera = _camera;

        var lights = new W3.Model3DGroup();
        lights.Children.Add(new W3.AmbientLight(WM.Color.FromRgb(95, 95, 95)));
        lights.Children.Add(new W3.DirectionalLight(WM.Color.FromRgb(200, 200, 195), new W3.Vector3D(-0.45, -0.6, -1.0)));
        lights.Children.Add(new W3.DirectionalLight(WM.Color.FromRgb(70, 70, 80), new W3.Vector3D(0.6, 0.4, -0.3)));
        _viewport.Children.Add(new W3.ModelVisual3D { Content = lights });
        _viewport.Children.Add(_content);

        Children.Add(_viewport);
        Children.Add(_empty);
        Children.Add(new WC.TextBlock
        {
            Text = "girar: botao esquerdo   arrastar: botao direito   zoom: roda   enquadrar: duplo clique",
            Foreground = ToolTheme.GraphicLine,
            FontSize = 10,
            Margin = new SW.Thickness(8, 0, 0, 4),
            VerticalAlignment = SW.VerticalAlignment.Bottom,
            IsHitTestVisible = false
        });

        MouseDown += OnDown;
        MouseMove += OnMove;
        MouseUp += (_, _) =>
        {
            _last = null;
            ReleaseMouseCapture();
        };
        MouseWheel += (_, e) =>
        {
            _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.85 : 1.18), 0.5, 5000);
            UpdateCamera();
        };
    }

    /// <summary>Monta a malha. exaggeration multiplica as cotas (meio-fio de 15 cm aparece melhor).</summary>
    public void SetModel(RebaixoModel? model, double exaggeration, bool keepCamera)
    {
        _content.Content = null;
        if (model is null || model.Sections.Count < 2)
        {
            _empty.Text = model is null ? "Selecione um rebaixo." : "Sem secoes calculadas neste trecho (Rebuild no corredor?).";
            _empty.Visibility = SW.Visibility.Visible;
            return;
        }

        List<Point3d> all = model.Sections.SelectMany(s => s.Subassemblies.SelectMany(a => a.Links.SelectMany(l => l.Points))).ToList();
        if (all.Count == 0)
        {
            _empty.Text = "Sem links calculados neste trecho.";
            _empty.Visibility = SW.Visibility.Visible;
            return;
        }

        _empty.Visibility = SW.Visibility.Collapsed;
        double cx = (all.Min(p => p.X) + all.Max(p => p.X)) / 2.0;
        double cy = (all.Min(p => p.Y) + all.Max(p => p.Y)) / 2.0;
        double z0 = all.Min(p => p.Z);
        double z1 = all.Max(p => p.Z);
        double ex = Math.Max(exaggeration, 0.1);
        W3.Point3D L(Point3d p, double lift = 0) => new(p.X - cx, p.Y - cy, ((p.Z - z0) * ex) + lift);

        Dictionary<WM.Color, W3.MeshGeometry3D> meshes = new();
        W3.MeshGeometry3D MeshOf(WM.Color c)
        {
            if (!meshes.TryGetValue(c, out W3.MeshGeometry3D? m))
                meshes[c] = m = new W3.MeshGeometry3D();
            return m;
        }

        void Quad(W3.MeshGeometry3D m, W3.Point3D a, W3.Point3D b, W3.Point3D c, W3.Point3D d)
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

        for (int k = 0; k + 1 < model.Sections.Count; k++)
        {
            ModelSection a = model.Sections[k], b = model.Sections[k + 1];
            foreach ((long key, List<ModelLink> linksA) in a.Subassemblies)
            {
                List<ModelLink>? linksB = b.Subassemblies.FirstOrDefault(x => x.Key == key).Links;
                if (linksB is null)
                    continue;

                int n = Math.Min(linksA.Count, linksB.Count);
                for (int j = 0; j < n; j++)
                {
                    ModelLink la = linksA[j], lb = linksB[j];
                    if (la.Points.Length < 2 || la.Points.Length != lb.Points.Length)
                        continue;

                    RebaixoStyle.Look look = RebaixoStyle.Of(la.Codes);
                    if (look.Order < 0)
                        continue;

                    double lift = look.Overlay ? 0.006 : 0;
                    W3.MeshGeometry3D mesh = MeshOf(look.Color);
                    for (int p = 0; p + 1 < la.Points.Length; p++)
                        Quad(mesh, L(la.Points[p], lift), L(la.Points[p + 1], lift), L(lb.Points[p + 1], lift), L(lb.Points[p], lift));
                }
            }
        }

        var group = new W3.Model3DGroup();
        foreach ((WM.Color color, W3.MeshGeometry3D mesh) in meshes)
        {
            if (mesh.Positions.Count == 0)
                continue;
            mesh.Freeze();
            var brush = new WM.SolidColorBrush(color);
            brush.Freeze();
            var material = new W3.DiffuseMaterial(brush);
            group.Children.Add(new W3.GeometryModel3D(mesh, material) { BackMaterial = material });
        }

        // Marcos S0..S3: postes amarelos no eixo da baseline.
        var posts = new W3.MeshGeometry3D();
        double h = Math.Max(0.6, (z1 - z0) * ex + 0.6);
        foreach (Point3d mk in model.Marks)
        {
            if (mk == Point3d.Origin)
                continue;
            W3.Point3D c = new(mk.X - cx, mk.Y - cy, 0);
            const double r = 0.06;
            W3.Point3D[] box =
            {
                new(c.X - r, c.Y - r, 0), new(c.X + r, c.Y - r, 0), new(c.X + r, c.Y + r, 0), new(c.X - r, c.Y + r, 0),
                new(c.X - r, c.Y - r, h), new(c.X + r, c.Y - r, h), new(c.X + r, c.Y + r, h), new(c.X - r, c.Y + r, h)
            };
            Quad(posts, box[0], box[1], box[5], box[4]);
            Quad(posts, box[1], box[2], box[6], box[5]);
            Quad(posts, box[2], box[3], box[7], box[6]);
            Quad(posts, box[3], box[0], box[4], box[7]);
            Quad(posts, box[4], box[5], box[6], box[7]);
        }

        if (posts.Positions.Count > 0)
        {
            posts.Freeze();
            var yellow = new W3.DiffuseMaterial(new WM.SolidColorBrush(WM.Color.FromRgb(255, 190, 30)));
            group.Children.Add(new W3.GeometryModel3D(posts, yellow) { BackMaterial = yellow });
        }

        group.Freeze();
        _content.Content = group;

        double span = Math.Max(all.Max(p => p.X) - all.Min(p => p.X), all.Max(p => p.Y) - all.Min(p => p.Y));
        _homeTarget = new W3.Point3D(0, 0, (z1 - z0) * ex / 2.0);
        _homeDistance = Math.Max(span * 1.1, 5);
        if (!keepCamera)
            Home();
        else
            UpdateCamera();
    }

    public void Home()
    {
        _target = _homeTarget;
        _distance = _homeDistance;
        _azimuth = -60;
        _elevation = 32;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        double az = _azimuth * Math.PI / 180.0, el = _elevation * Math.PI / 180.0;
        W3.Vector3D dir = new(Math.Cos(el) * Math.Cos(az), Math.Cos(el) * Math.Sin(az), Math.Sin(el));
        _camera.Position = _target + (dir * _distance);
        _camera.LookDirection = -dir * _distance;
        _camera.UpDirection = new W3.Vector3D(0, 0, 1);
    }

    private void OnDown(object sender, WI.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Home();
            return;
        }

        _last = e.GetPosition(this);
        _pan = e.ChangedButton != WI.MouseButton.Left;
        CaptureMouse();
    }

    private void OnMove(object sender, WI.MouseEventArgs e)
    {
        if (_last is not SW.Point last)
            return;
        SW.Point p = e.GetPosition(this);
        double dx = p.X - last.X, dy = p.Y - last.Y;
        _last = p;

        if (_pan)
        {
            double az = _azimuth * Math.PI / 180.0;
            W3.Vector3D right = new(-Math.Sin(az), Math.Cos(az), 0);
            W3.Vector3D forward = new(-Math.Cos(az), -Math.Sin(az), 0);
            double k = _distance / Math.Max(ActualHeight, 100) * 1.2;
            _target += (-dx * k * right) + (dy * k * forward);
        }
        else
        {
            _azimuth -= dx * 0.4;
            _elevation = Math.Clamp(_elevation + (dy * 0.3), 3, 89);
        }

        UpdateCamera();
    }
}
