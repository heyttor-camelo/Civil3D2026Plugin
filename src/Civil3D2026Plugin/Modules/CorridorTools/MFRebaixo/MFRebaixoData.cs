using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;

using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>Um rebaixo lançado pelo MFREBAIXO (par de Corridor Transition Sets).</summary>
internal sealed class RebaixoInfo
{
    public AcadDb.ObjectId CorridorId { get; init; }
    public string CorridorName { get; init; } = "";
    public string BaselineKey { get; init; } = "";
    public string BaselineName { get; init; } = "";
    public string RegionName { get; init; } = "";
    public string SetName { get; init; } = "";
    public string? HighlightSetName { get; init; }
    public string SubassemblyName { get; init; } = "";
    public string Comment { get; init; } = "";
    public bool Left { get; init; }

    public double S0 { get; init; }
    public double S1 { get; init; }
    public double S2 { get; init; }
    public double S3 { get; init; }

    /// <summary>REB (profundidade) ou AMF_LIVRE (altura livre).</summary>
    public string Parameter { get; init; } = "";
    public double ValueStart { get; init; }
    public double ValueIn { get; init; }
    public double ValueOut { get; init; }
    public double ValueEnd { get; init; }

    public bool ByDepth => Parameter.Equals(MFRebaixoCommands.DepthParameterName, StringComparison.OrdinalIgnoreCase);

    // Lidos do corredor (seção mais perto do meio do trecho rebaixado e seção antes de S0).
    public double? NormalHeight { get; set; }
    public double? ExposedAtMiddle { get; set; }
    public double? RampSlope { get; set; }
    public double? RampOk { get; set; }
    public double? FreeWidth { get; set; }
    public bool HasHighlightParameter { get; set; }

    public Point3d P0 { get; set; }
    public Point3d P1 { get; set; }
    public Point3d P2 { get; set; }
    public Point3d P3 { get; set; }

    /// <summary>Eixo da faixa do rebaixo de S0 a S3 (meio entre borda da pista e fundo do MF); sem o meio-fio lido, a baseline.</summary>
    public List<Point3d> Path { get; } = new();

    /// <summary>Faixa real do rebaixo no desenho: em cada secao de S0 a S3, borda da pista e fundo do meio-fio.</summary>
    public List<(double Station, Point3d Inner, Point3d Outer)> Band { get; } = new();

    /// <summary>Parte da faixa entre duas estacas (rampa de entrada, trecho rebaixado, rampa de saida).</summary>
    public List<(double Station, Point3d Inner, Point3d Outer)> BandBetween(double from, double to) =>
        Band.Where(e => e.Station >= from - 0.0005 && e.Station <= to + 0.0005).ToList();

    public double EntryLength => S1 - S0;
    public double LoweredLength => S2 - S1;
    public double ExitLength => S3 - S2;
    public double TotalLength => S3 - S0;

    /// <summary>Altura livre do MF no trecho rebaixado.</summary>
    public double? FinalHeight =>
        ByDepth
            // O exposto lido no proprio trecho rebaixado e o valor real; a altura normal varia ao longo
            // do meio-fio, entao "normal antes do rebaixo - REB" dava outro numero (11,2 em vez de 12 cm).
            ? ExposedAtMiddle ?? (NormalHeight.HasValue ? Math.Max(0, NormalHeight.Value - ValueIn) : null)
            : ValueIn;

    public string SideLabel => Left ? "Esquerdo" : "Direito";

    /// <summary>Codigo sequencial do rebaixo no desenho (R01, R02...).</summary>
    public string Code { get; set; } = "";

    /// <summary>Nome dado pelo usuario (guardado no comentario do Transition Set: "REBAIXO MF | nome").</summary>
    public string CustomName =>
        Comment.Contains('|') ? Comment[(Comment.IndexOf('|') + 1)..].Trim() : "";

    /// <summary>Como o rebaixo aparece no painel: "R03" ou "R03 Garagem 120".</summary>
    public string Label => CustomName.Length > 0 ? $"{Code} {CustomName}" : Code;

    /// <summary>Trecho rebaixado em estacas da baseline.</summary>
    public string Stations => $"{Num(S1, "0.00")} - {Num(S2, "0.00")}";

    public static string Num(double v, string fmt = "0.000") => v.ToString(fmt, CultureInfo.InvariantCulture);
}

/// <summary>Valores do MF numa Corridor Section (aba Seções).</summary>
internal sealed class RebaixoSection
{
    public double Station { get; init; }
    public string Mark { get; init; } = "";
    public double? Reb { get; init; }
    public double? Highlight { get; init; }
    public double? Exposed { get; init; }
    public double? FreeHeight { get; init; }
    public double? RampOk { get; init; }
    public double? FreeWidth { get; init; }
}

internal sealed class RebaixoWarning
{
    public RebaixoInfo? Item { get; init; }
    public bool Serious { get; init; }
    public string Text { get; init; } = "";
    public string Reference { get; init; } = "";
}

internal sealed class CorridorSummary
{
    public AcadDb.ObjectId Id { get; init; }
    public string Name { get; init; } = "";
    public int Baselines { get; init; }
    public int Regions { get; init; }
    public int MfRegions { get; init; }
    public int Rebaixos { get; set; }
}

/// <summary>Link calculado do corredor, em coordenadas do mundo.</summary>
internal sealed class ModelLink
{
    public string[] Codes { get; init; } = Array.Empty<string>();
    public Point3d[] Points { get; init; } = Array.Empty<Point3d>();

    /// <summary>Os mesmos pontos em estaca (X), offset (Y) na baseline e elevacao absoluta (Z) (perfil, secao e 3D).</summary>
    public Point3d[] Soe { get; init; } = Array.Empty<Point3d>();
}

/// <summary>Shape calculada do corredor (contorno fechado da peca na secao): base dos solidos do 3D e do preenchimento da secao.</summary>
internal sealed class ModelShape
{
    public string[] Codes { get; init; } = Array.Empty<string>();

    /// <summary>Contorno em coordenadas do desenho (sem repetir o primeiro ponto).</summary>
    public Point3d[] Points { get; init; } = Array.Empty<Point3d>();

    /// <summary>O mesmo contorno em estaca (X), offset (Y) e elevacao absoluta (Z).</summary>
    public Point3d[] Soe { get; init; } = Array.Empty<Point3d>();
}

internal sealed class ModelSection
{
    public double Station { get; init; }
    public List<(long Key, List<ModelLink> Links)> Subassemblies { get; } = new();

    /// <summary>Origem de cada subassembly na secao, em estaca (X), offset (Y) e elevacao absoluta (Z).</summary>
    public Dictionary<long, Point3d> Origins { get; } = new();

    /// <summary>Codes das shapes de cada subassembly na secao (ex. MF_T30: tipo do meio-fio do MFSARJETA).</summary>
    public Dictionary<long, string[]> ShapeCodes { get; } = new();

    /// <summary>Shapes (contornos fechados) de cada subassembly na secao, na ordem da peca.</summary>
    public Dictionary<long, List<ModelShape>> Shapes { get; } = new();
}

