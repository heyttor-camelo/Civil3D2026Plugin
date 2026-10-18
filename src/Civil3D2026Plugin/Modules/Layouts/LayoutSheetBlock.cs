using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Definicao de uma prancha (ABNT NBR 10068, em mm).</summary>
internal sealed record SheetFormat(string Nome, double Largura, double Altura)
{
    public static readonly SheetFormat[] Padrao =
    {
        new("A0", 1189, 841), new("A1", 841, 594), new("A2", 594, 420), new("A3", 420, 297), new("A4", 297, 210)
    };

    /// <summary>Margens NBR 10068: esquerda (encadernacao) e demais.</summary>
    public (double Esquerda, double Outras) Margens => Nome switch
    {
        "A0" or "A1" => (25, 10),
        "A2" => (25, 7),
        _ => (25, 7)
    };
}

/// <summary>Opcoes do bloco de prancha.</summary>
internal sealed class SheetBlockOptions
{
    public string Formato { get; set; } = "A1";
    public double Largura { get; set; } = 841;      // usado quando Formato = "Personalizado"
    public double Altura { get; set; } = 594;
    public int Estendimentos { get; set; }           // quantos modulos a mais na largura
    public double Estendimento { get; set; } = 210;  // largura de cada modulo (A4 = 210)
    public double MargemEsquerda { get; set; } = 25;
    public double MargemOutras { get; set; } = 10;
    public bool MarcasDeDobra { get; set; } = true;
    public bool Retrato { get; set; }
    public string Layer { get; set; } = "PRANCHA";

    public double LarguraTotal => (Formato == "Personalizado" ? Largura : Base().Largura) + Estendimentos * Estendimento;
    public double AlturaTotal => Formato == "Personalizado" ? Altura : Base().Altura;

    public SheetFormat Base() => Array.Find(SheetFormat.Padrao, f => f.Nome == Formato) ?? new SheetFormat("Personalizado", Largura, Altura);

    public string NomeBloco()
    {
        string tam = Formato == "Personalizado" ? $"{Largura:0}x{Altura:0}" : Formato;
        return $"PRANCHA_{tam}" + (Estendimentos > 0 ? $"_E{Estendimentos}" : "") + (Retrato ? "_R" : "");
    }
}

/// <summary>
/// Insere um bloco de prancha (contorno do papel, margem e marcas de dobra) no espaco atual,
/// na origem ou num ponto clicado. O bloco e refeito se ja existir com o mesmo nome.
/// </summary>
internal static class LayoutSheetBlock
{
    public static OpResult Inserir(Document doc, SheetBlockOptions o, Point3d ponto)
    {
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using Transaction tr = db.TransactionManager.StartTransaction();

        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        string nome = o.NomeBloco();
        BlockTableRecord btr;
        if (bt.Has(nome))
        {
            btr = (BlockTableRecord)tr.GetObject(bt[nome], OpenMode.ForWrite);
            foreach (ObjectId id in btr)
                tr.GetObject(id, OpenMode.ForWrite).Erase();
        }
        else
        {
            bt.UpgradeOpen();
            btr = new BlockTableRecord { Name = nome, Origin = Point3d.Origin };
            bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
        }

        double w = o.Retrato ? o.AlturaTotal : o.LarguraTotal;
        double h = o.Retrato ? o.LarguraTotal : o.AlturaTotal;
        GarantirLayer(tr, db, o.Layer);

        // Contorno do papel.
        Add(tr, btr, Retangulo(0, 0, w, h, o.Layer, 0.0));
        // Margem (quadro): esquerda maior para encadernacao.
        Add(tr, btr, Retangulo(o.MargemEsquerda, o.MargemOutras, w - o.MargemOutras, h - o.MargemOutras, o.Layer, 0.5));

        if (o.MarcasDeDobra && !o.Retrato)
        {
            // Dobras: modulos de 210 (A4) a partir da direita; ultima dobra com 190/185 fica a criterio do usuario.
            double x = w;
            while (x - 210 > o.MargemEsquerda + 10)
            {
                x -= 210;
                Add(tr, btr, Linha(x, 0, x, o.MargemOutras, o.Layer));
                Add(tr, btr, Linha(x, h - o.MargemOutras, x, h, o.Layer));
            }
            // Dobra horizontal no meio (altura > 297).
            if (h > 297 + 1e-6)
            {
                double y = 297;
                Add(tr, btr, Linha(0, y, o.MargemEsquerda, y, o.Layer));
                Add(tr, btr, Linha(w - o.MargemOutras, y, w, y, o.Layer));
            }
        }

        // Insercao no espaco atual.
        var espaco = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
        var br = new BlockReference(ponto, btr.ObjectId) { Layer = o.Layer };
        espaco.AppendEntity(br);
        tr.AddNewlyCreatedDBObject(br, true);
        tr.Commit();

        return new OpResult(1, 0, $"Prancha {nome} ({w:0} x {h:0} mm) inserida em {ponto.X.ToString("0.##", CultureInfo.InvariantCulture)},{ponto.Y.ToString("0.##", CultureInfo.InvariantCulture)}.");
    }

    private static Polyline Retangulo(double x0, double y0, double x1, double y1, string layer, double espessura)
    {
        var pl = new Polyline { Closed = true, Layer = layer };
        pl.AddVertexAt(0, new Point2d(x0, y0), 0, espessura, espessura);
        pl.AddVertexAt(1, new Point2d(x1, y0), 0, espessura, espessura);
        pl.AddVertexAt(2, new Point2d(x1, y1), 0, espessura, espessura);
        pl.AddVertexAt(3, new Point2d(x0, y1), 0, espessura, espessura);
        return pl;
    }

    private static Line Linha(double x0, double y0, double x1, double y1, string layer) =>
        new(new Point3d(x0, y0, 0), new Point3d(x1, y1, 0)) { Layer = layer };

    private static void Add(Transaction tr, BlockTableRecord btr, Entity e)
    {
        btr.AppendEntity(e);
        tr.AddNewlyCreatedDBObject(e, true);
    }

    private static void GarantirLayer(Transaction tr, Database db, string nome)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(nome))
            return;
        lt.UpgradeOpen();
        var ltr = new LayerTableRecord { Name = nome, Color = AcColor.FromColorIndex(AcColorMethod.ByAci, 7) };
        lt.Add(ltr);
        tr.AddNewlyCreatedDBObject(ltr, true);
    }
}
