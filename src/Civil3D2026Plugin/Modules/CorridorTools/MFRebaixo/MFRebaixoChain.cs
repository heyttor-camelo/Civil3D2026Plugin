using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>
/// Núcleo do rebaixo sobre uma "cadeia" de meio-fio (13/11/2026, pedido do Heyvid: "é só invadir a
/// outra região também"): a Region do rebaixo, as Regions coladas da mesma Baseline e, onde a Baseline
/// acaba, a Baseline vizinha cujo meio-fio encosta na ponta e segue no mesmo sentido (a sarjeta da
/// esquina, por exemplo; o retorno da esquina, que vira de lado, não entra). Rampas e trecho rebaixado
/// podem atravessar as divisas: cada pedaço vira um Transition Set na Region dele, com a altura
/// contínua na divisa, e tudo é gravado como UM rebaixo (nome do set principal + _PARTE_...; o set
/// principal guarda as estacas e valores do rebaixo inteiro no comentário).
/// </summary>
public sealed partial class MFRebaixoCommands
{
    /// <summary>Pedaço da cadeia: uma Region, com a conversão da estaca do rebaixo para a estaca da Baseline dela.</summary>
    private sealed class CurbSeg
    {
        public CivilCorridor Corridor = null!;
        public Baseline Baseline = null!;
        public BaselineRegion Region = null!;
        public ObjectId SubassemblyId;
        public string SubassemblyName = "";
        public SubassemblySideType Side;
        public bool Main;
        public bool OtherBaseline;

        /// <summary>Trecho coberto, em estacas da Baseline do rebaixo.</summary>
        public double OurFrom;
        public double OurTo;

        /// <summary>Estaca na Baseline deste pedaço = A + K * estaca do rebaixo (K = +1 ou -1).</summary>
        public double A;
        public int K = 1;

        public double Their(double our) => A + (K * our);

        public double Our(double their) => K * (their - A);
    }

    private const double ChainReach = 40.0;

