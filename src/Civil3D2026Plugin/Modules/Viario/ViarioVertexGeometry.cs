using System;
using System.Collections.Generic;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>Vertice de polilinha-eixo com quebra de direcao (ou ja concordado por um arco).</summary>
internal sealed class VertexPick
{
    public object? Id;
    public string AxisKey = "";
    public List<(V2 P, double Bulge)> Vertices = new();
    public bool Closed;

    /// <summary>Indice do vertice (canto vivo) ou do inicio do arco de concordancia.</summary>
    public int Index;
    public bool HasArc;
    public V2 Pi;
    public V2 Prev;   // ponto anterior (fim do trecho reto que chega)
    public V2 Next;   // ponto seguinte (inicio do trecho reto que sai)
    public double Deflection;
    public int Turn;  // +1 esquerda, -1 direita
    public double AxisRadius;
    public double Face;
    public double Distance;
}


/// <summary>Geometria pura do raio em vertice de eixo (testavel fora do AutoCAD).</summary>
internal static class ViarioVertexGeometry
{
    /// <summary>
    /// Canto no vertice i: reta-reta (canto vivo em i) ou reta-arco-reta (arco i..i+1 tangente
    /// as retas vizinhas, i.e. concordancia ja feita).
    /// </summary>
    public static VertexPick? CornerAt(List<(V2 P, double Bulge)> v, bool closed, int i)
    {
        int n = v.Count;
        int segCount = closed ? n : n - 1;
        Seg SegAt(int k) => new(v[((k % n) + n) % n].P, v[((k + 1) % n + n) % n].P, v[((k % n) + n) % n].Bulge);
        bool Valid(int k) => closed || (k >= 0 && k < segCount);

        // Arco de concordancia entre duas retas.
        if (Math.Abs(v[i].Bulge) > 1e-9 && Valid(i) && Valid(i - 1) && Valid(i + 1))
        {
            Seg before = SegAt(i - 1);
            Seg arc = SegAt(i);
            Seg after = SegAt(i + 1);
            if (before.IsArc || after.IsArc)
                return null;

            V2 d1 = (before.B - before.A).Unit();
            V2 d2 = (after.B - after.A).Unit();
            if (Math.Abs(V2.Cross(d1, arc.TangentAt(0.0))) > 1e-3 || Math.Abs(V2.Cross(d2, arc.TangentAt(1.0))) > 1e-3)
                return null;

            V2? pi = LineIntersection(before.A, d1, after.B, d2);
            if (pi is null)
                return null;

            return Build(i, true, pi.Value, before.A, after.B, d1, d2, arc.Radius);
        }

        // Canto vivo em i: segmentos i-1 e i retos.
        if (!Valid(i - 1) || !Valid(i))
            return null;

        Seg a = SegAt(i - 1);
        Seg b = SegAt(i);
        if (a.IsArc || b.IsArc || a.Length < 1e-6 || b.Length < 1e-6)
            return null;

        V2 u1 = (a.B - a.A).Unit();
        V2 u2 = (b.B - b.A).Unit();
        if (Math.Abs(V2.Cross(u1, u2)) < Math.Sin(1.0 * Math.PI / 180.0) && V2.Dot(u1, u2) > 0)
            return null;

        return Build(i, false, v[i].P, a.A, b.B, u1, u2, 0.0);
    }

    private static VertexPick Build(int index, bool hasArc, V2 pi, V2 prev, V2 next, V2 d1, V2 d2, double radius)
    {
        double cross = V2.Cross(d1, d2);
        double deflection = Math.Acos(Math.Clamp(V2.Dot(d1, d2), -1.0, 1.0));
        return new VertexPick
        {
            Index = index,
            HasArc = hasArc,
            Pi = pi,
            Prev = prev,
            Next = next,
            Deflection = deflection,
            Turn = cross >= 0 ? 1 : -1,
            AxisRadius = radius
        };
    }

    private static V2? LineIntersection(V2 p, V2 d, V2 q, V2 e)
    {
        double den = V2.Cross(d, e);
        if (Math.Abs(den) < 1e-12)
            return null;
        double t = V2.Cross(q - p, e) / den;
        return p + (d * t);
    }

