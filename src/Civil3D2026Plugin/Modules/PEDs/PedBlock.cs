using System;
using System.Collections.Generic;
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

    public static void EnsureLayer(AcadDb.Transaction tr, AcadDb.Database db)
    {
        AcadDb.LayerTable lt = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        if (lt.Has(LayerName))
            return;

        lt.UpgradeOpen();
        AcadDb.LayerTableRecord layer = new()
        {
            Name = LayerName,
            Color = AcadColor.FromColorIndex(ColorMethod.ByAci, 1)
        };
        lt.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    /// <summary>Id da definicao do bloco PED (cria se nao existir).</summary>
    public static AcadDb.ObjectId EnsureBlock(AcadDb.Transaction tr, AcadDb.Database db, out bool created)
    {
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        created = false;
        if (bt.Has(BlockName))
            return bt[BlockName];

        bt.UpgradeOpen();
        AcadDb.BlockTableRecord btr = new() { Name = BlockName, Origin = Point3d.Origin };
        AcadDb.ObjectId id = bt.Add(btr);
        tr.AddNewlyCreatedDBObject(btr, true);
        created = true;

        AcadColor byLayer = AcadColor.FromColorIndex(ColorMethod.ByLayer, 256);

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

        AcadDb.AttributeDefinition ad = new();
        ad.SetDatabaseDefaults(db);
        ad.Layer = "0";
        ad.Color = byLayer;
        ad.Tag = AttTag;
        ad.Prompt = "Codigo do PED";
        ad.TextString = "PED XX";
        ad.Height = TextHeight;
        ad.TextStyleId = db.Textstyle;
        ad.Position = Point3d.Origin;
        ad.Justify = AcadDb.AttachmentPoint.MiddleCenter;
        ad.AlignmentPoint = Point3d.Origin;
        btr.AppendEntity(ad);
        tr.AddNewlyCreatedDBObject(ad, true);

        return id;
    }

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
        AcadDb.BlockTableRecord def = (AcadDb.BlockTableRecord)tr.GetObject(br.BlockTableRecord, AcadDb.OpenMode.ForRead);
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
        AcadDb.BlockTableRecord def = (AcadDb.BlockTableRecord)tr.GetObject(br.BlockTableRecord, AcadDb.OpenMode.ForRead);
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