    private static void ApplyRebaixoCore(
        Transaction tr,
        Editor ed,
        CivilCorridor corridor,
        Baseline baseline,
        BaselineRegion region,
        SubassemblySideType side,
        double s1,
        double s2,
        double finalHeight,
        string sideOrigin,
        double? expectedNominal,
        double? expectedStart,
        string comment = "REBAIXO MF",
        double? slopeIn = null,
        double? slopeOut = null)
    {
        // Qualquer ordem (painel e reposicionar tambem): so a mesma estaca e recusada.
        if (s2 < s1)
            (s1, s2) = (s2, s1);

        if (s2 <= s1 + StationTolerance)
            throw new InvalidOperationException("O início e o fim do trecho rebaixado caíram na mesma estaca.");

        ObjectId subassemblyId = FindMfSubassemblyId(tr, region, side, out string subassemblyInstanceName);
        if (subassemblyId.IsNull)
        {
            throw new InvalidOperationException(
                "Não foi encontrada a subassembly MF " +
                $"no lado {GetSideLabel(side)}." +
                DescribeAssemblySubassemblies(tr, region));
        }

        var subassembly = (Subassembly)tr.GetObject(subassemblyId, OpenMode.ForRead);
        if (!TryGetNominalFreeHeight(subassembly, out double normalHeight))
            throw new InvalidOperationException($"Não foi possível ler o parâmetro '{NormalParameterName}' nem '{ParameterName}'.");
        if (!HasDoubleParameter(subassembly, ParameterName))
            throw new InvalidOperationException($"A subassembly selecionada não possui o parâmetro '{ParameterName}'.");
        if (expectedNominal.HasValue && Math.Abs(normalHeight - expectedNominal.Value) > 1e-4)
            throw new InvalidOperationException("A altura livre nominal da subassembly mudou entre os cliques. Reinicie este par.");

        if (!TryGetDoubleParameterValue(subassembly, RampSlopeParameterName, out double rampSlope))
        {
            rampSlope = DefaultRampSlope;
            if (slopeIn is null)
                Say(ed, $"\n[AVISO] Subassembly sem '{RampSlopeParameterName}'; usando {DefaultRampSlope * 100.0:0.##}% nas rampas do MF.");
        }
        double rampSlopeAbs = Math.Abs(rampSlope);
        if (rampSlopeAbs <= 1e-9)
            throw new InvalidOperationException($"O parâmetro '{RampSlopeParameterName}' deve ser diferente de 0 para calcular a rampa.");

        // Cadeia de meio-fio em volta do trecho.
        var notes = new List<string>();
        List<CurbSeg> chain = BuildChain(tr, corridor, baseline, region, side, subassemblyId, subassemblyInstanceName, s1 - ChainReach, s2 + ChainReach, notes);
        double chainLo = chain[0].OurFrom, chainHi = chain[^1].OurTo;
        if (s1 < chainLo - StationTolerance || s2 > chainHi + StationTolerance)
        {
            throw new InvalidOperationException(
                $"O trecho rebaixado ({s1:0.000} - {s2:0.000}) sai do meio-fio: a Region '{region.Name}' e as vizinhas vão de {chainLo:0.000} a {chainHi:0.000} " +
                "(sem Region colada nem Baseline de meio-fio encostando na ponta).");
        }

        // Altura livre normal: seção existente antes do trecho e depois dele, em qualquer pedaço da cadeia.
        if (!TryReferenceHeight(chain, s1, before: true, out double startEffectiveHeight, out string startEffectiveSource, out double referenceStartStation, out CurbSeg? startRefSeg))
            throw new InvalidOperationException("Não existe Corridor Section antes do trecho rebaixado (nesta Region ou nas vizinhas) para ler a altura normal efetiva.");
        if (expectedStart.HasValue && Math.Abs(startEffectiveHeight - expectedStart.Value) > 1e-4)
            throw new InvalidOperationException("A altura livre efetiva da seção anterior mudou entre os cliques. Reinicie este par para recalcular o rebaixo.");
        if (!TryReferenceHeight(chain, s2, before: false, out double endEffectiveHeight, out string endEffectiveSource, out double referenceEndStation, out CurbSeg? endRefSeg))
            throw new InvalidOperationException("Não existe Corridor Section depois do trecho rebaixado (nesta Region ou nas vizinhas) para ler a altura normal efetiva.");

        if (finalHeight < -StationTolerance)
            throw new InvalidOperationException($"Altura livre final inválida ({finalHeight:0.###} m): não pode ser negativa.");

        double maximumFinalHeight = Math.Min(startEffectiveHeight, endEffectiveHeight);
        if (finalHeight > maximumFinalHeight + StationTolerance)
        {
            string aviso =
                $"Altura final {finalHeight:0.###} m é maior que a altura do meio-fio lida nas pontas " +
                $"(antes={startEffectiveHeight:0.###} m; depois={endEffectiveHeight:0.###} m): " +
                "o meio-fio sobe nesse trecho. Precisa do MFSARJETA v1.25 ou mais novo no corredor; " +
                "com peça mais antiga o meio-fio não sobe.";
            PluginLog.Aviso("MFREBAIXO", aviso);
            Say(ed, "\n[MFREBAIXO] Aviso: " + aviso);
        }

        // Declividade escolhida pelo usuario (comando, painel ou lista): no lugar do iRampa da peca.
        double slopeEntry = slopeIn is double si && Math.Abs(si) > 1e-6 ? Math.Abs(si) : rampSlopeAbs;
        double slopeExit = slopeOut is double so && Math.Abs(so) > 1e-6 ? Math.Abs(so) : slopeIn is double si2 && Math.Abs(si2) > 1e-6 ? Math.Abs(si2) : rampSlopeAbs;
        if (Math.Max(slopeEntry, slopeExit) > MaxRampSlopeNbr + 1e-6)
        {
            string aviso = $"Declividade da rampa do meio-fio de {Math.Max(slopeEntry, slopeExit) * 100.0:0.##}% (acima de 8,33%, NBR 9050).";
            PluginLog.Aviso("MFREBAIXO", aviso);
            Say(ed, "\n[MFREBAIXO] Aviso: " + aviso);
        }

        double entryRampLength = Math.Abs(startEffectiveHeight - finalHeight) / slopeEntry;
        double exitRampLength = Math.Abs(endEffectiveHeight - finalHeight) / slopeExit;
        if (entryRampLength <= StationTolerance || exitRampLength <= StationTolerance)
        {
            throw new InvalidOperationException(
                "A diferença entre a altura normal efetiva e a altura final é insuficiente para gerar uma rampa de meio-fio.");
        }

        double s0 = s1 - entryRampLength;
        double s3 = s2 + exitRampLength;
        if (s0 < chainLo - StationTolerance)
        {
            notes.Add($"rampa de entrada cortada em {chainLo:0.000} ({s1 - chainLo:0.##} m em vez de {entryRampLength:0.##} m): o meio-fio não continua antes dali (sem Region colada nem Baseline de meio-fio encostando)");
            s0 = chainLo;
        }
        if (s3 > chainHi + StationTolerance)
        {
            notes.Add($"rampa de saída cortada em {chainHi:0.000} ({chainHi - s2:0.##} m em vez de {exitRampLength:0.##} m): o meio-fio não continua depois dali");
            s3 = chainHi;
        }
        if (s1 - s0 <= StationTolerance || s3 - s2 <= StationTolerance)
            throw new InvalidOperationException("O trecho rebaixado encosta na ponta do meio-fio e não sobra lugar para a rampa.");

        double depthIn = startEffectiveHeight - finalHeight;
        double depthOut = endEffectiveHeight - finalHeight;

        // Altura normal de cada pedaco: a lida nele (referencia de antes ou de depois) ou a nominal da peca.
        // Regions vizinhas podem ter meio-fio de altura diferente (16 x 25 cm): cada pedaco rebaixa a partir
        // da sua (senao sobra degrau na divisa; teste de 13/11/2026).
        double SegNormal(CurbSeg seg)
        {
            if (seg == startRefSeg)
                return startEffectiveHeight;
            if (seg == endRefSeg)
                return endEffectiveHeight;
            var sub = (Subassembly)tr.GetObject(seg.SubassemblyId, OpenMode.ForRead);
            return TryGetNominalFreeHeight(sub, out double nominal) ? nominal : startEffectiveHeight;
        }

        double ProfileFor(bool depth, double st, CurbSeg seg)
        {
            // Pedaco com as duas referencias (o caso de sempre) ou sem altura propria diferente: perfil inteiro.
            if (seg == startRefSeg && seg == endRefSeg)
                return ProfileAt(depth, st);
            double n = SegNormal(seg);
            double k = st <= s1
                ? Math.Clamp((st - s0) / Math.Max(s1 - s0, 1e-9), 0.0, 1.0)
                : st >= s2 ? Math.Clamp((s3 - st) / Math.Max(s3 - s2, 1e-9), 0.0, 1.0) : 1.0;
            return depth ? k * (n - finalHeight) : n + (k * (finalHeight - n));
        }

        double ProfileAt(bool depth, double st)
        {
            if (st <= s1)
            {
                double k = Math.Clamp((st - s0) / Math.Max(s1 - s0, 1e-9), 0.0, 1.0);
                return depth ? k * depthIn : startEffectiveHeight + (k * (finalHeight - startEffectiveHeight));
            }
            if (st >= s2)
            {
                double k = Math.Clamp((s3 - st) / Math.Max(s3 - s2, 1e-9), 0.0, 1.0);
                return depth ? k * depthOut : endEffectiveHeight + (k * (finalHeight - endEffectiveHeight));
            }
            double m = (st - s1) / Math.Max(s2 - s1, 1e-9);
            return depth ? depthIn + (m * (depthOut - depthIn)) : finalHeight;
        }

        // Transition Sets: um por pedaço da cadeia que o rebaixo toca.
        var groups = new Dictionary<string, (CivilCorridor Corridor, Baseline Baseline, List<CorridorTransitionSet> Sets)>();
        (CivilCorridor Corridor, Baseline Baseline, List<CorridorTransitionSet> Sets) Group(CurbSeg seg)
        {
            string key = seg.Corridor.ObjectId.Handle + "|" + GetBaselineKey(seg.Baseline);
            if (!groups.TryGetValue(key, out var g))
            {
                g = (seg.Corridor, seg.Baseline, seg.Baseline.getTransitions());
                groups[key] = g;
            }
            return g;
        }

        List<CorridorTransitionSet> mainSets = Group(chain.First(x => x.Main)).Sets;
        string setName = MakeUniquePairSetName(mainSets, s1, s2, side);
        bool mainDepth = HasDoubleParameter(subassembly, DepthParameterName);
        var touched = chain.Where(x => Math.Min(s3, x.OurTo) - Math.Max(s0, x.OurFrom) > StationTolerance).ToList();
        CurbSeg main = chain.First(x => x.Main);
        bool standard = touched.Count == 1 && touched[0] == main;
        int addedSections = 0, pieces = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CurbSeg seg in touched)
        {
            double from = Math.Max(s0, seg.OurFrom), to = Math.Min(s3, seg.OurTo);
            var cuts = new List<double> { from, to };
            foreach (double b in new[] { s1, s2 })
            {
                if (b > from + StationTolerance && b < to - StationTolerance)
                    cuts.Add(b);
            }
            cuts.Sort();

            var segSub = (Subassembly)tr.GetObject(seg.SubassemblyId, OpenMode.ForRead);
            bool depth = HasDoubleParameter(segSub, DepthParameterName);
            var spans = new List<(double A, double Va, double B, double Vb)>();
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                double a = cuts[i], b = cuts[i + 1];
                double ta = seg.Their(a), tb = seg.Their(b), va = ProfileFor(depth, a, seg), vb = ProfileFor(depth, b, seg);
                spans.Add(ta <= tb ? (ta, va, tb, vb) : (tb, vb, ta, va));
            }
            spans.Sort((x, y) => x.A.CompareTo(y.A));

