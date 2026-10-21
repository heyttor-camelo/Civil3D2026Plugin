using System;
using System.Collections.Generic;

using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Icones vetoriais do HYDRO (grade 32x32, estilo do Viario). Cores com significado:
/// ciano = agua/tubo, branco = estrutura, verde = construido/superficie, amarelo = acao,
/// vermelho = erro/remover, magenta = bacia.
/// </summary>
internal static class HydroIcons
{
    private static readonly WM.Color Plate = WM.Color.FromRgb(28, 43, 60);
    private static readonly WM.Color Water = WM.Color.FromRgb(120, 220, 235);
    private static readonly WM.Color Built = WM.Color.FromRgb(110, 210, 110);
    private static readonly WM.Color Yellow = WM.Color.FromRgb(244, 196, 64);
    private static readonly WM.Color Magenta = WM.Color.FromRgb(230, 90, 230);
    private static readonly WM.Color Orange = WM.Color.FromRgb(240, 160, 50);
    private static readonly WM.Color White = WM.Color.FromRgb(235, 240, 245);
    private static readonly WM.Color Red = WM.Color.FromRgb(240, 70, 70);

    private sealed record Part(string Data, WM.Color? Stroke, double Thickness, WM.Color? Fill = null, bool Dashed = false);

    // Pecas repetidas: PV (circulo branco) e tubo (linha ciano).
    private const string Pv = "M8,16 a4,4 0 1,0 0.1,0";
    private const string Pv2 = "M24,16 a4,4 0 1,0 0.1,0";

