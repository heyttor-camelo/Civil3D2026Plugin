using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Utilitarios;

/// <summary>
/// Utilitarios de desenho convertidos das LISPs: SMARTHATCH, LSLAYOUTS e MOFF.
/// </summary>
public sealed class DrawingToolsCommands
{
    private static double _gapTol = 50.0;
    private static string _layerState = "";
    private static string _layouts = "*";
    private static double? _moffDist;
    private static int _moffQtd = 1;
    private static string _moffDir = "Fora";

    // ------------------------------------------------------------------
    // SMARTHATCH - hachura ignorando gaps (antes: smart_hatch.lsp)
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Utilitarios.SmartHatch, CommandFlags.Modal)]
    public void HachuraComGap()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        Point3d ponto;
        while (true)
        {
            var ppo = new PromptPointOptions($"\nClique dentro da area para hachurar [Tolerancia] <gap {_gapTol:0.###}>: ", "Tolerancia");
            PromptPointResult ppr = ed.GetPoint(ppo);
            if (ppr.Status == PromptStatus.Keyword)
            {
                var pdo = new PromptDistanceOptions($"\nTolerancia de gap (HPGAPTOL, 0 a 5000) <{_gapTol:0.###}>: ")
                {
                    AllowNone = true,
                    AllowNegative = false
                };
                PromptDoubleResult pdr = ed.GetDistance(pdo);
                if (pdr.Status == PromptStatus.OK)
                    _gapTol = Math.Min(5000.0, pdr.Value);
                continue;
            }
            if (ppr.Status != PromptStatus.OK)
                return;
            ponto = ppr.Value;
            break;
        }

        // Contornos criados pelo -BOUNDARY (externo e ilhas).
        var criados = new List<ObjectId>();
        void OnAppended(object sender, ObjectEventArgs e)
        {
            if (e.DBObject is Polyline)
                criados.Add(e.DBObject.ObjectId);
        }

        object gapAnterior = AcApp.GetSystemVariable("HPGAPTOL");
        db.ObjectAppended += OnAppended;
        try
        {
            AcApp.SetSystemVariable("HPGAPTOL", _gapTol);
            ed.Command("_.-BOUNDARY", ponto, "");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nNao foi possivel gerar o contorno: {ex.Message}");
        }
        finally
        {
            db.ObjectAppended -= OnAppended;
            AcApp.SetSystemVariable("HPGAPTOL", gapAnterior);
        }

        if (criados.Count == 0)
        {
            ed.WriteMessage("\nNao foi possivel determinar o contorno mesmo com a tolerancia. Aumente o gap ([Tolerancia]).");
            return;
        }

        using Transaction tr = db.TransactionManager.StartTransaction();
        List<Polyline> contornos = criados
            .Where(id => !id.IsErased)
            .Select(id => (Polyline)tr.GetObject(id, OpenMode.ForWrite))
            .ToList();
        if (contornos.Count == 0)
            return;

        // O maior contorno e o externo; os outros sao ilhas.
        Polyline externo = contornos.OrderByDescending(p => Math.Abs(p.Area)).First();

        var hatch = new Hatch();
        var espaco = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
        espaco.AppendEntity(hatch);
        tr.AddNewlyCreatedDBObject(hatch, true);
        hatch.SetDatabaseDefaults();
        hatch.Normal = externo.Normal;
        hatch.Elevation = externo.Elevation;
        hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
        hatch.Associative = false;
        hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { externo.ObjectId });
        foreach (Polyline ilha in contornos.Where(p => p.ObjectId != externo.ObjectId))
            hatch.AppendLoop(HatchLoopTypes.Default, new ObjectIdCollection { ilha.ObjectId });
        hatch.EvaluateHatch(true);

        // O contorno era so auxiliar.
        foreach (Polyline p in contornos)
            p.Erase();

        tr.Commit();
        ed.WriteMessage(contornos.Count > 1
            ? $"\nHachura SOLID criada com {contornos.Count - 1} ilha(s)."
            : "\nHachura SOLID criada.");
    }

    // ------------------------------------------------------------------
    // LSLAYOUTS - layer state como override de viewport em varios layouts
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Utilitarios.LsLayouts, CommandFlags.Modal)]
    public void LayerStateEmLayouts()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        Database db = doc.Database;
        LayerStateManager lsm = db.LayerStateManager;

        string padrao = _layerState.Length > 0 ? $" <{_layerState}>" : "";
        var pso = new PromptStringOptions($"\nNome do layer state{padrao}: ") { AllowSpaces = true };
        PromptResult pr = ed.GetString(pso);
        if (pr.Status != PromptStatus.OK)
            return;
        string nome = pr.StringResult.Trim().Length > 0 ? pr.StringResult.Trim() : _layerState;
        if (nome.Length == 0)
            return;
        if (!lsm.HasLayerState(nome))
        {
            ed.WriteMessage($"\nLayer state \"{nome}\" nao encontrado no desenho.");
            return;
        }
        _layerState = nome;

        var plo = new PromptStringOptions($"\nLayouts (curinga, ex. PR-*,DET-?) <{_layouts}>: ") { AllowSpaces = false };
        PromptResult plr = ed.GetString(plo);
        if (plr.Status != PromptStatus.OK)
            return;
        if (plr.StringResult.Trim().Length > 0)
            _layouts = plr.StringResult.Trim();

        int viewports = 0, layouts = 0;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            LayerStateMasks mascara = lsm.GetLayerStateMask(nome);
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in dic)
            {
                var layout = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                if (layout.ModelType || !UtilHelpers.WildcardMatch(layout.LayoutName, _layouts))
                    continue;

                List<ObjectId> vps = ViewportsDoLayout(tr, layout);
                if (vps.Count == 0)
                    continue;

                layouts++;
                foreach (ObjectId vp in vps)
                {
                    lsm.RestoreLayerState(nome, vp, 4, mascara); // 4 = restaurar como override de viewport
                    viewports++;
                }
            }
            tr.Commit();
        }

        if (viewports > 0)
            ed.Command("_.REGENALL");
        ed.WriteMessage($"\n\"{nome}\" aplicado em {viewports} viewport(s) de {layouts} layout(s).");
    }

    /// <summary>Viewports de modelo do layout, sem o viewport do papel.</summary>
    private static List<ObjectId> ViewportsDoLayout(Transaction tr, Autodesk.AutoCAD.DatabaseServices.Layout layout)
    {
        var lista = new List<ObjectId>();
        var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        bool primeiro = true;
        foreach (ObjectId id in btr)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Viewport vp)
                continue;

            // O primeiro viewport do layout e o da folha (numero 1 quando o layout ja foi aberto).
            bool papel = vp.Number == 1 || (vp.Number < 1 && primeiro);
            primeiro = false;
            if (!papel)
                lista.Add(id);
        }
        return lista;
    }

    // ------------------------------------------------------------------
    // MOFF - varios offsets em polilinhas fechadas (antes: MOFF_v1.0.lsp)
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Utilitarios.Moff, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void VariosOffsets() => ExecutarMoff();

    [CommandMethod(C3DCommands.Utilitarios.MoffSet, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void VariosOffsetsAlias() => ExecutarMoff();

    private static void ExecutarMoff()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;

        var pdo = new PromptDistanceOptions(_moffDist is double d0
            ? $"\nDistancia entre offsets <{d0:0.####}>: "
            : "\nDistancia entre offsets: ")
        {
            AllowNegative = false,
            AllowZero = false,
            AllowNone = _moffDist.HasValue
        };
        PromptDoubleResult pdr = ed.GetDistance(pdo);
        if (pdr.Status == PromptStatus.OK)
            _moffDist = pdr.Value;
        else if (pdr.Status != PromptStatus.None)
            return;
        double dist = _moffDist!.Value;

        var pio = new PromptIntegerOptions($"\nQuantidade de offsets <{_moffQtd}>: ")
        {
            AllowNone = true,
            AllowZero = false,
            AllowNegative = false
        };
        PromptIntegerResult pir = ed.GetInteger(pio);
        if (pir.Status == PromptStatus.OK)
            _moffQtd = pir.Value;
        else if (pir.Status != PromptStatus.None)
            return;

        var pko = new PromptKeywordOptions($"\nDirecao [Dentro/Fora] <{_moffDir}>: ", "Dentro Fora") { AllowNone = true };
        PromptResult pkr = ed.GetKeywords(pko);
        if (pkr.Status == PromptStatus.OK)
            _moffDir = pkr.StringResult;
        else if (pkr.Status != PromptStatus.None)
            return;
        bool dentro = _moffDir == "Dentro";

        var filtro = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE,POLYLINE") });
        PromptSelectionResult psr = ed.GetSelection(
            new PromptSelectionOptions { MessageForAdding = "\nSelecione as polilinhas fechadas: " }, filtro);
        if (psr.Status != PromptStatus.OK)
            return;

        int criados = 0, falhas = 0, ignorados = 0;
        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in psr.Value.GetObjectIds())
            {
                var curva = (Curve)tr.GetObject(id, OpenMode.ForRead);
                bool fechada = curva switch
                {
                    Polyline p => p.Closed,
                    Polyline2d p2 => p2.Closed,
                    _ => false
                };
                double a0 = fechada ? SafeArea(curva) : 0.0;
                if (!fechada || a0 <= 1e-12)
                {
                    ignorados++;
                    continue;
                }

                double tol = Math.Max(1e-10, a0 * 1e-10);
                bool LadoCerto(double area) => dentro ? area < a0 - tol : area > a0 + tol;

                // Primeiro offset: testa os dois sinais para descobrir o lado.
                List<Curve> mais = UtilHelpers.Offset(curva, dist);
                List<Curve> menos = UtilHelpers.Offset(curva, -dist);
                double sinal;
                List<Curve>? escolhido;
                if (mais.Count > 0 && menos.Count > 0)
                {
                    double am = UtilHelpers.Area(mais), an = UtilHelpers.Area(menos);
                    bool usarMais = dentro ? am <= an : am >= an;
                    escolhido = usarMais ? mais : menos;
                    sinal = usarMais ? 1.0 : -1.0;
                    UtilHelpers.Dispose(usarMais ? menos : mais);
                }
                else if (mais.Count > 0 && LadoCerto(UtilHelpers.Area(mais)))
                {
                    escolhido = mais;
                    sinal = 1.0;
                }
                else if (menos.Count > 0 && LadoCerto(UtilHelpers.Area(menos)))
                {
                    escolhido = menos;
                    sinal = -1.0;
                }
                else
                {
                    UtilHelpers.Dispose(mais);
                    UtilHelpers.Dispose(menos);
                    falhas += _moffQtd;
                    continue;
                }

                UtilHelpers.AppendToOwner(tr, curva.OwnerId, escolhido);
                criados += escolhido.Count;

                // Demais offsets: usa o sinal ja descoberto e confere pela area.
                for (int n = 2; n <= _moffQtd; n++)
                {
                    List<Curve> outros = UtilHelpers.Offset(curva, sinal * dist * n);
                    if (outros.Count > 0 && LadoCerto(UtilHelpers.Area(outros)))
                    {
                        UtilHelpers.AppendToOwner(tr, curva.OwnerId, outros);
                        criados += outros.Count;
                    }
                    else
                    {
                        UtilHelpers.Dispose(outros);
                        falhas++;
                    }
                }
            }
            tr.Commit();
        }

        string msg = $"\nMOFF concluido. {criados} offset(s) criado(s)";
        if (falhas > 0)
            msg += $"; {falhas} tentativa(s) sem geometria valida";
        if (ignorados > 0)
            msg += $"; {ignorados} objeto(s) ignorado(s) (nao sao polilinhas 2D fechadas)";
        ed.WriteMessage(msg + ".");
    }

    private static double SafeArea(Curve c)
    {
        try
        {
            return Math.Abs(c.Area);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return 0.0;
        }
    }
}
