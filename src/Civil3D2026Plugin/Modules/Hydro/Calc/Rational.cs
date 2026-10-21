using System;

namespace Civil3D2026Plugin.Modules.Hydro.Calc;

/// <summary>Metodo racional. Vazoes sempre em m3/s.</summary>
public static class Rational
{
    /// <summary>Q = C * i * A / 360, com i em mm/h e A em hectares; resultado em m3/s.</summary>
    public static double Flow(double c, double intensityMmH, double areaHa)
    {
        if (areaHa <= 0 || intensityMmH <= 0 || c <= 0)
            return 0;
        return c * intensityMmH * areaHa / 360.0;
    }

    /// <summary>C medio ponderado pela area (areas em qualquer unidade, iguais entre si).</summary>
    public static double WeightedC(double sumCA, double sumA) => sumA > 1e-12 ? Math.Clamp(sumCA / sumA, 0, 1) : 0;
}
