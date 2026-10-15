using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace Civil3D2026Plugin.Modules.Utilitarios;

/// <summary>
/// Utilitarios simples convertidos das LISPs: ALLTORGB, TLEN e JOBECOUNT.
/// </summary>
public sealed class SimpleToolsCommands
{
    private static string _rgbModo = "Objetos";
    private static int _jobeInicio = 1;
    private static int _jobeIncremento = 1;

    // ------------------------------------------------------------------
    // ALLTORGB - cores ACI para RGB (antes: ALL TO RGB.LSP, Lee Mac)
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Utilitarios.AllToRgb, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void AllToRgb()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;

        var pko = new PromptKeywordOptions($"\nConverter [Objetos/Layers/Ambos] <{_rgbModo}>: ", "Objetos Layers Ambos") { AllowNone = true };
        PromptResult pkr = ed.GetKeywords(pko);
        if (pkr.Status == PromptStatus.OK)
            _rgbModo = pkr.StringResult;
        else if (pkr.Status != PromptStatus.None)
            return;
        bool objetos = _rgbModo != "Layers";
        bool layers = _rgbModo != "Objetos";

        var pso = new PromptSelectionOptions
        {
            MessageForAdding = layers && !objetos
                ? "\nSelecione objetos das layers que terao a cor convertida para RGB: "
                : "\nSelecione os objetos para converter a cor para RGB: ",
            // Layer travada: o objeto nao muda, mas a cor da layer pode ser convertida.
            RejectObjectsOnLockedLayers = !layers
        };
        PromptSelectionResult psr = ed.GetSelection(pso);
        if (psr.Status != PromptStatus.OK)
            return;

