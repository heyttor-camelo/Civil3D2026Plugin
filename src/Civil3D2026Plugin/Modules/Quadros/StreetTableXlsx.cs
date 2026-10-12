using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace Civil3D2026Plugin.Modules.Quadros;

/// <summary>Uma linha do quadro de ruas, ja calculada.</summary>
internal sealed record StreetRow(
    string Name,
    int StartStake,
    double StartOffset,
    int EndStake,
    double EndOffset,
    string WidthFormula,
    double? WidthValue,
    string StartLatitude,
    string StartLatHemisphere,
    string StartLongitude,
    string StartLonHemisphere,
    string EndLatitude,
    string EndLatHemisphere,
    string EndLongitude,
    string EndLonHemisphere);

/// <summary>Bloco de assinatura abaixo do quadro.</summary>
internal sealed record StreetTableSignature(string Responsible, string Registration, string Place, DateTime Date);

/// <summary>
/// Gera o .xlsx do QUADRO DE RUAS no layout da planilha modelo
/// (Planilha_em_RTA_TCE_GEO_IMP_R01): colunas D..Q, titulo na linha 5,
/// cabecalho nas linhas 6-7 e dados a partir da linha 8, com as mesmas
/// mesclagens, larguras, bordas (contorno medio, internas finas) e formulas
/// (extensao pelas estacas, area = largura x extensao).
///
/// Escrito sem dependencias externas (OpenXML minimo via ZipArchive).
/// </summary>
internal static class StreetTableXlsx
{
    private const int FirstDataRow = 8;

    // Colunas D..Q (4..17).
    private const int ColFirst = 4;
    private const int ColLast = 17;

    public static void Write(
        string path,
        IReadOnlyList<StreetRow> rows,
        double stakeInterval,
        bool includeTotals,
        StreetTableSignature? signature)
    {
        StyleBook styles = new();
        SheetBuilder sheet = new(styles);

        string interval = Num(stakeInterval);

        // ---------------- Titulo e cabecalho (linhas 5-7) ----------------
        int titleRow = 5;
        int lastTableRow = FirstDataRow + rows.Count - 1 + (includeTotals ? 1 : 0);
        if (rows.Count == 0)
            lastTableRow = FirstDataRow - 1;

        sheet.Text(titleRow, 4, "QUADRO DE RUAS", Align.Center);
        sheet.Merge(titleRow, 4, titleRow, 17);

        sheet.Text(6, 4, "RUA/AVENIDA", Align.Center, vCenter: true);
        sheet.Merge(6, 4, 7, 4);
        sheet.Text(6, 5, "ESTAQUEAMENTO", Align.Center);
        sheet.Merge(6, 5, 6, 10);
        sheet.Text(6, 11, "EXTENSÃO\n(m)", Align.Center, vCenter: true, wrap: true);
        sheet.Merge(6, 11, 7, 11);
        sheet.Text(6, 12, "LARGURA PISTA\n(m)", Align.Center, vCenter: true, wrap: true);
        sheet.Merge(6, 12, 7, 12);
        sheet.Text(6, 13, "ÁREA\n(m²)", Align.Center, vCenter: true, wrap: true);
        sheet.Merge(6, 13, 7, 13);
        sheet.Text(6, 14, "COORDENADAS", Align.Center);
        sheet.Merge(6, 14, 6, 17);

        sheet.Text(7, 5, "INÍCIO", Align.Center);
        sheet.Merge(7, 5, 7, 7);
        sheet.Text(7, 8, "FIM", Align.Center);
        sheet.Merge(7, 8, 7, 10);
        sheet.Text(7, 14, "INÍCIO", Align.Center);
        sheet.Merge(7, 14, 7, 15);
        sheet.Text(7, 16, "FIM", Align.Center);
        sheet.Merge(7, 16, 7, 17);

        // ---------------- Dados ----------------
        int r = FirstDataRow;
        foreach (StreetRow row in rows)
        {
            sheet.Text(r, 4, row.Name, Align.Left, vCenter: true, wrap: true);

            sheet.Number(r, 5, row.StartStake, NumFmt.General, Align.Center);
            sheet.Text(r, 6, "+", Align.Center, vCenter: true);
            sheet.Number(r, 7, row.StartOffset, NumFmt.Two, Align.Center);

            sheet.Number(r, 8, row.EndStake, NumFmt.General, Align.Center);
            sheet.Text(r, 9, "+", Align.Center, vCenter: true);
            sheet.Number(r, 10, row.EndOffset, NumFmt.Three, Align.Center);

            sheet.Formula(r, 11, $"((H{r}*{interval})+J{r})-((E{r}*{interval})+G{r})", NumFmt.Three, Align.Center);

            if (!string.IsNullOrEmpty(row.WidthFormula))
                sheet.Formula(r, 12, row.WidthFormula, NumFmt.Two, Align.Center);
            else
                sheet.Number(r, 12, row.WidthValue ?? 0.0, NumFmt.Two, Align.Center);

            sheet.Formula(r, 13, $"L{r}*K{r}", NumFmt.Two, Align.Center);

            sheet.Text(r, 14, row.StartLatitude, Align.Center, vCenter: true, fmt: Hemisphere(row.StartLatHemisphere));
            sheet.Text(r, 15, row.StartLongitude, Align.Center, vCenter: true, fmt: Hemisphere(row.StartLonHemisphere));
            sheet.Text(r, 16, row.EndLatitude, Align.Center, vCenter: true, fmt: Hemisphere(row.EndLatHemisphere));
            sheet.Text(r, 17, row.EndLongitude, Align.Center, vCenter: true, fmt: Hemisphere(row.EndLonHemisphere));
            r++;
        }

        if (includeTotals && rows.Count > 0)
        {
            int last = r - 1;
            sheet.Text(r, 4, "TOTAL", Align.Left, vCenter: true);
            sheet.Merge(r, 5, r, 10);
            sheet.Formula(r, 11, $"SUM(K{FirstDataRow}:K{last})", NumFmt.Three, Align.Center);
            sheet.Formula(r, 13, $"SUM(M{FirstDataRow}:M{last})", NumFmt.Two, Align.Center);
            sheet.Merge(r, 14, r, 17);
            r++;
        }

        // Bordas: tudo fino por dentro, contorno medio (como no modelo).
        sheet.ApplyGrid(titleRow, lastTableRow, ColFirst, ColLast);

        // ---------------- Assinatura ----------------
        if (signature is not null)
        {
            int s = lastTableRow + 3;

            sheet.Text(s, 4, "Local e data: " + signature.Place +
                (string.IsNullOrWhiteSpace(signature.Place) ? "" : ", ") +
                signature.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), Align.Left);
            sheet.Merge(s, 4, s, 17);

            sheet.Text(s + 3, 9, "________________________________________", Align.Center);
            sheet.Merge(s + 3, 9, s + 3, 16);
            sheet.Text(s + 4, 9, signature.Responsible, Align.Center);
            sheet.Merge(s + 4, 9, s + 4, 16);
            sheet.Text(s + 5, 9, "Responsável técnico - " + signature.Registration, Align.Center);
            sheet.Merge(s + 5, 9, s + 5, 16);
        }

