using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;

namespace Civil3D2026Plugin.Modules.Geometry
{
    public class PointNormalCommands
    {
        [CommandMethod(C3DCommands.Geometria.PointNormalDebug)]
        public void PointNormalDebug()
        {
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor ed = doc.Editor;

            try
            {
                PromptPointResult p1r = ed.GetPoint("\n1º ponto: ");
                if (p1r.Status != PromptStatus.OK) return;

                PromptPointOptions p2o = new PromptPointOptions("\n2º ponto: ");
                p2o.BasePoint = p1r.Value;
                p2o.UseBasePoint = true;

                PromptPointResult p2r = ed.GetPoint(p2o);
                if (p2r.Status != PromptStatus.OK) return;

                PromptPointOptions p3o = new PromptPointOptions("\n3º ponto: ");
                p3o.BasePoint = p2r.Value;
                p3o.UseBasePoint = true;

                PromptPointResult p3r = ed.GetPoint(p3o);
                if (p3r.Status != PromptStatus.OK) return;

                Point3d p1 = p1r.Value;
                Point3d p2 = p2r.Value;
                Point3d p3 = p3r.Value;

                Vector3d u = p2 - p1;
                Vector3d v = p3 - p1;
                Vector3d n = u.CrossProduct(v);

                double normN = n.Length;

                if (normN < 1e-12)
                {
                    ed.WriteMessage("\nOs 3 pontos são colineares ou praticamente colineares.");
                    ed.WriteMessage("\nNão existe vetor normal confiável para esse plano.");
                    return;
                }

                Vector3d nu = new Vector3d(n.X / normN, n.Y / normN, n.Z / normN);

                double nx = nu.X;
                double ny = nu.Y;
                double nz = nu.Z;

                double projXY = Math.Sqrt((nx * nx) + (ny * ny));

                double angNormalPlanoXYRad = Math.Atan2(Math.Abs(nz), projXY);
                double angNormalPlanoXYDeg = RadToDeg(angNormalPlanoXYRad);

                double angPlanoPlanoXYRad = Math.Atan2(projXY, Math.Abs(nz));
                double angPlanoPlanoXYDeg = RadToDeg(angPlanoPlanoXYRad);

                double slopePercentPlane;
                string hvPlane;
                string vhPlane;

                if (Math.Abs(nz) < 1e-12)
                {
                    slopePercentPlane = double.MaxValue;
                    hvPlane = "0:1";
                    vhPlane = "1:0";
                }
                else
                {
                    double slopeDecimal = projXY / Math.Abs(nz);
                    slopePercentPlane = 100.0 * slopeDecimal;

                    if (projXY < 1e-12)
                    {
                        hvPlane = "0:1";
                        vhPlane = "0:1";
                    }
                    else
                    {
                        double hOverV = 1.0 / slopeDecimal;
                        double vOverH = slopeDecimal;

                        hvPlane = $"{hOverV:F6}:1";
                        vhPlane = $"{vOverH:F6}:1";
                    }
                }

                double azimuteRad = Math.Atan2(ny, nx);
                double azimuteDeg = RadToDeg(azimuteRad);
                if (azimuteDeg < 0.0) azimuteDeg += 360.0;

                ed.WriteMessage("\n================ RESULTADO ================");

                ed.WriteMessage($"\nP1 = ({p1.X:F6}, {p1.Y:F6}, {p1.Z:F6})");
                ed.WriteMessage($"\nP2 = ({p2.X:F6}, {p2.Y:F6}, {p2.Z:F6})");
                ed.WriteMessage($"\nP3 = ({p3.X:F6}, {p3.Y:F6}, {p3.Z:F6})");

                ed.WriteMessage("\n---------------- VETORES ----------------");
                ed.WriteMessage($"\nu = P2-P1 = ({u.X:F6}, {u.Y:F6}, {u.Z:F6})");
                ed.WriteMessage($"\nv = P3-P1 = ({v.X:F6}, {v.Y:F6}, {v.Z:F6})");

                ed.WriteMessage("\n------------- NORMAL BRUTA --------------");
                ed.WriteMessage($"\nn = u x v = ({n.X:F6}, {n.Y:F6}, {n.Z:F6})");
                ed.WriteMessage($"\n|n| = {normN:F6}");

                ed.WriteMessage("\n----------- NORMAL UNITÁRIA -------------");
                ed.WriteMessage($"\nnu = ({nu.X:F6}, {nu.Y:F6}, {nu.Z:F6})");
                ed.WriteMessage($"\nNx = {nx:F6}");
                ed.WriteMessage($"\nNy = {ny:F6}");
                ed.WriteMessage($"\nNz = {nz:F6}");

                ed.WriteMessage("\n--------- GEOMETRIA DA NORMAL -----------");
                ed.WriteMessage($"\nProjeção da normal no XY = {projXY:F6}");
                ed.WriteMessage($"\nÂngulo da NORMAL com o plano XY = {angNormalPlanoXYDeg:F6}°");
                ed.WriteMessage($"\nAzimute da projeção da normal no XY = {azimuteDeg:F6}°");

                ed.WriteMessage("\n---------- GEOMETRIA DO PLANO -----------");
                ed.WriteMessage($"\nÂngulo do PLANO com o plano XY = {angPlanoPlanoXYDeg:F6}°");

                if (slopePercentPlane == double.MaxValue)
                {
                    ed.WriteMessage("\nInclinação do PLANO = infinito (plano vertical)");
                }
                else
                {
                    ed.WriteMessage($"\nInclinação do PLANO = {slopePercentPlane:F6}%");
                }

                ed.WriteMessage($"\nH:V do PLANO = {hvPlane}");
                ed.WriteMessage($"\nV:H do PLANO = {vhPlane}");

                ed.WriteMessage("\n=========================================");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nErro: {ex.Message}");
            }
        }

        private static double RadToDeg(double rad)
        {
            return rad * 180.0 / Math.PI;
        }
    }
}