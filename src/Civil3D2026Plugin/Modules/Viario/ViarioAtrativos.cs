using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AcColors = Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.Modules.PEDs;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>Ponto de interesse (atrativo turistico) vindo de um KML/KMZ.</summary>
internal sealed class AtrativoItem
{
    public string Nome { get; set; } = "";
    /// <summary>Pasta do KML (Levantamento, Patrimonio Historico, Parque Nacional...).</summary>
    public string Categoria { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double E { get; set; }
    public double N { get; set; }
    /// <summary>Raio do circulo no desenho (m) = escala do bloco.</summary>
    public double Raio { get; set; } = 25.0;
    public string Fonte { get; set; } = "";

    public V2 Ponto => new(E, N);

    private const string Sep = "~|~";

    public string Serialize() => string.Join(Sep, new[]
    {
        Nome, Categoria, Lat.ToString("0.#######", CultureInfo.InvariantCulture), Lon.ToString("0.#######", CultureInfo.InvariantCulture),
        E.ToString("0.###", CultureInfo.InvariantCulture), N.ToString("0.###", CultureInfo.InvariantCulture),
        Raio.ToString("0.##", CultureInfo.InvariantCulture), Fonte
    });

    public static AtrativoItem? Deserialize(string text)
    {
        string[] f = text.Split(Sep);
        if (f.Length < 7)
            return null;
        return new AtrativoItem
        {
            Nome = f[0], Categoria = f[1], Lat = D(f[2]), Lon = D(f[3]), E = D(f[4]), N = D(f[5]), Raio = D(f[6]),
            Fonte = f.Length > 7 ? f[7] : ""
        };
    }

    private static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;
}

/// <summary>
/// Atrativos turisticos (pontos de um KMZ da prefeitura) no desenho: bloco VIARIO-ATRATIVO
/// (circulo unitario com hachura solida transparente) inserido com escala = raio, e os
/// atributos NOME (visivel), CATEGORIA, LAT, LON e FONTE (invisiveis). XData C3DVIARIO /
/// ATRATIVO para o painel. Mudar a escala do bloco muda o raio do circulo.
/// </summary>
internal static class ViarioAtrativos
{
    public const string Modulo = "VIARIO.ATRATIVOS";
    public const string Tag = "ATRATIVO";
    public const string BlockName = "VIARIO-ATRATIVO";
    public const string Layer = "VIARIO-ATRATIVOS";
    public const string LayerText = "VIARIO-ATRATIVOS-NOME";

    // ------------------------------------------------------------------
    // KML / KMZ
    // ------------------------------------------------------------------

    /// <summary>Placemarks com ponto; a pasta vira a categoria; coordenadas DMS no fim do nome sao tiradas.</summary>
    public static List<AtrativoItem> ReadKml(string path)
    {
        XDocument doc;
        if (string.Equals(Path.GetExtension(path), ".kmz", StringComparison.OrdinalIgnoreCase))
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using ZipArchive zip = new(fs, ZipArchiveMode.Read);
            ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("doc.kml", StringComparison.OrdinalIgnoreCase))
                                    ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
                                    ?? throw new InvalidDataException("O KMZ nao tem nenhum arquivo .kml dentro.");
            using Stream s = entry.Open();
            doc = XDocument.Load(s);
        }
        else
        {
            doc = XDocument.Load(path);
        }

        List<AtrativoItem> list = new();
        string fonte = Path.GetFileName(path);
        foreach (XElement pm in doc.Descendants().Where(e => e.Name.LocalName == "Placemark"))
        {
            XElement? coords = pm.Descendants().FirstOrDefault(e => e.Name.LocalName == "Point")?
                .Descendants().FirstOrDefault(e => e.Name.LocalName == "coordinates");
            if (coords is null)
                continue;
            string[] parts = coords.Value.Trim().Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).First().Split(',');
            if (parts.Length < 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
                continue;

            string name = Child(pm, "name")?.Value.Trim() ?? "";
            name = CleanName(name);
            if (name.Length == 0)
                name = $"Ponto {list.Count + 1}";

            // Categoria = pasta mais proxima.
            string categoria = "";
            for (XElement? up = pm.Parent; up is not null; up = up.Parent)
            {
                if (up.Name.LocalName == "Folder")
                {
                    categoria = Child(up, "name")?.Value.Trim() ?? "";
                    break;
                }
            }

            list.Add(new AtrativoItem { Nome = name, Categoria = categoria, Lat = lat, Lon = lon, Fonte = fonte });
        }

