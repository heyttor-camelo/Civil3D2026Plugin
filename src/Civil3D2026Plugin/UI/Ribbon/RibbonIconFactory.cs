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
/// Icones da Ribbon: o pictograma do comando direto no fundo da Ribbon, como os do AutoCAD (desenho
/// em IconArt, o mesmo dos paineis), com os detalhes na cor do modulo e a marca ambar; variantes
/// (ajuda, configurar, atualizar, editar, previa...) ganham um selo ambar quadrado no canto. As
/// cores seguem o tema do AutoCAD (COLORTHEME: escuro ou claro; trocar o tema pede C3DRIBBONRESET).
/// A rede viaria usa os pictogramas dela.
/// </summary>
internal static class RibbonIconFactory
{
    private static readonly Dictionary<string, WpfImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tema escuro do AutoCAD (COLORTHEME = 0).</summary>
    private static bool TemaEscuro()
    {
        try
        {
            return Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("COLORTHEME"), CultureInfo.InvariantCulture) == 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static WpfImageSource Create(string category, string commandName, int size)
    {
        bool escuro = TemaEscuro();
        string key = $"{category}|{commandName}|{size}|{(escuro ? "e" : "c")}";
        if (Cache.TryGetValue(key, out WpfImageSource? cached))
            return cached;

        (WpfColor cima, _, WpfColor tinta) = IconArt.Categorias.TryGetValue(category, out var c) ? c : IconArt.Categorias["Geral"];
        (string icone, string? selo) = IconArt.Comandos.TryGetValue(commandName, out var m) ? m : (PorPrefixo(commandName), null);
        WpfColor linha = escuro ? WpfColor.FromRgb(228, 232, 237) : WpfColor.FromRgb(43, 48, 54);
        WpfColor destaque = escuro ? tinta : cima;
        WpfColor fundo = escuro ? WpfColor.FromRgb(59, 68, 83) : WpfColor.FromRgb(242, 242, 242);

        var grupo = new WM.DrawingGroup();
        // Moldura transparente: o icone ocupa sempre 16x16.
        grupo.Children.Add(new WM.GeometryDrawing(WpfBrushes.Transparent, null, new WM.RectangleGeometry(new WpfRect(0, 0, 16, 16))));

        // Pictograma (menor e puxado para cima/esquerda quando tem selo).
        double escala = selo is null ? 1.0 : 0.8, d = selo is null ? 0 : -0.7;
        string viario = Civil3D2026Plugin.Modules.Viario.ViarioIcons.KeyForCommand(commandName);
        string hydro = Civil3D2026Plugin.Modules.Hydro.HydroIcons.KeyForCommand(commandName);
        bool usaViario = category == "Viario" && Civil3D2026Plugin.Modules.Viario.ViarioIcons.Has(viario);
        bool usaHydro = category == "Hydro" && Civil3D2026Plugin.Modules.Hydro.HydroIcons.Has(hydro);
        if (usaViario || usaHydro)
        {
            // Pictogramas da rede viaria e do HYDRO (grade 32), com o branco trocado pelo tom da linha no tema claro.
            WM.Drawing figura = usaViario
                ? Civil3D2026Plugin.Modules.Viario.ViarioIcons.Image(viario, plate: false, branco: escuro ? null : linha).Drawing
                : Civil3D2026Plugin.Modules.Hydro.HydroIcons.Image(hydro, plate: false, branco: escuro ? null : linha).Drawing;
            var vt = new WM.TransformGroup();
            vt.Children.Add(new WM.TranslateTransform(-16, -16));
            vt.Children.Add(new WM.ScaleTransform(escala * 0.5, escala * 0.5));
            vt.Children.Add(new WM.TranslateTransform(8 + d, 8 + d));
            var gv = new WM.DrawingGroup { Transform = vt };
            gv.Children.Add(figura);
            grupo.Children.Add(gv);
        }
        else
        {
            WM.DrawingGroup desenho = IconArt.Desenho(icone, new WpfSolidColorBrush(linha), new WpfSolidColorBrush(destaque), new WpfSolidColorBrush(IconArt.Ambar), 1.3);
            var tg = new WM.TransformGroup();
            tg.Children.Add(new WM.TranslateTransform(-8, -8));
            tg.Children.Add(new WM.ScaleTransform(escala, escala));
            tg.Children.Add(new WM.TranslateTransform(8 + d, 8 + d));
            desenho.Transform = tg;
            grupo.Children.Add(desenho);
        }

        // Selo ambar quadrado da variante, com um contorno no tom do fundo para destacar do desenho.
        if (selo is not null && IconArt.Selos.TryGetValue(selo, out string? dadosSelo))
        {
            grupo.Children.Add(new WM.GeometryDrawing(new WpfSolidColorBrush(IconArt.Ambar), new WpfPen(new WpfSolidColorBrush(fundo), 0.8),
                new WM.RectangleGeometry(new WpfRect(9.3, 9.3, 5.9, 5.9))));
            var simbolo = WM.Geometry.Parse(dadosSelo).Clone();
            var st = new WM.TransformGroup();
            st.Children.Add(new WM.TranslateTransform(-12.2, -12.2));
            st.Children.Add(new WM.ScaleTransform(0.85, 0.85));
            st.Children.Add(new WM.TranslateTransform(12.25, 12.25));
            simbolo.Transform = st;
            var caneta = new WpfPen(new WpfSolidColorBrush(WpfColor.FromRgb(42, 36, 16)), 0.9) { StartLineCap = WM.PenLineCap.Round, EndLineCap = WM.PenLineCap.Round, LineJoin = WM.PenLineJoin.Round };
            grupo.Children.Add(new WM.GeometryDrawing(null, caneta, simbolo));
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
