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

    /// <summary>Lado esquerdo = +1 (a esquerda do sentido do eixo), direito = -1.</summary>
    public const int Esquerda = 1;
    public const int Direita = -1;

    /// <summary>Em que lados existe sarjeta (padrao: os dois).</summary>
    public Lados LadosSarjeta { get; set; } = Lados.Ambos;

    /// <summary>Em que lados existe meio-fio (padrao: os dois).</summary>
    public Lados LadosMeioFio { get; set; } = Lados.Ambos;

    /// <summary>Em que lados existe calcada (padrao: os dois).</summary>
    public Lados LadosCalcada { get; set; } = Lados.Ambos;

    public static bool Tem(Lados lados, int side) =>
        side >= 0 ? (lados & Lados.Esquerda) != 0 : (lados & Lados.Direita) != 0;

    public double SarjetaEm(int side) => Tem(LadosSarjeta, side) ? Sarjeta : 0.0;
    public double MeioFioEm(int side) => Tem(LadosMeioFio, side) ? MeioFio : 0.0;
    public double CalcadaEm(int side) => Tem(LadosCalcada, side) ? Calcada : 0.0;

    /// <summary>Lados iguais (sarjeta, meio-fio e calcada dos dois lados).</summary>
    public bool Simetrica => LadosSarjeta == Lados.Ambos && LadosMeioFio == Lados.Ambos && LadosCalcada == Lados.Ambos;

    /// <summary>Afastamento do eixo ate a linha, no lado dado (+1 esquerda, -1 direita).</summary>
    public double EdgeOffset(EdgeLevel level, int side)
    {
        double pista = (Canteiro / 2.0) + (Pista / 2.0);
        double face = pista + SarjetaEm(side);
        double costas = face + MeioFioEm(side);
        return level switch
        {
            EdgeLevel.Pista => pista,
            EdgeLevel.FaceMF => face,
            EdgeLevel.CostasMF => costas,
            EdgeLevel.Alinhamento => costas + CalcadaEm(side),
            _ => Canteiro / 2.0
        };
    }

    /// <summary>Afastamento nominal (o maior dos dois lados), para limites e distancias.</summary>
    public double EdgeOffset(EdgeLevel level) =>
        Math.Max(EdgeOffset(level, Esquerda), EdgeOffset(level, Direita));

    /// <summary>Se a linha aparece no lado dado (componente com largura nesse lado).</summary>
    public bool Shows(EdgeLevel level, int side) => level switch
    {
        EdgeLevel.Pista => SarjetaEm(side) > 1e-3,
        EdgeLevel.FaceMF => true,
        EdgeLevel.CostasMF => MeioFioEm(side) > 1e-3,
        EdgeLevel.Alinhamento => CalcadaEm(side) > 1e-3,
        EdgeLevel.Canteiro => Canteiro > 1e-3,
        _ => true
    };

    /// <summary>Se a linha aparece em algum dos lados.</summary>
    public bool Shows(EdgeLevel level) => Shows(level, Esquerda) || Shows(level, Direita);

    public double TotalWidth => EdgeOffset(EdgeLevel.Alinhamento, Esquerda) + EdgeOffset(EdgeLevel.Alinhamento, Direita);

    /// <summary>Classificacao viaria (CTB): Local, Coletora, Arterial, Transito rapido.</summary>
    public string Tipo { get; set; } = "Local";

    /// <summary>Faixas de rolamento somando os dois sentidos (0 = automatico pela largura).</summary>
    public int Faixas { get; set; }

    /// <summary>Velocidade regulamentada (km/h), usada na sinalizacao horizontal.</summary>
    public double Velocidade { get; set; } = 40.0;

    /// <summary>Faixas por sentido (minimo 1).</summary>
    public int FaixasPorSentido => Faixas > 0
        ? Math.Max(1, Faixas / 2)
        : Math.Max(1, (int)Math.Round((Pista / 2.0) / 3.5));

    public RoadSection Clone() => new()
    {
        Id = Id, Name = Name, Pista = Pista, Canteiro = Canteiro, Sarjeta = Sarjeta,
        MeioFio = MeioFio, Calcada = Calcada, Raio = Raio, Tipo = Tipo, Faixas = Faixas, Velocidade = Velocidade,
        LadosSarjeta = LadosSarjeta, LadosMeioFio = LadosMeioFio, LadosCalcada = LadosCalcada
    };
}

/// <summary>Lados do eixo em que um componente da secao existe.</summary>
[Flags]
internal enum Lados
{
    Nenhum = 0,
    Esquerda = 1,
    Direita = 2,
    Ambos = 3
}

internal enum EdgeLevel
{
    Pista = 0,
    FaceMF = 1,
    CostasMF = 2,
    Alinhamento = 3,
    Canteiro = 4,
    Ilha = 5
}

internal sealed class AxisInput
{
    public AxisInput(string key, GPath path, RoadSection section, double? radius = null, IReadOnlyList<AxisEvent>? events = null, bool chained = false)
    {
        Key = key;
        Path = path;
        Section = section;
        Radius = radius;
        Events = events ?? Array.Empty<AxisEvent>();
        Chained = chained;
    }

    /// <summary>Elementos ligados ao eixo (alargamentos, gotas, baias, aberturas).</summary>
    public IReadOnlyList<AxisEvent> Events { get; }

    /// <summary>Retas e arcos concordantes, refeitos a cada edicao (lancamento tipo Vehicle Tracking).</summary>
    public bool Chained { get; }

    public AxisInput With(double? radius) => new(Key, Path, Section, radius, Events, Chained);

    public AxisInput With(IReadOnlyList<AxisEvent> events) => new(Key, Path, Section, Radius, events, Chained);

    public AxisInput With(GPath path) => new(Key, path, Section, Radius, Events, Chained);

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

    /// <summary>Raio pedido (secao/eixo/especifico) quando o aplicado ficou menor.</summary>
    public double Requested { get; init; }

    /// <summary>Por que o raio pedido nao coube (vazio quando coube).</summary>
    public string Limit { get; init; } = "";
}

internal sealed class EngineSettings
{
    /// <summary>Distancia maxima entre a ponta de um eixo e outro eixo para formar no.</summary>
    public double SnapTolerance { get; set; } = 0.05;

    /// <summary>Tolerancia angular para continuidade reta entre eixos (graus).</summary>
    public double StraightToleranceDeg { get; set; } = 1.0;

    /// <summary>Recuo do nariz do canteiro em relacao a pista transversal.</summary>
    public double MedianSetback { get; set; } = 1.0;

    /// <summary>
    /// Ponta de eixo a ate esta distancia de outro eixo e tratada como ligada a ele
    /// (a ponta e levada ao outro eixo so no calculo; o desenho nao muda). Cobre
    /// EXTEND/grip que para um pouco antes ou passa um pouco do eixo transversal.
    /// </summary>
    public double EndCapture { get; set; } = 1.0;

