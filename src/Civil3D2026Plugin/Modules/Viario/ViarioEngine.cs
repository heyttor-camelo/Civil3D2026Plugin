using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Viario;

// =============================================================================
// Motor da rede viaria (2D, sem dependencia do AutoCAD).
//
// Entrada: eixos (caminhos com retas/arcos, abertos ou fechados) + secao de
// cada eixo + raios especificos (por esquina e por eixo).
// Saida: polilinhas por elemento da secao (bordo da pista, face e costas do
// meio-fio, alinhamento, canteiro central) e a lista de esquinas.
//
// Algoritmo:
// 1. Nos: cruzamentos entre eixos e pontas de eixo encostadas em outro eixo.
// 2. Ramos: em cada no, os trechos de eixo que saem dele, ordenados pelo angulo.
// 3. Esquinas: entre ramos vizinhos, a linha esquerda de um encontra a direita
//    do outro. Setor < 180 graus: concordancia (raio definido na face do
//    meio-fio, demais linhas concentricas). Setor > 180: canto vivo.
// 4. Cada linha e paralela ao eixo (prolongada nas pontas que chegam em nos);
//    as esquinas marcam os trechos a remover; o resto + os arcos sao
//    encadeados em polilinhas.
// 5. Canteiro central: ilhas entre os nos, cortadas antes da via transversal,
//    com nariz semicircular.
// =============================================================================

/// <summary>Secao transversal da via (simetrica).</summary>
internal sealed class RoadSection
{
    public string Id { get; set; } = "S1";
    public string Name { get; set; } = "Local";

    /// <summary>Largura total de rolamento (os dois sentidos, sem o canteiro).</summary>
    public double Pista { get; set; } = 7.0;

    /// <summary>Canteiro central (0 = sem canteiro).</summary>
    public double Canteiro { get; set; }

    /// <summary>Sarjeta de cada lado (0 = sem sarjeta).</summary>
    public double Sarjeta { get; set; } = 0.30;

    /// <summary>Meio-fio de cada lado, da face as costas (0 = sem meio-fio).</summary>
    public double MeioFio { get; set; } = 0.15;

    /// <summary>Calcada de cada lado, apos o meio-fio (0 = sem calcada).</summary>
    public double Calcada { get; set; } = 2.5;

    /// <summary>Raio de esquina medido na face do meio-fio.</summary>
    public double Raio { get; set; } = 6.0;

    public double EdgeOffset(EdgeLevel level)
    {
        double pista = (Canteiro / 2.0) + (Pista / 2.0);
        double face = pista + Sarjeta;
        double costas = face + MeioFio;
        return level switch
        {
            EdgeLevel.Pista => pista,
            EdgeLevel.FaceMF => face,
            EdgeLevel.CostasMF => costas,
            EdgeLevel.Alinhamento => costas + Calcada,
            _ => Canteiro / 2.0
        };
    }

    /// <summary>Se a linha aparece (componente com largura).</summary>
    public bool Shows(EdgeLevel level) => level switch
    {
        EdgeLevel.Pista => Sarjeta > 1e-3,
        EdgeLevel.FaceMF => true,
        EdgeLevel.CostasMF => MeioFio > 1e-3,
        EdgeLevel.Alinhamento => Calcada > 1e-3,
        _ => Canteiro > 1e-3
    };

    public double TotalWidth => 2.0 * EdgeOffset(EdgeLevel.Alinhamento);

    public RoadSection Clone() => new()
    {
        Id = Id, Name = Name, Pista = Pista, Canteiro = Canteiro, Sarjeta = Sarjeta,
        MeioFio = MeioFio, Calcada = Calcada, Raio = Raio
    };
}

internal enum EdgeLevel
{
    Pista = 0,
    FaceMF = 1,
    CostasMF = 2,
    Alinhamento = 3,
    Canteiro = 4
}

internal sealed class AxisInput
{
    public AxisInput(string key, GPath path, RoadSection section, double? radius = null)
    {
        Key = key;
        Path = path;
        Section = section;
        Radius = radius;
    }

    public string Key { get; }
    public GPath Path { get; }
    public RoadSection Section { get; }

