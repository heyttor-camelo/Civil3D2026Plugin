using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using AecPropData = Autodesk.Aec.PropertyData;
using AecPropDb = Autodesk.Aec.PropertyData.DatabaseServices;

using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>Semi-elevada: plato abaixo da calcada (rebaixo completa a diferenca). Elevada: plato no nivel da calcada.</summary>
internal enum TravessiaTipo
{
    SemiElevada = 0,
    Elevada = 1
}

/// <summary>Parametros da travessia (lombofaixa) entre dois rebaixos.</summary>
internal sealed class TravessiaParams
{
    public TravessiaTipo Tipo { get; set; } = TravessiaTipo.SemiElevada;

    /// <summary>Altura do plato acima da borda da pista (semi-elevada). Na elevada sai da altura do meio-fio.</summary>
    public double Altura { get; set; } = 0.12;

    /// <summary>Largura da plataforma (faixa de pedestres), no sentido dos carros.</summary>
    public double Plataforma { get; set; } = 5.00;

    /// <summary>Comprimento de cada rampa dos veiculos.</summary>
    public double Rampa { get; set; } = 1.50;

    /// <summary>Tampa de concreto sobre a sarjeta (a agua passa por baixo).</summary>
    public bool Tampa { get; set; } = true;

    public double TampaEspessura { get; set; } = 0.10;

    /// <summary>Move os rebaixos para o eixo da travessia.</summary>
    public bool Centralizar { get; set; } = true;

    /// <summary>Superficie de topo escolhida pelo usuario (nome); vazio = a de topo do corredor.</summary>
    public string Superficie { get; set; } = "";

    public string TipoTexto => Tipo == TravessiaTipo.Elevada ? "elevada" : "semi-elevada";
}

/// <summary>Um ponto do meio-fio lido do corredor numa secao: borda da pista, pe do meio-fio e topo.</summary>
internal readonly record struct CurbSample(double Station, Point3d Edge, Point3d Foot, double? Top);

/// <summary>Um lado da travessia: o meio-fio (MFSARJETA/MF) de um rebaixo, lido em volta do eixo.</summary>
internal sealed class TravessiaSide
{
    public AcadDb.ObjectId CorridorId { get; init; }
    public string CorridorHandle { get; init; } = "";
    public string BaselineKey { get; init; } = "";
    public string SubassemblyName { get; init; } = "";
    public bool Left { get; init; }
    public string Label { get; init; } = "";
    public double Station { get; init; }
    public List<CurbSample> Samples { get; } = new();

    /// <summary>O meio-fio foi emendado com a curva da esquina (retorno) de outra baseline.</summary>
    public bool CurveSplice { get; set; }

    /// <summary>Altura normal do meio-fio (topo - pe) nas pontas do trecho lido, fora do rebaixo.</summary>
    public double? NormalExposed
    {
        get
        {
            var ends = new List<double>();
            // So as secoes da propria baseline (as emendadas de outra baseline vem sem topo).
            foreach (CurbSample s in new[] { Samples.FirstOrDefault(x => x.Top is not null), Samples.LastOrDefault(x => x.Top is not null) })
            {
                if (s.Top is double top)
                    ends.Add(top - s.Foot.Z);
            }
            return ends.Count == 0 ? null : ends.Max();
        }
    }
}

/// <summary>Geometria calculada: grades do plato e das tampas, contornos em planta e alvos dos rebaixos.</summary>
internal sealed class TravessiaGeometry
{
    public Point3d Origin { get; init; }
    public Vector3d Along { get; init; }
    public Vector3d Across { get; init; }
    public double HalfPlatform { get; init; }
    public double Total { get; init; }

    public Point3d[,] Top { get; init; } = new Point3d[0, 0];
    public Point3d[,] Bottom { get; init; } = new Point3d[0, 0];

    public List<(Point3d[,] Top, Point3d[,] Bottom, bool SideA)> Lids { get; } = new();

    public List<Point3d> EdgeA { get; } = new();
    public List<Point3d> EdgeB { get; } = new();
    public List<Point3d> FootA { get; } = new();
    public List<Point3d> FootB { get; } = new();
    public List<double> Rows { get; } = new();

    public double HeightA { get; init; }
    public double HeightB { get; init; }

    /// <summary>Altura final do meio-fio (exposto) que deixa o topo do meio-fio no nivel do plato.</summary>
    public double TargetExposedA { get; init; }
    public double TargetExposedB { get; init; }

    public double WidthAtAxis { get; init; }
    public double TopElevationAtCenter { get; init; }
    public string RoadSource { get; set; } = "";
    public List<string> Notes { get; } = new();
}

internal static partial class MFTravessia
{
    public const string RegApp = "C3D_MFTRAVESSIA";
    public const string Marker = "MFTRAVESSIA";
    public const string PartPlato = "PLATO";
    public const string PropertySetName = "C3D_TRAVESSIA";

    // Espessura minima do solido no pe da rampa (so para o solido fechar).
    private const double MinThickness = 0.005;
    private const double Step = 0.50;

    // ------------------------------------------------------------------
    // Leitura do meio-fio
    // ------------------------------------------------------------------

