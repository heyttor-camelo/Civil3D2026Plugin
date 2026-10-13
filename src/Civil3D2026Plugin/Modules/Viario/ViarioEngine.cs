using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Viario;

// =============================================================================
// Motor da rede viaria (2D, sem dependencia do AutoCAD).
//
// Entrada: eixos (caminhos com retas/arcos) + perfil de cada eixo.
// Saida: polilinhas de BORDO (meio-fio) e de ALINHAMENTO (fim da calcada).
//
// Algoritmo:
// 1. Nos: cruzamentos entre eixos e pontas de eixo encostadas em outro eixo
//    (tolerancia de captura), agrupados por proximidade.
// 2. Ramos: em cada no, os trechos de eixo que saem dele, ordenados pelo angulo.
// 3. Cantos: entre cada par de ramos vizinhos, o bordo esquerdo de um e o
//    direito do outro se encontram. Canto concavo (setor &lt; 180 graus):
//    concordancia com raio de esquina (alinhamento concentrico). Setor
//    &gt; 180 graus: canto vivo (prolonga ate a intersecao).
// 4. Cada bordo e um paralelo do eixo (prolongado nas pontas que chegam em
//    nos); os cantos marcam os trechos a remover. O que sobra + os arcos de
//    concordancia sao encadeados em polilinhas.
// =============================================================================

internal sealed class RoadProfile
{
    public string Id { get; set; } = "P1";
    public string Name { get; set; } = "Local";

    /// <summary>Largura total da pista (m).</summary>
    public double Pista { get; set; } = 7.0;

    /// <summary>Largura da calcada de cada lado (m).</summary>
    public double Calcada { get; set; } = 2.5;

    /// <summary>Raio de esquina no meio-fio (m).</summary>
    public double Raio { get; set; } = 6.0;

    public double EdgeOffset(EdgeLevel level) =>
        level == EdgeLevel.Bordo ? Pista / 2.0 : (Pista / 2.0) + Calcada;

    public bool HasLevel(EdgeLevel level) => level == EdgeLevel.Bordo || Calcada > 1e-3;

    public RoadProfile Clone() => new() { Id = Id, Name = Name, Pista = Pista, Calcada = Calcada, Raio = Raio };
}

internal enum EdgeLevel
{
    Bordo = 0,
    Alinhamento = 1
}

internal sealed class AxisInput
{
    public AxisInput(string key, GPath path, RoadProfile profile)
    {
        Key = key;
        Path = path;
        Profile = profile;
    }

    public string Key { get; }
    public GPath Path { get; }
    public RoadProfile Profile { get; }
}

internal sealed class OutputLine
{
    public OutputLine(EdgeLevel level, List<Seg> segs, bool closed)
    {
        Level = level;
        Segs = segs;
        Closed = closed;
    }

    public EdgeLevel Level { get; }
    public List<Seg> Segs { get; }
    public bool Closed { get; }
}

internal sealed class NodeInfo
{
    public NodeInfo(V2 point, int legCount, IReadOnlyList<string> axisKeys)
    {
        Point = point;
        LegCount = legCount;
        AxisKeys = axisKeys;
    }

    public V2 Point { get; }
    public int LegCount { get; }
    public IReadOnlyList<string> AxisKeys { get; }
}

internal sealed class EngineSettings
{
    /// <summary>Distancia maxima entre a ponta de um eixo e outro eixo para formar no (m).</summary>
    public double SnapTolerance { get; set; } = 0.05;

    /// <summary>Tolerancia angular para continuidade reta entre eixos (graus).</summary>
    public double StraightToleranceDeg { get; set; } = 1.0;
}

internal sealed class ViarioResult
{
    public List<OutputLine> Lines { get; } = new();
    public List<NodeInfo> Nodes { get; } = new();
    public List<string> Warnings { get; } = new();
}

internal static class ViarioEngine
{
    /// <summary>Saida de diagnostico (somente testes).</summary>
    public static Action<string>? Debug { get; set; }

