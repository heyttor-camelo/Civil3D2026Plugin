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
        var coreClock = System.Diagnostics.Stopwatch.StartNew();
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

        // Trechos ja rebaixados por outros rebaixos do mesmo lado nesta baseline: as secoes ali nao servem de
        // altura "normal" (senao a rampa some: "rampa de saida 0,00 m", visto no R19 do Heyvid, 13/11/2026).
        var busy = new List<(double From, double To)>();
        string sideTag = side == SubassemblySideType.Left ? "_ESQ_" : "_DIR_";
        foreach (CorridorTransitionSet other in baseline.getTransitions())
        {
            string on = other.Name ?? "";
            if (!on.StartsWith(MFRebaixoData.PairPrefix, StringComparison.OrdinalIgnoreCase) || !on.Contains(sideTag, StringComparison.OrdinalIgnoreCase) ||
                on.Contains(MFRebaixoData.HighlightSuffix, StringComparison.OrdinalIgnoreCase) || MFRebaixoData.IsAltSet(on) || other.TransitionCount == 0 || MFRebaixoData.IsNeutral(other))
                continue;
            double a = double.MaxValue, z = double.MinValue;
            for (int i = 0; i < other.TransitionCount; i++)
            {
                var t = other.GetTransitionAt(i);
                a = Math.Min(a, Math.Min(t.StartStation, t.EndStation));
                z = Math.Max(z, Math.Max(t.StartStation, t.EndStation));
            }
            busy.Add((a + 0.001, z - 0.001));
        }

        // Altura livre normal: seção existente antes do trecho e depois dele, em qualquer pedaço da cadeia.
        if (!TryReferenceHeight(chain, busy, s1, before: true, out double startEffectiveHeight, out string startEffectiveSource, out double referenceStartStation, out CurbSeg? startRefSeg))
            throw new InvalidOperationException("Não existe Corridor Section antes do trecho rebaixado (nesta Region ou nas vizinhas) para ler a altura normal efetiva.");
        if (expectedStart.HasValue && Math.Abs(startEffectiveHeight - expectedStart.Value) > 1e-4)
            throw new InvalidOperationException("A altura livre efetiva da seção anterior mudou entre os cliques. Reinicie este par para recalcular o rebaixo.");
        if (!TryReferenceHeight(chain, busy, s2, before: false, out double endEffectiveHeight, out string endEffectiveSource, out double referenceEndStation, out CurbSeg? endRefSeg))
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
        // A prumo (2.121.0): sem rampa desse lado, transicao de PlumbLength; o aviso da NBR 9050 nao vale
        // (escolha consciente), a ficha do painel diz que o lado nao e acessivel.
        bool plumbIn = IsPlumb(slopeEntry), plumbOut = IsPlumb(slopeExit);
        double rampSlopesMax = Math.Max(plumbIn ? 0 : slopeEntry, plumbOut ? 0 : slopeExit);
        if (rampSlopesMax > MaxRampSlopeNbr + 1e-5)
        {
            string aviso = $"Declividade da rampa do meio-fio de {rampSlopesMax * 100.0:0.##}% (acima de 8,33%, NBR 9050).";
            PluginLog.Aviso("MFREBAIXO", aviso);
            Say(ed, "\n[MFREBAIXO] Aviso: " + aviso);
        }

        double entryRampLength = plumbIn ? PlumbLength : Math.Abs(startEffectiveHeight - finalHeight) / slopeEntry;
        double exitRampLength = plumbOut ? PlumbLength : Math.Abs(endEffectiveHeight - finalHeight) / slopeExit;
        // Ponta onde o meio-fio ja esta na altura final (encostado em outro rebaixo, ou a altura pedida e a
        // normal): antes dava erro; agora a rampa desse lado fica curta (MinRampLength) e quase nao desce.
        // Pedido do Heyvid em 17/11/2026 ("o que esse erro quer dizer?").
        if (!plumbIn && entryRampLength < MinRampLength)
        {
            notes.Add($"rampa de entrada de {MinRampLength:0.00} m: a altura normal antes do trecho ({startEffectiveHeight * 100.0:0.#} cm) já é praticamente a altura final");
            entryRampLength = MinRampLength;
        }
        if (!plumbOut && exitRampLength < MinRampLength)
        {
            notes.Add($"rampa de saída de {MinRampLength:0.00} m: a altura normal depois do trecho ({endEffectiveHeight * 100.0:0.#} cm) já é praticamente a altura final");
            exitRampLength = MinRampLength;
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
        // Outro rebaixo do mesmo lado colado: as rampas nao podem entrar nele (dois Transition Sets no mesmo
        // parametro e trecho brigam e o vizinho deixava de sair na altura dele: teste de 17/11/2026, rebaixo a 0
        // encostado noutro a 0 ficava com 0,8 a 18,8 cm). A rampa e cortada na ponta do vizinho (fica mais
        // inclinada, com aviso); sem lugar nem para a rampa minima, erro dizendo o que fazer.
        bool shortened = false;
        foreach ((double bf, double bt) in busy)
        {
            if (bf < s2 - StationTolerance && bt > s1 + StationTolerance)
            {
                throw new InvalidOperationException(
                    $"O trecho rebaixado ({s1:0.000} - {s2:0.000}) cai dentro de outro rebaixo deste lado ({bf:0.000} - {bt:0.000}). " +
                    "Para juntar os dois, edite o comprimento do que ja existe; ou mude este trecho de lugar.");
            }
            if (bt > s0 + 0.005 && bt <= s1 + StationTolerance)
            {
                if (bt > s1 - MinRampLength)
                {
                    throw new InvalidOperationException(
                        $"O inicio do trecho ({s1:0.000}) encosta na rampa de outro rebaixo deste lado (que vai ate {bt:0.000}) e nao sobra lugar para a rampa. " +
                        $"Comece o trecho depois de {bt + MinRampLength:0.00} ou, para juntar os dois, edite o comprimento do rebaixo que ja existe.");
                }
                notes.Add($"rampa de entrada encurtada para {s1 - bt:0.##} m (era {s1 - s0:0.##} m) para nao entrar no rebaixo vizinho (ate {bt:0.000}); a rampa fica mais inclinada");
                s0 = bt;
                shortened = true;
            }
            if (bf < s3 - 0.005 && bf >= s2 - StationTolerance)
            {
                if (bf < s2 + MinRampLength)
                {
                    throw new InvalidOperationException(
                        $"O fim do trecho ({s2:0.000}) encosta na rampa de outro rebaixo deste lado (que comeca em {bf:0.000}) e nao sobra lugar para a rampa. " +
                        $"Termine o trecho antes de {bf - MinRampLength:0.00} ou, para juntar os dois, edite o comprimento do rebaixo que ja existe.");
                }
                notes.Add($"rampa de saida encurtada para {bf - s2:0.##} m (era {s3 - s2:0.##} m) para nao entrar no rebaixo vizinho (a partir de {bf:0.000}); a rampa fica mais inclinada");
                s3 = bf;
                shortened = true;
            }
        }
        double realIn = Math.Abs(startEffectiveHeight - finalHeight) / Math.Max(s1 - s0, 1e-9);
        double realOut = Math.Abs(endEffectiveHeight - finalHeight) / Math.Max(s3 - s2, 1e-9);
        double realMax = Math.Max(plumbIn ? 0 : realIn, plumbOut ? 0 : realOut);
        if (shortened && realMax > MaxRampSlopeNbr + 0.0005)
        {
            string aviso = $"Rampa do meio-fio com {realMax * 100.0:0.##}% depois de encurtada (acima de 8,33%, NBR 9050).";
            PluginLog.Aviso("MFREBAIXO", aviso);
            Say(ed, "\n[MFREBAIXO] Aviso: " + aviso);
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
        bool mainDirect = IsDirect(subassembly);
        bool mainDepth = mainDirect || HasDoubleParameter(subassembly, DepthParameterName);

        // Peso da altura final (REB_K) ao longo do rebaixo: 0 em S0, 1 de S1 a S2, 0 em S3.
        double WeightAt(double st) =>
            st <= s1 ? Math.Clamp((st - s0) / Math.Max(s1 - s0, 1e-9), 0.0, 1.0)
            : st >= s2 ? Math.Clamp((s3 - st) / Math.Max(s3 - s2, 1e-9), 0.0, 1.0)
            : 1.0;
        var written = new List<WrittenPart>();
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
            bool direct = IsDirect(segSub);
            bool depth = direct || HasDoubleParameter(segSub, DepthParameterName);
            string param = direct ? FinalWeightParameterName : depth ? DepthParameterName : ParameterName;
            double ValueAt(double st) => direct ? WeightAt(st) : ProfileFor(depth, st, seg);
            var spans = new List<(double A, double Va, double B, double Vb)>();
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                double a = cuts[i], b = cuts[i + 1];
                double ta = seg.Their(a), tb = seg.Their(b), va = ValueAt(a), vb = ValueAt(b);
                spans.Add(ta <= tb ? (ta, va, tb, vb) : (tb, vb, ta, va));
            }
            spans.Sort((x, y) => x.A.CompareTo(y.A));

            (_, _, List<CorridorTransitionSet> sets) = Group(seg);
            string name;
            string setComment;
            if (seg.Main)
            {
                name = setName;
                // Desde a 2.120.0 o set principal sempre leva o token, com a altura final pedida (F=):
                // o REB acertado secao por secao e o REB_K tem mais (ou outros) valores que os 3 trechos.
                setComment = MFRebaixoData.WithToken(comment, MFRebaixoData.MakeToken(s0, s1, s2, s3,
                        mainDepth ? 0.0 : startEffectiveHeight, mainDepth ? depthIn : finalHeight,
                        mainDepth ? depthOut : finalHeight, mainDepth ? 0.0 : endEffectiveHeight, finalHeight, plumbIn, plumbOut));
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
                CorridorTransition t = i == 0 ? AddFirst(set, param, segSub) : set.AddTransition();
                SetTransition(t, spans[i].A, spans[i].Va, spans[i].B, spans[i].Vb);
            }
            set.StationLocked = true;
            sets.Add(set);
            if (direct)
            {
                // ALT_FINAL constante no pedaco inteiro (o REB_K decide onde vale).
                var alt = new CorridorTransitionSet(MakeUniqueName(sets, name + MFRebaixoData.AltSuffix), seg.Region, seg.SubassemblyId, CorridorTransitionNameType.SubassemblyName)
                {
                    Comment = "REBAIXO MF - ALTURA FINAL (" + setName + ")"
                };
                CorridorTransition ta = AddFirst(alt, FinalHeightParameterName, segSub);
                SetTransition(ta, spans[0].A, finalHeight, spans[^1].B, finalHeight);
                alt.StationLocked = true;
                sets.Add(alt);
            }
            written.Add(new WrittenPart(seg, set, name, setComment, direct, depth, param, from, to, cuts));

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
            CorridorTransition h1 = AddFirst(highlightSet, HighlightParameterName, subassembly);
            SetTransition(h1, Math.Max(region.StartStation, hs - HighlightLead), 1.0, s1, 2.0);
            CorridorTransition h2 = highlightSet.AddTransition();
            SetTransition(h2, s1, 2.0, s2, 3.0);
            CorridorTransition h3 = highlightSet.AddTransition();
            SetTransition(h3, s2, 3.0, he, 4.0);
            highlightSet.StationLocked = true;
            mainSets.Add(highlightSet);
            highlightApplied = true;
        }

        long beforeSetMs = coreClock.ElapsedMilliseconds;
        foreach (var g in groups.Values)
            g.Baseline.SetTransitions(g.Sets);
        long setMs = coreClock.ElapsedMilliseconds - beforeSetMs;

        foreach (string note in notes)
        {
            PluginLog.Aviso("MFREBAIXO", note);
            Say(ed, "\n[MFREBAIXO] " + note + ".");
        }

        // Modo manual (2.126.0): so as transicoes e as secoes; Rebuild, acerto da altura final e hachura ficam para o
        // MFREBAIXORECONSTRUIR. O rebaixo entra na lista de pendentes do desenho (mesma transacao: o U tira tambem).
        if (DeferRebuild)
        {
            MFRebaixoPending.Add(tr, corridor.Database, setName, groups.Values.Select(g => g.Corridor.ObjectId));
            tr.Commit();
            int pendN = MFRebaixoPending.Count(corridor.Database);
            Say(ed, $"\n[OK] REBAIXO MF lancado SEM Rebuild (modo manual): {setName}, {s1:0.000} -> {s2:0.000}, altura final pedida {finalHeight:0.###} m" +
                    $" | rampas {s0:0.000} -> {s1:0.000} e {s2:0.000} -> {s3:0.000} | secoes adicionadas: {addedSections}.");
            Say(ed, $"\n     Altura normal das pontas lida da ultima reconstrucao: antes {startEffectiveHeight:0.###} m ({startEffectiveSource}), depois {endEffectiveHeight:0.###} m ({endEffectiveSource}).");
            Say(ed, $"\n     Pendentes no desenho: {pendN}. O corredor so mostra o rebaixo depois do MFREBAIXORECONSTRUIR (ou do botao Reconstruir do painel).\n");
            PluginLog.Info("MFREBAIXO", $"{setName}: lancado sem Rebuild (modo manual); {pendN} pendente(s); {coreClock.ElapsedMilliseconds} ms (antes de gravar {beforeSetMs} ms, SetTransitions {setMs} ms).");
            return;
        }

        // Rebuild de cada corredor tocado (a Baseline vizinha pode ser de outro corredor).
        void RebuildAll()
        {
            foreach (CivilCorridor c in groups.Values.Select(g => g.Corridor).Distinct())
            {
                CivilCorridor w = c.IsWriteEnabled ? c : (CivilCorridor)tr.GetObject(c.ObjectId, OpenMode.ForWrite);
                w.Rebuild();
            }
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        RebuildAll();
        long firstRebuildMs = clock.ElapsedMilliseconds;

        // Peca sem REB_K/ALT_FINAL (MFSARJETA ate 1.26): o REB foi calculado com a altura normal lida
        // antes de S0, mas a normal varia no trecho (alvos de topo e de flowline). Acerta o REB em cada
        // secao do trecho rebaixado para o exposto ficar igual ao pedido e faz UM Rebuild a mais
        // (pedido do Heyvid em 17/11/2026: "mesmo colocando altura 0 ... ainda fica com uma altura final").
        int adjusted = 0;
        List<WrittenPart> toFix = written.Where(w => !w.Direct && w.Depth).ToList();
        if (toFix.Count > 0)
        {
            foreach (WrittenPart w in toFix)
                adjusted += AdjustDepthBySection(w, s0, s1, s2, s3, finalHeight, (st, seg) => ProfileFor(true, st, seg), Group(w.Seg).Sets, setName);
            if (adjusted > 0)
            {
                foreach (var g in groups.Values)
                    g.Baseline.SetTransitions(g.Sets);
                RebuildAll();
            }
        }
        long totalRebuildMs = clock.ElapsedMilliseconds;

        // Altura obtida de S1 a S2 (todas as secoes dos pedacos que o trecho rebaixado toca).
        double? gotMin = null, gotMax = null;
        foreach (WrittenPart w in written)
        {
            foreach ((double their, double exposed, _) in SectionValues(w.Seg.Region, w.Seg.SubassemblyId))
            {
                double our = w.Seg.Our(their);
                if (our < Math.Max(s1, w.From) - 0.0005 || our > Math.Min(s2, w.To) + 0.0005)
                    continue;
                gotMin = Math.Min(gotMin ?? exposed, exposed);
                gotMax = Math.Max(gotMax ?? exposed, exposed);
            }
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
        Say(ed, $"\n     Rampas: entrada {(plumbIn ? "a prumo" : $"{slopeEntry * 100.0:0.###}%")}, saida {(plumbOut ? "a prumo" : $"{slopeExit * 100.0:0.###}%")}" +
                $" | L entrada={s1 - s0:0.###} m | L saída={s3 - s2:0.###} m");
        Say(ed, $"\n     Rampa entrada: {s0:0.000} -> {s1:0.000}");
        Say(ed, $"\n     Trecho rebaixado: {s1:0.000} -> {s2:0.000}");
        Say(ed, $"\n     Rampa saída: {s2:0.000} -> {s3:0.000}");
        Say(ed, highlightApplied ? $"\n     Code REBAIXO: {s0:0.000} -> {s3:0.000}" : $"\n     {HighlightParameterName}: sem codes de destaque");
        Say(ed, mainDepth ? $"\n     Transition em {DepthParameterName} (profundidade)" : $"\n     Transition em {ParameterName} (altura livre)");
        Say(ed, $"\n     Corridor Sections adicionadas: {addedSections}");
        string how = written.Any(w => w.Direct)
            ? "altura final direta (MFSARJETA 1.27+: REB_K + ALT_FINAL)"
            : adjusted > 0 ? $"REB acertado em {adjusted} secao(oes) do trecho (1 Rebuild a mais)" : "REB";
        string got = gotMin is double gmin && gotMax is double gmax
            ? Math.Abs(gmax - gmin) < 0.0005 ? $"{gmin:0.###} m" : $"{gmin:0.###} a {gmax:0.###} m"
            : "nao lida";
        Say(ed, $"\n     Altura final: pedida {finalHeight:0.###} m, obtida {got} ({how}; Rebuild {totalRebuildMs / 1000.0:0.0} s, o 1o {firstRebuildMs / 1000.0:0.0} s)");
        PluginLog.Info("MFREBAIXO", $"{setName}: altura final pedida {finalHeight:0.###} obtida {got}; {how}; rebuild {firstRebuildMs} ms + {totalRebuildMs - firstRebuildMs} ms.");
        if (gotMin is double lo2 && gotMax is double hi2 && Math.Max(Math.Abs(lo2 - finalHeight), Math.Abs(hi2 - finalHeight)) > FinalHeightTolerance)
        {
            string aviso = $"{setName}: altura final obtida ({got}) difere da pedida ({finalHeight:0.###} m) mais de 2 mm. " +
                           "Confira no painel (aba Secoes); com MFSARJETA 1.27 ou mais novo a altura sai exata.";
            PluginLog.Aviso("MFREBAIXO", aviso);
            Say(ed, "\n[MFREBAIXO] Aviso: " + aviso);
        }
        Say(ed, hatches > 0
            ? $"\n     Hachura em planta: {hatches} HATCH na layer {MFRebaixoSettingsStore.Load().HachuraLayer}\n"
            : "\n     Hachura em planta: não desenhada (ver C3DLOG)\n");
    }

    /// <summary>Set gravado num pedaco da cadeia (para o acerto secao por secao depois do Rebuild).</summary>
    private sealed record WrittenPart(CurbSeg Seg, CorridorTransitionSet Set, string Name, string Comment, bool Direct, bool Depth,
        string Parameter, double From, double To, List<double> Cuts);

    /// <summary>A peca tem REB_K e ALT_FINAL (MFSARJETA 1.27+) e o caminho direto esta ligado.</summary>
    internal static bool IsDirect(Subassembly sa) =>
        DirectFinalHeightEnabled && HasDoubleParameter(sa, FinalWeightParameterName) && HasDoubleParameter(sa, FinalHeightParameterName);

    /// <summary>Exposto (EXPOSTO) e REB aplicados em cada secao calculada da Region, so da peca indicada.</summary>
    internal static List<(double Station, double Exposed, double Reb)> SectionValues(BaselineRegion region, ObjectId subassemblyId)
    {
        var list = new List<(double, double, double)>();
        foreach (AppliedAssembly aa in region.AppliedAssemblies)
        {
            try
            {
                foreach (AppliedSubassembly asa in aa.GetAppliedSubassemblies())
                {
                    if (asa.SubassemblyId != subassemblyId)
                        continue;
                    double? exp = null, reb = null;
                    foreach (IAppliedSubassemblyParam prm in asa.Parameters)
                    {
                        if (prm.KeyName.Equals(EffectiveOutputAltParameterName, StringComparison.OrdinalIgnoreCase))
                            exp = Convert.ToDouble(prm.ValueAsObject, CultureInfo.InvariantCulture);
                        else if (prm.KeyName.Equals(DepthParameterName, StringComparison.OrdinalIgnoreCase))
                            reb = Convert.ToDouble(prm.ValueAsObject, CultureInfo.InvariantCulture);
                    }
                    if (exp is double e)
                        list.Add((asa.OriginStationOffsetElevationToBaseline.X, e, reb ?? 0.0));
                    break;
                }
            }
            catch
            {
                // Secao sem a peca ou sem valores.
            }
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    /// <summary>
    /// REB acertado secao por secao no trecho rebaixado (peca sem REB_K): em cada secao, REB novo =
    /// exposto obtido + REB aplicado - altura pedida (o exposto e normal - REB). As rampas ficam como
    /// estao, so a ponta delas em S1/S2 passa a ser o valor acertado. Devolve quantas secoes mudaram.
    /// </summary>
    private static int AdjustDepthBySection(WrittenPart w, double s0, double s1, double s2, double s3, double finalHeight,
        Func<double, CurbSeg, double> profile, List<CorridorTransitionSet> sets, string setName)
    {
        CurbSeg seg = w.Seg;
        double lo = Math.Max(s1, w.From), hi = Math.Min(s2, w.To);
        if (hi - lo <= StationTolerance)
            return 0;
        var fixedAt = new SortedDictionary<double, double>();
        int changed = 0, skipped = 0;
        foreach ((double their, double exposed, double reb) in SectionValues(seg.Region, seg.SubassemblyId))
        {
            double our = seg.Our(their);
            if (our < lo - 0.0005 || our > hi + 0.0005)
                continue;
            double value = reb;
            double miss = exposed - finalHeight;
            if (Math.Abs(miss) > 0.0002)
            {
                // Exposto 0 com altura pedida > 0: a peca cortou o REB na altura normal (que fica
                // desconhecida); tira so a diferenca, o aviso de min/max mostra se nao bastou.
                double candidate = exposed < 0.0005 && finalHeight > 0.0005 ? reb - finalHeight : reb + miss;
                if (Math.Abs(candidate - reb) <= 0.15)
                {
                    value = candidate;
                    changed++;
                }
                else
                {
                    skipped++;
                }
            }
            fixedAt[Math.Clamp(our, lo, hi)] = value;
        }
        if (skipped > 0)
        {
            PluginLog.Aviso("MFREBAIXO", $"{setName}: {skipped} secao(oes) do trecho nao responderam ao REB (meio-fio Kassel, ou meio-fio levantado com MFSARJETA anterior a 1.25) e ficaram como estavam.");
        }
        if (changed == 0 || fixedAt.Count == 0)
            return 0;

        double FixedValue(double our)
        {
            // Interpolado entre as secoes acertadas (nas pontas, a mais perto).
            KeyValuePair<double, double>? before = null, after = null;
            foreach (var kv in fixedAt)
            {
                if (kv.Key <= our + 1e-6)
                    before = kv;
                if (kv.Key >= our - 1e-6)
                {
                    after = kv;
                    break;
                }
            }
            if (before is null)
                return after!.Value.Value;
            if (after is null || after.Value.Key - before.Value.Key < 1e-6)
                return before.Value.Value;
            double k = (our - before.Value.Key) / (after.Value.Key - before.Value.Key);
            return before.Value.Value + (k * (after.Value.Value - before.Value.Value));
        }

        var cuts = new SortedSet<double>(w.Cuts);
        foreach (double st in fixedAt.Keys)
        {
            if (st > w.From + 0.001 && st < w.To - 0.001 && !cuts.Any(c => Math.Abs(c - st) < 0.001))
                cuts.Add(st);
        }
        List<double> list = cuts.ToList();
        double ValueAt(double our) => our >= lo - 1e-6 && our <= hi + 1e-6 ? FixedValue(our) : profile(our, seg);

        var spans = new List<(double A, double Va, double B, double Vb)>();
        for (int i = 0; i + 1 < list.Count; i++)
        {
            double a = list[i], b = list[i + 1];
            double ta = seg.Their(a), tb = seg.Their(b), va = ValueAt(a), vb = ValueAt(b);
            spans.Add(ta <= tb ? (ta, va, tb, vb) : (tb, vb, ta, va));
        }
        spans.Sort((x, y) => x.A.CompareTo(y.A));

        var set = new CorridorTransitionSet(w.Name, seg.Region, seg.SubassemblyId, CorridorTransitionNameType.SubassemblyName)
        {
            Comment = w.Comment
        };
        for (int i = 0; i < spans.Count; i++)
        {
            CorridorTransition t = i == 0 ? AddFirst(set, w.Parameter, (Subassembly)w.Seg.SubassemblyId.GetObject(OpenMode.ForRead)) : set.AddTransition();
            SetTransition(t, spans[i].A, spans[i].Va, spans[i].B, spans[i].Vb);
        }
        set.StationLocked = true;
        int idx = sets.IndexOf(w.Set);
        if (idx >= 0)
            sets[idx] = set;
        else
            sets.Add(set);
        return changed;
    }

    /// <summary>Primeira Transition do set; se o Civil recusar o parametro, a mensagem diz qual e em que peca.</summary>
    private static CorridorTransition AddFirst(CorridorTransitionSet set, string parameter, Subassembly subassembly)
    {
        try
        {
            return set.AddTransition(parameter);
        }
        catch (System.Exception ex)
        {
            // Peca SAC (PKT) cujo arquivo sumiu: o Civil nao consegue validar os parametros dela (KM_01 do Heyvid,
            // 17/11/2026: MFSARJETA_v1.24.pkt apagado da pasta do projeto).
            string pkt = "";
            try
            {
                pkt = subassembly.GeometryGenerator?.ProjectOrAssemblyName ?? "";
            }
            catch
            {
                // Sem gerador legivel.
            }
            if (pkt.EndsWith(".pkt", StringComparison.OrdinalIgnoreCase) && !System.IO.File.Exists(pkt))
            {
                throw new InvalidOperationException(
                    $"O arquivo da peca '{subassembly.Name}' nao existe mais: {pkt}. Sem ele o Civil 3D nao aceita gravar o rebaixo. " +
                    "Troque a peca para o MFSARJETA atual (Prospector > Subassemblies > a peca > Properties > Change Subassembly) ou recoloque o arquivo nesse caminho.", ex);
            }
            throw new InvalidOperationException($"O Civil 3D recusou o parametro '{parameter}' na peca '{subassembly.Name}' (set {set.Name}): {ex.Message}", ex);
        }
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
    private static bool TryReferenceHeight(List<CurbSeg> chain, List<(double From, double To)> busy, double station, bool before, out double height, out string source, out double ourStation, out CurbSeg? refSeg)
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
                if (seg.Main || (!seg.OtherBaseline))
                {
                    // Secao dentro de outro rebaixo desta baseline: pula.
                    if (busy.Any(x => our > x.From && our < x.To))
                        continue;
                }
                if (before ? our < station - 0.001 : our > station + 0.001)
                    candidates.Add((our, seg, t));
            }
        }
        foreach ((double our, CurbSeg seg, double t) in (before ? candidates.OrderByDescending(x => x.Our) : candidates.OrderBy(x => x.Our)).Take(8))
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
