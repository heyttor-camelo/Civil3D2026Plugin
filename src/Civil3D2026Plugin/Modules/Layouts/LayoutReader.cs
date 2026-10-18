using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.Infrastructure;

using WPoint = System.Windows.Point;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Viewport de modelo de um layout, ja no espaco do papel.</summary>
internal sealed class SheetViewport
{
    public ObjectId Id { get; init; }
    public WPoint[] Contorno { get; init; } = Array.Empty<WPoint>();
    public WPoint Centro { get; init; }
    public double Largura { get; init; }
    public double Altura { get; init; }
    public string Escala { get; init; } = "";
    public bool Ligada { get; init; } = true;
    public bool Travada { get; init; }
    public double EscalaCustom { get; init; }

    /// <summary>Vista em planta (direcao de vista paralela a Z).</summary>
    public bool Planta { get; init; } = true;

    /// <summary>Numero da viewport dentro do layout (1 = primeira de modelo).</summary>
    public int Indice { get; init; }

    /// <summary>Layer state aplicado nesta sessao da janela (so para mostrar).</summary>
    public string? Aplicado { get; set; }
}

/// <summary>
/// Texto do papel para a miniatura. Posicao no papel; alinhamento: H 0=esquerda 1=centro 2=direita,
/// V 0=base 1=meio 2=topo 3=baixo. Multilinha = MTEXT (topo do quadro na referencia).
/// </summary>
internal sealed record SheetText(WPoint Pos, double Altura, double RotacaoGraus, string Texto, int H, int V, double LarguraMax, bool Multilinha);

/// <summary>Um layout com o desenho simplificado do papel (para a miniatura) e as viewports.</summary>
internal sealed class SheetData
{
    public string Nome { get; init; } = "";
    public int Ordem { get; init; }
    public ObjectId Id { get; init; }
    public bool Atual { get; init; }
    public string Dispositivo { get; init; } = "";
    public string Papel { get; init; } = "";
    public string EstiloPlot { get; init; } = "";
    public double PapelLargura { get; init; }
    public double PapelAltura { get; init; }
    /// <summary>Folha do page setup no espaco do papel (4 cantos) e a area de impressao (dentro das margens). null = sem page setup legivel.</summary>
    public WPoint[]? Folha { get; set; }
    public WPoint[]? AreaImpressao { get; set; }
    public List<WPoint[]> Linhas { get; } = new();
    public List<SheetText> Textos { get; } = new();
    public List<WPoint[]> Preenchimentos { get; } = new();

    /// <summary>Molduras de imagem raster e OLE (retangulo + diagonal), para a miniatura.</summary>
    public List<WPoint[]> Imagens { get; } = new();

    /// <summary>Fator mm de papel por unidade de desenho (INSUNITS); 1000 para metros.</summary>
    public double FatorUnidade { get; init; } = 1000;

    /// <summary>Cache da geometria desenhada (montado pela vista grafica).</summary>
    public object? Cache { get; set; }
    public List<SheetViewport> Viewports { get; } = new();
    public double MinX { get; set; } = double.MaxValue;
    public double MinY { get; set; } = double.MaxValue;
    public double MaxX { get; set; } = double.MinValue;
    public double MaxY { get; set; } = double.MinValue;

    public double Largura => MaxX - MinX;
    public double Altura => MaxY - MinY;

    /// <summary>Resumo da leitura (entidades por tipo, ignoradas), para diagnostico no painel.</summary>
    public string Diagnostico { get; set; } = "";

    public void Incluir(WPoint p)
    {
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
            return;
        MinX = Math.Min(MinX, p.X);
        MinY = Math.Min(MinY, p.Y);
        MaxX = Math.Max(MaxX, p.X);
        MaxY = Math.Max(MaxY, p.Y);
    }
}

/// <summary>Um layer state do desenho.</summary>
internal sealed record LayerStateItem(string Nome, string Descricao);

