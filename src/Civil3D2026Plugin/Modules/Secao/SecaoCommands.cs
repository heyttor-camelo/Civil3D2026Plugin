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

        // Numero do corte novo: lido antes de abrir a transacao de escrita (o model space
        // ja aberto para escrita nao pode ser reaberto por uma transacao open/close).
        int n = SecaoStore.ReadCuts(db).Count + 1;
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
        ed.WriteMessage($"\n  Caixa de corte criada (layer {SecaoStore.LayerCut}): mova os grips e a janela acompanha; altere profundidades e cotas na aba Propriedades.");
        int found = SecaoStore.ReadCuts(db).Count(c => c.Id == id);
        if (found == 0)
            ed.WriteMessage("\n  [C3DSECAO] A polilinha foi criada, mas a leitura de volta nao achou a caixa (XData). Me avise com esta mensagem.");
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
        List<DrawLayer>? layers = SecaoPanel.ExportLayers();
        if (layers is null || layers.Count == 0)
        {
            ed.WriteMessage("\n  Nenhuma secao na janela (abra C3DSECAO e escolha um corte, uma estaca ou secoes fixadas em Comparar).");
            return;
        }

        SecaoStore.Prefs prefs = SecaoPanel.CurrentPrefs ?? SecaoStore.LoadPrefs();
        double ex = SecaoPanel.CurrentExagero;
        Point3d origin;
        while (true)
        {
            PromptPointOptions opt = new($"\nCanto inferior esquerdo da secao [Escala/Texto/eXagero] <escala 1:{prefs.EscalaDesenho:0}, texto {prefs.TextoMm:0.0#} mm, exagero {ex:0.##}x>: ");
            opt.Keywords.Add("Escala");
            opt.Keywords.Add("Texto");
            opt.Keywords.Add("eXagero");
            opt.AppendKeywordsToMessage = false;
            PromptPointResult pr = ed.GetPoint(opt);
            if (pr.Status == PromptStatus.OK)
            {
                origin = pr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                break;
            }

            if (pr.Status != PromptStatus.Keyword)
                return;

            switch (pr.StringResult)
            {
                case "Escala":
                    double n = AskDistance(ed, "\nEscala do desenho 1:", prefs.EscalaDesenho);
                    if (!double.IsNaN(n) && n >= 1)
                        prefs.EscalaDesenho = n;
                    break;
                case "Texto":
                    double mm = AskDistance(ed, "\nAltura do texto no papel [mm]", prefs.TextoMm);
                    if (!double.IsNaN(mm) && mm >= 0.5)
                        prefs.TextoMm = mm;
                    break;
                default:
                    double e = AskDistance(ed, "\nExagero vertical", ex);
                    if (!double.IsNaN(e) && e > 0)
                        ex = e;
                    break;
            }

            SecaoStore.SavePrefs(prefs);
        }

        int count = SecaoDraw.Draw(doc.Database, layers, origin, ex, prefs);
        ed.WriteMessage($"\n  Secao desenhada: {count} entidade(s) na layer {SecaoStore.LayerDraw} (escala 1:{prefs.EscalaDesenho:0}, texto {prefs.TextoMm * prefs.EscalaDesenho / 1000:0.###} m, exagero {ex:0.##}x).");
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
                AcadDb.DBObject obj = e.DBObject;
                SecaoReader.Invalidate(obj.Database, obj.ObjectId);
                if (obj is CivilDb.Corridor)
                    SecaoPanel.InvalidateCorridor(obj.ObjectId);
                else if (obj is AcadDb.Polyline pl && SecaoStore.IsCut(pl))
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
                SecaoReader.Invalidate(e.DBObject.Database, e.DBObject.ObjectId);
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

/// <summary>Uma secao a desenhar no model (normal: a mostrada; comparacao: as visiveis, sobrepostas).</summary>
internal sealed record DrawLayer(SectionData Data, double Offset, int? Aci, string Label, bool Ghosts);

