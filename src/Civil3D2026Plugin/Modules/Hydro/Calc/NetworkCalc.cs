using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Hydro.Calc;

/// <summary>Bacia local ligada a um no.</summary>
public sealed record CatchmentInput(string Id, string NodeId, double AreaHa, double C, double TcMinutes);

/// <summary>No da rede (estrutura). KnownFlow em m3/s entra direto na soma. Inlet: dispositivo de engolimento (boca de lobo).</summary>
public sealed record NodeInput(string Id, double KnownFlow = 0, bool Inlet = false);

/// <summary>Trecho (tubo) de montante para jusante.</summary>
public sealed record PipeInput(string Id, string FromNode, string ToNode, double Diameter, double Slope, double Length, double Manning);

/// <summary>Resultado de um trecho ou de um no (vazao em m3/s).</summary>
public sealed class ReachResult
{
    public string Id { get; init; } = "";
    public double AreaHa { get; set; }
    public double C { get; set; }
    public double TcMinutes { get; set; }
    public double Intensity { get; set; }
    public double Flow { get; set; }
    public double DepthRatio { get; set; }
    public double Velocity { get; set; }
    public double TravelTimeMinutes { get; set; }
    public bool Flows { get; set; } = true;
    public double FullFlow { get; set; }

    /// <summary>No: vazao propria estimada pela capacidade (sem bacia), m3/s; 0 quando veio de bacia.</summary>
    public double EstimatedFlow { get; set; }
}

/// <summary>Resultado da rede: um registro por tubo e por no (a vazao do no e a que sai dele).</summary>
public sealed class NetworkResult
{
    public Dictionary<string, ReachResult> Pipes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ReachResult> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Messages { get; } = new();
}

