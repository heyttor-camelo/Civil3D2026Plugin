using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Hydro.Calc;

/// <summary>Estrutura para a numeracao: id (handle), grupo (familia/tipo), nome atual e posicao.</summary>
public sealed record NumNode(string Id, string Group, string OldName, double X, double Y);

/// <summary>Tubo para a numeracao: pontas pelas estruturas (null = solta) e montante/jusante pelo fluxo.</summary>
public sealed record NumEdge(string Id, string OldName, string? Start, string? End, string? Up, string? Down, double Length);

/// <summary>Regra de um grupo (tipo de estrutura, ou os tubos): numerar ou nao, prefixo.</summary>
public sealed record NumGroupRule(bool Enabled, string Prefix);

/// <summary>Um nome novo calculado.</summary>
public sealed record NumResult(string Id, bool IsPipe, string Group, string OldName, string NewName, int Order, int Component, string? UpName = null, string? DownName = null);

/// <summary>Opcoes da numeracao.</summary>
public sealed class NumOptions
{
    /// <summary>Regras por grupo de estrutura (chave = NumNode.Group).</summary>
    public Dictionary<string, NumGroupRule> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Regra dos tubos.</summary>
    public NumGroupRule Pipes { get; set; } = new(true, "T-");

    /// <summary>Numero inicial de cada prefixo (sem entrada = proximo livre depois dos nomes de fora).</summary>
    public Dictionary<string, int> StartByPrefix { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Digitos minimos do numero (2 = 01, 02...).</summary>
    public int Digits { get; set; } = 2;

    /// <summary>Nomes que ja existem fora do lote (outras pecas, outras redes): o numero pula eles.</summary>
    public HashSet<string> UsedOutside { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ids que ficam como estao (desmarcados na planta).</summary>
    public HashSet<string> Excluded { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Numeracao hidraulica de uma rede: estruturas e tubos de montante para jusante (o ramo mais
/// longo primeiro, como o DRENNUM), com UM contador por prefixo. Tipos diferentes com o mesmo
/// prefixo seguem a mesma sequencia (PV-01 caixa, PV-02 poco, PV-03 caixa...) e nunca colidem;
/// numeros que ja existem fora do lote sao pulados.
/// </summary>
public static class Numbering
{
    public static List<NumResult> Plan(IReadOnlyList<NumNode> nodes, IReadOnlyList<NumEdge> edges, NumOptions o, out List<string> warnings)
    {
        warnings = new List<string>();
        Dictionary<string, NumNode> byId = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        (List<NumNode> orderNodes, List<NumEdge> orderEdges, Dictionary<string, int> component) = Order(nodes, edges);

        Dictionary<string, int> next = new(StringComparer.OrdinalIgnoreCase);
        int Take(string prefix)
        {
            if (!next.TryGetValue(prefix, out int n))
                n = o.StartByPrefix.TryGetValue(prefix, out int s) && s > 0 ? s : SuggestStart(prefix, o.UsedOutside);
            while (IsUsed(prefix, n, o.UsedOutside))
                n++;
            next[prefix] = n + 1;
            return n;
        }

        List<NumResult> result = new();
        int order = 1;
        foreach (NumNode n in orderNodes)
        {
            if (o.Excluded.Contains(n.Id) || !o.Groups.TryGetValue(n.Group, out NumGroupRule? rule) || !rule.Enabled)
                continue;
            string name = Format(rule.Prefix, Take(rule.Prefix), o.Digits);
            result.Add(new NumResult(n.Id, false, n.Group, n.OldName, name, order++, component.GetValueOrDefault(n.Id)));
        }

        Dictionary<string, string> newNames = result.ToDictionary(r => r.Id, r => r.NewName, StringComparer.OrdinalIgnoreCase);
        string NameOf(string? id) => id is null ? "-" : newNames.TryGetValue(id, out string? nn) ? nn : byId.TryGetValue(id, out NumNode? node) ? node.OldName : "-";

        order = 1;
        foreach (NumEdge e in orderEdges)
        {
            if (o.Excluded.Contains(e.Id) || !o.Pipes.Enabled)
                continue;
            string name = Format(o.Pipes.Prefix, Take(o.Pipes.Prefix), o.Digits);
            string? up = e.Up ?? e.Start, down = e.Down ?? e.End;
            result.Add(new NumResult(e.Id, true, "", e.OldName, name, order++, component.GetValueOrDefault(e.Id), NameOf(up), NameOf(down)));
            if (e.Up is null || e.Down is null)
            {
                warnings.Add(e.Start is null || e.End is null
                    ? $"Tubo {e.OldName} ({name}) tem ponta sem estrutura."
                    : $"Tubo {e.OldName} ({name}) sem sentido de fluxo definido: numerado pela ordem de desenho.");
            }
        }

        // Nomes que continuam (pecas fora da numeracao) contam como usados: avisa colisao.
        HashSet<string> kept = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> renamed = result.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (NumNode n in nodes.Where(n => !renamed.Contains(n.Id)))
            kept.Add(n.OldName);
        foreach (NumEdge e in edges.Where(e => !renamed.Contains(e.Id)))
            kept.Add(e.OldName);
        foreach (NumResult r in result.Where(r => kept.Contains(r.NewName)))
            warnings.Add($"{r.NewName} ja e o nome de uma peca que nao sera renumerada: o Civil vai recusar. Inclua essa peca ou mude o prefixo/inicio.");
        foreach (IGrouping<string, NumResult> g in result.GroupBy(r => r.NewName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            warnings.Add($"Nome repetido: {g.Key}.");
        return result;
    }

    /// <summary>Numero formatado com os digitos minimos.</summary>
    public static string Format(string prefix, int number, int digits) =>
        prefix + number.ToString("D" + Math.Clamp(digits, 1, 6), CultureInfo.InvariantCulture);

    /// <summary>Proximo numero depois do maior ja usado fora do lote com esse prefixo (1 se nenhum).</summary>
    public static int SuggestStart(string prefix, IEnumerable<string> used)
    {
        int max = 0;
        foreach (string name in used)
        {
            if (NumberOf(prefix, name) is int n && n > max)
                max = n;
        }

        return max + 1;
    }

    /// <summary>Numero do nome se ele for prefixo + digitos (PV-7 e PV-07 dao 7); senao null.</summary>
    public static int? NumberOf(string prefix, string name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string tail = name[prefix.Length..];
        if (tail.Length == 0 || tail.Length > 9 || !tail.All(char.IsDigit))
            return null;
        return int.Parse(tail, CultureInfo.InvariantCulture);
    }

    /// <summary>Prefixo de um nome (tudo antes dos digitos finais), ou null se nao termina em numero.</summary>
    public static string? PrefixOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        int i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1]))
            i--;
        return i == name.Length || i == 0 ? null : name[..i];
    }

    private static bool IsUsed(string prefix, int n, HashSet<string> used)
    {
        if (used.Count == 0)
            return false;
        foreach (string name in used)
        {
            if (NumberOf(prefix, name) == n)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Ordem hidraulica por componente conectado: cada deságue (mais longo primeiro) visita os
    /// ramos que chegam (o de maior caminho a montante primeiro) antes da propria estrutura; o tubo
    /// entra logo depois da estrutura de montante dele.
    /// </summary>
    public static (List<NumNode> Nodes, List<NumEdge> Edges, Dictionary<string, int> Component) Order(IReadOnlyList<NumNode> nodes, IReadOnlyList<NumEdge> edges)
    {
        Dictionary<string, NumNode> byId = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<NumEdge>> incoming = new(StringComparer.OrdinalIgnoreCase), outgoing = new(StringComparer.OrdinalIgnoreCase), attached = new(StringComparer.OrdinalIgnoreCase);
        foreach (NumNode n in nodes)
        {
            incoming[n.Id] = new();
            outgoing[n.Id] = new();
            attached[n.Id] = new();
        }

        foreach (NumEdge e in edges)
        {
            if (e.Up is not null && e.Down is not null && byId.ContainsKey(e.Up) && byId.ContainsKey(e.Down))
            {
                incoming[e.Down].Add(e);
                outgoing[e.Up].Add(e);
            }

            foreach (string? end in new[] { e.Start, e.End })
            {
                if (end is not null && attached.TryGetValue(end, out List<NumEdge>? list))
                    list.Add(e);
            }
        }

        // Componentes (ligacao por qualquer tubo, sem olhar o sentido).
        Dictionary<string, int> comp = new(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (NumNode seed in nodes.OrderBy(n => n.X).ThenBy(n => n.Y))
        {
            if (comp.ContainsKey(seed.Id))
                continue;
            count++;
            Stack<string> stack = new();
            stack.Push(seed.Id);
            while (stack.Count > 0)
            {
                string id = stack.Pop();
                if (!comp.TryAdd(id, count))
                    continue;
                foreach (NumEdge e in attached[id])
                {
                    comp.TryAdd(e.Id, count);
                    foreach (string? other in new[] { e.Start, e.End })
                    {
                        if (other is not null && byId.ContainsKey(other) && !comp.ContainsKey(other))
                            stack.Push(other);
                    }
                }
            }
        }

        foreach (NumEdge e in edges)
        {
            if (!comp.ContainsKey(e.Id))
                comp[e.Id] = ++count;
        }

        Dictionary<string, double> longest = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visiting = new(StringComparer.OrdinalIgnoreCase);
        double Longest(string id)
        {
            if (longest.TryGetValue(id, out double v))
                return v;
            if (!visiting.Add(id))
                return 0;
            v = 0;
            foreach (NumEdge e in incoming[id])
                v = Math.Max(v, e.Length + Longest(e.Up!));
            visiting.Remove(id);
            longest[id] = v;
            return v;
        }

        List<NumNode> orderNodes = new();
        List<NumEdge> orderEdges = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenEdges = new(StringComparer.OrdinalIgnoreCase);

        void Visit(string id)
        {
            if (!seen.Add(id))
                return;
            List<NumEdge> ins = incoming[id]
                .OrderByDescending(e => e.Length + Longest(e.Up!))
                .ThenBy(e => byId[e.Up!].X).ThenBy(e => byId[e.Up!].Y).ThenBy(e => e.Id, StringComparer.Ordinal)
                .ToList();
            foreach (NumEdge e in ins)
            {
                Visit(e.Up!);
                if (seenEdges.Add(e.Id))
                    orderEdges.Add(e);
            }

            orderNodes.Add(byId[id]);
        }

        foreach (IGrouping<int, NumNode> g in nodes.GroupBy(n => comp[n.Id]).OrderBy(g => g.Key))
        {
            // Saidas: estruturas sem tubo de saida definido; a de maior caminho primeiro.
            foreach (NumNode outlet in g.Where(n => outgoing[n.Id].Count == 0)
                         .OrderByDescending(n => Longest(n.Id)).ThenBy(n => n.X).ThenBy(n => n.Y))
            {
                Visit(outlet.Id);
                foreach (NumEdge e in attached[outlet.Id].Where(e => e.Up == outlet.Id || (e.Up is null && (e.Start is null || e.End is null))))
                {
                    if (seenEdges.Add(e.Id))
                        orderEdges.Add(e);
                }
            }

            foreach (NumNode n in g.OrderBy(n => n.X).ThenBy(n => n.Y))
                Visit(n.Id);

            // Tubos sem sentido ou soltos do componente, na ordem das estruturas.
            foreach (NumNode n in orderNodes.Where(n => comp[n.Id] == g.Key).ToList())
            {
                foreach (NumEdge e in attached[n.Id])
                {
                    if (seenEdges.Add(e.Id))
                        orderEdges.Add(e);
                }
            }
        }

        foreach (NumEdge e in edges)
        {
            if (seenEdges.Add(e.Id))
                orderEdges.Add(e);
        }

        // Tubo depois da sua estrutura de montante: reordena pela posicao dela.
        Dictionary<string, int> pos = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < orderNodes.Count; i++)
            pos[orderNodes[i].Id] = i;
        List<NumEdge> sorted = orderEdges
            .Select((e, i) => (e, i))
            .OrderBy(t => comp[t.e.Id])
            .ThenBy(t => (t.e.Up ?? t.e.Start ?? t.e.End) is string u && pos.TryGetValue(u, out int p) ? p : int.MaxValue)
            .ThenBy(t => t.i)
            .Select(t => t.e).ToList();
        return (orderNodes, sorted, comp);
    }
}
