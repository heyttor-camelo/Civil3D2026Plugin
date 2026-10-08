using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.Colors;
using AcadColor = Autodesk.AutoCAD.Colors.Color;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;

namespace Civil3D2026Plugin.Modules.Surfaces
{
    public class TrimSurfaceLinesCommands
    {
        private enum SelectionMode { Add, Remove }

        private sealed class EdgeInfo
        {
            public string Key { get; set; } = string.Empty;
            public Point3d P1 { get; set; }
            public Point3d P2 { get; set; }
            public double MinX, MaxX, MinY, MaxY;
        }

        [CommandMethod(C3DCommands.Superficies.TrimLines)]
        public void TrimSurfaceLines()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            List<Line> committedLines = new List<Line>();
            List<Line> hoverLines = new List<Line>();
            HashSet<string> selectedKeys = new HashSet<string>();
            SelectionMode mode = SelectionMode.Add;

            try
            {
                // 1. Seleção da Superfície
                PromptEntityOptions peo = new PromptEntityOptions("\nSelecione a TinSurface:");
                peo.SetRejectMessage("\nSelecione apenas uma superfície TIN.");
                peo.AddAllowedClass(typeof(TinSurface), true);
                PromptEntityResult per = ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK) return;
                ObjectId surfaceId = per.ObjectId;

                // 2. Cache de Arestas
                List<EdgeInfo> edgeCache = BuildEdgeCache(db, surfaceId);
                Vector3d zOffset = GetZOffset(ed, 0.1);

                // 3. Loop de Seleção Principal
                while (true)
                {
                    PromptPointOptions ppo = new PromptPointOptions($"\nSelecione ponto ou [Adicionar/Remover/Sair] <Sair>:");
                    ppo.Keywords.Add("Adicionar");
                    ppo.Keywords.Add("Remover");
                    ppo.Keywords.Add("Sair");
                    ppo.AllowNone = true;

                    PromptPointResult ppr = ed.GetPoint(ppo);
                    if (ppr.Status == PromptStatus.None || (ppr.Status == PromptStatus.Keyword && ppr.StringResult == "Sair")) break;

                    if (ppr.Status == PromptStatus.Keyword)
                    {
                        mode = ppr.StringResult == "Adicionar" ? SelectionMode.Add : SelectionMode.Remove;
                        continue;
                    }

                    Point3d startPt = ppr.Value;

                    // Monitor de Mouse (Highlight em tempo real)
                    PointMonitorEventHandler monitor = (s, e) => {
                        HashSet<string> tempKeys = CollectIntersections(edgeCache, startPt, e.Context.ComputedPoint);
                        UpdatePreview(tempKeys, hoverLines, zOffset, 2, LineWeight.LineWeight050, TransientDrawingMode.DirectShortTerm);
                    };

                    ed.PointMonitor += monitor;
                    PromptPointOptions ppoNext = new PromptPointOptions("\nPróximo ponto da cerca:") { BasePoint = startPt, UseBasePoint = true };
                    PromptPointResult pprNext = ed.GetPoint(ppoNext);
                    ed.PointMonitor -= monitor;
                    ClearTransients(hoverLines);

                    if (pprNext.Status == PromptStatus.OK)
                    {
                        HashSet<string> passKeys = CollectIntersections(edgeCache, startPt, pprNext.Value);
                        if (mode == SelectionMode.Add) foreach (var k in passKeys) selectedKeys.Add(k);
                        else foreach (var k in passKeys) selectedKeys.Remove(k);

                        // Atualiza o highlight fixo (Vermelho)
                        UpdatePreview(selectedKeys, committedLines, zOffset, 1, LineWeight.LineWeight030, TransientDrawingMode.Highlight, edgeCache);
                    }
                }

