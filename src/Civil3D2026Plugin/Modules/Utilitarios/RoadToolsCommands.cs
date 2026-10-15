using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;

namespace Civil3D2026Plugin.Modules.Utilitarios;

/// <summary>
/// Utilitarios de vias convertidos das LISPs: SEPARA_CURVAS_RAIO, KB e PP.
/// </summary>
public sealed class RoadToolsCommands
{
    private const string KerbLayer = "Kerb Line";
    private const string KerbLinetype = "ACAD_ISO03W100";

    private static double? _raioLimite;
    private static double _kerbLargura = 0.30;

    // ------------------------------------------------------------------
    // SEPARA_CURVAS_RAIO - separa trechos por raio (antes: mf curvo.lsp)
    // ------------------------------------------------------------------

    private enum Trecho { Reto, CurvaManter, CurvaSeparar }

    [CommandMethod(C3DCommands.Utilitarios.SeparaCurvas, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void SepararCurvasPorRaio()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;

        var pdo = new PromptDistanceOptions(_raioLimite is double r0 ? $"\nRaio limite <{r0:0.###}>: " : "\nRaio limite: ")
        {
            AllowNegative = false,
            AllowZero = false,
            AllowNone = _raioLimite.HasValue
        };
        PromptDoubleResult pdr = ed.GetDistance(pdo);
        if (pdr.Status == PromptStatus.OK)
            _raioLimite = pdr.Value;
        else if (pdr.Status != PromptStatus.None)
            return;
        double raio = _raioLimite!.Value;

        var filtro = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
        PromptSelectionResult psr = ed.GetSelection(
            new PromptSelectionOptions { MessageForAdding = "\nSelecione as polilinhas: " }, filtro);
        if (psr.Status != PromptStatus.OK)
            return;

        int retos = 0, mantidos = 0, separados = 0;
        double compReto = 0, compMantido = 0, compSeparado = 0;

        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in psr.Value.GetObjectIds())
            {
                var pl = (Polyline)tr.GetObject(id, OpenMode.ForRead);
                int nv = pl.NumberOfVertices;
                int nSeg = pl.Closed ? nv : nv - 1;
                if (nSeg < 1)
                    continue;

                var tipos = new Trecho[nSeg];
                for (int i = 0; i < nSeg; i++)
                {
                    Point2d p1 = pl.GetPoint2dAt(i);
                    Point2d p2 = pl.GetPoint2dAt((i + 1) % nv);
                    double b = pl.GetBulgeAt(i);

                    if (Math.Abs(b) < 1e-12)
                    {
                        tipos[i] = Trecho.Reto;
                        retos++;
                        compReto += p1.GetDistanceTo(p2);
                        continue;
                    }

                    double r = Math.Abs(p1.GetDistanceTo(p2) / 2.0 / Math.Sin(2.0 * Math.Atan(b)));
                    double comp = r * Math.Abs(4.0 * Math.Atan(b));
                    if (r > raio)
                    {
                        tipos[i] = Trecho.CurvaManter;
                        mantidos++;
                        compMantido += comp;
                    }
                    else
                    {
                        tipos[i] = Trecho.CurvaSeparar;
                        separados++;
                        compSeparado += comp;
                    }
                }

                // Sem curva para separar: a polilinha fica como esta.
                if (!tipos.Contains(Trecho.CurvaSeparar))
                    continue;

                // Ordem dos segmentos. Na fechada, comeca logo apos a primeira curva a separar
                // para que nenhum trecho fique partido na emenda.
                List<int> ordem;
                if (pl.Closed)
                {
                    int primeira = Array.IndexOf(tipos, Trecho.CurvaSeparar);
                    ordem = Enumerable.Range(1, nSeg).Select(k => (primeira + k) % nSeg).ToList();
                }
                else
                {
                    ordem = Enumerable.Range(0, nSeg).ToList();
                }

                var novas = new List<Entity>();
                var pontos = new List<Point2d>();
                var bulges = new List<double>();
                Trecho atual = tipos[ordem[0]];
                foreach (int s in ordem)
                {
                    if (pontos.Count > 0 && tipos[s] != atual)
                    {
                        novas.Add(CriarTrecho(pl, pontos, bulges, atual == Trecho.CurvaSeparar));
                        pontos.Clear();
                        bulges.Clear();
                    }
                    if (pontos.Count == 0)
                        pontos.Add(pl.GetPoint2dAt(s));
                    pontos.Add(pl.GetPoint2dAt((s + 1) % nv));
                    bulges.Add(pl.GetBulgeAt(s));
                    atual = tipos[s];
                }
                novas.Add(CriarTrecho(pl, pontos, bulges, atual == Trecho.CurvaSeparar));

                UtilHelpers.AppendToOwner(tr, pl.OwnerId, novas);
                pl.UpgradeOpen();
                pl.Erase();
            }
            tr.Commit();
        }

