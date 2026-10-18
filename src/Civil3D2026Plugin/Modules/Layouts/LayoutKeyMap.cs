using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Configuracao do mapa-chave, guardada no desenho (NOD, Xrecord C3D_MAPACHAVE).</summary>
internal sealed class KeyMapConfig
{
    public List<ObjectId> Base { get; } = new();
    public Point2d? CaixaMin { get; set; }
    public Point2d? CaixaMax { get; set; }

    /// <summary>0 = numero da prancha, 1 = nome do layout, 2 = os dois.</summary>
    public int Rotulo { get; set; }

    public bool Automatico { get; set; } = true;
    public short CorDestaque { get; set; } = 1;
    public int Transparencia { get; set; } = 50;

    /// <summary>Padrao da hachura da folha atual: SOLID, ANSI31, ANSI37, DOTS...</summary>
    public string Padrao { get; set; } = "SOLID";
    public double EscalaHachura { get; set; } = 1.0;
    public double AnguloHachura { get; set; }       // graus, somado ao giro da folha

    /// <summary>Cor de fundo da hachura de padrao (-1 = sem fundo).</summary>
    public short CorFundo { get; set; } = -1;

    /// <summary>Multiplica a altura do texto calculada como na LISP (1 = igual).</summary>
    public double FatorTexto { get; set; } = 1.0;

    /// <summary>Numero/total dos textos marcados do carimbo (senao ordem/quantidade de layouts).</summary>
    public bool UsarCarimbo { get; set; } = true;

    /// <summary>Total informado a mao (0 = quantidade de layouts com folha).</summary>
    public int TotalManual { get; set; }
    public bool Titulo { get; set; } = true;

    public bool TemCaixa => CaixaMin.HasValue && CaixaMax.HasValue;
}

/// <summary>Resumo de um layout para a lista do painel.</summary>
internal sealed record KeyMapSheet(string Layout, int Ordem, int Regioes, bool TemMapa);

/// <summary>
/// Mapa-chave automatico. A base (objetos escolhidos no Model) vira o bloco C3D_MAPACHAVE.
/// Em cada layout com viewport sobre a base, o plugin insere o bloco na caixa escolhida,
/// desenha o contorno da area de cada folha com o numero/nome e hachura a area da propria
/// folha. Tudo o que o plugin cria leva XData C3D_MAPACHAVE e e refeito a cada atualizacao.
/// </summary>
internal static class LayoutKeyMap
{
    public const string App = "C3D_MAPACHAVE";
    public const string Bloco = "C3D_MAPACHAVE";
    public const string LayerBase = "MAPA_CHAVE_BASE";
    public const string LayerFolhas = "MAPA_CHAVE_FOLHAS";
    public const string LayerTexto = "MAPA_CHAVE_TEXTO";
    public const string LayerDestaque = "MAPA_CHAVE_DESTAQUE";

    /// <summary>Disparado depois de gerar, remover ou mudar a configuracao (o painel se atualiza).</summary>
    public static event EventHandler? Changed;

    internal static void AvisarMudanca() => Changed?.Invoke(null, EventArgs.Empty);

    // ------------------------------------------------------------------
    // Configuracao
    // ------------------------------------------------------------------

