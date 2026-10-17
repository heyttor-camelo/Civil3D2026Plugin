using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;

namespace Civil3D2026Plugin.Modules.Secao;

/// <summary>Conteudo 3D da caixa de corte (C# puro: malhas, tubos, arestas e pontos em coordenadas do desenho, ja recortados na caixa).</summary>
internal sealed class Scene3D
{
    public sealed class Mesh
    {
        public string Name = "";
        public ItemKind Kind;
        public int Aci;
        public List<P3> Pos = new();
        public List<int> Tri = new();
        public string Key => Kind + "|" + Name;
    }

    public sealed class Tube
    {
        public string Name = "";
        public ItemKind Kind;
        public int Aci;
        public List<P3> Pts = new();
        public double Radius;
        public string Key => Kind + "|" + Name;
    }

    /// <summary>Arestas (Brep) de solidos e blocos, recortadas; aparecem nos estilos com arestas.</summary>
    public sealed class Wire
    {
        public string Name = "";
        public ItemKind Kind;
        public int Aci;
        public List<P3> Pts = new();
        public string Key => Kind + "|" + Name;
    }

    public List<Mesh> Meshes = new();
    public List<Tube> Tubes = new();
    public List<Wire> Wires = new();
    public List<(P3 P, string Label)> Points = new();
    public CutBox Box = new();
    public double ZMin, ZMax;
    public TimeSpan Elapsed;
    public List<string> Warnings = new();

    public const string PointsKey = "Ponto|Pontos COGO";

    public int Triangles => Meshes.Sum(m => m.Tri.Count / 3);

    public string Summary() =>
        $"{Meshes.Count(m => m.Kind == ItemKind.Superficie)} superficie(s), {Meshes.Count(m => m.Kind == ItemKind.Corredor)} corredor(es), " +
        $"{Meshes.Count(m => m.Kind is ItemKind.Solido or ItemKind.Bloco)} solido(s)/bloco(s), " +
        $"{Tubes.Count} linha(s)/tubo(s), {Points.Count} ponto(s), {Triangles} triangulos, {Elapsed.TotalMilliseconds:0} ms";

    /// <summary>Objetos da cena (para a lista e para mostrar/ocultar): chave, tipo, nome e tamanho.</summary>
    public List<(string Key, ItemKind Kind, string Name, string Info)> Objects()
    {
        List<(string, ItemKind, string, string)> list = new();
        foreach (IGrouping<string, Mesh> g in Meshes.GroupBy(m => m.Key))
            list.Add((g.Key, g.First().Kind, g.First().Name, g.Sum(m => m.Tri.Count / 3) + " triangulos"));
        foreach (IGrouping<string, Tube> g in Tubes.GroupBy(t => t.Key))
        {
            if (!list.Any(o => o.Item1 == g.Key))
                list.Add((g.Key, g.First().Kind, g.First().Name, g.Sum(t => t.Pts.Count - 1) + " trecho(s)"));
        }

        if (Points.Count > 0)
            list.Add((PointsKey, ItemKind.Ponto, "Pontos COGO", Points.Count + " ponto(s)"));
        return list;
    }
}

internal static partial class SecaoReader
{
    /// <summary>Teto de triangulos de solidos/blocos na cena (o resto vira aviso).</summary>
    private const int MaxSolidTriangles = 400000;

    /// <summary>
    /// Conteudo 3D da caixa, recortado exatamente nela (laterais, juncoes e, se fixada, a
    /// faixa de elevacao): superficies numa grade, corredor (fitas entre estacas), solidos,
    /// corpos, malhas e blocos (malha do Brep + arestas), tubos, estruturas, linhas 3D e
    /// pontos. Usa os mesmos caches do corte (corredores, extents, malhas).
    /// </summary>
    public static Scene3D Build3D(AcadDb.Database db, CutBox box, SecaoStore.Prefs prefs, List<CorridorCache> corridors, double zmin, double zmax)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Scene3D scene = new() { Box = box, ZMin = zmin, ZMax = zmax };
        if (!box.IsValid)
            return scene;

