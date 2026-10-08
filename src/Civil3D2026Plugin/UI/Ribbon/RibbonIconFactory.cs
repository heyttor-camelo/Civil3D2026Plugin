using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// O projeto usa WinForms e WPF ao mesmo tempo.
// Tipos como Color, Point, FlowDirection, FontFamily e Brushes existem em
// mais de um namespace; use aliases WPF explícitos para evitar CS0104.
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfDrawingContext = System.Windows.Media.DrawingContext;
using WpfDrawingVisual = System.Windows.Media.DrawingVisual;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfImageSource = System.Windows.Media.ImageSource;
using WpfPoint = System.Windows.Point;
using WpfPen = System.Windows.Media.Pen;
using WpfRect = System.Windows.Rect;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfTypeface = System.Windows.Media.Typeface;
using WpfFormattedText = System.Windows.Media.FormattedText;
using WpfRenderTargetBitmap = System.Windows.Media.Imaging.RenderTargetBitmap;
using WpfPixelFormats = System.Windows.Media.PixelFormats;

namespace Civil3D2026Plugin.UI.Ribbon;

/// <summary>
/// Gera icones simples em memoria para manter o plugin autocontido.
/// No futuro, esta classe pode ser substituida por PNG/SVG corporativos sem
/// alterar o catalogo nem a montagem da Ribbon.
/// </summary>
internal static class RibbonIconFactory
{
    private static readonly Dictionary<string, WpfImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static WpfImageSource Create(string category, string commandName, int size)
    {
        string key = $"{category}|{commandName}|{size}";
        if (Cache.TryGetValue(key, out WpfImageSource? cached))
            return cached;

        string glyph = GetGlyph(commandName);
        WpfColor accent = GetCategoryColor(category);

        // Identidade visual estilo ferramentas CAD: icones azulados em
        // placa escura, separados em paineis pela Ribbon existente.
        var visual = new WpfDrawingVisual();
        using (WpfDrawingContext dc = visual.RenderOpen())
        {
            double margin = Math.Max(1.0, size * 0.06);
            double radius = Math.Max(2.0, size * 0.16);

            dc.DrawRoundedRectangle(
                new WpfSolidColorBrush(WpfColor.FromRgb(28, 43, 60)),
                null,
                new WpfRect(margin, margin, size - (margin * 2.0), size - (margin * 2.0)),
                radius,
                radius);

            if (commandName.Equals("CORRSPLIT", StringComparison.OrdinalIgnoreCase))
            {
                // Dois eixos independentes e seta de separacao.
                var pen = new WpfPen(new WpfSolidColorBrush(accent), Math.Max(1.4, size * 0.07));
                var thin = new WpfPen(new WpfSolidColorBrush(WpfColor.FromRgb(225, 237, 247)),
                    Math.Max(0.8, size * 0.037));
                dc.DrawLine(pen, new WpfPoint(size * 0.29, size * 0.20), new WpfPoint(size * 0.29, size * 0.80));
                dc.DrawLine(pen, new WpfPoint(size * 0.68, size * 0.20), new WpfPoint(size * 0.68, size * 0.80));
                dc.DrawLine(thin, new WpfPoint(size * 0.38, size * 0.22), new WpfPoint(size * 0.38, size * 0.78));
                dc.DrawLine(thin, new WpfPoint(size * 0.59, size * 0.22), new WpfPoint(size * 0.59, size * 0.78));
                dc.DrawLine(pen, new WpfPoint(size * 0.43, size * 0.50), new WpfPoint(size * 0.54, size * 0.50));
            }
            else
            {
                double fontSize = glyph.Length >= 3 ? size * 0.30 : size * 0.38;
                var text = new WpfFormattedText(
                    glyph,
                    CultureInfo.InvariantCulture,
                    WpfFlowDirection.LeftToRight,
                    new WpfTypeface(new WpfFontFamily("Segoe UI"), System.Windows.FontStyles.Normal,
                        System.Windows.FontWeights.SemiBold, System.Windows.FontStretches.Normal),
                    fontSize,
                    new WpfSolidColorBrush(accent),
                    1.0);
                WpfPoint origin = new(
                    (size - text.Width) / 2.0,
                    (size - text.Height) / 2.0);
                dc.DrawText(text, origin);
            }
        }

        var bitmap = new WpfRenderTargetBitmap(size, size, 96.0, 96.0, WpfPixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        Cache[key] = bitmap;
        return bitmap;
    }

    private static string GetGlyph(string commandName)
    {
        if (commandName.StartsWith("DRENEXCEL", StringComparison.OrdinalIgnoreCase))
            return "DX";
        if (commandName.StartsWith("DRENNUM", StringComparison.OrdinalIgnoreCase))
            return "DN";
        if (commandName.StartsWith("QTO", StringComparison.OrdinalIgnoreCase) ||
            commandName.Equals("TESTEQTO", StringComparison.OrdinalIgnoreCase))
            return "Q";
        if (commandName.StartsWith("MF", StringComparison.OrdinalIgnoreCase))
            return "MF";
        if (commandName.StartsWith("C3DSOLID", StringComparison.OrdinalIgnoreCase))
            return "3D";
        if (commandName.StartsWith("PASSAGEM", StringComparison.OrdinalIgnoreCase))
            return "PS";
        if (commandName.StartsWith("FL", StringComparison.OrdinalIgnoreCase))
            return "FL";
        if (commandName.StartsWith("SURF", StringComparison.OrdinalIgnoreCase) ||
            commandName.StartsWith("SF", StringComparison.OrdinalIgnoreCase) ||
            commandName.StartsWith("TRIM", StringComparison.OrdinalIgnoreCase) ||
            commandName.StartsWith("CORTAR", StringComparison.OrdinalIgnoreCase))
            return "SF";
        if (commandName.StartsWith("PT", StringComparison.OrdinalIgnoreCase))
            return "N";
        if (commandName.StartsWith("C3D", StringComparison.OrdinalIgnoreCase))
            return "C3D";

        string compact = new(commandName.Where(char.IsLetterOrDigit).Take(2).ToArray());
        return string.IsNullOrWhiteSpace(compact) ? "C3D" : compact.ToUpperInvariant();
    }

    private static WpfColor GetCategoryColor(string category) =>
        category switch
        {
            "Drenagem" => WpfColor.FromRgb(65, 158, 229),
            "QTO" => WpfColor.FromRgb(123, 202, 147),
            "Corridor" => WpfColor.FromRgb(56, 175, 241),
            "Feature Lines" => WpfColor.FromRgb(78, 198, 187),
            "Superficies" => WpfColor.FromRgb(218, 172, 98),
            "Geometria" => WpfColor.FromRgb(162, 141, 225),
            _ => WpfColor.FromRgb(192, 205, 215)
        };
}
