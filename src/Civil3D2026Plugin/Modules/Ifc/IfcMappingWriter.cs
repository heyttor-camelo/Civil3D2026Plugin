using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace Civil3D2026Plugin.Modules.Ifc;

/// <summary>
/// Gera a planilha de mapeamento da extensao IFC do Civil 3D: copia a planilha padrao da Autodesk e
/// muda/acrescenta as linhas dos criterios (aba Mapping). Os textos vao pela tabela compartilhada do
/// xlsx (o leitor da extensao ignora texto direto na celula: teste de 13/11/2026).
/// </summary>
internal static class IfcMappingWriter
{
    public const string BundleFolder = @"C:\Program Files\Autodesk\ApplicationPlugins\Civil3D_IFC-2026.bundle";
    public const string MappingSuffix = "-IfcInfraExportMapping-IFC4X3_ADD2.xlsx";
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static string DefaultMappingPath => Path.Combine(BundleFolder, "Contents", "Mapping", "IfcInfraExportMapping-IFC4X3_ADD2.xlsx");

    public static bool ExtensionInstalled => File.Exists(Path.Combine(BundleFolder, "Contents", "Civil3D_IFC.dll"));

    /// <summary>Escreve a planilha; devolve (linhas mudadas, acrescentadas).</summary>
    public static (int Changed, int Added) Write(string output, IEnumerable<IfcMapRow> rows)
    {
        File.Copy(DefaultMappingPath, output, true);
        using ZipArchive zip = ZipFile.Open(output, ZipArchiveMode.Update);
        XmlDocument shared = Load(zip, "xl/sharedStrings.xml");
        XmlDocument sheet = Load(zip, "xl/worksheets/sheet1.xml");
        var ns = new XmlNamespaceManager(sheet.NameTable);
        ns.AddNamespace("m", Main);
        var sns = new XmlNamespaceManager(shared.NameTable);
        sns.AddNamespace("m", Main);
        List<string> strings = shared.SelectNodes("//m:si", sns)!.Cast<XmlNode>().Select(n => n.InnerText).ToList();
        XmlNode data = sheet.SelectSingleNode("//m:sheetData", ns)!;

        string CellText(XmlNode? c)
        {
            if (c is null)
                return "";
            string? t = c.Attributes?["t"]?.Value;
            string v = c.SelectSingleNode("m:v", ns)?.InnerText ?? "";
            return t == "s" && int.TryParse(v, out int i) && i < strings.Count ? strings[i] : t == "inlineStr" ? c.InnerText : v;
        }

        int StringIndex(string value)
        {
            int idx = strings.IndexOf(value);
            if (idx >= 0)
                return idx;
            XmlElement si = shared.CreateElement("si", Main);
            XmlElement st = shared.CreateElement("t", Main);
            st.InnerText = value;
            si.AppendChild(st);
            shared.DocumentElement!.AppendChild(si);
            strings.Add(value);
            return strings.Count - 1;
        }

        void SetCell(XmlElement row, string col, object value)
        {
            string r = col + row.GetAttribute("r");
            if (row.SelectSingleNode($"m:c[@r='{r}']", ns) is not XmlElement c)
            {
                c = sheet.CreateElement("c", Main);
                row.AppendChild(c);
            }
            c.RemoveAll();
            c.SetAttribute("r", r);
            c.SetAttribute("s", "2");
            XmlElement v = sheet.CreateElement("v", Main);
            if (value is bool b)
            {
                c.SetAttribute("t", "b");
                v.InnerText = b ? "1" : "0";
            }
            else
            {
                c.SetAttribute("t", "s");
                v.InnerText = StringIndex(value?.ToString() ?? "").ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            c.AppendChild(v);
        }

        var existing = new Dictionary<string, XmlElement>(StringComparer.OrdinalIgnoreCase);
        int last = 0;
        foreach (XmlElement row in data.SelectNodes("m:row", ns)!.Cast<XmlElement>())
        {
            string a = CellText(row.SelectSingleNode("m:c[starts-with(@r,'A')]", ns));
            string bName = CellText(row.SelectSingleNode("m:c[starts-with(@r,'B')]", ns));
            if (a.Length > 0 && bName.Length > 0)
                existing[a + "|" + bName] = row;
            if (row.SelectSingleNode("m:c", ns) is not null)
                last = Math.Max(last, int.Parse(row.GetAttribute("r"), System.Globalization.CultureInfo.InvariantCulture));
        }

        int changed = 0, added = 0;
        // "CodeName" e do plugin (pos-processamento do .ifc): a extensao nao conhece.
        foreach (IfcMapRow m in rows.Where(r => !string.IsNullOrWhiteSpace(r.Nome) && r.Mapeamento != "CodeName"))
        {
            if (existing.TryGetValue(m.Mapeamento + "|" + m.Nome, out XmlElement? row))
            {
                changed++;
            }
            else
            {
                last++;
                row = data.SelectSingleNode($"m:row[@r='{last}']", ns) as XmlElement;
                if (row is null)
                {
                    row = sheet.CreateElement("row", Main);
                    row.SetAttribute("r", last.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    data.AppendChild(row);
                }
                SetCell(row, "A", m.Mapeamento);
                SetCell(row, "B", m.Nome);
                existing[m.Mapeamento + "|" + m.Nome] = row;
                added++;
            }
            SetCell(row, "C", m.Exportar);
            SetCell(row, "D", m.Exportar ? m.ClasseIfc ?? "" : "");
        }

        if (sheet.SelectSingleNode("//m:dimension", ns) is XmlElement dim)
            dim.SetAttribute("ref", $"A1:H{Math.Max(last, 422)}");
        shared.DocumentElement!.SetAttribute("uniqueCount", strings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Save(zip, "xl/sharedStrings.xml", shared);
        Save(zip, "xl/worksheets/sheet1.xml", sheet);
        return (changed, added);
    }

    private static XmlDocument Load(ZipArchive zip, string name)
    {
        var doc = new XmlDocument();
        using Stream s = zip.GetEntry(name)!.Open();
        doc.Load(s);
        return doc;
    }

    private static void Save(ZipArchive zip, string name, XmlDocument doc)
    {
        zip.GetEntry(name)?.Delete();
        using Stream s = zip.CreateEntry(name).Open();
        using var w = XmlWriter.Create(s, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        doc.Save(w);
    }
}