        BoxClipper clip = new(box, box.ZMin, box.ZMax);
        (double minX, double minY, double maxX, double maxY) = box.Bounds(1.0);
        ExtentsCache cache = CacheFor(db);
        Dictionary<AcadDb.ObjectId, int> layerColors = new();
        int solidTriangles = 0;

        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);

        void AddMesh(string name, ItemKind kind, int color, IReadOnlyList<P3> pos, IReadOnlyList<int> tri)
        {
            (List<P3> cp, List<int> ct) = clip.ClipMesh(pos, tri);
            if (ct.Count > 0)
                scene.Meshes.Add(new Scene3D.Mesh { Name = name, Kind = kind, Aci = color, Pos = cp, Tri = ct });
        }

        void AddLines(string name, ItemKind kind, int color, IReadOnlyList<P3> pts, double radius = 0)
        {
            foreach (List<P3> run in clip.ClipPolyline(pts))
                scene.Tubes.Add(new Scene3D.Tube { Name = name, Kind = kind, Aci = color, Pts = run, Radius = radius });
        }

        void AddSolid(MeshGeom g, ItemKind kind, int color)
        {
            if (!g.Overlaps(minX, minY, maxX, maxY))
                return;
            if (solidTriangles + g.Triangles > MaxSolidTriangles)
            {
                const string w = "Limite de triangulos de solidos/blocos atingido: alguns ficaram fora da vista 3D.";
                if (!scene.Warnings.Contains(w))
                    scene.Warnings.Add(w);
                return;
            }

            solidTriangles += g.Triangles;
            AddMesh(g.Name, kind, color, g.Tri, Enumerable.Range(0, g.Tri.Count).ToList());
            foreach (List<P3> e in g.Edges)
            {
                foreach (List<P3> run in clip.ClipPolyline(e))
                    scene.Wires.Add(new Scene3D.Wire { Name = g.Name, Kind = kind, Aci = color, Pts = run });
            }
        }

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

                if (!Classes.Contains(dxf) && !dxf.StartsWith("AECC_", StringComparison.OrdinalIgnoreCase) && !IsAcadSurface(id))
                {
                    cache.Kinds[id] = "";
                    continue;
                }
            }

            try
            {
                if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Entity ent)
                {
                    cache.Kinds[id] = "";
                    continue;
                }

                string kind = KindOf(ent);
                cache.Kinds[id] = kind;
                if (kind.Length == 0)
                    continue;
                if (!cache.Boxes.ContainsKey(id))
                    cache.Boxes[id] = ent is CivilDb.Corridor ? null : BoxOf(ent);
                if (cache.Boxes[id] is (double ex0, double ey0, double ex1, double ey1) && (ex1 < minX || ex0 > maxX || ey1 < minY || ey0 > maxY))
                    continue;

                int color = LayerColor(tr, ent, layerColors);
                switch (ent)
                {
                    case CivilDb.Surface surface when prefs.Superficies && !prefs.SuperficiesOcultas.Contains(surface.Name):
                        if (SampleSurface(surface, box) is Scene3D.Mesh mesh)
                            AddMesh(mesh.Name, ItemKind.Superficie, color, mesh.Pos, mesh.Tri);
                        break;
                    case CivilDb.Corridor corridor when prefs.Corredores:
                        CorridorCache cc = corridors.FirstOrDefault(c => c.Id == id) ?? ReadCorridor(tr, corridor);
                        if (!corridors.Contains(cc))
                            corridors.Add(cc);
                        Scene3D.Mesh cm = CorridorMesh(cc, box);
                        if (cm.Tri.Count > 0)
                            AddMesh(cm.Name, ItemKind.Corredor, color, cm.Pos, cm.Tri);
                        break;
                    case CivilDb.Pipe pipe when prefs.Tubos:
                        Point3d a = pipe.StartPoint, b = pipe.EndPoint;
                        AddLines(pipe.Name, ItemKind.Tubo, color, new List<P3> { new(a.X, a.Y, a.Z), new(b.X, b.Y, b.Z) }, pipe.OuterDiameterOrWidth / 2.0);
                        break;
                    case CivilDb.Structure st when prefs.Tubos:
                        Point3d pos = st.Position;
                        AddLines(st.Name, ItemKind.Estrutura, color, new List<P3> { new(pos.X, pos.Y, st.SumpElevation), new(pos.X, pos.Y, st.RimElevation) }, st.InnerDiameterOrWidth / 2.0);
                        break;
                    case CivilDb.FeatureLine fl when prefs.FeatureLines:
                        AddLines(fl.Name.Length > 0 ? fl.Name : "Feature line", ItemKind.FeatureLine, color, Points(fl));
                        break;
                    case AcadDb.Polyline3d p3 when prefs.Polilinhas3d:
                        AddLines("Polilinha 3D", ItemKind.Polilinha3d, color, Points(tr, p3));
                        break;
                    case CivilDb.CogoPoint cogo when prefs.Pontos:
                        Point3d loc = cogo.Location;
                        P3 lp = new(loc.X, loc.Y, loc.Z);
                        if (clip.Contains(lp))
                            scene.Points.Add((lp, cogo.PointNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        break;
                    case AcadDb.Solid3d or AcadDb.Body or AcadDb.SubDMesh or AcadDb.Surface or AcadDb.PolyFaceMesh or AcadDb.Face when prefs.Solidos:
                        AddSolid(MeshOf(tr, cache, ent), ItemKind.Solido, color);
                        break;
                    case AcadDb.BlockReference br when prefs.Blocos:
                        MeshGeom bm = MeshOf(tr, cache, br);
                        if (!bm.IsEmpty)
                            AddSolid(bm, ItemKind.Bloco, color);
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

    /// <summary>Fitas do corredor entre estacas vizinhas (quadrilateros perto da caixa; o recorte exato vem depois).</summary>
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
                            double qx0 = Math.Min(Math.Min(q0.X, q1.X), Math.Min(q2.X, q3.X)), qx1 = Math.Max(Math.Max(q0.X, q1.X), Math.Max(q2.X, q3.X));
                            double qy0 = Math.Min(Math.Min(q0.Y, q1.Y), Math.Min(q2.Y, q3.Y)), qy1 = Math.Max(Math.Max(q0.Y, q1.Y), Math.Max(q2.Y, q3.Y));
                            if (qx1 < bminX || qx0 > bmaxX || qy1 < bminY || qy0 > bmaxY)
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
}