/// <summary>
/// Leva a secao da janela para o model space: moldura, grade de cotas e distancias,
/// polilinhas 2D, circulos, pontos e rotulos; destaque (espessura) e hachura (ANSI31)
/// conforme escolhido na janela; itens ocultos ficam de fora. Os textos tem a altura
/// do papel (mm) na escala do desenho, e o passo das reguas se ajusta para nao encavalar.
/// </summary>
internal static class SecaoDraw
{
    public static int Draw(AcadDb.Database db, IReadOnlyList<DrawLayer> layers, Point3d origin, double exagero, SecaoStore.Prefs prefs)
    {
        if (layers.Count == 0)
            return 0;

        double zmin = double.MaxValue, zmax = double.MinValue, smin = double.MaxValue, smax = double.MinValue;
        foreach (DrawLayer l in layers)
        {
            (double a, double b) = l.Data.ViewZ();
            zmin = Math.Min(zmin, a);
            zmax = Math.Max(zmax, b);
            smin = Math.Min(smin, l.Offset);
            smax = Math.Max(smax, l.Offset + l.Data.Length);
        }

        double textH = Math.Max(0.01, prefs.TextoMm * prefs.EscalaDesenho / 1000.0);
        double charW = textH * 0.85;
        int count = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        SecaoStore.EnsureLayer(tr, db, SecaoStore.LayerDraw, 7);
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

        Point2d Map(double s, double z) => new(origin.X + (s - smin), origin.Y + ((z - zmin) * exagero));
        Point3d Map3(double s, double z)
        {
            Point2d p = Map(s, z);
            return new Point3d(p.X, p.Y, 0);
        }

        void Add(AcadDb.Entity e, int color, bool strong = false)
        {
            e.SetDatabaseDefaults(db);
            e.Layer = SecaoStore.LayerDraw;
            e.ColorIndex = color;
            if (strong)
                e.LineWeight = AcadDb.LineWeight.LineWeight050;
            ms.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
            count++;
        }

        void Text(string text, Point3d at, double h, int color, AcadDb.TextHorizontalMode mode = AcadDb.TextHorizontalMode.TextLeft)
        {
            AcadDb.DBText t = new() { Position = at, Height = h, TextString = text };
            if (mode != AcadDb.TextHorizontalMode.TextLeft)
            {
                t.HorizontalMode = mode;
                t.AlignmentPoint = at;
            }

            Add(t, color);
        }

        // Rotulos sem encavalar: sobe uma linha por vez ate achar lugar.
        List<(double X0, double Y0, double X1, double Y1)> placed = new();
        void Label(string text, double x, double y, int color)
        {
            double w = text.Length * charW, h = textH * 1.25;
            double y0 = y;
            for (int i = 0; i < 8 && placed.Any(r => x < r.X1 && x + w > r.X0 && y0 < r.Y1 && y0 + h > r.Y0); i++)
                y0 += h;
            placed.Add((x, y0, x + w, y0 + h));
            Text(text, new Point3d(x, y0, 0), textH, color);
        }

        // Moldura.
        AcadDb.Polyline frame = new();
        frame.AddVertexAt(0, Map(smin, zmin), 0, 0, 0);
        frame.AddVertexAt(1, Map(smax, zmin), 0, 0, 0);
        frame.AddVertexAt(2, Map(smax, zmax), 0, 0, 0);
        frame.AddVertexAt(3, Map(smin, zmax), 0, 0, 0);
        frame.Closed = true;
        Add(frame, 8);

        // Reguas: passo que cabe o texto (cotas: 2,2 alturas; distancias: 7 alturas).
        double zStep = SecaoGeometry.NiceStep(Math.Max((zmax - zmin) / 6.0, textH * 2.2 / exagero));
        double sStep = SecaoGeometry.NiceStep(Math.Max((smax - smin) / 8.0, textH * 7.0));
        string zFmt = zStep < 0.1 ? "0.000" : zStep < 1 ? "0.00" : "0.0";
        for (double z = Math.Ceiling(zmin / zStep) * zStep; z <= zmax + 1e-9; z += zStep)
        {
            Add(new AcadDb.Line(Map3(smin, z), Map3(smax, z)), 252);
            Point3d at = Map3(smin, z);
            Text(z.ToString(zFmt, CultureInfo.InvariantCulture), new Point3d(at.X - (textH * 0.6), at.Y - (textH * 0.5), 0), textH, 8, AcadDb.TextHorizontalMode.TextRight);
        }

        string sFmt = sStep < 1 ? "0.0" : "0";
        for (double s = Math.Ceiling(smin / sStep) * sStep; s <= smax + 1e-9; s += sStep)
        {
            Point3d at = Map3(s, zmin);
            Add(new AcadDb.Line(at, new Point3d(at.X, at.Y - (textH * 0.8), 0)), 8);
            Text(s.ToString(sFmt, CultureInfo.InvariantCulture), new Point3d(at.X, at.Y - (textH * 2.2), 0), textH, 8, AcadDb.TextHorizontalMode.TextCenter);
        }

        // Titulo e legenda.
        Point3d top = Map3(smin, zmax);
        string title = layers.Count == 1 ? layers[0].Data.Box.Name : $"Comparacao de {layers.Count} secoes";
        Text($"{title}  (exagero {exagero:0.##}x, escala 1:{prefs.EscalaDesenho:0})", new Point3d(top.X, top.Y + (textH * 1.5), 0), textH * 1.4, 7);
        if (layers.Count > 1)
        {
            Point3d right = Map3(smax, zmax);
            double ly = right.Y + (textH * 1.5);
            foreach (DrawLayer l in layers)
            {
                int c = l.Aci ?? 7;
                Add(new AcadDb.Line(new Point3d(right.X + textH, ly + (textH * 0.5), 0), new Point3d(right.X + (textH * 3), ly + (textH * 0.5), 0)), c, true);
                Text(l.Label, new Point3d(right.X + (textH * 3.5), ly, 0), textH, c);
                ly -= textH * 1.6;
            }
        }

        foreach (DrawLayer layer in layers)
        {
            double off = layer.Offset;
            foreach (SectionItem item in layer.Data.Items)
            {
                if (item.Ghost && !layer.Ghosts)
                    continue;
                ItemStyle style = prefs.StyleOf(item);
                if (style == ItemStyle.Oculto)
                    continue;
                bool strong = style == ItemStyle.Destaque;
                int color = layer.Aci ?? (item.Ghost && !strong ? 9 : SecaoPanel.ColorFor(item));

                if (style == ItemStyle.Hachura && item.Fills.Count > 0)
                {
                    try
                    {
                        AcadDb.Hatch hatch = new();
                        hatch.SetDatabaseDefaults(db);
                        hatch.Layer = SecaoStore.LayerDraw;
                        hatch.ColorIndex = color;
                        ms.AppendEntity(hatch);
                        tr.AddNewlyCreatedDBObject(hatch, true);
                        hatch.PatternScale = Math.Max(0.01, textH * 0.4);
                        hatch.SetHatchPattern(AcadDb.HatchPatternType.PreDefined, "ANSI31");
                        hatch.Associative = false;
                        foreach (List<(double S, double Z)> poly in item.Fills)
                        {
                            Point2dCollection pts = new();
                            DoubleCollection bulges = new();
                            for (int i = 0; i + 1 < poly.Count; i++)
                            {
                                pts.Add(Map(poly[i].S + off, Math.Clamp(poly[i].Z, zmin, zmax)));
                                bulges.Add(0.0);
                            }

                            if (pts.Count >= 3)
                                hatch.AppendLoop(AcadDb.HatchLoopTypes.Polyline, pts, bulges);
                        }

                        hatch.EvaluateHatch(true);
                        count++;
                    }
                    catch
                    {
                        // Regiao degenerada: fica so o contorno.
                    }
                }

                foreach (List<(double S, double Z)> pl in item.Polylines)
                {
                    if (pl.Count < 2)
                        continue;
                    AcadDb.Polyline p = new();
                    for (int i = 0; i < pl.Count; i++)
                        p.AddVertexAt(i, Map(pl[i].S + off, Math.Clamp(pl[i].Z, zmin, zmax)), 0, 0, 0);
                    Add(p, color, strong);
                }

                foreach ((double s, double z, double r) in item.Circles)
                {
                    if (Math.Abs(exagero - 1.0) < 1e-9)
                    {
                        Point2d c = Map(s + off, z);
                        Add(new AcadDb.Circle(new Point3d(c.X, c.Y, 0), Vector3d.ZAxis, r), color, strong);
                    }
                    else
                    {
                        AcadDb.Polyline p = new();
                        for (int i = 0; i < 36; i++)
                        {
                            double ang = i * Math.PI * 2 / 36;
                            p.AddVertexAt(i, Map(s + off + (r * Math.Cos(ang)), z + (r * Math.Sin(ang))), 0, 0, 0);
                        }

                        p.Closed = true;
                        Add(p, color, strong);
                    }
                }

                foreach ((double s, double z, string label) in item.Points)
                {
                    Point2d c = Map(s + off, z);
                    double m = textH * 0.4;
                    Add(new AcadDb.Line(new Point3d(c.X - m, c.Y - m, 0), new Point3d(c.X + m, c.Y + m, 0)), color);
                    Add(new AcadDb.Line(new Point3d(c.X - m, c.Y + m, 0), new Point3d(c.X + m, c.Y - m, 0)), color);
                    if (prefs.Rotulos && label.Length > 0)
                        Label(label, c.X + m, c.Y + m, color);
                }

                if (prefs.Rotulos && !item.Ghost && layers.Count == 1 && (item.Kind == ItemKind.Superficie || item.Kind == ItemKind.Corredor) &&
                    item.Polylines.Count > 0 && item.Polylines[0].Count > 0)
                {
                    (double s, double z) = item.Polylines[0][^1];
                    Point2d e = Map(s + off, Math.Clamp(z, zmin, zmax));
                    Label(item.Name, e.X + (textH * 0.5), e.Y, color);
                }
            }
        }

        tr.Commit();
        return count;
    }
}
