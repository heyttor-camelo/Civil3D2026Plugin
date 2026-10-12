using System;
using System.Globalization;

namespace Civil3D2026Plugin.Modules.Quadros;

/// <summary>
/// Conversao de coordenadas planas UTM para geograficas (latitude/longitude)
/// e formatacao em graus, minutos e segundos.
///
/// Elipsoide GRS80 (SIRGAS 2000); para fins de quadro de ruas a diferenca
/// para o WGS84 e desprezivel (submilimetrica). Formulas inversas de
/// Snyder (USGS PP 1395), precisao milimetrica dentro do fuso.
/// </summary>
internal static class GeoConversion
{
    private const double A = 6378137.0;
    private const double F = 1.0 / 298.257222101;
    private const double K0 = 0.9996;
    private const double FalseEasting = 500000.0;
    private const double FalseNorthingSouth = 10000000.0;

    /// <summary>UTM (fuso, hemisferio sul?) -> latitude/longitude em graus decimais (sul e oeste negativos).</summary>
    public static (double Latitude, double Longitude) UtmToGeographic(double easting, double northing, int zone, bool south)
    {
        double e2 = F * (2.0 - F);
        double ep2 = e2 / (1.0 - e2);
        double e1 = (1.0 - Math.Sqrt(1.0 - e2)) / (1.0 + Math.Sqrt(1.0 - e2));

        double x = easting - FalseEasting;
        double y = south ? northing - FalseNorthingSouth : northing;

        double m = y / K0;
        double mu = m / (A * (1.0 - (e2 / 4.0) - (3.0 * e2 * e2 / 64.0) - (5.0 * e2 * e2 * e2 / 256.0)));

        double phi1 = mu
            + (((3.0 * e1 / 2.0) - (27.0 * Math.Pow(e1, 3) / 32.0)) * Math.Sin(2.0 * mu))
            + (((21.0 * e1 * e1 / 16.0) - (55.0 * Math.Pow(e1, 4) / 32.0)) * Math.Sin(4.0 * mu))
            + ((151.0 * Math.Pow(e1, 3) / 96.0) * Math.Sin(6.0 * mu))
            + ((1097.0 * Math.Pow(e1, 4) / 512.0) * Math.Sin(8.0 * mu));

        double sin1 = Math.Sin(phi1);
        double cos1 = Math.Cos(phi1);
        double tan1 = Math.Tan(phi1);

        double c1 = ep2 * cos1 * cos1;
        double t1 = tan1 * tan1;
        double n1 = A / Math.Sqrt(1.0 - (e2 * sin1 * sin1));
        double r1 = A * (1.0 - e2) / Math.Pow(1.0 - (e2 * sin1 * sin1), 1.5);
        double d = x / (n1 * K0);

        double latitude = phi1 - ((n1 * tan1 / r1) * (
            (d * d / 2.0)
            - ((5.0 + (3.0 * t1) + (10.0 * c1) - (4.0 * c1 * c1) - (9.0 * ep2)) * Math.Pow(d, 4) / 24.0)
            + ((61.0 + (90.0 * t1) + (298.0 * c1) + (45.0 * t1 * t1) - (252.0 * ep2) - (3.0 * c1 * c1)) * Math.Pow(d, 6) / 720.0)));

        double lon0 = ((zone * 6.0) - 183.0) * Math.PI / 180.0;
        double longitude = lon0 + ((
            d
            - ((1.0 + (2.0 * t1) + c1) * Math.Pow(d, 3) / 6.0)
            + ((5.0 - (2.0 * c1) + (28.0 * t1) - (3.0 * c1 * c1) + (8.0 * ep2) + (24.0 * t1 * t1)) * Math.Pow(d, 5) / 120.0)) / cos1);

        return (latitude * 180.0 / Math.PI, longitude * 180.0 / Math.PI);
    }

    /// <summary>
    /// Graus decimais -> DD°MM'SS.ss" (sem sinal; o hemisferio vai separado).
    /// Arredonda os segundos e propaga o "vai um" (59.995" -> 1').
    /// </summary>
    public static string ToDms(double decimalDegrees, int secondDecimals = 2)
    {
        double value = Math.Abs(decimalDegrees);

        double scale = Math.Pow(10, secondDecimals);
        double totalSeconds = Math.Round(value * 3600.0 * scale, MidpointRounding.AwayFromZero) / scale;

        int degrees = (int)Math.Floor(totalSeconds / 3600.0);
        double rest = totalSeconds - (degrees * 3600.0);
        int minutes = (int)Math.Floor(rest / 60.0);
        double seconds = rest - (minutes * 60.0);

        // Protecao contra residuo de ponto flutuante (ex.: 59.99999999 -> 60.00).
        if (Math.Round(seconds, secondDecimals) >= 60.0)
        {
            seconds = 0.0;
            minutes++;
        }

        if (minutes >= 60)
        {
            minutes = 0;
            degrees++;
        }

        string secondsFormat = secondDecimals > 0 ? "00." + new string('0', secondDecimals) : "00";

        return degrees.ToString(CultureInfo.InvariantCulture) + "°" +
               minutes.ToString("00", CultureInfo.InvariantCulture) + "'" +
               seconds.ToString(secondsFormat, CultureInfo.InvariantCulture) + "\"";
    }

    /// <summary>Le "21S", "21 S", "UTM-21S", "SIRGAS2000.UTM-21S" etc.</summary>
    public static bool TryParseZone(string? text, out int zone, out bool south)
    {
        zone = 0;
        south = true;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            text.ToUpperInvariant(),
            @"(\d{1,2})\s*([NS])(?![A-Z])");

        if (!match.Success)
            return false;

        zone = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        south = match.Groups[2].Value == "S";
        return zone >= 1 && zone <= 60;
    }
}