    public static KeyMapConfig Ler(Database db)
    {
        var cfg = new KeyMapConfig();
        using Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (nod.Contains(App) && tr.GetObject(nod.GetAt(App), OpenMode.ForRead) is Xrecord xr && xr.Data is ResultBuffer rb)
        {
            foreach (TypedValue tv in rb)
            {
                switch (tv.TypeCode)
                {
                    case 70: cfg.Rotulo = Convert.ToInt32(tv.Value); break;
                    case 71: cfg.Automatico = Convert.ToInt32(tv.Value) != 0; break;
                    case 62: cfg.CorDestaque = Convert.ToInt16(tv.Value); break;
                    case 72: cfg.Transparencia = Convert.ToInt32(tv.Value); break;
                    case 2: cfg.Padrao = Convert.ToString(tv.Value) ?? "SOLID"; break;
                    case 40: cfg.EscalaHachura = Convert.ToDouble(tv.Value); break;
                    case 50: cfg.AnguloHachura = Convert.ToDouble(tv.Value); break;
                    case 63: cfg.CorFundo = Convert.ToInt16(tv.Value); break;
                    case 41: cfg.FatorTexto = Convert.ToDouble(tv.Value); break;
                    case 73: cfg.UsarCarimbo = Convert.ToInt32(tv.Value) != 0; break;
                    case 74: cfg.TotalManual = Convert.ToInt32(tv.Value); break;
                    case 75: cfg.Titulo = Convert.ToInt32(tv.Value) != 0; break;
                    case 10: { var p = (Point3d)tv.Value; cfg.CaixaMin = new Point2d(p.X, p.Y); break; }
                    case 11: { var p = (Point3d)tv.Value; cfg.CaixaMax = new Point2d(p.X, p.Y); break; }
                    case 330: cfg.Base.Add((ObjectId)tv.Value); break;
                }
            }
        }
        tr.Commit();
        return cfg;
    }