    /// <summary>Raio do nariz em bicos agudos (ramos a menos de 35 graus), na face do meio-fio.</summary>
    public double NoseRadius { get; set; } = 1.0;

    /// <summary>Raios especificos por esquina (chave de CornerInfo).</summary>
    public Dictionary<string, double> CornerRadius { get; set; } = new();

    /// <summary>Esquinas com faixa livre a direita (chave de CornerInfo).</summary>
    public Dictionary<string, SlipLane> SlipLanes { get; set; } = new();

    public EngineSettings Clone() => new()
    {
        SnapTolerance = SnapTolerance,
        StraightToleranceDeg = StraightToleranceDeg,
        MedianSetback = MedianSetback,
        EndCapture = EndCapture,
        NoseRadius = NoseRadius,
        CornerRadius = new Dictionary<string, double>(CornerRadius),
        SlipLanes = new Dictionary<string, SlipLane>(SlipLanes),
        RemovedIslands = new List<V2>(RemovedIslands),
        IslandRadii = new List<(V2, double)>(IslandRadii)
    };

    /// <summary>Pontos dentro de ilhas que o usuario eliminou (vira pavimento).</summary>
    public List<V2> RemovedIslands { get; set; } = new();

    /// <summary>Raio dos cantos de ilhas canalizadoras (gota, ilha da faixa livre): ponto dentro + raio.</summary>
    public List<(V2 Seed, double Radius)> IslandRadii { get; set; } = new();
}

/// <summary>Alargamento de um lado do eixo (patamar + taper), para os objetos do Civil.</summary>
internal sealed class WidenInfo
{
    public string AxisKey { get; init; } = "";

    /// <summary>+1 = direita, -1 = esquerda, no sentido do eixo (convencao do Civil).</summary>
    public int Side { get; init; }

    /// <summary>Estaca do no (inicio do patamar) e sentido em que o patamar avanca.</summary>
    public double A { get; init; }
    public int Dir { get; init; }
    public double Full { get; init; }
    public double Taper { get; init; }
    public double Width { get; init; }
    public bool Smooth { get; init; }

    /// <summary>O patamar tambem vale atras do no (eixo termina no no).</summary>
    public bool Behind { get; init; }

    public double ValueAt(double s)
    {
        double u = (s - A) * Dir;
        if (u < 0)
            return Behind ? Width : 0.0;
        if (u <= Full)
            return Width;
        if (Taper <= 1e-9)
            return 0.0;
        double t = (u - Full) / Taper;
        if (t >= 1.0)
            return 0.0;
        return Width * (Smooth ? 0.5 * (1.0 + Math.Cos(Math.PI * t)) : 1.0 - t);
    }
}

