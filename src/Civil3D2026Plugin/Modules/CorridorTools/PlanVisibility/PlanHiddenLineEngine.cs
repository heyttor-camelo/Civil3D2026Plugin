using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

using AcBr = Autodesk.AutoCAD.BoundaryRepresentation;

namespace Civil3D2026Plugin.Modules.CorridorTools.PlanVisibility;

/// <summary>
/// Remocao de linhas ocultas para a vista de planta (camera olhando para -Z).
///
/// Para cada solido/body "sujeito", gera as polilinhas visiveis de cima,
/// considerando como obstaculos as faces de todos os solidos "ocultadores".
///
/// Algoritmo (vetorial, exato nas transicoes):
/// 1. cada entidade e convertida em triangulos (Brep -> Mesh2d) e em linhas
///    candidatas: arestas do Brep (curvas amostradas) + arestas de silhueta da
///    malha (dobras vistas de cima, para cilindros/superficies curvas);
/// 2. os triangulos com area em planta sao indexados numa grade XY;
/// 3. cada segmento candidato e dividido nos pontos em que cruza, em planta,
///    as arestas dos triangulos proximos (e onde atravessa o plano deles);
///    entre dois cortes a visibilidade e constante, entao basta testar o ponto
///    medio: oculto se algum triangulo cobre o ponto em XY acima dele em Z.
/// </summary>
internal static class PlanHiddenLineEngine
{
    private readonly struct Tri
    {
        public Tri(Point3d a, Point3d b, Point3d c)
        {
            A = a;
            B = b;
            C = c;
            MinX = Math.Min(a.X, Math.Min(b.X, c.X));
            MaxX = Math.Max(a.X, Math.Max(b.X, c.X));
            MinY = Math.Min(a.Y, Math.Min(b.Y, c.Y));
            MaxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            Area2 = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
        }

        public Point3d A { get; }
        public Point3d B { get; }
        public Point3d C { get; }
        public double MinX { get; }
        public double MaxX { get; }
        public double MinY { get; }
        public double MaxY { get; }

        /// <summary>Duas vezes a area assinada em planta.</summary>
        public double Area2 { get; }
    }

    /// <summary>Geometria de uma entidade pronta para o calculo.</summary>
    internal sealed class EntityGeometry
    {
        public long Key { get; init; }
        public List<Tri3> Triangles { get; } = new();
        public List<Point3d[]> Candidates { get; } = new();
        public Extents2dBox Box { get; set; }
    }

    internal readonly record struct Tri3(Point3d A, Point3d B, Point3d C);

    internal struct Extents2dBox
    {
        public double MinX;
        public double MinY;
        public double MaxX;
        public double MaxY;
        public bool IsValid;

        public void Add(Point3d p)
        {
            if (!IsValid)
            {
                MinX = MaxX = p.X;
                MinY = MaxY = p.Y;
                IsValid = true;
                return;
            }

            MinX = Math.Min(MinX, p.X);
            MinY = Math.Min(MinY, p.Y);
            MaxX = Math.Max(MaxX, p.X);
            MaxY = Math.Max(MaxY, p.Y);
        }

        public void Add(Extents2dBox other)
        {
            if (!other.IsValid)
                return;

            Add(new Point3d(other.MinX, other.MinY, 0));
            Add(new Point3d(other.MaxX, other.MaxY, 0));
        }

        public bool Overlaps(Extents2dBox other, double margin) =>
            IsValid && other.IsValid &&
            MinX - margin <= other.MaxX && other.MinX - margin <= MaxX &&
            MinY - margin <= other.MaxY && other.MinY - margin <= MaxY;

        public double Diagonal => IsValid ? Math.Sqrt(((MaxX - MinX) * (MaxX - MinX)) + ((MaxY - MinY) * (MaxY - MinY))) : 0.0;
    }

