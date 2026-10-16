using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.Geometry;

using AcadColor = Autodesk.AutoCAD.Colors.Color;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>
/// Bloco "PED": retangulo 8 x 3 hachurado a 45 graus (ANSI31) com o texto "PED XX"
/// no meio (a hachura tem um vazio atras do texto). Entidades na layer 0, ByLayer:
/// o bloco assume a cor da layer PED (vermelho). O ponto de insercao e o centro.
///
/// Se o desenho ja tiver um bloco chamado PED, ele e usado como esta: os atributos
/// cujo valor padrao contem "XX" recebem o codigo no lugar do XX; um atributo de tag
/// PED/COD/CODIGO sem "XX" recebe o codigo inteiro.
/// </summary>
internal static class PedBlock
{
    public const string BlockName = "PED";
    public const string LayerName = "PED";
    public const string AttTag = "PED";

    private const double Width = 8.0;
    private const double Height = 3.0;
    private const double GapWidth = 5.6;
    private const double GapHeight = 1.3;
    private const double TextHeight = 0.7;
    private const double HatchScale = 0.09;

    private static readonly string[] CodeTags = { "PED", "COD", "CODIGO", "CÓDIGO", "ID" };

    public const string LayerFicha = "PED-FICHA";
    public const string LayerFoto = "PED-FOTO";

    /// <summary>Versao do desenho do bloco gerado (XData no BTR). Sobe quando o layout muda.</summary>
    private const int BlockVersion = 2;
    private const string BlockMark = "BLOCO";

    // ----- Ficha (tabela) dentro do bloco, a direita do simbolo -----
    private const double FichaX = 6.0;          // canto superior esquerdo da tabela
    private const double FichaY = 1.5;
    private const double RowH = 0.9;
    private const double CellText = 0.42;
    private const double Pad = 0.3;
    private static readonly double[] ColW = { 11.0, 14.0, 7.0, 7.0 };

    /// <summary>Largura total da tabela (para posicionar foto e mapa ao lado).</summary>
    public static double FichaWidth => ColW[0] + ColW[1] + ColW[2] + ColW[3];

    /// <summary>Canto superior esquerdo da coluna das imagens (coordenadas do bloco) e largura.</summary>
    public static (double X, double Y, double Width) PhotoSlot => (FichaX + FichaWidth + 1.0, FichaY, 24.0);

    public static void EnsureLayer(AcadDb.Transaction tr, AcadDb.Database db)
    {
        EnsureLayer(tr, db, LayerName, 1);
        EnsureLayer(tr, db, LayerFicha, 7);
        EnsureLayer(tr, db, LayerFoto, 7);
    }