    private sealed class Leg
    {
        public int Axis;
        public int Dir;          // +1 = no sentido do eixo, -1 = contra
        public double S;         // posicao do no no eixo
        public double Angle;     // direcao de saida
        public bool Through;     // o eixo continua do outro lado do no
        public double Extent;    // comprimento do ramo ate o proximo no/ponta
        public bool ExtentEndsAtNode;
    }

    private sealed class Node
    {
        public V2 Point;
        public List<(int Axis, double S)> Items = new();
        public List<Leg> Legs = new();
    }

    private sealed class Curve
    {
        public int Axis;
        public int Side;
        public EdgeLevel Level;
        public GPath P = null!;
        public double OrigStart;
        public double OrigEnd;
        public bool ExtStart;
        public bool ExtEnd;
        public List<(double A, double B)> Removals = new();
    }

    private sealed class Ctx
    {
        public IReadOnlyList<AxisInput> Axes = null!;
        public EngineSettings Settings = null!;
        public GPath[] Ext = null!;
        public double[] ExtStartLen = null!;
        public Dictionary<(int, int, EdgeLevel), Curve> Curves = new();
        public Dictionary<(int, long), GPath> OffsetCache = new();
        public List<Seg>[] Pieces = { new(), new() };
        public ViarioResult Result = null!;
    }

