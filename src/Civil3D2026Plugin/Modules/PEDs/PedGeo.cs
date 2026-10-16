using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.Modules.Quadros;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>
/// Converte lon/lat (WGS84, do KMZ) para coordenadas do desenho. Ordem:
/// 1) geolocalizacao do desenho (GEOGRAPHICLOCATION), qualquer sistema;
/// 2) sistema de coordenadas do Civil 3D (Drawing Settings > Units and Zone) com fuso UTM;
/// 3) fuso UTM informado (SIRGAS 2000).
/// SIRGAS 2000 e WGS84 coincidem na pratica (diferenca centimetrica).
/// </summary>
internal sealed class PedGeo
{
    private readonly Func<double, double, Point3d> _toDrawing;

    private PedGeo(string description, Func<double, double, Point3d> toDrawing)
    {
        Description = description;
        _toDrawing = toDrawing;
    }

    public string Description { get; }

    public Point3d ToDrawing(double longitude, double latitude) => _toDrawing(longitude, latitude);

    public static PedGeo? Create(Document doc, Editor ed)
    {
        AcadDb.Database db = doc.Database;

        PedGeo? fromGeo = TryGeoLocation(db);
        if (fromGeo is not null)
            return fromGeo;

        double f = MetersPerUnit(db);
        string code = string.Empty;
        try
        {
            code = Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument
                .Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode ?? string.Empty;
        }
        catch
        {
            // Sem Civil ou sem sistema configurado.
        }

        if (GeoConversion.TryParseZone(code, out int zone, out bool south))
            return Utm(zone, south, f, $"UTM fuso {zone}{(south ? "S" : "N")} (sistema do Civil 3D: {code})");

        ed.WriteMessage("\nO desenho nao tem geolocalizacao nem sistema de coordenadas UTM no Civil 3D." +
                        "\nAs coordenadas serao calculadas em UTM SIRGAS 2000 (E, N).");
        PromptStringOptions opt = new("\nFuso UTM (Belo Horizonte = 23S) <23S>: ") { AllowSpaces = false };
        PromptResult r = ed.GetString(opt);
        if (r.Status != PromptStatus.OK)
            return null;

        string text = string.IsNullOrWhiteSpace(r.StringResult) ? "23S" : r.StringResult;
        if (!GeoConversion.TryParseZone(text, out zone, out south))
        {
            ed.WriteMessage("\nFuso invalido.");
            return null;
        }

        return Utm(zone, south, f, $"UTM fuso {zone}{(south ? "S" : "N")} (informado)");
    }

    private static PedGeo Utm(int zone, bool south, double metersPerUnit, string description) =>
        new(description, (lon, lat) =>
        {
            (double e, double n) = GeoConversion.GeographicToUtm(lat, lon, zone, south);
            return new Point3d(e / metersPerUnit, n / metersPerUnit, 0);
        });

    private static PedGeo? TryGeoLocation(AcadDb.Database db)
    {
        try
        {
            AcadDb.ObjectId geoId = db.GeoDataObject;
            if (geoId.IsNull)
                return null;

            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            if (tr.GetObject(geoId, AcadDb.OpenMode.ForRead, false) is not AcadDb.GeoLocationData geo ||
                string.IsNullOrWhiteSpace(geo.CoordinateSystem))
                return null;

            // A conversao precisa funcionar.
            geo.TransformFromLonLatAlt(geo.TransformToLonLatAlt(geo.DesignPoint));

            return new PedGeo("geolocalizacao do desenho", (lon, lat) =>
            {
                using AcadDb.Transaction t = db.TransactionManager.StartOpenCloseTransaction();
                AcadDb.GeoLocationData g = (AcadDb.GeoLocationData)t.GetObject(geoId, AcadDb.OpenMode.ForRead, false);
                Point3d p = g.TransformFromLonLatAlt(new Point3d(lon, lat, 0));
                return new Point3d(p.X, p.Y, 0);
            });
        }
        catch
        {
            return null;
        }
    }

    private static double MetersPerUnit(AcadDb.Database db) => db.Insunits switch
    {
        AcadDb.UnitsValue.Millimeters => 0.001,
        AcadDb.UnitsValue.Centimeters => 0.01,
        AcadDb.UnitsValue.Kilometers => 1000.0,
        AcadDb.UnitsValue.Inches => 0.0254,
        AcadDb.UnitsValue.Feet => 0.3048,
        _ => 1.0
    };
}
