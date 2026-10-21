using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Autodesk.Civil.ApplicationServices;
using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Catalogo de pecas (Part Builder) do Civil 3D e o catalogo CSBIM saneado: leitura dos .apc
/// (capitulos e pecas com GUID), deteccao do catalogo ativo pela API e importacao de familias e
/// tamanhos para uma parts list.
///
/// A API 2026 nao expoe o caminho do catalogo ativo (so o nativo SETPIPENETWORKCATALOG muda);
/// o que ela da e `PartsList.GetAvailablePartFamilies(dominio)`, que lista as familias do
/// catalogo ativo do desenho. Comparando esses GUIDs com os dos .apc descobre-se qual catalogo esta ativo.
/// </summary>
internal static class HydroCatalog
{
    private const string Module = "Hydro";

    /// <summary>Pasta padrao dos catalogos do Civil 3D 2026.</summary>
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "C3D 2026", "enu", "Pipes Catalog");

    public const string CsbimStructures = "U3929348-IRIWUEH-393J4J2-DIJFI";
    public const string CsbimPipes = "93920319-9R9OIJSD-OI3NJ3-IJFOE";

    public sealed record CatalogPart(string Guid, string Name, string Description, string Chapter, string PartType, string SubType, string Table);

    public sealed record CatalogInfo(string Folder, string Name, string Domain, List<CatalogPart> Parts, bool Sanitized)
    {
        public IEnumerable<string> Chapters => Parts.Select(p => p.Chapter).Distinct(StringComparer.OrdinalIgnoreCase);
        public bool IsCsbim => Path.GetFileName(Folder) is CsbimStructures or CsbimPipes;
    }

    /// <summary>Le todos os catalogos da pasta (um .apc por subpasta).</summary>
    public static List<CatalogInfo> Scan(string? root = null)
    {
        root ??= Root;
        List<CatalogInfo> list = new();
        if (!Directory.Exists(root))
            return list;
        foreach (string dir in Directory.GetDirectories(root))
        {
            string apc = Path.Combine(dir, Path.GetFileName(dir) + ".apc");
            if (!File.Exists(apc))
            {
                string? any = Directory.GetFiles(dir, "*.apc").FirstOrDefault();
                if (any is null)
                    continue;
                apc = any;
            }

            try
            {
                list.Add(Read(apc));
            }
            catch (Exception ex)
            {
                PluginLog.Diag(Module, $"Catalogo {dir} nao lido: {ex.Message}");
            }
        }

        return list;
    }

    public static CatalogInfo Read(string apcPath)
    {
        XmlDocument doc = new();
        doc.Load(apcPath);
        XmlElement rootEl = doc.DocumentElement ?? throw new InvalidOperationException("apc vazio");
        string name = rootEl.GetAttribute("name");
        string domain = doc.SelectSingleNode("//DomainSet/DomainEnum")?.InnerText.Trim() ?? "";
        List<CatalogPart> parts = new();
        foreach (XmlElement chapter in doc.SelectNodes("//Chapter")?.Cast<XmlElement>() ?? Enumerable.Empty<XmlElement>())
        {
            string chapterName = chapter.GetAttribute("name");
            foreach (XmlElement part in chapter.SelectNodes("Part")?.Cast<XmlElement>() ?? Enumerable.Empty<XmlElement>())
            {
                XmlElement? attrs = part.SelectSingleNode(".//AeccPartSearchAttribList") as XmlElement;
                parts.Add(new CatalogPart(
                    part.GetAttribute("id").Trim().ToUpperInvariant(),
                    part.GetAttribute("name"),
                    part.SelectSingleNode("Desc")?.InnerText.Trim() ?? part.GetAttribute("name"),
                    chapterName,
                    attrs?.GetAttribute("part_type") ?? "",
                    attrs?.GetAttribute("part_subtype") ?? "",
                    (part.SelectSingleNode("TableRef/URL") as XmlElement)?.GetAttribute("xlink:href") ?? ""));
            }
        }

        string folder = Path.GetDirectoryName(apcPath)!;
        bool sanitized = Directory.GetFiles(Path.GetDirectoryName(folder)!, "_saneamento_CSBIM_*.md").Length > 0 &&
                         Directory.Exists(Path.Combine(folder, "_quarentena"));
        return new CatalogInfo(folder, name, domain.Replace("_Domain", ""), parts, sanitized);
    }

    /// <summary>Familias disponiveis no catalogo ativo do desenho (API), por dominio.</summary>
    public static List<CivilDb.Styles.DataPartFamily> Available(CivilDb.DomainType domain)
    {
        try
        {
            return CivilDb.Styles.PartsList.GetAvailablePartFamilies(domain).ToList();
        }
        catch (Exception ex)
        {
            PluginLog.Aviso(Module, $"GetAvailablePartFamilies({domain}): {ex.Message}");
            return new List<CivilDb.Styles.DataPartFamily>();
        }
    }

    /// <summary>Qual catalogo da pasta esta ativo para o dominio: o que tem mais GUIDs em comum com as familias disponiveis.</summary>
    public static CatalogInfo? Active(CivilDb.DomainType domain, List<CatalogInfo> catalogs)
    {
        HashSet<string> guids = Available(domain).Select(f => f.GUID.Trim().ToUpperInvariant()).ToHashSet();
        if (guids.Count == 0)
            return null;
        string dom = domain == CivilDb.DomainType.Pipe ? "Pipe" : "Structure";
        return catalogs.Where(c => c.Domain == dom)
            .Select(c => (c, n: c.Parts.Count(p => guids.Contains(p.Guid))))
            .Where(x => x.n > 0).OrderByDescending(x => x.n).Select(x => x.c).FirstOrDefault();
    }

    /// <summary>Resultado da importacao para a parts list.</summary>
    public sealed class ImportReport
    {
        public int Families;
        public int Sizes;
        public int Skipped;
        public List<string> Errors { get; } = new();
    }

    /// <summary>
    /// Importa as familias dos capitulos escolhidos (ou todos) do catalogo ativo para a parts list.
    /// Tamanhos: para parametros de lista fechada (ex.: diametro de tubo) entra um tamanho por
    /// valor; para faixas entra um tamanho com o padrao (o valor exato e ajustado depois, por peca).
    /// </summary>
    public static ImportReport Import(AcadDb.Database db, string partsListName, CatalogInfo catalog, IEnumerable<string>? chapters)
    {
        ImportReport report = new();
        CivilDb.DomainType domain = catalog.Domain == "Pipe" ? CivilDb.DomainType.Pipe : CivilDb.DomainType.Structure;
        HashSet<string> wanted = chapters is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : chapters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> available = Available(domain).Select(f => f.GUID.Trim().ToUpperInvariant()).ToHashSet();

        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDocument cdoc = CivilDocument.GetCivilDocument(db);
        AcadDb.ObjectId plId = AcadDb.ObjectId.Null;
        foreach (AcadDb.ObjectId id in cdoc.Styles.PartsListSet)
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.PartsList pl && string.Equals(pl.Name, partsListName, StringComparison.OrdinalIgnoreCase))
            {
                plId = id;
                break;
            }
        }

        if (plId.IsNull)
            plId = cdoc.Styles.PartsListSet.Add(partsListName);
        CivilDb.Styles.PartsList list = (CivilDb.Styles.PartsList)tr.GetObject(plId, AcadDb.OpenMode.ForWrite, false);
        HashSet<string> already = new(StringComparer.OrdinalIgnoreCase);
        foreach (AcadDb.ObjectId fid in list.GetPartFamilyIdsByDomain(domain))
        {
            if (tr.GetObject(fid, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.PartFamily f)
                already.Add(f.GUID.Trim().ToUpperInvariant());
        }

        foreach (CatalogPart part in catalog.Parts)
        {
            if (wanted.Count > 0 && !wanted.Contains(part.Chapter))
                continue;
            if (!available.Contains(part.Guid))
            {
                report.Skipped++;
                report.Errors.Add($"{part.Chapter}/{part.Name}: nao esta no catalogo ativo do desenho (SETPIPENETWORKCATALOG / PARTCATALOGREGEN).");
                continue;
            }

            if (already.Contains(part.Guid))
            {
                report.Skipped++;
                continue;
            }

            try
            {
                list.AddPartFamilyByGuid(domain, part.Guid);
                report.Families++;
                AcadDb.ObjectId fid = FindFamily(tr, list, domain, part.Guid);
                if (!fid.IsNull && tr.GetObject(fid, AcadDb.OpenMode.ForWrite, false) is CivilDb.Styles.PartFamily fam)
                    report.Sizes += AddSizes(fam, part, report);
            }
            catch (Exception ex)
            {
                report.Errors.Add($"{part.Chapter}/{part.Name}: {ex.Message}");
            }
        }

        tr.Commit();
        return report;
    }

    private static AcadDb.ObjectId FindFamily(AcadDb.Transaction tr, CivilDb.Styles.PartsList list, CivilDb.DomainType domain, string guid)
    {
        foreach (AcadDb.ObjectId fid in list.GetPartFamilyIdsByDomain(domain))
        {
            if (tr.GetObject(fid, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.PartFamily f && string.Equals(f.GUID.Trim(), guid, StringComparison.OrdinalIgnoreCase))
                return fid;
        }

        return AcadDb.ObjectId.Null;
    }

    /// <summary>Um tamanho por valor da primeira lista fechada dimensional (diametro), ou um so com os padroes.</summary>
    private static int AddSizes(CivilDb.Styles.PartFamily fam, CatalogPart part, ImportReport report)
    {
        int n = 0;
        try
        {
            using CivilDb.Styles.SizeFilterRecord probe = new CivilDb.Styles.SizeFilterRecord(fam);
            CivilDb.Styles.SizeFilterField? listField = null;
            for (int i = 0; i < probe.ParamCount; i++)
            {
                CivilDb.Styles.SizeFilterField f = probe[i];
                if (f.DataSource == CivilDb.Styles.PartDataSourceType.FixedList && f.DataType == CivilDb.PartCatalogDataType.Double &&
                    f.Context is CivilDb.PartContextType.PipeInnerDiameter or CivilDb.PartContextType.StructInnerDiameter or CivilDb.PartContextType.PipeInnerWidth)
                {
                    listField = f;
                    break;
                }
            }

            if (listField is null || listField.ValueList is null || listField.ValueList.Count == 0)
            {
                using CivilDb.Styles.SizeFilterRecord one = new CivilDb.Styles.SizeFilterRecord(fam);
                fam.AddPartSize(one);
                return 1;
            }

            string name = listField.Name;
            int count = listField.ValueList.Count;
            List<object> values = new();
            for (int i = 0; i < count; i++)
                values.Add(listField.ValueList[i]);
            foreach (object v in values)
            {
                try
                {
                    using CivilDb.Styles.SizeFilterRecord rec = new CivilDb.Styles.SizeFilterRecord(fam);
                    rec[name].Value = v;
                    fam.AddPartSize(rec);
                    n++;
                }
                catch (Exception ex)
                {
                    report.Errors.Add($"{part.Name} tamanho {v}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            report.Errors.Add($"{part.Name} (tamanhos): {ex.Message}");
        }

        return n;
    }

    /// <summary>Familias da parts list que vieram de um catalogo CSBIM (pelo GUID).</summary>
    public static (int Total, int Csbim) CountCsbimFamilies(AcadDb.Database db, string partsListName, List<CatalogInfo> catalogs)
    {
        HashSet<string> csbim = catalogs.Where(c => c.IsCsbim).SelectMany(c => c.Parts).Select(p => p.Guid).ToHashSet();
        int total = 0, hit = 0;
        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            CivilDocument cdoc = CivilDocument.GetCivilDocument(db);
            foreach (AcadDb.ObjectId id in cdoc.Styles.PartsListSet)
            {
                if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not CivilDb.Styles.PartsList pl || !string.Equals(pl.Name, partsListName, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (CivilDb.DomainType d in new[] { CivilDb.DomainType.Structure, CivilDb.DomainType.Pipe })
                {
                    foreach (AcadDb.ObjectId fid in pl.GetPartFamilyIdsByDomain(d))
                    {
                        if (tr.GetObject(fid, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.PartFamily f)
                        {
                            total++;
                            if (csbim.Contains(f.GUID.Trim().ToUpperInvariant()))
                                hit++;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Diag(Module, "Parts list nao lida: " + ex.Message);
        }

        return (total, hit);
    }
}
