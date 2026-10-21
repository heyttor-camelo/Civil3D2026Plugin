using System;
using System.Collections.Generic;
using System.Globalization;

namespace Civil3D2026Plugin.Modules.Hydro.Calc;

/// <summary>
/// Equacao IDF do tipo i = K * Tr^a / (t + b)^c, com t em minutos, Tr em anos e i em mm/h.
/// E a forma usual das equacoes brasileiras (Pfafstetter ajustado, Denardin, etc.).
/// </summary>
public sealed record IdfEquation(string Name, double K, double A, double B, double C)
{
    /// <summary>Intensidade em mm/h para a duracao t (min) e o periodo de retorno Tr (anos).</summary>
    public double Intensity(double tMinutes, double returnPeriodYears)
    {
        double t = Math.Max(tMinutes, 1);
        double tr = Math.Max(returnPeriodYears, 1);
        return K * Math.Pow(tr, A) / Math.Pow(t + B, C);
    }

    public string Formula() =>
        $"{K.ToString("0.###", CultureInfo.InvariantCulture)} * Tr^{A.ToString("0.###", CultureInfo.InvariantCulture)} / (t + {B.ToString("0.##", CultureInfo.InvariantCulture)})^{C.ToString("0.###", CultureInfo.InvariantCulture)}";
}

/// <summary>Biblioteca de equacoes IDF (editavel: a personalizada entra pelos coeficientes do painel).</summary>
public static class HydroIdf
{
    /// <summary>
    /// Equacoes usuais. Os coeficientes de Cuiaba devem ser conferidos/fornecidos pelo usuario
    /// (fonte local); a entrada aqui serve de ponto de partida e pode ser sobrescrita no painel.
    /// </summary>
    public static readonly IReadOnlyList<IdfEquation> Library = new[]
    {
        new IdfEquation("Cuiaba-MT (conferir fonte local)", 1500.0, 0.17, 12.0, 0.80),
        new IdfEquation("Sao Paulo-SP (Martinez e Magni, aprox.)", 3462.7, 0.172, 22.0, 1.025),
        new IdfEquation("Rio de Janeiro-RJ (Pfafstetter ajustado)", 1239.0, 0.15, 20.0, 0.74),
        new IdfEquation("Belo Horizonte-MG", 1447.9, 0.10, 20.0, 0.84),
        new IdfEquation("Curitiba-PR", 5950.0, 0.217, 26.0, 1.15),
        new IdfEquation("Goiania-GO", 1860.0, 0.207, 15.0, 0.904)
    };

    public static IdfEquation Find(string name)
    {
        foreach (IdfEquation e in Library)
        {
            if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                return e;
        }

        return Library[0];
    }
}
