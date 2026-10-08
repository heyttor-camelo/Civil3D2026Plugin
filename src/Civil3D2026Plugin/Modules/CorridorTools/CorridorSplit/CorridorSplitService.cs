using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Civil.ApplicationServices;

namespace Civil3D2026Plugin.Modules.CorridorTools.CorridorSplit;

/// <summary>
/// Motor conservador da separacao. Uma unica transacao abrange destino e origem.
/// Nao tenta clonar overrides, offset baselines ou Corridor Surfaces na v1.0.
/// </summary>
internal static class CorridorSplitService
{
    private const double Tol = 1e-6;

    public static (int Regions, int Baselines) Execute(
        CivilDocument civil, AcadDb.Transaction tr, CivilDb.Corridor source,
        string targetName, IReadOnlyList<RegionChoice> selection, bool remove)
    {
        if (selection.Count == 0) throw new InvalidOperationException("Nenhuma regiao selecionada.");

        var grouped = selection.GroupBy(x => x.BaselineIndex).OrderBy(x => x.Key).ToList();
        var plans = new List<BaselinePlan>();

        // Pre-validacao ANTES de criar qualquer objeto ou alterar a origem.
        foreach (var group in grouped)
        {
            var baseline = source.Baselines[group.Key];
            var indexes = new HashSet<int>(group.Select(x => x.RegionIndex));
            var plan = new BaselinePlan(baseline, indexes);
            for (int i = 0; i < baseline.BaselineRegions.Count; i++)
            {
                var region = baseline.BaselineRegions[i];
                if (indexes.Contains(i))
                {
                    if (region.GetOverriddenStations().Length != 0)
                        throw new InvalidOperationException("Regiao '" + region.Name +
                            "' possui overrides de secoes (nao suportados na v1.0).");
                    if (region.OffsetBaselines.Count != 0)
                        throw new InvalidOperationException("Regiao '" + region.Name +
                            "' possui Offset Baselines (nao suportadas na v1.0).");
                }
            }
            SplitTransitionSets(plan);
            plans.Add(plan);
        }

        AcadDb.ObjectId targetId = civil.CorridorCollection.Add(targetName);
        var target = (CivilDb.Corridor)tr.GetObject(targetId, AcadDb.OpenMode.ForWrite);
        target.CodeSetStyleId = source.CodeSetStyleId;
        target.RegionLockMode = source.RegionLockMode;
        target.MaximumTriangleSideLength = source.MaximumTriangleSideLength;

        foreach (var plan in plans)
        {
            var srcBase = plan.Source;
            CivilDb.Baseline dstBase = srcBase.IsFeatureLineBased()
                ? target.Baselines.Add(srcBase.Name, srcBase.FeatureLineId)
                : target.Baselines.Add(srcBase.Name, srcBase.AlignmentId, srcBase.ProfileId);

            foreach (int index in plan.Indexes.OrderBy(x => x))
            {
                var srcReg = srcBase.BaselineRegions[index];
                var dstReg = dstBase.BaselineRegions.Add(
                    srcReg.Name, srcReg.AssemblyId, srcReg.StartStation, srcReg.EndStation);
                CopyFrequency(srcReg, dstReg);
                // Os targets sao vinculados apos todas as baselines e regioes existirem.
                // Nesta fase valida-se apenas a configuracao estrutural.
                plan.NewRegions.Add(dstReg);
            }

            if (plan.MovingTransitions.Count > 0)
            {
                var newSets = new List<CivilDb.CorridorTransitionSet>();
                foreach (var original in plan.MovingTransitions)
                {
                    var first = original.GetTransitionAt(0);
                    var seed = plan.NewRegions.FirstOrDefault(r =>
                        first.StartStation >= r.StartStation - Tol &&
                        first.StartStation <= r.EndStation + Tol);
                    if (seed == null)
                        throw new InvalidOperationException("Nao encontrei regiao base para a transition '" +
                            original.Name + "'.");

                    CivilDb.CorridorTransitionSet copy;
                    if (first.HasSide)
                        copy = new CivilDb.CorridorTransitionSet(original.Name, seed,
                            original.SubassemblyName, original.NameType, first.Side);
                    else
                        copy = new CivilDb.CorridorTransitionSet(original.Name, seed,
                            original.SubassemblyName, original.NameType);
                    copy.Comment = original.Comment;

                    for (int i = 0; i < original.TransitionCount; i++)
                    {
                        var data = original.GetTransitionAt(i);
                        var created = i == 0 ? copy.AddTransition(data.ParameterName) : copy.AddTransition();
                        created.StartStation = data.StartStation;
                        created.EndStation = data.EndStation;
                        created.StartValue = data.StartValue;
                        created.EndValue = data.EndValue;
                        created.TransitionType = data.TransitionType;
                    }
                    copy.StationLocked = original.StationLocked;
                    newSets.Add(copy);
                }
                dstBase.SetTransitions(newSets);
                if (dstBase.getTransitions().Count != newSets.Count)
                    throw new InvalidOperationException("Divergencia na quantidade de Corridor Transition Sets.");
            }
        }

        // Primeiro rebuild sem target mapping: materializa todas as regioes,
        // inclusive as que dependem de outras baselines no mesmo Corridor.
        try
        {
            target.Rebuild();
        }
        catch (System.Exception ex)
        {
            throw new InvalidOperationException("Rebuild inicial do Corridor destino: " + ex.Message, ex);
        }

        // Aplicar targets somente quando TODAS as regioes foram criadas.
        for (int baseIndex = 0; baseIndex < plans.Count; baseIndex++)
        {
            var plan = plans[baseIndex];
            var destinationBase = target.Baselines[baseIndex];
            int position = 0;
            foreach (int index in plan.Indexes.OrderBy(x => x))
            {
                // Apos Rebuild, obter novos wrappers da colecao atual,
                // evitando referencias potencialmente invalidadas da fase de criacao.
                var sourceRegion = plan.Source.BaselineRegions[index];
                var destinationRegion = destinationBase.BaselineRegions[position++];
                RestoreAdditionalStations(sourceRegion, destinationRegion, "apos Rebuild inicial");
                CopyTargets(sourceRegion, destinationRegion);
            }
        }

        // Rebuild final do DESTINO com targets e transitions, antes de
        // QUALQUER remocao no Corridor original.
        try
        {
            target.Rebuild();
        }
        catch (System.Exception ex)
        {
            throw new InvalidOperationException("Rebuild final do Corridor destino: " + ex.Message, ex);
        }
        // Algumas versoes do Civil 3D podem normalizar as estacas adicionais no
        // Rebuild com targets. Fazer uma unica tentativa de reparo, reconstruir
        // novamente e entao exigir equivalencia (sem ignorar perda de dados).
        bool needsStationRebuild = false;
        for (int b = 0; b < plans.Count; b++)
        {
            var plan = plans[b];
            var dstBase = target.Baselines[b];
            int r = 0;
            foreach (int originalIndex in plan.Indexes.OrderBy(x => x))
            {
                var originalRegion = plan.Source.BaselineRegions[originalIndex];
                var copyRegion = dstBase.BaselineRegions[r++];
                if (!SameAdditionalStations(
                        originalRegion.AppliedAssemblySetting.AdditionalAppliedAssemblies,
                        copyRegion.AppliedAssemblySetting.AdditionalAppliedAssemblies))
                {
                    RestoreAdditionalStations(originalRegion, copyRegion, "apos Rebuild com targets");
                    needsStationRebuild = true;
                }
            }
        }

        if (needsStationRebuild)
        {
            try
            {
                target.Rebuild();
            }
            catch (System.Exception ex)
            {
                throw new InvalidOperationException(
                    "Rebuild de validacao apos restaurar estacas adicionais: " + ex.Message, ex);
            }
        }

        foreach (var plan in plans)
        {
            CivilDb.Baseline dstBase = target.Baselines[plans.IndexOf(plan)];
            if (dstBase.BaselineRegions.Count != plan.Indexes.Count)
                throw new InvalidOperationException("Quantidade de regioes no destino diverge da origem.");
            int n = 0;
            foreach (int index in plan.Indexes.OrderBy(x => x))
                CheckRegion(plan.Source.BaselineRegions[index], dstBase.BaselineRegions[n++]);
            VerifyTransitions(plan.MovingTransitions, dstBase.getTransitions());
        }

        if (remove)
        {
            foreach (var plan in plans)
            {
                if (plan.MovingTransitions.Count > 0)
                    plan.Source.SetTransitions(plan.KeepingTransitions);

                foreach (int index in plan.Indexes.OrderByDescending(x => x))
                {
                    var region = plan.Source.BaselineRegions[index];
                    if (!plan.Source.BaselineRegions.Remove(region))
                        throw new InvalidOperationException("Falha ao remover a regiao '" + region.Name + "'.");
                }
            }
            source.Rebuild();
        }
        return (selection.Count, plans.Count);
    }

