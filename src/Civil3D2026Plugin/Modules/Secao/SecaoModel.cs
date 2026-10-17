using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Secao;

/// <summary>Ponto em planta (C# puro, sem AutoCAD).</summary>
internal readonly record struct P2(double X, double Y)
{
    public static P2 operator -(P2 a, P2 b) => new(a.X - b.X, a.Y - b.Y);
    public static P2 operator +(P2 a, P2 b) => new(a.X + b.X, a.Y + b.Y);
    public static P2 operator *(P2 a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt((X * X) + (Y * Y));
    public P2 Unit() => Length < 1e-12 ? new P2(1, 0) : this * (1.0 / Length);
    public P2 Left => new(-Y, X);
    public static double Dot(P2 a, P2 b) => (a.X * b.X) + (a.Y * b.Y);
    public static double Cross(P2 a, P2 b) => (a.X * b.Y) - (a.Y * b.X);
    public static double Dist(P2 a, P2 b) => (a - b).Length;
}

/// <summary>Ponto 3D (C# puro).</summary>
internal readonly record struct P3(double X, double Y, double Z)
{
    public P2 XY => new(X, Y);
}

/// <summary>
/// Caixa de corte: a linha em planta (um ou mais trechos retos) define o plano
/// vertical do corte; <see cref="Frente"/> e <see cref="Atras"/> sao as
/// profundidades para cada lado; <see cref="ZMin"/>/<see cref="ZMax"/> a faixa
/// de elevacao mostrada (null = automatica).
///
/// Convencao de vista: o observador fica do lado direito da linha (olhando no
/// sentido dela) e olha para a esquerda; o inicio da linha aparece a esquerda da
/// tela. "Frente" e o lado esquerdo da linha (alem do plano, visto pelo
/// observador); "atras" e o lado direito (entre o observador e o plano).
/// </summary>
internal sealed class CutBox
{
    public string Name = "Corte";
    public List<P2> Line = new();
    public double Frente = 10.0;
    public double Atras = 5.0;
    public double? ZMin;
    public double? ZMax;
    public double Exagero = 1.0;

    public double Length
    {
        get
        {
            double l = 0;
            for (int i = 1; i < Line.Count; i++)
                l += P2.Dist(Line[i - 1], Line[i]);
            return l;
        }
    }

    public bool IsValid => Line.Count >= 2 && Length > 1e-6;

    /// <summary>Comprimento acumulado ate o inicio de cada trecho.</summary>
    public double[] Starts()
    {
        double[] s = new double[Math.Max(Line.Count - 1, 0)];
        double acc = 0;
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = acc;
            acc += P2.Dist(Line[i], Line[i + 1]);
        }

        return s;
    }

    /// <summary>
    /// Projeta um ponto em planta sobre a linha de corte: distancia ao longo (S) e
    /// afastamento lateral com sinal (D, positivo = frente/esquerda). Usa o trecho
    /// mais proximo; fora das pontas, Inside = false.
    /// </summary>
    public (double S, double D, bool Inside) Project(P2 p)
    {
        double[] starts = Starts();
        double bestS = 0, bestD = 0, bestDist = double.MaxValue;
        bool inside = false;
        for (int i = 0; i + 1 < Line.Count; i++)
        {
            P2 a = Line[i], b = Line[i + 1];
            P2 ab = b - a;
            double len = ab.Length;
            if (len < 1e-9)
                continue;
            P2 t = ab * (1.0 / len);
            double along = P2.Dot(p - a, t);
            double lateral = P2.Cross(t, p - a); // positivo a esquerda
            bool within = along >= -1e-9 && along <= len + 1e-9;
            double clamped = Math.Clamp(along, 0, len);
            P2 foot = a + (t * clamped);
            double dist = P2.Dist(foot, p);
            if (dist < bestDist - 1e-9 || (Math.Abs(dist - bestDist) <= 1e-9 && within && !inside))
            {
                bestDist = dist;
                bestS = starts[i] + clamped;
                bestD = lateral;
                inside = within;
            }
        }

        return (bestS, bestD, inside);
    }

    /// <summary>
    /// Copia sem limite de profundidade (faixa de vista "tudo"): a frente e atras vao
    /// longe; a linha, as elevacoes e o exagero ficam.
    /// </summary>
    public CutBox Unlimited() => new() { Name = Name, Line = Line, Frente = 1e5, Atras = 1e5, ZMin = ZMin, ZMax = ZMax, Exagero = Exagero };

    /// <summary>Normal (para a frente) do trecho mais proximo do ponto: direcao em que se olha ali.</summary>
    public P2 NormalAt(P2 p)
    {
        P2 best = Line.Count >= 2 ? (Line[1] - Line[0]).Unit().Left : new P2(0, 1);
        double bestDist = double.MaxValue;
        for (int i = 0; i + 1 < Line.Count; i++)
        {
            P2 a = Line[i], b = Line[i + 1];
            double len = P2.Dist(a, b);
            if (len < 1e-9)
                continue;
            P2 t = (b - a) * (1.0 / len);
            double along = Math.Clamp(P2.Dot(p - a, t), 0, len);
            double d = P2.Dist(a + (t * along), p);
            if (d < bestDist)
            {
                bestDist = d;
                best = t.Left;
            }
        }

        return best;
    }

    /// <summary>O ponto esta dentro da caixa (em planta)?</summary>
    public bool Contains(P2 p)
    {
        (_, double d, bool inside) = Project(p);
        return inside && d <= Frente + 1e-9 && d >= -Atras - 1e-9;
    }

    /// <summary>Caixa envolvente em planta (linha mais as profundidades), para filtrar objetos.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds(double extra = 0.0)
    {
        double r = Math.Max(Frente, Atras) + extra;
        double minX = Line.Min(p => p.X) - r, maxX = Line.Max(p => p.X) + r;
        double minY = Line.Min(p => p.Y) - r, maxY = Line.Max(p => p.Y) + r;
        return (minX, minY, maxX, maxY);
    }

    /// <summary>Os quatro cantos (ou mais) do contorno da caixa em planta, para desenhar.</summary>
    public List<P2> Outline()
    {
        List<P2> left = OffsetLine(Frente), right = OffsetLine(-Atras);
        right.Reverse();
        left.AddRange(right);
        return left;
    }

    /// <summary>Paralela simples da linha (por vertice, bissetriz).</summary>
    public List<P2> OffsetLine(double d)
    {
        List<P2> result = new();
        for (int i = 0; i < Line.Count; i++)
        {
            P2 n;
            if (i == 0)
                n = (Line[1] - Line[0]).Unit().Left;
            else if (i == Line.Count - 1)
                n = (Line[i] - Line[i - 1]).Unit().Left;
            else
            {
                P2 n1 = (Line[i] - Line[i - 1]).Unit().Left, n2 = (Line[i + 1] - Line[i]).Unit().Left;
                P2 bis = (n1 + n2);
                if (bis.Length < 1e-9)
                    n = n1;
                else
                {
                    bis = bis.Unit();
                    double cos = P2.Dot(bis, n1);
                    n = bis * (1.0 / Math.Max(cos, 0.2));
                }
            }

            result.Add(Line[i] + (n * d));
        }

        return result;
    }

    /// <summary>
    /// Intersecao do segmento 3D a-b com o plano vertical do corte (todos os trechos).
    /// Devolve (S, Z) do ponto de cruzamento, ou null. Segmentos que apenas tocam
    /// o plano numa ponta contam uma vez (t em [0,1)).
    /// </summary>
    public IEnumerable<(double S, double Z)> Cross(P3 a, P3 b)
    {
        double[] starts = Starts();
        for (int i = 0; i + 1 < Line.Count; i++)
        {
            P2 p = Line[i], q = Line[i + 1];
            P2 t = (q - p).Unit();
            double len = P2.Dist(p, q);
            double da = P2.Cross(t, a.XY - p);
            double db = P2.Cross(t, b.XY - p);
            if ((da > 0 && db > 0) || (da < 0 && db < 0))
                continue;
            if (da == 0 && db == 0)
                continue; // no plano: nao e cruzamento
            if (db == 0)
                continue; // ponta final sobre o plano: conta como inicio do segmento seguinte
            double u = da / (da - db); // em [0, 1)
            double x = a.X + ((b.X - a.X) * u), y = a.Y + ((b.Y - a.Y) * u), z = a.Z + ((b.Z - a.Z) * u);
            double along = P2.Dot(new P2(x, y) - p, t);
            if (along < -1e-6 || along > len + 1e-6)
                continue;
            yield return (starts[i] + along, z);
        }
    }

    /// <summary>
    /// Intersecao de um triangulo 3D com o plano do corte: um segmento (S,Z)-(S,Z),
    /// se houver, por trecho da linha.
    /// </summary>
    public IEnumerable<((double S, double Z) A, (double S, double Z) B)> CutTriangle(P3 a, P3 b, P3 c)
    {
        List<(double S, double Z)> hits = new();
        foreach ((P3 u, P3 v) in new[] { (a, b), (b, c), (c, a) })
            hits.AddRange(Cross(u, v));
        if (hits.Count < 2)
            yield break;
        // Pode haver 2 (caso normal) ou 3 (vertice no plano): usa os dois mais afastados.
        (double S, double Z) p0 = hits[0], p1 = hits[1];
        double best = Math.Abs(p0.S - p1.S) + Math.Abs(p0.Z - p1.Z);
        for (int i = 0; i < hits.Count; i++)
        {
            for (int j = i + 1; j < hits.Count; j++)
            {
                double d = Math.Abs(hits[i].S - hits[j].S) + Math.Abs(hits[i].Z - hits[j].Z);
                if (d > best)
                {
                    best = d;
                    p0 = hits[i];
                    p1 = hits[j];
                }
            }
        }

        if (best > 1e-6)
            yield return (p0, p1);
    }
}

