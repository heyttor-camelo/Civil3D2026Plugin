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

        // Classifica: laco externo = nao esta dentro de nenhum outro.
        List<Loop> loops = closed.Where(c => c.Count > 0 && Math.Abs(ViarioEngine.RingArea(c)) > 1e-3)
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
            int depth = loops.Count(o => !ReferenceEquals(o, loop) && o.Area > loop.Area && ViarioEngine.RingContains(o.Segs, probe));
            loop.Outer = depth % 2 == 0;
        }

        plan.Loops = loops;
        return plan;
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