        string msg =
            "Processamento concluido.\n" +
            "----------------------------------\n" +
            $"Trechos retos: {retos}\n" +
            $"Comprimento reto total: {compReto:0.000}\n" +
            $"Curvos mantidos (R > limite): {mantidos}\n" +
            $"Comprimento curvo mantido: {compMantido:0.000}\n" +
            $"Curvos separados (R <= limite): {separados}\n" +
            $"Comprimento curvo separado: {compSeparado:0.000}\n" +
            "----------------------------------";
        ed.WriteMessage("\n" + msg);
        AcApp.ShowAlertDialog(msg);
    }

    private static Polyline CriarTrecho(Polyline origem, List<Point2d> pontos, List<double> bulges, bool separar)
    {
        var pl = new Polyline();
        pl.SetPropertiesFrom(origem);
        for (int i = 0; i < pontos.Count; i++)
            pl.AddVertexAt(i, pontos[i], i < bulges.Count ? bulges[i] : 0.0, 0.0, 0.0);
        pl.Normal = origem.Normal;
        pl.Elevation = origem.Elevation;
        pl.Thickness = origem.Thickness;
        try
        {
            if (origem.ConstantWidth > 0.0)
                pl.ConstantWidth = origem.ConstantWidth;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Larguras variaveis: os trechos ficam sem largura, como na LISP.
        }
        if (separar)
            pl.Color = AcColor.FromColorIndex(AcColorMethod.ByAci, 6);
        return pl;
    }

    // ------------------------------------------------------------------
    // KB - marcacao de meio-fio (antes: Kerb Line.lsp, Bijoy Manoharan)
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Utilitarios.Kb, CommandFlags.Modal)]
    public void MarcacaoMeioFio()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        var pdo = new PromptDistanceOptions($"\nLargura do meio-fio <{_kerbLargura:0.00}>: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = false
        };
        PromptDoubleResult pdr = ed.GetDistance(pdo);
        if (pdr.Status == PromptStatus.OK)
            _kerbLargura = pdr.Value;
        else if (pdr.Status != PromptStatus.None)
            return;
        double largura = _kerbLargura;

        bool tracejado = PrepararLayerELinetype(db, ed);
        Matrix3d ucs = ed.CurrentUserCoordinateSystem;
        int feitos = 0;

        while (true)
        {
            var peo = new PromptEntityOptions("\nSelecione a polilinha do meio-fio <sair>: ") { AllowNone = true };
            peo.SetRejectMessage("\nSelecione uma polilinha.");
            peo.AddAllowedClass(typeof(Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                break;

            PromptPointResult ppr = ed.GetPoint("\nClique o lado do offset: ");
            if (ppr.Status != PromptStatus.OK)
                break;
            Point3d lado = ppr.Value.TransformBy(ucs);

            using Transaction tr = db.TransactionManager.StartTransaction();
            var pl = (Polyline)tr.GetObject(per.ObjectId, OpenMode.ForWrite);

            // Polilinha original: layer do meio-fio e propriedades ByLayer.
            pl.Layer = KerbLayer;
            pl.LinetypeScale = 1.0;
            pl.Color = AcColor.FromColorIndex(AcColorMethod.ByLayer, 256);
            pl.Linetype = "ByLayer";
            pl.LineWeight = LineWeight.ByLayer;

            // Face do meio-fio na largura informada.
            List<Curve> face = UtilHelpers.OffsetToward(pl, largura, lado);
            foreach (Curve c in face)
                c.Layer = KerbLayer;

            // Linha central tracejada com a largura do meio-fio.
            List<Curve> meio = UtilHelpers.OffsetToward(pl, largura / 2.0, lado);
            foreach (Curve c in meio)
            {
                c.Layer = KerbLayer;
                if (tracejado)
                {
                    c.Linetype = KerbLinetype;
                    c.LinetypeScale = 0.2;
                }
                if (c is Polyline p)
                {
                    p.ConstantWidth = largura;
                    p.Plinegen = true;
                }
            }

            if (face.Count == 0 || meio.Count == 0)
            {
                UtilHelpers.Dispose(face);
                UtilHelpers.Dispose(meio);
                ed.WriteMessage("\nNao foi possivel fazer o offset desta polilinha.");
                continue;
            }

            UtilHelpers.AppendToOwner(tr, pl.OwnerId, face.Concat(meio));
            tr.Commit();
            feitos++;
        }

        if (feitos > 0)
            ed.WriteMessage($"\n{feitos} meio(s)-fio(s) marcado(s) na layer \"{KerbLayer}\".");
    }

    private static bool PrepararLayerELinetype(Database db, Editor ed)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        UtilHelpers.EnsureLayer(tr, db, KerbLayer, 7);

        var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        bool ok = ltt.Has(KerbLinetype);
        if (!ok)
        {
            try
            {
                db.LoadLineTypeFile(KerbLinetype, "acadiso.lin");
                ok = true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                ed.WriteMessage($"\nLinetype {KerbLinetype} nao carregado ({ex.Message}); a linha central fica continua.");
            }
        }
        tr.Commit();
        return ok;
    }

    // ------------------------------------------------------------------
    // PP - unir em polilinha (PEDIT Join, tolerancia 0,01)
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Utilitarios.Pp, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void UnirPolilinhas()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;

        var filtro = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LINE,ARC,LWPOLYLINE,POLYLINE") });
        PromptSelectionResult psr = ed.GetSelection(
            new PromptSelectionOptions { MessageForAdding = "\nSelecione linhas, arcos e polilinhas para unir: " }, filtro);
        if (psr.Status != PromptStatus.OK)
            return;

        object aceitar = AcApp.GetSystemVariable("PEDITACCEPT");
        try
        {
            AcApp.SetSystemVariable("PEDITACCEPT", 1);
            ed.Command("_.PEDIT", "_M", psr.Value, "", "_J", 0.01, "");
            ed.WriteMessage("\nObjetos unidos.");
        }
        finally
        {
            AcApp.SetSystemVariable("PEDITACCEPT", aceitar);
        }
    }
}
