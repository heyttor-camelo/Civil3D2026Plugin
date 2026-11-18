using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.Infrastructure;
using FLType = Autodesk.Civil.FeatureLinePointType;

namespace Civil3D2026Plugin.Modules.FeatureLines;

/// <summary>
/// Troca a geometria em planta de uma feature line existente pela de uma polilinha, sem perder a identidade da feature
/// line (mesmo ObjectId/handle: corredores, alvos e site continuam ligados a ela). Pedido do Heyvid em 18/11/2026
/// (canteiros e ilhas).
///
/// A API da feature line nao move PI nem cria arco em trecho reto (testado em 18/11/2026: SetBulge so muda arco que ja
/// existe, COM SetBulgeAtPoint nao faz nada, fechada nao estende). Por isso o comando cria uma feature line nova com a
/// geometria certa (arcos exatos, aberta ou fechada) e troca a identidade com a antiga (SwapIdWith): o handle original
/// passa a ter a geometria nova e o objeto com a geometria antiga e apagado. Teste no KM_01: handle igual, baseline
/// intacta, Rebuild sem erro, AUDIT sem erros depois de salvar e reabrir.
/// Elevacao: mesmo numero de vertices = vertice a vertice; senao, a da feature line antiga no ponto mais proximo
/// (interpolada). Pontos de elevacao: cada um vai para o ponto mais proximo da geometria nova (ate 1 m) com a mesma
/// elevacao; os mais longe se perdem (contados no resumo).
/// </summary>
internal static class FeatureLineGeometry
{
    private const double Tol = 1e-4;

    /// <summary>Distancia maxima para recolocar um ponto de elevacao na geometria nova.</summary>
    internal const double ElevationReach = 1.0;

    internal sealed class Plan
    {
        public List<Point2d> V { get; } = new();
        public List<double> Bulge { get; } = new();
        public List<double> Z { get; } = new();
        public bool Closed { get; set; }
        public bool Reversed { get; set; }
        public int Rotated { get; set; }
        public string ZSource { get; set; } = "";
        public List<Point3d> ElevationPoints { get; } = new();
        public int ElevationLost { get; set; }
        public int ElevationWeeded { get; set; }
        public int ElevationBeforeWeed { get; set; }
        public int OldPis { get; set; }
        public int OldElevationPoints { get; set; }
        public double OldLength { get; set; }
        public double NewLength { get; set; }
        public double ZMaxChange { get; set; }
        public List<BaselineUse> Baselines { get; } = new();
        public List<string> Notes { get; } = new();
    }

    internal sealed class BaselineUse
    {
        public AcadDb.ObjectId CorridorId { get; set; }
        public string Corridor { get; set; } = "";
        public string Baseline { get; set; } = "";
        public bool Reference { get; set; }
        public List<(string Name, double Start, double End)> Regions { get; } = new();
        public string Note { get; set; } = "";
    }

    /// <summary>Vertices e bulges da polilinha (LW, 2D ou 3D). Lanca erro com mensagem clara se nao der.</summary>
    internal static (List<Point3d> V, List<double> B, bool Closed, bool Has3d) ReadPolyline(AcadDb.Transaction tr, AcadDb.Entity e)
    {
        var v = new List<Point3d>();
        var b = new List<double>();
        bool closed;
        bool has3d = false;
        switch (e)
        {
            case AcadDb.Polyline pl:
                for (int i = 0; i < pl.NumberOfVertices; i++)
                {
                    v.Add(pl.GetPoint3dAt(i));
                    b.Add(pl.GetBulgeAt(i));
                }
                closed = pl.Closed;
                break;
            case AcadDb.Polyline2d p2:
                if (p2.PolyType != AcadDb.Poly2dType.SimplePoly)
                    throw new InvalidOperationException("Polilinha 2D suavizada (spline/fit): use uma polilinha simples (PEDIT > Decurve) ou LWPOLYLINE.");
                foreach (AcadDb.ObjectId vid in p2)
                {
                    var vx = (AcadDb.Vertex2d)tr.GetObject(vid, AcadDb.OpenMode.ForRead);
                    v.Add(p2.VertexPosition(vx));
                    b.Add(vx.Bulge);
                }
                closed = p2.Closed;
                break;
            case AcadDb.Polyline3d p3:
                if (p3.PolyType != AcadDb.Poly3dType.SimplePoly)
                    throw new InvalidOperationException("Polilinha 3D suavizada: use uma polilinha 3D simples.");
                foreach (AcadDb.ObjectId vid in p3)
                {
                    var vx = (AcadDb.PolylineVertex3d)tr.GetObject(vid, AcadDb.OpenMode.ForRead);
                    v.Add(vx.Position);
                    b.Add(0);
                }
                closed = p3.Closed;
                has3d = v.Select(p => p.Z).Distinct().Count() > 1;
                break;
            default:
                throw new InvalidOperationException("Selecione uma polilinha (LWPOLYLINE, polilinha 2D ou 3D).");
        }
        // Vertices repetidos seguidos saem; ultimo igual ao primeiro = fechada.
        for (int i = v.Count - 1; i > 0; i--)
        {
            if (Flat(v[i]).GetDistanceTo(Flat(v[i - 1])) < Tol)
            {
                v.RemoveAt(i);
                b.RemoveAt(i - 1); // o segmento de comprimento zero sai; fica o bulge do seguinte
            }
        }
        if (v.Count > 2 && Flat(v[0]).GetDistanceTo(Flat(v[^1])) < Tol)
        {
            v.RemoveAt(v.Count - 1);
            b.RemoveAt(b.Count - 1);
            closed = true;
        }
        while (b.Count < v.Count)
            b.Add(0);
        if (v.Count < 2 || (closed && v.Count < 3))
            throw new InvalidOperationException("A polilinha precisa de pelo menos 2 vertices (3 se fechada).");
        return (v, b, closed, has3d);
    }

