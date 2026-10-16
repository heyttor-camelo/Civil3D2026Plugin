using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.Geometry;

using AcadColor = Autodesk.AutoCAD.Colors.Color;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>
/// Ficha do PED na planta, so com recursos nativos do AutoCAD (quem nao tem o plugin usa igual):
///
/// - um bloco por PED, "PED_FICHA_codigo", com a tabela no arranjo da aba "Ficha para desenhos"
///   e, a direita, a foto do local e o mapa (raster dentro do bloco);
/// - esse bloco e o conteudo de um MULTILEADER cuja seta fica na borda do simbolo PED:
///   arrastar o grip do multileader move a ficha e a linha de chamada acompanha;
/// - layers por PED: PED-FICHA-codigo (multileader + tabela) e PED-FOTO-codigo (imagens).
///   LAYOFF/LAYFRZ na ficha esconde aquele PED; os filtros "PED - fichas" e "PED - fotos"
///   do gerenciador de layers ligam/desligam todos de uma vez.
///
/// Reimportar refaz o conteudo do bloco da ficha, mas mantem onde o usuario deixou a ficha.
/// </summary>
internal static class PedFichaBlock
{
    public const string BlockPrefix = "PED_FICHA_";
    public const string LayerFichaPrefix = "PED-FICHA-";
    public const string LayerFotoPrefix = "PED-FOTO-";
    public const string FilterFichas = "PED - fichas";
    public const string FilterFotos = "PED - fotos";
    public const string LeaderMark = "FICHA";

    // Tabela (coordenadas do bloco da ficha; origem = canto superior esquerdo).
    private const double RowH = 0.9;
    private const double CellText = 0.42;
    private const double Pad = 0.3;
    private static readonly double[] ColW = { 11.0, 14.0, 7.0, 7.0 };
    private const double PhotoGap = 1.0;
    private const double PhotoWidth = 24.0;

    private static double TableWidth => ColW.Sum();

    public static string LayerFicha(string code) => LayerFichaPrefix + PedStore.KeyOf(code);

    public static string LayerFoto(string code) => LayerFotoPrefix + PedStore.KeyOf(code);

    // ------------------------------------------------------------------
    // Bloco da ficha (um por PED)
    // ------------------------------------------------------------------

    /// <summary>
    /// Cria ou refaz o bloco PED_FICHA_codigo com a tabela e as imagens. Retorna o id da definicao.
    /// </summary>
    public static AcadDb.ObjectId EnsureDefinition(AcadDb.Transaction tr, AcadDb.Database db, string code,
                                                   IReadOnlyList<PedFicha.Attr> fields, PedPhotos.ImageFile? foto, PedPhotos.ImageFile? mapa)
    {
        PedBlock.EnsureLayer(tr, db, LayerFicha(code), 7);
        PedBlock.EnsureLayer(tr, db, LayerFoto(code), 7);

        string name = BlockPrefix + PedStore.KeyOf(code);
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord btr;
        AcadDb.ObjectId id;
        if (bt.Has(name))
        {
            id = bt[name];
            btr = (AcadDb.BlockTableRecord)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
            List<AcadDb.ObjectId> old = new();
            foreach (AcadDb.ObjectId e in btr)
                old.Add(e);
            foreach (AcadDb.ObjectId e in old)
                tr.GetObject(e, AcadDb.OpenMode.ForWrite).Erase();
        }
        else
        {
            bt.UpgradeOpen();
            btr = new AcadDb.BlockTableRecord { Name = name, Origin = Point3d.Origin };
            id = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
        }

        Dictionary<string, string> v = fields.ToDictionary(f => f.Tag, f => f.Value, StringComparer.OrdinalIgnoreCase);
        new TableWriter(tr, db, btr, v, LayerFicha(code)).Build();

        // Imagens a direita da tabela, na layer PED-FOTO-codigo.
        double x = TableWidth + PhotoGap;
        double top = 0;
        foreach ((PedPhotos.ImageFile? file, string kind) in new[] { (foto, PedPhotos.KindFoto), (mapa, PedPhotos.KindMapa) })
        {
            if (file is not PedPhotos.ImageFile f || f.Saved.Length == 0)
                continue;
            AcadDb.RasterImageDef? def = PedPhotos.Definition(tr, db, code, kind, f);
            if (def is null)
                continue;

            double aspect = def.Size.X > 0 ? def.Size.Y / def.Size.X : 0.6;
            double h = PhotoWidth * aspect;
            AcadDb.RasterImage img = new();
            img.SetDatabaseDefaults(db);
            img.Layer = LayerFoto(code);
            img.ImageDefId = def.ObjectId;
            img.Orientation = new CoordinateSystem3d(new Point3d(x, top - h, 0), new Vector3d(PhotoWidth, 0, 0), new Vector3d(0, h, 0));
            img.ShowImage = true;
            btr.AppendEntity(img);
            tr.AddNewlyCreatedDBObject(img, true);
            AcadDb.RasterImage.EnableReactors(true);
            img.AssociateRasterDef(def);
            top -= h + PhotoGap;
        }

        return id;
    }