/// <summary>Como um item aparece (escolha do usuario, por item).</summary>
internal enum ItemStyle
{
    Normal = 0,
    Destaque = 1,
    Hachura = 2,
    Oculto = 3
}

internal enum ItemKind
{
    Superficie,
    Corredor,
    Tubo,
    Estrutura,
    Solido,
    FeatureLine,
    Polilinha3d,
    Ponto,
    Bloco
}

/// <summary>Um objeto do desenho ja levado ao plano do corte (coordenadas S ao longo, Z elevacao).</summary>
internal sealed class SectionItem
{
    public ItemKind Kind;
    public string Name = "";
    public string Layer = "";
    /// <summary>Projetado da profundidade (nao cortado): desenha apagado.</summary>
    public bool Ghost;
    /// <summary>Afastamento do plano (fantasmas), para ordenar e informar.</summary>
    public double Depth;
    public List<List<(double S, double Z)>> Polylines = new();
    public List<(double S, double Z, double R)> Circles = new();
    public List<(double S, double Z, string Label)> Points = new();
    public Dictionary<string, string> Info = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Indice de cor sugerido (ACI) quando o objeto tem cor propria; 0 = pela classe.</summary>
    public int ColorIndex;
    /// <summary>Regioes fechadas do item (contorno do corredor, solidos, estruturas, tubos), para hachurar.</summary>
    public List<List<(double S, double Z)>> Fills = new();

    /// <summary>Chave estavel do item entre recalculos (para destaque/hachura escolhidos pelo usuario).</summary>
    public string Key => Kind + "|" + Name + (Ghost ? "|g" : "");

    public bool IsEmpty => Polylines.All(p => p.Count == 0) && Circles.Count == 0 && Points.Count == 0;

    public (double Min, double Max)? ZRange()
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (List<(double S, double Z)> pl in Polylines)
            foreach ((_, double z) in pl)
            {
                min = Math.Min(min, z);
                max = Math.Max(max, z);
            }

        foreach ((_, double z, double r) in Circles)
        {
            min = Math.Min(min, z - r);
            max = Math.Max(max, z + r);
        }

        foreach ((_, double z, _) in Points)
        {
            min = Math.Min(min, z);
            max = Math.Max(max, z);
        }

        return min <= max ? (min, max) : null;
    }
}