/// <summary>Trecho do corredor em volta de um rebaixo (planta e 3D).</summary>
internal sealed class RebaixoModel
{
    public List<ModelSection> Sections { get; } = new();
    public List<Point3d> Baseline { get; } = new();
    public Point3d[] Marks { get; init; } = Array.Empty<Point3d>();
    public string[] MarkNames { get; init; } = Array.Empty<string>();

    /// <summary>Nome de cada subassembly do trecho, pela chave usada em ModelSection.Subassemblies.</summary>
    public Dictionary<long, string> Names { get; } = new();

    /// <summary>Corredor e baseline das secoes (para achar os rebaixos que caem neste modelo).</summary>
    public AcadDb.ObjectId CorridorId { get; init; }
    public string BaselineKey { get; init; } = "";
}

/// <summary>Leitura e remoção dos rebaixos do desenho (para o painel MFREBAIXOPAINEL).</summary>
internal static class MFRebaixoData
{
    public const string PairPrefix = "MF_REBAIXO_";
    public const string HighlightSuffix = "_DESTAQUE";

    private const string ExposedOutput = "EXPOSTO";
    private const string RampOkOutput = "RAMPA_OK";
    private const string FreeWidthOutput = "FAIXA_LIVRE";

    // ------------------------------------------------------------------
    // Corredores
    // ------------------------------------------------------------------

    public static List<AcadDb.ObjectId> CorridorIds(AcadDb.Database db, AcadDb.Transaction tr)
    {
        List<AcadDb.ObjectId> ids = new();
        var bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        var ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        RXClass cls = RXObject.GetClass(typeof(CivilCorridor));
        foreach (AcadDb.ObjectId id in ms)
        {
            if (!id.IsErased && id.ObjectClass.IsDerivedFrom(cls))
                ids.Add(id);
        }

        return ids;
    }

    public static List<CorridorSummary> Corridors(AcadDb.Database db)
    {
        List<CorridorSummary> list = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (AcadDb.ObjectId id in CorridorIds(db, tr))
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not CivilCorridor corridor)
                continue;

            int baselines = 0, regions = 0, mf = 0;
            foreach (CivilDb.Baseline b in corridor.Baselines)
            {
                baselines++;
                foreach (CivilDb.BaselineRegion r in b.BaselineRegions)
                {
                    regions++;
                    if (RegionHasMf(tr, r))
                        mf++;
                }
            }