internal sealed class ViarioResult
{
    public List<WidenInfo> Widenings { get; } = new();
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
        public bool FarNodeStraight; // o no do outro lado e so continuacao reta (sem esquinas)
    }

    private sealed class Node
    {
        public V2 Point;
        public List<(int Axis, double S)> Items = new();
        public List<Leg> Legs = new();

        /// <summary>Ordem deste no entre os nos do mesmo par de eixos (1 = primeiro).</summary>
        public Dictionary<(string, string), int> PairOrdinal = new();
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

        /// <summary>Distancia com sinal da linha ao eixo, na estaca s do eixo original.</summary>
        public Func<double, double> OffsetAt = _ => 0.0;
    }

    /// <summary>Variacao de largura ao longo do eixo (patamar + taper).</summary>
    private sealed class Ramp
    {
        public double A;
        public int Dir;
        public double Full;
        public double Taper;
        public double Width;
        public bool Smooth;
        public bool Behind;

        /// <summary>Taper de entrada: a largura cresce de In0 ate In0 + TaperIn (0 = sem).</summary>
        public double In0;
        public double TaperIn;

        public double ValueAt(double s)
        {
            double u = (s - A) * Dir;
            if (TaperIn > 1e-9)
            {
                if (u < In0)
                    return Behind ? Width : 0.0;
                if (u < In0 + TaperIn)
                {
                    double ti = (u - In0) / TaperIn;
                    return Width * (Smooth ? 0.5 * (1.0 - Math.Cos(Math.PI * ti)) : ti);
                }
            }
            else if (u < 0)
            {
                return Behind ? Width : 0.0;
            }

            if (u <= Full)
                return Width;
            if (Taper <= 1e-9)
                return 0.0;
            double t = (u - Full) / Taper;
            if (t >= 1.0)
                return 0.0;
            return Width * (Smooth ? 0.5 * (1.0 + Math.Cos(Math.PI * t)) : 1.0 - t);
        }

        public IEnumerable<double> Breaks()
        {
            yield return A;
            if (TaperIn > 1e-9)
            {
                yield return A + (Dir * In0);
                yield return A + (Dir * (In0 + TaperIn));
            }

            yield return A + (Dir * Full);
            yield return A + (Dir * (Full + Taper));
        }
    }

    private sealed class GotaPlace
    {
        public int Axis;
        public double A;
        public int Dir;
        public double U0;
        public double Length;
        public double Width;
    }

    private sealed class Ctx
    {
        public IReadOnlyList<AxisInput> Axes = null!;
        public EngineSettings Settings = null!;
        public GPath[] Ext = null!;
        public Dictionary<(int, int, EdgeLevel), Curve> Curves = new();
        public Dictionary<(int, long), GPath> OffsetCache = new();
        public List<Seg>[] Pieces = Enumerable.Range(0, 6).Select(_ => new List<Seg>()).ToArray();
        public ViarioResult Result = null!;
        public Dictionary<(int Axis, int Side), List<Ramp>> Widen = new();
        public Dictionary<(int Axis, int Side), List<Ramp>> Narrow = new();
        public Dictionary<int, List<(double A, double B)>> Openings = new();
        public List<GotaPlace> Gotas = new();
        public Dictionary<(int, int, EdgeLevel, long), GPath> CurveOffsetCache = new();
        public HashSet<string> RepeatedKeys = new();
        public double[] ExtStartLen = Array.Empty<double>();
    }

    public static ViarioResult Compute(IReadOnlyList<AxisInput> axes, EngineSettings settings)
    {
        ViarioResult result = new();
        List<AxisInput> valid = axes.Where(a => !a.Path.IsEmpty && a.Path.Length > 1e-6).ToList();
        if (valid.Count == 0)
            return result;

        valid = SnapEnds(valid, settings);
        Ctx ctx = new() { Axes = valid, Settings = settings, Result = result };

        List<Node> nodes = FindNodes(ctx);
        BuildLegs(ctx, nodes);
        NumberPairs(ctx, nodes);
        ResolveEvents(ctx, nodes);
        foreach (((int axis, int side), List<Ramp> ramps) in ctx.Widen)
        {
            foreach (Ramp r in ramps)
            {
                result.Widenings.Add(new WidenInfo
                {
                    AxisKey = valid[axis].Key, Side = -side, A = r.A, Dir = r.Dir, Full = r.Full,
                    Taper = r.Taper, Width = r.Width, Smooth = r.Smooth, Behind = r.Behind
                });
            }
        }

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
        BuildGotas(ctx);
        RoundIslands(result, settings.IslandRadii);
        RemoveIslands(result, settings.RemovedIslands);
        return result;
    }

    /// <summary>
    /// Ilha eliminada: em cada nivel, o menor anel fechado que contem o ponto sai
    /// (face, costas, alinhamento, canteiro, gota...). O resto da rede nao muda.
    /// </summary>
    private static void RemoveIslands(ViarioResult result, List<V2> seeds)
    {
        foreach (V2 seed in seeds)
        {
            foreach (IGrouping<EdgeLevel, OutputLine> level in result.Lines.Where(l => l.Closed).GroupBy(l => l.Level).ToList())
            {
                OutputLine? ring = level
                    .Where(l => RingContains(l.Segs, seed))
                    .OrderBy(l => RingArea(l.Segs))
                    .FirstOrDefault();
                if (ring is not null)
                    result.Lines.Remove(ring);
            }
        }
    }

    /// <summary>Arredonda os cantos das ilhas canalizadoras marcadas.</summary>
    private static void RoundIslands(ViarioResult result, List<(V2 Seed, double Radius)> radii)
    {
        foreach ((V2 seed, double radius) in radii)
        {
            if (radius <= 1e-6)
                continue;

            OutputLine? ring = result.Lines
                .Where(l => l.Closed && l.Level == EdgeLevel.Ilha && RingContains(l.Segs, seed))
                .OrderBy(l => RingArea(l.Segs))
                .FirstOrDefault();
            if (ring is null)
                continue;

            List<Seg> rounded = FilletRing(ring.Segs, radius);
            int index = result.Lines.IndexOf(ring);
            result.Lines[index] = new OutputLine(ring.Level, rounded, closed: true);
        }
    }

    /// <summary>
    /// Concorda cada canto (quebra de tangente) de um anel fechado com o raio dado,
    /// pelo lado de dentro. Raio que nao cabe e reduzido ate caber.
    /// </summary>
    public static List<Seg> FilletRing(IReadOnlyList<Seg> ring, double radius)
    {
        List<Seg> segs = ring.Where(sg => sg.Length > 1e-6).ToList();
        int n = segs.Count;
        if (n < 2)
            return segs;

        // Orientacao: anti-horario => interior a esquerda (offset positivo).
        double signedArea = 0.0;
        List<V2> pts = RingPoints(segs);
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            signedArea += V2.Cross(pts[j], pts[i]);
        double inward = signedArea >= 0 ? 1.0 : -1.0;

        // Parametros de corte no inicio/fim de cada trecho e o arco de cada canto.
        double[] t0 = new double[n];
        double[] t1 = Enumerable.Repeat(1.0, n).ToArray();
        Seg?[] arcs = new Seg?[n];

        for (int i = 0; i < n; i++)
        {
            Seg a = segs[i];
            Seg b = segs[(i + 1) % n];
            double turn = Math.Abs(V2.Cross(a.TangentAt(1.0), b.TangentAt(0.0)));
            if (turn < Math.Sin(1.0 * Math.PI / 180.0) && V2.Dot(a.TangentAt(1.0), b.TangentAt(0.0)) > 0)
                continue;

            for (double r = radius; r > 0.05; r *= 0.8)
            {
                Seg? oa = a.Offset(inward * r);
                Seg? ob = b.Offset(inward * r);
                if (oa is null || ob is null)
                    continue;

                V2 corner = a.B;
                List<V2> centers = Seg.CarrierIntersections(oa, ob);
                if (centers.Count == 0)
                    continue;
                V2 c = centers.OrderBy(q => V2.Dist(q, corner)).First();

                double ta = a.ParamOf(a.Closest(c).Point);
                double tb = b.ParamOf(b.Closest(c).Point);
                V2 pa = a.PointAt(ta);
                V2 pb = b.PointAt(tb);
                if (ta <= t0[i] + 1e-6 || ta >= 1.0 - 1e-9 || tb <= 1e-9 || tb >= t1[(i + 1) % n] - 1e-6)
                    continue;
                if (Math.Abs(V2.Dist(pa, c) - r) > 1e-3 || Math.Abs(V2.Dist(pb, c) - r) > 1e-3)
                    continue;

                V2 u = pa - c;
                V2 w = pb - c;
                double sweep = Math.Atan2(V2.Cross(u, w), V2.Dot(u, w));
                t1[i] = ta;
                t0[(i + 1) % n] = tb;
                arcs[i] = new Seg(pa, pb, Math.Tan(sweep / 4.0));
                break;
            }
        }

        List<Seg> result = new();
        for (int i = 0; i < n; i++)
        {
            if (t1[i] - t0[i] > 1e-9)
                result.Add(segs[i].Sub(t0[i], t1[i]));
            if (arcs[i] is Seg arc)
                result.Add(arc);
        }

        return result;
    }

    private static List<V2> RingPoints(IReadOnlyList<Seg> segs)
    {
        List<V2> pts = new();
        foreach (Seg s in segs)
        {
            int n = s.IsArc ? 16 : 1;
            for (int k = 0; k < n; k++)
                pts.Add(s.PointAt((double)k / n));
        }

        return pts;
    }

    public static bool RingContains(IReadOnlyList<Seg> segs, V2 p)
    {
        List<V2> pts = RingPoints(segs);
        bool inside = false;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            if (((pts[i].Y > p.Y) != (pts[j].Y > p.Y)) &&
                (p.X < ((pts[j].X - pts[i].X) * (p.Y - pts[i].Y) / (pts[j].Y - pts[i].Y)) + pts[i].X))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    public static double RingArea(IReadOnlyList<Seg> segs)
    {
        List<V2> pts = RingPoints(segs);
        double area = 0;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            area += V2.Cross(pts[j], pts[i]);
        return Math.Abs(area / 2.0);
    }

    /// <summary>Chave de uma esquina: os dois ramos (eixo e sentido), na ordem anti-horaria.</summary>
    private static (string, string) PairOf(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    /// <summary>
    /// Dois eixos que se cruzam em mais de um no geram esquinas com a mesma chave base;
    /// o segundo no em diante (ao longo do eixo de menor chave) ganha o sufixo "#n".
    /// </summary>
    private static void NumberPairs(Ctx ctx, List<Node> nodes)
    {
        Dictionary<(string, string), List<(Node Node, double S)>> pairs = new();
        foreach (Node node in nodes)
        {
            List<(string Key, double S)> items = node.Items.Select(it => (ctx.Axes[it.Axis].Key, it.S)).ToList();
            HashSet<(string, string)> seen = new();
            for (int x = 0; x < items.Count; x++)
            {
                for (int y = x; y < items.Count; y++)
                {
                    if (x == y && items.Count(it => it.Key == items[x].Key) < 2)
                        continue;

                    (string, string) pair = PairOf(items[x].Key, items[y].Key);
                    if (!seen.Add(pair))
                        continue;

                    double s = items.Where(it => it.Key == pair.Item1).Select(it => it.S).DefaultIfEmpty(0.0).Min();
                    if (!pairs.TryGetValue(pair, out List<(Node, double)>? list))
                        pairs[pair] = list = new List<(Node, double)>();
                    list.Add((node, s));
                }
            }
        }

        foreach (((string, string) pair, List<(Node Node, double S)> list) in pairs)
        {
            int n = 1;
            foreach ((Node node, _) in list.OrderBy(t => t.S))
                node.PairOrdinal[pair] = n++;
        }

        // So recebem sufixo as chaves que de fato se repetem (desenhos antigos mantem as suas).
        Dictionary<string, int> count = new();
        foreach (Node node in nodes)
        {
            int legs = node.Legs.Count;
            for (int i = 0; i < legs; i++)
            {
                Leg li = node.Legs[i];
                Leg lj = node.Legs[(i + 1) % legs];
                string key = CornerKey(ctx.Axes[li.Axis].Key, li.Dir, ctx.Axes[lj.Axis].Key, lj.Dir);
                count[key] = count.TryGetValue(key, out int c) ? c + 1 : 1;
            }
        }

        foreach ((string key, int c) in count)
        {
            if (c > 1)
                ctx.RepeatedKeys.Add(key);
        }
    }

    public static string CornerKey(string axisA, int dirA, string axisB, int dirB) =>
        axisA + (dirA > 0 ? "+" : "-") + "|" + axisB + (dirB > 0 ? "+" : "-");

    // ------------------------------------------------------------------
    // Pontas quase encostadas
    // ------------------------------------------------------------------

    /// <summary>
    /// Pontas de eixos abertos a menos de EndCapture de outro eixo (sem encostar)
    /// sao levadas ate ele: reta final prolongada/encurtada na propria direcao
    /// (concordancia preservada); arco final vai pela perpendicular.
    /// </summary>
    private static List<AxisInput> SnapEnds(List<AxisInput> axes, EngineSettings settings)
    {
        double capture = settings.EndCapture;
        double snap = Math.Max(settings.SnapTolerance, 1e-4);
        if (capture <= snap || axes.Count < 2)
            return axes;

        List<AxisInput> result = new(axes);
        for (int i = 0; i < axes.Count; i++)
        {
            GPath path = axes[i].Path;
            if (path.Closed || path.Segs.Count == 0)
                continue;

            List<Seg> segs = new(path.Segs);
            bool changed = false;
            foreach (bool atEnd in new[] { false, true })
            {
                Seg seg = atEnd ? segs[^1] : segs[0];
                V2 end = atEnd ? seg.B : seg.A;
                V2 dir = atEnd ? seg.TangentAt(1.0) : -seg.TangentAt(0.0);
                if (dir.Length < 1e-12)
                    continue;
                dir = dir.Unit();

                V2? target = null;
                double best = capture;
                for (int j = 0; j < axes.Count; j++)
                {
                    if (j == i || !axes[j].Path.Bounds().Inflate(capture).Intersects(Box2.Empty.Add(end)))
                        continue;

                    (_, V2 q, double dist) = axes[j].Path.Project(end);
                    if (dist <= snap)
                    {
                        target = null; // ja encosta
                        best = -1;
                        break;
                    }

                    if (dist > best)
                        continue;

                    // Reta final: prolonga/encurta na direcao; se nao cruza, usa a perpendicular.
                    V2 candidate = q;
                    if (!seg.IsArc)
                    {
                        GPath probe = new(new List<Seg> { Seg.Line(end - (dir * (capture * 3.0)), end + (dir * (capture * 3.0))) });
                        V2? along = null;
                        double alongDist = capture * 2.0;
                        foreach ((_, _, V2 x) in probe.Intersections(axes[j].Path))
                        {
                            double d = V2.Dist(x, end);
                            if (d < alongDist)
                            {
                                alongDist = d;
                                along = x;
                            }
                        }

                        if (along is V2 a)
                            candidate = a;
                    }

                    best = dist;
                    target = candidate;
                }

                if (best < 0)
                    continue; // ja encosta em alguem
                if (target is not V2 t || V2.Dist(t, end) < 1e-9)
                    continue;

                if (atEnd)
                {
                    Seg last = segs[^1];
                    if (V2.Dist(last.A, t) > 1e-6)
                        segs[^1] = new Seg(last.A, t, last.IsArc ? last.Bulge : 0.0);
                }
                else
                {
                    Seg firstSeg = segs[0];
                    if (V2.Dist(t, firstSeg.B) > 1e-6)
                        segs[0] = new Seg(t, firstSeg.B, firstSeg.IsArc ? firstSeg.Bulge : 0.0);
                }

                changed = true;
            }

            if (changed)
                result[i] = axes[i].With(new GPath(segs));
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

        // No do outro lado que so continua o eixo em linha reta nao disputa espaco de esquina.
        double straight = ctx.Settings.StraightToleranceDeg * Math.PI / 180.0;
        foreach (Node node in nodes)
        {
            foreach (Leg leg in node.Legs.Where(l => l.ExtentEndsAtNode && !ctx.Axes[l.Axis].Path.Closed))
            {
                double target = leg.S + (leg.Dir * leg.Extent);
                Node? far = nodes.FirstOrDefault(n => n != node && n.Items.Any(it => it.Axis == leg.Axis && Math.Abs(it.S - target) <= snap + 1e-6));
                if (far is not null && far.Legs.Count == 2 &&
                    Math.Abs(GMath.Norm(far.Legs[1].Angle - far.Legs[0].Angle) - Math.PI) <= straight)
                {
                    leg.FarNodeStraight = true;
                }
            }
        }
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
        if (ctx.Settings.SlipLanes.Count > 0)
            maxR = Math.Max(maxR, ctx.Settings.SlipLanes.Values.Max(sl => sl.Radius + sl.Width));
        double maxWiden = ctx.Widen.Values.SelectMany(l => l).Select(w => w.Width).DefaultIfEmpty(0.0).Max();
        double ext = (3.0 * (maxEdge + maxWiden)) + (2.0 * maxR) + 20.0;

        ctx.Ext = new GPath[n];
        ctx.ExtStartLen = new double[n];

        for (int i = 0; i < n; i++)
        {
            GPath axis = ctx.Axes[i].Path;
            bool closed = axis.Closed;
            double e0 = !closed && startAtNode[i] ? ext : 0.0;
            double e1 = !closed && endAtNode[i] ? ext : 0.0;
            ctx.Ext[i] = axis.Extended(e0, e1);
            ctx.ExtStartLen[i] = e0;

            RoadSection section = ctx.Axes[i].Section;
            foreach (EdgeLevel level in EdgeLevels)
            {
                foreach (int side in new[] { 1, -1 })
                {
                    double d = section.EdgeOffset(level, side);
                    ctx.Widen.TryGetValue((i, side), out List<Ramp>? ramps);
                    Func<double, double> widen = Profile(ramps);
                    int sd = side;
                    Func<double, double> offsetAt = s => sd * (d + widen(s));

                    GPath p = ramps is null || ramps.Count == 0
                        ? OffsetOf(ctx, i, side * d)
                        : VariableOffset(ctx.Ext[i], side * d, sExt => side * widen(sExt - e0), ramps.SelectMany(rp => rp.Breaks()).Select(b => b + e0));
                    if (p.IsEmpty)
                        continue;

                    Curve curve = new()
                    {
                        Axis = i,
                        Side = side,
                        Level = level,
                        Visible = section.Shows(level, side),
                        Closed = closed && p.Closed,
                        P = p,
                        ExtStart = e0 > 0,
                        ExtEnd = e1 > 0,
                        OffsetAt = offsetAt
                    };

                    curve.OrigStart = e0 > 0 ? SOnCurve(ctx, curve, 0.0) : 0.0;
                    curve.OrigEnd = e1 > 0 ? SOnCurve(ctx, curve, axis.Length) : p.Length;
                    ctx.Curves[(i, side, level)] = curve;
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

    /// <summary>Posicao na linha correspondente a estaca s do eixo original.</summary>
    private static double SOnCurve(Ctx ctx, Curve curve, double s)
    {
        GPath path = ctx.Axes[curve.Axis].Path;
        s = Math.Clamp(s, 0, path.Length);
        V2 p = path.PointAt(s) + (path.TangentAt(s).Left * curve.OffsetAt(s));
        return curve.P.Project(p).S;
    }

    /// <summary>Largura extra (maior das rampas) na estaca s.</summary>
    private static Func<double, double> Profile(List<Ramp>? ramps)
    {
        if (ramps is null || ramps.Count == 0)
            return _ => 0.0;
        return s => ramps.Max(r => r.ValueAt(s));
    }

    /// <summary>
    /// Paralela com distancia variavel: trechos de distancia constante usam o
    /// offset exato; trechos em taper sao amostrados (e simplificados).
    /// </summary>
    private static GPath VariableOffset(GPath ext, double baseSigned, Func<double, double> delta, IEnumerable<double> breaks)
    {
        double length = ext.Length;
        List<double> cuts = breaks.Where(b => b > 1e-6 && b < length - 1e-6)
            .Append(0.0).Append(length).Distinct().OrderBy(b => b).ToList();

        List<Seg> segs = new();
        for (int k = 0; k + 1 < cuts.Count; k++)
        {
            double b0 = cuts[k], b1 = cuts[k + 1];
            if (b1 - b0 < 1e-6)
                continue;

            double w0 = delta(b0 + 1e-7), wm = delta((b0 + b1) / 2.0), w1 = delta(b1 - 1e-7);
            List<Seg> piece;

            if (Math.Abs(w0 - wm) < 1e-9 && Math.Abs(w1 - wm) < 1e-9)
            {
                piece = ext.Sub(b0, b1).Offset(baseSigned + wm).Segs.ToList();
            }
            else
            {
                int n = Math.Clamp((int)Math.Ceiling((b1 - b0) / 0.5), 4, 400);
                List<V2> pts = new();
                for (int j = 0; j <= n; j++)
                {
                    double sj = b0 + ((b1 - b0) * j / n);
                    double se = j == 0 ? sj + 1e-7 : j == n ? sj - 1e-7 : sj;
                    pts.Add(ext.PointAt(sj) + (ext.TangentAt(sj).Left * (baseSigned + delta(se))));
                }

                pts = Simplify(pts);
                piece = new List<Seg>();
                for (int j = 1; j < pts.Count; j++)
                    piece.Add(Seg.Line(pts[j - 1], pts[j]));
            }

            if (piece.Count == 0)
                continue;
            if (segs.Count > 0 && V2.Dist(segs[^1].B, piece[0].A) > 1e-7)
                segs.Add(Seg.Line(segs[^1].B, piece[0].A));
            segs.AddRange(piece);
        }

        return new GPath(segs);
    }

    /// <summary>Remove pontos colineares.</summary>
    private static List<V2> Simplify(List<V2> pts)
    {
        if (pts.Count < 3)
            return pts;

        List<V2> result = new() { pts[0] };
        for (int i = 1; i < pts.Count - 1; i++)
        {
            V2 a = result[^1], b = pts[i], c = pts[i + 1];
            V2 ac = c - a;
            double len = ac.Length;
            double dev = len < 1e-12 ? 0 : Math.Abs(V2.Cross(ac, b - a)) / len;
            if (dev > 1e-5)
                result.Add(b);
        }

        result.Add(pts[^1]);
        return result;
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

    /// <summary>Angulo entre ramos abaixo do qual a esquina e tratada como nariz de ilha.</summary>
    private const double NoseAngle = 35.0 * Math.PI / 180.0;

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
            if (ctx.RepeatedKeys.Contains(key) &&
                node.PairOrdinal.TryGetValue(PairOf(ctx.Axes[li.Axis].Key, ctx.Axes[lj.Axis].Key), out int ordinal) && ordinal > 1)
                key += "#" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SlipLane? slip = ctx.Settings.SlipLanes.TryGetValue(key, out SlipLane? sl) ? sl : null;
            double faceRadius = slip?.Radius ?? ResolveRadius(ctx, li, lj, key);
            bool concave = theta < Math.PI - straightTol;

            // Bico agudo (ramos que se separam em angulo pequeno): o que fica entre eles e um
            // nariz de ilha/canteiro, com raio de 0,5-1,0 m (IPR-718), nao o raio de esquina.
            if (concave && slip is null && !ctx.Settings.CornerRadius.ContainsKey(key) && theta < NoseAngle)
                faceRadius = Math.Min(faceRadius, ctx.Settings.NoseRadius);

            foreach (EdgeLevel level in EdgeLevels)
                ProcessCorner(ctx, node, li, lj, sideI, sideJ, theta, concave, level, faceRadius, key, concave ? slip : null);
        }
    }

    private static void ProcessCorner(
        Ctx ctx, Node node, Leg li, Leg lj, int sideI, int sideJ, double theta, bool concave,
        EdgeLevel level, double faceRadius, string key, SlipLane? slip)
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
        double d1 = si.EdgeOffset(level, sideI);
        double d2 = sj.EdgeOffset(level, sideJ);
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
            double delta = ((si.EdgeOffset(EdgeLevel.FaceMF, sideI) - d1) + (sj.EdgeOffset(EdgeLevel.FaceMF, sideJ) - d2)) / 2.0;
            radius = faceRadius > 1e-6 || delta > 0 ? Math.Max(faceRadius + delta, 0.0) : 0.0;
        }

        double t1 = x.Value.S1;
        double t2 = x.Value.S2;
        Seg? arc = null;
        double usedRadius = 0.0;
        string limitText = "";

        if (radius > 1e-6)
        {
            (bool ok, double r, double s1, double s2, Seg? a, V2 center) =
                Fillet(ctx, li, lj, sideI, sideJ, d1, d2, c1, c2, x.Value.P, x.Value.S1, x.Value.S2, radius, theta);

            if (ok && a is not null && level == EdgeLevel.FaceMF && slip is not null)
                BuildSlipIsland(ctx, c1, c2, x.Value.P, center, r, slip);

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
                    limitText = _fitReason ?? "nao cabe";
                    ctx.Result.Warnings.Add(
                        $"Raio de esquina reduzido para {r:0.##} m perto de {node.Point} (pedido {radius:0.##} m; {limitText}).");
                }
            }
            else if (level == EdgeLevel.FaceMF)
            {
                limitText = _fitReason ?? "o raio nao coube";
                ctx.Result.Warnings.Add($"Esquina em canto vivo perto de {node.Point} ({limitText}).");
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
                AxisB = ctx.Axes[lj.Axis].Key,
                Requested = radius,
                Limit = limitText
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

    /// <summary>Paralela de uma linha da secao, para o lado da quadra (cache).</summary>
    private static GPath CurveOffset(Ctx ctx, Curve curve, double distance)
    {
        var key = (curve.Axis, curve.Side, curve.Level, (long)Math.Round(distance * 1e6));
        if (!ctx.CurveOffsetCache.TryGetValue(key, out GPath? path))
        {
            path = curve.P.Offset(curve.Side * distance);
            ctx.CurveOffsetCache[key] = path;
        }

        return path;
    }

    /// <summary>Motivo pelo qual o ultimo raio tentado nao coube (para a mensagem ao usuario).</summary>
    [ThreadStatic] private static string? _fitReason;

    private static (bool Ok, double Radius, double S1, double S2, Seg? Arc, V2 Center) Fillet(
        Ctx ctx, Leg li, Leg lj, int sideI, int sideJ, double d1, double d2,
        Curve c1, Curve c2, V2 corner, double sx1, double sx2, double radius, double theta)
    {
        double r = radius;
        _fitReason = null;
        // Em bico agudo o centro fica longe da esquina (r / sen(theta/2)).
        double sinHalf = Math.Max(Math.Abs(Math.Sin(theta / 2.0)), 0.05);

        // Tenta o raio pedido e vai reduzindo aos poucos (ate ~5% dele), para ficar
        // perto do maior raio que cabe - bicos agudos (narizes de ilha) precisam disso.
        for (int attempt = 0; attempt < 14; attempt++, r *= 0.8)
        {
            // Centro: encontro das paralelas (a distancia r) das proprias linhas,
            // que ja incluem alargamentos e tapers.
            GPath o1 = CurveOffset(ctx, c1, r);
            GPath o2 = CurveOffset(ctx, c2, r);

            V2? center = null;
            double best = double.MaxValue;
            double limit = Math.Max((3.0 * r) + d1 + d2 + 5.0, (1.2 * r / sinHalf) + 1.0);
            foreach ((_, _, V2 p) in o1.Intersections(o2))
            {
                double dist = V2.Dist(p, corner);
                if (dist < best && dist <= limit)
                {
                    best = dist;
                    center = p;
                }
            }

            if (center is not V2 o)
            {
                Debug?.Invoke($"fillet r={r:0.##}: sem centro (paralelas nao se cruzam perto da esquina)");
                if (_fitReason is null)
                {
                    // Uma das linhas e curva e se afasta da esquina: nao ha circulo tangente as duas com esse raio.
                    bool curvedI = c1.P.Segs.Count > 0 && c1.P.SegAt(sx1).IsArc;
                    bool curvedJ = c2.P.Segs.Count > 0 && c2.P.SegAt(sx2).IsArc;
                    string who = curvedI && curvedJ ? "as curvas dos dois eixos se afastam" :
                        curvedI ? $"a curva do eixo {ctx.Axes[li.Axis].Key} se afasta" :
                        curvedJ ? $"a curva do eixo {ctx.Axes[lj.Axis].Key} se afasta" : "os bordos se afastam";
                    _fitReason = who + " da esquina: nao existe arco tangente aos dois com esse raio (estique a tangente do eixo antes do no ou aumente o raio da curva dele)";
                }

                continue;
            }

            (double s1, V2 p1, double dist1) = c1.P.Project(o);
            (double s2, V2 p2, double dist2) = c2.P.Project(o);

            double tol = Math.Max(1e-3, r * 1e-4);
            if (Math.Abs(dist1 - r) > tol || Math.Abs(dist2 - r) > tol)
            {
                Debug?.Invoke($"fillet r={r:0.##}: centro a {dist1:0.###}/{dist2:0.###} das linhas");
                continue;
            }

            if (SignedAlong(c1, sx1, s1, li.Dir) < -1e-6 || SignedAlong(c2, sx2, s2, lj.Dir) < -1e-6)
            {
                Debug?.Invoke($"fillet r={r:0.##}: tangencia antes da esquina ({SignedAlong(c1, sx1, s1, li.Dir):0.##}/{SignedAlong(c2, sx2, s2, lj.Dir):0.##})");
                continue;
            }

            if (!FitsInLeg(ctx, li, p1, radius) || !FitsInLeg(ctx, lj, p2, radius))
            {
                Debug?.Invoke($"fillet r={r:0.##}: nao cabe no ramo");
                _fitReason ??= "a tangente do arco passaria do no vizinho (ramo curto)";
                continue;
            }

            double a0 = (p1 - o).Angle;
            double a1 = (p2 - o).Angle;
            double sweep = GMath.Norm(a1 - a0);
            if (sweep > Math.PI)
                sweep -= GMath.TwoPi;

            return (true, r, s1, s2, new Seg(p1, p2, Math.Tan(sweep / 4.0)), o);
        }

        return (false, 0.0, sx1, sx2, null, default);
    }

    /// <summary>
    /// O ponto de tangencia precisa caber no ramo. Quando o ramo termina em outro no,
    /// este no pode usar ate a metade do ramo ou, se sobrar, tudo menos o que o outro
    /// no precisa para as proprias esquinas (bicos agudos ficam longe do no e
    /// precisam dessa folga).
    /// </summary>
    private static bool FitsInLeg(Ctx ctx, Leg leg, V2 tangentPoint, double faceRadius)
    {
        GPath axis = ctx.Axes[leg.Axis].Path;
        double s = axis.Project(tangentPoint).S;
        double along = (s - leg.S) * leg.Dir;
        if (axis.Closed)
            along = ((along % axis.Length) + axis.Length) % axis.Length;
        double available = leg.ExtentEndsAtNode && !leg.FarNodeStraight
            ? Math.Max(leg.Extent / 2.0, leg.Extent - Math.Max(2.0 * faceRadius, 6.0))
            : leg.Extent;
        return along <= available + 1e-6;
    }

    private static void AddRemoval(Ctx ctx, Curve curve, Leg leg, double sT)
    {
        double length = curve.P.Length;

        if (leg.Through)
        {
            double sNode = SOnCurve(ctx, curve, leg.S);

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

            if (ctx.Openings.TryGetValue(i, out List<(double A, double B)>? openings))
                blocked.AddRange(openings);

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

            Curve left = MedianCurve(ctx, i, 1, half);
            Curve right = MedianCurve(ctx, i, -1, half);

            foreach ((double a, double b) in free)
            {
                double ha = Math.Max(Math.Abs(left.OffsetAt(a)), Math.Abs(right.OffsetAt(a)));
                double hb = Math.Max(Math.Abs(left.OffsetAt(b)), Math.Abs(right.OffsetAt(b)));
                if (b - a < ha + hb + 0.5)
                    continue;

                double s0 = a + ((Math.Abs(left.OffsetAt(a)) + Math.Abs(right.OffsetAt(a))) / 2.0);
                double s1 = b - ((Math.Abs(left.OffsetAt(b)) + Math.Abs(right.OffsetAt(b))) / 2.0);
                double l0 = SOnCurve(ctx, left, s0), l1 = SOnCurve(ctx, left, s1);
                double r0 = SOnCurve(ctx, right, s0), r1 = SOnCurve(ctx, right, s1);

                GPath leftPart = left.P.Sub(Math.Min(l0, l1), Math.Max(l0, l1));
                GPath rightPart = right.P.Sub(Math.Min(r0, r1), Math.Max(r0, r1));
                if (leftPart.IsEmpty || rightPart.IsEmpty)
                    continue;

                V2 tipEnd = axis.Path.PointAt(Math.Min(b, length));
                V2 tipStart = axis.Path.PointAt(Math.Max(a, 0));

                List<Seg> ring = new(leftPart.Segs);
                ring.Add(SemiCircle(leftPart.End, rightPart.End, tipEnd));
                ring.AddRange(rightPart.Reverse().Segs);
                ring.Add(SemiCircle(rightPart.Start, leftPart.Start, tipStart));
                ctx.Result.Lines.Add(new OutputLine(EdgeLevel.Canteiro, ring, closed: true));
            }
        }
    }

    /// <summary>Linha do canteiro de um lado, estreitada pelas baias.</summary>
    private static Curve MedianCurve(Ctx ctx, int axis, int side, double half)
    {
        double e0 = ctx.ExtStartLen[axis];
        ctx.Narrow.TryGetValue((axis, side), out List<Ramp>? ramps);
        Func<double, double> narrow = Profile(ramps);
        Func<double, double> width = s => Math.Max(half - narrow(s), Math.Min(half, 0.3));

        GPath p = ramps is null || ramps.Count == 0
            ? OffsetOf(ctx, axis, side * half)
            : VariableOffset(ctx.Ext[axis], side * half, sExt => side * (width(sExt - e0) - half),
                ramps.SelectMany(rp => rp.Breaks()).Select(b => b + e0));

        return new Curve
        {
            Axis = axis,
            Side = side,
            Level = EdgeLevel.Canteiro,
            Visible = true,
            P = p,
            OffsetAt = s => side * width(s)
        };
    }

    /// <summary>Semicirculo de a ate b passando do lado de "towards".</summary>
    private static Seg SemiCircle(V2 a, V2 b, V2 towards)
    {
        Seg plus = new(a, b, 1.0);
        Seg minus = new(a, b, -1.0);
        return V2.Dist(plus.PointAt(0.5), towards) <= V2.Dist(minus.PointAt(0.5), towards) ? plus : minus;
    }

    // ------------------------------------------------------------------
    // Elementos ligados aos eixos
    // ------------------------------------------------------------------

    private static void ResolveEvents(Ctx ctx, List<Node> nodes)
    {
        double snap = Math.Max(ctx.Settings.SnapTolerance, 1e-4);

        for (int i = 0; i < ctx.Axes.Count; i++)
        {
            AxisInput axis = ctx.Axes[i];
            if (axis.Events.Count == 0)
                continue;

            double length = axis.Path.Length;
            List<(Node Node, double S)> stations = nodes
                .SelectMany(n => n.Items.Where(it => it.Axis == i).Select(it => (n, it.S)))
                .ToList();

            foreach (AxisEvent ev in axis.Events)
            {
                if (ev.Kind == AxisEventKind.Abertura)
                {
                    if (!ctx.Openings.TryGetValue(i, out List<(double, double)>? list))
                        ctx.Openings[i] = list = new List<(double, double)>();
                    list.Add((ev.S - (ev.Length / 2.0), ev.S + (ev.Length / 2.0)));
                    continue;
                }

                // Ancora no no mais proximo da estaca gravada.
                (Node Node, double S)? anchor = stations
                    .Where(st => Math.Abs(st.S - ev.S) <= 60.0)
                    .OrderBy(st => Math.Abs(st.S - ev.S))
                    .Select(st => ((Node, double)?)st)
                    .FirstOrDefault();

                double a = anchor?.S ?? ev.S;
                bool through = axis.Path.Closed || (a > snap && a < length - snap);
                double crossHalf = anchor is null
                    ? 0.0
                    : anchor.Value.Node.Legs.Where(l => l.Axis != i)
                        .Select(l => ctx.Axes[l.Axis].Section.EdgeOffset(EdgeLevel.Pista))
                        .DefaultIfEmpty(0.0).Max();

                switch (ev.Kind)
                {
                    case AxisEventKind.Alargamento:
                        foreach (int side in ev.Side == 0 ? new[] { 1, -1 } : new[] { ev.Side * ev.Dir })
                        {
                            AddRamp(ctx.Widen, i, side, new Ramp
                            {
                                A = a, Dir = ev.Dir, Full = crossHalf + ev.TaperIn + ev.Length, Taper = ev.Taper,
                                Width = ev.Width, Smooth = ev.Smooth, Behind = !through,
                                In0 = crossHalf, TaperIn = ev.TaperIn
                            });
                        }

                        break;

                    case AxisEventKind.Gota:
                        double u0 = crossHalf + ev.Gap;
                        foreach (int side in new[] { 1, -1 })
                        {
                            AddRamp(ctx.Widen, i, side, new Ramp
                            {
                                A = a, Dir = ev.Dir, Full = u0 + ev.Length, Taper = ev.Taper,
                                Width = ev.Width / 2.0, Smooth = ev.Smooth, Behind = !through
                            });
                        }

                        ctx.Gotas.Add(new GotaPlace { Axis = i, A = a, Dir = ev.Dir, U0 = u0, Length = ev.Length, Width = ev.Width });
                        break;

                    case AxisEventKind.Baia:
                        // Conversao a esquerda (Side -1) corta o canteiro do lado da pista de quem chega,
                        // que fica a direita do eixo no sentido de chegada (lado motor = Dir).
                        int medianSide = -(ev.Side == 0 ? -1 : ev.Side) * ev.Dir;
                        AddRamp(ctx.Narrow, i, medianSide, new Ramp
                        {
                            A = a, Dir = ev.Dir, Full = crossHalf + ev.TaperIn + ev.Length, Taper = ev.Taper,
                            Width = ev.Width, Smooth = ev.Smooth, Behind = !through,
                            In0 = crossHalf, TaperIn = ev.TaperIn
                        });
                        break;
                }
            }
        }
    }

    private static void AddRamp(Dictionary<(int, int), List<Ramp>> map, int axis, int side, Ramp ramp)
    {
        if (!map.TryGetValue((axis, side), out List<Ramp>? list))
            map[(axis, side)] = list = new List<Ramp>();
        list.Add(ramp);
    }

    /// <summary>Ilhas em gota: ponta arredondada junto ao no, afinando ate a cauda.</summary>
    private static void BuildGotas(Ctx ctx)
    {
        foreach (GotaPlace g in ctx.Gotas)
        {
            GPath path = ctx.Axes[g.Axis].Path;
            double r = g.Width / 2.0;
            double uNose = g.U0 + r;
            double uTip = g.U0 + g.Length;
            if (uTip <= uNose + 0.5 || r <= 0.05)
                continue;

            double S(double u) => Math.Clamp(g.A + (g.Dir * u), 0.0, path.Length);
            V2 At(double u, double h)
            {
                double s = S(u);
                return path.PointAt(s) + (path.TangentAt(s).Left * h);
            }

            const int n = 12;
            List<V2> left = new();
            List<V2> right = new();
            for (int k = 0; k <= n; k++)
            {
                double u = uNose + ((uTip - uNose) * k / n);
                double h = r * (1.0 - ((double)k / n));
                left.Add(At(u, h));
                right.Add(At(u, -h));
            }

            List<Seg> ring = new();
            for (int k = 1; k <= n; k++)
                ring.Add(Seg.Line(left[k - 1], left[k]));
            for (int k = n; k >= 1; k--)
                ring.Add(Seg.Line(right[k], right[k - 1]));
            ring.RemoveAll(sg => sg.Length < 1e-9);
            ring.Add(SemiCircle(right[0], left[0], At(g.U0, 0.0)));

            ctx.Result.Lines.Add(new OutputLine(EdgeLevel.Ilha, ring, closed: true));
        }
    }

    /// <summary>
    /// Ilha triangular da faixa livre: entre os bordos das duas vias (recuados)
    /// e a borda interna da faixa (concentrica ao meio-fio externo).
    /// </summary>
    private static void BuildSlipIsland(Ctx ctx, Curve c1, Curve c2, V2 corner, V2 center, double radius, SlipLane slip)
    {
        // A ilha fica no canto "cortado" da quadra: bordos recuados para o lado da quadra.
        GPath e1 = CurveOffset(ctx, c1, slip.Setback);
        GPath e2 = CurveOffset(ctx, c2, slip.Setback);

        (double S1, double S2, V2 P)? x = e1.Intersections(e2)
            .OrderBy(h => V2.Dist(h.P, corner))
            .Select(h => ((double, double, V2)?)h)
            .FirstOrDefault();
        if (x is null)
            return;

        double inner = radius + slip.Width;
        GPath circle = new(new[]
        {
            Seg.FromArc(center, inner, 0.0, Math.PI),
            Seg.FromArc(center, inner, Math.PI, Math.PI)
        });

        (double S, V2 P)? Hit(GPath edge) => edge.Intersections(circle)
            .OrderBy(h => V2.Dist(h.P, x.Value.P))
            .Select(h => ((double, V2)?)(h.S1, h.P))
            .FirstOrDefault();

        (double S, V2 P)? p1 = Hit(e1);
        (double S, V2 P)? p2 = Hit(e2);
        if (p1 is null || p2 is null || V2.Dist(center, x.Value.P) <= inner + 0.3)
        {
            ctx.Result.Warnings.Add($"Faixa livre perto de {corner}: nao sobra espaco para a ilha (aumente o raio ou diminua a largura).");
            return;
        }

        GPath side1 = e1.Sub(Math.Min(x.Value.S1, p1.Value.S), Math.Max(x.Value.S1, p1.Value.S));
        GPath side2 = e2.Sub(Math.Min(x.Value.S2, p2.Value.S), Math.Max(x.Value.S2, p2.Value.S));
        if (side1.IsEmpty || side2.IsEmpty)
            return;

        if (V2.Dist(side1.Start, x.Value.P) > V2.Dist(side1.End, x.Value.P))
            side1 = side1.Reverse();
        if (V2.Dist(side2.End, x.Value.P) > V2.Dist(side2.Start, x.Value.P))
            side2 = side2.Reverse();

        double a0 = (side1.End - center).Angle;
        double a1 = (side2.Start - center).Angle;
        double sweep = GMath.Norm(a1 - a0);
        if (sweep > Math.PI)
            sweep -= GMath.TwoPi;

        List<Seg> ring = new(side1.Segs);
        ring.Add(new Seg(side1.End, side2.Start, Math.Tan(sweep / 4.0)));
        ring.AddRange(side2.Segs);
        ctx.Result.Lines.Add(new OutputLine(EdgeLevel.Ilha, ring, closed: true));
    }

    public static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
