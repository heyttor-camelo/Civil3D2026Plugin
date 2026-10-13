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

    private static readonly Dictionary<AcadDb.Database, bool> Active = new();

    internal sealed class Report
    {
        public int AxesCreated, AxesUpdated, EdgesCreated, EdgesKept, CurbsCreated, CurbsKept, Erased;
        public int CurbsConnected, CurbsSimple, CurbsFailed;
        public List<string> Messages { get; } = new();

        public override string ToString() =>
            $"Civil: eixos +{AxesCreated}/~{AxesUpdated} | bordos +{EdgesCreated} (mantidos {EdgesKept}) | " +
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
        AcadDb.ObjectId siteId = EnsureSite(tr, cdoc);
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
                            Math.Abs(t.S0 - e.S0) < Tol && Math.Abs(t.S1 - e.S1) < Tol && Math.Abs(t.Offset - e.Offset) < Tol)
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
                AcadDb.ObjectId id = CivilDb.Alignment.CreateOffsetAlignment(name, parentId, e.Offset, styleId, st0, st1);
                CivilDb.Alignment al = (CivilDb.Alignment)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                al.Layer = LayerEdge;
                SetTag(al, KindEdge, e.AxisKey, e.Side.ToString(CultureInfo.InvariantCulture), F(e.S0), F(e.S1), F(e.Offset), string.Empty, string.Empty);
                edgeIds[e] = id;
                report.EdgesCreated++;
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
                id = CivilDb.Alignment.CreateConnectedAlignment(name, siteId, layerId, styleId, labelId, p);
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
        ConnectionOverlapLengthIn = 0.0,
        ConnectionOverlapLengthOut = 0.0
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

            V2 t = s.TangentAt(0.0);
            try
            {
                al.Entities.AddFixedCurve(P3(s.A), new Vector3d(t.X, t.Y, 0.0), P3(s.B));
            }
            catch
            {
                // Arco quase reto: vira reta.
                al.Entities.AddFixedLine(P3(s.A), P3(s.B));
            }
        }
    }

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