/// <summary>Resultado de um corte: itens e faixa de elevacao dos dados.</summary>
internal sealed class SectionData
{
    public CutBox Box = new();
    public List<SectionItem> Items = new();
    public List<string> Warnings = new();
    public TimeSpan Elapsed;
    /// <summary>Objetos examinados por tipo (diagnostico: o que havia perto da caixa).</summary>
    public SortedDictionary<string, int> Scanned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resumo em uma linha: o que foi lido, cortado e projetado.</summary>
    public string Summary()
    {
        string lidos = Scanned.Count(kv => kv.Value > 0) == 0 ? "nenhum objeto do Civil/3D perto da caixa"
            : string.Join(", ", Scanned.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value} {kv.Key}")) +
              string.Concat(Scanned.Where(kv => kv.Value == 0).Select(kv => " " + kv.Key));
        int cut = Items.Count(i => !i.Ghost), ghost = Items.Count(i => i.Ghost);
        string porTipo = string.Join(", ", Items.Where(i => !i.Ghost).GroupBy(i => i.Kind).Select(g => $"{g.Count()} {g.Key}"));
        return $"lidos: {lidos} | cortados: {cut}{(porTipo.Length > 0 ? " (" + porTipo + ")" : "")} | na profundidade: {ghost}" +
               (Warnings.Count > 0 ? $" | {Warnings.Count} aviso(s)" : "");
    }

    public double Length => Box.Length;

    /// <summary>Posicao do eixo ao longo da linha (secao na estaca do corredor); NaN se nao houver.</summary>
    public double AxisS = double.NaN;

    public (double Min, double Max) DataZ()
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (SectionItem it in Items)
        {
            if (it.ZRange() is (double a, double b))
            {
                min = Math.Min(min, a);
                max = Math.Max(max, b);
            }
        }

        if (min > max)
            return (0, 10);
        if (max - min < 0.5)
        {
            min -= 1;
            max += 1;
        }

        return (min, max);
    }

    /// <summary>Faixa de elevacao efetiva: a da caixa, ou a dos dados com folga.</summary>
    public (double Min, double Max) ViewZ()
    {
        (double dmin, double dmax) = DataZ();
        double pad = Math.Max(0.5, (dmax - dmin) * 0.08);
        return (Box.ZMin ?? dmin - pad, Box.ZMax ?? dmax + pad);
    }
}

/// <summary>Utilidades geometricas do corte (C# puro, testaveis).</summary>
internal static class SecaoGeometry
{
    /// <summary>
    /// Corta a "fita" entre duas polilinhas 3D (mesmo numero de pontos, p.ex. o mesmo
    /// link em duas estacas vizinhas) com o plano: triangula cada quadrilatero e
    /// devolve os segmentos.
    /// </summary>
    public static List<((double S, double Z) A, (double S, double Z) B)> CutRibbon(CutBox box, IReadOnlyList<P3> a, IReadOnlyList<P3> b)
    {
        List<((double S, double Z), (double S, double Z))> segs = new();
        int n = Math.Min(a.Count, b.Count);
        for (int k = 0; k + 1 < n; k++)
        {
            segs.AddRange(box.CutTriangle(a[k], a[k + 1], b[k + 1]));
            segs.AddRange(box.CutTriangle(a[k], b[k + 1], b[k]));
        }

        return segs;
    }