    private static Point2d Flat(Point3d p) => new(p.X, p.Y);

    private static double Area2(IReadOnlyList<Point2d> v)
    {
        double a = 0;
        for (int i = 0; i < v.Count; i++)
        {
            Point2d p = v[i], q = v[(i + 1) % v.Count];
            a += (p.X * q.Y) - (q.X * p.Y);
        }
        return a;
    }

    /// <summary>PIs da feature line sem o ultimo repetido da fechada.</summary>
    internal static List<Point3d> UniquePis(CivilDb.FeatureLine fl)
    {
        var p = fl.GetPoints(FLType.PIPoint).Cast<Point3d>().ToList();
        if (fl.Closed && p.Count > 3 && Flat(p[0]).GetDistanceTo(Flat(p[^1])) < Tol)
            p.RemoveAt(p.Count - 1);
        return p;
    }

    /// <summary>Elevacao da feature line antiga no ponto mais proximo em planta (segmentos retos entre todos os pontos).</summary>
    private static double ZNearest(List<Point3d> path, bool closed, Point2d q, out double dist)
    {
        double best = double.MaxValue, z = path[0].Z;
        int n = closed ? path.Count : path.Count - 1;
        for (int i = 0; i < n; i++)
        {
            Point3d a = path[i], b = path[(i + 1) % path.Count];
            double ex = b.X - a.X, ey = b.Y - a.Y, l2 = (ex * ex) + (ey * ey);
            double t = l2 < 1e-12 ? 0 : Math.Clamp((((q.X - a.X) * ex) + ((q.Y - a.Y) * ey)) / l2, 0, 1);
            double dx = a.X + (ex * t) - q.X, dy = a.Y + (ey * t) - q.Y, d = Math.Sqrt((dx * dx) + (dy * dy));
            if (d < best)
            {
                best = d;
                z = a.Z + ((b.Z - a.Z) * t);
            }
        }
        dist = best;
        return z;
    }

    /// <summary>Polilinha em memoria (fora do desenho) com a geometria nova, para medir e achar o ponto mais proximo.</summary>
    internal static AcadDb.Polyline Shape(Plan p)
    {
        var pl = new AcadDb.Polyline();
        for (int i = 0; i < p.V.Count; i++)
            pl.AddVertexAt(i, p.V[i], p.Bulge[i], 0, 0);
        pl.Closed = p.Closed;
        return pl;
    }

    /// <summary>Monta o plano: alinha sentido/inicio com a feature line, calcula elevacoes e acha as baselines.</summary>
    internal static Plan Prepare(AcadDb.Transaction tr, AcadDb.Database db, CivilDb.FeatureLine fl, AcadDb.Entity polyEnt, bool usePolyZ, double weedTol = 0.001)
    {
        (List<Point3d> pv, List<double> pb, bool pClosed, bool has3d) = ReadPolyline(tr, polyEnt);
        if (pClosed != fl.Closed)
        {
            throw new InvalidOperationException(fl.Closed
                ? "A feature line e fechada e a polilinha e aberta: feche a polilinha (PEDIT > Fechar) para a feature line continuar fechada."
                : "A feature line e aberta e a polilinha e fechada" + (EndGap(fl).Gap < 0.01 ? $" (a feature line parece fechada: pontas a {EndGap(fl).Gap * 1000:0.0} mm; feche de verdade com FLFECHADAS e rode de novo)" : ": use uma polilinha aberta") + ".");
        }

        var plan = new Plan { Closed = pClosed };
        List<Point3d> oldPis = UniquePis(fl);
        List<Point3d> oldAll = fl.GetPoints(FLType.AllPoints).Cast<Point3d>().ToList();
        if (fl.Closed && oldAll.Count > 3 && Flat(oldAll[0]).GetDistanceTo(Flat(oldAll[^1])) < Tol)
            oldAll.RemoveAt(oldAll.Count - 1);
        plan.OldPis = oldPis.Count;
        plan.OldElevationPoints = fl.ElevationPointsCount;
        plan.OldLength = fl.Length2D;

        var v = pv.Select(Flat).ToList();
        var b = new List<double>(pb);
        var z = pv.Select(p => p.Z).ToList();
        int n = v.Count;

        // Sentido: o da feature line (estacas dos corredores nao invertem).
        if (!pClosed)
        {
            Point2d s = Flat(oldPis[0]), e = Flat(oldPis[^1]);
            if (v[0].GetDistanceTo(s) + v[^1].GetDistanceTo(e) > v[^1].GetDistanceTo(s) + v[0].GetDistanceTo(e))
            {
                v.Reverse();
                z.Reverse();
                var nb = new List<double>();
                for (int j = 0; j < n; j++)
                    nb.Add(j < n - 1 ? -b[n - 2 - j] : 0);
                b = nb;
                plan.Reversed = true;
            }
        }
        else
        {
            if (Math.Sign(Area2(v)) != Math.Sign(Area2(oldPis.Select(Flat).ToList())))
            {
                var nv = new List<Point2d>();
                var nz = new List<double>();
                var nb = new List<double>();
                for (int j = 0; j < n; j++)
                {
                    nv.Add(v[(n - j) % n]);
                    nz.Add(z[(n - j) % n]);
                    nb.Add(-b[(n - j - 1 + n) % n]);
                }
                v = nv;
                z = nz;
                b = nb;
                plan.Reversed = true;
            }
            // Inicio no vertice mais perto do inicio antigo (a estaca 0 do corredor fica no mesmo lugar).
            Point2d s = Flat(oldPis[0]);
            int r = Enumerable.Range(0, n).OrderBy(i => v[i].GetDistanceTo(s)).First();
            if (r != 0)
            {
                v = Enumerable.Range(0, n).Select(i => v[(i + r) % n]).ToList();
                z = Enumerable.Range(0, n).Select(i => z[(i + r) % n]).ToList();
                b = Enumerable.Range(0, n).Select(i => b[(i + r) % n]).ToList();
                plan.Rotated = r;
            }
        }
        if (!pClosed)
            b[n - 1] = 0;

        // Elevacoes dos vertices novos.
        var zNew = new List<double>();
        if (usePolyZ && has3d)
        {
            zNew.AddRange(z);
            plan.ZSource = "da polilinha 3D";
        }
        else if (n == oldPis.Count)
        {
            zNew.AddRange(oldPis.Select(p => p.Z));
            plan.ZSource = "vertice a vertice (mesmo numero de vertices)";
        }
        else
        {
            foreach (Point2d q in v)
                zNew.Add(ZNearest(oldAll, fl.Closed, q, out _));
            plan.ZSource = "da feature line antiga no ponto mais proximo";
        }
        for (int i = 0; i < n; i++)
        {
            plan.V.Add(v[i]);
            plan.Bulge.Add(b[i]);
            plan.Z.Add(zNew[i]);
            plan.ZMaxChange = Math.Max(plan.ZMaxChange, Math.Abs(zNew[i] - ZNearest(oldAll, fl.Closed, v[i], out _)));
        }

        using (AcadDb.Polyline shape = Shape(plan))
        {
            plan.NewLength = shape.Length;
            // Pontos de elevacao: mais proximo na geometria nova (ate ElevationReach), mesma elevacao; perto de um PI sai.
            if (!(usePolyZ && has3d))
            {
                foreach (Point3d ep in fl.GetPoints(FLType.ElevationPoint).Cast<Point3d>())
                {
                    Point3d c = shape.GetClosestPointTo(new Point3d(ep.X, ep.Y, shape.Elevation), false);
                    double d = Flat(c).GetDistanceTo(Flat(ep));
                    if (d > ElevationReach)
                    {
                        plan.ElevationLost++;
                        continue;
                    }
                    if (plan.V.Any(q => q.GetDistanceTo(Flat(c)) < 0.02))
                        continue;
                    if (plan.ElevationPoints.Any(q => Flat(q).GetDistanceTo(Flat(c)) < 0.005))
                        continue;
                    plan.ElevationPoints.Add(new Point3d(c.X, c.Y, ep.Z));
                }
            }
            else
            {
                plan.ElevationLost = fl.ElevationPointsCount;
            }
        }

        plan.ElevationBeforeWeed = plan.ElevationPoints.Count;
        if (weedTol > 0)
            WeedElevationPoints(plan, weedTol);
        FindBaselines(tr, db, fl.ObjectId, plan);
        return plan;
    }

