using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using AcBr = Autodesk.AutoCAD.BoundaryRepresentation;

using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>Uma travessia (ou uniao de cruzamento) como esta no desenho, para o painel de rebaixos.</summary>
internal sealed class TravessiaView
{
    public string Id { get; init; } = "";

    /// <summary>Dados guardados no solido do plato; null numa uniao (ou se o plato sumiu).</summary>
    public MFTravessia.Stored? Data { get; set; }

    public bool IsUnion => Id.StartsWith("U", StringComparison.Ordinal) && Data is null;

    /// <summary>Contornos das hachuras de planta, por parte (PLANTA_PLATO, PLANTA_RAMPA, FAIXA...).</summary>
    public List<(string Part, List<Point2d> Loop)> Plan { get; } = new();

    /// <summary>Triangulos dos solidos (plato, tampas, pintura), por parte, para a vista 3D.</summary>
    public List<(string Part, Point3d A, Point3d B, Point3d C)> Tris { get; } = new();

    /// <summary>Solidos da travessia: parte, handle, volume e as propriedades gravadas (Property Set).</summary>
    public List<(string Part, string Handle, double Volume, Dictionary<string, string> Props)> Solids { get; } = new();

    public int Objects { get; set; }
    public double Volume { get; set; }
    public double MinX { get; set; } = double.MaxValue;
    public double MinY { get; set; } = double.MaxValue;
    public double MaxX { get; set; } = double.MinValue;
    public double MaxY { get; set; } = double.MinValue;
    public double MinZ { get; set; } = double.MaxValue;
    public double MaxZ { get; set; } = double.MinValue;

    public bool HasBox => MinX <= MaxX;

    public void Grow(Point3d p)
    {
        MinX = Math.Min(MinX, p.X);
        MinY = Math.Min(MinY, p.Y);
        MaxX = Math.Max(MaxX, p.X);
        MaxY = Math.Max(MaxY, p.Y);
        MinZ = Math.Min(MinZ, p.Z);
        MaxZ = Math.Max(MaxZ, p.Z);
    }
}

internal static partial class MFTravessia
{
    /// <summary>
    /// Le as travessias e unioes do desenho: hachuras de planta (contornos) e solidos em triangulos
    /// (Brep do AutoCAD), com a caixa e o volume do plato. Abre a propria transacao.
    /// </summary>
    public static List<TravessiaView> ReadViews(AcadDb.Database db)
    {
        var views = new Dictionary<string, TravessiaView>();
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        var bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        var ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (id.IsErased || tr.GetObject(id, AcadDb.OpenMode.ForRead) is not AcadDb.Entity e || PartOf(e) is not { } part)
                continue;
            if (!views.TryGetValue(part.Id, out TravessiaView? v))
                views[part.Id] = v = new TravessiaView { Id = part.Id };
            v.Objects++;
            try
            {
                if (part.Part == PartPlato)
                    v.Data = ReadStored(e);
                switch (e)
                {
                    case AcadDb.Hatch h:
                        for (int i = 0; i < h.NumberOfLoops; i++)
                        {
                            AcadDb.HatchLoop loop = h.GetLoopAt(i);
                            if (!loop.IsPolyline)
                                continue;
                            var pts = new List<Point2d>();
                            foreach (AcadDb.BulgeVertex bv in loop.Polyline)
                                pts.Add(bv.Vertex);
                            if (pts.Count >= 3)
                                v.Plan.Add((part.Part, pts));
                        }
                        break;
                    case AcadDb.Solid3d s:
                    {
                        double vol = 0;
                        try
                        {
                            vol = s.MassProperties.Volume;
                        }
                        catch
                        {
                            // Sem volume.
                        }
                        if (part.Part is PartPlato or "UNIAO_PLATO")
                            v.Volume += vol;
                        v.Solids.Add((part.Part, s.Handle.ToString(), vol, ReadProps(tr, s)));
                        Tessellate(s, part.Part, v);
                        break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Aviso("MFTRAVESSIA", $"Painel: parte {part.Part} da travessia {part.Id} nao lida: {ex.Message}");
            }
        }
        tr.Commit();
        return views.Values.OrderBy(x => x.IsUnion).ThenBy(x => x.Data?.LabelA ?? x.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, string> ReadProps(AcadDb.Transaction tr, AcadDb.Entity e)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (AcadDb.ObjectId psId in Autodesk.Aec.PropertyData.DatabaseServices.PropertyDataServices.GetPropertySets(e))
            {
                if (tr.GetObject(psId, AcadDb.OpenMode.ForRead) is not Autodesk.Aec.PropertyData.DatabaseServices.PropertySet ps || ps.PropertySetDefinitionName != PropertySetName)
                    continue;
                foreach (Autodesk.Aec.PropertyData.DatabaseServices.PropertySetData d in ps.PropertySetData)
                    map[ps.PropertyIdToName(d.Id)] = Convert.ToString(d.GetData(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
            }
        }
        catch
        {
            // Sem Property Set.
        }
        return map;
    }

    private static void Tessellate(AcadDb.Solid3d solid, string part, TravessiaView v)
    {
        using var brep = new AcBr.Brep(solid);
        using var control = new AcBr.Mesh2dControl();
        using var filter = new AcBr.Mesh2dFilter();
        filter.Insert(brep, control);
        using var mesh = new AcBr.Mesh2d(filter);
        foreach (AcBr.Element2d element in mesh.Element2ds)
        {
            var pts = new List<Point3d>();
            foreach (AcBr.Node node in element.Nodes)
            {
                pts.Add(node.Point);
                node.Dispose();
            }
            element.Dispose();
            for (int i = 1; i + 1 < pts.Count; i++)
                v.Tris.Add((part, pts[0], pts[i], pts[i + 1]));
            foreach (Point3d p in pts)
                v.Grow(p);
        }
    }

    /// <summary>Apaga a travessia (todas as partes); se ela era de uma uniao, a uniao e refeita sem ela.</summary>
    public static int EraseTravessia(AcadDb.Database db, Autodesk.AutoCAD.EditorInput.Editor ed, string id)
    {
        Stored? data = FindAll(db).Select(x => x.Data).FirstOrDefault(d => d.Id == id);
        int n;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            n = Erase(tr, db, id);
            tr.Commit();
        }
        if (data is not null && data.UnionId.Length > 0)
            RedoUnions(db, ed, new List<string> { data.UnionId });
        return n;
    }

    /// <summary>Redesenha uma travessia com os dados dela (os de agora, ou os novos parametros).</summary>
    public static (int Count, string Info) Redraw(AcadDb.Database db, Autodesk.AutoCAD.EditorInput.Editor ed, Stored d)
    {
        MFRebaixoSettings settings = MFRebaixoSettingsStore.Load();
        List<RebaixoInfo> rebaixos = MFRebaixoData.ReadAll(db);
        TravessiaGeometry g = GeometryFor(db, d, rebaixos, out string roadInfo);
        (int count, List<string> notes) = Draw(db, g, d, settings);
        if (d.UnionId.Length > 0)
            RedoUnions(db, ed, new List<string> { d.UnionId });
        string info = $"{roadInfo}; {string.Join("; ", notes.Concat(g.Notes))}. {Summary(db, d.Id)}";
        PluginLog.Info("MFTRAVESSIA", $"Painel: travessia {d.Id} redesenhada: {count} objeto(s). {info}");
        return (count, info);
    }
}
