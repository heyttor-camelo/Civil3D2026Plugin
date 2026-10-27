using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Realce no desenho do que esta selecionado no painel (lista ou monitor), como o realce de
/// selecao do Civil: as pecas ficam destacadas ate a proxima selecao ou ate fechar o painel.
/// </summary>
internal sealed partial class HydroPanel
{
    private const int MaxHighlight = 400;
    private static readonly List<AcadDb.ObjectId> Highlighted = new();

    private void HighlightSelection()
    {
        List<string> handles = _list.SelectedItems.OfType<Row>().Select(r => r.Data switch
        {
            StructureInfo s => s.Handle,
            PipeInfo p => p.Handle,
            CatchmentInfo c => c.Handle,
            _ => ""
        }).Where(h => h.Length > 0).Take(MaxHighlight).ToList();
        HighlightInDrawing(handles);
    }

    /// <summary>Tira o realce anterior e realca as pecas dadas (lista vazia so limpa).</summary>
    internal static void HighlightInDrawing(IReadOnlyCollection<string> handles)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || (handles.Count == 0 && Highlighted.Count == 0))
            return;
        try
        {
            // Com comando ativo (jig, prompt) nao mexe no desenho.
            if (!string.IsNullOrEmpty(doc.CommandInProgress))
                return;
            using (doc.LockDocument())
            using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (AcadDb.ObjectId id in Highlighted)
                {
                    if (id.Database == doc.Database && !id.IsErased && tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.Entity e)
                        e.Unhighlight();
                }

                Highlighted.Clear();
                foreach (string h in handles)
                {
                    if (HydroReader.Id(doc.Database, h) is AcadDb.ObjectId id && !id.IsErased && tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.Entity e)
                    {
                        e.Highlight();
                        Highlighted.Add(id);
                    }
                }

                tr.Commit();
            }

            doc.Editor.UpdateScreen();
        }
        catch (Exception ex)
        {
            PluginLog.Diag(Module, "Realce no desenho: " + ex.Message);
        }
    }
}