    /// <summary>
    /// Pontos de elevacao: so ficam os que mudam o perfil (estaca x elevacao) mais de 1 mm em relacao a reta entre os
    /// vizinhos que ficam (Douglas-Peucker; os vertices sempre ficam). As feature lines vindas de corredor tem um ponto a
    /// cada ~15 cm e, dentro de um site, cada ponto inserido custa caro.
    /// </summary>
    private static void WeedElevationPoints(Plan plan, double tol)
    {
        if (plan.ElevationPoints.Count == 0)
            return;
        using AcadDb.Polyline shape = Shape(plan);
        var all = new List<(double S, double Z, Point3d P, bool Fixed)>();
        for (int i = 0; i < plan.V.Count; i++)
        {
            Point3d v = new(plan.V[i].X, plan.V[i].Y, shape.Elevation);
            all.Add((shape.GetDistAtPoint(v), plan.Z[i], v, true));
        }
        foreach (Point3d p in plan.ElevationPoints)
        {
            Point3d on = shape.GetClosestPointTo(new Point3d(p.X, p.Y, shape.Elevation), false);
            all.Add((shape.GetDistAtPoint(on), p.Z, p, false));
        }
        if (plan.Closed)
            all.Add((shape.Length, plan.Z[0], new Point3d(plan.V[0].X, plan.V[0].Y, 0), true));
        all.Sort((a, b) => a.S.CompareTo(b.S));
        var keep = new bool[all.Count];
        for (int i = 0; i < all.Count; i++)
            keep[i] = all[i].Fixed;
        keep[0] = keep[^1] = true;
        void Dp(int i0, int i1)
        {
            if (i1 <= i0 + 1)
                return;
            double best = 0;
            int at = -1;
            for (int i = i0 + 1; i < i1; i++)
            {
                double t = all[i1].S - all[i0].S < 1e-9 ? 0 : (all[i].S - all[i0].S) / (all[i1].S - all[i0].S);
                double d = Math.Abs(all[i].Z - (all[i0].Z + ((all[i1].Z - all[i0].Z) * t)));
                if (d > best)
                {
                    best = d;
                    at = i;
                }
            }
            if (at >= 0 && best > tol)
            {
                keep[at] = true;
                Dp(i0, at);
                Dp(at, i1);
            }
        }
        // Entre vertices fixos (que sempre ficam), cada trecho separado.
        int prev = 0;
        for (int i = 1; i < all.Count; i++)
        {
            if (!keep[i] && i != all.Count - 1)
                continue;
            if (all[i].Fixed || i == all.Count - 1)
            {
                Dp(prev, i);
                prev = i;
            }
        }
        int before = plan.ElevationPoints.Count;
        plan.ElevationPoints.Clear();
        for (int i = 0; i < all.Count; i++)
        {
            if (keep[i] && !all[i].Fixed)
                plan.ElevationPoints.Add(all[i].P);
        }
        plan.ElevationWeeded = before - plan.ElevationPoints.Count;
    }

