using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;

namespace Civil3D2026Plugin.Modules.Secao;

/// <summary>Conteudo 3D da caixa de corte (C# puro: malhas, tubos e pontos em coordenadas do desenho).</summary>
internal sealed class Scene3D
{
    public sealed class Mesh
    {
        public string Name = "";
        public ItemKind Kind;
        public int Aci;
        public List<P3> Pos = new();
        public List<int> Tri = new();
    }

    public sealed class Tube
    {
        public string Name = "";
        public ItemKind Kind;
        public int Aci;
        public List<P3> Pts = new();
        public double Radius;
    }

    public List<Mesh> Meshes = new();
    public List<Tube> Tubes = new();
    public List<(P3 P, string Label)> Points = new();
    public CutBox Box = new();
    public double ZMin, ZMax;
    public TimeSpan Elapsed;
    public List<string> Warnings = new();

    public int Triangles => Meshes.Sum(m => m.Tri.Count / 3);

    public string Summary() =>
        $"{Meshes.Count(m => m.Kind == ItemKind.Superficie)} superficie(s), {Meshes.Count(m => m.Kind == ItemKind.Corredor)} corredor(es), " +
        $"{Tubes.Count} linha(s)/tubo(s), {Points.Count} ponto(s), {Triangles} triangulos, {Elapsed.TotalMilliseconds:0} ms";
}

internal static partial class SecaoReader
{
    /// <summary>
    /// Conteudo 3D da caixa: superficies amostradas numa grade (linhas paralelas ao corte),
    /// corredor (fitas entre estacas que tocam a caixa), tubos, estruturas, linhas 3D e pontos.
    /// Usa os mesmos caches do corte (corredores e extents), entao abre poucos objetos.
    /// </summary>
    public static Scene3D Build3D(AcadDb.Database db, CutBox box, SecaoStore.Prefs prefs, List<CorridorCache> corridors, double zmin, double zmax)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Scene3D scene = new() { Box = box, ZMin = zmin, ZMax = zmax };
        if (!box.IsValid)
            return scene;

