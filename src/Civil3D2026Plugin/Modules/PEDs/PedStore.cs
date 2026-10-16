using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>Dados de importacao gravados no DWG.</summary>
internal sealed class PedMeta
{
    public string XlsxPath { get; set; } = string.Empty;
    public string KmzPath { get; set; } = string.Empty;
    public string ImageFolder { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ImportedAt { get; set; } = string.Empty;
    public double BlockScale { get; set; } = 1.0;
}

/// <summary>Um PED como fica gravado no DWG (dados da planilha + nomes das imagens extraidas).</summary>
internal sealed class PedStored
{
    public string Code { get; set; } = string.Empty;
    public double? Longitude { get; set; }
    public double? Latitude { get; set; }
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Letra da coluna -> nome do arquivo na pasta de imagens.</summary>
    public Dictionary<string, string> Images { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Get(string letter) => Values.TryGetValue(letter, out string? v) ? v : string.Empty;
}

/// <summary>
/// Persistencia do modulo PEDs no DWG (o desenho nao depende da planilha depois de importado).
///
/// - NOD / C3D_PED_V1 / META: caminhos, aba, titulo, escala do bloco.
/// - NOD / C3D_PED_V1 / COLS: letra e rotulo de cada coluna da aba de dados.
/// - NOD / C3D_PED_V1 / P_codigo: valores da linha do PED e imagens.
/// - Bloco inserido: XData "C3DPED" = (codigo). Copiar o bloco copia o vinculo.
/// </summary>
internal static class PedStore
{
    public const string RegApp = "C3DPED";

    private const string RootName = "C3D_PED_V1";
    private const string MetaKey = "META";
    private const string ColsKey = "COLS";
    private const string PedPrefix = "P_";
    private const string MetaSig = "PEDMETA1";
    private const string ColsSig = "PEDCOLS1";
    private const string PedSig = "PED1";
    private const string ImgMark = "\u0001IMG";

    private static int TextCode => (int)AcadDb.DxfCode.Text;
    private static int RealCode => (int)AcadDb.DxfCode.Real;

    // ------------------------------------------------------------------
    // Leitura
    // ------------------------------------------------------------------

    public static bool HasData(AcadDb.Database db)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        return Root(tr, db, create: false) is not null;
    }

    public static PedMeta? LoadMeta(AcadDb.Database db)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.DBDictionary? root = Root(tr, db, create: false);
        List<string>? v = root is null ? null : ReadTexts(tr, root, MetaKey);
        if (v is null || v.Count < 7 || v[0] != MetaSig)
            return null;

        return new PedMeta
        {
            XlsxPath = v[1],
            KmzPath = v[2],
            ImageFolder = v[3],
            SheetName = v[4],
            Title = v[5],
            ImportedAt = v[6],
            BlockScale = v.Count > 7 && double.TryParse(v[7], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 0 ? s : 1.0
        };
    }

    public static List<PedColumn> LoadColumns(AcadDb.Database db)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.DBDictionary? root = Root(tr, db, create: false);
        List<string>? v = root is null ? null : ReadTexts(tr, root, ColsKey);
        List<PedColumn> cols = new();
        if (v is null || v.Count == 0 || v[0] != ColsSig)
            return cols;
        for (int i = 1; i + 1 < v.Count; i += 2)
            cols.Add(new PedColumn(v[i], v[i + 1]));
        return cols;
    }

    public static List<PedStored> LoadAll(AcadDb.Database db)
    {
        List<PedStored> list = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.DBDictionary? root = Root(tr, db, create: false);
        if (root is null)
            return list;

        foreach (AcadDb.DBDictionaryEntry entry in root)
        {
            if (!entry.Key.StartsWith(PedPrefix, StringComparison.Ordinal))
                continue;
            if (tr.GetObject(entry.Value, AcadDb.OpenMode.ForRead) is AcadDb.Xrecord xr && Parse(xr) is PedStored p)
                list.Add(p);
        }

        return list;
    }