    private static void SplitTransitionSets(BaselinePlan plan)
    {
        var selectedRanges = new List<(double From, double To)>();
        foreach (int index in plan.Indexes)
        {
            var r = plan.Source.BaselineRegions[index];
            selectedRanges.Add((r.StartStation, r.EndStation));
        }

        foreach (var set in plan.Source.getTransitions())
        {
            if (set.TransitionCount == 0) continue;
            bool anyMoved = false;
            bool anyStaying = false;
            for (int i = 0; i < set.TransitionCount; i++)
            {
                var item = set.GetTransitionAt(i);
                double a = Math.Min(item.StartStation, item.EndStation);
                double b = Math.Max(item.StartStation, item.EndStation);
                bool overlaps = selectedRanges.Any(r => a < r.To - Tol && b > r.From + Tol);
                if (!overlaps)
                    anyStaying = true;
                else if (CoveredBy(selectedRanges, a, b))
                    anyMoved = true;
                else
                    throw new InvalidOperationException("Transition '" + set.Name +
                        "' atravessa a fronteira de regioes selecionadas. Separe a transition antes de transferir.");
            }
            if (anyMoved && anyStaying)
                throw new InvalidOperationException("Transition Set '" + set.Name +
                    "' mistura intervalos a transferir e a manter. Separe o conjunto antes de executar CORRSPLIT.");

            if (anyMoved) plan.MovingTransitions.Add(set);
            else plan.KeepingTransitions.Add(set);
        }
    }

