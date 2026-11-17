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
    private static List<List<CurbSample>> CurbLines(AcadDb.Database db, Point3d center, double radius)
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
                                    if (footElev is not null)
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

        List<List<CurbSample>> curbs = CurbLines(db, c, radius);
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
            if (rays[i].Member >= 0 || rays[i].CurbTop is not double ct)
                continue;
            double hc = ct - bottom[i, rings].Z;
            if (Math.Abs(hc - edgeHs[i]) <= follow + 1e-9)
            {
                edgeHs[i] = Math.Max(0.005, hc);
                curbDiff[i] = ct - (bottom[i, rings].Z + edgeHs[i]);
            }
            else
            {
                notLowered[i] = true;
            }
        }
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k <= rings; k++)
            {
                double f = k / (double)rings;
                Point3d b0 = bottom[i, k];
                top[i, k] = new Point3d(b0.X, b0.Y, b0.Z + hCenter + ((edgeHs[i] - hCenter) * f));
            }
        }
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

            AcadDb.Entity? solid = FanSolid(tr, ms, top, bottom, layer3d, out string mode);
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