    /// <summary>Lado clicado e o de dentro da curva?</summary>
    public static bool InnerSide(VertexPick v, V2 click)
    {
        V2 d1 = (v.Pi - v.Prev).Unit();
        return Math.Sign(V2.Cross(d1, click - v.Prev)) == v.Turn;
    }

    /// <summary>Raio na face do meio-fio do lado clicado -> raio do eixo.</summary>
    public static double AxisRadiusFor(VertexPick v, bool inner, double faceRadius) =>
        inner ? faceRadius + v.Face : faceRadius - v.Face;

    public static double FaceRadiusOf(VertexPick v, bool inner) =>
        v.AxisRadius <= 1e-9 ? 0.0 : inner ? v.AxisRadius - v.Face : v.AxisRadius + v.Face;

    /// <summary>Vertices da polilinha com o canto concordado pelo raio do eixo (0 = canto vivo).</summary>
    public static List<(V2 P, double Bulge)>? Apply(VertexPick v, double axisRadius, out string? error)
    {
        error = null;
        List<(V2 P, double Bulge)> verts = new(v.Vertices);
        int n = verts.Count;

        // Desfaz a concordancia existente: volta ao PI.
        int at = v.Index;
        if (v.HasArc)
        {
            int next = (at + 1) % n;
            verts[at] = (v.Pi, 0.0);
            verts.RemoveAt(next);
            if (next < at)
                at--;
        }
        else
        {
            verts[at] = (v.Pi, 0.0);
        }

        if (axisRadius <= 1e-6)
            return verts;

        double tangent = axisRadius * Math.Tan(v.Deflection / 2.0);
        double room1 = V2.Dist(v.Prev, v.Pi);
        double room2 = V2.Dist(v.Pi, v.Next);
        if (tangent > room1 - 0.01 || tangent > room2 - 0.01)
        {
            error = $"raio grande demais para o canto (tangente {tangent:0.##} m, cabe ate {Math.Min(room1, room2):0.##} m).";
            return null;
        }

        V2 d1 = (v.Pi - v.Prev).Unit();
        V2 d2 = (v.Next - v.Pi).Unit();
        V2 t1 = v.Pi - (d1 * tangent);
        V2 t2 = v.Pi + (d2 * tangent);
        double bulge = v.Turn * Math.Tan(v.Deflection / 4.0);

        verts[at] = (t1, bulge);
        verts.Insert(at + 1, (t2, 0.0));
        return verts;
    }

    public static GPath PathOf(List<(V2 P, double Bulge)> verts, bool closed)
    {
        List<Seg> segs = new();
        int n = verts.Count;
        int count = closed ? n : n - 1;
        for (int i = 0; i < count; i++)
            segs.Add(new Seg(verts[i].P, verts[(i + 1) % n].P, verts[i].Bulge));
        return new GPath(segs);
    }


    /// <summary>
    /// Aplica o mesmo raio de face em varios cantos da mesma polilinha (identificados pelo PI).
    /// Recalcula o canto a cada passo porque os indices mudam ao inserir/remover vertices.
    /// </summary>
    public static List<(V2 P, double Bulge)>? ApplyMany(List<(V2 P, double Bulge)> verts, bool closed, double face,
        IEnumerable<(V2 Pi, bool Inner)> targets, double faceRadius, out string? error)
    {
        error = null;
        List<(V2 P, double Bulge)> current = new(verts);
        foreach ((V2 pi, bool inner) in targets)
        {
            VertexPick? pick = null;
            for (int i = 0; i < current.Count && pick is null; i++)
            {
                VertexPick? c = CornerAt(current, closed, i);
                if (c is not null && V2.Dist(c.Pi, pi) < 1e-4)
                    pick = c;
            }

            if (pick is null)
                continue;

            pick.Vertices = current;
            pick.Closed = closed;
            pick.Face = face;
            double axisRadius = faceRadius <= 1e-6 ? 0.0 : Math.Max(AxisRadiusFor(pick, inner, faceRadius), 0.0);
            List<(V2 P, double Bulge)>? next = Apply(pick, axisRadius, out error);
            if (next is null)
                return null;
            current = next;
        }

        return current;
    }
}
