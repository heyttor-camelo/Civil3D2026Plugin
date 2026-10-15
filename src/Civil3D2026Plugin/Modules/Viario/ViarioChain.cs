using System;
using System.Collections.Generic;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Eixo lancado como no Vehicle Tracking: uma sequencia de retas e arcos em que
/// cada elemento parte tangente ao fim do anterior (concordancia).
///
/// A polilinha guarda so os vertices e os bulges; o que define a cadeia e:
/// - o tipo de cada trecho (bulge zero = reta, outro = arco);
/// - o ponto final de cada trecho (vertice seguinte), que e o "alvo";
/// - a direcao inicial (primeira reta, ou tangente inicial do primeiro arco).
///
/// A resolucao percorre os trechos: a reta segue a direcao corrente ate a
/// projecao do alvo; o arco e o arco tangente a direcao corrente que passa
/// pelo alvo (o raio resulta). Assim, mover um vertice refaz o que vem depois
/// dele mantendo a concordancia, como no VT.
/// </summary>
internal static class ViarioChain
{
    /// <summary>Comprimento minimo de uma reta (evita trecho nulo ao arrastar para tras).</summary>
    public const double MinTangent = 0.05;

    /// <summary>
    /// Resolve a cadeia. Devolve os vertices/bulges concordantes e se algo mudou
    /// em relacao ao que entrou.
    /// </summary>
    public static (List<V2> Points, List<double> Bulges, bool Changed) Solve(IReadOnlyList<V2> pts, IReadOnlyList<double> bulges)
    {
        List<V2> outPts = new(pts);
        List<double> outBulges = new();
        for (int i = 0; i < pts.Count - 1; i++)
            outBulges.Add(i < bulges.Count ? bulges[i] : 0.0);

        if (pts.Count < 2)
            return (outPts, outBulges, false);

        V2 p = pts[0];
        V2? heading = StartHeading(pts, outBulges);
        if (heading is null)
            return (outPts, outBulges, false);

        V2 h = heading.Value;
        bool changed = false;

        for (int k = 0; k < outPts.Count - 1; k++)
        {
            V2 target = pts[k + 1];
            bool arc = Math.Abs(outBulges[k]) > 1e-9;

            if (!arc)
            {
                double d = V2.Dot(target - p, h);
                if (k == 0)
                    d = V2.Dist(target, p); // primeira reta: a direcao vem dela mesma
                if (d < MinTangent)
                    d = MinTangent;
                V2 end = p + (h * d);
                if (V2.Dist(end, outPts[k + 1]) > 1e-6)
                {
                    outPts[k + 1] = end;
                    changed = true;
                }

                p = end;
            }
            else
            {
                if (V2.Dist(target, p) < 1e-6)
                {
                    // Arco nulo: nao ha como resolver; segue como esta.
                    p = target;
                    continue;
                }

                double bulge = TangentBulge(h, p, target);
                if (Math.Abs(bulge - outBulges[k]) > 1e-9)
                {
                    outBulges[k] = bulge;
                    changed = true;
                }

                h = new Seg(p, target, bulge).TangentAt(1.0);
                p = target;
            }
        }

        return (outPts, outBulges, changed);
    }

    /// <summary>Bulge do arco tangente a t que sai de a e chega em b (raio resulta do ponto).</summary>
    public static double TangentBulge(V2 tangent, V2 a, V2 b)
    {
        V2 chord = b - a;
        double alpha = Math.Atan2(V2.Cross(tangent, chord), V2.Dot(tangent, chord));
        alpha = Math.Clamp(alpha, -Math.PI + 0.02, Math.PI - 0.02);
        return Math.Tan(alpha / 2.0);
    }

    /// <summary>Direcao inicial: a da primeira reta, ou a tangente inicial do primeiro arco.</summary>
    private static V2? StartHeading(IReadOnlyList<V2> pts, IReadOnlyList<double> bulges)
    {
        if (V2.Dist(pts[0], pts[1]) < 1e-9)
            return null;
        Seg first = new(pts[0], pts[1], bulges[0]);
        V2 t = first.TangentAt(0.0);
        return t.Length < 1e-12 ? null : t.Unit();
    }

