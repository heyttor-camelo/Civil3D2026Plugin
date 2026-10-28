using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// HYENTRAR: nas bocas de lobo de calcada do CSBIM o ponto de insercao e a face da guia, e o Civil
/// liga o tubo nesse ponto. O comando leva a ponta de cada tubo ligado para o centro da caixa em
/// planta (centro do retangulo envolvente do solido, que vale com a estrutura girada e para caixas
/// atras, na frente ou dos dois lados da guia), mantendo a cota da ponta.
/// Fase de teste: depois de gravar, rele a ponta numa transacao nova e diz se o Civil a manteve.
/// </summary>
public sealed class HydroPipeInside
{
    private const string Module = "Hydro";

    [CommandMethod(C3DCommands.Hydro.Entrar, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void Entrar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        PromptEntityOptions peo = new("\nEstrutura (boca de lobo) para levar os tubos para dentro: ");
        peo.SetRejectMessage("\nSelecione uma estrutura da Pipe Network.");
        peo.AddAllowedClass(typeof(CivilDb.Structure), false);
        PromptEntityResult per = ed.GetEntity(peo);
        if (per.Status != PromptStatus.OK)
            return;
        try
        {
            using (PluginLog.Medir(Module, "HYENTRAR"))
            {
                List<string> lines = MoveInside(doc.Database, per.ObjectId);
                foreach (string l in lines)
                    ed.WriteMessage("\n[HYDRO] " + l);
            }
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Module, "HYENTRAR falhou", ex);
            ed.WriteMessage("\n[HYDRO] HYENTRAR falhou: " + ex.Message);
        }

        HydroPanel.NotifyChanged();
    }

    /// <summary>Centro da caixa em planta: centro do retangulo envolvente da estrutura.</summary>
    public static Point2d BoxCenter(CivilDb.Structure s)
    {
        AcadDb.Extents3d e = s.GeometricExtents;
        return new Point2d((e.MinPoint.X + e.MaxPoint.X) / 2.0, (e.MinPoint.Y + e.MaxPoint.Y) / 2.0);
    }

    /// <summary>Leva as pontas ligadas a estrutura para o centro da caixa e confere se ficaram.</summary>
    public static List<string> MoveInside(AcadDb.Database db, AcadDb.ObjectId structureId)
    {
        List<string> report = new();
        List<(AcadDb.ObjectId Pipe, bool Start, Point3d Target)> moved = new();
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            CivilDb.Structure s = (CivilDb.Structure)tr.GetObject(structureId, AcadDb.OpenMode.ForRead, false);
            Point2d c = BoxCenter(s);
            report.Add($"{s.Name}: insercao ({N(s.Location.X)}; {N(s.Location.Y)}), centro da caixa ({N(c.X)}; {N(c.Y)}), {s.ConnectedPipesCount} tubo(s).");
            for (int i = 0; i < s.ConnectedPipesCount; i++)
            {
                CivilDb.Pipe p = (CivilDb.Pipe)tr.GetObject(s.get_ConnectedPipe(i), AcadDb.OpenMode.ForWrite, false);
                bool start = p.StartStructureId == structureId;
                Point3d old = start ? p.StartPoint : p.EndPoint;
                Point3d target = new(c.X, c.Y, old.Z);
                if (old.DistanceTo(target) < 1e-4)
                {
                    report.Add($"  {p.Name}: ponta ja no centro.");
                    continue;
                }

                if (start)
                    p.StartPoint = target;
                else
                    p.EndPoint = target;
                moved.Add((p.ObjectId, start, target));
                report.Add($"  {p.Name} ({(start ? "inicio" : "fim")}): ({N(old.X)}; {N(old.Y)}) -> ({N(target.X)}; {N(target.Y)}), deslocamento {N(old.DistanceTo(target))} m.");
            }

            tr.Commit();
        }