    // ------------------------------------------------------------------
    // Multileader
    // ------------------------------------------------------------------

    /// <summary>Multileaders de ficha no espaco modelo: codigo -> id.</summary>
    public static Dictionary<string, AcadDb.ObjectId> FindLeaders(AcadDb.Transaction tr, AcadDb.Database db)
    {
        Dictionary<string, AcadDb.ObjectId> map = new(StringComparer.OrdinalIgnoreCase);
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        Autodesk.AutoCAD.Runtime.RXClass mlClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AcadDb.MLeader));
        foreach (AcadDb.ObjectId id in ms)
        {
            if (id.ObjectClass.IsDerivedFrom(mlClass) && PedStore.CodeOf(tr.GetObject(id, AcadDb.OpenMode.ForRead)) is string code)
                map.TryAdd(code, id);
        }
        return map;
    }

    /// <summary>
    /// Cria o multileader da ficha (seta na borda do simbolo, ficha acima a direita) ou,
    /// se ja existe, so atualiza o conteudo e leva a seta para o simbolo (a ficha fica onde estava).
    /// </summary>
    public static AcadDb.ObjectId EnsureLeader(AcadDb.Transaction tr, AcadDb.Database db, string code, AcadDb.BlockReference symbol,
                                               AcadDb.ObjectId fichaDefId, AcadDb.ObjectId existing)
    {
        double scale = Math.Abs(symbol.ScaleFactors.X) > 1e-9 ? Math.Abs(symbol.ScaleFactors.X) : 1.0;
        Point3d arrow = PedBlock.LeaderAnchor.TransformBy(symbol.BlockTransform);

        if (!existing.IsNull && !existing.IsErased)
        {
            AcadDb.MLeader ml = (AcadDb.MLeader)tr.GetObject(existing, AcadDb.OpenMode.ForWrite);
            ml.BlockContentId = fichaDefId;
            foreach (object o in ml.GetLeaderLineIndexes())
                ml.SetFirstVertex(Convert.ToInt32(o), arrow);
            ml.RecordGraphicsModified(true);
            return existing;
        }

        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

        AcadDb.MLeader leader = new();
        leader.SetDatabaseDefaults(db);
        leader.Layer = LayerFicha(code);
        leader.ContentType = AcadDb.ContentType.BlockContent;
        leader.BlockContentId = fichaDefId;
        leader.BlockConnectionType = AcadDb.BlockConnectionType.ConnectExtents;
        leader.BlockScale = new Scale3d(scale);
        // Ficha acima e a direita do simbolo; o usuario arrasta para onde quiser.
        leader.BlockPosition = new Point3d(arrow.X + 6.0 * scale, arrow.Y + 8.0 * scale, 0);
        leader.ArrowSize = 0.8 * scale;
        leader.EnableLanding = true;
        leader.EnableDogleg = true;
        leader.DoglegLength = 2.0 * scale;
        leader.LandingGap = 0.3 * scale;

        AcadDb.ObjectId id = ms.AppendEntity(leader);
        tr.AddNewlyCreatedDBObject(leader, true);
        int line = leader.AddLeaderLine(new Point3d(arrow.X + 4.0 * scale, arrow.Y + 4.0 * scale, 0));
        leader.AddFirstVertex(line, arrow);

        leader.XData = new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, PedStore.RegApp),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, code),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, LeaderMark));
        return id;
    }

    /// <summary>Leva a seta do multileader para o simbolo (depois de mover/girar o bloco PED).</summary>
    public static void FollowSymbol(AcadDb.Transaction tr, AcadDb.ObjectId leaderId, AcadDb.BlockReference symbol)
    {
        if (leaderId.IsNull || leaderId.IsErased)
            return;
        AcadDb.MLeader ml = (AcadDb.MLeader)tr.GetObject(leaderId, AcadDb.OpenMode.ForWrite);
        Point3d arrow = PedBlock.LeaderAnchor.TransformBy(symbol.BlockTransform);
        foreach (object o in ml.GetLeaderLineIndexes())
            ml.SetFirstVertex(Convert.ToInt32(o), arrow);
    }

    // ------------------------------------------------------------------
    // Layers e filtros
    // ------------------------------------------------------------------

    /// <summary>Liga/desliga a ficha e as fotos de um PED (layers dele).</summary>
    public static void SetVisible(AcadDb.Transaction tr, AcadDb.Database db, string code, bool ficha, bool foto)
    {
        AcadDb.LayerTable lt = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        foreach ((string name, bool on) in new[] { (LayerFicha(code), ficha), (LayerFoto(code), foto) })
        {
            if (!lt.Has(name))
                continue;
            AcadDb.LayerTableRecord l = (AcadDb.LayerTableRecord)tr.GetObject(lt[name], AcadDb.OpenMode.ForWrite);
            if (l.IsOff == on)
                l.IsOff = !on;
            if (on && l.IsFrozen && db.Clayer != l.ObjectId)
                l.IsFrozen = false;
        }
    }

    /// <summary>Filtros de layer "PED - fichas" e "PED - fotos" (por nome, pegam PEDs novos sozinhos).</summary>
    public static void EnsureLayerFilters(AcadDb.Database db)
    {
        try
        {
            Autodesk.AutoCAD.LayerManager.LayerFilterTree tree = db.LayerFilters;
            Autodesk.AutoCAD.LayerManager.LayerFilterCollection root = tree.Root.NestedFilters;
            bool changed = false;
            foreach ((string name, string prefix) in new[] { (FilterFichas, LayerFichaPrefix), (FilterFotos, LayerFotoPrefix) })
            {
                if (root.Cast<Autodesk.AutoCAD.LayerManager.LayerFilter>().Any(f => f.Name == name))
                    continue;
                Autodesk.AutoCAD.LayerManager.LayerFilter filter = new()
                {
                    Name = name,
                    FilterExpression = "NAME==\"" + prefix + "*\""
                };
                root.Add(filter);
                changed = true;
            }
            if (changed)
                db.LayerFilters = tree;
        }
        catch
        {
            // Filtro e conveniencia.
        }
    }

    // ------------------------------------------------------------------
    // Tabela
    // ------------------------------------------------------------------

    /// <summary>Linhas e textos da ficha (valores ja escritos: o bloco e de um PED so), na layer PED-FICHA-codigo.</summary>
    private sealed class TableWriter
    {
        private readonly AcadDb.Transaction _tr;
        private readonly AcadDb.Database _db;
        private readonly AcadDb.BlockTableRecord _btr;
        private readonly Dictionary<string, string> _v;
        private readonly string _layer;
        private readonly double[] _x = new double[5];
        private double _y;

        private const int Left = 0, Center = 1, Right = 2;

        public TableWriter(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockTableRecord btr, Dictionary<string, string> values, string layer)
        {
            _layer = layer;
            _tr = tr;
            _db = db;
            _btr = btr;
            _v = values;
            for (int i = 0; i < 4; i++)
                _x[i + 1] = _x[i] + ColW[i];
        }

        private string V(string tag) => _v.TryGetValue(tag, out string? s) ? s : string.Empty;

        public void Build()
        {
            double top = _y;
            HLine(_x[0], _x[4]);

            Row(() => { Label("PED", 0, bold: true); Value("CODIGO", 1, Left, bold: true); }, 1);
            Row(() => { Label("Via:", 0); Value("VIA", 1, Left); }, 1);
            Row(() => { Label("Referência:", 0); Value("REFERENCIA", 1, Left); }, 1);
            Row(() => { Label("Quadra:", 0); Value("QUADRA", 1, Left); }, 1);
            Row(() => { Label("Sentido:", 0); Value("SENTIDO", 1, Left); }, 1);

            Row(() => Label("Larguras dos componentes viários (em metros)", 0, center: true, span: 4, bold: true));
            Row(() => { Label("Componente", 0, center: true, span: 2); Label("Atual", 2, center: true); Label("Proposta", 3, center: true); }, 2, 3);

            Group("Pista junto ao PED", new[]
            {
                ("Calçada", "CALCADA_ATUAL", "CALCADA_PROP"),
                ("Faixa exclusiva de ônibus", "FX_EXCL_ATUAL", "FX_EXCL_PROP"),
                ("Faixas do tráfego geral", "TRAFEGO_ATUAL", "TRAFEGO_PROP"),
                ("Ciclovia/ ciclofaixa", string.Empty, "CICLO_PED_PROP")
            });
            Row(() => { Label("Canteiro central", 0, span: 2); Value("CANTEIRO_ATUAL", 2, Right); Value("CANTEIRO_PROP", 3, Right); }, 2, 3);
            Group("Pista oposta ao PED", new[]
            {
                ("Pista", "PISTA_OPOSTA_ATUAL", "PISTA_OPOSTA_PROP"),
                ("Calçada", "CALCADA_OPOSTA_ATUAL", "CALCADA_OPOSTA_PROP"),
                ("Ciclovia/ ciclofaixa", "CICLO_OPOSTA_ATUAL", "CICLO_OPOSTA_PROP")
            });
            Row(() => { Label("Seção viária total", 0, span: 2, bold: true); Value("SECAO_TOTAL_ATUAL", 2, Right, bold: true); Value("SECAO_TOTAL_PROP", 3, Right, bold: true); }, 2, 3);

            Row(() => Label("Informações complementares da proposta", 0, center: true, span: 4, bold: true));
            foreach ((string label, string tag) in new[]
            {
                ("Modelo de abrigo proposto", "ABRIGO"),
                ("Faixa exclusiva", "FAIXA_EXCLUSIVA"),
                ("Ciclovia ou ciclofaixa", "CICLOVIA"),
                ("Alteração calçada PED", "ALT_CALCADA_PED"),
                ("Alteração do canteiro central", "ALT_CANTEIRO"),
                ("Alteração calçada oposta PED", "ALT_CALCADA_OPOSTA"),
                ("Embarques PM (passag.)", "EMBARQUES_PM"),
                ("Embarques PT (passag.)", "EMBARQUES_PT")
            })
            {
                Row(() => { Label(label, 0, span: 2); Value(tag, 2, Left); }, 2);
            }
            Row(() => { Label("Distância do PED anterior (m)", 0, span: 2); Value("PED_ANTERIOR", 2, Center); Value("DIST_ANTERIOR", 3, Right); }, 2, 3);
            Row(() => { Label("Distância do PED posterior (m)", 0, span: 2); Value("PED_POSTERIOR", 2, Center); Value("DIST_POSTERIOR", 3, Right); }, 2, 3);
            Row(() => { Label("Obs:", 0); Value("OBS", 1, Left); }, 1);

            VLine(_x[0], top, _y);
            VLine(_x[4], top, _y);
        }

        private void Group(string group, (string Item, string Atual, string Proposta)[] rows)
        {
            double groupTop = _y;
            for (int i = 0; i < rows.Length; i++)
            {
                double yMid = _y - RowH / 2.0;
                Text(rows[i].Item, _x[1] + Pad, yMid, AcadDb.AttachmentPoint.MiddleLeft, false);
                if (rows[i].Atual.Length > 0)
                    Text(V(rows[i].Atual), _x[3] - Pad, yMid, AcadDb.AttachmentPoint.MiddleRight, false);
                Text(V(rows[i].Proposta), _x[4] - Pad, yMid, AcadDb.AttachmentPoint.MiddleRight, false);
                VLine(_x[1], _y, _y - RowH);
                VLine(_x[2], _y, _y - RowH);
                VLine(_x[3], _y, _y - RowH);
                _y -= RowH;
                HLine(i == rows.Length - 1 ? _x[0] : _x[1], _x[4]);
            }
            Text(group, _x[0] + Pad, (groupTop + _y) / 2.0, AcadDb.AttachmentPoint.MiddleLeft, false);
        }

        private void Row(Action content, int split = -1, int split2 = -1)
        {
            content();
            if (split > 0)
                VLine(_x[split], _y, _y - RowH);
            if (split2 > 0)
                VLine(_x[split2], _y, _y - RowH);
            _y -= RowH;
            HLine(_x[0], _x[4]);
        }

        private void Label(string text, int col, bool center = false, int span = 1, bool bold = false)
        {
            double yMid = _y - RowH / 2.0;
            if (center)
                Text(text, (_x[col] + _x[col + span]) / 2.0, yMid, AcadDb.AttachmentPoint.MiddleCenter, bold);
            else
                Text(text, _x[col] + Pad, yMid, AcadDb.AttachmentPoint.MiddleLeft, bold);
        }

        private void Value(string tag, int col, int align, bool bold = false)
        {
            string text = V(tag);
            if (text.Length == 0)
                return;
            double yMid = _y - RowH / 2.0;
            (double x, AcadDb.AttachmentPoint j) = align switch
            {
                Right => (_x[col + 1] - Pad, AcadDb.AttachmentPoint.MiddleRight),
                Center => ((_x[col] + _x[col + 1]) / 2.0, AcadDb.AttachmentPoint.MiddleCenter),
                _ => (_x[col] + Pad, AcadDb.AttachmentPoint.MiddleLeft)
            };
            Text(text, x, yMid, j, bold);
        }

        private void Text(string text, double x, double y, AcadDb.AttachmentPoint j, bool bold)
        {
            if (string.IsNullOrEmpty(text))
                return;
            AcadDb.DBText t = new();
            t.SetDatabaseDefaults(_db);
            t.Layer = _layer;
            t.Color = AcadColor.FromColorIndex(ColorMethod.ByLayer, 256);
            t.TextStyleId = _db.Textstyle;
            t.Height = bold ? CellText * 1.15 : CellText;
            t.TextString = text;
            t.Position = new Point3d(x, y, 0);
            t.Justify = j;
            if (j != AcadDb.AttachmentPoint.BaseLeft)
                t.AlignmentPoint = new Point3d(x, y, 0);
            _btr.AppendEntity(t);
            _tr.AddNewlyCreatedDBObject(t, true);
            try
            {
                t.AdjustAlignment(_db);
            }
            catch
            {
                // Alinhamento cosmetico.
            }
        }

        private void HLine(double x1, double x2) => Line(new Point3d(x1, _y, 0), new Point3d(x2, _y, 0));

        private void VLine(double x, double y1, double y2) => Line(new Point3d(x, y1, 0), new Point3d(x, y2, 0));

        private void Line(Point3d a, Point3d b)
        {
            AcadDb.Line l = new(a, b);
            l.SetDatabaseDefaults(_db);
            l.Layer = _layer;
            l.Color = AcadColor.FromColorIndex(ColorMethod.ByLayer, 256);
            _btr.AppendEntity(l);
            _tr.AddNewlyCreatedDBObject(l, true);
        }
    }
}
