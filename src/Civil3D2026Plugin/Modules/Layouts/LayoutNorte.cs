using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// Seta do norte nos layouts. O bloco C3D_NORTE e a rosa dos ventos "Arrow10" do Civil 3D
/// (circulo central, 4 pontas principais e 4 diagonais preenchidas, N/S/E/W), normalizada para
/// 1 unidade de altura: a escala da insercao e o tamanho em mm no papel. O norte do Model (+Y)
/// aparece no papel girado pelo TwistAngle da viewport, entao a rotacao do bloco = giro da
/// viewport em que ele esta. "Girar" refaz isso em todos os layouts (e as sheets do Plan &amp;
/// Profile fazem sozinhas ao atualizar), inclusive nas copias do layout base.
/// </summary>
internal static class LayoutNorte
{
    public const string Bloco = "C3D_NORTE";
    public const string App = "C3D_NORTE";
    private const string Estilo = "C3D_NORTE";
    private const string Mod = "Layouts";

    /// <summary>Altura total do desenho original (do pe do S ao topo do N), para normalizar em 1.</summary>
    private const double AlturaOriginal = 0.5331;

    // ------------------------------------------------------------------
    // Inserir
    // ------------------------------------------------------------------

    /// <summary>
    /// Insere no espaco do papel do layout, no ponto dado, girado pela viewport do ponto (ou pela
    /// dada). tamanho = altura total em mm. No Model: norte para cima (+Y), tamanho pela escala de anotacao.
    /// </summary>
    public static OpResult Inserir(Document doc, ObjectId espaco, Point3d ponto, double tamanho, ObjectId viewport)
    {
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using Transaction tr = db.TransactionManager.StartTransaction();
        ObjectId def = GarantirBloco(tr, db);
        var btr = (BlockTableRecord)tr.GetObject(espaco, OpenMode.ForWrite, false, true);
        bool model = btr.ObjectId == SymbolUtilityServices.GetBlockModelSpaceId(db);
        double escala = tamanho;
        double rot = 0;
        string onde;
        if (model)
        {
            escala = tamanho * UnidadesPorMmModel(db);
            onde = "no Model (norte para cima)";
        }
        else
        {
            Viewport? vp = viewport.IsNull ? ViewportDoPonto(tr, btr, ponto) : tr.GetObject(viewport, OpenMode.ForRead) as Viewport;
            rot = vp is null ? 0 : Rotacao(vp);
            onde = vp is null ? "sem viewport embaixo (norte para cima)" : $"girado {rot * 180 / Math.PI:0.##} graus pela viewport";
        }
        var br = new BlockReference(ponto, def) { ScaleFactors = new Scale3d(escala), Rotation = rot };
        LayoutConjunto.Layer(tr, db, br, PadraoConjunto.Ler().NorteLayer);
        btr.AppendEntity(br);
        tr.AddNewlyCreatedDBObject(br, true);
        Marcar(tr, db, br);
        tr.Commit();
        return new OpResult(1, 0, $"Norte inserido ({tamanho:0.#} mm), {onde}.");
    }

