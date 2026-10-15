using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Contorno do pavimento para a hachura (C# puro).
///
/// O pavimento e o que fica entre as faces de meio-fio: as linhas FaceMF da rede
/// formam os contornos; nas pontas livres dos eixos uma tampa reta fecha o
/// contorno entre as duas faces. Os aneis fechados (quadras, canteiros, ilhas,
/// ilha central de rotatoria) entram como furos. Com o estilo "normal" de
/// hachura (par/impar), o conjunto de lacos preenche so o pavimento.
/// </summary>
internal static class ViarioHatch
{
    public sealed class Loop
    {
        public List<Seg> Segs = new();
        public bool Outer;
        public double Area;
        /// <summary>Menor laco que contem este (null para o externo de fora).</summary>
        public Loop? Parent;
        /// <summary>Furos diretos (so em lacos externos).</summary>
        public List<Loop> Holes = new();
        /// <summary>Ponto em que o laco cruza a si mesmo, se cruza.</summary>
        public V2? Crossing;
    }

    public sealed class Plan
    {
        public List<Loop> Loops = new();
        public List<string> Warnings = new();
        public int Caps;
    }

    /// <summary>Tolerancia para considerar duas pontas de contorno coincidentes.</summary>
    private const double JoinTol = 0.05;

    public static Plan Build(IReadOnlyList<AxisInput> axes, ViarioResult result)
    {
        Plan plan = new();

        List<List<Seg>> pieces = new();
        List<List<Seg>> closed = new();
        foreach (OutputLine line in result.Lines)
        {
            if (line.Segs.Count == 0)
                continue;
            if (line.Level == EdgeLevel.FaceMF)
            {
                if (line.Closed || V2.Dist(line.Segs[0].A, line.Segs[^1].B) < JoinTol)
                    closed.Add(line.Segs);
                else
                    pieces.Add(line.Segs);
            }
            else if ((line.Level == EdgeLevel.Ilha || line.Level == EdgeLevel.Canteiro) &&
                     (line.Closed || V2.Dist(line.Segs[0].A, line.Segs[^1].B) < JoinTol))
            {
                closed.Add(line.Segs);
            }
        }

        // Tampas nas pontas livres: para cada ponta de eixo, as duas pontas de contorno
        // mais proximas das posicoes esperadas (eixo +/- afastamento da face).
        List<(V2 P, int Piece, int End)> ends = new();
        for (int i = 0; i < pieces.Count; i++)
        {
            ends.Add((pieces[i][0].A, i, 0));
            ends.Add((pieces[i][^1].B, i, 1));
        }

        bool[] capped = new bool[ends.Count];
        foreach (AxisInput axis in axes)
        {
            if (axis.Path.Closed)
                continue;
            foreach (double s in new[] { 0.0, axis.Path.Length })
            {
                V2 e = axis.Path.PointAt(s);
                V2 n = axis.Path.TangentAt(s).Left;
                int[] picked = new int[2];
                bool ok = true;
                for (int k = 0; k < 2 && ok; k++)
                {
                    int side = k == 0 ? RoadSection.Esquerda : RoadSection.Direita;
                    V2 expected = e + (n * (side * axis.Section.EdgeOffset(EdgeLevel.FaceMF, side)));
                    int best = -1;
                    double bestD = 1.5 + 6.0; // folga para alargamento na ponta
                    for (int m = 0; m < ends.Count; m++)
                    {
                        if (capped[m] || (k == 1 && m == picked[0]))
                            continue;
                        double d = V2.Dist(ends[m].P, expected);
                        if (d >= bestD)
                            continue;
                        (double sp, _, _) = axis.Path.Project(ends[m].P);
                        if (Math.Abs(sp - s) > 0.75)
                            continue;
                        // Do lado certo do eixo.
                        if (Math.Sign(V2.Dot(ends[m].P - e, n)) != side)
                            continue;
                        bestD = d;
                        best = m;
                    }

                    if (best < 0)
                        ok = false;
                    else
                        picked[k] = best;
                }

                if (!ok)
                    continue; // ponta em no (faces viraram esquina) ou sem par: nada a tampar

                V2 a = ends[picked[0]].P, b = ends[picked[1]].P;
                if (V2.Dist(a, b) < 1e-6)
                    continue;
                pieces.Add(new List<Seg> { Seg.Line(a, b) });
                capped[picked[0]] = capped[picked[1]] = true;
                plan.Caps++;
            }
        }

        // Pequenas falhas (ate 2 m) entre pontas soltas sao fechadas com uma reta.
        for (int pass = 0; pass < 2; pass++)
        {
            List<(List<Seg> Segs, bool Closed)> partial = Chain(pieces);
            List<(V2 P, int Chain, int End)> loose = new();
            for (int i = 0; i < partial.Count; i++)
            {
                if (partial[i].Closed)
                    continue;
                loose.Add((partial[i].Segs[0].A, i, 0));
                loose.Add((partial[i].Segs[^1].B, i, 1));
            }

            if (loose.Count == 0)
                break;

            bool[] done = new bool[loose.Count];
            int added = 0;
            for (int i = 0; i < loose.Count; i++)
            {
                if (done[i])
                    continue;
                int best = -1;
                double bestD = 2.0;
                for (int j = 0; j < loose.Count; j++)
                {
                    if (j == i || done[j])
                        continue;
                    double d = V2.Dist(loose[i].P, loose[j].P);
                    if (d < bestD && d > 1e-9)
                    {
                        bestD = d;
                        best = j;
                    }
                }

                if (best < 0)
                    continue;
                pieces.Add(new List<Seg> { Seg.Line(loose[i].P, loose[best].P) });
                done[i] = done[best] = true;
                added++;
            }

            if (added == 0)
                break;
        }

        // Encadeia tudo em lacos fechados.
        foreach ((List<Seg> segs, bool isClosed) in Chain(pieces))
        {
            if (!isClosed)
            {
                plan.Warnings.Add($"Contorno do pavimento aberto perto de {segs[0].A} (linha desvinculada ou ponta sem tampa).");
                continue;
            }

            closed.Add(segs);
        }

        // Lacos que cruzam a si mesmos (duas faces de meio-fio que se tocam, dois arcos de
        // esquina sobrepostos) sao desatados no cruzamento: a lasca fica fora, o resto segue.
        List<List<Seg>> untangled = new();
        foreach (List<Seg> ring in closed.Select(Clean))
        {
            List<List<Seg>> parts = Untangle(ring, 0);
            if (parts.Count > 1)
            {
                V2 at = SelfCrossing(ring) ?? ring[0].A;
                double dropped = parts.Where(p => Math.Abs(ViarioEngine.RingArea(p)) < SliverArea).Sum(p => Math.Abs(ViarioEngine.RingArea(p)));
                plan.Warnings.Add($"Contorno cruzava a si mesmo perto de {at} (faces de meio-fio se tocam): lasca de {dropped:0.##} m2 descartada; confira a geometria nesse trecho.");
            }

            untangled.AddRange(parts.Where(p => Math.Abs(ViarioEngine.RingArea(p)) >= SliverArea));
        }

        // Classifica: laco externo = nao esta dentro de nenhum outro.
        List<Loop> loops = untangled
            .Where(c => c.Count > 2 && Math.Abs(ViarioEngine.RingArea(c)) > 1e-3)
            .Select(c => new Loop { Segs = c, Area = Math.Abs(ViarioEngine.RingArea(c)) })
            .OrderByDescending(l => l.Area)
            .ToList();

        foreach (Loop loop in loops)
        {
            V2 probe = loop.Segs[0].PointAt(0.5) + (loop.Segs[0].TangentAt(0.5).Left * 1e-3);
            // ponto um pouco para dentro do laco: tenta os dois lados
            bool inside = ViarioEngine.RingContains(loop.Segs, probe);
            if (!inside)
                probe = loop.Segs[0].PointAt(0.5) - (loop.Segs[0].TangentAt(0.5).Left * 1e-3);
            List<Loop> around = loops.Where(o => !ReferenceEquals(o, loop) && o.Area > loop.Area && ViarioEngine.RingContains(o.Segs, probe)).ToList();
            loop.Outer = around.Count % 2 == 0;
            loop.Parent = around.Count == 0 ? null : around.OrderBy(o => o.Area).First();
        }

        // Furos pendurados no seu contorno externo (o menor que os contem).
        foreach (Loop loop in loops.Where(l => !l.Outer && l.Parent is not null))
            loop.Parent!.Holes.Add(loop);

        // Diagnostico de contorno: um laco que cruza a si mesmo (ilha estreita, faces que se
        // tocam) faz o AutoCAD descartar ou preencher errado a regiao.
        foreach (Loop loop in loops)
        {
            V2? x = SelfCrossing(loop.Segs);
            if (x is not null)
            {
                loop.Crossing = x;
                plan.Warnings.Add($"Contorno do pavimento cruza a si mesmo perto de {x.Value} (faces de meio-fio se tocam; a hachura pode falhar ali).");
            }
        }

        plan.Loops = loops;
        return plan;
    }

    /// <summary>Area liquida esperada da hachura (externos menos furos diretos).</summary>
    public static double NetArea(Loop outer) => Math.Max(0.0, outer.Area - outer.Holes.Sum(h => h.Area));

    /// <summary>Remove trechos nulos e emenda as pontas (um vertice repetido faz o laco falhar no AutoCAD).</summary>
    private static List<Seg> Clean(List<Seg> segs)
    {
        List<Seg> result = new();
        foreach (Seg s in segs)
        {
            if (s.Length < 1e-4)
                continue;
            if (result.Count > 0 && V2.Dist(result[^1].B, s.A) > 1e-9)
                result.Add(new Seg(result[^1].B, s.A, 0.0));
            result.Add(s);
        }

        if (result.Count > 0 && V2.Dist(result[^1].B, result[0].A) > 1e-9)
            result.Add(Seg.Line(result[^1].B, result[0].A));
        return result;
    }

    /// <summary>Area abaixo da qual um pedaco desatado e lasca (sobreposicao de faces), nao regiao.</summary>
    private const double SliverArea = 0.5;

    /// <summary>
    /// Separa um laco que cruza a si mesmo em lacos simples, cortando no cruzamento:
    /// a parte entre os dois trechos que se cruzam e o resto. Recursivo ate nao
    /// haver cruzamento (ou 8 niveis).
    /// </summary>
    private static List<List<Seg>> Untangle(List<Seg> segs, int depth)
    {
        (int I, int J, double T1, double T2, V2 P)? x = FirstCrossing(segs);
        if (x is null || depth > 8)
            return new List<List<Seg>> { segs };

        (int i, int j, double t1, double t2, V2 p) = x.Value;
        List<Seg> inner = new();
        Seg head = segs[i].Sub(t1, 1.0);
        if (head.Length > 1e-6)
            inner.Add(new Seg(p, head.B, head.Bulge));
        for (int k = i + 1; k < j; k++)
            inner.Add(segs[k]);
        Seg tail = segs[j].Sub(0.0, t2);
        if (tail.Length > 1e-6)
            inner.Add(new Seg(tail.A, p, tail.Bulge));

        List<Seg> outer = new();
        Seg jRest = segs[j].Sub(t2, 1.0);
        if (jRest.Length > 1e-6)
            outer.Add(new Seg(p, jRest.B, jRest.Bulge));
        for (int k = j + 1; k < segs.Count; k++)
            outer.Add(segs[k]);
        for (int k = 0; k < i; k++)
            outer.Add(segs[k]);
        Seg iRest = segs[i].Sub(0.0, t1);
        if (iRest.Length > 1e-6)
            outer.Add(new Seg(iRest.A, p, iRest.Bulge));

        List<List<Seg>> result = new();
        foreach (List<Seg> part in new[] { inner, outer })
        {
            List<Seg> clean = Clean(part);
            if (clean.Count < 2 || Math.Abs(ViarioEngine.RingArea(clean)) < 1e-6)
                continue;
            result.AddRange(Untangle(clean, depth + 1));
        }

        return result.Count == 0 ? new List<List<Seg>> { segs } : result;
    }

    private static (int I, int J, double T1, double T2, V2 P)? FirstCrossing(IReadOnlyList<Seg> segs)
    {
        int n = segs.Count;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1)
                    continue;
                foreach ((double t1, double t2, V2 p) in Seg.Intersect(segs[i], segs[j], 1e-6))
                {
                    bool endI = t1 < 1e-6 || t1 > 1 - 1e-6;
                    bool endJ = t2 < 1e-6 || t2 > 1 - 1e-6;
                    if (endI && endJ)
                        continue;
                    return (i, j, t1, t2, p);
                }
            }
        }

        return null;
    }

    /// <summary>Primeiro ponto em que o laco cruza a si mesmo (fora das emendas entre vizinhos), ou null.</summary>
    private static V2? SelfCrossing(IReadOnlyList<Seg> segs)
    {
        return FirstCrossing(segs)?.P;
    }

    /// <summary>Encadeia pedacos pelas pontas (tolerancia JoinTol) em cadeias; fechada quando as pontas se encontram.</summary>
    private static List<(List<Seg> Segs, bool Closed)> Chain(List<List<Seg>> pieces)
    {
        List<List<Seg>> list = pieces.Where(p => p.Count > 0).ToList();
        bool[] used = new bool[list.Count];
        List<(List<Seg>, bool)> result = new();

        (int Piece, int End)? FindAt(V2 p)
        {
            int best = -1, bestEnd = 0;
            double bestD = JoinTol;
            for (int i = 0; i < list.Count; i++)
            {
                if (used[i])
                    continue;
                double d0 = V2.Dist(list[i][0].A, p), d1 = V2.Dist(list[i][^1].B, p);
                if (d0 < bestD) { bestD = d0; best = i; bestEnd = 0; }
                if (d1 < bestD) { bestD = d1; best = i; bestEnd = 1; }
            }

            return best < 0 ? null : (best, bestEnd);
        }

        static List<Seg> Reversed(List<Seg> segs) => segs.AsEnumerable().Reverse().Select(s => s.Reverse()).ToList();

        for (int i = 0; i < list.Count; i++)
        {
            if (used[i])
                continue;
            used[i] = true;
            List<Seg> chain = new(list[i]);
            while (V2.Dist(chain[0].A, chain[^1].B) >= JoinTol && FindAt(chain[^1].B) is (int piece, int end))
            {
                used[piece] = true;
                List<Seg> add = end == 0 ? list[piece] : Reversed(list[piece]);
                // Emenda exata: a primeira ponta do pedaco passa a ser a ultima da cadeia.
                if (V2.Dist(add[0].A, chain[^1].B) > 1e-9)
                    add[0] = new Seg(chain[^1].B, add[0].B, add[0].Bulge);
                chain.AddRange(add);
            }

            bool isClosed = V2.Dist(chain[0].A, chain[^1].B) < JoinTol;
            if (isClosed && V2.Dist(chain[0].A, chain[^1].B) > 1e-9)
                chain[^1] = new Seg(chain[^1].A, chain[0].A, chain[^1].Bulge);
            result.Add((chain, isClosed));
        }

        return result;
    }
}
