using System;
using System.Linq;

using WM = System.Windows.Media;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>Cor e ordem de desenho de um link do corredor pelos codes (planta do rebaixo).</summary>
internal static class RebaixoStyle
{
    public readonly record struct Look(WM.Color Color, int Order, bool Plan, bool Overlay);

    public static Look Of(string[] codes)
    {
        if (codes.Length == 0)
            return new Look(WM.Colors.Transparent, -1, false, false);

        bool Has(string c) => codes.Any(x => x.Equals(c, StringComparison.OrdinalIgnoreCase));
        bool top = Has("Top") || Has("TOPO");

        if (Has("REBAIXO"))
            return new Look(WM.Color.FromRgb(225, 45, 60), 100, true, codes.Length == 1);
        if (Has("Curb"))
            return new Look(WM.Color.FromRgb(228, 228, 218), 60, top, false);
        if (Has("Sarjeta"))
            return new Look(WM.Color.FromRgb(172, 172, 172), 55, top, false);
        if (Has("Calcada"))
            return new Look(WM.Color.FromRgb(206, 176, 118), 50, true, false);
        if (top || Has("Pave") || Has("Pave1") || Has("Pave2"))
            return new Look(WM.Color.FromRgb(92, 94, 100), 10, true, false);
        if (Has("Calcada_Fundo") || Has("CalcadaBase"))
            return new Look(WM.Color.FromRgb(150, 128, 92), 5, false, false);
        if (Has("Base"))
            return new Look(WM.Color.FromRgb(146, 116, 82), 4, false, false);
        if (Has("SubBase"))
            return new Look(WM.Color.FromRgb(128, 104, 78), 3, false, false);
        if (Has("SubGrade"))
            return new Look(WM.Color.FromRgb(108, 90, 70), 2, false, false);
        return new Look(WM.Color.FromRgb(132, 134, 140), 1, false, false);
    }
}
