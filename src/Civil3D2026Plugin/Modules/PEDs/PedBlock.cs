using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.Geometry;

using AcadColor = Autodesk.AutoCAD.Colors.Color;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>
/// Bloco "PED": placa de ponto de onibus (poste, placa azul com borda, quadro claro
/// com o pictograma do onibus de frente) e o codigo "PED XX" em vermelho na faixa azul.
/// Ao lado, os atributos N e E com as coordenadas do proprio bloco (campos: atualizam
/// sozinhos ao mover + REGEN, sem o plugin). O ponto de insercao e o pe do poste.
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

    public const string TagNorte = "N";
    public const string TagEste = "E";

    private const double TextHeight = 0.62;
    private const double CoordTextHeight = 0.45;
    private static readonly double Arc90 = Math.Tan(Math.PI / 8.0);

    // Placa (coordenadas do bloco, origem no pe do poste).
    private const double SignCx = 0.0, SignCy = 5.2, SignSize = 6.4;

    private static readonly string[] CodeTags = { "PED", "COD", "CODIGO", "CÓDIGO", "ID" };

    /// <summary>Versao do desenho do bloco gerado (XData no BTR). Sobe quando o layout muda.</summary>
    /// <remarks>3 = so o simbolo (a ficha virou multileader); 4 = placa de onibus + N/E.</remarks>
    private const int BlockVersion = 4;
    private const string BlockMark = "BLOCO";

    /// <summary>Ponto da borda direita da placa, em cima (coordenadas do bloco): onde chega a seta da ficha.</summary>
    public static Point3d LeaderAnchor => new(SignCx + SignSize / 2.0, SignCy + 2.0, 0);

    public static void EnsureLayer(AcadDb.Transaction tr, AcadDb.Database db) => EnsureLayer(tr, db, LayerName, 1);

    internal static AcadDb.ObjectId EnsureLayer(AcadDb.Transaction tr, AcadDb.Database db, string name, short aci)
    {
        AcadDb.LayerTable lt = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        if (lt.Has(name))
            return lt[name];

        lt.UpgradeOpen();
        AcadDb.LayerTableRecord layer = new()
        {
            Name = name,
            Color = AcadColor.FromColorIndex(ColorMethod.ByAci, aci)
        };
        AcadDb.ObjectId id = lt.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
        return id;
    }

    /// <summary>
    /// Id da definicao do bloco PED. Cria se nao existir; se existir e for o bloco gerado
    /// por versao anterior do plugin (1.0/1.1 ou a 1.2 com a ficha dentro), redesenha o conteudo
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
        AcadColor black = AcadColor.FromRgb(20, 20, 20);
        AcadColor white = AcadColor.FromRgb(255, 255, 255);
        AcadColor blue = AcadColor.FromRgb(26, 84, 140);
        AcadColor light = AcadColor.FromRgb(235, 235, 235);

        void Solid(AcadColor color, (Point2dCollection Pts, DoubleCollection Bulges) loop)
        {
            AcadDb.Hatch h = new();
            h.SetDatabaseDefaults(db);
            h.Layer = "0";
            h.Color = color;
            btr.AppendEntity(h);
            tr.AddNewlyCreatedDBObject(h, true);
            h.SetHatchPattern(AcadDb.HatchPatternType.PreDefined, "SOLID");
            h.Associative = false;
            h.AppendLoop(AcadDb.HatchLoopTypes.External | AcadDb.HatchLoopTypes.Polyline, loop.Pts, loop.Bulges);
            h.EvaluateHatch(true);
        }

        // Poste (atras da placa; a ponta arredondada aparece em cima).
        Solid(black, Loop(new[] { (-0.3, 0.0, 0.0), (0.3, 0.0, 0.0), (0.3, 8.75, 1.0), (-0.3, 8.75, 0.0) }));

        // Placa: contorno preto, filete branco, fundo azul, quadro claro.
        Solid(black, Rounded(SignCx, SignCy, SignSize, SignSize, 0.7));
        Solid(white, Rounded(SignCx, SignCy, 6.1, 6.1, 0.55));
        Solid(blue, Rounded(SignCx, SignCy, 5.7, 5.7, 0.45));
        Solid(light, Box(-2.1, 2.85, 2.1, 6.75, 0.35));

        // Onibus de frente.
        Solid(black, Box(-1.25, 3.45, 1.25, 6.05, 0.3));
        Solid(black, Box(-1.05, 3.1, -0.6, 3.5, 0.08));
        Solid(black, Box(0.6, 3.1, 1.05, 3.5, 0.08));
        Solid(black, Box(1.2, 5.75, 1.55, 5.83, 0.02));
        Solid(black, Box(-1.55, 5.75, -1.2, 5.83, 0.02));
        Solid(black, Box(1.45, 4.95, 1.7, 5.6, 0.08));
        Solid(black, Box(-1.7, 4.95, -1.45, 5.6, 0.08));
        Solid(light, Box(-0.55, 5.75, 0.55, 5.92, 0.06));
        Solid(light, Box(-1.05, 4.55, -0.05, 5.6, 0.08));
        Solid(light, Box(0.05, 4.55, 1.05, 5.6, 0.08));
        Solid(light, Circle(0.72, 4.05, 0.17));
        Solid(light, Circle(-0.72, 4.05, 0.17));
        Solid(light, Box(-0.35, 3.98, 0.35, 4.12, 0.05));
        foreach ((double x1, double y1, double x2, double y2) in new[] { (-0.8, 4.75, -0.5, 5.05), (-0.65, 4.75, -0.35, 5.05) })
        {
            AcadDb.Line l = new(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0));
            l.SetDatabaseDefaults(db);
            l.Layer = "0";
            l.Color = black;
            btr.AppendEntity(l);
            tr.AddNewlyCreatedDBObject(l, true);
        }

        // Codigo na faixa azul, em vermelho.
        AcadDb.AttributeDefinition code = AddAtt(tr, db, btr, AttTag, "Codigo do PED", "PED XX", "0", TextHeight,
            new Point3d(SignCx, 7.4, 0), AcadDb.AttachmentPoint.MiddleCenter, invisible: false);
        code.Color = AcadColor.FromColorIndex(ColorMethod.ByAci, 1);

        // Coordenadas ao lado da placa (valores sao campos do proprio bloco, ver SetCoordinateFields).
        double xl = SignCx + SignSize / 2.0 + 0.4;
        foreach ((string label, string tag, double y) in new[] { ("N:", TagNorte, 5.6), ("E:", TagEste, 4.9) })
        {
            AcadDb.DBText t = new();
            t.SetDatabaseDefaults(db);
            t.Layer = "0";
            t.Color = AcadColor.FromColorIndex(ColorMethod.ByAci, 7);
            t.TextStyleId = db.Textstyle;
            t.Height = CoordTextHeight;
            t.TextString = label;
            t.Position = new Point3d(xl, y, 0);
            t.Justify = AcadDb.AttachmentPoint.MiddleLeft;
            t.AlignmentPoint = new Point3d(xl, y, 0);
            btr.AppendEntity(t);
            tr.AddNewlyCreatedDBObject(t, true);
            try { t.AdjustAlignment(db); } catch { }

            AcadDb.AttributeDefinition ad = AddAtt(tr, db, btr, tag, label == "N:" ? "Coordenada N" : "Coordenada E", string.Empty, "0",
                CoordTextHeight, new Point3d(xl + 0.55, y, 0), AcadDb.AttachmentPoint.MiddleLeft, invisible: false);
            ad.Color = AcadColor.FromColorIndex(ColorMethod.ByAci, 7);
        }
    }

    /// <summary>
    /// N e E do bloco como campos (AcObjProp InsertionPoint do proprio bloco): acompanham o bloco
    /// ao mover, atualizando no REGEN/salvar, sem o plugin. Se o campo falhar, grava o valor fixo.
    /// </summary>
    public static void SetCoordinateFields(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockReference br)
    {
        string objId = br.ObjectId.OldIdPtr.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (AcadDb.ObjectId attId in br.AttributeCollection)
        {
            if (tr.GetObject(attId, AcadDb.OpenMode.ForRead) is not AcadDb.AttributeReference ar)
                continue;
            int pt = ar.Tag.Equals(TagNorte, StringComparison.OrdinalIgnoreCase) ? 2
                   : ar.Tag.Equals(TagEste, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (pt == 0)
                continue;

            ar.UpgradeOpen();
            try
            {
                AcadDb.Field field = new("%<\\AcObjProp Object(%<\\_ObjId " + objId + ">%).InsertionPoint \\f \"%lu2%pt" + pt + "%pr3%ds44\">%");
                field.Evaluate();
                ar.SetField(field);
                tr.AddNewlyCreatedDBObject(field, true);
            }
            catch
            {
                double v = pt == 2 ? br.Position.Y : br.Position.X;
                ar.TextString = v.ToString("0.000", PedFormat.PtBr);
            }
            Align(ar, db);
        }
    }

    private static AcadDb.AttributeDefinition AddAtt(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockTableRecord btr, string tag, string prompt, string text,
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
        return ad;
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
        SetCoordinateFields(tr, db, br);
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
        SetCoordinateFields(tr, db, br);
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
            if (tr.GetObject(attId, AcadDb.OpenMode.ForRead) is not AcadDb.AttributeReference ar || IsCoordinateTag(ar.Tag))
                continue;
            string value = AttributeValue(ar.Tag, defaults.TryGetValue(ar.Tag, out string? d) ? d : string.Empty, code);
            if (value == ar.TextString)
                continue;
            ar.UpgradeOpen();
            ar.TextString = value;
            Align(ar, db);
        }
    }

    /// <summary>Hyperlink do bloco para a foto do local (Ctrl+clique abre).</summary>
    public static void SetHyperlink(AcadDb.BlockReference br, string? path, string description)
    {
        try
        {
            if (!br.IsWriteEnabled)
                br.UpgradeOpen();
            br.Hyperlinks.Clear();
            if (!string.IsNullOrEmpty(path))
                br.Hyperlinks.Add(new AcadDb.HyperLink { Name = path, Description = description, SubLocation = string.Empty });
        }
        catch
        {
            // Hyperlink e conveniencia.
        }
    }

    public static bool IsCoordinateTag(string tag) =>
        tag.Equals(TagNorte, StringComparison.OrdinalIgnoreCase) || tag.Equals(TagEste, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Contorno fechado (x, y, bulge do segmento que sai do vertice).</summary>
    private static (Point2dCollection, DoubleCollection) Loop(IEnumerable<(double X, double Y, double Bulge)> vertices)
    {
        Point2dCollection pts = new();
        DoubleCollection bulges = new();
        List<(double X, double Y, double Bulge)> v = vertices.ToList();
        foreach ((double x, double y, double b) in v)
        {
            pts.Add(new Point2d(x, y));
            bulges.Add(b);
        }
        pts.Add(new Point2d(v[0].X, v[0].Y));
        bulges.Add(0);
        return (pts, bulges);
    }

    /// <summary>Retangulo de cantos arredondados entre (x0,y0) e (x1,y1).</summary>
    private static (Point2dCollection, DoubleCollection) Box(double x0, double y0, double x1, double y1, double r)
    {
        if (r <= 1e-9)
            return Loop(new[] { (x0, y0, 0.0), (x1, y0, 0.0), (x1, y1, 0.0), (x0, y1, 0.0) });
        return Loop(new[]
        {
            (x0 + r, y0, 0.0), (x1 - r, y0, Arc90), (x1, y0 + r, 0.0), (x1, y1 - r, Arc90),
            (x1 - r, y1, 0.0), (x0 + r, y1, Arc90), (x0, y1 - r, 0.0), (x0, y0 + r, Arc90)
        });
    }

    private static (Point2dCollection, DoubleCollection) Rounded(double cx, double cy, double w, double h, double r) =>
        Box(cx - w / 2.0, cy - h / 2.0, cx + w / 2.0, cy + h / 2.0, r);

    private static (Point2dCollection, DoubleCollection) Circle(double cx, double cy, double r) =>
        Loop(new[] { (cx + r, cy, 1.0), (cx - r, cy, 1.0) });
}
