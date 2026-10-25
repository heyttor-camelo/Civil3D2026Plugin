using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Utilitarios.Contorno;

// Geometria pura (sem AutoCAD) do C3DUNIRHACHURAS: testada em tests/Contorno.Tests.

/// <summary>Ponto 2D no plano da hachura (OCS).</summary>
public readonly record struct P2(double X, double Y)
{
    public static P2 operator +(P2 a, P2 b) => new(a.X + b.X, a.Y + b.Y);
    public static P2 operator -(P2 a, P2 b) => new(a.X - b.X, a.Y - b.Y);
    public static P2 operator *(P2 a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt((X * X) + (Y * Y));
    public double DistanceTo(P2 o) => (this - o).Length;
}

/// <summary>
/// Laco fechado como polilinha: o trecho i vai de Pontos[i] a Pontos[i+1] (o ultimo volta ao primeiro)
/// com o bulge Bulges[i] (0 = reto; tan(angulo/4) = arco, positivo anti-horario).
/// </summary>
public sealed class Laco
{
    public List<P2> Pontos { get; } = new();
    public List<double> Bulges { get; } = new();

    public int Count => Pontos.Count;

    public Laco() { }

    public Laco(IEnumerable<(P2 P, double Bulge)> vertices)
    {
        foreach ((P2 p, double b) in vertices)
            Add(p, b);
    }

    public void Add(P2 p, double bulge = 0.0)
    {
        Pontos.Add(p);
        Bulges.Add(bulge);
    }

    /// <summary>Area com sinal (positiva no sentido anti-horario), contando os segmentos de arco.</summary>
    public double AreaComSinal()
    {
        double a = 0.0;
        int n = Count;
        for (int i = 0; i < n; i++)
        {
            P2 p = Pontos[i], q = Pontos[(i + 1) % n];
            a += (p.X * q.Y) - (q.X * p.Y);
        }
        a *= 0.5;
        for (int i = 0; i < n; i++)
        {
            double b = Bulges[i];
            if (b == 0.0)
                continue;
            double l = Pontos[i].DistanceTo(Pontos[(i + 1) % n]);
            double th = 4.0 * Math.Atan(b);
            double r = Geo.Raio(l, b);
            a += 0.5 * r * r * (th - Math.Sin(th));
        }
        return a;
    }

    public Laco Invertido()
    {
        var r = new Laco();
        int n = Count;
        for (int i = n - 1; i >= 0; i--)
        {
            // O trecho que chega em Pontos[i] (de i-1 para i) sai dele, ao contrario.
            r.Add(Pontos[i], -Bulges[(i - 1 + n) % n]);
        }
        return r;
    }

    /// <summary>Contorno aproximado por segmentos retos (arcos divididos), para teste de ponto interno.</summary>
    public List<P2> Poligono()
    {
        var pts = new List<P2>();
        int n = Count;
        for (int i = 0; i < n; i++)
        {
            P2 a = Pontos[i], b = Pontos[(i + 1) % n];
            pts.Add(a);
            double bu = Bulges[i];
            if (bu == 0.0)
                continue;
            double th = 4.0 * Math.Atan(bu);
            int k = Math.Max(2, (int)Math.Ceiling(Math.Abs(th) / (Math.PI / 32)));
            for (int j = 1; j < k; j++)
                pts.Add(Geo.PontoNoArco(a, b, bu, (double)j / k));
        }
        return pts;
    }

    /// <summary>Ponto sobre o contorno (meio do primeiro trecho), para saber em que laco ele esta.</summary>
    public P2 PontoAmostra()
    {
        P2 a = Pontos[0], b = Pontos[1 % Count];
        return Bulges[0] == 0.0 ? (a + b) * 0.5 : Geo.PontoNoArco(a, b, Bulges[0], 0.5);
    }
}

/// <summary>Contorno externo (anti-horario) com os furos (horarios) que ficam dentro dele.</summary>
public sealed record Ilha(Laco Externo, List<Laco> Furos);

/// <summary>Resultado da uniao de um grupo de pecas.</summary>
public sealed class ResultadoUniao
{
    public List<Ilha> Ilhas { get; } = new();
    /// <summary>Soma das areas das pecas de entrada (furos descontados).</summary>
    public double AreaEntrada { get; set; }
    /// <summary>Area do contorno montado (externos menos furos).</summary>
    public double AreaSaida { get; set; }
    /// <summary>Trechos de borda que nao fecharam um laco (geometria quebrada na origem).</summary>
    public int TrechosAbertos { get; set; }
    /// <summary>Lacos de entrada descartados (menos de 2 vertices ou area nula).</summary>
    public int LacosDescartados { get; set; }
}

internal static class Geo
{
    public static double Raio(double corda, double bulge)
        => Math.Abs(corda * (1.0 + (bulge * bulge)) / (4.0 * bulge));

    public static P2 Centro(P2 a, P2 b, double bulge)
    {
        P2 c = b - a;
        double l = c.Length;
        var nEsq = new P2(-c.Y / l, c.X / l);
        double h = l * (1.0 - (bulge * bulge)) / (4.0 * bulge);
        return ((a + b) * 0.5) + (nEsq * h);
    }

    /// <summary>Ponto na fracao t (0..1) do arco de a ate b.</summary>
    public static P2 PontoNoArco(P2 a, P2 b, double bulge, double t)
    {
        P2 c = Centro(a, b, bulge);
        double r = a.DistanceTo(c);
        double a0 = Math.Atan2(a.Y - c.Y, a.X - c.X);
        double th = 4.0 * Math.Atan(bulge);
        double ang = a0 + (th * t);
        return new P2(c.X + (r * Math.Cos(ang)), c.Y + (r * Math.Sin(ang)));
    }

    /// <summary>Direcao (angulo) da tangente no inicio (inicio = true) ou no fim do trecho.</summary>
    public static double Tangente(P2 a, P2 b, double bulge, bool inicio)
    {
        double ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
        if (bulge == 0.0)
            return ang;
        double meia = 2.0 * Math.Atan(bulge);
        return inicio ? ang - meia : ang + meia;
    }

    public static double Norm2Pi(double a)
    {
        a %= 2.0 * Math.PI;
        if (a < 0)
            a += 2.0 * Math.PI;
        return a;
    }

    /// <summary>Ponto dentro do poligono (par-impar).</summary>
    public static bool Dentro(List<P2> pol, P2 p)
    {
        bool dentro = false;
        int n = pol.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            P2 a = pol[i], b = pol[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < ((b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y)) + a.X)
                dentro = !dentro;
        }
        return dentro;
    }
}

