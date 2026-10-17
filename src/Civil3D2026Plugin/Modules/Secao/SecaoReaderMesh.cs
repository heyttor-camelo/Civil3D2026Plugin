using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.Modules.CorridorTools.PlanVisibility;

namespace Civil3D2026Plugin.Modules.Secao;

internal static partial class SecaoReader
{
    /// <summary>Classes DXF de objetos 3D lidos como malha (alem do 3DSOLID e dos blocos).</summary>
    private static readonly HashSet<string> MeshClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "3DSOLID", "BODY", "MESH", "3DFACE", "POLYLINE",
        "PLANESURFACE", "EXTRUDEDSURFACE", "LOFTEDSURFACE", "REVOLVEDSURFACE", "SWEPTSURFACE", "NURBSURFACE", "NURBSSURFACE"
    };

    private static Autodesk.AutoCAD.Runtime.RXClass? _surfaceClass;

    /// <summary>Superficie do AutoCAD de qualquer tipo (nurbs, blend, patch, offset...): pela classe, nao pelo nome DXF.</summary>
    private static bool IsAcadSurface(AcadDb.ObjectId id)
    {
        try
        {
            _surfaceClass ??= Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AcadDb.Surface));
            return id.ObjectClass.IsDerivedFrom(_surfaceClass);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Objeto lido como malha (fora blocos): solido, corpo, superficie, malha SubD, polyface, 3D face.</summary>
    private static bool IsMeshEntity(AcadDb.Entity e) =>
        e is AcadDb.Solid3d or AcadDb.Body or AcadDb.Surface or AcadDb.SubDMesh or AcadDb.PolyFaceMesh or AcadDb.Face;

    /// <summary>Malha do objeto (cache por ObjectId); vazia se nao for 3D ou nao puder ser lida.</summary>
    private static MeshGeom MeshOf(AcadDb.Transaction tr, ExtentsCache cache, AcadDb.Entity ent)
    {
        if (cache.Meshes.TryGetValue(ent.ObjectId, out MeshGeom? g))
            return g;
        g = ent switch
        {
            AcadDb.BlockReference br => FromBlock(tr, cache, br),
            _ => FromEntity(tr, ent)
        } ?? new MeshGeom();
        if (g.Name.Length == 0)
            g.Name = ent switch
            {
                AcadDb.BlockReference br => "Bloco " + br.Name,
                AcadDb.Solid3d => "Solido " + ent.Handle,
                AcadDb.SubDMesh or AcadDb.PolyFaceMesh or AcadDb.Face => "Malha " + ent.Handle,
                AcadDb.Surface => "Superficie " + ent.Handle,
                _ => "Corpo " + ent.Handle
            };
        cache.Meshes[ent.ObjectId] = g;
        if (ent is AcadDb.BlockReference)
            cache.BlockRefs.Add(ent.ObjectId);
        return g;
    }

    private static MeshGeom? FromEntity(AcadDb.Transaction tr, AcadDb.Entity ent) => ent switch
    {
        AcadDb.SubDMesh sm => FromSubD(sm),
        AcadDb.PolyFaceMesh pf => FromPolyFace(tr, pf),
        AcadDb.Face f => FromFace(f),
        AcadDb.Solid3d or AcadDb.Body or AcadDb.Surface => FromBrep(ent),
        _ => null
    };

    private static MeshGeom? FromBrep(AcadDb.Entity ent)
    {
        (List<PlanHiddenLineEngine.Tri3> Triangles, List<Point3d[]> Edges)? m = PlanHiddenLineEngine.ExtractMesh(ent);
        if (m is not ({ } tris, { } edges))
            return null;
        MeshGeom g = new();
        foreach (PlanHiddenLineEngine.Tri3 t in tris)
        {
            foreach (Point3d p in new[] { t.A, t.B, t.C })
            {
                P3 q = new(p.X, p.Y, p.Z);
                g.Tri.Add(q);
                g.Grow(q);
            }
        }

        foreach (Point3d[] e in edges)
        {
            List<P3> pl = e.Select(p => new P3(p.X, p.Y, p.Z)).ToList();
            foreach (P3 q in pl)
                g.Grow(q);
            g.Edges.Add(pl);
        }

        g.ComputeFolds();
        return g;
    }

    private static MeshGeom? FromSubD(AcadDb.SubDMesh mesh)
    {
        Point3dCollection verts;
        Int32Collection faces;
        try
        {
            // Malha suavizada: a superficie vista e a subdividida, nao a gaiola de controle.
            bool smooth = mesh.SmoothLevel > 0;
            verts = smooth ? mesh.GetSubDividedVertices() : mesh.Vertices;
            faces = smooth ? mesh.GetSubDividedFaceArray() : mesh.FaceArray;
        }
        catch
        {
            verts = mesh.Vertices;
            faces = mesh.FaceArray;
        }

        List<P3> v = new();
        foreach (Point3d p in verts)
            v.Add(new P3(p.X, p.Y, p.Z));
        MeshGeom g = new();
        HashSet<(int, int)> seen = new();
        int i = 0;
        while (i < faces.Count)
        {
            int n = faces[i];
            if (n < 3 || i + n >= faces.Count)
                break;
            List<int> f = new();
            for (int k = 1; k <= n; k++)
                f.Add(faces[i + k]);
            i += n + 1;
            if (f.Count < 3 || f.Any(x => x < 0 || x >= v.Count))
                continue;
            for (int k = 1; k + 1 < f.Count; k++)
            {
                foreach (int idx in new[] { f[0], f[k], f[k + 1] })
                {
                    g.Tri.Add(v[idx]);
                    g.Grow(v[idx]);
                }
            }

            for (int k = 0; k < f.Count && g.Edges.Count < 20000; k++)
            {
                int a = f[k], b = f[(k + 1) % f.Count];
                if (seen.Add(a < b ? (a, b) : (b, a)))
                    g.Edges.Add(new List<P3> { v[a], v[b] });
            }
        }

        g.ComputeFolds();
        return g;
    }

    /// <summary>Polyface mesh (POLYLINE de faces, comum em blocos importados).</summary>
    private static MeshGeom? FromPolyFace(AcadDb.Transaction tr, AcadDb.PolyFaceMesh pf)
    {
        List<P3> v = new();
        List<short[]> faces = new();
        foreach (AcadDb.ObjectId vid in pf)
        {
            AcadDb.DBObject o = tr.GetObject(vid, AcadDb.OpenMode.ForRead);
            if (o is AcadDb.PolyFaceMeshVertex pv)
                v.Add(new P3(pv.Position.X, pv.Position.Y, pv.Position.Z));
            else if (o is AcadDb.FaceRecord fr)
                faces.Add(new[] { fr.GetVertexAt(0), fr.GetVertexAt(1), fr.GetVertexAt(2), fr.GetVertexAt(3) });
        }

        MeshGeom g = new();
        HashSet<(int, int)> seen = new();
        foreach (short[] f in faces)
        {
            // Indices a partir de 1; negativo = aresta invisivel; 0 = sem vertice.
            List<(int Index, bool Visible)> idx = f.Where(x => x != 0).Select(x => (Math.Abs((int)x) - 1, x > 0)).Where(x => x.Item1 < v.Count).ToList();
            if (idx.Count < 3)
                continue;
            for (int k = 1; k + 1 < idx.Count; k++)
            {
                foreach (int i in new[] { idx[0].Index, idx[k].Index, idx[k + 1].Index })
                {
                    g.Tri.Add(v[i]);
                    g.Grow(v[i]);
                }
            }

            for (int k = 0; k < idx.Count && g.Edges.Count < 20000; k++)
            {
                if (!idx[k].Visible)
                    continue;
                int a = idx[k].Index, b = idx[(k + 1) % idx.Count].Index;
                if (a != b && seen.Add(a < b ? (a, b) : (b, a)))
                    g.Edges.Add(new List<P3> { v[a], v[b] });
            }
        }

        g.ComputeFolds();
        return g;
    }

    /// <summary>3D face (triangulo ou quadrilatero).</summary>
    private static MeshGeom? FromFace(AcadDb.Face face)
    {
        P3[] p = new P3[4];
        for (short i = 0; i < 4; i++)
        {
            Point3d q = face.GetVertexAt(i);
            p[i] = new P3(q.X, q.Y, q.Z);
        }

        MeshGeom g = new();
        foreach (P3 q in new[] { p[0], p[1], p[2] })
        {
            g.Tri.Add(q);
            g.Grow(q);
        }

        if (p[3] != p[2])
        {
            foreach (P3 q in new[] { p[0], p[2], p[3] })
            {
                g.Tri.Add(q);
                g.Grow(q);
            }
        }

        int n = p[3] != p[2] ? 4 : 3;
        for (short i = 0; i < n; i++)
        {
            bool visible = true;
            try
            {
                visible = face.IsEdgeVisibleAt(i);
            }
            catch
            {
                // sem informacao: visivel
            }

            if (visible)
                g.Edges.Add(new List<P3> { p[i], p[(i + 1) % n] });
        }

        return g;
    }

    /// <summary>Bloco: malha da definicao (cache por definicao) levada pela matriz da insercao.</summary>
    private static MeshGeom? FromBlock(AcadDb.Transaction tr, ExtentsCache cache, AcadDb.BlockReference br)
    {
        MeshGeom? def = BlockMesh(tr, cache, br.BlockTableRecord, 0);
        if (def is null || def.IsEmpty)
            return null;
        string name = br.Name;
        try
        {
            if (br.IsDynamicBlock && tr.GetObject(br.DynamicBlockTableRecord, AcadDb.OpenMode.ForRead) is AcadDb.BlockTableRecord dyn)
                name = dyn.Name;
        }
        catch
        {
            // fica o nome da insercao
        }

        Matrix3d m = br.BlockTransform;
        return def.Transformed(p =>
        {
            Point3d q = new Point3d(p.X, p.Y, p.Z).TransformBy(m);
            return new P3(q.X, q.Y, q.Z);
        }, "Bloco " + name);
    }

    /// <summary>
    /// Malha de uma definicao de bloco (coordenadas do bloco): solidos, corpos, superficies,
    /// malhas e blocos aninhados. Xrefs e definicoes sem 3D ficam de fora (null).
    /// </summary>
    private static MeshGeom? BlockMesh(AcadDb.Transaction tr, ExtentsCache cache, AcadDb.ObjectId btrId, int depth)
    {
        if (cache.Blocks.TryGetValue(btrId, out MeshGeom? known))
            return known;
        cache.Blocks[btrId] = null; // protege contra recursao
        if (depth > 6 || tr.GetObject(btrId, AcadDb.OpenMode.ForRead) is not AcadDb.BlockTableRecord btr ||
            btr.IsFromExternalReference || btr.IsDependent || btr.IsLayout)
        {
            return null;
        }

        MeshGeom acc = new();
        foreach (AcadDb.ObjectId eid in btr)
        {
            string dxf;
            try
            {
                dxf = eid.ObjectClass.DxfName;
            }
            catch
            {
                continue;
            }

            if (!MeshClasses.Contains(dxf) && !dxf.Equals("INSERT", StringComparison.OrdinalIgnoreCase) && !IsAcadSurface(eid))
                continue;
            try
            {
                if (tr.GetObject(eid, AcadDb.OpenMode.ForRead) is not AcadDb.Entity e)
                    continue;
                cache.BlockOwner[eid] = btrId;
                // Estados de visibilidade de blocos dinamicos e layers desligadas/congeladas.
                if (!e.Visible || LayerHidden(tr, cache, e.LayerId))
                    continue;
                MeshGeom? part = e switch
                {
                    AcadDb.BlockReference nested => BlockMesh(tr, cache, nested.BlockTableRecord, depth + 1) is { } nm
                        ? nm.Transformed(p =>
                        {
                            Point3d q = new Point3d(p.X, p.Y, p.Z).TransformBy(nested.BlockTransform);
                            return new P3(q.X, q.Y, q.Z);
                        }, "")
                        : null,
                    _ => IsMeshEntity(e) ? FromEntity(tr, e) : null
                };
                acc.Append(part);
            }
            catch
            {
                // objeto ilegivel dentro do bloco
            }
        }

        MeshGeom? result = acc.IsEmpty ? null : acc;
        cache.Blocks[btrId] = result;
        return result;
    }

    /// <summary>Layer desligada ou congelada (cache por layer durante a vida do cache de extents).</summary>
    private static bool LayerHidden(AcadDb.Transaction tr, ExtentsCache cache, AcadDb.ObjectId layerId)
    {
        if (cache.LayerHidden.TryGetValue(layerId, out bool hidden))
            return hidden;
        try
        {
            hidden = tr.GetObject(layerId, AcadDb.OpenMode.ForRead) is AcadDb.LayerTableRecord l && (l.IsOff || l.IsFrozen);
        }
        catch
        {
            hidden = false;
        }

        cache.LayerHidden[layerId] = hidden;
        return hidden;
    }

    // ------------------------------------------------------------------
    // Secao de objetos em malha
    // ------------------------------------------------------------------

    /// <summary>Passo de subdivisao das arestas projetadas (acompanha a entrada/saida da caixa).</summary>
    private static double GhostStep(CutBox box) => Math.Clamp(Math.Min(box.Frente + box.Atras, box.Length) / 12.0, 0.2, 2.0);

    /// <summary>Corte pela malha (blocos, corpos, superficies, malhas): triangulos x plano.</summary>
    private static void CutMesh(MeshGeom g, ItemKind kind, string layer, CutBox box, SectionData data, int color, string handle)
    {
        (double minX, double minY, double maxX, double maxY) = box.Bounds();
        if (!g.Overlaps(minX, minY, maxX, maxY) || g.Tri.Count == 0)
            return;
        List<((double S, double Z) A, (double S, double Z) B)> segs = new();
        HashSet<(long, long, long, long)> seen = new();
        for (int k = 0; k + 2 < g.Tri.Count; k += 3)
        {
            foreach (((double S, double Z) A, (double S, double Z) B) sg in box.CutTriangle(g.Tri[k], g.Tri[k + 1], g.Tri[k + 2]))
            {
                // Aresta deitada no plano sai dos dois triangulos vizinhos: fica uma.
                (long, long) qa = ((long)Math.Round(sg.A.S * 1e5), (long)Math.Round(sg.A.Z * 1e5)), qb = ((long)Math.Round(sg.B.S * 1e5), (long)Math.Round(sg.B.Z * 1e5));
                (long, long, long, long) key = qa.CompareTo(qb) <= 0 ? (qa.Item1, qa.Item2, qb.Item1, qb.Item2) : (qb.Item1, qb.Item2, qa.Item1, qa.Item2);
                if (seen.Add(key))
                    segs.Add(sg);
            }
        }

        if (segs.Count == 0)
            return;
        // Fica so o que cai dentro do comprimento da linha.
        List<List<(double S, double Z)>> chains = new();
        foreach (List<(double S, double Z)> c in SecaoGeometry.Chain(segs, 1e-3))
            chains.AddRange(SecaoGeometry.ClipS(c, box.Length));
        if (chains.Count == 0)
            return;
        SectionItem item = new() { Kind = kind, Name = g.Name, Layer = layer, ColorIndex = color, Polylines = chains };
        item.Fills = SecaoGeometry.ClosedLoops(chains);
        item.Info["Handle"] = handle;
        item.Info["Triangulos"] = g.Triangles.ToString(CultureInfo.InvariantCulture);
        data.Items.Add(item);
    }

    /// <summary>
    /// O que o objeto mostra na profundidade: arestas do Brep e silhuetas (dobras que viram
    /// de lado na direcao da vista), projetadas no plano; regiao hachuravel pelo contorno.
    /// </summary>
    private static void GhostMesh(MeshGeom g, ItemKind kind, string layer, CutBox gbox, SectionData data, int color, string handle)
    {
        (double minX, double minY, double maxX, double maxY) = gbox.Bounds();
        if (!g.Overlaps(minX, minY, maxX, maxY))
            return;
        double step = GhostStep(gbox);
        SectionItem ghost = new() { Kind = kind, Name = g.Name, Layer = layer, Ghost = true, ColorIndex = color };
        double sum = 0;
        int n = 0;
        void Add(List<P3> pts)
        {
            foreach (List<(double S, double Z)> part in SecaoGeometry.ProjectInside(gbox, Subdivide(pts, step), out double d))
            {
                if (part.Count < 2)
                    continue;
                ghost.Polylines.Add(part);
                sum += d;
                n++;
            }
        }

        foreach (List<P3> e in g.Edges)
            Add(e);
        int silhouettes = 0;
        foreach ((P3 a, P3 b, P3 n1, P3 n2) in g.Folds)
        {
            P2 v = gbox.NormalAt(new P2((a.X + b.X) / 2, (a.Y + b.Y) / 2));
            double s1 = (n1.X * v.X) + (n1.Y * v.Y), s2 = (n2.X * v.X) + (n2.Y * v.Y);
            if (s1 * s2 >= 0)
                continue;
            Add(new List<P3> { a, b });
            silhouettes++;
        }

        if (ghost.Polylines.Count == 0)
            return;
        ghost.Depth = n == 0 ? 0 : sum / n;
        int segCount = ghost.Polylines.Sum(p => p.Count - 1);
        // Contorno projetado (hachuravel): caro em objetos detalhados, entao so e calculado
        // quando alguem pede (hachura, menu de estilo), e so ate um limite de trechos.
        List<List<(double S, double Z)>> lines = ghost.Polylines;
        ghost.FillsFactory = segCount <= 1500 ? () => SecaoGeometry.Outline(lines, 1e-3) : null;
        ghost.Info["Handle"] = handle;
        ghost.Info["Afastamento medio"] = ghost.Depth.ToString("0.00", CultureInfo.InvariantCulture) + " m";
        ghost.Info["Arestas projetadas"] = $"{ghost.Polylines.Count} ({silhouettes} de silhueta)";
        data.Items.Add(ghost);
    }
}
