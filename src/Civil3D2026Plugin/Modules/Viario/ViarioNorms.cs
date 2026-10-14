using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Viario;

internal enum HintLevel
{
    Info,
    Atencao
}

/// <summary>Recomendacao de projeto (estilo "aviso" do TQS): discreta, com a fonte.</summary>
internal sealed class Hint
{
    public HintLevel Level { get; init; }

    /// <summary>Secao, Eixo, Esquina, No, Ilha, Elemento, Rotatoria.</summary>
    public string Topic { get; init; } = "";

    /// <summary>Chave do objeto (id da secao, handle do eixo, chave da esquina...).</summary>
    public string Target { get; init; } = "";

    public string Message { get; init; } = "";
    public string Source { get; init; } = "";
    public V2? Where { get; init; }

    /// <summary>Identidade para comparar antes/depois de um comando.</summary>
    public string Key => $"{Topic}|{Target}|{Message}";

    public string Text => (Level == HintLevel.Atencao ? "! " : "i ") + Message + (Source.Length > 0 ? $" ({Source})" : "");
}

/// <summary>
/// Recomendacoes dos manuais do DNIT: IPR-718 (Projeto de Intersecoes, 2005) e
/// IPR-740 (Projetos Geometricos de Travessias Urbanas, 2010). Valores de
/// referencia para conferencia; nao substituem o projeto.
/// </summary>
internal static class ViarioNorms
{
    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string N(double v, int decimals) => Math.Round(v, decimals).ToString(CultureInfo.InvariantCulture);

    public static List<Hint> Check(IReadOnlyList<AxisInput> axes, ViarioResult result, EngineSettings settings,
        IReadOnlyList<RoadSection> sections)
    {
        List<Hint> hints = new();
        HashSet<string> used = new(axes.Select(a => a.Section.Id));

        foreach (RoadSection s in sections.Where(s => used.Contains(s.Id)))
            CheckSection(s, hints);

        Dictionary<string, AxisInput> byKey = axes.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (AxisInput axis in axes)
            CheckEvents(axis, hints);

        CheckCorners(result, byKey, hints);
        CheckNodes(axes, result, hints);
        CheckIslands(result, hints);
        CheckSlips(result, settings, hints);
        CheckRings(axes, hints);
        return hints;
    }

    // ------------------------------------------------------------------
    // Secao transversal
    // ------------------------------------------------------------------

