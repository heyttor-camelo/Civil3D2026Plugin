using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.FeatureLines
{
    public class FeatureLineRenameCommands
    {
        private class FlHit
        {
            public ObjectId Id { get; set; }
            public double T { get; set; }
        }

        [CommandMethod(C3DCommands.FeatureLines.RenomearCorte, CommandFlags.UsePickSet)]
        public void FLRenomearCorte()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            try
            {
                PromptSelectionResult preSelRes = ed.SelectImplied();

                if (preSelRes.Status != PromptStatus.OK || preSelRes.Value.Count == 0)
                {
                    ed.WriteMessage("\nSelecione previamente as Feature Lines antes de rodar o comando.");
                    return;
                }

                List<ObjectId> featureLineIds = new List<ObjectId>();

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject so in preSelRes.Value)
                    {
                        if (so == null)
                            continue;

                        FeatureLine? fl = tr.GetObject(so.ObjectId, OpenMode.ForRead) as FeatureLine;

                        if (fl != null)
                            featureLineIds.Add(so.ObjectId);
                    }

                    tr.Commit();
                }

                if (featureLineIds.Count == 0)
                {
                    ed.WriteMessage("\nA seleção prévia não contém Feature Lines.");
                    return;
                }

                ed.WriteMessage($"\nFeature Lines selecionadas para análise: {featureLineIds.Count}");

                PromptStringOptions nomeOpts = new PromptStringOptions(
                    "\nTemplate do novo nome. Use {n} para número e {old} para nome antigo <FL-{n}>: ")
                {
                    AllowSpaces = true,
                    DefaultValue = "FL-{n}",
                    UseDefaultValue = true
                };

                PromptResult nomeRes = ed.GetString(nomeOpts);

                if (nomeRes.Status != PromptStatus.OK)
                    return;

                string template = nomeRes.StringResult;

                PromptIntegerOptions inicioOpts = new PromptIntegerOptions("\nNúmero inicial <1>: ")
                {
                    DefaultValue = 1,
                    UseDefaultValue = true,
                    AllowNegative = false,
                    AllowZero = false
                };

                PromptIntegerResult inicioRes = ed.GetInteger(inicioOpts);

                if (inicioRes.Status != PromptStatus.OK)
                    return;

                int numero = inicioRes.Value;

                PromptIntegerOptions casasOpts = new PromptIntegerOptions("\nQuantidade de casas do número <3>: ")
                {
                    DefaultValue = 3,
                    UseDefaultValue = true,
                    AllowNegative = false,
                    AllowZero = true
                };

                PromptIntegerResult casasRes = ed.GetInteger(casasOpts);

                if (casasRes.Status != PromptStatus.OK)
                    return;

                int casas = casasRes.Value;

                int totalRenomeadas = 0;
                HashSet<ObjectId> jaRenomeadas = new HashSet<ObjectId>();

                while (true)
                {
                    PromptPointOptions p1Opts = new PromptPointOptions(
                        "\nPrimeiro ponto do corte ou ENTER para finalizar: ")
                    {
                        AllowNone = true
                    };

                    PromptPointResult p1Res = ed.GetPoint(p1Opts);

                    if (p1Res.Status == PromptStatus.None)
                        break;

                    if (p1Res.Status != PromptStatus.OK)
                        return;

                    PromptPointOptions p2Opts = new PromptPointOptions("\nSegundo ponto do corte: ")
                    {
                        BasePoint = p1Res.Value,
                        UseBasePoint = true
                    };

                    PromptPointResult p2Res = ed.GetPoint(p2Opts);

                    if (p2Res.Status != PromptStatus.OK)
                        return;

                    Point3d p1 = p1Res.Value;
                    Point3d p2 = p2Res.Value;

                    if (p1.DistanceTo(p2) < 0.000001)
                    {
                        ed.WriteMessage("\nCorte inválido.");
                        continue;
                    }

                    List<FlHit> hits = new List<FlHit>();

                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId id in featureLineIds)
                        {
                            if (jaRenomeadas.Contains(id))
                                continue;

                            FeatureLine? fl = tr.GetObject(id, OpenMode.ForRead) as FeatureLine;

                            if (fl == null)
                                continue;

                            if (FeatureLineCortada(fl, p1, p2, out double t))
                            {
                                hits.Add(new FlHit
                                {
                                    Id = id,
                                    T = t
                                });
                            }
                        }

                        hits = hits
                            .GroupBy(x => x.Id)
                            .Select(g => g.OrderBy(x => x.T).First())
                            .OrderBy(x => x.T)
                            .ToList();

                        int renomeadasNesteCorte = 0;

                        foreach (FlHit hit in hits)
                        {
                            FeatureLine? fl = tr.GetObject(hit.Id, OpenMode.ForWrite) as FeatureLine;

                            if (fl == null)
                                continue;

                            string nomeAntigo = fl.Name;

                            string numeroFormatado = casas > 0
                                ? numero.ToString(new string('0', casas))
                                : numero.ToString();

                            string novoNome = template
                                .Replace("{n}", numeroFormatado)
                                .Replace("{old}", nomeAntigo);

                            fl.Name = novoNome;

                            jaRenomeadas.Add(hit.Id);

                            numero++;
                            renomeadasNesteCorte++;
                            totalRenomeadas++;
                        }

                        tr.Commit();

                        ed.WriteMessage($"\nFeature Lines selecionadas cortadas neste segmento: {renomeadasNesteCorte}");
                    }
                }

                ed.WriteMessage($"\nFinalizado. Total de Feature Lines renomeadas: {totalRenomeadas}");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nErro: {ex.Message}");
            }
        }

        private static bool FeatureLineCortada(FeatureLine fl, Point3d corteP1, Point3d corteP2, out double menorT)
        {
            menorT = double.MaxValue;

            Point3dCollection pts = fl.GetPoints(FeatureLinePointType.AllPoints);

            if (pts.Count < 2)
                return false;

            bool cortou = false;

            for (int i = 0; i < pts.Count - 1; i++)
            {
                Point3d a = pts[i];
                Point3d b = pts[i + 1];

                if (SegmentosInterceptam2D(corteP1, corteP2, a, b, out double t))
                {
                    if (t < menorT)
                        menorT = t;

                    cortou = true;
                }
            }

            return cortou;
        }

        private static bool SegmentosInterceptam2D(
            Point3d p,
            Point3d p2,
            Point3d q,
            Point3d q2,
            out double t)
        {
            t = 0;

            const double eps = 0.0000001;

            double rx = p2.X - p.X;
            double ry = p2.Y - p.Y;

            double sx = q2.X - q.X;
            double sy = q2.Y - q.Y;

            double qpx = q.X - p.X;
            double qpy = q.Y - p.Y;

            double rxs = Cross(rx, ry, sx, sy);
            double qpxr = Cross(qpx, qpy, rx, ry);

            if (Math.Abs(rxs) < eps && Math.Abs(qpxr) < eps)
            {
                double rr = rx * rx + ry * ry;

                if (rr < eps)
                    return false;

                double t0 = ((q.X - p.X) * rx + (q.Y - p.Y) * ry) / rr;
                double t1 = ((q2.X - p.X) * rx + (q2.Y - p.Y) * ry) / rr;

                double min = Math.Min(t0, t1);
                double max = Math.Max(t0, t1);

                if (max < -eps || min > 1 + eps)
                    return false;

                t = Math.Max(0, min);
                return true;
            }

            if (Math.Abs(rxs) < eps)
                return false;

            t = Cross(qpx, qpy, sx, sy) / rxs;
            double u = Cross(qpx, qpy, rx, ry) / rxs;

            return t >= -eps && t <= 1 + eps && u >= -eps && u <= 1 + eps;
        }

        private static double Cross(double ax, double ay, double bx, double by)
        {
            return ax * by - ay * bx;
        }
    }
}