using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Opcoes da sinalizacao horizontal previa (valores do Manual Brasileiro de
/// Sinalizacao de Transito, Vol. IV - conferir no projeto executivo).
/// </summary>
internal sealed class SignageOptions
{
    /// <summary>Linha divisoria de fluxos opostos (amarela) em vias sem canteiro.</summary>
    public bool Central { get; set; } = true;

    /// <summary>Seccionada (LFO-2), Continua (LFO-1) ou Dupla (LFO-3).</summary>
    public string CentralTipo { get; set; } = "Seccionada";

    /// <summary>Trecho da divisoria que vira continuo antes da linha de retencao (m).</summary>
    public double ContinuaAntes { get; set; } = 15.0;

    /// <summary>Linhas de divisao de fluxos de mesmo sentido (LMS-2, branca seccionada).</summary>
    public bool Faixas { get; set; } = true;

    /// <summary>Divisao de faixas continua (LMS-1) nos ultimos metros antes da retencao.</summary>
    public double FaixasContinuasAntes { get; set; } = 15.0;

    /// <summary>Desenho vazado: so contornos cinzas (transparentes), nada pintado.</summary>
    public bool Vazada { get; set; }

    /// <summary>Linha de bordo (LBO, branca continua).</summary>
    public bool Bordo { get; set; }

    /// <summary>Faixa de travessia de pedestres zebrada (FTP-1) em cada aproximacao.</summary>
    public bool Travessia { get; set; } = true;

    /// <summary>Linha de retencao (LRE) na aproximacao de cada no.</summary>
    public bool Retencao { get; set; } = true;

    /// <summary>Cadencia/largura pelas velocidades das secoes (senao usa Traco/Espaco/Largura).</summary>
    public bool CadenciaAuto { get; set; } = true;

    public double Largura { get; set; } = 0.10;
    public double Traco { get; set; } = 2.0;
    public double Espaco { get; set; } = 4.0;

    /// <summary>Extensao das listras da faixa de pedestres (no sentido da via).</summary>
    public double TravessiaLargura { get; set; } = 4.0;
    public double ListraLargura { get; set; } = 0.40;
    public double ListraEspaco { get; set; } = 0.40;

    /// <summary>Afastamento da faixa de pedestres em relacao ao fim da curva da esquina.</summary>
    public double TravessiaRecuo { get; set; } = 1.0;

    public double RetencaoLargura { get; set; } = 0.40;

    /// <summary>Distancia da linha de retencao ate a faixa de pedestres (min. 1,60 m).</summary>
    public double RetencaoDistancia { get; set; } = 1.60;

    public SignageOptions Clone() => (SignageOptions)MemberwiseClone();

    /// <summary>Largura, traco e espaco da linha seccionada para a velocidade (km/h).</summary>
    public (double Width, double Dash, double Gap) Cadence(double speed)
    {
        if (!CadenciaAuto)
            return (Largura, Traco, Espaco);
        if (speed >= 80.0)
            return (0.15, 4.0, 12.0);
        if (speed >= 60.0)
            return (0.10, 3.0, 6.0);
        return (0.10, 2.0, 4.0);
    }
}

internal sealed class MarkLine
{
    public MarkLine(List<Seg> segs, double width, bool yellow, string kind)
    {
        Segs = segs;
        Width = width;
        Yellow = yellow;
        Kind = kind;
    }

    public List<Seg> Segs { get; }
    public double Width { get; }
    public bool Yellow { get; }
    public string Kind { get; }
}

/// <summary>Sinalizacao horizontal previa a partir da rede calculada (geometria pura).</summary>
internal static class ViarioSignage
{
    private sealed class Approach
    {
        public double SNode;
        public int Dir;
        public double U0;      // inicio da faixa de pedestres (a partir do no)
        public double UStop;   // eixo da linha de retencao
        public double UCut;    // ate onde as linhas longitudinais param
        public bool Yield;     // entrada de rotatoria: "De a preferencia" em vez de retencao
    }

    public static List<MarkLine> Build(IReadOnlyList<AxisInput> axes, ViarioResult result, SignageOptions o)
    {
        List<MarkLine> marks = new();
        List<List<Seg>> islands = result.Lines.Where(l => l.Level == EdgeLevel.Ilha && l.Closed).Select(l => l.Segs).ToList();
        Dictionary<string, AxisInput> byKey = axes.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());

        foreach (AxisInput axis in axes)
        {
            GPath path = axis.Path;
            if (path.IsEmpty || path.Closed || path.Length < 1.0)
                continue;

            RoadSection sec = axis.Section;
            double c = sec.Canteiro / 2.0;
            double half = sec.Pista / 2.0;
            int n = sec.FaixasPorSentido;
            double lane = half / n;
            (double lineW, double dash, double gap) = o.Cadence(sec.Velocidade);

            List<Approach> approaches = Approaches(axis, byKey, result, o);
            List<(double A, double B)> cuts = approaches
                .Select(ap => ap.Dir < 0 ? (ap.SNode - ap.UCut, ap.SNode) : (ap.SNode, ap.SNode + ap.UCut))
                .ToList();

            // Gota: a divisoria central nao passa por cima da ilha.
            List<(double A, double B)> centralCuts = new(cuts);
            foreach (AxisEvent ev in axis.Events.Where(e => e.Kind == AxisEventKind.Gota))
            {
                double far = ev.Gap + ev.Length + 30.0;
                centralCuts.Add(ev.Dir < 0 ? (ev.S - far, ev.S) : (ev.S, ev.S + far));
            }

            List<(double A, double B)> free = Free(path.Length, cuts);
            List<(double A, double B)> freeCentral = Free(path.Length, centralCuts);

            // Longitudinais: continuas nos ultimos metros antes de cada retencao.
            double centerHalf = 0.0;
            if (c <= 1e-3 && o.Central)
            {
                (List<(double A, double B)> solid, List<(double A, double B)> dashed) =
                    Split(freeCentral, approaches, o.CentralTipo == "Seccionada" ? o.ContinuaAntes : double.MaxValue);
                double[] offsets = o.CentralTipo == "Dupla" ? new[] { -lineW, lineW } : new[] { 0.0 };
                centerHalf = o.CentralTipo == "Dupla" ? 1.5 * lineW : lineW / 2.0;
                foreach (double off in offsets)
                {
                    foreach ((double a, double b) in solid)
                        Add(marks, path, a, b, off, lineW, true, "LFO");
                    foreach ((double a, double b) in dashed)
                        Dashes(marks, path, a, b, off, lineW, dash, gap, true, "LFO");
                }
            }

            if (o.Faixas && n > 1)
            {
                (List<(double A, double B)> solid, List<(double A, double B)> dashed) = Split(free, approaches, o.FaixasContinuasAntes);
                for (int k = 1; k < n; k++)
                {
                    foreach (int side in new[] { -1, 1 })
                    {
                        double off = side * (c + (k * lane));
                        foreach ((double a, double b) in solid)
                            Add(marks, path, a, b, off, lineW, false, "LMS");
                        foreach ((double a, double b) in dashed)
                            Dashes(marks, path, a, b, off, lineW, dash, gap, false, "LMS");
                    }
                }
            }

            if (o.Bordo)
            {
                foreach ((double a, double b) in free)
                {
                    foreach (int side in new[] { -1, 1 })
                    {
                        Add(marks, path, a, b, side * (c + half - 0.15), lineW, false, "LBO");
                        if (c > 1e-3)
                            Add(marks, path, a, b, side * (c + 0.15), lineW, false, "LBO");
                    }
                }
            }

            // Transversais em cada aproximacao.
            foreach (Approach ap in approaches)
            {
                if (o.Travessia && !double.IsNaN(ap.U0))
                {
                    double s0 = Clamp(path, ap.SNode + (ap.Dir * ap.U0));
                    double s1 = Clamp(path, ap.SNode + (ap.Dir * (ap.U0 + o.TravessiaLargura)));
                    double step = o.ListraLargura + o.ListraEspaco;
                    double margin = 0.30;
                    foreach (int side in new[] { -1, 1 })
                    {
                        // Listras centradas na meia pista (mesma folga junto ao meio-fio e ao eixo/canteiro).
                        double from = c + margin, to = c + half - margin;
                        int count = (int)Math.Floor(((to - from) + o.ListraEspaco) / step);
                        if (count <= 0)
                            continue;
                        double used = (count * step) - o.ListraEspaco;
                        double first = from + (((to - from) - used) / 2.0) + (o.ListraLargura / 2.0);
                        for (int k = 0; k < count; k++)
                        {
                            double t = first + (k * step);
                            // Listra que cai dentro de uma ilha (gota) nao e pintada.
                            V2 mid = path.PointAt((s0 + s1) / 2.0) + (path.TangentAt((s0 + s1) / 2.0).Unit().Left * (side * t));
                            if (islands.Any(ring => ViarioEngine.RingContains(ring, mid)))
                                continue;
                            Add(marks, path, Math.Min(s0, s1), Math.Max(s0, s1), side * t, o.ListraLargura, false, "FTP");
                        }
                    }
                }

                if (o.Retencao)
                {
                    double s = Clamp(path, ap.SNode + (ap.Dir * ap.UStop));
                    V2 p = path.PointAt(s);
                    V2 normal = path.TangentAt(s).Unit().Left;

                    // Quem chega anda contra Dir; a pista dele fica do lado motor = Dir.
                    // Comeca depois da divisoria (ou do canteiro, ou da gota) e vai ate o bordo.
                    double t0 = c > 1e-3 ? c : centerHalf + (centerHalf > 0 ? 0.10 : 0.0);
                    AxisEvent? gota = axis.Events.FirstOrDefault(e =>
                        e.Kind == AxisEventKind.Gota && e.Dir == ap.Dir && Math.Abs(e.S - ap.SNode) < 1.0);
                    if (gota is not null)
                        t0 = Math.Max(t0, (gota.Width / 2.0) + 0.30);
                    double t1 = c + half;
                    if (t1 - t0 < 0.5)
                        continue;

                    if (ap.Yield)
                    {
                        // Rotatoria: linha de "De a preferencia" (tracejada) na entrada do anel.
                        double len = t1 - t0;
                        for (double x = 0.0; x < len; x += 1.0)
                        {
                            double xa = t0 + x, xb = t0 + Math.Min(len, x + 0.5);
                            marks.Add(new MarkLine(
                                new List<Seg> { Seg.Line(p + (normal * (ap.Dir * xa)), p + (normal * (ap.Dir * xb))) },
                                0.40, false, "LDP"));
                        }
                    }
                    else
                    {
                        marks.Add(new MarkLine(
                            new List<Seg> { Seg.Line(p + (normal * (ap.Dir * t0)), p + (normal * (ap.Dir * t1))) },
                            o.RetencaoLargura, false, "LRE"));
                    }
                }
            }
        }

        return marks;
    }

    /// <summary>Aproximacoes deste eixo nos nos com esquina (cruzamento, T, rotatoria).</summary>
    private static List<Approach> Approaches(AxisInput axis, Dictionary<string, AxisInput> byKey, ViarioResult result, SignageOptions o)
    {
        List<Approach> list = new();
        GPath path = axis.Path;
        double face = axis.Section.EdgeOffset(EdgeLevel.FaceMF);

        List<double> nodeStations = result.Nodes
            .Where(nd => nd.AxisKeys.Contains(axis.Key))
            .Select(nd => path.Project(nd.Point))
            .Where(pr => pr.Dist <= 0.5)
            .Select(pr => pr.S)
            .ToList();

        foreach (NodeInfo node in result.Nodes.Where(nd => nd.AxisKeys.Contains(axis.Key)))
        {
            (double sN, _, double dist) = path.Project(node.Point);
            if (dist > 0.5)
                continue;

            double crossHalf = node.AxisKeys
                .Where(k => k != axis.Key && byKey.ContainsKey(k))
                .Select(k => byKey[k].Section.EdgeOffset(EdgeLevel.FaceMF))
                .DefaultIfEmpty(0.0)
                .Max();

            List<CornerInfo> corners = result.Corners
                .Where(cr => V2.Dist(cr.Node, node.Point) < 0.5 && (cr.AxisA == axis.Key || cr.AxisB == axis.Key) && cr.Arc is not null)
                .ToList();

            foreach (int dir in new[] { -1, 1 })
            {
                bool exists = dir < 0 ? sN > 1.0 : sN < path.Length - 1.0;
                if (!exists)
                    continue;

                double uArc = -1.0;
                foreach (CornerInfo cr in corners)
                {
                    foreach (V2 q in new[] { cr.Arc!.A, cr.Arc.B })
                    {
                        (double s, _, double d) = path.Project(q);
                        double u = (s - sN) * dir;
                        if (u > 0 && Math.Abs(d - face) < 1.5)
                            uArc = Math.Max(uArc, u);
                    }
                }

                // Sem esquina neste ramo: continuacao reta, nao ha travessia.
                if (uArc < 0)
                    continue;

                // Rotatoria: travessia a >= 20 m da linha de "De a preferencia" (IPR-718 p.190).
                bool ring = node.AxisKeys.Any(k => k != axis.Key && byKey.TryGetValue(k, out AxisInput? other) && other.Path.Closed);
                double u0 = Math.Max(uArc, crossHalf) + o.TravessiaRecuo;
                double uStop = (o.Travessia ? u0 + o.TravessiaLargura + o.RetencaoDistancia : uArc + 1.0) + (o.RetencaoLargura / 2.0);
                if (ring)
                {
                    uStop = Math.Max(uArc, crossHalf) + 0.5;
                    u0 = uStop + 20.0;
                }

                // Ramo curto: ate o proximo no (metade para cada lado) ou ate a ponta do eixo.
                double sOther = dir > 0
                    ? nodeStations.Where(x => x > sN + 0.5).DefaultIfEmpty(double.NaN).Min()
                    : nodeStations.Where(x => x < sN - 0.5).DefaultIfEmpty(double.NaN).Max();
                double room = double.IsNaN(sOther)
                    ? (dir > 0 ? path.Length - sN : sN)
                    : Math.Abs(sOther - sN) / 2.0;
                double reach = ring ? (o.Travessia ? u0 + o.TravessiaLargura : uStop) : uStop + (o.RetencaoLargura / 2.0);
                if (reach > room)
                {
                    if (!ring)
                        continue;
                    u0 = double.NaN; // sem espaco para a travessia; fica so a linha de "De a preferencia"
                }
                list.Add(new Approach
                {
                    SNode = sN,
                    Dir = dir,
                    U0 = u0,
                    UStop = uStop,
                    UCut = ring ? (double.IsNaN(u0) ? uStop + 0.5 : u0 + o.TravessiaLargura + 0.5) : uStop + (o.RetencaoLargura / 2.0),
                    Yield = ring
                });
            }
        }

        return list;
    }

    /// <summary>
    /// Separa os trechos livres em continuos (ultimos <paramref name="solidLength"/> metros antes
    /// de cada retencao) e seccionados.
    /// </summary>
    private static (List<(double A, double B)> Solid, List<(double A, double B)> Dashed) Split(
        List<(double A, double B)> free, List<Approach> approaches, double solidLength)
    {
        List<(double A, double B)> solid = new();
        List<(double A, double B)> dashed = new();
        foreach ((double a, double b) in free)
        {
            if (solidLength >= double.MaxValue / 2)
            {
                solid.Add((a, b));
                continue;
            }

            double a2 = a, b2 = b;
            if (solidLength > 0)
            {
                if (approaches.Any(ap => ap.Dir > 0 && !ap.Yield && Math.Abs((ap.SNode + ap.UCut) - a) < 0.01))
                {
                    a2 = Math.Min(b, a + solidLength);
                    solid.Add((a, a2));
                }

                if (approaches.Any(ap => ap.Dir < 0 && !ap.Yield && Math.Abs((ap.SNode - ap.UCut) - b) < 0.01))
                {
                    b2 = Math.Max(a2, b - solidLength);
                    solid.Add((b2, b));
                }
            }

            if (b2 - a2 > 0.1)
                dashed.Add((a2, b2));
        }

        return (solid, dashed);
    }

    private static List<(double A, double B)> Free(double length, List<(double A, double B)> cuts)
    {
        List<(double A, double B)> merged = cuts
            .Select(cut => (Math.Max(0.0, Math.Min(cut.A, cut.B)), Math.Min(length, Math.Max(cut.A, cut.B))))
            .Where(cut => cut.Item2 > cut.Item1)
            .OrderBy(cut => cut.Item1)
            .ToList();

        List<(double A, double B)> free = new();
        double pos = 0.0;
        foreach ((double a, double b) in merged)
        {
            if (a > pos + 0.1)
                free.Add((pos, a));
            pos = Math.Max(pos, b);
        }

        if (length > pos + 0.1)
            free.Add((pos, length));
        return free;
    }

    /// <summary>Contorno fechado de uma marca (desenho vazado).</summary>
    public static List<Seg> Outline(MarkLine mark)
    {
        GPath center = new(mark.Segs);
        GPath left = center.Offset(mark.Width / 2.0);
        GPath right = center.Offset(-mark.Width / 2.0).Reverse();
        if (left.IsEmpty || right.IsEmpty)
            return new List<Seg>();

        List<Seg> ring = new(left.Segs);
        ring.Add(Seg.Line(left.End, right.Start));
        ring.AddRange(right.Segs);
        ring.Add(Seg.Line(right.End, left.Start));
        return ring;
    }

    private static double Clamp(GPath path, double s) => Math.Clamp(s, 0.0, path.Length);

    private static void Add(List<MarkLine> marks, GPath path, double a, double b, double offset, double width, bool yellow, string kind)
    {
        if (b - a < 0.05)
            return;

        GPath piece = path.Sub(a, b);
        if (Math.Abs(offset) > 1e-9)
            piece = piece.Offset(offset);
        if (!piece.IsEmpty)
            marks.Add(new MarkLine(piece.Segs.ToList(), width, yellow, kind));
    }

    private static void Dashes(List<MarkLine> marks, GPath path, double a, double b, double offset,
        double width, double dash, double gap, bool yellow, string kind)
    {
        if (dash <= 0)
            return;

        for (double x = a + (gap / 2.0); x < b; x += dash + gap)
            Add(marks, path, x, Math.Min(b, x + dash), offset, width, yellow, kind);
    }
}