        int nObjetos = 0, nLayers = 0, xref = 0;
        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            var layerIds = new HashSet<ObjectId>();
            foreach (ObjectId id in psr.Value.GetObjectIds())
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent)
                    continue;

                layerIds.Add(ent.LayerId);

                if (objetos && ParaRgb(ent.Color) is AcColor nova)
                {
                    var lt = (LayerTableRecord)tr.GetObject(ent.LayerId, OpenMode.ForRead);
                    if (lt.IsLocked)
                        continue;
                    ent.UpgradeOpen();
                    ent.Color = nova;
                    nObjetos++;
                }
            }

            if (layers)
            {
                foreach (ObjectId lid in layerIds)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                    if (ltr.IsDependent)
                    {
                        // Layer de xref: a cor nao fica salva no desenho.
                        xref++;
                        continue;
                    }
                    if (ParaRgb(ltr.Color) is AcColor nova)
                    {
                        ltr.UpgradeOpen();
                        ltr.Color = nova;
                        nLayers++;
                    }
                }
            }
            tr.Commit();
        }

        var partes = new List<string>();
        if (objetos)
            partes.Add($"{nObjetos} objeto(s)");
        if (layers)
            partes.Add($"{nLayers} layer(s)");
        ed.WriteMessage($"\nConvertido(s) para RGB: {string.Join(" e ", partes)}. Cores ByLayer/ByBlock e ja RGB ficam como estao." +
                        (xref > 0 ? $" {xref} layer(s) de xref ignorada(s)." : ""));
    }

    /// <summary>Cor ACI 1-255 convertida para o RGB equivalente; null se nao for ACI.</summary>
    private static AcColor? ParaRgb(AcColor cor)
    {
        if (!cor.IsByAci || cor.ColorIndex < 1 || cor.ColorIndex > 255)
            return null;
        System.Drawing.Color rgb = cor.ColorValue;
        return AcColor.FromRgb(rgb.R, rgb.G, rgb.B);
    }

    // ------------------------------------------------------------------
    // TLEN - comprimento total (antes: TLEN.LSP, Tee Square Graphics)
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Utilitarios.Tlen, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void ComprimentoTotal()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;

        var pso = new PromptSelectionOptions { MessageForAdding = "\nSelecione linhas, arcos, polilinhas, splines...: " };
        PromptSelectionResult psr = ed.GetSelection(pso);
        if (psr.Status != PromptStatus.OK)
            return;

        double total = 0.0;
        int contados = 0, ignorados = 0;
        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in psr.Value.GetObjectIds())
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                if (obj is not Curve c || obj is Xline || obj is Ray)
                {
                    ignorados++;
                    continue;
                }

                try
                {
                    total += Math.Abs(c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam));
                    contados++;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    ignorados++;
                }
            }
            tr.Commit();
        }

        string valor = Converter.DistanceToString(total, DistanceUnitFormat.Current, -1);
        string msg = $"Comprimento total: {valor}\n{contados} objeto(s) somado(s)" +
                     (ignorados > 0 ? $", {ignorados} ignorado(s) (nao sao curvas)." : ".");
        ed.WriteMessage("\n" + msg);
        AcApp.ShowAlertDialog(msg);
    }

    // ------------------------------------------------------------------
    // JOBECOUNT - numerar textos em sequencia (antes: jobecount.lsp)
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Utilitarios.JobeCount, CommandFlags.Modal)]
    public void NumerarTextos()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;

        var pInicio = new PromptIntegerOptions($"\nNumero inicial <{_jobeInicio}>: ") { AllowNone = true };
        PromptIntegerResult rInicio = ed.GetInteger(pInicio);
        if (rInicio.Status == PromptStatus.OK)
            _jobeInicio = rInicio.Value;
        else if (rInicio.Status != PromptStatus.None)
            return;

        var pInc = new PromptIntegerOptions($"\nIncremento <{_jobeIncremento}>: ") { AllowNone = true };
        PromptIntegerResult rInc = ed.GetInteger(pInc);
        if (rInc.Status == PromptStatus.OK)
            _jobeIncremento = rInc.Value;
        else if (rInc.Status != PromptStatus.None)
            return;

        var filtro = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "TEXT,MTEXT,ATTDEF") });
        var pso = new PromptSelectionOptions { MessageForAdding = "\nSelecione os textos para numerar: " };
        PromptSelectionResult psr = ed.GetSelection(pso, filtro);
        if (psr.Status != PromptStatus.OK)
            return;

        int n = 0;
        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            var itens = new List<(double X, double Y, Entity Ent)>();
            foreach (ObjectId id in psr.Value.GetObjectIds())
            {
                Entity ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                switch (ent)
                {
                    case DBText t: itens.Add((t.Position.X, t.Position.Y, t)); break;
                    case MText m: itens.Add((m.Location.X, m.Location.Y, m)); break;
                }
            }

            // Ordem de leitura: linhas de cima para baixo (tolerancia em Y) e, na linha, da esquerda para a direita.
            const double tolY = 1e-4;
            var ordenados = new List<Entity>();
            List<(double X, double Y, Entity Ent)> porY = itens.OrderByDescending(i => i.Y).ToList();
            int k = 0;
            while (k < porY.Count)
            {
                double yLinha = porY[k].Y;
                var linha = new List<(double X, double Y, Entity Ent)>();
                while (k < porY.Count && Math.Abs(porY[k].Y - yLinha) <= tolY)
                    linha.Add(porY[k++]);
                ordenados.AddRange(linha.OrderBy(i => i.X).Select(i => i.Ent));
            }

            foreach (Entity ent in ordenados)
            {
                string numero = (_jobeInicio + n * _jobeIncremento).ToString();
                ent.UpgradeOpen();
                switch (ent)
                {
                    case AttributeDefinition a: a.TextString = $"{a.TextString} {numero}"; break;
                    case DBText t: t.TextString = $"{t.TextString} {numero}"; break;
                    case MText m: m.Contents = $"{m.Contents} {numero}"; break;
                }
                n++;
            }
            tr.Commit();
        }

        ed.WriteMessage($"\n{n} objeto(s) numerado(s).");
    }
}
