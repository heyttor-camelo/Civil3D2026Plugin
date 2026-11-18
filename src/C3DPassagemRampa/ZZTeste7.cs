using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo.ZZTeste7))]
namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

public static class ZZTeste7
{
    static void L(string s) { File.AppendAllText(Environment.GetEnvironmentVariable("Z5DIR") + "saida-" + Environment.GetEnvironmentVariable("Z5TAG") + ".txt", s + Environment.NewLine); }

    static string P(Point3d p) => $"({p.X:0.000},{p.Y:0.000},{p.Z:0.000})";
    [CommandMethod("ZZT7PROBE")]
    public static void Probe()
    {
        try
        {
            var db = AcApp.DocumentManager.MdiActiveDocument.Database;
            string name = Environment.GetEnvironmentVariable("Z7FL") ?? "SARJETA 16";
            using var tr = db.TransactionManager.StartTransaction();
            var ms = (AcadDb.BlockTableRecord)tr.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db), AcadDb.OpenMode.ForRead);
            CivilDb.FeatureLine? fl = null;
            foreach (AcadDb.ObjectId id in ms) if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is CivilDb.FeatureLine f && f.Name == name) { fl = f; break; }
            if (fl == null) { L("sem FL"); return; }
            fl.UpgradeOpen();
            var pis = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
            var els = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.ElevationPoint);
            var all = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints);
            L($"{name}: PI {pis.Count} EL {els.Count} ALL {all.Count} closed {fl.Closed} len {fl.Length2D:0.000}");
            for (int i = 0; i < Math.Min(5, pis.Count); i++) L($"  PI{i} {P(pis[i])} bulge {fl.GetBulge(i):0.0000}");
            var grips = new Point3dCollection(); var gs = new Autodesk.AutoCAD.Geometry.IntegerCollection(); var gs2 = new Autodesk.AutoCAD.Geometry.IntegerCollection();

            var gp = new Point3dCollection(); fl.GetGripPoints(gp, gs, gs2); L($"  grip points {gp.Count}: {string.Join(" ", gp.Cast<Point3d>().Take(6).Select(P))}");
            // mover o PI 1 por stretch
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { fl.MoveGripPointsAt(new Autodesk.AutoCAD.Geometry.IntegerCollection(new[] { 1 }), new Vector3d(0.5, 0.25, 0)); L($"  grip 1 ok {sw.ElapsedMilliseconds} ms"); } catch (System.Exception ex) { L("  stretch ERRO " + ex.Message); }
            pis = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint); L($"  depois: PI {pis.Count} PI1 {P(pis[1])} EL {fl.GetPoints(Autodesk.Civil.FeatureLinePointType.ElevationPoint).Count}");
            // inserir PI no meio do segmento 2-3 (na linha)
            Point3d m = new Point3d((pis[2].X + pis[3].X) / 2, (pis[2].Y + pis[3].Y) / 2, (pis[2].Z + pis[3].Z) / 2);
            try { fl.InsertPIPoint(m); L($"  insert PI meio ok"); } catch (System.Exception ex) { L("  insert ERRO " + ex.Message); }
            pis = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint); L($"  depois: PI {pis.Count}; PI3 {P(pis[3])}");
            // inserir PI fora da linha (1 m ao lado do meio do seg 4-5)
            Point3d m2 = new Point3d((pis[4].X + pis[5].X) / 2 + 1, (pis[4].Y + pis[5].Y) / 2 + 1, 0);
            try { fl.InsertPIPoint(m2); L($"  insert PI fora ok"); } catch (System.Exception ex) { L("  insert fora ERRO " + ex.Message); }
            var pis2 = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint); L($"  depois: PI {pis2.Count}; " + string.Join(" ", pis2.Cast<Point3d>().Skip(3).Take(4).Select(P)));
            // apagar PI 1
            try { fl.DeletePIPoint(pis2[1]); L("  delete PI1 ok"); } catch (System.Exception ex) { L("  delete ERRO " + ex.Message); }
            L($"  depois: PI {fl.PIPointsCount}");
            // elevacao do PI 0 por SetPointElevation(index em AllPoints?)
            var a0 = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints);
            try { fl.SetPointElevation(0, a0[0].Z + 1); L($"  setElev(0) ok -> PI0 {P(fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint)[0])} ALL0 {P(fl.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints)[0])}"); } catch (System.Exception ex) { L("  setElev ERRO " + ex.Message); }
            // apagar todos os pontos de elevacao
            sw.Restart();
            var e2 = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.ElevationPoint);
            try { fl.DeleteElevationPoints(e2); L($"  delete {e2.Count} EL ok {sw.ElapsedMilliseconds} ms; sobram {fl.ElevationPointsCount}"); } catch (System.Exception ex) { L("  delete EL ERRO " + ex.Message); }
            // bulge
            try { fl.SetBulge(2, 0.3); L($"  bulge(2) {fl.GetBulge(2):0.000}"); } catch (System.Exception ex) { L("  bulge ERRO " + ex.Message); }
            tr.Commit();
        }
        catch (System.Exception ex) { L("ERRO " + ex); }
    }
    [CommandMethod("ZZT7PROBE2")]
    public static void Probe2()
    {
        try
        {
            var db = AcApp.DocumentManager.MdiActiveDocument.Database;
            string name = Environment.GetEnvironmentVariable("Z7FL") ?? "SARJETA 16";
            using var tr = db.TransactionManager.StartTransaction();
            var ms = (AcadDb.BlockTableRecord)tr.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db), AcadDb.OpenMode.ForRead);
            CivilDb.FeatureLine? fl = null;
            foreach (AcadDb.ObjectId id in ms) if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is CivilDb.FeatureLine f && f.Name == name) { fl = f; break; }
            if (fl == null) { L("sem FL"); return; }
            fl.UpgradeOpen();
            fl.DeleteElevationPoints(fl.GetPoints(Autodesk.Civil.FeatureLinePointType.ElevationPoint));
            var pis = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
            L($"PI {pis.Count}: " + string.Join(" ", pis.Cast<Point3d>().Select(P)));
            var gp = new Point3dCollection(); var gs = new Autodesk.AutoCAD.Geometry.IntegerCollection(); var gs2 = new Autodesk.AutoCAD.Geometry.IntegerCollection();
            fl.GetGripPoints(gp, gs, gs2); L($"grips {gp.Count}: " + string.Join(" ", gp.Cast<Point3d>().Select(P)));
            fl.MoveGripPointsAt(new Autodesk.AutoCAD.Geometry.IntegerCollection(new[] { 2 }), new Vector3d(0.5, 0.25, 0));
            pis = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
            L($"apos grip2 +0.5,+0.25: PI {pis.Count}: " + string.Join(" ", pis.Cast<Point3d>().Select(P)));
            // inserir fora da linha num ponto conhecido
            Point3d m2 = new Point3d((pis[4].X + pis[5].X) / 2 + 0.7, (pis[4].Y + pis[5].Y) / 2 + 0.7, 999);
            fl.InsertPIPoint(m2);
            pis = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
            L($"apos insert {P(m2)}: PI {pis.Count}: " + string.Join(" ", pis.Cast<Point3d>().Select(P)));
            // inserir alem do fim (fechada nao tem fim) e longe
            Point3d far = new Point3d(pis[0].X + 30, pis[0].Y, 999);
            fl.InsertPIPoint(far);
            pis = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
            L($"apos insert longe {P(far)}: PI {pis.Count}: " + string.Join(" ", pis.Cast<Point3d>().Select(P)));
            tr.Commit();
        }
        catch (System.Exception ex) { L("ERRO " + ex); }
    }
    [CommandMethod("ZZT7PROBE3")]
    public static void Probe3()
    {
        try
        {
            var db = AcApp.DocumentManager.MdiActiveDocument.Database;
            string name = Environment.GetEnvironmentVariable("Z7FL") ?? "SARJETA 16";
            using var tr = db.TransactionManager.StartTransaction();
            var ms = (AcadDb.BlockTableRecord)tr.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db), AcadDb.OpenMode.ForRead);
            CivilDb.FeatureLine? fl = null;
            foreach (AcadDb.ObjectId id in ms) if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is CivilDb.FeatureLine f && f.Name == name) { fl = f; break; }
            if (fl == null) { L("sem FL"); return; }
            fl.UpgradeOpen();
            fl.DeleteElevationPoints(fl.GetPoints(Autodesk.Civil.FeatureLinePointType.ElevationPoint));
            var grips = new AcadDb.GripDataCollection();
            fl.GetGripPoints(grips, 1.0, 5, Vector3d.ZAxis, AcadDb.GetGripPointsFlags.GripPointsOnly);
            L($"gripdata {grips.Count}: " + string.Join(" ", grips.Cast<AcadDb.GripData>().Select(g => P(g.GripPoint) + g.GetType().Name)));
            if (grips.Count > 2)
            {
                var sel = new AcadDb.GripDataCollection { grips[2] };
                fl.MoveGripPointsAt(sel, new Vector3d(0.5, 0.25, 0), AcadDb.MoveGripPointsFlags.Polar);
                var pis = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
                L($"apos mover grip2: PI {pis.Count}: " + string.Join(" ", pis.Cast<Point3d>().Select(P)));
            }
            // extend numa aberta
            L($"closed {fl.Closed}");
            tr.Commit();
        }
        catch (System.Exception ex) { L("ERRO " + ex); }
    }
    static CivilDb.FeatureLine? FindFl(AcadDb.Transaction tr, AcadDb.Database db, string name)
    {
        var ms = (AcadDb.BlockTableRecord)tr.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db), AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms) if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is CivilDb.FeatureLine f && f.Name == name) return f;
        return null;
    }
    static string Pis(CivilDb.FeatureLine fl) { var p = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint); return $"PI {p.Count}: " + string.Join(" ", p.Cast<Point3d>().Select(q => $"({q.X - 601500:0.00},{q.Y - 7788800:0.00},{q.Z:0.00})")); }
    [CommandMethod("ZZT7PROBE4")]
    public static void Probe4()
    {
        try
        {
            var db = AcApp.DocumentManager.MdiActiveDocument.Database;
            using var tr = db.TransactionManager.StartTransaction();
            var fl = FindFl(tr, db, "SARJETA 16")!; fl.UpgradeOpen();
            fl.DeleteElevationPoints(fl.GetPoints(Autodesk.Civil.FeatureLinePointType.ElevationPoint));
            L("16 " + Pis(fl));
            var p = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
            // ponto 0.2 m ao lado do meio do segmento 7-8 (longo), Z medio
            Point3d a = p[7], b = p[8]; var d = (b - a).GetNormal(); var n = new Vector3d(-d.Y, d.X, 0);
            Point3d q = a + (b - a) * 0.5 + n * 0.2; q = new Point3d(q.X, q.Y, (a.Z + b.Z) / 2);
            fl.InsertPIPoint(q); L($"insert perto do meio 7-8 ({q.X - 601500:0.00},{q.Y - 7788800:0.00}): " + Pis(fl));
            // apagar o PI 0 de uma fechada
            p = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
            try { fl.DeletePIPoint(p[0]); L("delete PI0 fechada: " + Pis(fl) + $" closed {fl.Closed}"); } catch (System.Exception ex) { L("delete PI0 ERRO " + ex.Message); }
            // aberta: SARJETA 03
            var f3 = FindFl(tr, db, "SARJETA 03")!; f3.UpgradeOpen();
            f3.DeleteElevationPoints(f3.GetPoints(Autodesk.Civil.FeatureLinePointType.ElevationPoint));
            var p3 = f3.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
            L($"03 PI {p3.Count} ini ({p3[0].X - 601500:0.00},{p3[0].Y - 7788800:0.00}) fim ({p3[p3.Count-1].X - 601500:0.00},{p3[p3.Count-1].Y - 7788800:0.00})");
            for (int i = p3.Count - 2; i >= 1; i--) f3.DeletePIPoint(p3[i]);
            L("03 sem internos: " + Pis(f3));
            Point3d e0 = p3[p3.Count - 1];
            var targets = new[] { e0 + new Vector3d(5, 5, 0), e0 + new Vector3d(10, 3, 0), e0 + new Vector3d(14, 8, 0) };
            foreach (var tg in targets) f3.ExtendWithFixedLine(false, new Point3d(tg.X, tg.Y, e0.Z + 0.5));
            L("03 extend fim x3: " + Pis(f3));
            f3.ExtendWithFixedLine(true, p3[0] + new Vector3d(-4, -2, 0));
            L("03 extend inicio: " + Pis(f3));
            var pp = f3.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
            try { f3.DeletePIPoint(pp[0]); L("03 delete ponta inicial: " + Pis(f3)); } catch (System.Exception ex) { L("delete ponta ERRO " + ex.Message); }
            pp = f3.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
            try { f3.DeletePIPoint(pp[pp.Count - 1]); L("03 delete ponta final: " + Pis(f3)); } catch (System.Exception ex) { L("delete ponta final ERRO " + ex.Message); }
            tr.Commit();
        }
        catch (System.Exception ex) { L("ERRO " + ex); }
    }
    [CommandMethod("ZZT7COM")]
    public static void Com()
    {
        try
        {
            var asm = System.Reflection.Assembly.LoadFrom(@"C:\Program Files\Autodesk\AutoCAD 2026\C3D\Autodesk.AECC.Interop.Land.dll");
            foreach (var ty in asm.GetTypes().Where(x => x.Name.Contains("FeatureLine") && x.IsInterface && !x.Name.Contains("Style") && !x.Name.Contains("Label")))
            {
                L("T " + ty.FullName);
                foreach (var m in ty.GetMethods()) L("   " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
            }
        }
        catch (System.Exception ex) { L("ERRO " + ex.Message); }
    }
    [CommandMethod("ZZT7GEO")]
    public static void Geo()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument; var db = doc.Database;
        string name = Environment.GetEnvironmentVariable("Z7FL") ?? "SARJETA 03";
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AcadDb.ObjectId flId, plId; string h0; List<Point3d> oldAll; bool closed;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var fl = FindFl(tr, db, name)!; flId = fl.ObjectId; h0 = fl.Handle.ToString(); closed = fl.Closed;
                oldAll = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints).Cast<Point3d>().ToList();
                var pis = Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.UniquePis(fl);
                // polilinha de teste: tira 1 de cada 3 vertices internos, desloca o do meio 0,4 m para o lado, um arco, e (aberta) estica o fim 2 m
                var pl = new AcadDb.Polyline(); int k = 0;
                for (int i = 0; i < pis.Count; i++)
                {
                    if (i > 0 && i < pis.Count - 1 && i % 3 == 0) continue;
                    var p = new Point2d(pis[i].X, pis[i].Y);
                    if (i == pis.Count / 2 && i > 0 && i < pis.Count - 1)
                    {
                        var a = pis[i - 1]; var b = pis[i + 1]; var d = new Vector2d(b.X - a.X, b.Y - a.Y).GetNormal(); p = p + new Vector2d(-d.Y, d.X) * 0.4;
                    }
                    pl.AddVertexAt(k++, p, 0, 0, 0);
                }
                if (!closed)
                {
                    var e = pis[^1]; var e1 = pis[^2]; var d = new Vector2d(e.X - e1.X, e.Y - e1.Y).GetNormal();
                    pl.AddVertexAt(k++, new Point2d(e.X, e.Y) + d * 2.0, 0, 0, 0);
                }
                pl.Closed = closed;
                // arco no segmento mais longo
                int lg = 0; double best = 0; for (int i = 0; i < pl.NumberOfVertices - (closed ? 0 : 1); i++) { double l = pl.GetPoint2dAt(i).GetDistanceTo(pl.GetPoint2dAt((i + 1) % pl.NumberOfVertices)); if (l > best) { best = l; lg = i; } }
                pl.SetBulgeAt(lg, 0.15);
                pl.Elevation = 0;
                var ms = (AcadDb.BlockTableRecord)tr.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db), AcadDb.OpenMode.ForWrite);
                ms.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true); plId = pl.ObjectId;
                L($"{name}: handle {h0} fechada {closed} PI antes {pis.Count} EL antes {fl.ElevationPointsCount} comp {fl.Length2D:0.000}; polilinha {pl.NumberOfVertices} vertices, arco no segmento {lg}, comp {pl.Length:0.000}");
                tr.Commit();
            }
            Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.Plan plan;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var fl = (CivilDb.FeatureLine)tr.GetObject(flId, AcadDb.OpenMode.ForWrite);
                var pl = (AcadDb.Entity)tr.GetObject(plId, AcadDb.OpenMode.ForRead);
                plan = Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.Prepare(tr, db, fl, pl, false);
                L($"  plano: {plan.V.Count} vertices, invertida {plan.Reversed}, girada {plan.Rotated}, elevacao {plan.ZSource}, EL a recolocar {plan.ElevationPoints.Count} perdidos {plan.ElevationLost}, baselines {plan.Baselines.Count}: " + string.Join("; ", plan.Baselines.Select(b => b.Corridor + "/" + b.Baseline + " regioes " + string.Join(",", b.Regions.Select(r => $"{r.Name} {r.Start:0.00}-{r.End:0.00}")))));
                var _nid = Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.CreateNew(tr, db, fl, plan);
                var bad = Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.Check(fl, plan);
                foreach (var m in plan.Notes) L("  nota: " + m);
                if (bad != null) { L("  CONFERENCIA FALHOU: " + bad); tr.Abort(); return; }
                tr.Commit();
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var m in Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.FitLastRegions(tr, plan)) L("  regiao: " + m);
                tr.Commit();
            }
            L($"  aplicado em {sw.ElapsedMilliseconds} ms");
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var fl = (CivilDb.FeatureLine)tr.GetObject(flId, AcadDb.OpenMode.ForRead);
                var pis = Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.UniquePis(fl);
                var pl = (AcadDb.Polyline)tr.GetObject(plId, AcadDb.OpenMode.ForRead);
                double dmax = 0; foreach (var p in pis) { var c = pl.GetClosestPointTo(new Point3d(p.X, p.Y, 0), false); dmax = Math.Max(dmax, Math.Sqrt((c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y))); }
                // amostra ao longo da FL nova: distancia ate a polilinha (pega arco)
                double smax = 0; var all = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints).Cast<Point3d>().ToList();
                foreach (var p in all) { var c = pl.GetClosestPointTo(new Point3d(p.X, p.Y, 0), false); smax = Math.Max(smax, Math.Sqrt((c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y))); }
                // elevacao nova x antiga no ponto mais proximo
                double zmax = 0; int nz = 0; foreach (var p in all) { double best = 1e9, zb = 0; for (int i = 0; i + 1 < oldAll.Count; i++) { var a = oldAll[i]; var b = oldAll[i + 1]; double ex = b.X - a.X, ey = b.Y - a.Y, l2 = ex * ex + ey * ey; double tt = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * ex + (p.Y - a.Y) * ey) / l2)); double dx = a.X + ex * tt - p.X, dy = a.Y + ey * tt - p.Y, d = Math.Sqrt(dx * dx + dy * dy); if (d < best) { best = d; zb = a.Z + (b.Z - a.Z) * tt; } } if (best < 0.05) { zmax = Math.Max(zmax, Math.Abs(p.Z - zb)); nz++; } }
                int arcs = 0; for (int i = 0; i < pis.Count; i++) { try { if (Math.Abs(fl.GetBulge(i)) > 1e-6) arcs++; } catch { } }
                L($"  depois: handle {fl.Handle} (igual {fl.Handle.ToString() == h0}), PI {pis.Count}, EL {fl.ElevationPointsCount}, arcos {arcs}, comp {fl.Length2D:0.000} (polilinha {pl.Length:0.000}); vertices fora da polilinha max {dmax * 1000:0.0} mm; pontos fora max {smax * 1000:0.0} mm; elevacao x antiga (pontos a menos de 5 cm da antiga: {nz}) max {zmax * 1000:0.0} mm");
                foreach (var b in plan.Baselines)
                {
                    var c = (CivilDb.Corridor)tr.GetObject(b.CorridorId, AcadDb.OpenMode.ForRead);
                    var bl = c.Baselines.Cast<CivilDb.Baseline>().First(x => x.Name == b.Baseline);
                    L($"  baseline {b.Baseline}: FL {(bl.FeatureLineId == flId ? "a mesma" : "OUTRA")}, regioes " + string.Join(", ", bl.BaselineRegions.Cast<CivilDb.BaselineRegion>().Select(r => $"{r.Name} {r.StartStation:0.000}-{r.EndStation:0.000}")) + $", baseline {bl.StartStation:0.000}-{bl.EndStation:0.000}");
                }
                tr.Commit();
            }
            sw.Restart();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var flx = (CivilDb.FeatureLine)tr.GetObject(flId, AcadDb.OpenMode.ForRead);
                var pp = flx.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
                for (int i = 0; i < pp.Count; i++) { double bb = double.NaN; try { bb = flx.GetBulge(i); } catch { } L($"  PI{i} {pp[i].X - 601500:0.000},{pp[i].Y - 7788800:0.000},{pp[i].Z:0.000} bulge {bb:0.0000}"); }
                L($"  curvas {flx.CurvesCount} comp2d {flx.Length2D:0.000} pontos {flx.PointsCount} closed {flx.Closed} curve? {(flx is AcadDb.Curve)}");
                if (flx is AcadDb.Curve cv)
                {
                    var plx = (AcadDb.Polyline)tr.GetObject(plId, AcadDb.OpenMode.ForRead);
                    double ln = cv.GetDistanceAtParameter(cv.EndParam); double worst = 0; Point3d wp = default;
                    for (double s = 0; s <= ln; s += 0.02) { var p = cv.GetPointAtDist(Math.Min(s, ln)); var q = plx.GetClosestPointTo(new Point3d(p.X, p.Y, 0), false); double dd = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y)); if (dd > worst) { worst = dd; wp = p; } }
                    L($"  amostra ao longo da FL (curve len {ln:0.000}): maior afastamento da polilinha {worst * 1000:0.0} mm em {wp.X - 601500:0.000},{wp.Y - 7788800:0.000}");
                }
                tr.Commit();
            }
            if (Environment.GetEnvironmentVariable("Z7NOREBUILD") == "1") return;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var id in plan.Baselines.Select(b => b.CorridorId).Distinct())
                {
                    var c = (CivilDb.Corridor)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                    try { c.Rebuild(); L($"  rebuild {c.Name} ok {sw.ElapsedMilliseconds} ms"); } catch (System.Exception ex) { L($"  rebuild {c.Name} ERRO {ex.Message}"); }
                    foreach (var b in plan.Baselines.Where(b => b.CorridorId == id))
                    {
                        var bl = c.Baselines.Cast<CivilDb.Baseline>().First(x => x.Name == b.Baseline);
                        L($"  apos rebuild: baseline {bl.StartStation:0.000}-{bl.EndStation:0.000}, regioes " + string.Join(", ", bl.BaselineRegions.Cast<CivilDb.BaselineRegion>().Select(r => $"{r.Name} {r.StartStation:0.000}-{r.EndStation:0.000} secoes {r.SortedStations().Length}")));
                    }
                }
                tr.Commit();
            }
            // planta antes x depois
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var fl = (CivilDb.FeatureLine)tr.GetObject(flId, AcadDb.OpenMode.ForRead);
                var outp = Path.Combine(Environment.GetEnvironmentVariable("Z5DIR")!, $"fl-{name.Replace(' ', '_')}.csv");
                var lines = new List<string>();
                foreach (var p in oldAll) lines.Add($"ANTES;{p.X:0.000};{p.Y:0.000};{p.Z:0.000}".Replace(',', '.').Replace(';', ','));
                foreach (Point3d p in fl.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints)) lines.Add($"DEPOIS;{p.X:0.000};{p.Y:0.000};{p.Z:0.000}".Replace(',', '.').Replace(';', ','));
                var pl = (AcadDb.Polyline)tr.GetObject(plId, AcadDb.OpenMode.ForRead);
                for (double s = 0; s <= pl.Length; s += 0.05) { var p = pl.GetPointAtDist(s); lines.Add($"PL;{p.X:0.000};{p.Y:0.000};0".Replace(',', '.').Replace(';', ',')); }
                File.WriteAllLines(outp, lines.Select(x => x.Replace(',', ';')));
                tr.Commit();
            }
        }
        catch (System.Exception ex) { L("ERRO GEO: " + ex); }
    }
    [CommandMethod("ZZT7BULGE")]
    public static void BulgeProbe()
    {
        try
        {
            var db = AcApp.DocumentManager.MdiActiveDocument.Database;
            using var tr = db.TransactionManager.StartTransaction();
            foreach (var nm in new[] { "SARJETA 03", "SARJETA 16" })
            {
                var fl = FindFl(tr, db, nm)!; fl.UpgradeOpen();
                L($"{nm}: EL {fl.ElevationPointsCount} PI {fl.PIPointsCount} curves {fl.CurvesCount}");
                for (int i = 0; i < 4; i++) { try { fl.SetBulge(i, 0.2); L($"  SetBulge({i}) ok -> {fl.GetBulge(i):0.000}"); } catch (System.Exception ex) { L($"  SetBulge({i}) ERRO {ex.Message}"); } }
                fl.DeleteElevationPoints(fl.GetPoints(Autodesk.Civil.FeatureLinePointType.ElevationPoint));
                L($"  sem EL: curves {fl.CurvesCount}");
                for (int i = 4; i < 8; i++) { try { fl.SetBulge(i, 0.2); L($"  SetBulge({i}) ok -> {fl.GetBulge(i):0.000} curves {fl.CurvesCount}"); } catch (System.Exception ex) { L($"  SetBulge({i}) ERRO {ex.Message}"); } }
                try
                {
                    object com = fl.AcadObject; var p = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint)[9];
                    com.GetType().InvokeMember("SetBulgeAtPoint", System.Reflection.BindingFlags.InvokeMethod, null, com, new object[] { new double[] { p.X, p.Y, p.Z }, 0.2 }); L($"  COM SetBulgeAtPoint PI9 ok -> GetBulge(9) {fl.GetBulge(9):0.000} curves {fl.CurvesCount}");
                }
                catch (System.Exception ex) { L("  COM ERRO " + ex.Message); }
            }
            tr.Commit();
        }
        catch (System.Exception ex) { L("ERRO " + ex); }
    }
    [CommandMethod("ZZT7EXTCLOSED")]
    public static void ExtClosed()
    {
        try
        {
            var db = AcApp.DocumentManager.MdiActiveDocument.Database;
            using var tr = db.TransactionManager.StartTransaction();
            var fl = FindFl(tr, db, "SARJETA 16")!; fl.UpgradeOpen();
            fl.DeleteElevationPoints(fl.GetPoints(Autodesk.Civil.FeatureLinePointType.ElevationPoint));
            L("antes " + Pis(fl) + $" curves {fl.CurvesCount}");
            var p = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
            try { fl.ExtendWithFixedLine(false, new Point3d(p[0].X + 3, p[0].Y + 3, p[0].Z)); L("extend fim: " + Pis(fl) + $" closed {fl.Closed}"); } catch (System.Exception ex) { L("extend fim ERRO " + ex.Message); }
            try { fl.ExtendWithFixedLine(true, new Point3d(p[0].X - 3, p[0].Y + 3, p[0].Z)); L("extend inicio: " + Pis(fl) + $" closed {fl.Closed}"); } catch (System.Exception ex) { L("extend inicio ERRO " + ex.Message); }
            // flatten arcs
            for (int k = fl.CurvesCount - 1; k >= 0; k--) { try { fl.SetBulge(k, 0); } catch (System.Exception ex) { L($"flat {k} ERRO {ex.Message}"); } }
            L($"apos SetBulge 0: curves {fl.CurvesCount}");
            tr.Commit();
        }
        catch (System.Exception ex) { L("ERRO " + ex); }
    }
    [CommandMethod("ZZT7BL")]
    public static void BlApi()
    {
        foreach (var ty in new[] { typeof(CivilDb.Baseline), typeof(CivilDb.BaselineRegion) })
            foreach (var m in ty.GetMethods().Where(m => m.Name.Contains("Feature") || m.Name.Contains("Set") || m.Name.Contains("Station")).OrderBy(m => m.Name))
                L(ty.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
        try
        {
            var db = AcApp.DocumentManager.MdiActiveDocument.Database;
            using var tr = db.TransactionManager.StartTransaction();
            foreach (var nm in new[] { "SARJETA 11", "SARJETA 12", "SARJETA 13", "SARJETA 14", "SARJETA 15", "SARJETA 16", "SARJETA 17", "SARJETA 01", "SARJETA 03" })
            {
                var fl = FindFl(tr, db, nm); if (fl == null) continue;
                int arcs = 0; int n = fl.PIPointsCount; for (int i = 0; i < n; i++) { try { if (Math.Abs(fl.GetBulge(i)) > 1e-9) arcs++; } catch { } }
                L($"{nm}: fechada {fl.Closed} PI {n} curvas {fl.CurvesCount} bulges!=0 {arcs}");
            }
            tr.Commit();
        }
        catch (System.Exception ex) { L("ERRO " + ex.Message); }
    }
    static string Segs(CivilDb.FeatureLine fl)
    {
        var cv = (AcadDb.Curve)(object)fl; var p = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
        var list = new List<string>();
        for (int i = 0; i + 1 < p.Count; i++)
        {
            double d0 = cv.GetDistAtPoint(cv.GetClosestPointTo(p[i], false)), d1 = i + 1 == p.Count - 1 && fl.Closed ? cv.GetDistanceAtParameter(cv.EndParam) : cv.GetDistAtPoint(cv.GetClosestPointTo(p[i + 1], false));
            double ch = Math.Sqrt((p[i + 1].X - p[i].X) * (p[i + 1].X - p[i].X) + (p[i + 1].Y - p[i].Y) * (p[i + 1].Y - p[i].Y));
            double dl = (d1 - d0);
            double b = double.NaN; try { b = fl.GetBulge(i); } catch { }
            if (Math.Abs(dl - ch) > 0.001) list.Add($"seg{i} arco (len {dl:0.000} corda {ch:0.000} getbulge {b:0.0000})");
        }
        return $"PI {p.Count} curvas {fl.CurvesCount} len {fl.Length2D:0.000}: " + string.Join("; ", list);
    }
    [CommandMethod("ZZT7ARC")]
    public static void ArcProbe()
    {
        try
        {
            var db = AcApp.DocumentManager.MdiActiveDocument.Database;
            using var tr = db.TransactionManager.StartTransaction();
            var fl = FindFl(tr, db, "SARJETA 16")!; fl.UpgradeOpen();
            fl.DeleteElevationPoints(fl.GetPoints(Autodesk.Civil.FeatureLinePointType.ElevationPoint));
            L("inicio " + Segs(fl));
            var p = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
            // ve qual segmento e arco e apaga a ponta final dele
            var cv = (AcadDb.Curve)(object)fl;
            int arcSeg = -1;
            for (int i = 0; i + 2 < p.Count; i++) { double d0 = cv.GetDistAtPoint(cv.GetClosestPointTo(p[i], false)), d1 = cv.GetDistAtPoint(cv.GetClosestPointTo(p[i + 1], false)); if (Math.Abs((d1 - d0) - p[i].DistanceTo(p[i + 1])) > 0.01) { arcSeg = i; break; } }
            L($"primeiro arco no segmento {arcSeg}");
            if (arcSeg >= 0)
            {
                try { fl.SetBulge(arcSeg, 1e-7); L("SetBulge(arcSeg,1e-7): " + Segs(fl)); } catch (System.Exception ex) { L("SetBulge arcSeg ERRO " + ex.Message); }
                p = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
                fl.DeletePIPoint(p[arcSeg + 1]); L("apaga ponta do arco: " + Segs(fl));
            }
            tr.Commit();
        }
        catch (System.Exception ex) { L("ERRO " + ex); }
    }
    [CommandMethod("ZZT7RULE")]
    public static void Rule()
    {
        try
        {
            var db = AcApp.DocumentManager.MdiActiveDocument.Database;
            AcadDb.ObjectId flId;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = (AcadDb.BlockTableRecord)tr.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db), AcadDb.OpenMode.ForWrite);
                var pl = new AcadDb.Polyline(); double ox = 700000, oy = 7000000;
                var pts = new[] { (0.0, 0.0), (100.0, 0.0), (100.0, 2.0), (0.0, 2.0) };
                for (int i = 0; i < pts.Length; i++) pl.AddVertexAt(i, new Point2d(ox + pts[i].Item1, oy + pts[i].Item2), 0, 0, 0);
                pl.Closed = true; ms.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);
                flId = CivilDb.FeatureLine.Create("ZZRULE", pl.ObjectId);
                tr.Commit();
            }
            void Try(string what, double x, double y, double z)
            {
                using var tr = db.TransactionManager.StartTransaction();
                var fl = (CivilDb.FeatureLine)tr.GetObject(flId, AcadDb.OpenMode.ForWrite);
                fl.InsertPIPoint(new Point3d(700000 + x, 7000000 + y, z));
                var p = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
                L($"{what} ({x},{y},{z}): " + string.Join(" ", p.Cast<Point3d>().Select(q => $"({q.X - 700000:0.##},{q.Y - 7000000:0.##},{q.Z:0.##})")));
                tr.Commit();
            }
            Try("perto de baixo", 50, 0.2, 0);
            Try("perto de cima z0", 30, 1.7, 0);
            Try("perto de cima z alto", 70, 1.7, 50);
            Try("fora abaixo longe", 20, -5, 0);
        }
        catch (System.Exception ex) { L("ERRO " + ex); }
    }
    [CommandMethod("ZZT7COMARC")]
    public static void ComArc()
    {
        try
        {
            var asm = System.Reflection.Assembly.LoadFrom(@"C:\Program Files\Autodesk\AutoCAD 2026\C3D\Autodesk.AECC.Interop.Land.dll");
            var itf = asm.GetType("Autodesk.AECC.Interop.Land.IAeccLandFeatureLine")!;
            var db = AcApp.DocumentManager.MdiActiveDocument.Database;
            foreach (var nm in new[] { "SARJETA 11", "SARJETA 16" })
            {
                using var tr = db.TransactionManager.StartTransaction();
                var fl = FindFl(tr, db, nm)!; fl.UpgradeOpen();
                fl.DeleteElevationPoints(fl.GetPoints(Autodesk.Civil.FeatureLinePointType.ElevationPoint));
                object com = fl.AcadObject;
                L($"{nm}: com tipo {com.GetType().FullName}; implementa? {itf.IsInstanceOfType(com)}; " + Segs(fl));
                var p = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
                // segmento reto qualquer: o primeiro que nao e arco
                var arcs = new HashSet<int>(); var cv = (AcadDb.Curve)(object)fl;
                int seg = 0; for (; seg + 1 < p.Count; seg++) { double d0 = cv.GetDistAtPoint(cv.GetClosestPointTo(p[seg], false)), d1 = cv.GetDistAtPoint(cv.GetClosestPointTo(p[seg + 1], false)); if (Math.Abs((d1 - d0) - p[seg].DistanceTo(p[seg + 1])) < 0.0005 && p[seg].DistanceTo(p[seg + 1]) > 1) break; }
                try
                {
                    var m = itf.GetMethod("SetBulgeAtPoint")!;
                    m.Invoke(com, new object[] { new double[] { p[seg].X, p[seg].Y, p[seg].Z }, 0.2 });
                    L($"  COM SetBulgeAtPoint no segmento reto {seg}: ok -> " + Segs(fl));
                }
                catch (System.Exception ex) { L($"  COM SetBulgeAtPoint ERRO {(ex.InnerException ?? ex).Message}"); }
                try
                {
                    var g = itf.GetMethod("GetBulgeAtPoint")!;
                    L($"  COM GetBulgeAtPoint({seg}) = {g.Invoke(com, new object[] { new double[] { p[seg].X, p[seg].Y, p[seg].Z } })}");
                }
                catch (System.Exception ex) { L($"  COM GetBulgeAtPoint ERRO {(ex.InnerException ?? ex).Message}"); }
                tr.Commit();
            }
        }
        catch (System.Exception ex) { L("ERRO " + ex); }
    }
    [CommandMethod("ZZT7NATIVE", CommandFlags.Modal)]
    public static void Native()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument; var db = doc.Database; var ed = doc.Editor;
        AcadDb.ObjectId flId; Point3d p0, p1;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var fl = FindFl(tr, db, "SARJETA 11")!; flId = fl.ObjectId;
            var p = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.PIPoint);
            p0 = p[0]; p1 = new Point3d((p[0].X + p[1].X) / 2, (p[0].Y + p[1].Y) / 2, (p[0].Z + p[1].Z) / 2);
            L($"antes: handle {fl.Handle} closed {fl.Closed} PI {fl.PIPointsCount} len {fl.Length2D:0.000}");
            tr.Commit();
        }
        void State(string what)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var ms = (AcadDb.BlockTableRecord)tr.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db), AcadDb.OpenMode.ForRead);
            var fls = new List<string>(); foreach (AcadDb.ObjectId id in ms) if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is CivilDb.FeatureLine f && f.Name.StartsWith("SARJETA 11")) fls.Add($"{f.Name} h={f.Handle} closed={f.Closed} PI={f.PIPointsCount} len={f.Length2D:0.000} erased={f.IsErased}");
            L($"{what}: " + string.Join(" | ", fls));
            tr.Commit();
        }
        ed.Command("_.ZOOM", "_E");
        foreach (var attempt in new Func<object[]>[] {
            () => new object[] { "_.AeccBreakFeatures", new AcadDb.ObjectId[] { flId }, "_F", p1, p1 },
            () => new object[] { "_.BreakFeatures", p1, "_F", p1, p1 },
            () => new object[] { "_.AeccBreakFeatureLines", p1, "_F", p1, p1 },
        })
        {
            object[] args = attempt();
            try { ed.Command(args); L($"comando {args[0]}: rodou"); } catch (System.Exception ex) { L($"comando {args[0]}: ERRO {ex.Message}"); }
            State("depois de " + args[0]);
        }
    }
    static AcadDb.ObjectId TestPoly(AcadDb.Transaction tr, AcadDb.Database db, CivilDb.FeatureLine fl)
    {
        var pis = Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.UniquePis(fl); bool closed = fl.Closed;
        var pl = new AcadDb.Polyline(); int k = 0;
        for (int i = 0; i < pis.Count; i++)
        {
            if (i > 0 && i < pis.Count - 1 && i % 3 == 0) continue;
            var p = new Point2d(pis[i].X, pis[i].Y);
            if (i == pis.Count / 2 && i > 0 && i < pis.Count - 1) { var a = pis[i - 1]; var b = pis[i + 1]; var d = new Vector2d(b.X - a.X, b.Y - a.Y).GetNormal(); p = p + new Vector2d(-d.Y, d.X) * 0.4; }
            pl.AddVertexAt(k++, p, 0, 0, 0);
        }
        if (!closed) { var e = pis[^1]; var e1 = pis[^2]; var d = new Vector2d(e.X - e1.X, e.Y - e1.Y).GetNormal(); pl.AddVertexAt(k++, new Point2d(e.X, e.Y) + d * 2.0, 0, 0, 0); }
        pl.Closed = closed;
        int lg = 0; double best = 0; for (int i = 0; i < pl.NumberOfVertices - (closed ? 0 : 1); i++) { double l = pl.GetPoint2dAt(i).GetDistanceTo(pl.GetPoint2dAt((i + 1) % pl.NumberOfVertices)); if (l > best) { best = l; lg = i; } }
        pl.SetBulgeAt(lg, 0.15);
        var ms = (AcadDb.BlockTableRecord)tr.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db), AcadDb.OpenMode.ForWrite);
        ms.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true); return pl.ObjectId;
    }

    [CommandMethod("ZZT7SWAP")]
    public static void Swap()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument; var db = doc.Database;
        string name = Environment.GetEnvironmentVariable("Z7FL") ?? "SARJETA 16";
        string mode = Environment.GetEnvironmentVariable("Z7SWAP") ?? "swap";
        try
        {
            AcadDb.ObjectId oldId, plId; string h0; List<Point3d> oldAll; AcadDb.ObjectId siteId;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var fl = FindFl(tr, db, name)!; oldId = fl.ObjectId; h0 = fl.Handle.ToString(); siteId = fl.SiteId;
                oldAll = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints).Cast<Point3d>().ToList();
                plId = TestPoly(tr, db, fl);
                tr.Commit();
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.Plan plan;
            AcadDb.ObjectId newId;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var fl = (CivilDb.FeatureLine)tr.GetObject(oldId, AcadDb.OpenMode.ForWrite);
                var pl = (AcadDb.Entity)tr.GetObject(plId, AcadDb.OpenMode.ForRead);
                plan = Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.Prepare(tr, db, fl, pl, false);
                L($"{name}: handle {h0} fechada {fl.Closed} site {(siteId.IsNull ? "-" : "sim")}; plano {plan.V.Count} vertices, arcos {plan.Bulge.Count(b => Math.Abs(b) > 1e-9)}, invertida {plan.Reversed}, girada {plan.Rotated}");
                newId = Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.CreateNew(tr, db, fl, plan);
                tr.Commit();
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nf = (CivilDb.FeatureLine)tr.GetObject(newId, AcadDb.OpenMode.ForRead);
                L($"  nova antes da troca: site {(nf.SiteId == siteId)} nome {nf.Name} PI {nf.PIPointsCount} EL {nf.ElevationPointsCount}");
                Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.SwapIn(tr, oldId, newId, name);
                L($"  swap feito; EL perdidos {plan.ElevationLost}; EL {plan.ElevationBeforeWeed} -> {plan.ElevationPoints.Count} (enxugados {plan.ElevationWeeded})");
                tr.Commit();
            }
            L($"  aplicado em {sw.ElapsedMilliseconds} ms");
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var fl = (CivilDb.FeatureLine)tr.GetObject(oldId, AcadDb.OpenMode.ForRead);
                var cv = (AcadDb.Curve)(object)fl; var pl = (AcadDb.Polyline)tr.GetObject(plId, AcadDb.OpenMode.ForRead);
                double len = cv.GetDistanceAtParameter(cv.EndParam), worst = 0;
                for (double s = 0; s <= len; s += 0.05) { var p = cv.GetPointAtDist(Math.Min(s, len)); var q = pl.GetClosestPointTo(new Point3d(p.X, p.Y, 0), false); worst = Math.Max(worst, Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y))); }
                double zmax = 0; int nz = 0; foreach (Point3d p in fl.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints)) { double bd = 1e9, zb = 0; for (int i = 0; i + 1 < oldAll.Count; i++) { var aa = oldAll[i]; var bb = oldAll[i + 1]; double ex = bb.X - aa.X, ey = bb.Y - aa.Y, l2 = ex * ex + ey * ey; double tt = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - aa.X) * ex + (p.Y - aa.Y) * ey) / l2)); double dx = aa.X + ex * tt - p.X, dy = aa.Y + ey * tt - p.Y, d = Math.Sqrt(dx * dx + dy * dy); if (d < bd) { bd = d; zb = aa.Z + (bb.Z - aa.Z) * tt; } } if (bd < 0.05) { zmax = Math.Max(zmax, Math.Abs(p.Z - zb)); nz++; } }
                { var st = (CivilDb.Site)tr.GetObject(siteId, AcadDb.OpenMode.ForRead); bool inSite = false; foreach (AcadDb.ObjectId i in st.GetFeatureLineIds()) if (i == oldId) inSite = true; L($"  site {st.Name}: contem a FL {inSite}; FL.SiteId {(fl.SiteId == siteId)}"); }
                L($"  depois: id {(fl.ObjectId == oldId)} handle {fl.Handle} (igual {fl.Handle.ToString() == h0}) nome '{fl.Name}' site {(fl.SiteId == siteId)} fechada {fl.Closed} PI {fl.PIPointsCount} EL {fl.ElevationPointsCount} arcos {Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.ArcSegments(fl).Count}; comp {len:0.000} (polilinha {pl.Length:0.000}); maior afastamento {worst * 1000:0.0} mm; elevacao x antiga ({nz} pontos) max {zmax * 1000:0.0} mm");
                foreach (var b in plan.Baselines)
                {
                    var c = (CivilDb.Corridor)tr.GetObject(b.CorridorId, AcadDb.OpenMode.ForRead);
                    var bl = c.Baselines.Cast<CivilDb.Baseline>().First(x => x.Name == b.Baseline);
                    L($"  baseline {b.Baseline}: FL {(bl.FeatureLineId == oldId ? "a mesma" : "OUTRA")}, {bl.StartStation:0.000}-{bl.EndStation:0.000}");
                }
                int flCount = 0; var ms = (AcadDb.BlockTableRecord)tr.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db), AcadDb.OpenMode.ForRead);
                foreach (AcadDb.ObjectId id in ms) if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is CivilDb.FeatureLine f && (f.Name == name || f.Name.StartsWith("ZZ_NOVA"))) flCount++;
                L($"  feature lines com esse nome/ZZ_NOVA no desenho: {flCount}");
                tr.Commit();
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var m in Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.FitLastRegions(tr, plan)) L("  regiao: " + m);
                tr.Commit();
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var id in plan.Baselines.Select(b => b.CorridorId).Distinct())
                {
                    var c = (CivilDb.Corridor)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                    var s2 = System.Diagnostics.Stopwatch.StartNew();
                    try { c.Rebuild(); L($"  rebuild {c.Name} ok {s2.ElapsedMilliseconds} ms"); } catch (System.Exception ex) { L($"  rebuild {c.Name} ERRO {ex.Message}"); }
                    foreach (var b in plan.Baselines.Where(b => b.CorridorId == id))
                    {
                        var bl = c.Baselines.Cast<CivilDb.Baseline>().First(x => x.Name == b.Baseline);
                        L($"  apos rebuild: baseline {bl.StartStation:0.000}-{bl.EndStation:0.000}, regioes " + string.Join(", ", bl.BaselineRegions.Cast<CivilDb.BaselineRegion>().Select(r => $"{r.StartStation:0.000}-{r.EndStation:0.000} secoes {r.SortedStations().Length}")));
                    }
                }
                tr.Commit();
            }
        }
        catch (System.Exception ex) { L("ERRO SWAP: " + ex); }
    }
    [CommandMethod("ZZT7CHECK")]
    public static void Check()
    {
        var db = AcApp.DocumentManager.MdiActiveDocument.Database;
        string name = Environment.GetEnvironmentVariable("Z7FL") ?? "SARJETA 16";
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var ms = (AcadDb.BlockTableRecord)tr.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db), AcadDb.OpenMode.ForRead);
            var list = new List<string>();
            foreach (AcadDb.ObjectId id in ms) if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is CivilDb.FeatureLine f && (f.Name == name || f.Name.StartsWith("ZZ_NOVA"))) list.Add($"{f.Name} h={f.Handle} PI={f.PIPointsCount} len={f.Length2D:0.000} arcos={Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometry.ArcSegments(f).Count} site={(f.SiteId.IsNull ? "-" : "sim")}");
            L("reaberto: " + string.Join(" | ", list));
            var site = Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument.GetSiteIds();
            foreach (AcadDb.ObjectId sid in site) { var s = (CivilDb.Site)tr.GetObject(sid, AcadDb.OpenMode.ForRead); var ids = s.GetFeatureLineIds(); int bad = 0; foreach (AcadDb.ObjectId i in ids) if (i.IsErased || !i.IsValid) bad++; L($"site {s.Name}: {ids.Count} feature lines, {bad} invalidas"); }
            foreach (AcadDb.ObjectId cid in Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument.CorridorCollection)
            {
                var c = (CivilDb.Corridor)tr.GetObject(cid, AcadDb.OpenMode.ForWrite);
                try { c.Rebuild(); L($"rebuild {c.Name} ok"); } catch (System.Exception ex) { L($"rebuild {c.Name} ERRO {ex.Message}"); }
                foreach (CivilDb.Baseline b in c.Baselines) { AcadDb.ObjectId fid = AcadDb.ObjectId.Null; try { fid = b.FeatureLineId; } catch { continue; } if (b.Name.StartsWith(name)) L($"baseline {b.Name}: FL h={fid.Handle} {b.StartStation:0.000}-{b.EndStation:0.000}"); }
            }
            tr.Commit();
        }
        catch (System.Exception ex) { L("ERRO CHECK: " + ex); }
    }
    [CommandMethod("ZZT7REG")]
    public static void Reg()
    {
        var db = AcApp.DocumentManager.MdiActiveDocument.Database;
        using var tr = db.TransactionManager.StartTransaction();
        foreach (AcadDb.ObjectId cid in Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument.CorridorCollection)
        {
            var c = (CivilDb.Corridor)tr.GetObject(cid, AcadDb.OpenMode.ForRead);
            foreach (CivilDb.Baseline b in c.Baselines) if (b.Name.StartsWith("SARJETA 17")) { int i = 0; foreach (CivilDb.BaselineRegion r in b.BaselineRegions) L($"{b.Name} [{i++}] {r.Name} {r.StartStation:0.000}-{r.EndStation:0.000}"); }
        }
        tr.Commit();
    }

    [CommandMethod("ZZT7API")]
    public static void Api()
    {
        try { Api0(); } catch (System.Exception ex) { L("ERRO " + ex); }
    }
    static void Api0()
    {
        var t = typeof(CivilDb.FeatureLine);
        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).OrderBy(m => m.Name))
            L("M " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ") : " + m.ReturnType.Name);
        foreach (var p in t.GetProperties().OrderBy(p => p.Name))
            L("P " + p.Name + " : " + p.PropertyType.Name + (p.CanWrite ? " rw" : ""));
        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).OrderBy(m => m.Name))
            L("S " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
        var db = AcApp.DocumentManager.MdiActiveDocument.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var civ = Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument;
        L("corredores: " + civ.CorridorCollection.Count);
        int nfl = 0; var bt = (AcadDb.BlockTableRecord)tr.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db), AcadDb.OpenMode.ForRead); foreach (AcadDb.ObjectId id in bt) if (id.ObjectClass.DxfName.Contains("FEATURE")) nfl++; L("feature lines no model: " + nfl);
        foreach (AcadDb.ObjectId cid in civ.CorridorCollection)
        {
            var c = (CivilDb.Corridor)tr.GetObject(cid, AcadDb.OpenMode.ForRead);
            foreach (CivilDb.Baseline b in c.Baselines)
            {
                AcadDb.ObjectId fid = AcadDb.ObjectId.Null; try { fid = b.FeatureLineId; } catch { }
                L($"BL0 {c.Name} | {b.Name} |  fl={(fid.IsNull ? "-" : fid.Handle.ToString())}");
                if (fid.IsNull) continue;
                var fl = tr.GetObject(fid, AcadDb.OpenMode.ForRead) as CivilDb.FeatureLine;
                L($"BL {c.Name} | {b.Name} | FL {fl?.Name} h={fid.Handle} site={(fl?.SiteId.IsNull == false ? "sim" : "nao")} PI={fl?.PIPointsCount} EL={fl?.ElevationPointsCount} len={fl?.Length2D:0.00} closed={fl?.Closed} ref={fl?.IsReferenceObject} regions={b.BaselineRegions.Count} st={b.StartStation:0.00}-{b.EndStation:0.00}");
            }
        }
        tr.Commit();
    }
}



































