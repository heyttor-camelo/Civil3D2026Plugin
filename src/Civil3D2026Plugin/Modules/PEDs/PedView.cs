using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>Zoom na vista atual (mesma conta do painel viario: WCS -> DCS).</summary>
internal static class PedView
{
    public static void ZoomTo(Document doc, Point3d min, Point3d max, double margin = 0.1, double minSize = 10.0)
    {
        using (doc.LockDocument())
        {
            using AcadDb.ViewTableRecord view = doc.Editor.GetCurrentView();
            Matrix3d dcsToWcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                                Matrix3d.Displacement(view.Target - Point3d.Origin) *
                                Matrix3d.PlaneToWorld(view.ViewDirection);
            Matrix3d wcsToDcs = dcsToWcs.Inverse();
            Point3d a = new Point3d(min.X, min.Y, 0).TransformBy(wcsToDcs);
            Point3d b = new Point3d(max.X, max.Y, 0).TransformBy(wcsToDcs);
            double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X), minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);

            double ratio = view.Width / Math.Max(view.Height, 1e-9);
            double width = Math.Max(maxX - minX, minSize);
            double height = Math.Max(maxY - minY, minSize);
            if (width / height > ratio)
                height = width / ratio;
            else
                width = height * ratio;

            view.CenterPoint = new Point2d((minX + maxX) / 2.0, (minY + maxY) / 2.0);
            view.Width = width * (1.0 + margin);
            view.Height = height * (1.0 + margin);
            doc.Editor.SetCurrentView(view);
            AcApp.UpdateScreen();
        }
    }

    /// <summary>Centraliza no ponto com uma janela de <paramref name="size"/> unidades.</summary>
    public static void ZoomAround(Document doc, Point3d center, double size)
    {
        double h = size / 2.0;
        ZoomTo(doc, new Point3d(center.X - h, center.Y - h, 0), new Point3d(center.X + h, center.Y + h, 0), 0.0, size);
    }
}
