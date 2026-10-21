using System;
using System.Collections.Generic;
using System.Linq;
using Civil3D2026Plugin.Modules.Hydro.Calc;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>Linha da planilha de dimensionamento (um trecho).</summary>
internal sealed record HydroSizingRow(
    string PipeHandle, string Trecho, string From, string To, double AreaLocalHa, double AreaAcumHa, double C, double Tc, double Intensity,
    double Flow, double Length, double Slope, double Diameter, double DepthRatio, double Velocity, double MinDiameter, double UpInvert, double DownInvert);

/// <summary>Planilha de dimensionamento e "menor diametro que atende".</summary>
internal static class HydroSizing
{
    public static List<HydroSizingRow> Rows(NetworkInfo net, HydroSnapshot snap, Dictionary<string, HydroResult> results, HydroSettings s)
    {
        List<HydroSizingRow> rows = new();
        double[] diameters = s.Diameters.ToArray();
        foreach (PipeInfo p in net.OrderedPipes())
        {
            string from = p.FromHandle is string f && net.StructureByHandle.TryGetValue(f, out StructureInfo? fs) ? fs.Name : "?";
            string to = p.ToHandle is string t && net.StructureByHandle.TryGetValue(t, out StructureInfo? ts) ? ts.Name : "?";
            double local = p.FromHandle is null ? 0 : snap.Catchments.Where(c => c.StructureHandle == p.FromHandle).Sum(c => c.AreaHa);
            HydroResult? r = results.GetValueOrDefault(p.Handle);
            double n = double.IsNaN(p.Manning) ? s.Manning : p.Manning;
            double flow = r?.Flow ?? double.NaN;
            double? min = r is null ? null : Manning.SmallestDiameter(flow, Math.Max(p.Slope, 1e-5), n, (double[])diameters.Clone(), s.Criteria);
            rows.Add(new HydroSizingRow(p.Handle, from + " > " + to + " (" + p.Name + ")", from, to, local, r?.AreaHa ?? double.NaN, r?.C ?? double.NaN,
                r?.Tc ?? double.NaN, r?.Intensity ?? double.NaN, flow, p.Length2D, p.Slope, p.InnerDiameter, r?.DepthRatio ?? double.NaN,
                r?.Velocity ?? double.NaN, min ?? double.NaN, p.UpInvert, p.DownInvert));
        }

        return rows;
    }
}
