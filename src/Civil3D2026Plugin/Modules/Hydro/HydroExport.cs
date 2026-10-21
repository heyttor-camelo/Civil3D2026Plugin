using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>Exportacao da planilha de dimensionamento e das violacoes: XLSX (OpenXML minimo, sem dependencias) e HTML.</summary>
internal static class HydroExport
{
    private static readonly string[] Headers =
    {
        "Trecho", "De", "Para", "A local [ha]", "A acum. [ha]", "C", "Tc [min]", "i [mm/h]", "Q [m3/s]", "L [m]", "Decliv. [m/m]",
        "D [mm]", "y/D", "V [m/s]", "D min [mm]", "Fundo mont.", "Fundo jus."
    };

    private static IEnumerable<object?[]> Cells(List<HydroSizingRow> rows) => rows.Select(r => new object?[]
    {
        r.Trecho, r.From, r.To, r.AreaLocalHa, r.AreaAcumHa, r.C, r.Tc, r.Intensity, r.Flow, r.Length, r.Slope,
        r.Diameter * 1000, r.DepthRatio, r.Velocity, double.IsNaN(r.MinDiameter) ? null : r.MinDiameter * 1000, r.UpInvert, r.DownInvert
    });

    public static void Xlsx(string path, NetworkInfo net, List<HydroSizingRow> rows, List<HydroViolation> violations, HydroSettings s)
    {
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        using ZipArchive zip = new(fs, ZipArchiveMode.Create);
        Entry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "</Types>");
        Entry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");
        Entry(zip, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Dimensionamento\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Verificacao\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
        Entry(zip, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
            "</Relationships>");

        List<object?[]> sheet1 = new()
        {
            new object?[] { "HYDRO - Planilha de dimensionamento", net.Name },
            new object?[] { "IDF", s.Idf.Name + ": i = " + s.Idf.Formula(), "Tr [anos]", s.ReturnPeriod, "Tc min [min]", s.MinTc, "n", s.Manning },
            new object?[] { "Criterios", $"y/D <= {s.MaxDepthRatio}", $"V {s.MinVelocity}-{s.MaxVelocity} m/s", $"recobrimento >= {s.MinCover} m", $"i {s.MinSlope * 100:0.00}-{s.MaxSlope * 100:0.00} %" },
            Array.Empty<object?>(),
            Headers.Cast<object?>().ToArray()
        };
        sheet1.AddRange(Cells(rows));
        Entry(zip, "xl/worksheets/sheet1.xml", Sheet(sheet1));

        List<object?[]> sheet2 = new() { new object?[] { "Item", "Regra", "Valor", "Limite", "Severidade", "Descricao", "Correcao sugerida" } };
        sheet2.AddRange(violations.Select(v => new object?[] { v.Item, v.Rule, v.Value, v.Limit, v.Severity.ToString(), v.Text, v.Fix }));
        Entry(zip, "xl/worksheets/sheet2.xml", Sheet(sheet2));
    }

    private static string Sheet(List<object?[]> rows)
    {
        StringBuilder sb = new();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (int r = 0; r < rows.Count; r++)
        {
            sb.Append("<row r=\"").Append(r + 1).Append("\">");
            for (int c = 0; c < rows[r].Length; c++)
            {
                object? v = rows[r][c];
                if (v is null)
                    continue;
                string cell = Column(c) + (r + 1);
                if (v is double d)
                {
                    if (double.IsNaN(d) || double.IsInfinity(d))
                        continue;
                    sb.Append("<c r=\"").Append(cell).Append("\"><v>").Append(d.ToString("R", CultureInfo.InvariantCulture)).Append("</v></c>");
                }
                else
                {
                    sb.Append("<c r=\"").Append(cell).Append("\" t=\"inlineStr\"><is><t>").Append(SecurityElement.Escape(v.ToString() ?? "")).Append("</t></is></c>");
                }
            }

            sb.Append("</row>");
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string Column(int index)
    {
        string s = "";
        index++;
        while (index > 0)
        {
            int rem = (index - 1) % 26;
            s = (char)('A' + rem) + s;
            index = (index - 1) / 26;
        }

        return s;
    }

    private static void Entry(ZipArchive zip, string name, string content)
    {
        ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using Stream st = e.Open();
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);
        st.Write(bytes, 0, bytes.Length);
    }

    public static void Html(string path, NetworkInfo net, List<HydroSizingRow> rows, List<HydroViolation> violations, HydroSettings s)
    {
        StringBuilder sb = new();
        sb.Append("<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>HYDRO - ").Append(E(net.Name)).Append("</title>");
        sb.Append("<style>body{font-family:Segoe UI,Tahoma,sans-serif;font-size:13px;margin:24px}table{border-collapse:collapse;margin:12px 0}th,td{border:1px solid #999;padding:3px 6px}th{background:#e5e5e5}td.n{text-align:right;font-family:Consolas,monospace}tr.erro td{background:#fde0e0}tr.aviso td{background:#fff4d6}</style></head><body>");
        sb.Append("<h1>HYDRO - Rede ").Append(E(net.Name)).Append("</h1>");
        sb.Append("<p>IDF ").Append(E(s.Idf.Name)).Append(": i = ").Append(E(s.Idf.Formula())).Append("; Tr = ").Append(s.ReturnPeriod).Append(" anos; Tc min = ").Append(s.MinTc).Append(" min; n = ").Append(s.Manning).Append("<br>");
        sb.Append("Criterios: y/D &le; ").Append(s.MaxDepthRatio).Append("; V entre ").Append(s.MinVelocity).Append(" e ").Append(s.MaxVelocity).Append(" m/s; recobrimento &ge; ").Append(s.MinCover).Append(" m; declividade entre ").Append((s.MinSlope * 100).ToString("0.00", CultureInfo.InvariantCulture)).Append(" e ").Append((s.MaxSlope * 100).ToString("0.00", CultureInfo.InvariantCulture)).Append(" %. Vazoes em m3/s.</p>");
        sb.Append("<h2>Planilha de dimensionamento</h2><table><tr>");
        foreach (string h in Headers)
            sb.Append("<th>").Append(E(h)).Append("</th>");
        sb.Append("</tr>");
        foreach (object?[] row in Cells(rows))
        {
            sb.Append("<tr>");
            foreach (object? v in row)
                sb.Append(v is double d ? "<td class=\"n\">" + (double.IsNaN(d) ? "-" : d.ToString("0.####", CultureInfo.InvariantCulture)) + "</td>" : "<td>" + E(v?.ToString() ?? "-") + "</td>");
            sb.Append("</tr>");
        }

        sb.Append("</table><h2>Verificacao (").Append(violations.Count).Append(")</h2><table><tr><th>Item</th><th>Regra</th><th>Valor</th><th>Limite</th><th>Severidade</th><th>Descricao</th><th>Correcao sugerida</th></tr>");
        foreach (HydroViolation v in violations)
        {
            sb.Append("<tr class=\"").Append(v.Severity == HydroSeverity.Erro ? "erro" : "aviso").Append("\"><td>").Append(E(v.Item)).Append("</td><td>").Append(E(v.Rule)).Append("</td><td>").Append(E(v.Value))
                .Append("</td><td>").Append(E(v.Limit)).Append("</td><td>").Append(v.Severity).Append("</td><td>").Append(E(v.Text)).Append("</td><td>").Append(E(v.Fix)).Append("</td></tr>");
        }

        sb.Append("</table><p>Gerado pelo Civil3D2026Plugin HYDRO v").Append(C3DVersions.Hydro).Append(" em ").Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)).Append("</p></body></html>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string E(string s) => SecurityElement.Escape(s) ?? "";
}