    public static PedStored? Load(AcadDb.Database db, string code)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.DBDictionary? root = Root(tr, db, create: false);
        string key = PedPrefix + KeyOf(code);
        if (root is null || !root.Contains(key))
            return null;
        return tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForRead) is AcadDb.Xrecord xr ? Parse(xr) : null;
    }

    private static PedStored? Parse(AcadDb.Xrecord xr)
    {
        if (xr.Data is null)
            return null;

        AcadDb.TypedValue[] tv = xr.Data.AsArray();
        if (tv.Length < 2 || tv[0].Value as string != PedSig)
            return null;

        PedStored p = new() { Code = tv[1].Value as string ?? string.Empty };
        int i = 2;
        if (i + 1 < tv.Length && tv[i].TypeCode == RealCode && tv[i + 1].TypeCode == RealCode)
        {
            p.Longitude = (double)tv[i].Value;
            p.Latitude = (double)tv[i + 1].Value;
            i += 2;
        }

        bool images = false;
        for (; i + 1 < tv.Length; i += 2)
        {
            string k = tv[i].Value as string ?? string.Empty;
            if (k == ImgMark)
            {
                images = true;
                i--; // a marca nao tem par
                continue;
            }

            string v = tv[i + 1].Value as string ?? string.Empty;
            if (images)
                p.Images[k] = v;
            else
                p.Values[k] = v;
        }

        return p;
    }

    // ------------------------------------------------------------------
    // Gravacao (dentro da transacao do comando)
    // ------------------------------------------------------------------

    public static void SaveMeta(AcadDb.Transaction tr, AcadDb.Database db, PedMeta m)
    {
        AcadDb.DBDictionary root = Root(tr, db, create: true)!;
        WriteTexts(tr, root, MetaKey, new[]
        {
            MetaSig, m.XlsxPath, m.KmzPath, m.ImageFolder, m.SheetName, m.Title, m.ImportedAt,
            m.BlockScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    public static void SaveColumns(AcadDb.Transaction tr, AcadDb.Database db, IEnumerable<PedColumn> cols)
    {
        AcadDb.DBDictionary root = Root(tr, db, create: true)!;
        List<string> v = new() { ColsSig };
        foreach (PedColumn c in cols)
        {
            v.Add(c.Letter);
            v.Add(c.Label);
        }
        WriteTexts(tr, root, ColsKey, v);
    }

    public static void Save(AcadDb.Transaction tr, AcadDb.Database db, PedStored p)
    {
        AcadDb.DBDictionary root = Root(tr, db, create: true)!;
        List<AcadDb.TypedValue> tv = new()
        {
            new(TextCode, PedSig),
            new(TextCode, p.Code)
        };
        if (p.Longitude is double lon && p.Latitude is double lat)
        {
            tv.Add(new(RealCode, lon));
            tv.Add(new(RealCode, lat));
        }
        foreach ((string k, string v) in p.Values)
        {
            tv.Add(new(TextCode, k));
            tv.Add(new(TextCode, Limit(v)));
        }
        tv.Add(new(TextCode, ImgMark));
        foreach ((string k, string v) in p.Images)
        {
            tv.Add(new(TextCode, k));
            tv.Add(new(TextCode, v));
        }

        Write(tr, root, PedPrefix + KeyOf(p.Code), new AcadDb.ResultBuffer(tv.ToArray()));
    }

    /// <summary>Remove do DWG os PEDs que nao estao mais na planilha (os blocos ficam, sem ficha).</summary>
    public static int RemoveMissing(AcadDb.Transaction tr, AcadDb.Database db, ICollection<string> keep)
    {
        AcadDb.DBDictionary? root = Root(tr, db, create: false);
        if (root is null)
            return 0;

        HashSet<string> keys = keep.Select(c => PedPrefix + KeyOf(c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<(string Key, AcadDb.ObjectId Id)> remove = new();
        foreach (AcadDb.DBDictionaryEntry e in root)
        {
            if (e.Key.StartsWith(PedPrefix, StringComparison.Ordinal) && !keys.Contains(e.Key))
                remove.Add((e.Key, e.Value));
        }

        if (remove.Count == 0)
            return 0;

        root.UpgradeOpen();
        foreach ((string key, AcadDb.ObjectId id) in remove)
        {
            root.Remove(key);
            tr.GetObject(id, AcadDb.OpenMode.ForWrite).Erase();
        }
        return remove.Count;
    }

    // ------------------------------------------------------------------
    // Vinculo no bloco (XData)
    // ------------------------------------------------------------------

    public static void EnsureRegApp(AcadDb.Transaction tr, AcadDb.Database db)
    {
        AcadDb.RegAppTable table = (AcadDb.RegAppTable)tr.GetObject(db.RegAppTableId, AcadDb.OpenMode.ForRead);
        if (table.Has(RegApp))
            return;

        table.UpgradeOpen();
        AcadDb.RegAppTableRecord record = new() { Name = RegApp };
        table.Add(record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    public static void Tag(AcadDb.Entity entity, string code)
    {
        entity.XData = new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, RegApp),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, code));
    }

    /// <summary>Codigo do PED vinculado ao objeto, ou null.</summary>
    public static string? CodeOf(AcadDb.DBObject obj)
    {
        using AcadDb.ResultBuffer? rb = obj.GetXDataForApplication(RegApp);
        if (rb is null)
            return null;
        foreach (AcadDb.TypedValue tv in rb.AsArray())
        {
            if (tv.TypeCode == (short)AcadDb.DxfCode.ExtendedDataAsciiString && tv.Value is string s && s.Length > 0)
                return s;
        }
        return null;
    }

    /// <summary>Blocos PED do espaco modelo: codigo -> ids (pode haver copias).</summary>
    public static Dictionary<string, List<AcadDb.ObjectId>> FindBlocks(AcadDb.Transaction tr, AcadDb.Database db)
    {
        Dictionary<string, List<AcadDb.ObjectId>> map = new(StringComparer.OrdinalIgnoreCase);
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        Autodesk.AutoCAD.Runtime.RXClass brClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AcadDb.BlockReference));

        foreach (AcadDb.ObjectId id in ms)
        {
            if (!id.ObjectClass.IsDerivedFrom(brClass))
                continue;
            AcadDb.DBObject obj = tr.GetObject(id, AcadDb.OpenMode.ForRead);
            if (CodeOf(obj) is string code)
            {
                if (!map.TryGetValue(code, out List<AcadDb.ObjectId>? ids))
                    map[code] = ids = new List<AcadDb.ObjectId>();
                ids.Add(id);
            }
        }

        return map;
    }

    // ------------------------------------------------------------------
    // Imagens
    // ------------------------------------------------------------------

    /// <summary>
    /// Caminho de uma imagem: na pasta gravada; se o projeto mudou de lugar,
    /// tenta PEDs_img ao lado do DWG atual.
    /// </summary>
    public static string? ImagePath(AcadDb.Database db, PedMeta? meta, string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        List<string> folders = new();
        if (meta is not null && meta.ImageFolder.Length > 0)
            folders.Add(meta.ImageFolder);
        string dwgDir = DwgFolder(db);
        if (dwgDir.Length > 0)
            folders.Add(Path.Combine(dwgDir, PedImages.FolderName));

        foreach (string f in folders)
        {
            string p = Path.Combine(f, fileName);
            if (File.Exists(p))
                return p;
        }
        return null;
    }

    public static string DwgFolder(AcadDb.Database db)
    {
        try
        {
            string name = db.Filename;
            if (string.IsNullOrEmpty(name) || name.EndsWith(".dwt", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            // Desenho novo ainda nao salvo aponta para o template/autosave.
            if (!File.Exists(name) || name.Contains(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            return Path.GetDirectoryName(name) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ------------------------------------------------------------------
    // Auxiliares
    // ------------------------------------------------------------------

    /// <summary>Chave de dicionario segura (sem caracteres proibidos em nomes de simbolo).</summary>
    public static string KeyOf(string code)
    {
        StringBuilder sb = new();
        foreach (char c in code.Trim().ToUpperInvariant())
            sb.Append("<>/\\\":;?*|,=`".IndexOf(c) >= 0 || char.IsControl(c) ? '_' : c);
        return sb.ToString();
    }

    private static string Limit(string s) => s.Length > 2000 ? s[..2000] : s;

    private static AcadDb.DBDictionary? Root(AcadDb.Transaction tr, AcadDb.Database db, bool create)
    {
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
        if (nod.Contains(RootName))
            return (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(RootName), create ? AcadDb.OpenMode.ForWrite : AcadDb.OpenMode.ForRead);
        if (!create)
            return null;

        nod.UpgradeOpen();
        AcadDb.DBDictionary root = new();
        nod.SetAt(RootName, root);
        tr.AddNewlyCreatedDBObject(root, true);
        return root;
    }

    private static List<string>? ReadTexts(AcadDb.Transaction tr, AcadDb.DBDictionary root, string key)
    {
        if (!root.Contains(key))
            return null;
        if (tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForRead) is not AcadDb.Xrecord xr || xr.Data is null)
            return null;
        return xr.Data.AsArray().Select(t => t.Value as string ?? Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).ToList();
    }

    private static void WriteTexts(AcadDb.Transaction tr, AcadDb.DBDictionary root, string key, IEnumerable<string> values) =>
        Write(tr, root, key, new AcadDb.ResultBuffer(values.Select(v => new AcadDb.TypedValue(TextCode, Limit(v ?? string.Empty))).ToArray()));

    private static void Write(AcadDb.Transaction tr, AcadDb.DBDictionary root, string key, AcadDb.ResultBuffer data)
    {
        using (data)
        {
            if (root.Contains(key))
            {
                AcadDb.Xrecord existing = (AcadDb.Xrecord)tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForWrite);
                existing.Data = data;
                return;
            }

            if (!root.IsWriteEnabled)
                root.UpgradeOpen();
            AcadDb.Xrecord record = new() { Data = data };
            root.SetAt(key, record);
            tr.AddNewlyCreatedDBObject(record, true);
        }
    }
}