    /// <summary>Encadeia segmentos (S,Z) pelas pontas em polilinhas (pontas indexadas numa grade).</summary>
    public static List<List<(double S, double Z)>> Chain(List<((double S, double Z) A, (double S, double Z) B)> segs, double tol = 1e-4)
    {
        List<List<(double S, double Z)>> result = new();
        bool[] used = new bool[segs.Count];
        double cell = Math.Max(tol * 2, 1e-9);
        Dictionary<(long, long), List<int>> grid = new();
        (long, long) Cell((double S, double Z) p) => ((long)Math.Floor(p.S / cell), (long)Math.Floor(p.Z / cell));
        for (int i = 0; i < segs.Count; i++)
        {
            foreach ((int end, (double S, double Z) p) in new[] { (0, segs[i].A), (1, segs[i].B) })
            {
                (long, long) c = Cell(p);
                if (!grid.TryGetValue(c, out List<int>? list))
                    grid[c] = list = new List<int>();
                list.Add((i * 2) + end);
            }
        }

        // Ponta livre (de segmento nao usado) perto de p: devolve a outra ponta do segmento.
        bool Next((double S, double Z) p, out (double S, double Z) other)
        {
            (long cx, long cz) = Cell(p);
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue((cx + dx, cz + dz), out List<int>? list))
                        continue;
                    foreach (int code in list)
                    {
                        int i = code / 2;
                        if (used[i])
                            continue;
                        (double S, double Z) q = code % 2 == 0 ? segs[i].A : segs[i].B;
                        if (Math.Abs(q.S - p.S) > tol || Math.Abs(q.Z - p.Z) > tol)
                            continue;
                        used[i] = true;
                        other = code % 2 == 0 ? segs[i].B : segs[i].A;
                        return true;
                    }
                }
            }

            other = default;
            return false;
        }

        for (int i = 0; i < segs.Count; i++)
        {
            if (used[i])
                continue;
            used[i] = true;
            List<(double S, double Z)> chain = new() { segs[i].A, segs[i].B };
            while (Next(chain[^1], out (double S, double Z) tail))
                chain.Add(tail);
            List<(double S, double Z)> head = new();
            while (Next(head.Count > 0 ? head[^1] : chain[0], out (double S, double Z) h))
                head.Add(h);
            if (head.Count > 0)
            {
                head.Reverse();
                head.AddRange(chain);
                chain = head;
            }

            result.Add(chain);
        }

        return result;
    }

    /// <summary>Projeta uma polilinha 3D no plano (S,Z), mantendo so a parte dentro da caixa; corta onde sai.</summary>
    public static List<List<(double S, double Z)>> ProjectInside(CutBox box, IReadOnlyList<P3> pts, out double meanDepth)
    {
        List<List<(double S, double Z)>> result = new();
        List<(double S, double Z)> current = new();
        double depthSum = 0;
        int depthN = 0;
        foreach (P3 p in pts)
        {
            (double s, double d, bool inside) = box.Project(p.XY);
            bool ok = inside && d <= box.Frente + 1e-9 && d >= -box.Atras - 1e-9;
            if (ok)
            {
                current.Add((s, p.Z));
                depthSum += d;
                depthN++;
            }
            else if (current.Count > 0)
            {
                result.Add(current);
                current = new List<(double S, double Z)>();
            }
        }

        if (current.Count > 0)
            result.Add(current);
        meanDepth = depthN == 0 ? 0 : depthSum / depthN;
        return result;
    }

    /// <summary>Recorta uma polilinha (S,Z) ao intervalo [0, L] em S.</summary>
    public static List<List<(double S, double Z)>> ClipS(List<(double S, double Z)> pl, double length)
    {
        List<List<(double S, double Z)>> result = new();
        List<(double S, double Z)> cur = new();
        for (int i = 0; i < pl.Count; i++)
        {
            (double s, double z) = pl[i];
            bool inside = s >= -1e-9 && s <= length + 1e-9;
            if (i > 0)
            {
                (double ps, double pz) = pl[i - 1];
                bool pin = ps >= -1e-9 && ps <= length + 1e-9;
                if (pin != inside)
                {
                    double lim = s < 0 || ps < 0 ? 0 : length;
                    double u = (lim - ps) / (s - ps);
                    (double, double) x = (lim, pz + ((z - pz) * u));
                    if (pin)
                    {
                        cur.Add(x);
                        result.Add(cur);
                        cur = new List<(double S, double Z)>();
                    }
                    else
                    {
                        cur.Add(x);
                    }
                }
            }

            if (inside)
                cur.Add((s, z));
        }

        if (cur.Count > 1)
            result.Add(cur);
        return result;
    }

    /// <summary>
    /// Contorno (envoltoria) de um conjunto de polilinhas (S,Z): topo da esquerda para a
    /// direita e fundo de volta, por trechos continuos em S. Paredes verticais (face do
    /// meio-fio) sao preservadas avaliando os limites a esquerda e a direita de cada
    /// abscissa. Devolve poligonos fechados (primeiro ponto repetido no fim).
    /// </summary>
    public static List<List<(double S, double Z)>> Envelope(IEnumerable<List<(double S, double Z)>> polylines, double tol = 1e-6)
    {
        List<(double S0, double Z0, double S1, double Z1)> segs = new();
        foreach (List<(double S, double Z)> pl in polylines)
        {
            for (int i = 0; i + 1 < pl.Count; i++)
            {
                (double s0, double z0) = pl[i];
                (double s1, double z1) = pl[i + 1];
                if (s1 < s0)
                    (s0, z0, s1, z1) = (s1, z1, s0, z0);
                segs.Add((s0, z0, s1, z1));
            }
        }

        List<List<(double S, double Z)>> result = new();
        if (segs.Count == 0)
            return result;

        List<double> xs = segs.SelectMany(g => new[] { g.S0, g.S1 }).OrderBy(x => x).ToList();
        List<double> breaks = new();
        foreach (double x in xs)
        {
            if (breaks.Count == 0 || x - breaks[^1] > tol)
                breaks.Add(x);
        }

        // Faixa (menor e maior Z) dos segmentos nao verticais que chegam (side -1) ou saem (+1) de x,
        // e dos verticais (side 0) que estao em x.
        (double Lo, double Hi)? Range(double x, int side)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach ((double s0, double z0, double s1, double z1) in segs)
            {
                bool vertical = s1 - s0 <= tol;
                if (side == 0)
                {
                    if (vertical && Math.Abs(x - s0) <= tol)
                    {
                        lo = Math.Min(lo, Math.Min(z0, z1));
                        hi = Math.Max(hi, Math.Max(z0, z1));
                    }

                    continue;
                }

                if (vertical)
                    continue;
                bool covers = side < 0 ? (s0 < x - tol && x <= s1 + tol) : (s0 - tol <= x && x < s1 - tol);
                if (!covers)
                    continue;
                double u = Math.Clamp((x - s0) / (s1 - s0), 0, 1);
                double z = z0 + ((z1 - z0) * u);
                lo = Math.Min(lo, z);
                hi = Math.Max(hi, z);
            }

            return lo <= hi ? (lo, hi) : null;
        }

        bool Covered(double a, double b)
        {
            double m = (a + b) / 2;
            return segs.Any(g => g.S1 - g.S0 > tol && g.S0 <= m && m <= g.S1);
        }

        List<(double S, double Z)> top = new(), bottom = new();
        void Add(List<(double S, double Z)> list, double x, double z)
        {
            if (list.Count == 0 || Math.Abs(list[^1].S - x) > tol || Math.Abs(list[^1].Z - z) > tol)
                list.Add((x, z));
        }

        void Close()
        {
            if (top.Count >= 2)
            {
                List<(double S, double Z)> poly = new(top);
                bottom.Reverse();
                poly.AddRange(bottom);
                poly.Add(poly[0]);
                result.Add(poly);
            }

            top = new List<(double S, double Z)>();
            bottom = new List<(double S, double Z)>();
        }

        for (int i = 0; i < breaks.Count; i++)
        {
            double x = breaks[i];
            bool inRun = top.Count > 0;
            bool continues = i + 1 < breaks.Count && Covered(x, breaks[i + 1]);
            (double Lo, double Hi)? left = inRun ? Range(x, -1) : null;
            (double Lo, double Hi)? right = continues ? Range(x, +1) : null;
            (double Lo, double Hi)? wall = Range(x, 0);
            if (left is null && right is null && wall is null)
                continue;

            double sideHi = Math.Max(left?.Hi ?? double.MinValue, right?.Hi ?? double.MinValue);
            double sideLo = Math.Min(left?.Lo ?? double.MaxValue, right?.Lo ?? double.MaxValue);

            if (left is (double llo, double lhi))
            {
                Add(top, x, lhi);
                Add(bottom, x, llo);
            }

            if (wall is (double wlo, double whi))
            {
                if (whi > sideHi + tol || (left is null && right is not null))
                    Add(top, x, Math.Max(whi, right?.Hi ?? whi));
                if (wlo < sideLo - tol || (left is null && right is not null))
                    Add(bottom, x, Math.Min(wlo, right?.Lo ?? wlo));
            }

            if (right is (double rlo, double rhi))
            {
                Add(top, x, rhi);
                Add(bottom, x, rlo);
            }
            else if (inRun)
            {
                Close();
            }
        }

        Close();
        // Envoltoria sem espessura (uma linha so): nao e regiao.
        result.RemoveAll(p => Math.Abs(Area(p)) < 1e-6);
        return result;
    }

    /// <summary>
    /// Contorno externo verdadeiro de um conjunto de polilinhas (S,Z), como grafo plano:
    /// junta as pontas (tolerancia), parte os trechos nos cruzamentos e nos "T", e
    /// percorre a face externa de cada parte conectada. Ao contrario da envoltoria
    /// (topo/fundo por abscissa), segue reentrancias - vigas I sob o tabuleiro, abas,
    /// consolos. Devolve poligonos fechados (anti-horario), sem os de area nula.
    /// </summary>
    public static List<List<(double S, double Z)>> Outline(IEnumerable<List<(double S, double Z)>> polylines, double tol = 1e-4)
    {
        // Vertices (com junta por grade de tolerancia).
        List<(double S, double Z)> verts = new();
        Dictionary<(long, long), int> grid = new();
        int Vertex((double S, double Z) p)
        {
            long gx = (long)Math.Floor(p.S / tol), gz = (long)Math.Floor(p.Z / tol);
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dz = -1; dz <= 1; dz++)
                {
                    if (grid.TryGetValue((gx + dx, gz + dz), out int id) && Math.Abs(verts[id].S - p.S) <= tol && Math.Abs(verts[id].Z - p.Z) <= tol)
                        return id;
                }
            }

            verts.Add(p);
            grid[(gx, gz)] = verts.Count - 1;
            return verts.Count - 1;
        }

        List<(int A, int B)> raw = new();
        foreach (List<(double S, double Z)> pl in polylines)
        {
            for (int i = 0; i + 1 < pl.Count; i++)
            {
                int a = Vertex(pl[i]), b = Vertex(pl[i + 1]);
                if (a != b)
                    raw.Add((a, b));
            }
        }

        if (raw.Count < 3)
            return new List<List<(double S, double Z)>>();

        // Parte cada trecho nos cruzamentos com os outros e nos vertices que caem sobre
        // ele ("T"); varredura por S para nao testar todos contra todos.
        List<List<(double T, int V)>> cuts = raw.Select(_ => new List<(double T, int V)>()).ToList();
        int[] order = Enumerable.Range(0, raw.Count).OrderBy(i => Math.Min(verts[raw[i].A].S, verts[raw[i].B].S)).ToArray();
        for (int oi = 0; oi < order.Length; oi++)
        {
            int i = order[oi];
            (double S, double Z) p = verts[raw[i].A], q = verts[raw[i].B];
            double maxS = Math.Max(p.S, q.S) + tol, minZ = Math.Min(p.Z, q.Z) - tol, maxZ = Math.Max(p.Z, q.Z) + tol;
            double dx = q.S - p.S, dz = q.Z - p.Z, len2 = (dx * dx) + (dz * dz), len = Math.Sqrt(len2);
            for (int oj = oi + 1; oj < order.Length; oj++)
            {
                int j = order[oj];
                (double S, double Z) r = verts[raw[j].A], u = verts[raw[j].B];
                if (Math.Min(r.S, u.S) > maxS)
                    break;
                if (Math.Max(r.Z, u.Z) < minZ || Math.Min(r.Z, u.Z) > maxZ)
                    continue;
                double ex = u.S - r.S, ez = u.Z - r.Z, elen2 = (ex * ex) + (ez * ez), elen = Math.Sqrt(elen2);
                double den = (dx * ez) - (dz * ex);
                if (Math.Abs(den) > 1e-9 * len * elen)
                {
                    double t = (((r.S - p.S) * ez) - ((r.Z - p.Z) * ex)) / den;
                    double w = (((r.S - p.S) * dz) - ((r.Z - p.Z) * dx)) / den;
                    double ti = tol / len, tj = tol / elen;
                    if (t > ti && t < 1 - ti && w >= -tj && w <= 1 + tj)
                        cuts[i].Add((t, Vertex((p.S + (dx * t), p.Z + (dz * t)))));
                    if (w > tj && w < 1 - tj && t >= -ti && t <= 1 + ti)
                        cuts[j].Add((w, Vertex((r.S + (ex * w), r.Z + (ez * w)))));
                }
                else
                {
                    // Paralelos: se colineares e sobrepostos, as pontas de um partem o outro.
                    foreach (int v in new[] { raw[j].A, raw[j].B })
                    {
                        double t = (((verts[v].S - p.S) * dx) + ((verts[v].Z - p.Z) * dz)) / len2;
                        double dist = Math.Abs(((verts[v].S - p.S) * dz) - ((verts[v].Z - p.Z) * dx)) / len;
                        if (dist <= tol && t > 1e-9 && t < 1 - 1e-9)
                            cuts[i].Add((t, v));
                    }

                    foreach (int v in new[] { raw[i].A, raw[i].B })
                    {
                        double w = (((verts[v].S - r.S) * ex) + ((verts[v].Z - r.Z) * ez)) / elen2;
                        double dist = Math.Abs(((verts[v].S - r.S) * ez) - ((verts[v].Z - r.Z) * ex)) / elen;
                        if (dist <= tol && w > 1e-9 && w < 1 - 1e-9)
                            cuts[j].Add((w, v));
                    }
                }
            }
        }

        HashSet<(int, int)> edges = new();
        for (int i = 0; i < raw.Count; i++)
        {
            List<int> chain = new() { raw[i].A };
            chain.AddRange(cuts[i].OrderBy(c => c.T).Select(c => c.V));
            chain.Add(raw[i].B);
            for (int k = 0; k + 1 < chain.Count; k++)
            {
                int a = chain[k], b = chain[k + 1];
                if (a != b)
                    edges.Add(a < b ? (a, b) : (b, a));
            }
        }

        Dictionary<int, List<int>> adj = new();
        foreach ((int a, int b) in edges)
        {
            if (!adj.TryGetValue(a, out List<int>? la))
                adj[a] = la = new List<int>();
            if (!adj.TryGetValue(b, out List<int>? lb))
                adj[b] = lb = new List<int>();
            la.Add(b);
            lb.Add(a);
        }

        // Tira as pontas soltas (links abertos, saias de talude): nao fecham regiao.
        Queue<int> leaves = new(adj.Where(kv => kv.Value.Count <= 1).Select(kv => kv.Key));
        while (leaves.Count > 0)
        {
            int v = leaves.Dequeue();
            if (!adj.TryGetValue(v, out List<int>? nb))
                continue;
            foreach (int w in nb)
            {
                if (adj.TryGetValue(w, out List<int>? nw))
                {
                    nw.Remove(v);
                    if (nw.Count <= 1)
                        leaves.Enqueue(w);
                }
            }

            adj.Remove(v);
        }

        if (adj.Count < 3)
            return new List<List<(double S, double Z)>>();

        // Vizinhos de cada vertice em ordem anti-horaria.
        foreach ((int v, List<int> list) in adj)
            list.Sort((x, y) => Math.Atan2(verts[x].Z - verts[v].Z, verts[x].S - verts[v].S).CompareTo(Math.Atan2(verts[y].Z - verts[v].Z, verts[y].S - verts[v].S)));

        // Percorre as faces (interior a esquerda); a externa de cada componente sai horaria.
        HashSet<(int, int)> used = new();
        List<List<(double S, double Z)>> result = new();
        int total = adj.Sum(kv => kv.Value.Count);
        foreach ((int v0, List<int> nb0) in adj)
        {
            foreach (int w0 in nb0)
            {
                if (used.Contains((v0, w0)))
                    continue;
                List<int> face = new();
                int u = v0, v = w0, guard = 0;
                while (!used.Contains((u, v)) && guard++ <= total)
                {
                    used.Add((u, v));
                    face.Add(u);
                    List<int> nb = adj[v];
                    int idx = nb.IndexOf(u);
                    int w = nb[(idx - 1 + nb.Count) % nb.Count];
                    u = v;
                    v = w;
                }

                if (face.Count < 3 || FaceArea(face, verts) >= -1e-9)
                    continue;

                // Face externa: separa os lacos que se tocam num vertice (pontes, beliscoes).
                foreach (List<int> loop in SimpleLoops(face))
                {
                    if (loop.Count < 3)
                        continue;
                    List<(double S, double Z)> poly = loop.Select(i => verts[i]).ToList();
                    poly.Add(poly[0]);
                    double area = Area(poly);
                    if (area < -1e-8)
                    {
                        poly.Reverse();
                        result.Add(poly);
                    }
                }
            }
        }

        return result;
    }

    private static double FaceArea(List<int> face, List<(double S, double Z)> verts)
    {
        double a = 0;
        for (int i = 0; i < face.Count; i++)
        {
            (double S, double Z) p = verts[face[i]], q = verts[face[(i + 1) % face.Count]];
            a += (p.S * q.Z) - (q.S * p.Z);
        }

        return a / 2;
    }

    /// <summary>Quebra um ciclo que repete vertices em lacos simples.</summary>
    private static List<List<int>> SimpleLoops(List<int> cycle)
    {
        List<List<int>> loops = new();
        List<int> stack = new();
        Dictionary<int, int> at = new();
        foreach (int v in cycle)
        {
            if (at.TryGetValue(v, out int k))
            {
                loops.Add(stack.GetRange(k, stack.Count - k));
                for (int i = k + 1; i < stack.Count; i++)
                    at.Remove(stack[i]);
                stack.RemoveRange(k + 1, stack.Count - k - 1);
            }
            else
            {
                at[v] = stack.Count;
                stack.Add(v);
            }
        }

        if (stack.Count > 0)
            loops.Add(stack);
        return loops;
    }

    /// <summary>Area com sinal de um poligono (S,Z).</summary>
    public static double Area(IReadOnlyList<(double S, double Z)> p)
    {
        double a = 0;
        for (int i = 0; i + 1 < p.Count; i++)
            a += (p[i].S * p[i + 1].Z) - (p[i + 1].S * p[i].Z);
        return a / 2;
    }

    /// <summary>Circulo como poligono fechado (para hachura de tubos).</summary>
    public static List<(double S, double Z)> CirclePolygon(double s, double z, double r, int n = 36)
    {
        List<(double S, double Z)> p = new();
        for (int i = 0; i <= n; i++)
        {
            double a = i * Math.PI * 2 / n;
            p.Add((s + (r * Math.Cos(a)), z + (r * Math.Sin(a))));
        }

        return p;
    }

    /// <summary>Cadeias fechadas (primeiro ponto = ultimo) dentre as dadas.</summary>
    public static List<List<(double S, double Z)>> ClosedLoops(List<List<(double S, double Z)>> chains, double tol = 1e-3)
    {
        List<List<(double S, double Z)>> loops = new();
        foreach (List<(double S, double Z)> c in chains)
        {
            if (c.Count >= 4 && Math.Abs(c[0].S - c[^1].S) <= tol && Math.Abs(c[0].Z - c[^1].Z) <= tol && Math.Abs(Area(c)) > 1e-6)
                loops.Add(c);
        }

        return loops;
    }

    /// <summary>
    /// Perfil de topo (maior Z) de um conjunto de polilinhas amostrado nas abscissas dadas;
    /// NaN onde nada cobre. Base da comparacao entre secoes.
    /// </summary>
    public static double[] TopAt(IEnumerable<List<(double S, double Z)>> polylines, IReadOnlyList<double> xs)
    {
        double[] top = Enumerable.Repeat(double.NaN, xs.Count).ToArray();
        foreach (List<(double S, double Z)> pl in polylines)
        {
            for (int i = 0; i + 1 < pl.Count; i++)
            {
                (double s0, double z0) = pl[i];
                (double s1, double z1) = pl[i + 1];
                if (s1 < s0)
                    (s0, z0, s1, z1) = (s1, z1, s0, z0);
                if (s1 - s0 < 1e-9)
                    continue;
                for (int k = 0; k < xs.Count; k++)
                {
                    double x = xs[k];
                    if (x < s0 || x > s1)
                        continue;
                    double z = z0 + ((z1 - z0) * (x - s0) / (s1 - s0));
                    if (double.IsNaN(top[k]) || z > top[k])
                        top[k] = z;
                }
            }
        }

        return top;
    }

    /// <summary>Passo "bonito" para reguas (1, 2, 5 x 10^n) perto do pedido.</summary>
    public static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw))
            return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double f = raw / exp;
        double nice = f < 1.5 ? 1 : f < 3.5 ? 2 : f < 7.5 ? 5 : 10;
        return nice * exp;
    }
}

