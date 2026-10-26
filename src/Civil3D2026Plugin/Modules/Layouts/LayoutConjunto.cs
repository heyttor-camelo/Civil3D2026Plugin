using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Canto da viewport usado como ancora de um item (I/S = inferior/superior, E/D = esquerdo/direito).</summary>
internal enum CantoVp { IE, ID, SE, SD }

/// <summary>Posicao de um item em relacao a viewport: canto mais proximo e deslocamento (mm de papel).</summary>
internal sealed record Ancora(CantoVp Canto, double Dx, double Dy);

/// <summary>
/// Padrao de insercao do conjunto malha + norte + escala grafica. Tamanhos em mm de papel: assim o
/// mesmo padrao serve para qualquer escala (o intervalo da malha e o comprimento da escala grafica
/// sao escolhidos por viewport, em valores redondos perto do tamanho no papel).
/// </summary>
internal sealed class PadraoConjunto
{
    public bool Norte { get; set; } = true;
    public double NorteTamanho { get; set; } = 20;
    public Ancora NortePos { get; set; } = new(CantoVp.SD, -20, -20);
    public string NorteLayer { get; set; } = LayerNorte;

    public bool Escala { get; set; } = true;
    public double EscalaMm { get; set; } = 60;
    public double EscalaTexto { get; set; } = 2.5;
    public Ancora EscalaPos { get; set; } = new(CantoVp.IE, 10, 15);
    public string EscalaLayer { get; set; } = LayerEscala;
    /// <summary>Total da escala grafica em metros; 0 = automatico pelo comprimento no papel.</summary>
    public double EscalaM { get; set; }

    public bool Malha { get; set; } = true;
    public double MalhaMm { get; set; } = 50;
    public double MalhaTexto { get; set; } = 2.0;
    public bool MalhaCruzes { get; set; }
    /// <summary>Transparencia das linhas da malha (0 a 90%); os textos ficam opacos.</summary>
    public double MalhaTransp { get; set; } = 50;
    public string MalhaLayer { get; set; } = LayerMalha;
    /// <summary>Intervalo da malha em metros; 0 = automatico pela distancia no papel.</summary>
    public double MalhaM { get; set; }

    public const string LayerNorte = "C3D_NORTE";
    public const string LayerEscala = "C3D_ESCALAGRAF";
    public const string LayerMalha = "C3D_MALHA";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static PadraoConjunto Ler()
    {
        var p = new PadraoConjunto();
        p.Norte = LayoutSettings.GetBool("mne_norte", p.Norte);
        p.NorteTamanho = LayoutSettings.GetDouble("mne_norte_tam", p.NorteTamanho);
        p.NortePos = LerAncora("mne_norte", p.NortePos);
        p.NorteLayer = LerLayer("mne_norte_layer", LayerNorte);
        p.Escala = LayoutSettings.GetBool("mne_esc", p.Escala);
        p.EscalaMm = LayoutSettings.GetDouble("mne_esc_mm", p.EscalaMm);
        p.EscalaTexto = LayoutSettings.GetDouble("mne_esc_texto", p.EscalaTexto);
        p.EscalaPos = LerAncora("mne_esc", p.EscalaPos);
        p.EscalaLayer = LerLayer("mne_esc_layer", LayerEscala);
        p.EscalaM = LayoutSettings.GetDouble("mne_esc_m", p.EscalaM);
        p.Malha = LayoutSettings.GetBool("mne_malha", p.Malha);
        p.MalhaMm = LayoutSettings.GetDouble("mne_malha_mm", p.MalhaMm);
        p.MalhaTexto = LayoutSettings.GetDouble("mne_malha_texto", p.MalhaTexto);
        p.MalhaCruzes = LayoutSettings.GetBool("mne_malha_cruzes", p.MalhaCruzes);
        p.MalhaTransp = LayoutSettings.GetDouble("mne_malha_transp", p.MalhaTransp);
        p.MalhaLayer = LerLayer("mne_malha_layer", LayerMalha);
        p.MalhaM = LayoutSettings.GetDouble("mne_malha_m", p.MalhaM);
        return p;
    }

