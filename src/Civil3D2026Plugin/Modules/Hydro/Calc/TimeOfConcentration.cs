using System;

namespace Civil3D2026Plugin.Modules.Hydro.Calc;

/// <summary>Formulas de tempo de concentracao disponiveis.</summary>
public enum TcMethod
{
    Kirpich,
    VenTeChow,
    Dooge,
    Kerby
}

/// <summary>
/// Tempo de concentracao em minutos. Entradas em unidades do SI (comprimento em m, desnivel em m,
/// area em m2); as formulas convertem internamente para km/km2 onde a literatura usa.
/// </summary>
public static class TimeOfConcentration
{
    public static string Name(TcMethod m) => m switch
    {
        TcMethod.Kirpich => "Kirpich (L, H)",
        TcMethod.VenTeChow => "Ven Te Chow (L, S)",
        TcMethod.Dooge => "Dooge (A, S)",
        TcMethod.Kerby => "Kerby - escoamento superficial (L, S, n)",
        _ => m.ToString()
    };

    public static string Key(TcMethod m) => m.ToString().ToUpperInvariant();

    public static bool TryParse(string key, out TcMethod method)
    {
        foreach (TcMethod m in Enum.GetValues<TcMethod>())
        {
            if (string.Equals(Key(m), key, StringComparison.OrdinalIgnoreCase))
            {
                method = m;
                return true;
            }
        }

        method = TcMethod.Kirpich;
        return false;
    }

    /// <summary>Kirpich (1940): tc = 57 * (L^3 / H)^0.385, L em km, H em m, tc em min.</summary>
    public static double Kirpich(double lengthM, double dropM)
    {
        if (lengthM <= 0 || dropM <= 0)
            return 0;
        double lKm = lengthM / 1000.0;
        return 57.0 * Math.Pow(lKm * lKm * lKm / dropM, 0.385);
    }

    /// <summary>Ven Te Chow: tc = 52.64 * (L / sqrt(S))^0.64, L em km, S em m/m, tc em min.</summary>
    public static double VenTeChow(double lengthM, double slope)
    {
        if (lengthM <= 0 || slope <= 0)
            return 0;
        return 52.64 * Math.Pow(lengthM / 1000.0 / Math.Sqrt(slope), 0.64);
    }

    /// <summary>Dooge: tc = 21.88 * A^0.41 * S^-0.17, A em km2, S em m/m, tc em min.</summary>
    public static double Dooge(double areaM2, double slope)
    {
        if (areaM2 <= 0 || slope <= 0)
            return 0;
        return 21.88 * Math.Pow(areaM2 / 1e6, 0.41) * Math.Pow(slope, -0.17);
    }

    /// <summary>Kerby (escoamento superficial): tc = 1.44 * (L * n / sqrt(S))^0.467, L em m (ate ~400 m), S em m/m, tc em min.</summary>
    public static double Kerby(double lengthM, double slope, double retardance = 0.4)
    {
        if (lengthM <= 0 || slope <= 0)
            return 0;
        return 1.44 * Math.Pow(lengthM * retardance / Math.Sqrt(slope), 0.467);
    }

    /// <summary>Calcula pela formula escolhida; o desnivel vira declividade quando preciso.</summary>
    public static double Compute(TcMethod method, double lengthM, double dropM, double areaM2 = 0, double retardance = 0.4)
    {
        double slope = lengthM > 0 ? dropM / lengthM : 0;
        return method switch
        {
            TcMethod.Kirpich => Kirpich(lengthM, dropM),
            TcMethod.VenTeChow => VenTeChow(lengthM, slope),
            TcMethod.Dooge => Dooge(areaM2, slope),
            TcMethod.Kerby => Kerby(lengthM, slope, retardance),
            _ => 0
        };
    }

    /// <summary>Aplica o minimo de projeto (ex.: 10 min).</summary>
    public static double WithMinimum(double tcMinutes, double minimumMinutes) => Math.Max(tcMinutes, minimumMinutes);
}
