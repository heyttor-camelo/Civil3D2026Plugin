using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.Secao.SecaoCommands))]

namespace Civil3D2026Plugin.Modules.Secao;

/// <summary>
/// Visualizador de secao (C3DSECAO): janela propria, fora da viewport, que mostra o
/// corte de uma caixa (linha em planta + profundidades + faixa de cotas) por tudo o
/// que ela atravessa, e a secao do corredor estaca a estaca.
/// </summary>
public sealed class SecaoCommands
{
    /// <summary>Abre o visualizador.</summary>
    [CommandMethod(C3DCommands.Secao.Painel, CommandFlags.Modal)]
    public void Painel() => SecaoPanel.ShowPanel();

    /// <summary>Cria uma caixa de corte: linha por pontos ou polilinha existente, profundidades.</summary>
    [CommandMethod(C3DCommands.Secao.Corte, CommandFlags.Modal)]
    public void Corte()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        SecaoStore.Prefs prefs = SecaoStore.LoadPrefs();

        PromptPointOptions first = new("\nPrimeiro ponto da linha de corte ou [Selecionar polilinha]: ") { AllowNone = false };
        first.Keywords.Add("Selecionar");
        first.AppendKeywordsToMessage = false;
        PromptPointResult pr = ed.GetPoint(first);
        List<P2> pts = new();
        AcadDb.ObjectId existing = AcadDb.ObjectId.Null;

        if (pr.Status == PromptStatus.Keyword)
        {
            PromptEntityOptions peo = new("\nPolilinha que sera a linha de corte: ");
            peo.SetRejectMessage("\nSo polilinha leve (LWPOLYLINE).");
            peo.AddAllowedClass(typeof(AcadDb.Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return;
            existing = per.ObjectId;
        }
        else if (pr.Status == PromptStatus.OK)
        {
            Point3d p0 = pr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            pts.Add(new P2(p0.X, p0.Y));
            while (true)
            {
                PromptPointOptions next = new(pts.Count < 2 ? "\nProximo ponto: " : "\nProximo ponto ou <Enter> para terminar: ")
                {
                    UseBasePoint = true,
                    BasePoint = new Point3d(pts[^1].X, pts[^1].Y, 0),
                    AllowNone = pts.Count >= 2
                };
                PromptPointResult np = ed.GetPoint(next);
                if (np.Status == PromptStatus.None)
                    break;
                if (np.Status != PromptStatus.OK)
                    return;
                Point3d p = np.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                pts.Add(new P2(p.X, p.Y));
            }
        }
        else
        {
            return;
        }

        double frente = AskDistance(ed, "\nProfundidade a frente (lado esquerdo da linha)", prefs.FrentePadrao);
        if (double.IsNaN(frente))
            return;
        double atras = AskDistance(ed, "\nProfundidade atras (lado direito da linha)", prefs.AtrasPadrao);
        if (double.IsNaN(atras))
            return;

        AcadDb.ObjectId id;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            SecaoStore.EnsureRegApp(tr, db);
            SecaoStore.EnsureLayer(tr, db, SecaoStore.LayerCut, 6);
            AcadDb.Polyline pl;
            if (existing.IsNull)
            {
                AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
                AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);
                pl = new AcadDb.Polyline();
                for (int i = 0; i < pts.Count; i++)
                    pl.AddVertexAt(i, new Point2d(pts[i].X, pts[i].Y), 0, 0, 0);
                pl.SetDatabaseDefaults(db);
                pl.Layer = SecaoStore.LayerCut;
                ms.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
            }
            else
            {
                pl = (AcadDb.Polyline)tr.GetObject(existing, AcadDb.OpenMode.ForWrite);
                pl.Layer = SecaoStore.LayerCut;
            }

            int n = SecaoStore.ReadCuts(db).Count + 1;
            CutBox box = SecaoStore.ReadCut(pl) ?? new CutBox { Name = "Corte " + n.ToString(CultureInfo.InvariantCulture) };
            box.Line = SecaoStore.Vertices(pl);
            box.Frente = frente;
            box.Atras = atras;
            box.Exagero = prefs.ExageroPadrao;
            SecaoStore.WriteCut(pl, box);
            id = pl.ObjectId;
            tr.Commit();
        }