    private static void EnsureLayer(AcadDb.Transaction tr, AcadDb.Database db, string name, short aci)
    {
        AcadDb.LayerTable lt = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        if (lt.Has(name))
            return;

        lt.UpgradeOpen();
        AcadDb.LayerTableRecord layer = new()
        {
            Name = name,
            Color = AcadColor.FromColorIndex(ColorMethod.ByAci, aci)
        };
        lt.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    /// <summary>
    /// Id da definicao do bloco PED. Cria se nao existir; se existir e for o bloco gerado
    /// por versao anterior do plugin (sem a ficha em tabela), redesenha o conteudo
    /// (<paramref name="regenerated"/> = true: os blocos inseridos precisam de
    /// <see cref="ResyncAttributes"/>). Bloco dinamico ou bloco proprio do usuario: intocado.
    /// </summary>
    public static AcadDb.ObjectId EnsureBlock(AcadDb.Transaction tr, AcadDb.Database db, out bool created, out bool regenerated)
    {
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        created = false;
        regenerated = false;

        AcadDb.BlockTableRecord btr;
        AcadDb.ObjectId id;
        if (bt.Has(BlockName))
        {
            id = bt[BlockName];
            btr = (AcadDb.BlockTableRecord)tr.GetObject(id, AcadDb.OpenMode.ForRead);
            if (btr.IsDynamicBlock || !IsOutdatedOwnBlock(tr, btr))
                return id;

            btr.UpgradeOpen();
            List<AcadDb.ObjectId> old = new();
            foreach (AcadDb.ObjectId entId in btr)
                old.Add(entId);
            foreach (AcadDb.ObjectId entId in old)
                tr.GetObject(entId, AcadDb.OpenMode.ForWrite).Erase();
            regenerated = true;
        }
        else
        {
            bt.UpgradeOpen();
            btr = new AcadDb.BlockTableRecord { Name = BlockName, Origin = Point3d.Origin };
            id = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
            created = true;
        }

        BuildContent(tr, db, btr);
        btr.XData = new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, PedStore.RegApp),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, BlockMark),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataInteger32, BlockVersion));
        return id;
    }

    /// <summary>Bloco gerado pelo plugin em versao anterior (marca antiga ou o desenho da 1.0/1.1).</summary>
    private static bool IsOutdatedOwnBlock(AcadDb.Transaction tr, AcadDb.BlockTableRecord btr)
    {
        using (AcadDb.ResultBuffer? rb = btr.GetXDataForApplication(PedStore.RegApp))
        {
            if (rb is not null)
            {
                AcadDb.TypedValue[] v = rb.AsArray();
                bool ours = v.Any(t => t.Value as string == BlockMark);
                int ver = v.Where(t => t.TypeCode == (short)AcadDb.DxfCode.ExtendedDataInteger32).Select(t => Convert.ToInt32(t.Value)).FirstOrDefault();
                return ours && ver < BlockVersion;
            }
        }

        // 1.0/1.1: so retangulo + hachura + atributo PED "PED XX" (+ atributos invisiveis), sem textos/linhas da ficha.
        bool hatch = false, pedAtt = false, other = false;
        foreach (AcadDb.ObjectId entId in btr)
        {
            switch (tr.GetObject(entId, AcadDb.OpenMode.ForRead))
            {
                case AcadDb.Hatch:
                    hatch = true;
                    break;
                case AcadDb.AttributeDefinition ad when ad.Tag.Equals(AttTag, StringComparison.OrdinalIgnoreCase) && ad.TextString == "PED XX":
                    pedAtt = true;
                    break;
                case AcadDb.AttributeDefinition ad when ad.Invisible:
                case AcadDb.Polyline:
                    break;
                default:
                    other = true;
                    break;
            }
        }
        return hatch && pedAtt && !other;
    }

    private static void BuildContent(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockTableRecord btr)
    {
        AcadColor byLayer = AcadColor.FromColorIndex(ColorMethod.ByLayer, 256);

        // ---------- Simbolo (layer 0 -> assume a layer PED do bloco inserido) ----------
        AcadDb.Polyline rect = Rectangle(Width, Height);
        rect.SetDatabaseDefaults(db);
        rect.Layer = "0";
        rect.Color = byLayer;
        btr.AppendEntity(rect);
        tr.AddNewlyCreatedDBObject(rect, true);

        AcadDb.Hatch hatch = new();
        hatch.SetDatabaseDefaults(db);
        hatch.Layer = "0";
        hatch.Color = byLayer;
        btr.AppendEntity(hatch);
        tr.AddNewlyCreatedDBObject(hatch, true);
        hatch.PatternScale = HatchScale;
        hatch.SetHatchPattern(AcadDb.HatchPatternType.PreDefined, "ANSI31");
        hatch.Associative = false;
        hatch.HatchStyle = AcadDb.HatchStyle.Normal;
        hatch.AppendLoop(AcadDb.HatchLoopTypes.External | AcadDb.HatchLoopTypes.Polyline, Loop(Width, Height), Bulges());
        hatch.AppendLoop(AcadDb.HatchLoopTypes.Polyline, Loop(GapWidth, GapHeight), Bulges());
        hatch.EvaluateHatch(true);

        AddAtt(tr, db, btr, AttTag, "Codigo do PED", "PED XX", "0", TextHeight, Point3d.Origin, AcadDb.AttachmentPoint.MiddleCenter, invisible: false);

        // ---------- Ficha em tabela (layer PED-FICHA), no arranjo da aba "Ficha para desenhos" ----------
        new FichaLayout(tr, db, btr).Build();
    }

    /// <summary>Desenha a tabela da ficha: linhas, rotulos fixos (DBText) e atributos com os valores.</summary>
    private sealed class FichaLayout
    {
        private readonly AcadDb.Transaction _tr;
        private readonly AcadDb.Database _db;
        private readonly AcadDb.BlockTableRecord _btr;
        private readonly double[] _x = new double[5];
        private double _y = FichaY;

        public FichaLayout(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockTableRecord btr)
        {
            _tr = tr;
            _db = db;
            _btr = btr;
            _x[0] = FichaX;
            for (int i = 0; i < 4; i++)
                _x[i + 1] = _x[i] + ColW[i];
        }

        public void Build()
        {
            double top = _y;

            // Ligacao simbolo -> ficha.
            Line(new Point3d(Width / 2.0, 0, 0), new Point3d(FichaX, 0, 0));

            // Cabecalho
            HLine(_x[0], _x[4]);
            Row(c => { Label("PED", 0); Att("CODIGO", "Codigo do PED", 1, Left, bold: true); }, split: 1);
            Row(c => { Label("Via:", 0); Att("VIA", "Via", 1, Left); }, split: 1);
            Row(c => { Label("Referência:", 0); Att("REFERENCIA", "Referencia", 1, Left); }, split: 1);
            Row(c => { Label("Quadra:", 0); Att("QUADRA", "Quadra", 1, Left); }, split: 1);
            Row(c => { Label("Sentido:", 0); Att("SENTIDO", "Sentido", 1, Left); }, split: 1);

            // Larguras
            Row(c => Label("Larguras dos componentes viários (em metros)", 0, center: true, span: 4));
            Row(c => { Label("Componente", 0, center: true, span: 2); Label("Atual", 2, center: true); Label("Proposta", 3, center: true); }, split: 2, split2: 3);

            string[] grupoPed = { "Calçada", "Faixa exclusiva de ônibus", "Faixas do tráfego geral", "Ciclovia/ ciclofaixa" };
            string[][] tagsPed =
            {
                new[] { "CALCADA_ATUAL", "CALCADA_PROP" },
                new[] { "FX_EXCL_ATUAL", "FX_EXCL_PROP" },
                new[] { "TRAFEGO_ATUAL", "TRAFEGO_PROP" },
                new[] { string.Empty, "CICLO_PED_PROP" }
            };
            Group("Pista junto ao PED", grupoPed, tagsPed);

            Row(c => { Label("Canteiro central", 0, span: 2); Att("CANTEIRO_ATUAL", "Canteiro central - atual (m)", 2, Right); Att("CANTEIRO_PROP", "Canteiro central - proposta (m)", 3, Right); }, split: 2, split2: 3);

            string[] grupoOp = { "Pista", "Calçada", "Ciclovia/ ciclofaixa" };
            string[][] tagsOp =
            {
                new[] { "PISTA_OPOSTA_ATUAL", "PISTA_OPOSTA_PROP" },
                new[] { "CALCADA_OPOSTA_ATUAL", "CALCADA_OPOSTA_PROP" },
                new[] { "CICLO_OPOSTA_ATUAL", "CICLO_OPOSTA_PROP" }
            };
            Group("Pista oposta ao PED", grupoOp, tagsOp);

            Row(c => { Label("Seção viária total", 0, span: 2, bold: true); Att("SECAO_TOTAL_ATUAL", "Secao viaria total - atual (m)", 2, Right, bold: true); Att("SECAO_TOTAL_PROP", "Secao viaria total - proposta (m)", 3, Right, bold: true); }, split: 2, split2: 3);

            // Complementares
            Row(c => Label("Informações complementares da proposta", 0, center: true, span: 4));
            Info("Modelo de abrigo proposto", "ABRIGO");
            Info("Faixa exclusiva", "FAIXA_EXCLUSIVA");
            Info("Ciclovia ou ciclofaixa", "CICLOVIA");
            Info("Alteração calçada PED", "ALT_CALCADA_PED");
            Info("Alteração do canteiro central", "ALT_CANTEIRO");
            Info("Alteração calçada oposta PED", "ALT_CALCADA_OPOSTA");
            Info("Embarques PM (passag.)", "EMBARQUES_PM");
            Info("Embarques PT (passag.)", "EMBARQUES_PT");
            Row(c => { Label("Distância do PED anterior (m)", 0, span: 2); Att("PED_ANTERIOR", "PED anterior", 2, Center); Att("DIST_ANTERIOR", "Distancia do PED anterior (m)", 3, Right); }, split: 2, split2: 3);
            Row(c => { Label("Distância do PED posterior (m)", 0, span: 2); Att("PED_POSTERIOR", "PED posterior", 2, Center); Att("DIST_POSTERIOR", "Distancia do PED posterior (m)", 3, Right); }, split: 2, split2: 3);
            Row(c => { Label("Obs:", 0); Att("OBS", "Obs.", 1, Left); }, split: 1);

            // Bordas laterais
            VLine(_x[0], top, _y);
            VLine(_x[4], top, _y);
        }

        private const int Left = 0, Center = 1, Right = 2;

        private void Group(string group, string[] items, string[][] tags)
        {
            double groupTop = _y;
            for (int i = 0; i < items.Length; i++)
            {
                double yMid = _y - RowH / 2.0;
                Text(items[i], _x[1] + Pad, yMid, AcadDb.AttachmentPoint.MiddleLeft, false);
                if (tags[i][0].Length > 0)
                    AttAt(tags[i][0], Prompt(group, items[i], false), _x[3] - Pad, yMid, AcadDb.AttachmentPoint.MiddleRight, false);
                AttAt(tags[i][1], Prompt(group, items[i], true), _x[4] - Pad, yMid, AcadDb.AttachmentPoint.MiddleRight, false);
                VLine(_x[1], _y, _y - RowH);
                VLine(_x[2], _y, _y - RowH);
                VLine(_x[3], _y, _y - RowH);
                _y -= RowH;
                HLine(i == items.Length - 1 ? _x[0] : _x[1], _x[4]);
            }
            Text(group, _x[0] + Pad, (groupTop + _y) / 2.0, AcadDb.AttachmentPoint.MiddleLeft, false);
        }

        private static string Prompt(string group, string item, bool proposta) =>
            $"{item} ({group}) - {(proposta ? "proposta" : "atual")} (m)";

        private void Info(string label, string tag)
        {
            Row(c => { Label(label, 0, span: 2); Att(tag, label, 2, Left); }, split: 2);
        }

        /// <summary>Uma linha da tabela: conteudo, divisorias verticais e linha de baixo.</summary>
        private void Row(Action<int> content, int split = -1, int split2 = -1)
        {
            content(0);
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

        private void Att(string tag, string prompt, int col, int align, bool bold = false)
        {
            double yMid = _y - RowH / 2.0;
            (double x, AcadDb.AttachmentPoint j) = align switch
            {
                Right => (_x[col + 1] - Pad, AcadDb.AttachmentPoint.MiddleRight),
                Center => ((_x[col] + _x[col + 1]) / 2.0, AcadDb.AttachmentPoint.MiddleCenter),
                _ => (_x[col] + Pad, AcadDb.AttachmentPoint.MiddleLeft)
            };
            AttAt(tag, prompt, x, yMid, j, bold);
        }

        private void AttAt(string tag, string prompt, double x, double y, AcadDb.AttachmentPoint j, bool bold)
        {
            AddAtt(_tr, _db, _btr, tag, prompt, string.Empty, LayerFicha, bold ? CellText * 1.15 : CellText, new Point3d(x, y, 0), j, invisible: false);
        }

        private void Text(string text, double x, double y, AcadDb.AttachmentPoint j, bool bold)
        {
            AcadDb.DBText t = new();
            t.SetDatabaseDefaults(_db);
            t.Layer = LayerFicha;
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
            l.Layer = LayerFicha;
            l.Color = AcadColor.FromColorIndex(ColorMethod.ByLayer, 256);
            _btr.AppendEntity(l);
            _tr.AddNewlyCreatedDBObject(l, true);
        }
    }

    private static void AddAtt(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockTableRecord btr, string tag, string prompt, string text,
                               string layer, double height, Point3d at, AcadDb.AttachmentPoint justify, bool invisible)
    {
        AcadDb.AttributeDefinition ad = new();
        ad.SetDatabaseDefaults(db);
        ad.Layer = layer;
        ad.Color = AcadColor.FromColorIndex(ColorMethod.ByLayer, 256);
        ad.Tag = tag;
        ad.Prompt = prompt;
        ad.TextString = text;
        ad.Height = height;
        ad.TextStyleId = db.Textstyle;
        ad.Invisible = invisible;
        ad.Position = at;
        ad.Justify = justify;
        if (justify != AcadDb.AttachmentPoint.BaseLeft)
            ad.AlignmentPoint = at;
        btr.AppendEntity(ad);
        tr.AddNewlyCreatedDBObject(ad, true);
        try
        {
            ad.AdjustAlignment(db);
        }
        catch
        {
            // Alinhamento cosmetico.
        }
    }

    /// <summary>
    /// Refaz os atributos de um bloco inserido a partir da definicao (como o ATTSYNC):
    /// necessario quando a definicao foi redesenhada. Os valores sao regravados depois por ApplyData.
    /// </summary>
    public static void ResyncAttributes(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockReference br, string code)
    {
        if (!br.IsWriteEnabled)
            br.UpgradeOpen();

        List<AcadDb.ObjectId> oldAtts = new();
        foreach (AcadDb.ObjectId attId in br.AttributeCollection)
            oldAtts.Add(attId);
        foreach (AcadDb.ObjectId attId in oldAtts)
            tr.GetObject(attId, AcadDb.OpenMode.ForWrite).Erase();

        AcadDb.BlockTableRecord def = (AcadDb.BlockTableRecord)tr.GetObject(DefinitionId(br), AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId entId in def)
        {
            if (tr.GetObject(entId, AcadDb.OpenMode.ForRead) is not AcadDb.AttributeDefinition ad || ad.Constant)
                continue;
            AcadDb.AttributeReference ar = new();
            ar.SetAttributeFromBlock(ad, br.BlockTransform);
            ar.TextString = AttributeValue(ad.Tag, ad.TextString, code);
            br.AttributeCollection.AppendAttribute(ar);
            tr.AddNewlyCreatedDBObject(ar, true);
            Align(ar, db);
        }
    }

    /// <summary>Definicao "de verdade" do bloco (em bloco dinamico, a do bloco e nao a representacao anonima).</summary>
    public static AcadDb.ObjectId DefinitionId(AcadDb.BlockReference br) =>
        br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;

    /// <summary>Insere o bloco centrado no ponto, na layer PED, com atributos e vinculo ao codigo.</summary>
    public static AcadDb.ObjectId Insert(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.ObjectId blockId, Point3d position, double scale, string code)
    {
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);
        AcadDb.BlockTableRecord def = (AcadDb.BlockTableRecord)tr.GetObject(blockId, AcadDb.OpenMode.ForRead);

        AcadDb.BlockReference br = new(position, blockId);
        br.SetDatabaseDefaults(db);
        br.Layer = LayerName;
        br.ScaleFactors = new Scale3d(scale);
        AcadDb.ObjectId id = ms.AppendEntity(br);
        tr.AddNewlyCreatedDBObject(br, true);

        if (def.HasAttributeDefinitions)
        {
            foreach (AcadDb.ObjectId entId in def)
            {
                if (tr.GetObject(entId, AcadDb.OpenMode.ForRead) is not AcadDb.AttributeDefinition ad || ad.Constant)
                    continue;

                AcadDb.AttributeReference ar = new();
                ar.SetAttributeFromBlock(ad, br.BlockTransform);
                ar.TextString = AttributeValue(ad.Tag, ad.TextString, code);
                br.AttributeCollection.AppendAttribute(ar);
                tr.AddNewlyCreatedDBObject(ar, true);
                Align(ar, db);
            }
        }

        PedStore.Tag(br, code);
        return id;
    }

    /// <summary>Reescreve os atributos de um bloco ja inserido (codigo pode ter mudado de caixa etc.).</summary>
    public static void UpdateAttributes(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockReference br, string code)
    {
        Dictionary<string, string> defaults = new(StringComparer.OrdinalIgnoreCase);
        AcadDb.BlockTableRecord def = (AcadDb.BlockTableRecord)tr.GetObject(DefinitionId(br), AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId entId in def)
        {
            if (tr.GetObject(entId, AcadDb.OpenMode.ForRead) is AcadDb.AttributeDefinition ad && !ad.Constant)
                defaults[ad.Tag] = ad.TextString;
        }

        foreach (AcadDb.ObjectId attId in br.AttributeCollection)
        {
            if (tr.GetObject(attId, AcadDb.OpenMode.ForRead) is not AcadDb.AttributeReference ar)
                continue;
            string value = AttributeValue(ar.Tag, defaults.TryGetValue(ar.Tag, out string? d) ? d : string.Empty, code);
            if (value == ar.TextString)
                continue;
            ar.UpgradeOpen();
            ar.TextString = value;
            Align(ar, db);
        }
    }

    /// <summary>
    /// Garante na definicao do bloco um atributo INVISIVEL para cada campo da ficha
    /// (tags que ja existirem ficam como estao). Assim os dados viajam no proprio bloco:
    /// quem abrir o DWG sem o plugin ve tudo no Properties, no duplo clique (EATTEDIT),
    /// no ATTOUT e na Data Extraction.
    /// </summary>
    public static int EnsureDataDefinitions(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.ObjectId blockId, IEnumerable<PedFicha.Attr> fields)
    {
        AcadDb.BlockTableRecord def = (AcadDb.BlockTableRecord)tr.GetObject(blockId, AcadDb.OpenMode.ForRead);
        HashSet<string> tags = new(StringComparer.OrdinalIgnoreCase);
        foreach (AcadDb.ObjectId entId in def)
        {
            if (tr.GetObject(entId, AcadDb.OpenMode.ForRead) is AcadDb.AttributeDefinition ad)
                tags.Add(ad.Tag);
        }

        int added = 0;
        double y = -Height / 2.0 - TextHeight;
        foreach (PedFicha.Attr f in fields)
        {
            if (tags.Contains(f.Tag))
                continue;

            if (!def.IsWriteEnabled)
                def.UpgradeOpen();

            AcadDb.AttributeDefinition ad = new();
            ad.SetDatabaseDefaults(db);
            ad.Layer = "0";
            ad.Color = AcadColor.FromColorIndex(ColorMethod.ByLayer, 256);
            ad.Tag = f.Tag;
            ad.Prompt = f.Prompt;
            ad.TextString = string.Empty;
            ad.Invisible = true;
            ad.Height = TextHeight * 0.5;
            ad.TextStyleId = db.Textstyle;
            ad.Position = new Point3d(-Width / 2.0, y, 0);
            def.AppendEntity(ad);
            tr.AddNewlyCreatedDBObject(ad, true);

            tags.Add(f.Tag);
            y -= TextHeight * 0.75;
            added++;
        }

        return added;
    }

    /// <summary>
    /// Escreve os campos da ficha nos atributos do bloco inserido (cria os que faltarem,
    /// em blocos inseridos antes de os atributos existirem) e o hyperlink para a foto.
    /// </summary>
    public static void ApplyData(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockReference br,
                                 IEnumerable<PedFicha.Attr> fields, string? hyperlink, string hyperlinkDescription)
    {
        Dictionary<string, AcadDb.AttributeReference> refs = new(StringComparer.OrdinalIgnoreCase);
        foreach (AcadDb.ObjectId attId in br.AttributeCollection)
        {
            if (tr.GetObject(attId, AcadDb.OpenMode.ForRead) is AcadDb.AttributeReference ar)
                refs[ar.Tag] = ar;
        }

        Dictionary<string, AcadDb.AttributeDefinition> defs = new(StringComparer.OrdinalIgnoreCase);
        AcadDb.BlockTableRecord def = (AcadDb.BlockTableRecord)tr.GetObject(DefinitionId(br), AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId entId in def)
        {
            if (tr.GetObject(entId, AcadDb.OpenMode.ForRead) is AcadDb.AttributeDefinition ad && !ad.Constant)
                defs[ad.Tag] = ad;
        }

        if (!br.IsWriteEnabled)
            br.UpgradeOpen();

        foreach (PedFicha.Attr f in fields)
        {
            string value = f.Value.Length > 1000 ? f.Value[..1000] : f.Value;

            if (refs.TryGetValue(f.Tag, out AcadDb.AttributeReference? ar))
            {
                if (ar.TextString == value)
                    continue;
                ar.UpgradeOpen();
                ar.TextString = value;
                continue;
            }

            if (!defs.TryGetValue(f.Tag, out AcadDb.AttributeDefinition? ad))
                continue;

            AcadDb.AttributeReference nar = new();
            nar.SetAttributeFromBlock(ad, br.BlockTransform);
            nar.TextString = value;
            br.AttributeCollection.AppendAttribute(nar);
            tr.AddNewlyCreatedDBObject(nar, true);
        }

        try
        {
            br.Hyperlinks.Clear();
            if (!string.IsNullOrEmpty(hyperlink))
            {
                br.Hyperlinks.Add(new AcadDb.HyperLink
                {
                    Name = hyperlink,
                    Description = hyperlinkDescription,
                    SubLocation = string.Empty
                });
            }
        }
        catch
        {
            // Hyperlink e conveniencia.
        }
    }

    // ------------------------------------------------------------------
    // Modelo (bloco dinamico feito pelo usuario no BEDIT)
    // ------------------------------------------------------------------

    /// <summary>DWG com o bloco PED modelo (gravado pelo PEDMODELO).</summary>
    public static string TemplatePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Civil3D2026Plugin", "PED_modelo.dwg");

    /// <summary>Se o desenho nao tem o bloco PED e existe o modelo, copia o bloco do modelo. Fora de transacao.</summary>
    public static bool ImportTemplateIfMissing(AcadDb.Database db)
    {
        string path = TemplatePath;
        if (!System.IO.File.Exists(path))
            return false;

        using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
            if (bt.Has(BlockName))
                return false;
        }

        using AcadDb.Database src = new(false, true);
        src.ReadDwgFile(path, AcadDb.FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
        AcadDb.ObjectId srcId;
        using (AcadDb.Transaction tr = src.TransactionManager.StartOpenCloseTransaction())
        {
            AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(src.BlockTableId, AcadDb.OpenMode.ForRead);
            if (!bt.Has(BlockName))
                return false;
            srcId = bt[BlockName];
        }

        AcadDb.IdMapping map = new();
        src.WblockCloneObjects(new AcadDb.ObjectIdCollection(new[] { srcId }), db.BlockTableId, map, AcadDb.DuplicateRecordCloning.Ignore, false);
        return true;
    }

    /// <summary>Grava o bloco PED do desenho (com a visibilidade criada no BEDIT) como modelo.</summary>
    public static string SaveTemplate(AcadDb.Database db)
    {
        AcadDb.ObjectId id;
        using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
            if (!bt.Has(BlockName))
                throw new InvalidOperationException($"O desenho nao tem o bloco {BlockName}.");
            id = bt[BlockName];
        }

        string path = TemplatePath;
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using AcadDb.Database dest = new(true, true);
        AcadDb.IdMapping map = new();
        db.WblockCloneObjects(new AcadDb.ObjectIdCollection(new[] { id }), dest.BlockTableId, map, AcadDb.DuplicateRecordCloning.Replace, false);
        dest.SaveAs(path, AcadDb.DwgVersion.Current);
        return path;
    }

    /// <summary>
    /// Troca o estado de visibilidade do bloco dinamico: "ponto" procura um estado com
    /// Ponto/Simbolo no nome (senao o primeiro); "ficha" procura Ficha (senao o ultimo).
    /// Retorna false se o bloco nao tem parametro de visibilidade.
    /// </summary>
    public static bool SetVisibility(AcadDb.BlockReference br, bool ficha)
    {
        if (!br.IsDynamicBlock)
            return false;

        foreach (AcadDb.DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
        {
            string n = PedFormat.Normalize(p.PropertyName);
            if (!n.StartsWith("visib", StringComparison.Ordinal) || p.ReadOnly)
                continue;

            List<string> values = p.GetAllowedValues().Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).ToList();
            if (values.Count == 0)
                return false;

            string? pick = ficha
                ? values.FirstOrDefault(v => PedFormat.Normalize(v).Contains("ficha", StringComparison.Ordinal)) ?? values[^1]
                : values.FirstOrDefault(v => PedFormat.Normalize(v) is string x && (x.Contains("ponto", StringComparison.Ordinal) || x.Contains("simbolo", StringComparison.Ordinal))) ?? values[0];

            if (!br.IsWriteEnabled)
                br.UpgradeOpen();
            p.Value = pick;
            return true;
        }

        return false;
    }

    /// <summary>Move o bloco (com os atributos) para o ponto.</summary>
    public static void MoveTo(AcadDb.BlockReference br, Point3d position)
    {
        Vector3d d = position - br.Position;
        if (d.Length > 1e-9)
            br.TransformBy(Matrix3d.Displacement(d));
    }

    public static string AttributeValue(string tag, string defaultText, string code)
    {
        if (defaultText.Contains("XX", StringComparison.OrdinalIgnoreCase))
            return defaultText.Replace("XX", code, StringComparison.OrdinalIgnoreCase);
        foreach (string t in CodeTags)
        {
            if (string.Equals(tag, t, StringComparison.OrdinalIgnoreCase))
                return code;
        }
        return defaultText;
    }

    private static void Align(AcadDb.AttributeReference ar, AcadDb.Database db)
    {
        try
        {
            if (ar.IsMTextAttribute)
                ar.UpdateMTextAttribute();
            else if (ar.Justify != AcadDb.AttachmentPoint.BaseLeft)
                ar.AdjustAlignment(db);
        }
        catch
        {
            // Alinhamento e cosmetico.
        }
    }

    private static AcadDb.Polyline Rectangle(double w, double h)
    {
        AcadDb.Polyline pl = new();
        Point2dCollection pts = Loop(w, h);
        for (int i = 0; i < 4; i++)
            pl.AddVertexAt(i, pts[i], 0, 0, 0);
        pl.Closed = true;
        return pl;
    }

    private static Point2dCollection Loop(double w, double h)
    {
        double x = w / 2.0, y = h / 2.0;
        return new Point2dCollection
        {
            new Point2d(-x, -y), new Point2d(x, -y), new Point2d(x, y), new Point2d(-x, y), new Point2d(-x, -y)
        };
    }

    private static DoubleCollection Bulges() => new() { 0, 0, 0, 0, 0 };
}
