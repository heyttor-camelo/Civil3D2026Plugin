using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Comandos do modulo Layouts.</summary>
public sealed class LayoutCommands
{
    /// <summary>Explorador de layouts (janela flutuante).</summary>
    [CommandMethod(C3DCommands.Layouts.Painel, CommandFlags.Modal)]
    public void Painel() => LayoutPanel.ShowPanel();

    /// <summary>Abre o explorador na categoria Viewports.</summary>
    [CommandMethod(C3DCommands.Layouts.Viewports, CommandFlags.Modal)]
    public void Viewports() => LayoutPanel.ShowPanel("VIEWPORTS");

    /// <summary>Abre o explorador na categoria Layer state (ex-LSLAYOUTS).</summary>
    [CommandMethod(C3DCommands.Layouts.LayerState, CommandFlags.Modal)]
    public void LayerState() => LayoutPanel.ShowPanel("LAYERSTATE");

    /// <summary>Abre o explorador na categoria Carimbo.</summary>
    [CommandMethod(C3DCommands.Layouts.Carimbo, CommandFlags.Modal)]
    public void Carimbo() => LayoutPanel.ShowPanel("CARIMBO");

    /// <summary>
    /// Numeracao de pranchas direto na linha de comando (ex-NP):
    /// [Configurar/Atualizar] como na LISP original.
    /// </summary>
    [CommandMethod(C3DCommands.Layouts.Numerar, CommandFlags.Modal)]
    public void Numerar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;

        var pko = new PromptKeywordOptions("\nNumeracao de pranchas [Configurar/Atualizar] <Atualizar>: ", "Configurar Atualizar") { AllowNone = true };
        PromptResult pr = ed.GetKeywords(pko);
        if (pr.Status == PromptStatus.Cancel || pr.Status == PromptStatus.Error)
            return;

        if (pr.Status == PromptStatus.OK && pr.StringResult == "Configurar")
        {
            if (!Escolher(doc, true) || !Escolher(doc, false))
                return;
        }

        OpResult r = LayoutOps.Numerar(doc);
        ed.WriteMessage("\n" + r.Mensagem);
        ed.Regen();
    }

    private static bool Escolher(Document doc, bool numero)
    {
        var opt = new PromptNestedEntityOptions(numero
            ? "\nClique no texto/atributo do NUMERO da prancha: "
            : "\nClique no texto/atributo do TOTAL de pranchas: ");
        PromptNestedEntityResult res = doc.Editor.GetNestedEntity(opt);
        if (res.Status != PromptStatus.OK)
            return false;

        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            if (tr.GetObject(res.ObjectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead)
                is not (Autodesk.AutoCAD.DatabaseServices.DBText or Autodesk.AutoCAD.DatabaseServices.MText or Autodesk.AutoCAD.DatabaseServices.AttributeReference))
            {
                doc.Editor.WriteMessage("\nSelecione um texto, MTEXT ou atributo de bloco.");
                return false;
            }
            tr.Commit();
        }

        LayoutOps.AssociarTexto(doc, res.ObjectId, numero);
        doc.Editor.WriteMessage($"\nTexto associado a {(numero ? LayoutOps.LayerNumero : LayoutOps.LayerTotal)}.");
        return true;
    }
}