    public static bool Existe(Database db)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        bool ok = nod.Contains(App);
        tr.Commit();
        return ok;
    }

    public static void Gravar(Document doc, KeyMapConfig cfg)
    {
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
        Xrecord xr;
        if (nod.Contains(App))
        {
            xr = (Xrecord)tr.GetObject(nod.GetAt(App), OpenMode.ForWrite);
        }
        else
        {
            xr = new Xrecord();
            nod.SetAt(App, xr);
            tr.AddNewlyCreatedDBObject(xr, true);
        }

        var dados = new List<TypedValue>
        {
            new(1, "v1"),
            new(70, (short)cfg.Rotulo),
            new(71, (short)(cfg.Automatico ? 1 : 0)),
            new(62, cfg.CorDestaque),
            new(72, (short)cfg.Transparencia),
            new(2, cfg.Padrao),
            new(40, cfg.EscalaHachura),
            new(50, cfg.AnguloHachura),
            new(63, cfg.CorFundo),
            new(41, cfg.FatorTexto),
            new(73, (short)(cfg.UsarCarimbo ? 1 : 0)),
            new(74, (short)cfg.TotalManual),
            new(75, (short)(cfg.Titulo ? 1 : 0))
        };
        if (cfg.CaixaMin is Point2d a && cfg.CaixaMax is Point2d b)
        {
            dados.Add(new TypedValue(10, new Point3d(a.X, a.Y, 0)));
            dados.Add(new TypedValue(11, new Point3d(b.X, b.Y, 0)));
        }
        foreach (ObjectId id in cfg.Base.Where(i => !i.IsNull && !i.IsErased))
            dados.Add(new TypedValue(330, id));
        xr.Data = new ResultBuffer(dados.ToArray());
        tr.Commit();

        KeyMapSync.Atualizar(db, cfg.Automatico);
    }

    // ------------------------------------------------------------------
    // Gerar / remover
    // ------------------------------------------------------------------

    /// <summary>
    /// Refaz o mapa-chave em todos os layouts que tem folha sobre a base.
    /// reconstruirBase = refaz o bloco a partir dos objetos do Model.
    /// </summary>
    public static OpResult Gerar(Document doc, bool reconstruirBase)
    {
        Database db = doc.Database;
        KeyMapConfig cfg = Ler(db);
        if (cfg.Base.Count(i => !i.IsNull && !i.IsErased) == 0)
            return new OpResult(0, 0, "Escolha primeiro os objetos da base no Model (Escolher base).");
        if (!cfg.TemCaixa)
            return new OpResult(0, 0, "Escolha primeiro a area do mapa no layout (Escolher area).");

        using DocumentLock _ = doc.LockDocument();
        int layouts = 0, regioesTotal = 0;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            GarantirApp(tr, db);
            foreach (string l in new[] { LayerBase, LayerFolhas, LayerTexto, LayerDestaque })
                GarantirLayer(tr, db, l, l == LayerDestaque ? cfg.CorDestaque : l == LayerTexto ? (short)7 : (short)8);

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            ObjectId blocoId = bt.Has(Bloco) ? bt[Bloco] : ObjectId.Null;
            if (blocoId.IsNull || reconstruirBase)
                blocoId = MontarBloco(tr, db, cfg);

            Extents3d? ext = Extensao(tr, blocoId);
            if (ext is null)
                return new OpResult(0, 0, "A base do mapa-chave ficou vazia (os objetos escolhidos nao puderam ser copiados).");

            // Regioes (area do Model mostrada) de cada folha, na ordem das abas.
            List<(string Nome, int Ordem, ObjectId Btr, List<Point2d[]> Regioes)> folhas = Folhas(tr, db, ext.Value);
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);

            Point2d c0 = cfg.CaixaMin!.Value, c1 = cfg.CaixaMax!.Value;
            double bw = Math.Abs(c1.X - c0.X), bh = Math.Abs(c1.Y - c0.Y);
            double ew = Math.Max(1e-9, ext.Value.MaxPoint.X - ext.Value.MinPoint.X);
            double eh = Math.Max(1e-9, ext.Value.MaxPoint.Y - ext.Value.MinPoint.Y);
            double s = Math.Min(bw / ew, bh / eh) * 0.96;
            var centroCaixa = new Point2d((c0.X + c1.X) / 2, (c0.Y + c1.Y) / 2);
            var centroBase = new Point2d((ext.Value.MinPoint.X + ext.Value.MaxPoint.X) / 2, (ext.Value.MinPoint.Y + ext.Value.MaxPoint.Y) / 2);
            Point2d Papel(Point2d p) => new(centroCaixa.X + (p.X - centroBase.X) * s, centroCaixa.Y + (p.Y - centroBase.Y) * s);

            foreach (var folha in folhas)
            {
                var btr = (BlockTableRecord)tr.GetObject(folha.Btr, OpenMode.ForWrite);
                Limpar(tr, btr);
                if (folha.Regioes.Count == 0)
                    continue;

                // Base.
                var br = new BlockReference(new Point3d(centroCaixa.X - centroBase.X * s, centroCaixa.Y - centroBase.Y * s, 0), blocoId)
                {
                    ScaleFactors = new Scale3d(s),
                    Layer = LayerBase
                };
                Adicionar(tr, btr, br);

                // Contorno e rotulo de todas as folhas (rotulo como na LISP: MTEXT acima da aresta
                // superior da viewport, centralizado e girado com a folha).
                int totalFolhas = cfg.TotalManual > 0 ? cfg.TotalManual : folhas.Count(f => f.Regioes.Count > 0);
                foreach (var outra in folhas.Where(f => f.Regioes.Count > 0))
                {
                    foreach (Point2d[] reg in outra.Regioes)
                    {
                        var pl = new Polyline { Layer = LayerFolhas, Closed = true };
                        for (int i = 0; i < reg.Length; i++)
                            pl.AddVertexAt(i, Papel(reg[i]), 0, 0, 0);
                        Adicionar(tr, btr, pl);
                    }

                    Point2d[] maior = outra.Regioes.OrderByDescending(Area).First().Select(Papel).ToArray();
                    (Point2d pos, double ang, double alt, double larg) = GeometriaRotulo(maior, cfg.Rotulo == 2);
                    alt *= Math.Max(0.1, cfg.FatorTexto);
                    string texto = RotuloFolha(tr, dic, cfg, outra.Nome, outra.Ordem, totalFolhas);
                    var mt = new MText
                    {
                        Contents = texto,
                        TextHeight = alt,
                        Layer = LayerTexto,
                        Attachment = AttachmentPoint.BottomCenter,
                        Location = new Point3d(pos.X, pos.Y, 0),
                        Rotation = ang,
                        Width = Math.Max(larg, alt * 4)
                    };
                    Adicionar(tr, btr, mt);
                }

                // Hachura da propria folha.
                var h = new Hatch { Layer = LayerDestaque };
                Adicionar(tr, btr, h);
                bool solida = string.Equals(cfg.Padrao, "SOLID", StringComparison.OrdinalIgnoreCase);
                h.SetHatchPattern(HatchPatternType.PreDefined, solida ? "SOLID" : cfg.Padrao.ToUpperInvariant());
                h.Associative = false;
                h.Color = AcColor.FromColorIndex(AcColorMethod.ByAci, cfg.CorDestaque);
                if (!solida)
                {
                    Point2d[] ref0 = folha.Regioes[0].Select(Papel).ToArray();
                    h.PatternScale = Math.Max(0.001, cfg.EscalaHachura * Math.Max(ref0[0].GetDistanceTo(ref0[1]) / 25.0, 0.05) * 0.25);
                    h.PatternAngle = AnguloMaior(ref0) + cfg.AnguloHachura * Math.PI / 180.0;
                    if (cfg.CorFundo >= 0)
                        h.BackgroundColor = AcColor.FromColorIndex(AcColorMethod.ByAci, cfg.CorFundo);
                }
                h.Transparency = new Autodesk.AutoCAD.Colors.Transparency((byte)Math.Round(255 * (1 - Math.Clamp(cfg.Transparencia, 0, 90) / 100.0)));
                foreach (Point2d[] reg in folha.Regioes)
                {
                    var pts = new Point2dCollection(reg.Select(Papel).Append(Papel(reg[0])).ToArray());
                    var bulges = new DoubleCollection(Enumerable.Repeat(0.0, pts.Count).ToArray());
                    h.AppendLoop(HatchLoopTypes.Default, pts, bulges);
                }
                h.EvaluateHatch(true);

                if (cfg.Titulo)
                {
                    double tituloAlt = Math.Min(bw, bh) * 0.05;
                    var titulo = new MText
                    {
                        Contents = "MAPA CHAVE",
                        TextHeight = tituloAlt,
                        Layer = LayerTexto,
                        Attachment = AttachmentPoint.TopCenter,
                        Location = new Point3d(centroCaixa.X, Math.Max(c0.Y, c1.Y) - tituloAlt * 0.3, 0),
                        Width = Math.Min(bw * 0.62, tituloAlt * 14)
                    };
                    Adicionar(tr, btr, titulo);
                    double y = Math.Max(c0.Y, c1.Y) - tituloAlt * 1.75;
                    Adicionar(tr, btr, new Line(new Point3d(centroCaixa.X - titulo.Width / 2, y, 0), new Point3d(centroCaixa.X + titulo.Width / 2, y, 0)) { Layer = LayerTexto });
                }

                layouts++;
                regioesTotal += folha.Regioes.Count;
            }

            tr.Commit();
        }

        AvisarMudanca();
        return new OpResult(layouts, 0,
            layouts > 0
                ? $"Mapa-chave em {layouts} layout(s) ({regioesTotal} area(s) de folha)."
                : "Nenhuma viewport mostra a base: confira a base escolhida e as viewports das folhas.");
    }

    /// <summary>Apaga o mapa-chave de todos os layouts (a configuracao fica guardada).</summary>
    public static OpResult Remover(Document doc)
    {
        int n = 0;
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dic)
            {
                var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                if (lay.ModelType)
                    continue;
                var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite);
                n += Limpar(tr, btr);
            }
            tr.Commit();
        }

        KeyMapConfig cfg = Ler(db);
        if (cfg.Automatico)
        {
            cfg.Automatico = false;
            Gravar(doc, cfg);
        }
        AvisarMudanca();
        return new OpResult(n, 0, $"Mapa-chave removido ({n} objeto(s)). Atualizacao automatica desligada.");
    }

    /// <summary>Layouts, area(s) de folha sobre a base e se ja tem mapa (lista do painel).</summary>
    public static List<KeyMapSheet> Resumo(Database db)
    {
        var lista = new List<KeyMapSheet>();
        using Transaction tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        Extents3d? ext = bt.Has(Bloco) ? Extensao(tr, bt[Bloco]) : null;
        foreach (var f in Folhas(tr, db, ext))
        {
            var btr = (BlockTableRecord)tr.GetObject(f.Btr, OpenMode.ForRead);
            bool tem = false;
            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is Entity en && en.GetXDataForApplication(App) is ResultBuffer rb)
                {
                    rb.Dispose();
                    tem = true;
                    break;
                }
            }
            lista.Add(new KeyMapSheet(f.Nome, f.Ordem, f.Regioes.Count, tem));
        }
        tr.Commit();
        return lista;
    }

    // ------------------------------------------------------------------
    // Base (bloco)
    // ------------------------------------------------------------------

    private static ObjectId MontarBloco(Transaction tr, Database db, KeyMapConfig cfg)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        BlockTableRecord btr;
        if (bt.Has(Bloco))
        {
            btr = (BlockTableRecord)tr.GetObject(bt[Bloco], OpenMode.ForWrite);
            foreach (ObjectId id in btr)
                tr.GetObject(id, OpenMode.ForWrite, false, true).Erase();
        }
        else
        {
            bt.UpgradeOpen();
            btr = new BlockTableRecord { Name = Bloco, Origin = Point3d.Origin };
            bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
        }

        foreach (ObjectId id in cfg.Base)
        {
            if (id.IsNull || id.IsErased || !id.IsValid)
                continue;
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent)
                continue;
            foreach (Entity copia in Simplificar(ent, 0, clonar: true))
            {
                btr.AppendEntity(copia);
                tr.AddNewlyCreatedDBObject(copia, true);
            }
        }
        return btr.ObjectId;
    }

    /// <summary>
    /// Copia de curvas/hachuras do AutoCAD; objetos do Civil e blocos sao explodidos
    /// (ate 3 niveis) e so as curvas sao mantidas.
    /// </summary>
    private static IEnumerable<Entity> Simplificar(Entity ent, int nivel, bool clonar)
    {
        bool nativo = ent.GetType().Namespace == "Autodesk.AutoCAD.DatabaseServices";
        if (nativo && ent is Curve and not Xline and not Ray)
        {
            yield return clonar ? (Entity)ent.Clone() : ent;
            yield break;
        }
        if (nativo && ent is Hatch)
        {
            yield return clonar ? (Entity)ent.Clone() : ent;
            yield break;
        }
        if (nivel >= 3)
        {
            if (!clonar)
                ent.Dispose();
            yield break;
        }

        var partes = new DBObjectCollection();
        try
        {
            ent.Explode(partes);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
        }
        if (!clonar)
            ent.Dispose();

        foreach (DBObject o in partes)
        {
            if (o is Entity e)
                foreach (Entity r in Simplificar(e, nivel + 1, clonar: false))
                    yield return r;
            else
                o.Dispose();
        }
    }

    private static Extents3d? Extensao(Transaction tr, ObjectId blocoId)
    {
        var btr = (BlockTableRecord)tr.GetObject(blocoId, OpenMode.ForRead);
        Extents3d? ext = null;
        foreach (ObjectId id in btr)
        {
            try
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity e)
                    continue;
                Extents3d x = e.GeometricExtents;
                if (ext is Extents3d a)
                {
                    a.AddExtents(x);
                    ext = a;
                }
                else
                {
                    ext = x;
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
            }
        }
        return ext;
    }

    // ------------------------------------------------------------------
    // Regioes das folhas
    // ------------------------------------------------------------------

    /// <summary>
    /// Para cada layout (ordem das abas), as areas do Model mostradas pelas viewports de planta
    /// que cruzam a base. Viewports de perfil/detalhe fora da base ficam de fora.
    /// </summary>
    private static List<(string Nome, int Ordem, ObjectId Btr, List<Point2d[]> Regioes)> Folhas(Transaction tr, Database db, Extents3d? baseExt)
    {
        var lista = new List<(string, int, ObjectId, List<Point2d[]>)>();
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dic)
        {
            var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (lay.ModelType)
                continue;

            var regioes = new List<Point2d[]>();
            var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
            ObjectIdCollection vpsLayout = lay.GetViewports();
            ObjectId papel = vpsLayout.Count > 0 ? vpsLayout[0] : ObjectId.Null;
            bool primeiro = vpsLayout.Count == 0;

            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Viewport vp)
                    continue;
                if (id == papel || primeiro)
                {
                    primeiro = false;
                    continue;
                }
                Point2d[]? reg = Regiao(tr, vp);
                if (reg is null)
                    continue;
                if (baseExt is Extents3d be && !Cruza(reg, be))
                    continue;
                regioes.Add(reg);
            }
            lista.Add((lay.LayoutName, lay.TabOrder, lay.BlockTableRecordId, regioes));
        }
        return lista.OrderBy(f => f.Item2).ToList();
    }

    /// <summary>Contorno da viewport levado do papel para o Model (so vista em planta).</summary>
    private static Point2d[]? Regiao(Transaction tr, Viewport vp)
    {
        if (!vp.ViewDirection.IsParallelTo(Vector3d.ZAxis) || vp.CustomScale <= 0 || vp.Width <= 0 || vp.Height <= 0)
            return null;

        var papel = new List<Point3d>();
        if (vp.NonRectClipOn && !vp.NonRectClipEntityId.IsNull && tr.GetObject(vp.NonRectClipEntityId, OpenMode.ForRead) is Curve clip)
        {
            try
            {
                const int n = 48;
                for (int k = 0; k < n; k++)
                    papel.Add(clip.GetPointAtParameter(clip.StartParam + (clip.EndParam - clip.StartParam) * k / n));
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                papel.Clear();
            }
        }
        if (papel.Count < 3)
        {
            Point3d c = vp.CenterPoint;
            double w = vp.Width / 2, h = vp.Height / 2;
            papel.AddRange(new[]
            {
                new Point3d(c.X - w, c.Y - h, 0), new Point3d(c.X + w, c.Y - h, 0),
                new Point3d(c.X + w, c.Y + h, 0), new Point3d(c.X - w, c.Y + h, 0)
            });
        }

        Matrix3d ps2wcs = Dcs2Wcs(vp) * Dcs2Psdcs(vp).Inverse();
        return papel.Select(p => p.TransformBy(ps2wcs)).Select(p => new Point2d(p.X, p.Y)).ToArray();
    }

    private static Matrix3d Dcs2Wcs(Viewport vp) =>
        Matrix3d.Rotation(-vp.TwistAngle, vp.ViewDirection, vp.ViewTarget) *
        Matrix3d.Displacement(vp.ViewTarget - Point3d.Origin) *
        Matrix3d.PlaneToWorld(vp.ViewDirection);

    private static Matrix3d Dcs2Psdcs(Viewport vp) =>
        Matrix3d.Scaling(vp.CustomScale, vp.CenterPoint) *
        Matrix3d.Displacement(vp.CenterPoint - Point3d.Origin) *
        Matrix3d.Displacement(new Vector3d(-vp.ViewCenter.X, -vp.ViewCenter.Y, 0.0));

    private static bool Cruza(Point2d[] reg, Extents3d e)
    {
        double minX = reg.Min(p => p.X), maxX = reg.Max(p => p.X), minY = reg.Min(p => p.Y), maxY = reg.Max(p => p.Y);
        return minX <= e.MaxPoint.X && maxX >= e.MinPoint.X && minY <= e.MaxPoint.Y && maxY >= e.MinPoint.Y;
    }

    // ------------------------------------------------------------------
    // Utilitarios
    // ------------------------------------------------------------------

    /// <summary>
    /// Posicao, rotacao, altura e largura do rotulo, como na LISP (MK:ViewportLabelGeom):
    /// os pontos vem na ordem BL, BR, TR, TL do papel; a aresta TL-TR e o topo da folha.
    /// </summary>
    private static (Point2d Pos, double Ang, double Alt, double Larg) GeometriaRotulo(Point2d[] p, bool ambos)
    {
        if (p.Length < 4)
        {
            Point2d c = Centroide(p);
            double d = Math.Sqrt(Area(p));
            return (c, 0, Math.Max(d * 0.08, 1e-3), d);
        }
        Point2d p0 = p[0], p1 = p[1], p2 = p[2], p3 = p[3];
        double l1 = p0.GetDistanceTo(p1), l2 = p0.GetDistanceTo(p3);
        double major = Math.Max(l1, l2), minor = Math.Min(l1, l2);
        if (major < 1e-12) major = 1;
        if (minor < 1e-12) minor = major * 0.2;

        var top = new Vector2d(p2.X - p3.X, p2.Y - p3.Y);
        if (top.Length < 1e-12) top = new Vector2d(p1.X - p0.X, p1.Y - p0.Y);
        double ang = Math.Atan2(top.Y, top.X);
        var topMid = new Point2d((p3.X + p2.X) / 2, (p3.Y + p2.Y) / 2);
        Point2d centro = Centroide(p);
        var fora = new Vector2d(topMid.X - centro.X, topMid.Y - centro.Y);
        fora = fora.Length < 1e-12 ? new Vector2d(-top.Y, top.X).GetNormal() : fora.GetNormal();

        double h = Math.Min(minor * (ambos ? 0.145 : 0.18), major * (ambos ? 0.065 : 0.08));
        h = Math.Max(h, major * 0.012);
        double offset = h * 0.22;
        var pos = new Point2d(topMid.X + fora.X * offset, topMid.Y + fora.Y * offset);
        return (pos, ang, h, Math.Max(major * 0.92, h * 4));
    }

    private static double AnguloMaior(Point2d[] p)
    {
        if (p.Length < 4) return 0;
        double l1 = p[0].GetDistanceTo(p[1]), l2 = p[0].GetDistanceTo(p[3]);
        Point2d a = p[0], b = l1 >= l2 ? p[1] : p[3];
        return Math.Atan2(b.Y - a.Y, b.X - a.X);
    }

    /// <summary>Numero/total do carimbo (textos marcados) ou ordem/quantidade de layouts.</summary>
    private static string RotuloFolha(Transaction tr, DBDictionary dic, KeyMapConfig cfg, string nome, int ordem, int total)
    {
        string numero = Fmt(ordem), tot = "/" + Fmt(total);
        if (cfg.UsarCarimbo && dic.Contains(nome))
        {
            var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(dic.GetAt(nome), OpenMode.ForRead);
            (string? n, string? t) = LayoutNumbering.LerNumeracao(tr, lay);
            if (!string.IsNullOrWhiteSpace(n)) numero = n.Trim();
            if (!string.IsNullOrWhiteSpace(t))
                tot = t.Trim().StartsWith('/') ? t.Trim() : "/" + t.Trim();
        }
        string num = numero + tot;
        return cfg.Rotulo switch { 1 => nome, 2 => nome + "\\P" + num, _ => num };
    }

    private static string Fmt(int n) => n < 10 ? "0" + n : n.ToString();

    private static double Area(Point2d[] p)
    {
        double a = 0;
        for (int i = 0; i < p.Length; i++)
        {
            Point2d q = p[i], r = p[(i + 1) % p.Length];
            a += q.X * r.Y - r.X * q.Y;
        }
        return Math.Abs(a / 2);
    }

    private static Point2d Centroide(Point2d[] p) => new(p.Average(q => q.X), p.Average(q => q.Y));

    private static void Adicionar(Transaction tr, BlockTableRecord btr, Entity ent)
    {
        btr.AppendEntity(ent);
        tr.AddNewlyCreatedDBObject(ent, true);
        ent.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App));
    }

    /// <summary>Apaga o que o plugin criou no layout. Retorna quantos objetos.</summary>
    private static int Limpar(Transaction tr, BlockTableRecord btr)
    {
        int n = 0;
        foreach (ObjectId id in btr)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity e)
                continue;
            using ResultBuffer? rb = e.GetXDataForApplication(App);
            if (rb is null)
                continue;
            tr.GetObject(id, OpenMode.ForWrite, false, true).Erase();
            n++;
        }
        return n;
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

    private static void GarantirLayer(Transaction tr, Database db, string nome, short cor)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(nome))
            return;
        lt.UpgradeOpen();
        var ltr = new LayerTableRecord { Name = nome, Color = AcColor.FromColorIndex(AcColorMethod.ByAci, cor) };
        lt.Add(ltr);
        tr.AddNewlyCreatedDBObject(ltr, true);
    }
}

