using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;

using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>
/// Segunda parte do MFTravessia: superficie de topo escolhida pelo usuario e uniao das travessias
/// de um cruzamento num plato so, limitado pelo meio-fio (MFTRAVESSIAUNIR).
///
/// A uniao: do centro do cruzamento saem raios; cada raio para na primeira linha que encontra
/// (borda da pista lida do corredor, ou a borda de fora da plataforma de uma travessia). Esses
/// pontos formam o contorno; o plato e uma malha em leque/aneis sobre a superficie de topo,
/// levantada na altura das travessias. Em cada travessia fica so a rampa de fora; a sarjeta dos
/// cantos continua livre, com tampa de concreto por cima (como nas travessias).
/// </summary>
internal static partial class MFTravessia
{
    // ------------------------------------------------------------------
    // Superficie de topo
    // ------------------------------------------------------------------

    /// <summary>Superficies do desenho (para o usuario escolher a de topo).</summary>
    public static List<(AcadDb.ObjectId Id, string Name)> Surfaces(AcadDb.Database db)
    {
        var list = new List<(AcadDb.ObjectId, string)>();
        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            var civil = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(db);
            foreach (AcadDb.ObjectId id in civil.GetSurfaceIds())
            {
                if (!id.IsErased && tr.GetObject(id, AcadDb.OpenMode.ForRead) is CivilDb.Surface s)
                    list.Add((id, s.Name));
            }
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso("MFTRAVESSIA", "Nao deu para listar as superficies: " + ex.Message);
        }
        return list.OrderBy(x => x.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Pista pela superficie escolhida (nome); sem nome ou sem achar, a de topo do corredor.</summary>
    public static Func<double, double, double?> RoadSurface(AcadDb.Database db, IEnumerable<AcadDb.ObjectId> corridorIds,
        string? surfaceName, out string description)
    {
        if (!string.IsNullOrWhiteSpace(surfaceName))
        {
            (AcadDb.ObjectId id, string name) = Surfaces(db).FirstOrDefault(x => x.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase));
            if (!id.IsNull)
            {
                description = "superficie de topo escolhida: " + name;
                return (x, y) =>
                {
                    try
                    {
                        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
                        double? z = null;
                        if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is CivilDb.Surface s)
                        {
                            double v = s.FindElevationAtXY(x, y);
                            if (!double.IsNaN(v))
                                z = v;
                        }
                        tr.Commit();
                        return z;
                    }
                    catch
                    {
                        return null; // fora da superficie
                    }
                };
            }
            Func<double, double, double?> auto = RoadSurface(db, corridorIds, out string autoInfo);
            description = $"superficie '{surfaceName}' nao existe mais no desenho; {autoInfo}";
            return auto;
        }
        return RoadSurface(db, corridorIds, out description);
    }

    // ------------------------------------------------------------------
    // Geometria de uma travessia guardada
    // ------------------------------------------------------------------

    public static AcadDb.ObjectId CorridorOf(AcadDb.Database db, string handle)
    {
        try
        {
            AcadDb.ObjectId id = db.GetObjectId(false, new AcadDb.Handle(Convert.ToInt64(handle, 16)), 0);
            return id.IsErased ? AcadDb.ObjectId.Null : id;
        }
        catch
        {
            return AcadDb.ObjectId.Null;
        }
    }

    /// <summary>Recalcula a geometria de uma travessia pelos dados guardados nela (corredor atual).</summary>
    public static TravessiaGeometry GeometryFor(AcadDb.Database db, Stored d, List<RebaixoInfo> rebaixos, out string roadInfo)
    {
        AcadDb.ObjectId ca = CorridorOf(db, d.CorridorA), cb = CorridorOf(db, d.CorridorB);
        if (ca.IsNull || cb.IsNull)
            throw new InvalidOperationException("corredor nao existe mais");
        if (!MFRebaixoCommands.TryStationAt(db, ca, d.BaselineA, d.AnchorA ?? d.Origin, out double stA))
            throw new InvalidOperationException($"eixo fora da baseline de {d.LabelA}");
        if (!MFRebaixoCommands.TryStationAt(db, cb, d.BaselineB, d.AnchorB ?? d.Origin, out double stB))
            throw new InvalidOperationException($"eixo fora da baseline de {d.LabelB}");
        double span = (d.Params.Plataforma / 2.0) + d.Params.Rampa + 4.0;
        TravessiaSide s1 = ReadSide(db, ca, d.BaselineA, d.SubassemblyA, d.LeftA, d.LabelA, d.Origin, stA, stA - span, stA + span);
        TravessiaSide s2 = ReadSide(db, cb, d.BaselineB, d.SubassemblyB, d.LeftB, d.LabelB, d.Origin, stB, stB - span, stB + span);
        RebaixoInfo? ra = NearRebaixo(rebaixos, ca, d.BaselineA, d.LeftA, stA), rb = NearRebaixo(rebaixos, cb, d.BaselineB, d.LeftB, stB);
        Func<double, double, double?> road = RoadSurface(db, new[] { ca, cb }, d.Params.Superficie, out roadInfo);
        return Build(s1, s2, d.Params, road, ra?.NormalHeight, rb?.NormalHeight, ra?.FinalHeight, rb?.FinalHeight);
    }

    // Pelo lado tambem: os dois lados da rua costumam estar na mesma baseline (MFSARJETA_ESQ/DIR).
    private static RebaixoInfo? NearRebaixo(List<RebaixoInfo> items, AcadDb.ObjectId corridorId, string baselineKey, bool left, double station) =>
        items.Where(r => r.CorridorId == corridorId && r.BaselineKey == baselineKey && r.Left == left && station >= r.S0 - 0.5 && station <= r.S3 + 0.5)
             .OrderBy(r => Math.Abs(((r.S1 + r.S2) / 2.0) - station))
             .FirstOrDefault();

    // ------------------------------------------------------------------
    // Meio-fio em volta do cruzamento
    // ------------------------------------------------------------------

    /// <summary>
    /// Linhas do meio-fio (MF/MFSARJETA) de todos os corredores a ate <paramref name="radius"/> do
    /// centro: borda da pista e pe do meio-fio, com elevacao absoluta. Uma lista por peca e trecho.
    /// </summary>
    private static List<List<CurbSample>> CurbLines(AcadDb.Database db, Point3d center, double radius, List<RebaixoInfo>? rebaixos = null)
    {
        var result = new List<List<CurbSample>>();
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (AcadDb.ObjectId cid in MFRebaixoData.CorridorIds(db, tr))
        {
            if (tr.GetObject(cid, AcadDb.OpenMode.ForRead) is not CivilCorridor corridor)
                continue;
            foreach (CivilDb.Baseline baseline in corridor.Baselines)
            {
                // Trecho da baseline perto do centro; se o centro nao projeta nela, a baseline inteira.
                double from = double.MinValue, to = double.MaxValue;
                if (MFRebaixoCommands.TryStationAt(db, cid, MFRebaixoCommands.GetBaselineKey(baseline), center, out double st))
                {
                    from = st - radius - 5.0;
                    to = st + radius + 5.0;
                }
                var lists = new Dictionary<AcadDb.ObjectId, List<CurbSample>>();
                var isMf = new Dictionary<AcadDb.ObjectId, bool>();
                try
                {
                    foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
                    {
                        if (Math.Max(region.StartStation, region.EndStation) < from || Math.Min(region.StartStation, region.EndStation) > to)
                            continue;
                        foreach (CivilDb.AppliedAssembly aa in region.AppliedAssemblies)
                        {
                            foreach (CivilDb.AppliedSubassembly asa in aa.GetAppliedSubassemblies())
                            {
                                Point3d origin;
                                try
                                {
                                    origin = asa.OriginStationOffsetElevationToBaseline;
                                }
                                catch
                                {
                                    continue;
                                }
                                if (origin.X < from || origin.X > to)
                                    break;
                                AcadDb.ObjectId said = asa.SubassemblyId;
                                if (!isMf.TryGetValue(said, out bool ok))
                                {
                                    ok = false;
                                    try
                                    {
                                        ok = tr.GetObject(said, AcadDb.OpenMode.ForRead) is CivilDb.Subassembly sa && MFRebaixoCommands.IsMfSubassemblyPublic(sa);
                                    }
                                    catch
                                    {
                                        // Subassembly apagada.
                                    }
                                    isMf[said] = ok;
                                }
                                if (!ok)
                                    continue;

                                double o0 = origin.Y;
                                double? footOff = null, footElev = null;
                                foreach (CivilDb.CalculatedLink link in asa.Links)
                                {
                                    bool gutter = false;
                                    foreach (string code in link.CorridorCodes)
                                    {
                                        if (!string.IsNullOrWhiteSpace(code) && code.Equals("Sarjeta", StringComparison.OrdinalIgnoreCase))
                                            gutter = true;
                                    }
                                    if (!gutter)
                                        continue;
                                    foreach (CivilDb.CalculatedPoint cp in link.CalculatedPoints)
                                    {
                                        Point3d soe = cp.StationOffsetElevationToBaseline;
                                        if (footOff is null || Math.Abs(soe.Y - o0) > Math.Abs(footOff.Value - o0))
                                        {
                                            footOff = soe.Y;
                                            footElev = soe.Z;
                                        }
                                    }
                                }
                                try
                                {
                                    Point3d edge = baseline.StationOffsetElevationToXYZ(new Point3d(origin.X, o0, origin.Z));
                                    if (Dist2d(edge, center) > radius + 5.0)
                                        continue;
                                    Point3d foot = edge;
                                    if (footOff is double fo && footElev is double fz)
                                        foot = baseline.StationOffsetElevationToXYZ(new Point3d(origin.X, fo, fz));
                                    if (!lists.TryGetValue(said, out var list))
                                        lists[said] = list = new List<CurbSample>();
                                    // Topo do meio-fio: linha d'agua (pe da face) + altura exposta da peca (2.122.0;
                                    // a uniao segue esse topo na borda do plato).
                                    double? curbTop = null;
                                    // 2.124.1: so no trecho rebaixado (S1-S2) de um rebaixo desta peca/baseline. Antes o "rebaixado"
                                    // era pela altura (ate 4 cm da do plato) e o meio-fio normal dos cantos (~15 cm x 12 cm) passava
                                    // por rebaixado: grelha em volta da uniao inteira (Heyvid, 17/11/2026).
                                    string bkey = MFRebaixoCommands.GetBaselineKey(baseline);
                                    string saName = "";
                                    try
                                    {
                                        saName = (tr.GetObject(said, AcadDb.OpenMode.ForRead) as CivilDb.Subassembly)?.Name ?? "";
                                    }
                                    catch
                                    {
                                        // Sem nome.
                                    }
                                    bool lowered = rebaixos is null || rebaixos.Any(r => r.CorridorId == cid && r.BaselineKey == bkey &&
                                        r.SubassemblyName.Equals(saName, StringComparison.OrdinalIgnoreCase) &&
                                        origin.X >= Math.Min(r.S1, r.S2) - 0.01 && origin.X <= Math.Max(r.S1, r.S2) + 0.01);
                                    if (footElev is not null && lowered)
                                    {
                                        foreach (CivilDb.IAppliedSubassemblyParam prm in asa.Parameters)
                                        {
                                            if (!prm.KeyName.Equals(MFRebaixoCommands.EffectiveOutputAltParameterName, StringComparison.OrdinalIgnoreCase))
                                                continue;
                                            try
                                            {
                                                curbTop = foot.Z + Convert.ToDouble(prm.ValueAsObject, System.Globalization.CultureInfo.InvariantCulture);
                                            }
                                            catch
                                            {
                                                // Sem altura exposta.
                                            }
                                        }
                                    }
                                    list.Add(new CurbSample(origin.X, edge, foot, curbTop));
                                }
                                catch
                                {
                                    // Estaca fora da baseline.
                                }
                            }
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    PluginLog.Aviso("MFTRAVESSIA", $"Meio-fio de {corridor.Name}/{baseline.Name} nao lido: {ex.Message}");
                }

                // Em ordem de estaca; quebra onde ha um salto (outra region, trecho sem meio-fio).
                foreach (List<CurbSample> raw in lists.Values)
                {
                    var current = new List<CurbSample>();
                    foreach (CurbSample s in raw.OrderBy(x => x.Station))
                    {
                        if (current.Count > 0 && s.Station - current[^1].Station < 1e-5)
                            continue;
                        if (current.Count > 0 && Dist2d(s.Edge, current[^1].Edge) > 12.0)
                        {
                            if (current.Count > 1)
                                result.Add(current);
                            current = new List<CurbSample>();
                        }
                        current.Add(s);
                    }
                    if (current.Count > 1)
                        result.Add(current);
                }
            }
        }
        tr.Commit();
        return result;
    }

    // ------------------------------------------------------------------
    // Uniao
    // ------------------------------------------------------------------

    private readonly record struct Seg(Point3d A, Point3d B, int Member, double? TopA = null, double? TopB = null);

    /// <summary>Topo da plataforma da travessia na linha <paramref name="row"/>, no ponto da borda mais perto de <paramref name="q"/>.</summary>
    private static double? PlatformTopAt(TravessiaGeometry g, int row, Point3d q)
    {
        int cols = g.Top.GetLength(1);
        if (row < 0 || row >= g.Top.GetLength(0) || cols < 2)
            return null;
        double best = double.MaxValue;
        double? z = null;
        for (int j = 0; j + 1 < cols; j++)
        {
            Point3d a = g.Top[row, j], b = g.Top[row, j + 1];
            double ex = b.X - a.X, ey = b.Y - a.Y, l2 = (ex * ex) + (ey * ey);
            double w = l2 < 1e-12 ? 0 : Math.Clamp((((q.X - a.X) * ex) + ((q.Y - a.Y) * ey)) / l2, 0, 1);
            double px = a.X + (ex * w), py = a.Y + (ey * w);
            double d = Math.Sqrt(((px - q.X) * (px - q.X)) + ((py - q.Y) * (py - q.Y)));
            if (d < best)
            {
                best = d;
                z = a.Z + ((b.Z - a.Z) * w);
            }
        }
        return best < 0.5 ? z : null;
    }

    /// <summary>Nivel da borda do plato da uniao x topo do meio-fio (2.122.0).</summary>
    internal sealed record UnionCurbLevel(double? Min, double? Max, int Points, int NotLowered, string Text, List<string> Warnings)
    {
        public double? Worst => Min is double a && Max is double b ? Math.Max(Math.Abs(a), Math.Abs(b)) : null;
    }

    /// <summary>
    /// Resume a diferenca topo do meio-fio - topo do plato nos pontos da borda que encostam no meio-fio; os trechos
    /// seguidos de meio-fio nao rebaixado viram aviso com o rebaixo mais perto (o que aumentar/lancar).
    /// </summary>
    private static UnionCurbLevel CurbLevel(AcadDb.Database db, List<Point3d> edge, double?[] diff, bool[] notLowered, double tol)
    {
        var warns = new List<string>();
        var vals = diff.Where(x => x is not null).Select(x => x!.Value).ToList();
        if (vals.Count == 0)
            return new UnionCurbLevel(null, null, 0, notLowered.Count(x => x), "nivel com o meio-fio rebaixado: nenhum ponto da borda encosta em meio-fio rebaixado", warns);
        double mn = vals.Min(), mx = vals.Max();
        int nl = notLowered.Count(x => x);
        List<RebaixoInfo>? all = null;
        string Near(Point3d q)
        {
            try
            {
                all ??= MFRebaixoData.ReadAll(db);
                RebaixoInfo? best = all.Where(r => r.Path.Count > 0).OrderBy(r => r.Path.Min(pp => Dist2d(pp, q))).FirstOrDefault();
                return best is null ? "" : $" (rebaixo mais perto: {best.Label}, a {N(best.Path.Min(pp => Dist2d(pp, q)))} m)";
            }
            catch
            {
                return "";
            }
        }
        // Trechos seguidos fora da tolerancia.
        int n = edge.Count;
        var run = new List<int>();
        void Flush()
        {
            if (run.Count == 0)
                return;
            double worst = run.Select(i => diff[i]!.Value).OrderByDescending(Math.Abs).First();
            bool high = false;
            Point3d mid = edge[run[run.Count / 2]];
            double len = 0;
            for (int k = 1; k < run.Count; k++)
                len += Dist2d(edge[run[k - 1]], edge[run[k]]);
            warns.Add((high
                ? $"borda do plato encostando em meio-fio nao rebaixado em {N(len)} m: meio-fio {Cm(worst)} {(worst > 0 ? "acima" : "abaixo")} do plato"
                : $"borda do plato {Cm(Math.Abs(worst))} {(worst > 0 ? "abaixo" : "acima")} do meio-fio em {N(len)} m") +
                $" perto de X {N(mid.X)} Y {N(mid.Y)}{Near(mid)}" +
                (high ? "; aumente esse rebaixo ate o canto (ou lance um rebaixo ali)" : "; refaca a uniao depois de acertar o rebaixo"));
            run.Clear();
        }
        for (int i = 0; i < n; i++)
        {
            bool bad = diff[i] is double d && Math.Abs(d) > tol;
            if (!bad)
                Flush();
            else
                run.Add(i);
        }
        Flush();
        string text = (Math.Max(Math.Abs(mn), Math.Abs(mx)) <= tol
            ? $"nivel com o meio-fio rebaixado: borda do plato rente (de {Cm(mn)} a {Cm(mx)}, {vals.Count} pontos)"
            : $"nivel com o meio-fio rebaixado: de {Cm(mn)} a {Cm(mx)} ({vals.Count} pontos; tolerancia {Cm(tol)})") +
            (nl > 0 ? $"; {nl} ponto(s) da borda encostam em meio-fio nao rebaixado (cantos): ali o plato segue a superficie + altura" : "");
        return new UnionCurbLevel(mn, mx, vals.Count, nl, text, warns);
    }

    /// <summary>Raio do centro: primeiro cruzamento com os segmentos (distancia, ponto com Z, segmento).</summary>
    private static (double S, Point3d P, int Index)? Hit(List<Seg> segs, Point3d c, double dx, double dy, double minS)
    {
        (double, Point3d, int)? best = null;
        double bestS = double.MaxValue;
        for (int i = 0; i < segs.Count; i++)
        {
            Point3d a = segs[i].A, b = segs[i].B;
            double ex = b.X - a.X, ey = b.Y - a.Y;
            double den = (dx * (-ey)) - (dy * (-ex));
            if (Math.Abs(den) < 1e-12)
                continue;
            double rx = a.X - c.X, ry = a.Y - c.Y;
            double s = ((rx * (-ey)) - (ry * (-ex))) / den;
            double w = ((dx * ry) - (dy * rx)) / den;
            if (s < minS || s >= bestS || w < -1e-6 || w > 1 + 1e-6)
                continue;
            w = Math.Clamp(w, 0, 1);
            bestS = s;
            best = (s, new Point3d(a.X + (ex * w), a.Y + (ey * w), a.Z + ((b.Z - a.Z) * w)), i);
        }
        return best;
    }

    /// <summary>
    /// Une as travessias (ja com a geometria recalculada) num plato so e redesenha cada uma so com a
    /// rampa de fora. Devolve o resumo. Abre as proprias transacoes.
    /// </summary>
    public static string Unite(AcadDb.Database db, string unionId, List<(Stored Data, TravessiaGeometry Geo)> members, MFRebaixoSettings settings)
    {
        if (members.Count < 2)
            throw new InvalidOperationException("A uniao precisa de pelo menos duas travessias.");

        Point3d c = new(members.Average(m => m.Geo.Origin.X), members.Average(m => m.Geo.Origin.Y), 0);
        double radius = members.Max(m => Dist2d(m.Geo.Origin, c) + m.Geo.Total + (m.Geo.WidthAtAxis / 2.0)) + 10.0;
        double height = members.Average(m => (m.Geo.HeightA + m.Geo.HeightB) / 2.0);

        // Borda de fora da plataforma de cada travessia (onde comeca a rampa que fica).
        var edges = new List<Seg>();
        var feet = new List<Seg>();
        var memberRows = new List<int>();
        for (int k = 0; k < members.Count; k++)
        {
            (Stored d, TravessiaGeometry g) = members[k];
            int sign = (g.Origin - c).DotProduct(g.Along) >= 0 ? 1 : -1;
            d.UnionId = unionId;
            d.OuterSign = sign;
            int row = Enumerable.Range(0, g.Rows.Count).OrderBy(i => Math.Abs(g.Rows[i] - (sign * g.HalfPlatform))).First();
            memberRows.Add(row);
            Point3d a = g.EdgeA[row], b = g.EdgeB[row];
            Vector3d dir = b - a;
            Vector3d ext = dir.Length > 1e-6 ? dir.GetNormal() * 0.03 : new Vector3d();
            edges.Add(new Seg(a - ext, b + ext, k));
        }

        List<List<CurbSample>> curbs = CurbLines(db, c, radius, MFRebaixoData.ReadAll(db));
        foreach (List<CurbSample> line in curbs)
        {
            for (int i = 0; i + 1 < line.Count; i++)
            {
                edges.Add(new Seg(line[i].Edge, line[i + 1].Edge, -1, line[i].Top, line[i + 1].Top));
                feet.Add(new Seg(line[i].Foot, line[i + 1].Foot, -1));
            }
        }
        if (curbs.Count == 0)
            throw new InvalidOperationException("Nao achei meio-fio do corredor em volta do cruzamento.");

        // Rumos: a cada 3 graus e na direcao de cada vertice (para nao cortar os cantos).
        var angles = new List<double>();
        for (int i = 0; i < 120; i++)
            angles.Add(i * (2.0 * Math.PI / 120.0));
        foreach (Seg s in edges)
        {
            foreach (Point3d q in new[] { s.A, s.B })
            {
                if (Dist2d(q, c) <= radius)
                {
                    double ang = Math.Atan2(q.Y - c.Y, q.X - c.X);
                    angles.Add(ang < 0 ? ang + (2.0 * Math.PI) : ang);
                }
            }
        }
        angles.Sort();
        // EdgeH: altura do plato acima da pista na borda deste rumo (2.122.0). Na travessia, a altura dela
        // (lado A -> lado B); no meio-fio, o topo do meio-fio rebaixado (CurbTop) - antes era uma altura so.
        var rays = new List<(double Ang, Point3d P, int Member, Point3d? Foot, double EdgeH, double? CurbTop)>();
        double last = -1;
        int open = 0;
        foreach (double ang in angles)
        {
            if (ang - last < 0.001)
                continue;
            last = ang;
            double dx = Math.Cos(ang), dy = Math.Sin(ang);
            var hit = Hit(edges, c, dx, dy, 0.05);
            if (hit is null || hit.Value.S > radius)
            {
                open++;
                continue;
            }
            int member = edges[hit.Value.Index].Member;
            Point3d? foot = null;
            if (member < 0)
            {
                var fh = Hit(feet, c, dx, dy, hit.Value.S - 0.01);
                if (fh is not null && fh.Value.S - hit.Value.S is > 0.05 and < 1.5)
                    foot = fh.Value.P;
            }
            Seg sg = edges[hit.Value.Index];
            double len = Dist2d(sg.A, sg.B);
            double wSeg = len < 1e-9 ? 0 : Math.Clamp(Dist2d(sg.A, hit.Value.P) / len, 0, 1);
            double edgeH = height;
            double? curbTop = null;
            if (member >= 0)
            {
                TravessiaGeometry mg = members[member].Geo;
                edgeH = mg.HeightA + ((mg.HeightB - mg.HeightA) * wSeg);
            }
            else if (sg.TopA is double ta && sg.TopB is double tb)
            {
                curbTop = ta + ((tb - ta) * wSeg);
            }
            rays.Add((ang, hit.Value.P, member, foot, edgeH, curbTop));
        }
        if (open > angles.Count / 20 || rays.Count < 12)
            throw new InvalidOperationException($"O contorno do cruzamento esta aberto em {open} rumo(s): falta meio-fio do corredor em algum canto (ou alguma travessia do cruzamento ficou de fora da selecao).");

        Func<double, double, double?> road = RoadSurface(db, members.SelectMany(m => new[] { CorridorOf(db, m.Data.CorridorA), CorridorOf(db, m.Data.CorridorB) }).Where(id => !id.IsNull),
            members[0].Data.Params.Superficie, out string roadInfo);

        // Grade em leque: aneis a cada ~1 m.
        int n = rays.Count;
        double maxR = rays.Max(r => Dist2d(r.P, c));
        int rings = Math.Max(2, (int)Math.Ceiling(maxR / 1.0));
        double zc = road(c.X, c.Y) ?? rays.Average(r => r.P.Z);
        var top = new Point3d[n, rings + 1];
        var bottom = new Point3d[n, rings + 1];
        int fromSurface = 0;
        // Topo do plato da uniao (2.122.0, diretriz do Heyvid em 17/11/2026: "pedi para ter uma superficie de topo como
        // referencia; o solido so precisa atender estritamente as alturas nas regioes de rebaixo do meio-fio" e, longe
        // dele, "segue a superficie, com a mesma altura do rebaixo de preferencia"):
        //  - em cada ponto: superficie de topo + altura; a altura e a das travessias (a do rebaixo), indo de uma para a
        //    outra pelo angulo em volta do centro (nada de uma altura unica/media);
        //  - na borda de cada travessia: o topo da plataforma dela, ponto a ponto (a rampa sai da mesma aresta);
        //  - na borda com meio-fio rebaixado: o topo do meio-fio (se estiver a ate TravessiaAjusteMaxMeioFio da altura ali);
        //  - na borda com meio-fio nao rebaixado (cantos entre rebaixos): superficie + altura, sem forcar (so informa).
        double follow = Math.Max(0.0, settings.TravessiaAjusteMaxMeioFio);
        double tol = Math.Max(0.001, settings.TravessiaToleranciaMeioFio);
        var edgeHs = new double[n];
        var curbDiff = new double?[n];
        var notLowered = new bool[n];
        var isMember = new bool[n];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k <= rings; k++)
            {
                double f = k / (double)rings;
                double x = c.X + ((rays[i].P.X - c.X) * f), y = c.Y + ((rays[i].P.Y - c.Y) * f);
                double zLine = zc + ((rays[i].P.Z - zc) * f);
                double z = zLine;
                // Na borda da pista vale a elevacao do corredor (a tampa encosta ali).
                if (k < rings || rays[i].Member >= 0)
                {
                    if (road(x, y) is double v && Math.Abs(v - zLine) < 1.0)
                    {
                        z = v;
                        fromSurface++;
                    }
                }
                bottom[i, k] = new Point3d(x, y, z);
            }
            // Borda de travessia: topo da plataforma dela na linha de fora, no ponto mais perto (interpolado).
            edgeHs[i] = rays[i].EdgeH;
            if (rays[i].Member >= 0)
            {
                isMember[i] = true;
                TravessiaGeometry mg = members[rays[i].Member].Geo;
                int row = memberRows[rays[i].Member];
                if (PlatformTopAt(mg, row, rays[i].P) is double zt)
                    edgeHs[i] = zt - bottom[i, rings].Z;
            }
        }
        // Altura nos rumos de meio-fio: a das travessias vizinhas pelo angulo (vai de uma para a outra).
        if (isMember.Any(x => x))
        {
            for (int i = 0; i < n; i++)
            {
                if (isMember[i])
                    continue;
                int prev = -1, next = -1;
                for (int s = 1; s < n && (prev < 0 || next < 0); s++)
                {
                    if (prev < 0 && isMember[((i - s) % n + n) % n])
                        prev = ((i - s) % n + n) % n;
                    if (next < 0 && isMember[(i + s) % n])
                        next = (i + s) % n;
                }
                double Gap(int a, int b) { double d = rays[b].Ang - rays[a].Ang; while (d < 0) d += 2.0 * Math.PI; return d; }
                double ga = Gap(prev, i), gb = Gap(i, next);
                edgeHs[i] = ga + gb < 1e-9 ? edgeHs[prev] : edgeHs[prev] + ((edgeHs[next] - edgeHs[prev]) * (ga / (ga + gb)));
            }
        }
        double hCenter = isMember.Any(x => x) ? Enumerable.Range(0, n).Where(i => isMember[i]).Average(i => edgeHs[i]) : height;
        // Meio-fio rebaixado: a borda encosta no topo dele.
        for (int i = 0; i < n; i++)
        {
            if (rays[i].Member >= 0)
                continue;
            if (rays[i].CurbTop is not double ct)
            {
                notLowered[i] = true;
                continue;
            }
            double hc = ct - bottom[i, rings].Z;
            // So chega aqui meio-fio de trecho rebaixado (CurbLines): a borda segue o topo dele; alem do ajuste maximo
            // (rebaixo numa altura bem diferente da lombofaixa) segue tambem, e a conferencia mostra a diferenca.
            edgeHs[i] = Math.Max(0.005, hc);
            curbDiff[i] = ct - (bottom[i, rings].Z + edgeHs[i]);
            if (Math.Abs(hc - edgeHs[i]) > follow + 1e-9)
                notLowered[i] = false;
        }
        // 2.124.0 (Heyvid: "segue a superficie, com a mesma altura do rebaixo ... ou seja, fica paralela"): no miolo,
        // superficie + altura do rebaixo (a da travessia daquele lado, passando de uma para a outra pelo angulo); so a
        // faixa TravessiaFaixaTransicao junto da borda vai ate o topo do meio-fio rebaixado / da plataforma de entrada.
        double band = Math.Max(0.05, settings.TravessiaFaixaTransicao);
        var baseH = new double[n];
        for (int i = 0; i < n; i++)
            baseH[i] = rays[i].Member >= 0 ? members[rays[i].Member].Data.Params.Altura : double.NaN;
        if (isMember.Any(x => x))
        {
            for (int i = 0; i < n; i++)
            {
                if (!double.IsNaN(baseH[i]))
                    continue;
                int prev = -1, next = -1;
                for (int s = 1; s < n && (prev < 0 || next < 0); s++)
                {
                    if (prev < 0 && isMember[((i - s) % n + n) % n])
                        prev = ((i - s) % n + n) % n;
                    if (next < 0 && isMember[(i + s) % n])
                        next = (i + s) % n;
                }
                double Gap2(int a, int b) { double d = rays[b].Ang - rays[a].Ang; while (d < 0) d += 2.0 * Math.PI; return d; }
                double ga = Gap2(prev, i), gb = Gap2(i, next);
                double hp = members[rays[prev].Member].Data.Params.Altura, hn = members[rays[next].Member].Data.Params.Altura;
                baseH[i] = ga + gb < 1e-9 ? hp : hp + ((hn - hp) * (ga / (ga + gb)));
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
                baseH[i] = members[0].Data.Params.Altura;
        }
        double baseC = baseH.Average();
        for (int i = 0; i < n; i++)
        {
            double len = Dist2d(rays[i].P, c);
            for (int k = 0; k <= rings; k++)
            {
                double f = k / (double)rings;
                Point3d b0 = bottom[i, k];
                double bh = baseC + ((baseH[i] - baseC) * f);
                double h = TravessiaGeometry.EdgeBlend(bh, edgeHs[i], (1.0 - f) * len, bh, double.MaxValue, band);
                top[i, k] = new Point3d(b0.X, b0.Y, b0.Z + Math.Max(0.005, h));
            }
        }
        hCenter = baseC;
        double inMin = double.MaxValue, inMax = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double len = Dist2d(rays[i].P, c);
            for (int k = 0; k <= rings; k++)
                if ((1.0 - (k / (double)rings)) * len > band + 1e-6)
                {
                    double hh = top[i, k].Z - bottom[i, k].Z;
                    inMin = Math.Min(inMin, hh);
                    inMax = Math.Max(inMax, hh);
                }
        }
        string coreInfo = inMin <= inMax
            ? $"miolo do plato (fora da faixa de {N(band)} m das bordas): {Cm(inMin)} a {Cm(inMax)} acima da superficie de topo"
            : "";
        // Para comparar (log): o metodo de antes (altura unica = media) dava degrau na borda das rampas e no meio-fio.
        double oldStepMax = 0, oldCurbMax = 0, newStepMax = 0;
        for (int i = 0; i < n; i++)
        {
            double oldTop = bottom[i, rings].Z + height;
            if (isMember[i])
            {
                oldStepMax = Math.Max(oldStepMax, Math.Abs((bottom[i, rings].Z + edgeHs[i]) - oldTop));
                TravessiaGeometry mg = members[rays[i].Member].Geo;
                if (PlatformTopAt(mg, memberRows[rays[i].Member], rays[i].P) is double zt)
                    newStepMax = Math.Max(newStepMax, Math.Abs(zt - top[i, rings].Z));
            }
            else if (curbDiff[i] is not null && rays[i].CurbTop is double ct2)
            {
                oldCurbMax = Math.Max(oldCurbMax, Math.Abs(ct2 - oldTop));
            }
        }
        PluginLog.Info("MFTRAVESSIA", $"Uniao {unionId}: altura unica antiga {Cm(height)} -> degrau rampa x plato {Cm(oldStepMax)}, meio-fio rebaixado x plato {Cm(oldCurbMax)}; agora degrau rampa x plato {Cm(newStepMax)}, altura no centro {Cm(hCenter)}.");
        height = hCenter;

        // Conferencia do nivel na borda que encosta no meio-fio rebaixado (o que a grelha liga).
        UnionCurbLevel level = CurbLevel(db, rays.Select(r => r.P).ToList(), curbDiff, notLowered, tol);
        int count = 0;
        var notes = new List<string>();
        if (coreInfo.Length > 0)
            notes.Add(coreInfo);

        string layer = string.IsNullOrWhiteSpace(settings.TravessiaLayer) ? "MF-TRAVESSIA" : settings.TravessiaLayer.Trim();
        string layer3d = layer + "-3D";
        bool grelha = members[0].Data.Params.Grelha;
        string layerLid = layer + (grelha ? "-GRELHA" : "-TAMPA");
        int lids = 0;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            // Editar sem recriar: as pecas antigas ficam ate o fim e as novas assumem os handles delas
            // (como nas travessias; antes a uniao era apagada e criada de novo, 17/11/2026).
            Dictionary<string, List<AcadDb.ObjectId>> oldParts = PartsOf(tr, db, unionId);
            var oldSet = new HashSet<AcadDb.ObjectId>(oldParts.Values.SelectMany(x => x));
            var unionInfo = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            EnsureRegApp(tr, db);
            EnsureLayer(tr, db, layer, 8, "Travessias elevadas/semi-elevadas entre rebaixos (planta)");
            EnsureLayer(tr, db, layer3d, 8, "Travessias entre rebaixos: solidos do plato");
            EnsureLayer(tr, db, layerLid, grelha ? (short)252 : (short)40, grelha ? "Travessias entre rebaixos: grelha metalica sobre a sarjeta" : "Travessias entre rebaixos: tampa de concreto sobre a sarjeta");
            var bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
            var ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);
            var hatches = new AcadDb.ObjectIdCollection();

            // 2.125.0: plato pela triangulacao da superficie de topo (TIN temporaria); sem ela, o solido em leque de antes.
            AcadDb.ObjectId surfId = Surfaces(db).FirstOrDefault(x => x.Name.Equals(members[0].Data.Params.Superficie ?? "", StringComparison.OrdinalIgnoreCase)).Id;
            var edgeTopL = Enumerable.Range(0, n).Select(i => top[i, rings]).ToList();
            var edgeBotL = Enumerable.Range(0, n).Select(i => bottom[i, rings]).ToList();
            string mode = "";
            AcadDb.Entity? solid = TinSolid(tr, db, ms, layer3d, surfId, c, edgeTopL, edgeBotL, rays.Select(r => r.Ang).ToList(), baseH, edgeHs, band, road, out mode)
                                   ?? FanSolid(tr, ms, top, bottom, layer3d, out mode);
            if (solid is not null)
            {
                solid.XData = PartXData(unionId, "UNIAO_PLATO");
                count++;
                notes.Add("plato: " + mode);
                if (solid is AcadDb.Solid3d s3)
                {
                    var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ID"] = unionId,
                        ["Versao"] = C3DVersions.MfRebaixo,
                        ["Objeto"] = $"Plato do cruzamento (uniao de {members.Count} travessias)",
                        ["Tipo"] = members[0].Data.Params.Tipo == TravessiaTipo.Elevada ? "Travessia elevada" : "Travessia semi-elevada",
                        ["Altura_Plato"] = Math.Round(height, 3),
                        ["Largura_Plataforma"] = 0.0,
                        ["Comprimento_Rampa"] = members[0].Data.Params.Rampa,
                        ["Extensao_Travessia"] = Math.Round(2.0 * maxR, 3),
                        ["Elevacao_Topo_Centro"] = Math.Round(zc + height, 3),
                        ["Desnivel_MeioFio_Max"] = level.Worst is double wv ? Math.Round(wv, 3) : 0.0,
                        ["Tampa_Sarjeta"] = !members[0].Data.Params.Tampa ? "Sem cobertura" : grelha ? $"Grelha metalica, {N(members[0].Data.Params.GrelhaEspessura)} m" : $"Concreto, {N(members[0].Data.Params.TampaEspessura)} m",
                        ["Rebaixo_A"] = string.Join(", ", members.Select(m => m.Data.LabelA)),
                        ["Rebaixo_B"] = string.Join(", ", members.Select(m => m.Data.LabelB)),
                        ["Material"] = "Concreto"
                    };
                    unionInfo["UNIAO_PLATO"] = values;
                }
            }

            // Planta: o contorno inteiro.
            count += Hatch(tr, db, ms, rays.Select(r => r.P).ToList(), layer, "SOLID", 1, 0, 256, unionId, "UNIAO_PLANTA", hatches);

            // Tampas nos cantos: trechos seguidos de meio-fio com sarjeta, fora das travessias (elas tem a sua).
            TravessiaParams p0 = members[0].Data.Params;
            if (p0.Tampa)
            {
                bool Covered(Point3d q) => members.Any(m =>
                    Math.Abs((q - m.Geo.Origin).DotProduct(m.Geo.Along)) <= m.Geo.HalfPlatform + 0.02 &&
                    Math.Abs((q - m.Geo.Origin).DotProduct(m.Geo.Across)) <= (m.Geo.WidthAtAxis / 2.0) + 1.5);
                var run = new List<int>();
                void Flush()
                {
                    if (run.Count >= 2)
                    {
                        var lt = new Point3d[run.Count, 2];
                        var lb = new Point3d[run.Count, 2];
                        var plan = new List<Point3d>();
                        for (int k = 0; k < run.Count; k++)
                        {
                            Point3d e = rays[run[k]].P, f = rays[run[k]].Foot!.Value;
                            // Grelha/tampa do canto na altura da borda do plato ali (que segue o meio-fio rebaixado).
                            double z = e.Z + edgeHs[run[k]];
                            lt[k, 0] = new Point3d(e.X, e.Y, z);
                            lt[k, 1] = new Point3d(f.X, f.Y, z);
                            lb[k, 0] = new Point3d(e.X, e.Y, z - p0.LidThickness);
                            lb[k, 1] = new Point3d(f.X, f.Y, z - p0.LidThickness);
                            plan.Add(e);
                        }
                        for (int k = run.Count - 1; k >= 0; k--)
                            plan.Add(rays[run[k]].Foot!.Value);
                        // Grelha do canto em X como a das travessias (antes chapa simples; IFC de 13/11/2026).
                        int bars = 0;
                        double freeArea = 0;
                        AcadDb.Entity? lid = grelha
                            ? GratingX(tr, ms, lt, lb, p0.GrelhaBarra, p0.GrelhaVao, layerLid, out string _, out bars, out freeArea)
                            : MeshSolid(tr, ms, lt, lb, layerLid, out _);
                        if (lid is not null)
                        {
                            lid.XData = PartXData(unionId, "UNIAO_TAMPA");
                            if (lid is AcadDb.Solid3d)
                                unionInfo["UNIAO_TAMPA"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["Barras"] = bars,
                                    ["Area_Livre"] = Math.Round(freeArea, 3),
                                    ["Altura_Espessura"] = Math.Round(p0.LidThickness, 3),
                                    ["ID"] = unionId,
                                    ["Versao"] = C3DVersions.MfRebaixo,
                                    ["Objeto"] = grelha ? "Grelha metalica sobre a sarjeta (canto do cruzamento)" : "Tampa de concreto sobre a sarjeta (canto do cruzamento)",
                                    ["Tipo"] = p0.Tipo == TravessiaTipo.Elevada ? "Travessia elevada" : "Travessia semi-elevada",
                                    ["Altura_Plato"] = Math.Round(height, 3),
                                    ["Tampa_Sarjeta"] = grelha ? $"Grelha metalica, {N(p0.GrelhaEspessura)} m" : $"Concreto, {N(p0.TampaEspessura)} m",
                                    ["Material"] = grelha ? "Grelha metalica" : "Concreto"
                                };
                            count++;
                            lids++;
                        }
                        count += Hatch(tr, db, ms, plan, layerLid, "ANSI37", 0.03, 0, 256, unionId, "UNIAO_PLANTA_TAMPA", hatches);
                    }
                    run.Clear();
                }
                for (int i = 0; i < n; i++)
                {
                    // 2.123.0 (pedido do Heyvid em 17/11/2026): grelha so onde o meio-fio e rebaixado; nos cantos com meio-fio
                    // nao rebaixado o plato termina na borda da pista, sem cobrir a sarjeta (opcao TravessiaGrelhaCantos volta a cobrir).
                    bool lowered = settings.TravessiaGrelhaCantos || curbDiff[i] is not null;
                    bool ok = rays[i].Member < 0 && rays[i].Foot is not null && !Covered(rays[i].P) && lowered
                              && (run.Count == 0 || Dist2d(rays[i].P, rays[run[^1]].P) < 6.0);
                    if (!ok)
                        Flush();
                    if (rays[i].Member < 0 && rays[i].Foot is not null && !Covered(rays[i].P) && lowered)
                        run.Add(i);
                }
                Flush();
            }

            try
            {
                if (hatches.Count > 0)
                {
                    var order = (AcadDb.DrawOrderTable)tr.GetObject(ms.DrawOrderTableId, AcadDb.OpenMode.ForWrite);
                    order.MoveToTop(hatches);
                    if (MFRebaixoSettingsStore.Load().TravessiaPlantaOculta)
                    {
                        foreach (AcadDb.ObjectId hid in hatches)
                        {
                            if (tr.GetObject(hid, AcadDb.OpenMode.ForWrite) is AcadDb.Hatch hh)
                                hh.Visible = false;
                        }
                    }
                }
            }
            catch
            {
                // Ordem de desenho nao alterada.
            }

            // Pecas novas assumem os handles das antigas; depois os Property Sets nas pecas finais.
            (int reused, int erasedOld) = Reuse(tr, db, unionId, oldParts, oldSet);
            if (reused > 0 || erasedOld > 0)
                notes.Add($"uniao editada no lugar: {reused} peca(s) mantiveram o handle, {erasedOld} sairam");
            foreach ((string part, List<AcadDb.ObjectId> ids) in PartsOf(tr, db, unionId))
            {
                if (!unionInfo.TryGetValue(part, out Dictionary<string, object>? values))
                    continue;
                foreach (AcadDb.ObjectId oid in ids)
                {
                    if (tr.GetObject(oid, AcadDb.OpenMode.ForWrite) is not AcadDb.Solid3d s3)
                        continue;
                    var v = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase)
                    {
                        ["Peca"] = part,
                        ["Classe_IFC"] = IfcClassFor(part)
                    };
                    try
                    {
                        v["Volume"] = Math.Round(s3.MassProperties.Volume, 4);
                    }
                    catch
                    {
                        // Sem volume.
                    }
                    if (!TryPropertySet(tr, db, s3, v, out string psMsg))
                        notes.Add($"Property Set de {part} nao gravado: " + psMsg);
                }
            }
            tr.Commit();
        }

        // Depois do plato, cada travessia e redesenhada so com a rampa de fora (a faixa e os
        // triangulos dela ficam por cima da hachura do plato). O nivel com o meio-fio e o da uniao.
        foreach ((Stored d, TravessiaGeometry g) in members)
        {
            d.CurbLevelMin = level.Min;
            d.CurbLevelMax = level.Max;
            d.CurbNotLowered = level.NotLowered;
            count += Draw(db, g, d, settings).Entities;
        }
        notes.Add(level.Text);
        foreach (string w in level.Warnings)
            notes.Add("AVISO " + w);

        string summary = $"Uniao {unionId}: {members.Count} travessias, contorno com {n} pontos (raio ate {N(maxR)} m), altura {Cm(height)}, {lids} tampa(s) nos cantos, {count} objeto(s). " +
                         $"{roadInfo} ({fromSurface} pontos pela superficie). {string.Join("; ", notes)}" + (open > 0 ? $" AVISO: {open} rumo(s) sem meio-fio foram pulados." : "");
        summary += " " + Summary(db, unionId);
        PluginLog.Info("MFTRAVESSIA", summary);
        return summary;
    }

    /// <summary>
    /// Solido em leque: top[i, k] e o ponto do raio i no anel k (k = 0 e o centro, igual em todos os
    /// raios). Fecha o giro (ultimo raio liga no primeiro) e a parede no anel de fora.
    /// </summary>
    /// <summary>
    /// Plato da uniao com os triangulos REAIS da superficie de topo (2.125.1; pedidos do Heyvid em 17/11/2026: "nao tem como
    /// recortar e aproveitar a triangulacao da superficie?" e, depois da 2.125.0, "o acabamento ficou ruim"). Cada triangulo
    /// da superficie e recortado pelo contorno da uniao (sem retriangular), o pedaco que sobra e dividido em poucos triangulos
    /// e vertices a menos de 2 cm sao fundidos (sem lascas). Topo = superficie + altura, a altura indo suave de borda a borda
    /// (media ponderada pela distancia das alturas da borda: na borda e exatamente a da travessia/meio-fio). Fundo = a
    /// superficie; laterais verticais; faces planas vizinhas fundidas no solido (CleanBody). Null se nao der.
    /// </summary>
    private static AcadDb.Entity? TinSolid(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockTableRecord ms, string layer,
        AcadDb.ObjectId surfaceId, Point3d c, List<Point3d> edgeTop, List<Point3d> edgeBottom, List<double> angles, double[] baseH, double[] edgeH,
        double band, Func<double, double, double?> road, out string mode)
    {
        mode = "";
        int n = edgeTop.Count;
        if (n < 3 || surfaceId.IsNull || tr.GetObject(surfaceId, AcadDb.OpenMode.ForRead) is not CivilDb.TinSurface src)
            return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Contorno (sem pontos a menos de 2 cm), no sentido anti-horario.
            var poly = new List<(double X, double Y, double H)>();
            for (int i = 0; i < n; i++)
            {
                Point3d p = edgeTop[i];
                if (poly.Count > 0 && Math.Abs(poly[^1].X - p.X) < 0.02 && Math.Abs(poly[^1].Y - p.Y) < 0.02)
                    continue;
                poly.Add((p.X, p.Y, edgeTop[i].Z - edgeBottom[i].Z));
            }
            if (poly.Count > 3 && Math.Abs(poly[0].X - poly[^1].X) < 0.02 && Math.Abs(poly[0].Y - poly[^1].Y) < 0.02)
                poly.RemoveAt(poly.Count - 1);
            double area2 = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                area2 += (a.X * b.Y) - (b.X * a.Y);
            }
            if (area2 < 0)
                poly.Reverse();
            // Tira do contorno os pontos alinhados (menos de 3 mm do segmento dos vizinhos, mesma altura): ponto demais na
            // borda vira leque de triangulos finos ate o miolo.
            for (bool removed = true; removed && poly.Count > 8;)
            {
                removed = false;
                for (int i = 0; i < poly.Count && poly.Count > 8; i++)
                {
                    var pa = poly[(i - 1 + poly.Count) % poly.Count];
                    var pm = poly[i];
                    var pb = poly[(i + 1) % poly.Count];
                    double ex = pb.X - pa.X, ey = pb.Y - pa.Y, l2 = (ex * ex) + (ey * ey);
                    if (l2 < 1e-10 || l2 > 4.0 * 4.0)
                        continue;
                    double tt = Math.Clamp((((pm.X - pa.X) * ex) + ((pm.Y - pa.Y) * ey)) / l2, 0, 1);
                    double dx = pa.X + (ex * tt) - pm.X, dy = pa.Y + (ey * tt) - pm.Y;
                    double za = SrcZ(pa.X, pa.Y), zm = SrcZ(pm.X, pm.Y), zb = SrcZ(pb.X, pb.Y);
                    double dh = pa.H + ((pb.H - pa.H) * tt) - pm.H;
                    if (!double.IsNaN(za) && !double.IsNaN(zm) && !double.IsNaN(zb))
                        dh += za + ((zb - za) * tt) - zm;
                    if ((dx * dx) + (dy * dy) < 0.003 * 0.003 && Math.Abs(dh) < 0.003)
                    {
                        poly.RemoveAt(i);
                        removed = true;
                        i--;
                    }
                }
            }
            double minX = poly.Min(q => q.X), maxX = poly.Max(q => q.X), minY = poly.Min(q => q.Y), maxY = poly.Max(q => q.Y);

            // Altura acima da superficie: na borda, a da borda (interpolada no lado do contorno); por dentro, media ponderada.
            // Altura acima da superficie: na borda, a da borda (topo do meio-fio rebaixado / topo de cada travessia); por
            // dentro, media ponderada pela distancia (paralela a superficie, sem "almofada" no miolo).
            double H(double x, double y)
            {
                double sw2 = 0, sh = 0;
                for (int i = 0; i < poly.Count; i++)
                {
                    var a = poly[i];
                    var b = poly[(i + 1) % poly.Count];
                    double ex = b.X - a.X, ey = b.Y - a.Y, l2 = (ex * ex) + (ey * ey);
                    double t = l2 < 1e-12 ? 0 : Math.Clamp((((x - a.X) * ex) + ((y - a.Y) * ey)) / l2, 0, 1);
                    double px = a.X + (ex * t) - x, py = a.Y + (ey * t) - y;
                    double d2 = (px * px) + (py * py);
                    double h = a.H + ((b.H - a.H) * t);
                    if (d2 < 1e-8)
                        return h;
                    double w = 1.0 / (d2 * Math.Sqrt(d2)) * Math.Sqrt(l2);
                    sw2 += w;
                    sh += w * h;
                }
                return sw2 > 0 ? sh / sw2 : baseH.Average();
            }
            // Ponto dentro do contorno ou a menos de 5 mm dele.
            bool OnOrIn(double x, double y)
            {
                if (InPoly(x, y))
                    return true;
                for (int i = 0, k = poly.Count - 1; i < poly.Count; k = i++)
                {
                    double dx = poly[i].X - poly[k].X, dy = poly[i].Y - poly[k].Y, l2 = (dx * dx) + (dy * dy);
                    double u = l2 < 1e-12 ? 0 : Math.Clamp((((x - poly[k].X) * dx) + ((y - poly[k].Y) * dy)) / l2, 0, 1);
                    double ex = poly[k].X + (u * dx) - x, ey = poly[k].Y + (u * dy) - y;
                    if ((ex * ex) + (ey * ey) < 0.005 * 0.005)
                        return true;
                }
                return false;
            }
            static HashSet<int> BowTies(List<(int A, int B, int C)> list)
            {
                var use = new Dictionary<(int, int), int>();
                foreach (var t in list)
                {
                    foreach ((int p, int q) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                    {
                        var key = p < q ? (p, q) : (q, p);
                        use[key] = use.TryGetValue(key, out int u) ? u + 1 : 1;
                    }
                }
                var deg = new Dictionary<int, int>();
                foreach (var kv in use.Where(kv => kv.Value == 1))
                {
                    deg[kv.Key.Item1] = deg.TryGetValue(kv.Key.Item1, out int d1) ? d1 + 1 : 1;
                    deg[kv.Key.Item2] = deg.TryGetValue(kv.Key.Item2, out int d2) ? d2 + 1 : 1;
                }
                return deg.Where(kv => kv.Value > 2).Select(kv => kv.Key).ToHashSet();
            }

            // Ponto dentro do contorno (par-impar).
            bool InPoly(double x, double y)
            {
                bool inside = false;
                for (int i = 0, k = poly.Count - 1; i < poly.Count; k = i++)
                {
                    if (((poly[i].Y > y) != (poly[k].Y > y)) &&
                        (x < ((poly[k].X - poly[i].X) * (y - poly[i].Y) / (poly[k].Y - poly[i].Y)) + poly[i].X))
                        inside = !inside;
                }
                return inside;
            }
            double SrcZ(double x, double y)
            {
                try
                {
                    double z = src.FindElevationAtXY(x, y);
                    return double.IsNaN(z) ? double.NaN : z;
                }
                catch
                {
                    return double.NaN;
                }
            }

            // Triangulos reais da superficie que tocam a uniao: vertices de dentro e as arestas (como breaklines, para a TIN
            // temporaria repetir as mesmas faixas da superficie em vez de retriangular).
            var pts = new Point3dCollection();
            var seen = new HashSet<(long, long)>();
            (long, long) K(double x, double y) => ((long)Math.Round(x * 1000.0), (long)Math.Round(y * 1000.0));
            var edges = new HashSet<((long, long), (long, long))>();
            var edgeList = new List<(Point3d A, Point3d B)>();
            int srcTris = 0;
            var runs = new List<((double X, double Y) A, (double X, double Y) B)>();
            var inserts = new List<(int Seg, double T, double X, double Y)>();
            (int Seg, double T, double D) Closest(double x, double y)
            {
                (int, double, double) best = (0, 0, double.MaxValue);
                for (int i = 0; i < poly.Count; i++)
                {
                    var pa = poly[i];
                    var pb = poly[(i + 1) % poly.Count];
                    double ex = pb.X - pa.X, ey = pb.Y - pa.Y, l2 = (ex * ex) + (ey * ey);
                    double tt = l2 < 1e-12 ? 0 : Math.Clamp((((x - pa.X) * ex) + ((y - pa.Y) * ey)) / l2, 0, 1);
                    double dx = pa.X + (ex * tt) - x, dy = pa.Y + (ey * tt) - y, dd = Math.Sqrt((dx * dx) + (dy * dy));
                    if (dd < best.Item3)
                        best = (i, tt, dd);
                }
                return best;
            }
            bool Deep(double x, double y) => InPoly(x, y) && Closest(x, y).D > 0.05;
            // Ponto do contorno: um vertice dele se estiver a menos de 5 cm, senao um ponto novo inserido no lado.
            (double X, double Y) OnContour(int seg, double tt)
            {
                var pa = poly[seg];
                var pb = poly[(seg + 1) % poly.Count];
                double len = Math.Sqrt(((pb.X - pa.X) * (pb.X - pa.X)) + ((pb.Y - pa.Y) * (pb.Y - pa.Y)));
                if (tt * len < 0.05)
                    return (pa.X, pa.Y);
                if ((1 - tt) * len < 0.05)
                    return (pb.X, pb.Y);
                double ix = pa.X + ((pb.X - pa.X) * tt), iy = pa.Y + ((pb.Y - pa.Y) * tt);
                foreach (var ins in inserts)
                {
                    if (Math.Abs(ins.X - ix) < 0.05 && Math.Abs(ins.Y - iy) < 0.05)
                        return (ins.X, ins.Y);
                }
                inserts.Add((seg, tt, ix, iy));
                return (ix, iy);
            }
            void InsideRuns(Point3d p, Point3d q)
            {
                (double X, double Y)? End(Point3d e)
                {
                    if (Deep(e.X, e.Y))
                        return (e.X, e.Y);
                    if (!InPoly(e.X, e.Y))
                        return null;
                    var cl = Closest(e.X, e.Y);
                    return OnContour(cl.Seg, cl.T);
                }
                var cuts = new List<(double U, (double X, double Y)? P)> { (0, End(p)), (1, End(q)) };
                double dx = q.X - p.X, dy = q.Y - p.Y;
                for (int i = 0; i < poly.Count; i++)
                {
                    var pa = poly[i];
                    var pb = poly[(i + 1) % poly.Count];
                    double ex = pb.X - pa.X, ey = pb.Y - pa.Y;
                    double den = (dx * ey) - (dy * ex);
                    if (Math.Abs(den) < 1e-12)
                        continue;
                    double u = (((pa.X - p.X) * ey) - ((pa.Y - p.Y) * ex)) / den;
                    double tt = (((pa.X - p.X) * dy) - ((pa.Y - p.Y) * dx)) / den;
                    if (u <= 1e-9 || u >= 1 - 1e-9 || tt < 0 || tt > 1)
                        continue;
                    cuts.Add((u, OnContour(i, tt)));
                }
                cuts.Sort((x1, x2) => x1.U.CompareTo(x2.U));
                for (int i = 0; i + 1 < cuts.Count; i++)
                {
                    double um = (cuts[i].U + cuts[i + 1].U) / 2.0;
                    if (cuts[i].P is not { } s || cuts[i + 1].P is not { } e2 || !InPoly(p.X + (dx * um), p.Y + (dy * um)))
                        continue;
                    if (Math.Abs(s.X - e2.X) < 1e-6 && Math.Abs(s.Y - e2.Y) < 1e-6)
                        continue;
                    runs.Add((s, e2));
                }
            }
            foreach (CivilDb.TinSurfaceTriangle t in src.GetTriangles(false))
            {
                Point3d a = t.Vertex1.Location, b = t.Vertex2.Location, d = t.Vertex3.Location;
                if (Math.Max(a.X, Math.Max(b.X, d.X)) < minX || Math.Min(a.X, Math.Min(b.X, d.X)) > maxX ||
                    Math.Max(a.Y, Math.Max(b.Y, d.Y)) < minY || Math.Min(a.Y, Math.Min(b.Y, d.Y)) > maxY)
                    continue;
                bool ia = InPoly(a.X, a.Y), ib = InPoly(b.X, b.Y), id = InPoly(d.X, d.Y);
                if (!ia && !ib && !id && !InPoly((a.X + b.X + d.X) / 3.0, (a.Y + b.Y + d.Y) / 3.0))
                    continue;
                srcTris++;
                foreach (Point3d v in new[] { a, b, d })
                {
                    if (Deep(v.X, v.Y) && seen.Add(K(v.X, v.Y)))
                        pts.Add(new Point3d(v.X, v.Y, v.Z + Math.Max(0.005, H(v.X, v.Y))));
                }
                foreach ((Point3d p, Point3d q) in new[] { (a, b), (b, d), (d, a) })
                {
                    var ka = K(p.X, p.Y);
                    var kb = K(q.X, q.Y);
                    var key = ka.CompareTo(kb) < 0 ? (ka, kb) : (kb, ka);
                    if (!edges.Add(key))
                        continue;
                    // Aresta inteira por dentro: vai como esta. Aresta que corta o contorno (ou com ponta colada nele):
                    // so o trecho de dentro, com a ponta levada para um ponto do contorno (evita lascas junto da borda).
                    InsideRuns(p, q);
                }
            }
            foreach (var run in runs)
            {
                Point3d A3 = new(run.A.X, run.A.Y, SrcZ(run.A.X, run.A.Y));
                Point3d B3 = new(run.B.X, run.B.Y, SrcZ(run.B.X, run.B.Y));
                if (double.IsNaN(A3.Z) || double.IsNaN(B3.Z) || A3.DistanceTo(B3) < 0.05)
                    continue;
                edgeList.Add((new Point3d(A3.X, A3.Y, A3.Z + Math.Max(0.005, H(A3.X, A3.Y))), new Point3d(B3.X, B3.Y, B3.Z + Math.Max(0.005, H(B3.X, B3.Y)))));
            }
            foreach (var ins in inserts.OrderByDescending(x => x.Seg).ThenByDescending(x => x.T))
                poly.Insert(ins.Seg + 1, (ins.X, ins.Y, poly[ins.Seg].H + ((poly[(ins.Seg + 1) % poly.Count].H - poly[ins.Seg].H) * ins.T)));
            foreach (var q in poly)
            {
                double zs = SrcZ(q.X, q.Y);
                if (!double.IsNaN(zs) && seen.Add(K(q.X, q.Y)))
                    pts.Add(new Point3d(q.X, q.Y, zs + Math.Max(0.005, q.H)));
            }

            var verts = new List<(double X, double Y, double Zs)>();
            var tris = new List<(int A, int B, int C)>();
            int filled = 0;
            AcadDb.ObjectId tinId = AcadDb.ObjectId.Null;
            var temp = new List<AcadDb.Entity>();
            try
            {
                tinId = CivilDb.TinSurface.Create(db, "_C3D_TMP_UNIAO_" + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture));
                var tin = (CivilDb.TinSurface)tr.GetObject(tinId, AcadDb.OpenMode.ForWrite);
                tin.AddVertices(pts);
                var blIds = new AcadDb.ObjectIdCollection();
                foreach ((Point3d a, Point3d b) in edgeList)
                {
                    var pl = new AcadDb.Polyline3d(AcadDb.Poly3dType.SimplePoly, new Point3dCollection { a, b }, false);
                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    temp.Add(pl);
                    blIds.Add(pl.ObjectId);
                }
                var contour3 = new Point3dCollection();
                foreach (var q in poly)
                {
                    double zs = SrcZ(q.X, q.Y);
                    contour3.Add(new Point3d(q.X, q.Y, (double.IsNaN(zs) ? 0 : zs) + Math.Max(0.005, q.H)));
                }
                var poly3 = new AcadDb.Polyline3d(AcadDb.Poly3dType.SimplePoly, contour3, true);
                ms.AppendEntity(poly3);
                tr.AddNewlyCreatedDBObject(poly3, true);
                temp.Add(poly3);
                blIds.Add(poly3.ObjectId);
                tin.BreaklinesDefinition.AddStandardBreaklines(blIds, 1.0, 0.0, 0.0, 0.0);
                tin.BoundariesDefinition.AddBoundaries(new AcadDb.ObjectIdCollection { poly3.ObjectId }, 0.1, Autodesk.Civil.SurfaceBoundaryType.Outer, false);
                tin.Rebuild();

                // Vertices a menos de 5 mm viram um so (a TIN cria pontos quase repetidos onde as breaklines se cruzam,
                // e eles dao triangulos-lasca); os triangulos que somem com isso sao descartados.
                var index = new Dictionary<(long, long), List<int>>();
                const double weld = 0.005;
                int Vid(Point3d p)
                {
                    long cx = (long)Math.Floor(p.X / weld), cy = (long)Math.Floor(p.Y / weld);
                    for (long ox = -1; ox <= 1; ox++)
                    {
                        for (long oy = -1; oy <= 1; oy++)
                        {
                            if (!index.TryGetValue((cx + ox, cy + oy), out var cell))
                                continue;
                            foreach (int j in cell)
                            {
                                if (Math.Abs(verts[j].X - p.X) < weld && Math.Abs(verts[j].Y - p.Y) < weld)
                                    return j;
                            }
                        }
                    }
                    double zs = SrcZ(p.X, p.Y);
                    int id = verts.Count;
                    if (!index.TryGetValue((cx, cy), out var list))
                        index[(cx, cy)] = list = new List<int>();
                    list.Add(id);
                    verts.Add((p.X, p.Y, double.IsNaN(zs) ? p.Z - Math.Max(0.005, H(p.X, p.Y)) : zs));
                    return id;
                }
                // Triangulos fora do contorno com os 3 vertices no contorno (lascas em cantos concavos) ficam de reserva:
                // entram so para desfazer "gravatas" (dois triangulos que se tocam so num vertice da borda), que travam o ConvertToSolid.
                var spare = new List<(int A, int B, int C)>();
                foreach (CivilDb.TinSurfaceTriangle t in tin.GetTriangles(false))
                {
                    Point3d a = t.Vertex1.Location, b = t.Vertex2.Location, d = t.Vertex3.Location;
                    bool inside = InPoly((a.X + b.X + d.X) / 3.0, (a.Y + b.Y + d.Y) / 3.0);
                    if (!inside && !(OnOrIn(a.X, a.Y) && OnOrIn(b.X, b.Y) && OnOrIn(d.X, d.Y)))
                        continue;
                    int ia = Vid(a), ib = Vid(b), ic = Vid(d);
                    if (ia == ib || ib == ic || ia == ic)
                        continue;
                    if (((b.X - a.X) * (d.Y - a.Y)) - ((b.Y - a.Y) * (d.X - a.X)) < 0)
                        (ib, ic) = (ic, ib);
                    (inside ? tris : spare).Add((ia, ib, ic));
                }
                for (int pass = 0; pass < 4 && spare.Count > 0; pass++)
                {
                    var bt = BowTies(tris);
                    if (bt.Count == 0)
                        break;
                    var add = spare.Where(s => bt.Contains(s.A) || bt.Contains(s.B) || bt.Contains(s.C)).ToList();
                    if (add.Count == 0)
                        break;
                    tris.AddRange(add);
                    spare.RemoveAll(add.Contains);
                    filled += add.Count;
                }
            }
            finally
            {
                foreach (AcadDb.Entity e in temp)
                {
                    try
                    {
                        if (!e.IsErased)
                            e.Erase();
                    }
                    catch
                    {
                        // Ja apagada.
                    }
                }
                try
                {
                    if (!tinId.IsNull && tr.GetObject(tinId, AcadDb.OpenMode.ForWrite) is AcadDb.DBObject tt && !tt.IsErased)
                        tt.Erase();
                }
                catch
                {
                    // TIN temporaria ja apagada.
                }
            }
            if (tris.Count < 3)
                return null;
            filled += FillHoles(tris, verts.Select(v => (v.X, v.Y)).ToList());
            double aspect = 0;
            foreach (var t in tris)
            {
                var pa = verts[t.A];
                var pb = verts[t.B];
                var pc = verts[t.C];
                double la = Math.Sqrt(((pb.X - pa.X) * (pb.X - pa.X)) + ((pb.Y - pa.Y) * (pb.Y - pa.Y)));
                double lb = Math.Sqrt(((pc.X - pb.X) * (pc.X - pb.X)) + ((pc.Y - pb.Y) * (pc.Y - pb.Y)));
                double lc = Math.Sqrt(((pa.X - pc.X) * (pa.X - pc.X)) + ((pa.Y - pc.Y) * (pa.Y - pc.Y)));
                double ar2 = Math.Abs(((pb.X - pa.X) * (pc.Y - pa.Y)) - ((pb.Y - pa.Y) * (pc.X - pa.X)));
                double lmax = Math.Max(la, Math.Max(lb, lc));
                if (ar2 > 1e-12)
                    aspect = Math.Max(aspect, lmax * lmax / ar2);
            }
            Point3d o = edgeTop[0];
            Vector3d shift = new(o.X, o.Y, Math.Floor(o.Z));
            var meshPts = new Point3dCollection();
            foreach (var v in verts)
                meshPts.Add(new Point3d(v.X, v.Y, v.Zs + Math.Max(0.005, H(v.X, v.Y))) - shift);
            int top = verts.Count;
            foreach (var v in verts)
                meshPts.Add(new Point3d(v.X, v.Y, v.Zs) - shift);
            var faces = new List<int[]>();
            var edgeUse = new Dictionary<(int, int), int>();
            foreach ((int a, int b, int d) in tris)
            {
                foreach ((int p, int q) in new[] { (a, b), (b, d), (d, a) })
                {
                    var key0 = p < q ? (p, q) : (q, p);
                    edgeUse[key0] = edgeUse.TryGetValue(key0, out int u0) ? u0 + 1 : 1;
                }
            }
            int nonManifold = edgeUse.Count(kv => kv.Value > 2);
            var boundaryDeg = new Dictionary<int, int>();
            foreach (var kv in edgeUse.Where(kv => kv.Value == 1))
            {
                boundaryDeg[kv.Key.Item1] = boundaryDeg.TryGetValue(kv.Key.Item1, out int b1) ? b1 + 1 : 1;
                boundaryDeg[kv.Key.Item2] = boundaryDeg.TryGetValue(kv.Key.Item2, out int b2) ? b2 + 1 : 1;
            }
            var bowTies = boundaryDeg.Where(kv => kv.Value > 2).Select(kv => kv.Key).ToList();
            if (bowTies.Count > 0 || nonManifold > 0)
                PluginLog.Aviso("MFTRAVESSIA", $"Uniao: malha do plato com {bowTies.Count} vertice(s) em gravata na borda ({string.Join("; ", bowTies.Take(5).Select(k => $"{verts[k].X:0.000},{verts[k].Y:0.000}"))}), {edgeUse.Count(kv => kv.Value == 1)} arestas de borda, {nonManifold} arestas com mais de 2 triangulos.");
            if (nonManifold > 0)
            {
                foreach (var kv in edgeUse.Where(kv => kv.Value > 2))
                {
                    var users = tris.Where(t => new[] { t.A, t.B, t.C }.Contains(kv.Key.Item1) && new[] { t.A, t.B, t.C }.Contains(kv.Key.Item2)).ToList();
                    string Tx((int A, int B, int C) t) => string.Join(" ", new[] { t.A, t.B, t.C }.Select(k => $"{k}({verts[k].X:0.000},{verts[k].Y:0.000})"));
                    PluginLog.Aviso("MFTRAVESSIA", $"Uniao: aresta {kv.Key} usada por {kv.Value} triangulos: " + string.Join(" | ", users.Select(Tx)));
                }
            }
            edgeUse.Clear();
            foreach ((int a, int b, int d) in tris)
            {
                faces.Add(new[] { a, b, d });
                faces.Add(new[] { d + top, b + top, a + top });
                foreach ((int p, int q) in new[] { (a, b), (b, d), (d, a) })
                {
                    var key = p < q ? (p, q) : (q, p);
                    edgeUse[key] = edgeUse.TryGetValue(key, out int u) ? u + 1 : 1;
                }
            }
            foreach ((int a, int b, int d) in tris)
            {
                foreach ((int p, int q) in new[] { (a, b), (b, d), (d, a) })
                {
                    if (edgeUse[p < q ? (p, q) : (q, p)] != 1)
                        continue;
                    faces.Add(new[] { q, p, p + top });
                    faces.Add(new[] { q, p + top, q + top });
                }
            }
            var idx = new Int32Collection();
            foreach (int[] f in faces)
            {
                idx.Add(3);
                foreach (int k in f)
                    idx.Add(k);
            }
            var mesh = new AcadDb.SubDMesh();
            mesh.SetDatabaseDefaults();
            mesh.SetSubDMesh(meshPts, idx, 0);
            mesh.Layer = layer;
            ms.AppendEntity(mesh);
            tr.AddNewlyCreatedDBObject(mesh, true);
            AcadDb.Solid3d solid = mesh.ConvertToSolid(false, true);
            CleanSolid(solid);
            solid.SetDatabaseDefaults();
            solid.Layer = layer;
            solid.TransformBy(Matrix3d.Displacement(shift));
            ms.AppendEntity(solid);
            tr.AddNewlyCreatedDBObject(solid, true);
            mesh.Erase();
            mode = $"solido com os triangulos da superficie ({srcTris} triangulos da superficie no contorno, {tris.Count} no topo, {filled} triangulo(s) para fechar a borda, {faces.Count} faces na malha, triangulo mais esticado {aspect:0} : 1 (aresta maior / altura); {sw.ElapsedMilliseconds} ms)";
            return solid;
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso("MFTRAVESSIA", "Uniao: plato com os triangulos da superficie nao saiu (" + ex.Message + "); fica o solido em leque.");
            return null;
        }
    }

    /// <summary>
    /// Fecha furos da malha do plato (a TIN as vezes deixa de fora um triangulo junto da borda, o que cria um vertice em
    /// "gravata" e trava o ConvertToSolid): cada laco de borda no sentido horario (furo) e preenchido por recorte de orelhas.
    /// Triangulos no sentido anti-horario. Devolve quantos triangulos entraram.
    /// </summary>
    internal static int FillHoles(List<(int A, int B, int C)> tris, List<(double X, double Y)> v)
    {
        int added = 0;
        for (int pass = 0; pass < 8; pass++)
        {
            var directed = new HashSet<(int, int)>();
            foreach (var t in tris)
            {
                directed.Add((t.A, t.B));
                directed.Add((t.B, t.C));
                directed.Add((t.C, t.A));
            }
            var next = new Dictionary<int, List<int>>();
            foreach ((int p, int q) in directed)
            {
                if (directed.Contains((q, p)))
                    continue;
                if (!next.TryGetValue(p, out var l))
                    next[p] = l = new List<int>();
                l.Add(q);
            }
            // Gravata: vertice com duas saidas de borda. Falta um triangulo em cunha (p -> v -> q, virando a direita);
            // entra o de menor angulo que nao cubra nenhum outro vertice.
            int notch = 0;
            foreach (var kv in next.Where(kv => kv.Value.Count > 1).ToList())
            {
                int vv = kv.Key;
                var ins = next.Where(e => e.Value.Contains(vv)).Select(e => e.Key).ToList();
                (int P, int Q, double Ang)? best = null;
                foreach (int p in ins)
                {
                    foreach (int q in kv.Value)
                    {
                        if (p == q)
                            continue;
                        var a = v[p];
                        var b = v[q];
                        var c = v[vv];
                        double cr = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
                        if (cr <= 1e-10)
                            continue;
                        double ang = Math.Abs(Math.Atan2(((a.X - c.X) * (b.Y - c.Y)) - ((a.Y - c.Y) * (b.X - c.X)), ((a.X - c.X) * (b.X - c.X)) + ((a.Y - c.Y) * (b.Y - c.Y))));
                        bool covers = false;
                        for (int k = 0; k < v.Count && !covers; k++)
                        {
                            if (k == p || k == q || k == vv)
                                continue;
                            var r = v[k];
                            double s1 = ((b.X - a.X) * (r.Y - a.Y)) - ((b.Y - a.Y) * (r.X - a.X));
                            double s2 = ((c.X - b.X) * (r.Y - b.Y)) - ((c.Y - b.Y) * (r.X - b.X));
                            double s3 = ((a.X - c.X) * (r.Y - c.Y)) - ((a.Y - c.Y) * (r.X - c.X));
                            covers = s1 > 1e-12 && s2 > 1e-12 && s3 > 1e-12;
                        }
                        if (!covers && (best is null || ang < best.Value.Ang))
                            best = (p, q, ang);
                    }
                }
                if (best is { } bb)
                {
                    tris.Add((bb.P, bb.Q, vv));
                    added++;
                    notch++;
                }
            }
            if (notch > 0)
                continue;

            // Lacos de borda; um laco que passa duas vezes pelo mesmo vertice e quebrado em lacos simples.
            var loops = new List<List<int>>();
            while (next.Count > 0)
            {
                int start = next.Keys.First();
                var path = new List<int> { start };
                int cur = start;
                while (true)
                {
                    if (!next.TryGetValue(cur, out var outs) || outs.Count == 0)
                        break;
                    int nx = outs[^1];
                    outs.RemoveAt(outs.Count - 1);
                    if (outs.Count == 0)
                        next.Remove(cur);
                    if (nx == start)
                        break;
                    int at = path.IndexOf(nx);
                    if (at >= 0)
                    {
                        loops.Add(path.GetRange(at, path.Count - at));
                        path.RemoveRange(at + 1, path.Count - at - 1);
                        cur = nx;
                        continue;
                    }
                    path.Add(nx);
                    cur = nx;
                }
                if (path.Count >= 3)
                    loops.Add(path);
            }
            int before = added;
            foreach (var loop in loops)
            {
                double a2 = 0;
                for (int i = 0; i < loop.Count; i++)
                {
                    var a = v[loop[i]];
                    var b = v[loop[(i + 1) % loop.Count]];
                    a2 += (a.X * b.Y) - (b.X * a.Y);
                }
                if (a2 >= 0 || -a2 / 2.0 > 4.0 || loop.Count > 200)
                    continue; // contorno externo (anti-horario) ou furo grande demais: fica como esta
                var poly = new List<int>(loop);
                poly.Reverse(); // furo em sentido anti-horario
                int guard = 0;
                while (poly.Count > 3 && guard++ < 10000)
                {
                    bool cut = false;
                    for (int i = 0; i < poly.Count; i++)
                    {
                        int ip = poly[(i - 1 + poly.Count) % poly.Count], ic = poly[i], inx = poly[(i + 1) % poly.Count];
                        var p0 = v[ip];
                        var p1 = v[ic];
                        var p2 = v[inx];
                        double cr = ((p1.X - p0.X) * (p2.Y - p0.Y)) - ((p1.Y - p0.Y) * (p2.X - p0.X));
                        if (cr <= 1e-12)
                            continue;
                        bool inside = false;
                        foreach (int k in poly)
                        {
                            if (k == ip || k == ic || k == inx)
                                continue;
                            var q = v[k];
                            double s1 = ((p1.X - p0.X) * (q.Y - p0.Y)) - ((p1.Y - p0.Y) * (q.X - p0.X));
                            double s2 = ((p2.X - p1.X) * (q.Y - p1.Y)) - ((p2.Y - p1.Y) * (q.X - p1.X));
                            double s3 = ((p0.X - p2.X) * (q.Y - p2.Y)) - ((p0.Y - p2.Y) * (q.X - p2.X));
                            if (s1 >= 0 && s2 >= 0 && s3 >= 0)
                            {
                                inside = true;
                                break;
                            }
                        }
                        if (inside)
                            continue;
                        tris.Add((ip, ic, inx));
                        added++;
                        poly.RemoveAt(i);
                        cut = true;
                        break;
                    }
                    if (!cut)
                        break;
                }
                if (poly.Count == 3)
                {
                    tris.Add((poly[0], poly[1], poly[2]));
                    added++;
                }
            }
            if (added == before)
                break;
        }
        return added;
    }

    /// <summary>Funde faces planas vizinhas e tira arestas sobrando do solido (menos arestas no Object Viewer e no IFC).</summary>
    internal static void CleanSolid(AcadDb.Solid3d solid)
    {
        try
        {
            solid.CleanBody();
        }
        catch
        {
            // Sem limpeza: o solido fica como saiu da malha.
        }
    }
    private static AcadDb.Entity? FanSolid(AcadDb.Transaction tr, AcadDb.BlockTableRecord ms, Point3d[,] top, Point3d[,] bottom,
        string layer, out string mode)
    {
        int n = top.GetLength(0), rings = top.GetLength(1) - 1;
        mode = "";
        if (n < 3 || rings < 1)
            return null;
        Point3d o = top[0, 0];
        Vector3d shift = new(o.X, o.Y, Math.Floor(o.Z));
        var verts = new Point3dCollection { top[0, 0] - shift, bottom[0, 0] - shift };
        for (int i = 0; i < n; i++)
            for (int k = 1; k <= rings; k++)
                verts.Add(top[i, k] - shift);
        for (int i = 0; i < n; i++)
            for (int k = 1; k <= rings; k++)
                verts.Add(bottom[i, k] - shift);
        int T(int i, int k) => k == 0 ? 0 : 2 + ((i % n) * rings) + (k - 1);
        int B(int i, int k) => k == 0 ? 1 : 2 + (n * rings) + ((i % n) * rings) + (k - 1);

        var faces = new List<int[]>();
        for (int i = 0; i < n; i++)
        {
            faces.Add(new[] { T(i, 0), T(i, 1), T(i + 1, 1) });
            faces.Add(new[] { B(i + 1, 1), B(i, 1), B(i, 0) });
            for (int k = 1; k < rings; k++)
            {
                faces.Add(new[] { T(i, k), T(i, k + 1), T(i + 1, k + 1) });
                faces.Add(new[] { T(i, k), T(i + 1, k + 1), T(i + 1, k) });
                faces.Add(new[] { B(i + 1, k + 1), B(i, k + 1), B(i, k) });
                faces.Add(new[] { B(i + 1, k), B(i + 1, k + 1), B(i, k) });
            }
            // Parede: borda de fora p -> q no giro do topo.
            int p = T(i, rings), q = T(i + 1, rings), pb = B(i, rings), qb = B(i + 1, rings);
            faces.Add(new[] { q, p, pb });
            faces.Add(new[] { q, pb, qb });
        }

        Point3d v0 = verts[T(0, 0)], v1 = verts[T(0, 1)], v2 = verts[T(1, 1)];
        if ((v1 - v0).CrossProduct(v2 - v0).Z < 0)
        {
            foreach (int[] f in faces)
                Array.Reverse(f);
        }

        var index = new Int32Collection();
        foreach (int[] f in faces)
        {
            index.Add(3);
            foreach (int i in f)
                index.Add(i);
        }

        var mesh = new AcadDb.SubDMesh();
        mesh.SetDatabaseDefaults();
        mesh.SetSubDMesh(verts, index, 0);
        mesh.Layer = layer;
        ms.AppendEntity(mesh);
        tr.AddNewlyCreatedDBObject(mesh, true);
        Matrix3d back = Matrix3d.Displacement(shift);
        try
        {
            AcadDb.Solid3d solid = mesh.ConvertToSolid(false, true);
            CleanSolid(solid);
            solid.SetDatabaseDefaults();
            solid.Layer = layer;
            solid.TransformBy(back);
            ms.AppendEntity(solid);
            tr.AddNewlyCreatedDBObject(solid, true);
            mesh.Erase();
            mode = $"solido ({n} raios x {rings} aneis)";
            return solid;
        }
        catch (System.Exception ex)
        {
            mesh.TransformBy(back);
            mode = $"malha (a conversao em solido falhou: {ex.Message})";
            PluginLog.Aviso("MFTRAVESSIA", "Uniao: conversao em solido falhou, ficou a malha: " + ex.Message);
            return mesh;
        }
    }

    /// <summary>
    /// O que ficou gravado no desenho para a travessia/uniao (relido do desenho, nao o que o comando
    /// pensa que fez): objetos por layer, se a layer esta desligada/congelada e a caixa do solido.
    /// </summary>
    public static string Summary(AcadDb.Database db, string id)
    {
        try
        {
            var byLayer = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string box = "", kind = "";
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            var bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
            var ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
            foreach (AcadDb.ObjectId oid in ms)
            {
                if (tr.GetObject(oid, AcadDb.OpenMode.ForRead) is not AcadDb.Entity e || PartOf(e) is not { } part || part.Id != id)
                    continue;
                byLayer[e.Layer] = byLayer.TryGetValue(e.Layer, out int n) ? n + 1 : 1;
                if (part.Part is PartPlato or "UNIAO_PLATO")
                {
                    kind = e is AcadDb.Solid3d ? "solido 3D" : e.GetType().Name;
                    try
                    {
                        AcadDb.Extents3d x = e.GeometricExtents;
                        box = $"X {x.MinPoint.X:0.00}-{x.MaxPoint.X:0.00}, Y {x.MinPoint.Y:0.00}-{x.MaxPoint.Y:0.00}, elevacao {x.MinPoint.Z:0.000}-{x.MaxPoint.Z:0.000}";
                    }
                    catch
                    {
                        // Sem extents.
                    }
                }
            }
            var lt = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
            var layers = new List<string>();
            foreach ((string name, int n) in byLayer)
            {
                string state = "";
                if (lt.Has(name) && tr.GetObject(lt[name], AcadDb.OpenMode.ForRead) is AcadDb.LayerTableRecord l)
                    state = (l.IsOff ? " DESLIGADA" : "") + (l.IsFrozen ? " CONGELADA" : "");
                layers.Add($"{name} {n}{state}");
            }
            tr.Commit();
            if (byLayer.Count == 0)
                return $"AVISO: nada da travessia {id} ficou gravado no desenho.";
            return $"Gravado no desenho: {string.Join(", ", layers)}" + (box.Length > 0 ? $"; plato ({kind}) em {box}." : ".");
        }
        catch (System.Exception ex)
        {
            return "Conferencia do que foi gravado falhou: " + ex.Message;
        }
    }

    /// <summary>Apaga os objetos da uniao (as travessias nao sao tocadas).</summary>
    public static int EraseUnion(AcadDb.Database db, string unionId)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        int n = Erase(tr, db, unionId);
        tr.Commit();
        return n;
    }

    /// <summary>
    /// Refaz as unioes dadas com o corredor atual (depois do MFTRAVESSIAREFAZER). Uniao que ficou com
    /// menos de duas travessias e desfeita: a travessia que sobrou volta a ser inteira.
    /// </summary>
    public static void RedoUnions(AcadDb.Database db, Editor ed, List<string> unionIds)
    {
        if (unionIds.Count == 0)
            return;
        MFRebaixoSettings settings = MFRebaixoSettingsStore.Load();
        List<RebaixoInfo> rebaixos = MFRebaixoData.ReadAll(db);
        foreach (string unionId in unionIds)
        {
            try
            {
                var members = new List<(Stored, TravessiaGeometry)>();
                foreach ((_, Stored d) in FindAll(db).Where(x => x.Data.UnionId == unionId))
                    members.Add((d, GeometryFor(db, d, rebaixos, out _)));
                if (members.Count < 2)
                {
                    EraseUnion(db, unionId);
                    foreach ((Stored d, TravessiaGeometry g) in members)
                    {
                        d.UnionId = "";
                        d.OuterSign = 0;
                        Draw(db, g, d, settings);
                    }
                    ed.WriteMessage($"\nUniao {unionId} desfeita (ficou com menos de duas travessias).");
                    continue;
                }
                ed.WriteMessage("\n" + Unite(db, unionId, members, settings));
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nUniao {unionId} nao refeita: {ex.Message}");
                PluginLog.Aviso("MFTRAVESSIA", $"Uniao {unionId} nao refeita: {ex.Message}");
            }
        }
    }
}

