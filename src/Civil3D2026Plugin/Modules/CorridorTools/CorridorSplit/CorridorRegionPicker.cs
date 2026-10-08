using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;

namespace Civil3D2026Plugin.Modules.CorridorTools.CorridorSplit;

/// <summary>
/// Selecao grafica do CORRSPLIT: identifica regioes pela faixa efetivamente
/// calculada nas secoes do Corridor (nao pela proximidade ao eixo).
/// Os contornos sao TRANSIENTES: nunca sao adicionados ao DWG.
/// </summary>
internal sealed class CorridorRegionPicker : IDisposable
{
    private readonly Autodesk.AutoCAD.ApplicationServices.Document _document;
    private readonly List<RegionFootprint> _footprints = new();
    private readonly List<AcadDb.Polyline> _selectedOverlays = new();
    private AcadDb.Polyline? _hoverOverlay;
    private RegionFootprint? _hovered;
    private bool _disposed;
    private bool _loaded;

    public int AvailableRegions => _footprints.Count;
    private static readonly IntegerCollection Viewports = new();
    private static TransientManager Transients => TransientManager.CurrentTransientManager;

    public CorridorRegionPicker(Autodesk.AutoCAD.ApplicationServices.Document document,
        AcadDb.ObjectId corridorId, IReadOnlyList<RegionChoice> choices)
    {
        _document = document;
        using var tr = document.Database.TransactionManager.StartTransaction();
        var corridor = (CivilDb.Corridor)tr.GetObject(corridorId, AcadDb.OpenMode.ForRead);

        foreach (RegionChoice choice in choices)
        {
            try
            {
                if (choice.BaselineIndex >= corridor.Baselines.Count) continue;
                var baseline = corridor.Baselines[choice.BaselineIndex];
                if (choice.RegionIndex >= baseline.BaselineRegions.Count) continue;
                var region = baseline.BaselineRegions[choice.RegionIndex];
                var outline = ComputeFootprint(baseline, region);
                if (outline != null) _footprints.Add(new RegionFootprint(choice, outline));
            }
            catch (System.Exception)
            {
                // Alguma regiao nao possui seccoes validas; ela continua disponivel na tabela.
            }
        }
        _loaded = true;
    }

    /// <summary>
    /// Seleciona exatamente uma regiao por clique dentro de seu contorno.
    /// ESC cancela apenas esta selecao e devolve foco a janela.
    /// </summary>
    public RegionChoice? Pick(Editor editor)
    {
        if (_disposed) return null;
        if (!_loaded || _footprints.Count == 0) return null;

        RegionChoice? chosen = null;
        editor.PointMonitor += OnPointMonitor;
        try
        {
            var options = new PromptPointOptions(
                "\nCORRSPLIT - clique DENTRO de uma regiao (contorno azul); ESC para voltar: ")
            {
                AllowNone = true
            };
            PromptPointResult result = editor.GetPoint(options);
            if (result.Status == PromptStatus.OK)
            {
                var hit = FindAt(result.Value);
                if (hit != null) chosen = hit.Choice;
            }
        }
        finally
        {
            editor.PointMonitor -= OnPointMonitor;
            ChangeHover(null);
        }

        return chosen;
    }

    /// <summary>
    /// Realce persistente das regioes marcadas enquanto a janela permanece
    /// aberta. Todos os transient graphics sao apagados no Dispose.
    /// </summary>
    public void ShowSelected(IEnumerable<RegionChoice> selected)
    {
        if (_disposed) return;
        ClearSelected();
        var set = new HashSet<RegionChoice>(selected);
        foreach (RegionFootprint footprint in _footprints)
        {
            if (!set.Contains(footprint.Choice)) continue;
            var poly = NewOutline(footprint, colorIndex: 151);
            try
            {
                if (Transients.AddTransient(poly, TransientDrawingMode.Main, 0, Viewports))
                    _selectedOverlays.Add(poly);
                else poly.Dispose();
            }
            catch (System.Exception)
            {
                poly.Dispose();
            }
        }
    }

