using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.Geometry;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;

using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>
/// Hachura do rebaixo em planta, desenhada pelo plugin (MFREBAIXO v1.8+).
/// Antes a hachura vinha do Code Set Style (code REBAIXO em links de sobreposicao do PKT, com
/// secoes a +-1 mm de S0/S3). Esses links viravam cascas (BODY) na extracao de solidos e a
/// hachura dependia do estilo. Agora o comando le os pontos calculados do corredor entre S0 e S3
/// (borda da pista -> fundo do meio-fio ou borda da calcada) e grava tres HATCH na layer
/// propria: rampa de entrada, trecho rebaixado e rampa de saida. Cada HATCH leva XData com o
/// nome do Transition Set, para o painel refazer e remover junto com o rebaixo.
/// </summary>
internal static class MFRebaixoHatch
{
    public const string RegApp = "C3D_MFREBAIXO";
    public const string Marker = "MFREBAIXO-HACHURA";
    public const string DefaultLayer = "MF-REBAIXO";

    // Codes do MFSARJETA: borda da pista (P1), flowline (P2), topo de tras do MF (P7), borda externa da calcada (P102).
    private const string InnerCode = "Flange";
    private const string InnerFallbackCode = "Flowline_Gutter";
    private const string OuterCode = "Back_Curb";
    private const string SidewalkCode = "Calcada_Ext";

    public enum Part
    {
        RampaEntrada,
        Rebaixado,
        RampaSaida
    }

    // ------------------------------------------------------------------
    // Desenhar
    // ------------------------------------------------------------------

    /// <summary>
    /// Desenha (ou redesenha) a hachura do rebaixo <paramref name="setName"/>. Abre a propria
    /// transacao: chamar depois do Rebuild + Commit do corredor. Devolve quantas hachuras sairam
    /// (0 = sem pontos no trecho; nada e lancado, so avisa no log).
    /// </summary>
    public static int Draw(AcadDb.Database db, AcadDb.ObjectId corridorId, string baselineKey, AcadDb.ObjectId subassemblyId,
        string setName, double s0, double s1, double s2, double s3, MFRebaixoSettings? settings = null)
    {
        settings ??= MFRebaixoSettingsStore.Load();
        if (!settings.HachuraAtiva)
            return 0;

        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        int erased = Erase(tr, db, setName);
        int drawn = 0;
        if (tr.GetObject(corridorId, AcadDb.OpenMode.ForRead) is CivilCorridor corridor)
        {
            CivilDb.Baseline? baseline = null;
            foreach (CivilDb.Baseline b in corridor.Baselines)
            {
                if (MFRebaixoCommands.GetBaselineKey(b) == baselineKey)
                {
                    baseline = b;
                    break;
                }
            }

            if (baseline is not null)
            {
                List<(double Station, Point3d Inner, Point3d Outer)> edges = Edges(baseline, subassemblyId, s0, s3, settings.HachuraAteCalcada);
                if (edges.Count >= 2)
                {
                    EnsureLayer(tr, db, settings.HachuraLayer, settings.HachuraCor);
                    EnsureRegApp(tr, db);
                    // Sempre no Model Space (a planta), mesmo com um layout ativo.
                    var bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
                    var owner = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);
                    drawn += DrawPart(tr, db, owner, edges, s0, s1, Part.RampaEntrada, setName, settings);
                    drawn += DrawPart(tr, db, owner, edges, s1, s2, Part.Rebaixado, setName, settings);
                    drawn += DrawPart(tr, db, owner, edges, s2, s3, Part.RampaSaida, setName, settings);
                }
                else
                {
                    PluginLog.Aviso("MFREBAIXO", $"Hachura de {setName}: so {edges.Count} secao(oes) com os codes {InnerCode}/{OuterCode} entre {s0:0.000} e {s3:0.000}; nada desenhado.");
                }
            }
        }

