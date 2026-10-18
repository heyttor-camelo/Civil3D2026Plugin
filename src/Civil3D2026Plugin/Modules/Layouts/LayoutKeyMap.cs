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
            new(72, (short)cfg.Transparencia)
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

            Point2d c0 = cfg.CaixaMin!.Value, c1 = cfg.CaixaMax!.Value;
            double bw = Math.Abs(c1.X - c0.X), bh = Math.Abs(c1.Y - c0.Y);
            double ew = Math.Max(1e-9, ext.Value.MaxPoint.X - ext.Value.MinPoint.X);
            double eh = Math.Max(1e-9, ext.Value.MaxPoint.Y - ext.Value.MinPoint.Y);
            double s = Math.Min(bw / ew, bh / eh) * 0.96;
            var centroCaixa = new Point2d((c0.X + c1.X) / 2, (c0.Y + c1.Y) / 2);
            var centroBase = new Point2d((ext.Value.MinPoint.X + ext.Value.MaxPoint.X) / 2, (ext.Value.MinPoint.Y + ext.Value.MaxPoint.Y) / 2);
            Point2d Papel(Point2d p) => new(centroCaixa.X + (p.X - centroBase.X) * s, centroCaixa.Y + (p.Y - centroBase.Y) * s);
            double alturaTexto = Math.Min(bw, bh) * 0.045;

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

                // Contorno e rotulo de todas as folhas.
                foreach (var outra in folhas.Where(f => f.Regioes.Count > 0))
                {
                    foreach (Point2d[] reg in outra.Regioes)
                    {
                        var pl = new Polyline { Layer = LayerFolhas, Closed = true };
                        for (int i = 0; i < reg.Length; i++)
                            pl.AddVertexAt(i, Papel(reg[i]), 0, 0, 0);
                        Adicionar(tr, btr, pl);
                    }

                    Point2d[] maior = outra.Regioes.OrderByDescending(Area).First();
                    Point2d centro = Papel(Centroide(maior));
                    var txt = new DBText
                    {
                        TextString = Rotulo(cfg.Rotulo, outra.Ordem, outra.Nome),
                        Height = alturaTexto,
                        Layer = LayerTexto,
                        Justify = AttachmentPoint.MiddleCenter,
                        AlignmentPoint = new Point3d(centro.X, centro.Y, 0),
                        Position = new Point3d(centro.X, centro.Y, 0)
                    };
                    Adicionar(tr, btr, txt);
                    txt.AdjustAlignment(db);
                }

                // Hachura da propria folha.
                var h = new Hatch { Layer = LayerDestaque };
                Adicionar(tr, btr, h);
                h.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                h.Associative = false;
                h.Color = AcColor.FromColorIndex(AcColorMethod.ByAci, cfg.CorDestaque);
                h.Transparency = new Autodesk.AutoCAD.Colors.Transparency((byte)Math.Round(255 * (1 - Math.Clamp(cfg.Transparencia, 0, 90) / 100.0)));
                foreach (Point2d[] reg in folha.Regioes)
                {
                    var pts = new Point2dCollection(reg.Select(Papel).Append(Papel(reg[0])).ToArray());
                    var bulges = new DoubleCollection(Enumerable.Repeat(0.0, pts.Count).ToArray());
                    h.AppendLoop(HatchLoopTypes.Default, pts, bulges);
                }
                h.EvaluateHatch(true);

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
                tr.GetObject(id, OpenMode.ForWrite).Erase();
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

    private static string Rotulo(int modo, int ordem, string nome)
    {
        string numero = ordem < 10 ? "0" + ordem : ordem.ToString();
        return modo switch { 1 => nome, 2 => $"{numero} - {nome}", _ => numero };
    }

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
            e.UpgradeOpen();
            e.Erase();
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
            LayoutKeyMap.Gerar(doc, reconstruirBase: false);
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage($"\n[Mapa-chave] Atualizacao automatica falhou: {ex.Message}");
        }
        finally
        {
            _gerando = false;
        }
    }
}