/// <summary>
/// Recorte exato pela caixa (vista 3D, como a section box do Model Viewer): uma regiao
/// convexa por trecho da linha (laterais nas profundidades, bissetriz nas juncoes, o
/// mesmo contorno que aparece em planta) e, se fixada, a faixa de elevacao.
/// </summary>
internal sealed class BoxClipper
{
    private readonly List<(double A, double B, double C)[]> _regions = new();
    private readonly double? _zmin, _zmax;

    public BoxClipper(CutBox box, double? zmin = null, double? zmax = null)
    {
        _zmin = zmin;
        _zmax = zmax;
        List<P2> pts = new();
        foreach (P2 p in box.Line)
        {
            if (pts.Count == 0 || P2.Dist(pts[^1], p) > 1e-9)
                pts.Add(p);
        }

        for (int i = 0; i + 1 < pts.Count; i++)
        {
            P2 a = pts[i], b = pts[i + 1];
            P2 t = (b - a).Unit(), n = t.Left;
            P2 qs = t, qe = t;
            if (i > 0)
            {
                P2 sum = (a - pts[i - 1]).Unit() + t;
                if (sum.Length > 1e-6)
                    qs = sum.Unit();
            }

            if (i + 2 < pts.Count)
            {
                P2 sum = t + (pts[i + 2] - b).Unit();
                if (sum.Length > 1e-6)
                    qe = sum.Unit();
            }

            _regions.Add(new[]
            {
                (n.X, n.Y, -P2.Dot(n, a) + box.Atras),   // n.(p-a) >= -atras
                (-n.X, -n.Y, P2.Dot(n, a) + box.Frente), // n.(p-a) <= frente
                (qs.X, qs.Y, -P2.Dot(qs, a)),            // depois da juncao inicial
                (-qe.X, -qe.Y, P2.Dot(qe, b))            // antes da juncao final
            });
        }
    }

