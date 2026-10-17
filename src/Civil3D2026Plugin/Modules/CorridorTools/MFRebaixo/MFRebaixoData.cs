using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;

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

    /// <summary>Baseline de S0 a S3 (planta geral).</summary>
    public List<Point3d> Path { get; } = new();

    public double EntryLength => S1 - S0;
    public double LoweredLength => S2 - S1;
    public double ExitLength => S3 - S2;
    public double TotalLength => S3 - S0;

    /// <summary>Altura livre do MF no trecho rebaixado.</summary>
    public double? FinalHeight =>
        ByDepth
            ? (NormalHeight.HasValue ? Math.Max(0, NormalHeight.Value - ValueIn) : ExposedAtMiddle)
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
}

internal sealed class ModelSection
{
    public double Station { get; init; }
    public List<(long Key, List<ModelLink> Links)> Subassemblies { get; } = new();
}

/// <summary>Trecho do corredor em volta de um rebaixo (planta e 3D).</summary>
internal sealed class RebaixoModel
{
    public List<ModelSection> Sections { get; } = new();
    public List<Point3d> Baseline { get; } = new();
    public Point3d[] Marks { get; init; } = Array.Empty<Point3d>();
    public string[] MarkNames { get; init; } = Array.Empty<string>();
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
                ReadCorridorValues(tr, region, info);

            return info;
        }
        catch
        {
            return null;
        }
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

    private static Point3d Xyz(CivilDb.Baseline baseline, double station, double offset, double elevation)
    {
        try
        {
            Point3d p = baseline.StationOffsetElevationToXYZ(new Point3d(station, offset, elevation));
            return new Point3d(p.X, p.Y, elevation);
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

    public static RebaixoModel Model(AcadDb.Database db, RebaixoInfo r, double margin)
    {
        double from = r.S0 - margin, to = r.S3 + margin;
        var model = new RebaixoModel
        {
            Marks = new[] { r.P0, r.P1, r.P2, r.P3 },
            MarkNames = new[] { "S0", "S1", "S2", "S3" }
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
                            List<Point3d> pts = new();
                            foreach (CivilDb.CalculatedPoint cp in link.CalculatedPoints)
                            {
                                Point3d soe = cp.StationOffsetElevationToBaseline;
                                Point3d w = Xyz(baseline, soe.X, soe.Y, soe.Z);
                                if (w != Point3d.Origin)
                                    pts.Add(w);
                            }

                            List<string> codes = new();
                            foreach (string code in link.CorridorCodes)
                            {
                                if (!string.IsNullOrWhiteSpace(code))
                                    codes.Add(code);
                            }

                            links.Add(new ModelLink { Codes = codes.ToArray(), Points = pts.ToArray() });
                        }

                        section.Subassemblies.Add((asa.SubassemblyId.Handle.Value, links));
                    }

                    model.Sections.Add(section);
                }
            }
        }

        tr.Commit();

        // Mesma estaca em duas Regions (limite): fica uma só.
        List<ModelSection> sorted = model.Sections.OrderBy(s => s.Station).ToList();
        model.Sections.Clear();
        foreach (ModelSection s in sorted)
        {
            if (model.Sections.Count == 0 || s.Station - model.Sections[^1].Station > 1e-5)
                model.Sections.Add(s);
        }

        return model;
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
    /// "Remove" o rebaixo. A API do Civil 3D nao apaga Transition Set: SetTransitions e
    /// ImportTransitions so acrescentam ou atualizam (pelo nome). Entao o rebaixo e DESLIGADO:
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
        if (own > 0)
        {
            List<CivilDb.CorridorTransitionSet> updated = sets.Select(x => IsOwnSet(x, r) ? Neutral(baseline, x) : x).ToList();
            baseline.SetTransitions(updated);

            List<CivilDb.CorridorTransitionSet> after = baseline.getTransitions();
            if (after.Count != sets.Count || after.Where(x => IsOwnSet(x, r)).Any(x => !IsNeutral(x)))
            {
                // Sem commit: nada fica gravado.
                throw new InvalidOperationException(
                    $"O Civil 3D nao aceitou desligar o rebaixo ({sets.Count} sets antes, {after.Count} depois). Nada foi alterado." +
                    " Apague no dialogo Corridor Transition (selecione o corredor > aba do Corridor > Modify Corridor > Edit Corridor Transition).");
            }
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
        return $"{own} Transition Set(s) desligado(s) (REB/DESTAQUE = 0) e {sections} secao(oes) removida(s); os sets vazios podem ser apagados no Edit Corridor Transition";
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

    public static List<RebaixoWarning> Check(List<RebaixoInfo> all)
    {
        List<RebaixoWarning> list = new();
        foreach (RebaixoInfo r in all)
        {
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

            if (r.HasHighlightParameter && r.HighlightSetName is null)
                list.Add(new RebaixoWarning { Item = r, Serious = false, Text = "Sem o Transition do DESTAQUE: o code REBAIXO nao aparece na planta.", Reference = "Reaplique o rebaixo pelo painel" });

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