/// <summary>MFTRAVESSIAUNIR: une (ou separa) as travessias de um cruzamento num plato so.</summary>
public sealed class MFTravessiaUniaoCommands
{
    [CommandMethod(C3DCommands.Corridor.MfTravessiaUnir, CommandFlags.Modal)]
    public void Unir()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        try
        {
            List<(AcadDb.ObjectId Id, MFTravessia.Stored Data)> all = MFTravessia.FindAll(db);
            if (all.Count < 2)
            {
                ed.WriteMessage("\nLance primeiro as travessias do cruzamento (MFTRAVESSIA); a uniao precisa de pelo menos duas.\n");
                return;
            }

            var mode = new PromptKeywordOptions("\nTravessias de um cruzamento [Unir/Separar]", "Unir Separar") { AllowNone = true };
            mode.Keywords.Default = "Unir";
            PromptResult mr = ed.GetKeywords(mode);
            if (mr.Status == PromptStatus.Cancel)
                return;
            bool separar = mr.Status == PromptStatus.OK && mr.StringResult == "Separar";

            PromptSelectionResult sel = ed.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = separar
                    ? "\nSelecione a uniao ou uma das travessias dela: "
                    : "\nSelecione as travessias do cruzamento (qualquer parte de cada uma): "
            });
            if (sel.Status != PromptStatus.OK)
                return;
            var picked = new HashSet<string>();
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (AcadDb.ObjectId oid in sel.Value.GetObjectIds())
                {
                    if (tr.GetObject(oid, AcadDb.OpenMode.ForRead) is not AcadDb.Entity e)
                        continue;
                    (string Id, string Part)? part = MFTravessia.PartOf(e);
                    if (part is not null)
                        picked.Add(part.Value.Id);
                }
                tr.Commit();
            }

            // Ids escolhidos podem ser de travessia ou de uniao (a uniao traz todas as travessias dela).
            List<MFTravessia.Stored> chosen = all.Select(x => x.Data)
                .Where(d => picked.Contains(d.Id) || (d.UnionId.Length > 0 && picked.Contains(d.UnionId)))
                .ToList();
            MFRebaixoSettings settings = MFRebaixoSettingsStore.Load();
            List<RebaixoInfo> rebaixos = MFRebaixoData.ReadAll(db);

            if (separar)
            {
                List<string> unions = chosen.Select(d => d.UnionId).Where(u => u.Length > 0).Distinct().ToList();
                if (unions.Count == 0)
                {
                    ed.WriteMessage("\nNenhuma uniao na selecao.\n");
                    return;
                }
                foreach (string u in unions)
                {
                    MFTravessia.EraseUnion(db, u);
                    foreach (MFTravessia.Stored d in all.Select(x => x.Data).Where(d => d.UnionId == u))
                    {
                        TravessiaGeometry g = MFTravessia.GeometryFor(db, d, rebaixos, out _);
                        d.UnionId = "";
                        d.OuterSign = 0;
                        MFTravessia.Draw(db, g, d, settings);
                    }
                }
                ed.WriteMessage($"\n{unions.Count} uniao(oes) desfeita(s): as travessias voltaram a ser inteiras.\n");
                ed.Regen();
                return;
            }

            if (chosen.Count < 2)
            {
                ed.WriteMessage("\nSelecione pelo menos duas travessias do mesmo cruzamento.\n");
                return;
            }

            ed.WriteMessage($"\n\nUnir {chosen.Count} travessias ({string.Join("; ", chosen.Select(d => d.LabelA + "-" + d.LabelB))}) num plato so:");
            ed.WriteMessage("\n  o miolo do cruzamento sobe na altura das travessias, limitado pelo meio-fio (borda da pista; a sarjeta fica livre com tampa de concreto por cima);");
            ed.WriteMessage("\n  em cada travessia fica so a rampa de fora; faixas, tampas e triangulos de fora continuam.");
            ed.WriteMessage("\n  Para desfazer: U logo em seguida, ou MFTRAVESSIAUNIR > Separar.");
            var confirm = new PromptKeywordOptions("\nUnir? [Sim/Nao]", "Sim Nao") { AllowNone = true };
            confirm.Keywords.Default = "Sim";
            PromptResult ok = ed.GetKeywords(confirm);
            if (ok.Status == PromptStatus.Cancel || (ok.Status == PromptStatus.OK && ok.StringResult == "Nao"))
            {
                ed.WriteMessage("\nNada foi feito.\n");
                return;
            }

            // Unioes antigas das escolhidas saem; todas entram numa uniao nova.
            foreach (string old in chosen.Select(d => d.UnionId).Where(u => u.Length > 0).Distinct())
                MFTravessia.EraseUnion(db, old);
            string unionId = "U" + Guid.NewGuid().ToString("N").Substring(0, 11).ToUpperInvariant();
            var members = new List<(MFTravessia.Stored, TravessiaGeometry)>();
            foreach (MFTravessia.Stored d in chosen)
                members.Add((d, MFTravessia.GeometryFor(db, d, rebaixos, out _)));
            string summary = MFTravessia.Unite(db, unionId, members, settings);
            ed.WriteMessage("\n" + summary + "\n");
            ed.Regen();
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("MFTRAVESSIA", "MFTRAVESSIAUNIR: " + ex.Message, ex);
            ed.WriteMessage("\nUniao nao feita: " + ex.Message + "\n");
        }
    }
}