        prefs.FrentePadrao = frente;
        prefs.AtrasPadrao = atras;
        SecaoStore.SavePrefs(prefs);
        ed.Regen();
        ed.WriteMessage($"\n  Caixa de corte criada (layer {SecaoStore.LayerCut}): mova os grips e a janela acompanha; altere profundidades e cotas na aba Propriedades.");
        SecaoPanel.ShowPanel();
        SecaoPanel.SelectCut(id);
        SectionData? data = SecaoPanel.CurrentData;
        if (data is null)
        {
            ed.WriteMessage("\n  [C3DSECAO] A janela nao montou a secao (veja a barra de status dela).");
            return;
        }

        ed.WriteMessage($"\n  [C3DSECAO] {data.Box.Name}: {data.Summary()}");
        foreach (string w in data.Warnings.Take(5))
            ed.WriteMessage("\n  [C3DSECAO] aviso: " + w);
        if (data.Items.Count == 0)
            ed.WriteMessage("\n  [C3DSECAO] Nada cortado: a linha precisa atravessar uma superficie, corredor, tubo ou solido (ou ter algo dentro da profundidade). Confira a aba Conteudo da janela.");
    }

    /// <summary>Desenha a secao corrente como polilinhas 2D no model space, no ponto clicado.</summary>
    [CommandMethod(C3DCommands.Secao.Desenhar, CommandFlags.Modal)]
    public void Desenhar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        SectionData? data = SecaoPanel.CurrentData;
        if (data is null || data.Items.Count == 0)
        {
            ed.WriteMessage("\n  Nenhuma secao na janela (abra C3DSECAO e escolha um corte).");
            return;
        }

        PromptPointResult pr = ed.GetPoint("\nCanto inferior esquerdo da secao no desenho: ");
        if (pr.Status != PromptStatus.OK)
            return;
        Point3d origin = pr.Value.TransformBy(ed.CurrentUserCoordinateSystem);

        PromptDoubleOptions pdo = new("\nExagero vertical") { DefaultValue = data.Box.Exagero, UseDefaultValue = true, AllowNegative = false, AllowZero = false, AllowNone = true };
        PromptDoubleResult pd = ed.GetDouble(pdo);
        if (pd.Status != PromptStatus.OK && pd.Status != PromptStatus.None)
            return;
        double ex = pd.Status == PromptStatus.OK ? pd.Value : data.Box.Exagero;

        int count = SecaoDraw.Draw(doc.Database, data, origin, ex);
        ed.WriteMessage($"\n  Secao desenhada: {count} entidade(s) na layer {SecaoStore.LayerDraw} (exagero {ex:0.##}).");
    }

    private static double AskDistance(Editor ed, string message, double def)
    {
        PromptDoubleOptions o = new(message) { DefaultValue = def, UseDefaultValue = true, AllowNegative = false, AllowZero = true, AllowNone = true };
        PromptDoubleResult r = ed.GetDouble(o);
        if (r.Status == PromptStatus.None)
            return def;
        return r.Status == PromptStatus.OK ? r.Value : double.NaN;
    }

    // ------------------------------------------------------------------
    // Servico: contorno da caixa em planta e aviso de edicao para a janela
    // ------------------------------------------------------------------

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        SecaoBoxOverrule.Register();
        AcApp.DocumentManager.DocumentCreated += (_, e) => Attach(e.Document);
        foreach (Document d in AcApp.DocumentManager)
            Attach(d);
    }

    public static void Terminate()
    {
        SecaoBoxOverrule.Unregister();
    }

    private static readonly HashSet<AcadDb.Database> Attached = new();

    private static void Attach(Document? doc)
    {
        if (doc is null || Attached.Contains(doc.Database))
            return;
        Attached.Add(doc.Database);
        doc.Database.ObjectModified += (_, e) =>
        {
            try
            {
                if (e.DBObject is AcadDb.Polyline pl && SecaoStore.IsCut(pl))
                    SecaoPanel.NotifyChanged(pl.ObjectId);
            }
            catch
            {
                // Nunca interromper a edicao.
            }
        };
        doc.Database.ObjectErased += (_, e) =>
        {
            try
            {
                if (e.DBObject is AcadDb.Polyline pl && SecaoStore.IsCut(pl))
                    SecaoPanel.NotifyChanged(pl.ObjectId);
            }
            catch
            {
                // idem
            }
        };
    }
}