    /// <summary>Raio especifico para todas as esquinas deste eixo (null = da secao).</summary>
    public double? Radius { get; }
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

/// <summary>Esquina concava (na face do meio-fio).</summary>
internal sealed class CornerInfo
{
    public string Key { get; init; } = "";
    public V2 Node { get; init; }
    public V2 Corner { get; init; }
    public double Radius { get; init; }
    public Seg? Arc { get; init; }
    public string AxisA { get; init; } = "";
    public string AxisB { get; init; } = "";
}

internal sealed class EngineSettings
{
    /// <summary>Distancia maxima entre a ponta de um eixo e outro eixo para formar no.</summary>
    public double SnapTolerance { get; set; } = 0.05;

    /// <summary>Tolerancia angular para continuidade reta entre eixos (graus).</summary>
    public double StraightToleranceDeg { get; set; } = 1.0;

    /// <summary>Recuo do nariz do canteiro em relacao a pista transversal.</summary>
    public double MedianSetback { get; set; } = 1.0;

    /// <summary>Raios especificos por esquina (chave de CornerInfo).</summary>
    public Dictionary<string, double> CornerRadius { get; set; } = new();
}

internal sealed class ViarioResult
{
    public List<OutputLine> Lines { get; } = new();
    public List<NodeInfo> Nodes { get; } = new();
    public List<CornerInfo> Corners { get; } = new();
    public List<string> Warnings { get; } = new();
}

internal static class ViarioEngine
{
    /// <summary>Saida de diagnostico (somente testes).</summary>
    public static Action<string>? Debug { get; set; }

    public static readonly EdgeLevel[] EdgeLevels =
        { EdgeLevel.Pista, EdgeLevel.FaceMF, EdgeLevel.CostasMF, EdgeLevel.Alinhamento };

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
        public bool Visible;
        public bool Closed;
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
        public Dictionary<(int, int, EdgeLevel), Curve> Curves = new();
        public Dictionary<(int, long), GPath> OffsetCache = new();
        public List<Seg>[] Pieces = Enumerable.Range(0, 5).Select(_ => new List<Seg>()).ToArray();
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

            ProcessNode(ctx, node);
        }

        foreach (EdgeLevel level in EdgeLevels)
        {
            List<List<Seg>> pieces = new();
            foreach (Curve curve in ctx.Curves.Values.Where(c => c.Level == level && c.Visible))
                pieces.AddRange(KeptPieces(curve));

            foreach (Seg seg in ctx.Pieces[(int)level])
                pieces.Add(new List<Seg> { seg });

            foreach ((List<Seg> segs, bool closed) in Chain(pieces))
                result.Lines.Add(new OutputLine(level, segs, closed));
        }

