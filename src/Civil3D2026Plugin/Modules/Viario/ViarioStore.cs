using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcColors = Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.Geometry;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Persistencia do modulo Viario no DWG.
///
/// - Eixos: XData "C3DVIARIO" = (EIXO, id do perfil). Copiar um eixo copia o
///   XData, entao a copia ja entra na rede.
/// - Linhas geradas: XData "C3DVIARIO" = (GERADO, nivel). Sao refeitas a cada
///   atualizacao; edicoes manuais nelas sao substituidas.
/// - Perfis e configuracao: dicionario C3D_VIARIO_V1 no NOD.
/// </summary>
internal static class ViarioStore
{
    public const string RegApp = "C3DVIARIO";
    public const string TagAxis = "EIXO";
    public const string TagGenerated = "GERADO";

    public const string LayerAxis = "VIARIO-EIXO";
    public const string LayerBordo = "VIARIO-BORDO";
    public const string LayerAlinhamento = "VIARIO-ALINHAMENTO";

    private const string RootName = "C3D_VIARIO_V1";
    private const string ProfilePrefix = "PERFIL_";
    private const string SettingsKey = "CONFIG";
    private const string ProfileSignature = "VPERF1";
    private const string SettingsSignature = "VCFG1";

    internal sealed class Settings
    {
        public string CurrentProfileId { get; set; } = "P1";

        /// <summary>Tolerancia para ponta de eixo encostar em outro eixo (m, unidades do desenho).</summary>
        public double SnapTolerance { get; set; } = 0.05;

        /// <summary>Distancia de captura das pontas ao lancar eixos (o ponto e puxado para o eixo vizinho).</summary>
        public double Capture { get; set; } = 1.0;
    }

    // ------------------------------------------------------------------
    // XData
    // ------------------------------------------------------------------

    public static string? ReadTag(AcadDb.DBObject obj, out string value)
    {
        value = string.Empty;
        AcadDb.ResultBuffer? rb = obj.GetXDataForApplication(RegApp);
        if (rb is null)
            return null;

        using (rb)
        {
            AcadDb.TypedValue[] values = rb.AsArray();
            string? tag = null;
            foreach (AcadDb.TypedValue tv in values)
            {
                if (tv.TypeCode != (short)AcadDb.DxfCode.ExtendedDataAsciiString)
                    continue;

                if (tag is null)
                {
                    tag = tv.Value as string;
                }
                else
                {
                    value = tv.Value as string ?? string.Empty;
                    break;
                }
            }

            return tag;
        }
    }

    public static bool IsAxis(AcadDb.DBObject obj, out string profileId) =>
        ReadTag(obj, out profileId) == TagAxis;

    public static bool IsGenerated(AcadDb.DBObject obj) =>
        ReadTag(obj, out _) == TagGenerated;

