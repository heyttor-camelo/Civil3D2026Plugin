using System;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;

namespace Civil3D2026Plugin.Modules.Surfaces
{
    public class SurfaceGapCommands
    {
        [CommandMethod(C3DCommands.Superficies.Cortar1Mm)]
        public void CortarUmMilimetro()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            try
            {
                // 1. Selecionar a Superfície TIN
                PromptEntityOptions peoSurf = new PromptEntityOptions("\nSelecione a TinSurface: ");
                peoSurf.SetRejectMessage("\nSelecione apenas uma superfície TIN.");
                peoSurf.AddAllowedClass(typeof(TinSurface), true);

                PromptEntityResult perSurf = ed.GetEntity(peoSurf);
                if (perSurf.Status != PromptStatus.OK) return;

                // 2. Definir a linha de corte
                PromptPointOptions ppo1 = new PromptPointOptions("\nClique no ponto inicial do corte: ");
                PromptPointResult ppr1 = ed.GetPoint(ppo1);
                if (ppr1.Status != PromptStatus.OK) return;

                PromptPointOptions ppo2 = new PromptPointOptions("\nClique no ponto final do corte cruzando a borda: ");
                ppo2.UseBasePoint = true;
                ppo2.BasePoint = ppr1.Value;
                PromptPointResult ppr2 = ed.GetPoint(ppo2);
                if (ppr2.Status != PromptStatus.OK) return;

                Point3d p1 = ppr1.Value;
                Point3d p2 = ppr2.Value;

                if (p1.DistanceTo(p2) < 0.001)
                {
                    ed.WriteMessage("\nOs pontos estão muito próximos. Trace uma linha maior atravessando a área.");
                    return;
                }

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    TinSurface? surface = tr.GetObject(perSurf.ObjectId, OpenMode.ForWrite) as TinSurface;
                    if (surface == null) return;

                    // --- CRIAÇÃO OU VERIFICAÇÃO DO LAYER OCULTO ---
                    string layerName = "C3D_FRESTAS_OCULTAS_1MM";
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                    if (!lt.Has(layerName))
                    {
                        lt.UpgradeOpen();
                        LayerTableRecord ltr = new LayerTableRecord();
                        ltr.Name = layerName;
                        ltr.IsOff = true; // Desliga a visualização (lâmpada apagada)
                        // Define uma cor chamativa (magenta) caso alguém ligue o layer depois
                        ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 6);

                        lt.Add(ltr);
                        tr.AddNewlyCreatedDBObject(ltr, true);
                    }
                    else
                    {
                        // Se já existir, garante que a visualização esteja desligada
                        LayerTableRecord ltr = (LayerTableRecord)tr.GetObject(lt[layerName], OpenMode.ForWrite);
                        if (!ltr.IsOff) ltr.IsOff = true;
                    }

                    // 3. Matemática para criar um retângulo de 1mm de largura
                    Vector3d dir = (p2 - p1).GetNormal();
                    Vector3d perp = new Vector3d(-dir.Y, dir.X, 0);

                    double halfWidth = 0.0005;

                    Point3d c1 = p1 + perp * halfWidth;
                    Point3d c2 = p2 + perp * halfWidth;
                    Point3d c3 = p2 - perp * halfWidth;
                    Point3d c4 = p1 - perp * halfWidth;

                    // 4. Desenhar a Polyline base no Z=0
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    Polyline boundaryPoly = new Polyline();
                    boundaryPoly.AddVertexAt(0, new Point2d(c1.X, c1.Y), 0, 0, 0);
                    boundaryPoly.AddVertexAt(1, new Point2d(c2.X, c2.Y), 0, 0, 0);
                    boundaryPoly.AddVertexAt(2, new Point2d(c3.X, c3.Y), 0, 0, 0);
                    boundaryPoly.AddVertexAt(3, new Point2d(c4.X, c4.Y), 0, 0, 0);
                    boundaryPoly.Closed = true;

                    // --- ATRIBUI O LAYER ESPECÍFICO À POLILINHA ---
                    boundaryPoly.Layer = layerName;

                    ObjectId polyId = btr.AppendEntity(boundaryPoly);
                    tr.AddNewlyCreatedDBObject(boundaryPoly, true);

                    // 5. Aplicar a Polyline como "Hide Boundary" na Superfície
                    ObjectIdCollection bndIds = new ObjectIdCollection();
                    bndIds.Add(polyId);

                    surface.BoundariesDefinition.AddBoundaries(bndIds, 1.0, Autodesk.Civil.SurfaceBoundaryType.Hide, true);

                    tr.Commit();
                    ed.WriteMessage($"\nFresta de 1mm criada e isolada no layer '{layerName}' (desligado).");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nErro na execução: {ex.Message}");
            }
        }
    }
}