    public int Regions => _regions.Count;

    private bool ZOk(double z) => (_zmin is not double lo || z >= lo - 1e-9) && (_zmax is not double hi || z <= hi + 1e-9);

    /// <summary>Regioes (bits) que contem o ponto (com a faixa de elevacao).</summary>
    public long Mask(P3 p)
    {
        if (!ZOk(p.Z))
            return 0;
        long m = 0;
        for (int r = 0; r < _regions.Count && r < 62; r++)
        {
            bool inside = true;
            foreach ((double A, double B, double C) in _regions[r])
            {
                if ((A * p.X) + (B * p.Y) + C < -1e-9)
                {
                    inside = false;
                    break;
                }
            }

            if (inside)
                m |= 1L << r;
        }

        return m;
    }

    public bool Contains(P3 p) => Mask(p) != 0;

    private static List<P3> ClipBy(List<P3> poly, Func<P3, double> f)
    {
        List<P3> result = new(poly.Count + 2);
        for (int i = 0; i < poly.Count; i++)
        {
            P3 p = poly[i], q = poly[(i + 1) % poly.Count];
            double fp = f(p), fq = f(q);
            if (fp >= 0)
                result.Add(p);
            if ((fp >= 0) != (fq >= 0))
            {
                double t = fp / (fp - fq);
                result.Add(new P3(p.X + ((q.X - p.X) * t), p.Y + ((q.Y - p.Y) * t), p.Z + ((q.Z - p.Z) * t)));
            }
        }

        return result;
    }

