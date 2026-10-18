using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Viario;

// =============================================================================
// Nucleo geometrico 2D do modulo Viario (sem dependencia do AutoCAD).
//
// - V2: vetor/ponto 2D.
// - Seg: segmento de reta ou arco, no formato de polilinha (A, B, bulge).
// - GPath: sequencia conectada de segmentos, parametrizada pelo comprimento.
//
// Tudo aqui e puro C# para poder ser testado fora do Civil 3D.
// =============================================================================

internal readonly struct V2
{
    public readonly double X;
    public readonly double Y;

    public V2(double x, double y)
    {
        X = x;
        Y = y;
    }

    public static V2 operator +(V2 a, V2 b) => new(a.X + b.X, a.Y + b.Y);
    public static V2 operator -(V2 a, V2 b) => new(a.X - b.X, a.Y - b.Y);
    public static V2 operator -(V2 a) => new(-a.X, -a.Y);
    public static V2 operator *(V2 a, double k) => new(a.X * k, a.Y * k);
    public static V2 operator *(double k, V2 a) => new(a.X * k, a.Y * k);
    public static V2 operator /(V2 a, double k) => new(a.X / k, a.Y / k);

    public double Length => Math.Sqrt((X * X) + (Y * Y));
    public double Angle => Math.Atan2(Y, X);

    /// <summary>Normal a esquerda (rotacao de +90 graus).</summary>
    public V2 Left => new(-Y, X);

    public V2 Unit()
    {
        double length = Length;
        return length < 1e-15 ? new V2(0, 0) : new V2(X / length, Y / length);
    }

    public static double Dot(V2 a, V2 b) => (a.X * b.X) + (a.Y * b.Y);
    public static double Cross(V2 a, V2 b) => (a.X * b.Y) - (a.Y * b.X);
    public static double Dist(V2 a, V2 b) => (a - b).Length;
    public static V2 FromAngle(double angle) => new(Math.Cos(angle), Math.Sin(angle));

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

internal static class GMath
{
    public const double TwoPi = Math.PI * 2.0;

    /// <summary>Angulo em [0, 2pi).</summary>
    public static double Norm(double angle)
    {
        angle %= TwoPi;
        if (angle < 0)
            angle += TwoPi;
        return angle;
    }
}

/// <summary>Caixa envolvente 2D.</summary>
internal readonly struct Box2
{
    public readonly double MinX, MinY, MaxX, MaxY;

    public Box2(double minX, double minY, double maxX, double maxY)
    {
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }

    public static Box2 Empty => new(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);

    public bool IsEmpty => MinX > MaxX;

    public Box2 Union(Box2 o) =>
        new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));

    public Box2 Add(V2 p) =>
        new(Math.Min(MinX, p.X), Math.Min(MinY, p.Y), Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y));

    public Box2 Inflate(double d) => new(MinX - d, MinY - d, MaxX + d, MaxY + d);

    public bool Intersects(Box2 o) =>
        !IsEmpty && !o.IsEmpty && MinX <= o.MaxX && o.MinX <= MaxX && MinY <= o.MaxY && o.MinY <= MaxY;
}

/// <summary>Segmento de polilinha: reta (bulge 0) ou arco.</summary>
internal sealed class Seg
{
    public V2 A { get; }
    public V2 B { get; }
    public double Bulge { get; }

    public bool IsArc { get; }
    public V2 Center { get; }
    public double Radius { get; }
    public double StartAngle { get; }

    /// <summary>Varredura com sinal (positivo = anti-horario).</summary>
    public double Sweep { get; }

    public double Length { get; }

    public Seg(V2 a, V2 b, double bulge = 0.0)
    {
        A = a;
        B = b;
        V2 chord = b - a;
        double c = chord.Length;

        if (Math.Abs(bulge) < 1e-12 || c < 1e-12)
        {
            Bulge = 0.0;
            IsArc = false;
            Length = c;
            return;
        }

        Bulge = bulge;
        IsArc = true;
        Sweep = 4.0 * Math.Atan(bulge);
        V2 mid = (a + b) * 0.5;
        double offset = (c / 2.0) * (1.0 - (bulge * bulge)) / (2.0 * bulge);
        Center = mid + (chord.Unit().Left * offset);
        Radius = Math.Abs((c / 2.0) * (1.0 + (bulge * bulge)) / (2.0 * bulge));
        StartAngle = (a - Center).Angle;
        Length = Radius * Math.Abs(Sweep);
    }