    /// <summary>
    /// Plano para mudar a origem (estaca 0) de uma feature line FECHADA (FLORIGEM, 18/11/2026): a mesma geometria
    /// (vertices, arcos, elevacoes e pontos de elevacao), com a lista de vertices comecando no ponto escolhido. Ponto no
    /// meio de um segmento vira vertice (arco dividido no mesmo raio). <paramref name="shift"/> = estaca antiga do novo inicio.
    /// </summary>
    internal static Plan PrepareRotation(AcadDb.Transaction tr, AcadDb.Database db, CivilDb.FeatureLine fl, Point3d pick, bool snapVertex, out double shift, out bool newVertex)
    {
        if (!fl.Closed)
            throw new InvalidOperationException("a feature line e aberta: a origem dela so pode ser uma das pontas (use o comando so em feature line fechada)");
        if ((object)fl is not AcadDb.Curve cv)
            throw new InvalidOperationException("feature line sem geometria de curva");
        List<Point3d> pis = UniquePis(fl);
        int n = pis.Count;
        double[] bul = PiBulges(fl, pis, true);
        double L = cv.GetDistanceAtParameter(cv.EndParam);
        var st = new double[n + 1];
        for (int i = 0; i < n; i++)
            st[i] = i == 0 ? 0 : cv.GetDistAtPoint(cv.GetClosestPointTo(pis[i], false));
        st[n] = L;

        Point3d on = cv.GetClosestPointTo(pick, Vector3d.ZAxis, false);
        double s = cv.GetDistAtPoint(on);
        int seg = Math.Clamp(Array.FindLastIndex(st, x => x <= s + 1e-9), 0, n - 1);
        int nearV = Enumerable.Range(0, n).OrderBy(i => Flat(pis[i]).GetDistanceTo(Flat(on))).First();
        var v = new List<Point3d>();
        var b = new List<double>();
        newVertex = false;
        if (snapVertex || Flat(pis[nearV]).GetDistanceTo(Flat(on)) < 0.001 || s - st[seg] < 0.001 || st[seg + 1] - s < 0.001)
        {
            int k0 = snapVertex || Flat(pis[nearV]).GetDistanceTo(Flat(on)) < 0.001 ? nearV : (s - st[seg] < 0.001 ? seg : (seg + 1) % n);
            shift = st[k0];
            for (int i = 0; i < n; i++)
            {
                v.Add(pis[(k0 + i) % n]);
                b.Add(bul[(k0 + i) % n]);
            }
        }
        else
        {
            // Novo vertice no meio do segmento: o arco (se for) vira dois arcos do mesmo circulo.
            newVertex = true;
            shift = s;
            double f = (s - st[seg]) / (st[seg + 1] - st[seg]);
            double theta = 4.0 * Math.Atan(bul[seg]);
            double b1 = Math.Tan(theta * f / 4.0), b2 = Math.Tan(theta * (1 - f) / 4.0);
            v.Add(on);
            b.Add(b2);
            for (int i = 1; i <= n; i++)
            {
                int k = (seg + i) % n;
                v.Add(pis[k]);
                b.Add(k == seg ? b1 : bul[k]);
            }
        }

        var plan = new Plan { Closed = true, ZSource = "a mesma (geometria nao muda)", OldPis = n, OldLength = fl.Length2D, NewLength = fl.Length2D, OldElevationPoints = fl.ElevationPointsCount };
        foreach (Point3d p in v)
        {
            plan.V.Add(Flat(p));
            plan.Z.Add(p.Z);
        }
        plan.Bulge.AddRange(b);
        foreach (Point3d ep in fl.GetPoints(FLType.ElevationPoint).Cast<Point3d>())
        {
            if (Flat(ep).GetDistanceTo(Flat(on)) > 0.005)
                plan.ElevationPoints.Add(ep);
        }
        plan.ElevationBeforeWeed = plan.ElevationPoints.Count;
        FindBaselines(tr, db, fl.ObjectId, plan);
        return plan;
    }