    public void Salvar()
    {
        LayoutSettings.Set("mne_norte", Norte);
        LayoutSettings.Set("mne_norte_tam", NorteTamanho);
        SalvarAncora("mne_norte", NortePos);
        LayoutSettings.Set("mne_norte_layer", NorteLayer);
        LayoutSettings.Set("mne_esc", Escala);
        LayoutSettings.Set("mne_esc_mm", EscalaMm);
        LayoutSettings.Set("mne_esc_texto", EscalaTexto);
        SalvarAncora("mne_esc", EscalaPos);
        LayoutSettings.Set("mne_esc_layer", EscalaLayer);
        LayoutSettings.Set("mne_esc_m", EscalaM);
        LayoutSettings.Set("mne_malha", Malha);
        LayoutSettings.Set("mne_malha_mm", MalhaMm);
        LayoutSettings.Set("mne_malha_texto", MalhaTexto);
        LayoutSettings.Set("mne_malha_cruzes", MalhaCruzes);
        LayoutSettings.Set("mne_malha_transp", MalhaTransp);
        LayoutSettings.Set("mne_malha_layer", MalhaLayer);
        LayoutSettings.Set("mne_malha_m", MalhaM);
    }

    /// <summary>Layer salva; vazia (padrao antigo = layer atual) volta para a layer padrao do item.</summary>
    private static string LerLayer(string chave, string padrao)
    {
        string v = LayoutSettings.Get(chave, padrao).Trim();
        return v.Length == 0 ? padrao : v;
    }

    private static Ancora LerAncora(string chave, Ancora padrao)
    {
        string c = LayoutSettings.Get(chave + "_canto", padrao.Canto.ToString());
        return new Ancora(
            Enum.TryParse(c, out CantoVp canto) ? canto : padrao.Canto,
            LayoutSettings.GetDouble(chave + "_dx", padrao.Dx),
            LayoutSettings.GetDouble(chave + "_dy", padrao.Dy));
    }

    private static void SalvarAncora(string chave, Ancora a)
    {
        LayoutSettings.Set(chave + "_canto", a.Canto.ToString());
        LayoutSettings.Set(chave + "_dx", a.Dx);
        LayoutSettings.Set(chave + "_dy", a.Dy);
    }