/// <summary>
/// Atualizacao automatica do mapa-chave: quando uma viewport ou um layout muda num desenho
/// com mapa-chave automatico, o mapa e refeito no proximo Idle (fora de comando).
/// </summary>
internal static class KeyMapSync
{
    private static readonly HashSet<Database> Ativos = new();
    private static readonly HashSet<Database> Sujos = new();
    private static readonly HashSet<Database> Ligados = new();
    private static bool _iniciado;
    private static bool _gerando;

    public static void Initialize()
    {
        if (_iniciado)
            return;
        _iniciado = true;
        AcApp.DocumentManager.DocumentCreated += (_, e) => Ligar(e.Document);
        foreach (Document d in AcApp.DocumentManager)
            Ligar(d);
        AcApp.Idle += OnIdle;
    }

    public static void Terminate()
    {
        if (!_iniciado)
            return;
        AcApp.Idle -= OnIdle;
        _iniciado = false;
    }

    /// <summary>Liga/desliga a atualizacao automatica de um desenho (chamado ao gravar a configuracao).</summary>
    public static void Atualizar(Database db, bool automatico)
    {
        if (automatico)
            Ativos.Add(db);
        else
            Ativos.Remove(db);
    }

    private static void Ligar(Document? doc)
    {
        if (doc is null)
            return;
        Database db = doc.Database;
        if (!Ligados.Add(db))
            return;

        db.ObjectModified += OnObjeto;
        db.ObjectAppended += OnObjeto;
        db.ObjectErased += OnApagado;
        try
        {
            if (LayoutKeyMap.Existe(db) && LayoutKeyMap.Ler(db).Automatico)
                Ativos.Add(db);
        }
        catch (System.Exception)
        {
        }
    }

