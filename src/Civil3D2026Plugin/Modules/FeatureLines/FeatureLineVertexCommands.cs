using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.Colors;
using AcadColor = Autodesk.AutoCAD.Colors.Color;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace Civil3D2026Plugin.Modules.FeatureLines
{
    public class FeatureLineVertexCommands
    {
        private enum ElevationApplyMode
        {
            Absolute = 1,
            Delta = 2
        }

        private class VertexSelection
        {
            public ObjectId FeatureLineId { get; set; }
            public int PointIndex { get; set; }
            public Point3d Point { get; set; }
        }

        private class CirclePreviewJig : DrawJig
        {
            private readonly Point2d _center2d;
            private Point2d _current2d;

            public CirclePreviewJig(Point3d center)
            {
                _center2d = new Point2d(center.X, center.Y);
                _current2d = _center2d;
            }

            public double Radius => _center2d.GetDistanceTo(_current2d);

            protected override SamplerStatus Sampler(JigPrompts prompts)
            {
                JigPromptPointOptions opts = new JigPromptPointOptions("\nInforme o segundo ponto para definir o raio: ");
                opts.BasePoint = new Point3d(_center2d.X, _center2d.Y, 0.0);
                opts.UseBasePoint = true;
                opts.Cursor = CursorType.RubberBand;

                PromptPointResult res = prompts.AcquirePoint(opts);

                if (res.Status == PromptStatus.Cancel)
                    return SamplerStatus.Cancel;

                if (res.Status != PromptStatus.OK)
                    return SamplerStatus.NoChange;

                Point2d newPt = new Point2d(res.Value.X, res.Value.Y);

                if (newPt.IsEqualTo(_current2d))
                    return SamplerStatus.NoChange;

                _current2d = newPt;
                return SamplerStatus.OK;
            }

            protected override bool WorldDraw(WorldDraw draw)
            {
                double radius = Radius;
                if (radius > 1e-9)
                {
                    draw.Geometry.Circle(
                        new Point3d(_center2d.X, _center2d.Y, 0.0),
                        radius,
                        Vector3d.ZAxis);
                }

                return true;
            }
        }

        [CommandMethod(C3DCommands.FeatureLines.SetPv)]
        public void SetFeatureLineVerticesElevation()
        {
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor ed = doc.Editor;
            Database db = doc.Database;

            try
            {
                PromptKeywordOptions modeOpts = new PromptKeywordOptions(
                    "\nEscolha o modo de seleção dos vértices [Livre/Circulo] <Livre>: ",
                    "Livre Circulo");
                modeOpts.AllowNone = true;

                PromptResult modeRes = ed.GetKeywords(modeOpts);
                if (modeRes.Status == PromptStatus.Cancel) return;

                string mode = string.IsNullOrWhiteSpace(modeRes.StringResult)
                    ? "Livre"
                    : modeRes.StringResult;

                List<ObjectId> featureLineIds = GetFeatureLinesFromUser(ed);
                if (featureLineIds.Count == 0)
                {
                    ed.WriteMessage("\nNenhuma FeatureLine foi selecionada.");
                    return;
                }

                List<VertexSelection> selectedVertices =
                    mode.Equals("Circulo", StringComparison.OrdinalIgnoreCase)
                    ? SelectVerticesByCircle(ed, db, featureLineIds)
                    : SelectVerticesByPolygon(ed, db, featureLineIds);

                if (selectedVertices.Count == 0)
                {
                    ed.WriteMessage("\nNenhum vértice foi selecionado.");
                    return;
                }

                using (VertexMarkers markers = new VertexMarkers(doc, db, selectedVertices))
                {
                    markers.Show();

                    PromptKeywordOptions confirmOpts = new PromptKeywordOptions(
                        $"\n{selectedVertices.Count} vértice(s) destacados. Confirmar seleção? [Sim/Nao] <Sim>: ",
                        "Sim Nao");
                    confirmOpts.AllowNone = true;

                    PromptResult confirmRes = ed.GetKeywords(confirmOpts);
                    if (confirmRes.Status == PromptStatus.Cancel)
                        return;

                    string confirm = string.IsNullOrWhiteSpace(confirmRes.StringResult)
                        ? "Sim"
                        : confirmRes.StringResult;

                    if (confirm.Equals("Nao", StringComparison.OrdinalIgnoreCase))
                    {
                        ed.WriteMessage("\nOperação cancelada.");
                        return;
                    }

                    ElevationApplyMode applyMode = PromptElevationApplyMode(ed);

                    PromptDoubleOptions valueOpts = new PromptDoubleOptions(
                        applyMode == ElevationApplyMode.Absolute
                            ? "\nDigite a cota final para os vértices selecionados: "
                            : "\nDigite o Delta Z a aplicar (use negativo para descer): ");
                    valueOpts.AllowNone = false;
                    valueOpts.AllowNegative = true;

                    PromptDoubleResult valueRes = ed.GetDouble(valueOpts);
                    if (valueRes.Status != PromptStatus.OK) return;

                    double inputValue = valueRes.Value;

                    ApplyElevationToVertices(doc, db, ed, selectedVertices, inputValue, applyMode);
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nErro: {ex.Message}");
            }
        }

        private static ElevationApplyMode PromptElevationApplyMode(Editor ed)
        {
            PromptKeywordOptions pko = new PromptKeywordOptions(
                "\nModo de aplicação [Cota/Delta] <Cota>: ",
                "Cota Delta");
            pko.AllowNone = true;

            PromptResult pr = ed.GetKeywords(pko);
            if (pr.Status != PromptStatus.OK && pr.Status != PromptStatus.None)
                throw new Autodesk.AutoCAD.Runtime.Exception(Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak);

            string value = string.IsNullOrWhiteSpace(pr.StringResult) ? "Cota" : pr.StringResult;

            return value.Equals("Delta", StringComparison.OrdinalIgnoreCase)
                ? ElevationApplyMode.Delta
                : ElevationApplyMode.Absolute;
        }

        private List<ObjectId> GetFeatureLinesFromUser(Editor ed)
        {
            List<ObjectId> ids = new List<ObjectId>();

            PromptSelectionOptions selOpts = new PromptSelectionOptions();
            selOpts.MessageForAdding = "\nSelecione uma ou mais FeatureLines: ";

            TypedValue[] tvs = new TypedValue[]
            {
                new TypedValue((int)DxfCode.Start, "AECC_FEATURE_LINE")
            };

            SelectionFilter filter = new SelectionFilter(tvs);
            PromptSelectionResult selRes = ed.GetSelection(selOpts, filter);

            if (selRes.Status != PromptStatus.OK || selRes.Value == null)
                return ids;

            SelectionSet ss = selRes.Value;
            foreach (SelectedObject so in ss)
            {
                if (so != null && !so.ObjectId.IsNull)
                    ids.Add(so.ObjectId);
            }

            return ids;
        }

        private List<VertexSelection> SelectVerticesByPolygon(Editor ed, Database db, List<ObjectId> featureLineIds)
        {
            List<VertexSelection> result = new List<VertexSelection>();
            HashSet<string> alreadyAdded = new HashSet<string>();
            List<Point2d> polygon = new List<Point2d>();

            ed.WriteMessage("\nModo livre por polígono.");
            ed.WriteMessage("\nClique os pontos do contorno e pressione Enter para finalizar.");

            PromptPointOptions firstOpts = new PromptPointOptions("\nPrimeiro ponto do polígono: ");
            PromptPointResult firstRes = ed.GetPoint(firstOpts);
            if (firstRes.Status != PromptStatus.OK)
                return result;

            Point3d firstPoint3d = firstRes.Value;
            polygon.Add(new Point2d(firstPoint3d.X, firstPoint3d.Y));

            Point3d previousPoint = firstPoint3d;

            while (true)
            {
                PromptPointOptions nextOpts = new PromptPointOptions("\nPróximo ponto do polígono ou Enter para fechar: ");
                nextOpts.UseBasePoint = true;
                nextOpts.BasePoint = previousPoint;
                nextOpts.AllowNone = true;

                PromptPointResult nextRes = ed.GetPoint(nextOpts);

                if (nextRes.Status == PromptStatus.None)
                    break;

                if (nextRes.Status != PromptStatus.OK)
                    return result;

                previousPoint = nextRes.Value;
                polygon.Add(new Point2d(previousPoint.X, previousPoint.Y));
            }

            if (polygon.Count < 3)
            {
                ed.WriteMessage("\nPolígono inválido. São necessários pelo menos 3 pontos.");
                return result;
            }

            AddVerticesInsidePolygon(db, featureLineIds, polygon, result, alreadyAdded);

            ed.WriteMessage($"\n{result.Count} vértice(s) encontrado(s) dentro do polígono.");
            return result;
        }

        private void AddVerticesInsidePolygon(
            Database db,
            List<ObjectId> featureLineIds,
            List<Point2d> polygon,
            List<VertexSelection> result,
            HashSet<string> alreadyAdded)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId flId in featureLineIds)
                {
                    FeatureLine? fl = tr.GetObject(flId, OpenMode.ForRead) as FeatureLine;
                    if (fl == null) continue;

                    Point3dCollection pts = fl.GetPoints(FeatureLinePointType.AllPoints);
                    for (int i = 0; i < pts.Count; i++)
                    {
                        Point3d pt = pts[i];
                        Point2d pt2d = new Point2d(pt.X, pt.Y);

                        if (!IsPointInsidePolygon(pt2d, polygon))
                            continue;

                        string key = flId.Handle.ToString() + "_" + i;
                        if (alreadyAdded.Contains(key))
                            continue;

                        alreadyAdded.Add(key);
                        result.Add(new VertexSelection
                        {
                            FeatureLineId = flId,
                            PointIndex = i,
                            Point = pt
                        });
                    }
                }

                tr.Commit();
            }
        }

        private bool IsPointInsidePolygon(Point2d point, List<Point2d> polygon)
        {
            bool inside = false;
            int count = polygon.Count;

            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                Point2d pi = polygon[i];
                Point2d pj = polygon[j];

                bool intersect =
                    ((pi.Y > point.Y) != (pj.Y > point.Y)) &&
                    (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / ((pj.Y - pi.Y) == 0.0 ? 1e-12 : (pj.Y - pi.Y)) + pi.X);

                if (intersect)
                    inside = !inside;
            }

            return inside;
        }

        private List<VertexSelection> SelectVerticesByCircle(Editor ed, Database db, List<ObjectId> featureLineIds)
        {
            List<VertexSelection> result = new List<VertexSelection>();
            HashSet<string> alreadyAdded = new HashSet<string>();

            PromptPointOptions centerOpts = new PromptPointOptions("\nInforme o centro do círculo: ");
            PromptPointResult centerRes = ed.GetPoint(centerOpts);
            if (centerRes.Status != PromptStatus.OK) return result;

            Point2d center2d = new Point2d(centerRes.Value.X, centerRes.Value.Y);

            CirclePreviewJig jig = new CirclePreviewJig(new Point3d(center2d.X, center2d.Y, 0.0));
            PromptResult dragRes = ed.Drag(jig);
            if (dragRes.Status != PromptStatus.OK) return result;

            double radius = jig.Radius;
            if (radius <= 1e-9)
            {
                ed.WriteMessage("\nRaio inválido.");
                return result;
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId flId in featureLineIds)
                {
                    FeatureLine? fl = tr.GetObject(flId, OpenMode.ForRead) as FeatureLine;
                    if (fl == null) continue;

                    Point3dCollection pts = fl.GetPoints(FeatureLinePointType.AllPoints);
                    for (int i = 0; i < pts.Count; i++)
                    {
                        Point3d pt = pts[i];
                        Point2d pt2d = new Point2d(pt.X, pt.Y);

                        if (pt2d.GetDistanceTo(center2d) <= radius)
                        {
                            string key = flId.Handle.ToString() + "_" + i;
                            if (alreadyAdded.Contains(key)) continue;

                            alreadyAdded.Add(key);
                            result.Add(new VertexSelection
                            {
                                FeatureLineId = flId,
                                PointIndex = i,
                                Point = pt
                            });
                        }
                    }
                }

                tr.Commit();
            }

            ed.WriteMessage($"\n{result.Count} vértice(s) encontrado(s) dentro do círculo.");
            return result;
        }

        private void ApplyElevationToVertices(
            Document doc,
            Database db,
            Editor ed,
            List<VertexSelection> selectedVertices,
            double inputValue,
            ElevationApplyMode applyMode)
        {
            int updated = 0;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (VertexSelection item in selectedVertices)
                {
                    FeatureLine? fl = tr.GetObject(item.FeatureLineId, OpenMode.ForWrite) as FeatureLine;
                    if (fl == null) continue;

                    try
                    {
                        double currentZ = item.Point.Z;
                        double targetZ = applyMode == ElevationApplyMode.Absolute
                            ? inputValue
                            : currentZ + inputValue;

                        fl.SetPointElevation(item.PointIndex, targetZ);
                        updated++;
                    }
                    catch
                    {
                    }
                }

                tr.Commit();
            }

            if (applyMode == ElevationApplyMode.Absolute)
            {
                ed.WriteMessage($"\n{updated} vértice(s) atualizado(s) para a cota {inputValue:F3}.");
            }
            else
            {
                ed.WriteMessage($"\n{updated} vértice(s) atualizado(s) com Delta Z {inputValue:F3}.");
            }
        }

        private class VertexMarkers : IDisposable
        {
            private readonly Document _doc;
            private readonly Database _db;
            private readonly List<ObjectId> _markerIds = new List<ObjectId>();
            private bool _shown;

            public VertexMarkers(Document doc, Database db, List<VertexSelection> vertices)
            {
                _doc = doc;
                _db = db;

                using (DocumentLock docLock = _doc.LockDocument())
                using (Transaction tr = _db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    double markerSize = GetMarkerSize(vertices);

                    foreach (VertexSelection v in vertices)
                    {
                        Solid tri = CreateTriangle(v.Point, markerSize);
                        tri.Color = AcadColor.FromRgb(0, 255, 0);

                        ms.AppendEntity(tri);
                        tr.AddNewlyCreatedDBObject(tri, true);
                        _markerIds.Add(tri.ObjectId);
                    }

                    tr.Commit();
                }
            }

            public void Show()
            {
                if (_shown) return;
                _doc.Editor.Regen();
                _shown = true;
            }

            public void Dispose()
            {
                using (DocumentLock docLock = _doc.LockDocument())
                using (Transaction tr = _db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in _markerIds)
                    {
                        if (id.IsErased || id.IsNull)
                            continue;

                        AcEntity? ent = tr.GetObject(id, OpenMode.ForWrite, false) as AcEntity;

                        if (ent != null && !ent.IsErased)
                        {
                            ent.Erase();
                        }
                    }

                    tr.Commit();
                }

                _doc.Editor.Regen();
            }

            private static double GetMarkerSize(List<VertexSelection> vertices)
            {
                if (vertices.Count < 2)
                    return 1.0;

                double minDist = double.MaxValue;

                for (int i = 0; i < vertices.Count; i++)
                {
                    Point2d a = new Point2d(vertices[i].Point.X, vertices[i].Point.Y);

                    for (int j = i + 1; j < vertices.Count; j++)
                    {
                        Point2d b = new Point2d(vertices[j].Point.X, vertices[j].Point.Y);
                        double d = a.GetDistanceTo(b);

                        if (d > 1e-9 && d < minDist)
                            minDist = d;
                    }
                }

                if (double.IsInfinity(minDist) || minDist == double.MaxValue)
                    return 1.0;

                double size = minDist * 0.15;

                if (size < 0.10) size = 0.10;
                if (size > 5.00) size = 5.00;

                return size;
            }

            private static Solid CreateTriangle(Point3d center, double size)
            {
                Point3d p1 = new Point3d(center.X, center.Y + size, center.Z);
                Point3d p2 = new Point3d(center.X - size * 0.866, center.Y - size * 0.5, center.Z);
                Point3d p3 = new Point3d(center.X + size * 0.866, center.Y - size * 0.5, center.Z);

                return new Solid(p1, p2, p3, p3);
            }
        }
    }
}