    /// <summary>
    /// Le o meio-fio do rebaixo em [from, to] da baseline: borda da pista = origem da subassembly
    /// (P1 do MFSARJETA) com a elevacao; pe do meio-fio = ponto mais longe dos links "Sarjeta";
    /// topo = ponto mais alto dos links do meio-fio ("Curb", GUIA*, MF_*). Mesmo caminho da planta do rebaixo (links + nome).
    /// </summary>
    public static TravessiaSide ReadSide(AcadDb.Database db, RebaixoInfo r, double station, double from, double to)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(r.CorridorId, AcadDb.OpenMode.ForRead) is not CivilCorridor corridor)
            throw new InvalidOperationException($"O corredor de {r.Label} nao existe mais.");
        CivilDb.Baseline baseline = FindBaseline(corridor, r.BaselineKey)
            ?? throw new InvalidOperationException($"A baseline de {r.Label} nao existe mais.");

        Point3d near = r.Path.Count > 0 ? r.Path[r.Path.Count / 2] : r.P1;
        List<CurbSample> samples = ReadSamples(tr, baseline, r.SubassemblyName, from, to, near);
        var side = new TravessiaSide
        {
            CorridorId = r.CorridorId,
            CorridorHandle = r.CorridorId.Handle.ToString(),
            BaselineKey = r.BaselineKey,
            SubassemblyName = r.SubassemblyName,
            Left = r.Left,
            Label = r.Label,
            Station = station
        };
        side.Samples.AddRange(samples);
        tr.Commit();
        ExtendAcrossBaselines(db, side, from, to);
        return side;
    }

    /// <summary>O mesmo, pelos dados guardados na travessia (MFTRAVESSIAREFAZER).</summary>
    public static TravessiaSide ReadSide(AcadDb.Database db, AcadDb.ObjectId corridorId, string baselineKey, string subassembly,
        bool left, string label, Point3d near, double station, double from, double to)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(corridorId, AcadDb.OpenMode.ForRead) is not CivilCorridor corridor)
            throw new InvalidOperationException($"O corredor de {label} nao existe mais.");
        CivilDb.Baseline baseline = FindBaseline(corridor, baselineKey)
            ?? throw new InvalidOperationException($"A baseline de {label} nao existe mais.");
        List<CurbSample> samples = ReadSamples(tr, baseline, subassembly, from, to, near);
        var side = new TravessiaSide
        {
            CorridorId = corridorId,
            CorridorHandle = corridorId.Handle.ToString(),
            BaselineKey = baselineKey,
            SubassemblyName = subassembly,
            Left = left,
            Label = label,
            Station = station
        };
        side.Samples.AddRange(samples);
        tr.Commit();
        ExtendAcrossBaselines(db, side, from, to);
        return side;
    }

    /// <summary>
    /// O meio-fio pode continuar em outra baseline (o corredor troca de baseline perto da esquina):
    /// se o trecho lido nao chega em <paramref name="from"/>/<paramref name="to"/>, emenda o meio-fio
    /// de outra baseline/corredor que comeca a menos de 1,5 m da ponta e segue para fora.
    /// </summary>
    private static void ExtendAcrossBaselines(AcadDb.Database db, TravessiaSide side, double from, double to)
    {
        List<CurbSample> own = side.Samples;
        if (own.Count < 2)
            return;
        // O trecho pedido tem 4 m de folga de cada lado (ReadSide): so emenda quando falta mais que a folga.
        bool needStart = own[0].Station > from + 3.0, needEnd = own[^1].Station < to - 3.0;
        if (!needStart && !needEnd)
            return;
        try
        {
            foreach (bool atStart in new[] { true, false })
            {
                if (atStart ? !needStart : !needEnd)
                    continue;
                CurbSample tip = atStart ? own[0] : own[^1];
                CurbSample inner = atStart ? own[1] : own[^2];
                Vector3d outward = Flat(tip.Edge - inner.Edge);
                if (outward.Length < 1e-6)
                    continue;
                outward = outward.GetNormal();
                double missing = (atStart ? own[0].Station - from : to - own[^1].Station) + 2.0;

                // Linha de outra baseline/corredor que passa a menos de 1,5 m da ponta (encostando ou
                // sobreposta). O sentido e lido no ponto mais perto da ponta (a linha inteira pode ir para
                // outro lado, como o retorno da esquina). Prefere a continuacao reta; sem ela, segue a curva
                // da esquina ate ela ficar de lado (pedido do Heyvid: a rampa acompanha a curva).
                List<CurbSample>? best = null;
                bool bestCurve = false;
                double bestD = 1.5;
                foreach (List<CurbSample> line in CurbLines(db, tip.Edge, 15.0))
                {
                    int k = Enumerable.Range(0, line.Count).OrderBy(x => Dist2d(line[x].Edge, tip.Edge)).First();
                    double d = Dist2d(line[k].Edge, tip.Edge);
                    if (d >= 1.5)
                        continue;
                    // Anda para o lado em que a linha sai da ponta para fora.
                    double fwd = k + 1 < line.Count ? Flat(line[k + 1].Edge - line[k].Edge).DotProduct(outward) : double.MinValue;
                    double back = k > 0 ? Flat(line[k - 1].Edge - line[k].Edge).DotProduct(outward) : double.MinValue;
                    int step = fwd >= back ? 1 : -1;
                    var path = new List<CurbSample>();
                    bool curve = false;
                    Point3d last = line[k].Edge;
                    for (int x = k + step; x >= 0 && x < line.Count; x += step)
                    {
                        CurbSample s = line[x];
                        Vector3d tan = Flat(s.Edge - last);
                        if (tan.Length > 1e-6)
                        {
                            double dot = tan.GetNormal().DotProduct(outward);
                            if (dot < 0.2)
                                break; // a curva ja ficou de lado (meio-fio da rua transversal)
                            if (dot < 0.9)
                                curve = true;
                        }
                        last = s.Edge;
                        if (Flat(s.Edge - tip.Edge).DotProduct(outward) > 0.05)
                            path.Add(s);
                    }
                    if (path.Count < 2 || Dist2d(path[0].Edge, tip.Edge) > 2.0)
                        continue;
                    // Reta ganha da curva; entre iguais, a mais perto.
                    if (best is not null && curve && !bestCurve)
                        continue;
                    if (best is not null && curve == bestCurve && d >= bestD)
                        continue;
                    bestD = d;
                    best = path;
                    bestCurve = curve;
                }
                if (best is null)
                    continue;
                if (bestCurve)
                    side.CurveSplice = true;

                var add = new List<CurbSample>();
                double run = 0;
                Point3d prev = tip.Edge;
                foreach (CurbSample s in best)
                {
                    run += Dist2d(prev, s.Edge);
                    prev = s.Edge;
                    double st = atStart ? tip.Station - run : tip.Station + run;
                    add.Add(new CurbSample(st, s.Edge, s.Foot, null));
                    if (run > missing)
                        break;
                }
                double zStep = add[0].Edge.Z - tip.Edge.Z, gap = Dist2d(add[0].Edge, tip.Edge);                if (atStart)
                {
                    add.Reverse();
                    own.InsertRange(0, add.Where(s => s.Station < own[0].Station - 1e-5));
                }
                else
                {
                    own.AddRange(add.Where(s => s.Station > own[^1].Station + 1e-5));
                }
                PluginLog.Info("MFTRAVESSIA", $"{side.Label}: meio-fio emendado com outra baseline ({(bestCurve ? "curva da esquina" : "reta")}) na {(atStart ? "ponta de tras" : "ponta da frente")} ({add.Count} secoes, {N(run)} m; distancia ate a outra linha {N(bestD)} m, diferenca de elevacao na emenda {Cm(zStep)} em {N(gap)} m).");
            }
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso("MFTRAVESSIA", $"{side.Label}: meio-fio de outra baseline nao emendado: {ex.Message}");
        }
    }

    private static List<CurbSample> ReadSamples(AcadDb.Transaction tr, CivilDb.Baseline baseline, string subassemblyName,
        double from, double to, Point3d near)
    {
        const double eps = 0.0005;
        var byName = new Dictionary<AcadDb.ObjectId, List<CurbSample>>();
        var anyMf = new Dictionary<AcadDb.ObjectId, List<CurbSample>>();
        var kind = new Dictionary<AcadDb.ObjectId, int>(); // 0 = nao e MF, 1 = MF com o nome, 2 = outro MF

        foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
        {
            if (Math.Max(region.StartStation, region.EndStation) < from - eps || Math.Min(region.StartStation, region.EndStation) > to + eps)
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
                    if (origin.X < from - eps || origin.X > to + eps)
                        break;

                    AcadDb.ObjectId said = asa.SubassemblyId;
                    if (!kind.TryGetValue(said, out int k))
                    {
                        k = 0;
                        try
                        {
                            if (tr.GetObject(said, AcadDb.OpenMode.ForRead) is CivilDb.Subassembly sa && MFRebaixoCommands.IsMfSubassemblyPublic(sa))
                                k = sa.Name.Equals(subassemblyName, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                        }
                        catch
                        {
                            // Subassembly apagada.
                        }
                        kind[said] = k;
                    }
                    if (k == 0)
                        continue;

                    double o0 = origin.Y;
                    double? footOff = null, footElev = null, top = null;
                    foreach (CivilDb.CalculatedLink link in asa.Links)
                    {
                        bool isCurb = false, isGutter = false;
                        foreach (string code in link.CorridorCodes)
                        {
                            if (string.IsNullOrWhiteSpace(code)) continue;
                            // MFSARJETA: o meio-fio vem com GUIA / MF_T30, sem "Curb" (mesma regra do painel).
                            if (code.Equals("Curb", StringComparison.OrdinalIgnoreCase)
                                || code.StartsWith("GUIA", StringComparison.OrdinalIgnoreCase)
                                || code.StartsWith("MF_", StringComparison.OrdinalIgnoreCase)) isCurb = true;
                            else if (code.Equals("Sarjeta", StringComparison.OrdinalIgnoreCase)) isGutter = true;
                        }
                        if (!isCurb && !isGutter)
                            continue;
                        foreach (CivilDb.CalculatedPoint cp in link.CalculatedPoints)
                        {
                            Point3d soe = cp.StationOffsetElevationToBaseline;
                            if (isGutter && (footOff is null || Math.Abs(soe.Y - o0) > Math.Abs(footOff.Value - o0)))
                            {
                                footOff = soe.Y;
                                footElev = soe.Z;
                            }
                            if (isCurb && (top is null || soe.Z > top.Value))
                                top = soe.Z;
                        }
                    }

                    try
                    {
                        // A elevacao dos pontos do corredor e relativa ao perfil da baseline; o XYZ
                        // devolvido pela baseline ja vem com a elevacao absoluta (licao do 1.13.2).
                        Point3d edge = baseline.StationOffsetElevationToXYZ(new Point3d(origin.X, o0, origin.Z));
                        double profile = edge.Z - origin.Z;
                        Point3d foot = edge;
                        if (footOff is double fo && footElev is double fz)
                            foot = baseline.StationOffsetElevationToXYZ(new Point3d(origin.X, fo, fz));
                        double? topAbs = top is double tv ? tv + profile : null;
                        var target = k == 1 ? byName : anyMf;
                        if (!target.TryGetValue(said, out var list))
                            target[said] = list = new List<CurbSample>();
                        list.Add(new CurbSample(origin.X, edge, foot, topAbs));
                    }
                    catch
                    {
                        // Estaca fora da baseline.
                    }
                }
            }
        }

        // Pelo nome; sem ele, o MF mais perto do rebaixo.
        List<CurbSample>? best = Closest(byName.Values, near) ?? Closest(anyMf.Values, near);
        if (best is null || best.Count < 2)
            return new List<CurbSample>();
        var clean = new List<CurbSample>();
        foreach (CurbSample s in best.OrderBy(x => x.Station))
        {
            if (clean.Count == 0 || s.Station - clean[^1].Station > 1e-5)
                clean.Add(s);
        }
        return clean;
    }

    private static List<CurbSample>? Closest(IEnumerable<List<CurbSample>> lists, Point3d near)
    {
        List<CurbSample>? best = null;
        double bestD = double.MaxValue;
        foreach (List<CurbSample> list in lists)
        {
            if (list.Count < 2)
                continue;
            double d = list.Min(s => Dist2d(s.Edge, near));
            if (d < bestD)
            {
                bestD = d;
                best = list;
            }
        }
        return best;
    }

    private static CivilDb.Baseline? FindBaseline(CivilCorridor corridor, string key)
    {
        foreach (CivilDb.Baseline b in corridor.Baselines)
        {
            if (MFRebaixoCommands.GetBaselineKey(b) == key)
                return b;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Superficie da pista
    // ------------------------------------------------------------------

    /// <summary>
    /// Elevacao da pista entre as duas bordas: a superficie do corredor mais alta que responde no
    /// ponto (a "Top"); sem superficie, null (o chamador usa a reta entre as bordas).
    /// </summary>
    public static Func<double, double, double?> RoadSurface(AcadDb.Database db, IEnumerable<AcadDb.ObjectId> corridorIds, out string description)
    {
        var surfaces = new List<(AcadDb.ObjectId Id, string Name)>();
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (AcadDb.ObjectId cid in corridorIds.Distinct())
            {
                if (tr.GetObject(cid, AcadDb.OpenMode.ForRead) is not CivilCorridor corridor)
                    continue;
                try
                {
                    foreach (CivilDb.CorridorSurface cs in corridor.CorridorSurfaces)
                    {
                        try
                        {
                            if (!cs.SurfaceId.IsNull && !cs.SurfaceId.IsErased)
                                surfaces.Add((cs.SurfaceId, cs.Name));
                        }
                        catch
                        {
                            // Superficie sem objeto.
                        }
                    }
                }
                catch
                {
                    // Corredor sem superficies.
                }
            }
            tr.Commit();
        }

        // Superficie de topo do corredor (nome com TOP/TOPO), para acompanhar o pavimento; sem ela, todas.
        var tops = surfaces.Where(s => s.Name.Contains("TOP", StringComparison.OrdinalIgnoreCase)).ToList();
        if (tops.Count > 0)
            surfaces = tops;

        description = surfaces.Count == 0
            ? "sem superficie no corredor: pista em linha reta entre as bordas"
            : "superficie(s) do corredor: " + string.Join(", ", surfaces.Select(s => s.Name));
        if (surfaces.Count == 0)
            return (_, _) => null;

        return (x, y) =>
        {
            double? best = null;
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            foreach ((AcadDb.ObjectId id, _) in surfaces)
            {
                try
                {
                    if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is CivilDb.Surface s)
                    {
                        double z = s.FindElevationAtXY(x, y);
                        if (!double.IsNaN(z) && (best is null || z > best.Value))
                            best = z;
                    }
                }
                catch
                {
                    // Fora da superficie.
                }
            }
            tr.Commit();
            return best;
        };
    }

    // ------------------------------------------------------------------
    // Geometria
    // ------------------------------------------------------------------

    /// <summary>
    /// Plato da travessia entre o lado A e o lado B, perpendicular a pista no eixo que passa pelos
    /// dois rebaixos. Linhas no sentido dos carros (t) a cada 0,5 m; em cada linha, de borda a borda
    /// da pista (s) a cada 0,5 m. Topo = pista + altura (na rampa, cresce em linha reta de 0 ate a altura).
    /// </summary>
    public static TravessiaGeometry Build(TravessiaSide a, TravessiaSide b, TravessiaParams p,
        Func<double, double, double?> roadZ, double? normalA, double? normalB)
    {
        if (a.Samples.Count < 2)
            throw new InvalidOperationException($"Nao deu para ler o meio-fio de {a.Label} no corredor (Rebuild no corredor e tente de novo).");
        if (b.Samples.Count < 2)
            throw new InvalidOperationException($"Nao deu para ler o meio-fio de {b.Label} no corredor (Rebuild no corredor e tente de novo).");
        if (p.Plataforma < 1.0)
            throw new InvalidOperationException("A plataforma tem que ter pelo menos 1 m.");
        if (p.Rampa < 0.10)
            throw new InvalidOperationException("A rampa tem que ter pelo menos 0,10 m.");

        CurbSample ca = At(a.Samples, a.Station);
        CurbSample cb = At(b.Samples, b.Station);

        Vector3d dirA = Flat(At(a.Samples, a.Station + 0.5).Edge - At(a.Samples, a.Station - 0.5).Edge);
        Vector3d dirB = Flat(At(b.Samples, b.Station + 0.5).Edge - At(b.Samples, b.Station - 0.5).Edge);
        if (dirA.Length < 1e-6 || dirB.Length < 1e-6)
            throw new InvalidOperationException("Nao deu para achar a direcao da pista nos rebaixos.");
        dirA = dirA.GetNormal();
        dirB = dirB.GetNormal();
        if (dirA.DotProduct(dirB) < 0)
            dirB = dirB.Negate();
        Vector3d along = (dirA + dirB).GetNormal();
        Vector3d across = new Vector3d(-along.Y, along.X, 0);
        Vector3d ab = Flat(cb.Edge - ca.Edge);
        if (across.DotProduct(ab) < 0)
            across = across.Negate();
        if (ab.Length < 1.0)
            throw new InvalidOperationException($"{a.Label} e {b.Label} estao a menos de 1 m um do outro: escolha rebaixos dos dois lados da rua.");
        if (Math.Abs(ab.GetNormal().DotProduct(across)) < 0.5)
            throw new InvalidOperationException($"{a.Label} e {b.Label} nao estao um de frente para o outro (ficam no mesmo lado da pista?).");

        Point3d origin = new((ca.Edge.X + cb.Edge.X) / 2.0, (ca.Edge.Y + cb.Edge.Y) / 2.0, 0);
        double half = p.Plataforma / 2.0;
        double total = half + p.Rampa;

        List<double> rows = Stations(-total, -half);
        rows.AddRange(Stations(-half, half).Skip(1));
        rows.AddRange(Stations(half, total).Skip(1));

        var ea = new List<Point3d>();
        var eb = new List<Point3d>();
        var fa = new List<Point3d>();
        var fb = new List<Point3d>();
        List<Point3d> lineA = a.Samples.Select(s => s.Edge).ToList();
        List<Point3d> lineB = b.Samples.Select(s => s.Edge).ToList();
        List<Point3d> footA = a.Samples.Select(s => s.Foot).ToList();
        List<Point3d> footB = b.Samples.Select(s => s.Foot).ToList();
        int mouth = 0;
        foreach (double t in rows)
        {
            Point3d basePt = origin + (along * t);
            // Depois da curva da esquina (boca da rua transversal) a borda e o prolongamento do
            // alinhamento do meio-fio: a rampa nao invade a transversal alem dele.
            Point3d pa = Cross(lineA, basePt, across)
                ?? (a.CurveSplice ? Alignment(a, basePt, across, ref mouth) : null)
                ?? throw new InvalidOperationException(OutOfCurb(a, total));
            Point3d pb = Cross(lineB, basePt, across)
                ?? (b.CurveSplice ? Alignment(b, basePt, across, ref mouth) : null)
                ?? throw new InvalidOperationException(OutOfCurb(b, total));
            ea.Add(pa);
            eb.Add(pb);
            fa.Add(Cross(footA, basePt, across) ?? pa);
            fb.Add(Cross(footB, basePt, across) ?? pb);
        }

        int mid = rows.IndexOf(rows.OrderBy(x => Math.Abs(x)).First());
        Point3d ea0 = ea[mid], eb0 = eb[mid], fa0 = fa[mid], fb0 = fb[mid];

        // Altura do plato acima da borda da pista em cada lado.
        double ha, hb;
        if (p.Tipo == TravessiaTipo.Elevada)
        {
            double na = normalA ?? a.NormalExposed ?? throw new InvalidOperationException($"Nao deu para ler a altura do meio-fio de {a.Label}.");
            double nb = normalB ?? b.NormalExposed ?? throw new InvalidOperationException($"Nao deu para ler a altura do meio-fio de {b.Label}.");
            ha = fa0.Z + na - ea0.Z;
            hb = fb0.Z + nb - eb0.Z;
        }
        else
        {
            ha = hb = p.Altura;
        }
        if (ha < 0.02 || hb < 0.02)
            throw new InvalidOperationException($"Altura do plato muito baixa ({Cm(Math.Min(ha, hb))}).");

        int cols = Math.Max(3, (int)Math.Ceiling(Dist2d(ea0, eb0) / Step) + 1);
        var top = new Point3d[rows.Count, cols];
        var bottom = new Point3d[rows.Count, cols];
        int fromSurface = 0, linear = 0;
        double maxDev = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            double t = Math.Abs(rows[i]);
            double f = t <= half + 1e-6 ? 1.0 : Math.Max(0.0, (total - t) / p.Rampa);
            for (int j = 0; j < cols; j++)
            {
                double w = j / (double)(cols - 1);
                double x = ea[i].X + ((eb[i].X - ea[i].X) * w);
                double y = ea[i].Y + ((eb[i].Y - ea[i].Y) * w);
                double zLine = ea[i].Z + ((eb[i].Z - ea[i].Z) * w);
                double z = zLine;
                if (j > 0 && j < cols - 1)
                {
                    double? zs = roadZ(x, y);
                    if (zs is double v && Math.Abs(v - zLine) < 0.60)
                    {
                        z = v;
                        fromSurface++;
                        maxDev = Math.Max(maxDev, Math.Abs(v - zLine));
                    }
                    else
                    {
                        linear++;
                    }
                }
                double h = ha + ((hb - ha) * w);
                bottom[i, j] = new Point3d(x, y, z);
                top[i, j] = new Point3d(x, y, z + Math.Max(h * f, MinThickness));
            }
        }

        TravessiaGeometry result = new()
        {
            Origin = origin,
            Along = along,
            Across = across,
            HalfPlatform = half,
            Total = total,
            Top = top,
            Bottom = bottom,
            HeightA = ha,
            HeightB = hb,
            TargetExposedA = ea0.Z + ha - fa0.Z,
            TargetExposedB = eb0.Z + hb - fb0.Z,
            WidthAtAxis = Dist2d(ea0, eb0),
            TopElevationAtCenter = top[mid, cols / 2].Z,
            RoadSource = fromSurface > 0
                ? $"pista pela superficie do corredor ({fromSurface} pontos; maior diferenca para a reta entre as bordas {Cm(maxDev)})"
                : "pista em linha reta entre as bordas (sem superficie do corredor no local)"
        };
        result.Rows.AddRange(rows);
        result.EdgeA.AddRange(ea);
        result.EdgeB.AddRange(eb);
        result.FootA.AddRange(fa);
        result.FootB.AddRange(fb);
        if (a.CurveSplice || b.CurveSplice)
            result.Notes.Add($"a rampa acompanha a curva da esquina em {(a.CurveSplice ? a.Label : "")}{(a.CurveSplice && b.CurveSplice ? " e " : "")}{(b.CurveSplice ? b.Label : "")} (meio-fio de outra baseline).");
        if (mouth > 0)
            result.Notes.Add($"{mouth} linha(s) da rampa passam da curva da esquina: limitadas pelo prolongamento do alinhamento do meio-fio.");
        if (linear > 0 && fromSurface > 0)
            result.Notes.Add($"{linear} ponto(s) fora da superficie do corredor: usada a reta entre as bordas.");

        // Tampas de concreto sobre a sarjeta, so na plataforma: topo no nivel do plato na borda da pista.
        if (p.Tampa)
        {
            AddLid(result, rows, ea, fa, ha, p, half, true, a.Label);
            AddLid(result, rows, eb, fb, hb, p, half, false, b.Label);
        }

        return result;
    }

    private static void AddLid(TravessiaGeometry geo, List<double> rows, List<Point3d> edge, List<Point3d> foot,
        double height, TravessiaParams p, double half, bool sideA, string label)
    {
        var idx = Enumerable.Range(0, rows.Count).Where(i => Math.Abs(rows[i]) <= half + 1e-6).ToList();
        if (idx.Count < 2)
            return;
        double width = idx.Average(i => Dist2d(edge[i], foot[i]));
        if (width < 0.05)
        {
            geo.Notes.Add($"{label}: sem sarjeta lida entre a pista e o meio-fio; tampa nao desenhada.");
            return;
        }
        var top = new Point3d[idx.Count, 2];
        var bottom = new Point3d[idx.Count, 2];
        double minGap = double.MaxValue;
        for (int k = 0; k < idx.Count; k++)
        {
            int i = idx[k];
            double z = edge[i].Z + height;
            double zb = z - p.TampaEspessura;
            top[k, 0] = new Point3d(edge[i].X, edge[i].Y, z);
            top[k, 1] = new Point3d(foot[i].X, foot[i].Y, z);
            bottom[k, 0] = new Point3d(edge[i].X, edge[i].Y, zb);
            bottom[k, 1] = new Point3d(foot[i].X, foot[i].Y, zb);
            minGap = Math.Min(minGap, zb - foot[i].Z);
        }
        geo.Lids.Add((top, bottom, sideA));
        if (minGap < 0.05)
            geo.Notes.Add($"{label}: so {Cm(minGap)} livres entre a tampa ({Cm(p.TampaEspessura)} de espessura) e o fundo da sarjeta.");
        else
            geo.Notes.Add($"{label}: tampa de {N(width)} m de largura, {Cm(minGap)} livres para a agua no pe do meio-fio.");
    }

    /// <summary>
    /// Na boca da rua transversal (a linha passa da curva da esquina): cruzamento com a reta que
    /// prolonga o alinhamento do meio-fio do proprio rebaixo, na ponta mais perto.
    /// </summary>
    private static Point3d? Alignment(TravessiaSide side, Point3d basePt, Vector3d across, ref int count)
    {
        List<CurbSample> own = side.Samples.Where(s => s.Top is not null).ToList();
        if (own.Count < 2)
            return null;
        bool atStart = Dist2d(own[0].Edge, basePt) <= Dist2d(own[^1].Edge, basePt);
        Point3d p0 = atStart ? own[0].Edge : own[^1].Edge;
        Point3d p1 = atStart ? own[1].Edge : own[^2].Edge;
        Vector3d e = Flat(p0 - p1);
        if (e.Length < 1e-6)
            return null;
        e = e.GetNormal();
        double den = (across.X * (-e.Y)) - (across.Y * (-e.X));
        if (Math.Abs(den) < 1e-9)
            return null;
        double rx = p0.X - basePt.X, ry = p0.Y - basePt.Y;
        double w = ((across.X * ry) - (across.Y * rx)) / den;
        if (w < -0.05 || w > 10.0)
            return null;
        count++;
        return new Point3d(p0.X + (e.X * w), p0.Y + (e.Y * w), p0.Z);
    }

    private static string OutOfCurb(TravessiaSide s, double total) =>
        $"A travessia ({N(2 * total)} m com as rampas, estacas {N(s.Station - total)} a {N(s.Station + total)}) passa do meio-fio que existe no corredor em {s.Label} " +
        $"(lido de {N(s.Samples[0].Station)} a {N(s.Samples[^1].Station)}, ja procurando o meio-fio das baselines vizinhas; se ali o meio-fio vira a esquina, a travessia nao passa). Diminua a plataforma/rampas, leve os rebaixos para mais longe da esquina ou, num cruzamento, una as travessias (MFTRAVESSIAUNIR).";

    private static List<double> Stations(double from, double to)
    {
        int n = Math.Max(1, (int)Math.Ceiling((to - from) / Step - 1e-9));
        var list = new List<double>(n + 1);
        for (int i = 0; i <= n; i++)
            list.Add(from + ((to - from) * i / n));
        return list;
    }

    /// <summary>Ponto da linha (com Z interpolado) cruzado pela reta basePt + across*s, o mais perto de basePt.</summary>
    private static Point3d? Cross(List<Point3d> line, Point3d basePt, Vector3d across)
    {
        Point3d? best = null;
        double bestS = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            Point3d a = line[i], b = line[i + 1];
            double ex = b.X - a.X, ey = b.Y - a.Y;
            double den = (across.X * (-ey)) - (across.Y * (-ex));
            if (Math.Abs(den) < 1e-12)
                continue;
            double rx = a.X - basePt.X, ry = a.Y - basePt.Y;
            // basePt + across*s = a + e*w
            double s = ((rx * (-ey)) - (ry * (-ex))) / den;
            double w = ((across.X * ry) - (across.Y * rx)) / den;
            if (w < -1e-6 || w > 1 + 1e-6)
                continue;
            if (Math.Abs(s) < Math.Abs(bestS))
            {
                bestS = s;
                w = Math.Clamp(w, 0, 1);
                best = new Point3d(a.X + (ex * w), a.Y + (ey * w), a.Z + ((b.Z - a.Z) * w));
            }
        }
        return best;
    }

    internal static CurbSample At(List<CurbSample> samples, double station)
    {
        if (station <= samples[0].Station)
            return samples[0];
        if (station >= samples[^1].Station)
            return samples[^1];
        for (int i = 0; i + 1 < samples.Count; i++)
        {
            CurbSample s0 = samples[i], s1 = samples[i + 1];
            if (station > s1.Station)
                continue;
            double w = (station - s0.Station) / Math.Max(1e-9, s1.Station - s0.Station);
            return new CurbSample(station, Lerp(s0.Edge, s1.Edge, w), Lerp(s0.Foot, s1.Foot, w),
                s0.Top is double a && s1.Top is double b ? a + ((b - a) * w) : s0.Top ?? s1.Top);
        }
        return samples[^1];
    }

    private static Point3d Lerp(Point3d a, Point3d b, double w) =>
        new(a.X + ((b.X - a.X) * w), a.Y + ((b.Y - a.Y) * w), a.Z + ((b.Z - a.Z) * w));

    private static Vector3d Flat(Vector3d v) => new(v.X, v.Y, 0);

    private static double Dist2d(Point3d a, Point3d b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    public static string N(double v, string fmt = "0.00") => v.ToString(fmt, CultureInfo.GetCultureInfo("pt-BR"));

    public static string Cm(double m) => (m * 100.0).ToString("0.0", CultureInfo.GetCultureInfo("pt-BR")) + " cm";

    // ------------------------------------------------------------------
    // Desenho
    // ------------------------------------------------------------------

    /// <summary>Dados gravados no solido do plato (XData) para refazer a travessia depois.</summary>
    internal sealed class Stored
    {
        public string Id { get; set; } = "";
        public TravessiaParams Params { get; set; } = new();
        public Point3d Origin { get; set; }
        public string CorridorA { get; set; } = "";
        public string BaselineA { get; set; } = "";
        public string SubassemblyA { get; set; } = "";
        public bool LeftA { get; set; }
        public string LabelA { get; set; } = "";
        public string CorridorB { get; set; } = "";
        public string BaselineB { get; set; } = "";
        public string SubassemblyB { get; set; } = "";
        public bool LeftB { get; set; }
        public string LabelB { get; set; } = "";

        /// <summary>Uniao de travessias num cruzamento (MFTRAVESSIAUNIR); vazio = travessia sozinha.</summary>
        public string UnionId { get; set; } = "";

        /// <summary>Na uniao: lado de fora do cruzamento no sentido Along (+1/-1); so essa rampa fica.</summary>
        public int OuterSign { get; set; }
    }

    /// <summary>
    /// Desenha a travessia: solido do plato (com Property Set C3D_TRAVESSIA), tampas, hachura do
    /// plato e das rampas, faixa zebrada, triangulos das rampas e hachura das tampas. Apaga antes o
    /// que houver com o mesmo id. Abre a propria transacao.
    /// </summary>
    public static (int Entities, List<string> Notes) Draw(AcadDb.Database db, TravessiaGeometry g, Stored data, MFRebaixoSettings settings)
    {
        var notes = new List<string>();
        int count = 0;
        string layer = string.IsNullOrWhiteSpace(settings.TravessiaLayer) ? "MF-TRAVESSIA" : settings.TravessiaLayer.Trim();
        string layer3d = layer + "-3D", layerSign = layer + "-SINALIZACAO", layerLid = layer + "-TAMPA";

        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        Erase(tr, db, data.Id);
        EnsureRegApp(tr, db);
        EnsureLayer(tr, db, layer, 8, "Travessias elevadas/semi-elevadas entre rebaixos (planta)");
        EnsureLayer(tr, db, layer3d, 8, "Travessias entre rebaixos: solidos do plato");
        EnsureLayer(tr, db, layerSign, 7, "Travessias entre rebaixos: faixa e triangulos (branco)");
        EnsureLayer(tr, db, layerLid, 40, "Travessias entre rebaixos: tampa de concreto sobre a sarjeta");

        var bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        var ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);
        var hatches = new AcadDb.ObjectIdCollection();
        var signs = new AcadDb.ObjectIdCollection();

        // Na uniao de um cruzamento (MFTRAVESSIAUNIR) so fica a rampa de fora; o plato e da uniao.
        int outer = string.IsNullOrEmpty(data.UnionId) ? 0 : Math.Sign(data.OuterSign);
        List<int> keep = Enumerable.Range(0, g.Rows.Count).Where(i => outer == 0 || outer * g.Rows[i] >= g.HalfPlatform - 1e-6).ToList();

        // Solido do plato (ou so da rampa de fora).
        AcadDb.Entity? plato = MeshSolid(tr, ms, SubRows(g.Top, keep), SubRows(g.Bottom, keep), layer3d, out string mode);
        if (plato is not null)
        {
            plato.XData = PlatoXData(data);
            count++;
            notes.Add("plato: " + mode);
            if (plato is AcadDb.Solid3d solid)
            {
                if (!TryPropertySet(tr, db, solid, PropertyValues(data, g, "Plato da travessia " + data.Params.TipoTexto), out string psMsg))
                    notes.Add("Property Set nao gravado: " + psMsg);
            }
        }

        // Tampas.
        foreach ((Point3d[,] top, Point3d[,] bottom, bool sideA) in g.Lids)
        {
            AcadDb.Entity? lid = MeshSolid(tr, ms, top, bottom, layerLid, out string lidMode);
            if (lid is null)
                continue;
            lid.XData = PartXData(data.Id, sideA ? "TAMPA_A" : "TAMPA_B");
            count++;
            if (lid is AcadDb.Solid3d ls)
                TryPropertySet(tr, db, ls, PropertyValues(data, g, "Tampa de concreto sobre a sarjeta"), out _);
            if (lidMode.Length > 0 && !lidMode.StartsWith("solido", StringComparison.Ordinal))
                notes.Add("tampa: " + lidMode);
        }

        // Planta: plato e rampas.
        int n = g.Rows.Count;
        List<int> plat = Enumerable.Range(0, n).Where(i => Math.Abs(g.Rows[i]) <= g.HalfPlatform + 1e-6).ToList();
        List<int> rampIn = Enumerable.Range(0, n).Where(i => g.Rows[i] <= -g.HalfPlatform + 1e-6).ToList();
        List<int> rampOut = Enumerable.Range(0, n).Where(i => g.Rows[i] >= g.HalfPlatform - 1e-6).ToList();
        if (outer == 0)
            count += Hatch(tr, db, ms, Band(g.EdgeA, g.EdgeB, plat), layer, "SOLID", 1, 0, 256, data.Id, "PLANTA_PLATO", hatches);
        if (outer <= 0)
            count += Hatch(tr, db, ms, Band(g.EdgeA, g.EdgeB, rampIn), layer, "SOLID", 1, 0, 9, data.Id, "PLANTA_RAMPA", hatches);
        if (outer >= 0)
            count += Hatch(tr, db, ms, Band(g.EdgeA, g.EdgeB, rampOut), layer, "SOLID", 1, 0, 9, data.Id, "PLANTA_RAMPA", hatches);

        // Tampas em planta.
        if (g.Lids.Count > 0)
        {
            foreach (bool sideA in g.Lids.Select(l => l.SideA))
            {
                List<Point3d> e = sideA ? g.EdgeA : g.EdgeB, f = sideA ? g.FootA : g.FootB;
                count += Hatch(tr, db, ms, Band(e, f, plat), layerLid, "ANSI37", 0.03, Angle(g.Along), 256, data.Id, "PLANTA_TAMPA", hatches);
            }
        }

        // Faixa zebrada: listras no sentido dos carros, empilhadas de borda a borda.
        double sa = (g.EdgeA[plat[plat.Count / 2]] - g.Origin).DotProduct(g.Across);
        double sb = (g.EdgeB[plat[plat.Count / 2]] - g.Origin).DotProduct(g.Across);
        double s0 = Math.Min(sa, sb) + 0.30, s1 = Math.Max(sa, sb) - 0.30;
        double stripe = settings.TravessiaListra, gap = settings.TravessiaListraEspaco;
        double len = Math.Max(g.HalfPlatform * 2.0 - 1.0, g.HalfPlatform * 1.2);
        int stripes = (int)Math.Floor((s1 - s0 + gap) / (stripe + gap));
        if (stripes > 0)
        {
            double used = (stripes * stripe) + ((stripes - 1) * gap);
            double start = s0 + ((s1 - s0 - used) / 2.0);
            for (int k = 0; k < stripes; k++)
            {
                double a0 = start + (k * (stripe + gap));
                var rect = new List<Point3d>
                {
                    Local(g, -len / 2, a0), Local(g, len / 2, a0), Local(g, len / 2, a0 + stripe), Local(g, -len / 2, a0 + stripe)
                };
                count += Hatch(tr, db, ms, rect, layerSign, "SOLID", 1, 0, 256, data.Id, "FAIXA", signs);
                count += PaintSolid(tr, ms, g, -len / 2, len / 2, a0 + (stripe / 2.0), stripe / 2.0, stripe / 2.0, layerSign, data.Id, "FAIXA_3D");
            }
        }

        // Triangulos nas rampas: base no pe da rampa, ponta para o plato.
        double rampLen = g.Total - g.HalfPlatform;
        double triH = Math.Min(0.80, rampLen * 0.8), triB = 0.90, triGap = 0.90;
        int tris = (int)Math.Floor((s1 - s0 + triGap) / (triB + triGap));
        if (tris > 0 && triH > 0.05)
        {
            double used = (tris * triB) + ((tris - 1) * triGap);
            double start = s0 + ((s1 - s0 - used) / 2.0);
            foreach (double sign in new[] { -1.0, 1.0 })
            {
                if (outer != 0 && Math.Sign(sign) != outer)
                    continue;
                double toe = sign * (g.Total - 0.05);
                double tip = toe - (sign * triH);
                for (int k = 0; k < tris; k++)
                {
                    double a0 = start + (k * (triB + triGap));
                    var tri = new List<Point3d> { Local(g, toe, a0), Local(g, toe, a0 + triB), Local(g, tip, a0 + (triB / 2.0)) };
                    count += Hatch(tr, db, ms, tri, layerSign, "SOLID", 1, 0, 256, data.Id, "TRIANGULO", signs);
                    count += PaintSolid(tr, ms, g, toe, tip, a0 + (triB / 2.0), triB / 2.0, 0.01, layerSign, data.Id, "TRIANGULO_3D");
                }
            }
        }

        try
        {
            var order = (AcadDb.DrawOrderTable)tr.GetObject(ms.DrawOrderTableId, AcadDb.OpenMode.ForWrite);
            if (hatches.Count > 0)
                order.MoveToTop(hatches);
            if (signs.Count > 0)
                order.MoveToTop(signs);
        }
        catch (System.Exception ex)
        {
            notes.Add("ordem de desenho nao alterada: " + ex.Message);
        }

        tr.Commit();
        return (count, notes);
    }

    // Espessura da pintura (faixa e triangulos) em solido, por cima do plato.
    private const double PaintThickness = 0.003;

    /// <summary>
    /// Solido fino da sinalizacao por cima do plato: de t0 a t1 (sentido dos carros), centrado em
    /// sCenter (de borda a borda), com meia largura h0 em t0 e h1 em t1 (triangulo: h1 quase zero).
    /// </summary>
    private static int PaintSolid(AcadDb.Transaction tr, AcadDb.BlockTableRecord ms, TravessiaGeometry g,
        double t0, double t1, double sCenter, double h0, double h1, string layer, string id, string part)
    {
        int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(t1 - t0) / Step) + 1);
        var top = new Point3d[n, 2];
        var bottom = new Point3d[n, 2];
        for (int i = 0; i < n; i++)
        {
            double f = i / (double)(n - 1);
            double t = t0 + ((t1 - t0) * f);
            double h = h0 + ((h1 - h0) * f);
            for (int c = 0; c < 2; c++)
            {
                Point3d q = Local(g, t, sCenter + (c == 0 ? -h : h));
                double z = TopAt(g, q);
                bottom[i, c] = new Point3d(q.X, q.Y, z);
                top[i, c] = new Point3d(q.X, q.Y, z + PaintThickness);
            }
        }
        AcadDb.Entity? e = MeshSolid(tr, ms, top, bottom, layer, out _);
        if (e is null)
            return 0;
        e.XData = PartXData(id, part);
        return 1;
    }

    /// <summary>Elevacao do topo do plato no ponto (interpolada na grade do plato).</summary>
    private static double TopAt(TravessiaGeometry g, Point3d p)
    {
        int rows = g.Rows.Count, cols = g.Top.GetLength(1);
        double t = (p - g.Origin).DotProduct(g.Along);
        int i = 0;
        while (i + 2 < rows && g.Rows[i + 1] < t)
            i++;
        double RowZ(int r)
        {
            Point3d a = g.EdgeA[r], b = g.EdgeB[r];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double w = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / Math.Max(1e-9, (dx * dx) + (dy * dy)), 0, 1) * (cols - 1);
            int c = Math.Min(cols - 2, (int)Math.Floor(w));
            double u = w - c;
            return g.Top[r, c].Z + ((g.Top[r, c + 1].Z - g.Top[r, c].Z) * u);
        }
        double z0 = RowZ(i), z1 = RowZ(i + 1);
        double f = Math.Clamp((t - g.Rows[i]) / Math.Max(1e-9, g.Rows[i + 1] - g.Rows[i]), 0, 1);
        return z0 + ((z1 - z0) * f);
    }

    private static Point3d[,] SubRows(Point3d[,] grid, List<int> rows)
    {
        int cols = grid.GetLength(1);
        var sub = new Point3d[rows.Count, cols];
        for (int i = 0; i < rows.Count; i++)
            for (int c = 0; c < cols; c++)
                sub[i, c] = grid[rows[i], c];
        return sub;
    }

    private static Point3d Local(TravessiaGeometry g, double t, double s) => g.Origin + (g.Along * t) + (g.Across * s);

    private static double Angle(Vector3d v) => Math.Atan2(v.Y, v.X);

    private static List<Point3d> Band(List<Point3d> a, List<Point3d> b, List<int> idx)
    {
        var loop = new List<Point3d>();
        foreach (int i in idx)
            loop.Add(a[i]);
        for (int k = idx.Count - 1; k >= 0; k--)
            loop.Add(b[idx[k]]);
        return loop;
    }

    private static int Hatch(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockTableRecord ms, List<Point3d> pts,
        string layer, string pattern, double scale, double angle, short color, string id, string part, AcadDb.ObjectIdCollection created)
    {
        var loop = new Point2dCollection();
        var bulges = new DoubleCollection();
        foreach (Point3d p in pts)
        {
            var q = new Point2d(p.X, p.Y);
            if (loop.Count > 0 && loop[loop.Count - 1].GetDistanceTo(q) < 0.0005)
                continue;
            loop.Add(q);
            bulges.Add(0.0);
        }
        if (loop.Count < 3)
            return 0;
        loop.Add(loop[0]);
        bulges.Add(0.0);

        var hatch = new AcadDb.Hatch();
        hatch.SetDatabaseDefaults(db);
        hatch.Layer = layer;
        hatch.ColorIndex = color;
        hatch.Normal = Vector3d.ZAxis;
        hatch.Elevation = 0.0;
        ms.AppendEntity(hatch);
        tr.AddNewlyCreatedDBObject(hatch, true);
        try
        {
            hatch.SetHatchPattern(AcadDb.HatchPatternType.PreDefined, pattern);
        }
        catch
        {
            hatch.SetHatchPattern(AcadDb.HatchPatternType.PreDefined, "SOLID");
        }
        if (!pattern.Equals("SOLID", StringComparison.OrdinalIgnoreCase))
        {
            hatch.PatternScale = scale;
            hatch.PatternAngle = angle;
        }
        hatch.Associative = false;
        hatch.AppendLoop(AcadDb.HatchLoopTypes.External | AcadDb.HatchLoopTypes.Polyline, loop, bulges);
        hatch.EvaluateHatch(true);
        hatch.XData = PartXData(id, part);
        created.Add(hatch.ObjectId);
        return 1;
    }

    /// <summary>
    /// Solido fechado a partir de duas grades (topo e base) de mesmo tamanho: malha de triangulos
    /// (sempre planos) convertida em Solid3d. Montada perto da origem (coordenadas UTM perdem
    /// precisao no modelador) e levada para o lugar depois. Se a conversao falhar, fica a malha.
    /// </summary>
    private static AcadDb.Entity? MeshSolid(AcadDb.Transaction tr, AcadDb.BlockTableRecord ms, Point3d[,] top, Point3d[,] bottom,
        string layer, out string mode)
    {
        int rows = top.GetLength(0), cols = top.GetLength(1);
        mode = "";
        if (rows < 2 || cols < 2)
            return null;
        Point3d o = top[0, 0];
        Vector3d shift = new(o.X, o.Y, Math.Floor(o.Z));
        var verts = new Point3dCollection();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                verts.Add(top[r, c] - shift);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                verts.Add(bottom[r, c] - shift);
        int T(int r, int c) => (r * cols) + c;
        int B(int r, int c) => (rows * cols) + (r * cols) + c;

        var faces = new List<int[]>();
        for (int r = 0; r + 1 < rows; r++)
        {
            for (int c = 0; c + 1 < cols; c++)
            {
                faces.Add(new[] { T(r, c), T(r, c + 1), T(r + 1, c + 1) });
                faces.Add(new[] { T(r, c), T(r + 1, c + 1), T(r + 1, c) });
                faces.Add(new[] { B(r + 1, c + 1), B(r, c + 1), B(r, c) });
                faces.Add(new[] { B(r + 1, c), B(r + 1, c + 1), B(r, c) });
            }
        }

        // Paredes: bordas do topo no mesmo giro dos triangulos de cima (p -> q), parede q, p, p', q'.
        var boundary = new List<(int r0, int c0, int r1, int c1)>();
        for (int c = 0; c + 1 < cols; c++) boundary.Add((0, c, 0, c + 1));
        for (int r = 0; r + 1 < rows; r++) boundary.Add((r, cols - 1, r + 1, cols - 1));
        for (int c = cols - 1; c > 0; c--) boundary.Add((rows - 1, c, rows - 1, c - 1));
        for (int r = rows - 1; r > 0; r--) boundary.Add((r, 0, r - 1, 0));
        foreach ((int r0, int c0, int r1, int c1) in boundary)
        {
            int p = T(r0, c0), q = T(r1, c1), pb = B(r0, c0), qb = B(r1, c1);
            faces.Add(new[] { q, p, pb });
            faces.Add(new[] { q, pb, qb });
        }

        // Topo virado para cima; se nao, inverte tudo.
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
            mode = $"solido ({rows}x{cols} pontos)";
            return solid;
        }
        catch (System.Exception ex)
        {
            mesh.TransformBy(back);
            mode = $"malha (a conversao em solido falhou: {ex.Message})";
            PluginLog.Aviso("MFTRAVESSIA", "Conversao em solido falhou, ficou a malha: " + ex.Message);
            return mesh;
        }
    }

    // ------------------------------------------------------------------
    // XData, Property Set, layers
    // ------------------------------------------------------------------

    private static AcadDb.ResultBuffer PartXData(string id, string part) => new(
        new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, RegApp),
        new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, Marker),
        new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, id),
        new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, part));

    private static AcadDb.ResultBuffer PlatoXData(Stored d)
    {
        TravessiaParams p = d.Params;
        string S(string v) => string.IsNullOrEmpty(v) ? "-" : v;
        return new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, RegApp),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, Marker),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, d.Id),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, PartPlato),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataInteger16, (short)p.Tipo),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataReal, p.Altura),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataReal, p.Plataforma),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataReal, p.Rampa),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataInteger16, (short)(p.Tampa ? 1 : 0)),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataReal, p.TampaEspessura),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataReal, d.Origin.X),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataReal, d.Origin.Y),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, S(d.CorridorA)),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, S(d.BaselineA)),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, S(d.SubassemblyA)),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataInteger16, (short)(d.LeftA ? 1 : 0)),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, S(d.LabelA)),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, S(d.CorridorB)),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, S(d.BaselineB)),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, S(d.SubassemblyB)),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataInteger16, (short)(d.LeftB ? 1 : 0)),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, S(d.LabelB)),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, S(d.UnionId)),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataInteger16, (short)d.OuterSign),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, S(p.Superficie)));
    }

    /// <summary>Le os dados da travessia gravados no solido do plato (null se nao for um).</summary>
    public static Stored? ReadStored(AcadDb.DBObject obj)
    {
        AcadDb.ResultBuffer? rb = obj.GetXDataForApplication(RegApp);
        if (rb is null)
            return null;
        AcadDb.TypedValue[] v = rb.AsArray();
        rb.Dispose();
        if (v.Length < 22 || (v[1].Value as string) != Marker || (v[3].Value as string) != PartPlato)
            return null;
        string S(int i) => v[i].Value as string is string s && s != "-" ? s : "";
        double D(int i) => Convert.ToDouble(v[i].Value, CultureInfo.InvariantCulture);
        bool B(int i) => Convert.ToInt32(v[i].Value, CultureInfo.InvariantCulture) != 0;
        return new Stored
        {
            Id = S(2),
            Params = new TravessiaParams
            {
                Tipo = (TravessiaTipo)Convert.ToInt32(v[4].Value, CultureInfo.InvariantCulture),
                Altura = D(5),
                Plataforma = D(6),
                Rampa = D(7),
                Tampa = B(8),
                TampaEspessura = D(9),
                Superficie = v.Length >= 25 ? S(24) : ""
            },
            Origin = new Point3d(D(10), D(11), 0),
            CorridorA = S(12),
            BaselineA = S(13),
            SubassemblyA = S(14),
            LeftA = B(15),
            LabelA = S(16),
            CorridorB = S(17),
            BaselineB = S(18),
            SubassemblyB = S(19),
            LeftB = B(20),
            LabelB = S(21),
            UnionId = v.Length >= 24 ? S(22) : "",
            OuterSign = v.Length >= 24 ? Convert.ToInt32(v[23].Value, CultureInfo.InvariantCulture) : 0
        };
    }

    /// <summary>Id da travessia e parte (null se o objeto nao e de travessia).</summary>
    public static (string Id, string Part)? PartOf(AcadDb.DBObject obj)
    {
        AcadDb.ResultBuffer? rb = obj.GetXDataForApplication(RegApp);
        if (rb is null)
            return null;
        AcadDb.TypedValue[] v = rb.AsArray();
        rb.Dispose();
        if (v.Length < 4 || (v[1].Value as string) != Marker)
            return null;
        return ((v[2].Value as string) ?? "", (v[3].Value as string) ?? "");
    }

    /// <summary>Todos os solidos de plato do desenho com os dados guardados.</summary>
    public static List<(AcadDb.ObjectId Id, Stored Data)> FindAll(AcadDb.Database db)
    {
        var list = new List<(AcadDb.ObjectId, Stored)>();
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        var bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        var ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (id.IsErased)
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is AcadDb.Entity e && ReadStored(e) is Stored s)
                list.Add((id, s));
        }
        tr.Commit();
        return list;
    }

    private static int Erase(AcadDb.Transaction tr, AcadDb.Database db, string id)
    {
        int n = 0;
        var bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        var ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId oid in ms)
        {
            if (oid.IsErased)
                continue;
            if (tr.GetObject(oid, AcadDb.OpenMode.ForRead) is not AcadDb.Entity e)
                continue;
            (string Id, string Part)? part = PartOf(e);
            if (part is not null && part.Value.Id == id)
            {
                try
                {
                    e.UpgradeOpen();
                    e.Erase();
                    n++;
                }
                catch (System.Exception ex)
                {
                    PluginLog.Aviso("MFTRAVESSIA", $"Nao deu para apagar uma parte antiga da travessia {id} (layer trancada?): {ex.Message}");
                }
            }
        }
        return n;
    }

    private static void EnsureLayer(AcadDb.Transaction tr, AcadDb.Database db, string name, short color, string description)
    {
        var lt = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        if (lt.Has(name))
            return;
        lt.UpgradeOpen();
        var ltr = new AcadDb.LayerTableRecord
        {
            Name = name,
            Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, color),
            Description = description
        };
        lt.Add(ltr);
        tr.AddNewlyCreatedDBObject(ltr, true);
    }

    private static void EnsureRegApp(AcadDb.Transaction tr, AcadDb.Database db)
    {
        var rat = (AcadDb.RegAppTable)tr.GetObject(db.RegAppTableId, AcadDb.OpenMode.ForRead);
        if (rat.Has(RegApp))
            return;
        rat.UpgradeOpen();
        var r = new AcadDb.RegAppTableRecord { Name = RegApp };
        rat.Add(r);
        tr.AddNewlyCreatedDBObject(r, true);
    }

    private static Dictionary<string, object> PropertyValues(Stored d, TravessiaGeometry g, string what) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ID"] = d.Id,
        ["Versao"] = C3DVersions.MfRebaixo,
        ["Objeto"] = what,
        ["Tipo"] = d.Params.Tipo == TravessiaTipo.Elevada ? "Travessia elevada" : "Travessia semi-elevada",
        ["Altura_Plato"] = Math.Round((g.HeightA + g.HeightB) / 2.0, 3),
        ["Largura_Plataforma"] = d.Params.Plataforma,
        ["Comprimento_Rampa"] = d.Params.Rampa,
        ["Extensao_Travessia"] = Math.Round(g.WidthAtAxis, 3),
        ["Elevacao_Topo_Centro"] = Math.Round(g.TopElevationAtCenter, 3),
        ["Tampa_Sarjeta"] = d.Params.Tampa ? $"Concreto, {N(d.Params.TampaEspessura)} m" : "Sem tampa",
        ["Rebaixo_A"] = d.LabelA,
        ["Rebaixo_B"] = d.LabelB
    };

    private static readonly (string Name, AecPropData.DataType Type)[] PsFields =
    {
        ("ID", AecPropData.DataType.Text), ("Versao", AecPropData.DataType.Text), ("Objeto", AecPropData.DataType.Text),
        ("Tipo", AecPropData.DataType.Text), ("Altura_Plato", AecPropData.DataType.Real), ("Largura_Plataforma", AecPropData.DataType.Real),
        ("Comprimento_Rampa", AecPropData.DataType.Real), ("Extensao_Travessia", AecPropData.DataType.Real),
        ("Elevacao_Topo_Centro", AecPropData.DataType.Real), ("Tampa_Sarjeta", AecPropData.DataType.Text),
        ("Rebaixo_A", AecPropData.DataType.Text), ("Rebaixo_B", AecPropData.DataType.Text)
    };

    /// <summary>Property Set C3D_TRAVESSIA (para a federacao BIM). Falha nao cancela o desenho.</summary>
    private static bool TryPropertySet(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.Solid3d solid, Dictionary<string, object> values, out string message)
    {
        message = "";
        try
        {
            var dictionary = new AecPropDb.DictionaryPropertySetDefinitions(db);
            AecPropDb.PropertySetDefinition definition;
            AcadDb.ObjectId definitionId;
            if (dictionary.Has(PropertySetName, tr))
            {
                definitionId = dictionary.GetAt(PropertySetName);
                definition = (AecPropDb.PropertySetDefinition)tr.GetObject(definitionId, AcadDb.OpenMode.ForWrite, false);
                AddFields(db, definition);
            }
            else
            {
                definition = new AecPropDb.PropertySetDefinition();
                definition.SetToStandard(db);
                definition.SubSetDatabaseDefaults(db);
                definition.Description = "Travessia elevada/semi-elevada entre rebaixos (Civil3D2026Plugin, MFTRAVESSIA).";
                definition.SetAppliesToFilter(new StringCollection { RXObject.GetClass(typeof(AcadDb.Solid3d)).Name }, false);
                AddFields(db, definition);
                dictionary.AddNewRecord(PropertySetName, definition);
                tr.AddNewlyCreatedDBObject(definition, true);
                definitionId = definition.ObjectId;
            }

            AcadDb.ObjectId psId;
            try
            {
                psId = AecPropDb.PropertyDataServices.GetPropertySet(solid, definitionId);
            }
            catch
            {
                psId = AcadDb.ObjectId.Null;
            }
            if (psId.IsNull)
            {
                AecPropDb.PropertyDataServices.AddPropertySet(solid, definitionId);
                psId = AecPropDb.PropertyDataServices.GetPropertySet(solid, definitionId);
            }
            if (tr.GetObject(psId, AcadDb.OpenMode.ForWrite, false) is not AecPropDb.PropertySet ps)
            {
                message = "Property Set nao abriu.";
                return false;
            }
            foreach ((string name, object value) in values)
                ps.SetAt(ps.PropertyNameToId(name), value);
            return true;
        }
        catch (System.Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static void AddFields(AcadDb.Database db, AecPropDb.PropertySetDefinition definition)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AecPropDb.PropertyDefinition pd in definition.Definitions)
            existing.Add(pd.Name);
        foreach ((string name, AecPropData.DataType type) in PsFields)
        {
            if (existing.Contains(name))
                continue;
            var pd = new AecPropDb.PropertyDefinition();
            pd.SetToStandard(db);
            pd.SubSetDatabaseDefaults(db);
            pd.Name = name;
            pd.DataType = type;
            pd.IsVisible = true;
            definition.Definitions.Add(pd);
        }
    }
}