    private static void OnObjeto(object? sender, ObjectEventArgs e)
    {
        if (_gerando || sender is not Database db || !Ativos.Contains(db))
            return;
        if (e.DBObject is Viewport or Autodesk.AutoCAD.DatabaseServices.Layout)
            Sujos.Add(db);
    }

    private static void OnApagado(object? sender, ObjectErasedEventArgs e)
    {
        if (_gerando || sender is not Database db || !Ativos.Contains(db))
            return;
        if (e.DBObject is Viewport or Autodesk.AutoCAD.DatabaseServices.Layout)
            Sujos.Add(db);
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        if (Sujos.Count == 0 || _gerando)
            return;
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || !Sujos.Contains(doc.Database) || !doc.Editor.IsQuiescent)
            return;

        Sujos.Remove(doc.Database);
        try
        {
            _gerando = true;
            using (Civil3D2026Plugin.Infrastructure.PluginLog.Medir("Layouts", "Mapa-chave automatico"))
                LayoutKeyMap.Gerar(doc, reconstruirBase: false);
        }
        catch (System.Exception ex)
        {
            Civil3D2026Plugin.Infrastructure.PluginLog.Erro("Layouts", "Mapa-chave: atualizacao automatica falhou", ex);
            doc.Editor.WriteMessage($"\n[Mapa-chave] Atualizacao automatica falhou: {ex.Message}");
        }
        finally
        {
            _gerando = false;
        }
    }
}