    private void OnPointMonitor(object sender, PointMonitorEventArgs args)
    {
        try
        {
            RegionFootprint? region = FindAt(args.Context.RawPoint);
            ChangeHover(region);
            if (region != null)
                args.AppendToolTipText(
                    "CORRSPLIT: " + region.Choice.RegionName +
                    "\nBaseline: " + region.Choice.BaselineName +
                    "\nClique para selecionar a regiao");
        }
        catch (System.Exception)
        {
            // Nunca propagar excecoes do callback de movimentacao do cursor ao AutoCAD.
        }
    }

    private void ChangeHover(RegionFootprint? value)
    {
        if (ReferenceEquals(value, _hovered)) return;
        ClearHover();
        _hovered = value;
        if (value == null) return;

        var poly = NewOutline(value, colorIndex: 5); // azul do CAD
        try
        {
            if (Transients.AddTransient(poly, TransientDrawingMode.Main, 0, Viewports))
                _hoverOverlay = poly;
            else poly.Dispose();
        }
        catch (System.Exception)
        {
            poly.Dispose();
        }
    }

    private RegionFootprint? FindAt(Point3d point)
    {
        RegionFootprint? candidate = null;
        double rank = double.PositiveInfinity;
        foreach (RegionFootprint footprint in _footprints)
        {
            if (!footprint.Contains(point)) continue;
            // Em regioes sobrepostas, priorizar a menor faixa visivel.
            // Isso tende a escolher a regiao de sarjeta em vez de uma pista larga.
            double score = footprint.Area;
            if (score < rank)
            {
                rank = score;
                candidate = footprint;
            }
        }
        return candidate;
    }

    private static AcadDb.Polyline NewOutline(RegionFootprint region, int colorIndex)
    {
        var outline = new AcadDb.Polyline(region.Points.Count)
        {
            Closed = true,
            ColorIndex = colorIndex,
            LineWeight = AcadDb.LineWeight.LineWeight050
        };
        for (int i = 0; i < region.Points.Count; i++)
            outline.AddVertexAt(i, region.Points[i], 0, 0, 0);
        return outline;
    }

    private static List<Point2d>? ComputeFootprint(
        CivilDb.Baseline baseline, CivilDb.BaselineRegion region)
    {
        // Cada AppliedAssembly possui pontos com Station e Offset referentes
        // a propria Baseline. Usar as laterais extrema de cada secao representa
        // a geometria real em planta, inclusive com targets de offset variaveis.
        var sections = new List<Section>();
        int count = region.AppliedAssemblies.Count;
        if (count == 0) return null;

        const int maxSections = 120;
        int step = Math.Max(1, (int)Math.Ceiling(count / (double)maxSections));
        for (int i = 0; i < count; i += step)
            AddSection(region.AppliedAssemblies[i], region, sections);
        if ((count - 1) % step != 0)
            AddSection(region.AppliedAssemblies[count - 1], region, sections);

        sections = sections.OrderBy(x => x.Station).ToList();
        if (sections.Count == 0) return null;

        // Garantir pontos exatamente no inicio/fim, usando a secao calculada
        // mais proxima. Os vertices intermediarios respeitam o deslocamento real.
        double start = Math.Min(region.StartStation, region.EndStation);
        double end = Math.Max(region.StartStation, region.EndStation);
        if (end - start < 1e-7) return null;

        var boundaries = new List<Section> { new(start, sections[0].Left, sections[0].Right) };
        foreach (var section in sections)
            if (section.Station > start + 1e-5 && section.Station < end - 1e-5)
                boundaries.Add(section);
        boundaries.Add(new Section(end, sections[^1].Left, sections[^1].Right));

        var left = new List<Point2d>();
        var right = new List<Point2d>();
        foreach (var section in boundaries)
        {
            if (!TryWorld(baseline, section.Station, section.Left, out Point2d pLeft) ||
                !TryWorld(baseline, section.Station, section.Right, out Point2d pRight))
                continue;
            left.Add(pLeft);
            right.Add(pRight);
        }

        if (left.Count < 2 || right.Count != left.Count) return null;
        var polygon = new List<Point2d>(left.Count + right.Count);
        polygon.AddRange(left);
        right.Reverse();
        polygon.AddRange(right);
        if (Math.Abs(PolygonArea(polygon)) < 0.0001) return null;
        return polygon;
    }