    private static void CheckSection(RoadSection s, List<Hint> hints)
    {
        void Add(HintLevel level, string message, string source) => hints.Add(new Hint
        {
            Level = level, Topic = "Secao", Target = s.Id, Message = $"{s.Name}: {message}", Source = source
        });

        int n = s.FaixasPorSentido;
        double lane = (s.Pista / 2.0) / n;
        (double min, double desired) = s.Tipo switch
        {
            "Transito rapido" => (3.50, 3.60),
            "Arterial" => s.Velocidade >= 60 ? (3.50, 3.60) : (3.30, 3.50),
            "Coletora" => (3.00, 3.50),
            _ => (3.00, 3.30)
        };

        if (lane < min - 1e-6)
            Add(HintLevel.Atencao, $"faixa de {N(lane)} m abaixo do minimo de {N(min)} m para via {s.Tipo.ToLowerInvariant()}", "IPR-740 Tab.70/77-83");
        else if (lane < desired - 1e-6)
            Add(HintLevel.Info, $"faixa de {N(lane)} m; desejavel {N(desired)} m (3,00 m so em area restrita)", "IPR-740 Tab.70");
        else if (lane > 3.90)
            Add(HintLevel.Info, $"faixa de {N(lane)} m e larga; confira o numero de faixas (larguras acima de 3,60 m induzem velocidade)", "IPR-740 Tab.70");

        if (s.Sarjeta > 1e-3 && s.Sarjeta < 0.30 - 1e-6)
            Add(HintLevel.Info, $"sarjeta de {N(s.Sarjeta)} m; o afastamento da faixa ao meio-fio deve ser >= 0,30 m", "IPR-740 p.306");

        if (s.Calcada > 1e-3)
        {
            if (s.Calcada < 1.20 - 1e-6)
                Add(HintLevel.Atencao, $"calcada de {N(s.Calcada)} m; faixa livre minima de 1,20 m", "IPR-740 p.100");
            else if (s.Tipo == "Arterial" && s.Calcada < 2.40 - 1e-6)
                Add(HintLevel.Info, $"calcada de {N(s.Calcada)} m; em arterial recomenda-se 2,40 m", "IPR-740 p.102");
            else if (s.Calcada < 1.80 - 1e-6)
                Add(HintLevel.Info, $"calcada de {N(s.Calcada)} m; em area residencial recomenda-se 1,80 m", "IPR-740 p.102");
        }

        if (s.Canteiro > 1e-3)
        {
            if (s.Canteiro < 0.60 - 1e-6)
                Add(HintLevel.Atencao, $"canteiro de {N(s.Canteiro)} m abaixo do minimo urbano de 0,60 m", "IPR-718 Tab.64");
            else if (s.Canteiro < 1.20 - 1e-6)
                Add(HintLevel.Info, $"canteiro de {N(s.Canteiro)} m nao serve de refugio de pedestres (min. 1,20 m)", "IPR-718 Tab.64");
            else if (s.Canteiro > 15.0)
                Add(HintLevel.Info, $"canteiro de {N(s.Canteiro)} m: acima de 15 m as colisoes aumentam em intersecoes nao semaforizadas", "IPR-740 p.328");
        }

        (double vmin, double vmax) = s.Tipo switch
        {
            "Local" => (30.0, 40.0),
            "Coletora" => (40.0, 60.0),
            "Arterial" => (50.0, 80.0),
            "Transito rapido" => (60.0, 110.0),
            _ => (0.0, 999.0)
        };
        if (s.Velocidade < vmin - 1e-6 || s.Velocidade > vmax + 1e-6)
            Add(HintLevel.Info, $"velocidade de {N(s.Velocidade)} km/h fora da faixa usual de {N(vmin)} a {N(vmax)} km/h para via {s.Tipo.ToLowerInvariant()}", "IPR-740 Tab.77-83");
    }

    // ------------------------------------------------------------------
    // Elementos (alargamento, gota, conversao, abertura)
    // ------------------------------------------------------------------