/// <summary>
/// Metodo racional acumulado de montante para jusante: em cada no soma as areas e C*A das bacias
/// locais e dos trechos que chegam; o Tc do no e o maior entre o Tc local e (Tc de montante +
/// tempo de percurso); o trecho que sai usa Tc >= minimo, a IDF e Manning para lamina e velocidade.
/// Classe pura (sem AutoCAD) para teste.
/// </summary>
public static class NetworkCalc
{
    /// <summary>
    /// Como Run, mas as bocas de lobo (e as pontas de montante) sem bacia nem vazao conhecida recebem
    /// uma vazao propria estimada pela capacidade: comeca na capacidade do tubo de saida (Manning na
    /// lamina maxima dos criterios) e, a cada volta, se algum trecho a jusante passa da capacidade, as
    /// estimativas a montante dele caem na mesma proporcao. Para quando a maior variacao relativa
    /// fica abaixo de tolerance (1%) ou em maxIterations.
    /// </summary>
    public static NetworkResult RunEstimated(
        IReadOnlyList<NodeInput> nodes,
        IReadOnlyList<PipeInput> pipes,
        IReadOnlyList<CatchmentInput> catchments,
        IdfEquation idf,
        double returnPeriodYears,
        Criteria criteria,
        double defaultManning = 0.013,
        double tolerance = 0.01,
        int maxIterations = 50)
    {
        HashSet<string> withCatchment = catchments.Select(c => c.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> hasIncoming = pipes.Select(p => p.ToNode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<PipeInput>> outOf = pipes.GroupBy(p => p.FromNode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        double Cap(PipeInput p) => Manning.Flow(p.Diameter, Math.Max(p.Slope, 1e-5), p.Manning > 0 ? p.Manning : defaultManning, criteria.MaxDepthRatio);

        // Estimativa inicial: capacidade do tubo de saida (o maior, se houver varios).
        Dictionary<string, double> est = new(StringComparer.OrdinalIgnoreCase);
        foreach (NodeInput n in nodes)
        {
            bool candidate = !withCatchment.Contains(n.Id) && n.KnownFlow <= 0 && (n.Inlet || !hasIncoming.Contains(n.Id));
            if (candidate && outOf.TryGetValue(n.Id, out List<PipeInput>? outs) && outs.Count > 0)
                est[n.Id] = outs.Max(Cap);
        }

        if (est.Count == 0)
            return Run(nodes, pipes, catchments, idf, returnPeriodYears, criteria, defaultManning);

        // Trechos a jusante de cada no estimado (caminho pela rede, sem repetir).
        Dictionary<string, List<PipeInput>> downstream = new(StringComparer.OrdinalIgnoreCase);
        foreach (string id in est.Keys)
        {
            List<PipeInput> path = new();
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            Stack<string> stack = new();
            stack.Push(id);
            while (stack.Count > 0)
            {
                string at = stack.Pop();
                if (!seen.Add(at) || !outOf.TryGetValue(at, out List<PipeInput>? outs))
                    continue;
                foreach (PipeInput p in outs)
                {
                    path.Add(p);
                    stack.Push(p.ToNode);
                }
            }

            downstream[id] = path;
        }

        NetworkResult result = Run(With(nodes, est), pipes, catchments, idf, returnPeriodYears, criteria, defaultManning);
        int iterations = 1;
        for (; iterations < maxIterations; iterations++)
        {
            double change = 0;
            Dictionary<string, double> next = new(est, StringComparer.OrdinalIgnoreCase);
            foreach ((string id, double q) in est)
            {
                double factor = 1;
                foreach (PipeInput p in downstream[id])
                {
                    if (result.Pipes.TryGetValue(p.Id, out ReachResult? r) && r.Flow > 1e-12)
                        factor = Math.Min(factor, Cap(p) / r.Flow);
                }

                double v = q * Math.Min(1, factor);
                next[id] = v;
                change = Math.Max(change, Math.Abs(v - q) / Math.Max(q, 1e-9));
            }

            est = next;
            result = Run(With(nodes, est), pipes, catchments, idf, returnPeriodYears, criteria, defaultManning);
            if (change < tolerance)
                break;
        }

        foreach ((string id, double q) in est)
        {
            if (result.Nodes.TryGetValue(id, out ReachResult? r))
                r.EstimatedFlow = q;
        }

        result.Messages.Add($"{est.Count} estrutura(s) sem bacia com vazao estimada pela capacidade ({iterations} iteracao(oes), tolerancia {tolerance:P0}).");
        return result;
    }

    private static List<NodeInput> With(IReadOnlyList<NodeInput> nodes, IReadOnlyDictionary<string, double> est) =>
        nodes.Select(n => est.TryGetValue(n.Id, out double q) ? n with { KnownFlow = n.KnownFlow + q } : n).ToList();

    public static NetworkResult Run(
        IReadOnlyList<NodeInput> nodes,
        IReadOnlyList<PipeInput> pipes,
        IReadOnlyList<CatchmentInput> catchments,
        IdfEquation idf,
        double returnPeriodYears,
        Criteria criteria,
        double defaultManning = 0.013)
    {
        NetworkResult result = new();
        Dictionary<string, NodeInput> nodeById = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<PipeInput>> outOf = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> incoming = nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (PipeInput p in pipes)
        {
            if (!nodeById.ContainsKey(p.FromNode) || !nodeById.ContainsKey(p.ToNode))
            {
                result.Messages.Add($"Trecho {p.Id} ignorado: ponta sem estrutura ou sem sentido.");
                continue;
            }

            if (!outOf.TryGetValue(p.FromNode, out List<PipeInput>? list))
                outOf[p.FromNode] = list = new List<PipeInput>();
            list.Add(p);
            incoming[p.ToNode]++;
        }

        // Acumuladores por no.
        Dictionary<string, (double A, double CA, double Tc, double Q)> acc = nodes.ToDictionary(n => n.Id, _ => (0.0, 0.0, 0.0, 0.0), StringComparer.OrdinalIgnoreCase);
        foreach (CatchmentInput c in catchments)
        {
            if (!acc.ContainsKey(c.NodeId))
            {
                result.Messages.Add($"Bacia {c.Id} ligada a estrutura fora da rede: ignorada.");
                continue;
            }

            (double a, double ca, double tc, double q) = acc[c.NodeId];
            acc[c.NodeId] = (a + c.AreaHa, ca + (c.C * c.AreaHa), Math.Max(tc, c.TcMinutes), q);
        }

        // Ordem topologica (Kahn). O que sobrar (ciclos) entra por ultimo com aviso.
        Queue<string> ready = new(nodes.Where(n => incoming[n.Id] == 0).Select(n => n.Id));
        HashSet<string> done = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = new();
        while (ready.Count > 0 || done.Count < nodes.Count)
        {
            if (ready.Count == 0)
            {
                string? stuck = nodes.Select(n => n.Id).FirstOrDefault(id => !done.Contains(id));
                if (stuck is null)
                    break;
                result.Messages.Add($"Ciclo na rede em {stuck}: resultados a jusante sao aproximados.");
                ready.Enqueue(stuck);
            }

            string id = ready.Dequeue();
            if (!done.Add(id))
                continue;
            order.Add(id);

            (double a, double ca, double tc, double qKnown) = acc[id];
            double nodeC = Rational.WeightedC(ca, a);
            double nodeTc = TimeOfConcentration.WithMinimum(tc, criteria.MinTcMinutes);
            double intensity = idf.Intensity(nodeTc, returnPeriodYears);
            double flow = Rational.Flow(nodeC, intensity, a) + qKnown + nodeById[id].KnownFlow;
            result.Nodes[id] = new ReachResult { Id = id, AreaHa = a, C = nodeC, TcMinutes = nodeTc, Intensity = intensity, Flow = flow };

            if (!outOf.TryGetValue(id, out List<PipeInput>? outs))
                continue;

            // Varios tubos de saida: a vazao vai toda pelo primeiro (o modelo e de rede arborea);
            // os demais recebem a mesma vazao como hipotese conservadora e um aviso.
            if (outs.Count > 1)
                result.Messages.Add($"{id} tem {outs.Count} tubos de saida: a vazao foi repetida em cada um (conferir).");

            foreach (PipeInput p in outs)
            {
                double n = p.Manning > 0 ? p.Manning : defaultManning;
                double slope = Math.Max(p.Slope, 1e-5);
                ManningResult m = Manning.Solve(flow, p.Diameter, slope, n);
                double travel = m.TravelTimeMinutes(p.Length);
                result.Pipes[p.Id] = new ReachResult
                {
                    Id = p.Id, AreaHa = a, C = nodeC, TcMinutes = nodeTc, Intensity = intensity, Flow = flow,
                    DepthRatio = m.DepthRatio, Velocity = m.Velocity, TravelTimeMinutes = travel, Flows = m.Flows, FullFlow = m.FullFlow
                };

                (double a2, double ca2, double tc2, double q2) = acc[p.ToNode];
                acc[p.ToNode] = (a2 + a, ca2 + ca, Math.Max(tc2, nodeTc + travel), q2 + qKnown + nodeById[id].KnownFlow);
                if (--incoming[p.ToNode] == 0)
                    ready.Enqueue(p.ToNode);
            }
        }

        return result;
    }
}