        WritePackage(path, sheet, styles);
    }

    private static NumFmt Hemisphere(string hemisphere) => hemisphere switch
    {
        "S" => NumFmt.South,
        "N" => NumFmt.North,
        "W" => NumFmt.West,
        "E" => NumFmt.East,
        _ => NumFmt.General
    };

    private static string Num(double value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    // =====================================================================
    // Estilos
    // =====================================================================

    internal enum Align { General, Left, Center }

    internal enum NumFmt { General, Two, Three, South, North, West, East }

    internal enum Line { None, Thin, Medium }

    private readonly record struct CellStyle(Align H, bool VCenter, bool Wrap, NumFmt Fmt, Line L, Line R, Line T, Line B);

    private sealed class StyleBook
    {
        private readonly List<CellStyle> _styles = new() { default };
        private readonly Dictionary<CellStyle, int> _index = new() { [default] = 0 };

        public int Get(CellStyle style)
        {
            if (!_index.TryGetValue(style, out int id))
            {
                id = _styles.Count;
                _styles.Add(style);
                _index[style] = id;
            }

            return id;
        }

        public string Xml()
        {
            static int FmtId(NumFmt f) => f switch
            {
                NumFmt.Two => 2,       // 0.00 (nativo)
                NumFmt.Three => 164,
                NumFmt.South => 165,
                NumFmt.North => 166,
                NumFmt.West => 167,
                NumFmt.East => 168,
                _ => 0
            };

            static string L(string tag, Line line) => line switch
            {
                Line.Thin => $"<{tag} style=\"thin\"><color auto=\"1\"/></{tag}>",
                Line.Medium => $"<{tag} style=\"medium\"><color auto=\"1\"/></{tag}>",
                _ => $"<{tag}/>"
            };

            List<(Line, Line, Line, Line)> borders = new() { (Line.None, Line.None, Line.None, Line.None) };
            Dictionary<(Line, Line, Line, Line), int> borderIndex = new() { [(Line.None, Line.None, Line.None, Line.None)] = 0 };

            int BorderId(CellStyle s)
            {
                var key = (s.L, s.R, s.T, s.B);
                if (!borderIndex.TryGetValue(key, out int id))
                {
                    id = borders.Count;
                    borders.Add(key);
                    borderIndex[key] = id;
                }

                return id;
            }

            StringBuilder xfs = new();
            foreach (CellStyle s in _styles)
            {
                int numFmt = FmtId(s.Fmt);
                int border = BorderId(s);
                string h = s.H switch { Align.Left => "left", Align.Center => "center", _ => "general" };

                xfs.Append("<xf numFmtId=\"").Append(numFmt).Append("\" fontId=\"0\" fillId=\"0\" borderId=\"").Append(border)
                   .Append("\" xfId=\"0\"")
                   .Append(numFmt != 0 ? " applyNumberFormat=\"1\"" : "")
                   .Append(border != 0 ? " applyBorder=\"1\"" : "")
                   .Append(" applyAlignment=\"1\"><alignment horizontal=\"").Append(h).Append('"')
                   .Append(s.VCenter ? " vertical=\"center\"" : "")
                   .Append(s.Wrap ? " wrapText=\"1\"" : "")
                   .Append("/></xf>");
            }

            StringBuilder borderXml = new();
            foreach ((Line l, Line r, Line t, Line b) in borders)
                borderXml.Append("<border>").Append(L("left", l)).Append(L("right", r)).Append(L("top", t)).Append(L("bottom", b)).Append("<diagonal/></border>");

            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<numFmts count=\"5\">" +
                "<numFmt numFmtId=\"164\" formatCode=\"0.000\"/>" +
                "<numFmt numFmtId=\"165\" formatCode=\"&quot;S&quot;@\"/>" +
                "<numFmt numFmtId=\"166\" formatCode=\"&quot;N&quot;@\"/>" +
                "<numFmt numFmtId=\"167\" formatCode=\"&quot;W&quot;@\"/>" +
                "<numFmt numFmtId=\"168\" formatCode=\"&quot;E&quot;@\"/>" +
                "</numFmts>" +
                "<fonts count=\"1\"><font><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/><scheme val=\"minor\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"" + borders.Count + "\">" + borderXml + "</borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"" + _styles.Count + "\">" + xfs + "</cellXfs>" +
                "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                "</styleSheet>";
        }
    }

    // =====================================================================
    // Planilha
    // =====================================================================

    private sealed class CellData
    {
        public string? Text;
        public double? Number;
        public string? Formula;
        public CellStyle Style;
    }

    private sealed class SheetBuilder
    {
        public int LastRow => _rows.Count == 0 ? 1 : _rows.Keys.Max();

        private readonly StyleBook _styles;
        private readonly SortedDictionary<int, SortedDictionary<int, CellData>> _rows = new();
        private readonly List<string> _merges = new();

        public SheetBuilder(StyleBook styles) => _styles = styles;

        private CellData Cell(int row, int col)
        {
            if (!_rows.TryGetValue(row, out SortedDictionary<int, CellData>? cols))
            {
                cols = new SortedDictionary<int, CellData>();
                _rows[row] = cols;
            }

            if (!cols.TryGetValue(col, out CellData? cell))
            {
                cell = new CellData();
                cols[col] = cell;
            }

            return cell;
        }

        public void Text(int row, int col, string text, Align h, bool vCenter = false, bool wrap = false, NumFmt fmt = NumFmt.General)
        {
            CellData c = Cell(row, col);
            c.Text = text;
            c.Style = c.Style with { H = h, VCenter = vCenter, Wrap = wrap, Fmt = fmt };
        }

        public void Number(int row, int col, double value, NumFmt fmt, Align h)
        {
            CellData c = Cell(row, col);
            c.Number = value;
            c.Style = c.Style with { H = h, VCenter = true, Fmt = fmt };
        }

        public void Formula(int row, int col, string formula, NumFmt fmt, Align h)
        {
            CellData c = Cell(row, col);
            c.Formula = formula.StartsWith("=", StringComparison.Ordinal) ? formula[1..] : formula;
            c.Style = c.Style with { H = h, VCenter = true, Fmt = fmt };
        }

        public void Merge(int r1, int c1, int r2, int c2) => _merges.Add(Ref(r1, c1) + ":" + Ref(r2, c2));

        /// <summary>Bordas finas em todas as celulas e contorno medio no retangulo.</summary>
        public void ApplyGrid(int r1, int r2, int c1, int c2)
        {
            for (int r = r1; r <= r2; r++)
            {
                for (int c = c1; c <= c2; c++)
                {
                    CellData cell = Cell(r, c);
                    cell.Style = cell.Style with
                    {
                        L = c == c1 ? Line.Medium : Line.Thin,
                        R = c == c2 ? Line.Medium : Line.Thin,
                        T = r == r1 ? Line.Medium : Line.Thin,
                        B = r == r2 ? Line.Medium : Line.Thin
                    };
                }
            }
        }

        public string Xml()
        {
            StringBuilder sb = new();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
              .Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ")
              .Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">")
              .Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>")
              .Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>")
              .Append("<sheetFormatPr defaultRowHeight=\"15\"/>")
              .Append("<cols>");

            // Larguras da planilha modelo.
            (int Col, double Width)[] widths =
            {
                (4, 30.0), (5, 4.0), (6, 2.43), (7, 6.0), (8, 4.57), (9, 1.57), (10, 6.86),
                (11, 10.86), (12, 15.29), (13, 11.0), (14, 14.0), (15, 14.0), (16, 14.0), (17, 14.0)
            };

            foreach ((int col, double width) in widths)
            {
                sb.Append("<col min=\"").Append(col).Append("\" max=\"").Append(col)
                  .Append("\" width=\"").Append(width.ToString(CultureInfo.InvariantCulture)).Append("\" customWidth=\"1\"/>");
            }

            sb.Append("</cols><sheetData>");

            foreach ((int row, SortedDictionary<int, CellData> cols) in _rows)
            {
                bool tall = cols.Values.Any(c => c.Style.Wrap && c.Text is not null && c.Text.Contains('\n'));
                sb.Append("<row r=\"").Append(row).Append('"');
                if (tall)
                    sb.Append(" ht=\"30\" customHeight=\"1\"");
                sb.Append('>');

                foreach ((int col, CellData cell) in cols)
                {
                    int s = _styles.Get(cell.Style);
                    string r = Ref(row, col);

                    if (cell.Formula is not null)
                    {
                        sb.Append("<c r=\"").Append(r).Append("\" s=\"").Append(s).Append("\"><f>")
                          .Append(SecurityElement.Escape(cell.Formula)).Append("</f></c>");
                    }
                    else if (cell.Number is double n)
                    {
                        sb.Append("<c r=\"").Append(r).Append("\" s=\"").Append(s).Append("\"><v>")
                          .Append(n.ToString("R", CultureInfo.InvariantCulture)).Append("</v></c>");
                    }
                    else if (cell.Text is not null)
                    {
                        sb.Append("<c r=\"").Append(r).Append("\" s=\"").Append(s).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                          .Append(SecurityElement.Escape(cell.Text)).Append("</t></is></c>");
                    }
                    else
                    {
                        sb.Append("<c r=\"").Append(r).Append("\" s=\"").Append(s).Append("\"/>");
                    }
                }

                sb.Append("</row>");
            }

            sb.Append("</sheetData>");

            if (_merges.Count > 0)
            {
                sb.Append("<mergeCells count=\"").Append(_merges.Count).Append("\">");
                foreach (string merge in _merges)
                    sb.Append("<mergeCell ref=\"").Append(merge).Append("\"/>");
                sb.Append("</mergeCells>");
            }

            sb.Append("<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/>")
              .Append("<pageSetup paperSize=\"9\" orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"0\"/>")
              .Append("</worksheet>");

            return sb.ToString();
        }

        private static string Ref(int row, int col) => ColumnName(col) + row.ToString(CultureInfo.InvariantCulture);

        private static string ColumnName(int col)
        {
            string name = string.Empty;
            while (col > 0)
            {
                int rem = (col - 1) % 26;
                name = (char)('A' + rem) + name;
                col = (col - 1) / 26;
            }

            return name;
        }
    }

    // =====================================================================
    // Pacote .xlsx
    // =====================================================================

    private static void WritePackage(string path, SheetBuilder sheet, StyleBook styles)
    {
        string sheetXml = sheet.Xml(); // gera os estilos usados antes de serializa-los
        string stylesXml = styles.Xml();
        string printArea = "'Quadro de Ruas'!$D$5:$Q$" + sheet.LastRow.ToString(CultureInfo.InvariantCulture);

        string tmp = path + ".tmp";
        if (File.Exists(tmp))
            File.Delete(tmp);

        using (FileStream fs = new(tmp, FileMode.CreateNew))
        using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                "</Types>");

            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");

            Add(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Quadro de Ruas\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
                "<definedNames><definedName name=\"_xlnm.Print_Area\" localSheetId=\"0\">" + SecurityElement.Escape(printArea) + "</definedName></definedNames>" +
                "<calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/>" +
                "</workbook>");

            Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "</Relationships>");

            Add(zip, "xl/worksheets/sheet1.xml", sheetXml);
            Add(zip, "xl/styles.xml", stylesXml);
        }

        if (File.Exists(path))
            File.Delete(path);

        File.Move(tmp, path);
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
