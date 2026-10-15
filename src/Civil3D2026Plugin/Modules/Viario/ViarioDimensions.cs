using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// C3DVIARIOCOTAR: cota a secao em planta com um clique sobre o eixo.
///
/// No ponto clicado (estaca do eixo) sao criadas cotas alinhadas, perpendiculares
/// ao eixo, em cadeia: calcada, meio-fio + sarjeta, cada faixa de rolamento,
/// canteiro... e, numa segunda linha, a largura total. As posicoes vem das
/// linhas geradas naquela estaca (alargamentos entram), as faixas da secao.
/// Usa o estilo de cota corrente; layer VIARIO-COTAS; [Remover] apaga todas.
/// </summary>
internal static class ViarioDimensions
{
    public const string Layer = "VIARIO-COTAS";
    public const string TagDim = "COTA";

    /// <summary>Lembrado na sessao: cotar sarjeta e meio-fio separados.</summary>
    private static bool _detail;

    public static void Command(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        while (true)
        {
            PromptPointOptions opt = new(
                "\nClique sobre o eixo onde quer a cota das faixas ou [Detalhe/Remover] <sair>" +
                (_detail ? " (sarjeta e meio-fio separados): " : " (sarjeta+meio-fio juntos): "))
            {
                AllowNone = true
            };
            opt.Keywords.Add("Detalhe");
            opt.Keywords.Add("Remover");
            opt.AppendKeywordsToMessage = false;

            PromptPointResult pick = ed.GetPoint(opt);
            if (pick.Status == PromptStatus.Keyword)
            {
                if (pick.StringResult == "Remover")
                {
                    int n = RemoveAll(db);
                    ed.WriteMessage($"\n  {n} cota(s) do viario removida(s).");
                }
                else
                {
                    _detail = !_detail;
                }

                continue;
            }

            if (pick.Status != PromptStatus.OK)
                return;

            Point3d w = pick.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            V2 p = new(w.X, w.Y);

            (List<AxisInput> axes, ViarioResult result, _) = ViarioSync.ComputeCurrent(db);
            AxisInput? axis = null;
            double best = double.MaxValue, bestS = 0;
            foreach (AxisInput a in axes)
            {
                (double s, _, double dist) = a.Path.Project(p);
                if (dist < best && dist <= a.Section.EdgeOffset(EdgeLevel.Alinhamento) + 2.0)
                {
                    best = dist;
                    bestS = s;
                    axis = a;
                }
            }

            if (axis is null)
            {
                ed.WriteMessage("\n  Nenhum eixo perto desse ponto.");
                continue;
            }

            int count = Place(doc, axis, bestS, result, _detail);
            ed.WriteMessage($"\n  {count} cota(s) na estaca {Estaca(bestS)} de {axis.Key} (layer {Layer}). " +
                            "Mova as cotas pelos grips se quiser; [Remover] apaga todas.");
        }
    }

    private static string Estaca(double s)
    {
        int e = (int)Math.Floor((s / 20.0) + 1e-9);
        return $"{e}+{(s - (20.0 * e)).ToString("0.00", CultureInfo.InvariantCulture)}";
    }

    private static int Place(Document doc, AxisInput axis, double s, ViarioResult result, bool detail)
    {
        AcadDb.Database db = doc.Database;
        GPath path = axis.Path;
        V2 p0 = path.PointAt(s);
        V2 t = path.TangentAt(s).Unit();
        V2 n = t.Left;
        List<double> breaks = ViarioDimensionPlan.Breaks(axis, s, result, detail);
        if (breaks.Count < 2)
            return 0;

        double txt = Math.Max(0.05, db.Dimtxt * (db.Dimscale > 0 ? db.Dimscale : 1.0));
        double off1 = 2.0 * txt;          // linha de cota da cadeia, adiante do ponto clicado
        double off2 = off1 + (3.5 * txt); // largura total

        int count = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        ViarioStore.EnsureRegApp(tr, db);
        ViarioStore.EnsureLayer(tr, db, Layer, 2);
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

        Point3d P(V2 v) => new(v.X, v.Y, 0);
        void Add(double a, double b, double off)
        {
            V2 pa = p0 + (n * a), pb = p0 + (n * b);
            V2 mid = ((pa + pb) * 0.5) + (t * off);
            AcadDb.AlignedDimension dim = new(P(pa), P(pb), P(mid), "<>", db.Dimstyle);
            dim.SetDatabaseDefaults(db);
            dim.Layer = Layer;
            ms.AppendEntity(dim);
            tr.AddNewlyCreatedDBObject(dim, true);
            ViarioStore.SetTag(dim, TagDim, axis.Key, s.ToString("0.###", CultureInfo.InvariantCulture));
            count++;
        }

        for (int i = 1; i < breaks.Count; i++)
            Add(breaks[i - 1], breaks[i], off1);
        Add(breaks[0], breaks[^1], off2);

        tr.Commit();
        return count;
    }

    private static int RemoveAll(AcadDb.Database db)
    {
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (!id.ObjectClass.DxfName.Equals("DIMENSION", StringComparison.OrdinalIgnoreCase))
                continue;
            AcadDb.DBObject obj = tr.GetObject(id, AcadDb.OpenMode.ForRead, false);
            if (ViarioStore.ReadTag(obj, out _) != TagDim)
                continue;
            obj.UpgradeOpen();
            obj.Erase();
            n++;
        }

        tr.Commit();
        return n;
    }
}