    private static readonly Dictionary<string, Part[]> Parts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PAINEL"] = new[]
        {
            new Part("M4,6 H28 V26 H4 Z", White, 1.5),
            new Part("M4,10 H28", White, 1.5),
            new Part("M7,14 a2,2 0 1,0 0.1,0 M7,21 a2,2 0 1,0 0.1,0", White, 1.2),
            new Part("M11,16 H25 M11,23 H25", Water, 1.6),
            new Part("M17,16 l3,-2 l0,4 z", Yellow, 1.0, Yellow)
        },
        ["REDES"] = new[]
        {
            new Part("M6,8 L16,16 L26,8 M16,16 V26", Water, 1.8),
            new Part("M6,8 a2.5,2.5 0 1,0 0.1,0 M26,8 a2.5,2.5 0 1,0 0.1,0 M16,16 a2.5,2.5 0 1,0 0.1,0 M16,26 a2.5,2.5 0 1,0 0.1,0", White, 1.4),
            new Part("M14,22 l2,2 l2,-2", Yellow, 1.4)
        },
        ["ESTRUTURAS"] = new[]
        {
            new Part("M8,6 H24 V26 H8 Z", White, 1.6),
            new Part("M5,6 H27", Built, 1.8),
            new Part("M2,18 H8 M24,18 H30", Water, 2.0),
            new Part("M12,10 H20 M12,14 H20 M12,18 H20 M12,22 H20", Yellow, 1.0)
        },
        ["TUBOS"] = new[]
        {
            new Part("M3,10 H29 M3,22 H29", Water, 1.8),
            new Part("M3,16 H29", Water, 1.0, null, true),
            new Part("M6,12 a3,6 0 1,0 0.1,0", White, 1.2),
            new Part("M18,13 l4,3 l-4,3", Yellow, 1.6)
        },
        ["BACIAS"] = new[]
        {
            new Part("M4,8 L14,4 L28,10 L26,22 L12,28 L4,20 Z", Magenta, 1.6, WM.Color.FromArgb(70, 230, 90, 230)),
            new Part("M16,10 L14,18 L18,26", Water, 1.2, null, true),
            new Part("M18,26 a2.5,2.5 0 1,0 0.1,0", White, 1.4)
        },
        ["CALCULO"] = new[]
        {
            new Part("M4,26 H28 M4,26 V4", White, 1.4),
            new Part("M6,24 C10,24 14,8 28,6", Water, 2.0),
            new Part("M10,10 H18 M14,6 V14", Yellow, 1.6),
            new Part("M22,18 H28", Yellow, 1.6)
        },
        ["VERIFICACAO"] = new[]
        {
            new Part("M16,4 L29,27 H3 Z", Yellow, 1.8),
            new Part("M16,12 V19", Yellow, 2.4),
            new Part("M16,23 V23.5", Yellow, 2.6)
        },
        ["SAIDAS"] = new[]
        {
            new Part("M6,4 H20 L26,10 V28 H6 Z", White, 1.4),
            new Part("M20,4 V10 H26", White, 1.2),
            new Part("M9,14 H23 M9,18 H23 M9,22 H18", Water, 1.2),
            new Part("M22,22 l4,4 l4,-6", Built, 1.8)
        },
        ["NOVAREDE"] = new[]
        {
            new Part("M6,24 L16,12 L26,20", Water, 1.8),
            new Part("M6,24 a2.5,2.5 0 1,0 0.1,0 M16,12 a2.5,2.5 0 1,0 0.1,0", White, 1.4),
            new Part("M22,6 H30 M26,2 V10", Yellow, 2.2)
        },
        ["LANCAR"] = new[]
        {
            new Part("M4,24 L14,16 L24,20", Water, 1.8),
            new Part("M4,24 a2.5,2.5 0 1,0 0.1,0 M14,16 a2.5,2.5 0 1,0 0.1,0", White, 1.4),
            new Part("M24,20 a2.5,2.5 0 1,0 0.1,0", Yellow, 1.4),
            new Part("M24,20 L30,8", Yellow, 1.0, null, true)
        },
        ["IMPORTAR"] = new[]
        {
            new Part("M4,6 H22 V26 H4 Z", Built, 1.4),
            new Part("M4,11 H22 M4,16 H22 M4,21 H22 M10,6 V26", Built, 0.9),
            new Part("M26,8 V22 M22,18 L26,22 L30,18", Yellow, 1.8)
        },
        ["NUMERAR"] = new[]
        {
            new Part("M6,24 L16,10 L26,24", Water, 1.6),
            new Part("M6,24 a2.5,2.5 0 1,0 0.1,0 M16,10 a2.5,2.5 0 1,0 0.1,0 M26,24 a2.5,2.5 0 1,0 0.1,0", White, 1.3),
            new Part("M8,6 h3 v8 M20,30 h-5 l5,-5 a2,2 0 0,0 -4,-2", Yellow, 1.4)
        },
        ["CALCULAR"] = new[]
        {
            new Part("M6,4 H26 V28 H6 Z", White, 1.4),
            new Part("M9,7 H23 V12 H9 Z", Water, 1.0, WM.Color.FromArgb(90, 120, 220, 235)),
            new Part("M10,16 H13 M15,16 H18 M20,16 H23 M10,20 H13 M15,20 H18 M20,20 H23 M10,24 H13 M15,24 H18", Yellow, 1.6),
            new Part("M20,24 H23", Built, 1.8)
        },
        ["LOTE"] = new[]
        {
            new Part("M4,7 H14 V13 H4 Z M4,19 H14 V25 H4 Z", White, 1.2),
            new Part("M18,10 H28 M18,22 H28", Water, 1.6),
            new Part("M6,10 l2,2 l4,-4 M6,22 l2,2 l4,-4", Yellow, 1.4)
        },
        ["TROCAR"] = new[]
        {
            new Part("M6,12 a3,5 0 1,0 0.1,0", White, 1.4),
            new Part("M26,8 a5,8 0 1,0 0.1,0", Water, 1.6),
            new Part("M11,10 H19 M15,6 L19,10 L15,14", Yellow, 1.6),
            new Part("M21,24 H13 M17,20 L13,24 L17,28", Yellow, 1.6)
        },
        ["INVERTER"] = new[]
        {
            new Part("M4,16 H28", Water, 2.0),
            new Part("M10,10 L4,16 L10,22", White, 1.6),
            new Part("M22,10 L28,16 L22,22", White, 1.6)
        },
        ["CONECTAR"] = new[]
        {
            new Part("M3,22 L11,14", Water, 1.8),
            new Part("M21,10 L29,4", Water, 1.8),
            new Part("M11,14 L21,10", Yellow, 1.8, null, true),
            new Part(Pv, White, 1.2)
        },
        ["REGRAS"] = new[]
        {
            new Part("M8,4 H24 V28 H8 Z", White, 1.4),
            new Part("M11,9 H21 M11,13 H21 M11,17 H18", Water, 1.0),
            new Part("M12,22 l3,3 l6,-6", Built, 2.0)
        },
        ["ZOOM"] = new[]
        {
            new Part("M13,6 a7,7 0 1,0 0.1,0", Water, 1.8),
            new Part("M18,18 L27,27", Yellow, 2.6),
            new Part("M10,13 H16 M13,10 V16", White, 1.4)
        },
        ["SELECIONAR"] = new[]
        {
            new Part("M8,4 L8,24 L13,19 L17,27 L20,25 L16,17 L23,17 Z", White, 1.5, WM.Color.FromArgb(60, 235, 240, 245)),
            new Part("M22,6 H30 M26,2 V10", Yellow, 1.4)
        },
        ["NOVABACIA"] = new[]
        {
            new Part("M4,10 L16,4 L28,12 L24,26 L8,24 Z", Magenta, 1.6, null, true),
            new Part("M22,20 H30 M26,16 V24", Yellow, 2.2)
        },
        ["LIGARBACIA"] = new[]
        {
            new Part("M3,8 L11,4 L17,10 L13,18 L4,16 Z", Magenta, 1.4, WM.Color.FromArgb(70, 230, 90, 230)),
            new Part("M13,18 L24,24", Yellow, 1.4, null, true),
            new Part(Pv2 .Replace("24,16", "24,24"), White, 1.4)
        },
        ["TC"] = new[]
        {
            new Part("M16,4 a12,12 0 1,0 0.1,0", White, 1.6),
            new Part("M16,9 V16 L21,19", Yellow, 1.8),
            new Part("M6,28 H26", Water, 1.2)
        },
        ["AGRUPAR"] = new[]
        {
            new Part("M4,6 H14 V14 H4 Z M18,6 H28 V14 H18 Z", Magenta, 1.2),
            new Part("M4,18 H28 V28 H4 Z", Magenta, 1.4, WM.Color.FromArgb(60, 230, 90, 230))
        },
        ["DIMENSIONAR"] = new[]
        {
            new Part("M6,12 a3,5 0 1,0 0.1,0", Water, 1.2),
            new Part("M16,10 a4,7 0 1,0 0.1,0", Water, 1.4),
            new Part("M27,8 a5,9 0 1,0 0.1,0", Water, 1.6),
            new Part("M4,28 H28", Yellow, 1.2),
            new Part("M14,22 l2,3 l4,-5", Built, 1.8)
        },
        ["MATRIZ"] = new[]
        {
            new Part("M4,4 H28 V28 H4 Z M4,12 H28 M4,20 H28 M12,4 V28 M20,4 V28", White, 1.0),
            new Part("M6,6 H10 V10 H6 Z", Built, 1.0, Built),
            new Part("M14,14 H18 V18 H14 Z", Yellow, 1.0, Yellow),
            new Part("M22,22 H26 V26 H22 Z", Red, 1.0, Red)
        },
        ["IRPARA"] = new[]
        {
            new Part("M16,4 a12,12 0 1,0 0.1,0", Water, 1.4),
            new Part("M16,10 V22 M10,16 H22", White, 1.2),
            new Part("M16,13 a3,3 0 1,0 0.1,0", Yellow, 1.6)
        },
        ["CORRIGIR"] = new[]
        {
            new Part("M6,26 L18,14", White, 2.2),
            new Part("M18,14 a5,5 0 1,1 7,-7 l-4,4 l3,3 l4,-4 a5,5 0 0,1 -5,7", Yellow, 1.4),
            new Part("M4,28 l2,-2", Red, 2.0)
        },
        ["PLANILHA"] = new[]
        {
            new Part("M4,6 H28 V26 H4 Z", Built, 1.4),
            new Part("M4,11 H28 M4,16 H28 M4,21 H28 M10,6 V26 M19,6 V26", Built, 0.9),
            new Part("M12,13 H17 M21,18 H26", Yellow, 1.4)
        },
        ["EXPORTAR"] = new[]
        {
            new Part("M6,4 H20 L26,10 V28 H6 Z", White, 1.4),
            new Part("M20,4 V10 H26", White, 1.2),
            new Part("M16,24 V12 M11,17 L16,12 L21,17", Yellow, 1.8)
        },
        ["ROTULOS"] = new[]
        {
            new Part("M4,24 H28", Water, 1.8),
            new Part("M16,24 L20,12", White, 1.0),
            new Part("M14,6 H28 V14 H14 Z", White, 1.2),
            new Part("M17,10 H25", Yellow, 1.4)
        },
        ["TABELA"] = new[]
        {
            new Part("M4,6 H28 V26 H4 Z", White, 1.4),
            new Part("M4,12 H28 M4,19 H28 M12,6 V26 M20,6 V26", White, 0.9),
            new Part("M6,8 H26", Water, 2.0)
        },
        ["ATUALIZAR"] = new[]
        {
            new Part("M24,10 A10,10 0 1,0 26,18", Water, 2.0),
            new Part("M24,4 V10 H18", Yellow, 2.0)
        },
        ["FUNDO"] = new[]
        {
            new Part("M8,4 H24 V26 H8 Z", White, 1.4),
            new Part("M2,20 H8 M24,20 H30", Water, 1.8),
            new Part("M10,24 H22", Yellow, 2.2),
            new Part("M16,10 V22 M13,19 L16,22 L19,19", Yellow, 1.4)
        },
        ["TAMPA"] = new[]
        {
            new Part("M8,8 H24 V28 H8 Z", White, 1.4),
            new Part("M4,8 H28", Built, 2.2),
            new Part("M16,22 V10 M13,13 L16,10 L19,13", Yellow, 1.4)
        },
        ["DECLIV"] = new[]
        {
            new Part("M4,8 L28,22", Water, 2.0),
            new Part("M4,8 H28 M28,8 V22", White, 1.0, null, true),
            new Part("M8,8 a4,4 0 0,1 1,3", Yellow, 1.4)
        },
        ["IGUALAR"] = new[]
        {
            new Part("M6,10 a3,5 0 1,0 0.1,0", Water, 1.6),
            new Part("M26,10 a3,5 0 1,0 0.1,0", Water, 1.6, null, true),
            new Part("M11,14 H21 M17,10 L21,14 L17,18", Yellow, 1.6),
            new Part("M4,28 H28", White, 1.0)
        },
        ["MOVER"] = new[]
        {
            new Part(Pv.Replace("8,16", "16,16"), White, 1.6),
            new Part("M16,4 V9 M16,23 V28 M4,16 H9 M23,16 H28", Yellow, 1.8),
            new Part("M13,6 L16,3 L19,6 M13,26 L16,29 L19,26 M6,13 L3,16 L6,19 M26,13 L29,16 L26,19", Yellow, 1.2),
            new Part("M4,26 L12,20 M20,12 L28,6", Water, 1.4)
        },
        ["INSERIR"] = new[]
        {
            new Part("M3,20 H29", Water, 2.0),
            new Part("M16,20 a3,3 0 1,0 0.1,0", White, 1.6),
            new Part("M16,4 V14 M13,11 L16,14 L19,11", Yellow, 1.8)
        },
        ["REMOVERPV"] = new[]
        {
            new Part("M3,20 H29", Water, 2.0),
            new Part("M16,20 a3,3 0 1,0 0.1,0", White, 1.2, null, true),
            new Part("M11,6 L21,16 M21,6 L11,16", Red, 2.2)
        },
        ["CAMINHO"] = new[]
        {
            new Part("M4,26 L12,10 L20,18 L28,6", Water, 1.4),
            new Part("M4,26 a2.5,2.5 0 1,0 0.1,0 M28,6 a2.5,2.5 0 1,0 0.1,0", Yellow, 1.6),
            new Part("M4,26 L12,10 L20,18 L28,6", Yellow, 2.2, null, true)
        },
        ["PERFIL"] = new[]
        {
            new Part("M4,26 H28 M4,26 V6", White, 1.2),
            new Part("M6,10 L14,12 L22,16 L28,18", Built, 1.4),
            new Part("M6,16 L14,18 L22,21 L28,23", Water, 1.8),
            new Part("M6,10 V16 M14,12 V18 M22,16 V21", White, 1.0)
        },
        ["INFO"] = new[]
        {
            new Part("M16,4 a12,12 0 1,0 0.1,0", Water, 1.8),
            new Part("M16,14 V24", Water, 2.6),
            new Part("M16,9 V9.5", Water, 2.8)
        },
        ["CONFIG"] = new[]
        {
            new Part("M16,10 a6,6 0 1,0 0.1,0", White, 1.8),
            new Part("M16,3 V8 M16,24 V29 M3,16 H8 M24,16 H29 M7,7 L10.5,10.5 M21.5,21.5 L25,25 M25,7 L21.5,10.5 M10.5,21.5 L7,25", White, 2.2),
            new Part("M16,14 a2,2 0 1,0 0.1,0", Yellow, 1.6)
        },
        ["ERRO"] = new[]
        {
            new Part("M16,4 a12,12 0 1,0 0.1,0", Red, 1.8),
            new Part("M11,11 L21,21 M21,11 L11,21", Red, 2.2)
        },
        ["CATALOGO"] = new[]
        {
            new Part("M5,5 H27 V27 H5 Z", White, 1.4),
            new Part("M5,12 H27 M5,19 H27 M12,5 V27", White, 0.9),
            new Part("M7,7 H10 V10 H7 Z", Water, 1.0, Water),
            new Part("M14,14 a2,2 0 1,0 0.1,0", White, 1.2),
            new Part("M21,21 H25 V25 H21 Z", Yellow, 1.0, Yellow)
        },
        ["IDF"] = new[]
        {
            new Part("M4,26 H28 M4,26 V4", White, 1.2),
            new Part("M6,8 C10,20 18,24 28,25", Water, 2.0),
            new Part("M6,14 C10,22 18,25 28,26", Orange, 1.2),
            new Part("M10,6 l-2,4 h4 z", Water, 1.0, Water)
        }
    };

    public static bool Has(string key) => Parts.ContainsKey(key);

    /// <summary>Desenho vetorial (escala livre), com ou sem placa escura.</summary>
    public static WM.DrawingImage Image(string key, bool plate = true, WM.Color? branco = null)
    {
        WM.DrawingGroup group = new();
        if (plate)
        {
            group.Children.Add(new WM.GeometryDrawing(new WM.SolidColorBrush(Plate), null,
                new WM.RectangleGeometry(new System.Windows.Rect(0, 0, 32, 32), 5, 5)));
        }
        else
        {
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
                if (branco is WM.Color b && stroke == White)
                    stroke = b;
                pen = new WM.Pen(new WM.SolidColorBrush(stroke), part.Thickness)
                {
                    StartLineCap = WM.PenLineCap.Round,
                    EndLineCap = WM.PenLineCap.Round,
                    LineJoin = WM.PenLineJoin.Round
                };
                if (part.Dashed)
                    pen.DashStyle = new WM.DashStyle(new double[] { 3, 2 }, 0);
            }

            WM.Brush? fill = part.Fill is WM.Color f ? new WM.SolidColorBrush(branco is WM.Color bf && f == White ? bf : f) : null;
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

    /// <summary>Chave de icone a partir do nome do comando (C3DHYDROTUBOS -> TUBOS, HYFUNDO -> FUNDO).</summary>
    public static string KeyForCommand(string command)
    {
        string key = command;
        if (key.StartsWith("C3DHYDRO", StringComparison.OrdinalIgnoreCase))
            key = key[8..];
        else if (key.StartsWith("HY", StringComparison.OrdinalIgnoreCase))
            key = key[2..];
        key = key.ToUpperInvariant();
        return key switch
        {
            "" => "PAINEL",
            "VERIFICAR" => "VERIFICACAO",
            "REMOVER" => "REMOVERPV",
            "LIGARBACIA" => "LIGARBACIA",
            "BACIA" => "NOVABACIA",
            _ => Has(key) ? key : "PAINEL"
        };
    }
}