        // Rele: o Civil pode puxar a ponta de volta para o ponto de insercao ao refazer a conexao.
        using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            foreach ((AcadDb.ObjectId id, bool start, Point3d target) in moved)
            {
                CivilDb.Pipe p = (CivilDb.Pipe)tr.GetObject(id, AcadDb.OpenMode.ForRead, false);
                Point3d now = start ? p.StartPoint : p.EndPoint;
                bool connected = (start ? p.StartStructureId : p.EndStructureId) == structureId;
                double d = new Point2d(now.X, now.Y).GetDistanceTo(new Point2d(target.X, target.Y));
                report.Add($"  {p.Name}: depois de gravar, ponta a {N(d)} m do centro; {(connected ? "continua ligada" : "DESLIGOU")}{(d > 0.005 ? " - o Civil MOVEU a ponta" : "")}.");
            }
        }

        PluginLog.Info(Module, "HYENTRAR\n" + string.Join("\n", report));
        return report;
    }

    /// <summary>Insercao a mais que isto do centro da caixa = peca com base point na face (calcada).</summary>
    private const double FaceOffset = 0.05;

    /// <summary>
    /// Automatico (fim de comando, HydroSync): nas estruturas cujo ponto de insercao fica fora do
    /// centro da caixa, leva para o centro so as pontas que estao no ponto de insercao (onde o Civil
    /// liga o tubo). Ponta posta em outro lugar pelo usuario fica onde esta. Rodar com o documento travado.
    /// </summary>
    public static List<string> Apply(AcadDb.Database db, IEnumerable<AcadDb.ObjectId> structureIds)
    {
        List<string> lines = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (AcadDb.ObjectId sid in structureIds)
        {
            if (sid.IsNull || sid.IsErased || tr.GetObject(sid, AcadDb.OpenMode.ForRead, false) is not CivilDb.Structure s || s.ConnectedPipesCount == 0)
                continue;
            Point2d c = BoxCenter(s);
            Point2d ins = new(s.Location.X, s.Location.Y);
            if (ins.GetDistanceTo(c) < FaceOffset || !OnFace(s, c, ins))
                continue;
            for (int i = 0; i < s.ConnectedPipesCount; i++)
            {
                if (tr.GetObject(s.get_ConnectedPipe(i), AcadDb.OpenMode.ForRead, false) is not CivilDb.Pipe p)
                    continue;
                bool start = p.StartStructureId == sid;
                Point3d end = start ? p.StartPoint : p.EndPoint;
                if (new Point2d(end.X, end.Y).GetDistanceTo(ins) > FaceOffset)
                    continue;
                p.UpgradeOpen();
                Point3d target = new(c.X, c.Y, end.Z);
                if (start)
                    p.StartPoint = target;
                else
                    p.EndPoint = target;
                lines.Add($"{p.Name}: ponta levada para dentro de {s.Name} ({N(end.DistanceTo(target))} m).");
            }
        }

        tr.Commit();
        if (lines.Count > 0)
            PluginLog.Info(Module, "Tubos para dentro das bocas de lobo:\n" + string.Join("\n", lines));
        return lines;
    }

    /// <summary>
    /// O ponto de insercao esta na face externa (base point na face da guia), e nao dentro da peca
    /// (chamine de PV, centro de dissipador...): no sistema local da estrutura (rotacao), o ponto fica
    /// a no maximo uma parede (+ 2 cm) da borda do retangulo externo Length x Width.
    /// </summary>
    private static bool OnFace(CivilDb.Structure s, Point2d center, Point2d ins)
    {
        double l2, w2, tol;
        try
        {
            l2 = s.Length / 2.0;
            w2 = (s.InnerDiameterOrWidth / 2.0) + s.WallThickness;
            tol = Math.Max(0.05, s.WallThickness + 0.02);
        }
        catch
        {
            return false;
        }

        if (l2 <= 0 || w2 <= 0)
            return false;
        Vector2d d = ins - center;
        double cos = Math.Cos(-s.Rotation), sin = Math.Sin(-s.Rotation);
        double x = Math.Abs((d.X * cos) - (d.Y * sin)), y = Math.Abs((d.X * sin) + (d.Y * cos));
        foreach ((double a, double b) in new[] { (l2, w2), (w2, l2) })
        {
            if (x > a + tol || y > b + tol)
                continue;   // fora do retangulo nesta orientacao
            if (x >= a - tol || y >= b - tol)
                return true;
        }

        return false;
    }

    private static string N(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
}