            (_, _, List<CorridorTransitionSet> sets) = Group(seg);
            string name;
            string setComment;
            if (seg.Main)
            {
                name = setName;
                setComment = standard
                    ? comment
                    : MFRebaixoData.WithToken(comment, MFRebaixoData.MakeToken(s0, s1, s2, s3,
                        mainDepth ? 0.0 : startEffectiveHeight, mainDepth ? depthIn : finalHeight,
                        mainDepth ? depthOut : finalHeight, mainDepth ? 0.0 : endEffectiveHeight));
            }
            else
            {
                string tag = (seg.OurTo <= main.OurFrom + StationTolerance ? "ENTRADA" : "SAIDA") + (seg.OtherBaseline ? "_BL" : "");
                name = setName + MFRebaixoData.PartSuffix + tag;
                for (int n = 2; used.Contains(name); n++)
                    name = setName + MFRebaixoData.PartSuffix + tag + n.ToString(CultureInfo.InvariantCulture);
                name = MakeUniqueName(sets, name);
                setComment = "REBAIXO MF - PARTE (" + setName + ")";
                pieces++;
            }
            used.Add(name);

            var set = new CorridorTransitionSet(name, seg.Region, seg.SubassemblyId, CorridorTransitionNameType.SubassemblyName)
            {
                Comment = setComment
            };
            for (int i = 0; i < spans.Count; i++)
            {
                CorridorTransition t = i == 0 ? set.AddTransition(depth ? DepthParameterName : ParameterName) : set.AddTransition();
                SetTransition(t, spans[i].A, spans[i].Va, spans[i].B, spans[i].Vb);
            }
            set.StationLocked = true;
            sets.Add(set);