    public static Seg Line(V2 a, V2 b) => new(a, b, 0.0);

    public static Seg FromArc(V2 center, double radius, double startAngle, double sweep)
    {
        V2 a = center + (V2.FromAngle(startAngle) * radius);
        V2 b = center + (V2.FromAngle(startAngle + sweep) * radius);
        return new Seg(a, b, Math.Tan(sweep / 4.0));
    }

    public V2 PointAt(double t)
    {
        if (!IsArc)
            return A + ((B - A) * t);

        return Center + (V2.FromAngle(StartAngle + (Sweep * t)) * Radius);
    }

    /// <summary>Tangente unitaria no sentido do segmento.</summary>
    public V2 TangentAt(double t)
    {
        if (!IsArc)
            return (B - A).Unit();

        double angle = StartAngle + (Sweep * t);
        V2 radial = V2.FromAngle(angle);
        return Sweep > 0 ? radial.Left : -radial.Left;
    }

    /// <summary>Sub-segmento entre t0 e t1 (aceita valores fora de [0,1] = prolongamento).</summary>
    public Seg Sub(double t0, double t1)
    {
        if (!IsArc)
            return Line(PointAt(t0), PointAt(t1));

        return FromArc(Center, Radius, StartAngle + (Sweep * t0), Sweep * (t1 - t0));
    }

    public Seg Reverse() => new(B, A, -Bulge);

    /// <summary>Paralela a distancia d (positivo = esquerda). Null se o arco degenerar.</summary>
    public Seg? Offset(double d)
    {
        if (!IsArc)
        {
            V2 n = (B - A).Unit().Left * d;
            return Line(A + n, B + n);
        }

        // Arco anti-horario: a esquerda aponta para o centro.
        double r = Sweep > 0 ? Radius - d : Radius + d;
        if (r < 1e-6)
            return null;

        return FromArc(Center, r, StartAngle, Sweep);
    }

    /// <summary>
    /// Parametro de um ponto sobre a reta/circulo suporte. Para arcos, pontos
    /// pouco antes do inicio retornam t negativo (prolongamento para tras).
    /// </summary>
    public double ParamOf(V2 p)
    {
        if (!IsArc)
        {
            V2 d = B - A;
            double len2 = V2.Dot(d, d);
            return len2 < 1e-24 ? 0.0 : V2.Dot(p - A, d) / len2;
        }

        double sweepAbs = Math.Abs(Sweep);
        double delta = GMath.Norm(((p - Center).Angle - StartAngle) * Math.Sign(Sweep));
        if (delta > sweepAbs + ((GMath.TwoPi - sweepAbs) / 2.0))
            delta -= GMath.TwoPi;
        return delta / sweepAbs;
    }

    /// <summary>Ponto mais proximo no segmento (t limitado a [0,1]).</summary>
    public (double T, V2 Point, double Dist) Closest(V2 p)
    {
        double t = Math.Clamp(ParamOf(p), 0.0, 1.0);

        if (IsArc)
        {
            // Fora do arco: escolhe a ponta mais proxima.
            double raw = ParamOf(p);
            if (raw < 0 || raw > 1)
                t = V2.Dist(p, A) <= V2.Dist(p, B) ? 0.0 : 1.0;
        }

        V2 q = PointAt(t);
        return (t, q, V2.Dist(p, q));
    }

    private Box2? _bounds;

    public Box2 Bounds() => _bounds ??= ComputeBounds();

    private Box2 ComputeBounds()
    {
        if (!IsArc)
            return Box2.Empty.Add(A).Add(B);

        Box2 box = Box2.Empty.Add(A).Add(B);
        // Pontos extremos do circulo que estao dentro do arco.
        for (int k = 0; k < 4; k++)
        {
            double angle = k * Math.PI / 2.0;
            double delta = GMath.Norm((angle - StartAngle) * Math.Sign(Sweep));
            if (delta <= Math.Abs(Sweep))
                box = box.Add(Center + (V2.FromAngle(angle) * Radius));
        }

        return box;
    }

