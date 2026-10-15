using System;

using WM = System.Windows.Media;

namespace Civil3D2026Plugin.UI.Common;

/// <summary>
/// Cores das janelas de ferramentas (C3DLISP, LSLAYOUTS). Segue o tema claro/escuro
/// escolhido no painel viario (Preferencias).
/// </summary>
internal sealed record ToolTheme(
    WM.Brush Back, WM.Brush Pane, WM.Brush Hover, WM.Brush Line, WM.Brush Text,
    WM.Brush Muted, WM.Brush Input, WM.Brush Accent, WM.Brush Selected)
{
    public static readonly ToolTheme Light = new(
        Brush(240, 240, 240), Brush(255, 255, 255), Brush(236, 241, 247), Brush(200, 200, 200),
        Brush(20, 20, 20), Brush(110, 110, 110), Brush(255, 255, 255), Brush(0, 95, 170), Brush(204, 228, 247));

    public static readonly ToolTheme Dark = new(
        Brush(45, 45, 48), Brush(37, 37, 38), Brush(50, 54, 60), Brush(70, 70, 74),
        Brush(222, 222, 222), Brush(150, 150, 150), Brush(30, 30, 30), Brush(80, 160, 255), Brush(38, 79, 120));

    // Vista grafica (miniaturas): escura nos dois temas, como no Project Explorer.
    public static readonly WM.Brush GraphicBack = Brush(56, 58, 62);
    public static readonly WM.Brush GraphicLine = Brush(150, 152, 156);
    public static readonly WM.Brush GraphicText = Brush(210, 210, 210);
    public static readonly WM.Color AccentColor = WM.Color.FromRgb(80, 160, 255);

    public static ToolTheme Current => IsDark() ? Dark : Light;

    public bool IsDarkTheme => ReferenceEquals(this, Dark);

    private static bool IsDark()
    {
        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Civil3D2026Plugin", "viario_painel.txt");
            return System.IO.File.Exists(path) &&
                   System.IO.File.ReadAllText(path).Contains("tema=escuro", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static WM.SolidColorBrush Brush(byte r, byte g, byte b, byte a = 255)
    {
        WM.SolidColorBrush brush = new(WM.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
