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

    public string Label => $"{Num(S1)} - {Num(S2)}";

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

                    RebaixoInfo? info = Build(tr, corridor, id, baseline, set, sets);
                    if (info is not null)
                        list.Add(info);
                }
            }
        }

        tr.Commit();
        return list.OrderBy(r => r.CorridorName).ThenBy(r => r.BaselineName).ThenBy(r => r.S1).ToList();
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
                ValueStart = t0.StartValue,
                ValueIn = t0.EndValue,
                ValueOut = t1.EndValue,
                ValueEnd = t2.EndValue
            };

            info.P0 = Xyz(baseline, s0, 0, 0);
            info.P1 = Xyz(baseline, s1, 0, 0);
            info.P2 = Xyz(baseline, s2, 0, 0);
            info.P3 = Xyz(baseline, s3, 0, 0);

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
            info.ExposedAtMiddle = Get(b.Values, ExposedOutput) ?? Get(b.Values, MFRebaixoCommands.EffectiveOutputAltParameterName);
            info.RampSlope = Get(b.Values, MFRebaixoCommands.RampSlopeParameterName);
            info.RampOk = Get(b.Values, RampOkOutput);
            info.FreeWidth = Get(b.Values, FreeWidthOutput);
            info.HasHighlightParameter = b.Values.ContainsKey(MFRebaixoCommands.HighlightParameterName);
        }

        if (before is { } n)
            info.NormalHeight = Get(n.Values, ExposedOutput) ?? Get(n.Values, MFRebaixoCommands.EffectiveOutputAltParameterName);
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
                        Exposed = Get(v, ExposedOutput) ?? Get(v, MFRebaixoCommands.EffectiveOutputAltParameterName),
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

    /// <summary>Tira os dois Transition Sets do rebaixo e as seções que o comando criou.</summary>
    public static void Remove(AcadDb.Database db, RebaixoInfo r, bool rebuild = true)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(r.CorridorId, AcadDb.OpenMode.ForWrite) is not CivilCorridor corridor)
            throw new InvalidOperationException("O corredor do rebaixo nao existe mais.");
        CivilDb.Baseline baseline = FindBaseline(corridor, r.BaselineKey)
            ?? throw new InvalidOperationException("A baseline do rebaixo nao existe mais.");

        List<CivilDb.CorridorTransitionSet> sets = baseline.getTransitions();
        List<CivilDb.CorridorTransitionSet> keep = sets.Where(x =>
        {
            string n = x.Name ?? "";
            return !n.Equals(r.SetName, StringComparison.OrdinalIgnoreCase) &&
                   !n.StartsWith(r.SetName + HighlightSuffix, StringComparison.OrdinalIgnoreCase);
        }).ToList();
        if (keep.Count != sets.Count)
            baseline.SetTransitions(keep);

        double[] own = { r.S0 - 0.001, r.S0, r.S0 + 0.001, r.S1, r.S2, r.S3 - 0.001, r.S3, r.S3 + 0.001 };
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
                if (!own.Any(o => Math.Abs(o - st) < 0.0002))
                    continue;
                try
                {
                    region.DeleteStation(st);
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
