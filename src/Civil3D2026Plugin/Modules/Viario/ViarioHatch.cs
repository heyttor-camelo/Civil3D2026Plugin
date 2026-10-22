using System;
using System.Collections.Generic;
using System.Globalization;
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
///
/// Redes grandes (municipio inteiro): o pavimento e recortado numa grade de blocos
/// (tileSize, em m). Cada bloco vira hachuras pequenas, com poucos furos, e so os
/// blocos que mudaram sao refeitos. Todas as buscas (pontas, encadeamento, cruzamentos,
/// "dentro de") usam grade espacial: o tempo cresce com o tamanho da rede, nao com o quadrado.
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
        /// <summary>Bloco da grade (0,0 sem recorte).</summary>
        public (int X, int Y) Tile;
        public Box2 Box;
    }

    public sealed class Plan
    {
        public List<Loop> Loops = new();
        public List<string> Warnings = new();
        public int Caps;
        /// <summary>Lado do bloco usado (0 = sem recorte).</summary>
        public double TileSize;

        /// <summary>Lacos de cada bloco.</summary>
        public IEnumerable<IGrouping<(int X, int Y), Loop>> ByTile() => Loops.GroupBy(l => l.Tile);
    }

    /// <summary>Tolerancia para considerar duas pontas de contorno coincidentes.</summary>
    private const double JoinTol = 0.05;

    /// <summary>
    /// Origem da grade de blocos. Fora dos valores redondos, para que linhas de grade nao
    /// caiam exatamente sobre vertices ou faces retas desenhadas em coordenadas inteiras.
    /// </summary>
    private const double TileOrigin = 0.3719;

    /// <summary>Menor bloco aceito (abaixo disso, recortar so multiplica hachuras).</summary>
    public const double MinTile = 50.0;

    public static Plan Build(IReadOnlyList<AxisInput> axes, ViarioResult result, double tileSize = 0.0)
    {
        Plan plan = new() { TileSize = tileSize >= MinTile ? tileSize : 0.0 };

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

        const double capReach = 1.5 + 6.0; // folga para alargamento na ponta
        PointGrid endGrid = new(capReach);
        for (int m = 0; m < ends.Count; m++)
            endGrid.Add(ends[m].P, m);

        bool[] capped = new bool[ends.Count];
        List<int> near = new();
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
                    double bestD = capReach;
                    endGrid.Query(expected, capReach, near);
                    foreach (int m in near)
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

            const double gap = 2.0;
            PointGrid looseGrid = new(gap);
            for (int i = 0; i < loose.Count; i++)
                looseGrid.Add(loose[i].P, i);

            bool[] done = new bool[loose.Count];
            int added = 0;
            for (int i = 0; i < loose.Count; i++)
            {
                if (done[i])
                    continue;
                int best = -1;
                double bestD = gap;
                looseGrid.Query(loose[i].P, gap, near);
                foreach (int j in near)
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

        untangled = untangled.Where(c => c.Count > 2 && Math.Abs(ViarioEngine.RingArea(c)) > 1e-3).ToList();

        // Recorte em blocos (ou um bloco so) e classificacao dentro de cada bloco.
        Dictionary<(int X, int Y), List<List<Seg>>> tiles = plan.TileSize > 0
            ? SplitIntoTiles(untangled, plan.TileSize, plan.Warnings)
            : new Dictionary<(int X, int Y), List<List<Seg>>> { [(0, 0)] = untangled };

        foreach (((int X, int Y) tile, List<List<Seg>> rings) in tiles.OrderBy(t => t.Key.Y).ThenBy(t => t.Key.X))
            plan.Loops.AddRange(Classify(rings, tile, plan.Warnings));

        return plan;
    }

    /// <summary>
    /// Classifica os lacos de um bloco: externo = nao esta dentro de nenhum outro (ou dentro
    /// de um numero par); furo = pendurado no menor externo que o contem.
    /// </summary>
    private static List<Loop> Classify(List<List<Seg>> rings, (int X, int Y) tile, List<string> warnings)
    {
        List<Loop> loops = rings
            .Where(c => c.Count > 2 && Math.Abs(ViarioEngine.RingArea(c)) > 1e-3)
            .Select(c => new Loop { Segs = c, Area = Math.Abs(ViarioEngine.RingArea(c)), Tile = tile, Box = BoxOf(c) })
            .OrderByDescending(l => l.Area)
            .ToList();

        foreach (Loop loop in loops)
        {
            V2 probe = loop.Segs[0].PointAt(0.5) + (loop.Segs[0].TangentAt(0.5).Left * 1e-3);
            // ponto um pouco para dentro do laco: tenta os dois lados
            bool inside = ViarioEngine.RingContains(loop.Segs, probe);
            if (!inside)
                probe = loop.Segs[0].PointAt(0.5) - (loop.Segs[0].TangentAt(0.5).Left * 1e-3);
            List<Loop> around = loops.Where(o => !ReferenceEquals(o, loop) && o.Area > loop.Area && Inside(o.Box, probe) &&
                                                 ViarioEngine.RingContains(o.Segs, probe)).ToList();
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
                warnings.Add($"Contorno do pavimento cruza a si mesmo perto de {x.Value} (faces de meio-fio se tocam; a hachura pode falhar ali).");
            }
        }

        return loops;
    }

    private static bool Inside(Box2 b, V2 p) => p.X >= b.MinX && p.X <= b.MaxX && p.Y >= b.MinY && p.Y <= b.MaxY;

    private static Box2 BoxOf(IEnumerable<Seg> segs)
    {
        Box2 box = Box2.Empty;
        foreach (Seg s in segs)
            box = box.Union(s.Bounds());
        return box;
    }

    /// <summary>Area liquida esperada da hachura (externos menos furos diretos).</summary>
    public static double NetArea(Loop outer) => Math.Max(0.0, outer.Area - outer.Holes.Sum(h => h.Area));

    /// <summary>Assinatura da geometria dos lacos de um bloco (para refazer so os blocos que mudaram).</summary>
    public static string Signature(IEnumerable<Loop> loops)
    {
        ulong hash = 14695981039346656037UL;
        void Mix(long v)
        {
            hash ^= (ulong)v;
            hash *= 1099511628211UL;
        }

        foreach (Loop l in loops)
        {
            Mix(l.Outer ? 1 : 2);
            Mix(l.Segs.Count);
            foreach (Seg s in l.Segs)
            {
                Mix((long)Math.Round(s.A.X * 1000.0));
                Mix((long)Math.Round(s.A.Y * 1000.0));
                Mix((long)Math.Round(s.Bulge * 1e6));
            }
        }

        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    /// <summary>Chave texto de um bloco (gravada na hachura).</summary>
    public static string TileKey((int X, int Y) tile) =>
        tile.X.ToString(CultureInfo.InvariantCulture) + "," + tile.Y.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------
    // Recorte em blocos
    // ------------------------------------------------------------------

    private static double GridLine(int k, double size) => TileOrigin + (k * size);

    private static int TileIndex(double v, double size) => (int)Math.Floor((v - TileOrigin) / size);

    /// <summary>
    /// Recorta os lacos (par/impar) na grade de blocos. Cada laco e partido onde cruza as
    /// linhas da grade; os trechos de cada bloco sao fechados pelas bordas do bloco que
    /// ficam dentro do pavimento. O que esta dentro ou fora troca a cada cruzamento ao
    /// longo da borda; o estado inicial vem de um teste de ponto (raio) contra todos os lacos.
    /// </summary>
    private static Dictionary<(int X, int Y), List<List<Seg>>> SplitIntoTiles(List<List<Seg>> rings, double size, List<string> warnings)
    {
        Dictionary<(int X, int Y), List<List<Seg>>> outTiles = new();
        Dictionary<(int X, int Y), List<List<Seg>>> runs = new();
        Dictionary<(int X, int Y), List<V2>> crossings = new();

        List<List<Seg>> Bucket(Dictionary<(int, int), List<List<Seg>>> map, (int, int) key)
        {
            if (!map.TryGetValue(key, out List<List<Seg>>? list))
                map[key] = list = new List<List<Seg>>();
            return list;
        }

        (int X, int Y) TileOf(Seg s)
        {
            V2 m = s.PointAt(0.5);
            return (TileIndex(m.X, size), TileIndex(m.Y, size));
        }

        foreach (List<Seg> ring in rings)
        {
            Box2 box = BoxOf(ring);
            if (TileIndex(box.MinX, size) == TileIndex(box.MaxX, size) && TileIndex(box.MinY, size) == TileIndex(box.MaxY, size))
            {
                Bucket(outTiles, (TileIndex(box.MinX, size), TileIndex(box.MinY, size))).Add(ring);
                continue;
            }

            // Parte cada trecho nas linhas da grade (pontas exatamente sobre a linha).
            List<Seg> parts = new();
            foreach (Seg s in ring)
                SplitAtGrid(s, size, parts);
            if (parts.Count < 2)
                continue;

            (int X, int Y)[] tileOf = parts.Select(TileOf).ToArray();
            int n = parts.Count;
            int first = -1;
            for (int i = 0; i < n; i++)
            {
                if (tileOf[i] != tileOf[(i + n - 1) % n])
                {
                    first = i;
                    break;
                }
            }

            if (first < 0)
            {
                Bucket(outTiles, tileOf[0]).Add(ring);
                continue;
            }

            // Emendas entre blocos: ponto exatamente sobre a linha da grade nos dois trechos.
            for (int i = 0; i < n; i++)
            {
                int prev = (i + n - 1) % n;
                if (tileOf[i] == tileOf[prev])
                    continue;
                V2 q = parts[i].A;
                double x = q.X, y = q.Y;
                if (tileOf[i].X != tileOf[prev].X)
                    x = GridLine(Math.Max(tileOf[i].X, tileOf[prev].X), size);
                if (tileOf[i].Y != tileOf[prev].Y)
                    y = GridLine(Math.Max(tileOf[i].Y, tileOf[prev].Y), size);
                q = new V2(x, y);
                parts[prev] = new Seg(parts[prev].A, q, parts[prev].Bulge);
                parts[i] = new Seg(q, parts[i].B, parts[i].Bulge);
            }

            // Trechos seguidos do mesmo bloco viram um caminho aberto com as pontas na borda.
            List<Seg>? run = null;
            (int X, int Y) runTile = default;
            for (int k = 0; k < n; k++)
            {
                int i = (first + k) % n;
                if (run is null || tileOf[i] != runTile)
                {
                    if (run is not null)
                        CloseRun(runTile, run);
                    run = new List<Seg>();
                    runTile = tileOf[i];
                }

                run.Add(parts[i]);
            }

            if (run is not null)
                CloseRun(runTile, run);
        }

        void CloseRun((int X, int Y) tile, List<Seg> run)
        {
            Bucket(runs, tile).Add(run);
            if (!crossings.TryGetValue(tile, out List<V2>? pts))
                crossings[tile] = pts = new List<V2>();
            pts.Add(run[0].A);
            pts.Add(run[^1].B);
        }

        if (rings.Count == 0)
            return outTiles;
        RayIndex region = new(rings);

        // Blocos sem cruzamento na borda: ou estao todos fora do pavimento, ou todos dentro
        // (praca maior que o bloco, area cercada sem a face interna): ai a borda inteira entra.
        Box2 extent = Box2.Empty;
        foreach (List<Seg> ring in rings)
            extent = extent.Union(BoxOf(ring));
        for (int tx = TileIndex(extent.MinX, size); tx <= TileIndex(extent.MaxX, size); tx++)
        {
            for (int ty = TileIndex(extent.MinY, size); ty <= TileIndex(extent.MaxY, size); ty++)
            {
                if (runs.ContainsKey((tx, ty)))
                    continue;
                double x0 = GridLine(tx, size), x1 = GridLine(tx + 1, size);
                double y0 = GridLine(ty, size), y1 = GridLine(ty + 1, size);
                if (!region.Contains(new V2((x0 + x1) / 2.0, y0)))
                    continue;
                V2 a = new(x0, y0), b = new(x1, y0), c = new(x1, y1), d = new(x0, y1);
                Bucket(outTiles, (tx, ty)).Add(new List<Seg> { Seg.Line(a, b), Seg.Line(b, c), Seg.Line(c, d), Seg.Line(d, a) });
            }
        }

        foreach (((int X, int Y) tile, List<List<Seg>> tileRuns) in runs)
        {
            double x0 = GridLine(tile.X, size), x1 = GridLine(tile.X + 1, size);
            double y0 = GridLine(tile.Y, size), y1 = GridLine(tile.Y + 1, size);
            double w = x1 - x0, h = y1 - y0;

            // Posicao ao longo da borda (anti-horario a partir do canto inferior esquerdo).
            (double U, V2 P) OnBorder(V2 p)
            {
                double dB = Math.Abs(p.Y - y0), dR = Math.Abs(p.X - x1), dT = Math.Abs(p.Y - y1), dL = Math.Abs(p.X - x0);
                double m = Math.Min(Math.Min(dB, dR), Math.Min(dT, dL));
                if (m == dB)
                    return (Math.Clamp(p.X - x0, 0, w), new V2(p.X, y0));
                if (m == dR)
                    return (w + Math.Clamp(p.Y - y0, 0, h), new V2(x1, p.Y));
                if (m == dT)
                    return (w + h + Math.Clamp(x1 - p.X, 0, w), new V2(p.X, y1));
                return ((2 * w) + h + Math.Clamp(y1 - p.Y, 0, h), new V2(x0, p.Y));
            }

            V2 AtU(double u)
            {
                if (u <= w)
                    return new V2(x0 + u, y0);
                if (u <= w + h)
                    return new V2(x1, y0 + (u - w));
                if (u <= (2 * w) + h)
                    return new V2(x1 - (u - w - h), y1);
                return new V2(x0, y1 - (u - (2 * w) - h));
            }

            // Cruzamentos e cantos em ordem ao longo da borda.
            List<(double U, V2 P, bool Cross)> marks = crossings[tile].Select(p =>
            {
                (double u, V2 q) = OnBorder(p);
                return (u, p, true);
            }).ToList();
            foreach (double u in new[] { 0.0, w, w + h, (2 * w) + h })
                marks.Add((u, AtU(u), false));
            marks = marks.OrderBy(m => m.U).ThenBy(m => m.Cross ? 1 : 0).ToList();

            // Bordas entre marcas consecutivas; dentro/fora troca a cada cruzamento.
            int count = marks.Count;
            double perimeter = 2 * (w + h);
            int longest = 0;
            double longestLen = -1;
            for (int i = 0; i < count; i++)
            {
                double len = ((i + 1 < count ? marks[i + 1].U : marks[0].U + perimeter) - marks[i].U);
                if (len > longestLen)
                {
                    longestLen = len;
                    longest = i;
                }
            }

            bool[] inside = new bool[count];
            {
                double u0 = marks[longest].U, u1 = longest + 1 < count ? marks[longest + 1].U : marks[0].U + perimeter;
                double um = (u0 + u1) / 2.0;
                inside[longest] = region.Contains(AtU(um >= perimeter ? um - perimeter : um));
                for (int k = 1; k < count; k++)
                {
                    int i = (longest + k) % count;
                    int prev = (i + count - 1) % count;
                    inside[i] = marks[i].Cross ? !inside[prev] : inside[prev];
                }
            }

            List<List<Seg>> edges = new(tileRuns);
            for (int i = 0; i < count; i++)
            {
                if (!inside[i])
                    continue;
                V2 a = marks[i].P;
                V2 b = marks[(i + 1) % count].P;
                if (V2.Dist(a, b) < 1e-9)
                    continue;
                edges.Add(new List<Seg> { Seg.Line(a, b) });
            }

            List<List<Seg>> tileRings = Bucket(outTiles, tile);
            foreach ((List<Seg> segs, bool isClosed) in ChainExact(edges))
            {
                if (!isClosed)
                {
                    warnings.Add($"Recorte da hachura no bloco {TileKey(tile)} nao fechou perto de {segs[0].A}; o trecho ficou sem hachura.");
                    continue;
                }

                List<Seg> clean = Clean(segs);
                if (clean.Count > 2)
                    tileRings.Add(clean);
            }
        }

        return outTiles;
    }

    /// <summary>Parte o trecho onde cruza as linhas da grade (pontas exatamente sobre a linha).</summary>
    private static void SplitAtGrid(Seg s, double size, List<Seg> output)
    {
        Box2 b = s.Bounds();
        List<(double T, V2 P)> cuts = new();
        for (int k = TileIndex(b.MinX, size) + 1; k <= TileIndex(b.MaxX, size); k++)
        {
            double x = GridLine(k, size);
            foreach ((double t, _, V2 p) in Seg.Intersect(s, Seg.Line(new V2(x, b.MinY - 1.0), new V2(x, b.MaxY + 1.0))))
            {
                if (t > 1e-9 && t < 1 - 1e-9)
                    cuts.Add((t, new V2(x, p.Y)));
            }
        }

        for (int k = TileIndex(b.MinY, size) + 1; k <= TileIndex(b.MaxY, size); k++)
        {
            double y = GridLine(k, size);
            foreach ((double t, _, V2 p) in Seg.Intersect(s, Seg.Line(new V2(b.MinX - 1.0, y), new V2(b.MaxX + 1.0, y))))
            {
                if (t > 1e-9 && t < 1 - 1e-9)
                    cuts.Add((t, new V2(p.X, y)));
            }
        }

        if (cuts.Count == 0)
        {
            output.Add(s);
            return;
        }

        cuts.Sort((p, q) => p.T.CompareTo(q.T));
        int start = output.Count;
        double t0 = 0.0;
        V2 a = s.A;
        foreach ((double t, V2 p) in cuts)
        {
            if (t - t0 < 1e-9)
                continue;
            output.Add(new Seg(a, p, s.IsArc ? s.Sub(t0, t).Bulge : 0.0));
            t0 = t;
            a = p;
        }

        if (1.0 - t0 > 1e-9)
            output.Add(new Seg(a, s.B, s.IsArc ? s.Sub(t0, 1.0).Bulge : 0.0));
        else if (output.Count > start)
            output[^1] = new Seg(output[^1].A, s.B, output[^1].Bulge);
        else
            output.Add(s);
    }

    /// <summary>Encadeia pedacos com pontas exatamente iguais (recorte da grade).</summary>
    private static List<(List<Seg> Segs, bool Closed)> ChainExact(List<List<Seg>> pieces)
    {
        static (long, long) Key(V2 p) => ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6));

        Dictionary<(long, long), List<(int Piece, int End)>> at = new();
        void Add(V2 p, int piece, int end)
        {
            if (!at.TryGetValue(Key(p), out List<(int, int)>? list))
                at[Key(p)] = list = new List<(int, int)>();
            list.Add((piece, end));
        }

        for (int i = 0; i < pieces.Count; i++)
        {
            Add(pieces[i][0].A, i, 0);
            Add(pieces[i][^1].B, i, 1);
        }

        bool[] used = new bool[pieces.Count];
        List<(List<Seg>, bool)> result = new();
        for (int i = 0; i < pieces.Count; i++)
        {
            if (used[i])
                continue;
            used[i] = true;
            List<Seg> chain = new(pieces[i]);
            while (Key(chain[0].A) != Key(chain[^1].B))
            {
                (int Piece, int End)? next = null;
                if (at.TryGetValue(Key(chain[^1].B), out List<(int Piece, int End)>? list))
                {
                    foreach ((int Piece, int End) c in list)
                    {
                        if (!used[c.Piece])
                        {
                            next = c;
                            break;
                        }
                    }
                }

                if (next is not (int piece, int end))
                    break;
                used[piece] = true;
                IEnumerable<Seg> add = end == 0 ? pieces[piece] : pieces[piece].AsEnumerable().Reverse().Select(s => s.Reverse());
                foreach (Seg s in add)
                    chain.Add(V2.Dist(s.A, chain[^1].B) > 1e-9 ? new Seg(chain[^1].B, s.B, s.Bulge) : s);
            }

            bool isClosed = Key(chain[0].A) == Key(chain[^1].B);
            if (isClosed && V2.Dist(chain[0].A, chain[^1].B) > 1e-9)
                chain[^1] = new Seg(chain[^1].A, chain[0].A, chain[^1].Bulge);
            result.Add((chain, isClosed));
        }

        return result;
    }

    /// <summary>
    /// Ponto dentro do pavimento (par/impar sobre todos os lacos): raio horizontal para a
    /// direita, com as arestas (arcos amostrados como no RingContains) guardadas por faixa de Y.
    /// </summary>
    private sealed class RayIndex
    {
        private readonly List<(V2 A, V2 B)>[] _bands;
        private readonly double _y0, _band;

        public RayIndex(List<List<Seg>> rings)
        {
            List<(V2, V2)> edges = new();
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (List<Seg> ring in rings)
            {
                List<V2> pts = new();
                foreach (Seg s in ring)
                {
                    int n = s.IsArc ? 16 : 1;
                    for (int k = 0; k < n; k++)
                        pts.Add(s.PointAt((double)k / n));
                }

                for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                {
                    edges.Add((pts[j], pts[i]));
                    minY = Math.Min(minY, pts[i].Y);
                    maxY = Math.Max(maxY, pts[i].Y);
                }
            }

            int count = Math.Clamp((int)Math.Sqrt(edges.Count), 1, 4096);
            _y0 = minY;
            _band = Math.Max((maxY - minY) / count, 1e-6);
            _bands = new List<(V2, V2)>[count];
            for (int i = 0; i < count; i++)
                _bands[i] = new List<(V2, V2)>();
            foreach ((V2 a, V2 b) in edges)
            {
                int i0 = Band(Math.Min(a.Y, b.Y)), i1 = Band(Math.Max(a.Y, b.Y));
                for (int i = i0; i <= i1; i++)
                    _bands[i].Add((a, b));
            }
        }

        private int Band(double y) => Math.Clamp((int)Math.Floor((y - _y0) / _band), 0, _bands.Length - 1);

        public bool Contains(V2 p)
        {
            bool inside = false;
            foreach ((V2 a, V2 b) in _bands[Band(p.Y)])
            {
                if (((b.Y > p.Y) != (a.Y > p.Y)) &&
                    (p.X < ((a.X - b.X) * (p.Y - b.Y) / (a.Y - b.Y)) + b.X))
                {
                    inside = !inside;
                }
            }

            return inside;
        }
    }

    // ------------------------------------------------------------------
    // Lacos
    // ------------------------------------------------------------------

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

    /// <summary>
    /// Primeiro cruzamento do laco com ele mesmo, na ordem (i, j) dos trechos. Lacos longos
    /// (o contorno de uma cidade inteira) so testam os pares de trechos que dividem uma
    /// celula da grade - o mesmo resultado da varredura de todos os pares.
    /// </summary>
    private static (int I, int J, double T1, double T2, V2 P)? FirstCrossing(IReadOnlyList<Seg> segs)
    {
        int n = segs.Count;
        (int, int, double, double, V2)? Test(int i, int j)
        {
            if (i == 0 && j == n - 1)
                return null;
            foreach ((double t1, double t2, V2 p) in Seg.Intersect(segs[i], segs[j], 1e-6))
            {
                bool endI = t1 < 1e-6 || t1 > 1 - 1e-6;
                bool endJ = t2 < 1e-6 || t2 > 1 - 1e-6;
                if (endI && endJ)
                    continue;
                return (i, j, t1, t2, p);
            }

            return null;
        }

        if (n <= 48)
        {
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 2; j < n; j++)
                {
                    if (Test(i, j) is { } hit)
                        return hit;
                }
            }

            return null;
        }

        // Grade: celula do tamanho medio de alguns trechos.
        double total = 0;
        Box2 all = Box2.Empty;
        Box2[] boxes = new Box2[n];
        for (int i = 0; i < n; i++)
        {
            boxes[i] = segs[i].Bounds().Inflate(1e-5);
            all = all.Union(boxes[i]);
            total += segs[i].Length;
        }

        double cell = Math.Max(total / n * 2.0, Math.Max(all.MaxX - all.MinX, all.MaxY - all.MinY) / 2048.0);
        cell = Math.Max(cell, 1e-3);
        Dictionary<(long, long), List<int>> grid = new();
        for (int i = 0; i < n; i++)
        {
            long cx0 = (long)Math.Floor(boxes[i].MinX / cell), cx1 = (long)Math.Floor(boxes[i].MaxX / cell);
            long cy0 = (long)Math.Floor(boxes[i].MinY / cell), cy1 = (long)Math.Floor(boxes[i].MaxY / cell);
            for (long cx = cx0; cx <= cx1; cx++)
            {
                for (long cy = cy0; cy <= cy1; cy++)
                {
                    if (!grid.TryGetValue((cx, cy), out List<int>? list))
                        grid[(cx, cy)] = list = new List<int>();
                    list.Add(i);
                }
            }
        }

        HashSet<long> pairs = new();
        foreach (List<int> list in grid.Values)
        {
            for (int a = 0; a < list.Count; a++)
            {
                for (int b = a + 1; b < list.Count; b++)
                {
                    int i = Math.Min(list[a], list[b]), j = Math.Max(list[a], list[b]);
                    if (j < i + 2 || !boxes[i].Intersects(boxes[j]))
                        continue;
                    pairs.Add(((long)i * n) + j);
                }
            }
        }

        foreach (long pair in pairs.OrderBy(p => p))
        {
            if (Test((int)(pair / n), (int)(pair % n)) is { } hit)
                return hit;
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

        PointGrid grid = new(JoinTol);
        for (int i = 0; i < list.Count; i++)
        {
            grid.Add(list[i][0].A, 2 * i);
            grid.Add(list[i][^1].B, (2 * i) + 1);
        }

        List<int> near = new();
        (int Piece, int End)? FindAt(V2 p)
        {
            int best = -1, bestEnd = 0;
            double bestD = JoinTol;
            grid.Query(p, JoinTol, near);
            // Mesma ordem da varredura completa: pedaco a pedaco, ponta inicial antes da final.
            foreach (int code in near)
            {
                int i = code / 2;
                if (used[i])
                    continue;
                double d = code % 2 == 0 ? V2.Dist(list[i][0].A, p) : V2.Dist(list[i][^1].B, p);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                    bestEnd = code % 2;
                }
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

    /// <summary>
    /// Grade de pontos para buscas por raio. A consulta devolve os indices em ordem
    /// crescente, a mesma ordem da varredura completa (desempates identicos).
    /// </summary>
    private sealed class PointGrid
    {
        private readonly double _cell;
        private readonly Dictionary<(long, long), List<(V2 P, int Index)>> _cells = new();

        public PointGrid(double cell) => _cell = Math.Max(cell, 1e-6);

        private (long, long) Cell(V2 p) => ((long)Math.Floor(p.X / _cell), (long)Math.Floor(p.Y / _cell));

        public void Add(V2 p, int index)
        {
            (long, long) c = Cell(p);
            if (!_cells.TryGetValue(c, out List<(V2, int)>? list))
                _cells[c] = list = new List<(V2, int)>();
            list.Add((p, index));
        }

        public void Query(V2 p, double radius, List<int> output)
        {
            output.Clear();
            long x0 = (long)Math.Floor((p.X - radius) / _cell), x1 = (long)Math.Floor((p.X + radius) / _cell);
            long y0 = (long)Math.Floor((p.Y - radius) / _cell), y1 = (long)Math.Floor((p.Y + radius) / _cell);
            for (long x = x0; x <= x1; x++)
            {
                for (long y = y0; y <= y1; y++)
                {
                    if (!_cells.TryGetValue((x, y), out List<(V2 P, int Index)>? list))
                        continue;
                    foreach ((V2 q, int index) in list)
                    {
                        if (Math.Abs(q.X - p.X) <= radius && Math.Abs(q.Y - p.Y) <= radius)
                            output.Add(index);
                    }
                }
            }

            output.Sort();
        }
    }
}