/// <summary>
/// Desenha em planta, junto da polilinha de corte, o contorno da caixa (profundidades)
/// e uma seta no sentido de quem olha a secao. Nada e gravado no desenho.
/// </summary>
internal sealed class SecaoBoxOverrule : DrawableOverrule
{
    private static SecaoBoxOverrule? _instance;

    public static void Register()
    {
        if (_instance is not null)
            return;
        _instance = new SecaoBoxOverrule();
        _instance.SetXDataFilter(SecaoStore.RegApp);
        Overrule.AddOverrule(RXObject.GetClass(typeof(AcadDb.Polyline)), _instance, false);
        Overrule.Overruling = true;
    }

    public static void Unregister()
    {
        if (_instance is null)
            return;
        try
        {
            Overrule.RemoveOverrule(RXObject.GetClass(typeof(AcadDb.Polyline)), _instance);
        }
        catch
        {
            // Encerramento.
        }

        _instance = null;
    }

    public override bool WorldDraw(Drawable drawable, WorldDraw wd)
    {
        bool result = base.WorldDraw(drawable, wd);
        try
        {
            if (drawable is not AcadDb.Polyline pl)
                return result;
            CutBox? box = SecaoStore.ReadCut(pl);
            if (box is null || !box.IsValid)
                return result;

            double z = pl.Elevation;
            short color = wd.SubEntityTraits.Color;
            wd.SubEntityTraits.Color = 8;
            List<P2> outline = box.Outline();
            for (int i = 0; i < outline.Count; i++)
            {
                P2 a = outline[i], b = outline[(i + 1) % outline.Count];
                Dashed(wd, a, b, z);
            }

            // Seta: do lado "atras" (direita) para o "frente" (esquerda), no meio da linha.
            double half = box.Length / 2.0;
            P2 mid = default, dir = default;
            double acc = 0;
            for (int i = 0; i + 1 < box.Line.Count; i++)
            {
                double len = P2.Dist(box.Line[i], box.Line[i + 1]);
                if (acc + len >= half || i == box.Line.Count - 2)
                {
                    dir = (box.Line[i + 1] - box.Line[i]).Unit();
                    mid = box.Line[i] + (dir * (half - acc));
                    break;
                }

                acc += len;
            }

            P2 n = dir.Left;
            double size = Math.Min(Math.Max(box.Length * 0.06, 1.0), Math.Max(box.Frente, 1.0));
            P2 tail = mid + (n * (-size * 0.6)), head = mid + (n * size);
            wd.SubEntityTraits.Color = 6;
            Line(wd, tail, head, z);
            Line(wd, head, head - (n * (size * 0.35)) + (dir * (size * 0.2)), z);
            Line(wd, head, head - (n * (size * 0.35)) - (dir * (size * 0.2)), z);
            wd.SubEntityTraits.Color = color;
        }
        catch
        {
            // Desenho auxiliar nunca derruba o regen.
        }

        return result;
    }

    private static void Line(WorldDraw wd, P2 a, P2 b, double z) =>
        wd.Geometry.WorldLine(new Point3d(a.X, a.Y, z), new Point3d(b.X, b.Y, z));

    private static void Dashed(WorldDraw wd, P2 a, P2 b, double z)
    {
        double len = P2.Dist(a, b);
        if (len < 1e-9)
            return;
        double dash = Math.Clamp(len / 24.0, 0.3, 3.0);
        P2 t = (b - a).Unit();
        for (double s = 0; s < len; s += dash * 2)
        {
            double e = Math.Min(len, s + dash);
            Line(wd, a + (t * s), a + (t * e), z);
        }
    }
}