/// <summary>
/// Remonta o contorno de hachuras picadas (ex.: filled regions exportadas do Revit, cada uma
/// quebrada em varios pedacos encostados). Cada peca e uma hachura (lista de lacos).
///
/// Como funciona (o "trim e join" feito pelas arestas):
/// 1. cada peca tem os lacos orientados pela profundidade: externo anti-horario, furo horario;
/// 2. vertices a menos da tolerancia viram um so ponto;
/// 3. cada trecho e quebrado nos vertices das outras pecas que caem sobre ele (juncoes em T);
/// 4. trechos iguais em sentidos opostos sao a divisa entre duas pecas e se cancelam;
/// 5. o que sobra e encadeado em lacos fechados; vertices alinhados sao removidos.
/// Pecas so encostadas (o caso das hachuras exportadas) saem certas; pecas sobrepostas
/// nao sao uniao de verdade, e a diferenca de area acusa isso.
/// </summary>
public static class ContornoUniao
{
    private sealed class Trecho
    {
        public int A, B;
        public double Bulge;
    }

    public static ResultadoUniao Unir(IReadOnlyList<IReadOnlyList<Laco>> pecas, double tol)
    {
        if (tol <= 0)
            tol = 1e-6;
        var res = new ResultadoUniao();

        // 1. Lacos limpos e orientados por peca.
        var lacos = new List<Laco>();
        foreach (IReadOnlyList<Laco> peca in pecas)
        {
            var limpos = new List<Laco>();
            foreach (Laco l in peca)
            {
                Laco? c = Limpar(l, tol);
                if (c is null)
                    res.LacosDescartados++;
                else
                    limpos.Add(c);
            }
            var pols = limpos.Select(l => l.Poligono()).ToList();
            for (int i = 0; i < limpos.Count; i++)
            {
                P2 amostra = limpos[i].PontoAmostra();
                int prof = 0;
                for (int j = 0; j < limpos.Count; j++)
                {
                    if (j != i && Geo.Dentro(pols[j], amostra))
                        prof++;
                }
                bool furo = (prof % 2) == 1;
                Laco l = limpos[i];
                double area = l.AreaComSinal();
                if ((area > 0) == furo)
                    l = l.Invertido();
                res.AreaEntrada += l.AreaComSinal();
                lacos.Add(l);
            }
        }

        // 2. Vertices fundidos pela tolerancia.
        var pontos = new List<P2>();
        var trechos = new List<Trecho>();
        var bruto = new List<P2>();
        foreach (Laco l in lacos)
            bruto.AddRange(l.Pontos);
        int[] rep = Fundir(bruto, tol, pontos);
        int k = 0;
        foreach (Laco l in lacos)
        {
            int n = l.Count;
            int baseK = k;
            for (int i = 0; i < n; i++)
            {
                int a = rep[baseK + i], b = rep[baseK + ((i + 1) % n)];
                if (a != b)
                    trechos.Add(new Trecho { A = a, B = b, Bulge = l.Bulges[i] });
            }
            k += n;
        }

        // 3. Quebra nos vertices que caem sobre o trecho.
        trechos = Quebrar(trechos, pontos, tol);

        // 4. Divisas se cancelam.
        List<Trecho> borda = Cancelar(trechos, pontos, tol);

        // 5. Encadeamento e limpeza.
        List<Laco> saida = Encadear(borda, pontos, res);
        var externos = new List<Laco>();
        var furos = new List<Laco>();
        foreach (Laco l in saida)
        {
            Laco s = Simplificar(l, tol);
            if (s.Count < 2)
                continue;
            double a = s.AreaComSinal();
            if (Math.Abs(a) <= tol * tol)
                continue;
            res.AreaSaida += a;
            (a > 0 ? externos : furos).Add(s);
        }

        var ilhas = externos
            .OrderBy(e => e.AreaComSinal())
            .Select(e => (Ilha: new Ilha(e, new List<Laco>()), Pol: e.Poligono(), Area: e.AreaComSinal()))
            .ToList();
        foreach (Laco f in furos)
        {
            P2 amostra = f.PontoAmostra();
            // O menor externo que contem o furo.
            var dono = ilhas.FirstOrDefault(i => i.Area >= Math.Abs(f.AreaComSinal()) && Geo.Dentro(i.Pol, amostra));
            if (dono.Ilha is not null)
                dono.Ilha.Furos.Add(f);
            else
                res.TrechosAbertos += f.Count; // furo sem externo: nao deveria acontecer
        }
        res.Ilhas.AddRange(ilhas.OrderByDescending(i => i.Area).Select(i => i.Ilha));
        return res;
    }