    /// <summary>Recorta um poligono plano (3D); uma parte por regiao que ele atravessa.</summary>
    public List<List<P3>> ClipPolygon(IReadOnlyList<P3> poly)
    {
        List<List<P3>> result = new();
        foreach ((double A, double B, double C)[] planes in _regions)
        {
            List<P3> cur = new(poly);
            foreach ((double A, double B, double C) in planes)
            {
                cur = ClipBy(cur, p => (A * p.X) + (B * p.Y) + C);
                if (cur.Count < 3)
                    break;
            }

            if (cur.Count >= 3 && _zmin is double lo)
                cur = ClipBy(cur, p => p.Z - lo);
            if (cur.Count >= 3 && _zmax is double hi)
                cur = ClipBy(cur, p => hi - p.Z);
            if (cur.Count >= 3)
                result.Add(cur);
        }

        return result;
    }

    /// <summary>
    /// Recorta uma malha (posicoes + indices de triangulos). Triangulos inteiros dentro
    /// de uma regiao ficam como estao (indices reaproveitados); os da borda sao recortados.
    /// </summary>
    public (List<P3> Pos, List<int> Tri) ClipMesh(IReadOnlyList<P3> pos, IReadOnlyList<int> tri)
    {
        long[] mask = new long[pos.Count];
        for (int i = 0; i < pos.Count; i++)
            mask[i] = Mask(pos[i]);
        List<P3> outPos = new();
        List<int> outTri = new();
        int[] remap = Enumerable.Repeat(-1, pos.Count).ToArray();
        int Keep(int i)
        {
            if (remap[i] < 0)
            {
                remap[i] = outPos.Count;
                outPos.Add(pos[i]);
            }

            return remap[i];
        }

        for (int k = 0; k + 2 < tri.Count; k += 3)
        {
            int a = tri[k], b = tri[k + 1], c = tri[k + 2];
            if ((mask[a] & mask[b] & mask[c]) != 0)
            {
                outTri.Add(Keep(a));
                outTri.Add(Keep(b));
                outTri.Add(Keep(c));
                continue;
            }

            foreach (List<P3> part in ClipPolygon(new[] { pos[a], pos[b], pos[c] }))
            {
                int baseIndex = outPos.Count;
                outPos.AddRange(part);
                for (int i = 1; i + 1 < part.Count; i++)
                {
                    outTri.Add(baseIndex);
                    outTri.Add(baseIndex + i);
                    outTri.Add(baseIndex + i + 1);
                }
            }
        }

        return (outPos, outTri);
    }

    /// <summary>Trechos de uma polilinha 3D dentro da caixa (cortes exatos nas faces).</summary>
    public List<List<P3>> ClipPolyline(IReadOnlyList<P3> pts)
    {
        List<List<P3>> runs = new();
        List<P3>? cur = null;
        bool endedAtOne = false;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            P3 p = pts[i], q = pts[i + 1];
            List<(double T0, double T1)> ivals = new();
            foreach ((double A, double B, double C)[] planes in _regions)
            {
                double t0 = 0, t1 = 1;
                bool ok = true;
                IEnumerable<(double Fp, double Fq)> fs = planes.Select(h => ((h.A * p.X) + (h.B * p.Y) + h.C, (h.A * q.X) + (h.B * q.Y) + h.C));
                if (_zmin is double lo)
                    fs = fs.Append((p.Z - lo, q.Z - lo));
                if (_zmax is double hi)
                    fs = fs.Append((hi - p.Z, hi - q.Z));
                foreach ((double fp, double fq) in fs)
                {
                    if (fp < 0 && fq < 0)
                    {
                        ok = false;
                        break;
                    }

                    if (fp < 0)
                        t0 = Math.Max(t0, fp / (fp - fq));
                    else if (fq < 0)
                        t1 = Math.Min(t1, fp / (fp - fq));
                }

                if (ok && t1 - t0 > 1e-12)
                    ivals.Add((t0, t1));
            }

            ivals.Sort((x, y) => x.T0.CompareTo(y.T0));
            List<(double T0, double T1)> merged = new();
            foreach ((double t0, double t1) in ivals)
            {
                if (merged.Count > 0 && t0 <= merged[^1].T1 + 1e-9)
                    merged[^1] = (merged[^1].T0, Math.Max(merged[^1].T1, t1));
                else
                    merged.Add((t0, t1));
            }

            P3 Lerp(double t) => new(p.X + ((q.X - p.X) * t), p.Y + ((q.Y - p.Y) * t), p.Z + ((q.Z - p.Z) * t));
            if (merged.Count == 0)
                endedAtOne = false;
            foreach ((double t0, double t1) in merged)
            {
                if (cur is not null && endedAtOne && t0 < 1e-9)
                {
                    cur.Add(Lerp(t1));
                }
                else
                {
                    cur = new List<P3> { Lerp(t0), Lerp(t1) };
                    runs.Add(cur);
                }

                endedAtOne = t1 > 1 - 1e-9;
            }
        }

