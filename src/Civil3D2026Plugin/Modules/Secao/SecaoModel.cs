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
/// de cotas mostrada (null = automatica).
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
    Ponto
}

/// <summary>Um objeto do desenho ja levado ao plano do corte (coordenadas S ao longo, Z cota).</summary>
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

/// <summary>Resultado de um corte: itens e faixa de cotas dos dados.</summary>
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

    /// <summary>Faixa de cotas efetiva: a da caixa, ou a dos dados com folga.</summary>
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

    /// <summary>Encadeia segmentos (S,Z) pelas pontas em polilinhas.</summary>
    public static List<List<(double S, double Z)>> Chain(List<((double S, double Z) A, (double S, double Z) B)> segs, double tol = 1e-4)
    {
        List<List<(double S, double Z)>> result = new();
        bool[] used = new bool[segs.Count];
        static bool Same((double S, double Z) p, (double S, double Z) q, double t) => Math.Abs(p.S - q.S) <= t && Math.Abs(p.Z - q.Z) <= t;

        for (int i = 0; i < segs.Count; i++)
        {
            if (used[i])
                continue;
            used[i] = true;
            List<(double S, double Z)> chain = new() { segs[i].A, segs[i].B };
            bool grown = true;
            while (grown)
            {
                grown = false;
                for (int j = 0; j < segs.Count; j++)
                {
                    if (used[j])
                        continue;
                    if (Same(segs[j].A, chain[^1], tol)) { chain.Add(segs[j].B); used[j] = true; grown = true; }
                    else if (Same(segs[j].B, chain[^1], tol)) { chain.Add(segs[j].A); used[j] = true; grown = true; }
                    else if (Same(segs[j].B, chain[0], tol)) { chain.Insert(0, segs[j].A); used[j] = true; grown = true; }
                    else if (Same(segs[j].A, chain[0], tol)) { chain.Insert(0, segs[j].B); used[j] = true; grown = true; }
                }
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