    /// <summary>Tira vertices repetidos e o fechamento duplicado; null se o laco nao tiver area.</summary>
    private static Laco? Limpar(Laco l, double tol)
    {
        var c = new Laco();
        int n = l.Count;
        for (int i = 0; i < n; i++)
        {
            if (c.Count > 0 && c.Pontos[^1].DistanceTo(l.Pontos[i]) <= tol)
            {
                // Trecho de comprimento nulo: o bulge que vale e o do trecho seguinte.
                c.Bulges[^1] = l.Bulges[i];
                continue;
            }
            c.Add(l.Pontos[i], l.Bulges[i]);
        }
        while (c.Count > 1 && c.Pontos[^1].DistanceTo(c.Pontos[0]) <= tol)
        {
            c.Pontos.RemoveAt(c.Count - 1);
            c.Bulges.RemoveAt(c.Bulges.Count - 1);
        }
        if (c.Count < 2 || (c.Count == 2 && c.Bulges.All(b => b == 0.0)))
            return null;
        if (Math.Abs(c.AreaComSinal()) <= tol * tol)
            return null;
        return c;
    }

    /// <summary>Agrupa pontos a menos de tol (grade + union-find). Devolve o indice de cada ponto em 'pontos'.</summary>
    private static int[] Fundir(List<P2> bruto, double tol, List<P2> pontos)
    {
        int n = bruto.Count;
        var pai = new int[n];
        for (int i = 0; i < n; i++)
            pai[i] = i;
        int Raiz(int i)
        {
            while (pai[i] != i)
            {
                pai[i] = pai[pai[i]];
                i = pai[i];
            }
            return i;
        }

        var grade = new Dictionary<(long, long), List<int>>();
        (long, long) Celula(P2 p) => ((long)Math.Floor(p.X / tol), (long)Math.Floor(p.Y / tol));
        for (int i = 0; i < n; i++)
        {
            (long cx, long cy) = Celula(bruto[i]);
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!grade.TryGetValue((cx + dx, cy + dy), out List<int>? lista))
                        continue;
                    foreach (int j in lista)
                    {
                        if (bruto[i].DistanceTo(bruto[j]) <= tol)
                        {
                            int ri = Raiz(i), rj = Raiz(j);
                            if (ri != rj)
                                pai[ri] = rj;
                        }
                    }
                }
            }
            if (!grade.TryGetValue((cx, cy), out List<int>? propria))
                grade[(cx, cy)] = propria = new List<int>();
            propria.Add(i);
        }

        var novo = new Dictionary<int, int>();
        var soma = new List<(double X, double Y, int N)>();
        var rep = new int[n];
        for (int i = 0; i < n; i++)
        {
            int r = Raiz(i);
            if (!novo.TryGetValue(r, out int id))
            {
                id = soma.Count;
                novo[r] = id;
                soma.Add((0, 0, 0));
            }
            (double x, double y, int c) = soma[id];
            soma[id] = (x + bruto[i].X, y + bruto[i].Y, c + 1);
            rep[i] = id;
        }
        foreach ((double x, double y, int c) in soma)
            pontos.Add(new P2(x / c, y / c));
        return rep;
    }

    private static List<Trecho> Quebrar(List<Trecho> trechos, List<P2> pontos, double tol)
    {
        if (trechos.Count == 0)
            return trechos;

        double medio = trechos.Average(t => pontos[t.A].DistanceTo(pontos[t.B]));
        double cel = Math.Max(medio, tol * 4);
        var grade = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < pontos.Count; i++)
        {
            var key = ((long)Math.Floor(pontos[i].X / cel), (long)Math.Floor(pontos[i].Y / cel));
            if (!grade.TryGetValue(key, out List<int>? l))
                grade[key] = l = new List<int>();
            l.Add(i);
        }

        var saida = new List<Trecho>(trechos.Count);
        var cortes = new List<(double Par, int P)>();
        foreach (Trecho t in trechos)
        {
            P2 a = pontos[t.A], b = pontos[t.B];
            double minX, minY, maxX, maxY;
            P2 centro = default;
            double r = 0, a0 = 0, th = 0;
            if (t.Bulge == 0.0)
            {
                minX = Math.Min(a.X, b.X); maxX = Math.Max(a.X, b.X);
                minY = Math.Min(a.Y, b.Y); maxY = Math.Max(a.Y, b.Y);
            }
            else
            {
                centro = Geo.Centro(a, b, t.Bulge);
                r = a.DistanceTo(centro);
                a0 = Math.Atan2(a.Y - centro.Y, a.X - centro.X);
                th = 4.0 * Math.Atan(t.Bulge);
                minX = centro.X - r; maxX = centro.X + r;
                minY = centro.Y - r; maxY = centro.Y + r;
            }
            minX -= tol; minY -= tol; maxX += tol; maxY += tol;

            cortes.Clear();
            long x0 = (long)Math.Floor(minX / cel), x1 = (long)Math.Floor(maxX / cel);
            long y0 = (long)Math.Floor(minY / cel), y1 = (long)Math.Floor(maxY / cel);
            for (long cx = x0; cx <= x1; cx++)
            {
                for (long cy = y0; cy <= y1; cy++)
                {
                    if (!grade.TryGetValue((cx, cy), out List<int>? lista))
                        continue;
                    foreach (int pi in lista)
                    {
                        if (pi == t.A || pi == t.B)
                            continue;
                        P2 p = pontos[pi];
                        if (p.X < minX || p.X > maxX || p.Y < minY || p.Y > maxY)
                            continue;
                        if (t.Bulge == 0.0)
                        {
                            P2 d = b - a;
                            double l2 = (d.X * d.X) + (d.Y * d.Y);
                            double u = (((p.X - a.X) * d.X) + ((p.Y - a.Y) * d.Y)) / l2;
                            double l = Math.Sqrt(l2);
                            if (u * l <= tol || (1.0 - u) * l <= tol)
                                continue;
                            P2 proj = a + (d * u);
                            if (proj.DistanceTo(p) <= tol)
                                cortes.Add((u, pi));
                        }
                        else
                        {
                            if (Math.Abs(p.DistanceTo(centro) - r) > tol)
                                continue;
                            double ang = Math.Atan2(p.Y - centro.Y, p.X - centro.X);
                            double phi = th > 0 ? Geo.Norm2Pi(ang - a0) : Geo.Norm2Pi(a0 - ang);
                            double margem = tol / r;
                            if (phi <= margem || phi >= Math.Abs(th) - margem)
                                continue;
                            cortes.Add((phi / Math.Abs(th), pi));
                        }
                    }
                }
            }

            if (cortes.Count == 0)
            {
                saida.Add(t);
                continue;
            }

            cortes.Sort((x, y) => x.Par.CompareTo(y.Par));
            int ini = t.A;
            double parIni = 0.0;
            foreach ((double par, int pi) in cortes)
            {
                saida.Add(new Trecho { A = ini, B = pi, Bulge = SubBulge(t.Bulge, th, parIni, par) });
                ini = pi;
                parIni = par;
            }
            saida.Add(new Trecho { A = ini, B = t.B, Bulge = SubBulge(t.Bulge, th, parIni, 1.0) });
        }
        return saida;
    }

    private static double SubBulge(double bulge, double th, double de, double ate)
        => bulge == 0.0 ? 0.0 : Math.Tan(th * (ate - de) / 4.0);

    /// <summary>Soma os trechos iguais com sinal pelo sentido; fica so o que nao se anulou.</summary>
    private static List<Trecho> Cancelar(List<Trecho> trechos, List<P2> pontos, double tol)
    {
        var mapa = new Dictionary<(int, int), List<(double Flecha, double Bulge, int Soma)>>();
        foreach (Trecho t in trechos)
        {
            bool direto = t.A < t.B;
            (int, int) key = direto ? (t.A, t.B) : (t.B, t.A);
            double bulge = direto ? t.Bulge : -t.Bulge;
            double flecha = bulge * pontos[t.A].DistanceTo(pontos[t.B]) / 2.0;
            int s = direto ? 1 : -1;

            if (!mapa.TryGetValue(key, out var lista))
                mapa[key] = lista = new List<(double, double, int)>();
            int achou = lista.FindIndex(e => Math.Abs(e.Flecha - flecha) <= tol);
            if (achou >= 0)
                lista[achou] = (lista[achou].Flecha, lista[achou].Bulge, lista[achou].Soma + s);
            else
                lista.Add((flecha, bulge, s));
        }

        var borda = new List<Trecho>();
        foreach (var kv in mapa)
        {
            foreach ((double _, double bulge, int soma) in kv.Value)
            {
                // Peca repetida (duas hachuras iguais) soma 2: fica uma borda so.
                if (soma > 0)
                    borda.Add(new Trecho { A = kv.Key.Item1, B = kv.Key.Item2, Bulge = bulge });
                else if (soma < 0)
                    borda.Add(new Trecho { A = kv.Key.Item2, B = kv.Key.Item1, Bulge = -bulge });
            }
        }
        // Ordem estavel, para o resultado nao depender da ordem do dicionario.
        borda.Sort((x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        return borda;
    }

    /// <summary>
    /// Encadeia os trechos orientados (interior a esquerda). Num vertice com mais de uma saida
    /// (pecas encostadas so por um canto) vira o mais a esquerda: cada laco fica separado.
    /// </summary>
    private static List<Laco> Encadear(List<Trecho> borda, List<P2> pontos, ResultadoUniao res)
    {
        var saidas = new Dictionary<int, List<int>>();
        for (int i = 0; i < borda.Count; i++)
        {
            if (!saidas.TryGetValue(borda[i].A, out List<int>? l))
                saidas[borda[i].A] = l = new List<int>();
            l.Add(i);
        }

        var usado = new bool[borda.Count];
        var lacos = new List<Laco>();
        for (int ini = 0; ini < borda.Count; ini++)
        {
            if (usado[ini])
                continue;

            var cadeia = new List<int> { ini };
            usado[ini] = true;
            int inicio = borda[ini].A;
            int atual = ini;
            bool fechou = false;
            while (true)
            {
                Trecho t = borda[atual];
                if (t.B == inicio)
                {
                    fechou = true;
                    break;
                }
                if (!saidas.TryGetValue(t.B, out List<int>? opcoes))
                    break;

                double chegada = Geo.Tangente(pontos[t.A], pontos[t.B], t.Bulge, inicio: false);
                double volta = chegada + Math.PI;
                int melhor = -1;
                double maior = double.MinValue;
                foreach (int o in opcoes)
                {
                    if (usado[o])
                        continue;
                    Trecho u = borda[o];
                    double sai = Geo.Tangente(pontos[u.A], pontos[u.B], u.Bulge, inicio: true);
                    // Angulo da volta (sentido de onde veio) ate a saida, anti-horario: o maior e a
                    // curva mais fechada a esquerda, que contorna a mesma peca.
                    double giro = Geo.Norm2Pi(sai - volta);
                    if (giro < 1e-12 || giro > (2.0 * Math.PI) - 1e-12)
                        giro = -1.0; // voltar pelo mesmo caminho e a ultima opcao
                    if (giro > maior)
                    {
                        maior = giro;
                        melhor = o;
                    }
                }
                if (melhor < 0)
                    break;
                usado[melhor] = true;
                cadeia.Add(melhor);
                atual = melhor;
            }

            if (!fechou)
            {
                res.TrechosAbertos += cadeia.Count;
                continue;
            }
            var laco = new Laco();
            foreach (int c in cadeia)
                laco.Add(pontos[borda[c].A], borda[c].Bulge);
            lacos.Add(laco);
        }
        return lacos;
    }

    /// <summary>Junta retas alinhadas e arcos do mesmo circulo (vertices que eram cantos das pecas).</summary>
    private static Laco Simplificar(Laco l, double tol)
    {
        var pts = new List<P2>(l.Pontos);
        var bul = new List<double>(l.Bulges);
        bool mudou = true;
        while (mudou && pts.Count > 2)
        {
            mudou = false;
            for (int i = 0; i < pts.Count && pts.Count > 2; i++)
            {
                int n = pts.Count;
                int ant = (i - 1 + n) % n;
                P2 p0 = pts[ant], p1 = pts[i], p2 = pts[(i + 1) % n];
                double b0 = bul[ant], b1 = bul[i];

                double? novo = null;
                if (b0 == 0.0 && b1 == 0.0)
                {
                    P2 d = p2 - p0;
                    double len = d.Length;
                    if (len > tol)
                    {
                        double dist = Math.Abs(((p1.X - p0.X) * d.Y) - ((p1.Y - p0.Y) * d.X)) / len;
                        double u = (((p1.X - p0.X) * d.X) + ((p1.Y - p0.Y) * d.Y)) / (len * len);
                        if (dist <= tol && u > 0 && u < 1)
                            novo = 0.0;
                    }
                }
                else if (b0 != 0.0 && b1 != 0.0 && Math.Sign(b0) == Math.Sign(b1))
                {
                    P2 c0 = Geo.Centro(p0, p1, b0), c1 = Geo.Centro(p1, p2, b1);
                    double r0 = p0.DistanceTo(c0), r1 = p1.DistanceTo(c1);
                    double th = (4.0 * Math.Atan(b0)) + (4.0 * Math.Atan(b1));
                    if (c0.DistanceTo(c1) <= tol && Math.Abs(r0 - r1) <= tol && Math.Abs(th) < (2.0 * Math.PI) - 1e-9)
                        novo = Math.Tan(th / 4.0);
                }

                if (novo is double nb)
                {
                    bul[ant] = nb;
                    pts.RemoveAt(i);
                    bul.RemoveAt(i);
                    mudou = true;
                    i--;
                }
            }
        }
        var s = new Laco();
        for (int i = 0; i < pts.Count; i++)
            s.Add(pts[i], bul[i]);
        return s;
    }
}