    /// <summary>Um norte em cada viewport marcada, no canto escolhido (dentro da viewport, a "afastamento" mm das bordas).</summary>
    public static OpResult InserirNasViewports(Document doc, IEnumerable<ObjectId> viewports, double tamanho, string canto, double afastamento)
    {
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        int n = 0, falhas = 0;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            ObjectId def = GarantirBloco(tr, db);
            foreach (ObjectId id in viewports)
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not Viewport vp)
                        continue;
                    double meio = tamanho / 2 + afastamento;
                    double x = canto.Contains("esquerdo") ? vp.CenterPoint.X - vp.Width / 2 + meio : vp.CenterPoint.X + vp.Width / 2 - meio;
                    double y = canto.Contains("inferior") ? vp.CenterPoint.Y - vp.Height / 2 + meio : vp.CenterPoint.Y + vp.Height / 2 - meio;
                    var btr = (BlockTableRecord)tr.GetObject(vp.OwnerId, OpenMode.ForWrite, false, true);
                    var br = new BlockReference(new Point3d(x, y, 0), def) { ScaleFactors = new Scale3d(tamanho), Rotation = Rotacao(vp) };
                    LayoutConjunto.Layer(tr, db, br, PadraoConjunto.Ler().NorteLayer);
                    btr.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);
                    Marcar(tr, db, br);
                    n++;
                }
                catch (System.Exception ex)
                {
                    falhas++;
                    PluginLog.Erro(Mod, "Norte: insercao na viewport", ex);
                }
            }
            tr.Commit();
        }
        return new OpResult(n, falhas, n == 0 && falhas == 0
            ? "Marque as viewports (na miniatura ou na lista)."
            : $"Norte inserido em {n} viewport(s), no canto {canto}" + (falhas > 0 ? $"; {falhas} falha(s) (log: Ctrl+Shift+L)." : "."));
    }

    // ------------------------------------------------------------------
    // Girar pelas viewports
    // ------------------------------------------------------------------

    /// <summary>Gira os nortes de todos os layouts pela viewport em que cada um esta.</summary>
    public static OpResult Girar(Document doc)
    {
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        int n, total;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var espacos = new List<ObjectId>();
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dic)
                if (tr.GetObject(e.Value, OpenMode.ForRead) is Autodesk.AutoCAD.DatabaseServices.Layout { ModelType: false } lay)
                    espacos.Add(lay.BlockTableRecordId);
            (n, total) = Girar(tr, espacos);
            tr.Commit();
        }
        string msg = total == 0
            ? $"Nenhum norte nos layouts (bloco {Bloco}, inserido pelo plugin)."
            : n == 0 ? $"{total} norte(s) conferido(s): ja estavam certos." : $"{n} de {total} norte(s) girado(s) pela viewport.";
        return new OpResult(n, 0, msg) { Mudou = n > 0 };
    }

    /// <summary>
    /// Gira os nortes dos espacos de papel dados pela viewport em que cada um esta. So escreve o
    /// que muda. Retorna (girados, encontrados).
    /// </summary>
    public static (int Girados, int Total) Girar(Transaction tr, IEnumerable<ObjectId> espacos)
    {
        int girados = 0, total = 0;
        foreach (ObjectId espaco in espacos.Distinct())
        {
            if (espaco.IsNull || espaco.IsErased || tr.GetObject(espaco, OpenMode.ForRead) is not BlockTableRecord btr)
                continue;
            List<(Viewport Vp, Extents2d Area)>? vps = null;
            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference br || !EhNorte(tr, br))
                    continue;
                total++;
                vps ??= Viewports(tr, btr);
                Viewport? vp = Escolher(vps, br.Position);
                if (vp is null)
                    continue;
                double rot = Rotacao(vp);
                if (Math.Abs(Math.IEEERemainder(br.Rotation - rot, 2 * Math.PI)) < 1e-9)
                    continue;
                var w = (BlockReference)tr.GetObject(id, OpenMode.ForWrite, false, true);
                w.Rotation = rot;
                girados++;
            }
        }
        return (girados, total);
    }

    /// <summary>Norte do plugin: XData C3D_NORTE ou bloco C3D_NORTE.</summary>
    private static bool EhNorte(Transaction tr, BlockReference br)
    {
        using (ResultBuffer? rb = br.GetXDataForApplication(App))
            if (rb is not null)
                return true;
        try
        {
            ObjectId def = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            return ((BlockTableRecord)tr.GetObject(def, OpenMode.ForRead)).Name.Equals(Bloco, StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>Giro com que o norte do Model (+Y) aparece no papel.</summary>
    public static double Rotacao(Viewport vp)
    {
        double r = Math.IEEERemainder(vp.TwistAngle, 2 * Math.PI);
        return r < 0 ? r + 2 * Math.PI : r;
    }

    /// <summary>Viewports de planta do layout (sem a do proprio papel), com o retangulo no papel.</summary>
    private static List<(Viewport Vp, Extents2d Area)> Viewports(Transaction tr, BlockTableRecord btr)
    {
        var lista = new List<(Viewport, Extents2d)>();
        var papel = new HashSet<ObjectId>();
        if (!btr.LayoutId.IsNull && tr.GetObject(btr.LayoutId, OpenMode.ForRead) is Autodesk.AutoCAD.DatabaseServices.Layout lay)
            papel = LayoutReader.ViewportsDoPapel(tr, lay, btr);
        foreach (ObjectId id in btr)
        {
            if (papel.Contains(id) || tr.GetObject(id, OpenMode.ForRead) is not Viewport vp || vp.Number == 1)
                continue;
            if (!vp.ViewDirection.IsParallelTo(Vector3d.ZAxis))
                continue;
            Point3d c = vp.CenterPoint;
            lista.Add((vp, new Extents2d(c.X - vp.Width / 2, c.Y - vp.Height / 2, c.X + vp.Width / 2, c.Y + vp.Height / 2)));
        }
        return lista;
    }

    /// <summary>A menor viewport que contem o ponto; sem nenhuma, a mais perto (o norte costuma ficar ao lado da planta).</summary>
    private static Viewport? Escolher(List<(Viewport Vp, Extents2d Area)> vps, Point3d p)
    {
        Viewport? dentro = vps.Where(v => p.X >= v.Area.MinPoint.X && p.X <= v.Area.MaxPoint.X && p.Y >= v.Area.MinPoint.Y && p.Y <= v.Area.MaxPoint.Y)
            .OrderBy(v => (v.Area.MaxPoint.X - v.Area.MinPoint.X) * (v.Area.MaxPoint.Y - v.Area.MinPoint.Y)).Select(v => v.Vp).FirstOrDefault();
        if (dentro is not null)
            return dentro;
        return vps.OrderBy(v => Distancia(v.Area, p)).Select(v => v.Vp).FirstOrDefault();
    }

    private static double Distancia(Extents2d a, Point3d p)
    {
        double dx = Math.Max(0, Math.Max(a.MinPoint.X - p.X, p.X - a.MaxPoint.X));
        double dy = Math.Max(0, Math.Max(a.MinPoint.Y - p.Y, p.Y - a.MaxPoint.Y));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static Viewport? ViewportDoPonto(Transaction tr, BlockTableRecord btr, Point3d p) => Escolher(Viewports(tr, btr), p);

    /// <summary>Unidades do Model por mm de papel, pela escala de anotacao atual (1:500 em metros = 0,5).</summary>
    private static double UnidadesPorMmModel(Database db)
    {
        try
        {
            AnnotationScale? a = db.Cannoscale;
            if (a is not null && a.PaperUnits > 0)
                return a.DrawingUnits / a.PaperUnits;
        }
        catch (System.Exception)
        {
        }
        return 1;
    }

    private static void Marcar(Transaction tr, Database db, Entity e)
    {
        var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (!rat.Has(App))
        {
            rat.UpgradeOpen();
            var r = new RegAppTableRecord { Name = App };
            rat.Add(r);
            tr.AddNewlyCreatedDBObject(r, true);
        }
        e.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, App),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "norte"));
    }

    // ------------------------------------------------------------------
    // Definicao do bloco (Arrow10 do Civil 3D, normalizado)
    // ------------------------------------------------------------------

    /// <summary>Bloco C3D_NORTE; se o desenho ja tem um com esse nome, usa o dele.</summary>
    public static ObjectId GarantirBloco(Transaction tr, Database db)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (bt.Has(Bloco))
            return bt[Bloco];

        ObjectId estilo = GarantirEstilo(tr, db);
        bt.UpgradeOpen();
        var btr = new BlockTableRecord { Name = Bloco, Origin = Point3d.Origin, Comments = "Seta do norte (Civil3D2026Plugin, Arrow10 do Civil 3D)" };
        ObjectId id = bt.Add(btr);
        tr.AddNewlyCreatedDBObject(btr, true);

        const double k = 1 / AlturaOriginal;
        void Add(Entity e)
        {
            e.Layer = "0";
            e.ColorIndex = 0; // ByBlock: pega a cor e a layer da insercao
            e.LineWeight = LineWeight.ByBlock;
            btr.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
        }

        // Circulo central (contorno + preenchimento).
        double r = 0.040861216827903 * k;
        Add(new Circle(Point3d.Origin, Vector3d.ZAxis, r));
        Hachura(btr, tr, Add, new[] { new[] { (r, 0.0, 1.0), (-r, 0.0, 1.0) } });

        // Pontas: 4 principais (N, L, S, O) e 4 diagonais, como no Arrow10.
        double[][] pontas =
        {
            new[] { -0.0223, 0.049, 0.0, 0.207, 0.0223, 0.049 },
            new[] { 0.049, -0.0223, 0.207, 0.0, 0.049, 0.0223 },
            new[] { 0.0223, -0.049, 0.0, -0.207, -0.0223, -0.049 },
            new[] { -0.049, 0.0223, -0.207, 0.0, -0.049, -0.0223 },
            new[] { 0.0499, 0.0648, 0.1227, 0.1227, 0.0648, 0.0499 },
            new[] { 0.0648, -0.0499, 0.1227, -0.1227, 0.0499, -0.0648 },
            new[] { -0.0499, -0.0648, -0.1227, -0.1227, -0.0648, -0.0499 },
            new[] { -0.0648, 0.0499, -0.1227, 0.1227, -0.0499, 0.0648 }
        };
        var lacos = new List<(double, double, double)[]>();
        foreach (double[] t in pontas)
        {
            var pl = new Polyline { Closed = true };
            for (int i = 0; i < 3; i++)
                pl.AddVertexAt(i, new Point2d(t[2 * i] * k, t[2 * i + 1] * k), 0, 0, 0);
            Add(pl);
            lacos.Add(new[] { (t[0] * k, t[1] * k, 0.0), (t[2] * k, t[3] * k, 0.0), (t[4] * k, t[5] * k, 0.0) });
        }
        Hachura(btr, tr, Add, lacos);

        // Letras (centro embaixo no ponto de alinhamento, como no original).
        (string Txt, double X, double Y, double H)[] letras =
        {
            ("N", 0.0009871363709086, 0.2315747211636958, 0.0416513835214791),
            ("S", 0.0009871363709086, -0.2597873765841521, 0.0200130499681919),
            ("E", 0.2242668269165038, -0.0166654479633579, 0.0200130499681919),
            ("W", -0.2300246007129447, -0.0162120327978974, 0.0200130499681919)
        };
        foreach ((string txt, double x, double y, double h) in letras)
        {
            var p = new Point3d(x * k, y * k, 0);
            var t = new DBText
            {
                TextString = txt,
                Height = h * k,
                TextStyleId = estilo,
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextBottom,
                Position = p,
                AlignmentPoint = p
            };
            Add(t);
            t.AdjustAlignment(db);
        }
        return id;
    }

    /// <summary>Hachura SOLID com os lacos dados (x, y, bulge por vertice).</summary>
    private static void Hachura(BlockTableRecord btr, Transaction tr, Action<Entity> add, IEnumerable<(double X, double Y, double Bulge)[]> lacos)
    {
        var h = new Hatch();
        add(h);
        h.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
        foreach ((double X, double Y, double Bulge)[] laco in lacos)
        {
            var pts = new Point2dCollection();
            var bulges = new DoubleCollection();
            foreach ((double x, double y, double b) in laco)
            {
                pts.Add(new Point2d(x, y));
                bulges.Add(b);
            }
            pts.Add(new Point2d(laco[0].X, laco[0].Y));
            bulges.Add(0);
            h.AppendLoop(HatchLoopTypes.Polyline, pts, bulges);
        }
        h.EvaluateHatch(true);
    }

    private static ObjectId GarantirEstilo(Transaction tr, Database db)
    {
        var st = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (st.Has(Estilo))
            return st[Estilo];
        st.UpgradeOpen();
        var r = new TextStyleTableRecord { Name = Estilo, FileName = "arial.ttf", XScale = 1 };
        ObjectId id = st.Add(r);
        tr.AddNewlyCreatedDBObject(r, true);
        return id;
    }

    internal static string Mm(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