                // 4. Execução do Corte (Delete)
                if (selectedKeys.Count > 0)
                {
                    PromptKeywordOptions pko = new PromptKeywordOptions($"\nDeletar {selectedKeys.Count} arestas? [Sim/Nao] <Sim>:");
                    pko.Keywords.Add("Sim"); pko.Keywords.Add("Nao");
                    pko.AllowNone = true;
                    var res = ed.GetKeywords(pko);

                    if (res.Status == PromptStatus.OK && res.StringResult == "Nao") return;

                    using (doc.LockDocument())
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        TinSurface surf = (TinSurface)tr.GetObject(surfaceId, OpenMode.ForWrite);
                        var edgesToDelete = GetEdgesFromKeys(surf, selectedKeys);
                        if (edgesToDelete.Count > 0)
                        {
                            surf.DeleteLines(edgesToDelete);
                            ed.WriteMessage($"\nSucesso: {edgesToDelete.Count} linhas cortadas.");
                        }
                        tr.Commit();
                    }
                }
            }
            finally
            {
                ClearTransients(committedLines);
                ClearTransients(hoverLines);
            }
        }

        private List<EdgeInfo> BuildEdgeCache(Database db, ObjectId id)
        {
            var list = new List<EdgeInfo>();
            var keys = new HashSet<string>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var s = (TinSurface)tr.GetObject(id, OpenMode.ForRead);
                foreach (var tri in s.GetTriangles(false))
                {
                    foreach (var e in new[] { tri.Edge1, tri.Edge2, tri.Edge3 })
                    {
                        string k = MakeKey(e.Vertex1.Location, e.Vertex2.Location);
                        if (keys.Add(k)) list.Add(new EdgeInfo
                        {
                            Key = k,
                            P1 = e.Vertex1.Location,
                            P2 = e.Vertex2.Location,
                            MinX = Math.Min(e.Vertex1.Location.X, e.Vertex2.Location.X),
                            MaxX = Math.Max(e.Vertex1.Location.X, e.Vertex2.Location.X),
                            MinY = Math.Min(e.Vertex1.Location.Y, e.Vertex2.Location.Y),
                            MaxY = Math.Max(e.Vertex1.Location.Y, e.Vertex2.Location.Y)
                        });
                    }
                }
            }
            return list;
        }

        private HashSet<string> CollectIntersections(List<EdgeInfo> cache, Point3d p1, Point3d p2)
        {
            var res = new HashSet<string>();
            double xmin = Math.Min(p1.X, p2.X), xmax = Math.Max(p1.X, p2.X), ymin = Math.Min(p1.Y, p2.Y), ymax = Math.Max(p1.Y, p2.Y);
            foreach (var e in cache)
            {
                if (xmax < e.MinX || xmin > e.MaxX || ymax < e.MinY || ymin > e.MaxY) continue;
                if (AreIntersecting(p1, p2, e.P1, e.P2)) res.Add(e.Key);
            }
            return res;
        }

        private void UpdatePreview(HashSet<string> keys, List<Line> lines, Vector3d off, short color, LineWeight lw, TransientDrawingMode mode, List<EdgeInfo>? cache = null)
        {
            ClearTransients(lines);
            if (cache == null) return; // Simples proteção
            foreach (var e in cache)
            {
                if (!keys.Contains(e.Key)) continue;
                Line ln = new Line(e.P1 + off, e.P2 + off) { Color = AcadColor.FromColorIndex(ColorMethod.ByAci, color), LineWeight = lw };
                lines.Add(ln);
                TransientManager.CurrentTransientManager.AddTransient(ln, mode, 128, new IntegerCollection(new int[] { 0 }));
            }
        }

        private List<TinSurfaceEdge> GetEdgesFromKeys(TinSurface s, HashSet<string> keys)
        {
            var res = new List<TinSurfaceEdge>();
            foreach (var tri in s.GetTriangles(false))
            {
                foreach (var e in new[] { tri.Edge1, tri.Edge2, tri.Edge3 })
                {
                    if (keys.Contains(MakeKey(e.Vertex1.Location, e.Vertex2.Location)))
                    {
                        res.Add(e);
                        keys.Remove(MakeKey(e.Vertex1.Location, e.Vertex2.Location)); // Evita duplicatas
                    }
                }
            }
            return res;
        }

        private void ClearTransients(List<Line> lines)
        {
            foreach (var ln in lines)
            {
                TransientManager.CurrentTransientManager.EraseTransient(ln, new IntegerCollection(new int[] { 0 }));
                ln.Dispose();
            }
            lines.Clear();
        }

        private string MakeKey(Point3d p1, Point3d p2)
        {
            string s1 = $"{Math.Round(p1.X, 4)},{Math.Round(p1.Y, 4)}", s2 = $"{Math.Round(p2.X, 4)},{Math.Round(p2.Y, 4)}";
            return string.CompareOrdinal(s1, s2) < 0 ? s1 + "|" + s2 : s2 + "|" + s1;
        }

        private bool AreIntersecting(Point3d a, Point3d b, Point3d c, Point3d d)
        {
            double den = (b.X - a.X) * (d.Y - c.Y) - (b.Y - a.Y) * (d.X - c.X);
            if (Math.Abs(den) < 1e-8) return false;
            double r = ((a.Y - c.Y) * (d.X - c.X) - (a.X - c.X) * (d.Y - c.Y)) / den;
            double s = ((a.Y - c.Y) * (b.X - a.X) - (a.X - c.X) * (b.Y - a.Y)) / den;
            return (r >= 0 && r <= 1) && (s >= 0 && s <= 1);
        }

        private Vector3d GetZOffset(Editor ed, double d)
        {
            try { return ed.CurrentUserCoordinateSystem.CoordinateSystem3d.Zaxis.GetNormal() * d; }
            catch { return new Vector3d(0, 0, d); }
        }
    }
}