    private static void CheckEvents(AxisInput axis, List<Hint> hints)
    {
        RoadSection s = axis.Section;
        foreach (AxisEvent ev in axis.Events)
        {
            V2 at = axis.Path.PointAt(Math.Clamp(ev.S + (ev.Dir * (ev.Gap + (ev.Length / 2.0))), 0.0, axis.Path.Length));
            void Add(HintLevel level, string message, string source) => hints.Add(new Hint
            {
                Level = level, Topic = "Elemento", Target = axis.Key, Where = at, Message = message, Source = source
            });

            double ratio = s.Velocidade <= 50 ? 8.0 : 15.0;
            switch (ev.Kind)
            {
                case AxisEventKind.Alargamento:
                case AxisEventKind.Baia:
                    string what = ev.Kind == AxisEventKind.Baia ? "Conversao no canteiro" : "Alargamento";
                    if (ev.Width < 2.70 - 1e-6)
                        Add(HintLevel.Atencao, $"{what}: faixa de {N(ev.Width)} m (min. 3,00 m; 2,70 m so em baixa velocidade)", "IPR-718 p.276");
                    else if (ev.Width < 3.00 - 1e-6)
                        Add(HintLevel.Info, $"{what}: faixa de {N(ev.Width)} m; minimo usual 3,00 m", "IPR-718 p.276");

                    if (ev.Taper > 1e-3 && ev.Taper < ratio * ev.Width - 1e-6)
                        Add(HintLevel.Info, $"{what}: taper de {N(ev.Taper)} m; razao {N(ratio)}:1 pede {N(ratio * ev.Width)} m (urbano 30-54 m)", "IPR-718 p.277");
                    else if (ev.Taper > 1e-3 && ev.Taper < 30.0 && !ev.Smooth)
                        Add(HintLevel.Info, $"{what}: taper reto curto junto a meio-fio; prefira curva reversa >= 30 m", "IPR-718 p.277");

                    if (ev.Length < 15.0 - 1e-6)
                        Add(HintLevel.Atencao, $"{what}: armazenamento de {N(ev.Length)} m (min. 2 carros, ~15 m)", "IPR-718 Tab.53");

                    if (ev.Kind == AxisEventKind.Baia)
                    {
                        double rest = s.Canteiro - ev.Width;
                        if (s.Canteiro < 3.00 - 1e-6)
                            Add(HintLevel.Atencao, $"Conversao no canteiro de {N(s.Canteiro)} m: giro a esquerda pede canteiro >= 3,00 m (desejavel 4,20-5,40)", "IPR-718 Tab.64");
                        else if (rest < 0.60 - 1e-6)
                            Add(HintLevel.Atencao, $"Conversao: sobra de {N(rest)} m de canteiro; separador minimo 0,60 m (pref. 1,20-1,80)", "IPR-718 p.281");
                        else if (rest < 1.20 - 1e-6)
                            Add(HintLevel.Info, $"Conversao: sobra de {N(rest)} m de canteiro; desejavel >= 1,20 m (nariz so pintado)", "IPR-718 p.276/281");
                    }

                    break;

                case AxisEventKind.Gota:
                    if (ev.Width < 0.50 - 1e-6)
                        Add(HintLevel.Atencao, $"Gota de {N(ev.Width)} m: largura minima 0,50 m (normal >= 1,00 m)", "IPR-718 p.359");
                    else if (ev.Width < 1.00 - 1e-6)
                        Add(HintLevel.Info, $"Gota de {N(ev.Width)} m: largura usual >= 1,00 m", "IPR-718 p.359");
                    if (ev.Width < 1.20 - 1e-6)
                        Add(HintLevel.Info, "Gota estreita para refugio de pedestres (refugio >= 1,20 m, desejavel 2,50 m)", "IPR-718 p.360");
                    break;

                case AxisEventKind.Abertura:
                    if (ev.Length < 12.0 - 1e-6)
                        Add(HintLevel.Atencao, $"Abertura de {N(ev.Length)} m: minimo 12 m", "IPR-718 p.364");
                    else if (ev.Length > 25.0 + 1e-6)
                        Add(HintLevel.Info, $"Abertura de {N(ev.Length)} m: evitar mais de 25 m", "IPR-718 p.365");
                    if (s.Canteiro < 7.0 - 1e-6)
                        Add(HintLevel.Info, $"Retorno em U com canteiro de {N(s.Canteiro)} m: VP pede >= 7 m (tipo C), CO >= 16 m; abaixo disso o giro invade a pista oposta", "IPR-718 Tab.65");
                    break;
            }
        }
    }

    // ------------------------------------------------------------------
    // Esquinas
    // ------------------------------------------------------------------

    /// <summary>Raio minimo de curva simples (VP, CO) pelo angulo de giro - IPR-718 Tab.39.</summary>
    public static (double Vp, double Co) MinCornerRadius(double turnDeg)
    {
        double[] ang = { 30, 45, 60, 75, 90, 105, 120 };
        double[] vp = { 18, 15, 12, 11, 9, 6, 6 };
        double[] co = { 30, 23, 18, 17, 15, 11, 9 };
        double a = Math.Clamp(turnDeg, 30, 120);
        for (int i = 1; i < ang.Length; i++)
        {
            if (a <= ang[i])
            {
                double t = (a - ang[i - 1]) / (ang[i] - ang[i - 1]);
                return (vp[i - 1] + (t * (vp[i] - vp[i - 1])), co[i - 1] + (t * (co[i] - co[i - 1])));
            }
        }

        return (vp[^1], co[^1]);
    }

