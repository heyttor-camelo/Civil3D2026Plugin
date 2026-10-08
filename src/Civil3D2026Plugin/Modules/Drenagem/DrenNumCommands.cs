/*
 * DRENNUM - Numeração hidráulica de Pipe Networks para Civil 3D 2026
 * ------------------------------------------------------------------
 * OBJETIVO
 * Renomear estruturas e tubos de redes de drenagem com foco no modo "Família".
 * A rotina trabalha por lotes hidráulicos (mini-redes), permite selecionar uma
 * ou várias mini-redes de uma vez e mantém os próximos números usados durante a
 * sessão para evitar conflitos entre lotes sucessivos.
 *
 * FLUXO
 * 1) O usuário seleciona um ou mais tubos/estruturas como sementes.
 * 2) A DLL expande automaticamente cada semente para a mini-rede conectada.
 * 3) A direção hidráulica é lida de Pipe.FlowDirection.
 * 4) Para cada Part de estrutura utilizada, é solicitado o prefixo.
 * 5) Para os tubos, é solicitado o prefixo.
 * 6) Na janela de preview, o usuário informa o número inicial de cada Part
 *    e dos tubos; a rotina sugere automaticamente o próximo número salvo.
 * 7) A máquina numera preservando a hierarquia hidráulica.
 * 8) A mesma janela mostra diagrama lógico, tabelas e avisos.
 * 9) Após confirmação, os nomes são aplicados em uma transação única.
 *
 * REGRAS IMPORTANTES
 * - Estruturas são ordenadas de montante para jusante.
 * - Tubos também são numerados rigorosamente de montante para jusante.
 * - A identidade hidráulica de um tubo é EstruturaMontante -> EstruturaJusante.
 * - Tubo com uma extremidade sem estrutura NÃO aborta o comando; gera aviso.
 * - FlowDirection indefinido/Bidirectional continua sendo bloqueante, pois a
 *   orientação montante/jusante não pode ser garantida com segurança.
 * - Vários pontos de deságue na mesma PipeNetwork são aceitos.
 * - A seleção pode abranger várias mini-redes; cada componente é expandido.
 * - Prefixos e próximos números ficam guardados na memória da sessão.
 * - Além da memória, os nomes já existentes na(s) rede(s) selecionada(s) são
 *   varridos para evitar reutilização de números existentes.
 *
 * COMANDOS
 * DRENNUM        - seleciona lote(s), pede prefixos, mostra preview e aplica.
 * DRENNUMPREVIEW - igual ao DRENNUM, mas não altera o desenho.
 * DRENNUMHELP    - mostra ajuda resumida na linha de comando.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CivilEntity = Autodesk.Civil.DatabaseServices.Entity;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;

namespace Civil3D2026Plugin.Modules.Drenagem;

public sealed class DrenNumCommands
{
    [CommandMethod(C3DCommands.Drenagem.Num)]
    public void DrenNum() => Execute(previewOnly: false);

    [CommandMethod(C3DCommands.Drenagem.NumPreview)]
    public void DrenNumPreview() => Execute(previewOnly: true);

    [CommandMethod(C3DCommands.Drenagem.NumHelp)]
    public void DrenNumHelp()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        var ed = doc.Editor;
        ed.WriteMessage("\n============================================================");
        ed.WriteMessage($"\n{C3DCommands.Drenagem.Num} v{C3DVersions.DrenNum} - Numeração hidráulica por Família");
        ed.WriteMessage("\n============================================================");
        ed.WriteMessage($"\n{C3DCommands.Drenagem.Num,-15}: seleciona lote(s), mostra preview e aplica.");
        ed.WriteMessage($"\n{C3DCommands.Drenagem.NumPreview,-15}: mostra o preview sem alterar o desenho.");
        ed.WriteMessage($"\n{C3DCommands.Drenagem.NumHelp,-15}: exibe esta ajuda.");
        ed.WriteMessage("\n\nFluxo:");
        ed.WriteMessage("\n  1. Selecione um ou mais tubos/estruturas como sementes.");
        ed.WriteMessage("\n  2. A rotina inclui automaticamente toda mini-rede conectada.");
        ed.WriteMessage("\n  3. Para cada Part de estrutura, informe o prefixo.");
        ed.WriteMessage("\n  4. Para os tubos, informe o prefixo.");
        ed.WriteMessage("\n  5. Na janela, ajuste o número inicial de cada Part e dos tubos.");
        ed.WriteMessage("\n  6. O valor sugerido é o próximo número salvo/seguro da sessão.");
        ed.WriteMessage("\n  7. A numeração é calculada pela hierarquia montante -> jusante.");
        ed.WriteMessage("\n\nExemplo:");
        ed.WriteMessage("\n  Prefixo PV-  => PV-01, PV-02, PV-03...");
        ed.WriteMessage("\n  Prefixo T-   => T-01, T-02, T-03...");
        ed.WriteMessage("\n  Tubo T-03    => PV-02 -> PV-03");
        ed.WriteMessage("\n\nObservações:");
        ed.WriteMessage("\n  - vários deságues/mini-redes na mesma PipeNetwork são aceitos;");
        ed.WriteMessage("\n  - tubo sem estrutura em uma ponta gera apenas aviso;");
        ed.WriteMessage("\n  - FlowDirection Bidirectional/indefinido bloqueia a aplicação;");
        ed.WriteMessage("\n  - a memória sugere automaticamente a continuação da numeração;");
        ed.WriteMessage("\n  - o número inicial pode ser sobrescrito na própria janela de preview.");
        ed.WriteMessage("\n============================================================\n");
    }

    private static void Execute(bool previewOnly)
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        var db = doc.Database;
        var ed = doc.Editor;

        try
        {
            var seedIds = SelectSeeds(ed, db);
            if (seedIds.Count == 0)
                return;

            BatchSelection batch;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                batch = BatchSelection.Build(seedIds, tr);
                batch.ValidateOrThrow();
                tr.Commit();
            }

            ed.WriteMessage($"\n{C3DCommands.Drenagem.Num}: {batch.Components.Count} mini-rede(s) identificada(s), " +
                            $"{batch.Nodes.Count} estrutura(s) e {batch.Edges.Count} tubo(s) incluídos no lote.\n");

            var session = DrenNumSession.Get(db);
            var partPrefixes = AskStructurePrefixes(ed, batch, session);
            if (partPrefixes is null)
                return;

            string? pipePrefix = AskPipePrefix(ed, batch, session);
            if (pipePrefix is null)
                return;

            using var preview = new HydraulicPreviewForm(batch, partPrefixes, pipePrefix, session, previewOnly);
            var dialogResult = AcadApp.ShowModalDialog(preview);
            var plan = preview.Plan;

            if (previewOnly)
            {
                ed.WriteMessage($"\n{C3DCommands.Drenagem.NumPreview}: nenhuma alteração foi feita.\n");
                PrintWarnings(ed, plan.Warnings);
                return;
            }

            if (dialogResult != Forms.DialogResult.OK || !preview.ApplyRequested)
            {
                ed.WriteMessage("\nOperação cancelada.\n");
                return;
            }

            ApplyPlan(db, plan);
            session.CommitCounters(plan.NextNumberByPrefix);

            ed.WriteMessage($"\n{C3DCommands.Drenagem.Num} concluído: {plan.Structures.Count} estrutura(s) e " +
                            $"{plan.Pipes.Count} tubo(s) renomeado(s).\n");
            PrintWarnings(ed, plan.Warnings);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            ed.WriteMessage($"\n[ERRO AutoCAD/Civil 3D] {ex.Message}\n");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n[ERRO] {ex.Message}\n");
        }
    }

    private static List<ObjectId> SelectSeeds(Editor ed, Database db)
    {
        ObjectId[] ids;

        var implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value is not null && implied.Value.Count > 0)
        {
            ids = implied.Value.GetObjectIds();
            ed.SetImpliedSelection(Array.Empty<ObjectId>());
        }
        else
        {
            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelecione um ou mais tubos/estruturas das mini-redes que deseja renumerar: "
            };
            var psr = ed.GetSelection(pso);
            if (psr.Status != PromptStatus.OK || psr.Value is null)
                return new List<ObjectId>();
            ids = psr.Value.GetObjectIds();
        }

        var result = new List<ObjectId>();
        int ignored = 0;

        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        foreach (var id in ids.Distinct())
        {
            var obj = tr.GetObject(id, OpenMode.ForRead, false);
            if (obj is Part part && !part.NetworkId.IsNull)
                result.Add(id);
            else
                ignored++;
        }
        tr.Commit();

        if (ignored > 0)
            ed.WriteMessage($"\nDRENNUM: {ignored} objeto(s) que não pertencem a Pipe Network foram ignorados.\n");

        if (result.Count == 0)
            ed.WriteMessage("\nDRENNUM: selecione pelo menos um tubo ou estrutura de Pipe Network.\n");

        return result;
    }

    private static Dictionary<string, string>? AskStructurePrefixes(Editor ed, BatchSelection batch, DrawingSession session)
    {
        var identities = batch.Nodes
            .GroupBy(n => n.PartIdentity.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First().PartIdentity)
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        ed.WriteMessage("\n--- PREFIXOS DAS ESTRUTURAS ---\n");
        foreach (var identity in identities)
        {
            var nodes = batch.Nodes.Where(n => string.Equals(n.PartIdentity.Key, identity.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            string suggested = session.GetPartPrefix(identity.Key)
                               ?? PrefixHelper.SuggestFromExistingNames(nodes.Select(n => n.OldName))
                               ?? PrefixHelper.MakeSuggestion(identity.DisplayName);

            string? prefix = AskPrefix(ed, $"Prefixo para [{identity.DisplayName}]", suggested);
            if (prefix is null)
                return null;

            result[identity.Key] = prefix;
            session.SetPartPrefix(identity.Key, prefix);
        }

        return result;
    }

    private static string? AskPipePrefix(Editor ed, BatchSelection batch, DrawingSession session)
    {
        ed.WriteMessage("\n--- PREFIXO DOS TUBOS ---\n");
        string suggested = session.PipePrefix ?? "T-";

        string? prefix = AskPrefix(ed, "Prefixo para os tubos", suggested);
        if (prefix is not null)
            session.PipePrefix = prefix;
        return prefix;
    }

    private static string? AskPrefix(Editor ed, string label, string defaultValue)
    {
        var pso = new PromptStringOptions($"\n{label}: ")
        {
            AllowSpaces = true,
            DefaultValue = defaultValue,
            UseDefaultValue = true
        };

        var psr = ed.GetString(pso);
        if (psr.Status == PromptStatus.Cancel)
            return null;

        string value = psr.Status == PromptStatus.None ? defaultValue : psr.StringResult;
        if (string.IsNullOrWhiteSpace(value))
            value = defaultValue;

        return value;
    }

    private static void ApplyPlan(Database db, NumberingPlan plan)
    {
        using var docLock = AcadApp.DocumentManager.MdiActiveDocument.LockDocument();
        using var tr = db.TransactionManager.StartTransaction();

        int tempIndex = 1;
        foreach (var item in plan.AllItems)
        {
            var part = tr.GetObject(item.Id, OpenMode.ForWrite, false) as Part
                ?? throw new InvalidOperationException("Um elemento do lote deixou de existir antes da aplicação.");
            SetPartName(part, $"__DRENNUM_TMP_{Guid.NewGuid():N}_{tempIndex++}");
        }

        foreach (var item in plan.AllItems)
        {
            var part = tr.GetObject(item.Id, OpenMode.ForWrite, false) as Part
                ?? throw new InvalidOperationException("Um elemento do lote deixou de existir antes da aplicação.");
            SetPartName(part, item.NewName);
        }

        tr.Commit();
    }

    private static void SetPartName(Part part, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("Foi gerado um nome vazio.");

        ((CivilEntity)part).Name = newName;
    }

    private static void PrintWarnings(Editor ed, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
            return;

        ed.WriteMessage($"\n[DRENNUM - AVISOS] {warnings.Count} aviso(s):\n");
        foreach (var warning in warnings)
            ed.WriteMessage($"  - {warning}\n");
    }
}

internal sealed class PartIdentity
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string PartSubType { get; init; } = string.Empty;

    public static PartIdentity FromStructure(Structure structure)
    {
        string subtype = structure.PartSubType?.Trim() ?? string.Empty;
        string description = ReflectionHelper.TryGetString(structure, "PartDescription");
        string sizeName = ReflectionHelper.TryGetString(structure, "PartSizeName");
        string familyName = ReflectionHelper.TryGetString(structure, "PartFamilyName");

        // Para o usuário, exibir o nome mais específico da Part/tamanho.
        // A chave interna continua usando subtipo/família/descrição/tamanho para
        // manter contadores independentes entre Parts diferentes.
        string display = FirstNotBlank(sizeName, description, familyName, subtype, "ESTRUTURA");

        string key = string.Join("|", new[] { subtype, familyName, description, sizeName }
            .Select(x => x?.Trim() ?? string.Empty));

        return new PartIdentity
        {
            Key = string.IsNullOrWhiteSpace(key) ? display : key,
            DisplayName = display,
            PartSubType = subtype
        };
    }

    private static string FirstNotBlank(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "ESTRUTURA";
}

internal static class ReflectionHelper
{
    public static string TryGetString(object source, string propertyName)
    {
        try
        {
            PropertyInfo? property = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            object? value = property?.GetValue(source);
            return value?.ToString()?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

internal sealed class StructureNode
{
    public ObjectId Id { get; init; }
    public string OldName { get; init; } = string.Empty;
    public PartIdentity PartIdentity { get; init; } = null!;
    public Autodesk.AutoCAD.Geometry.Point3d Location { get; init; }
    public List<PipeEdge> Incoming { get; } = new();
    public List<PipeEdge> Outgoing { get; } = new();
    public List<PipeEdge> AttachedEdges { get; } = new();
}

internal sealed class PipeEdge
{
    public ObjectId Id { get; init; }
    public string OldName { get; init; } = string.Empty;
    public StructureNode? StartNode { get; init; }
    public StructureNode? EndNode { get; init; }
    public StructureNode? Upstream { get; init; }
    public StructureNode? Downstream { get; init; }
    public double Length { get; init; }
    public bool DirectionKnown { get; init; }
    public string DirectionProblem { get; init; } = string.Empty;

    public bool HasOpenEnd => StartNode is null || EndNode is null;
}

internal sealed class NetworkGraph
{
    public ObjectId NetworkId { get; init; }
    public string NetworkName { get; init; } = string.Empty;
    public Dictionary<ObjectId, StructureNode> Nodes { get; } = new();
    public Dictionary<ObjectId, PipeEdge> Edges { get; } = new();

    public IEnumerable<(ObjectId Id, string Name)> AllPartNames =>
        Nodes.Values.Select(n => (n.Id, n.OldName))
            .Concat(Edges.Values.Select(e => (e.Id, e.OldName)));

    public static NetworkGraph Build(ObjectId networkId, Transaction tr)
    {
        var network = tr.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork
            ?? throw new InvalidOperationException("Não foi possível abrir uma das Pipe Networks selecionadas.");

        var graph = new NetworkGraph
        {
            NetworkId = networkId,
            NetworkName = network.Name
        };

        foreach (ObjectId sid in network.GetStructureIds())
        {
            if (tr.GetObject(sid, OpenMode.ForRead, false) is not Structure structure)
                continue;

            graph.Nodes[sid] = new StructureNode
            {
                Id = sid,
                OldName = structure.Name,
                PartIdentity = PartIdentity.FromStructure(structure),
                Location = structure.Location
            };
        }

        foreach (ObjectId pid in network.GetPipeIds())
        {
            if (tr.GetObject(pid, OpenMode.ForRead, false) is not Pipe pipe)
                continue;

            StructureNode? startNode = null;
            StructureNode? endNode = null;

            if (!pipe.StartStructureId.IsNull)
                graph.Nodes.TryGetValue(pipe.StartStructureId, out startNode);
            if (!pipe.EndStructureId.IsNull)
                graph.Nodes.TryGetValue(pipe.EndStructureId, out endNode);

            StructureNode? upstream = null;
            StructureNode? downstream = null;
            bool directionKnown = true;
            string directionProblem = string.Empty;

            try
            {
                switch (pipe.FlowDirection)
                {
                    case FlowDirectionType.StartToEnd:
                        upstream = startNode;
                        downstream = endNode;
                        break;
                    case FlowDirectionType.EndToStart:
                        upstream = endNode;
                        downstream = startNode;
                        break;
                    default:
                        directionKnown = false;
                        directionProblem = $"Tubo '{pipe.Name}' está com FlowDirection Bidirectional/indefinido.";
                        break;
                }
            }
            catch
            {
                directionKnown = false;
                directionProblem = $"Não foi possível ler o FlowDirection do tubo '{pipe.Name}'.";
            }

            double length;
            try
            {
                length = pipe.Length2DCenterToCenter;
            }
            catch
            {
                try
                {
                    length = pipe.StartPoint.DistanceTo(pipe.EndPoint);
                }
                catch
                {
                    length = 0.0;
                }
            }

            var edge = new PipeEdge
            {
                Id = pid,
                OldName = pipe.Name,
                StartNode = startNode,
                EndNode = endNode,
                Upstream = upstream,
                Downstream = downstream,
                Length = Math.Max(length, 0.0),
                DirectionKnown = directionKnown,
                DirectionProblem = directionProblem
            };

            graph.Edges[pid] = edge;

            if (startNode is not null)
                startNode.AttachedEdges.Add(edge);
            if (endNode is not null && (startNode is null || endNode.Id != startNode.Id))
                endNode.AttachedEdges.Add(edge);

            if (directionKnown)
            {
                if (upstream is not null)
                    upstream.Outgoing.Add(edge);
                if (downstream is not null)
                    downstream.Incoming.Add(edge);
            }
        }

        return graph;
    }

    public List<HydraulicComponent> ComponentsContainingSeeds(IEnumerable<ObjectId> seedIds)
    {
        var result = new List<HydraulicComponent>();
        var alreadyIncluded = new HashSet<ObjectId>();

        foreach (var seed in seedIds)
        {
            if (alreadyIncluded.Contains(seed))
                continue;

            if (!Nodes.ContainsKey(seed) && !Edges.ContainsKey(seed))
                continue;

            var nodeIds = new HashSet<ObjectId>();
            var edgeIds = new HashSet<ObjectId>();
            var queue = new Queue<ObjectId>();
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (nodeIds.Contains(id) || edgeIds.Contains(id))
                    continue;

                if (Nodes.TryGetValue(id, out var node))
                {
                    nodeIds.Add(id);
                    alreadyIncluded.Add(id);
                    foreach (var edge in node.AttachedEdges)
                        queue.Enqueue(edge.Id);
                }
                else if (Edges.TryGetValue(id, out var edge))
                {
                    edgeIds.Add(id);
                    alreadyIncluded.Add(id);
                    if (edge.StartNode is not null)
                        queue.Enqueue(edge.StartNode.Id);
                    if (edge.EndNode is not null)
                        queue.Enqueue(edge.EndNode.Id);
                }
            }

            result.Add(new HydraulicComponent
            {
                Network = this,
                Nodes = nodeIds.Select(id => Nodes[id]).ToList(),
                Edges = edgeIds.Select(id => Edges[id]).ToList()
            });
        }

        return result;
    }
}

internal sealed class HydraulicComponent
{
    public NetworkGraph Network { get; init; } = null!;
    public List<StructureNode> Nodes { get; init; } = new();
    public List<PipeEdge> Edges { get; init; } = new();
    public int ComponentIndex { get; set; }

    public HashSet<ObjectId> NodeIds => Nodes.Select(n => n.Id).ToHashSet();
    public HashSet<ObjectId> EdgeIds => Edges.Select(e => e.Id).ToHashSet();
}

internal sealed class BatchSelection
{
    public List<NetworkGraph> Networks { get; init; } = new();
    public List<HydraulicComponent> Components { get; init; } = new();

    public List<StructureNode> Nodes => Components.SelectMany(c => c.Nodes).GroupBy(n => n.Id).Select(g => g.First()).ToList();
    public List<PipeEdge> Edges => Components.SelectMany(c => c.Edges).GroupBy(e => e.Id).Select(g => g.First()).ToList();

    public static BatchSelection Build(List<ObjectId> seedIds, Transaction tr)
    {
        var seedsByNetwork = new Dictionary<ObjectId, List<ObjectId>>();

        foreach (var seedId in seedIds)
        {
            if (tr.GetObject(seedId, OpenMode.ForRead, false) is not Part part || part.NetworkId.IsNull)
                continue;

            if (!seedsByNetwork.TryGetValue(part.NetworkId, out var list))
            {
                list = new List<ObjectId>();
                seedsByNetwork[part.NetworkId] = list;
            }
            list.Add(seedId);
        }

        if (seedsByNetwork.Count == 0)
            throw new InvalidOperationException("Nenhuma Pipe Network válida foi encontrada na seleção.");

        var batch = new BatchSelection();
        foreach (var pair in seedsByNetwork)
        {
            var graph = NetworkGraph.Build(pair.Key, tr);
            batch.Networks.Add(graph);
            batch.Components.AddRange(graph.ComponentsContainingSeeds(pair.Value));
        }

        var ordered = batch.Components
            .OrderByDescending(ComponentHydraulics.LongestPathLength)
            .ThenBy(c => c.Network.NetworkName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Nodes.Select(n => n.Id.Handle.Value).DefaultIfEmpty(long.MaxValue).Min())
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
            ordered[i].ComponentIndex = i + 1;

        batch.Components.Clear();
        batch.Components.AddRange(ordered);
        return batch;
    }

    public void ValidateOrThrow()
    {
        if (Components.Count == 0)
            throw new InvalidOperationException("Nenhuma mini-rede hidráulica foi encontrada a partir da seleção.");

        var errors = new List<string>();

        foreach (var component in Components)
        {
            var edgeIds = component.EdgeIds;

            foreach (var edge in component.Edges)
            {
                if (!edge.DirectionKnown)
                    errors.Add(edge.DirectionProblem);
            }

            foreach (var node in component.Nodes)
            {
                int outgoing = node.Outgoing.Count(e => edgeIds.Contains(e.Id));
                if (outgoing > 1)
                {
                    errors.Add($"Estrutura '{node.OldName}' possui {outgoing} tubos saindo. " +
                               "Não é possível definir uma hierarquia hidráulica única.");
                }
            }

            DetectCycles(component, errors);
        }

        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join("\n", errors.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    private static void DetectCycles(HydraulicComponent component, List<string> errors)
    {
        var edgeIds = component.EdgeIds;
        var nodeIds = component.NodeIds;
        var state = new Dictionary<ObjectId, int>();

        foreach (var node in component.Nodes)
            Visit(node);

        void Visit(StructureNode node)
        {
            state.TryGetValue(node.Id, out int s);
            if (s == 1)
            {
                errors.Add($"Foi detectado um ciclo hidráulico envolvendo a estrutura '{node.OldName}'.");
                return;
            }
            if (s == 2)
                return;

            state[node.Id] = 1;
            var outgoing = node.Outgoing.FirstOrDefault(e => edgeIds.Contains(e.Id));
            if (outgoing?.Downstream is not null && nodeIds.Contains(outgoing.Downstream.Id))
                Visit(outgoing.Downstream);
            state[node.Id] = 2;
        }
    }

    public IEnumerable<(ObjectId Id, string Name)> AllKnownPartNames =>
        Networks.SelectMany(n => n.AllPartNames);
}

internal static class ComponentHydraulics
{
    public static double LongestPathLength(HydraulicComponent component)
    {
        var edgeIds = component.EdgeIds;
        var cache = new Dictionary<ObjectId, double>();

        double Longest(StructureNode node)
        {
            if (cache.TryGetValue(node.Id, out double cached))
                return cached;

            double value = 0.0;
            foreach (var edge in node.Incoming.Where(e => edgeIds.Contains(e.Id)))
            {
                double candidate = edge.Length + (edge.Upstream is null ? 0.0 : Longest(edge.Upstream));
                if (candidate > value)
                    value = candidate;
            }
            cache[node.Id] = value;
            return value;
        }

        double max = component.Nodes.Count == 0 ? 0.0 : component.Nodes.Max(Longest);
        foreach (var edge in component.Edges.Where(e => e.Downstream is null))
        {
            double candidate = edge.Length + (edge.Upstream is null ? 0.0 : Longest(edge.Upstream));
            if (candidate > max)
                max = candidate;
        }
        return max;
    }

    public static HydraulicOrder BuildOrder(HydraulicComponent component)
    {
        var nodeIds = component.NodeIds;
        var edgeIds = component.EdgeIds;
        var nodeOrder = new List<StructureNode>();
        var pipeOrder = new List<PipeEdge>();
        var seenNodes = new HashSet<ObjectId>();
        var seenEdges = new HashSet<ObjectId>();
        var distanceCache = new Dictionary<ObjectId, double>();

        double LongestUpstream(StructureNode node)
        {
            if (distanceCache.TryGetValue(node.Id, out double cached))
                return cached;

            double value = 0.0;
            foreach (var edge in node.Incoming.Where(e => edgeIds.Contains(e.Id)))
            {
                double candidate = edge.Length + (edge.Upstream is null ? 0.0 : LongestUpstream(edge.Upstream));
                if (candidate > value)
                    value = candidate;
            }
            distanceCache[node.Id] = value;
            return value;
        }

        IEnumerable<PipeEdge> IncomingOrdered(StructureNode node) =>
            node.Incoming
                .Where(e => edgeIds.Contains(e.Id))
                .OrderByDescending(e => e.Length + (e.Upstream is null ? 0.0 : LongestUpstream(e.Upstream)))
                .ThenBy(e => e.Upstream?.Location.X ?? double.MinValue)
                .ThenBy(e => e.Upstream?.Location.Y ?? double.MinValue)
                .ThenBy(e => e.Id.Handle.Value);

        void VisitNode(StructureNode node)
        {
            if (seenNodes.Contains(node.Id))
                return;

            foreach (var incoming in IncomingOrdered(node))
            {
                if (incoming.Upstream is not null && nodeIds.Contains(incoming.Upstream.Id))
                    VisitNode(incoming.Upstream);

                if (seenEdges.Add(incoming.Id))
                    pipeOrder.Add(incoming);
            }

            if (seenNodes.Add(node.Id))
                nodeOrder.Add(node);
        }

        // Saída aberta: a estrutura a montante é visitada primeiro e o tubo de descarga por último.
        foreach (var openOutlet in component.Edges
                     .Where(e => e.Downstream is null && e.Upstream is not null)
                     .OrderByDescending(e => e.Length + LongestUpstream(e.Upstream!))
                     .ThenBy(e => e.Id.Handle.Value))
        {
            VisitNode(openOutlet.Upstream!);
            if (seenEdges.Add(openOutlet.Id))
                pipeOrder.Add(openOutlet);
        }

        // Descarga em estrutura: estrutura sem tubo de saída dentro do componente.
        foreach (var outlet in component.Nodes
                     .Where(n => !n.Outgoing.Any(e => edgeIds.Contains(e.Id)))
                     .OrderByDescending(LongestUpstream)
                     .ThenBy(n => n.Location.X)
                     .ThenBy(n => n.Location.Y)
                     .ThenBy(n => n.Id.Handle.Value))
        {
            VisitNode(outlet);
        }

        // Tubos totalmente sem estrutura ainda recebem número, mas ficam isolados no preview.
        foreach (var edge in component.Edges.Where(e => e.StartNode is null && e.EndNode is null).OrderBy(e => e.Id.Handle.Value))
        {
            if (seenEdges.Add(edge.Id))
                pipeOrder.Add(edge);
        }

        // Segurança para qualquer elemento restante.
        foreach (var node in component.Nodes.OrderBy(n => n.Id.Handle.Value))
            VisitNode(node);
        foreach (var edge in component.Edges.OrderBy(e => e.Id.Handle.Value))
        {
            if (seenEdges.Add(edge.Id))
                pipeOrder.Add(edge);
        }

        return new HydraulicOrder(nodeOrder, pipeOrder);
    }
}

internal sealed record HydraulicOrder(List<StructureNode> Structures, List<PipeEdge> Pipes);

internal static class DrenNumSession
{
    private static ConditionalWeakTable<Database, DrawingSession> _sessions = new();

    public static DrawingSession Get(Database db) => _sessions.GetValue(db, _ => new DrawingSession());

}

internal sealed class DrawingSession
{
    private readonly Dictionary<string, string> _partPrefixByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _nextNumberByPrefix = new(StringComparer.OrdinalIgnoreCase);

    public string? PipePrefix { get; set; }

    public string? GetPartPrefix(string key) =>
        _partPrefixByKey.TryGetValue(key, out var value) ? value : null;

    public void SetPartPrefix(string key, string prefix) =>
        _partPrefixByKey[key] = prefix;

    public int GetSessionNext(string prefix) =>
        _nextNumberByPrefix.TryGetValue(prefix, out int value) ? value : 1;

    public void CommitCounters(IReadOnlyDictionary<string, int> nextByPrefix)
    {
        // A última aplicação passa a ser a nova referência da sessão.
        // Isso permite ao usuário reduzir/reiniciar a sequência simplesmente
        // informando outro número inicial na janela, sem comando RESET.
        foreach (var pair in nextByPrefix)
            _nextNumberByPrefix[pair.Key] = Math.Max(1, pair.Value);
    }
}

internal static class PrefixHelper
{
    public static string? SuggestFromExistingNames(IEnumerable<string> names)
    {
        var prefixes = names
            .Select(GetTrailingNumberPrefix)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Length)
            .Select(g => g.Key)
            .ToList();

        return prefixes.FirstOrDefault();
    }

    public static string MakeSuggestion(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return "EST-";

        string normalized = RemoveDiacritics(source).ToUpperInvariant();
        var words = normalized
            .Split(new[] { ' ', '-', '_', '/', '\\', '.', '(', ')', '[', ']', '|', ':' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not "DE" and not "DA" and not "DO" and not "DAS" and not "DOS" and not "E")
            .Where(w => w.Any(char.IsLetterOrDigit))
            .ToList();

        string code;
        if (words.Count >= 2)
        {
            code = string.Concat(words.Take(4).Select(w => w.First(char.IsLetterOrDigit)));
        }
        else
        {
            string only = new string((words.FirstOrDefault() ?? "EST").Where(char.IsLetterOrDigit).ToArray());
            code = only.Length <= 4 ? only : only[..2];
        }

        if (string.IsNullOrWhiteSpace(code))
            code = "EST";
        return code + "-";
    }

    public static int FindMaxUsedNumber(string prefix, IEnumerable<string> names)
    {
        int max = 0;
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string suffix = name[prefix.Length..];
            if (suffix.Length == 0 || suffix.Any(c => !char.IsDigit(c)))
                continue;

            if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > max)
                max = value;
        }
        return max;
    }

    private static string? GetTrailingNumberPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        int i = value.Length - 1;
        while (i >= 0 && char.IsDigit(value[i]))
            i--;

        if (i == value.Length - 1)
            return null;

        string prefix = value[..(i + 1)];
        return string.IsNullOrWhiteSpace(prefix) ? null : prefix;
    }

    private static string RemoveDiacritics(string text)
    {
        string normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

internal abstract class RenameItem
{
    public ObjectId Id { get; init; }
    public string OldName { get; init; } = string.Empty;
    public string NewName { get; init; } = string.Empty;
    public int HydraulicOrder { get; init; }
    public int ComponentIndex { get; init; }
}

internal sealed class StructureRenameItem : RenameItem
{
    public string PartKey { get; init; } = string.Empty;
    public string PartLabel { get; init; } = string.Empty;
}

internal sealed class PipeRenameItem : RenameItem
{
    public ObjectId UpstreamStructureId { get; init; } = ObjectId.Null;
    public ObjectId DownstreamStructureId { get; init; } = ObjectId.Null;
    public string UpstreamName { get; set; } = "SEM ESTRUTURA";
    public string DownstreamName { get; set; } = "SEM ESTRUTURA";
    public bool HasOpenEnd { get; init; }
}

internal sealed class NumberingPlan
{
    public List<StructureRenameItem> Structures { get; init; } = new();
    public List<PipeRenameItem> Pipes { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public List<PlanComponent> Components { get; init; } = new();
    public Dictionary<string, int> NextNumberByPrefix { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<RenameItem> AllItems => Structures.Cast<RenameItem>().Concat(Pipes);
}

internal sealed class PlanComponent
{
    public int Index { get; init; }
    public string NetworkName { get; init; } = string.Empty;
    public List<ObjectId> StructureIds { get; init; } = new();
    public List<ObjectId> PipeIds { get; init; } = new();
}

internal sealed class NumberingStartDefaults
{
    public Dictionary<string, int> StructureStartByPartKey { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int PipeStart { get; set; } = 1;
}

internal static class NumberingEngine
{
    public static NumberingStartDefaults GetSuggestedStarts(
        BatchSelection batch,
        IReadOnlyDictionary<string, string> partPrefixes,
        string pipePrefix,
        DrawingSession session)
    {
        var result = new NumberingStartDefaults();
        var allKnownNames = batch.AllKnownPartNames.Select(x => x.Name).ToList();

        // Base de cada prefixo: maior entre memória da sessão e nomes já existentes
        // nas Pipe Networks envolvidas. A memória guarda o PRÓXIMO número disponível.
        var allPrefixes = partPrefixes.Values
            .Concat(new[] { pipePrefix })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nextByPrefix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in allPrefixes)
        {
            int observedNext = PrefixHelper.FindMaxUsedNumber(prefix, allKnownNames) + 1;
            int sessionNext = session.GetSessionNext(prefix);
            nextByPrefix[prefix] = Math.Max(1, Math.Max(observedNext, sessionNext));
        }

        // Um campo para cada Part. Caso duas Parts usem o mesmo prefixo, as sugestões
        // são encadeadas para não nascerem sobrepostas.
        var groups = batch.Nodes
            .GroupBy(n => n.PartIdentity.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Key = g.Key,
                Identity = g.First().PartIdentity,
                Count = g.Count()
            })
            .OrderBy(x => x.Identity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            if (!partPrefixes.TryGetValue(group.Key, out var prefix))
                continue;

            int start = nextByPrefix[prefix];
            result.StructureStartByPartKey[group.Key] = start;
            nextByPrefix[prefix] = start + group.Count;
        }

        result.PipeStart = nextByPrefix.TryGetValue(pipePrefix, out int pipeStart)
            ? pipeStart
            : 1;

        return result;
    }

    public static NumberingPlan CreateFamilyPlan(
        BatchSelection batch,
        IReadOnlyDictionary<string, string> partPrefixes,
        string pipePrefix,
        IReadOnlyDictionary<string, int> structureStartByPartKey,
        int pipeStart)
    {
        if (pipeStart < 1)
            throw new InvalidOperationException("O número inicial dos tubos deve ser maior ou igual a 1.");

        var plan = new NumberingPlan();
        var selectedIds = batch.Nodes.Select(n => n.Id).Concat(batch.Edges.Select(e => e.Id)).ToHashSet();
        var untouchedKnownNames = batch.AllKnownPartNames
            .Where(x => !selectedIds.Contains(x.Id))
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var currentByPartKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in partPrefixes)
        {
            if (!structureStartByPartKey.TryGetValue(pair.Key, out int start) || start < 1)
                throw new InvalidOperationException($"Número inicial inválido para a Part '{pair.Key}'.");
            currentByPartKey[pair.Key] = start;
        }

        int currentPipeNumber = pipeStart;
        int globalStructureOrder = 1;
        int globalPipeOrder = 1;

        foreach (var component in batch.Components)
        {
            var order = ComponentHydraulics.BuildOrder(component);

            plan.Components.Add(new PlanComponent
            {
                Index = component.ComponentIndex,
                NetworkName = component.Network.NetworkName,
                StructureIds = component.Nodes.Select(n => n.Id).ToList(),
                PipeIds = component.Edges.Select(e => e.Id).ToList()
            });

            foreach (var node in order.Structures)
            {
                if (!partPrefixes.TryGetValue(node.PartIdentity.Key, out string? prefix))
                    throw new InvalidOperationException($"Prefixo não encontrado para a Part '{node.PartIdentity.DisplayName}'.");

                int number = currentByPartKey[node.PartIdentity.Key];
                currentByPartKey[node.PartIdentity.Key] = number + 1;
                string newName = prefix + number.ToString("D2", CultureInfo.InvariantCulture);

                plan.Structures.Add(new StructureRenameItem
                {
                    Id = node.Id,
                    OldName = node.OldName,
                    NewName = newName,
                    HydraulicOrder = globalStructureOrder++,
                    ComponentIndex = component.ComponentIndex,
                    PartKey = node.PartIdentity.Key,
                    PartLabel = node.PartIdentity.DisplayName
                });
            }

            foreach (var edge in order.Pipes)
            {
                int number = currentPipeNumber++;
                string newName = pipePrefix + number.ToString("D2", CultureInfo.InvariantCulture);

                plan.Pipes.Add(new PipeRenameItem
                {
                    Id = edge.Id,
                    OldName = edge.OldName,
                    NewName = newName,
                    HydraulicOrder = globalPipeOrder++,
                    ComponentIndex = component.ComponentIndex,
                    UpstreamStructureId = edge.Upstream?.Id ?? ObjectId.Null,
                    DownstreamStructureId = edge.Downstream?.Id ?? ObjectId.Null,
                    HasOpenEnd = edge.HasOpenEnd
                });

                if (edge.HasOpenEnd)
                {
                    string missingSide;
                    if (edge.Upstream is null && edge.Downstream is null)
                        missingSide = "nas duas extremidades";
                    else if (edge.Upstream is null)
                        missingSide = "na extremidade de montante";
                    else
                        missingSide = "na extremidade de jusante";

                    plan.Warnings.Add($"Tubo '{edge.OldName}' ({newName}) está sem estrutura {missingSide}.");
                }
            }
        }

        var structureNameById = plan.Structures.ToDictionary(x => x.Id, x => x.NewName);
        foreach (var pipe in plan.Pipes)
        {
            if (!pipe.UpstreamStructureId.IsNull && structureNameById.TryGetValue(pipe.UpstreamStructureId, out string? up))
                pipe.UpstreamName = up;
            if (!pipe.DownstreamStructureId.IsNull && structureNameById.TryGetValue(pipe.DownstreamStructureId, out string? down))
                pipe.DownstreamName = down;
        }

        // Guarda na sessão o PRÓXIMO número após o lote aplicado. Quando duas Parts
        // compartilham um prefixo, prevalece o maior próximo número.
        foreach (var pair in partPrefixes)
        {
            string prefix = pair.Value;
            int next = currentByPartKey[pair.Key];
            if (!plan.NextNumberByPrefix.TryGetValue(prefix, out int existing) || next > existing)
                plan.NextNumberByPrefix[prefix] = next;
        }
        if (!plan.NextNumberByPrefix.TryGetValue(pipePrefix, out int existingPipe) || currentPipeNumber > existingPipe)
            plan.NextNumberByPrefix[pipePrefix] = currentPipeNumber;

        EnsureGeneratedNamesAreUnique(plan);

        foreach (var item in plan.AllItems)
        {
            if (untouchedKnownNames.Contains(item.NewName))
                throw new InvalidOperationException($"O nome '{item.NewName}' já existe fora do lote selecionado. " +
                                                    "Escolha outro prefixo ou outro número inicial.");
        }

        return plan;
    }

    private static void EnsureGeneratedNamesAreUnique(NumberingPlan plan)
    {
        var duplicate = plan.AllItems
            .GroupBy(i => i.NewName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
            throw new InvalidOperationException($"A numeração gerou nome duplicado: '{duplicate.Key}'.");
    }
}

internal sealed class HydraulicPreviewForm : Forms.Form
{
    private readonly BatchSelection _batch;
    private readonly IReadOnlyDictionary<string, string> _partPrefixes;
    private readonly string _pipePrefix;
    private readonly bool _previewOnly;
    private readonly Dictionary<string, Forms.NumericUpDown> _structureStartControls = new(StringComparer.OrdinalIgnoreCase);

    private readonly Forms.Label _header;
    private readonly Forms.Label _statusLabel;
    private readonly Forms.Panel _contentHost;
    private readonly Forms.NumericUpDown _pipeStartControl;
    private readonly Forms.Button _refreshButton;
    private readonly Forms.Button _closeButton;
    private readonly Forms.Button? _applyButton;

    public bool ApplyRequested { get; private set; }
    public NumberingPlan Plan { get; private set; }

    public HydraulicPreviewForm(
        BatchSelection batch,
        IReadOnlyDictionary<string, string> partPrefixes,
        string pipePrefix,
        DrawingSession session,
        bool previewOnly)
    {
        _batch = batch;
        _partPrefixes = partPrefixes;
        _pipePrefix = pipePrefix;
        _previewOnly = previewOnly;

        var defaults = NumberingEngine.GetSuggestedStarts(batch, partPrefixes, pipePrefix, session);
        Plan = NumberingEngine.CreateFamilyPlan(
            batch,
            partPrefixes,
            pipePrefix,
            defaults.StructureStartByPartKey,
            defaults.PipeStart);

        Text = previewOnly ? "DRENNUM - Preview hidráulico" : "DRENNUM - Confirmar numeração hidráulica";
        StartPosition = Forms.FormStartPosition.CenterScreen;
        Width = 1280;
        Height = 830;
        MinimumSize = new Drawing.Size(980, 650);
        AutoScaleMode = Forms.AutoScaleMode.Dpi;
        KeyPreview = true;

        var root = new Forms.TableLayoutPanel
        {
            Dock = Forms.DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = new Forms.Padding(0),
            Padding = new Forms.Padding(0)
        };
        root.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100f));
        root.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 58f));
        root.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 116f));
        root.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 24f));
        root.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100f));
        root.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 56f));
        Controls.Add(root);

        _header = new Forms.Label
        {
            Dock = Forms.DockStyle.Fill,
            Padding = new Forms.Padding(12, 10, 12, 6),
            Font = new Drawing.Font(this.Font.FontFamily, 10.5f, Drawing.FontStyle.Bold)
        };
        root.Controls.Add(_header, 0, 0);

        var numberingPanel = new Forms.FlowLayoutPanel
        {
            Dock = Forms.DockStyle.Fill,
            Padding = new Forms.Padding(10, 6, 10, 4),
            AutoScroll = true,
            WrapContents = true,
            FlowDirection = Forms.FlowDirection.LeftToRight,
            BackColor = Drawing.SystemColors.ControlLight
        };
        root.Controls.Add(numberingPanel, 0, 1);

        var title = new Forms.Label
        {
            AutoSize = false,
            Width = 205,
            Height = 70,
            Text = "NÚMERO INICIAL\r\nAjuste e clique em Atualizar preview.",
            Font = new Drawing.Font(this.Font.FontFamily, 9f, Drawing.FontStyle.Bold),
            Padding = new Forms.Padding(4, 8, 4, 4)
        };
        numberingPanel.Controls.Add(title);

        var partGroups = batch.Nodes
            .GroupBy(n => n.PartIdentity.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First().PartIdentity)
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var identity in partGroups)
        {
            if (!partPrefixes.TryGetValue(identity.Key, out string? prefix))
                continue;

            int suggested = defaults.StructureStartByPartKey.TryGetValue(identity.Key, out int start) ? start : 1;
            var card = CreateNumberCard(identity.DisplayName, prefix, suggested, out var control);
            _structureStartControls[identity.Key] = control;
            numberingPanel.Controls.Add(card);
        }

        var pipeCard = CreateNumberCard("Tubos", pipePrefix, defaults.PipeStart, out var pipeStartControl);
        _pipeStartControl = pipeStartControl;
        numberingPanel.Controls.Add(pipeCard);

        _refreshButton = new Forms.Button
        {
            Text = "Atualizar preview",
            Width = 132,
            Height = 34,
            Margin = new Forms.Padding(8, 24, 8, 4)
        };
        _refreshButton.Click += (_, _) => TryRefreshPlan(showErrorDialog: true);
        numberingPanel.Controls.Add(_refreshButton);

        _statusLabel = new Forms.Label
        {
            Dock = Forms.DockStyle.Fill,
            Padding = new Forms.Padding(12, 3, 12, 2),
            ForeColor = Drawing.Color.DarkRed,
            Text = string.Empty
        };
        root.Controls.Add(_statusLabel, 0, 2);

        var footer = new Forms.Panel
        {
            Dock = Forms.DockStyle.Fill,
            Padding = new Forms.Padding(10)
        };
        root.Controls.Add(footer, 0, 4);

        _closeButton = new Forms.Button
        {
            Text = previewOnly ? "Fechar" : "Cancelar",
            Width = 105,
            Height = 32,
            Anchor = Forms.AnchorStyles.Right | Forms.AnchorStyles.Top,
            Top = 10
        };
        _closeButton.Click += (_, _) =>
        {
            ApplyRequested = false;
            DialogResult = previewOnly ? Forms.DialogResult.OK : Forms.DialogResult.Cancel;
            Close();
        };
        footer.Controls.Add(_closeButton);

        if (!previewOnly)
        {
            _applyButton = new Forms.Button
            {
                Text = "Aplicar",
                Width = 105,
                Height = 32,
                Anchor = Forms.AnchorStyles.Right | Forms.AnchorStyles.Top,
                Top = 10
            };
            _applyButton.Click += (_, _) =>
            {
                // Recalcula sempre antes de aplicar, mesmo que o usuário tenha alterado
                // um número e não tenha clicado em "Atualizar preview".
                if (!TryRefreshPlan(showErrorDialog: true))
                    return;

                ApplyRequested = true;
                DialogResult = Forms.DialogResult.OK;
                Close();
            };
            footer.Controls.Add(_applyButton);
            AcceptButton = _applyButton;
        }

        CancelButton = _closeButton;

        _contentHost = new Forms.Panel { Dock = Forms.DockStyle.Fill };
        root.Controls.Add(_contentHost, 0, 3);

        footer.Resize += (_, _) => PositionFooterButtons(footer);
        Shown += (_, _) =>
        {
            PositionFooterButtons(footer);
            RebuildPreviewArea();
        };

        UpdateHeader();
    }

    private static Forms.Panel CreateNumberCard(
        string label,
        string prefix,
        int suggested,
        out Forms.NumericUpDown numberControl)
    {
        var panel = new Forms.Panel
        {
            Width = 235,
            Height = 76,
            Margin = new Forms.Padding(5, 2, 5, 2),
            BorderStyle = Forms.BorderStyle.FixedSingle
        };

        string shortLabel = label.Length > 29 ? label[..29] + "…" : label;
        var text = new Forms.Label
        {
            Left = 8,
            Top = 6,
            Width = 218,
            Height = 32,
            Text = $"{shortLabel}\r\nPrefixo: {prefix}",
            AutoEllipsis = true
        };
        panel.Controls.Add(text);

        var numberLabel = new Forms.Label
        {
            Left = 8,
            Top = 48,
            Width = 88,
            Height = 20,
            Text = "Número inicial:"
        };
        panel.Controls.Add(numberLabel);

        numberControl = new Forms.NumericUpDown
        {
            Left = 102,
            Top = 44,
            Width = 105,
            Minimum = 1,
            Maximum = 999999,
            Value = Math.Clamp(suggested, 1, 999999),
            ThousandsSeparator = false
        };
        panel.Controls.Add(numberControl);

        return panel;
    }

    private Dictionary<string, int> ReadStructureStarts()
    {
        return _structureStartControls.ToDictionary(
            x => x.Key,
            x => Decimal.ToInt32(x.Value.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private bool TryRefreshPlan(bool showErrorDialog)
    {
        try
        {
            var newPlan = NumberingEngine.CreateFamilyPlan(
                _batch,
                _partPrefixes,
                _pipePrefix,
                ReadStructureStarts(),
                Decimal.ToInt32(_pipeStartControl.Value));

            Plan = newPlan;
            _statusLabel.Text = "Preview atualizado. Os números informados serão usados como início da sequência.";
            _statusLabel.ForeColor = Drawing.Color.DarkGreen;
            UpdateHeader();
            RebuildPreviewArea();
            if (_applyButton is not null)
                _applyButton.Enabled = true;
            return true;
        }
        catch (System.Exception ex)
        {
            _statusLabel.Text = ex.Message;
            _statusLabel.ForeColor = Drawing.Color.DarkRed;
            if (_applyButton is not null)
                _applyButton.Enabled = false;

            if (showErrorDialog)
                Forms.MessageBox.Show(this, ex.Message, "DRENNUM - Numeração inválida",
                    Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            return false;
        }
    }

    private void UpdateHeader()
    {
        _header.Text = $"{Plan.Components.Count} mini-rede(s) | {Plan.Structures.Count} estruturas | {Plan.Pipes.Count} tubos\r\n" +
                       "Diagrama lógico: montante à esquerda, jusante à direita. As setas indicam o sentido do fluxo.";
    }

    private void RebuildPreviewArea()
    {
        _contentHost.SuspendLayout();
        try
        {
            _contentHost.Controls.Clear();

            var split = new Forms.SplitContainer
            {
                Dock = Forms.DockStyle.Fill,
                Orientation = Forms.Orientation.Vertical
            };
            _contentHost.Controls.Add(split);

            var diagram = new HydraulicDiagramPanel(Plan)
            {
                Dock = Forms.DockStyle.Fill
            };
            split.Panel1.Controls.Add(diagram);

            var tabs = new Forms.TabControl { Dock = Forms.DockStyle.Fill };
            split.Panel2.Controls.Add(tabs);
            tabs.TabPages.Add(BuildStructureTab(Plan));
            tabs.TabPages.Add(BuildPipeTab(Plan));
            tabs.TabPages.Add(BuildWarningsTab(Plan));

            // Somente depois do layout real do controle é seguro ajustar o splitter.
            BeginInvoke(new Action(() =>
            {
                if (split.IsDisposed)
                    return;

                int width = split.ClientSize.Width;
                int min = split.Panel1MinSize;
                int max = width - split.Panel2MinSize - split.SplitterWidth;
                if (width <= 0 || max < min)
                    return;

                split.SplitterDistance = Math.Clamp((int)Math.Round(width * 0.64), min, max);
            }));
        }
        finally
        {
            _contentHost.ResumeLayout(true);
        }
    }

    private void PositionFooterButtons(Forms.Panel footer)
    {
        _closeButton.Left = footer.ClientSize.Width - _closeButton.Width - 10;
        if (_applyButton is not null)
            _applyButton.Left = _closeButton.Left - _applyButton.Width - 10;
    }

    private static Forms.TabPage BuildStructureTab(NumberingPlan plan)
    {
        var page = new Forms.TabPage("Estruturas");
        var grid = CreateGrid();
        grid.Columns.Add("ordem", "Ordem");
        grid.Columns.Add("lote", "Lote");
        grid.Columns.Add("part", "Part");
        grid.Columns.Add("atual", "Nome atual");
        grid.Columns.Add("novo", "Novo nome");

        foreach (var item in plan.Structures.OrderBy(x => x.HydraulicOrder))
            grid.Rows.Add(item.HydraulicOrder, item.ComponentIndex, item.PartLabel, item.OldName, item.NewName);

        page.Controls.Add(grid);
        return page;
    }

    private static Forms.TabPage BuildPipeTab(NumberingPlan plan)
    {
        var page = new Forms.TabPage("Tubos");
        var grid = CreateGrid();
        grid.Columns.Add("ordem", "Ordem");
        grid.Columns.Add("lote", "Lote");
        grid.Columns.Add("atual", "Nome atual");
        grid.Columns.Add("novo", "Novo nome");
        grid.Columns.Add("mont", "Montante");
        grid.Columns.Add("jus", "Jusante");

        foreach (var item in plan.Pipes.OrderBy(x => x.HydraulicOrder))
            grid.Rows.Add(item.HydraulicOrder, item.ComponentIndex, item.OldName, item.NewName, item.UpstreamName, item.DownstreamName);

        page.Controls.Add(grid);
        return page;
    }

    private static Forms.TabPage BuildWarningsTab(NumberingPlan plan)
    {
        var page = new Forms.TabPage(plan.Warnings.Count == 0 ? "Avisos" : $"Avisos ({plan.Warnings.Count})");
        var box = new Forms.TextBox
        {
            Dock = Forms.DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = Forms.ScrollBars.Vertical,
            Font = new Drawing.Font("Consolas", 9.5f),
            Text = plan.Warnings.Count == 0
                ? "Nenhum aviso no lote selecionado."
                : string.Join(Environment.NewLine, plan.Warnings.Select((x, i) => $"{i + 1}. {x}"))
        };
        page.Controls.Add(box);
        return page;
    }

    private static Forms.DataGridView CreateGrid()
    {
        return new Forms.DataGridView
        {
            Dock = Forms.DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = Forms.DataGridViewAutoSizeColumnsMode.DisplayedCells,
            SelectionMode = Forms.DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false
        };
    }
}

internal sealed class HydraulicDiagramPanel : Forms.Panel
{
    private readonly NumberingPlan _plan;
    private readonly Dictionary<ObjectId, DiagramNode> _nodes = new();
    private readonly List<DiagramEdge> _edges = new();
    private int _canvasWidth = 1100;
    private int _canvasHeight = 700;

    private const int NodeWidth = 118;
    private const int NodeHeight = 34;
    private const int ColumnGap = 190;
    private const int RowGap = 74;
    private const int MarginX = 70;
    private const int MarginY = 70;

    public HydraulicDiagramPanel(NumberingPlan plan)
    {
        _plan = plan;
        DoubleBuffered = true;
        AutoScroll = true;
        BackColor = Drawing.SystemColors.Window;
        BuildLayout();
        AutoScrollMinSize = new Drawing.Size(_canvasWidth, _canvasHeight);
    }

    private void BuildLayout()
    {
        _nodes.Clear();
        _edges.Clear();

        var structureById = _plan.Structures.ToDictionary(x => x.Id);
        var pipeById = _plan.Pipes.ToDictionary(x => x.Id);
        int yBase = MarginY;
        int globalMaxX = 1000;

        foreach (var component in _plan.Components.OrderBy(c => c.Index))
        {
            var structureIds = component.StructureIds.Where(structureById.ContainsKey).ToHashSet();
            var componentPipes = component.PipeIds.Where(pipeById.ContainsKey).Select(id => pipeById[id]).ToList();

            var incoming = structureIds.ToDictionary(id => id, _ => new List<PipeRenameItem>());
            var outgoing = structureIds.ToDictionary(id => id, _ => new List<PipeRenameItem>());

            foreach (var pipe in componentPipes)
            {
                if (!pipe.DownstreamStructureId.IsNull && incoming.TryGetValue(pipe.DownstreamStructureId, out var inList))
                    inList.Add(pipe);
                if (!pipe.UpstreamStructureId.IsNull && outgoing.TryGetValue(pipe.UpstreamStructureId, out var outList))
                    outList.Add(pipe);
            }

            var depths = new Dictionary<ObjectId, int>();
            var yPositions = new Dictionary<ObjectId, float>();
            int leafIndex = 0;

            var roots = structureIds
                .Where(id => outgoing[id].Count == 0 || outgoing[id].Any(p => p.DownstreamStructureId.IsNull))
                .OrderBy(id => structureById[id].HydraulicOrder)
                .ToList();

            if (roots.Count == 0)
                roots = structureIds.OrderBy(id => structureById[id].HydraulicOrder).Take(1).ToList();

            foreach (var root in roots)
            {
                int rootDepth = outgoing[root].Any(p => p.DownstreamStructureId.IsNull) ? 1 : 0;
                AssignDepth(root, rootDepth, new HashSet<ObjectId>());
                AssignY(root, new HashSet<ObjectId>());
            }

            foreach (var id in structureIds.OrderBy(id => structureById[id].HydraulicOrder))
            {
                if (!depths.ContainsKey(id))
                    AssignDepth(id, 0, new HashSet<ObjectId>());
                if (!yPositions.ContainsKey(id))
                    AssignY(id, new HashSet<ObjectId>());
            }

            int maxDepth = depths.Values.DefaultIfEmpty(0).Max();
            float maxLocalY = yPositions.Values.DefaultIfEmpty(0).Max();

            foreach (var id in structureIds)
            {
                int depth = depths[id];
                float x = MarginX + (maxDepth - depth) * ColumnGap;
                float y = yBase + yPositions[id] * RowGap;
                var item = structureById[id];
                _nodes[id] = new DiagramNode(id, item.NewName, item.PartLabel, component.Index, new Drawing.PointF(x, y));
                globalMaxX = Math.Max(globalMaxX, (int)x + NodeWidth + MarginX);
            }

            foreach (var pipe in componentPipes)
            {
                Drawing.PointF from;
                Drawing.PointF to;

                if (!pipe.UpstreamStructureId.IsNull && _nodes.TryGetValue(pipe.UpstreamStructureId, out var upNode))
                {
                    from = new Drawing.PointF(upNode.Position.X + NodeWidth, upNode.Position.Y + NodeHeight / 2f);
                }
                else if (!pipe.DownstreamStructureId.IsNull && _nodes.TryGetValue(pipe.DownstreamStructureId, out var downForOpenUp))
                {
                    from = new Drawing.PointF(downForOpenUp.Position.X - ColumnGap + NodeWidth, downForOpenUp.Position.Y + NodeHeight / 2f);
                }
                else
                {
                    from = new Drawing.PointF(MarginX, yBase + (maxLocalY + 1) * RowGap);
                }

                if (!pipe.DownstreamStructureId.IsNull && _nodes.TryGetValue(pipe.DownstreamStructureId, out var downNode))
                {
                    to = new Drawing.PointF(downNode.Position.X, downNode.Position.Y + NodeHeight / 2f);
                }
                else if (!pipe.UpstreamStructureId.IsNull && _nodes.TryGetValue(pipe.UpstreamStructureId, out var upForOpenDown))
                {
                    to = new Drawing.PointF(upForOpenDown.Position.X + NodeWidth + ColumnGap - NodeWidth, upForOpenDown.Position.Y + NodeHeight / 2f);
                }
                else
                {
                    to = new Drawing.PointF(from.X + ColumnGap, from.Y);
                }

                _edges.Add(new DiagramEdge(pipe, from, to));
            }

            int componentRows = Math.Max(1, (int)Math.Ceiling(maxLocalY + 1));
            yBase += componentRows * RowGap + 120;


            void AssignDepth(ObjectId id, int depth, HashSet<ObjectId> visiting)
            {
                if (!visiting.Add(id))
                    return;

                if (!depths.TryGetValue(id, out int existing) || depth > existing)
                    depths[id] = depth;

                foreach (var pipe in incoming[id].OrderBy(p => p.HydraulicOrder))
                {
                    if (!pipe.UpstreamStructureId.IsNull && structureIds.Contains(pipe.UpstreamStructureId))
                        AssignDepth(pipe.UpstreamStructureId, depth + 1, visiting);
                }

                visiting.Remove(id);
            }

            float AssignY(ObjectId id, HashSet<ObjectId> visiting)
            {
                if (yPositions.TryGetValue(id, out float existingY))
                    return existingY;
                if (!visiting.Add(id))
                    return leafIndex++;

                var children = incoming[id]
                    .Where(p => !p.UpstreamStructureId.IsNull && structureIds.Contains(p.UpstreamStructureId))
                    .OrderBy(p => p.HydraulicOrder)
                    .Select(p => p.UpstreamStructureId)
                    .Distinct()
                    .ToList();

                float y;
                if (children.Count == 0)
                {
                    y = leafIndex++;
                }
                else
                {
                    var ys = children.Select(child => AssignY(child, visiting)).ToList();
                    y = ys.Average();
                }

                visiting.Remove(id);
                yPositions[id] = y;
                return y;
            }
        }

        _canvasWidth = Math.Max(globalMaxX, 900);
        _canvasHeight = Math.Max(yBase + MarginY, 600);
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);

        using var titleFont = new Drawing.Font(this.Font.FontFamily, 9f, Drawing.FontStyle.Bold);
        using var nodeFont = new Drawing.Font(this.Font.FontFamily, 9f, Drawing.FontStyle.Bold);
        using var smallFont = new Drawing.Font(this.Font.FontFamily, 7.8f, Drawing.FontStyle.Regular);
        using var edgeFont = new Drawing.Font(this.Font.FontFamily, 8f, Drawing.FontStyle.Bold);
        using var pen = new Drawing.Pen(Drawing.Color.SteelBlue, 2.2f);
        using var openPen = new Drawing.Pen(Drawing.Color.DarkOrange, 2.2f) { DashStyle = Drawing2D.DashStyle.Dash };
        using var nodeBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(239, 246, 255));
        using var borderPen = new Drawing.Pen(Drawing.Color.FromArgb(70, 95, 125), 1.2f);
        using var textBrush = new Drawing.SolidBrush(Drawing.SystemColors.ControlText);
        using var labelBrush = new Drawing.SolidBrush(Drawing.Color.DarkRed);
        using var hintBrush = new Drawing.SolidBrush(Drawing.Color.DimGray);
        using var arrow = new Drawing2D.AdjustableArrowCap(4, 6, true);

        pen.CustomEndCap = arrow;
        openPen.CustomEndCap = arrow;

        e.Graphics.DrawString("MONTANTE", titleFont, hintBrush, MarginX, 18);
        e.Graphics.DrawString("JUSANTE", titleFont, hintBrush, Math.Max(MarginX + 250, _canvasWidth - 170), 18);

        foreach (var edge in _edges.OrderBy(x => x.Item.HydraulicOrder))
        {
            var drawPen = edge.Item.HasOpenEnd ? openPen : pen;
            e.Graphics.DrawLine(drawPen, edge.From, edge.To);

            float mx = (edge.From.X + edge.To.X) / 2f;
            float my = (edge.From.Y + edge.To.Y) / 2f - 18;
            string relation = $"{edge.Item.NewName}  |  {edge.Item.UpstreamName} → {edge.Item.DownstreamName}";
            var size = e.Graphics.MeasureString(relation, edgeFont);
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(235, Drawing.SystemColors.Window));
            e.Graphics.FillRectangle(bg, mx - size.Width / 2f - 3, my - 1, size.Width + 6, size.Height + 2);
            e.Graphics.DrawString(relation, edgeFont, labelBrush, mx - size.Width / 2f, my);
        }

        foreach (var node in _nodes.Values.OrderBy(n => n.ComponentIndex).ThenBy(n => n.Position.Y))
        {
            var rect = new Drawing.RectangleF(node.Position.X, node.Position.Y, NodeWidth, NodeHeight);
            e.Graphics.FillRectangle(nodeBrush, rect);
            e.Graphics.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width, rect.Height);

            var nameSize = e.Graphics.MeasureString(node.Name, nodeFont);
            e.Graphics.DrawString(node.Name, nodeFont, textBrush,
                rect.X + (rect.Width - nameSize.Width) / 2f,
                rect.Y + 3);

            string part = node.PartLabel.Length > 24 ? node.PartLabel[..24] + "…" : node.PartLabel;
            var partSize = e.Graphics.MeasureString(part, smallFont);
            e.Graphics.DrawString(part, smallFont, hintBrush,
                rect.X + (rect.Width - partSize.Width) / 2f,
                rect.Y + 19);
        }
    }

    private sealed record DiagramNode(ObjectId Id, string Name, string PartLabel, int ComponentIndex, Drawing.PointF Position);
    private sealed record DiagramEdge(PipeRenameItem Item, Drawing.PointF From, Drawing.PointF To);
}