    /// <summary>Intersecoes das retas/circulos suporte (sem limitar aos segmentos).</summary>
    public static List<V2> CarrierIntersections(Seg s1, Seg s2)
    {
        List<V2> result = new();

        if (!s1.IsArc && !s2.IsArc)
        {
            V2 d1 = s1.B - s1.A;
            V2 d2 = s2.B - s2.A;
            double den = V2.Cross(d1, d2);
            if (Math.Abs(den) < 1e-12 * d1.Length * d2.Length)
                return result;

            double u = V2.Cross(s2.A - s1.A, d2) / den;
            result.Add(s1.A + (d1 * u));
            return result;
        }

        if (s1.IsArc && s2.IsArc)
            return CircleCircle(s1.Center, s1.Radius, s2.Center, s2.Radius);

        Seg line = s1.IsArc ? s2 : s1;
        Seg arc = s1.IsArc ? s1 : s2;
        return LineCircle(line.A, line.B, arc.Center, arc.Radius);
    }

    private static List<V2> LineCircle(V2 a, V2 b, V2 c, double r)
    {
        List<V2> result = new();
        V2 d = (b - a).Unit();
        if (d.Length < 1e-15)
            return result;

        double proj = V2.Dot(c - a, d);
        V2 foot = a + (d * proj);
        double h2 = (r * r) - V2.Dot(c - foot, c - foot);

        if (h2 < -1e-9 * r * r)
            return result;

        double h = Math.Sqrt(Math.Max(0, h2));
        result.Add(foot - (d * h));
        if (h > 1e-12)
            result.Add(foot + (d * h));
        return result;
    }

    private static List<V2> CircleCircle(V2 c1, double r1, V2 c2, double r2)
    {
        List<V2> result = new();
        double d = V2.Dist(c1, c2);
        if (d < 1e-12 || d > r1 + r2 + 1e-9 || d < Math.Abs(r1 - r2) - 1e-9)
            return result;

        double a = ((r1 * r1) - (r2 * r2) + (d * d)) / (2 * d);
        double h = Math.Sqrt(Math.Max(0, (r1 * r1) - (a * a)));
        V2 u = (c2 - c1) / d;
        V2 p = c1 + (u * a);
        result.Add(p + (u.Left * h));
        if (h > 1e-12)
            result.Add(p - (u.Left * h));
        return result;
    }

    /// <summary>Intersecoes dentro dos dois segmentos: (t1, t2, ponto).</summary>
    public static List<(double T1, double T2, V2 P)> Intersect(Seg s1, Seg s2, double eps = 1e-9)
    {
        List<(double, double, V2)> result = new();
        foreach (V2 p in CarrierIntersections(s1, s2))
        {
            double t1 = s1.ParamOf(p);
            double t2 = s2.ParamOf(p);
            double e1 = eps / Math.Max(s1.Length, 1e-9);
            double e2 = eps / Math.Max(s2.Length, 1e-9);
            if (t1 >= -e1 && t1 <= 1 + e1 && t2 >= -e2 && t2 <= 1 + e2)
                result.Add((Math.Clamp(t1, 0, 1), Math.Clamp(t2, 0, 1), p));
        }

        return result;
    }
}

/// <summary>Caminho conectado de segmentos, parametrizado pelo comprimento s.</summary>
internal sealed class GPath
{
    private readonly double[] _cum;

    // Caixas em cache (o caminho nao muda depois de criado): a do caminho, a de cada
    // segmento e a de cada bloco de segmentos seguidos. Os blocos deixam intersecoes e
    // projecoes em eixos longos (polilinhas de GIS com milhares de vertices) perto de
    // lineares, com o mesmo resultado da varredura completa.
    private const int ChunkSize = 16;
    private Box2? _box;
    private Box2[]? _segBoxes;
    private Box2[]? _chunkBoxes;

    public IReadOnlyList<Seg> Segs { get; }
    public double Length { get; }

    public GPath(IEnumerable<Seg> segs)
    {
        List<Seg> list = segs.Where(s => s.Length > 1e-9).ToList();
        Segs = list;
        _cum = new double[list.Count + 1];
        for (int i = 0; i < list.Count; i++)
            _cum[i + 1] = _cum[i] + list[i].Length;
        Length = _cum[list.Count];
    }

    public bool IsEmpty => Segs.Count == 0;
    public V2 Start => Segs[0].A;
    public V2 End => Segs[^1].B;

    /// <summary>Caminho fechado (inicio coincide com o fim).</summary>
    public bool Closed => Segs.Count > 1 && V2.Dist(Start, End) < 1e-6;