/// <summary>
/// Le os layouts (miniatura e viewports) e os layer states do desenho e aplica um
/// layer state como override nas viewports escolhidas.
/// </summary>
internal static class LayoutReader
{
    private const int MaxPontosPorLayout = 150000;
    private const int MaxTextosPorLayout = 6000;
    private const int MaxProfundidadeBloco = 8;

    // ------------------------------------------------------------------
    // Layer states
    // ------------------------------------------------------------------

    /// <summary>
    /// Nomes dos layer states. Ficam no dicionario ACAD_LAYERSTATES do dicionario de
    /// extensao da tabela de layers.
    /// </summary>
    public static List<LayerStateItem> LerLayerStates(Database db)
    {
        var lista = new List<LayerStateItem>();
        LayerStateManager lsm = db.LayerStateManager;

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.ExtensionDictionary.IsNull)
            {
                var ext = (DBDictionary)tr.GetObject(lt.ExtensionDictionary, OpenMode.ForRead);
                if (ext.Contains("ACAD_LAYERSTATES"))
                {
                    var states = (DBDictionary)tr.GetObject(ext.GetAt("ACAD_LAYERSTATES"), OpenMode.ForRead);
                    foreach (DBDictionaryEntry e in states)
                    {
                        if (!lsm.HasLayerState(e.Key))
                            continue;
                        string desc = "";
                        try
                        {
                            desc = lsm.GetLayerStateDescription(e.Key) ?? "";
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                        }
                        lista.Add(new LayerStateItem(e.Key, desc));
                    }
                }
            }
            tr.Commit();
        }

        return lista.OrderBy(l => l.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------
    // Layouts
    // ------------------------------------------------------------------

    public static List<SheetData> LerLayouts(Database db)
    {
        var folhas = new List<SheetData>();
        using Transaction tr = db.TransactionManager.StartTransaction();

        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in dic)
        {
            var layout = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
            if (layout.ModelType)
                continue;

            string atual = LayoutManager.Current.CurrentLayout;
            var folha = new SheetData
            {
                FatorUnidade = FatorUnidade(db),
                Nome = layout.LayoutName,
                Ordem = layout.TabOrder,
                Id = layout.ObjectId,
                Atual = string.Equals(layout.LayoutName, atual, StringComparison.OrdinalIgnoreCase),
                Dispositivo = layout.PlotConfigurationName,
                Papel = layout.CanonicalMediaName,
                EstiloPlot = layout.CurrentStyleSheet,
                PapelLargura = layout.PlotPaperSize.X,
                PapelAltura = layout.PlotPaperSize.Y
            };
            try
            {
                FolhaDoPageSetup(layout, folha);
            }
            catch (System.Exception ex)
            {
                PluginLog.Diag("Layouts", $"Miniatura: folha do page setup de {layout.LayoutName} ilegivel ({ex.Message}).");
            }
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            HashSet<ObjectId> papel = ViewportsDoPapel(tr, layout, btr);
            int pontos = 0, entidades = 0, semGeometria = 0, invisiveis = 0, erros = 0, resumidos = 0;
            var tipos = new Dictionary<string, int>();
            var blocosVazios = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ObjectId id in btr)
            {
                Entity? ent;
                try
                {
                    ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    erros++;
                    continue;
                }
                if (ent is null)
                    continue;
                entidades++;
                if (!ent.Visible)
                {
                    invisiveis++;
                    continue;
                }

                if (ent is Viewport vp)
                {
                    if (papel.Contains(id))
                        continue;
                    try
                    {
                        folha.Viewports.Add(LerViewport(tr, vp, folha, folha.Viewports.Count + 1));
                    }
                    catch (System.Exception)
                    {
                        erros++;
                    }
                    continue;
                }

                string tipo = id.ObjectClass?.DxfName ?? ent.GetType().Name;
                tipos[tipo] = tipos.GetValueOrDefault(tipo) + 1;
                if (pontos >= MaxPontosPorLayout)
                {
                    // Limite de pontos: o resto aparece como o retangulo das extensoes (nada some).
                    if (Caixa(ent, folha))
                        resumidos++;
                    continue;
                }
                int n;
                try
                {
                    n = Desenhar(tr, ent, Matrix3d.Identity, folha, 0);
                }
                catch (System.Exception)
                {
                    erros++;
                    n = Caixa(ent, folha) ? 5 : 0;
                }
                if (n == 0)
                {
                    semGeometria++;
                    if (ent is BlockReference br0)
                        blocosVazios.Add(NomeDoBloco(tr, br0));
                }
                pontos += n;
            }
            if (blocosVazios.Count > 0)
                PluginLog.Diag("Layouts", $"Miniatura de {layout.LayoutName}: bloco(s) sem geometria desenhavel: {string.Join(", ", blocosVazios)}.");

            folha.Diagnostico = $"{entidades} entidade(s): {string.Join(", ", tipos.OrderByDescending(t => t.Value).Take(8).Select(t => $"{t.Key} {t.Value}"))}"
                + (semGeometria > 0 ? $"; {semGeometria} sem geometria na miniatura" : "")
                + (invisiveis > 0 ? $"; {invisiveis} invisivel(is)" : "")
                + (erros > 0 ? $"; {erros} erro(s) de leitura" : "")
                + (blocosVazios.Count > 0 ? $"; blocos sem geometria: {string.Join(", ", blocosVazios.Take(5))}" : "")
                + (resumidos > 0 ? $"; limite de pontos atingido ({resumidos} objeto(s) como retangulo)" : "")
                + $"; papel {layout.PlotPaperSize.X:0}x{layout.PlotPaperSize.Y:0}";

            if (folha.MinX > folha.MaxX)
            {
                // Sem desenho no papel: usa so as viewports.
                foreach (SheetViewport v in folha.Viewports)
                    foreach (WPoint p in v.Contorno)
                        folha.Incluir(p);
                if (folha.MinX > folha.MaxX)
                {
                    // Layout vazio: mostra o papel.
                    folha.Incluir(new WPoint(0, 0));
                    folha.Incluir(new WPoint(Math.Max(folha.PapelLargura, 1), Math.Max(folha.PapelAltura, 1)));
                }
            }

            folhas.Add(folha);
        }

        tr.Commit();
        return folhas.OrderBy(f => f.Ordem).ToList();
    }

    /// <summary>
    /// Viewport do proprio papel (nao recebe override). E a primeira da lista do layout;
    /// em layout ainda nao inicializado a lista vem vazia e vale a primeira do bloco.
    /// </summary>
    private static HashSet<ObjectId> ViewportsDoPapel(Transaction tr, Autodesk.AutoCAD.DatabaseServices.Layout layout, BlockTableRecord btr)
    {
        var set = new HashSet<ObjectId>();
        ObjectIdCollection ids = layout.GetViewports();
        if (ids.Count > 0)
        {
            set.Add(ids[0]);
            return set;
        }

        // Layout nunca ativado: a viewport do papel ainda nao existe. So considera "papel" uma
        // viewport do tamanho do papel (ou maior); as demais sao viewports de verdade.
        double pw = layout.PlotPaperSize.X, ph = layout.PlotPaperSize.Y;
        foreach (ObjectId id in btr)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is Viewport vp)
            {
                bool tamanhoDoPapel = pw > 0 && ph > 0 &&
                    ((vp.Width >= pw * 0.9 && vp.Height >= ph * 0.9) || (vp.Width >= ph * 0.9 && vp.Height >= pw * 0.9));
                if (tamanhoDoPapel || vp.Number == 1)
                    set.Add(id);
                break;
            }
        }
        return set;
    }

    /// <summary>mm de papel por unidade de desenho, pelo INSUNITS (sem unidade: metros).</summary>
    public static double FatorUnidade(Database db) => db.Insunits switch
    {
        UnitsValue.Millimeters => 1.0,
        UnitsValue.Centimeters => 10.0,
        UnitsValue.Decimeters => 100.0,
        UnitsValue.Meters => 1000.0,
        UnitsValue.Kilometers => 1_000_000.0,
        UnitsValue.Inches => 25.4,
        UnitsValue.Feet => 304.8,
        UnitsValue.Yards => 914.4,
        _ => 1000.0
    };

    private static SheetViewport LerViewport(Transaction tr, Viewport vp, SheetData folha, int indice)
    {
        WPoint[] contorno;
        if (vp.NonRectClipOn && !vp.NonRectClipEntityId.IsNull &&
            tr.GetObject(vp.NonRectClipEntityId, OpenMode.ForRead) is Curve clip)
        {
            contorno = Amostrar(clip, Matrix3d.Identity).Where(q => double.IsFinite(q.X) && double.IsFinite(q.Y)).ToArray();
        }
        else
        {
            contorno = Array.Empty<WPoint>();
        }
        if (contorno.Length < 3)
        {
            Point3d c = vp.CenterPoint;
            double w = vp.Width / 2.0, h = vp.Height / 2.0;
            contorno = new[]
            {
                new WPoint(c.X - w, c.Y - h), new WPoint(c.X + w, c.Y - h),
                new WPoint(c.X + w, c.Y + h), new WPoint(c.X - w, c.Y + h), new WPoint(c.X - w, c.Y - h)
            };
        }

        foreach (WPoint p in contorno)
            folha.Incluir(p);

        string escala = "";
        try
        {
            escala = vp.AnnotationScale?.Name ?? "";
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
        }

        return new SheetViewport
        {
            Id = vp.ObjectId,
            Contorno = contorno,
            Centro = new WPoint(vp.CenterPoint.X, vp.CenterPoint.Y),
            Largura = vp.Width,
            Altura = vp.Height,
            Escala = escala,
            Ligada = vp.On,
            Travada = vp.Locked,
            EscalaCustom = vp.CustomScale,
            Planta = vp.ViewDirection.IsParallelTo(Vector3d.ZAxis),
            Indice = indice
        };
    }

    /// <summary>
    /// Linhas, arcos, polilinhas, textos, atributos, cotas, tabelas e hachuras do papel,
    /// entrando em blocos (carimbo, margem). Retorna quantos pontos foram gerados.
    /// </summary>
    private static int Desenhar(Transaction? tr, Entity ent, Matrix3d xf, SheetData folha, int nivel)
    {
        try
        {
            switch (ent)
            {
                case Curve c when ent is not Xline and not Ray:
                {
                    WPoint[] pts = Amostrar(c, xf).ToArray();
                    if (pts.Length < 2)
                        return 0;
                    folha.Linhas.Add(pts);
                    foreach (WPoint p in pts)
                        folha.Incluir(p);
                    return pts.Length;
                }

                case AttributeReference att:
                    if (!att.Invisible)
                        Texto(att, xf, folha);
                    return 1;

                case DBText t when t is not AttributeDefinition:
                    Texto(t, xf, folha);
                    return 1;

                case MText m:
                    TextoM(m, xf, folha);
                    return 1;

                case Hatch h:
                    return Hachura(h, xf, folha);

                case Solid so:
                {
                    // Solid 2D: vertices 0,1,3,2 formam o contorno.
                    var pts = new[] { so.GetPointAt(0), so.GetPointAt(1), so.GetPointAt(3), so.GetPointAt(2) }
                        .Select(p => p.TransformBy(xf)).Select(p => new WPoint(p.X, p.Y)).ToArray();
                    folha.Preenchimentos.Add(pts);
                    return 4;
                }

                case Dimension d when nivel < MaxProfundidadeBloco && !d.DimBlockId.IsNull && tr is not null:
                    return Bloco(tr, d.DimBlockId, xf, folha, nivel);

                case Wipeout:
                    return 1; // mascara: nao desenha (e nao conta como "sem geometria")

                case AttributeDefinition or DBPoint:
                    return 0;

                case RasterImage img:
                {
                    Point3dCollection v = img.GetVertices();
                    if (v.Count < 4)
                        return 0;
                    var pts = new[] { v[0], v[1], v[2], v[3] }.Select(p => p.TransformBy(xf)).Select(p => new WPoint(p.X, p.Y)).ToArray();
                    folha.Imagens.Add(pts);
                    foreach (WPoint p in pts)
                        folha.Incluir(p);
                    return 4;
                }

                case Ole2Frame ole:
                {
                    Rectangle3d r = ole.Position3d;
                    var pts = new[] { r.LowerLeft, r.LowerRight, r.UpperRight, r.UpperLeft }.Select(p => p.TransformBy(xf)).Select(p => new WPoint(p.X, p.Y)).ToArray();
                    folha.Imagens.Add(pts);
                    foreach (WPoint p in pts)
                        folha.Incluir(p);
                    return 4;
                }

                case MLeader or Leader when nivel < MaxProfundidadeBloco:
                    return Explodido(tr, ent, xf, folha, nivel);

                case BlockReference br when nivel < MaxProfundidadeBloco:
                {
                    // Tabela tambem e BlockReference: o bloco anonimo tem as linhas e os textos.
                    // Bloco dinamico: BlockTableRecord e a representacao anonima (*U) com o estado atual.
                    int total = 0;
                    if (tr is not null)
                        total = Bloco(tr, br.BlockTableRecord, xf * br.BlockTransform, folha, nivel);
                    // Nada pela definicao (representacao do bloco dinamico ainda nao gerada, por exemplo):
                    // explode (respeita o estado de visibilidade); por ultimo, a definicao do bloco dinamico.
                    if (total == 0)
                        total = Explodido(tr, br, xf, folha, nivel);
                    if (total == 0 && tr is not null && br.IsDynamicBlock && br.DynamicBlockTableRecord != br.BlockTableRecord)
                        total = Bloco(tr, br.DynamicBlockTableRecord, xf * br.BlockTransform, folha, nivel);
                    if (tr is not null)
                        foreach (ObjectId aid in br.AttributeCollection)
                            if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference a && !a.Invisible)
                                Texto(a, xf, folha);
                    return total;
                }

                case Autodesk.AutoCAD.DatabaseServices.Region or Face or PolyFaceMesh or PolygonMesh or Solid3d or Body when nivel < MaxProfundidadeBloco:
                    return Explodido(tr, ent, xf, folha, nivel);

                default:
                    // Tipo sem desenho proprio (proxy, objeto do Civil no papel...): tenta explodir.
                    return nivel < MaxProfundidadeBloco ? Explodido(tr, ent, xf, folha, nivel) : 0;
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return 0;
        }
    }

    /// <summary>Multileader, proxies e blocos sem definicao legivel: desenha as partes explodidas (copias temporarias).</summary>
    private static int Explodido(Transaction? tr, Entity ent, Matrix3d xf, SheetData folha, int nivel)
    {
        int total = 0;
        var partes = new DBObjectCollection();
        try
        {
            ent.Explode(partes);
        }
        catch (System.Exception)
        {
            foreach (DBObject o in partes)
                o.Dispose();
            return 0;
        }
        foreach (DBObject o in partes)
        {
            try
            {
                // Bloco explodido: as partes ja estao no espaco do pai (xf continua o mesmo).
                if (o is Entity e && e.Visible && e is not AttributeDefinition && (tr is not null || e is not (BlockReference or Dimension)))
                    total += Desenhar(tr, e, xf, folha, nivel + 1);
            }
            catch (System.Exception)
            {
                // Parte ilegivel: segue com as outras.
            }
            finally
            {
                o.Dispose();
            }
        }
        return total;
    }

    private static string NomeDoBloco(Transaction tr, BlockReference br)
    {
        try
        {
            ObjectId id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            return ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
        }
        catch (System.Exception)
        {
            return "?";
        }
    }

    /// <summary>Retangulo das extensoes do objeto (quando o desenho dele nao sai ou passou do limite).</summary>
    private static bool Caixa(Entity ent, SheetData folha)
    {
        try
        {
            Extents3d e = ent.GeometricExtents;
            if (!double.IsFinite(e.MinPoint.X) || e.MaxPoint.X - e.MinPoint.X > 1e7)
                return false;
            var pts = new[]
            {
                new WPoint(e.MinPoint.X, e.MinPoint.Y), new WPoint(e.MaxPoint.X, e.MinPoint.Y),
                new WPoint(e.MaxPoint.X, e.MaxPoint.Y), new WPoint(e.MinPoint.X, e.MaxPoint.Y), new WPoint(e.MinPoint.X, e.MinPoint.Y)
            };
            folha.Linhas.Add(pts);
            foreach (WPoint p in pts)
                folha.Incluir(p);
            return true;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Folha do page setup no espaco do papel: tamanho do papel (mm, girado pelo PlotRotation),
    /// margens nao imprimiveis do dispositivo e a origem da plotagem (o (0,0) do layout cai no
    /// canto da area de impressao mais o PlotOrigin), na escala da plotagem.
    /// </summary>
    private static void FolhaDoPageSetup(Autodesk.AutoCAD.DatabaseServices.Layout layout, SheetData folha)
    {
        Point2d tam = layout.PlotPaperSize;
        if (tam.X <= 0 || tam.Y <= 0)
            return;
        Extents2d mg = layout.PlotPaperMargins;
        double w = tam.X, h = tam.Y;
        double l = mg.MinPoint.X, b = mg.MinPoint.Y, r = mg.MaxPoint.X, t = mg.MaxPoint.Y;
        switch (layout.PlotRotation)
        {
            case PlotRotation.Degrees090:
                (w, h) = (h, w);
                (l, b, r, t) = (t, l, b, r);
                break;
            case PlotRotation.Degrees180:
                (l, b, r, t) = (r, t, l, b);
                break;
            case PlotRotation.Degrees270:
                (w, h) = (h, w);
                (l, b, r, t) = (b, r, t, l);
                break;
        }
        // mm de papel por unidade do layout (layout em mm plotado 1:1 = 1).
        double escala = 1;
        try
        {
            if (layout.UseStandardScale)
                escala = layout.StdScale;
            else if (layout.CustomPrintScale.Denominator > 0)
                escala = layout.CustomPrintScale.Numerator / layout.CustomPrintScale.Denominator;
        }
        catch (System.Exception)
        {
            escala = 1;
        }
        if (!(escala > 0) || !double.IsFinite(escala))
            escala = 1;
        double mmPorUnidade = layout.PlotPaperUnits == PlotPaperUnit.Inches ? escala * 25.4 : escala;
        if (layout.PlotPaperUnits == PlotPaperUnit.Pixels)
            return;
        double k = 1 / mmPorUnidade;
        Point2d o = layout.PlotOrigin;
        double x0 = (-l - o.X) * k, y0 = (-b - o.Y) * k;
        WPoint[] Ret(double xa, double ya, double xb, double yb) => new[] { new WPoint(xa, ya), new WPoint(xb, ya), new WPoint(xb, yb), new WPoint(xa, yb) };
        folha.Folha = Ret(x0, y0, x0 + w * k, y0 + h * k);
        if (w - l - r > 1 && h - b - t > 1)
            folha.AreaImpressao = Ret(x0 + l * k, y0 + b * k, x0 + (w - r) * k, y0 + (h - t) * k);
        foreach (WPoint p in folha.Folha)
            folha.Incluir(p);
    }

    private static int Bloco(Transaction tr, ObjectId defId, Matrix3d xf, SheetData folha, int nivel)
    {
        int total = 0;
        if (defId.IsNull || tr.GetObject(defId, OpenMode.ForRead) is not BlockTableRecord def)
            return 0;
        foreach (ObjectId id in def)
        {
            if (total > MaxPontosPorLayout)
                break;
            try
            {
                if (tr.GetObject(id, OpenMode.ForRead) is Entity filho && filho.Visible && filho is not AttributeDefinition)
                    total += Desenhar(tr, filho, xf, folha, nivel + 1);
            }
            catch (System.Exception)
            {
                // Um objeto ilegivel nao apaga o resto do bloco.
            }
        }
        return total;
    }

    private static void Texto(DBText t, Matrix3d xf, SheetData folha)
    {
        if (folha.Textos.Count >= MaxTextosPorLayout || string.IsNullOrWhiteSpace(t.TextString))
            return;

        int h = 0, v = 0;
        Point3d pos = t.Position;
        switch (t.HorizontalMode)
        {
            case TextHorizontalMode.TextCenter: h = 1; pos = t.AlignmentPoint; break;
            case TextHorizontalMode.TextRight: h = 2; pos = t.AlignmentPoint; break;
            case TextHorizontalMode.TextMid: h = 1; v = 1; pos = t.AlignmentPoint; break;
        }
        if (t.HorizontalMode is TextHorizontalMode.TextLeft or TextHorizontalMode.TextCenter or TextHorizontalMode.TextRight)
        {
            switch (t.VerticalMode)
            {
                case TextVerticalMode.TextVerticalMid: v = 1; pos = t.AlignmentPoint; break;
                case TextVerticalMode.TextTop: v = 2; pos = t.AlignmentPoint; break;
                case TextVerticalMode.TextBottom: v = 3; pos = t.AlignmentPoint; break;
            }
        }

        Adicionar(folha, pos, t.Height, t.Rotation, t.TextString, h, v, 0, false, xf);
    }

    private static void TextoM(MText m, Matrix3d xf, SheetData folha)
    {
        if (folha.Textos.Count >= MaxTextosPorLayout)
            return;
        string texto = m.Text;
        if (string.IsNullOrWhiteSpace(texto))
            return;

        // AttachmentPoint: 1..9 = TopLeft..BottomRight.
        int ap = (int)m.Attachment;
        int h = (ap - 1) % 3;
        int v = ap <= 3 ? 2 : ap <= 6 ? 1 : 3;
        Adicionar(folha, m.Location, m.TextHeight, m.Rotation, texto, h, v, m.Width, true, xf);
    }

    private static void Adicionar(SheetData folha, Point3d pos, double altura, double rotacao, string texto,
        int h, int v, double largura, bool multilinha, Matrix3d xf)
    {
        // Escala e giro do bloco que contem o texto.
        Vector3d eixoX = Vector3d.XAxis.TransformBy(xf);
        double escala = Math.Max(1e-9, eixoX.Length);
        double giro = Math.Atan2(eixoX.Y, eixoX.X);
        Point3d p = pos.TransformBy(xf);
        double alt = altura * escala;
        if (alt <= 1e-9)
            return;

        folha.Textos.Add(new SheetText(new WPoint(p.X, p.Y), alt, (rotacao + giro) * 180.0 / Math.PI,
            texto.Replace("\r", "").Trim(), h, v, largura * escala, multilinha));
        folha.Incluir(new WPoint(p.X, p.Y));
    }

    /// <summary>Hachura solida vira preenchimento; padrao vira so o contorno.</summary>
    private static int Hachura(Hatch h, Matrix3d xf, SheetData folha)
    {
        bool solida = h.PatternType == HatchPatternType.PreDefined &&
                      string.Equals(h.PatternName, "SOLID", StringComparison.OrdinalIgnoreCase);
        int total = 0;
        for (int i = 0; i < h.NumberOfLoops; i++)
        {
            var pts = new List<Point3d>();
            HatchLoop loop = h.GetLoopAt(i);
            if (loop.IsPolyline)
            {
                BulgeVertexCollection bv = loop.Polyline;
                for (int k = 0; k < bv.Count; k++)
                {
                    Point2d a = bv[k].Vertex;
                    Point2d b = bv[(k + 1) % bv.Count].Vertex;
                    double bulge = bv[k].Bulge;
                    int div = Math.Abs(bulge) > 1e-9 ? 8 : 1;
                    for (int j = 0; j < div; j++)
                        pts.Add(PontoArco(a, b, bulge, (double)j / div));
                }
            }
            else
            {
                foreach (Curve2d c in loop.Curves)
                    foreach (Point2d pc in c.GetSamplePoints(c is LineSegment2d ? 2 : 12))
                        pts.Add(new Point3d(pc.X, pc.Y, 0));
            }

            if (pts.Count < 3)
                continue;

            // Contorno da hachura esta no sistema do plano (OCS): leva para o papel.
            Matrix3d ocs = Matrix3d.Displacement(h.Normal * h.Elevation) * Matrix3d.PlaneToWorld(h.Normal);
            WPoint[] wp = pts.Select(p => p.TransformBy(xf * ocs)).Select(p => new WPoint(p.X, p.Y)).ToArray();
            if (solida)
                folha.Preenchimentos.Add(wp);
            else
                folha.Linhas.Add(wp.Append(wp[0]).ToArray());
            foreach (WPoint p in wp)
                folha.Incluir(p);
            total += wp.Length;
        }
        return total;
    }

    private static Point3d PontoArco(Point2d a, Point2d b, double bulge, double t)
    {
        if (Math.Abs(bulge) < 1e-9)
            return new Point3d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, 0);

        double theta = 4.0 * Math.Atan(bulge);
        double corda = a.GetDistanceTo(b);
        double r = corda / (2.0 * Math.Sin(theta / 2.0));
        double mx = (a.X + b.X) / 2.0, my = (a.Y + b.Y) / 2.0;
        double dx = (b.X - a.X) / corda, dy = (b.Y - a.Y) / corda;
        double d = r * Math.Cos(theta / 2.0);
        // Centro a esquerda da corda para bulge positivo (anti-horario).
        double cx = mx - dy * d, cy = my + dx * d;
        double a0 = Math.Atan2(a.Y - cy, a.X - cx);
        double ang = a0 + theta * t;
        double raio = Math.Abs(r);
        return new Point3d(cx + raio * Math.Cos(ang), cy + raio * Math.Sin(ang), 0);
    }

    private static IEnumerable<WPoint> Amostrar(Curve c, Matrix3d xf)
    {
        var pts = new List<Point3d>();
        try
        {
            switch (c)
            {
                case Line l:
                    pts.Add(l.StartPoint);
                    pts.Add(l.EndPoint);
                    break;
                case Polyline pl:
                {
                    int nSeg = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
                    for (int i = 0; i < nSeg; i++)
                    {
                        int div = Math.Abs(pl.GetBulgeAt(i)) > 1e-9 ? 8 : 1;
                        for (int k = 0; k < div; k++)
                            pts.Add(pl.GetPointAtParameter(i + (double)k / div));
                    }
                    pts.Add(pl.GetPointAtParameter(nSeg));
                    break;
                }
                default:
                {
                    const int n = 24;
                    double a = c.StartParam, b = c.EndParam;
                    for (int k = 0; k <= n; k++)
                        pts.Add(c.GetPointAtParameter(a + (b - a) * k / n));
                    break;
                }
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return Array.Empty<WPoint>();
        }

        return pts.Select(p => p.TransformBy(xf)).Select(p => new WPoint(p.X, p.Y));
    }
}
