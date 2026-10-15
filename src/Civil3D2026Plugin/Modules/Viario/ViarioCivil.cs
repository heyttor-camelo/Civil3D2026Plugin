using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Etapa 1 dos objetos do Civil: eixos, bordos LD/LE (alinhamentos offset por trecho)
/// e curb returns (alinhamento conectado; se falhar, alinhamento CurbReturn simples).
/// Tudo e editado no lugar quando possivel; o que mudou de forma e refeito.
/// </summary>
internal static class ViarioCivil
{
    public const string Tag = "CIVIL";
    public const string KindAxis = "EIXO";
    public const string KindEdge = "BORDO";
    public const string KindCurb = "CR";

    public const string SiteName = "VIARIO";
    public const string LayerAxis = "VIARIO-C3D-EIXO";
    public const string LayerEdge = "VIARIO-C3D-BORDO";
    public const string LayerCurb = "VIARIO-C3D-CR";

    private const double Tol = 0.005;
    private const double VerifyTol = 0.03;

    /// <summary>Trecho do curb return conectado sobre cada pai (o Civil exige > 0).</summary>
    private const double CurbOverlap = 0.5;

    private static readonly Dictionary<AcadDb.Database, bool> Active = new();

    internal sealed class Report
    {
        public int AxesCreated, AxesUpdated, EdgesCreated, EdgesKept, CurbsCreated, CurbsKept, Erased;
        public int CurbsConnected, CurbsSimple, CurbsFailed, EdgesFixed;
        public List<string> Messages { get; } = new();
        public string Styles { get; set; } = "";

        public override string ToString() =>
            $"Civil: eixos +{AxesCreated}/~{AxesUpdated} | bordos +{EdgesCreated} (mantidos {EdgesKept}, divergentes {EdgesFixed}) | " +
            $"curb returns +{CurbsCreated} (mantidos {CurbsKept}; conectados {CurbsConnected}, simples {CurbsSimple}, falhas {CurbsFailed}) | removidos {Erased}";
    }

    private sealed class Tagged
    {
        public AcadDb.ObjectId Id;
        public string Kind = "";
        public string Key = "";
        public int Side;
        public double S0;
        public double S1;
        public double Offset;
        public string Geom = "";
        public string Method = "";
        public bool Used;
    }

    // ------------------------------------------------------------------
    // Estado
    // ------------------------------------------------------------------

    /// <summary>Se o desenho ja tem objetos do Civil gerados pelo modulo (atualizacao automatica).</summary>
    public static bool IsActive(AcadDb.Database db)
    {
        if (Active.TryGetValue(db, out bool active))
            return active;

        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            active = Scan(tr, db).Count > 0;
        }
        catch
        {
            active = false;
        }

