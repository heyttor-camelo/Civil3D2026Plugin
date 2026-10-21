using System;

namespace Civil3D2026Plugin.Modules.Hydro.Calc;

/// <summary>Resultado de Manning em secao circular para uma vazao dada.</summary>
public readonly record struct ManningResult(bool Flows, double DepthRatio, double Depth, double Area, double Velocity, double FullFlow)
{
    /// <summary>Tempo de percurso em minutos para o comprimento dado.</summary>
    public double TravelTimeMinutes(double lengthM) => Velocity > 1e-9 ? lengthM / Velocity / 60.0 : 0;
}

/// <summary>Manning em secao circular (SI: m, m/m, m3/s, m/s).</summary>
public static class Manning
{
    /// <summary>Area molhada para a relacao y/D.</summary>
    public static double Area(double diameter, double depthRatio)
    {
        double theta = Theta(depthRatio);
        return diameter * diameter / 8.0 * (theta - Math.Sin(theta));
    }

    /// <summary>Perimetro molhado para a relacao y/D.</summary>
    public static double WettedPerimeter(double diameter, double depthRatio) => diameter * Theta(depthRatio) / 2.0;

    private static double Theta(double depthRatio)
    {
        double r = Math.Clamp(depthRatio, 0, 1);
        return 2.0 * Math.Acos(1.0 - (2.0 * r));
    }

    /// <summary>Vazao para a relacao y/D (Q = A R^(2/3) S^(1/2) / n).</summary>
    public static double Flow(double diameter, double slope, double n, double depthRatio)
    {
        if (diameter <= 0 || slope <= 0 || n <= 0 || depthRatio <= 0)
            return 0;
        double a = Area(diameter, depthRatio);
        double p = WettedPerimeter(diameter, depthRatio);
        if (p <= 0)
            return 0;
        double r = a / p;
        return a * Math.Pow(r, 2.0 / 3.0) * Math.Sqrt(slope) / n;
    }

    /// <summary>Vazao a secao plena.</summary>
    public static double FullFlow(double diameter, double slope, double n) => Flow(diameter, slope, n, 1.0);

    /// <summary>Velocidade a secao plena.</summary>
    public static double FullVelocity(double diameter, double slope, double n) =>
        diameter > 0 ? FullFlow(diameter, slope, n) / Area(diameter, 1.0) : 0;

    /// <summary>
    /// Lamina normal para a vazao: bissecao em y/D no trecho crescente da curva (ate 0,94, onde a
    /// vazao e maxima). Se a vazao passa da plena, Flows = false e y/D = 1.
    /// </summary>
    public static ManningResult Solve(double flow, double diameter, double slope, double n)
    {
        double full = FullFlow(diameter, slope, n);
        if (flow <= 0 || diameter <= 0)
            return new ManningResult(true, 0, 0, 0, 0, full);
        if (full <= 0)
            return new ManningResult(false, 1, diameter, Area(diameter, 1), 0, 0);

        const double peak = 0.94;
        double qPeak = Flow(diameter, slope, n, peak);
        if (flow > full)
        {
            double aFull = Area(diameter, 1);
            return new ManningResult(false, 1, diameter, aFull, flow / aFull, full);
        }

        if (flow >= qPeak)
        {
            // Entre o pico e a plena: adota a plena (regiao instavel na pratica).
            double aFull = Area(diameter, 1);
            return new ManningResult(true, 1, diameter, aFull, flow / aFull, full);
        }

        double lo = 0, hi = peak;
        for (int i = 0; i < 60; i++)
        {
            double mid = (lo + hi) / 2;
            if (Flow(diameter, slope, n, mid) < flow)
                lo = mid;
            else
                hi = mid;
        }

        double yd = (lo + hi) / 2;
        double area = Area(diameter, yd);
        return new ManningResult(true, yd, yd * diameter, area, area > 1e-12 ? flow / area : 0, full);
    }

    /// <summary>Menor diametro da lista que atende lamina e velocidades (ou nulo se nenhum).</summary>
    public static double? SmallestDiameter(double flow, double slope, double n, double[] diameters, Criteria c)
    {
        Array.Sort(diameters);
        foreach (double d in diameters)
        {
            if (d <= 0)
                continue;
            ManningResult r = Solve(flow, d, slope, n);
            if (!r.Flows)
                continue;
            if (r.DepthRatio > c.MaxDepthRatio + 1e-9)
                continue;
            if (r.Velocity > c.MaxVelocity + 1e-9)
                continue;
            // Velocidade minima: so limita quando ha vazao de fato (trechos sem contribuicao passam).
            if (flow > 1e-6 && r.Velocity < c.MinVelocity - 1e-9)
                continue;
            return d;
        }

        return null;
    }
}

/// <summary>Criterios de verificacao do projeto.</summary>
public sealed record Criteria(
    double MaxDepthRatio = 0.85,
    double MinVelocity = 0.75,
    double MaxVelocity = 5.0,
    double MinCover = 1.0,
    double MinSlope = 0.003,
    double MaxSlope = 0.15,
    double MinDrop = 0.0,
    double MaxDrop = 1.5,
    double MinTcMinutes = 10.0);