    /// <summary>Se os trechos ja sao concordantes (dentro da tolerancia angular).</summary>
    public static bool IsTangentContinuous(IReadOnlyList<V2> pts, IReadOnlyList<double> bulges, double tolDeg = 0.05)
    {
        for (int k = 1; k < pts.Count - 1; k++)
        {
            Seg a = new(pts[k - 1], pts[k], k - 1 < bulges.Count ? bulges[k - 1] : 0.0);
            Seg b = new(pts[k], pts[k + 1], k < bulges.Count ? bulges[k] : 0.0);
            V2 ta = a.TangentAt(1.0), tb = b.TangentAt(0.0);
            if (ta.Length < 1e-12 || tb.Length < 1e-12)
                continue;
            double ang = Math.Abs(Math.Atan2(V2.Cross(ta, tb), V2.Dot(ta, tb))) * 180.0 / Math.PI;
            if (ang > tolDeg)
                return false;
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Reconcordancia local num vertice
    // ------------------------------------------------------------------

    /// <summary>
    /// Mantem o arco (circulo: centro e raio) e move a reta vizinha para ficar
    /// tangente a ele: o vertice comum vai para o ponto de tangencia tirado da
    /// outra ponta da reta (fixa); o arco e encurtado/prolongado no mesmo circulo.
    /// </summary>
    /// <param name="arc">Arco atual.</param>
    /// <param name="arcFixedEnd">Ponta do arco que nao muda (a oposta ao vertice).</param>
    /// <param name="lineFarEnd">Ponta da reta que nao muda.</param>
    /// <param name="vertex">Vertice atual (para escolher a tangente mais proxima).</param>
    /// <param name="arcEndsAtVertex">true: o arco chega no vertice (arco antes da reta); false: sai dele.</param>
    /// <returns>Novo vertice e novo bulge do arco, ou null se a ponta da reta esta dentro do circulo.</returns>
    public static (V2 Vertex, double Bulge)? LineToArc(Seg arc, V2 arcFixedEnd, V2 lineFarEnd, V2 vertex, bool arcEndsAtVertex)
    {
        if (!arc.IsArc)
            return null;

        V2 c = arc.Center;
        double r = arc.Radius;
        double d = V2.Dist(lineFarEnd, c);
        if (d <= r + 1e-9)
            return null; // ponta da reta dentro do circulo: nao ha tangente

        // Pontos de tangencia de lineFarEnd ao circulo.
        double ang = Math.Acos(r / d);
        double baseAng = (lineFarEnd - c).Angle;
        V2 t1 = c + (V2.FromAngle(baseAng + ang) * r);
        V2 t2 = c + (V2.FromAngle(baseAng - ang) * r);
        V2 t = V2.Dist(t1, vertex) <= V2.Dist(t2, vertex) ? t1 : t2;

        // Arco no mesmo circulo e mesmo sentido de giro, entre a ponta fixa e o novo vertice.
        int dir = Math.Sign(arc.Bulge);
        V2 a = arcEndsAtVertex ? arcFixedEnd : t;
        V2 b = arcEndsAtVertex ? t : arcFixedEnd;
        double sweep = GMath.Norm((b - c).Angle - (a - c).Angle); // [0, 2pi)
        if (dir < 0)
            sweep -= 2.0 * Math.PI; // horario: negativo
        if (Math.Abs(sweep) < 1e-6 || Math.Abs(sweep) > 2.0 * Math.PI - 1e-6)
            return null;

        return (t, Math.Tan(sweep / 4.0));
    }

    /// <summary>Descricao dos trechos (tipo, comprimento, raio) para listas e relatorios.</summary>
    public static IEnumerable<(int Index, string Tipo, double Length, double Radius, double S0, double S1, V2 A, V2 B)> Elements(GPath path)
    {
        double s = 0.0;
        int i = 0;
        foreach (Seg seg in path.Segs)
        {
            double len = seg.Length;
            double r = seg.IsArc ? seg.Radius : 0.0;
            yield return (++i, seg.IsArc ? "Arco" : "Reta", len, r, s, s + len, seg.A, seg.B);
            s += len;
        }
    }
}
