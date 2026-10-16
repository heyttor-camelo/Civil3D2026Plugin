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

    /// <summary>Versao do desenho do bloco gerado (XData no BTR). Sobe quando o layout muda.</summary>
    /// <remarks>3 = so o simbolo (a ficha virou multileader em PedFichaBlock).</remarks>
    private const int BlockVersion = 3;
    private const string BlockMark = "BLOCO";

    /// <summary>Ponto da borda direita do simbolo (coordenadas do bloco): onde chega a seta da ficha.</summary>
    public static Point3d LeaderAnchor => new(Width / 2.0, 0, 0);

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