    public static void SetTag(AcadDb.Entity entity, string tag, string value)
    {
        entity.XData = new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, RegApp),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, tag),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, value));
    }

    public static void ClearTag(AcadDb.Entity entity)
    {
        entity.XData = new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, RegApp));
    }

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

    // ------------------------------------------------------------------
    // Layers
    // ------------------------------------------------------------------

    public static void EnsureLayers(AcadDb.Transaction tr, AcadDb.Database db)
    {
        EnsureLayer(tr, db, LayerAxis, 1);
        EnsureLayer(tr, db, LayerBordo, 2);
        EnsureLayer(tr, db, LayerAlinhamento, 8);
    }

    private static void EnsureLayer(AcadDb.Transaction tr, AcadDb.Database db, string name, short color)
    {
        AcadDb.LayerTable table = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        if (table.Has(name))
            return;

        table.UpgradeOpen();
        AcadDb.LayerTableRecord layer = new()
        {
            Name = name,
            Color = AcColors.Color.FromColorIndex(AcColors.ColorMethod.ByAci, color)
        };
        table.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    public static string LayerFor(EdgeLevel level) =>
        level == EdgeLevel.Bordo ? LayerBordo : LayerAlinhamento;

    // ------------------------------------------------------------------
    // Perfis e configuracao (NOD)
    // ------------------------------------------------------------------

    public static RoadProfile DefaultProfile() =>
        new() { Id = "P1", Name = "Local", Pista = 7.0, Calcada = 2.5, Raio = 6.0 };

    public static List<RoadProfile> LoadProfiles(AcadDb.Database db)
    {
        List<RoadProfile> result = new();

        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.DBDictionary? root = TryRoot(tr, db);
        if (root is not null)
        {
            foreach (AcadDb.DBDictionaryEntry entry in root)
            {
                if (!entry.Key.StartsWith(ProfilePrefix, StringComparison.Ordinal))
                    continue;

                if (tr.GetObject(entry.Value, AcadDb.OpenMode.ForRead) is not AcadDb.Xrecord xr)
                    continue;

                AcadDb.TypedValue[]? v = xr.Data?.AsArray();
                if (v is null || v.Length < 6 || v[0].Value as string != ProfileSignature)
                    continue;

                result.Add(new RoadProfile
                {
                    Id = v[1].Value as string ?? "P?",
                    Name = v[2].Value as string ?? "",
                    Pista = v[3].Value is double pista ? pista : 7.0,
                    Calcada = v[4].Value is double calcada ? calcada : 2.5,
                    Raio = v[5].Value is double raio ? raio : 6.0
                });
            }
        }

        if (result.Count == 0)
            result.Add(DefaultProfile());

        return result.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
    }

    public static void SaveProfile(AcadDb.Transaction tr, AcadDb.Database db, RoadProfile p)
    {
        WriteRecord(tr, db, ProfilePrefix + p.Id, new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, ProfileSignature),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, p.Id),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, p.Name),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.Pista),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.Calcada),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.Raio)));
    }

    public static void DeleteProfile(AcadDb.Transaction tr, AcadDb.Database db, string id)
    {
        AcadDb.DBDictionary? root = TryRoot(tr, db);
        string key = ProfilePrefix + id;
        if (root is not null && root.Contains(key))
            tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForWrite).Erase();
    }

    public static Settings LoadSettings(AcadDb.Database db)
    {
        Settings settings = new();

        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.DBDictionary? root = TryRoot(tr, db);
        if (root is null || !root.Contains(SettingsKey))
            return settings;

        if (tr.GetObject(root.GetAt(SettingsKey), AcadDb.OpenMode.ForRead) is not AcadDb.Xrecord xr)
            return settings;

        AcadDb.TypedValue[]? v = xr.Data?.AsArray();
        if (v is null || v.Length < 4 || v[0].Value as string != SettingsSignature)
            return settings;

        settings.CurrentProfileId = v[1].Value as string ?? "P1";
        settings.SnapTolerance = v[2].Value is double snap && snap > 0 ? snap : 0.05;
        settings.Capture = v[3].Value is double capture && capture >= 0 ? capture : 1.0;
        return settings;
    }

    public static void SaveSettings(AcadDb.Transaction tr, AcadDb.Database db, Settings s)
    {
        WriteRecord(tr, db, SettingsKey, new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, SettingsSignature),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, s.CurrentProfileId),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.SnapTolerance),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.Capture)));
    }

    public static string NextProfileId(IEnumerable<RoadProfile> profiles)
    {
        int max = 0;
        foreach (RoadProfile p in profiles)
        {
            if (p.Id.Length > 1 && p.Id[0] == 'P' &&
                int.TryParse(p.Id[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                max = Math.Max(max, n);
            }
        }

        return "P" + (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static AcadDb.DBDictionary? TryRoot(AcadDb.Transaction tr, AcadDb.Database db)
    {
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
        return nod.Contains(RootName)
            ? (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(RootName), AcadDb.OpenMode.ForRead)
            : null;
    }

    private static void WriteRecord(AcadDb.Transaction tr, AcadDb.Database db, string key, AcadDb.ResultBuffer data)
    {
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
        AcadDb.DBDictionary root;

        if (nod.Contains(RootName))
        {
            root = (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(RootName), AcadDb.OpenMode.ForWrite);
        }
        else
        {
            nod.UpgradeOpen();
            root = new AcadDb.DBDictionary();
            nod.SetAt(RootName, root);
            tr.AddNewlyCreatedDBObject(root, true);
        }

        if (root.Contains(key))
        {
            ((AcadDb.Xrecord)tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForWrite)).Data = data;
            return;
        }

        AcadDb.Xrecord record = new() { Data = data };
        root.SetAt(key, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    // ------------------------------------------------------------------
    // Conversao AutoCAD <-> GPath
    // ------------------------------------------------------------------

    /// <summary>Eixo do AutoCAD como caminho 2D (planta). Null se o tipo nao for suportado.</summary>
    public static GPath? ToPath(AcadDb.Curve curve)
    {
        switch (curve)
        {
            case AcadDb.Polyline pl:
            {
                if (pl.Normal.Z < 0.999)
                    return null;

                List<Seg> segs = new();
                int n = pl.NumberOfVertices;
                int count = pl.Closed ? n : n - 1;
                for (int i = 0; i < count; i++)
                {
                    Point2d a = pl.GetPoint2dAt(i);
                    Point2d b = pl.GetPoint2dAt((i + 1) % n);
                    segs.Add(new Seg(new V2(a.X, a.Y), new V2(b.X, b.Y), pl.GetBulgeAt(i)));
                }

                GPath path = new(segs);
                return path.IsEmpty ? null : path;
            }

            case AcadDb.Line line:
            {
                GPath path = new(new[]
                {
                    Seg.Line(new V2(line.StartPoint.X, line.StartPoint.Y), new V2(line.EndPoint.X, line.EndPoint.Y))
                });
                return path.IsEmpty ? null : path;
            }

            case AcadDb.Arc arc:
            {
                if (arc.Normal.Z < 0.999)
                    return null;

                double sweep = GMath.Norm(arc.EndAngle - arc.StartAngle);
                if (sweep < 1e-9)
                    return null;

                return new GPath(new[]
                {
                    Seg.FromArc(new V2(arc.Center.X, arc.Center.Y), arc.Radius, arc.StartAngle, sweep)
                });
            }

            default:
                return null;
        }
    }

    /// <summary>Polilinha (nao residente) com os segmentos dados.</summary>
    public static AcadDb.Polyline ToPolyline(IReadOnlyList<Seg> segs, bool closed)
    {
        AcadDb.Polyline pl = new();
        int index = 0;
        foreach (Seg s in segs)
            pl.AddVertexAt(index++, new Point2d(s.A.X, s.A.Y), s.Bulge, 0, 0);

        if (closed)
        {
            pl.Closed = true;
        }
        else if (segs.Count > 0)
        {
            V2 end = segs[^1].B;
            pl.AddVertexAt(index, new Point2d(end.X, end.Y), 0, 0, 0);
        }

        return pl;
    }

    // ------------------------------------------------------------------
    // Chave geometrica (para atualizar so o que mudou)
    // ------------------------------------------------------------------

    /// <summary>
    /// Chave independente do sentido e do vertice inicial: conjunto dos vertices
    /// e dos bulges (arredondados), nivel e se e fechada.
    /// </summary>
    public static string GeometryKey(EdgeLevel level, IEnumerable<(double X, double Y, double Bulge)> vertices, bool closed)
    {
        List<string> points = new();
        List<string> bulges = new();

        foreach ((double x, double y, double b) in vertices)
        {
            points.Add(Math.Round(x, 3).ToString("0.000", CultureInfo.InvariantCulture) + "," +
                       Math.Round(y, 3).ToString("0.000", CultureInfo.InvariantCulture));
            if (Math.Abs(b) > 1e-9)
                bulges.Add(Math.Round(Math.Abs(b), 5).ToString("0.00000", CultureInfo.InvariantCulture));
        }

        points.Sort(StringComparer.Ordinal);
        bulges.Sort(StringComparer.Ordinal);
        return ((int)level).ToString(CultureInfo.InvariantCulture) + (closed ? "C" : "O") + "|" +
               string.Join(";", points) + "|" + string.Join(";", bulges);
    }

    public static string GeometryKey(OutputLine line)
    {
        List<(double, double, double)> vertices = line.Segs.Select(s => (s.A.X, s.A.Y, s.Bulge)).ToList();
        if (!line.Closed && line.Segs.Count > 0)
            vertices.Add((line.Segs[^1].B.X, line.Segs[^1].B.Y, 0.0));
        return GeometryKey(line.Level, vertices, line.Closed);
    }

    public static string GeometryKey(AcadDb.Polyline pl, EdgeLevel level)
    {
        List<(double, double, double)> vertices = new();
        for (int i = 0; i < pl.NumberOfVertices; i++)
        {
            Point2d p = pl.GetPoint2dAt(i);
            bool lastOpen = !pl.Closed && i == pl.NumberOfVertices - 1;
            vertices.Add((p.X, p.Y, lastOpen ? 0.0 : pl.GetBulgeAt(i)));
        }

        return GeometryKey(level, vertices, pl.Closed);
    }

    public static EdgeLevel LevelOfLayer(string layer) =>
        string.Equals(layer, LayerAlinhamento, StringComparison.OrdinalIgnoreCase) ? EdgeLevel.Alinhamento : EdgeLevel.Bordo;
}