    /// <summary>
    /// Arco de cada trecho entre PIs. GetBulge(i) e o segmento i entre TODOS os pontos (PIs e pontos de elevacao): o arco
    /// de um trecho e a soma dos angulos dos pedacos entre os pontos dele.
    /// </summary>
    internal static double[] PiBulges(CivilDb.FeatureLine fl, List<Point3d> pis, bool closed)
    {
        int n = pis.Count;
        var all = fl.GetPoints(FLType.AllPoints).Cast<Point3d>().ToList();
        var bul = new double[n];
        int ai = 0;
        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            Point2d end = Flat(pis[(i + 1) % n]);
            double theta = 0;
            for (int guard = 0; ai < all.Count - 1 && guard < all.Count; guard++)
            {
                try
                {
                    theta += 4.0 * Math.Atan(fl.GetBulge(ai));
                }
                catch
                {
                    // segmento reto
                }
                ai++;
                if (Flat(all[ai]).GetDistanceTo(end) < 1e-4)
                    break;
            }
            bul[i] = Math.Tan(theta / 4.0);
        }
        return bul;
    }

    /// <summary>Feature line aberta com as pontas coladas: distancia em planta e diferenca de elevacao entre as pontas.</summary>
    internal static (double Gap, double Dz) EndGap(CivilDb.FeatureLine fl)
    {
        var p = fl.GetPoints(FLType.PIPoint).Cast<Point3d>().ToList();
        return (Flat(p[0]).GetDistanceTo(Flat(p[^1])), Math.Abs(p[0].Z - p[^1].Z));
    }

    /// <summary>
    /// Plano para FECHAR de verdade uma feature line aberta cujas pontas estao coladas ("parece fechada"): mesmos
    /// vertices, arcos e elevacoes; o ultimo PI sai se estiver em cima do primeiro (senao vira o trecho de fechamento).
    /// </summary>
    internal static Plan PrepareClose(AcadDb.Transaction tr, AcadDb.Database db, CivilDb.FeatureLine fl)
    {
        if (fl.Closed)
            throw new InvalidOperationException("a feature line ja e fechada");
        var pis = fl.GetPoints(FLType.PIPoint).Cast<Point3d>().ToList();
        double[] bul = PiBulges(fl, pis, false);
        bool dupEnd = Flat(pis[0]).GetDistanceTo(Flat(pis[^1])) < 0.005;
        int n = dupEnd ? pis.Count - 1 : pis.Count;
        var plan = new Plan { Closed = true, ZSource = "a mesma", OldPis = pis.Count, OldLength = fl.Length2D, OldElevationPoints = fl.ElevationPointsCount };
        for (int i = 0; i < n; i++)
        {
            plan.V.Add(Flat(pis[i]));
            plan.Z.Add(pis[i].Z);
            plan.Bulge.Add(i < pis.Count - 1 ? bul[i] : 0);
        }
        if (dupEnd)
            plan.Bulge[n - 1] = bul[n - 1]; // o ultimo trecho (ate a ponta repetida) vira o de fechamento
        foreach (Point3d ep in fl.GetPoints(FLType.ElevationPoint).Cast<Point3d>())
            plan.ElevationPoints.Add(ep);
        plan.ElevationBeforeWeed = plan.ElevationPoints.Count;
        using (AcadDb.Polyline shape = Shape(plan))
            plan.NewLength = shape.Length;
        FindBaselines(tr, db, fl.ObjectId, plan);
        return plan;
    }
    /// <summary>Estaca nova depois de mudar a origem: (antiga - deslocamento) mod comprimento.</summary>
    internal static double Rotate(double s, double shift, double len)
    {
        double r = s - shift;
        while (r < -1e-6)
            r += len;
        while (r > len + 1e-6)
            r -= len;
        return Math.Clamp(r, 0, len);
    }

    /// <summary>
    /// Transicoes do corredor (Corridor Transitions, inclusive os rebaixos do MFREBAIXO) nas baselines desta feature line
    /// depois de mudar a origem: o Civil remapeia regioes e secoes extras pela posicao, mas nao as transicoes. Cada
    /// transicao vai para a estaca nova; o token de estacas do comentario dos rebaixos ("{S=s0;s1;s2;s3;...}") tambem.
    /// Transicao que atravessaria a nova origem: erro (verificado antes, em <see cref="CrossingTransitions"/>).
    /// </summary>
    internal static List<string> ShiftTransitions(AcadDb.Transaction tr, Plan plan, double shift)
    {
        var msgs = new List<string>();
        double len = plan.NewLength;
        foreach (BaselineUse use in plan.Baselines.Where(b => !b.Reference))
        {
            var c = (CivilDb.Corridor)tr.GetObject(use.CorridorId, AcadDb.OpenMode.ForWrite);
            CivilDb.Baseline? bl = c.Baselines.Cast<CivilDb.Baseline>().FirstOrDefault(x => x.Name == use.Baseline);
            if (bl is null)
                continue;
            List<CivilDb.CorridorTransitionSet> sets = bl.getTransitions().ToList();
            if (sets.Count == 0)
                continue;
            // O conjunto fica preso a regiao em que foi criado (estacas fora dela sao recusadas): cada um e criado de
            // novo na regiao que contem as estacas novas, com os mesmos parametros, valores, tipos e comentario.
            var regs = bl.BaselineRegions.Cast<CivilDb.BaselineRegion>().ToList();
            var fresh = new List<CivilDb.CorridorTransitionSet>();
            int moved = 0;
            foreach (CivilDb.CorridorTransitionSet set in sets)
            {
                var items = new List<(string Param, double A, double B, object Va, object Vb, CivilDb.CorridorTransitionType Type, bool HasSide, CivilDb.SubassemblySideType Side)>();
                for (int i = 0; i < set.TransitionCount; i++)
                {
                    CivilDb.CorridorTransition t = set.GetTransitionAt(i);
                    double a = Rotate(t.StartStation, shift, len), b = Rotate(t.EndStation, shift, len);
                    if (b < a + 1e-6 && t.EndStation > t.StartStation + 1e-6)
                        b = len; // terminava exatamente na origem antiga = fim da volta
                    items.Add((t.ParameterName, a, b, t.StartValue, t.EndValue, t.TransitionType, t.HasSide, t.HasSide ? t.Side : default));
                }
                if (items.Count == 0)
                {
                    fresh.Add(set);
                    continue;
                }
                double lo = items.Min(x => x.A), hi = items.Max(x => x.B);
                CivilDb.BaselineRegion? reg = regs.FirstOrDefault(r => r.StartStation <= lo + 0.001 && r.EndStation >= hi - 0.001)
                                              ?? regs.OrderBy(r => Math.Abs(((r.StartStation + r.EndStation) / 2.0) - ((lo + hi) / 2.0))).First();
                CivilDb.CorridorTransitionSet ns = items[0].HasSide
                    ? new CivilDb.CorridorTransitionSet(set.Name, reg, set.SubassemblyName, set.NameType, items[0].Side)
                    : new CivilDb.CorridorTransitionSet(set.Name, reg, set.SubassemblyName, set.NameType);
                ns.Comment = ShiftToken(set.Comment, shift, len);
                string? lastParam = null;
                foreach (var it in items)
                {
                    CivilDb.CorridorTransition nt = lastParam is null || it.Param != lastParam ? ns.AddTransition(it.Param) : ns.AddTransition();
                    lastParam = it.Param;
                    nt.StartStation = it.A;
                    nt.EndStation = it.B;
                    nt.StartValue = it.Va;
                    nt.EndValue = it.Vb;
                    nt.TransitionType = it.Type;
                    moved++;
                }
                ns.StationLocked = set.StationLocked;
                fresh.Add(ns);
            }
            bl.SetTransitions(fresh);
            sets = fresh;            msgs.Add($"{use.Corridor} / {use.Baseline}: {sets.Count} conjunto(s) de transicao, {moved} transicao(oes) deslocadas para a nova origem.");
        }
        return msgs;
    }

    /// <summary>Estacas do token "{S=s0;s1;s2;s3;V=...}" dos rebaixos, deslocadas.</summary>
    internal static string ShiftToken(string? comment, double shift, double len)
    {
        if (string.IsNullOrEmpty(comment))
            return comment ?? "";
        int i = comment.IndexOf("{S=", StringComparison.Ordinal);
        if (i < 0)
            return comment;
        int j = comment.IndexOf(";V=", i, StringComparison.Ordinal);
        if (j < 0)
            return comment;
        string[] parts = comment.Substring(i + 3, j - i - 3).Split(';');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var outp = new List<string>();
        foreach (string p in parts)
        {
            outp.Add(double.TryParse(p, System.Globalization.NumberStyles.Float, inv, out double v)
                ? Rotate(v, shift, len).ToString("0.######", inv)
                : p);
        }
        return comment.Substring(0, i + 3) + string.Join(";", outp) + comment.Substring(j);
    }

    /// <summary>Transicoes e regioes que a nova origem cortaria ao meio (estaca do novo inicio estritamente dentro delas).</summary>
    internal static List<string> CrossingTransitions(AcadDb.Transaction tr, Plan plan, double shift)
    {
        var list = new List<string>();
        double len = plan.OldLength;
        if (shift < 0.001 || shift > len - 0.001)
            return list;
        foreach (BaselineUse use in plan.Baselines.Where(b => !b.Reference))
        {
            var c = (CivilDb.Corridor)tr.GetObject(use.CorridorId, AcadDb.OpenMode.ForRead);
            CivilDb.Baseline? bl = c.Baselines.Cast<CivilDb.Baseline>().FirstOrDefault(x => x.Name == use.Baseline);
            if (bl is null)
                continue;
            foreach (CivilDb.CorridorTransitionSet set in bl.getTransitions())
            {
                for (int i = 0; i < set.TransitionCount; i++)
                {
                    CivilDb.CorridorTransition t = set.GetTransitionAt(i);
                    if (t.StartStation < shift - 0.001 && t.EndStation > shift + 0.001)
                    {
                        list.Add($"transicao '{set.Name}' ({t.StartStation:0.000}-{t.EndStation:0.000})");
                        break;
                    }
                }
            }
            foreach ((string name, double s0, double e0) in use.Regions)
            {
                bool full = s0 < 0.01 && e0 > len - 0.01;
                if (!full && s0 < shift - 0.001 && e0 > shift + 0.001)
                    list.Add($"regiao '{name}' ({s0:0.000}-{e0:0.000})");
            }
        }
        return list;
    }

    /// <summary>Baselines de corredor que usam a feature line.</summary>
    private static void FindBaselines(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.ObjectId flId, Plan plan)
    {
        var bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        var ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (!id.ObjectClass.DxfName.Contains("CORRIDOR", StringComparison.OrdinalIgnoreCase))
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not CivilDb.Corridor c)
                continue;
            foreach (CivilDb.Baseline bl in c.Baselines)
            {
                AcadDb.ObjectId fid;
                try
                {
                    fid = bl.FeatureLineId;
                }
                catch
                {
                    continue; // baseline de alinhamento
                }
                if (fid != flId)
                    continue;
                var use = new BaselineUse { CorridorId = id, Corridor = c.Name, Baseline = bl.Name, Reference = c.IsReferenceObject };
                foreach (CivilDb.BaselineRegion r in bl.BaselineRegions)
                    use.Regions.Add((r.Name, r.StartStation, r.EndStation));
                plan.Baselines.Add(use);
            }
        }
    }

    /// <summary>
    /// Aplica o plano trocando a identidade (18/11/2026): cria uma feature line nova a partir da geometria nova (arcos
    /// exatos, inclusive na fechada), no mesmo site, com layer/estilo e as elevacoes calculadas, e troca o ObjectId/handle
    /// com a antiga (SwapIdWith). Quem apontava para a antiga (baselines, alvos, site) passa a ver a geometria nova; o
    /// objeto que ficou com a geometria antiga e apagado. A API da feature line nao cria arco em trecho reto nem move
    /// PI; este caminho nao tem essas limitacoes. Tudo na transacao do chamador (abortar = nada muda; U desfaz).
    /// </summary>
    internal static AcadDb.ObjectId CreateNew(AcadDb.Transaction tr, AcadDb.Database db, CivilDb.FeatureLine fl, Plan plan)
    {
        string name = fl.Name;
        var ms = (AcadDb.BlockTableRecord)tr.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db), AcadDb.OpenMode.ForWrite);
        AcadDb.Polyline shape = Shape(plan);
        ms.AppendEntity(shape);
        tr.AddNewlyCreatedDBObject(shape, true);
        string temp = "_C3D_TMP_FL_" + DateTime.Now.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Criada direto no site da antiga: MoveToSite (fora do site e levar depois, que seria muito mais rapido) nao moveu
        // nada nos testes de 18/11/2026, dentro nem fora de transacao.
        AcadDb.ObjectId newId = fl.SiteId.IsNull
            ? CivilDb.FeatureLine.Create(temp, shape.ObjectId)
            : CivilDb.FeatureLine.Create(temp, shape.ObjectId, fl.SiteId);
        shape.Erase();

        var nf = (CivilDb.FeatureLine)tr.GetObject(newId, AcadDb.OpenMode.ForWrite);
        nf.Layer = fl.Layer;
        nf.Color = fl.Color;
        nf.LinetypeId = fl.LinetypeId;
        nf.LineWeight = fl.LineWeight;
        try
        {
            nf.StyleId = fl.StyleId;
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso("FLGEOMETRIA", $"{name}: estilo nao copiado ({ex.Message}).");
        }
        CopyProperty(fl, nf, "Description");

        // Vertices na ordem da polilinha alinhada: confere e poe as elevacoes (indice = PI, ainda sem pontos de elevacao).
        List<Point3d> np = UniquePis(nf);
        if (np.Count != plan.V.Count || Enumerable.Range(0, np.Count).Any(i => Flat(np[i]).GetDistanceTo(plan.V[i]) > 0.002))
            throw new InvalidOperationException($"a feature line nova saiu com {np.Count} vertices fora da ordem da polilinha");
        for (int i = 0; i < np.Count; i++)
            nf.SetPointElevation(i, plan.Z[i]);
        if (plan.ElevationPoints.Count > 0)
        {
            // Ponto em cima da propria feature line nova (projecao em planta); a elevacao e a calculada.
            var cv = (AcadDb.Curve)(object)nf;
            var pts = new Point3dCollection();
            foreach (Point3d p in plan.ElevationPoints)
            {
                Point3d on = cv.GetClosestPointTo(new Point3d(p.X, p.Y, p.Z), Vector3d.ZAxis, false);
                if (np.Any(q => Flat(q).GetDistanceTo(Flat(on)) < 0.01))
                    continue;
                pts.Add(new Point3d(on.X, on.Y, p.Z));
            }
            try
            {
                nf.InsertElevationPoints(pts);
            }
            catch (System.Exception ex)
            {
                int bad = 0;
                string first = ex.Message;
                foreach (Point3d p in pts)
                {
                    try
                    {
                        nf.InsertElevationPoint(p);
                    }
                    catch (System.Exception ex2)
                    {
                        bad++;
                        first = ex2.Message;
                    }
                }
                plan.ElevationLost += bad;
                PluginLog.Aviso("FLGEOMETRIA", $"{name}: pontos de elevacao em lote falharam ({ex.Message}); um a um: {pts.Count - bad} entraram, {bad} nao ({first}).");
            }
        }
        return newId;
    }

    /// <summary>Segunda etapa: troca a identidade (o handle antigo fica com a geometria nova), apaga a sobra e acerta o nome.</summary>
    internal static void SwapIn(AcadDb.Transaction tr, AcadDb.ObjectId oldId, AcadDb.ObjectId newId, string name)
    {
        var fl = (CivilDb.FeatureLine)tr.GetObject(oldId, AcadDb.OpenMode.ForWrite);
        fl.SwapIdWith(newId, true, true);
        var leftover = (CivilDb.FeatureLine)tr.GetObject(newId, AcadDb.OpenMode.ForWrite);
        leftover.Erase();
        var result = (CivilDb.FeatureLine)tr.GetObject(oldId, AcadDb.OpenMode.ForWrite);
        if (result.Name != name)
            result.Name = name;
    }
    private static void CopyProperty(object from, object to, string prop)
    {
        try
        {
            var p = from.GetType().GetProperty(prop);
            if (p is not null && p.CanWrite && p.CanRead)
                p.SetValue(to, p.GetValue(from));
        }
        catch
        {
            // propriedade que nao copia: fica a padrao
        }
    }
    /// <summary>
    /// Conferencia antes de gravar: percorre a feature line a cada 5 cm e mede a distancia ate a geometria nova.
    /// Devolve a mensagem de erro (mais de 5 mm) ou null.
    /// </summary>
    internal static string? Check(CivilDb.FeatureLine fl, Plan plan)
    {
        if ((object)fl is not AcadDb.Curve cv)
            return null;
        using AcadDb.Polyline shape = Shape(plan);
        double len = cv.GetDistanceAtParameter(cv.EndParam), worst = 0;
        for (double s = 0; s <= len; s += 0.05)
        {
            Point3d p = cv.GetPointAtDist(Math.Min(s, len));
            Point3d q = shape.GetClosestPointTo(new Point3d(p.X, p.Y, shape.Elevation), false);
            worst = Math.Max(worst, Flat(p).GetDistanceTo(Flat(q)));
        }
        PluginLog.Diag("FLGEOMETRIA", $"conferencia: comprimento {len:0.000} x {plan.NewLength:0.000}, maior afastamento {worst * 1000:0.0} mm");
        return worst > 0.005
            ? $"a feature line ficou ate {worst * 100:0.0} cm fora da polilinha (algum arco antigo nao saiu); nada foi gravado"
            : null;
    }

    /// <summary>Segmentos em arco (comprimento ao longo da feature line maior que a corda entre os PIs).</summary>
    internal static List<int> ArcSegments(CivilDb.FeatureLine fl)
    {
        var list = new List<int>();
        if ((object)fl is not AcadDb.Curve cv)
            return list;
        var p = fl.GetPoints(FLType.PIPoint).Cast<Point3d>().ToList();
        double total = cv.GetDistanceAtParameter(cv.EndParam);
        double prev = 0;
        for (int i = 0; i + 1 < p.Count; i++)
        {
            double next = i + 1 == p.Count - 1 ? total : cv.GetDistAtPoint(cv.GetClosestPointTo(p[i + 1], false));
            double chord = Flat(p[i]).GetDistanceTo(Flat(p[i + 1]));
            if (next - prev - chord > 0.001)
                list.Add(i);
            prev = next;
        }
        return list;
    }

    /// <summary>
    /// Regioes depois da troca (pedido do Heyvid em 18/11/2026): o Civil remapeia as estacas das regioes quando a
    /// geometria muda; aqui elas voltam para as estacas de antes. A ULTIMA regiao, se ia ate o fim (ou ate menos de
    /// 3 m dele), vai ate o novo fim mantendo a mesma folga; se parava antes, fica na estaca dela. Regiao que passa do
    /// novo comprimento: avisa. Varias passadas nas duas ordens (o Civil recusa uma regiao invadindo a vizinha).
    /// </summary>
    internal static List<string> FitLastRegions(AcadDb.Transaction tr, Plan plan)
    {
        double len = plan.NewLength;
        return RestoreRegions(tr, plan, (k, count, s0, e0) =>
        {
            bool last = k == count - 1;
            double gap = plan.OldLength - e0;
            double end = last && gap < 3.0 ? len - Math.Max(0, gap) : Math.Min(e0, len);
            return (Math.Min(s0, len), end);
        });
    }

    /// <summary>Mudar a origem: cada regiao vai para (estaca - deslocamento) mod comprimento; a de volta inteira fica 0-fim.</summary>
    internal static List<string> RotateRegions(AcadDb.Transaction tr, Plan plan, double shift)
    {
        double len = plan.NewLength;
        return RestoreRegions(tr, plan, (k, count, s0, e0) =>
        {
            if (s0 < 0.01 && e0 > len - 0.01)
                return (0.0, len);
            double a = Rotate(s0, shift, len), b = Rotate(e0, shift, len);
            if (b <= a + 1e-6)
                b = len; // terminava na origem antiga
            return (a, b);
        });
    }

    /// <summary>Regrava as regioes das baselines nas estacas dadas por <paramref name="target"/> (indice, total, inicio e fim de antes).</summary>
    internal static List<string> RestoreRegions(AcadDb.Transaction tr, Plan plan, Func<int, int, double, double, (double S, double E)> target)
    {
        var msgs = new List<string>();
        double len = plan.NewLength;
        foreach (BaselineUse use in plan.Baselines)
        {
            if (use.Reference)
            {
                msgs.Add($"{use.Corridor} / {use.Baseline}: corredor de referencia (data shortcut): regioes nao mexidas.");
                continue;
            }
            var c = (CivilDb.Corridor)tr.GetObject(use.CorridorId, AcadDb.OpenMode.ForWrite);
            CivilDb.Baseline? bl = c.Baselines.Cast<CivilDb.Baseline>().FirstOrDefault(x => x.Name == use.Baseline);
            if (bl is null)
                continue;
            var regs = bl.BaselineRegions.Cast<CivilDb.BaselineRegion>().ToList();
            if (regs.Count == 0 || regs.Count != use.Regions.Count)
            {
                msgs.Add($"{use.Corridor} / {use.Baseline}: numero de regioes mudou ({use.Regions.Count} -> {regs.Count}); confira as estacas no Corridor Properties.");
                continue;
            }
            var targets = new List<(CivilDb.BaselineRegion R, double S, double E, string Name, double S0, double E0)>();
            for (int k = 0; k < regs.Count; k++)
            {
                (string name, double s0, double e0) = use.Regions[k];
                (double start, double end) = target(k, regs.Count, s0, e0);
                // Dentro da faixa real da baseline (o comprimento novo pode diferir em decimos de mm).
                start = Math.Max(start, bl.StartStation);
                end = Math.Min(end, bl.EndStation);
                if (start >= end - 0.001)
                {
                    msgs.Add($"{use.Corridor} / {use.Baseline}: regiao '{name}' ({s0:0.000}-{e0:0.000}) fica fora do novo comprimento {len:0.000}: acerte ou apague a regiao.");
                    start = Math.Max(0, len - 0.02 * (regs.Count - k));
                    end = start + 0.01;
                }
                PluginLog.Diag("FLGEOMETRIA", $"{use.Baseline} regiao [{k}] {name}: antes {s0:0.000}-{e0:0.000}, remapeada pelo Civil {regs[k].StartStation:0.000}-{regs[k].EndStation:0.000}, alvo {start:0.000}-{end:0.000}");
                targets.Add((regs[k], start, end, name, s0, e0));
            }
            bool Done((CivilDb.BaselineRegion R, double S, double E, string Name, double S0, double E0) t) =>
                Math.Abs(t.R.StartStation - t.S) < 0.001 && Math.Abs(t.R.EndStation - t.E) < 0.001;
            string lastErr = "";
            void Set(CivilDb.BaselineRegion r, double s, double e)
            {
                // Crescendo para a direita: fim antes; encolhendo/indo para a esquerda: inicio antes.
                foreach (bool endFirst in new[] { e > r.EndStation, e <= r.EndStation })
                {
                    try
                    {
                        if (endFirst)
                        {
                            r.EndStation = e;
                            r.StartStation = s;
                        }
                        else
                        {
                            r.StartStation = s;
                            r.EndStation = e;
                        }
                        if (Math.Abs(r.StartStation - s) < 0.001 && Math.Abs(r.EndStation - e) < 0.001)
                            return;
                    }
                    catch (System.Exception ex)
                    {
                        lastErr = ex.Message;
                    }
                }
            }
            if (targets.Any(t => !Done(t)))
            {
                // 1) Todas encolhem para faixas minimas separadas no comeco (na ordem da lista, sem sobrepor).
                for (int pass = 0; pass < 3; pass++)
                {
                    for (int k = 0; k < targets.Count; k++)
                        Set(targets[k].R, 0.0001 + (k * 0.02), 0.0001 + (k * 0.02) + 0.01);
                }
                // 2) Abrem da ultima para a primeira ate as estacas certas.
                for (int pass = 0; pass < 3 && targets.Any(t => !Done(t)); pass++)
                {
                    for (int k = targets.Count - 1; k >= 0; k--)
                        Set(targets[k].R, targets[k].S, targets[k].E);
                }
            }            foreach (var t in targets)
            {
                bool last = t.R == targets[^1].R;
                if (!Done(t))
                    msgs.Add($"{use.Corridor} / {use.Baseline}: regiao '{t.Name}' ficou {t.R.StartStation:0.000}-{t.R.EndStation:0.000} (queria {t.S:0.000}-{t.E:0.000}; {lastErr}); acerte no Corridor Properties.");
                else if (last && Math.Abs(t.E0 - t.E) > 0.001)
                    msgs.Add($"{use.Corridor} / {use.Baseline}: ultima regiao '{t.Name}' {t.S0:0.000}-{t.E0:0.000} -> {t.S:0.000}-{t.E:0.000} (novo fim).");
                else if (!last && t.E0 > len + 0.001)
                    msgs.Add($"{use.Corridor} / {use.Baseline}: regiao '{t.Name}' passava do novo comprimento: {t.S0:0.000}-{t.E0:0.000} -> {t.S:0.000}-{t.E:0.000}.");
            }
        }
        return msgs;
    }
}