        return runs;
    }
}

/// <summary>
/// Malha de um objeto 3D do AutoCAD (solido, corpo, superficie, malha SubD ou bloco com
/// esses objetos), em coordenadas do desenho: triangulos, arestas do Brep e as "dobras"
/// (arestas internas entre faces quase paralelas) que viram silhueta conforme a direcao
/// em que se olha. C# puro depois de montada; guardada em cache por objeto.
/// </summary>
internal sealed class MeshGeom
{
    public string Name = "";
    /// <summary>Triangulos em sequencia (3 pontos cada).</summary>
    public List<P3> Tri = new();
    public List<List<P3>> Edges = new();
    /// <summary>Dobras: aresta e as normais das duas faces (silhueta quando uma olha para cada lado).</summary>
    public List<(P3 A, P3 B, P3 N1, P3 N2)> Folds = new();
    public double MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue;
    public double MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue;

    public bool IsEmpty => Tri.Count == 0 && Edges.Count == 0;

    public int Triangles => Tri.Count / 3;

    public void Grow(P3 p)
    {
        MinX = Math.Min(MinX, p.X); MaxX = Math.Max(MaxX, p.X);
        MinY = Math.Min(MinY, p.Y); MaxY = Math.Max(MaxY, p.Y);
        MinZ = Math.Min(MinZ, p.Z); MaxZ = Math.Max(MaxZ, p.Z);
    }

    public void Append(MeshGeom? other)
    {
        if (other is null)
            return;
        Tri.AddRange(other.Tri);
        Edges.AddRange(other.Edges);
        Folds.AddRange(other.Folds);
        if (!other.IsEmpty)
        {
            Grow(new P3(other.MinX, other.MinY, other.MinZ));
            Grow(new P3(other.MaxX, other.MaxY, other.MaxZ));
        }
    }

    public bool Overlaps(double minX, double minY, double maxX, double maxY) =>
        !IsEmpty && MaxX >= minX && MinX <= maxX && MaxY >= minY && MinY <= maxY;

    /// <summary>Copia com os pontos levados por f (blocos: matriz da insercao).</summary>
    public MeshGeom Transformed(Func<P3, P3> f, string name)
    {
        MeshGeom g = new() { Name = name };
        foreach (P3 p in Tri)
        {
            P3 q = f(p);
            g.Tri.Add(q);
            g.Grow(q);
        }

        foreach (List<P3> e in Edges)
        {
            List<P3> t = e.Select(f).ToList();
            foreach (P3 q in t)
                g.Grow(q);
            g.Edges.Add(t);
        }

        foreach ((P3 a, P3 b, P3 n1, P3 n2) in Folds)
        {
            P3 ta = f(a), tb = f(b);
            g.Folds.Add((ta, tb, Vec(f, a, n1, ta), Vec(f, a, n2, ta)));
        }

        return g;
    }

    private static P3 Vec(Func<P3, P3> f, P3 origin, P3 n, P3 tOrigin)
    {
        P3 tip = f(new P3(origin.X + n.X, origin.Y + n.Y, origin.Z + n.Z));
        return new P3(tip.X - tOrigin.X, tip.Y - tOrigin.Y, tip.Z - tOrigin.Z);
    }

    /// <summary>
    /// Acha as dobras da malha (vertices soldados): arestas com duas faces cujas normais
    /// diferem entre ~1 e 60 graus. As quinas vivas ja vem nas arestas do Brep.
    /// </summary>
    public void ComputeFolds(int limit = 20000)
    {
        Folds.Clear();
        if (Tri.Count < 6)
            return;
        double q = 1e-5;
        Dictionary<(long, long, long), int> ids = new();
        List<P3> verts = new();
        int Id(P3 p)
        {
            (long, long, long) k = ((long)Math.Round(p.X / q), (long)Math.Round(p.Y / q), (long)Math.Round(p.Z / q));
            if (!ids.TryGetValue(k, out int id))
            {
                id = verts.Count;
                ids[k] = id;
                verts.Add(p);
            }

            return id;
        }

        Dictionary<(int, int), List<P3>> edges = new();
        for (int k = 0; k + 2 < Tri.Count; k += 3)
        {
            P3 a = Tri[k], b = Tri[k + 1], c = Tri[k + 2];
            P3 n = Normal(a, b, c);
            if (n.X == 0 && n.Y == 0 && n.Z == 0)
                continue;
            int ia = Id(a), ib = Id(b), ic = Id(c);
            foreach ((int u, int v) in new[] { (ia, ib), (ib, ic), (ic, ia) })
            {
                if (u == v)
                    continue;
                (int, int) key = u < v ? (u, v) : (v, u);
                if (!edges.TryGetValue(key, out List<P3>? list))
                    edges[key] = list = new List<P3>(2);
                list.Add(n);
            }
        }

        foreach (((int u, int v), List<P3> normals) in edges)
        {
            if (normals.Count != 2)
                continue;
            P3 n1 = normals[0], n2 = normals[1];
            double cos = (n1.X * n2.X) + (n1.Y * n2.Y) + (n1.Z * n2.Z);
            if (cos > 0.99985 || cos < 0.5)
                continue; // plana (triangulacao) ou quina viva
            Folds.Add((verts[u], verts[v], n1, n2));
            if (Folds.Count >= limit)
                break;
        }
    }

    private static P3 Normal(P3 a, P3 b, P3 c)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        double nx = (uy * vz) - (uz * vy), ny = (uz * vx) - (ux * vz), nz = (ux * vy) - (uy * vx);
        double len = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        return len < 1e-14 ? default : new P3(nx / len, ny / len, nz / len);
    }
}