        Active[db] = active;
        return active;
    }

    public static void Forget(AcadDb.Database db) => Active.Remove(db);

    // ------------------------------------------------------------------
    // Sincronizacao
    // ------------------------------------------------------------------

    public static Report Sync(Document doc, bool create)
    {
        (List<AxisInput> axes, ViarioResult result, _) = ViarioSync.ComputeCurrent(doc.Database);
        return Sync(doc, axes, result, create);
    }

    /// <summary>Atualiza os objetos do Civil. O documento precisa estar travado.</summary>
    public static Report Sync(Document doc, IReadOnlyList<AxisInput> axes, ViarioResult result, bool create)
    {
        AcadDb.Database db = doc.Database;
        Report report = new();
        CivilPlan plan = ViarioCivilPlanner.Build(axes, result);
        report.Messages.AddRange(plan.Warnings);

        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        List<Tagged> existing = Scan(tr, db);
        if (existing.Count == 0 && !create)
            return report;

        CivilDocument cdoc = CivilDocument.GetCivilDocument(db);
        ViarioStore.EnsureRegApp(tr, db);
        Dictionary<string, AcadDb.ObjectId> layers = EnsureLayers(tr, db);
        // Sem site: alinhamentos num mesmo site formam lotes (parcels) nas quadras fechadas.
        AcadDb.ObjectId siteId = AcadDb.ObjectId.Null;
        AcadDb.ObjectId styleId = PickStyle(tr, cdoc.Styles.AlignmentStyles, "Proposed", "Proposto", "Layout", "Basic", "Basico");
        AcadDb.ObjectId labelId = PickStyle(tr, cdoc.Styles.LabelSetStyles.AlignmentLabelSetStyles, "No Labels", "Sem");

        // ---------- Decisoes (sem alterar nada) ----------
        Dictionary<string, AxisInput> planAxes = plan.Axes.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
        Dictionary<string, Tagged> axisTags = new();
        foreach (Tagged t in existing.Where(t => t.Kind == KindAxis))
        {
            if (!axisTags.ContainsKey(t.Key))
                axisTags[t.Key] = t;
        }

        HashSet<string> changedAxes = new();
        foreach (AxisInput axis in plan.Axes)
        {
            if (axisTags.TryGetValue(axis.Key, out Tagged? t) && t.Geom != PathKey(axis.Path))
                changedAxes.Add(axis.Key);
        }

        // Bordos: casa cada trecho planejado com o existente de maior sobreposicao.
        Dictionary<CivilEdgePlan, Tagged> keepEdge = new();
        List<Tagged> edgeTags = existing.Where(t => t.Kind == KindEdge).ToList();
        foreach (CivilEdgePlan e in plan.Edges)
        {
            if (changedAxes.Contains(e.AxisKey) || !axisTags.ContainsKey(e.AxisKey))
                continue;

            Tagged? match = edgeTags
                .Where(t => !t.Used && t.Key == e.AxisKey && t.Side == e.Side &&
                            Math.Abs(t.S0 - e.S0) < Tol && Math.Abs(t.S1 - e.S1) < Tol && Math.Abs(t.Offset - e.Offset) < Tol && t.Geom == e.WidenKey)
                .FirstOrDefault();
            if (match is null)
                continue;

            match.Used = true;
            keepEdge[e] = match;
        }

        // Curb returns: mantem se os dois bordos foram mantidos e a geometria confere.
        Dictionary<CivilCurbPlan, Tagged> keepCurb = new();
        List<Tagged> curbTags = existing.Where(t => t.Kind == KindCurb).ToList();
        foreach (CivilCurbPlan c in plan.Curbs)
        {
            Tagged? match = curbTags.FirstOrDefault(t => !t.Used && t.Key == c.Key);
            if (match is null)
                continue;

            bool edgesKept = c.EdgeIn is not null && c.EdgeOut is not null && keepEdge.ContainsKey(c.EdgeIn) && keepEdge.ContainsKey(c.EdgeOut);
            bool axesKept = !changedAxes.Contains(c.AxisIn) && !changedAxes.Contains(c.AxisOut);
            bool simpleOk = match.Method.StartsWith("SIMPLES", StringComparison.Ordinal) && axesKept;
            if ((edgesKept || simpleOk) && match.Geom == ArcKey(c.Arc) &&
                tr.GetObject(match.Id, AcadDb.OpenMode.ForRead, false) is CivilDb.Alignment al && VerifyArc(al, c.Arc))
            {
                match.Used = true;
                keepCurb[c] = match;
            }
        }

        // ---------- Remocoes (dependentes primeiro) ----------
        foreach (Tagged t in curbTags.Where(t => !t.Used))
            Erase(tr, t.Id, report);
        foreach (Tagged t in edgeTags.Where(t => !t.Used))
            Erase(tr, t.Id, report);
        foreach (Tagged t in axisTags.Values.Where(t => !planAxes.ContainsKey(t.Key)))
            Erase(tr, t.Id, report);
        foreach (Tagged t in existing.Where(t => t.Kind == KindAxis && axisTags.TryGetValue(t.Key, out Tagged? first) && first != t))
            Erase(tr, t.Id, report); // duplicados (copias do usuario)

        // Nomes livres so depois das remocoes (trechos refeitos mantem o nome).
        HashSet<string> names = AlignmentNames(tr, db);
        report.Styles = $"estilo '{StyleName(tr, styleId)}', rotulos '{StyleName(tr, labelId)}'";

        // ---------- Eixos ----------
        Dictionary<string, AcadDb.ObjectId> axisIds = new();
        int axisNumber = 1;
        foreach (AxisInput axis in plan.Axes)
        {
            try
            {
                if (axisTags.TryGetValue(axis.Key, out Tagged? t))
                {
                    if (changedAxes.Contains(axis.Key))
                    {
                        CivilDb.Alignment al = (CivilDb.Alignment)tr.GetObject(t.Id, AcadDb.OpenMode.ForWrite);
                        BuildEntities(al, axis.Path);
                        TagAxis(al, axis);
                        CheckPath(al, axis.Path, report);
                        report.AxesUpdated++;
                    }

                    axisIds[axis.Key] = t.Id;
                    continue;
                }

                string name = Unique(names, NextAxisName(names, ref axisNumber));
                AcadDb.ObjectId id = CivilDb.Alignment.Create(cdoc, name, siteId, layers[LayerAxis], styleId, labelId);
                CivilDb.Alignment created = (CivilDb.Alignment)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                BuildEntities(created, axis.Path);
                TagAxis(created, axis);
                CheckPath(created, axis.Path, report);
                axisIds[axis.Key] = id;
                report.AxesCreated++;
            }
            catch (System.Exception ex)
            {
                report.Messages.Add($"Eixo {axis.Key}: {ex.Message}");
            }
        }

        // ---------- Bordos ----------
        Dictionary<CivilEdgePlan, AcadDb.ObjectId> edgeIds = new();
        foreach (CivilEdgePlan e in plan.Edges)
        {
            if (keepEdge.TryGetValue(e, out Tagged? kept))
            {
                edgeIds[e] = kept.Id;
                report.EdgesKept++;
                continue;
            }

            if (!axisIds.TryGetValue(e.AxisKey, out AcadDb.ObjectId parentId))
                continue;

            try
            {
                CivilDb.Alignment parent = (CivilDb.Alignment)tr.GetObject(parentId, AcadDb.OpenMode.ForRead);
                AxisInput axis = planAxes[e.AxisKey];
                double st0 = StationOf(parent, axis.Path.PointAt(e.S0), parent.StartingStation);
                double st1 = StationOf(parent, axis.Path.PointAt(e.S1), parent.EndingStation);
                if (e.S0 <= Tol)
                    st0 = parent.StartingStation;
                if (axis.Path.Length - e.S1 <= Tol)
                    st1 = parent.EndingStation;
                if (st1 - st0 < 0.1)
                    continue;

                string name = Unique(names, $"{parent.Name} - {e.Code}");
                AcadDb.ObjectId id = CreateEdge(tr, cdoc, e, axis, parentId, st0, st1, name, siteId, layers[LayerEdge], styleId, labelId, report);
                if (!id.IsNull)
                {
                    edgeIds[e] = id;
                    report.EdgesCreated++;
                }
            }
            catch (System.Exception ex)
            {
                report.Messages.Add($"Bordo {e.Code} do eixo {e.AxisKey}: {ex.Message}");
            }
        }

        // ---------- Curb returns ----------
        Dictionary<string, int> crCount = new();
        foreach (CivilCurbPlan c in plan.Curbs)
        {
            if (keepCurb.ContainsKey(c))
            {
                // Conta tambem os mantidos para os novos nao repetirem o numero.
                CountPair(tr, c, axisIds, crCount);
                report.CurbsKept++;
                continue;
            }

            try
            {
                CreateCurb(tr, cdoc, c, axisIds, edgeIds, siteId, layers[LayerCurb], styleId, labelId, names, crCount, report);
            }
            catch (System.Exception ex)
            {
                report.CurbsFailed++;
                report.Messages.Add($"Curb return {c.Key}: {ex.Message}");
            }
        }

        tr.Commit();
        Active[db] = true;
        return report;
    }

    // ------------------------------------------------------------------
    // Autoteste (C3DVIARIOTESTE)
    // ------------------------------------------------------------------

    /// <summary>
    /// Confere cada objeto do Civil contra a rede calculada agora. Somente leitura.
    /// Linhas: "OK ...", "FALHA ...", "AVISO ...".
    /// </summary>
    public static List<string> Check(Document doc, out int ok, out int fail)
    {
        ok = 0;
        fail = 0;
        List<string> lines = new();
        AcadDb.Database db = doc.Database;
        (List<AxisInput> axes, ViarioResult result, _) = ViarioSync.ComputeCurrent(db);
        CivilPlan plan = ViarioCivilPlanner.Build(axes, result);
        lines.Add($"Rede: {axes.Count} eixo(s), {result.Nodes.Count} no(s), {result.Lines.Count} linha(s), {result.Corners.Count} esquina(s), {result.Warnings.Distinct().Count()} aviso(s) do motor.");
        List<OutputLine> pink = result.Lines.Where(l => l.Level == EdgeLevel.Ilha).ToList();
        lines.Add($"Ilhas canalizadoras: {pink.Count} ({string.Join(", ", pink.Select(l => $"{l.Segs.Count} trechos/{l.Segs.Count(sg => sg.IsArc)} arcos"))}); eliminadas: {ViarioStore.LoadRemovedIslands(db).Count}; com raio: {ViarioStore.LoadIslandRadii(db).Count}.");
        lines.Add("Elementos: " + string.Join(", ", axes.SelectMany(a => a.Events).GroupBy(e => e.Kind)
                      .Select(g => $"{g.Key} {g.Count()}")) +
                  $"; faixas livres: {ViarioStore.LoadSlipLanes(db).Count}; raios de esquina: {ViarioStore.LoadCornerRadii(db).Count}" +
                  $"; eixos com raio proprio: {axes.Count(a => a.Radius is not null)}; desvinculadas: {ViarioStore.LoadReleased(db).Count}.");
        List<AxisInput> chained = axes.Where(a => a.Chained).ToList();
        lines.Add($"Concordancia mantida: {chained.Count} eixo(s)" + (chained.Count == 0 ? "." : " - " + string.Join(", ", chained.Select(a =>
        {
            List<V2> pts = new() { a.Path.Segs[0].A };
            pts.AddRange(a.Path.Segs.Select(sg => sg.B));
            bool cont = ViarioChain.IsTangentContinuous(pts, a.Path.Segs.Select(sg => sg.IsArc ? sg.Bulge : 0.0).ToList());
            return $"{a.Key} {a.Path.Segs.Count} trechos {(cont ? "OK" : "SEM CONCORDANCIA")}";
        })) + "."));
        lines.Add("Secoes: " + string.Join(" | ", ViarioStore.LoadSections(db).Select(s =>
                      $"{s.Id} {s.Name} pista {s.Pista:0.##} cant {s.Canteiro:0.##} raio {s.Raio:0.##}" +
                      (s.Simetrica ? "" : $" lados sarj/mf/calc {ViarioStore.LadosNome(s.LadosSarjeta)}/{ViarioStore.LadosNome(s.LadosMeioFio)}/{ViarioStore.LadosNome(s.LadosCalcada)}") +
                      $" ({axes.Count(a => a.Section.Id == s.Id)} eixo(s))")) +
                  $"; corrente {ViarioStore.LoadSettings(db).CurrentSectionId}.");
        lines.Add("Linhas por nivel: " + string.Join(", ", result.Lines.GroupBy(l => l.Level).OrderBy(g => g.Key)
                      .Select(g => $"{g.Key} {g.Count()}")) + ".");
        foreach (string w in result.Warnings.Distinct())
            lines.Add("AVISO motor: " + w);
        List<Hint> hints = ViarioNorms.Check(axes, result, ViarioSync.EngineSettingsFor(db), ViarioStore.LoadSections(db));
        lines.Add($"Recomendacoes DNIT: {hints.Count(h => h.Level == HintLevel.Atencao)} atencao, {hints.Count(h => h.Level == HintLevel.Info)} informacao.");
        foreach (Hint h in hints.OrderByDescending(h => h.Level).Take(12))
            lines.Add("  " + h.Text);

        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        CheckDrawnLines(tr, db, result, lines, ref ok, ref fail);
        CheckSignage(tr, db, axes, result, lines, ref ok, ref fail);
        List<Tagged> existing = Scan(tr, db);
        if (existing.Count == 0)
        {
            lines.Add("AVISO nenhum objeto do Civil gerado (rode C3DVIARIOCIVIL > Gerar).");
            return lines;
        }

        HashSet<Tagged> seen = new();
        Dictionary<string, AxisInput> byKey = axes.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());

        void Ok(string m) { lines.Add("OK    " + m); }
        void Fail(string m) { lines.Add("FALHA " + m); }

        foreach (AxisInput axis in plan.Axes)
        {
            Tagged? t = existing.FirstOrDefault(x => x.Kind == KindAxis && x.Key == axis.Key);
            if (t is null) { Fail($"eixo {axis.Key}: sem alinhamento"); fail++; continue; }
            seen.Add(t);
            CivilDb.Alignment al = (CivilDb.Alignment)tr.GetObject(t.Id, AcadDb.OpenMode.ForRead);
            int bad = axis.Path.Segs.Count(sg => sg.Length > 1e-3 && !OnAlignment(al, sg.PointAt(0.5)));
            double dl = Math.Abs(al.Length - axis.Path.Length);
            if (bad == 0 && dl < 0.01) { Ok($"{al.Name}: {axis.Path.Segs.Count} trecho(s), L={al.Length:0.###}"); ok++; }
            else { Fail($"{al.Name}: {bad} trecho(s) fora, dL={dl:0.###}"); fail++; }
        }

        foreach (CivilEdgePlan e in plan.Edges)
        {
            Tagged? t = existing.FirstOrDefault(x => x.Kind == KindEdge && x.Key == e.AxisKey && x.Side == e.Side &&
                                                     Math.Abs(x.S0 - e.S0) < 0.01 && Math.Abs(x.S1 - e.S1) < 0.01);
            if (t is null) { Fail($"bordo {e.Code} do eixo {e.AxisKey}: nao existe"); fail++; continue; }
            seen.Add(t);
            CivilDb.Alignment al = (CivilDb.Alignment)tr.GetObject(t.Id, AcadDb.OpenMode.ForRead);
            AxisInput axis = byKey[e.AxisKey];
            bool plate = VerifyEdge(al, axis, e, out string why);
            string taper = string.Empty;
            bool taperBad = false;
            if (e.Widenings.Count > 0)
            {
                double dev = TaperDeviation(al, axis, e);
                taper = $", taper desvio max {dev:0.###} m";
                taperBad = dev > 0.10;
            }

            string info = $"{al.Name} [{t.Method}] {e.Widenings.Count} alarg.{taper}";
            if (plate && !taperBad) { Ok(info); ok++; }
            else { Fail(info + (plate ? "" : " | " + why) + (e.Widenings.Count > 0 ? " | " + DescribeRegions(al) : "")); fail++; }
        }

        foreach (CivilCurbPlan c in plan.Curbs)
        {
            Tagged? t = existing.FirstOrDefault(x => x.Kind == KindCurb && x.Key == c.Key);
            if (t is null) { Fail($"curb return {c.Key}: nao existe"); fail++; continue; }
            seen.Add(t);
            CivilDb.Alignment al = (CivilDb.Alignment)tr.GetObject(t.Id, AcadDb.OpenMode.ForRead);
            string info = $"{al.Name} [{t.Method}] R={c.Radius:0.##}";
            if (VerifyArc(al, c.Arc)) { Ok(info); ok++; }
            else { Fail(info + " | arco nao confere"); fail++; }
        }

        foreach (Tagged t in existing.Where(x => !seen.Contains(x)))
        {
            string name = tr.GetObject(t.Id, AcadDb.OpenMode.ForRead, false) is CivilDb.Alignment al ? al.Name : t.Id.ToString();
            Fail($"{name}: sobrando ({t.Kind} {t.Key})");
            fail++;
        }

        return lines;
    }

    /// <summary>Maior distancia entre o bordo da rede e o alinhamento dentro dos tapers.</summary>
    private static double TaperDeviation(CivilDb.Alignment al, AxisInput axis, CivilEdgePlan e)
    {
        double max = 0.0;
        foreach (CivilWidenRegion w in e.Widenings)
        {
            foreach ((double a, double b) in new[] { (w.Start - w.TaperIn, w.Start), (w.End, w.End + w.TaperOut) })
            {
                if (b - a < 0.1)
                    continue;
                for (int i = 1; i < 10; i++)
                {
                    double s = a + ((b - a) * i / 10.0);
                    if (NearKink(axis.Path, s, Math.Abs(e.Offset) + w.Width + 0.5))
                        continue;
                    V2 p = OffsetPoint(axis.Path, s, e.OffsetAt(s));
                    try
                    {
                        double station = 0, offset = 0;
                        bool outOfRange = false;
                        al.StationOffsetAcceptOutOfRange(p.X, p.Y, ref station, ref offset, ref outOfRange);
                        max = Math.Max(max, outOfRange ? 99.0 : Math.Abs(offset));
                    }
                    catch
                    {
                        max = 99.0;
                    }
                }
            }
        }

        return max;
    }

    /// <summary>Apaga todos os objetos do Civil gerados pelo modulo.</summary>
    public static int RemoveAll(Document doc)
    {
        AcadDb.Database db = doc.Database;
        Report report = new();
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            List<Tagged> all = Scan(tr, db);
            foreach (string kind in new[] { KindCurb, KindEdge, KindAxis })
            {
                foreach (Tagged t in all.Where(t => t.Kind == kind))
                    Erase(tr, t.Id, report);
            }

            tr.Commit();
        }

        Active[db] = false;
        return report.Erased;
    }

    // ------------------------------------------------------------------
    // Bordo (offset dinamico, com alargamentos; senao geometria fixa)
    // ------------------------------------------------------------------

    private static AcadDb.ObjectId CreateEdge(AcadDb.Transaction tr, CivilDocument cdoc, CivilEdgePlan e, AxisInput axis,
        AcadDb.ObjectId parentId, double st0, double st1, string name, AcadDb.ObjectId siteId, AcadDb.ObjectId layerId,
        AcadDb.ObjectId styleId, AcadDb.ObjectId labelId, Report report)
    {
        string side = e.Side.ToString(CultureInfo.InvariantCulture);
        List<string> errors = new();

        // Sempre offset dinamico do Civil (ligado ao eixo). Com alargamento, testa a forma
        // do valor (largura total ou acrescimo, com ou sem sinal) ate o bordo conferir.
        int variants = e.Widenings.Count == 0 ? 1 : 4;
        for (int pass = 0; pass <= variants; pass++)
        {
            bool last = pass == variants;
            int v = last ? 0 : pass;
            AcadDb.ObjectId id = AcadDb.ObjectId.Null;
            try
            {
                id = CivilDb.Alignment.CreateOffsetAlignment(name, parentId, e.Offset, styleId, st0, st1);
                CivilDb.Alignment al = (CivilDb.Alignment)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                foreach (CivilWidenRegion w in e.Widenings)
                {
                    try
                    {
                        ApplyWidening(al, axis, e, w, v);
                    }
                    catch (System.Exception ex)
                    {
                        errors.Add($"alargamento: {ex.Message}");
                    }
                }

                bool ok = VerifyEdge(al, axis, e, out string why);
                if (e.Widenings.Count > 0 && DebugWidening)
                    report.Messages.Add($"{name} [v{v}]: {DescribeRegions(al)}");
                if (ok || last)
                {
                    al.Layer = LayerEdge;
                    SetTag(al, KindEdge, e.AxisKey, side, F(e.S0), F(e.S1), F(e.Offset), e.WidenKey, ok ? "OFFSET" + (e.Widenings.Count > 0 ? "-ALARG" + v : "") : "OFFSET-DIVERGE");
                    if (!ok)
                    {
                        report.EdgesFixed++;
                        report.Messages.Add($"{name}: offset criado, mas nao bate com a rede ({why}; {string.Join(" | ", errors.Take(2))}).");
                    }

                    return id;
                }

                errors.Add($"variante {v}: {why}");
                al.Erase();
            }
            catch (System.Exception ex)
            {
                errors.Add($"variante {v}: {ex.Message}");
                TryErase(tr, id);
                if (last)
                    report.Messages.Add($"{name}: {string.Join(" | ", errors.Take(3))}");
            }
        }

        return AcadDb.ObjectId.Null;
    }

    private static void ApplyWidening(CivilDb.Alignment al, AxisInput axis, CivilEdgePlan e, CivilWidenRegion w, int variant)
    {
        double sa = StationOf(al, OffsetPoint(axis.Path, w.Start, e.Offset), al.StartingStation);
        double sb = StationOf(al, OffsetPoint(axis.Path, w.End, e.Offset), al.EndingStation);
        if (w.Start <= e.S0 + Tol)
            sa = al.StartingStation;
        if (w.End >= e.S1 - Tol)
            sb = al.EndingStation;

        double total = Math.Abs(e.Offset) + w.Width;
        double value = variant switch
        {
            0 => total,
            1 => e.Side * total,
            2 => w.Width,
            _ => e.Side * w.Width
        };

        CivilDb.OffsetAlignmentInfo info = al.OffsetAlignmentInfo;
        info.AddWidening(Math.Min(sa, sb), Math.Max(sa, sb), value);

        // Comprimento e forma das transicoes da regiao criada.
        double mid = (Math.Min(sa, sb) + Math.Max(sa, sb)) / 2.0;
        foreach (CivilDb.AlignmentRegion region in info.Regions)
        {
            if (mid < region.StartStation - 1e-6 || mid > region.EndStation + 1e-6)
                continue;

            // A regiao inicial nao tem transicao de entrada (e a final nao tem de saida):
            // o Civil lanca excecao ao ler; cada lado e tratado a parte.
            if (w.TaperIn > 1e-3)
                SetTransition(Try(() => region.EntryTransition), region.StartStation - TaperStations(al, axis, e, w.Start - w.TaperIn, w.Start), region.StartStation, w.Smooth, w.Width);
            if (w.TaperOut > 1e-3)
                SetTransition(Try(() => region.ExitTransition), region.EndStation, region.EndStation + TaperStations(al, axis, e, w.End, w.End + w.TaperOut), w.Smooth, w.Width);
            break;
        }
    }

    /// <summary>Diagnostico temporario das regioes/transicoes criadas pelo Civil.</summary>
    public static bool DebugWidening { get; set; } = true;

    private static string DescribeRegions(CivilDb.Alignment al)
    {
        try
        {
            List<string> parts = new();
            foreach (CivilDb.AlignmentRegion r in al.OffsetAlignmentInfo.Regions)
            {
                parts.Add($"R[{r.StartStation:0.##}-{r.EndStation:0.##} off={r.Offset:0.###} tipo={r.RegionType}] in={DescribeTransition(Try(() => r.EntryTransition))} out={DescribeTransition(Try(() => r.ExitTransition))}");
            }

            return string.Join(" ; ", parts);
        }
        catch (System.Exception ex)
        {
            return "erro: " + ex.Message;
        }
    }

    private static string DescribeTransition(CivilDb.AlignmentTransition? t)
    {
        if (t is null)
            return "-";

        try
        {
            object? d = t.TransitionDescription;
            if (d is null)
                return t.TransitionType + "(sem desc)";

            List<string> props = new();
            foreach (System.Reflection.PropertyInfo p in d.GetType().GetProperties())
            {
                object? val = null;
                try { val = p.GetValue(d); } catch { val = "?"; }
                props.Add($"{p.Name}{(p.CanWrite ? "*" : "")}={(val is double dv ? dv.ToString("0.###", CultureInfo.InvariantCulture) : val)}");
            }

            return $"{t.TransitionType}/{d.GetType().Name}{{{string.Join(",", props)}}}";
        }
        catch (System.Exception ex)
        {
            return "erro " + ex.Message;
        }
    }

    private static T? Try<T>(Func<T> get) where T : class
    {
        try
        {
            return get();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Comprimento, em estacas do bordo, de um trecho [s0, s1] do eixo.</summary>
    private static double TaperStations(CivilDb.Alignment al, AxisInput axis, CivilEdgePlan e, double s0, double s1)
    {
        double a = StationOf(al, OffsetPoint(axis.Path, s0, e.Offset), al.StartingStation);
        double b = StationOf(al, OffsetPoint(axis.Path, s1, e.Offset), al.EndingStation);
        double d = Math.Abs(b - a);
        return d > 1e-3 ? d : Math.Abs(s1 - s0);
    }

    /// <summary>
    /// Transicao entre estacas [start, end] do bordo. O Civil guarda a transicao como
    /// StartStation/EndStation (Length vem zerado ao criar o alargamento).
    /// </summary>
    private static void SetTransition(CivilDb.AlignmentTransition? transition, double start, double end, bool smooth, double width)
    {
        if (transition is null || end - start <= 1e-3)
            return;

        try
        {
            transition.TransitionType = smooth ? Autodesk.Civil.TransitionType.CurveReverseCurve : Autodesk.Civil.TransitionType.Linear;
        }
        catch
        {
            // Mantem o tipo padrao.
        }

        object? d = Try(() => (object?)transition.TransitionDescription);
        if (d is null)
            return;

        // Depois de trocar o tipo a descricao muda de classe: le de novo.
        Type type = d.GetType();
        void Set(string name, object value)
        {
            try
            {
                System.Reflection.PropertyInfo? p = type.GetProperty(name);
                if (p is not null && p.CanWrite)
                    p.SetValue(d, value);
            }
            catch
            {
                // Propriedade recusada pelo Civil.
            }
        }

        // Ordem: alarga o intervalo primeiro para nao inverter inicio/fim no meio.
        Set("StartStation", start);
        Set("EndStation", end);
        Set("StartStation", start);
        Set("Length", end - start);

        // Curva reversa simetrica: dois arcos iguais que vencem a largura no comprimento
        // do taper (fica a poucos cm do taper suave em cosseno do motor).
        if (smooth && width > 1e-3)
        {
            double length = end - start;
            double radius = ((length * length) + (width * width)) / (4.0 * width);
            Set("EntryCurveRadius", radius);
            Set("ReverseCurveRadius", radius);
        }
    }

    /// <summary>Confere o bordo fora das transicoes (patamares e trechos normais).</summary>
    private static bool VerifyEdge(CivilDb.Alignment al, AxisInput axis, CivilEdgePlan e, out string why)
    {
        why = string.Empty;
        int n = Math.Clamp((int)(e.Length / 4.0), 4, 60);
        for (int i = 0; i <= n; i++)
        {
            double s = e.S0 + (e.Length * i / n);
            if (s - e.S0 < 0.05 || e.S1 - s < 0.05)
                continue;
            if (NearKink(axis.Path, s, Math.Abs(e.Offset) + 0.5))
                continue;
            if (e.Widenings.Any(w => (s > w.Start - w.TaperIn - 0.05 && s < w.Start + 0.05 && w.TaperIn > 0) ||
                                     (s > w.End - 0.05 && s < w.End + w.TaperOut + 0.05 && w.TaperOut > 0)))
                continue;

            V2 p = OffsetPoint(axis.Path, s, e.OffsetAt(s));
            if (!OnAlignment(al, p))
            {
                why = $"fora do bordo na estaca {s:0.##} do eixo";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Perto de um vertice com quebra de direcao (polilinha sem curva) o bordo do lado de
    /// dentro e cortado e o de fora vira arco: a amostragem pela normal nao vale ali.
    /// </summary>
    private static bool NearKink(GPath path, double s, double radius)
    {
        double cum = 0.0;
        for (int i = 0; i < path.Segs.Count; i++)
        {
            cum += path.Segs[i].Length;
            bool last = i == path.Segs.Count - 1;
            if (last && !path.Closed)
                break;

            Seg a = path.Segs[i];
            Seg b = path.Segs[last ? 0 : i + 1];
            double turn = Math.Abs(GMath.Norm(b.TangentAt(0.0).Angle - a.TangentAt(1.0).Angle + Math.PI) - Math.PI);
            if (turn < 1.0 * Math.PI / 180.0)
                continue;

            double at = last ? path.Length : cum;
            double d = Math.Abs(s - at);
            if (path.Closed)
                d = Math.Min(d, path.Length - d);
            if (d <= radius)
                return true;
        }

        return false;
    }

    /// <summary>Bordo exato: paralela quando a largura e constante, amostrado nos tapers.</summary>
    private static GPath FixedEdgePath(GPath axis, CivilEdgePlan e)
    {
        if (e.Widenings.Count == 0 && !e.ForceFixed)
            return axis.SubWrap(e.S0, e.S1).Offset(-e.Offset);

        List<V2> pts = new();
        double step = 0.5;
        int n = Math.Max(2, (int)Math.Ceiling(e.Length / step));
        for (int i = 0; i <= n; i++)
        {
            double s = e.S0 + (e.Length * i / n);
            pts.Add(OffsetPoint(axis, s, e.OffsetAt(s)));
        }

        // Junta pontos alinhados para nao gerar centenas de retas.
        List<V2> simple = new() { pts[0] };
        for (int i = 1; i < pts.Count - 1; i++)
        {
            V2 a = simple[^1];
            V2 b = pts[i + 1];
            V2 ab = b - a;
            double len = ab.Length;
            double dev = len < 1e-9 ? 0.0 : Math.Abs(V2.Cross(ab, pts[i] - a)) / len;
            if (dev > 0.003)
                simple.Add(pts[i]);
        }

        simple.Add(pts[^1]);
        List<Seg> segs = new();
        for (int i = 1; i < simple.Count; i++)
            segs.Add(Seg.Line(simple[i - 1], simple[i]));
        return new GPath(segs);
    }

    /// <summary>Ponto a distancia com sinal (positivo = direita, como no Civil).</summary>
    private static V2 OffsetPoint(GPath path, double s, double offset)
    {
        V2 t = path.TangentAt(s);
        return path.PointAt(s) + (new V2(t.Y, -t.X) * offset);
    }

    // ------------------------------------------------------------------
    // Curb return
    // ------------------------------------------------------------------

    private static void CreateCurb(AcadDb.Transaction tr, CivilDocument cdoc, CivilCurbPlan c,
        Dictionary<string, AcadDb.ObjectId> axisIds, Dictionary<CivilEdgePlan, AcadDb.ObjectId> edgeIds,
        AcadDb.ObjectId siteId, AcadDb.ObjectId layerId, AcadDb.ObjectId styleId, AcadDb.ObjectId labelId,
        HashSet<string> names, Dictionary<string, int> crCount, Report report)
    {
        if (!axisIds.TryGetValue(c.AxisIn, out AcadDb.ObjectId axisIn) || !axisIds.TryGetValue(c.AxisOut, out AcadDb.ObjectId axisOut))
            return;

        string nameIn = ((CivilDb.Alignment)tr.GetObject(axisIn, AcadDb.OpenMode.ForRead)).Name;
        string nameOut = ((CivilDb.Alignment)tr.GetObject(axisOut, AcadDb.OpenMode.ForRead)).Name;
        string pair = nameIn == nameOut ? nameIn : $"{nameIn} x {nameOut}";
        crCount[pair] = crCount.TryGetValue(pair, out int n) ? n + 1 : 1;
        string name = Unique(names, $"{pair} - CR{crCount[pair]}");

        // Tentativas de alinhamento conectado (o Civil mantem o vinculo com os pais).
        List<(string Method, CivilDb.ConnectedAlignmentParams P)> attempts = new();
        if (c.EdgeIn is not null && c.EdgeOut is not null &&
            edgeIds.TryGetValue(c.EdgeIn, out AcadDb.ObjectId eIn) && edgeIds.TryGetValue(c.EdgeOut, out AcadDb.ObjectId eOut))
        {
            CivilDb.Alignment aIn = (CivilDb.Alignment)tr.GetObject(eIn, AcadDb.OpenMode.ForRead);
            CivilDb.Alignment aOut = (CivilDb.Alignment)tr.GetObject(eOut, AcadDb.OpenMode.ForRead);
            double sIn = StationOf(aIn, c.Arc.A, aIn.StartingStation);
            double sOut = StationOf(aOut, c.Arc.B, aOut.StartingStation);
            attempts.Add(("CON-BORDOS", Params(eIn, sIn, 0, eOut, sOut, 0, c.Radius)));
            attempts.Add(("CON-BORDOS-INV", Params(eOut, sOut, 0, eIn, sIn, 0, c.Radius)));
        }

        {
            CivilDb.Alignment pIn = (CivilDb.Alignment)tr.GetObject(axisIn, AcadDb.OpenMode.ForRead);
            CivilDb.Alignment pOut = (CivilDb.Alignment)tr.GetObject(axisOut, AcadDb.OpenMode.ForRead);
            double sIn = StationOf(pIn, c.Arc.A, pIn.StartingStation);
            double sOut = StationOf(pOut, c.Arc.B, pOut.StartingStation);
            double oIn = Math.Abs(c.OffsetIn);
            double oOut = Math.Abs(c.OffsetOut);
            attempts.Add(("CON-EIXOS", Params(axisIn, sIn, oIn, axisOut, sOut, oOut, c.Radius)));
            attempts.Add(("CON-EIXOS-SINAL", Params(axisIn, sIn, c.OffsetIn, axisOut, sOut, c.OffsetOut, c.Radius)));
            attempts.Add(("CON-EIXOS-INV", Params(axisOut, sOut, oOut, axisIn, sIn, oIn, c.Radius)));
        }

        List<string> errors = new();
        foreach ((string method, CivilDb.ConnectedAlignmentParams p) in attempts)
        {
            AcadDb.ObjectId id = AcadDb.ObjectId.Null;
            try
            {
                try
                {
                    id = CivilDb.Alignment.CreateConnectedAlignment(name, siteId, layerId, styleId, labelId, p);
                }
                catch (System.Exception) when (siteId.IsNull)
                {
                    // Se o Civil exigir site para o conectado: site proprio so de curb returns.
                    id = CivilDb.Alignment.CreateConnectedAlignment(name, EnsureSite(tr, cdoc), layerId, styleId, labelId, p);
                }

                CivilDb.Alignment al = (CivilDb.Alignment)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                if (VerifyArc(al, c.Arc))
                {
                    al.Layer = LayerCurb;
                    SetTag(al, KindCurb, c.Key, "0", "0", "0", "0", ArcKey(c.Arc), method);
                    report.CurbsCreated++;
                    report.CurbsConnected++;
                    return;
                }

                errors.Add(method + ": geometria nao confere");
                al.Erase();
            }
            catch (System.Exception ex)
            {
                errors.Add(method + ": " + ex.Message);
                TryErase(tr, id);
            }
        }

        // Alternativa: alinhamento do tipo CurbReturn com o arco fixo (o plugin mantem).
        AcadDb.ObjectId simpleId;
        try
        {
            simpleId = CivilDb.Alignment.Create(cdoc, name, siteId, layerId, styleId, labelId, CivilDb.AlignmentType.CurbReturn);
        }
        catch
        {
            simpleId = CivilDb.Alignment.Create(cdoc, name, siteId, layerId, styleId, labelId);
        }

        CivilDb.Alignment simple = (CivilDb.Alignment)tr.GetObject(simpleId, AcadDb.OpenMode.ForWrite);
        BuildEntities(simple, new GPath(new[] { c.Arc }));
        simple.Layer = LayerCurb;
        SetTag(simple, KindCurb, c.Key, "0", "0", "0", "0", ArcKey(c.Arc), "SIMPLES");
        report.CurbsCreated++;
        report.CurbsSimple++;
        if (errors.Count > 0)
            report.Messages.Add($"Curb return {name}: conectado nao deu ({string.Join(" | ", errors.Take(3))}); criado simples.");
    }

    private static void CountPair(AcadDb.Transaction tr, CivilCurbPlan c, Dictionary<string, AcadDb.ObjectId> axisIds, Dictionary<string, int> crCount)
    {
        if (!axisIds.TryGetValue(c.AxisIn, out AcadDb.ObjectId a) || !axisIds.TryGetValue(c.AxisOut, out AcadDb.ObjectId b))
            return;
        string nameIn = ((CivilDb.Alignment)tr.GetObject(a, AcadDb.OpenMode.ForRead)).Name;
        string nameOut = ((CivilDb.Alignment)tr.GetObject(b, AcadDb.OpenMode.ForRead)).Name;
        string pair = nameIn == nameOut ? nameIn : $"{nameIn} x {nameOut}";
        crCount[pair] = crCount.TryGetValue(pair, out int n) ? n + 1 : 1;
    }

    private static CivilDb.ConnectedAlignmentParams Params(AcadDb.ObjectId inId, double inSta, double inOff,
        AcadDb.ObjectId outId, double outSta, double outOff, double radius) => new()
    {
        IncomingParentAlignmentId = inId,
        IncomingParentAlignmentStation = inSta,
        OffsetIn = inOff,
        OutgoingParentAlignmentId = outId,
        OutgoingParentAlignmentStation = outSta,
        OffsetOut = outOff,
        CurveGroupType = Autodesk.Civil.CurbReturnCurveGroupType.Arc,
        CurveRadius = radius,
        GreaterThan180 = false,
        ConnectionOverlapLengthIn = CurbOverlap,
        ConnectionOverlapLengthOut = CurbOverlap
    };

    /// <summary>O arco planejado esta sobre o alinhamento (inicio, meio e fim).</summary>
    private static bool VerifyArc(CivilDb.Alignment al, Seg arc)
    {
        foreach (V2 p in new[] { arc.A, arc.PointAt(0.5), arc.B })
        {
            if (!OnAlignment(al, p))
                return false;
        }

        return true;
    }

    private static bool OnAlignment(CivilDb.Alignment al, V2 p)
    {
        try
        {
            double station = 0, offset = 0;
            bool outOfRange = false;
            al.StationOffsetAcceptOutOfRange(p.X, p.Y, ref station, ref offset, ref outOfRange);
            if (Math.Abs(offset) <= VerifyTol && !outOfRange)
                return true;

            // Ponta exata pode cair como "fora" por arredondamento: confere a distancia as pontas.
            double e = 0, n = 0;
            foreach (double s in new[] { al.StartingStation, al.EndingStation })
            {
                al.PointLocation(s, 0.0, ref e, ref n);
                if (Math.Sqrt(((e - p.X) * (e - p.X)) + ((n - p.Y) * (n - p.Y))) <= VerifyTol)
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Geometria
    // ------------------------------------------------------------------

    /// <summary>Refaz as entidades do alinhamento (retas e arcos fixos) a partir do caminho.</summary>
    public static void BuildEntities(CivilDb.Alignment al, GPath path)
    {
        al.Entities.Clear();
        foreach (Seg s in path.Segs)
        {
            if (s.Length < 1e-6)
                continue;

            if (!s.IsArc || Math.Abs(s.Bulge) < 1e-9)
            {
                al.Entities.AddFixedLine(P3(s.A), P3(s.B));
                continue;
            }

            AddArc(al, s);
        }
    }

    /// <summary>
    /// Arco fixo do inicio ao fim. As sobrecargas do Civil interpretam ponto/direcao de formas
    /// diferentes; cada tentativa e conferida (centro e comprimento) e desfeita se nao bater.
    /// </summary>
    private static void AddArc(CivilDb.Alignment al, Seg s)
    {
        V2 t0 = s.TangentAt(0.0);
        V2 t1 = s.TangentAt(1.0);
        bool cw = s.Sweep < 0;
        List<Func<object>> attempts = new()
        {
            () => al.Entities.AddFixedCurve(P3(s.A), P3(s.B), V3(t1)),
            () => al.Entities.AddFixedCurve(P3(s.A), V3(t0), P3(s.B)),
            () => al.Entities.AddFixedCurve(P3(s.A), P3(s.B), s.Radius, cw)
        };

        foreach (Func<object> attempt in attempts)
        {
            object? entity = null;
            try
            {
                entity = attempt();
                if (ArcMatches(entity, s))
                    return;
            }
            catch
            {
                // Proxima forma.
            }

            if (entity is CivilDb.AlignmentEntity ae)
            {
                try
                {
                    al.Entities.Remove(ae);
                }
                catch
                {
                    // Sem remover nao da para tentar outra forma.
                    return;
                }
            }
        }

        // Ultimo recurso: arco em duas metades (cada uma < 180 graus) pela forma explicita de raio.
        if (Math.Abs(s.Sweep) > 1e-3 && s.Length > 0.05)
        {
            Seg h1 = s.Sub(0.0, 0.5);
            Seg h2 = s.Sub(0.5, 1.0);
            al.Entities.AddFixedCurve(P3(h1.A), P3(h1.B), h1.Radius, cw);
            al.Entities.AddFixedCurve(P3(h2.A), P3(h2.B), h2.Radius, cw);
        }
    }

    /// <summary>Confere centro, raio, sentido e angulo do arco criado.</summary>
    private static bool ArcMatches(object entity, Seg s)
    {
        if (entity is not CivilDb.AlignmentArc arc)
            return false;

        if (V2.Dist(new V2(arc.CenterPoint.X, arc.CenterPoint.Y), s.Center) > 0.01)
            return false;
        if (Math.Abs(arc.Radius - s.Radius) > 0.01)
            return false;
        if (arc.Clockwise != (s.Sweep < 0))
            return false;
        return Math.Abs(Math.Abs(arc.Delta) - Math.Abs(s.Sweep)) <= 1e-3;
    }

    /// <summary>Confere o meio de cada trecho do caminho sobre o alinhamento.</summary>
    private static void CheckPath(CivilDb.Alignment al, GPath path, Report report)
    {
        int bad = path.Segs.Count(sg => sg.Length > 1e-3 && !OnAlignment(al, sg.PointAt(0.5)));
        if (bad > 0)
            report.Messages.Add($"{al.Name}: {bad} trecho(s) nao conferem com o eixo desenhado.");
    }

    private static Vector3d V3(V2 v) => new(v.X, v.Y, 0.0);

    private static double StationOf(CivilDb.Alignment al, V2 p, double fallback)
    {
        try
        {
            double station = 0, offset = 0;
            bool outOfRange = false;
            al.StationOffsetAcceptOutOfRange(p.X, p.Y, ref station, ref offset, ref outOfRange);
            return Math.Max(al.StartingStation, Math.Min(al.EndingStation, station));
        }
        catch
        {
            return fallback;
        }
    }

    private static Point3d P3(V2 p) => new(p.X, p.Y, 0.0);

    private static string F(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    private static double D(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0.0;

    public static string PathKey(GPath path) =>
        string.Join(";", path.Segs.Select(s => $"{s.A.X:0.###},{s.A.Y:0.###},{s.B.X:0.###},{s.B.Y:0.###},{s.Bulge:0.#####}"));

    private static string ArcKey(Seg arc) =>
        $"{arc.A.X:0.###},{arc.A.Y:0.###},{arc.B.X:0.###},{arc.B.Y:0.###},{Math.Abs(arc.Bulge):0.#####}";

    // ------------------------------------------------------------------
    // Banco
    // ------------------------------------------------------------------

    /// <summary>Nome do alinhamento do Civil de cada eixo (chave = handle do eixo).</summary>
    public static Dictionary<string, string> AxisNames(AcadDb.Database db)
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        foreach (Tagged t in Scan(tr, db).Where(t => t.Kind == KindAxis))
        {
            if (tr.GetObject(t.Id, AcadDb.OpenMode.ForRead, false) is CivilDb.Alignment al)
                names[t.Key] = al.Name;
        }

        return names;
    }

    /// <summary>Bloco de sinalizacao (se existir) x marcas que a rede pede agora.</summary>
    private static void CheckSignage(AcadDb.Transaction tr, AcadDb.Database db, List<AxisInput> axes, ViarioResult result,
        List<string> lines, ref int ok, ref int fail)
    {
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        if (!bt.Has(ViarioSignageSync.BlockName))
        {
            lines.Add("Sinalizacao: sem bloco.");
            return;
        }

        List<MarkLine> marks = ViarioSignage.Build(axes, result, ViarioStore.LoadSignage(db));
        AcadDb.BlockTableRecord block = (AcadDb.BlockTableRecord)tr.GetObject(bt[ViarioSignageSync.BlockName], AcadDb.OpenMode.ForRead);
        int count = 0;
        foreach (AcadDb.ObjectId id in block)
        {
            if (!id.IsErased)
                count++;
        }

        int refs = block.GetBlockReferenceIds(true, false).Count;
        bool good = count == marks.Count && refs == 1;
        lines.Add($"{(good ? "OK   " : "FALHA")} Sinalizacao: {count} marca(s) no bloco, esperado {marks.Count} ({ViarioSignageSync.Summary(marks)}); {refs} insercao(oes).");
        if (good) ok++; else fail++;
    }

    /// <summary>Linhas geradas no desenho x linhas que a rede pede agora (menos as soltas).</summary>
    private static void CheckDrawnLines(AcadDb.Transaction tr, AcadDb.Database db, ViarioResult result,
        List<string> lines, ref int ok, ref int fail)
    {
        HashSet<string> released = ViarioStore.LoadReleased(db);
        Dictionary<string, int> wanted = new(StringComparer.Ordinal);
        foreach (OutputLine line in result.Lines)
        {
            string key = ViarioStore.GeometryKey(line);
            if (!released.Contains(key))
                wanted[key] = wanted.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        int expected = wanted.Values.Sum(), drawn = 0, extra = 0;
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        RXClass plClass = RXObject.GetClass(typeof(AcadDb.Polyline));
        foreach (AcadDb.ObjectId id in ms)
        {
            if (id.IsErased || !id.ObjectClass.IsDerivedFrom(plClass))
                continue;
            AcadDb.Polyline pl = (AcadDb.Polyline)tr.GetObject(id, AcadDb.OpenMode.ForRead, false);
            if (ViarioStore.ReadTag(pl, out string levelText) != ViarioStore.TagGenerated)
                continue;

            drawn++;
            string key = ViarioStore.GeometryKey(pl, ViarioStore.ParseLevel(levelText));
            if (wanted.TryGetValue(key, out int n) && n > 0)
                wanted[key] = n - 1;
            else
                extra++;
        }

        int missing = wanted.Values.Sum();
        bool good = missing == 0 && extra == 0;
        lines.Add($"{(good ? "OK   " : "FALHA")} Desenho: {drawn} linha(s) gerada(s), esperado {expected}; faltando {missing}, sobrando {extra}.");
        if (good) ok++; else fail++;
    }

    private static List<Tagged> Scan(AcadDb.Transaction tr, AcadDb.Database db)
    {
        List<Tagged> list = new();
        RXClass alClass = RXObject.GetClass(typeof(CivilDb.Alignment));
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);

        foreach (AcadDb.ObjectId id in ms)
        {
            if (id.IsErased || !id.ObjectClass.IsDerivedFrom(alClass))
                continue;

            AcadDb.DBObject obj = tr.GetObject(id, AcadDb.OpenMode.ForRead, false);
            List<string>? v = ViarioStore.ReadValues(obj);
            if (v is null || v.Count < 3 || v[0] != Tag)
                continue;

            list.Add(new Tagged
            {
                Id = id,
                Kind = v[1],
                Key = v[2],
                Side = v.Count > 3 && int.TryParse(v[3], out int side) ? side : 0,
                S0 = v.Count > 4 ? D(v[4]) : 0,
                S1 = v.Count > 5 ? D(v[5]) : 0,
                Offset = v.Count > 6 ? D(v[6]) : 0,
                Geom = v.Count > 7 ? v[7] : "",
                Method = v.Count > 8 ? v[8] : ""
            });
        }

        return list;
    }

    private static HashSet<string> AlignmentNames(AcadDb.Transaction tr, AcadDb.Database db)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        RXClass alClass = RXObject.GetClass(typeof(CivilDb.Alignment));
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (!id.IsErased && id.ObjectClass.IsDerivedFrom(alClass) && tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Alignment al)
                names.Add(al.Name);
        }

        return names;
    }

    private static string Unique(HashSet<string> names, string name)
    {
        string candidate = name;
        int i = 2;
        while (names.Contains(candidate))
            candidate = $"{name} ({i++})";
        names.Add(candidate);
        return candidate;
    }

    private static string NextAxisName(HashSet<string> names, ref int n)
    {
        while (names.Contains($"Eixo VIARIO {n}"))
            n++;
        return $"Eixo VIARIO {n++}";
    }

    private static void TagAxis(CivilDb.Alignment al, AxisInput axis)
    {
        al.Layer = LayerAxis;
        SetTag(al, KindAxis, axis.Key, "0", "0", F(axis.Path.Length), "0", PathKey(axis.Path), string.Empty);
    }

    private static void SetTag(AcadDb.Entity entity, string kind, string key, string side, string s0, string s1, string offset, string geom, string method) =>
        ViarioStore.SetTag(entity, Tag, kind, key, side, s0, s1, offset, geom, method);

    private static void Erase(AcadDb.Transaction tr, AcadDb.ObjectId id, Report report)
    {
        if (TryErase(tr, id))
            report.Erased++;
    }

    private static bool TryErase(AcadDb.Transaction tr, AcadDb.ObjectId id)
    {
        if (id.IsNull || id.IsErased)
            return false;

        try
        {
            AcadDb.DBObject obj = tr.GetObject(id, AcadDb.OpenMode.ForWrite, false);
            obj.Erase();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AcadDb.ObjectId EnsureSite(AcadDb.Transaction tr, CivilDocument cdoc)
    {
        foreach (AcadDb.ObjectId id in cdoc.GetSiteIds())
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Site site &&
                string.Equals(site.Name, SiteName, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return CivilDb.Site.Create(cdoc, SiteName);
    }

    private static string StyleName(AcadDb.Transaction tr, AcadDb.ObjectId id)
    {
        try
        {
            return !id.IsNull && tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.StyleBase st ? st.Name : "(nenhum)";
        }
        catch
        {
            return "?";
        }
    }

    private static AcadDb.ObjectId PickStyle(AcadDb.Transaction tr, System.Collections.IEnumerable styles, params string[] preferred)
    {
        List<(AcadDb.ObjectId Id, string Name)> all = new();
        foreach (object item in styles)
        {
            if (item is not AcadDb.ObjectId id)
                continue;

            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.StyleBase style)
                all.Add((id, style.Name));
        }

        foreach (string p in preferred)
        {
            foreach ((AcadDb.ObjectId id, string name) in all)
            {
                if (name.Contains(p, StringComparison.OrdinalIgnoreCase))
                    return id;
            }
        }

        return all.Count > 0 ? all[0].Id : AcadDb.ObjectId.Null;
    }

    private static Dictionary<string, AcadDb.ObjectId> EnsureLayers(AcadDb.Transaction tr, AcadDb.Database db)
    {
        Dictionary<string, AcadDb.ObjectId> ids = new();
        AcadDb.LayerTable lt = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        foreach ((string name, short color) in new[] { (LayerAxis, (short)1), (LayerEdge, (short)4), (LayerCurb, (short)6) })
        {
            if (lt.Has(name))
            {
                ids[name] = lt[name];
                continue;
            }

            if (!lt.IsWriteEnabled)
                lt.UpgradeOpen();

            AcadDb.LayerTableRecord rec = new()
            {
                Name = name,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color)
            };
            ids[name] = lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        return ids;
    }
}