    /// <summary>
    /// Extrai triangulos e linhas candidatas de uma entidade (Solid3d, Body...).
    /// transform leva as coordenadas do Brep para o WCS (solidos dentro de
    /// blocos); null = ja em WCS. Retorna null se o Brep nao puder ser lido.
    /// </summary>
    public static EntityGeometry? Extract(AcadDb.Entity entity, long key, Matrix3d? transform = null)
    {
        try
        {
            EntityGeometry geometry = new() { Key = key };

            using AcBr.Brep brep = new(entity);

            ExtractTriangles(brep, geometry, MeshDistanceTolerance(entity));
            ExtractBrepEdges(brep, geometry);

            if (transform is Matrix3d m)
                ApplyTransform(geometry, m);

            // Silhuetas dependem da direcao de vista: calcular ja em WCS.
            AddSilhouettes(geometry);
            UpdateBox(geometry);
            return geometry.Box.IsValid ? geometry : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Somente as arestas do Brep (sem malha), nas coordenadas do proprio objeto.
    /// Usado para desenhar o solido em wireframe sem redesenhar o objeto nativo.
    /// </summary>
    public static List<Point3d[]>? ExtractEdges(AcadDb.Entity entity)
    {
        try
        {
            EntityGeometry geometry = new();
            using AcBr.Brep brep = new(entity);
            ExtractBrepEdges(brep, geometry);
            return geometry.Candidates;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Malha (triangulos) e arestas do Brep, sem as silhuetas de planta, nas coordenadas do
    /// proprio objeto. Usado pela vista 3D e pelos fantasmas do visualizador de secao.
    /// </summary>
    public static (List<Tri3> Triangles, List<Point3d[]> Edges)? ExtractMesh(AcadDb.Entity entity)
    {
        try
        {
            EntityGeometry geometry = new();
            using AcBr.Brep brep = new(entity);
            try
            {
                ExtractTriangles(brep, geometry, MeshDistanceTolerance(entity));
            }
            catch
            {
                // Sem malha (Brep so com arestas): ficam as arestas.
            }

            ExtractBrepEdges(brep, geometry);
            return (geometry.Triangles, geometry.Candidates);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Junta varias geometrias (ex.: solidos de um bloco) sob uma chave.</summary>
    public static EntityGeometry? Merge(long key, IEnumerable<EntityGeometry> parts)
    {
        EntityGeometry merged = new() { Key = key };

        foreach (EntityGeometry part in parts)
        {
            merged.Triangles.AddRange(part.Triangles);
            merged.Candidates.AddRange(part.Candidates);
        }

        UpdateBox(merged);
        return merged.Box.IsValid ? merged : null;
    }

    private static void ApplyTransform(EntityGeometry geometry, Matrix3d m)
    {
        for (int i = 0; i < geometry.Triangles.Count; i++)
        {
            Tri3 t = geometry.Triangles[i];
            geometry.Triangles[i] = new Tri3(t.A.TransformBy(m), t.B.TransformBy(m), t.C.TransformBy(m));
        }

        foreach (Point3d[] line in geometry.Candidates)
        {
            for (int i = 0; i < line.Length; i++)
                line[i] = line[i].TransformBy(m);
        }
    }

    private static void UpdateBox(EntityGeometry geometry)
    {
        {
            Extents2dBox box = default;
            foreach (Tri3 t in geometry.Triangles)
            {
                box.Add(t.A);
                box.Add(t.B);
                box.Add(t.C);
            }

            foreach (Point3d[] line in geometry.Candidates)
            {
                foreach (Point3d p in line)
                    box.Add(p);
            }

            geometry.Box = box;
        }
    }

    /// <summary>
    /// Tolerancia de corda da malha proporcional ao tamanho do objeto, para
    /// limitar a quantidade de triangulos em solidos grandes e curvos
    /// (lofts, sweeps). 0 = manter o padrao do AutoCAD.
    /// </summary>
    private static double MeshDistanceTolerance(AcadDb.Entity entity)
    {
        try
        {
            AcadDb.Extents3d extents = entity.GeometricExtents;
            double diagonal = extents.MinPoint.DistanceTo(extents.MaxPoint);
            return diagonal > 0 ? Math.Max(diagonal * 5e-4, 1e-4) : 0.0;
        }
        catch
        {
            return 0.0;
        }
    }

    private static void ExtractTriangles(AcBr.Brep brep, EntityGeometry geometry, double distanceTolerance)
    {
        using AcBr.Mesh2dControl control = new();

        try
        {
            control.AngleTolerance = Math.PI / 18.0; // 10 graus

            if (distanceTolerance > 0)
                control.DistanceTolerance = distanceTolerance;
        }
        catch
        {
            // Mantem o padrao se a propriedade nao puder ser ajustada.
        }

        using AcBr.Mesh2dFilter filter = new();
        filter.Insert(brep, control);

        using AcBr.Mesh2d mesh = new(filter);

        foreach (AcBr.Element2d element in mesh.Element2ds)
        {
            List<Point3d> points = new();

            foreach (AcBr.Node node in element.Nodes)
            {
                points.Add(node.Point);
                node.Dispose();
            }

            element.Dispose();

            // Poligono -> leque de triangulos.
            for (int i = 1; i + 1 < points.Count; i++)
                geometry.Triangles.Add(new Tri3(points[0], points[i], points[i + 1]));
        }
    }

    private static void ExtractBrepEdges(AcBr.Brep brep, EntityGeometry geometry)
    {
        foreach (AcBr.Edge edge in brep.Edges)
        {
            try
            {
                Curve3d curve = edge.Curve;
                Point3d[] samples = SampleCurve(curve);

                if (samples.Length >= 2)
                    geometry.Candidates.Add(samples);
            }
            catch
            {
                // Aresta sem curva legivel: ignorada.
            }
            finally
            {
                edge.Dispose();
            }
        }
    }

    private static Point3d[] SampleCurve(Curve3d curve)
    {
        Curve3d native = curve is ExternalCurve3d external && external.NativeCurve is Curve3d n ? n : curve;

        Interval interval = native.GetInterval();
        double lo = interval.LowerBound;
        double hi = interval.UpperBound;

        if (native is LineSegment3d line)
            return new[] { line.StartPoint, line.EndPoint };

        if (double.IsInfinity(lo) || double.IsInfinity(hi) || hi <= lo)
            return Array.Empty<Point3d>();

        int count = 32;
        try
        {
            Point3d a = native.EvaluatePoint(lo);
            Point3d b = native.EvaluatePoint(hi);
            Point3d m = native.EvaluatePoint((lo + hi) / 2.0);

            // Quase reta: 2 pontos bastam.
            Vector3d chord = b - a;
            if (chord.Length > 1e-9)
            {
                double deviation = (m - a).CrossProduct(chord).Length / chord.Length;
                if (deviation <= chord.Length * 1e-6)
                    return new[] { a, b };
            }
        }
        catch
        {
            // Segue com amostragem uniforme.
        }

        Point3d[] points = new Point3d[count + 1];
        for (int i = 0; i <= count; i++)
            points[i] = native.EvaluatePoint(lo + ((hi - lo) * i / count));

        return points;
    }

    /// <summary>
    /// Arestas de silhueta da malha: arestas internas cujas duas faces vizinhas
    /// ficam do mesmo lado da aresta quando projetadas em planta (a superficie
    /// "dobra" vista de cima). Teste independente da orientacao dos triangulos.
    /// </summary>
    private static void AddSilhouettes(EntityGeometry geometry)
    {
        if (geometry.Triangles.Count == 0)
            return;

        Dictionary<(long, long, long), int> vertexIds = new();
        List<Point3d> vertices = new();

        double quantum = 1e-6;

        int VertexId(Point3d p)
        {
            var key = ((long)Math.Round(p.X / quantum), (long)Math.Round(p.Y / quantum), (long)Math.Round(p.Z / quantum));
            if (!vertexIds.TryGetValue(key, out int id))
            {
                id = vertices.Count;
                vertexIds[key] = id;
                vertices.Add(p);
            }

            return id;
        }

        Dictionary<(int, int), List<int>> edges = new();

        void AddEdge(int u, int v, int opposite)
        {
            var key = u < v ? (u, v) : (v, u);
            if (!edges.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>(2);
                edges[key] = list;
            }

            list.Add(opposite);
        }

        foreach (Tri3 t in geometry.Triangles)
        {
            int a = VertexId(t.A);
            int b = VertexId(t.B);
            int c = VertexId(t.C);

            if (a == b || b == c || a == c)
                continue;

            AddEdge(a, b, c);
            AddEdge(b, c, a);
            AddEdge(c, a, b);
        }

        foreach (KeyValuePair<(int, int), List<int>> pair in edges)
        {
            if (pair.Value.Count != 2)
                continue;

            Point3d p = vertices[pair.Key.Item1];
            Point3d q = vertices[pair.Key.Item2];

            double ex = q.X - p.X;
            double ey = q.Y - p.Y;
            double lengthXY = Math.Sqrt((ex * ex) + (ey * ey));
            if (lengthXY <= 1e-9)
                continue;

            Point3d r1 = vertices[pair.Value[0]];
            Point3d r2 = vertices[pair.Value[1]];

            double s1 = (ex * (r1.Y - p.Y)) - (ey * (r1.X - p.X));
            double s2 = (ex * (r2.Y - p.Y)) - (ey * (r2.X - p.X));

            double tolerance = 1e-9 * lengthXY * lengthXY;
            if (Math.Abs(s1) <= tolerance || Math.Abs(s2) <= tolerance)
                continue;

            if (Math.Sign(s1) == Math.Sign(s2))
                geometry.Candidates.Add(new[] { p, q });
        }
    }

    /// <summary>
    /// Calcula as polilinhas visiveis de cima de cada sujeito, usando como
    /// obstaculos os triangulos de todos os ocultadores (os sujeitos inclusos).
    /// </summary>
    public static Dictionary<long, List<Point3d[]>> Compute(
        IReadOnlyCollection<EntityGeometry> subjects,
        IReadOnlyCollection<EntityGeometry> occluders,
        Func<bool>? shouldCancel = null,
        Action? subjectDone = null)
    {
        Dictionary<long, List<Point3d[]>> result = new();

        Extents2dBox scene = default;
        foreach (EntityGeometry g in occluders)
            scene.Add(g.Box);
        foreach (EntityGeometry g in subjects)
            scene.Add(g.Box);

        if (!scene.IsValid)
            return result;

        double diagonal = Math.Max(scene.Diagonal, 1e-6);
        double zTolerance = Math.Max(diagonal * 1e-7, 1e-6);
        double xyTolerance = Math.Max(diagonal * 1e-9, 1e-9);

        List<Tri> triangles = new();
        foreach (EntityGeometry g in occluders)
        {
            foreach (Tri3 t in g.Triangles)
            {
                Tri tri = new(t.A, t.B, t.C);

                // Faces verticais nao cobrem nada em planta.
                if (Math.Abs(tri.Area2) > xyTolerance * xyTolerance * 4.0)
                    triangles.Add(tri);
            }
        }

        TriangleGrid grid = new(triangles, scene);

        foreach (EntityGeometry subject in subjects)
        {
            List<Point3d[]> visible = new();

            if (shouldCancel?.Invoke() == true)
                throw new OperationCanceledException();

            foreach (Point3d[] chain in subject.Candidates)
                AppendVisible(chain, grid, zTolerance, xyTolerance, visible);

            result[subject.Key] = visible;
            subjectDone?.Invoke();
        }

        return result;
    }

    private static void AppendVisible(
        Point3d[] chain,
        TriangleGrid grid,
        double zTolerance,
        double xyTolerance,
        List<Point3d[]> output)
    {
        List<Point3d> current = new();

        void Flush()
        {
            if (current.Count >= 2)
                output.Add(RemoveCollinear(current, xyTolerance));

            current = new List<Point3d>();
        }

        for (int i = 0; i + 1 < chain.Length; i++)
        {
            Point3d p = chain[i];
            Point3d q = chain[i + 1];

            double dx = q.X - p.X;
            double dy = q.Y - p.Y;

            // Segmentos verticais viram um ponto em planta.
            if (Math.Sqrt((dx * dx) + (dy * dy)) <= xyTolerance)
                continue;

            // Segmentos longos sao processados em pedacos do tamanho da grade,
            // para que cada consulta so veja os triangulos realmente proximos.
            int pieces = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((dx * dx) + (dy * dy)) / (grid.CellSize * 4.0)));
            List<double> cuts = new();

            for (int piece = 0; piece < pieces; piece++)
            {
                double s0 = (double)piece / pieces;
                double s1 = (double)(piece + 1) / pieces;

                foreach (double local in grid.CollectCuts(Lerp(p, q, s0), Lerp(p, q, s1), xyTolerance))
                    cuts.Add(s0 + ((s1 - s0) * local));
            }

            cuts.Sort();

            for (int k = 0; k + 1 < cuts.Count; k++)
            {
                double t0 = cuts[k];
                double t1 = cuts[k + 1];
                if (t1 - t0 <= 1e-12)
                    continue;

                Point3d mid = Lerp(p, q, (t0 + t1) / 2.0);
                bool isVisible = !grid.IsCovered(mid, zTolerance, xyTolerance);

                if (!isVisible)
                {
                    Flush();
                    continue;
                }

                Point3d a = Lerp(p, q, t0);
                Point3d b = Lerp(p, q, t1);

                if (current.Count == 0 || current[^1].DistanceTo(a) > xyTolerance * 10.0)
                {
                    Flush();
                    current.Add(a);
                }

                current.Add(b);
            }
        }

        Flush();
    }

    /// <summary>Remove pontos intermediarios alinhados (os cortes geram muitos).</summary>
    private static Point3d[] RemoveCollinear(List<Point3d> points, double tolerance)
    {
        if (points.Count <= 2)
            return points.ToArray();

        List<Point3d> result = new() { points[0] };

        for (int i = 1; i + 1 < points.Count; i++)
        {
            Point3d a = result[^1];
            Point3d b = points[i];
            Point3d c = points[i + 1];

            Vector3d ac = c - a;
            double length = ac.Length;
            if (length <= tolerance)
                continue;

            double deviation = (b - a).CrossProduct(ac).Length / length;
            if (deviation > tolerance * 10.0)
                result.Add(b);
        }

        result.Add(points[^1]);
        return result.ToArray();
    }

    private static Point3d Lerp(Point3d a, Point3d b, double t) =>
        new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));

    /// <summary>Grade XY de triangulos para consultas rapidas.</summary>
    private sealed class TriangleGrid
    {
        private readonly List<Tri> _triangles;
        private readonly Dictionary<(int, int), List<int>> _cells = new();
        private readonly double _originX;
        private readonly double _originY;
        private readonly double _cellSize;

        public TriangleGrid(List<Tri> triangles, Extents2dBox scene)
        {
            _triangles = triangles;
            _originX = scene.MinX;
            _originY = scene.MinY;

            double width = Math.Max(scene.MaxX - scene.MinX, 1e-6);
            double height = Math.Max(scene.MaxY - scene.MinY, 1e-6);
            double area = width * height;

            // ~4 triangulos por celula em media, limitado para cenas grandes.
            double target = Math.Sqrt(area / Math.Max(1, triangles.Count) * 4.0);
            double minCell = Math.Max(width, height) / 2000.0;
            _cellSize = Math.Max(target, minCell);

            for (int i = 0; i < triangles.Count; i++)
            {
                Tri t = triangles[i];
                int x0 = CellX(t.MinX);
                int x1 = CellX(t.MaxX);
                int y0 = CellY(t.MinY);
                int y1 = CellY(t.MaxY);

                for (int x = x0; x <= x1; x++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        if (!_cells.TryGetValue((x, y), out List<int>? list))
                        {
                            list = new List<int>();
                            _cells[(x, y)] = list;
                        }

                        list.Add(i);
                    }
                }
            }
        }

        public double CellSize => _cellSize;

        private int CellX(double x) => (int)Math.Floor((x - _originX) / _cellSize);

        private int CellY(double y) => (int)Math.Floor((y - _originY) / _cellSize);

        private IEnumerable<int> CandidatesInBox(double minX, double minY, double maxX, double maxY)
        {
            HashSet<int> seen = new();
            int x0 = CellX(minX);
            int x1 = CellX(maxX);
            int y0 = CellY(minY);
            int y1 = CellY(maxY);

            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    if (!_cells.TryGetValue((x, y), out List<int>? list))
                        continue;

                    foreach (int index in list)
                    {
                        if (seen.Add(index))
                            yield return index;
                    }
                }
            }
        }