    private static void CheckCorners(ViarioResult result, Dictionary<string, AxisInput> byKey, List<Hint> hints)
    {
        foreach (CornerInfo c in result.Corners)
        {
            if (c.Arc is null || c.Radius <= 1e-3)
                continue;

            double turn = Math.Abs(c.Arc.Sweep) * 180.0 / Math.PI;
            if (turn < 20)
                continue;

            // Entradas de rotatoria seguem outra logica (deflexao), nao a Tab.39.
            if (!byKey.TryGetValue(c.AxisA, out AxisInput? a) || !byKey.TryGetValue(c.AxisB, out AxisInput? b) ||
                a.Path.Closed || b.Path.Closed)
                continue;

            (double vp, double co) = MinCornerRadius(turn);
            string local = a.Section.Tipo == "Local" && b.Section.Tipo == "Local" ? "Local" : "";
            void Add(HintLevel level, string message, string source) => hints.Add(new Hint
            {
                Level = level, Topic = "Esquina", Target = c.Key, Where = c.Arc.PointAt(0.5), Message = message, Source = source
            });

            if (c.Radius < (0.6 * vp) - 0.1)
                Add(HintLevel.Atencao, $"Esquina R {N(c.Radius)} m (giro {N(turn, 0)} graus): muito abaixo dos {N(vp)} m do carro (VP) em curva simples", "IPR-718 Tab.39");
            else if (c.Radius < vp - 0.5)
                Add(HintLevel.Info, $"Esquina R {N(c.Radius)} m (giro {N(turn, 0)} graus): carro (VP) pede {N(vp)} m em curva simples (ou 3 centros 30-6-30)", "IPR-718 Tab.39");
            else if (c.Radius < co - 0.5 && local != "Local")
                Add(HintLevel.Info, $"Esquina R {N(c.Radius)} m: onibus/caminhao (CO) pede {N(co)} m ou curva de 3 centros", "IPR-718 Tab.39");
            else if (local == "Local" && c.Radius > 15.0)
                Add(HintLevel.Info, $"Esquina R {N(c.Radius)} m em via local: raio grande alonga a travessia de pedestres", "IPR-740 4.4.5");
        }
    }

    // ------------------------------------------------------------------
    // Nos: angulo de cruzamento e espacamento
    // ------------------------------------------------------------------