    public static ViarioResult Compute(IReadOnlyList<AxisInput> axes, EngineSettings settings)
    {
        ViarioResult result = new();
        List<AxisInput> valid = axes.Where(a => !a.Path.IsEmpty && a.Path.Length > 1e-6).ToList();
        if (valid.Count == 0)
            return result;

        Ctx ctx = new() { Axes = valid, Settings = settings, Result = result };

        List<Node> nodes = FindNodes(ctx);
        BuildLegs(ctx, nodes);
        BuildCurves(ctx, nodes);

        foreach (Node node in nodes)
        {
            result.Nodes.Add(new NodeInfo(
                node.Point,
                node.Legs.Count,
                node.Items.Select(i => valid[i.Axis].Key).Distinct().ToList()));

            foreach (EdgeLevel level in new[] { EdgeLevel.Bordo, EdgeLevel.Alinhamento })
                ProcessCorners(ctx, node, level);
        }

        foreach (EdgeLevel level in new[] { EdgeLevel.Bordo, EdgeLevel.Alinhamento })
        {
            List<List<Seg>> pieces = new();
            foreach (Curve curve in ctx.Curves.Values.Where(c => c.Level == level))
                pieces.AddRange(KeptPieces(curve));

            foreach (Seg seg in ctx.Pieces[(int)level])
                pieces.Add(new List<Seg> { seg });

            foreach ((List<Seg> segs, bool closed) in Chain(pieces))
                result.Lines.Add(new OutputLine(level, segs, closed));
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Nos
    // ------------------------------------------------------------------

    private static List<Node> FindNodes(Ctx ctx)
    {
        IReadOnlyList<AxisInput> axes = ctx.Axes;
        double snap = Math.Max(ctx.Settings.SnapTolerance, 1e-4);
        List<(V2 P, List<(int, double)> Items)> hits = new();
        Box2[] boxes = axes.Select(a => a.Path.Bounds()).ToArray();

        for (int i = 0; i < axes.Count; i++)
        {
            for (int j = i + 1; j < axes.Count; j++)
            {
                if (!boxes[i].Inflate(snap).Intersects(boxes[j]))
                    continue;

                GPath a = axes[i].Path;
                GPath b = axes[j].Path;

                foreach ((double s1, double s2, V2 p) in a.Intersections(b))
                    hits.Add((p, new List<(int, double)> { (i, s1), (j, s2) }));

                AddEndpointHits(hits, i, a, j, b, snap);
                AddEndpointHits(hits, j, b, i, a, snap);
            }
        }

        // Agrupa por proximidade (uniao simples).
        double merge = Math.Max(snap, 1e-3);
        int[] parent = Enumerable.Range(0, hits.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);

        for (int i = 0; i < hits.Count; i++)
        {
            for (int j = i + 1; j < hits.Count; j++)
            {
                if (V2.Dist(hits[i].P, hits[j].P) <= merge)
                    parent[Find(i)] = Find(j);
            }
        }

        List<Node> nodes = new();
        foreach (IGrouping<int, int> group in Enumerable.Range(0, hits.Count).GroupBy(Find))
        {
            Node node = new();
            double x = 0, y = 0;
            int count = 0;

            foreach (int h in group)
            {
                x += hits[h].P.X;
                y += hits[h].P.Y;
                count++;

                foreach ((int axis, double s) in hits[h].Items)
                {
                    int existing = node.Items.FindIndex(it => it.Axis == axis && Math.Abs(it.S - s) <= merge * 2);
                    if (existing < 0)
                        node.Items.Add((axis, s));
                }
            }

            node.Point = new V2(x / count, y / count);
            nodes.Add(node);
        }

        return nodes;
    }

    private static void AddEndpointHits(
        List<(V2 P, List<(int, double)> Items)> hits, int ia, GPath a, int ib, GPath b, double snap)
    {
        foreach (double s in new[] { 0.0, a.Length })
        {
            V2 end = a.PointAt(s);
            (double sb, _, double dist) = b.Project(end);
            if (dist <= snap)
                hits.Add((end, new List<(int, double)> { (ia, s), (ib, sb) }));
        }
    }

    private static void BuildLegs(Ctx ctx, List<Node> nodes)
    {
        double snap = Math.Max(ctx.Settings.SnapTolerance, 1e-4);

        foreach (Node node in nodes)
        {
            foreach ((int axis, double s) in node.Items)
            {
                GPath path = ctx.Axes[axis].Path;
                bool back = s > snap;
                bool forward = s < path.Length - snap;
                V2 tangent = path.TangentAt(Math.Clamp(s, 0, path.Length));

                if (forward)
                    node.Legs.Add(new Leg { Axis = axis, Dir = 1, S = s, Angle = tangent.Angle, Through = back });
                if (back)
                    node.Legs.Add(new Leg { Axis = axis, Dir = -1, S = s, Angle = (-tangent).Angle, Through = forward });
            }

            // So interessa no com eixos diferentes.
            if (node.Legs.Select(l => l.Axis).Distinct().Count() < 2)
                node.Legs.Clear();

            node.Legs.Sort((a, b) => a.Angle.CompareTo(b.Angle));
        }

        nodes.RemoveAll(n => n.Legs.Count < 2);

        // Comprimento de cada ramo ate o proximo no do mesmo eixo.
        Dictionary<int, List<double>> stations = new();
        foreach (Node node in nodes)
        {
            foreach ((int axis, double s) in node.Items)
            {
                if (!stations.TryGetValue(axis, out List<double>? list))
                    stations[axis] = list = new List<double>();
                list.Add(s);
            }
        }

        foreach (Node node in nodes)
        {
            foreach (Leg leg in node.Legs)
            {
                List<double> list = stations[leg.Axis];
                double length = ctx.Axes[leg.Axis].Path.Length;

                if (leg.Dir > 0)
                {
                    double next = list.Where(v => v > leg.S + snap).DefaultIfEmpty(double.NaN).Min();
                    leg.ExtentEndsAtNode = !double.IsNaN(next);
                    leg.Extent = (leg.ExtentEndsAtNode ? next : length) - leg.S;
                }
                else
                {
                    double prev = list.Where(v => v < leg.S - snap).DefaultIfEmpty(double.NaN).Max();
                    leg.ExtentEndsAtNode = !double.IsNaN(prev);
                    leg.Extent = leg.S - (leg.ExtentEndsAtNode ? prev : 0.0);
                }
            }

            // Eixo que passou um pouco do outro (sem snap): a sobra que fica
            // dentro da pista do outro eixo nao e um ramo.
            List<Leg> stubs = node.Legs
                .Where(leg => !leg.ExtentEndsAtNode)
                .Where(leg =>
                {
                    double otherHalf = node.Legs
                        .Where(o => o.Axis != leg.Axis)
                        .Select(o => ctx.Axes[o.Axis].Profile.Pista / 2.0)
                        .DefaultIfEmpty(0.0)
                        .Max();
                    return leg.Extent < otherHalf;
                })
                .ToList();

            foreach (Leg stub in stubs)
            {
                node.Legs.Remove(stub);
                foreach (Leg other in node.Legs.Where(o => o.Axis == stub.Axis && Math.Abs(o.S - stub.S) < 1e-9))
                    other.Through = false;
            }
        }

        nodes.RemoveAll(n => n.Legs.Count < 2 || n.Legs.Select(l => l.Axis).Distinct().Count() < 2);
    }

    // ------------------------------------------------------------------
    // Bordos (paralelas dos eixos)
    // ------------------------------------------------------------------

    private static void BuildCurves(Ctx ctx, List<Node> nodes)
    {
        int n = ctx.Axes.Count;
        double snap = Math.Max(ctx.Settings.SnapTolerance, 1e-4);
        bool[] startAtNode = new bool[n];
        bool[] endAtNode = new bool[n];

        foreach (Node node in nodes)
        {
            foreach ((int axis, double s) in node.Items)
            {
                if (s <= snap)
                    startAtNode[axis] = true;
                if (s >= ctx.Axes[axis].Path.Length - snap)
                    endAtNode[axis] = true;
            }
        }

        double maxEdge = ctx.Axes.Max(a => a.Profile.EdgeOffset(EdgeLevel.Alinhamento));
        double maxR = ctx.Axes.Max(a => a.Profile.Raio);
        double ext = (3.0 * maxEdge) + (2.0 * maxR) + 20.0;

        ctx.Ext = new GPath[n];
        ctx.ExtStartLen = new double[n];

        for (int i = 0; i < n; i++)
        {
            GPath axis = ctx.Axes[i].Path;
            double e0 = startAtNode[i] ? ext : 0.0;
            double e1 = endAtNode[i] ? ext : 0.0;
            ctx.Ext[i] = axis.Extended(e0, e1);
            ctx.ExtStartLen[i] = e0;

            foreach (EdgeLevel level in new[] { EdgeLevel.Bordo, EdgeLevel.Alinhamento })
            {
                RoadProfile profile = ctx.Axes[i].Profile;
                if (!profile.HasLevel(level))
                    continue;

                double d = profile.EdgeOffset(level);
                foreach (int side in new[] { 1, -1 })
                {
                    GPath p = OffsetOf(ctx, i, side * d);
                    if (p.IsEmpty)
                        continue;

                    Curve curve = new()
                    {
                        Axis = i,
                        Side = side,
                        Level = level,
                        P = p,
                        ExtStart = e0 > 0,
                        ExtEnd = e1 > 0,
                        OrigStart = e0 > 0 ? SOnCurve(ctx, p, i, 0.0, side * d) : 0.0,
                        OrigEnd = e1 > 0 ? SOnCurve(ctx, p, i, axis.Length, side * d) : p.Length
                    };

                    ctx.Curves[(i, side, level)] = curve;
                }
            }
        }
    }

    /// <summary>Paralela do eixo prolongado a distancia com sinal (cache).</summary>
    private static GPath OffsetOf(Ctx ctx, int axis, double signedDistance)
    {
        long key = (long)Math.Round(signedDistance * 1e6);
        if (!ctx.OffsetCache.TryGetValue((axis, key), out GPath? path))
        {
            path = ctx.Ext[axis].Offset(signedDistance);
            ctx.OffsetCache[(axis, key)] = path;
        }

        return path;
    }

    /// <summary>Posicao, no bordo, correspondente a estaca s do eixo original.</summary>
    private static double SOnCurve(Ctx ctx, GPath curve, int axis, double s, double signedDistance)
    {
        GPath path = ctx.Axes[axis].Path;
        V2 p = path.PointAt(s) + (path.TangentAt(s).Left * signedDistance);
        return curve.Project(p).S;
    }

    // ------------------------------------------------------------------
    // Cantos
    // ------------------------------------------------------------------

    private static void ProcessCorners(Ctx ctx, Node node, EdgeLevel level)
    {
        int count = node.Legs.Count;
        double straightTol = ctx.Settings.StraightToleranceDeg * Math.PI / 180.0;

        for (int i = 0; i < count; i++)
        {
            Leg li = node.Legs[i];
            Leg lj = node.Legs[(i + 1) % count];

            double theta = GMath.Norm(lj.Angle - li.Angle);
            if (count == 1 || theta < 1e-9)
                continue;

            int sideI = li.Dir;   // esquerda do ramo i
            int sideJ = -lj.Dir;  // direita do ramo j

            // Mesmo eixo passando pelo no: o bordo desse lado e continuo.
            if (li.Axis == lj.Axis && sideI == sideJ)
                continue;

            if (!ctx.Curves.TryGetValue((li.Axis, sideI, level), out Curve? c1) ||
                !ctx.Curves.TryGetValue((lj.Axis, sideJ, level), out Curve? c2))
            {
                continue;
            }

            if (Math.Abs(theta - Math.PI) <= straightTol)
            {
                // Continuacao reta entre eixos diferentes: liga as pontas.
                if (!li.Through && !lj.Through)
                {
                    V2 a = c1.P.PointAt(li.Dir > 0 ? c1.OrigStart : c1.OrigEnd);
                    V2 b = c2.P.PointAt(lj.Dir > 0 ? c2.OrigStart : c2.OrigEnd);
                    if (V2.Dist(a, b) > 1e-6)
                        ctx.Pieces[(int)level].Add(Seg.Line(a, b));
                }

                continue;
            }

            RoadProfile pi = ctx.Axes[li.Axis].Profile;
            RoadProfile pj = ctx.Axes[lj.Axis].Profile;
            double d1 = pi.EdgeOffset(level);
            double d2 = pj.EdgeOffset(level);
            double limit = (4.0 * (d1 + d2)) + (2.0 * Math.Max(pi.Raio, pj.Raio)) + 10.0;

            // Intersecao dos bordos mais proxima do no.
            (double S1, double S2, V2 P)? x = null;
            double best = double.MaxValue;
            foreach ((double s1, double s2, V2 p) in c1.P.Intersections(c2.P))
            {
                double dist = V2.Dist(p, node.Point);
                if (dist < best && dist <= limit)
                {
                    best = dist;
                    x = (s1, s2, p);
                }
            }

            if (Debug is not null)
                Debug($"node {node.Point} lvl {level} Li=({li.Axis},{li.Dir},{li.Angle * 180 / Math.PI:0.#}) Lj=({lj.Axis},{lj.Dir},{lj.Angle * 180 / Math.PI:0.#}) theta={theta * 180 / Math.PI:0.#} x={(x is null ? "none" : x.Value.P.ToString())} nInt={c1.P.Intersections(c2.P).Count}");

            if (x is null)
            {
                ctx.Result.Warnings.Add(
                    $"Esquina sem encontro de bordos perto de {node.Point} ({ctx.Axes[li.Axis].Key} x {ctx.Axes[lj.Axis].Key}).");
                continue;
            }

            bool concave = theta < Math.PI;
            double radius = 0.0;
            if (concave)
            {
                double baseRadius = Math.Max(pi.Raio, pj.Raio);
                radius = level == EdgeLevel.Bordo
                    ? baseRadius
                    : Math.Max(baseRadius - ((pi.Calcada + pj.Calcada) / 2.0), 0.0);
            }

            double t1 = x.Value.S1;
            double t2 = x.Value.S2;

            if (radius > 1e-6)
            {
                (bool ok, double r, double s1, double s2, Seg? arc) =
                    Fillet(ctx, node, li, lj, sideI, sideJ, d1, d2, c1, c2, x.Value.P, x.Value.S1, x.Value.S2, radius);

                if (ok && arc is not null)
                {
                    t1 = s1;
                    t2 = s2;
                    ctx.Pieces[(int)level].Add(arc);
                    if (r < radius - 1e-6 && level == EdgeLevel.Bordo)
                    {
                        ctx.Result.Warnings.Add(
                            $"Raio de esquina reduzido para {r:0.##} m perto de {node.Point} (nao cabe {radius:0.##} m).");
                    }
                }
                else if (level == EdgeLevel.Bordo)
                {
                    ctx.Result.Warnings.Add($"Esquina em canto vivo perto de {node.Point} (o raio nao coube).");
                }
            }

            AddRemoval(ctx, c1, li, node, t1);
            AddRemoval(ctx, c2, lj, node, t2);
        }
    }

    private static (bool Ok, double Radius, double S1, double S2, Seg? Arc) Fillet(
        Ctx ctx, Node node, Leg li, Leg lj, int sideI, int sideJ, double d1, double d2,
        Curve c1, Curve c2, V2 corner, double sx1, double sx2, double radius)
    {
        double r = radius;

        for (int attempt = 0; attempt < 8; attempt++, r *= 0.6)
        {
            GPath o1 = OffsetOf(ctx, li.Axis, sideI * (d1 + r));
            GPath o2 = OffsetOf(ctx, lj.Axis, sideJ * (d2 + r));

            V2? center = null;
            double best = double.MaxValue;
            foreach ((_, _, V2 p) in o1.Intersections(o2))
            {
                double dist = V2.Dist(p, corner);
                if (dist < best && dist <= (3.0 * r) + d1 + d2 + 5.0)
                {
                    best = dist;
                    center = p;
                }
            }

            if (center is not V2 o)
                continue;

            (double s1, V2 p1, double dist1) = c1.P.Project(o);
            (double s2, V2 p2, double dist2) = c2.P.Project(o);

            double tol = Math.Max(1e-3, r * 1e-4);
            if (Math.Abs(dist1 - r) > tol || Math.Abs(dist2 - r) > tol)
                continue;

            // Tangencia do lado do ramo (afastando-se do no).
            if ((s1 - sx1) * li.Dir < -1e-6 || (s2 - sx2) * lj.Dir < -1e-6)
                continue;

            if (!FitsInLeg(ctx, li, p1) || !FitsInLeg(ctx, lj, p2))
                continue;

            double a0 = (p1 - o).Angle;
            double a1 = (p2 - o).Angle;
            double sweep = GMath.Norm(a1 - a0);
            if (sweep > Math.PI)
                sweep -= GMath.TwoPi;

            return (true, r, s1, s2, new Seg(p1, p2, Math.Tan(sweep / 4.0)));
        }

        return (false, 0.0, sx1, sx2, null);
    }

    /// <summary>O ponto de tangencia precisa ficar no proprio ramo (metade, se o ramo termina em outro no).</summary>
    private static bool FitsInLeg(Ctx ctx, Leg leg, V2 tangentPoint)
    {
        GPath axis = ctx.Axes[leg.Axis].Path;
        double s = axis.Project(tangentPoint).S;
        double along = (s - leg.S) * leg.Dir;
        double available = leg.ExtentEndsAtNode ? leg.Extent / 2.0 : leg.Extent;
        return along <= available + 1e-6;
    }

    private static void AddRemoval(Ctx ctx, Curve curve, Leg leg, Node node, double sT)
    {
        if (leg.Through)
        {
            double sNode = SOnCurve(ctx, curve.P, leg.Axis, leg.S, curve.Side * ctx.Axes[leg.Axis].Profile.EdgeOffset(curve.Level));
            curve.Removals.Add((Math.Min(sNode, sT), Math.Max(sNode, sT)));
        }
        else if (leg.Dir > 0)
        {
            // O eixo comeca no no: remove do inicio (prolongamento) ate a tangencia.
            curve.Removals.Add((0.0, sT));
        }
        else
        {
            curve.Removals.Add((sT, curve.P.Length));
        }
    }

    // ------------------------------------------------------------------
    // Trechos mantidos e encadeamento
    // ------------------------------------------------------------------

    private static IEnumerable<List<Seg>> KeptPieces(Curve curve)
    {
        double length = curve.P.Length;
        List<(double A, double B)> removals = curve.Removals
            .Select(r => (Math.Max(0, r.A), Math.Min(length, r.B)))
            .Where(r => r.Item2 > r.Item1)
            .OrderBy(r => r.Item1)
            .ToList();

        List<(double A, double B)> kept = new();
        double cursor = 0.0;
        foreach ((double a, double b) in removals)
        {
            if (a > cursor)
                kept.Add((cursor, a));
            cursor = Math.Max(cursor, b);
        }

        if (cursor < length)
            kept.Add((cursor, length));

        foreach ((double a0, double b0) in kept)
        {
            double a = a0, b = b0;

            // Prolongamentos que nenhum canto usou nao entram no desenho.
            if (curve.ExtStart && a <= 1e-9)
                a = Math.Max(a, curve.OrigStart);
            if (curve.ExtEnd && b >= length - 1e-9)
                b = Math.Min(b, curve.OrigEnd);

            if (b - a > 1e-6)
                yield return curve.P.Sub(a, b).Segs.ToList();
        }
    }

    private static List<(List<Seg> Segs, bool Closed)> Chain(List<List<Seg>> pieces)
    {
        const double tol = 1e-4;
        const double cell = 0.01;

        List<List<Seg>> list = pieces.Where(p => p.Count > 0).ToList();
        bool[] used = new bool[list.Count];

        // Indice espacial das pontas: (peca, 0 = inicio / 1 = fim).
        Dictionary<(long, long), List<(int Piece, int End)>> index = new();
        (long, long) Key(V2 p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));

        for (int i = 0; i < list.Count; i++)
        {
            foreach ((V2 p, int end) in new[] { (list[i][0].A, 0), (list[i][^1].B, 1) })
            {
                (long, long) key = Key(p);
                if (!index.TryGetValue(key, out List<(int, int)>? bucket))
                    index[key] = bucket = new List<(int, int)>();
                bucket.Add((i, end));
            }
        }

        (int Piece, int End)? FindAt(V2 p)
        {
            (long kx, long ky) = Key(p);
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!index.TryGetValue((kx + dx, ky + dy), out List<(int Piece, int End)>? bucket))
                        continue;

                    foreach ((int piece, int end) in bucket)
                    {
                        if (used[piece])
                            continue;

                        V2 q = end == 0 ? list[piece][0].A : list[piece][^1].B;
                        if (V2.Dist(p, q) < tol)
                            return (piece, end);
                    }
                }
            }

            return null;
        }

        List<(List<Seg>, bool)> result = new();

        for (int i = 0; i < list.Count; i++)
        {
            if (used[i])
                continue;

            used[i] = true;
            List<Seg> chain = new(list[i]);

            // Para frente.
            while (FindAt(chain[^1].B) is (int piece, int end))
            {
                used[piece] = true;
                chain.AddRange(end == 0 ? list[piece] : Reversed(list[piece]));
            }

            // Para tras.
            List<Seg> front = new();
            while (FindAt(front.Count > 0 ? front[0].A : chain[0].A) is (int piece, int end))
            {
                used[piece] = true;
                front.InsertRange(0, end == 1 ? list[piece] : Reversed(list[piece]));
            }

            if (front.Count > 0)
                chain.InsertRange(0, front);

            bool closed = chain.Count > 1 && V2.Dist(chain[0].A, chain[^1].B) < tol;
            result.Add((chain, closed));
        }

        return result;
    }

    private static List<Seg> Reversed(List<Seg> segs) =>
        segs.AsEnumerable().Reverse().Select(s => s.Reverse()).ToList();
}
