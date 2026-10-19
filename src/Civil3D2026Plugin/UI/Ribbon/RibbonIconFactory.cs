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
using WM = System.Windows.Media;
using Civil3D2026Plugin.UI.Common;

namespace Civil3D2026Plugin.UI.Ribbon;

/// <summary>
/// Icones da Ribbon: placa arredondada na cor do modulo (degrade, grade de prancheta bem leve e
/// brilho no topo) com o pictograma do comando em branco e na tinta do modulo (desenho em
/// IconArt, o mesmo dos paineis); variantes (ajuda, configurar, atualizar, editar, previa...)
/// ganham um selo ambar no canto. A rede viaria usa os pictogramas dela sobre a mesma placa.
/// </summary>
internal static class RibbonIconFactory
{
    private static readonly Dictionary<string, WpfImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static WpfImageSource Create(string category, string commandName, int size)
    {
        string key = $"{category}|{commandName}|{size}";
        if (Cache.TryGetValue(key, out WpfImageSource? cached))
            return cached;

        (WpfColor cima, WpfColor baixo, WpfColor tinta) = IconArt.Categorias.TryGetValue(category, out var c) ? c : IconArt.Categorias["Geral"];
        (string icone, string? selo) = IconArt.Comandos.TryGetValue(commandName, out var m) ? m : (PorPrefixo(commandName), null);

        var grupo = new WM.DrawingGroup();
        // Placa: degrade de cima para baixo, grade bem leve e brilho no topo.
        var degrade = new WM.LinearGradientBrush(cima, baixo, new WpfPoint(0, 0), new WpfPoint(0, 1));
        grupo.Children.Add(new WM.GeometryDrawing(degrade, null, new WM.RectangleGeometry(new WpfRect(0.3, 0.3, 15.4, 15.4), 3.4, 3.4)));
        var grade = new WpfPen(new WpfSolidColorBrush(WpfColor.FromArgb(18, 255, 255, 255)), 0.3);
        foreach (double v in new[] { 4.0, 8.0, 12.0 })
        {
            grupo.Children.Add(new WM.GeometryDrawing(null, grade, new WM.LineGeometry(new WpfPoint(v, 1), new WpfPoint(v, 15))));
            grupo.Children.Add(new WM.GeometryDrawing(null, grade, new WM.LineGeometry(new WpfPoint(1, v), new WpfPoint(15, v))));
        }
        var brilho = new WpfPen(new WpfSolidColorBrush(WpfColor.FromArgb(64, 255, 255, 255)), 0.5) { StartLineCap = WM.PenLineCap.Round, EndLineCap = WM.PenLineCap.Round };
        grupo.Children.Add(new WM.GeometryDrawing(null, brilho, new WM.LineGeometry(new WpfPoint(3.4, 0.9), new WpfPoint(12.6, 0.9))));

        // Pictograma (menor e puxado para cima/esquerda quando tem selo).
        double escala = selo is null ? 0.8 : 0.74, d = selo is null ? 0 : -0.5;
        string viario = Civil3D2026Plugin.Modules.Viario.ViarioIcons.KeyForCommand(commandName);
        if (category == "Viario" && Civil3D2026Plugin.Modules.Viario.ViarioIcons.Has(viario))
        {
            // Pictogramas da rede viaria (grade 32) sobre a placa nova.
            WM.Drawing figura = Civil3D2026Plugin.Modules.Viario.ViarioIcons.Image(viario, plate: false).Drawing;
            escala *= 0.5;
            var vt = new WM.TransformGroup();
            vt.Children.Add(new WM.TranslateTransform(-16, -16));
            vt.Children.Add(new WM.ScaleTransform(escala, escala));
            vt.Children.Add(new WM.TranslateTransform(8 + d, 8 + d));
            var gv = new WM.DrawingGroup { Transform = vt };
            gv.Children.Add(figura);
            grupo.Children.Add(gv);
        }
        else
        {
            var branco = new WpfSolidColorBrush(WpfColor.FromRgb(244, 248, 252));
            WM.DrawingGroup desenho = IconArt.Desenho(icone, branco, new WpfSolidColorBrush(tinta), new WpfSolidColorBrush(IconArt.Ambar), 1.35);
            var tg = new WM.TransformGroup();
            tg.Children.Add(new WM.TranslateTransform(-8, -8));
            tg.Children.Add(new WM.ScaleTransform(escala, escala));
            tg.Children.Add(new WM.TranslateTransform(8 + d, 8 + d));
            desenho.Transform = tg;
            grupo.Children.Add(desenho);
        }

        // Selo ambar da variante.
        if (selo is not null && IconArt.Selos.TryGetValue(selo, out string? dadosSelo))
        {
            grupo.Children.Add(new WM.GeometryDrawing(new WpfSolidColorBrush(IconArt.Ambar), new WpfPen(new WpfSolidColorBrush(baixo), 0.8),
                new WM.EllipseGeometry(new WpfPoint(12.2, 12.2), 3.25, 3.25)));
            var simbolo = WM.Geometry.Parse(dadosSelo).Clone();
            var st = new WM.TransformGroup();
            st.Children.Add(new WM.TranslateTransform(-12.2, -12.2));
            st.Children.Add(new WM.ScaleTransform(0.85, 0.85));
            st.Children.Add(new WM.TranslateTransform(12.2, 12.2));
            simbolo.Transform = st;
            var tinta2 = new WpfPen(new WpfSolidColorBrush(WpfColor.FromRgb(42, 36, 16)), 0.9) { StartLineCap = WM.PenLineCap.Round, EndLineCap = WM.PenLineCap.Round, LineJoin = WM.PenLineJoin.Round };
            grupo.Children.Add(new WM.GeometryDrawing(null, tinta2, simbolo));
        }

        var visual = new WpfDrawingVisual();
        using (WpfDrawingContext dc = visual.RenderOpen())
        {
            dc.PushTransform(new WM.ScaleTransform(size / 16.0, size / 16.0));
            dc.DrawDrawing(grupo);
            dc.Pop();
        }
        var bitmap = new WpfRenderTargetBitmap(size, size, 96.0, 96.0, WpfPixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        Cache[key] = bitmap;
        return bitmap;
    }

    /// <summary>Comando fora do mapa (novo): icone pelo comeco do nome.</summary>
    private static string PorPrefixo(string comando)
    {
        string c = comando.ToUpperInvariant();
        if (c.StartsWith("DREN")) return "drenagem";
        if (c.StartsWith("QTO")) return "qto";
        if (c.StartsWith("MF")) return "rebaixo";
        if (c.StartsWith("C3DSOLIDLIGAR")) return "estais";
        if (c.StartsWith("C3DSOLID")) return "solido3d";
        if (c.StartsWith("C3DSECAO")) return "secoes";
        if (c.StartsWith("C3DLAYOUT") || c.StartsWith("C3DPRANCHA")) return "layouts";
        if (c.StartsWith("C3DLISP")) return "lisp";
        if (c.StartsWith("PASSAGEM")) return "passagem";
        if (c.StartsWith("PED")) return "ped";
        if (c.StartsWith("FL")) return "featureline";
        if (c.StartsWith("SURF") || c.StartsWith("SF")) return "superficie";
        return "generico";
    }
}