    private static bool CoveredBy(IEnumerable<(double From, double To)> ranges, double start, double end)
    {
        var sorted = ranges.OrderBy(x => x.From).ToList();
        double covered = start;
        foreach (var r in sorted)
        {
            if (r.To < covered - Tol) continue;
            if (r.From > covered + Tol) return false;
            covered = Math.Max(covered, r.To);
            if (covered >= end - Tol) return true;
        }
        return false;
    }

    private static void CopyFrequency(CivilDb.BaselineRegion from, CivilDb.BaselineRegion to)
    {
        var src = from.AppliedAssemblySetting;
        var dst = to.AppliedAssemblySetting;
        dst.AppliedAdjacentToOffsetTargetStartEnd = src.AppliedAdjacentToOffsetTargetStartEnd;
        dst.CorridorAlongCurvesOption = src.CorridorAlongCurvesOption;
        dst.FrequencyAlongCurves = src.FrequencyAlongCurves;
        dst.FrequencyAlongProfileCurves = src.FrequencyAlongProfileCurves;
        dst.FrequencyAlongSpirals = src.FrequencyAlongSpirals;
        dst.FrequencyAlongTangents = src.FrequencyAlongTangents;
        dst.FrequencyAlongTargetCurves = src.FrequencyAlongTargetCurves;
        dst.MODAlongCurves = src.MODAlongCurves;
        dst.MODAlongTargetCurves = src.MODAlongTargetCurves;
        dst.TargetCurveOption = src.TargetCurveOption;
        // Nao copiar AdditionalAppliedAssemblies aqui: a API pode recalcular essa
        // colecao durante Rebuild. As estacas manuais sao restauradas explicitamente
        // via BaselineRegion.AddStation APOS a criacao de todas as regioes.
    }

