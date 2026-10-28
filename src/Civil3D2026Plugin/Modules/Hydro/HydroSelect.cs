using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// HYSELECIONAR: seleciona no desenho o que o painel pediu (clique no monitor, lista). Feito por
/// comando porque SetImpliedSelection a partir da paleta, sem comando, nem sempre mostra os grips.
/// Faz zoom so se a peca estiver fora da vista. Selecionar uma estrutura mostra as cotas editaveis.
/// </summary>
public sealed class HydroSelect
{
    private const string Module = "Hydro";
    private static List<string> _pending = new();

    /// <summary>Pede a selecao (e, se then nao for vazio, roda esse comando depois, com a selecao como pickfirst).</summary>
    public static void Request(IEnumerable<string> handles, string? then = null)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        _pending = handles.ToList();
        doc.SendStringToExecute(C3DCommands.Hydro.Selecionar + " " + (string.IsNullOrEmpty(then) ? "" : then + " "), true, false, false);
    }

    [CommandMethod(C3DCommands.Hydro.Selecionar, CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.NoHistory)]
    public void Selecionar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        try
        {
            List<AcadDb.ObjectId> ids = _pending.Select(h => HydroReader.Id(doc.Database, h)).Where(i => i.HasValue).Select(i => i!.Value).ToList();
            _pending = new List<string>();
            if (ids.Count == 0)
                return;
            ZoomIfOutside(doc, ids);
            ed.SetImpliedSelection(ids.ToArray());
            PluginLog.Diag(Module, $"HYSELECIONAR: {ids.Count} item(ns).");
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Module, "HYSELECIONAR falhou", ex);
        }
    }

    /// <summary>Zoom so quando alguma peca esta fora da vista atual (a vista e mantida se ja aparece).</summary>
    private static void ZoomIfOutside(Document doc, List<AcadDb.ObjectId> ids)
    {
        AcadDb.Extents3d? box = null;
        using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            foreach (AcadDb.ObjectId id in ids)
            {
                try
                {
                    if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.Entity e)
                    {
                        AcadDb.Extents3d x = e.GeometricExtents;
                        if (box is AcadDb.Extents3d b)
                        {
                            b.AddExtents(x);
                            box = b;
                        }
                        else
                        {
                            box = x;
                        }
                    }
                }
                catch
                {
                    // Sem extents: ignora.
                }
            }
        }

        if (box is not AcadDb.Extents3d ext)
            return;
        using AcadDb.ViewTableRecord view = doc.Editor.GetCurrentView();
        double hw = view.Width / 2, hh = view.Height / 2;
        Point2d c = view.CenterPoint;
        // Vista em coordenadas do DCS; com a vista plana (o caso da planta) bate com o WCS sem giro.
        bool inside = ext.MinPoint.X >= c.X - hw && ext.MaxPoint.X <= c.X + hw && ext.MinPoint.Y >= c.Y - hh && ext.MaxPoint.Y <= c.Y + hh;
        if (inside || Math.Abs(view.ViewTwist) > 1e-9 || !view.ViewDirection.IsParallelTo(Vector3d.ZAxis))
            return;
        double w = Math.Max(ext.MaxPoint.X - ext.MinPoint.X, 5) * 3, h = Math.Max(ext.MaxPoint.Y - ext.MinPoint.Y, 5) * 3;
        double ratio = view.Width / Math.Max(view.Height, 1e-9);
        if (w / h > ratio)
            h = w / ratio;
        else
            w = h * ratio;
        view.CenterPoint = new Point2d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2);
        view.Width = w;
        view.Height = h;
        doc.Editor.SetCurrentView(view);
    }
}
