using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>
/// Le os pontos de um .kmz/.kml (Google Earth, TransCAD etc.): nome do placemark e
/// lon/lat WGS84. Ignora namespaces (ha arquivos com xmlns="" no Document) e
/// placemarks repetidos com o mesmo nome (rotulos duplicados): fica o primeiro.
/// </summary>
internal static class PedKml
{
    public static List<PedPoint> Read(string path)
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

        List<PedPoint> points = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (XElement pm in doc.Descendants().Where(e => e.Name.LocalName == "Placemark"))
        {
            string name = Child(pm, "name")?.Value.Trim() ?? string.Empty;
            if (name.Length == 0 || seen.Contains(name))
                continue;

            XElement? point = pm.Descendants().FirstOrDefault(e => e.Name.LocalName == "Point");
            XElement? coords = point?.Descendants().FirstOrDefault(e => e.Name.LocalName == "coordinates");
            if (coords is null)
                continue;

            string[] parts = coords.Value.Trim().Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                   .First().Split(',');
            if (parts.Length < 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
                continue;

            string style = Child(pm, "styleUrl")?.Value.Trim().TrimStart('#') ?? string.Empty;
            seen.Add(name);
            points.Add(new PedPoint(name, lon, lat, style));
        }

        return points;
    }

    private static XElement? Child(XElement e, string localName) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName == localName);
}