        /// <summary>
        /// Parametros (0..1) em que o segmento PQ cruza, em planta, arestas de
        /// triangulos proximos, ou atravessa o plano de um triangulo.
        /// </summary>
        public List<double> CollectCuts(Point3d p, Point3d q, double xyTolerance)
        {
            List<double> cuts = new() { 0.0, 1.0 };

            double minX = Math.Min(p.X, q.X) - xyTolerance;
            double maxX = Math.Max(p.X, q.X) + xyTolerance;
            double minY = Math.Min(p.Y, q.Y) - xyTolerance;
            double maxY = Math.Max(p.Y, q.Y) + xyTolerance;

            foreach (int index in CandidatesInBox(minX, minY, maxX, maxY))
            {
                Tri t = _triangles[index];
                if (t.MaxX < minX || t.MinX > maxX || t.MaxY < minY || t.MinY > maxY)
                    continue;

                AddCut(cuts, p, q, t.A, t.B);
                AddCut(cuts, p, q, t.B, t.C);
                AddCut(cuts, p, q, t.C, t.A);
                AddPlaneCrossing(cuts, p, q, t);
            }

            cuts.Sort();
            return cuts;
        }

        private static void AddCut(List<double> cuts, Point3d p, Point3d q, Point3d a, Point3d b)
        {
            double rX = q.X - p.X;
            double rY = q.Y - p.Y;
            double sX = b.X - a.X;
            double sY = b.Y - a.Y;

            double denominator = (rX * sY) - (rY * sX);
            if (Math.Abs(denominator) <= 1e-15)
                return;

            double qpX = a.X - p.X;
            double qpY = a.Y - p.Y;

            double t = ((qpX * sY) - (qpY * sX)) / denominator;
            double u = ((qpX * rY) - (qpY * rX)) / denominator;

            if (t > 0.0 && t < 1.0 && u >= -1e-9 && u <= 1.0 + 1e-9)
                cuts.Add(t);
        }

