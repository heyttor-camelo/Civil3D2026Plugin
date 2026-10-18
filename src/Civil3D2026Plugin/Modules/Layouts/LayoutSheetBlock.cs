using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    /// <summary>Quantidade e modulo de estendimento guardados por formato (A0..A4, Personalizado).</summary>
    public Dictionary<string, (int N, double Modulo)> PorFormato { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Guarda os estendimentos atuais no formato atual.</summary>
    public void GuardarFormato() => PorFormato[Formato] = (Estendimentos, Estendimento);

    /// <summary>Carrega os estendimentos guardados do formato dado (ou mantem os atuais).</summary>
    public void AplicarFormato(string formato)
    {
        Formato = formato;
        if (PorFormato.TryGetValue(formato, out (int N, double Modulo) e))
        {
            Estendimentos = e.N;
            Estendimento = e.Modulo;
        }
    }

    /// <summary>Carrega as preferencias gravadas (prancha_<formato>=n;modulo).</summary>
    public static SheetBlockOptions Carregar()
    {
        var o = new SheetBlockOptions
        {
            Formato = LayoutSettings.Get("prancha_formato", "A1"),
            Largura = LayoutSettings.GetDouble("prancha_largura", 841),
            Altura = LayoutSettings.GetDouble("prancha_altura", 594),
            MargemEsquerda = LayoutSettings.GetDouble("prancha_margem_esq", 25),
            MargemOutras = LayoutSettings.GetDouble("prancha_margem", 10),
            MarcasDeDobra = LayoutSettings.GetBool("prancha_dobras", true),
            Retrato = LayoutSettings.GetBool("prancha_retrato", false),
            Layer = LayoutSettings.Get("prancha_layer", "PRANCHA")
        };
        foreach (string f in SheetFormat.Padrao.Select(x => x.Nome).Append("Personalizado"))
        {
            string[] partes = LayoutSettings.Get("prancha_" + f, "").Split(';');
            if (partes.Length == 2 && int.TryParse(partes[0], out int n) &&
                double.TryParse(partes[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0)
                o.PorFormato[f] = (n, m);
        }
        o.AplicarFormato(o.Formato);
        return o;
    }

    public void Salvar()
    {
        GuardarFormato();
        LayoutSettings.Set("prancha_formato", Formato);
        LayoutSettings.Set("prancha_largura", Largura);
        LayoutSettings.Set("prancha_altura", Altura);
        LayoutSettings.Set("prancha_margem_esq", MargemEsquerda);
        LayoutSettings.Set("prancha_margem", MargemOutras);
        LayoutSettings.Set("prancha_dobras", MarcasDeDobra);
        LayoutSettings.Set("prancha_retrato", Retrato);
        LayoutSettings.Set("prancha_layer", Layer);
        foreach ((string f, (int n, double m)) in PorFormato)
            LayoutSettings.Set("prancha_" + f, $"{n};{m.ToString("0.##", CultureInfo.InvariantCulture)}");
    }

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
    /// <summary>Insere no espaco atual.</summary>
    public static OpResult Inserir(Document doc, SheetBlockOptions o, Point3d ponto) =>
        Inserir(doc, o, ponto, new[] { doc.Database.CurrentSpaceId });

    /// <summary>Insere nos layouts dados (pelo nome), sem precisar abri-los.</summary>
    public static OpResult InserirEmLayouts(Document doc, SheetBlockOptions o, Point3d ponto, IEnumerable<string> layouts)
    {
        var espacos = new List<ObjectId>();
        Database db = doc.Database;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (string nome in layouts)
                if (dic.Contains(nome))
                    espacos.Add(((Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(dic.GetAt(nome), OpenMode.ForRead)).BlockTableRecordId);
            tr.Commit();
        }
        if (espacos.Count == 0)
            return new OpResult(0, 0, "Nenhum layout marcado para receber a prancha.");
        return Inserir(doc, o, ponto, espacos);
    }

    public static OpResult Inserir(Document doc, SheetBlockOptions o, Point3d ponto, IReadOnlyList<ObjectId> espacos)
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
                tr.GetObject(id, OpenMode.ForWrite, false, true).Erase();
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

        foreach (ObjectId espacoId in espacos)
        {
            var espaco = (BlockTableRecord)tr.GetObject(espacoId, OpenMode.ForWrite);
            var br = new BlockReference(ponto, btr.ObjectId) { Layer = o.Layer };
            espaco.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);
        }
        tr.Commit();

        string onde = espacos.Count == 1 ? "" : $" em {espacos.Count} layouts";
        return new OpResult(espacos.Count, 0, $"Prancha {nome} ({w:0} x {h:0} mm) inserida{onde} em {ponto.X.ToString("0.##", CultureInfo.InvariantCulture)},{ponto.Y.ToString("0.##", CultureInfo.InvariantCulture)}.");
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