            list.Add(new CorridorSummary { Id = id, Name = corridor.Name, Baselines = baselines, Regions = regions, MfRegions = mf });
        }

        tr.Commit();
        return list;
    }

    private static bool RegionHasMf(AcadDb.Transaction tr, CivilDb.BaselineRegion region)
    {
        try
        {
            if (tr.GetObject(region.AssemblyId, AcadDb.OpenMode.ForRead) is not CivilDb.Assembly assembly)
                return false;
            foreach (CivilDb.AssemblyGroup group in assembly.Groups)
            {
                foreach (AcadDb.ObjectId id in group.GetSubassemblyIds())
                {
                    if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is CivilDb.Subassembly sa && MFRebaixoCommands.IsMfSubassemblyPublic(sa))
                        return true;
                }
            }
        }
        catch
        {
            // Region sem assembly.
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Rebaixos
    // ------------------------------------------------------------------

    public static List<RebaixoInfo> ReadAll(AcadDb.Database db)
    {
        List<RebaixoInfo> list = new();
        RemovedSets.Clear();
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (AcadDb.ObjectId id in CorridorIds(db, tr))
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not CivilCorridor corridor)
                continue;

            foreach (CivilDb.Baseline baseline in corridor.Baselines)
            {
                List<CivilDb.CorridorTransitionSet> sets;
                try
                {
                    sets = baseline.getTransitions();
                }
                catch
                {
                    continue;
                }

                foreach (CivilDb.CorridorTransitionSet set in sets)
                {
                    string name = set.Name ?? "";
                    if (!name.StartsWith(PairPrefix, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(HighlightSuffix, StringComparison.OrdinalIgnoreCase) ||
                        set.TransitionCount < 3)
                    {
                        continue;
                    }

                    if (IsNeutral(set))
                    {
                        RemovedSets.Add($"{corridor.Name} / {baseline.Name}: {name}");
                        continue;
                    }

                    RebaixoInfo? info = Build(tr, corridor, id, baseline, set, sets);
                    if (info is not null)
                        list.Add(info);
                }
            }
        }

        tr.Commit();
        List<RebaixoInfo> ordered = list.OrderBy(r => r.CorridorName).ThenBy(r => r.BaselineName).ThenBy(r => r.S1).ToList();
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].Code = $"R{i + 1:00}";
        return ordered;
    }

    private static RebaixoInfo? Build(AcadDb.Transaction tr, CivilCorridor corridor, AcadDb.ObjectId corridorId,
        CivilDb.Baseline baseline, CivilDb.CorridorTransitionSet set, List<CivilDb.CorridorTransitionSet> all)
    {
        try
        {
            var t0 = set.GetTransitionAt(0);
            var t1 = set.GetTransitionAt(1);
            var t2 = set.GetTransitionAt(2);

            double s0 = t0.StartStation, s1 = t0.EndStation, s2 = t1.EndStation, s3 = t2.EndStation;
            string highlightPrefix = set.Name + HighlightSuffix;
            string? highlight = all.FirstOrDefault(x => (x.Name ?? "").StartsWith(highlightPrefix, StringComparison.OrdinalIgnoreCase))?.Name;

            CivilDb.BaselineRegion? region = RegionAt(baseline, (s1 + s2) / 2.0);

            var info = new RebaixoInfo
            {
                CorridorId = corridorId,
                CorridorName = corridor.Name,
                BaselineKey = MFRebaixoCommands.GetBaselineKey(baseline),
                BaselineName = baseline.Name,
                RegionName = region?.Name ?? "",
                SetName = set.Name ?? "",
                HighlightSetName = highlight,
                SubassemblyName = set.SubassemblyName ?? "",
                Comment = set.Comment ?? "",
                Left = (set.Name ?? "").Contains("_ESQ_", StringComparison.OrdinalIgnoreCase),
                S0 = s0,
                S1 = s1,
                S2 = s2,
                S3 = s3,
                Parameter = t0.ParameterName ?? "",
                ValueStart = Convert.ToDouble((object)t0.StartValue, CultureInfo.InvariantCulture),
                ValueIn = Convert.ToDouble((object)t0.EndValue, CultureInfo.InvariantCulture),
                ValueOut = Convert.ToDouble((object)t1.EndValue, CultureInfo.InvariantCulture),
                ValueEnd = Convert.ToDouble((object)t2.EndValue, CultureInfo.InvariantCulture)
            };

            info.P0 = Xyz(baseline, s0, 0, 0);
            info.P1 = Xyz(baseline, s1, 0, 0);
            info.P2 = Xyz(baseline, s2, 0, 0);
            info.P3 = Xyz(baseline, s3, 0, 0);
            double step = Math.Max(0.25, (s3 - s0) / 40.0);
            for (double st = s0; st < s3 + (step / 2); st += step)
            {
                Point3d p = Xyz(baseline, Math.Min(st, s3), 0, 0);
                if (p != Point3d.Origin)
                    info.Path.Add(p);
            }

            if (region is not null)
            {
                ReadCorridorValues(tr, region, info);
                ReadBand(tr, baseline, region, info);
            }

            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Le a faixa real do rebaixo (sarjeta + meio-fio) nas secoes de S0 a S3. Com ela, o eixo do
    /// rebaixo (Path) e os marcos S0..S3 passam para cima do meio-fio, em vez da baseline.
    /// </summary>
    private static void ReadBand(AcadDb.Transaction tr, CivilDb.Baseline baseline, CivilDb.BaselineRegion region, RebaixoInfo info)
    {
        try
        {
            List<(double Station, Point3d Inner, Point3d Outer)> band = MFRebaixoHatch.Edges(tr, baseline, info.SubassemblyName, info.S0, info.S3, false, null, info.Left);
            if (band.Count < 2)
                return;
            info.Band.AddRange(band);
            info.Path.Clear();
            foreach ((_, Point3d a, Point3d b) in band)
                info.Path.Add(new Point3d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, 0));

            Point3d At(double st)
            {
                var e = band.OrderBy(x => Math.Abs(x.Station - st)).First();
                return new Point3d((e.Inner.X + e.Outer.X) / 2.0, (e.Inner.Y + e.Outer.Y) / 2.0, 0);
            }

            info.P0 = At(info.S0);
            info.P1 = At(info.S1);
            info.P2 = At(info.S2);
            info.P3 = At(info.S3);
        }
        catch
        {
            // Sem a faixa: o rebaixo continua representado sobre a baseline.
        }
    }

    /// <summary>
    /// Meios-fios dos corredores (planta geral): para cada baseline, region e lado com MF/MFSARJETA,
    /// a faixa borda da pista -> fundo do MF em todas as secoes.
    /// </summary>
    public static List<(AcadDb.ObjectId Corridor, List<(double Station, Point3d Inner, Point3d Outer)> Band)> Curbs(AcadDb.Database db)
    {
        List<(AcadDb.ObjectId, List<(double Station, Point3d Inner, Point3d Outer)>)> list = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (AcadDb.ObjectId id in CorridorIds(db, tr))
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not CivilCorridor corridor)
                continue;
            foreach (CivilDb.Baseline baseline in corridor.Baselines)
            {
                foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
                {
                    try
                    {
                        double a = Math.Min(region.StartStation, region.EndStation), z = Math.Max(region.StartStation, region.EndStation);
                        foreach (var band in MFRebaixoHatch.EdgesBySubassembly(tr, baseline, a, z, false, MFRebaixoCommands.IsMfSubassemblyPublic).Values)
                        {
                            if (band.Count > 1)
                                list.Add((id, band));
                        }
                    }
                    catch
                    {
                        // Region sem assembly ou sem meio-fio.
                    }
                }
            }
        }
        tr.Commit();
        return list;
    }

    private static void ReadCorridorValues(AcadDb.Transaction tr, CivilDb.BaselineRegion region, RebaixoInfo info)
    {
        Dictionary<AcadDb.ObjectId, string> names = new();
        double mid = (info.S1 + info.S2) / 2.0;
        (double Station, Dictionary<string, double> Values)? best = null, before = null;

        foreach ((double station, Dictionary<string, double> values) in MfValues(tr, region, info.SubassemblyName, names))
        {
            if (best is null || Math.Abs(station - mid) < Math.Abs(best.Value.Station - mid))
                best = (station, values);
            if (station < info.S0 - 0.0005 && (before is null || station > before.Value.Station))
                before = (station, values);
        }

        if (best is { } b)
        {
            info.ExposedAtMiddle = Get(b.Values, ExposedOutput) ?? Get(b.Values, MFRebaixoCommands.EffectiveOutputParameterName);
            info.RampSlope = Get(b.Values, MFRebaixoCommands.RampSlopeParameterName);
            info.RampOk = Get(b.Values, RampOkOutput);
            info.FreeWidth = Get(b.Values, FreeWidthOutput);
            info.HasHighlightParameter = b.Values.ContainsKey(MFRebaixoCommands.HighlightParameterName);
        }

        if (before is { } n)
            info.NormalHeight = Get(n.Values, ExposedOutput) ?? Get(n.Values, MFRebaixoCommands.EffectiveOutputParameterName);
        else if (info.ByDepth && info.ExposedAtMiddle.HasValue)
            info.NormalHeight = info.ExposedAtMiddle + info.ValueIn;
        else if (!info.ByDepth)
            info.NormalHeight = info.ValueStart;
    }

    private static double? Get(Dictionary<string, double> values, string key) =>
        values.TryGetValue(key, out double v) ? v : null;

    /// <summary>Parâmetros e saídas do MF (subassembly do rebaixo) em cada seção calculada da Region.</summary>
    private static IEnumerable<(double Station, Dictionary<string, double> Values)> MfValues(
        AcadDb.Transaction tr, CivilDb.BaselineRegion region, string subassemblyName, Dictionary<AcadDb.ObjectId, string> names)
    {
        foreach (CivilDb.AppliedAssembly aa in region.AppliedAssemblies)
        {
            double? station = StationOf(aa);
            if (station is null)
                continue;

            foreach (CivilDb.AppliedSubassembly asa in aa.GetAppliedSubassemblies())
            {
                if (!names.TryGetValue(asa.SubassemblyId, out string? name))
                {
                    name = "";
                    try
                    {
                        if (tr.GetObject(asa.SubassemblyId, AcadDb.OpenMode.ForRead) is CivilDb.Subassembly sa)
                            name = sa.Name;
                    }
                    catch
                    {
                        // Subassembly apagada.
                    }

                    names[asa.SubassemblyId] = name;
                }

                if (!name.Equals(subassemblyName, StringComparison.OrdinalIgnoreCase))
                    continue;

                Dictionary<string, double> values = new(StringComparer.OrdinalIgnoreCase);
                foreach (CivilDb.IAppliedSubassemblyParam prm in asa.Parameters)
                {
                    if (TryDouble(prm.ValueAsObject, out double v))
                        values[prm.KeyName] = v;
                }

                yield return (station.Value, values);
                break;
            }
        }
    }

    private static bool TryDouble(object? raw, out double value)
    {
        value = 0;
        if (raw is null)
            return false;
        if (raw is double d)
        {
            value = d;
            return true;
        }

        try
        {
            value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            return double.IsFinite(value);
        }
        catch
        {
            return false;
        }
    }

    private static double? StationOf(CivilDb.AppliedAssembly aa)
    {
        try
        {
            foreach (CivilDb.CalculatedPoint p in aa.Points)
                return p.StationOffsetElevationToBaseline.X;
            foreach (CivilDb.AppliedSubassembly asa in aa.GetAppliedSubassemblies())
                return asa.OriginStationOffsetElevationToBaseline.X;
        }
        catch
        {
            // Seção sem pontos.
        }

        return null;
    }

    private static CivilDb.BaselineRegion? RegionAt(CivilDb.Baseline baseline, double station)
    {
        foreach (CivilDb.BaselineRegion r in baseline.BaselineRegions)
        {
            if (station >= Math.Min(r.StartStation, r.EndStation) - 1e-6 && station <= Math.Max(r.StartStation, r.EndStation) + 1e-6)
                return r;
        }

        return null;
    }

    /// <summary>
    /// Estaca/offset/elevacao da baseline para X, Y e elevacao ABSOLUTA do desenho. A elevacao dos
    /// pontos do corredor (StationOffsetElevationToBaseline.Z) e relativa ao perfil da baseline; o Z
    /// devolvido aqui ja soma o perfil (igual ao CalculatedPoint.XYZ). Antes o Z era trocado pela
    /// elevacao relativa e o painel mostrava -0,1 / 0,2 num projeto na elevacao 949 (teste de 11/11/2026).
    /// </summary>
    private static Point3d Xyz(CivilDb.Baseline baseline, double station, double offset, double elevation)
    {
        try
        {
            return baseline.StationOffsetElevationToXYZ(new Point3d(station, offset, elevation));
        }
        catch
        {
            return Point3d.Origin;
        }
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

    /// <summary>Baselines dos corredores amostradas (planta geral).</summary>
    public static List<(AcadDb.ObjectId Corridor, string Name, List<Point3d> Points)> Overview(AcadDb.Database db)
    {
        List<(AcadDb.ObjectId, string, List<Point3d>)> list = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (AcadDb.ObjectId id in CorridorIds(db, tr))
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not CivilCorridor corridor)
                continue;
            foreach (CivilDb.Baseline b in corridor.Baselines)
            {
                List<Point3d> pts = new();
                try
                {
                    double a = Math.Min(b.StartStation, b.EndStation), z = Math.Max(b.StartStation, b.EndStation);
                    double step = Math.Max(1.0, (z - a) / 400.0);
                    for (double st = a; st < z + (step / 2); st += step)
                    {
                        Point3d p = Xyz(b, Math.Min(st, z), 0, 0);
                        if (p != Point3d.Origin)
                            pts.Add(p);
                    }
                }
                catch
                {
                    // Baseline sem geometria.
                }

                if (pts.Count > 1)
                    list.Add((id, $"{corridor.Name} / {b.Name}", pts));
            }
        }

        tr.Commit();
        return list;
    }

    // ------------------------------------------------------------------
    // Seções (aba Seções) e modelo (planta / 3D)
    // ------------------------------------------------------------------

    public static List<RebaixoSection> Sections(AcadDb.Database db, RebaixoInfo r, double margin)
    {
        List<RebaixoSection> list = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(r.CorridorId, AcadDb.OpenMode.ForRead) is CivilCorridor corridor && FindBaseline(corridor, r.BaselineKey) is CivilDb.Baseline baseline)
        {
            Dictionary<AcadDb.ObjectId, string> names = new();
            foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
            {
                foreach ((double st, Dictionary<string, double> v) in MfValues(tr, region, r.SubassemblyName, names))
                {
                    if (st < r.S0 - margin || st > r.S3 + margin)
                        continue;
                    list.Add(new RebaixoSection
                    {
                        Station = st,
                        Mark = MarkOf(r, st),
                        Reb = Get(v, MFRebaixoCommands.DepthParameterName),
                        Highlight = Get(v, MFRebaixoCommands.HighlightParameterName),
                        Exposed = Get(v, ExposedOutput) ?? Get(v, MFRebaixoCommands.EffectiveOutputParameterName),
                        FreeHeight = Get(v, MFRebaixoCommands.ParameterName),
                        RampOk = Get(v, RampOkOutput),
                        FreeWidth = Get(v, FreeWidthOutput)
                    });
                }
            }
        }

        tr.Commit();
        return list.GroupBy(s => Math.Round(s.Station, 4)).Select(g => g.First()).OrderBy(s => s.Station).ToList();
    }

    private static string MarkOf(RebaixoInfo r, double st)
    {
        (double S, string N)[] marks =
        {
            (r.S0, "S0 inicio da rampa"), (r.S1, "S1 inicio do rebaixo"), (r.S2, "S2 fim do rebaixo"), (r.S3, "S3 fim da rampa"),
            (r.S0 - 0.001, "S0 - 1 mm"), (r.S0 + 0.001, "S0 + 1 mm"), (r.S3 - 0.001, "S3 - 1 mm"), (r.S3 + 0.001, "S3 + 1 mm")
        };
        foreach ((double s, string n) in marks)
        {
            if (Math.Abs(st - s) < 0.0002)
                return n;
        }

        if (st < r.S0) return "antes";
        if (st < r.S1) return "rampa de entrada";
        if (st <= r.S2) return "rebaixado";
        if (st <= r.S3) return "rampa de saida";
        return "depois";
    }

    /// <summary>Secoes calculadas de uma Region (links com codes e pontos em XY do desenho).</summary>
    private static List<ModelSection> ReadSections(CivilDb.Baseline baseline, CivilDb.BaselineRegion region, double from, double to, bool planOnly)
    {
        List<ModelSection> list = new();
        foreach (CivilDb.AppliedAssembly aa in region.AppliedAssemblies)
        {
            double? station = StationOf(aa);
            if (station is null || station < from || station > to)
                continue;
            var section = new ModelSection { Station = station.Value };
            foreach (CivilDb.AppliedSubassembly asa in aa.GetAppliedSubassemblies())
            {
                List<ModelLink> links = new();
                foreach (CivilDb.CalculatedLink link in asa.Links)
                {
                    List<string> codes = new();
                    foreach (string code in link.CorridorCodes)
                    {
                        if (!string.IsNullOrWhiteSpace(code))
                            codes.Add(code);
                    }
                    string[] codeArray = codes.ToArray();
                    // Planta geral: so guarda os pontos do que aparece em planta (a ordem dos links e mantida).
                    List<Point3d> pts = new();
                    List<Point3d> soes = new();
                    if (!planOnly || RebaixoStyle.Of(codeArray).Plan)
                    {
                        foreach (CivilDb.CalculatedPoint cp in link.CalculatedPoints)
                        {
                            Point3d soe = cp.StationOffsetElevationToBaseline;
                            Point3d w = Xyz(baseline, soe.X, soe.Y, soe.Z);
                            if (w != Point3d.Origin)
                            {
                                pts.Add(w);
                                if (!planOnly)
                                    soes.Add(new Point3d(soe.X, soe.Y, w.Z));
                            }
                        }
                    }
                    links.Add(new ModelLink { Codes = codeArray, Points = pts.ToArray(), Soe = soes.ToArray() });
                }
                section.Subassemblies.Add((asa.SubassemblyId.Handle.Value, links));
                try
                {
                    Point3d o = asa.OriginStationOffsetElevationToBaseline;
                    Point3d ow = Xyz(baseline, o.X, o.Y, o.Z);
                    if (ow != Point3d.Origin)
                        section.Origins[asa.SubassemblyId.Handle.Value] = new Point3d(o.X, o.Y, ow.Z);
                }
                catch
                {
                    // Sem origem: as vistas usam o primeiro ponto.
                }

                if (!planOnly)
                {
                    try
                    {
                        List<string> shapeCodes = new();
                        List<ModelShape> shapes = new();
                        foreach (CivilDb.CalculatedShape shape in asa.Shapes)
                        {
                            List<string> own = new();
                            foreach (string code in shape.CorridorCodes)
                            {
                                if (string.IsNullOrWhiteSpace(code))
                                    continue;
                                own.Add(code);
                                if (!shapeCodes.Contains(code))
                                    shapeCodes.Add(code);
                            }

                            // Contorno: os links da shape encadeados ponta a ponta.
                            List<List<Point3d>> segments = new();
                            foreach (CivilDb.CalculatedLink sl in shape.CalculatedLinks)
                            {
                                List<Point3d> seg = new();
                                foreach (CivilDb.CalculatedPoint cp in sl.CalculatedPoints)
                                    seg.Add(cp.StationOffsetElevationToBaseline);
                                if (seg.Count > 1)
                                    segments.Add(seg);
                            }

                            List<Point3d> loop = ChainLoop(segments);
                            if (loop.Count < 3)
                                continue;
                            Point3d[] world = loop.Select(q => Xyz(baseline, q.X, q.Y, q.Z)).ToArray();
                            if (world.Any(q => q == Point3d.Origin))
                                continue;
                            shapes.Add(new ModelShape { Codes = own.ToArray(), Points = world, Soe = loop.Select((q, i) => new Point3d(q.X, q.Y, world[i].Z)).ToArray() });
                        }

                        section.ShapeCodes[asa.SubassemblyId.Handle.Value] = shapeCodes.ToArray();
                        section.Shapes[asa.SubassemblyId.Handle.Value] = shapes;
                    }
                    catch
                    {
                        // Sem shapes: o tipo do meio-fio fica pelo nome da peca e o 3D pelas faces dos links.
                    }
                }
            }
            list.Add(section);
        }
        return list;
    }

    /// <summary>Encadeia os links de uma shape num contorno fechado (offset/elevacao), sem repetir o primeiro ponto.</summary>
    private static List<Point3d> ChainLoop(List<List<Point3d>> segments)
    {
        List<Point3d> chain = new();
        if (segments.Count == 0)
            return chain;
        static bool Same(Point3d a, Point3d b) => Math.Abs(a.Y - b.Y) < 1e-4 && Math.Abs(a.Z - b.Z) < 1e-4;
        List<List<Point3d>> rest = segments.Skip(1).ToList();
        chain.AddRange(segments[0]);
        while (rest.Count > 0)
        {
            Point3d end = chain[^1];
            int i = rest.FindIndex(x => Same(x[0], end));
            if (i >= 0)
            {
                chain.AddRange(rest[i].Skip(1));
                rest.RemoveAt(i);
                continue;
            }

            i = rest.FindIndex(x => Same(x[^1], end));
            if (i >= 0)
            {
                chain.AddRange(Enumerable.Reverse(rest[i]).Skip(1));
                rest.RemoveAt(i);
                continue;
            }

            // Primeiro link ao contrario dos outros: tenta pelo comeco da cadeia.
            Point3d start = chain[0];
            i = rest.FindIndex(x => Same(x[0], start) || Same(x[^1], start));
            if (i < 0)
                break;
            chain.Reverse();
        }

        if (chain.Count > 1 && Same(chain[0], chain[^1]))
            chain.RemoveAt(chain.Count - 1);
        // Pontos repetidos em sequencia (links de comprimento zero) saem.
        for (int k = chain.Count - 1; k > 0; k--)
        {
            if (Same(chain[k], chain[k - 1]))
                chain.RemoveAt(k);
        }

        return chain;
    }

    /// <summary>
    /// Planta de todos os corredores (planta geral do painel): as secoes de cada Region, em ordem
    /// de estaca, so com os links que aparecem em planta (pista, sarjeta, meio-fio, calcada).
    /// Acima de <paramref name="maxSections"/> secoes no total devolve lista vazia (corredor grande
    /// demais para desenhar inteiro; o painel fica so com os meios-fios).
    /// </summary>
    public static List<List<ModelSection>> PlanStrips(AcadDb.Database db, int maxSections = 8000)
    {
        List<List<ModelSection>> list = new();
        int total = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (AcadDb.ObjectId id in CorridorIds(db, tr))
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not CivilCorridor corridor)
                continue;
            foreach (CivilDb.Baseline baseline in corridor.Baselines)
            {
                foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
                {
                    try
                    {
                        List<ModelSection> sections = ReadSections(baseline, region, double.MinValue, double.MaxValue, true)
                            .OrderBy(x => x.Station).ToList();
                        if (sections.Count > 1)
                        {
                            list.Add(sections);
                            total += sections.Count;
                        }
                    }
                    catch
                    {
                        // Region sem secoes calculadas.
                    }
                    if (total > maxSections)
                    {
                        tr.Commit();
                        return new List<List<ModelSection>>();
                    }
                }
            }
        }
        tr.Commit();
        return list;
    }

    public static RebaixoModel Model(AcadDb.Database db, RebaixoInfo r, double margin)
    {
        double from = r.S0 - margin, to = r.S3 + margin;
        var model = new RebaixoModel
        {
            Marks = new[] { r.P0, r.P1, r.P2, r.P3 },
            MarkNames = new[] { "S0", "S1", "S2", "S3" },
            CorridorId = r.CorridorId,
            BaselineKey = r.BaselineKey
        };

        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(r.CorridorId, AcadDb.OpenMode.ForRead) is CivilCorridor corridor && FindBaseline(corridor, r.BaselineKey) is CivilDb.Baseline baseline)
        {
            double step = Math.Max(0.25, (to - from) / 200.0);
            for (double s = from; s <= to + 1e-9; s += step)
            {
                double st = Math.Clamp(s, baseline.StartStation, baseline.EndStation);
                Point3d p = Xyz(baseline, st, 0, 0);
                if (p != Point3d.Origin)
                    model.Baseline.Add(p);
            }

            foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
            {
                if (Math.Max(region.StartStation, region.EndStation) < from || Math.Min(region.StartStation, region.EndStation) > to)
                    continue;
                model.Sections.AddRange(ReadSections(baseline, region, from, to, false));
            }

            FillNames(db, tr, model);
        }
        tr.Commit();

        SortSections(model);
        return model;
    }

    /// <summary>Mesma estaca em duas Regions (limite): fica uma só.</summary>
    private static void SortSections(RebaixoModel model)
    {
        List<ModelSection> sorted = model.Sections.OrderBy(s => s.Station).ToList();
        model.Sections.Clear();
        foreach (ModelSection s in sorted)
        {
            if (model.Sections.Count == 0 || s.Station - model.Sections[^1].Station > 1e-5)
                model.Sections.Add(s);
        }
    }

    /// <summary>Nomes das subassemblies (para achar o meio-fio do rebaixo e para o clique no 3D).</summary>
    private static void FillNames(AcadDb.Database db, AcadDb.Transaction tr, RebaixoModel model)
    {
        foreach (long key in model.Sections.SelectMany(x => x.Subassemblies.Select(a => a.Key)).Distinct())
        {
            try
            {
                if (db.TryGetObjectId(new AcadDb.Handle(key), out AcadDb.ObjectId said) &&
                    tr.GetObject(said, AcadDb.OpenMode.ForRead) is CivilDb.Subassembly sa)
                {
                    model.Names[key] = sa.Name;
                }
            }
            catch
            {
                // Subassembly apagada.
            }
        }
    }

    /// <summary>
    /// Todos os corredores para o 3D geral: um modelo por baseline, com as secoes inteiras. Passando de
    /// <paramref name="maxSections"/> secoes no total (corredor grande), fica so o entorno dos rebaixos
    /// (<paramref name="margin"/> metros antes e depois de cada um).
    /// </summary>
    public static List<RebaixoModel> ModelsAll(AcadDb.Database db, IReadOnlyList<RebaixoInfo> items, double margin, out bool onlyNear, int maxSections = 4000)
    {
        onlyNear = false;
        List<RebaixoModel>? list = ReadAllModels(db, items, margin, false, maxSections);
        if (list is null)
        {
            onlyNear = true;
            list = ReadAllModels(db, items, margin, true, int.MaxValue) ?? new List<RebaixoModel>();
        }

        return list;
    }

    private static List<RebaixoModel>? ReadAllModels(AcadDb.Database db, IReadOnlyList<RebaixoInfo> items, double margin, bool onlyNear, int maxSections)
    {
        List<RebaixoModel> list = new();
        int total = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (AcadDb.ObjectId id in CorridorIds(db, tr))
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not CivilCorridor corridor)
                continue;
            foreach (CivilDb.Baseline baseline in corridor.Baselines)
            {
                string key = MFRebaixoCommands.GetBaselineKey(baseline);
                List<RebaixoInfo> mine = items.Where(r => r.CorridorId == id && r.BaselineKey == key).ToList();
                if (onlyNear && mine.Count == 0)
                    continue;
                var model = new RebaixoModel { CorridorId = id, BaselineKey = key };
                foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
                {
                    try
                    {
                        double from = onlyNear ? mine.Min(r => r.S0) - margin : double.MinValue;
                        double to = onlyNear ? mine.Max(r => r.S3) + margin : double.MaxValue;
                        IEnumerable<ModelSection> sections = ReadSections(baseline, region, from, to, false);
                        if (onlyNear)
                            sections = sections.Where(s => mine.Any(r => s.Station >= r.S0 - margin && s.Station <= r.S3 + margin));
                        model.Sections.AddRange(sections);
                    }
                    catch
                    {
                        // Region sem secoes calculadas.
                    }
                }

                total += model.Sections.Count;
                if (total > maxSections)
                {
                    tr.Commit();
                    return null;
                }

                if (model.Sections.Count < 2)
                    continue;
                SortSections(model);
                FillNames(db, tr, model);
                list.Add(model);
            }
        }

        tr.Commit();
        return list;
    }

    // ------------------------------------------------------------------
    // Remover
    // ------------------------------------------------------------------

    private static bool IsOwnSet(CivilDb.CorridorTransitionSet x, RebaixoInfo r)
    {
        string n = x.Name ?? "";
        return n.Equals(r.SetName, StringComparison.OrdinalIgnoreCase) ||
               n.StartsWith(r.SetName + HighlightSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Apaga de vez os Transition Sets de rebaixo que ficaram so desligados ("REMOVIDO") em todos
    /// os corredores. O Civil aceita regravar a lista da baseline sem eles, desde que ela nao fique
    /// vazia: nesse caso um set desligado e mantido. Com <paramref name="orphans"/> saem tambem os
    /// rebaixos presos a uma subassembly que nao existe mais na assembly (nao agem no corredor).
    /// </summary>
    public static (int Deleted, int Kept, int Baselines) PurgeRemoved(AcadDb.Database db, bool orphans)
    {
        int deleted = 0, kept = 0, baselines = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (AcadDb.ObjectId id in CorridorIds(db, tr))
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForWrite) is not CivilCorridor corridor)
                continue;
            foreach (CivilDb.Baseline baseline in corridor.Baselines)
            {
                List<CivilDb.CorridorTransitionSet> sets;
                try
                {
                    sets = baseline.getTransitions();
                }
                catch
                {
                    continue;
                }
                HashSet<string> present = orphans ? SubassemblyNames(tr, baseline) : new HashSet<string>();
                bool Drop(CivilDb.CorridorTransitionSet x)
                {
                    string name = x.Name ?? "";
                    if (!name.StartsWith(PairPrefix, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (IsNeutral(x))
                        return true;
                    return orphans && present.Count > 0 && !string.IsNullOrWhiteSpace(x.SubassemblyName) && !present.Contains(x.SubassemblyName);
                }
                List<CivilDb.CorridorTransitionSet> drop = sets.Where(Drop).ToList();
                if (drop.Count == 0)
                    continue;
                List<CivilDb.CorridorTransitionSet> keep = sets.Where(x => !Drop(x)).ToList();
                int keptHere = 0;
                if (keep.Count == 0)
                {
                    // A lista nao pode ficar vazia: fica um (de preferencia ja desligado).
                    CivilDb.CorridorTransitionSet stay = drop.FirstOrDefault(IsNeutral) ?? drop[0];
                    keep.Add(stay);
                    keptHere = 1;
                }
                int expected = keep.Count;
                if (expected == sets.Count)
                {
                    kept += keptHere;
                    continue;
                }
                baseline.SetTransitions(keep);
                int after = baseline.getTransitions().Count;
                if (after != expected)
                    throw new InvalidOperationException($"O Civil 3D nao aceitou limpar a baseline '{baseline.Name}' ({sets.Count} sets antes, {after} depois, esperado {expected}). Nada foi alterado.");
                deleted += sets.Count - expected;
                kept += keptHere;
                baselines++;
                PluginLog.Info("MFREBAIXO", $"Limpar removidos: {corridor.Name} / {baseline.Name}: {sets.Count} -> {after} sets.");
            }
        }
        tr.Commit();
        return (deleted, kept, baselines);
    }

    public const string RemovedComment = "REBAIXO MF - REMOVIDO (apagar no Edit Corridor Transition)";

    /// <summary>Sets ja desligados (lidos no ultimo ReadAll), para avisar e limpar a mao.</summary>
    public static List<string> RemovedSets { get; } = new();

    private static double Val(object v) => Convert.ToDouble(v, CultureInfo.InvariantCulture);

    /// <summary>Set de rebaixo desligado: REB/DESTAQUE todos 0, ou altura livre constante.</summary>
    public static bool IsNeutral(CivilDb.CorridorTransitionSet s)
    {
        try
        {
            if (s.TransitionCount == 0)
                return true;
            List<double> values = new();
            string param = s.GetTransitionAt(0).ParameterName ?? "";
            for (int i = 0; i < s.TransitionCount; i++)
            {
                var t = s.GetTransitionAt(i);
                values.Add(Val(t.StartValue));
                values.Add(Val(t.EndValue));
            }

            bool zeroParam = param.Equals(MFRebaixoCommands.DepthParameterName, StringComparison.OrdinalIgnoreCase) ||
                             param.Equals(MFRebaixoCommands.HighlightParameterName, StringComparison.OrdinalIgnoreCase);
            return zeroParam
                ? values.All(v => Math.Abs(v) < 1e-6)
                : values.All(v => Math.Abs(v - values[0]) < 1e-6);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Copia do set com o mesmo nome e as mesmas estacas, mas com valores que nao mudam nada.</summary>
    private static CivilDb.CorridorTransitionSet Neutral(CivilDb.Baseline baseline, CivilDb.CorridorTransitionSet original)
    {
        var first = original.GetTransitionAt(0);
        CivilDb.BaselineRegion seed = RegionAt(baseline, first.StartStation) ?? RegionAt(baseline, first.EndStation)
            ?? throw new InvalidOperationException($"Sem region para o Transition Set '{original.Name}'.");
        CivilDb.CorridorTransitionSet copy = first.HasSide
            ? new CivilDb.CorridorTransitionSet(original.Name, seed, original.SubassemblyName, original.NameType, first.Side)
            : new CivilDb.CorridorTransitionSet(original.Name, seed, original.SubassemblyName, original.NameType);
        copy.Comment = RemovedComment;

        string param = first.ParameterName ?? "";
        bool zeroParam = param.Equals(MFRebaixoCommands.DepthParameterName, StringComparison.OrdinalIgnoreCase) ||
                         param.Equals(MFRebaixoCommands.HighlightParameterName, StringComparison.OrdinalIgnoreCase);
        // AMF_LIVRE (rebaixo antigo): fica a altura normal da entrada em todo o trecho.
        double neutral = zeroParam ? 0.0 : Val(first.StartValue);

        for (int i = 0; i < original.TransitionCount; i++)
        {
            var data = original.GetTransitionAt(i);
            var created = i == 0 ? copy.AddTransition(data.ParameterName) : copy.AddTransition();
            created.StartStation = data.StartStation;
            created.EndStation = data.EndStation;
            created.StartValue = neutral;
            created.EndValue = neutral;
            created.TransitionType = data.TransitionType;
        }

        copy.StationLocked = original.StationLocked;
        return copy;
    }

    /// <summary>
    /// "Remove" o rebaixo. Set orfao (subassembly que nao existe mais no corredor) e apagado:
    /// SetTransitions com uma lista NAO vazia apaga os sets que ficaram de fora (lista vazia nao
    /// faz nada). Os demais continuam so DESLIGADOS (comportamento conferido no Civil):
    /// os dois sets (geometria e destaque) ficam com o mesmo nome e valores neutros (REB = 0,
    /// DESTAQUE = 0) e o comentario "REMOVIDO"; o painel nao os lista mais. As secoes que o
    /// comando criou saem. Para sumir de vez com os sets, apague no Edit Corridor Transition.
    /// </summary>
    public static string Remove(AcadDb.Database db, RebaixoInfo r, bool rebuild = true)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(r.CorridorId, AcadDb.OpenMode.ForWrite) is not CivilCorridor corridor)
            throw new InvalidOperationException("O corredor do rebaixo nao existe mais.");
        CivilDb.Baseline baseline = FindBaseline(corridor, r.BaselineKey)
            ?? throw new InvalidOperationException("A baseline do rebaixo nao existe mais.");

        List<CivilDb.CorridorTransitionSet> sets = baseline.getTransitions();
        int own = sets.Count(x => IsOwnSet(x, r));
        int deleted = 0;
        if (own > 0)
        {
            // Set orfao: a subassembly dele (ex. MF_DIR antigo) nao existe mais nas assemblies do
            // corredor (trocada pelo MFSARJETA). Ele ja nao age no corredor e nao da para recria-lo
            // com valores neutros ("Subassembly is failed to find by name"). Sai da lista: o
            // SetTransitions com uma lista nao vazia apaga os sets que ficaram de fora.
            HashSet<string> present = SubassemblyNames(tr, baseline);
            // v1.10: com outros sets na baseline, o rebaixo e apagado de vez (nao fica set "REMOVIDO").
            // So quando ele e o unico set e que continua sendo apenas desligado.
            bool canDelete = sets.Any(x => !IsOwnSet(x, r));
            bool Orphan(CivilDb.CorridorTransitionSet x) =>
                canDelete || (present.Count > 0 && !string.IsNullOrWhiteSpace(x.SubassemblyName) && !present.Contains(x.SubassemblyName));
            deleted = sets.Count(x => IsOwnSet(x, r) && Orphan(x));
            List<CivilDb.CorridorTransitionSet> updated = sets
                .Where(x => !(IsOwnSet(x, r) && Orphan(x)))
                .Select(x => IsOwnSet(x, r) ? Neutral(baseline, x) : x)
                .ToList();
            if (updated.Count == 0)
            {
                throw new InvalidOperationException(
                    "O rebaixo e o unico Transition Set da baseline e a subassembly dele nao existe mais; o Civil 3D nao apaga o ultimo set pela API." +
                    " Apague no dialogo Corridor Transition (selecione o corredor > aba do Corridor > Modify Corridor > Edit Corridor Transition).");
            }
            baseline.SetTransitions(updated);

            List<CivilDb.CorridorTransitionSet> after = baseline.getTransitions();
            if (after.Count != sets.Count - deleted || after.Where(x => IsOwnSet(x, r)).Any(x => Orphan(x) || !IsNeutral(x)))
            {
                // Sem commit: nada fica gravado.
                throw new InvalidOperationException(
                    $"O Civil 3D nao aceitou remover o rebaixo ({sets.Count} sets antes, {after.Count} depois). Nada foi alterado." +
                    " Apague no dialogo Corridor Transition (selecione o corredor > aba do Corridor > Modify Corridor > Edit Corridor Transition).");
            }
            if (deleted > 0)
                PluginLog.Info("MFREBAIXO", $"Remover {r.SetName}: {deleted} set(s) orfao(s) apagado(s) {sets.Count} -> {after.Count} sets.");
        }

        int sections = 0;
        double[] ownStations = { r.S0 - 0.001, r.S0, r.S0 + 0.001, r.S1, r.S2, r.S3 - 0.001, r.S3, r.S3 + 0.001 };
        foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
        {
            double[] extra;
            try
            {
                extra = region.AdditionalStations();
            }
            catch
            {
                continue;
            }

            foreach (double st in extra)
            {
                if (!ownStations.Any(o => Math.Abs(o - st) < 0.0002))
                    continue;
                try
                {
                    region.DeleteStation(st);
                    sections++;
                }
                catch
                {
                    // Estaca que nao e adicional (frequencia): fica.
                }
            }
        }

        if (rebuild)
            corridor.Rebuild();
        tr.Commit();
        int hatches = 0;
        try
        {
            hatches = MFRebaixoHatch.Erase(db, r.SetName);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("MFREBAIXO", $"Hachura de {r.SetName} nao apagada: {ex.Message}", ex);
        }

        string setsText = deleted == own
            ? $"{own} Transition Set(s) apagado(s)"
            : deleted > 0
                ? $"{deleted} Transition Set(s) apagado(s) e {own - deleted} desligado(s) (REB/DESTAQUE = 0)"
                : $"{own} Transition Set(s) desligado(s) (REB/DESTAQUE = 0)";
        string tail = own > deleted ? "; era o unico set da baseline, entao ficou so desligado (o Civil nao apaga o ultimo): use Limpar removidos depois de lancar outro rebaixo" : "";
        return $"{setsText}, {sections} secao(oes) e {hatches} hachura(s) removida(s){tail}";
    }

    /// <summary>Nomes das subassemblies das assemblies usadas nas regions da baseline.</summary>
    private static HashSet<string> SubassemblyNames(AcadDb.Transaction tr, CivilDb.Baseline baseline)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
        {
            try
            {
                if (tr.GetObject(region.AssemblyId, AcadDb.OpenMode.ForRead) is not CivilDb.Assembly asm)
                    continue;
                foreach (CivilDb.AssemblyGroup g in asm.Groups)
                {
                    foreach (AcadDb.ObjectId sid in g.GetSubassemblyIds())
                    {
                        if (tr.GetObject(sid, AcadDb.OpenMode.ForRead) is CivilDb.Subassembly sa)
                            names.Add(sa.Name);
                    }
                }
            }
            catch
            {
                // Region sem assembly: segue.
            }
        }

        return names;
    }

    public const string BaseComment = "REBAIXO MF";

    public static string CommentFor(string? name) =>
        string.IsNullOrWhiteSpace(name) ? BaseComment : BaseComment + " | " + name.Trim().Replace('|', '/');

    /// <summary>Da nome ao rebaixo: grava no comentario do Transition Set (SetTransitions atualiza pelo nome).</summary>
    public static void Rename(AcadDb.Database db, RebaixoInfo r, string? name)
    {
        string comment = CommentFor(name);
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(r.CorridorId, AcadDb.OpenMode.ForWrite) is not CivilCorridor corridor)
            throw new InvalidOperationException("O corredor do rebaixo nao existe mais.");
        CivilDb.Baseline baseline = FindBaseline(corridor, r.BaselineKey)
            ?? throw new InvalidOperationException("A baseline do rebaixo nao existe mais.");

        List<CivilDb.CorridorTransitionSet> sets = baseline.getTransitions();
        CivilDb.CorridorTransitionSet set = sets.FirstOrDefault(x => (x.Name ?? "").Equals(r.SetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("O Transition Set do rebaixo nao foi encontrado.");
        set.Comment = comment;
        baseline.SetTransitions(sets);

        List<CivilDb.CorridorTransitionSet> after = baseline.getTransitions();
        CivilDb.CorridorTransitionSet? check = after.FirstOrDefault(x => (x.Name ?? "").Equals(r.SetName, StringComparison.OrdinalIgnoreCase));
        if (after.Count != sets.Count || check is null || (check.Comment ?? "") != comment)
            throw new InvalidOperationException("O Civil 3D nao aceitou o novo nome. Nada foi alterado.");

        tr.Commit();
    }

    public static void Rebuild(AcadDb.Database db, AcadDb.ObjectId corridorId)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(corridorId, AcadDb.OpenMode.ForWrite) is CivilCorridor corridor)
            corridor.Rebuild();
        tr.Commit();
    }

    // ------------------------------------------------------------------
    // Avisos (NBR 9050 e consistencia)
    // ------------------------------------------------------------------

    public static List<RebaixoWarning> Check(List<RebaixoInfo> all, HashSet<string>? setsWithHatch = null)
    {
        List<RebaixoWarning> list = new();
        foreach (RebaixoInfo r in all)
        {
            if (setsWithHatch is not null && !setsWithHatch.Contains(r.SetName))
                list.Add(new RebaixoWarning { Item = r, Serious = false, Text = "Sem hachura em planta.", Reference = "Use Redesenhar hachuras no painel (a hachura e desenhada pelo plugin desde o MFREBAIXO 1.8)" });
            double slope = Math.Abs(r.RampSlope ?? 0.0833);
            if (slope > 0.0834)
                list.Add(new RebaixoWarning { Item = r, Serious = true, Text = $"Rampa do meio-fio com {slope * 100:0.##}% (acima de 8,33%).", Reference = "NBR 9050 - rampas: inclinacao maxima 8,33%" });

            if (r.LoweredLength < 1.50 - 1e-6)
                list.Add(new RebaixoWarning { Item = r, Serious = false, Text = $"Trecho rebaixado com {r.LoweredLength:0.00} m (menos de 1,50 m).", Reference = "NBR 9050 - rebaixamento de calcada: largura minima 1,50 m (travessia de pedestres)" });

            if (r.FinalHeight is double fh && fh > 0.005)
                list.Add(new RebaixoWarning { Item = r, Serious = false, Text = $"Altura livre final de {fh * 100:0.#} cm.", Reference = "NBR 9050 - na travessia de pedestres nao pode haver desnivel entre o rebaixo e o leito carrocavel (garagem: ok)" });

            if (r.RampOk is double ok && ok < 0.5)
                list.Add(new RebaixoWarning { Item = r, Serious = true, Text = "A rampa da calcada nao cabe na largura com a declividade maxima.", Reference = "MFSARJETA - saida Rampa OK = 0" });

            if (r.FreeWidth is double fw && fw > 0 && fw < 1.20 - 1e-6)
                list.Add(new RebaixoWarning { Item = r, Serious = true, Text = $"Faixa livre da calcada depois da rampa: {fw:0.00} m (menos de 1,20 m).", Reference = "NBR 9050 - faixa livre minima de 1,20 m" });


            if (!r.ByDepth)
                list.Add(new RebaixoWarning { Item = r, Serious = false, Text = "Rebaixo pela altura livre (AMF_LIVRE): a calcada do MFSARJETA nao gera a rampa.", Reference = "Reaplique pelo painel com o MFSARJETA 1.14+ (parametro REB)" });
        }

        foreach (IGrouping<string, RebaixoInfo> g in all.GroupBy(r => r.CorridorId.Handle.Value + "|" + r.BaselineKey + "|" + r.Left))
        {
            List<RebaixoInfo> ordered = g.OrderBy(r => r.S0).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].S0 < ordered[i - 1].S3 - 1e-4)
                    list.Add(new RebaixoWarning { Item = ordered[i], Serious = true, Text = $"Sobrepoe o rebaixo {ordered[i - 1].Label}.", Reference = "Dois rebaixos no mesmo trecho brigam pelo mesmo parametro" });
            }
        }

        return list;
    }
}