    private static void AddSection(CivilDb.AppliedAssembly assembly,
        CivilDb.BaselineRegion region, List<Section> sections)
    {
        bool initialized = false;
        double station = 0, min = 0, max = 0;
        foreach (CivilDb.CalculatedPoint point in assembly.Points)
        {
            var soe = point.StationOffsetElevationToBaseline;
            if (!double.IsFinite(soe.X) || !double.IsFinite(soe.Y)) continue;
            if (!initialized)
            {
                station = soe.X;
                min = max = soe.Y;
                initialized = true;
            }
            else
            {
                min = Math.Min(min, soe.Y);
                max = Math.Max(max, soe.Y);
            }
        }
        if (!initialized || max - min < 0.025) return;
        if (station < Math.Min(region.StartStation, region.EndStation) - 0.01 ||
            station > Math.Max(region.StartStation, region.EndStation) + 0.01)
            return;
        sections.Add(new Section(station, min, max));
    }

    private static bool TryWorld(
        CivilDb.Baseline baseline, double station, double offset, out Point2d result)
    {
        result = new Point2d();
        try
        {
            Point3d xyz = baseline.StationOffsetElevationToXYZ(new Point3d(station, offset, 0));
            if (xyz.DistanceTo(Point3d.Origin) < 1e-10 ||
                !double.IsFinite(xyz.X) || !double.IsFinite(xyz.Y))
                return false;
            result = new Point2d(xyz.X, xyz.Y);
            return true;
        }
        catch (System.Exception) { return false; }
    }

    private static double PolygonArea(List<Point2d> points)
    {
        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            Point2d a = points[i], b = points[(i + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area * .5;
    }

    private void ClearHover()
    {
        if (_hoverOverlay == null) { _hovered = null; return; }
        try { Transients.EraseTransient(_hoverOverlay, Viewports); }
        catch (System.Exception) { }
        _hoverOverlay.Dispose();
        _hoverOverlay = null;
        _hovered = null;
    }

    private void ClearSelected()
    {
        foreach (var poly in _selectedOverlays)
        {
            try { Transients.EraseTransient(poly, Viewports); }
            catch (System.Exception) { }
            poly.Dispose();
        }
        _selectedOverlays.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearHover();
        ClearSelected();
        _footprints.Clear();
    }

    private sealed record Section(double Station, double Left, double Right);

    private sealed class RegionFootprint
    {
        public RegionChoice Choice { get; }
        public List<Point2d> Points { get; }
        public double Area { get; }
        private readonly double _minX, _maxX, _minY, _maxY;

        public RegionFootprint(RegionChoice choice, List<Point2d> points)
        {
            Choice = choice;
            Points = points;
            Area = Math.Abs(PolygonArea(points));
            _minX = points.Min(p => p.X);
            _maxX = points.Max(p => p.X);
            _minY = points.Min(p => p.Y);
            _maxY = points.Max(p => p.Y);
        }

        public bool Contains(Point3d world)
        {
            double x = world.X, y = world.Y;
            if (x < _minX - .10 || x > _maxX + .10 ||
                y < _minY - .10 || y > _maxY + .10) return false;

            // Mesmo criterio do CAD: ponto dentro da faixa ou muito perto
            // do seu contorno. Nao existe mais o limiar arbitrario de 35 m.
            bool inside = false;
            for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
            {
                var a = Points[i];
                var b = Points[j];
                if (DistanceToSegment(x, y, a, b) <= .10) return true;
                if ((a.Y > y) != (b.Y > y) &&
                    x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        private static double DistanceToSegment(double x, double y, Point2d a, Point2d b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double denominator = vx * vx + vy * vy;
            double t = denominator < 1e-12 ? 0 :
                Math.Clamp(((x - a.X) * vx + (y - a.Y) * vy) / denominator, 0, 1);
            double dx = x - (a.X + t * vx), dy = y - (a.Y + t * vy);
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
