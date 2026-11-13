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
    public static int Draw(AcadDb.Database db, AcadDb.ObjectId corridorId, string baselineKey, string subassemblyName,
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
                var diag = new List<string>();
                List<(double Station, Point3d Inner, Point3d Outer)> edges = Edges(tr, baseline, subassemblyName, s0, s3, settings.HachuraAteCalcada, diag, setName.Contains("_ESQ_", StringComparison.OrdinalIgnoreCase));
                if (edges.Count >= 2)
                {
                    EnsureLayer(tr, db, settings.HachuraLayer, settings.HachuraCor);
                    EnsureRegApp(tr, db);
                    SetLocked(tr, db, settings.HachuraLayer, false);
                    // Sempre no Model Space (a planta), mesmo com um layout ativo.
                    var bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
                    var owner = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);
                    var created = new AcadDb.ObjectIdCollection();
                    drawn += DrawPart(tr, db, owner, edges, s0, s1, Part.RampaEntrada, setName, settings, created);
                    drawn += DrawPart(tr, db, owner, edges, s1, s2, Part.Rebaixado, setName, settings, created);
                    drawn += DrawPart(tr, db, owner, edges, s2, s3, Part.RampaSaida, setName, settings, created);
                    // Por cima do corredor (a hachura e um destaque com transparencia).
                    if (created.Count > 0 && settings.HachuraNaFrente)
                    {
                        try
                        {
                            var order = (AcadDb.DrawOrderTable)tr.GetObject(owner.DrawOrderTableId, AcadDb.OpenMode.ForWrite);
                            order.MoveToTop(created);
                        }
                        catch (System.Exception ex)
                        {
                            PluginLog.Aviso("MFREBAIXO", $"Hachura de {setName}: ordem de desenho nao alterada: {ex.Message}");
                        }
                    }
                }
                else
                {
                    PluginLog.Aviso("MFREBAIXO", $"Hachura de {setName}: so {edges.Count} secao(oes) do meio-fio '{subassemblyName}' entre {s0:0.000} e {s3:0.000}; nada desenhado. " + string.Join(" | ", diag));
                }
            }
        }

        SetLocked(tr, db, settings.HachuraLayer, settings.HachuraTrancada);
        tr.Commit();
        PluginLog.Diag("MFREBAIXO", $"Hachura de {setName}: {erased} apagada(s), {drawn} desenhada(s).");
        return drawn;
    }

    /// <summary>O mesmo, a partir do id da subassembly na Assembly (o comando MFREBAIXO).</summary>
    public static int Draw(AcadDb.Database db, AcadDb.ObjectId corridorId, string baselineKey, AcadDb.ObjectId subassemblyId,
        string setName, double s0, double s1, double s2, double s3, MFRebaixoSettings? settings = null)
    {
        string name = "";
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            if (!subassemblyId.IsNull && tr.GetObject(subassemblyId, AcadDb.OpenMode.ForRead) is CivilDb.Subassembly sa)
                name = sa.Name;
            tr.Commit();
        }
        return Draw(db, corridorId, baselineKey, name, setName, s0, s1, s2, s3, settings);
    }

    /// <summary>Redesenha a hachura de todos os rebaixos lidos (botao do painel e depois de Rebuild).</summary>
    public static (int Rebaixos, int Hachuras) RedrawAll(AcadDb.Database db, IEnumerable<RebaixoInfo> items)
    {
        MFRebaixoSettings settings = MFRebaixoSettingsStore.Load();
        int n = 0, h = 0;
        foreach (RebaixoInfo r in items)
        {
            h += Draw(db, r.CorridorId, r.BaselineKey, r.SubassemblyName, r.SetName, r.S0, r.S1, r.S2, r.S3, settings);
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

    /// <summary>A layer das hachuras existe e esta ligada? (null = a layer nao existe)</summary>
    public static bool? IsLayerOn(AcadDb.Database db, string layer)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        var lt = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        bool? on = null;
        if (lt.Has(layer) && tr.GetObject(lt[layer], AcadDb.OpenMode.ForRead) is AcadDb.LayerTableRecord ltr)
            on = !ltr.IsOff && !ltr.IsFrozen;
        tr.Commit();
        return on;
    }

    /// <summary>Liga ou desliga a layer das hachuras (o botao "representacao em planta" do painel).</summary>
    public static bool SetLayerOn(AcadDb.Database db, string layer, bool on)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        var lt = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        bool done = false;
        if (lt.Has(layer) && tr.GetObject(lt[layer], AcadDb.OpenMode.ForWrite) is AcadDb.LayerTableRecord ltr)
        {
            ltr.IsOff = !on;
            if (on && ltr.IsFrozen && db.Clayer != ltr.ObjectId)
                ltr.IsFrozen = false;
            done = true;
        }

        tr.Commit();
        return done;
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
        List<(double Station, Point3d Inner, Point3d Outer)> edges, double from, double to, Part part, string setName, MFRebaixoSettings settings, AcadDb.ObjectIdCollection created)
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
        // Laco fechado (primeiro ponto repetido no fim) e marcado como externo.
        double wanted = PolygonArea(loop);
        loop.Add(loop[0]);
        bulges.Add(0.0);
        hatch.AppendLoop(AcadDb.HatchLoopTypes.External | AcadDb.HatchLoopTypes.Polyline, loop, bulges);
        hatch.EvaluateHatch(true);
        try
        {
            double got = hatch.Area;
            string line = $"Hachura de {setName} / {part}: {loop.Count - 1} vertices, area do poligono {wanted:0.000} m2, area da hachura {got:0.000} m2";
            if (Math.Abs(got - wanted) > Math.Max(0.01, wanted * 0.02))
                PluginLog.Aviso("MFREBAIXO", line + "  <-- DIFERENTE");
            else
                PluginLog.Diag("MFREBAIXO", line);
        }
        catch
        {
            // Area nao calculada: segue.
        }
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
        created.Add(hatch.ObjectId);
        return 1;
    }

    /// <summary>Area do poligono (formula do cadarco), para conferir com a area da hachura.</summary>
    private static double PolygonArea(Point2dCollection loop)
    {
        double sum = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            Point2d a = loop[i], b = loop[(i + 1) % loop.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }
        return Math.Abs(sum) / 2.0;
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
    /// Faixa do meio-fio (borda da pista -> fundo do MF, ou borda da calcada) da subassembly de
    /// nome <paramref name="subassemblyName"/> nas secoes calculadas entre s0 e s3, em XY do desenho.
    /// </summary>
    internal static List<(double Station, Point3d Inner, Point3d Outer)> Edges(AcadDb.Transaction tr, CivilDb.Baseline baseline,
        string subassemblyName, double s0, double s3, bool toSidewalk, List<string>? diag = null, bool? left = null)
    {
        var all = EdgesBySubassembly(tr, baseline, s0, s3, toSidewalk,
            sa => sa.Name.Equals(subassemblyName, StringComparison.OrdinalIgnoreCase), diag);
        // Cada Region tem a sua copia da peca (ids diferentes, mesmo nome): o rebaixo que passa da
        // divisa (rampa na Region vizinha, 13/11/2026) junta as secoes de todas, em ordem de estaca.
        List<(double Station, Point3d Inner, Point3d Outer)> best = new();
        foreach (var e in all.Values.SelectMany(x => x).OrderBy(x => x.Station))
        {
            if (best.Count == 0 || e.Station - best[^1].Station > 1e-5)
                best.Add(e);
        }
        if (best.Count >= 2)
            return best;

        // Nome nao bateu: qualquer MF/MFSARJETA do trecho, do lado do rebaixo quando ha mais de um.
        all = EdgesBySubassembly(tr, baseline, s0, s3, toSidewalk, MFRebaixoCommands.IsMfSubassemblyPublic, diag);
        List<(double Station, Point3d Inner, Point3d Outer)>? fallback = null;
        foreach (var list in all.Values)
        {
            if (list.Count < 2)
                continue;
            if (left is bool wantLeft && all.Count > 1 && IsLeft(list) != wantLeft)
                continue;
            if (fallback is null || list.Count > fallback.Count)
                fallback = list;
        }
        diag?.Add("nome da subassembly nao encontrado; usado o MF do trecho: " + (fallback is null ? "nenhum" : fallback.Count + " secoes"));
        return fallback ?? new List<(double Station, Point3d Inner, Point3d Outer)>();
    }

    /// <summary>A faixa fica a esquerda da baseline (no sentido das estacas)?</summary>
    private static bool IsLeft(List<(double Station, Point3d Inner, Point3d Outer)> band)
    {
        Vector3d along = band[^1].Inner - band[0].Inner;
        Vector3d across = band[0].Outer - band[0].Inner;
        return (along.X * across.Y) - (along.Y * across.X) > 0;
    }

    /// <summary>
    /// Faixas de todas as subassemblies aceitas por <paramref name="match"/>, uma lista por subassembly.
    /// A borda interna e a origem da subassembly (P1 do MFSARJETA = borda da pista); a externa e o
    /// ponto mais longe da origem entre os links com code Curb (sem eles: Sarjeta; sem eles: qualquer
    /// link que nao seja de calcada). Usa os codes dos LINKS, os mesmos da planta do rebaixo do painel.
    /// </summary>
    internal static Dictionary<AcadDb.ObjectId, List<(double Station, Point3d Inner, Point3d Outer)>> EdgesBySubassembly(
        AcadDb.Transaction tr, CivilDb.Baseline baseline, double s0, double s3, bool toSidewalk,
        Func<CivilDb.Subassembly, bool> match, List<string>? diag = null)
    {
        const double eps = 0.0005;
        var raw = new Dictionary<AcadDb.ObjectId, List<(double Station, Point3d Inner, Point3d Outer)>>();
        var matched = new Dictionary<AcadDb.ObjectId, bool>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int regions = 0, sections = 0, hits = 0, noOuter = 0;
        foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
        {
            if (Math.Max(region.StartStation, region.EndStation) < s0 - eps || Math.Min(region.StartStation, region.EndStation) > s3 + eps)
                continue;
            regions++;
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
                    if (origin.X < s0 - eps || origin.X > s3 + eps)
                        break; // secao fora do trecho
                    AcadDb.ObjectId said = asa.SubassemblyId;
                    if (!matched.TryGetValue(said, out bool ok))
                    {
                        ok = false;
                        try
                        {
                            if (tr.GetObject(said, AcadDb.OpenMode.ForRead) is CivilDb.Subassembly sa)
                            {
                                seenNames.Add(sa.Name);
                                ok = match(sa);
                            }
                        }
                        catch
                        {
                            // Subassembly apagada.
                        }
                        matched[said] = ok;
                    }
                    if (!ok)
                        continue;
                    hits++;
                    double o0 = origin.Y;
                    double? curb = null, gutter = null, walk = null, any = null;
                    foreach (CivilDb.CalculatedLink link in asa.Links)
                    {
                        bool isCurb = false, isGutter = false, isWalk = false;
                        foreach (string code in link.CorridorCodes)
                        {
                            if (string.IsNullOrWhiteSpace(code)) continue;
                            if (code.Equals("Curb", StringComparison.OrdinalIgnoreCase)) isCurb = true;
                            else if (code.Equals("Sarjeta", StringComparison.OrdinalIgnoreCase)) isGutter = true;
                            else if (code.StartsWith("Calc", StringComparison.OrdinalIgnoreCase)) isWalk = true;
                        }
                        foreach (CivilDb.CalculatedPoint cp in link.CalculatedPoints)
                        {
                            double off = cp.StationOffsetElevationToBaseline.Y;
                            if (isWalk)
                                walk = Farther(walk, off, o0);
                            else
                            {
                                any = Farther(any, off, o0);
                                if (isCurb) curb = Farther(curb, off, o0);
                                if (isGutter) gutter = Farther(gutter, off, o0);
                            }
                        }
                    }
                    double? outer = (toSidewalk ? walk : null) ?? curb ?? gutter ?? any;
                    if (outer is null || Math.Abs(outer.Value - o0) < 0.01)
                    {
                        noOuter++;
                        continue;
                    }
                    try
                    {
                        Point3d pa = baseline.StationOffsetElevationToXYZ(new Point3d(origin.X, o0, origin.Z));
                        Point3d pb = baseline.StationOffsetElevationToXYZ(new Point3d(origin.X, outer.Value, origin.Z));
                        if (!raw.TryGetValue(said, out var list))
                            raw[said] = list = new List<(double Station, Point3d Inner, Point3d Outer)>();
                        list.Add((origin.X, new Point3d(pa.X, pa.Y, 0), new Point3d(pb.X, pb.Y, 0)));
                        sections++;
                    }
                    catch
                    {
                        // Estaca fora da baseline.
                    }
                }
            }
        }
        // Em ordem de estaca; mesma estaca em duas regions (limite) fica uma.
        var result = new Dictionary<AcadDb.ObjectId, List<(double Station, Point3d Inner, Point3d Outer)>>();
        foreach (var kv in raw)
        {
            var clean = new List<(double Station, Point3d Inner, Point3d Outer)>();
            foreach (var e in kv.Value.OrderBy(x => x.Station))
            {
                if (clean.Count == 0 || e.Station - clean[^1].Station > 1e-5)
                    clean.Add(e);
            }
            result[kv.Key] = clean;
        }
        diag?.Add($"regions no trecho: {regions}; subassemblies vistas: {string.Join(", ", seenNames)}; aplicacoes da MF: {hits}; sem borda externa: {noOuter}; secoes com faixa: {sections}");
        return result;
    }

    private static double? Farther(double? current, double offset, double origin) =>
        current is null || Math.Abs(offset - origin) > Math.Abs(current.Value - origin) ? offset : current;

    private static int Erase(AcadDb.Transaction tr, AcadDb.Database db, string setName)
    {
        int n = 0;
        foreach (AcadDb.Hatch h in OwnHatches(tr, db, setName).ToList())
        {
            // forceOpenOnLockedLayer: a layer da hachura pode estar trancada.
            if (tr.GetObject(h.ObjectId, AcadDb.OpenMode.ForWrite, false, true) is AcadDb.Hatch w)
                w.Erase();
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

    /// <summary>Tranca ou destranca a layer da hachura (se existir).</summary>
    private static void SetLocked(AcadDb.Transaction tr, AcadDb.Database db, string layer, bool locked)
    {
        try
        {
            var lt = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
            if (!lt.Has(layer))
                return;
            var ltr = (AcadDb.LayerTableRecord)tr.GetObject(lt[layer], AcadDb.OpenMode.ForRead);
            if (ltr.IsLocked == locked)
                return;
            ltr.UpgradeOpen();
            ltr.IsLocked = locked;
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso("MFREBAIXO", $"Layer {layer}: nao deu para {(locked ? "trancar" : "destrancar")}: {ex.Message}");
        }
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

/// <summary>
/// Destaque temporario do rebaixo no desenho (grafico transiente, nao grava nada no DWG):
/// contorno amarelo da faixa sarjeta + meio-fio de S0 a S3. A espessura acompanha o zoom (fica
/// sempre com o mesmo tamanho na tela, como um lineweight): e recalculada a cada mudanca de vista.
/// </summary>
internal static class MFRebaixoHighlight
{
    // Espessura do contorno = esta fracao da altura da vista (uns 4 px numa tela comum).
    private const double ScreenFraction = 0.0045;

    private static AcadDb.Polyline? _shown;
    private static Autodesk.AutoCAD.ApplicationServices.Document? _doc;

    public static void Clear()
    {
        if (_doc is not null)
        {
            try
            {
                _doc.ViewChanged -= OnViewChanged;
            }
            catch
            {
                // Documento ja fechado.
            }
            _doc = null;
        }
        if (_shown is null)
            return;
        try
        {
            Autodesk.AutoCAD.GraphicsInterface.TransientManager.CurrentTransientManager.EraseTransient(_shown, new IntegerCollection());
        }
        catch
        {
            // Vista ja fechada.
        }
        try
        {
            _shown.Dispose();
        }
        catch
        {
            // Ja liberado.
        }
        _shown = null;
    }

    /// <summary>Mostra o contorno da faixa (pontos internos na ida, externos na volta).</summary>
    public static void Show(IReadOnlyList<(double Station, Point3d Inner, Point3d Outer)> band)
    {
        if (band.Count < 2)
        {
            Clear();
            return;
        }
        var pts = new List<Point3d>();
        foreach ((_, Point3d inner, _) in band)
            pts.Add(inner);
        for (int i = band.Count - 1; i >= 0; i--)
            pts.Add(band[i].Outer);
        ShowLine(pts, true);
    }

    /// <summary>Mostra uma linha amarela (rebaixo sem faixa lida: fica no eixo dele).</summary>
    public static void ShowLine(IReadOnlyList<Point3d> points, bool closed)
    {
        Clear();
        if (points.Count < 2)
            return;
        var pl = new AcadDb.Polyline();
        int n = 0;
        foreach (Point3d p in points)
            pl.AddVertexAt(n++, new Point2d(p.X, p.Y), 0, 0, 0);
        pl.Closed = closed;
        pl.ConstantWidth = ScreenWidth();
        pl.ColorIndex = 2; // amarelo
        try
        {
            Autodesk.AutoCAD.GraphicsInterface.TransientManager.CurrentTransientManager.AddTransient(
                pl, Autodesk.AutoCAD.GraphicsInterface.TransientDrawingMode.DirectTopmost, 128, new IntegerCollection());
            _shown = pl;
            _doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (_doc is not null)
                _doc.ViewChanged += OnViewChanged;
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso("MFREBAIXO", "Destaque no desenho nao mostrado: " + ex.Message);
            pl.Dispose();
        }
    }

    /// <summary>Largura em unidades do desenho que da a mesma espessura na tela no zoom atual.</summary>
    private static double ScreenWidth()
    {
        try
        {
            double viewSize = Convert.ToDouble(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("VIEWSIZE"),
                System.Globalization.CultureInfo.InvariantCulture);
            if (viewSize > 0)
                return viewSize * ScreenFraction;
        }
        catch
        {
            // Sem vista ativa.
        }
        return 0.08;
    }

    private static void OnViewChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_shown is null)
                return;
            double width = ScreenWidth();
            if (Math.Abs(width - _shown.ConstantWidth) < width * 0.02)
                return;
            _shown.ConstantWidth = width;
            Autodesk.AutoCAD.GraphicsInterface.TransientManager.CurrentTransientManager.UpdateTransient(_shown, new IntegerCollection());
        }
        catch
        {
            // O destaque e so visual: nunca atrapalha o zoom.
        }
    }
}
