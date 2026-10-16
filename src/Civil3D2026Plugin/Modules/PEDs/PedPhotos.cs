using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>
/// Foto do local e mapa de localizacao como imagens raster ao lado da ficha do bloco
/// (layer PED-FOTO). Cada imagem leva XData C3DPED = (codigo, FOTO/MAPA) e fica num
/// grupo com o bloco (PED_codigo) para andar junto ao mover.
///
/// Imagem nao pode ficar dentro da definicao do bloco (seria a mesma em todos os PEDs),
/// por isso fica fora: liga/desliga pela layer PED-FOTO ou pelo PEDVISTA.
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

    /// <summary>
    /// Recoloca foto e mapa ao lado da ficha do bloco (apaga as imagens antigas do PED).
    /// <paramref name="visible"/> null = mantem a visibilidade das imagens antigas (ou visivel se nao havia).
    /// Retorna os ids das imagens criadas.
    /// </summary>
    public static List<AcadDb.ObjectId> Place(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockReference br, string code,
                                              ImageFile? foto, ImageFile? mapa, IEnumerable<AcadDb.ObjectId> old, bool? visible)
    {
        bool show = visible ?? true;
        foreach (AcadDb.ObjectId id in old)
        {
            if (id.IsErased)
                continue;
            AcadDb.Entity e = (AcadDb.Entity)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
            if (visible is null)
                show = e.Visible;
            e.Erase();
        }

        List<AcadDb.ObjectId> created = new();
        (double x, double y, double w) = PedBlock.PhotoSlot;
        double top = y;
        foreach ((ImageFile? file, string kind) in new[] { (foto, KindFoto), (mapa, KindMapa) })
        {
            if (file is not ImageFile f || f.Saved.Length == 0)
                continue;
            AcadDb.RasterImageDef? def = Definition(tr, db, code, kind, f);
            if (def is null)
                continue;

            double aspect = def.Size.X > 0 ? def.Size.Y / def.Size.X : 0.6;
            double h = w * aspect;

            Matrix3d xf = br.BlockTransform;
            Point3d origin = new Point3d(x, top - h, 0).TransformBy(xf);
            Vector3d u = new Vector3d(w, 0, 0).TransformBy(xf);
            Vector3d v = new Vector3d(0, h, 0).TransformBy(xf);

            AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
            AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

            AcadDb.RasterImage img = new();
            img.SetDatabaseDefaults(db);
            img.Layer = PedBlock.LayerFoto;
            img.ImageDefId = def.ObjectId;
            img.Orientation = new CoordinateSystem3d(origin, u, v);
            img.ShowImage = true;
            AcadDb.ObjectId id = ms.AppendEntity(img);
            tr.AddNewlyCreatedDBObject(img, true);
            AcadDb.RasterImage.EnableReactors(true);
            img.AssociateRasterDef(def);
            img.XData = new AcadDb.ResultBuffer(
                new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, PedStore.RegApp),
                new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, code),
                new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, kind));
            img.Visible = show;

            created.Add(id);
            top -= h + 1.0;
        }

        return created;
    }

    /// <summary>Definicao da imagem (dicionario ACAD_IMAGE_DICT), reaproveitada entre importacoes.</summary>
    private static AcadDb.RasterImageDef? Definition(AcadDb.Transaction tr, AcadDb.Database db, string code, string kind, ImageFile f)
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

    /// <summary>Grupo PED_codigo com o bloco e as imagens (mover o bloco leva as imagens).</summary>
    public static void Group(AcadDb.Transaction tr, AcadDb.Database db, string code, IEnumerable<AcadDb.ObjectId> ids)
    {
        string name = "PED_" + PedStore.KeyOf(code);
        AcadDb.DBDictionary groups = (AcadDb.DBDictionary)tr.GetObject(db.GroupDictionaryId, AcadDb.OpenMode.ForWrite);
        if (groups.Contains(name))
        {
            AcadDb.ObjectId oldId = groups.GetAt(name);
            groups.Remove(name);
            tr.GetObject(oldId, AcadDb.OpenMode.ForWrite).Erase();
        }

        List<AcadDb.ObjectId> list = ids.Where(i => !i.IsNull && !i.IsErased).ToList();
        if (list.Count < 2)
            return;

        AcadDb.Group g = new("PED " + code + " (bloco + foto + mapa)", true);
        groups.SetAt(name, g);
        tr.AddNewlyCreatedDBObject(g, true);
        g.Append(new AcadDb.ObjectIdCollection(list.ToArray()));
    }
}