/// <summary>Leva a secao da janela para o model space (polilinhas 2D + moldura + cotas).</summary>
internal static class SecaoDraw
{
    public static int Draw(AcadDb.Database db, SectionData data, Point3d origin, double exagero)
    {
        (double zmin, double zmax) = data.ViewZ();
        double length = data.Length;
        int count = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        SecaoStore.EnsureLayer(tr, db, SecaoStore.LayerDraw, 7);
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

        Point2d Map(double s, double z) => new(origin.X + s, origin.Y + ((z - zmin) * exagero));

        void Add(AcadDb.Entity e, int color)
        {
            e.SetDatabaseDefaults(db);
            e.Layer = SecaoStore.LayerDraw;
            e.ColorIndex = color;
            ms.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
            count++;
        }

        // Moldura e grade de cotas.
        AcadDb.Polyline frame = new();
        frame.AddVertexAt(0, Map(0, zmin), 0, 0, 0);
        frame.AddVertexAt(1, Map(length, zmin), 0, 0, 0);
        frame.AddVertexAt(2, Map(length, zmax), 0, 0, 0);
        frame.AddVertexAt(3, Map(0, zmax), 0, 0, 0);
        frame.Closed = true;
        Add(frame, 8);

        double zStep = SecaoGeometry.NiceStep((zmax - zmin) / 6.0);
        double textH = Math.Max(0.2, Math.Min(length, (zmax - zmin) * exagero) / 40.0);
        for (double z = Math.Ceiling(zmin / zStep) * zStep; z <= zmax + 1e-9; z += zStep)
        {
            Point2d a = Map(0, z), b = Map(length, z);
            Add(new AcadDb.Line(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0)), 252);
            Add(new AcadDb.DBText { Position = new Point3d(a.X - (textH * 0.6), a.Y, 0), Height = textH, TextString = z.ToString("0.00", CultureInfo.InvariantCulture), HorizontalMode = AcadDb.TextHorizontalMode.TextRight, AlignmentPoint = new Point3d(a.X - (textH * 0.6), a.Y, 0) }, 8);
        }

        double sStep = SecaoGeometry.NiceStep(length / 8.0);
        for (double s = 0; s <= length + 1e-9; s += sStep)
        {
            Point2d a = Map(s, zmin);
            Add(new AcadDb.Line(new Point3d(a.X, a.Y, 0), new Point3d(a.X, a.Y - (textH * 0.8), 0)), 8);
            Add(new AcadDb.DBText { Position = new Point3d(a.X, a.Y - (textH * 2.2), 0), Height = textH, TextString = s.ToString("0.0", CultureInfo.InvariantCulture), HorizontalMode = AcadDb.TextHorizontalMode.TextCenter, AlignmentPoint = new Point3d(a.X, a.Y - (textH * 2.2), 0) }, 8);
        }

        Add(new AcadDb.DBText { Position = new Point3d(origin.X, origin.Y + ((zmax - zmin) * exagero) + (textH * 1.5), 0), Height = textH * 1.3, TextString = $"{data.Box.Name}  (exagero {exagero:0.##}x)" }, 7);

        foreach (SectionItem item in data.Items)
        {
            int color = item.Ghost ? 9 : SecaoPanel.ColorFor(item);
            foreach (List<(double S, double Z)> pl in item.Polylines)
            {
                if (pl.Count < 2)
                    continue;
                AcadDb.Polyline p = new();
                for (int i = 0; i < pl.Count; i++)
                    p.AddVertexAt(i, Map(pl[i].S, Math.Clamp(pl[i].Z, zmin, zmax)), 0, 0, 0);
                Add(p, color);
            }

            foreach ((double s, double z, double r) in item.Circles)
            {
                if (Math.Abs(exagero - 1.0) < 1e-9)
                {
                    Point2d c = Map(s, z);
                    Add(new AcadDb.Circle(new Point3d(c.X, c.Y, 0), Vector3d.ZAxis, r), color);
                }
                else
                {
                    AcadDb.Polyline p = new();
                    for (int i = 0; i < 36; i++)
                    {
                        double ang = i * Math.PI * 2 / 36;
                        p.AddVertexAt(i, Map(s + (r * Math.Cos(ang)), z + (r * Math.Sin(ang))), 0, 0, 0);
                    }

                    p.Closed = true;
                    Add(p, color);
                }
            }

            foreach ((double s, double z, string label) in item.Points)
            {
                Point2d c = Map(s, z);
                double m = textH * 0.5;
                Add(new AcadDb.Line(new Point3d(c.X - m, c.Y - m, 0), new Point3d(c.X + m, c.Y + m, 0)), color);
                Add(new AcadDb.Line(new Point3d(c.X - m, c.Y + m, 0), new Point3d(c.X + m, c.Y - m, 0)), color);
                if (label.Length > 0)
                    Add(new AcadDb.DBText { Position = new Point3d(c.X + m, c.Y + m, 0), Height = textH * 0.8, TextString = label }, color);
            }
        }

        tr.Commit();
        return count;
    }
}
