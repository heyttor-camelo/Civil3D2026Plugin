using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>
/// Definicoes das imagens (foto do local e mapa) usadas dentro dos blocos de ficha
/// (PedFichaBlock), e limpeza das imagens soltas da versao 1.2.
/// </summary>
internal static class PedPhotos
{
    public const string KindFoto = "FOTO";
    public const string KindMapa = "MAPA";

    /// <summary>Caminho gravado no DWG (relativo quando possivel) e caminho absoluto para carregar.</summary>
    public readonly record struct ImageFile(string Saved, string Absolute);

    /// <summary>Imagens dos PEDs no espaco modelo: codigo -> ids.</summary>
    public static Dictionary<string, List<AcadDb.ObjectId>> FindImages(AcadDb.Transaction tr, AcadDb.Database db)
    {
        Dictionary<string, List<AcadDb.ObjectId>> map = new(StringComparer.OrdinalIgnoreCase);
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        Autodesk.AutoCAD.Runtime.RXClass imgClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AcadDb.RasterImage));

        foreach (AcadDb.ObjectId id in ms)
        {
            if (!id.ObjectClass.IsDerivedFrom(imgClass))
                continue;
            if (PedStore.CodeOf(tr.GetObject(id, AcadDb.OpenMode.ForRead)) is string code)
            {
                if (!map.TryGetValue(code, out List<AcadDb.ObjectId>? ids))
                    map[code] = ids = new List<AcadDb.ObjectId>();
                ids.Add(id);
            }
        }
        return map;
    }

    /// <summary>Definicao da imagem (dicionario ACAD_IMAGE_DICT), reaproveitada entre importacoes.</summary>
    internal static AcadDb.RasterImageDef? Definition(AcadDb.Transaction tr, AcadDb.Database db, string code, string kind, ImageFile f)
    {
        AcadDb.ObjectId dictId = AcadDb.RasterImageDef.GetImageDictionary(db);
        if (dictId.IsNull)
            dictId = AcadDb.RasterImageDef.CreateImageDictionary(db);
        AcadDb.DBDictionary dict = (AcadDb.DBDictionary)tr.GetObject(dictId, AcadDb.OpenMode.ForWrite);

        string key = "PED_" + PedStore.KeyOf(code) + "_" + kind;
        AcadDb.RasterImageDef def;
        if (dict.Contains(key))
        {
            def = (AcadDb.RasterImageDef)tr.GetObject(dict.GetAt(key), AcadDb.OpenMode.ForWrite);
            if (!string.Equals(def.SourceFileName, f.Saved, StringComparison.OrdinalIgnoreCase))
                def.SourceFileName = f.Saved;
        }
        else
        {
            def = new AcadDb.RasterImageDef { SourceFileName = f.Saved };
            dict.SetAt(key, def);
            tr.AddNewlyCreatedDBObject(def, true);
        }

        // Caminho relativo (PEDs_img\...) resolve pela pasta do DWG; se nao achar, usa o absoluto.
        try
        {
            def.Load();
        }
        catch
        {
            try
            {
                def.SourceFileName = f.Absolute;
                def.Load();
            }
            catch
            {
                return null;
            }
        }
        return def;
    }

    /// <summary>
    /// Remove o que a versao 1.2 criava no espaco modelo (imagens soltas e grupos PED_codigo):
    /// agora foto e mapa ficam dentro do bloco da ficha (multileader).
    /// </summary>
    public static int CleanupLegacy(AcadDb.Transaction tr, AcadDb.Database db)
    {
        int n = 0;
        foreach (List<AcadDb.ObjectId> ids in FindImages(tr, db).Values)
        {
            foreach (AcadDb.ObjectId id in ids.Where(i => !i.IsErased))
            {
                tr.GetObject(id, AcadDb.OpenMode.ForWrite).Erase();
                n++;
            }
        }

        AcadDb.DBDictionary groups = (AcadDb.DBDictionary)tr.GetObject(db.GroupDictionaryId, AcadDb.OpenMode.ForRead);
        List<(string Key, AcadDb.ObjectId Id)> old = new();
        foreach (AcadDb.DBDictionaryEntry e in groups)
        {
            if (e.Key.StartsWith("PED_", StringComparison.OrdinalIgnoreCase) &&
                tr.GetObject(e.Value, AcadDb.OpenMode.ForRead) is AcadDb.Group g && g.Description.Contains("bloco + foto + mapa", StringComparison.Ordinal))
                old.Add((e.Key, e.Value));
        }
        if (old.Count > 0)
        {
            groups.UpgradeOpen();
            foreach ((string key, AcadDb.ObjectId id) in old)
            {
                groups.Remove(key);
                tr.GetObject(id, AcadDb.OpenMode.ForWrite).Erase();
            }
        }
        return n;
    }
}