    // Evitar a comparacao por indice: a API pode devolver as estacas em ordem
    // diferente. Ainda exigimos mesma quantidade, estaca e descricao.
    private static bool SameAdditionalStations(
        List<CivilDb.AdditionalAppliedAssemblyInfo> original,
        List<CivilDb.AdditionalAppliedAssemblyInfo> copy)
    {
        if (original.Count != copy.Count) return false;
        var expected = original.OrderBy(x => x.Station)
            .ThenBy(x => x.Description ?? string.Empty, StringComparer.Ordinal).ToList();
        var actual = copy.OrderBy(x => x.Station)
            .ThenBy(x => x.Description ?? string.Empty, StringComparer.Ordinal).ToList();

        for (int i = 0; i < expected.Count; i++)
        {
            if (Math.Abs(expected[i].Station - actual[i].Station) > Tol ||
                !string.Equals(expected[i].Description ?? string.Empty,
                    actual[i].Description ?? string.Empty, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static string FormatAdditionalStations(
        List<CivilDb.AdditionalAppliedAssemblyInfo> stations)
    {
        if (stations.Count == 0) return "(nenhuma)";
        const int maxDisplay = 12;
        var ordered = stations.OrderBy(x => x.Station).ToList();
        string values = string.Join("; ", ordered.Take(maxDisplay).Select(s =>
            s.Station.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
            " [" + (s.Description ?? "") + "]"));
        return ordered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " estaca(s): " + values +
            (ordered.Count > maxDisplay ? "; ... +" + (ordered.Count - maxDisplay) : "");
    }

    private static void RestoreAdditionalStations(
        CivilDb.BaselineRegion original, CivilDb.BaselineRegion copy, string phase)
    {
        var expected = original.AppliedAssemblySetting.AdditionalAppliedAssemblies;
        var current = copy.AppliedAssemblySetting.AdditionalAppliedAssemblies;
        if (SameAdditionalStations(expected, current)) return;

        try
        {
            // ClearAdditionalStations e AddStation trabalham diretamente na
            // BaselineRegion; nao dependem do setter de frequencia antes do Rebuild.
            copy.ClearAdditionalStations();
            foreach (var item in expected.OrderBy(x => x.Station))
                copy.AddStation(item.Station, item.Description ?? string.Empty);
        }
        catch (System.Exception ex)
        {
            throw new InvalidOperationException(
                "Regiao '" + original.Name + "': falhou ao restaurar estacas adicionais " +
                "(" + phase + "). Origem: " + FormatAdditionalStations(expected) +
                " | Destino antes: " + FormatAdditionalStations(current) +
                ". Detalhe: " + ex.Message, ex);
        }

        // A colecao de AppliedAssemblySetting pode atualizar apenas apos Rebuild.
        // Nao exigir equivalencia imediata: CheckRegion valida rigorosamente
        // depois do ultimo Rebuild, com estacas e descricoes detalhadas no erro.
    }

    private static void CopyTargets(CivilDb.BaselineRegion from, CivilDb.BaselineRegion to)
    {
        var src = from.GetTargets();
        if (src.Count == 0) return;

        var dst = to.GetTargets(); // colecao tem de pertencer a regiao DESTINO
        if (src.Count != dst.Count)
            throw new InvalidOperationException(
                "Regiao '" + from.Name + "': contagem de parametros target difere (" +
                src.Count + " na origem, " + dst.Count + " no destino).");

        var used = new HashSet<int>();
        var matching = new int[src.Count];
        bool changed = false;

        for (int i = 0; i < src.Count; i++)
        {
            var a = src[i];
            int match = -1;
            for (int j = 0; j < dst.Count; j++)
            {
                if (!used.Contains(j) && SameTarget(a, dst[j]))
                {
                    match = j;
                    break;
                }
            }
            if (match < 0)
                throw new InvalidOperationException("Regiao '" + from.Name +
                    "': parametro target nao encontrado no destino: " +
                    a.SubassemblyName + " / " + a.DisplayName + " / " + a.LogicalName);

            used.Add(match);
            matching[i] = match;
            var b = dst[match];
            string step = "identificacao";
            int count = a.TargetIds.Count;

            try
            {
                // A contagem de TargetIds e POR PARAMETRO de subassembly.
                // Offset (horizontal) e Elevation (vertical) costumam ser
                // parametros diferentes e cada um pode ter um unico TargetId.
                //
                // Nao reescrever parametros sem target: isso evita validacoes
                // de TargetToOption sobre parametros vazios na API Autodesk.
                if (count == 0)
                    continue;

                var ids = new AcadDb.ObjectIdCollection();
                foreach (AcadDb.ObjectId targetId in a.TargetIds)
                    ids.Add(targetId);

                step = "atribuir TargetIds";
                if (!SameIds(b.TargetIds, ids))
                {
                    b.TargetIds = ids;
                    changed = true;
                }

                // Nao acessar nem atribuir TargetToOption com 0/1 TargetIds.
                // Tambem exigir que a colecao DESTINO realmente retenha 2 IDs.
                step = "configurar TargetToOption";
                if (count >= 2 && b.TargetIds.Count >= 2 &&
                    b.TargetToOption != a.TargetToOption)
                {
                    b.TargetToOption = a.TargetToOption;
                    changed = true;
                }

                // UseSameSideTarget: a API verifica tanto a quantidade de IDs
                // quanto o TIPO. So Offset e aceito (nem Elevation, nem
                // OffsetPipe). O getter tambem lanca excecao fora dessas regras.
                step = "configurar UseSameSideTarget";
                if (CanReadSameSideTarget(a, b) &&
                    b.UseSameSideTarget != a.UseSameSideTarget)
                {
                    b.UseSameSideTarget = a.UseSameSideTarget;
                    changed = true;
                }
            }
            catch (System.Exception ex)
            {
                throw new InvalidOperationException(
                    "Regiao '" + from.Name + "', subassembly '" + a.SubassemblyName +
                    "', parametro '" + a.DisplayName + "' (" + a.LogicalName +
                    ", tipo " + a.TargetType + ", " + count +
                    " TargetIds): falhou em '" + step + "': " + ex.Message, ex);
            }
        }

        if (changed)
        {
            try
            {
                to.SetTargets(dst);
            }
            catch (System.Exception ex)
            {
                throw new InvalidOperationException("Regiao '" + from.Name +
                    "': erro ao executar BaselineRegion.SetTargets: " + ex.Message, ex);
            }
        }

        try
        {
            var after = to.GetTargets();
            if (after.Count != dst.Count)
                throw new InvalidOperationException("Contagem de parametros target divergente.");
            for (int i = 0; i < src.Count; i++)
            {
                var a = src[i];
                var b = after[matching[i]];
                if (!SameTarget(a, b) || !SameIds(a.TargetIds, b.TargetIds) ||
                    (a.TargetIds.Count >= 2 && b.TargetIds.Count >= 2 &&
                        a.TargetToOption != b.TargetToOption) ||
                    (CanReadSameSideTarget(a, b) &&
                        a.UseSameSideTarget != b.UseSameSideTarget))
                    throw new InvalidOperationException("Divergencia no target '" +
                        a.SubassemblyName + " / " + a.DisplayName + "' (" +
                        a.TargetIds.Count + " IDs).");
            }
        }
        catch (System.Exception ex)
        {
            throw new InvalidOperationException("Regiao '" + from.Name +
                "': verificacao do mapeamento dos targets: " + ex.Message, ex);
        }
    }

    // Evita consultar a propriedade sequer quando o parametro nao possui
    // suporte da API: so Offset (EXCLUINDO OffsetPipe), com pelo menos dois
    // TargetIds em AMBOS os lados. Este predicado nao invoca a propriedade.
    private static bool CanReadSameSideTarget(
        CivilDb.SubassemblyTargetInfo source, CivilDb.SubassemblyTargetInfo target) =>
        source.TargetType == CivilDb.SubassemblyLogicalNameType.Offset &&
        target.TargetType == CivilDb.SubassemblyLogicalNameType.Offset &&
        source.TargetIds.Count >= 2 &&
        target.TargetIds.Count >= 2;

    private static bool SameTarget(CivilDb.SubassemblyTargetInfo a, CivilDb.SubassemblyTargetInfo b) =>
        a.SubassemblyName == b.SubassemblyName &&
        a.AssemblyGroupName == b.AssemblyGroupName &&
        a.DisplayName == b.DisplayName &&
        a.LogicalName == b.LogicalName &&
        a.TargetType == b.TargetType;

    private static bool SameIds(AcadDb.ObjectIdCollection a, AcadDb.ObjectIdCollection b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static void CheckRegion(CivilDb.BaselineRegion from, CivilDb.BaselineRegion to)
    {
        if (from.Name != to.Name || from.AssemblyId != to.AssemblyId ||
            Math.Abs(from.StartStation - to.StartStation) > Tol ||
            Math.Abs(from.EndStation - to.EndStation) > Tol)
            throw new InvalidOperationException("Dados da regiao '" + from.Name + "' divergiram no destino.");
        var src = from.AppliedAssemblySetting;
        var dst = to.AppliedAssemblySetting;
        if (Math.Abs(src.FrequencyAlongTangents - dst.FrequencyAlongTangents) > Tol ||
            Math.Abs(src.FrequencyAlongCurves - dst.FrequencyAlongCurves) > Tol ||
            Math.Abs(src.FrequencyAlongSpirals - dst.FrequencyAlongSpirals) > Tol ||
            Math.Abs(src.FrequencyAlongProfileCurves - dst.FrequencyAlongProfileCurves) > Tol ||
            Math.Abs(src.FrequencyAlongTargetCurves - dst.FrequencyAlongTargetCurves) > Tol ||
            src.CorridorAlongCurvesOption != dst.CorridorAlongCurvesOption ||
            src.TargetCurveOption != dst.TargetCurveOption ||
            src.MODAlongCurves != dst.MODAlongCurves ||
            src.MODAlongTargetCurves != dst.MODAlongTargetCurves ||
            src.AppliedAdjacentToOffsetTargetStartEnd != dst.AppliedAdjacentToOffsetTargetStartEnd)
            throw new InvalidOperationException(
                "Frequencias de assembly divergiram na regiao '" + from.Name + "'.");

        if (!SameAdditionalStations(src.AdditionalAppliedAssemblies, dst.AdditionalAppliedAssemblies))
            throw new InvalidOperationException(
                "Estacas adicionais divergiram APOS reconstruir a regiao '" + from.Name +
                "'. Origem: " + FormatAdditionalStations(src.AdditionalAppliedAssemblies) +
                " | Destino: " + FormatAdditionalStations(dst.AdditionalAppliedAssemblies));
        VerifyTargets(from, to);
    }


    private static void VerifyTargets(CivilDb.BaselineRegion from, CivilDb.BaselineRegion to)
    {
        var original = from.GetTargets();
        var newTargets = to.GetTargets();
        if (original.Count != newTargets.Count)
            throw new InvalidOperationException("Quantidade de targets alterada apos Rebuild.");
        var used = new HashSet<int>();
        for (int i = 0; i < original.Count; i++)
        {
            var src = original[i];
            int j = 0;
            for (; j < newTargets.Count; j++)
                if (!used.Contains(j) && SameTarget(src, newTargets[j]) &&
                    SameIds(src.TargetIds, newTargets[j].TargetIds) &&
                    (src.TargetIds.Count < 2 || newTargets[j].TargetIds.Count < 2 ||
                        src.TargetToOption == newTargets[j].TargetToOption) &&
                    (!CanReadSameSideTarget(src, newTargets[j]) ||
                        src.UseSameSideTarget == newTargets[j].UseSameSideTarget)) break;
            if (j == newTargets.Count)
                throw new InvalidOperationException("Target da regiao '" + from.Name + "' divergiu apos Rebuild: " + src.DisplayName);
            used.Add(j);
        }
    }

    private static void VerifyTransitions(
        IReadOnlyList<CivilDb.CorridorTransitionSet> originals,
        List<CivilDb.CorridorTransitionSet> copies)
    {
        if (originals.Count != copies.Count)
            throw new InvalidOperationException("Transition Sets divergiram apos o Rebuild.");
        var used = new HashSet<int>();
        foreach (var source in originals)
        {
            int match = -1;
            for (int i = 0; i < copies.Count; i++)
                if (!used.Contains(i) &&
                    source.Name == copies[i].Name &&
                    source.SubassemblyName == copies[i].SubassemblyName &&
                    source.NameType == copies[i].NameType)
                {
                    match = i;
                    break;
                }
            if (match < 0) throw new InvalidOperationException("Transition Set ausente: " + source.Name);
            used.Add(match);
            var target = copies[match];
            if (source.TransitionCount != target.TransitionCount ||
                source.Comment != target.Comment || source.StationLocked != target.StationLocked)
                throw new InvalidOperationException("Propriedades de Transition Set divergiram: " + source.Name);
            for (int i = 0; i < source.TransitionCount; i++)
            {
                var a = source.GetTransitionAt(i);
                var b = target.GetTransitionAt(i);
                if (a.ParameterName != b.ParameterName ||
                    Math.Abs(a.StartStation - b.StartStation) > Tol ||
                    Math.Abs(a.EndStation - b.EndStation) > Tol ||
                    !Equals(a.StartValue, b.StartValue) ||
                    !Equals(a.EndValue, b.EndValue) ||
                    a.TransitionType != b.TransitionType ||
                    a.HasSide != b.HasSide ||
                    (a.HasSide && a.Side != b.Side))
                    throw new InvalidOperationException("Valores de transition divergiram: " + source.Name);
            }
        }
    }

    private sealed class BaselinePlan
    {
        public CivilDb.Baseline Source { get; }
        public HashSet<int> Indexes { get; }
        public List<CivilDb.CorridorTransitionSet> MovingTransitions { get; } = new();
        public List<CivilDb.CorridorTransitionSet> KeepingTransitions { get; } = new();
        public List<CivilDb.BaselineRegion> NewRegions { get; } = new();
        public BaselinePlan(CivilDb.Baseline source, HashSet<int> indexes)
        {
            Source = source;
            Indexes = indexes;
        }
    }
}
