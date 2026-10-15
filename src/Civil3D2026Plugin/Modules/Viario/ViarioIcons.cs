using System;
using System.Collections.Generic;

using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Icones vetoriais dos comandos da rede viaria (grade 32x32). Usados no painel
/// (pequenos e na ilustracao grande) e na Ribbon.
/// </summary>
internal static class ViarioIcons
{
    private static readonly WM.Color Plate = WM.Color.FromRgb(28, 43, 60);
    private static readonly WM.Color Asphalt = WM.Color.FromRgb(70, 80, 92);
    private static readonly WM.Color Edge = WM.Color.FromRgb(120, 220, 235);
    private static readonly WM.Color Curb = WM.Color.FromRgb(110, 210, 110);
    private static readonly WM.Color Axis = WM.Color.FromRgb(235, 90, 90);
    private static readonly WM.Color Yellow = WM.Color.FromRgb(244, 196, 64);
    private static readonly WM.Color Magenta = WM.Color.FromRgb(230, 90, 230);
    private static readonly WM.Color Orange = WM.Color.FromRgb(240, 160, 50);
    private static readonly WM.Color White = WM.Color.FromRgb(235, 240, 245);
    private static readonly WM.Color Red = WM.Color.FromRgb(240, 70, 70);

    private sealed record Part(string Data, WM.Color? Stroke, double Thickness, WM.Color? Fill = null, bool Dashed = false);

