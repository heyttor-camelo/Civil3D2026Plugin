using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.Modules.CorridorTools.PlanVisibility;

using M3 = System.Windows.Media.Media3D;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Geometria real da estrutura no desenho (o solido que o Civil gera do modelo do Part Builder),
/// em triangulos no sistema local da peca: origem no ponto de insercao na cota do fundo, eixo X
/// na rotacao da estrutura. Tenta o Solid3dBody da peca; senao explode e junta os solidos/malhas.
/// </summary>
internal static class HydroPartMesh
{
    private const int MaxTriangles = 150000;

    /// <summary>Triangulos (3 pontos cada) ou null se a peca nao tiver geometria 3D legivel.</summary>
    public static List<M3.Point3D>? Read(AcadDb.Database db, string handle)
    {
        AcadDb.ObjectId? id = HydroReader.Id(db, handle);
        if (id is null)
            return null;
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        if (tr.GetObject(id.Value, AcadDb.OpenMode.ForRead, false) is not CivilDb.Structure st)
            return null;

        List<Point3d> world = new();
        if (BodyOf(st) is AcadDb.Entity body)
        {
            try
            {
                Collect(body, world, 0);
            }
            finally
            {
                if (body.ObjectId.IsNull)
                    body.Dispose();
            }
        }

        if (world.Count == 0)
            Collect(st, world, 0);
        if (world.Count == 0)
            return null;

        Point3d o = new(st.Position.X, st.Position.Y, st.SumpElevation);
        double c = Math.Cos(-st.Rotation), s = Math.Sin(-st.Rotation);
        List<M3.Point3D> local = new(world.Count);
        foreach (Point3d p in world)
        {
            double x = p.X - o.X, y = p.Y - o.Y;
            local.Add(new M3.Point3D((x * c) - (y * s), (x * s) + (y * c), p.Z - o.Z));
        }

        return local;
    }

    /// <summary>Part.Solid3dBody (por reflexao: nem todo update expoe).</summary>
    private static AcadDb.Entity? BodyOf(CivilDb.Part part)
    {
        try
        {
            PropertyInfo? p = part.GetType().GetProperty("Solid3dBody", BindingFlags.Public | BindingFlags.Instance);
            return p?.GetValue(part) as AcadDb.Entity;
        }
        catch
        {
            return null;
        }
    }

    private static void Collect(AcadDb.Entity e, List<Point3d> tris, int depth)
    {
        if (tris.Count / 3 > MaxTriangles)
            return;
        switch (e)
        {
            case AcadDb.Solid3d or AcadDb.Body or AcadDb.Surface:
                if (PlanHiddenLineEngine.ExtractMesh(e) is ({ } list, _))
                {
                    foreach (PlanHiddenLineEngine.Tri3 t in list)
                    {
                        tris.Add(t.A);
                        tris.Add(t.B);
                        tris.Add(t.C);
                    }
                }

                return;
            case AcadDb.SubDMesh m:
                FromSubD(m, tris);
                return;
            case AcadDb.Face f:
                Point3d a = f.GetVertexAt(0), b = f.GetVertexAt(1), c = f.GetVertexAt(2), d = f.GetVertexAt(3);
                tris.Add(a);
                tris.Add(b);
                tris.Add(c);
                if (!d.IsEqualTo(c))
                {
                    tris.Add(a);
                    tris.Add(c);
                    tris.Add(d);
                }

                return;
        }

        if (depth > 4)
            return;
        AcadDb.DBObjectCollection parts = new();
        try
        {
            e.Explode(parts);
        }
        catch
        {
            // Sem explode (curva, texto...): nao tem malha.
        }

        foreach (AcadDb.DBObject o in parts)
        {
            try
            {
                if (o is AcadDb.Entity child)
                    Collect(child, tris, depth + 1);
            }
            catch
            {
                // Pedaco ilegivel: segue com os outros.
            }
            finally
            {
                o.Dispose();
            }
        }
    }

    private static void FromSubD(AcadDb.SubDMesh mesh, List<Point3d> tris)
    {
        Point3dCollection v = mesh.Vertices;
        Int32Collection faces = mesh.FaceArray;
        int i = 0;
        while (i < faces.Count)
        {
            int n = faces[i];
            if (n < 3 || i + n >= faces.Count)
                break;
            for (int k = 2; k < n; k++)
            {
                int i0 = faces[i + 1], i1 = faces[i + k], i2 = faces[i + k + 1];
                if (i0 < v.Count && i1 < v.Count && i2 < v.Count)
                {
                    tris.Add(v[i0]);
                    tris.Add(v[i1]);
                    tris.Add(v[i2]);
                }
            }

            i += n + 1;
        }
    }
}
