using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;

namespace Civil3D2026Plugin.Modules.Secao;

/// <summary>
/// Le o desenho e leva ao plano do corte tudo o que a caixa atravessa ou contem:
/// superficies (amostradas ao longo da linha), corredores (links interpolados entre
/// estacas vizinhas; estacas dentro da caixa como fantasma), tubos e estruturas,
/// solidos 3D (secao pelo plano), feature lines, polilinhas 3D e pontos COGO.
/// </summary>
internal static class SecaoReader
{
    private static readonly HashSet<string> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AECC_TIN_SURFACE", "AECC_GRID_SURFACE", "AECC_TIN_VOLUME_SURFACE", "AECC_GRID_VOLUME_SURFACE",
        "AECC_CORRIDOR", "AECC_PIPE", "AECC_STRUCTURE", "AECC_FEATURE_LINE", "AECC_COGO_POINT",
        "3DSOLID", "POLYLINE"
    };

    /// <summary>Corredor ja lido em memoria (estacas com links), para a vista por estaca e para reuso.</summary>
    public sealed class CorridorCache
    {
        public string Name = "";
        public AcadDb.ObjectId Id;
        public List<RegionCache> Regions = new();
    }

    public sealed class RegionCache
    {
        public string Baseline = "";
        public string Region = "";
        public double StartStation, EndStation;
        public List<AssemblyCache> Assemblies = new();
    }

    public sealed class AssemblyCache
    {
        public double Station;
        public P2 Origin;
        public double MinX, MinY, MaxX, MaxY;
        /// <summary>Links por subassembly (ordem da aplicacao).</summary>
        public List<List<LinkCache>> Subassemblies = new();
        public List<string> SubassemblyNames = new();
    }

    public sealed class LinkCache
    {
        public List<P3> Points = new();
        public string Codes = "";
    }

    /// <summary>Monta a secao da caixa a partir do desenho ativo.</summary>
    public static SectionData Build(AcadDb.Database db, CutBox box, SecaoStore.Prefs prefs, List<CorridorCache>? corridors = null)
    {
        Stopwatch sw = Stopwatch.StartNew();
        SectionData data = new() { Box = box };
        if (!box.IsValid)
            return data;

        (double minX, double minY, double maxX, double maxY) = box.Bounds(60.0);

        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        Dictionary<AcadDb.ObjectId, int> layerColors = new();

        foreach (AcadDb.ObjectId id in ms)
        {
            string dxf;
            try
            {
                dxf = id.ObjectClass.DxfName;
            }
            catch
            {
                continue;
            }

            if (!Classes.Contains(dxf))
                continue;

            try
            {
                if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Entity ent)
                    continue;

                // Filtro rapido pela caixa envolvente (superficies e corredores sao grandes: entram sempre).
                if (ent is not CivilDb.Surface && ent is not CivilDb.Corridor && !Near(ent, minX, minY, maxX, maxY))
                    continue;

                switch (ent)
                {
                    case CivilDb.Surface surface when prefs.Superficies && !prefs.SuperficiesOcultas.Contains(surface.Name):
                        ReadSurface(surface, box, data, LayerColor(tr, ent, layerColors));
                        break;
                    case CivilDb.Corridor corridor when prefs.Corredores:
                        CorridorCache cache = corridors?.FirstOrDefault(c => c.Id == id) ?? ReadCorridor(tr, corridor);
                        corridors?.RemoveAll(c => c.Id == id);
                        corridors?.Add(cache);
                        CutCorridor(cache, box, data, prefs.FantasmaCorredor, LayerColor(tr, ent, layerColors));
                        break;
                    case CivilDb.Pipe pipe when prefs.Tubos:
                        ReadPipe(pipe, box, data, prefs.FantasmaTubos, LayerColor(tr, ent, layerColors));
                        break;
                    case CivilDb.Structure structure when prefs.Tubos:
                        ReadStructure(structure, box, data, prefs.FantasmaTubos, LayerColor(tr, ent, layerColors));
                        break;
                    case CivilDb.FeatureLine fl when prefs.FeatureLines:
                        ReadLine(fl.Name.Length > 0 ? fl.Name : "Feature line", ItemKind.FeatureLine, Points(fl), fl.Layer, box, data, prefs.FantasmaLinhas, LayerColor(tr, ent, layerColors));
                        break;
                    case AcadDb.Polyline3d p3 when prefs.Polilinhas3d:
                        ReadLine("Polilinha 3D", ItemKind.Polilinha3d, Points(tr, p3), p3.Layer, box, data, prefs.FantasmaLinhas, LayerColor(tr, ent, layerColors));
                        break;
                    case CivilDb.CogoPoint cogo when prefs.Pontos:
                        ReadCogo(cogo, box, data, prefs.FantasmaPontos);
                        break;
                    case AcadDb.Solid3d solid when prefs.Solidos:
                        ReadSolid(solid, box, data, LayerColor(tr, ent, layerColors));
                        break;
                }
            }
            catch (System.Exception ex)
            {
                data.Warnings.Add($"{dxf} {id.Handle}: {ex.Message}");
            }
        }

        data.Items.RemoveAll(i => i.IsEmpty);
        // Cortados antes dos fantasmas; fantasmas mais longe primeiro (ficam atras).
        data.Items = data.Items.OrderBy(i => i.Ghost ? 1 : 0).ThenByDescending(i => Math.Abs(i.Depth)).ToList();
        data.Elapsed = sw.Elapsed;
        return data;
    }

    /// <summary>Le todos os corredores do desenho (para a categoria Corredores).</summary>
    public static List<CorridorCache> ReadCorridors(AcadDb.Database db)
    {
        List<CorridorCache> list = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (!id.ObjectClass.DxfName.Equals("AECC_CORRIDOR", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Corridor corridor)
                    list.Add(ReadCorridor(tr, corridor));
            }
            catch
            {
                // Corredor sem secoes calculadas.
            }
        }

        return list;
    }

    private static bool Near(AcadDb.Entity ent, double minX, double minY, double maxX, double maxY)
    {
        try
        {
            AcadDb.Extents3d ext = ent.GeometricExtents;
            return ext.MaxPoint.X >= minX && ext.MinPoint.X <= maxX && ext.MaxPoint.Y >= minY && ext.MinPoint.Y <= maxY;
        }
        catch
        {
            return true;
        }
    }

    private static int LayerColor(AcadDb.Transaction tr, AcadDb.Entity ent, Dictionary<AcadDb.ObjectId, int> cache)
    {
        try
        {
            int ci = ent.ColorIndex;
            if (ci > 0 && ci < 256)
                return ci;
            if (!cache.TryGetValue(ent.LayerId, out int lc))
            {
                lc = tr.GetObject(ent.LayerId, AcadDb.OpenMode.ForRead) is AcadDb.LayerTableRecord l ? l.Color.ColorIndex : 7;
                cache[ent.LayerId] = lc;
            }

            return lc;
        }
        catch
        {
            return 7;
        }
    }

    // ------------------------------------------------------------------
    // Superficies
    // ------------------------------------------------------------------

    private static void ReadSurface(CivilDb.Surface surface, CutBox box, SectionData data, int color)
    {
        SectionItem item = new() { Kind = ItemKind.Superficie, Name = surface.Name, Layer = surface.Layer, ColorIndex = color };
        double[] starts = box.Starts();
        for (int i = 0; i + 1 < box.Line.Count; i++)
        {
            P2 a = box.Line[i], b = box.Line[i + 1];
            List<(double S, double Z)> pl = new();
            P2 t = (b - a).Unit();
            Point3dCollection? pts = null;
            // SampleElevations fica na interface ITerrainSurface (TinSurface, GridSurface e as de volume), nao na classe Surface.
            if (surface is CivilDb.ITerrainSurface terrain)
            {
                try
                {
                    pts = terrain.SampleElevations(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0));
                }
                catch
                {
                    pts = null; // fora da superficie ou tipo sem amostragem
                }
            }

            if (pts is not null)
            {
                foreach (Point3d p in pts)
                    pl.Add((starts[i] + P2.Dot(new P2(p.X, p.Y) - a, t), p.Z));
            }
            else
            {
                // Outros tipos de superficie: amostra a cada 0,5 m com FindElevationAtXY (fora da superficie lanca).
                double len = P2.Dist(a, b);
                int n = Math.Clamp((int)Math.Ceiling(len / 0.5), 1, 4000);
                for (int k = 0; k <= n; k++)
                {
                    P2 q = a + (t * (len * k / n));
                    try
                    {
                        pl.Add((starts[i] + (len * k / n), surface.FindElevationAtXY(q.X, q.Y)));
                    }
                    catch
                    {
                        if (pl.Count > 1)
                            item.Polylines.Add(pl);
                        pl = new List<(double S, double Z)>();
                    }
                }
            }

            if (pl.Count > 1)
                item.Polylines.Add(pl);
        }

        item.Info["Tipo"] = surface.GetType().Name;
        data.Items.Add(item);
    }

    // ------------------------------------------------------------------
    // Corredores
    // ------------------------------------------------------------------

    private static CorridorCache ReadCorridor(AcadDb.Transaction tr, CivilDb.Corridor corridor)
    {
        CorridorCache cache = new() { Name = corridor.Name, Id = corridor.ObjectId };
        Dictionary<AcadDb.ObjectId, string> subNames = new();
        foreach (CivilDb.Baseline baseline in corridor.Baselines)
        {
            foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
            {
                RegionCache rc = new()
                {
                    Baseline = baseline.Name,
                    Region = region.Name,
                    StartStation = region.StartStation,
                    EndStation = region.EndStation
                };

                foreach (CivilDb.AppliedAssembly applied in region.AppliedAssemblies)
                {
                    AssemblyCache ac = new() { MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue };
                    bool first = true;
                    foreach (CivilDb.AppliedSubassembly sub in applied.GetAppliedSubassemblies())
                    {
                        if (first)
                        {
                            Point3d soe = sub.OriginStationOffsetElevationToBaseline;
                            ac.Station = soe.X;
                            first = false;
                        }

                        List<LinkCache> links = new();
                        foreach (CivilDb.CalculatedLink link in sub.Links)
                        {
                            LinkCache lc = new();
                            foreach (CivilDb.CalculatedPoint cp in link.CalculatedPoints)
                            {
                                Point3d xyz = cp.XYZ;
                                lc.Points.Add(new P3(xyz.X, xyz.Y, xyz.Z));
                                ac.MinX = Math.Min(ac.MinX, xyz.X); ac.MaxX = Math.Max(ac.MaxX, xyz.X);
                                ac.MinY = Math.Min(ac.MinY, xyz.Y); ac.MaxY = Math.Max(ac.MaxY, xyz.Y);
                            }

                            try
                            {
                                lc.Codes = string.Join(",", link.CorridorCodes.Cast<string>());
                            }
                            catch
                            {
                                // sem codigos
                            }

                            links.Add(lc);
                        }

                        ac.Subassemblies.Add(links);
                        string name = "";
                        try
                        {
                            if (!subNames.TryGetValue(sub.SubassemblyId, out name!))
                            {
                                name = tr.GetObject(sub.SubassemblyId, AcadDb.OpenMode.ForRead) is CivilDb.Subassembly s ? s.Name : "";
                                subNames[sub.SubassemblyId] = name;
                            }
                        }
                        catch
                        {
                            name = "";
                        }

                        ac.SubassemblyNames.Add(name);
                    }

                    if (ac.Subassemblies.Count == 0 || ac.MinX > ac.MaxX)
                        continue;
                    // Origem: primeiro ponto do primeiro link (perto do eixo).
                    P3? o = ac.Subassemblies.SelectMany(s => s).SelectMany(l => l.Points).Cast<P3?>().FirstOrDefault();
                    ac.Origin = o is P3 op ? op.XY : new P2((ac.MinX + ac.MaxX) / 2, (ac.MinY + ac.MaxY) / 2);
                    rc.Assemblies.Add(ac);
                }

                rc.Assemblies.Sort((x, y) => x.Station.CompareTo(y.Station));
                if (rc.Assemblies.Count > 0)
                    cache.Regions.Add(rc);
            }
        }

        return cache;
    }

    /// <summary>Corte do corredor: fitas entre estacas vizinhas; estacas dentro da caixa como fantasma.</summary>
    public static void CutCorridor(CorridorCache corridor, CutBox box, SectionData data, bool ghosts, int color)
    {
        double lminX = box.Line.Min(p => p.X), lmaxX = box.Line.Max(p => p.X);
        double lminY = box.Line.Min(p => p.Y), lmaxY = box.Line.Max(p => p.Y);
        (double bminX, double bminY, double bmaxX, double bmaxY) = box.Bounds();

        foreach (RegionCache region in corridor.Regions)
        {
            List<((double, double), (double, double))> segs = new();
            List<AssemblyCache> list = region.Assemblies;
            for (int i = 0; i + 1 < list.Count; i++)
            {
                AssemblyCache a = list[i], b = list[i + 1];
                double minX = Math.Min(a.MinX, b.MinX), maxX = Math.Max(a.MaxX, b.MaxX);
                double minY = Math.Min(a.MinY, b.MinY), maxY = Math.Max(a.MaxY, b.MaxY);
                if (maxX < lminX || minX > lmaxX || maxY < lminY || minY > lmaxY)
                    continue;

                int subs = Math.Min(a.Subassemblies.Count, b.Subassemblies.Count);
                for (int si = 0; si < subs; si++)
                {
                    List<LinkCache> la = a.Subassemblies[si], lb = b.Subassemblies[si];
                    int links = Math.Min(la.Count, lb.Count);
                    for (int li = 0; li < links; li++)
                        segs.AddRange(SecaoGeometry.CutRibbon(box, la[li].Points, lb[li].Points));
                }
            }

            if (segs.Count > 0)
            {
                SectionItem item = new()
                {
                    Kind = ItemKind.Corredor,
                    Name = corridor.Name + " / " + region.Baseline,
                    ColorIndex = color
                };
                item.Polylines.AddRange(SecaoGeometry.Chain(segs));
                item.Info["Regiao"] = region.Region;
                item.Info["Segmentos"] = segs.Count.ToString(CultureInfo.InvariantCulture);
                data.Items.Add(item);
            }

            if (!ghosts)
                continue;

            foreach (AssemblyCache ac in list)
            {
                if (ac.MaxX < bminX || ac.MinX > bmaxX || ac.MaxY < bminY || ac.MinY > bmaxY)
                    continue;
                (_, double d, bool inside) = box.Project(ac.Origin);
                if (!inside || d > box.Frente || d < -box.Atras)
                    continue;

                SectionItem ghost = new()
                {
                    Kind = ItemKind.Corredor,
                    Ghost = true,
                    Depth = d,
                    Name = $"{corridor.Name} est. {Station(ac.Station)}",
                    ColorIndex = color
                };
                for (int si = 0; si < ac.Subassemblies.Count; si++)
                {
                    foreach (LinkCache link in ac.Subassemblies[si])
                    {
                        foreach (List<(double S, double Z)> pl in SecaoGeometry.ProjectInside(box, link.Points, out _))
                            ghost.Polylines.Add(pl);
                    }
                }

                ghost.Info["Estaca"] = Station(ac.Station);
                ghost.Info["Afastamento"] = d.ToString("0.00", CultureInfo.InvariantCulture) + " m";
                ghost.Info["Subassemblies"] = string.Join(", ", ac.SubassemblyNames.Where(n => n.Length > 0).Distinct());
                data.Items.Add(ghost);
            }
        }
    }

    /// <summary>
    /// Secao "na estaca": a propria estaca do corredor vira a linha de corte (links da
    /// estaca em traco cheio) e as vizinhas dentro da profundidade, fantasmas.
    /// </summary>
    public static SectionData AtStation(AcadDb.Database db, CorridorCache corridor, RegionCache region, int index, SecaoStore.Prefs prefs, double frente, double atras, List<CorridorCache> corridors)
    {
        AssemblyCache ac = region.Assemblies[Math.Clamp(index, 0, region.Assemblies.Count - 1)];
        // Direcao da estaca: pela vizinha (tangente do eixo); a linha de corte e a perpendicular.
        P2 dir;
        if (index + 1 < region.Assemblies.Count)
            dir = (region.Assemblies[index + 1].Origin - ac.Origin).Unit();
        else if (index > 0)
            dir = (ac.Origin - region.Assemblies[index - 1].Origin).Unit();
        else
            dir = new P2(1, 0);
        // Largura: pelos pontos da estaca projetados na transversal, com folga.
        double lo = 0, hi = 0;
        foreach (P3 p in ac.Subassemblies.SelectMany(s => s).SelectMany(l => l.Points))
        {
            double u = P2.Dot(p.XY - ac.Origin, dir.Left);
            lo = Math.Min(lo, u);
            hi = Math.Max(hi, u);
        }

        lo -= 3; hi += 3;
        // Linha da esquerda para a direita de quem olha no sentido do eixo: comeca no lado esquerdo (Left*hi) e vai ao direito (Left*lo).
        CutBox box = new()
        {
            Name = $"{corridor.Name} est. {Station(ac.Station)}",
            Line = new List<P2> { ac.Origin + (dir.Left * hi), ac.Origin + (dir.Left * lo) },
            Frente = frente,
            Atras = atras,
            Exagero = prefs.ExageroPadrao
        };
        SectionData data = Build(db, box, prefs, corridors);
        // Os links desta estaca em traco cheio (substituem o corte interpolado do proprio corredor).
        data.Items.RemoveAll(i => i.Kind == ItemKind.Corredor && !i.Ghost && i.Name.StartsWith(corridor.Name + " /", StringComparison.Ordinal));
        data.Items.RemoveAll(i => i.Kind == ItemKind.Corredor && i.Ghost && Math.Abs(i.Depth) < 1e-6 && i.Name.StartsWith(corridor.Name, StringComparison.Ordinal));
        SectionItem own = new() { Kind = ItemKind.Corredor, Name = corridor.Name + " / " + region.Baseline, ColorIndex = 0 };
        for (int si = 0; si < ac.Subassemblies.Count; si++)
        {
            foreach (LinkCache link in ac.Subassemblies[si])
            {
                List<(double S, double Z)> pl = new();
                foreach (P3 p in link.Points)
                {
                    (double s, _, _) = box.Project(p.XY);
                    pl.Add((s, p.Z));
                }

                if (pl.Count > 1)
                    own.Polylines.Add(pl);
            }
        }

        own.Info["Estaca"] = Station(ac.Station);
        own.Info["Regiao"] = region.Region;
        own.Info["Subassemblies"] = string.Join(", ", ac.SubassemblyNames.Where(n => n.Length > 0).Distinct());
        data.Items.Insert(0, own);
        return data;
    }

    public static string Station(double s)
    {
        double k = Math.Floor(s / 20.0);
        double rem = s - (k * 20.0);
        if (rem > 19.995)
        {
            k += 1;
            rem = 0;
        }

        return k.ToString("0", CultureInfo.InvariantCulture) + "+" + rem.ToString("00.00", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // Tubos e estruturas
    // ------------------------------------------------------------------

    private static void ReadPipe(CivilDb.Pipe pipe, CutBox box, SectionData data, bool ghosts, int color)
    {
        Point3d a = pipe.StartPoint, b = pipe.EndPoint;
        P3 pa = new(a.X, a.Y, a.Z), pb = new(b.X, b.Y, b.Z);
        double ro = pipe.OuterDiameterOrWidth / 2.0, ri = pipe.InnerDiameterOrWidth / 2.0;
        string name = pipe.Name;
        bool cut = false;
        foreach ((double s, double z) in box.Cross(pa, pb))
        {
            SectionItem item = new() { Kind = ItemKind.Tubo, Name = name, Layer = pipe.Layer, ColorIndex = color };
            item.Circles.Add((s, z, ro));
            if (ri > 1e-6 && ri < ro)
                item.Circles.Add((s, z, ri));
            item.Info["Diametro"] = (ri * 2).ToString("0.000", CultureInfo.InvariantCulture) + " m";
            item.Info["Cota eixo"] = z.ToString("0.000", CultureInfo.InvariantCulture);
            data.Items.Add(item);
            cut = true;
        }

        if (cut || !ghosts)
            return;

        // Dentro da caixa sem cruzar: projeta o eixo e as geratrizes superior/inferior.
        List<P3> axis = new() { pa, pb };
        List<List<(double S, double Z)>> parts = SecaoGeometry.ProjectInside(box, Subdivide(axis, 1.0), out double depth);
        if (parts.Count == 0)
            return;
        SectionItem ghost = new() { Kind = ItemKind.Tubo, Name = name, Layer = pipe.Layer, Ghost = true, Depth = depth, ColorIndex = color };
        foreach (List<(double S, double Z)> pl in parts)
        {
            ghost.Polylines.Add(pl.Select(p => (p.S, p.Z + ro)).ToList());
            ghost.Polylines.Add(pl.Select(p => (p.S, p.Z - ro)).ToList());
        }

        ghost.Info["Afastamento"] = depth.ToString("0.00", CultureInfo.InvariantCulture) + " m";
        data.Items.Add(ghost);
    }

    private static void ReadStructure(CivilDb.Structure st, CutBox box, SectionData data, bool ghosts, int color)
    {
        Point3d pos = st.Position;
        (double s, double d, bool inside) = box.Project(new P2(pos.X, pos.Y));
        if (!inside || d > box.Frente || d < -box.Atras)
            return;
        double w = st.InnerDiameterOrWidth;
        bool ghost = Math.Abs(d) > w / 2.0;
        if (ghost && !ghosts)
            return;
        double rim = st.RimElevation, sump = st.SumpElevation;
        SectionItem item = new() { Kind = ItemKind.Estrutura, Name = st.Name, Layer = st.Layer, Ghost = ghost, Depth = d, ColorIndex = color };
        item.Polylines.Add(new List<(double, double)> { (s - w / 2, sump), (s - w / 2, rim), (s + w / 2, rim), (s + w / 2, sump), (s - w / 2, sump) });
        item.Info["Tampa"] = rim.ToString("0.000", CultureInfo.InvariantCulture);
        item.Info["Fundo"] = sump.ToString("0.000", CultureInfo.InvariantCulture);
        item.Info["Afastamento"] = d.ToString("0.00", CultureInfo.InvariantCulture) + " m";
        data.Items.Add(item);
    }

    // ------------------------------------------------------------------
    // Linhas e pontos
    // ------------------------------------------------------------------

    private static List<P3> Points(CivilDb.FeatureLine fl)
    {
        List<P3> pts = new();
        foreach (Point3d p in fl.GetPoints(FeatureLinePointType.AllPoints))
            pts.Add(new P3(p.X, p.Y, p.Z));
        return pts;
    }

    private static List<P3> Points(AcadDb.Transaction tr, AcadDb.Polyline3d pl)
    {
        List<P3> pts = new();
        foreach (AcadDb.ObjectId vid in pl)
        {
            if (tr.GetObject(vid, AcadDb.OpenMode.ForRead) is AcadDb.PolylineVertex3d v)
                pts.Add(new P3(v.Position.X, v.Position.Y, v.Position.Z));
        }

        if (pl.Closed && pts.Count > 1)
            pts.Add(pts[0]);
        return pts;
    }

    private static void ReadLine(string name, ItemKind kind, List<P3> pts, string layer, CutBox box, SectionData data, bool ghosts, int color)
    {
        if (pts.Count < 2)
            return;
        SectionItem cut = new() { Kind = kind, Name = name, Layer = layer, ColorIndex = color };
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            foreach ((double s, double z) in box.Cross(pts[i], pts[i + 1]))
                cut.Points.Add((s, z, name));
        }

        if (cut.Points.Count > 0)
            data.Items.Add(cut);

        if (!ghosts)
            return;
        List<List<(double S, double Z)>> parts = SecaoGeometry.ProjectInside(box, Subdivide(pts, 2.0), out double depth);
        if (parts.Count == 0)
            return;
        SectionItem ghost = new() { Kind = kind, Name = name, Layer = layer, Ghost = true, Depth = depth, ColorIndex = color };
        ghost.Polylines.AddRange(parts);
        ghost.Info["Afastamento medio"] = depth.ToString("0.00", CultureInfo.InvariantCulture) + " m";
        data.Items.Add(ghost);
    }

    private static void ReadCogo(CivilDb.CogoPoint cogo, CutBox box, SectionData data, bool ghosts)
    {
        if (!ghosts)
            return;
        Point3d p = cogo.Location;
        (double s, double d, bool inside) = box.Project(new P2(p.X, p.Y));
        if (!inside || d > box.Frente || d < -box.Atras)
            return;
        string label = cogo.PointNumber.ToString(CultureInfo.InvariantCulture);
        try
        {
            if (!string.IsNullOrWhiteSpace(cogo.RawDescription))
                label += " " + cogo.RawDescription;
        }
        catch
        {
            // sem descricao
        }

        SectionItem item = new() { Kind = ItemKind.Ponto, Name = "Ponto " + label, Ghost = Math.Abs(d) > 0.05, Depth = d };
        item.Points.Add((s, p.Z, label));
        item.Info["Afastamento"] = d.ToString("0.00", CultureInfo.InvariantCulture) + " m";
        data.Items.Add(item);
    }

    /// <summary>Subdivide trechos longos para a projecao acompanhar a entrada/saida da caixa.</summary>
    private static List<P3> Subdivide(List<P3> pts, double step)
    {
        List<P3> result = new();
        for (int i = 0; i < pts.Count; i++)
        {
            if (i > 0)
            {
                P3 a = pts[i - 1], b = pts[i];
                double len = P2.Dist(a.XY, b.XY);
                int n = (int)Math.Min(200, Math.Floor(len / step));
                for (int k = 1; k < n; k++)
                {
                    double u = (double)k / n;
                    result.Add(new P3(a.X + ((b.X - a.X) * u), a.Y + ((b.Y - a.Y) * u), a.Z + ((b.Z - a.Z) * u)));
                }
            }

            result.Add(pts[i]);
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Solidos
    // ------------------------------------------------------------------

    private static void ReadSolid(AcadDb.Solid3d solid, CutBox box, SectionData data, int color)
    {
        double[] starts = box.Starts();
        SectionItem item = new() { Kind = ItemKind.Solido, Name = "Solido " + solid.Handle, Layer = solid.Layer, ColorIndex = color };
        for (int i = 0; i + 1 < box.Line.Count; i++)
        {
            P2 a = box.Line[i], b = box.Line[i + 1];
            P2 n = (b - a).Unit().Left;
            double len = P2.Dist(a, b);
            AcadDb.Region? region;
            try
            {
                region = solid.GetSection(new Plane(new Point3d(a.X, a.Y, 0), new Vector3d(n.X, n.Y, 0)));
            }
            catch
            {
                region = null;
            }

            if (region is null)
                continue;

            using (region)
            {
                AcadDb.DBObjectCollection parts = new();
                try
                {
                    region.Explode(parts);
                }
                catch
                {
                    continue;
                }

                foreach (AcadDb.DBObject part in parts)
                {
                    using (part)
                    {
                        if (part is not AcadDb.Curve curve)
                            continue;
                        List<(double S, double Z)> pl = new();
                        double total;
                        try
                        {
                            total = curve.GetDistanceAtParameter(curve.EndParam);
                        }
                        catch
                        {
                            continue;
                        }

                        int steps = curve is AcadDb.Line ? 1 : Math.Clamp((int)Math.Ceiling(total / 0.2), 8, 200);
                        for (int k = 0; k <= steps; k++)
                        {
                            Point3d p;
                            try
                            {
                                p = curve.GetPointAtDist(total * k / steps);
                            }
                            catch
                            {
                                continue;
                            }

                            double s = P2.Dot(new P2(p.X, p.Y) - a, (b - a).Unit());
                            pl.Add((s, p.Z));
                        }

                        foreach (List<(double S, double Z)> clipped in SecaoGeometry.ClipS(pl, len))
                            item.Polylines.Add(clipped.Select(q => (q.S + starts[i], q.Z)).ToList());
                    }
                }
            }
        }

        if (item.Polylines.Count > 0)
        {
            item.Info["Handle"] = solid.Handle.ToString();
            data.Items.Add(item);
        }
    }
}