        /// <summary>Onde o segmento atravessa o plano do triangulo (interpenetracao).</summary>
        private static void AddPlaneCrossing(List<double> cuts, Point3d p, Point3d q, Tri t)
        {
            double zp = PlaneZ(t, p);
            double zq = PlaneZ(t, q);
            if (double.IsNaN(zp) || double.IsNaN(zq))
                return;

            double dp = p.Z - zp;
            double dq = q.Z - zq;

            if ((dp > 0 && dq < 0) || (dp < 0 && dq > 0))
            {
                double crossing = dp / (dp - dq);
                if (crossing > 0.0 && crossing < 1.0)
                    cuts.Add(crossing);
            }
        }

        /// <summary>Z do plano do triangulo na posicao XY do ponto (NaN se degenerado).</summary>
        private static double PlaneZ(Tri t, Point3d point)
        {
            if (Math.Abs(t.Area2) <= 1e-18)
                return double.NaN;

            double w1 = (((t.B.X - point.X) * (t.C.Y - point.Y)) - ((t.C.X - point.X) * (t.B.Y - point.Y))) / t.Area2;
            double w2 = (((t.C.X - point.X) * (t.A.Y - point.Y)) - ((t.A.X - point.X) * (t.C.Y - point.Y))) / t.Area2;
            double w3 = 1.0 - w1 - w2;

            return (w1 * t.A.Z) + (w2 * t.B.Z) + (w3 * t.C.Z);
        }

        /// <summary>True se algum triangulo cobre o ponto em planta e fica acima dele.</summary>
        public bool IsCovered(Point3d point, double zTolerance, double xyTolerance)
        {
            foreach (int index in CandidatesInBox(point.X, point.Y, point.X, point.Y))
            {
                Tri t = _triangles[index];
                if (point.X < t.MinX - xyTolerance || point.X > t.MaxX + xyTolerance ||
                    point.Y < t.MinY - xyTolerance || point.Y > t.MaxY + xyTolerance)
                {
                    continue;
                }

                double w1 = (((t.B.X - point.X) * (t.C.Y - point.Y)) - ((t.C.X - point.X) * (t.B.Y - point.Y))) / t.Area2;
                double w2 = (((t.C.X - point.X) * (t.A.Y - point.Y)) - ((t.A.X - point.X) * (t.C.Y - point.Y))) / t.Area2;
                double w3 = 1.0 - w1 - w2;

                const double inside = -1e-9;
                if (w1 < inside || w2 < inside || w3 < inside)
                    continue;

                double z = (w1 * t.A.Z) + (w2 * t.B.Z) + (w3 * t.C.Z);
                if (z > point.Z + zTolerance)
                    return true;
            }

            return false;
        }
    }
}