            foreach (double c in cuts)
                addedSections += EnsureStation(seg.Region, seg.Their(c), "REBAIXO MF");

            if (!seg.Main)
            {
                notes.Add($"{(seg.OurTo <= main.OurFrom + StationTolerance ? "início" : "fim")} do rebaixo continua em " +
                          (seg.OtherBaseline ? $"outra Baseline ('{seg.Baseline.Name}', Region '{seg.Region.Name}')" : $"Region '{seg.Region.Name}'") +
                          $" ({to - from:0.##} m)");
            }
        }

        // DESTAQUE (PKTs antigos): so quando o trecho todo esta na Region principal.
        bool highlightApplied = false;
        if (HasDoubleParameter(subassembly, HighlightParameterName) && s1 >= main.OurFrom - StationTolerance && s2 <= main.OurTo + StationTolerance)
        {
            double hs = Math.Max(s0, main.OurFrom), he = Math.Min(s3, main.OurTo);
            var highlightSet = new CorridorTransitionSet(MakeUniqueName(mainSets, setName + "_DESTAQUE"), region, subassemblyId, CorridorTransitionNameType.SubassemblyName)
            {
                Comment = "REBAIXO MF - DESTAQUE"
            };
            CorridorTransition h1 = highlightSet.AddTransition(HighlightParameterName);
            SetTransition(h1, Math.Max(region.StartStation, hs - HighlightLead), 1.0, s1, 2.0);
            CorridorTransition h2 = highlightSet.AddTransition();
            SetTransition(h2, s1, 2.0, s2, 3.0);
            CorridorTransition h3 = highlightSet.AddTransition();
            SetTransition(h3, s2, 3.0, he, 4.0);
            highlightSet.StationLocked = true;
            mainSets.Add(highlightSet);
            highlightApplied = true;
        }

        foreach (var g in groups.Values)
            g.Baseline.SetTransitions(g.Sets);

        foreach (string note in notes)
        {
            PluginLog.Aviso("MFREBAIXO", note);
            Say(ed, "\n[MFREBAIXO] " + note + ".");
        }

        // Rebuild de cada corredor tocado (a Baseline vizinha pode ser de outro corredor).
        foreach (CivilCorridor c in groups.Values.Select(g => g.Corridor).Distinct())
        {
            CivilCorridor w = c.IsWriteEnabled ? c : (CivilCorridor)tr.GetObject(c.ObjectId, OpenMode.ForWrite);
            w.Rebuild();
        }

        ObjectId corridorIdForHatch = corridor.ObjectId;
        string baselineKeyForHatch = GetBaselineKey(baseline);
        Database hatchDb = corridor.Database;
        tr.Commit();

        int hatches = 0;
        try
        {
            hatches = MFRebaixoHatch.Draw(hatchDb, corridorIdForHatch, baselineKeyForHatch, subassemblyId, setName, s0, s1, s2, s3);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("MFREBAIXO", $"Hachura de {setName} não desenhada: {ex.Message}", ex);
            Say(ed, $"\n[AVISO] Hachura em planta não desenhada: {ex.Message}");
        }

        Say(ed, "\n[OK] REBAIXO MF");
        Say(ed, $"\n     Baseline: {baseline.Name} | Region: {region.Name}" + (pieces > 0 ? $" (+{pieces} pedaço(s) nas vizinhas)" : ""));
        Say(ed, $"\n     Lado: {GetSideLabel(side)} ({sideOrigin})");
        Say(ed, $"\n     Subassembly: {subassemblyInstanceName}");
        Say(ed, $"\n     Altura livre nominal={normalHeight:0.###} m");
        Say(ed, $"\n     Entrada efetiva={startEffectiveHeight:0.###} m ({startEffectiveSource}, ref. {referenceStartStation:0.000}) | redução={startEffectiveHeight - finalHeight:0.###} m | final={finalHeight:0.###} m");
        Say(ed, $"\n     Saída efetiva={endEffectiveHeight:0.###} m ({endEffectiveSource}, ref. {referenceEndStation:0.000})");
        Say(ed, $"\n     Declividade das rampas={slopeEntry * 100.0:0.###}%" + (Math.Abs(slopeExit - slopeEntry) > 1e-6 ? $" / {slopeExit * 100.0:0.###}%" : "") +
                $" | L entrada={s1 - s0:0.###} m | L saída={s3 - s2:0.###} m");
        Say(ed, $"\n     Rampa entrada: {s0:0.000} -> {s1:0.000}");
        Say(ed, $"\n     Trecho rebaixado: {s1:0.000} -> {s2:0.000}");
        Say(ed, $"\n     Rampa saída: {s2:0.000} -> {s3:0.000}");
        Say(ed, highlightApplied ? $"\n     Code REBAIXO: {s0:0.000} -> {s3:0.000}" : $"\n     {HighlightParameterName}: sem codes de destaque");
        Say(ed, mainDepth ? $"\n     Transition em {DepthParameterName} (profundidade)" : $"\n     Transition em {ParameterName} (altura livre)");
        Say(ed, $"\n     Corridor Sections adicionadas: {addedSections}");
        Say(ed, hatches > 0
            ? $"\n     Hachura em planta: {hatches} HATCH na layer {MFRebaixoSettingsStore.Load().HachuraLayer}\n"
            : "\n     Hachura em planta: não desenhada (ver C3DLOG)\n");
    }

    private static void Say(Editor ed, string text)
    {
        try
        {
            ed.WriteMessage(text);
        }
        catch
        {
            // Sem linha de comando (console): fica so no log.
        }
    }

    /// <summary>
    /// Monta a cadeia em volta da Region do rebaixo: Regions coladas da mesma Baseline com a peça MF do
    /// mesmo lado e, onde a Baseline do rebaixo acaba, a Baseline de meio-fio que encosta na ponta.
    /// </summary>
    private static List<CurbSeg> BuildChain(
        Transaction tr,
        CivilCorridor corridor,
        Baseline baseline,
        BaselineRegion region,
        SubassemblySideType side,
        ObjectId subassemblyId,
        string subassemblyName,
        double lo,
        double hi,
        List<string> notes)
    {
        var main = new CurbSeg
        {
            Corridor = corridor,
            Baseline = baseline,
            Region = region,
            SubassemblyId = subassemblyId,
            SubassemblyName = subassemblyName,
            Side = side,
            Main = true,
            OurFrom = region.StartStation,
            OurTo = region.EndStation
        };
        var chain = new List<CurbSeg> { main };
        foreach (bool backward in new[] { true, false })
        {
            bool jumped = false;
            for (int guard = 0; guard < 12; guard++)
            {
                CurbSeg tip = backward ? chain[0] : chain[^1];
                if (backward ? tip.OurFrom <= lo : tip.OurTo >= hi)
                    break;
                CurbSeg? next = NextOnBaseline(tr, tip, backward);
                if (next is null && !jumped && !tip.OtherBaseline)
                {
                    next = OtherBaselineAt(tr, tip, backward);
                    jumped = next is not null;
                }
                if (next is null)
                    break;
                if (backward)
                    chain.Insert(0, next);
                else
                    chain.Add(next);
            }
        }
        return chain;
    }

    /// <summary>Region colada na mesma Baseline do pedaço, com a peça MF do mesmo lado.</summary>
    private static CurbSeg? NextOnBaseline(Transaction tr, CurbSeg seg, bool backwardInOurs)
    {
        bool beforeTheirs = backwardInOurs == (seg.K > 0);
        BaselineRegion? neighbor = NeighborRegion(seg.Baseline, seg.Region, beforeTheirs);
        if (neighbor is null)
            return null;
        ObjectId sa = FindMfSubassemblyId(tr, neighbor, seg.Side, out string name);
        if (sa.IsNull)
            return null;
        double a = seg.Our(neighbor.StartStation), b = seg.Our(neighbor.EndStation);
        var next = new CurbSeg
        {
            Corridor = seg.Corridor,
            Baseline = seg.Baseline,
            Region = neighbor,
            SubassemblyId = sa,
            SubassemblyName = name,
            Side = seg.Side,
            OtherBaseline = seg.OtherBaseline,
            A = seg.A,
            K = seg.K,
            OurFrom = backwardInOurs ? Math.Min(a, b) : seg.OurTo,
            OurTo = backwardInOurs ? seg.OurFrom : Math.Max(a, b)
        };
        return next.OurTo - next.OurFrom > StationTolerance ? next : null;
    }

    /// <summary>
    /// Baseline de outro trecho (mesmo corredor ou outro) cujo meio-fio encosta na ponta do pedaço (até
    /// 2 m) e segue para fora no mesmo sentido (até ~30 graus): a continuação do meio-fio. O retorno da
    /// esquina, que sai de lado, não entra.
    /// </summary>
    private static CurbSeg? OtherBaselineAt(Transaction tr, CurbSeg seg, bool backward)
    {
        double b = backward ? seg.OurFrom : seg.OurTo;
        List<(double Station, Point3d Inner, Point3d Outer)> own = MFRebaixoHatch.Edges(tr, seg.Baseline, seg.SubassemblyName,
            backward ? b : b - 2.0, backward ? b + 2.0 : b, false, null, seg.Side == SubassemblySideType.Left);
        if (own.Count < 2)
            return null;
        var tipE = backward ? own[0] : own[^1];
        var innerE = backward ? own[^1] : own[0];
        Vector3d outward = new(tipE.Inner.X - innerE.Inner.X, tipE.Inner.Y - innerE.Inner.Y, 0);
        if (outward.Length < 1e-6)
            return null;
        outward = outward.GetNormal();
        Point3d tip = tipE.Inner;
        string ownKey = seg.Corridor.ObjectId.Handle + "|" + GetBaselineKey(seg.Baseline);

        CurbSeg? best = null;
        double bestD = 2.0;
        foreach (ObjectId cid in MFRebaixoData.CorridorIds(seg.Corridor.Database, tr))
        {
            if (tr.GetObject(cid, OpenMode.ForRead) is not CivilCorridor c)
                continue;
            foreach (Baseline b2 in c.Baselines)
            {
                if (c.ObjectId.Handle + "|" + GetBaselineKey(b2) == ownKey)
                    continue;
                if (!TryGetBaselineStationOffset(tr, b2, tip, out double t2, out double off2) || Math.Abs(off2) > 60)
                    continue;
                BaselineRegion? r2 = null;
                foreach (BaselineRegion candidate in b2.BaselineRegions)
                {
                    if (t2 >= candidate.StartStation - 0.05 && t2 <= candidate.EndStation + 0.05)
                    {
                        r2 = candidate;
                        break;
                    }
                }
                if (r2 is null)
                    continue;
                ObjectId sa2;
                SubassemblySideType side2;
                string name2;
                try
                {
                    sa2 = ResolveMfSubassembly(tr, r2, SideSelectionMode.Auto, off2, out side2, out name2);
                }
                catch
                {
                    continue;
                }
                List<(double Station, Point3d Inner, Point3d Outer)> e2 = MFRebaixoHatch.Edges(tr, b2, name2, t2 - 3.0, t2 + 3.0, false, null, side2 == SubassemblySideType.Left);
                if (e2.Count < 2)
                    continue;
                int k = Enumerable.Range(0, e2.Count).OrderBy(i => Dist2(e2[i].Inner, tip)).First();
                double d = Dist2(e2[k].Inner, tip);
                if (d >= bestD)
                    continue;
                int k2 = k + 1 < e2.Count ? k + 1 : k - 1;
                Vector3d up = new(e2[Math.Max(k, k2)].Inner.X - e2[Math.Min(k, k2)].Inner.X, e2[Math.Max(k, k2)].Inner.Y - e2[Math.Min(k, k2)].Inner.Y, 0);
                if (up.Length < 1e-6)
                    continue;
                double dot = up.GetNormal().DotProduct(outward);
                if (Math.Abs(dot) < 0.85)
                    continue; // vira de lado (retorno da esquina): nao e a continuacao

                // Estaca deles = A + K * nossa, com a ponta (b) na estaca t2 deles.
                bool theirsGoOut = dot > 0;
                int kk = backward ? (theirsGoOut ? -1 : 1) : (theirsGoOut ? 1 : -1);
                double aa = t2 - (kk * b);
                var next = new CurbSeg
                {
                    Corridor = c,
                    Baseline = b2,
                    Region = r2,
                    SubassemblyId = sa2,
                    SubassemblyName = name2,
                    Side = side2,
                    OtherBaseline = true,
                    A = aa,
                    K = kk
                };
                double ra = next.Our(r2.StartStation), rb = next.Our(r2.EndStation);
                next.OurFrom = backward ? Math.Min(ra, rb) : b;
                next.OurTo = backward ? b : Math.Max(ra, rb);
                if (next.OurTo - next.OurFrom <= StationTolerance)
                    continue;
                bestD = d;
                best = next;
            }
        }
        if (best is not null)
            PluginLog.Info("MFREBAIXO", $"Meio-fio continua na Baseline '{best.Baseline.Name}' (Region '{best.Region.Name}', {best.SubassemblyName}) a {bestD:0.000} m da ponta {(backward ? "de trás" : "da frente")} ({b:0.000}).");
        return best;
    }

    private static double Dist2(Point3d a, Point3d b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>Altura livre efetiva numa seção existente antes (ou depois) da estaca, em qualquer pedaço da cadeia.</summary>
    private static bool TryReferenceHeight(List<CurbSeg> chain, double station, bool before, out double height, out string source, out double ourStation, out CurbSeg? refSeg)
    {
        refSeg = null;
        height = 0;
        source = "";
        ourStation = station;
        var candidates = new List<(double Our, CurbSeg Seg, double Their)>();
        foreach (CurbSeg seg in chain)
        {
            foreach (double t in GetExistingStations(seg.Region))
            {
                double our = seg.Our(t);
                if (our < seg.OurFrom - 0.001 || our > seg.OurTo + 0.001)
                    continue;
                if (before ? our < station - 0.001 : our > station + 0.001)
                    candidates.Add((our, seg, t));
            }
        }
        foreach ((double our, CurbSeg seg, double t) in (before ? candidates.OrderByDescending(x => x.Our) : candidates.OrderBy(x => x.Our)).Take(4))
        {
            if (TryGetEffectiveFreeHeightAtStation(seg.Baseline, seg.Region, t, seg.SubassemblyId, out height, out source))
            {
                ourStation = our;
                refSeg = seg;
                if (!seg.Main)
                    source += seg.OtherBaseline ? $" (Baseline '{seg.Baseline.Name}', {t:0.000})" : $" (Region '{seg.Region.Name}')";
                return true;
            }
        }
        return false;
    }
}