    public string Resumo()
    {
        var partes = new List<string>();
        partes.Add(Malha ? $"malha {(MalhaCruzes ? "em cruzes" : "em linhas")} a cada {(MalhaM > 0 ? MalhaM.ToString("0.##", Inv) + " m" : "~" + MalhaMm.ToString("0.#", Inv) + " mm")}, texto {MalhaTexto.ToString("0.##", Inv)} mm, layer {MalhaLayer}" : "sem malha");
        partes.Add(Norte ? $"norte {NorteTamanho.ToString("0.#", Inv)} mm no canto {NomeCanto(NortePos.Canto)}, layer {NorteLayer}" : "sem norte");
        partes.Add(Escala ? $"escala grafica {(EscalaM > 0 ? EscalaM.ToString("0.##", Inv) + " m" : "~" + EscalaMm.ToString("0.#", Inv) + " mm")}, texto {EscalaTexto.ToString("0.##", Inv)} mm, canto {NomeCanto(EscalaPos.Canto)}, layer {EscalaLayer}" : "sem escala grafica");
        return string.Join("; ", partes);
    }

    public static string NomeCanto(CantoVp c) => c switch
    {
        CantoVp.IE => "inferior esquerdo",
        CantoVp.ID => "inferior direito",
        CantoVp.SE => "superior esquerdo",
        _ => "superior direito"
    };
}

/// <summary>
/// C3DMNE: malha de coordenadas, norte e escala grafica de uma vez, direto no layout, pelo padrao
/// do usuario. O padrao sai de uma viewport em que os tres foram lancados e ajustados a mao
/// (posicao, tamanho, layer). Cada item recebe XData C3D_MNE com o handle da viewport, para
/// refazer (inserir de novo troca os antigos) ou remover.
/// </summary>
internal static class LayoutConjunto
{
    public const string App = "C3D_MNE";
    private const string Mod = "Layouts";
    private const string EstiloTexto = "ROMANS";
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    // ------------------------------------------------------------------
    // Inserir
    // ------------------------------------------------------------------

    public static OpResult Inserir(Document doc, IEnumerable<ObjectId> viewports, PadraoConjunto p)
    {
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        int n = 0, falhas = 0;
        var avisos = new List<string>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in viewports.Distinct())
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not Viewport vp)
                        continue;
                    if (vp.CustomScale <= 0 || !vp.ViewDirection.IsParallelTo(Vector3d.ZAxis))
                    {
                        avisos.Add($"viewport {vp.Handle}: sem escala ou nao esta em planta");
                        falhas++;
                        continue;
                    }
                    Remover(tr, vp);
                    InserirNaViewport(tr, db, vp, p, avisos);
                    n++;
                }
                catch (System.Exception ex)
                {
                    falhas++;
                    PluginLog.Erro(Mod, "Malha/norte/escala na viewport", ex);
                }
            }
            tr.Commit();
        }
        foreach (string a in avisos)
            PluginLog.Aviso(Mod, "Malha/norte/escala: " + a);
        if (n == 0 && falhas == 0)
            return new OpResult(0, 0, "Escolha as viewports.") { Mudou = false };
        return new OpResult(n, falhas, $"Malha, norte e escala em {n} viewport(s)" +
            (falhas > 0 || avisos.Count > 0 ? $"; {falhas} falha(s), {avisos.Count} aviso(s) (log: Ctrl+Shift+L)." : "."));
    }

    private static void InserirNaViewport(Transaction tr, Database db, Viewport vp, PadraoConjunto p, List<string> avisos)
    {
        string handle = vp.Handle.ToString();
        double mmPorM = LayoutEscalaGrafica.MmPorMetro(db, vp);
        var espaco = (BlockTableRecord)tr.GetObject(vp.OwnerId, OpenMode.ForWrite, false, true);

        if (p.Malha)
        {
            // A malha ja sai com a marca (e os parametros) no XData.
            InserirMalha(tr, db, vp, espaco, p, mmPorM);
        }

        if (p.Norte)
        {
            ObjectId def = LayoutNorte.GarantirBloco(tr, db);
            var br = new BlockReference(Ponto(vp, p.NortePos), def)
            {
                ScaleFactors = new Scale3d(p.NorteTamanho),
                Rotation = LayoutNorte.Rotacao(vp)
            };
            Layer(tr, db, br, p.NorteLayer);
            ObjectId id = espaco.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);
            Marcar(tr, db, id, "norte", handle);
        }

        if (p.Escala)
        {
            double maximo = p.EscalaM > 0 ? p.EscalaM : ComprimentoRedondo(p.EscalaMm / mmPorM);
            OpResult r = LayoutEscalaGrafica.Inserir(tr, db, vp, maximo, p.EscalaTexto, Ponto(vp, p.EscalaPos), out ObjectId esc);
            if (esc.IsNull)
            {
                avisos.Add("escala grafica: " + r.Mensagem);
            }
            else
            {
                Layer(tr, db, (Entity)tr.GetObject(esc, OpenMode.ForWrite), p.EscalaLayer);
                Marcar(tr, db, esc, "escala", handle);
            }
        }
    }

    /// <summary>Ponto no papel: canto da viewport + deslocamento.</summary>
    private static Point3d Ponto(Viewport vp, Ancora a)
    {
        (double x, double y) = Canto(vp, a.Canto);
        return new Point3d(x + a.Dx, y + a.Dy, 0);
    }

    private static (double X, double Y) Canto(Viewport vp, CantoVp c)
    {
        double x0 = vp.CenterPoint.X - vp.Width / 2, x1 = vp.CenterPoint.X + vp.Width / 2;
        double y0 = vp.CenterPoint.Y - vp.Height / 2, y1 = vp.CenterPoint.Y + vp.Height / 2;
        return c switch
        {
            CantoVp.IE => (x0, y0),
            CantoVp.ID => (x1, y0),
            CantoVp.SE => (x0, y1),
            _ => (x1, y1)
        };
    }

    /// <summary>Canto da viewport mais perto do ponto e o deslocamento ate ele.</summary>
    private static Ancora AncoraDe(Viewport vp, Point3d p)
    {
        CantoVp melhor = CantoVp.IE;
        double dist = double.MaxValue;
        foreach (CantoVp c in Enum.GetValues(typeof(CantoVp)))
        {
            (double x, double y) = Canto(vp, c);
            double d = Math.Abs(p.X - x) + Math.Abs(p.Y - y);
            if (d < dist)
            {
                dist = d;
                melhor = c;
            }
        }
        (double cx, double cy) = Canto(vp, melhor);
        return new Ancora(melhor, Math.Round(p.X - cx, 2), Math.Round(p.Y - cy, 2));
    }

    /// <summary>Maior valor redondo (1, 2, 2,5, 5 x 10^k) que nao passa do alvo.</summary>
    private static double ComprimentoRedondo(double alvo)
    {
        if (alvo <= 0)
            return 10;
        double best = 1;
        for (int k = -1; k < 8; k++)
            foreach (double b in new[] { 1, 2, 2.5, 5 })
            {
                double v = b * Math.Pow(10, k);
                if (v <= alvo * 1.0001)
                    best = v;
            }
        return best;
    }

    /// <summary>Valor redondo (1, 2, 5 x 10^k) mais perto do alvo, em escala logaritmica.</summary>
    private static double IntervaloRedondo(double alvo)
    {
        double best = 1, erro = double.MaxValue;
        for (int k = -1; k < 8; k++)
            foreach (double b in new[] { 1, 2, 5 })
            {
                double v = b * Math.Pow(10, k);
                double e = Math.Abs(Math.Log(v / alvo));
                if (e < erro)
                {
                    erro = e;
                    best = v;
                }
            }
        return best;
    }

    // ------------------------------------------------------------------
    // Malha de coordenadas no papel
    // ------------------------------------------------------------------

    private static Matrix3d Dcs2Wcs(Viewport vp) =>
        Matrix3d.Rotation(-vp.TwistAngle, vp.ViewDirection, vp.ViewTarget) *
        Matrix3d.Displacement(vp.ViewTarget - Point3d.Origin) *
        Matrix3d.PlaneToWorld(vp.ViewDirection);

    private static Matrix3d Dcs2Psdcs(Viewport vp) =>
        Matrix3d.Scaling(vp.CustomScale, vp.CenterPoint) *
        Matrix3d.Displacement(vp.CenterPoint - Point3d.Origin) *
        Matrix3d.Displacement(new Vector3d(-vp.ViewCenter.X, -vp.ViewCenter.Y, 0.0));

    /// <summary>
    /// Bloco com a malha da viewport, em coordenadas do papel (insercao na origem): linhas (ou cruzes
    /// com traco na borda) a cada intervalo redondo do Model, cortadas no retangulo da viewport, e as
    /// coordenadas E= / N= nas bordas, ao longo das linhas.
    /// </summary>
    private static ObjectId InserirMalha(Transaction tr, Database db, Viewport vp, BlockTableRecord espaco, PadraoConjunto p, double mmPorM)
    {
        double unidadesPorM = 1000.0 / LayoutReader.FatorUnidade(db);
        double intervalo = (p.MalhaM > 0 ? p.MalhaM : IntervaloRedondo(p.MalhaMm / mmPorM)) * unidadesPorM;
        Matrix3d ps2wcs = Dcs2Wcs(vp) * Dcs2Psdcs(vp).Inverse();
        Matrix3d wcs2ps = ps2wcs.Inverse();

        double x0 = vp.CenterPoint.X - vp.Width / 2, x1 = vp.CenterPoint.X + vp.Width / 2;
        double y0 = vp.CenterPoint.Y - vp.Height / 2, y1 = vp.CenterPoint.Y + vp.Height / 2;
        Point3d[] cantos = { new(x0, y0, 0), new(x1, y0, 0), new(x1, y1, 0), new(x0, y1, 0) };
        Point3d[] noModel = cantos.Select(c => c.TransformBy(ps2wcs)).ToArray();
        double mx0 = noModel.Min(c => c.X), mx1 = noModel.Max(c => c.X);
        double my0 = noModel.Min(c => c.Y), my1 = noModel.Max(c => c.Y);
        if ((mx1 - mx0) / intervalo > 400 || (my1 - my0) / intervalo > 400)
            throw new InvalidOperationException("Intervalo da malha pequeno demais para esta viewport.");

        string nome = "C3D_MALHA_" + vp.Handle;
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        BlockTableRecord def;
        if (bt.Has(nome))
        {
            def = (BlockTableRecord)tr.GetObject(bt[nome], OpenMode.ForWrite);
            foreach (ObjectId e in def)
                tr.GetObject(e, OpenMode.ForWrite).Erase();
        }
        else
        {
            bt.UpgradeOpen();
            def = new BlockTableRecord { Name = nome, Origin = Point3d.Origin, Comments = "Malha de coordenadas da viewport (Civil3D2026Plugin, C3DMNE)" };
            bt.Add(def);
            tr.AddNewlyCreatedDBObject(def, true);
        }
        ObjectId estilo = Estilo(tr, db);
        double th = p.MalhaTexto;

        void Add(Entity e)
        {
            e.Layer = "0";
            e.ColorIndex = 0;
            e.LineWeight = LineWeight.ByBlock;
            if (e is Curve)
                e.Transparency = new Autodesk.AutoCAD.Colors.Transparency((byte)Math.Round(255 * (100 - Math.Clamp(p.MalhaTransp, 0, 90)) / 100));
            def.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
        }

        // Caixas (no papel) dos rotulos ja colocados, para um nao cair em cima do outro.
        var ocupadas = new List<Point3d[]>();
        double folga = th * 0.4;

        DBText NovoTexto(string texto)
        {
            var t = new DBText { TextString = texto, Height = th, TextStyleId = estilo, Position = Point3d.Origin };
            Add(t);
            return t;
        }

        double Largura(DBText t)
        {
            try
            {
                Extents3d e = t.GeometricExtents;
                return Math.Max(e.MaxPoint.X, th);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return t.TextString.Length * th * 0.9;
            }
        }

        bool Dentro(Point3d[] caixa) =>
            caixa.All(c => c.X >= x0 + folga && c.X <= x1 - folga && c.Y >= y0 + folga && c.Y <= y1 - folga);

        void Rotulos(string texto, Point3d a, Point3d b)
        {
            // Texto ao longo da linha, sempre legivel (da esquerda para a direita / de baixo para cima).
            Vector3d d = (b - a).GetNormal();
            if (d.X < -1e-9 || (Math.Abs(d.X) <= 1e-9 && d.Y < 0))
            {
                d = d.Negate();
                (a, b) = (b, a);
            }
            Vector3d perp = d.RotateBy(Math.PI / 2, Vector3d.ZAxis);
            double comp = a.DistanceTo(b);
            double desl = th * 0.4;
            DBText t = NovoTexto(texto);
            double largura = Largura(t);

            Point3d[] Caixa(double s)
            {
                // Retangulo do texto com uma folga, comecando a "s" do inicio da linha.
                Point3d p0 = a + d * (s - th * 0.25) + perp * (desl - th * 0.3);
                Vector3d ao = d * (largura + th * 0.5), alto = perp * (th * 1.6);
                return new[] { p0, p0 + ao, p0 + ao + alto, p0 + alto };
            }

            // Desliza o texto para dentro da viewport a partir da ponta ate caber inteiro no
            // retangulo e sem encostar em outro rotulo. Cada ponta procura so na sua metade.
            double? Procurar(bool noInicio)
            {
                double passo = th * 0.5, metade = comp / 2;
                for (double r = th * 0.6; r + largura <= metade; r += passo)
                {
                    double s = noInicio ? r : comp - r - largura;
                    Point3d[] caixa = Caixa(s);
                    if (Dentro(caixa) && !ocupadas.Any(o => Sobrepoe(o, caixa)))
                        return s;
                }
                return null;
            }

            void Colocar(DBText texto1, double s)
            {
                texto1.Rotation = Math.Atan2(d.Y, d.X);
                texto1.Position = a + d * s + perp * desl;
                ocupadas.Add(Caixa(s));
            }

            double? s1 = Procurar(true);
            if (s1 is double v1)
                Colocar(t, v1);

            // Linha curta (canto da viewport): um rotulo so, para nao repetir o mesmo valor colado.
            if (s1 is null || comp >= largura * 4)
            {
                double? s2 = Procurar(false);
                if (s2 is double v2)
                    Colocar(s1 is null ? t : NovoTexto(texto), v2);
                else if (s1 is null)
                    t.Erase();
            }
        }

        void Familia(bool verticais)
        {
            double ini = verticais ? mx0 : my0, fim = verticais ? mx1 : my1;
            for (double v = Math.Ceiling(ini / intervalo) * intervalo; v <= fim + 1e-9; v += intervalo)
            {
                Point3d a = verticais ? new Point3d(v, my0 - intervalo, 0) : new Point3d(mx0 - intervalo, v, 0);
                Point3d b = verticais ? new Point3d(v, my1 + intervalo, 0) : new Point3d(mx1 + intervalo, v, 0);
                if (!Cortar(a.TransformBy(wcs2ps), b.TransformBy(wcs2ps), x0, y0, x1, y1, out Point3d pa, out Point3d pb))
                    continue;
                if (pa.DistanceTo(pb) < th * 4)
                    continue;
                if (p.MalhaCruzes)
                {
                    // So os tracos nas bordas; as cruzes vao nas intersecoes.
                    Vector3d d = (pb - pa).GetNormal();
                    Add(new Line(pa, pa + d * th * 1.5));
                    Add(new Line(pb, pb - d * th * 1.5));
                }
                else
                {
                    Add(new Line(pa, pb));
                }
                string texto = (verticais ? "E=" : "N=") + Math.Round(v / unidadesPorM).ToString("#,0", Br);
                Rotulos(texto, pa, pb);
            }
        }

        Familia(true);
        Familia(false);

        if (p.MalhaCruzes)
        {
            double braco = th * 1.2;
            for (double x = Math.Ceiling(mx0 / intervalo) * intervalo; x <= mx1 + 1e-9; x += intervalo)
                for (double y = Math.Ceiling(my0 / intervalo) * intervalo; y <= my1 + 1e-9; y += intervalo)
                {
                    Point3d c = new Point3d(x, y, 0).TransformBy(wcs2ps);
                    if (c.X < x0 + braco || c.X > x1 - braco || c.Y < y0 + braco || c.Y > y1 - braco)
                        continue;
                    Vector3d ex = new Vector3d(1, 0, 0).TransformBy(wcs2ps).GetNormal() * braco;
                    Vector3d ey = new Vector3d(0, 1, 0).TransformBy(wcs2ps).GetNormal() * braco;
                    Add(new Line(c - ex, c + ex));
                    Add(new Line(c - ey, c + ey));
                }
        }

        var br = new BlockReference(Point3d.Origin, def.ObjectId);
        Layer(tr, db, br, p.MalhaLayer);
        ObjectId id = espaco.AppendEntity(br);
        tr.AddNewlyCreatedDBObject(br, true);
        br.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, GarantirApp(tr, db)),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "malha"),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, vp.Handle.ToString()),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "mm=" + p.MalhaMm.ToString("R", CultureInfo.InvariantCulture)),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "texto=" + th.ToString("R", CultureInfo.InvariantCulture)),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "cruzes=" + (p.MalhaCruzes ? "1" : "0")));
        return id;
    }

    /// <summary>Dois quadrilateros convexos se sobrepoem (eixos separadores).</summary>
    private static bool Sobrepoe(Point3d[] p, Point3d[] q)
    {
        foreach (Point3d[] poli in new[] { p, q })
            for (int i = 0; i < poli.Length; i++)
            {
                Point3d u = poli[i], w = poli[(i + 1) % poli.Length];
                double nx = -(w.Y - u.Y), ny = w.X - u.X;
                double pMin = double.MaxValue, pMax = double.MinValue, qMin = double.MaxValue, qMax = double.MinValue;
                foreach (Point3d c in p)
                {
                    double v = c.X * nx + c.Y * ny;
                    pMin = Math.Min(pMin, v);
                    pMax = Math.Max(pMax, v);
                }
                foreach (Point3d c in q)
                {
                    double v = c.X * nx + c.Y * ny;
                    qMin = Math.Min(qMin, v);
                    qMax = Math.Max(qMax, v);
                }
                if (pMax < qMin || qMax < pMin)
                    return false;
            }
        return true;
    }

    /// <summary>Corta o segmento no retangulo (Liang-Barsky).</summary>
    private static bool Cortar(Point3d a, Point3d b, double x0, double y0, double x1, double y1, out Point3d pa, out Point3d pb)
    {
        pa = a;
        pb = b;
        double dx = b.X - a.X, dy = b.Y - a.Y, t0 = 0, t1 = 1;
        double[] pp = { -dx, dx, -dy, dy };
        double[] qq = { a.X - x0, x1 - a.X, a.Y - y0, y1 - a.Y };
        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(pp[i]) < 1e-12)
            {
                if (qq[i] < 0)
                    return false;
                continue;
            }
            double r = qq[i] / pp[i];
            if (pp[i] < 0)
            {
                if (r > t1)
                    return false;
                t0 = Math.Max(t0, r);
            }
            else
            {
                if (r < t0)
                    return false;
                t1 = Math.Min(t1, r);
            }
        }
        pa = new Point3d(a.X + t0 * dx, a.Y + t0 * dy, 0);
        pb = new Point3d(a.X + t1 * dx, a.Y + t1 * dy, 0);
        return t1 > t0;
    }

    // ------------------------------------------------------------------
    // Padrao: ler de uma viewport ajustada a mao
    // ------------------------------------------------------------------

    /// <summary>
    /// Le o padrao da viewport: os itens ligados a ela (XData C3D_MNE) ou, sem eles, o norte e a
    /// escala grafica do plugin mais perto dela no mesmo layout. Posicao pelo canto mais proximo,
    /// tamanho pela escala da insercao, layer de cada um.
    /// </summary>
    public static OpResult LerPadrao(Document doc, ObjectId viewport, out PadraoConjunto padrao)
    {
        padrao = PadraoConjunto.Ler();
        Database db = doc.Database;
        var lidos = new List<string>();
        using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        if (tr.GetObject(viewport, OpenMode.ForRead) is not Viewport vp)
            return new OpResult(0, 1, "Escolha uma viewport.") { Mudou = false };
        string handle = vp.Handle.ToString();
        double mmPorM = LayoutEscalaGrafica.MmPorMetro(db, vp);
        var espaco = (BlockTableRecord)tr.GetObject(vp.OwnerId, OpenMode.ForRead);

        BlockReference? norte = null, escala = null, malha = null;
        double dNorte = double.MaxValue, dEscala = double.MaxValue;
        foreach (ObjectId id in espaco)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference br)
                continue;
            (string Tipo, string Viewport)? m = Marca(br);
            if (m is { } marca && marca.Viewport == handle)
            {
                if (marca.Tipo == "norte") { norte = br; dNorte = -1; }
                else if (marca.Tipo == "escala") { escala = br; dEscala = -1; }
                else if (marca.Tipo == "malha") malha = br;
                continue;
            }
            // Sem marca (lancado pelos comandos avulsos): o mais perto, dentro ou ate 50 mm da viewport.
            double d = Distancia(vp, br.Position);
            if (d > 50)
                continue;
            string nomeBloco = ((BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead)).Name;
            bool ehEscala;
            using (ResultBuffer? rb = br.GetXDataForApplication(LayoutEscalaGrafica.App))
                ehEscala = rb is not null;
            if (nomeBloco.Equals(LayoutNorte.Bloco, StringComparison.OrdinalIgnoreCase) && dNorte >= 0 && d < dNorte)
            {
                norte = br;
                dNorte = d;
            }
            else if (ehEscala && dEscala >= 0 && d < dEscala)
            {
                escala = br;
                dEscala = d;
            }
        }

        padrao.Norte = norte is not null;
        if (norte is not null)
        {
            padrao.NorteTamanho = Math.Round(Math.Abs(norte.ScaleFactors.X), 2);
            padrao.NortePos = AncoraDe(vp, norte.Position);
            if (norte.Layer != "0")
                padrao.NorteLayer = norte.Layer;
            lidos.Add($"norte {padrao.NorteTamanho:0.#} mm ({PadraoConjunto.NomeCanto(padrao.NortePos.Canto)})");
        }

        padrao.Escala = escala is not null;
        if (escala is not null)
        {
            double s = Math.Abs(escala.ScaleFactors.X);
            (double max, double texto) = DadosEscala(escala);
            if (max > 0)
                padrao.EscalaMm = Math.Round(max * mmPorM * s, 1);
            if (texto > 0)
                padrao.EscalaTexto = Math.Round(texto * s, 2);
            padrao.EscalaPos = AncoraDe(vp, escala.Position);
            if (escala.Layer != "0")
                padrao.EscalaLayer = escala.Layer;
            if (padrao.EscalaM > 0 && max > 0)
                padrao.EscalaM = Math.Round(max * s, 2);
            lidos.Add($"escala grafica ~{padrao.EscalaMm:0.#} mm, texto {padrao.EscalaTexto:0.##} ({PadraoConjunto.NomeCanto(padrao.EscalaPos.Canto)})");
        }

        if (malha is not null)
        {
            padrao.Malha = true;
            if (malha.Layer != "0")
                padrao.MalhaLayer = malha.Layer;
            lidos.Add($"malha na layer {malha.Layer}");
        }
        else
        {
            padrao.Malha = LayoutSettings.GetBool("mne_malha", true);
        }
        tr.Commit();

        if (norte is null && escala is null && malha is null)
            return new OpResult(0, 1, "Nenhum norte, escala grafica ou malha do plugin perto dessa viewport.") { Mudou = false };
        padrao.Salvar();
        return new OpResult(1, 0, "Padrao salvo: " + string.Join("; ", lidos) + ".") { Mudou = false };
    }

    private static (double Max, double Texto) DadosEscala(BlockReference br)
    {
        double max = 0, texto = 0;
        using ResultBuffer? rb = br.GetXDataForApplication(LayoutEscalaGrafica.App);
        if (rb is null)
            return (0, 0);
        foreach (TypedValue v in rb)
        {
            if (v.Value is not string s)
                continue;
            if (s.StartsWith("max=", StringComparison.Ordinal))
                double.TryParse(s.Substring(4), NumberStyles.Float, CultureInfo.InvariantCulture, out max);
            else if (s.StartsWith("texto=", StringComparison.Ordinal))
                double.TryParse(s.Substring(6), NumberStyles.Float, CultureInfo.InvariantCulture, out texto);
        }
        return (max, texto);
    }

    /// <summary>Distancia do ponto ao retangulo da viewport (0 dentro).</summary>
    private static double Distancia(Viewport vp, Point3d p)
    {
        double dx = Math.Max(0, Math.Abs(p.X - vp.CenterPoint.X) - vp.Width / 2);
        double dy = Math.Max(0, Math.Abs(p.Y - vp.CenterPoint.Y) - vp.Height / 2);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ------------------------------------------------------------------
    // Remover / marcas
    // ------------------------------------------------------------------

    public static OpResult Remover(Document doc, IEnumerable<ObjectId> viewports)
    {
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        int n = 0;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in viewports.Distinct())
                if (tr.GetObject(id, OpenMode.ForRead) is Viewport vp)
                    n += Remover(tr, vp);
            tr.Commit();
        }
        return new OpResult(n, 0, n == 0 ? "Nenhum item do conjunto nessas viewports." : $"{n} item(ns) removido(s).");
    }

    /// <summary>Apaga os itens ligados a viewport (malha, norte e escala inseridos pelo conjunto).</summary>
    private static int Remover(Transaction tr, Viewport vp)
    {
        string handle = vp.Handle.ToString();
        var espaco = (BlockTableRecord)tr.GetObject(vp.OwnerId, OpenMode.ForRead);
        int n = 0;
        foreach (ObjectId id in espaco)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference br)
                continue;
            if (Marca(br) is { } marca && marca.Viewport == handle)
            {
                br.UpgradeOpen();
                br.Erase();
                n++;
            }
        }
        return n;
    }

    private static (string Tipo, string Viewport)? Marca(BlockReference br)
    {
        using ResultBuffer? rb = br.GetXDataForApplication(App);
        if (rb is null)
            return null;
        TypedValue[] v = rb.AsArray();
        return v.Length > 2 && v[1].Value is string tipo && v[2].Value is string h ? (tipo, h) : null;
    }

    private static void Marcar(Transaction tr, Database db, ObjectId id, string tipo, string vpHandle)
    {
        string app = GarantirApp(tr, db);
        var e = (Entity)tr.GetObject(id, OpenMode.ForWrite);
        e.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, app),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, tipo),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, vpHandle));
    }

    private static string GarantirApp(Transaction tr, Database db)
    {
        var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (!rat.Has(App))
        {
            rat.UpgradeOpen();
            var r = new RegAppTableRecord { Name = App };
            rat.Add(r);
            tr.AddNewlyCreatedDBObject(r, true);
        }
        return App;
    }

    /// <summary>Layer do item: a do padrao (criada se faltar); vazio = layer atual.</summary>
    internal static void Layer(Transaction tr, Database db, Entity e, string nome)
    {
        if (string.IsNullOrWhiteSpace(nome))
            return;
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!lt.Has(nome))
        {
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = nome };
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }
        // Pelo id: o bloco pode ainda nao estar no desenho.
        e.LayerId = lt[nome];
    }

    private static ObjectId Estilo(Transaction tr, Database db)
    {
        var st = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (st.Has(EstiloTexto))
            return st[EstiloTexto];
        st.UpgradeOpen();
        var r = new TextStyleTableRecord { Name = EstiloTexto, FileName = "romans.shx", XScale = 1 };
        ObjectId id = st.Add(r);
        tr.AddNewlyCreatedDBObject(r, true);
        return id;
    }

    /// <summary>Viewports de modelo de todos os layouts (ou so do layout dado), sem a do papel.</summary>
    public static List<ObjectId> ViewportsDosLayouts(Database db, string? soLayout)
    {
        var lista = new List<ObjectId>();
        using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dic)
        {
            var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (lay.ModelType || (soLayout is not null && !lay.LayoutName.Equals(soLayout, StringComparison.OrdinalIgnoreCase)))
                continue;
            var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
            HashSet<ObjectId> papel = LayoutReader.ViewportsDoPapel(tr, lay, btr);
            foreach (ObjectId id in btr)
                if (!papel.Contains(id) && tr.GetObject(id, OpenMode.ForRead) is Viewport vp && vp.ViewDirection.IsParallelTo(Vector3d.ZAxis))
                    lista.Add(id);
        }
        tr.Commit();
        return lista;
    }
}
