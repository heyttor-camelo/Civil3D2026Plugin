using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3D2026Plugin.Modules.Utilitarios;

/// <summary>Rotinas comuns aos utilitarios convertidos das LISPs.</summary>
internal static class UtilHelpers
{
    /// <summary>Offsets da curva; lista vazia se o AutoCAD nao conseguir gerar.</summary>
    public static List<Curve> Offset(Curve curve, double distance)
    {
        var result = new List<Curve>();
        try
        {
            using DBObjectCollection col = curve.GetOffsetCurves(distance);
            foreach (DBObject obj in col)
            {
                if (obj is Curve c)
                    result.Add(c);
                else
                    obj.Dispose();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Offset impossivel (distancia maior que a curva, auto-intersecao etc.).
        }
        return result;
    }

    /// <summary>Soma das areas (em modulo) das curvas fechadas.</summary>
    public static double Area(IEnumerable<Curve> curves)
    {
        double total = 0.0;
        foreach (Curve c in curves)
        {
            try
            {
                total += Math.Abs(c.Area);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
            }
        }
        return total;
    }

    public static void Dispose(IEnumerable<Curve> curves)
    {
        foreach (Curve c in curves)
            c.Dispose();
    }

    /// <summary>
    /// Offset para o lado do ponto: gera +d e -d e fica com o que passa mais perto do ponto.
    /// </summary>
    public static List<Curve> OffsetToward(Curve curve, double distance, Point3d pointWcs)
    {
        List<Curve> plus = Offset(curve, distance);
        List<Curve> minus = Offset(curve, -distance);

        double dp = MinDistance(plus, pointWcs);
        double dm = MinDistance(minus, pointWcs);

        if (dp <= dm)
        {
            Dispose(minus);
            return plus;
        }

        Dispose(plus);
        return minus;
    }

    private static double MinDistance(List<Curve> curves, Point3d p)
    {
        double best = double.MaxValue;
        foreach (Curve c in curves)
        {
            try
            {
                best = Math.Min(best, c.GetClosestPointTo(p, false).DistanceTo(p));
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
            }
        }
        return best;
    }

    /// <summary>Acrescenta as entidades no mesmo espaco (Model ou layout) do objeto de origem.</summary>
    public static void AppendToOwner(Transaction tr, ObjectId ownerId, IEnumerable<Entity> entities)
    {
        var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForWrite);
        foreach (Entity e in entities)
        {
            owner.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
        }
    }

    /// <summary>Cria a layer se nao existir.</summary>
    public static void EnsureLayer(Transaction tr, Database db, string name, short colorIndex)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(name))
            return;

        lt.UpgradeOpen();
        var ltr = new LayerTableRecord
        {
            Name = name,
            Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex)
        };
        lt.Add(ltr);
        tr.AddNewlyCreatedDBObject(ltr, true);
    }

    /// <summary>Curinga do AutoCAD (* ? #, lista separada por virgula), sem diferenciar maiusculas.</summary>
    public static bool WildcardMatch(string text, string pattern)
    {
        foreach (string part in pattern.Split(','))
        {
            string p = part.Trim();
            if (p.Length == 0)
                continue;

            string rx = "^" + Regex.Escape(p)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".")
                .Replace("\\#", "[0-9]") + "$";
            if (Regex.IsMatch(text, rx, RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }
}