        (double minX, double minY, double maxX, double maxY) = box.Bounds(1.0);
        ExtentsCache cache = CacheFor(db);
        Dictionary<AcadDb.ObjectId, int> layerColors = new();

        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);

        foreach (AcadDb.ObjectId id in ms)
        {
            if (id.IsErased)
                continue;
            if (cache.Kinds.TryGetValue(id, out string? known))
            {
                if (known.Length == 0)
                    continue;
                if (cache.Boxes.TryGetValue(id, out (double MinX, double MinY, double MaxX, double MaxY)? kb) && kb is (double x0, double y0, double x1, double y1) &&
                    (x1 < minX || x0 > maxX || y1 < minY || y0 > maxY))
                {
                    continue;
                }
            }
            else
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

                if (!dxf.StartsWith("AECC_", StringComparison.OrdinalIgnoreCase) && dxf != "3DSOLID" && dxf != "POLYLINE")
                {
                    cache.Kinds[id] = "";
                    continue;
                }
            }

            try
            {
                if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Entity ent)
                    continue;
                if (!cache.Boxes.ContainsKey(id))
                    cache.Boxes[id] = ent is CivilDb.Corridor ? null : BoxOf(ent);
                if (cache.Boxes[id] is (double ex0, double ey0, double ex1, double ey1) && (ex1 < minX || ex0 > maxX || ey1 < minY || ey0 > maxY))
                    continue;

                int color = LayerColor(tr, ent, layerColors);
                switch (ent)
                {
                    case CivilDb.Surface surface when prefs.Superficies && !prefs.SuperficiesOcultas.Contains(surface.Name):
                        cache.Kinds[id] = "superficie(s)";
                        Scene3D.Mesh? mesh = SampleSurface(surface, box);
                        if (mesh is not null)
                        {
                            mesh.Aci = color;
                            scene.Meshes.Add(mesh);
                        }

                        break;
                    case CivilDb.Corridor corridor when prefs.Corredores:
                        cache.Kinds[id] = "corredor(es)";
                        CorridorCache cc = corridors.FirstOrDefault(c => c.Id == id) ?? ReadCorridor(tr, corridor);
                        if (!corridors.Contains(cc))
                            corridors.Add(cc);
                        Scene3D.Mesh cm = CorridorMesh(cc, box);
                        if (cm.Tri.Count > 0)
                        {
                            cm.Aci = color;
                            scene.Meshes.Add(cm);
                        }

                        break;
                    case CivilDb.Pipe pipe when prefs.Tubos:
                        cache.Kinds[id] = "tubo(s)";
                        Point3d a = pipe.StartPoint, b = pipe.EndPoint;
                        foreach (List<P3> run in InsideRuns(box, new List<P3> { new(a.X, a.Y, a.Z), new(b.X, b.Y, b.Z) }, 0.5))
                            scene.Tubes.Add(new Scene3D.Tube { Name = pipe.Name, Kind = ItemKind.Tubo, Aci = color, Pts = run, Radius = pipe.OuterDiameterOrWidth / 2.0 });
                        break;
                    case CivilDb.Structure st when prefs.Tubos:
                        cache.Kinds[id] = "estrutura(s)";
                        Point3d pos = st.Position;
                        if (box.Contains(new P2(pos.X, pos.Y)))
                        {
                            scene.Tubes.Add(new Scene3D.Tube
                            {
                                Name = st.Name, Kind = ItemKind.Estrutura, Aci = color, Radius = st.InnerDiameterOrWidth / 2.0,
                                Pts = new List<P3> { new(pos.X, pos.Y, st.SumpElevation), new(pos.X, pos.Y, st.RimElevation) }
                            });
                        }

                        break;
                    case CivilDb.FeatureLine fl when prefs.FeatureLines:
                        cache.Kinds[id] = "feature line(s)";
                        foreach (List<P3> run in InsideRuns(box, Points(fl), 1.0))
                            scene.Tubes.Add(new Scene3D.Tube { Name = fl.Name, Kind = ItemKind.FeatureLine, Aci = color, Pts = run });
                        break;
                    case AcadDb.Polyline3d p3 when prefs.Polilinhas3d:
                        cache.Kinds[id] = "polilinha(s) 3D";
                        foreach (List<P3> run in InsideRuns(box, Points(tr, p3), 1.0))
                            scene.Tubes.Add(new Scene3D.Tube { Name = "Polilinha 3D", Kind = ItemKind.Polilinha3d, Aci = color, Pts = run });
                        break;
                    case CivilDb.CogoPoint cogo when prefs.Pontos:
                        cache.Kinds[id] = "ponto(s) COGO";
                        Point3d loc = cogo.Location;
                        if (box.Contains(new P2(loc.X, loc.Y)))
                            scene.Points.Add((new P3(loc.X, loc.Y, loc.Z), cogo.PointNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        break;
                    case AcadDb.Solid3d when prefs.Solidos:
                        cache.Kinds[id] = "solido(s)";
                        if (!scene.Warnings.Contains("Solidos 3D ainda nao entram na vista 3D (so no corte)."))
                            scene.Warnings.Add("Solidos 3D ainda nao entram na vista 3D (so no corte).");
                        break;
                }
            }
            catch (System.Exception ex)
            {
                scene.Warnings.Add($"{id.Handle}: {ex.Message}");
            }
        }

        scene.Elapsed = sw.Elapsed;
        return scene;
    }

    /// <summary>
    /// Superficie numa grade alinhada a caixa (um bloco por trecho da linha). TIN: uma
    /// amostragem por linha da grade (SampleElevations) reinterpolada nas colunas; as
    /// demais, FindElevationAtXY numa grade menor.
    /// </summary>
    private static Scene3D.Mesh? SampleSurface(CivilDb.Surface surface, CutBox box)
    {
        Scene3D.Mesh mesh = new() { Name = surface.Name, Kind = ItemKind.Superficie };
        double width = box.Frente + box.Atras;
        if (width < 1e-6)
            return null;
        bool tin = surface is CivilDb.TinSurface;
        int budget = tin ? 6000 : 1600;

        for (int i = 0; i + 1 < box.Line.Count; i++)
        {
            P2 a = box.Line[i], b = box.Line[i + 1];
            double len = P2.Dist(a, b);
            if (len < 1e-6)
                continue;
            P2 t = (b - a).Unit(), n = t.Left;
            double step = Math.Max(0.25, Math.Sqrt(len * width / budget));
            int nu = Math.Clamp((int)Math.Ceiling(len / step), 2, 200);
            int nv = Math.Clamp((int)Math.Ceiling(width / step), 2, 80);
            double[,] z = new double[nv + 1, nu + 1];

            for (int k = 0; k <= nv; k++)
            {
                double v = -box.Atras + (width * k / nv);
                P2 p0 = a + (n * v), p1 = b + (n * v);
                if (tin)
                {
                    List<(double U, double Z)> row = new();
                    try
                    {
                        Point3dCollection pts = ((CivilDb.TinSurface)surface).SampleElevations(new Point3d(p0.X, p0.Y, 0), new Point3d(p1.X, p1.Y, 0));
                        foreach (Point3d p in pts)
                            row.Add((P2.Dot(new P2(p.X, p.Y) - p0, t), p.Z));
                    }
                    catch
                    {
                        // linha fora da superficie
                    }

                    row.Sort((x, y) => x.U.CompareTo(y.U));
                    for (int j = 0; j <= nu; j++)
                        z[k, j] = Interp(row, len * j / nu);
                }
                else
                {
                    for (int j = 0; j <= nu; j++)
                    {
                        P2 q = p0 + (t * (len * j / nu));
                        try
                        {
                            z[k, j] = surface.FindElevationAtXY(q.X, q.Y);
                        }
                        catch
                        {
                            z[k, j] = double.NaN;
                        }
                    }
                }
            }

            int[,] index = new int[nv + 1, nu + 1];
            for (int k = 0; k <= nv; k++)
            {
                double v = -box.Atras + (width * k / nv);
                for (int j = 0; j <= nu; j++)
                {
                    if (double.IsNaN(z[k, j]))
                    {
                        index[k, j] = -1;
                        continue;
                    }

                    P2 q = a + (t * (len * j / nu)) + (n * v);
                    index[k, j] = mesh.Pos.Count;
                    mesh.Pos.Add(new P3(q.X, q.Y, z[k, j]));
                }
            }

            for (int k = 0; k < nv; k++)
            {
                for (int j = 0; j < nu; j++)
                {
                    int p00 = index[k, j], p01 = index[k, j + 1], p10 = index[k + 1, j], p11 = index[k + 1, j + 1];
                    if (p00 >= 0 && p01 >= 0 && p11 >= 0)
                        mesh.Tri.AddRange(new[] { p00, p01, p11 });
                    if (p00 >= 0 && p11 >= 0 && p10 >= 0)
                        mesh.Tri.AddRange(new[] { p00, p11, p10 });
                }
            }
        }

        return mesh.Tri.Count > 0 ? mesh : null;
    }

    /// <summary>Interpolacao linear numa linha amostrada (NaN fora das pontas).</summary>
    private static double Interp(List<(double U, double Z)> row, double u)
    {
        if (row.Count == 0 || u < row[0].U - 1e-6 || u > row[^1].U + 1e-6)
            return double.NaN;
        for (int i = 0; i + 1 < row.Count; i++)
        {
            if (u >= row[i].U - 1e-9 && u <= row[i + 1].U + 1e-9)
            {
                double du = row[i + 1].U - row[i].U;
                return du < 1e-12 ? row[i].Z : row[i].Z + ((row[i + 1].Z - row[i].Z) * (u - row[i].U) / du);
            }
        }

        return row.Count == 1 ? row[0].Z : double.NaN;
    }

    /// <summary>Fitas do corredor entre estacas vizinhas, so os quadrilateros que tocam a caixa.</summary>
    private static Scene3D.Mesh CorridorMesh(CorridorCache corridor, CutBox box)
    {
        Scene3D.Mesh mesh = new() { Name = corridor.Name, Kind = ItemKind.Corredor };
        (double bminX, double bminY, double bmaxX, double bmaxY) = box.Bounds(1.0);
        foreach (RegionCache region in corridor.Regions)
        {
            List<AssemblyCache> list = region.Assemblies;
            for (int i = 0; i + 1 < list.Count; i++)
            {
                AssemblyCache a = list[i], b = list[i + 1];
                if (Math.Max(a.MaxX, b.MaxX) < bminX || Math.Min(a.MinX, b.MinX) > bmaxX || Math.Max(a.MaxY, b.MaxY) < bminY || Math.Min(a.MinY, b.MinY) > bmaxY)
                    continue;
                int subs = Math.Min(a.Subassemblies.Count, b.Subassemblies.Count);
                for (int si = 0; si < subs; si++)
                {
                    List<LinkCache> la = a.Subassemblies[si], lb = b.Subassemblies[si];
                    for (int li = 0; li < Math.Min(la.Count, lb.Count); li++)
                    {
                        List<P3> pa = la[li].Points, pb = lb[li].Points;
                        for (int k = 0; k + 1 < Math.Min(pa.Count, pb.Count); k++)
                        {
                            P3 q0 = pa[k], q1 = pa[k + 1], q2 = pb[k + 1], q3 = pb[k];
                            if (!box.Contains(q0.XY) && !box.Contains(q1.XY) && !box.Contains(q2.XY) && !box.Contains(q3.XY))
                                continue;
                            int baseIndex = mesh.Pos.Count;
                            mesh.Pos.AddRange(new[] { q0, q1, q2, q3 });
                            mesh.Tri.AddRange(new[] { baseIndex, baseIndex + 1, baseIndex + 2, baseIndex, baseIndex + 2, baseIndex + 3 });
                        }
                    }
                }
            }
        }

        return mesh;
    }

    /// <summary>Trechos de uma polilinha 3D que ficam dentro da caixa (subdividida para recortar nas bordas).</summary>
    private static List<List<P3>> InsideRuns(CutBox box, List<P3> pts, double step)
    {
        List<List<P3>> runs = new();
        List<P3> cur = new();
        foreach (P3 p in Subdivide(pts, step))
        {
            if (box.Contains(p.XY))
            {
                cur.Add(p);
            }
            else if (cur.Count > 0)
            {
                if (cur.Count > 1)
                    runs.Add(cur);
                cur = new List<P3>();
            }
        }

        if (cur.Count > 1)
            runs.Add(cur);
        return runs;
    }
}