    private static readonly Dictionary<string, Part[]> Parts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PAINEL"] = new[]
        {
            new Part("M4,6 H28 V26 H4 Z", White, 1.5),
            new Part("M4,10 H28", White, 1.5),
            new Part("M7,13 H13 V17 H7 Z M7,19 H13 V23 H7 Z", Yellow, 1.2),
            new Part("M16,13 H25 M16,16 H23 M16,19 H25 M16,22 H22", Edge, 1.2)
        },
        ["EIXO"] = new[]
        {
            new Part("M3,23 L29,9", Edge, 1.2),
            new Part("M5,29 L29,16", Edge, 1.2),
            new Part("M3,17 L27,4", Edge, 1.2),
            new Part("M3,26 L29,12", Axis, 1.6, null, true),
            new Part("M24,15 a2,2 0 1,0 0.1,0", Yellow, 1.6)
        },
        ["ADICIONAR"] = new[]
        {
            new Part("M3,24 L12,12 L20,18 L27,8", Axis, 1.8),
            new Part("M22,22 H30 M26,18 V26", Yellow, 2.2)
        },
        ["REMOVEREIXO"] = new[]
        {
            new Part("M3,24 L12,12 L20,18 L27,8", Axis, 1.8, null, true),
            new Part("M22,24 H30", Red, 2.4)
        },
        ["ATRIBUIR"] = new[]
        {
            new Part("M2,20 L8,18 H24 L30,20", Edge, 1.5),
            new Part("M8,18 V22 M24,18 V22", Curb, 1.5),
            new Part("M16,4 V14 M12,10 L16,14 L20,10", Yellow, 1.8)
        },
        ["SECAO"] = new[]
        {
            new Part("M2,18 H6 V20 H10 L16,18 L22,20 H26 V18 H30", Edge, 1.6),
            new Part("M2,22 H30", Curb, 1.0),
            new Part("M16,8 V28", Axis, 1.2, null, true),
            new Part("M6,12 H26 M6,10 V14 M26,10 V14", Yellow, 1.2)
        },
        ["RAIO"] = new[]
        {
            new Part("M4,4 V18 A10,10 0 0,0 14,28 H28", Edge, 1.8),
            new Part("M4,18 A10,10 0 0,0 14,28", Yellow, 2.4),
            new Part("M14,18 L7.5,24.5", White, 1.2),
            new Part("M14,18 a1.2,1.2 0 1,0 0.1,0", White, 1.2)
        },
        ["ROTATORIA"] = new[]
        {
            new Part("M16,9 a7,7 0 1,0 0.1,0", Edge, 1.6),
            new Part("M16,13 a3,3 0 1,0 0.1,0", Curb, 1.4, Curb),
            new Part("M16,2 V9 M16,23 V30 M2,16 H9 M23,16 H30", Edge, 1.6),
            new Part("M21,10 l2,-1 l-0.5,2.2", Yellow, 1.4)
        },
        ["MODELOS"] = new[]
        {
            new Part("M3,3 H14 V14 H3 Z M18,3 H29 V14 H18 Z M3,18 H14 V29 H3 Z M18,18 H29 V29 H18 Z", White, 1.0),
            new Part("M8.5,4 V13 M4,8.5 H13", Edge, 1.4),
            new Part("M23.5,4 V8.5 M19,8.5 H28", Edge, 1.4),
            new Part("M8.5,23.5 m-3,0 a3,3 0 1,0 6,0 a3,3 0 1,0 -6,0", Edge, 1.4),
            new Part("M19,23.5 H28 M23.5,19 V28", Edge, 1.4),
            new Part("M23.5,25 l-1,3 h2 z", Magenta, 1.0, Magenta)
        },
        ["ALARGAR"] = new[]
        {
            new Part("M2,10 H30", Edge, 1.6),
            new Part("M2,22 H10 C14,22 14,27 18,27 H30", Edge, 1.6),
            new Part("M18,22 H30", White, 1.0, null, true),
            new Part("M10,22 C14,22 14,27 18,27", Yellow, 2.2),
            new Part("M2,16 H30", Axis, 1.0, null, true)
        },
        ["GOTA"] = new[]
        {
            new Part("M2,8 H30 M2,24 H30", Edge, 1.6),
            new Part("M6,16 a2.5,2.5 0 0,1 2.5,-2.5 L28,16 L8.5,18.5 a2.5,2.5 0 0,1 -2.5,-2.5 Z", Magenta, 1.4, WM.Color.FromArgb(90, 230, 90, 230))
        },
        ["CONVERSAO"] = new[]
        {
            new Part("M2,6 H30 M2,26 H30", Edge, 1.6),
            new Part("M2,13 H30 M2,19 H10 C14,19 14,15 18,15 H28 a2,2 0 0,0 0,-2", Orange, 1.6),
            new Part("M10,19 C14,19 14,15 18,15 H28", Yellow, 2.2),
            new Part("M24,22 h-6 l2,-1.6 m-2,1.6 l2,1.6", White, 1.2)
        },
        ["ABERTURA"] = new[]
        {
            new Part("M2,6 H30 M2,26 H30", Edge, 1.6),
            new Part("M2,13 H10 a3,3 0 0,1 0,6 H2 M30,13 H22 a3,3 0 0,0 0,6 H30", Orange, 1.6),
            new Part("M13,16 C16,11 18,11 19,16", Yellow, 1.6)
        },
        ["LIVRE"] = new[]
        {
            new Part("M3,2 V12 A16,16 0 0,0 20,29 H30", Edge, 1.6),
            new Part("M10,2 V10 M22,22 H30", Edge, 1.4),
            new Part("M11,13 L19,21 L11,21 Z", Magenta, 1.4, WM.Color.FromArgb(90, 230, 90, 230)),
            new Part("M3,12 A16,16 0 0,0 20,29", Yellow, 2.0)
        },
        ["ELEMREMOVER"] = new[]
        {
            new Part("M2,8 H30 M2,24 H30", Edge, 1.4),
            new Part("M6,16 a2.5,2.5 0 0,1 2.5,-2.5 L26,16 L8.5,18.5 a2.5,2.5 0 0,1 -2.5,-2.5 Z", Magenta, 1.2),
            new Part("M18,10 L28,20 M28,10 L18,20", Red, 2.4)
        },
        ["ILHA"] = new[]
        {
            new Part("M6,8 H22 a4,4 0 0,1 4,4 V20 a4,4 0 0,1 -4,4 H10 a4,4 0 0,1 -4,-4 Z", Curb, 1.6, WM.Color.FromArgb(70, 110, 210, 110)),
            new Part("M14,10 L26,22 M26,10 L14,22", Red, 2.4)
        },
        ["DESVINCULAR"] = new[]
        {
            new Part("M3,24 L11,14", Edge, 1.8),
            new Part("M18,12 L29,6", Edge, 1.8),
            new Part("M10,10 a3,3 0 0,1 5,-1 l2,2 M18,20 a3,3 0 0,1 -5,1 l-2,-2", Yellow, 1.6)
        },
        ["ATUALIZAR"] = new[]
        {
            new Part("M24,10 A10,10 0 1,0 26,18", Edge, 2.0),
            new Part("M24,4 V10 H18", Yellow, 2.0)
        },
        ["CIVIL"] = new[]
        {
            new Part("M3,24 C10,24 14,8 29,8", Curb, 2.0),
            new Part("M7,21 l2,3 M13,16 l3,2 M19,11 l2,3 M25,8 l1,3", White, 1.2),
            new Part("M3,24 a1.5,1.5 0 1,0 0.1,0 M29,8 a1.5,1.5 0 1,0 0.1,0", Yellow, 1.4)
        },
        ["SINAL"] = new[]
        {
            new Part("M2,5 H30 M2,27 H30", Edge, 1.4),
            new Part("M2,16 H6 M10,16 H14", Yellow, 1.6),
            new Part("M18,8 V24 M21,8 V24 M24,8 V24 M27,8 V24", White, 1.8),
            new Part("M16,17 V25", White, 1.2)
        },
        ["TESTE"] = new[]
        {
            new Part("M5,6 H21 V26 H5 Z", White, 1.2),
            new Part("M8,11 H18 M8,15 H18 M8,19 H14", Edge, 1.0),
            new Part("M16,21 L20,25 L29,13", Curb, 2.4)
        },
        ["CONFIG"] = new[]
        {
            new Part("M16,10 a6,6 0 1,0 0.1,0", White, 1.8),
            new Part("M16,3 V8 M16,24 V29 M3,16 H8 M24,16 H29 M7,7 L10.5,10.5 M21.5,21.5 L25,25 M25,7 L21.5,10.5 M10.5,21.5 L7,25", White, 2.2),
            new Part("M16,14 a2,2 0 1,0 0.1,0", Yellow, 1.6)
        },
        ["AVISOS"] = new[]
        {
            new Part("M16,4 L29,27 H3 Z", Yellow, 1.8),
            new Part("M16,12 V19", Yellow, 2.4),
            new Part("M16,23 V23.5", Yellow, 2.6)
        },
        ["RECONCORDAR"] = new[]
        {
            new Part("M3,26 H14", Edge, 1.8),
            new Part("M14,26 a12,12 0 0,0 12,-12 V5", Edge, 1.8),
            new Part("M10,22 L18,22", Yellow, 1.4),
            new Part("M14,26 a3,3 0 1,0 0.1,0", Yellow, 1.6)
        },
        ["INVERTER"] = new[]
        {
            new Part("M4,16 H28", Axis, 2.0),
            new Part("M10,10 L4,16 L10,22", White, 1.6),
            new Part("M22,10 L28,16 L22,22", White, 1.6)
        },
        ["HACHURA"] = new[]
        {
            new Part("M4,6 H28 V26 H4 Z", Edge, 1.4),
            new Part("M4,14 L12,6 M4,22 L20,6 M4,26 L24,6 M12,26 L28,10 M20,26 L28,18", White, 1.0),
            new Part("M13,13 H21 V19 H13 Z", Plate, 1.0)
        },
        ["COTAR"] = new[]
        {
            new Part("M4,8 V26 M16,8 V26 M28,8 V26", Edge, 1.0),
            new Part("M4,20 H28", White, 1.4),
            new Part("M2,22 L6,18 M14,22 L18,18 M26,22 L30,18", White, 1.6),
            new Part("M7,12 H13 M19,12 H25", Yellow, 1.6)
        },
        ["INFO"] = new[]
        {
            new Part("M16,4 a12,12 0 1,0 0.1,0", Edge, 1.8),
            new Part("M16,14 V24", Edge, 2.6),
            new Part("M16,9 V9.5", Edge, 2.8)
        },
        ["CONSULTA"] = new[]
        {
            new Part("M5,6 H14 a2,2 0 0,1 2,2 V27 a2,2 0 0,0 -2,-2 H5 Z", White, 1.4),
            new Part("M27,6 H18 a2,2 0 0,0 -2,2 V27 a2,2 0 0,1 2,-2 H27 Z", White, 1.4),
            new Part("M8,11 H13 M8,15 H13 M19,11 H24 M19,15 H24 M19,19 H22", Edge, 1.0)
        },
        ["DIAG"] = new[]
        {
            new Part("M13,6 a7,7 0 1,0 0.1,0", Edge, 1.8),
            new Part("M18,18 L27,27", Yellow, 2.6)
        }
    };

    public static bool Has(string key) => Parts.ContainsKey(key);

    /// <summary>Desenho vetorial (escala livre) com placa escura de fundo.</summary>
    public static WM.DrawingImage Image(string key, bool plate = true)
    {
        WM.DrawingGroup group = new();
        if (plate)
        {
            group.Children.Add(new WM.GeometryDrawing(new WM.SolidColorBrush(Plate), null,
                new WM.RectangleGeometry(new System.Windows.Rect(0, 0, 32, 32), 5, 5)));
        }
        else
        {
            // Mantem o enquadramento 32x32 mesmo sem placa.
            group.Children.Add(new WM.GeometryDrawing(WM.Brushes.Transparent, null,
                new WM.RectangleGeometry(new System.Windows.Rect(0, 0, 32, 32))));
        }

        if (!Parts.TryGetValue(key, out Part[]? parts))
            parts = Parts["PAINEL"];

        foreach (Part part in parts)
        {
            WM.Pen? pen = null;
            if (part.Stroke is WM.Color stroke)
            {
                pen = new WM.Pen(new WM.SolidColorBrush(stroke), part.Thickness)
                {
                    StartLineCap = WM.PenLineCap.Round,
                    EndLineCap = WM.PenLineCap.Round,
                    LineJoin = WM.PenLineJoin.Round
                };
                if (part.Dashed)
                    pen.DashStyle = new WM.DashStyle(new double[] { 3, 2 }, 0);
            }

            WM.Brush? fill = part.Fill is WM.Color f ? new WM.SolidColorBrush(f) : null;
            group.Children.Add(new WM.GeometryDrawing(fill, pen, WM.Geometry.Parse(part.Data)));
        }

        WM.DrawingImage image = new(group);
        image.Freeze();
        return image;
    }

    /// <summary>Bitmap (Ribbon).</summary>
    public static WM.ImageSource Bitmap(string key, int size)
    {
        WM.DrawingVisual visual = new();
        using (WM.DrawingContext dc = visual.RenderOpen())
            dc.DrawImage(Image(key), new System.Windows.Rect(0, 0, size, size));

        WMI.RenderTargetBitmap bmp = new(size, size, 96, 96, WM.PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Chave de icone a partir do nome do comando (C3DVIARIOGOTA -> GOTA).</summary>
    public static string KeyForCommand(string command)
    {
        string key = command.StartsWith("C3DVIARIO", StringComparison.OrdinalIgnoreCase) ? command[9..] : command;
        return key.Length == 0 ? "PAINEL" : key.ToUpperInvariant();
    }
}