        BuildMedians(ctx, nodes);
        return result;
    }

    /// <summary>Chave de uma esquina: os dois ramos (eixo e sentido), na ordem anti-horaria.</summary>
    public static string CornerKey(string axisA, int dirA, string axisB, int dirB) =>
        axisA + (dirA > 0 ? "+" : "-") + "|" + axisB + (dirB > 0 ? "+" : "-");

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
                    hits.Add((p, new List<(int, double)> { (i, a.Wrap(s1)), (j, b.Wrap(s2)) }));

                AddEndpointHits(hits, i, a, j, b, snap);
                AddEndpointHits(hits, j, b, i, a, snap);
            }
        }

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
                    GPath path = axes[axis].Path;
                    int existing = node.Items.FindIndex(it =>
                        it.Axis == axis &&
                        (Math.Abs(it.S - s) <= merge * 2 ||
                         (path.Closed && Math.Abs(Math.Abs(it.S - s) - path.Length) <= merge * 2)));
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
        if (a.Closed)
            return;

        foreach (double s in new[] { 0.0, a.Length })
        {
            V2 end = a.PointAt(s);
            (double sb, _, double dist) = b.Project(end);
            if (dist <= snap)
                hits.Add((end, new List<(int, double)> { (ia, s), (ib, b.Wrap(sb)) }));
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
                bool closed = path.Closed;
                bool back = closed || s > snap;
                bool forward = closed || s < path.Length - snap;
                V2 tangent = path.TangentAt(Math.Clamp(s, 0, path.Length));

                if (forward)
                    node.Legs.Add(new Leg { Axis = axis, Dir = 1, S = s, Angle = tangent.Angle, Through = back });
                if (back)
                    node.Legs.Add(new Leg { Axis = axis, Dir = -1, S = s, Angle = (-tangent).Angle, Through = forward });
            }

            if (node.Legs.Select(l => l.Axis).Distinct().Count() < 2)
                node.Legs.Clear();

            node.Legs.Sort((a, b) => a.Angle.CompareTo(b.Angle));
        }

        nodes.RemoveAll(n => n.Legs.Count < 2);

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
                GPath path = ctx.Axes[leg.Axis].Path;
                double length = path.Length;

                if (path.Closed)
                {
                    // Distancia ate o proximo no dando a volta.
                    double best = length;
                    foreach (double v in list)
                    {
                        double d = leg.Dir > 0 ? v - leg.S : leg.S - v;
                        d = ((d % length) + length) % length;
                        if (d > snap && d < best)
                            best = d;
                    }

                    leg.Extent = best;
                    leg.ExtentEndsAtNode = true;
                }
                else if (leg.Dir > 0)
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

            // Eixo que passou um pouco do outro: a sobra dentro da pista nao e ramo.
            List<Leg> stubs = node.Legs
                .Where(leg => !leg.ExtentEndsAtNode)
                .Where(leg =>
                {
                    double otherHalf = node.Legs
                        .Where(o => o.Axis != leg.Axis)
                        .Select(o => ctx.Axes[o.Axis].Section.EdgeOffset(EdgeLevel.Pista))
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
    // Linhas paralelas
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
                if (ctx.Axes[axis].Path.Closed)
                    continue;
                if (s <= snap)
                    startAtNode[axis] = true;
                if (s >= ctx.Axes[axis].Path.Length - snap)
                    endAtNode[axis] = true;
            }
        }

        double maxEdge = ctx.Axes.Max(a => a.Section.EdgeOffset(EdgeLevel.Alinhamento));
        double maxR = ctx.Axes.Max(a => Math.Max(a.Section.Raio, a.Radius ?? 0.0));
        if (ctx.Settings.CornerRadius.Count > 0)
            maxR = Math.Max(maxR, ctx.Settings.CornerRadius.Values.Max());
        double ext = (3.0 * maxEdge) + (2.0 * maxR) + 20.0;

        ctx.Ext = new GPath[n];

        for (int i = 0; i < n; i++)
        {
            GPath axis = ctx.Axes[i].Path;
            bool closed = axis.Closed;
            double e0 = !closed && startAtNode[i] ? ext : 0.0;
            double e1 = !closed && endAtNode[i] ? ext : 0.0;
            ctx.Ext[i] = axis.Extended(e0, e1);

            RoadSection section = ctx.Axes[i].Section;
            foreach (EdgeLevel level in EdgeLevels)
            {
                double d = section.EdgeOffset(level);
                foreach (int side in new[] { 1, -1 })
                {
                    GPath p = OffsetOf(ctx, i, side * d);
                    if (p.IsEmpty)
                        continue;

                    ctx.Curves[(i, side, level)] = new Curve
                    {
                        Axis = i,
                        Side = side,
                        Level = level,
                        Visible = section.Shows(level),
                        Closed = closed && p.Closed,
                        P = p,
                        ExtStart = e0 > 0,
                        ExtEnd = e1 > 0,
                        OrigStart = e0 > 0 ? SOnCurve(ctx, p, i, 0.0, side * d) : 0.0,
                        OrigEnd = e1 > 0 ? SOnCurve(ctx, p, i, axis.Length, side * d) : p.Length
                    };
                }
            }
        }
    }

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

    private static double SOnCurve(Ctx ctx, GPath curve, int axis, double s, double signedDistance)
    {
        GPath path = ctx.Axes[axis].Path;
        V2 p = path.PointAt(s) + (path.TangentAt(s).Left * signedDistance);
        return curve.Project(p).S;
    }

    // ------------------------------------------------------------------
    // Esquinas
    // ------------------------------------------------------------------

    private static double ResolveRadius(Ctx ctx, Leg li, Leg lj, string key)
    {
        if (ctx.Settings.CornerRadius.TryGetValue(key, out double specific))
            return Math.Max(0.0, specific);

        AxisInput ai = ctx.Axes[li.Axis];
        AxisInput aj = ctx.Axes[lj.Axis];
        if (ai.Radius is not null || aj.Radius is not null)
            return Math.Max(ai.Radius ?? 0.0, aj.Radius ?? 0.0);

        return Math.Max(ai.Section.Raio, aj.Section.Raio);
    }

    private static void ProcessNode(Ctx ctx, Node node)
    {
        int count = node.Legs.Count;
        double straightTol = ctx.Settings.StraightToleranceDeg * Math.PI / 180.0;

        for (int i = 0; i < count; i++)
        {
            Leg li = node.Legs[i];
            Leg lj = node.Legs[(i + 1) % count];

            double theta = GMath.Norm(lj.Angle - li.Angle);
            if (theta < 1e-9)
                continue;

            int sideI = li.Dir;
            int sideJ = -lj.Dir;

            if (li.Axis == lj.Axis && sideI == sideJ)
                continue;

            string key = CornerKey(ctx.Axes[li.Axis].Key, li.Dir, ctx.Axes[lj.Axis].Key, lj.Dir);
            double faceRadius = ResolveRadius(ctx, li, lj, key);
            bool concave = theta < Math.PI - straightTol;

            foreach (EdgeLevel level in EdgeLevels)
                ProcessCorner(ctx, node, li, lj, sideI, sideJ, theta, concave, level, faceRadius, key);
        }
    }

    private static void ProcessCorner(
        Ctx ctx, Node node, Leg li, Leg lj, int sideI, int sideJ, double theta, bool concave,
        EdgeLevel level, double faceRadius, string key)
    {
        double straightTol = ctx.Settings.StraightToleranceDeg * Math.PI / 180.0;

        if (!ctx.Curves.TryGetValue((li.Axis, sideI, level), out Curve? c1) ||
            !ctx.Curves.TryGetValue((lj.Axis, sideJ, level), out Curve? c2))
        {
            return;
        }

        if (Math.Abs(theta - Math.PI) <= straightTol)
        {
            // Continuacao reta entre eixos diferentes: liga as pontas.
            if (!li.Through && !lj.Through && (c1.Visible || c2.Visible))
            {
                V2 a = c1.P.PointAt(li.Dir > 0 ? c1.OrigStart : c1.OrigEnd);
                V2 b = c2.P.PointAt(lj.Dir > 0 ? c2.OrigStart : c2.OrigEnd);
                if (V2.Dist(a, b) > 1e-6)
                    ctx.Pieces[(int)level].Add(Seg.Line(a, b));
            }

            return;
        }

        RoadSection si = ctx.Axes[li.Axis].Section;
        RoadSection sj = ctx.Axes[lj.Axis].Section;
        double d1 = si.EdgeOffset(level);
        double d2 = sj.EdgeOffset(level);
        double limit = (4.0 * (d1 + d2)) + (2.0 * faceRadius) + 10.0;

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

        Debug?.Invoke($"node {node.Point} {level} {key} theta={theta * 180 / Math.PI:0.#} x={(x is null ? "none" : x.Value.P.ToString())}");

        if (x is null)
        {
            if (level == EdgeLevel.FaceMF)
            {
                ctx.Result.Warnings.Add(
                    $"Esquina sem encontro de bordos perto de {node.Point} ({ctx.Axes[li.Axis].Key} x {ctx.Axes[lj.Axis].Key}).");
            }

            return;
        }

        double radius = 0.0;
        if (concave)
        {
            // Raio na face do meio-fio; demais linhas concentricas.
            double delta = ((si.EdgeOffset(EdgeLevel.FaceMF) - d1) + (sj.EdgeOffset(EdgeLevel.FaceMF) - d2)) / 2.0;
            radius = faceRadius > 1e-6 || delta > 0 ? Math.Max(faceRadius + delta, 0.0) : 0.0;
        }

        double t1 = x.Value.S1;
        double t2 = x.Value.S2;
        Seg? arc = null;
        double usedRadius = 0.0;

        if (radius > 1e-6)
        {
            (bool ok, double r, double s1, double s2, Seg? a) =
                Fillet(ctx, li, lj, sideI, sideJ, d1, d2, c1, c2, x.Value.P, x.Value.S1, x.Value.S2, radius);

            if (ok && a is not null)
            {
                t1 = s1;
                t2 = s2;
                arc = a;
                usedRadius = r;
                if (c1.Visible || c2.Visible)
                    ctx.Pieces[(int)level].Add(a);
                if (r < radius - 1e-6 && level == EdgeLevel.FaceMF)
                {
                    ctx.Result.Warnings.Add(
                        $"Raio de esquina reduzido para {r:0.##} m perto de {node.Point} (nao cabe {radius:0.##} m).");
                }
            }
            else if (level == EdgeLevel.FaceMF)
            {
                ctx.Result.Warnings.Add($"Esquina em canto vivo perto de {node.Point} (o raio nao coube).");
            }
        }

        if (level == EdgeLevel.FaceMF && concave)
        {
            ctx.Result.Corners.Add(new CornerInfo
            {
                Key = key,
                Node = node.Point,
                Corner = x.Value.P,
                Radius = usedRadius,
                Arc = arc,
                AxisA = ctx.Axes[li.Axis].Key,
                AxisB = ctx.Axes[lj.Axis].Key
            });
        }

        AddRemoval(ctx, c1, li, t1);
        AddRemoval(ctx, c2, lj, t2);
    }

    private static double SignedAlong(Curve curve, double from, double to, int dir)
    {
        double delta = (to - from) * dir;
        if (curve.Closed)
        {
            double length = curve.P.Length;
            delta = ((delta % length) + length) % length;
            if (delta > length / 2.0)
                delta -= length;
        }

        return delta;
    }

    private static (bool Ok, double Radius, double S1, double S2, Seg? Arc) Fillet(
        Ctx ctx, Leg li, Leg lj, int sideI, int sideJ, double d1, double d2,
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

            if (SignedAlong(c1, sx1, s1, li.Dir) < -1e-6 || SignedAlong(c2, sx2, s2, lj.Dir) < -1e-6)
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

    private static bool FitsInLeg(Ctx ctx, Leg leg, V2 tangentPoint)
    {
        GPath axis = ctx.Axes[leg.Axis].Path;
        double s = axis.Project(tangentPoint).S;
        double along = (s - leg.S) * leg.Dir;
        if (axis.Closed)
            along = ((along % axis.Length) + axis.Length) % axis.Length;
        double available = leg.ExtentEndsAtNode ? leg.Extent / 2.0 : leg.Extent;
        return along <= available + 1e-6;
    }

    private static void AddRemoval(Ctx ctx, Curve curve, Leg leg, double sT)
    {
        double length = curve.P.Length;

        if (leg.Through)
        {
            double sNode = SOnCurve(ctx, curve.P, leg.Axis, leg.S,
                curve.Side * ctx.Axes[leg.Axis].Section.EdgeOffset(curve.Level));

            if (curve.Closed)
            {
                // Do no ate a tangencia, no sentido do ramo (pode dar a volta).
                double from = leg.Dir > 0 ? sNode : sT;
                double to = leg.Dir > 0 ? sT : sNode;
                if (from <= to)
                {
                    curve.Removals.Add((from, to));
                }
                else
                {
                    curve.Removals.Add((from, length));
                    curve.Removals.Add((0.0, to));
                }
            }
            else
            {
                curve.Removals.Add((Math.Min(sNode, sT), Math.Max(sNode, sT)));
            }
        }
        else if (leg.Dir > 0)
        {
            curve.Removals.Add((0.0, sT));
        }
        else
        {
            curve.Removals.Add((sT, length));
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

        if (curve.Closed && removals.Count == 0)
        {
            yield return curve.P.Segs.ToList();
            yield break;
        }

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

        // Fechado: o trecho que cruza o ponto inicial vira um so.
        if (curve.Closed && kept.Count >= 2 && kept[0].A <= 1e-9 && kept[^1].B >= length - 1e-9)
        {
            List<Seg> joined = curve.P.Sub(kept[^1].A, length).Segs.ToList();
            joined.AddRange(curve.P.Sub(0, kept[0].B).Segs);
            kept.RemoveAt(kept.Count - 1);
            kept.RemoveAt(0);
            if (joined.Count > 0)
                yield return joined;
        }

        foreach ((double a0, double b0) in kept)
        {
            double a = a0, b = b0;

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

            while (FindAt(chain[^1].B) is (int piece, int end))
            {
                used[piece] = true;
                chain.AddRange(end == 0 ? list[piece] : Reversed(list[piece]));
            }

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

    // ------------------------------------------------------------------
    // Canteiro central
    // ------------------------------------------------------------------

    private static void BuildMedians(Ctx ctx, List<Node> nodes)
    {
        for (int i = 0; i < ctx.Axes.Count; i++)
        {
            AxisInput axis = ctx.Axes[i];
            RoadSection section = axis.Section;
            if (!section.Shows(EdgeLevel.Canteiro) || axis.Path.Closed)
                continue;

            double half = section.Canteiro / 2.0;
            double length = axis.Path.Length;
            List<(double A, double B)> blocked = new();

            foreach (Node node in nodes)
            {
                foreach ((int ax, double s) in node.Items.Where(it => it.Axis == i))
                {
                    V2 t = axis.Path.TangentAt(Math.Clamp(s, 0, length));
                    double cut = 0.0;

                    foreach (Leg other in node.Legs.Where(l => l.Axis != i))
                    {
                        double sin = Math.Abs(V2.Cross(t, V2.FromAngle(other.Angle)));
                        double cos = Math.Abs(V2.Dot(t, V2.FromAngle(other.Angle)));
                        sin = Math.Max(sin, 0.1);
                        double d = ctx.Axes[other.Axis].Section.EdgeOffset(EdgeLevel.Pista);
                        cut = Math.Max(cut, (d / sin) + (half * cos / sin) + ctx.Settings.MedianSetback);
                    }

                    blocked.Add((s - cut, s + cut));
                }
            }

            // Trechos livres entre os bloqueios.
            List<(double A, double B)> free = new();
            double cursor = 0.0;
            foreach ((double a, double b) in blocked.OrderBy(b => b.A))
            {
                if (a > cursor)
                    free.Add((cursor, a));
                cursor = Math.Max(cursor, b);
            }

            if (cursor < length)
                free.Add((cursor, length));

            GPath left = OffsetOf(ctx, i, half);
            GPath right = OffsetOf(ctx, i, -half);

            foreach ((double a, double b) in free)
            {
                if (b - a < (2.0 * half) + 0.5)
                    continue;

                double s0 = a + half;
                double s1 = b - half;
                double l0 = SOnCurve(ctx, left, i, s0, half);
                double l1 = SOnCurve(ctx, left, i, s1, half);
                double r0 = SOnCurve(ctx, right, i, s0, -half);
                double r1 = SOnCurve(ctx, right, i, s1, -half);

                GPath leftPart = left.Sub(Math.Min(l0, l1), Math.Max(l0, l1));
                GPath rightPart = right.Sub(Math.Min(r0, r1), Math.Max(r0, r1));
                if (leftPart.IsEmpty || rightPart.IsEmpty)
                    continue;

                List<Seg> ring = new(leftPart.Segs);
                ring.Add(new Seg(leftPart.End, rightPart.End, -1.0));   // nariz final
                ring.AddRange(rightPart.Reverse().Segs);
                ring.Add(new Seg(rightPart.Start, leftPart.Start, -1.0)); // nariz inicial
                ctx.Result.Lines.Add(new OutputLine(EdgeLevel.Canteiro, ring, closed: true));
            }
        }
    }

    public static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