        return list;
    }

    /// <summary>Novo atrativo clicando no desenho: nome, categoria e raio; bloco em vermelho (ponto levantado a mao).</summary>
    private static void NewByClick(Document doc)
    {
        Editor ed = doc.Editor;
        int zone = 21;
        bool south = true;
        try
        {
            string code = Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument.Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode ?? "";
            if (Civil3D2026Plugin.Modules.Quadros.GeoConversion.TryParseZone(code, out int z, out bool s))
            {
                zone = z;
                south = s;
            }
        }
        catch
        {
            // sem sistema de coordenadas: fuso padrao
        }

        double raio = 25.0;
        List<AtrativoItem> existentes = Read(doc.Database);
        if (existentes.Count > 0)
            raio = existentes[0].Raio;

        while (true)
        {
            PromptPointOptions po = new("\nPonto do atrativo (Enter termina): ") { AllowNone = true };
            PromptPointResult pr = ed.GetPoint(po);
            if (pr.Status != PromptStatus.OK)
                return;
            Point3d w = pr.Value.TransformBy(ed.CurrentUserCoordinateSystem);

            PromptStringOptions no = new("\nNome: ") { AllowSpaces = true };
            PromptResult nr = ed.GetString(no);
            if (nr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(nr.StringResult))
                return;
            PromptStringOptions co = new("\nCategoria <Levantamento em campo>: ") { AllowSpaces = true };
            PromptResult cr = ed.GetString(co);
            if (cr.Status == PromptStatus.Cancel)
                return;
            string categoria = string.IsNullOrWhiteSpace(cr.StringResult) ? "Levantamento em campo" : cr.StringResult.Trim();
            PromptDoubleOptions ro = new($"\nRaio (m) <{raio.ToString("0.#", CultureInfo.InvariantCulture)}>: ") { AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = raio, UseDefaultValue = true };
            PromptDoubleResult rr = ed.GetDouble(ro);
            if (rr.Status == PromptStatus.Cancel)
                return;
            if (rr.Status == PromptStatus.OK)
                raio = rr.Value;

            (double lat, double lon) = Civil3D2026Plugin.Modules.Quadros.GeoConversion.UtmToGeographic(w.X, w.Y, zone, south);
            AtrativoItem a = new() { Nome = nr.StringResult.Trim(), Categoria = categoria, E = w.X, N = w.Y, Lat = lat, Lon = lon, Fonte = "clique no desenho" };
            Insert(doc, new[] { a }, raio, 6, corBloco: 1);
            ed.WriteMessage($"\n  {a.Nome} inserido em E {w.X:0.00} N {w.Y:0.00} (vermelho).");
            ViarioPanel.NotifyChanged(doc);
        }
    }

    private static XElement? Child(XElement e, string local) => e.Elements().FirstOrDefault(c => c.Name.LocalName == local);

    /// <summary>Tira coordenadas em graus/minutos/segundos coladas no nome ("Alto do Ceu 15°28'58.22"S 55°49'2.32"O").</summary>
    public static string CleanName(string name)
    {
        string s = Regex.Replace(name, @"\s*\d{1,3}\s*°\s*\d{1,2}\s*'\s*[\d.,]+\s*""?\s*[NSOWEL]\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim().TrimEnd(',', ';', '-');
        return s;
    }

    // ------------------------------------------------------------------
    // Desenho
    // ------------------------------------------------------------------

    public static List<AtrativoItem> Read(AcadDb.Database db)
    {
        List<AtrativoItem> list = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        foreach ((AtrativoItem a, _) in Scan(tr, db))
            list.Add(a);
        return list.OrderBy(a => a.Categoria, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Nome, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<(AtrativoItem Item, AcadDb.BlockReference Ref)> Scan(AcadDb.Transaction tr, AcadDb.Database db)
    {
        List<(AtrativoItem, AcadDb.BlockReference)> list = new();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (id.IsErased || id.ObjectClass.DxfName != "INSERT")
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.BlockReference br)
                continue;
            List<string>? values = ViarioStore.ReadValues(br);
            if (values is null || values.Count < 2 || values[0] != Tag)
                continue;
            AtrativoItem? a = AtrativoItem.Deserialize(string.Concat(values.Skip(1)));
            if (a is null)
                continue;
            // Posicao e raio atuais vem do bloco (o usuario pode ter movido ou mudado a escala).
            a.E = br.Position.X;
            a.N = br.Position.Y;
            a.Raio = Math.Abs(br.ScaleFactors.X);
            list.Add((a, br));
        }

        return list;
    }

    private static AcadDb.ObjectId EnsureBlock(AcadDb.Transaction tr, AcadDb.Database db)
    {
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        if (bt.Has(BlockName))
            return bt[BlockName];

        bt.UpgradeOpen();
        AcadDb.BlockTableRecord btr = new() { Name = BlockName, Origin = Point3d.Origin };
        AcadDb.ObjectId id = bt.Add(btr);
        tr.AddNewlyCreatedDBObject(btr, true);

        AcadDb.Circle c = new(Point3d.Origin, Vector3d.ZAxis, 1.0);
        c.SetDatabaseDefaults(db);
        c.Layer = "0";
        c.ColorIndex = 0; // ByBlock
        btr.AppendEntity(c);
        tr.AddNewlyCreatedDBObject(c, true);

        AcadDb.Hatch h = new();
        h.SetDatabaseDefaults(db);
        h.Layer = "0";
        h.ColorIndex = 0;
        btr.AppendEntity(h);
        tr.AddNewlyCreatedDBObject(h, true);
        h.SetHatchPattern(AcadDb.HatchPatternType.PreDefined, "SOLID");
        h.AppendLoop(AcadDb.HatchLoopTypes.Outermost, new AcadDb.ObjectIdCollection { c.ObjectId });
        h.EvaluateHatch(true);
        h.Transparency = new AcColors.Transparency((byte)Math.Round(255.0 * 0.30)); // 70% transparente

        // Centro.
        AcadDb.Circle dot = new(Point3d.Origin, Vector3d.ZAxis, 0.06);
        dot.SetDatabaseDefaults(db);
        dot.Layer = "0";
        dot.ColorIndex = 0;
        btr.AppendEntity(dot);
        tr.AddNewlyCreatedDBObject(dot, true);

        // Nome visivel acima do circulo (altura 0,35 do raio, acompanha a escala), demais invisiveis.
        AddAtt(tr, db, btr, "NOME", new Point3d(0.0, 1.15, 0.0), 0.35, false);
        AddAtt(tr, db, btr, "CATEGORIA", new Point3d(0.0, -1.5, 0.0), 0.25, true);
        AddAtt(tr, db, btr, "LAT", new Point3d(0.0, -1.8, 0.0), 0.25, true);
        AddAtt(tr, db, btr, "LON", new Point3d(0.0, -2.1, 0.0), 0.25, true);
        AddAtt(tr, db, btr, "FONTE", new Point3d(0.0, -2.4, 0.0), 0.25, true);
        return id;
    }

    private static void AddAtt(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockTableRecord btr, string tag, Point3d at, double height, bool invisible)
    {
        AcadDb.AttributeDefinition ad = new();
        ad.SetDatabaseDefaults(db);
        ad.Layer = LayerText;
        ad.Tag = tag;
        ad.Prompt = tag;
        ad.TextString = "";
        ad.Height = height;
        ad.TextStyleId = db.Textstyle;
        ad.Invisible = invisible;
        ad.Position = at;
        ad.Justify = AcadDb.AttachmentPoint.BottomCenter;
        ad.AlignmentPoint = at;
        ad.LockPositionInBlock = false;
        btr.AppendEntity(ad);
        tr.AddNewlyCreatedDBObject(ad, true);
        try
        {
            ad.AdjustAlignment(db);
        }
        catch
        {
            // cosmetico
        }
    }

    /// <summary>Insere os atrativos (ja com E/N). Retorna quantos entraram. <paramref name="corBloco"/> > 0 pinta o bloco (ex.: 1 = vermelho).</summary>
    public static int Insert(Document doc, IEnumerable<AtrativoItem> items, double raio, short cor, short corBloco = 0)
    {
        AcadDb.Database db = doc.Database;
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        ViarioStore.EnsureRegApp(tr, db);
        ViarioStore.EnsureLayer(tr, db, Layer, cor);
        ViarioStore.EnsureLayer(tr, db, LayerText, 7);
        AcadDb.ObjectId blockId = EnsureBlock(tr, db);
        AcadDb.BlockTableRecord def = (AcadDb.BlockTableRecord)tr.GetObject(blockId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

        foreach (AtrativoItem a in items)
        {
            a.Raio = raio;
            AcadDb.BlockReference br = new(new Point3d(a.E, a.N, 0.0), blockId) { ScaleFactors = new Scale3d(raio) };
            br.SetDatabaseDefaults(db);
            br.Layer = Layer;
            if (corBloco > 0)
                br.ColorIndex = corBloco;
            ms.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);
            foreach (AcadDb.ObjectId entId in def)
            {
                if (tr.GetObject(entId, AcadDb.OpenMode.ForRead) is not AcadDb.AttributeDefinition ad || ad.Constant)
                    continue;
                AcadDb.AttributeReference ar = new();
                ar.SetAttributeFromBlock(ad, br.BlockTransform);
                ar.Layer = LayerText;
                ar.TextString = ad.Tag switch
                {
                    "NOME" => a.Nome,
                    "CATEGORIA" => a.Categoria,
                    "LAT" => a.Lat.ToString("0.#######", CultureInfo.InvariantCulture),
                    "LON" => a.Lon.ToString("0.#######", CultureInfo.InvariantCulture),
                    "FONTE" => a.Fonte,
                    _ => ""
                };
                br.AttributeCollection.AppendAttribute(ar);
                tr.AddNewlyCreatedDBObject(ar, true);
                try
                {
                    ar.AdjustAlignment(db);
                }
                catch
                {
                    // cosmetico
                }
            }

            ViarioStore.SetTag(br, Chunks(a.Serialize()));
            n++;
        }

        tr.Commit();
        PluginLog.Info(Modulo, $"{n} atrativo(s) inserido(s), raio {raio} m.");
        return n;
    }

    /// <summary>Tag + texto em pedacos de 250 caracteres (limite de cada string do XData).</summary>
    private static string[] Chunks(string text)
    {
        List<string> values = new() { Tag };
        for (int i = 0; i < text.Length; i += 250)
            values.Add(text.Substring(i, Math.Min(250, text.Length - i)));
        return values.ToArray();
    }

    /// <summary>Muda o raio (escala) de todos os atrativos.</summary>
    public static int SetRadius(Document doc, double raio)
    {
        AcadDb.Database db = doc.Database;
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach ((AtrativoItem a, AcadDb.BlockReference br) in Scan(tr, db))
        {
            br.UpgradeOpen();
            br.ScaleFactors = new Scale3d(raio);
            a.Raio = raio;
            ViarioStore.SetTag(br, Chunks(a.Serialize()));
            n++;
        }

        tr.Commit();
        return n;
    }

    public static int Remove(Document doc)
    {
        AcadDb.Database db = doc.Database;
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach ((_, AcadDb.BlockReference br) in Scan(tr, db))
        {
            br.UpgradeOpen();
            br.Erase();
            n++;
        }

        tr.Commit();
        return n;
    }

    /// <summary>Atrativos a menos de <paramref name="dist"/> m do ponto (para a placa no painel).</summary>
    public static List<(AtrativoItem Item, double Dist)> Near(IEnumerable<AtrativoItem> items, V2 p, double dist) =>
        items.Select(a => (a, V2.Dist(a.Ponto, p))).Where(x => x.Item2 <= dist).OrderBy(x => x.Item2).ToList();

    // ------------------------------------------------------------------
    // Comando C3DVIARIOATRATIVOS
    // ------------------------------------------------------------------

    public static void Command(Document doc)
    {
        Editor ed = doc.Editor;
        int existing = Read(doc.Database).Count;
        PromptKeywordOptions options = new(
            $"\nAtrativos turisticos ({existing} no desenho) [Importar/Novo/Raio/Listar/Remover/Painel] <Importar>: ",
            "Importar Novo Raio Listar Remover Painel")
        {
            AllowNone = true
        };
        PromptResult r = ed.GetKeywords(options);
        if (r.Status == PromptStatus.Cancel || r.Status == PromptStatus.Error)
            return;

        switch (r.Status == PromptStatus.OK ? r.StringResult : "Importar")
        {
            case "Novo":
                NewByClick(doc);
                return;

            case "Raio":
            {
                PromptDoubleOptions po = new("\nNovo raio dos circulos (m): ") { AllowNegative = false, AllowZero = false };
                PromptDoubleResult pr = ed.GetDouble(po);
                if (pr.Status != PromptStatus.OK)
                    return;
                ed.WriteMessage($"\n  {SetRadius(doc, pr.Value)} atrativo(s) com raio {pr.Value} m.");
                ViarioPanel.NotifyChanged(doc);
                return;
            }

            case "Listar":
            {
                List<AtrativoItem> list = Read(doc.Database);
                foreach (AtrativoItem a in list)
                    ed.WriteMessage($"\n  {a.Nome,-45} {a.Categoria,-32} E {a.E,12:0.00}  N {a.N,13:0.00}  r {a.Raio,5:0.#}");
                ed.WriteMessage($"\n  {list.Count} atrativo(s).");
                return;
            }

            case "Remover":
            {
                PromptKeywordOptions ko = new("\nRemover todos os atrativos? [Sim/Nao] <Nao>: ", "Sim Nao") { AllowNone = true };
                PromptResult kr = ed.GetKeywords(ko);
                if (kr.Status == PromptStatus.OK && kr.StringResult == "Sim")
                {
                    ed.WriteMessage($"\n  {Remove(doc)} atrativo(s) removido(s).");
                    ViarioPanel.NotifyChanged(doc);
                }

                return;
            }

            case "Painel":
                ViarioPanel.ShowPanel("ATRATIVOS");
                return;
        }

        string? kmz = ViarioPlacaSync.AskFile(ed, "KML/KMZ com os atrativos", "Google Earth (*.kmz;*.kml)|*.kmz;*.kml|Todos (*.*)|*.*", "kmz");
        if (kmz is null)
            return;

        List<AtrativoItem> items;
        try
        {
            items = ReadKml(kmz);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n  Nao foi possivel ler o arquivo: " + ex.Message);
            PluginLog.Erro(Modulo, "Falha ao ler " + kmz, ex);
            return;
        }

        if (items.Count == 0)
        {
            ed.WriteMessage("\n  Nenhum ponto no arquivo.");
            return;
        }

        PedGeo? geo = PedGeo.Create(doc, ed);
        if (geo is null)
            return;
        foreach (AtrativoItem a in items)
        {
            Point3d p = geo.ToDrawing(a.Lon, a.Lat);
            a.E = p.X;
            a.N = p.Y;
        }

        PromptDoubleOptions ro = new("\nRaio do circulo de cada atrativo (m) <25>: ") { AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = 25.0, UseDefaultValue = true };
        PromptDoubleResult rr = ed.GetDouble(ro);
        if (rr.Status == PromptStatus.Cancel)
            return;
        double raio = rr.Status == PromptStatus.OK ? rr.Value : 25.0;

        int n = Insert(doc, items, raio, 6);
        ed.WriteMessage($"\n  {n} atrativo(s) inserido(s) ({geo.Description}); bloco {BlockName}, layer {Layer}. " +
                        "Mudar a escala do bloco muda o raio; o nome e um atributo.");
        foreach (IGrouping<string, AtrativoItem> g in items.GroupBy(a => a.Categoria))
            ed.WriteMessage($"\n    {g.Key}: {g.Count()}");
        ViarioPanel.NotifyChanged(doc);
    }
}