        tr.Commit();
        PluginLog.Diag("MFREBAIXO", $"Hachura de {setName}: {erased} apagada(s), {drawn} desenhada(s).");
        return drawn;
    }

    /// <summary>Redesenha a hachura de todos os rebaixos lidos (botao do painel e depois de Rebuild).</summary>
    public static (int Rebaixos, int Hachuras) RedrawAll(AcadDb.Database db, IEnumerable<RebaixoInfo> items)
    {
        MFRebaixoSettings settings = MFRebaixoSettingsStore.Load();
        int n = 0, h = 0;
        foreach (RebaixoInfo r in items)
        {
            AcadDb.ObjectId saId = FindSubassemblyId(db, r);
            h += Draw(db, r.CorridorId, r.BaselineKey, saId, r.SetName, r.S0, r.S1, r.S2, r.S3, settings);
            n++;
        }

        return (n, h);
    }

    /// <summary>Apaga as hachuras do rebaixo (propria transacao).</summary>
    public static int Erase(AcadDb.Database db, string setName)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        int n = Erase(tr, db, setName);
        tr.Commit();
        return n;
    }

    /// <summary>Nomes dos Transition Sets que tem hachura no desenho (para o aviso "sem hachura").</summary>
    public static HashSet<string> SetsWithHatch(AcadDb.Database db)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (AcadDb.Hatch h in OwnHatches(tr, db, null))
        {
            string? name = SetNameOf(h);
            if (name is not null)
                set.Add(name);
        }

        tr.Commit();
        return set;
    }

    // ------------------------------------------------------------------
    // Internos
    // ------------------------------------------------------------------

    private static int DrawPart(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockTableRecord owner,
        List<(double Station, Point3d Inner, Point3d Outer)> edges, double from, double to, Part part, string setName, MFRebaixoSettings settings)
    {
        const double eps = 0.0005;
        List<(double Station, Point3d Inner, Point3d Outer)> inside = edges.Where(e => e.Station >= from - eps && e.Station <= to + eps).ToList();
        if (inside.Count < 2 || to - from < 0.002)
            return 0;

        var loop = new Point2dCollection();
        var bulges = new DoubleCollection();
        foreach ((_, Point3d inner, _) in inside)
            Add(loop, bulges, inner);
        for (int i = inside.Count - 1; i >= 0; i--)
            Add(loop, bulges, inside[i].Outer);
        if (loop.Count < 3)
            return 0;

        var hatch = new AcadDb.Hatch();
        hatch.SetDatabaseDefaults(db);
        hatch.Layer = settings.HachuraLayer;
        hatch.ColorIndex = 256; // ByLayer
        hatch.Normal = Vector3d.ZAxis;
        hatch.Elevation = 0.0;
        owner.AppendEntity(hatch);
        tr.AddNewlyCreatedDBObject(hatch, true);
        string pattern = part == Part.Rebaixado ? settings.HachuraPadraoRebaixo : settings.HachuraPadraoRampa;
        try
        {
            hatch.SetHatchPattern(AcadDb.HatchPatternType.PreDefined, pattern);
        }
        catch
        {
            hatch.SetHatchPattern(AcadDb.HatchPatternType.PreDefined, "ANSI31");
        }

        if (!pattern.Equals("SOLID", StringComparison.OrdinalIgnoreCase))
        {
            hatch.PatternScale = Math.Max(0.01, settings.HachuraEscala);
            hatch.PatternAngle = part == Part.RampaSaida ? Math.PI / 2.0 : 0.0;
        }

        hatch.Associative = false;
        hatch.AppendLoop(AcadDb.HatchLoopTypes.Polyline, loop, bulges);
        hatch.EvaluateHatch(true);
        if (settings.HachuraTransparencia > 0)
        {
            int pct = Math.Clamp(settings.HachuraTransparencia, 0, 90);
            hatch.Transparency = new Transparency((byte)Math.Round(255.0 * (100 - pct) / 100.0));
        }

        hatch.XData = new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, RegApp),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, Marker),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, setName),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, part.ToString()),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataReal, from),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataReal, to));
        return 1;
    }

    private static void Add(Point2dCollection loop, DoubleCollection bulges, Point3d p)
    {
        var q = new Point2d(p.X, p.Y);
        if (loop.Count > 0 && loop[loop.Count - 1].GetDistanceTo(q) < 0.0005)
            return;
        loop.Add(q);
        bulges.Add(0.0);
    }

    /// <summary>
    /// Em cada secao calculada do corredor entre s0 e s3, o ponto interno (borda da pista) e o
    /// externo (fundo do MF ou borda da calcada) da subassembly do rebaixo, em XY do desenho.
    /// </summary>
    private static List<(double Station, Point3d Inner, Point3d Outer)> Edges(CivilDb.Baseline baseline, AcadDb.ObjectId subassemblyId,
        double s0, double s3, bool toSidewalk)
    {
        const double eps = 0.0005;
        List<(double, Point3d, Point3d)> list = new();
        foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
        {
            if (Math.Max(region.StartStation, region.EndStation) < s0 - eps || Math.Min(region.StartStation, region.EndStation) > s3 + eps)
                continue;

            foreach (CivilDb.AppliedAssembly aa in region.AppliedAssemblies)
            {
                foreach (CivilDb.AppliedSubassembly asa in aa.GetAppliedSubassemblies())
                {
                    if (asa.SubassemblyId != subassemblyId)
                        continue;

                    double? st = null;
                    Point3d? inner = null, innerFallback = null, outer = null, sidewalk = null;
                    foreach (CivilDb.CalculatedPoint cp in asa.Points)
                    {
                        Point3d soe = cp.StationOffsetElevationToBaseline;
                        st ??= soe.X;
                        foreach (string code in cp.CorridorCodes)
                        {
                            if (code.Equals(InnerCode, StringComparison.OrdinalIgnoreCase)) inner = soe;
                            else if (code.Equals(InnerFallbackCode, StringComparison.OrdinalIgnoreCase)) innerFallback = soe;
                            else if (code.Equals(OuterCode, StringComparison.OrdinalIgnoreCase)) outer = soe;
                            else if (code.Equals(SidewalkCode, StringComparison.OrdinalIgnoreCase)) sidewalk = soe;
                        }
                    }

                    if (st is null || st < s0 - eps || st > s3 + eps)
                        break;
                    Point3d? a = inner ?? innerFallback;
                    Point3d? b = (toSidewalk ? sidewalk : null) ?? outer;
                    if (a is null || b is null)
                        break;
                    try
                    {
                        Point3d pa = baseline.StationOffsetElevationToXYZ(new Point3d(st.Value, a.Value.Y, 0));
                        Point3d pb = baseline.StationOffsetElevationToXYZ(new Point3d(st.Value, b.Value.Y, 0));
                        list.Add((st.Value, new Point3d(pa.X, pa.Y, 0), new Point3d(pb.X, pb.Y, 0)));
                    }
                    catch
                    {
                        // Estaca fora da baseline.
                    }

                    break;
                }
            }
        }

        // Mesma estaca em duas regions (limite): fica uma.
        List<(double Station, Point3d Inner, Point3d Outer)> sorted = list.OrderBy(e => e.Item1).Select(e => (e.Item1, e.Item2, e.Item3)).ToList();
        List<(double Station, Point3d Inner, Point3d Outer)> result = new();
        foreach (var e in sorted)
        {
            if (result.Count == 0 || e.Station - result[^1].Station > 1e-5)
                result.Add(e);
        }

        return result;
    }

    /// <summary>Subassembly MF do rebaixo (pela region do trecho rebaixado e pelo lado).</summary>
    private static AcadDb.ObjectId FindSubassemblyId(AcadDb.Database db, RebaixoInfo r)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.ObjectId found = AcadDb.ObjectId.Null;
        if (tr.GetObject(r.CorridorId, AcadDb.OpenMode.ForRead) is CivilCorridor corridor)
        {
            double mid = (r.S1 + r.S2) / 2.0;
            foreach (CivilDb.Baseline b in corridor.Baselines)
            {
                if (MFRebaixoCommands.GetBaselineKey(b) != r.BaselineKey)
                    continue;
                foreach (CivilDb.BaselineRegion region in b.BaselineRegions)
                {
                    if (mid < Math.Min(region.StartStation, region.EndStation) || mid > Math.Max(region.StartStation, region.EndStation))
                        continue;
                    try
                    {
                        found = MFRebaixoCommands.FindMfSubassemblyId(tr, region,
                            r.Left ? CivilDb.SubassemblySideType.Left : CivilDb.SubassemblySideType.Right, out _);
                    }
                    catch
                    {
                        // Region sem assembly.
                    }

                    if (!found.IsNull)
                        break;
                }

                break;
            }
        }

        tr.Commit();
        return found;
    }

    private static int Erase(AcadDb.Transaction tr, AcadDb.Database db, string setName)
    {
        int n = 0;
        foreach (AcadDb.Hatch h in OwnHatches(tr, db, setName).ToList())
        {
            h.UpgradeOpen();
            h.Erase();
            n++;
        }

        return n;
    }

    private static IEnumerable<AcadDb.Hatch> OwnHatches(AcadDb.Transaction tr, AcadDb.Database db, string? setName)
    {
        var bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId btrId in new[] { bt[AcadDb.BlockTableRecord.ModelSpace], db.CurrentSpaceId }.Distinct())
        {
            if (tr.GetObject(btrId, AcadDb.OpenMode.ForRead) is not AcadDb.BlockTableRecord btr)
                continue;
            foreach (AcadDb.ObjectId id in btr)
            {
                if (id.IsErased || id.ObjectClass.DxfName != "HATCH")
                    continue;
                if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not AcadDb.Hatch h)
                    continue;
                string? name = SetNameOf(h);
                if (name is null)
                    continue;
                if (setName is null || name.Equals(setName, StringComparison.OrdinalIgnoreCase))
                    yield return h;
            }
        }
    }

    private static string? SetNameOf(AcadDb.Hatch h)
    {
        using AcadDb.ResultBuffer? rb = h.GetXDataForApplication(RegApp);
        if (rb is null)
            return null;
        AcadDb.TypedValue[] values = rb.AsArray();
        // [0] regapp, [1] marcador, [2] nome do set.
        if (values.Length < 3 || values[1].Value as string != Marker)
            return null;
        return values[2].Value as string;
    }

    private static void EnsureLayer(AcadDb.Transaction tr, AcadDb.Database db, string name, short color)
    {
        var lt = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        if (lt.Has(name))
            return;
        lt.UpgradeOpen();
        var ltr = new AcadDb.LayerTableRecord
        {
            Name = name,
            Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, color),
            Description = "Hachura dos rebaixos de meio-fio (MFREBAIXO)"
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
}