    /// <summary>Trecho de s0 a s1 no sentido do caminho; em caminho fechado, s0 &gt; s1 da a volta.</summary>
    public GPath SubWrap(double s0, double s1)
    {
        if (!Closed || s0 <= s1)
            return Sub(Math.Min(s0, s1), Math.Max(s0, s1));

        List<Seg> segs = new(Sub(s0, Length).Segs);
        segs.AddRange(Sub(0, s1).Segs);
        return new GPath(segs);
    }

    /// <summary>Normaliza s para [0, L) em caminho fechado.</summary>
    public double Wrap(double s)
    {
        if (!Closed || Length <= 0)
            return s;
        s %= Length;
        return s < 0 ? s + Length : s;
    }

    /// <summary>Trecho que contem a estaca s (a curva local naquele ponto).</summary>
    public Seg SegAt(double s) => Segs[Math.Clamp(Locate(s).Index, 0, Segs.Count - 1)];

    public (int Index, double T) Locate(double s)
    {
        if (s <= 0)
            return (0, Segs[0].Length > 0 ? s / Segs[0].Length : 0);
        if (s >= Length)
        {
            int last = Segs.Count - 1;
            return (last, 1.0 + ((s - Length) / Segs[last].Length));
        }

        int lo = 0, hi = Segs.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_cum[mid + 1] < s)
                lo = mid + 1;
            else
                hi = mid;
        }

        return (lo, (s - _cum[lo]) / Segs[lo].Length);
    }

    public double SAt(int index, double t) => _cum[index] + (t * Segs[index].Length);

    public V2 PointAt(double s)
    {
        (int i, double t) = Locate(s);
        return Segs[i].PointAt(t);
    }

    public V2 TangentAt(double s)
    {
        (int i, double t) = Locate(s);
        return Segs[i].TangentAt(Math.Clamp(t, 0, 1));
    }

    /// <summary>Trecho entre s0 e s1 (s0 &lt; s1).</summary>
    public GPath Sub(double s0, double s1)
    {
        s0 = Math.Clamp(s0, 0, Length);
        s1 = Math.Clamp(s1, 0, Length);
        if (s1 - s0 < 1e-9)
            return new GPath(Array.Empty<Seg>());

        (int i0, double t0) = Locate(s0);
        (int i1, double t1) = Locate(s1);

        // Ponto exatamente no fim de um segmento: comeca no proximo.
        if (t0 >= 1.0 - 1e-12 && i0 < Segs.Count - 1)
        {
            i0++;
            t0 = 0;
        }

        List<Seg> result = new();
        for (int i = i0; i <= i1; i++)
        {
            double a = i == i0 ? t0 : 0.0;
            double b = i == i1 ? t1 : 1.0;
            if (b - a > 1e-12)
                result.Add(a <= 1e-12 && b >= 1 - 1e-12 ? Segs[i] : Segs[i].Sub(a, b));
        }

        return new GPath(result);
    }

    public GPath Reverse() => new(Segs.Reverse().Select(s => s.Reverse()));

    /// <summary>Ponto do caminho mais proximo de p.</summary>
    public (double S, V2 Point, double Dist) Project(V2 p)
    {
        double bestS = 0, bestD = double.MaxValue;
        V2 bestP = default;
        int n = Segs.Count;
        if (n <= 2 * ChunkSize)
        {
            for (int i = 0; i < n; i++)
            {
                (double t, V2 q, double d) = Segs[i].Closest(p);
                if (d < bestD)
                {
                    bestD = d;
                    bestS = SAt(i, t);
                    bestP = q;
                }
            }

            return (bestS, bestP, bestD);
        }

        // Limite superior pelo bloco mais perto; depois a varredura na ordem, pulando os
        // blocos que nao podem ter nada mais perto que o limite (mesmo desempate).
        Box2[] chunks = ChunkBoxes;
        int nearest = 0;
        double nearestBox = double.MaxValue;
        for (int c = 0; c < chunks.Length; c++)
        {
            double d = BoxDistance(chunks[c], p);
            if (d < nearestBox)
            {
                nearestBox = d;
                nearest = c;
            }
        }

        double upper = double.MaxValue;
        for (int i = nearest * ChunkSize, end = Math.Min(n, i + ChunkSize); i < end; i++)
            upper = Math.Min(upper, Segs[i].Closest(p).Dist);
        double limit = upper + 1e-6 + (upper * 1e-9);

        for (int c = 0; c < chunks.Length; c++)
        {
            if (BoxDistance(chunks[c], p) > limit)
                continue;

            for (int i = c * ChunkSize, end = Math.Min(n, i + ChunkSize); i < end; i++)
            {
                (double t, V2 q, double d) = Segs[i].Closest(p);
                if (d < bestD)
                {
                    bestD = d;
                    bestS = SAt(i, t);
                    bestP = q;
                }
            }
        }

        return (bestS, bestP, bestD);
    }

    /// <summary>Primeiro e ultimo segmento cuja caixa toca a janela ((-1, -1) se nenhum).</summary>
    public (int First, int Last) SegmentRange(Box2 window)
    {
        int first = -1, last = -1;
        Box2[] chunks = ChunkBoxes;
        Box2[] boxes = SegBoxes;
        for (int c = 0; c < chunks.Length; c++)
        {
            if (!chunks[c].Intersects(window))
                continue;
            for (int i = c * ChunkSize, end = Math.Min(boxes.Length, i + ChunkSize); i < end; i++)
            {
                if (!boxes[i].Intersects(window))
                    continue;
                if (first < 0)
                    first = i;
                last = i;
            }
        }

        return (first, last);
    }

    private static double BoxDistance(Box2 b, V2 p)
    {
        double dx = Math.Max(Math.Max(b.MinX - p.X, p.X - b.MaxX), 0.0);
        double dy = Math.Max(Math.Max(b.MinY - p.Y, p.Y - b.MaxY), 0.0);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public Box2 Bounds()
    {
        if (_box is Box2 cached)
            return cached;

        Box2 box = Box2.Empty;
        foreach (Box2 b in SegBoxes)
            box = box.Union(b);
        _box = box;
        return box;
    }

    /// <summary>Caixa de cada segmento (em cache).</summary>
    private Box2[] SegBoxes
    {
        get
        {
            if (_segBoxes is null)
            {
                Box2[] boxes = new Box2[Segs.Count];
                for (int i = 0; i < boxes.Length; i++)
                    boxes[i] = Segs[i].Bounds();
                _segBoxes = boxes;
            }

            return _segBoxes;
        }
    }

    /// <summary>Caixa de cada bloco de ChunkSize segmentos seguidos (em cache).</summary>
    private Box2[] ChunkBoxes
    {
        get
        {
            if (_chunkBoxes is null)
            {
                Box2[] segs = SegBoxes;
                Box2[] chunks = new Box2[(segs.Length + ChunkSize - 1) / ChunkSize];
                for (int c = 0; c < chunks.Length; c++)
                {
                    Box2 box = Box2.Empty;
                    for (int i = c * ChunkSize, end = Math.Min(segs.Length, i + ChunkSize); i < end; i++)
                        box = box.Union(segs[i]);
                    chunks[c] = box;
                }

                _chunkBoxes = chunks;
            }

            return _chunkBoxes;
        }
    }

    /// <summary>Intersecoes com outro caminho: (s neste, s no outro, ponto).</summary>
    public List<(double S1, double S2, V2 P)> Intersections(GPath other, double eps = 1e-7)
    {
        List<(double, double, V2)> result = new();
        if (IsEmpty || other.IsEmpty || !Bounds().Inflate(eps).Intersects(other.Bounds()))
            return result;

        Box2[] boxesA = SegBoxes;
        Box2[] boxesB = other.SegBoxes;

        void Test(int i, int j)
        {
            foreach ((double t1, double t2, V2 p) in Seg.Intersect(Segs[i], other.Segs[j], eps))
            {
                double s1 = SAt(i, t1);
                double s2 = other.SAt(j, t2);
                bool duplicate = result.Any(r => V2.Dist(r.Item3, p) < 1e-6);
                if (!duplicate)
                    result.Add((s1, s2, p));
            }
        }

        if ((long)Segs.Count * other.Segs.Count <= 4L * ChunkSize * ChunkSize)
        {
            for (int i = 0; i < Segs.Count; i++)
            {
                Box2 boxA = boxesA[i].Inflate(eps);
                for (int j = 0; j < other.Segs.Count; j++)
                {
                    if (boxA.Intersects(boxesB[j].Inflate(eps)))
                        Test(i, j);
                }
            }

            return result;
        }

        // Pares de segmentos pelos blocos que se tocam, testados na mesma ordem (i, j) da
        // varredura completa: o resultado (e o descarte de repetidos) e o mesmo.
        Box2[] chunksA = ChunkBoxes;
        Box2[] chunksB = other.ChunkBoxes;
        double margin = (4.0 * eps) + 1e-6;
        Box2 otherBox = other.Bounds().Inflate(margin);
        List<(int I, int J)> pairs = new();
        for (int a = 0; a < chunksA.Length; a++)
        {
            Box2 ca = chunksA[a].Inflate(margin);
            if (!ca.Intersects(otherBox))
                continue;

            for (int b = 0; b < chunksB.Length; b++)
            {
                if (!ca.Intersects(chunksB[b]))
                    continue;

                int j0 = b * ChunkSize, j1 = Math.Min(boxesB.Length, j0 + ChunkSize);
                for (int i = a * ChunkSize, i1 = Math.Min(boxesA.Length, i + ChunkSize); i < i1; i++)
                {
                    Box2 boxA = boxesA[i].Inflate(eps);
                    for (int j = j0; j < j1; j++)
                    {
                        if (boxA.Intersects(boxesB[j].Inflate(eps)))
                            pairs.Add((i, j));
                    }
                }
            }
        }

        pairs.Sort();
        foreach ((int i, int j) in pairs)
            Test(i, j);

        return result;
    }

    /// <summary>Caminho com prolongamentos retos no inicio e/ou no fim.</summary>
    public GPath Extended(double startLength, double endLength)
    {
        if (Closed)
            return this;

        List<Seg> list = new();
        if (startLength > 0)
        {
            V2 t = TangentAt(0);
            list.Add(Seg.Line(Start - (t * startLength), Start));
        }

        list.AddRange(Segs);

        if (endLength > 0)
        {
            V2 t = TangentAt(Length);
            list.Add(Seg.Line(End, End + (t * endLength)));
        }

        return new GPath(list);
    }

    /// <summary>
    /// Paralela a distancia d (positivo = esquerda). Juntas entre segmentos:
    /// prolonga/apara ate a intersecao (canto vivo); se nao houver intersecao
    /// razoavel, liga com uma reta.
    /// </summary>
    public GPath Offset(double d)
    {
        List<Seg> offs = new();
        List<V2> originalVertex = new();

        foreach (Seg s in Segs)
        {
            Seg? o = s.Offset(d);
            if (o is null)
                continue;
            offs.Add(o);
            originalVertex.Add(s.A);
        }

        if (offs.Count <= 1)
            return new GPath(offs);

        List<Seg> result = new() { offs[0] };
        double limit = Math.Max(Math.Abs(d) * 10.0, 1.0);

        for (int i = 1; i < offs.Count; i++)
        {
            (Seg? a, Seg? link, Seg b) = Join(result[^1], offs[i], originalVertex[i], limit);
            result[^1] = a!;
            if (link is not null)
                result.Add(link);
            result.Add(b);
        }

        // Fechamento: junta o ultimo com o primeiro.
        if (Closed && result.Count > 1)
        {
            (Seg? a, Seg? link, Seg b) = Join(result[^1], result[0], Segs[0].A, limit);
            result[^1] = a!;
            result[0] = b;
            if (link is not null)
                result.Add(link);
        }

        return new GPath(result);
    }

    /// <summary>Junta dois segmentos paralelos consecutivos (apara/prolonga ou liga com reta).</summary>
    private static (Seg A, Seg? Link, Seg B) Join(Seg prev, Seg next, V2 vertex, double limit)
    {
        if (V2.Dist(prev.B, next.A) < 1e-7)
            return (prev, null, next);

        V2 guess = (prev.B + next.A) * 0.5;
        V2? best = null;
        double bestDist = double.MaxValue;

        foreach (V2 p in Seg.CarrierIntersections(prev, next))
        {
            double dist = V2.Dist(p, guess);
            if (dist < bestDist && V2.Dist(p, vertex) <= limit)
            {
                bestDist = dist;
                best = p;
            }
        }

        if (best is V2 x)
        {
            double tp = prev.ParamOf(x);
            double tn = next.ParamOf(x);
            if (tp > 1e-9 && tn < 1.0 - 1e-9)
                return (prev.Sub(0, tp), null, next.Sub(tn, 1));
        }

        return (prev, Seg.Line(prev.B, next.A), next);
    }
}
