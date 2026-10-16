using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>
/// Le a planilha de locacao de PEDs (.xlsx) sem dependencias externas (ZipArchive + XML).
///
/// - Acha a aba de dados pela linha de cabecalho que comeca com "Codigo" na coluna A
///   (ou usa a aba informada).
/// - Usa o valor em cache das formulas (o Excel grava o resultado junto da formula).
/// - Mapeia as imagens "dentro da celula" (Inserir > Imagem na celula / IMAGE): sao
///   rich values (xl/metadata.xml + xl/richData) e aparecem como #VALUE! para quem
///   le so o XML. Cada uma vira PedRecord.ImageEntries[coluna] = "xl/media/imageN.png".
/// </summary>
internal static class PedXlsxReader
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pr = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Rv = "http://schemas.microsoft.com/office/spreadsheetml/2017/richdata";

    public static PedSheet Read(string path, string? sheetName = null)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using ZipArchive zip = new(fs, ZipArchiveMode.Read);

        List<string> shared = ReadSharedStrings(zip);
        List<(string Name, string Part)> sheets = ReadSheets(zip);
        Dictionary<int, string> images = ReadCellImages(zip);

        PedSheet result = new();

        IEnumerable<(string Name, string Part)> candidates = sheetName is null
            ? sheets
            : sheets.Where(s => string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase));

        foreach ((string name, string part) in candidates)
        {
            ZipArchiveEntry? entry = zip.GetEntry(part);
            if (entry is null)
                continue;

            Dictionary<int, Dictionary<string, Cell>> rows = ReadCells(entry, shared);
            int headerRow = FindHeaderRow(rows);
            if (headerRow == 0)
                continue;

            result.SheetName = name;
            Fill(result, rows, headerRow, images);
            return result;
        }

        throw new InvalidDataException(sheetName is null
            ? "Nenhuma aba da planilha tem a linha de cabecalho com \"Codigo\" na coluna A."
            : $"A aba \"{sheetName}\" nao existe ou nao tem o cabecalho com \"Codigo\" na coluna A.");
    }

    /// <summary>Bytes de uma imagem do xlsx (entrada do zip, ex.: xl/media/image1.png).</summary>
    public static byte[]? ReadEntry(string path, string entryName)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using ZipArchive zip = new(fs, ZipArchiveMode.Read);
        return ReadEntry(zip, entryName);
    }

    public static byte[]? ReadEntry(ZipArchive zip, string entryName)
    {
        ZipArchiveEntry? e = zip.GetEntry(entryName);
        if (e is null)
            return null;
        using Stream s = e.Open();
        using MemoryStream ms = new();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------

    private sealed record Cell(string Text, int ValueMeta);

    private static void Fill(PedSheet result, Dictionary<int, Dictionary<string, Cell>> rows, int headerRow, Dictionary<int, string> images)
    {
        Dictionary<string, Cell> header = rows[headerRow];
        rows.TryGetValue(headerRow - 1, out Dictionary<string, Cell>? groups);

        // Titulo: primeiro texto da linha 2 (descricao do cenario na planilha original).
        if (rows.TryGetValue(2, out Dictionary<string, Cell>? row2))
        {
            result.Title = row2.OrderBy(kv => PedFormat.ColumnIndex(kv.Key))
                               .Select(kv => kv.Value.Text)
                               .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t) && !PedFormat.TryNumber(t, out _)) ?? string.Empty;
        }

        // Colunas com cabecalho. Rotulos repetidos ganham o grupo da linha de cima (mais proximo a esquerda).
        List<(int Index, string Letter, string Label)> cols = header
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Text))
            .Select(kv => (PedFormat.ColumnIndex(kv.Key), kv.Key, Clean(kv.Value.Text)))
            .OrderBy(c => c.Item1)
            .ToList();

        HashSet<string> repeated = cols.GroupBy(c => PedFormat.Normalize(c.Label))
                                       .Where(g => g.Count() > 1)
                                       .Select(g => g.Key)
                                       .ToHashSet();

        foreach ((int index, string letter, string label) in cols)
        {
            string finalLabel = label;
            if (repeated.Contains(PedFormat.Normalize(label)) && groups is not null)
            {
                string? group = groups.Where(g => PedFormat.ColumnIndex(g.Key) <= index && !string.IsNullOrWhiteSpace(g.Value.Text))
                                      .OrderByDescending(g => PedFormat.ColumnIndex(g.Key))
                                      .Select(g => Clean(g.Value.Text))
                                      .FirstOrDefault();
                if (!string.IsNullOrEmpty(group))
                    finalLabel = label + " (" + group + ")";
            }
            result.Columns.Add(new PedColumn(letter, finalLabel));
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (int r in rows.Keys.Where(k => k > headerRow).OrderBy(k => k))
        {
            Dictionary<string, Cell> row = rows[r];
            if (!row.TryGetValue("A", out Cell? codeCell) || string.IsNullOrWhiteSpace(codeCell.Text))
                continue;

            string code = codeCell.Text.Trim();
            if (!seen.Add(code))
            {
                result.Warnings.Add($"Codigo {code} repetido na linha {r}; mantida a primeira ocorrencia.");
                continue;
            }

            PedRecord rec = new(code);
            foreach ((string letter, Cell cell) in row)
            {
                if (cell.ValueMeta > 0 && images.TryGetValue(cell.ValueMeta, out string? img))
                {
                    rec.ImageEntries[letter] = img;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(cell.Text))
                    rec.Values[letter] = cell.Text.Trim();
            }

            result.Records.Add(rec);
        }
    }

    private static string Clean(string text) =>
        string.Join(" ", text.Replace('\n', ' ').Replace('\r', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static int FindHeaderRow(Dictionary<int, Dictionary<string, Cell>> rows)
    {
        foreach (int r in rows.Keys.Where(k => k <= 15).OrderBy(k => k))
        {
            if (rows[r].TryGetValue("A", out Cell? c) && PedFormat.Normalize(c.Text) == "codigo")
                return r;
        }
        return 0;
    }

    private static Dictionary<int, Dictionary<string, Cell>> ReadCells(ZipArchiveEntry entry, List<string> shared)
    {
        Dictionary<int, Dictionary<string, Cell>> rows = new();
        XDocument doc;
        using (Stream s = entry.Open())
            doc = XDocument.Load(s);

        XElement? data = doc.Root?.Element(S + "sheetData");
        if (data is null)
            return rows;

        foreach (XElement c in data.Elements(S + "row").Elements(S + "c"))
        {
            string? cref = (string?)c.Attribute("r");
            if (cref is null)
                continue;

            (string letters, int row) = PedFormat.SplitRef(cref);
            string type = (string?)c.Attribute("t") ?? "n";
            int vm = int.TryParse((string?)c.Attribute("vm"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            string? raw = (string?)c.Element(S + "v");

            string text = type switch
            {
                "s" => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < shared.Count ? shared[i] : string.Empty,
                "inlineStr" => string.Concat(c.Descendants(S + "t").Select(t => t.Value)),
                "str" => raw ?? string.Empty,
                "b" => raw == "1" ? "Sim" : "Nao",
                "e" => vm > 0 ? string.Empty : raw ?? string.Empty,
                _ => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? PedFormat.Number(d) : raw ?? string.Empty
            };

            if (string.IsNullOrEmpty(text) && vm == 0)
                continue;

            if (!rows.TryGetValue(row, out Dictionary<string, Cell>? cells))
                rows[row] = cells = new Dictionary<string, Cell>(StringComparer.OrdinalIgnoreCase);
            cells[letters] = new Cell(text, vm);
        }

        return rows;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        List<string> list = new();
        ZipArchiveEntry? e = zip.GetEntry("xl/sharedStrings.xml");
        if (e is null)
            return list;
        using Stream s = e.Open();
        XDocument doc = XDocument.Load(s);
        foreach (XElement si in doc.Root!.Elements(S + "si"))
        {
            // Texto simples (<t>) ou com formatacao (<r><t>); ignora a fonetica (<rPh>).
            string text = string.Concat(si.Elements(S + "t").Select(t => t.Value)) +
                          string.Concat(si.Elements(S + "r").Elements(S + "t").Select(t => t.Value));
            list.Add(text);
        }
        return list;
    }

    private static List<(string Name, string Part)> ReadSheets(ZipArchive zip)
    {
        List<(string, string)> list = new();
        XDocument wb = Load(zip, "xl/workbook.xml") ?? throw new InvalidDataException("xl/workbook.xml nao encontrado: o arquivo nao e um .xlsx valido.");
        Dictionary<string, string> rels = ReadRels(zip, "xl/_rels/workbook.xml.rels", "xl/");

        foreach (XElement sh in wb.Root!.Element(S + "sheets")?.Elements(S + "sheet") ?? Enumerable.Empty<XElement>())
        {
            string name = (string?)sh.Attribute("name") ?? string.Empty;
            string id = (string?)sh.Attribute(R + "id") ?? string.Empty;
            if (rels.TryGetValue(id, out string? part))
                list.Add((name, part));
        }
        return list;
    }

    /// <summary>vm (1-based, atributo da celula) -> entrada da imagem no zip.</summary>
    private static Dictionary<int, string> ReadCellImages(ZipArchive zip)
    {
        Dictionary<int, string> map = new();
        try
        {
            XDocument? meta = Load(zip, "xl/metadata.xml");
            XDocument? rvData = Load(zip, "xl/richData/rdrichvalue.xml");
            XDocument? rvStruct = Load(zip, "xl/richData/rdrichvaluestructure.xml");
            XDocument? rvRel = Load(zip, "xl/richData/richValueRel.xml");
            if (meta is null || rvData is null || rvStruct is null || rvRel is null)
                return map;

            // Tipos de metadado (1-based) -> nome; queremos XLRICHVALUE.
            List<string> types = meta.Root!.Element(S + "metadataTypes")?.Elements(S + "metadataType")
                .Select(t => (string?)t.Attribute("name") ?? string.Empty).ToList() ?? new List<string>();

            // futureMetadata XLRICHVALUE: lista de indices de rich value.
            List<int> rvbIndex = meta.Root.Elements(S + "futureMetadata")
                .Where(f => (string?)f.Attribute("name") == "XLRICHVALUE")
                .Elements(S + "bk")
                .Select(bk => bk.Descendants().FirstOrDefault(d => d.Name.LocalName == "rvb"))
                .Select(rvb => rvb is null ? -1 : (int?)rvb.Attribute("i") ?? -1)
                .ToList();

            // Estruturas: posicao da chave _rvRel:LocalImageIdentifier.
            List<int> imageKeyPos = rvStruct.Root!.Elements(Rv + "s")
                .Select(s => s.Elements(Rv + "k").Select((k, i) => ((string?)k.Attribute("n"), i))
                              .Where(p => p.Item1 == "_rvRel:LocalImageIdentifier")
                              .Select(p => p.i).DefaultIfEmpty(-1).First())
                .ToList();

            List<int> rvRelIndex = rvData.Root!.Elements(Rv + "rv")
                .Select(rv =>
                {
                    int s = (int?)rv.Attribute("s") ?? 0;
                    int pos = s >= 0 && s < imageKeyPos.Count ? imageKeyPos[s] : -1;
                    List<XElement> vs = rv.Elements(Rv + "v").ToList();
                    return pos >= 0 && pos < vs.Count && int.TryParse(vs[pos].Value, out int ri) ? ri : -1;
                })
                .ToList();

            List<string> relIds = rvRel.Root!.Elements()
                .Select(e => (string?)e.Attribute(R + "id") ?? string.Empty)
                .ToList();
            Dictionary<string, string> rels = ReadRels(zip, "xl/richData/_rels/richValueRel.xml.rels", "xl/richData/");

            List<XElement> valueBks = meta.Root.Element(S + "valueMetadata")?.Elements(S + "bk").ToList() ?? new List<XElement>();
            for (int vm = 1; vm <= valueBks.Count; vm++)
            {
                XElement? rc = valueBks[vm - 1].Element(S + "rc");
                if (rc is null)
                    continue;
                int t = (int?)rc.Attribute("t") ?? 0;
                int v = (int?)rc.Attribute("v") ?? -1;
                if (t < 1 || t > types.Count || types[t - 1] != "XLRICHVALUE")
                    continue;
                if (v < 0 || v >= rvbIndex.Count)
                    continue;
                int rvIdx = rvbIndex[v];
                if (rvIdx < 0 || rvIdx >= rvRelIndex.Count)
                    continue;
                int relIdx = rvRelIndex[rvIdx];
                if (relIdx < 0 || relIdx >= relIds.Count)
                    continue;
                if (rels.TryGetValue(relIds[relIdx], out string? target))
                    map[vm] = target;
            }
        }
        catch
        {
            // Sem imagens legiveis: segue so com os dados.
        }

        return map;
    }

    private static XDocument? Load(ZipArchive zip, string name)
    {
        ZipArchiveEntry? e = zip.GetEntry(name);
        if (e is null)
            return null;
        using Stream s = e.Open();
        return XDocument.Load(s);
    }

    /// <summary>Id -> caminho absoluto dentro do zip (resolve "../media/x.png").</summary>
    private static Dictionary<string, string> ReadRels(ZipArchive zip, string relsPath, string baseDir)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        XDocument? doc = Load(zip, relsPath);
        if (doc is null)
            return map;

        foreach (XElement rel in doc.Root!.Elements(Pr + "Relationship"))
        {
            string id = (string?)rel.Attribute("Id") ?? string.Empty;
            string target = (string?)rel.Attribute("Target") ?? string.Empty;
            map[id] = ResolvePath(baseDir, target);
        }
        return map;
    }

    private static string ResolvePath(string baseDir, string target)
    {
        if (target.StartsWith('/'))
            return target.TrimStart('/');

        List<string> parts = baseDir.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (string p in target.Split('/'))
        {
            if (p == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else if (p != "." && p.Length > 0)
            {
                parts.Add(p);
            }
        }
        return string.Join("/", parts);
    }
}
