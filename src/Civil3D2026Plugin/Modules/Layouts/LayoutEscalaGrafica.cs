using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// Escala grafica no layout pela escala de uma viewport (ex-ESCALAGRAF_V2.LSP, mesmo desenho):
/// barra de duas faixas com o padrao preto da referencia (0-2,5 em cima, 2,5-5 embaixo, 5-10 em
/// cima, depois blocos de 10 m alternados; sobra dividida ao meio), metragem a cada 10 m e no
/// fim, "ESCALA GRAFICA" em cima e "1:N" embaixo, ROMANS, lineweight 0,25. Diferenca da LISP:
/// sai como um bloco (move e apaga de uma vez), com a origem no canto inferior esquerdo da barra.
/// </summary>
internal static class LayoutEscalaGrafica
{
    public const string App = "C3D_ESCALAGRAF";
    private const string Estilo = "ROMANS";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static double UltimoMaximo
    {
        get => LayoutSettings.GetDouble("eg_max", 25);
        set => LayoutSettings.Set("eg_max", value);
    }

    public static double UltimaAltura
    {
        get => LayoutSettings.GetDouble("eg_texto", 4);
        set => LayoutSettings.Set("eg_texto", value);
    }

    /// <summary>Insere a escala grafica da viewport no espaco dela, com o canto inferior esquerdo da barra no ponto.</summary>
    public static OpResult Inserir(Document doc, ObjectId viewport, double maximo, double alturaTexto, Point3d ponto)
    {
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using Transaction tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(viewport, OpenMode.ForRead) is not Viewport vp)
            return new OpResult(0, 1, "O objeto escolhido nao e uma viewport.") { Mudou = false };
        OpResult r = Inserir(tr, db, vp, maximo, alturaTexto, ponto);
        tr.Commit();
        return r;
    }

    /// <summary>Uma escala grafica em cada viewport marcada, dentro dela no canto inferior esquerdo.</summary>
    public static OpResult InserirNasViewports(Document doc, IEnumerable<ObjectId> viewports, double maximo, double alturaTexto, double afastamento)
    {
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        int n = 0, falhas = 0;
        var msgs = new List<string>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in viewports)
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not Viewport vp)
                        continue;
                    // Abaixo da barra fica o "1:N" (1,65 x texto); a barra comeca acima dele.
                    var p = new Point3d(vp.CenterPoint.X - vp.Width / 2 + afastamento,
                                        vp.CenterPoint.Y - vp.Height / 2 + afastamento + alturaTexto * 2.2, 0);
                    OpResult r = Inserir(tr, db, vp, maximo, alturaTexto, p);
                    if (r.Falhas > 0)
                        falhas++;
                    else
                        n++;
                    msgs.Add(r.Mensagem);
                }
                catch (System.Exception ex)
                {
                    falhas++;
                    PluginLog.Erro("Layouts", "Escala grafica na viewport", ex);
                }
            }
            tr.Commit();
        }
        if (n == 0 && falhas == 0)
            return new OpResult(0, 0, "Marque as viewports (na miniatura ou na lista).") { Mudou = false };
        return new OpResult(n, falhas, $"Escala grafica em {n} viewport(s)" + (falhas > 0 ? $"; {falhas} falha(s) (log: Ctrl+Shift+L)." : ".") +
                                       (n == 1 ? " " + msgs[0] : ""));
    }

    private static OpResult Inserir(Transaction tr, Database db, Viewport vp, double maximo, double alturaTexto, Point3d ponto)
    {
        if (vp.CustomScale <= 0)
            return new OpResult(0, 1, "Nao foi possivel ler a escala da viewport.") { Mudou = false };
        if (maximo <= 0 || alturaTexto <= 0)
            return new OpResult(0, 1, "Comprimento e altura do texto precisam ser maiores que zero.") { Mudou = false };

        // mm de papel por metro do Model: CustomScale = mm de papel por unidade do Model.
        double mmPorUnidade = LayoutReader.FatorUnidade(db); // mm reais por unidade do Model (metros: 1000)
        double ppm = vp.CustomScale * 1000.0 / mmPorUnidade;
        double den = mmPorUnidade / vp.CustomScale;
        string nome = $"C3D_ESCALAGRAF_1-{Math.Round(den).ToString(Inv)}_{Fmt(maximo)}m_{Fmt(alturaTexto)}";

        ObjectId def = GarantirBloco(tr, db, nome, ppm, den, maximo, alturaTexto);
        var espaco = (BlockTableRecord)tr.GetObject(vp.OwnerId, OpenMode.ForWrite, false, true);
        var br = new BlockReference(ponto, def);
        espaco.AppendEntity(br);
        tr.AddNewlyCreatedDBObject(br, true);
        GarantirApp(tr, db);
        br.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, App),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "max=" + maximo.ToString("R", Inv)),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "texto=" + alturaTexto.ToString("R", Inv)));
        return new OpResult(1, 0, $"Escala grafica criada - 1:{Math.Round(den).ToString(Inv)} - ate {Fmt(maximo)} m.");
    }

    // ------------------------------------------------------------------
    // Desenho (o mesmo da LISP, em coordenadas do bloco: canto inferior esquerdo da barra na origem)
    // ------------------------------------------------------------------

    private static ObjectId GarantirBloco(Transaction tr, Database db, string nome, double ppm, double den, double maxm, double th)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (bt.Has(nome))
            return bt[nome];
        ObjectId estilo = GarantirEstilo(tr, db);
        bt.UpgradeOpen();
        var btr = new BlockTableRecord { Name = nome, Origin = Point3d.Origin, Comments = "Escala grafica (Civil3D2026Plugin, ESCALAGRAF)" };
        ObjectId id = bt.Add(btr);
        tr.AddNewlyCreatedDBObject(btr, true);

        void Add(Entity e)
        {
            e.Layer = "0";
            e.ColorIndex = 0; // ByBlock: cor da insercao (na LISP, a layer atual)
            e.LineWeight = LineWeight.LineWeight025;
            btr.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
        }
        void Linha(double x1, double y1, double x2, double y2) => Add(new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0)));
        void Bloco(double x1, double x2, double ya, double yb) =>
            Add(new Solid(new Point3d(x1, ya, 0), new Point3d(x2, ya, 0), new Point3d(x1, yb, 0), new Point3d(x2, yb, 0)));
        void Texto(string txt, double x, double y, double h)
        {
            var p = new Point3d(x, y, 0);
            var t = new DBText
            {
                TextString = txt,
                Height = h,
                TextStyleId = estilo,
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid,
                Position = p,
                AlignmentPoint = p
            };
            Add(t);
            t.AdjustAlignment(db);
        }

        const double x0 = 0, y0 = 0;
        double totalw = maxm * ppm;
        double rowh = th * 0.62;
        double ymid = y0 + rowh, ytop = y0 + 2 * rowh;

        // Contorno e divisao horizontal.
        Linha(x0, y0, x0 + totalw, y0);
        Linha(x0 + totalw, y0, x0 + totalw, ytop);
        Linha(x0 + totalw, ytop, x0, ytop);
        Linha(x0, ytop, x0, y0);
        Linha(x0, ymid, x0 + totalw, ymid);

        // Preenchimento.
        void Preto(double a, double b, bool emCima)
        {
            if (b <= a)
                return;
            if (emCima)
                Bloco(x0 + a * ppm, x0 + b * ppm, ymid, ytop);
            else
                Bloco(x0 + a * ppm, x0 + b * ppm, y0, ymid);
        }
        Preto(0, Math.Min(maxm, 2.5), true);
        if (maxm > 2.5)
            Preto(2.5, Math.Min(maxm, 5.0), false);
        if (maxm > 5.0)
            Preto(5.0, Math.Min(maxm, 10.0), true);
        bool upper = false;
        double pos = 10.0;
        while (pos < maxm - 1e-6)
        {
            double resto = maxm - pos;
            if (resto >= 10.0)
            {
                Preto(pos, pos + 10.0, upper);
                pos += 10.0;
                upper = !upper;
            }
            else if (resto <= 5.000001)
            {
                double meio = pos + resto / 2.0;
                Preto(pos, meio, upper);
                upper = !upper;
                Preto(meio, maxm, upper);
                pos = maxm;
            }
            else
            {
                Preto(pos, pos + 5.0, upper);
                upper = !upper;
                Preto(pos + 5.0, maxm, upper);
                pos = maxm;
            }
        }

        // Metragem: 0, 10, 20... e o final.
        void Marca(double v, string rotulo)
        {
            double xx = x0 + v * ppm;
            Linha(xx, y0, xx, ytop + th * 0.90);
            Texto(Math.Abs(v - maxm) < 1e-9 ? rotulo + "m" : rotulo, xx, ytop + th * 1.55, th);
        }
        Marca(0, "0");
        for (double m = 10.0; m < maxm - 1e-6; m += 10.0)
            Marca(m, Fmt(m));
        Marca(maxm, Fmt(maxm));

        Texto("ESCALA GRAFICA", x0 + totalw / 2, ytop + th * 3.25, th);
        Texto("1:" + Math.Round(den).ToString(Inv), x0 + totalw / 2, y0 - th * 1.65, th);
        return id;
    }

    /// <summary>Inteiro sem casas; senao 2 casas (como EG-Fmt).</summary>
    private static string Fmt(double v)
    {
        double i = Math.Floor(v + 1e-7);
        return Math.Abs(v - i) < 1e-6 ? i.ToString("0", Inv) : v.ToString("0.00", Inv);
    }

    private static ObjectId GarantirEstilo(Transaction tr, Database db)
    {
        var st = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (st.Has(Estilo))
            return st[Estilo];
        st.UpgradeOpen();
        var r = new TextStyleTableRecord { Name = Estilo, FileName = "romans.shx", XScale = 1 };
        ObjectId id = st.Add(r);
        tr.AddNewlyCreatedDBObject(r, true);
        return id;
    }

    private static void GarantirApp(Transaction tr, Database db)
    {
        var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (rat.Has(App))
            return;
        rat.UpgradeOpen();
        var r = new RegAppTableRecord { Name = App };
        rat.Add(r);
        tr.AddNewlyCreatedDBObject(r, true);
    }
}