    private static void CheckNodes(IReadOnlyList<AxisInput> axes, ViarioResult result, List<Hint> hints)
    {
        Dictionary<string, AxisInput> byKey = axes.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (NodeInfo node in result.Nodes)
        {
            List<double> dirs = new();
            foreach (string key in node.AxisKeys)
            {
                if (!byKey.TryGetValue(key, out AxisInput? axis) || axis.Path.Closed)
                    continue;
                (double s, _, double d) = axis.Path.Project(node.Point);
                if (d > 0.5)
                    continue;
                V2 t = axis.Path.TangentAt(s).Unit();
                if (s > 1.0)
                    dirs.Add((-t).Angle);
                if (s < axis.Path.Length - 1.0)
                    dirs.Add(t.Angle);
            }

            if (dirs.Count < 3)
                continue;

            dirs.Sort();
            double minGap = double.MaxValue;
            for (int i = 0; i < dirs.Count; i++)
            {
                double gap = (i + 1 < dirs.Count ? dirs[i + 1] : dirs[0] + (2 * Math.PI)) - dirs[i];
                minGap = Math.Min(minGap, gap);
            }

            double deg = minGap * 180.0 / Math.PI;
            Hint Make(HintLevel level, string message) => new()
            {
                Level = level, Topic = "No", Target = $"{node.Point.X:0.##},{node.Point.Y:0.##}", Where = node.Point,
                Message = message, Source = "IPR-718 8.4.1"
            };

            if (deg < 60.0 - 0.5)
                hints.Add(Make(HintLevel.Atencao, $"Intersecao com angulo de {N(deg)} graus: abaixo de 60 graus convem realinhar"));
            else if (deg < 75.0 - 0.5)
                hints.Add(Make(HintLevel.Info, $"Intersecao com angulo de {N(deg)} graus (ideal 75-90)"));
        }

        // Nos muito proximos no mesmo eixo (intersecoes deslocadas).
        foreach (AxisInput axis in axes.Where(a => !a.Path.Closed))
        {
            List<double> st = result.Nodes
                .Where(nd => nd.AxisKeys.Contains(axis.Key))
                .Select(nd => axis.Path.Project(nd.Point))
                .Where(p => p.Dist < 0.5)
                .Select(p => p.S)
                .OrderBy(x => x)
                .ToList();
            for (int i = 1; i < st.Count; i++)
            {
                double gap = st[i] - st[i - 1];
                if (gap > 1.0 && gap < 50.0)
                {
                    hints.Add(new Hint
                    {
                        Level = HintLevel.Info, Topic = "Eixo", Target = axis.Key,
                        Where = axis.Path.PointAt((st[i] + st[i - 1]) / 2.0),
                        Message = $"Intersecoes a {N(gap)} m no mesmo eixo: deslocadas pedem >= 50 m (100 m com armazenamento)",
                        Source = "IPR-718 p.104"
                    });
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Ilhas
    // ------------------------------------------------------------------

    private static void CheckIslands(ViarioResult result, List<Hint> hints)
    {
        foreach (OutputLine line in result.Lines.Where(l => l.Level == EdgeLevel.Ilha && l.Closed))
        {
            double area = ViarioEngine.RingArea(line.Segs);
            V2 c = new(line.Segs.Average(sg => sg.A.X), line.Segs.Average(sg => sg.A.Y));
            if (area < 5.0)
            {
                hints.Add(new Hint
                {
                    Level = HintLevel.Atencao, Topic = "Ilha", Target = $"{c.X:0.#},{c.Y:0.#}", Where = c,
                    Message = $"Ilha de {N(area)} m2: abaixo de 5 m2 (urbano) prefira so pintura", Source = "IPR-718 p.354/361"
                });
            }
        }
    }

    private static void CheckSlips(ViarioResult result, EngineSettings settings, List<Hint> hints)
    {
        foreach ((string key, SlipLane slip) in settings.SlipLanes)
        {
            CornerInfo? corner = result.Corners.FirstOrDefault(c => c.Key == key);
            V2? where = corner?.Corner;
            double inner = Math.Max(slip.Radius - slip.Width, 1.0);
            double need = SlipWidth(inner);
            void Add(HintLevel level, string message, string source) => hints.Add(new Hint
            {
                Level = level, Topic = "Elemento", Target = key, Where = where, Message = message, Source = source
            });

            if (slip.Width < need - 0.05)
                Add(HintLevel.Atencao, $"Faixa livre de {N(slip.Width)} m com raio interno ~{N(inner)} m: carro (VP) pede {N(need)} m (+0,60 com meio-fio dos dois lados)", "IPR-718 Tab.45");
            if (slip.Setback < 0.60 - 1e-6)
                Add(HintLevel.Info, $"Ilha da faixa livre a {N(slip.Setback)} m dos bordos; afastamento recomendado 0,60-1,00 m", "IPR-718 p.354");
        }
    }

    /// <summary>Largura da pista de conversao, caso I condicao A (VP) - IPR-718 Tab.45.</summary>
    public static double SlipWidth(double innerRadius)
    {
        double[] r = { 15, 25, 30, 50, 75, 100, 150 };
        double[] w = { 5.4, 4.8, 4.5, 4.2, 3.9, 3.9, 3.6 };
        if (innerRadius <= r[0])
            return w[0];
        for (int i = 1; i < r.Length; i++)
        {
            if (innerRadius <= r[i])
                return w[i - 1] + ((innerRadius - r[i - 1]) / (r[i] - r[i - 1]) * (w[i] - w[i - 1]));
        }

        return 3.6;
    }

    private static void CheckRings(IReadOnlyList<AxisInput> axes, List<Hint> hints)
    {
        foreach (AxisInput ring in axes.Where(a => a.Path.Closed))
        {
            Box2 box = ring.Path.Bounds();
            double axisDiameter = ((box.MaxX - box.MinX) + (box.MaxY - box.MinY)) / 2.0;
            double outer = axisDiameter + (2.0 * ring.Section.EdgeOffset(EdgeLevel.FaceMF));
            V2 c = new((box.MinX + box.MaxX) / 2.0, (box.MinY + box.MaxY) / 2.0);
            void Add(HintLevel level, string message) => hints.Add(new Hint
            {
                Level = level, Topic = "Rotatoria", Target = ring.Key, Where = c, Message = message, Source = "IPR-718 p.183-189"
            });

            if (outer < 30.0)
                Add(HintLevel.Info, $"Rotatoria com diametro externo ~{N(outer)} m: onibus e caminhoes pedem 30-40 m (mini-rotatoria: ilha transponivel)");
            else if (outer > 75.0)
                Add(HintLevel.Info, $"Rotatoria com diametro externo ~{N(outer)} m: acima de 75 m a seguranca decresce");
            Add(HintLevel.Info, "Rotatoria: faixas de pedestres a >= 20 m antes da linha de 'De a preferencia'");
        }
    }
}
