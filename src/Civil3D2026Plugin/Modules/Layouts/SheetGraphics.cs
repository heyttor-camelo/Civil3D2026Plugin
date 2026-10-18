using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Civil3D2026Plugin.UI.Common;

using SW = System.Windows;
using WM = System.Windows.Media;
using WPoint = System.Windows.Point;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// Desenho da miniatura de um layout. A geometria e montada uma vez por layout (em cache)
/// em coordenadas do papel com Y invertido; a cada desenho so muda a transformacao.
/// Linhas usam a transformacao da geometria (traco constante em pixels); textos ficam
/// num DrawingGroup a parte (escalam junto, sem custo de remontar).
/// </summary>
internal static class SheetGraphics
{
    private static readonly WM.Typeface Fonte = new("Arial");
    private static readonly WM.Brush Preenchimento = ToolTheme.Brush(150, 152, 156, 140);

    // Folha branca do page setup (como o layout do AutoCAD): desenho escuro sobre o papel.
    private static readonly WM.Brush Papel = ToolTheme.Brush(255, 255, 255);
    private static readonly WM.Brush Sombra = ToolTheme.Brush(0, 0, 0, 70);
    private static readonly WM.Brush TintaLinha = ToolTheme.Brush(45, 45, 48);
    private static readonly WM.Brush TintaTexto = ToolTheme.Brush(20, 20, 24);
    private static readonly WM.Brush TintaPreenchimento = ToolTheme.Brush(70, 72, 76, 150);
    private static readonly WM.Brush BordaArea = ToolTheme.Brush(150, 150, 155);

    /// <summary>Mostra a folha do page setup (papel branco e area de impressao). Opcao da barra da vista grafica.</summary>
    public static bool MostrarPapel { get; set; } = true;

    /// <summary>A folha deste layout e desenhada (papel branco).</summary>
    public static bool ComPapel(SheetData folha) => MostrarPapel && folha.Folha is { Length: 4 };

    /// <summary>Cor de texto legivel sobre a miniatura (rotulos das viewports).</summary>
    public static WM.Brush TextoSobre(SheetData folha) => ComPapel(folha) ? TintaTexto : ToolTheme.GraphicText;

    private sealed class Cache
    {
        public WM.StreamGeometry Linhas = null!;
        public WM.StreamGeometry Preenchimentos = null!;
        public WM.StreamGeometry Imagens = null!;
        public WM.DrawingGroup? TextosTodos;
        public WM.DrawingGroup? TextosGrandes;
        public bool TextosNoPapel;
        public SW.Rect Limites;
    }

    /// <summary>Transformacao papel -> tela usada no ultimo desenho.</summary>
    internal sealed record Mapa(double Escala, double Ox, double Oy, double MinX, double MaxY)
    {
        public SW.Point Tela(WPoint p) => new(Ox + (p.X - MinX) * Escala, Oy + (MaxY - p.Y) * Escala);

        public WPoint Papel(SW.Point t) => new(MinX + (t.X - Ox) / Escala, MaxY - (t.Y - Oy) / Escala);
    }

    /// <summary>
    /// Desenha o papel do layout no canvas (limpa antes). Retorna o mapa papel->tela para
    /// as viewports serem desenhadas por cima. zoom=1 e pan=0 enquadram a folha toda.
    /// </summary>
    public static Mapa Desenhar(System.Windows.Controls.Canvas canvas, SheetData folha, double w, double h,
        double zoom, SW.Vector pan, bool textos, bool soTextosGrandes, double margem = 12)
    {
        canvas.Children.Clear();
        Cache c = Obter(folha);

        double bw = double.IsFinite(folha.Largura) ? Math.Max(folha.Largura, 1e-6) : 1;
        double bh = double.IsFinite(folha.Altura) ? Math.Max(folha.Altura, 1e-6) : 1;
        double s0 = Math.Min((w - 2 * margem) / bw, (h - 2 * margem) / bh);
        double s = Math.Max(1e-9, s0 * zoom);
        double ox = (w - bw * s) / 2.0 + pan.X, oy = (h - bh * s) / 2.0 + pan.Y;
        var mapa = new Mapa(s, ox, oy, folha.MinX, folha.MaxY);

        // Geometria em (x, -y): tela = (ox + (x - minX) s, oy + (-y + maxY) s).
        var mt = new WM.MatrixTransform(new WM.Matrix(s, 0, 0, s, ox - folha.MinX * s, oy + folha.MaxY * s));
        mt.Freeze();

        bool papel = ComPapel(folha);
        if (papel)
        {
            // Folha do page setup: sombra, papel branco e a area de impressao tracejada.
            SW.Point[] f = folha.Folha!.Select(mapa.Tela).ToArray();
            var ret = new SW.Rect(f[0], f[2]);
            var sombra = new System.Windows.Shapes.Rectangle { Width = ret.Width, Height = ret.Height, Fill = Sombra, IsHitTestVisible = false };
            System.Windows.Controls.Canvas.SetLeft(sombra, ret.X + 3);
            System.Windows.Controls.Canvas.SetTop(sombra, ret.Y + 3);
            canvas.Children.Add(sombra);
            var folhaBranca = new System.Windows.Shapes.Rectangle { Width = ret.Width, Height = ret.Height, Fill = Papel, IsHitTestVisible = false };
            System.Windows.Controls.Canvas.SetLeft(folhaBranca, ret.X);
            System.Windows.Controls.Canvas.SetTop(folhaBranca, ret.Y);
            canvas.Children.Add(folhaBranca);
            if (folha.AreaImpressao is { Length: 4 } area)
            {
                SW.Point[] a = area.Select(mapa.Tela).ToArray();
                var ra = new SW.Rect(a[0], a[2]);
                var borda = new System.Windows.Shapes.Rectangle
                {
                    Width = ra.Width,
                    Height = ra.Height,
                    Stroke = BordaArea,
                    StrokeThickness = 0.8,
                    StrokeDashArray = new WM.DoubleCollection { 4, 3 },
                    IsHitTestVisible = false
                };
                System.Windows.Controls.Canvas.SetLeft(borda, ra.X);
                System.Windows.Controls.Canvas.SetTop(borda, ra.Y);
                canvas.Children.Add(borda);
            }
        }

        if (!c.Preenchimentos.IsEmpty())
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Envolver(c.Preenchimentos, mt),
                Fill = papel ? TintaPreenchimento : Preenchimento,
                IsHitTestVisible = false
            });

        if (!c.Imagens.IsEmpty())
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Envolver(c.Imagens, mt),
                Fill = ToolTheme.Brush(90, 110, 130, 90),
                Stroke = ToolTheme.GraphicLine,
                StrokeThickness = 0.7,
                StrokeDashArray = new WM.DoubleCollection { 2, 2 },
                IsHitTestVisible = false
            });

        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Envolver(c.Linhas, mt),
            Stroke = papel ? TintaLinha : ToolTheme.GraphicLine,
            StrokeThickness = 0.7,
            IsHitTestVisible = false
        });

        if (textos && folha.Textos.Count > 0)
        {
            if (c.TextosNoPapel != papel)
            {
                // Cor do texto mudou (papel ligado/desligado): refaz os grupos de texto.
                c.TextosTodos = c.TextosGrandes = null;
                c.TextosNoPapel = papel;
            }
            WM.DrawingGroup? grupo = soTextosGrandes ? Grandes(folha, c) : Todos(folha, c);
            if (grupo is not null)
            {
                var img = new System.Windows.Controls.Image
                {
                    Source = new WM.DrawingImage(grupo),
                    Width = c.Limites.Width * s,
                    Height = c.Limites.Height * s,
                    Stretch = WM.Stretch.Fill,
                    IsHitTestVisible = false
                };
                System.Windows.Controls.Canvas.SetLeft(img, ox + (c.Limites.X - folha.MinX) * s);
                System.Windows.Controls.Canvas.SetTop(img, oy + (c.Limites.Y + folha.MaxY) * s);
                canvas.Children.Add(img);
            }
        }

        return mapa;
    }

    private static WM.Geometry Envolver(WM.Geometry g, WM.Transform t)
    {
        var grupo = new WM.GeometryGroup { Transform = t };
        grupo.Children.Add(g);
        grupo.Freeze();
        return grupo;
    }

    private static Cache Obter(SheetData folha)
    {
        if (folha.Cache is Cache c)
            return c;

        c = new Cache
        {
            TextosNoPapel = ComPapel(folha),
            Linhas = Geometria(folha.Linhas, fechar: false),
            Preenchimentos = Geometria(folha.Preenchimentos, fechar: true),
            Imagens = Molduras(folha.Imagens),
            Limites = new SW.Rect(folha.MinX, -folha.MaxY, Math.Max(folha.Largura, 1e-6), Math.Max(folha.Altura, 1e-6))
        };
        folha.Cache = c;
        return c;
    }

    private static WM.StreamGeometry Geometria(List<WPoint[]> lista, bool fechar)
    {
        var geo = new WM.StreamGeometry();
        using (WM.StreamGeometryContext ctx = geo.Open())
        {
            foreach (WPoint[] pts in lista)
            {
                if (pts.Length < 2)
                    continue;
                ctx.BeginFigure(new SW.Point(pts[0].X, -pts[0].Y), fechar, fechar);
                ctx.PolyLineTo(pts.Skip(1).Select(p => new SW.Point(p.X, -p.Y)).ToList(), true, false);
            }
        }
        geo.Freeze();
        return geo;
    }

    /// <summary>Retangulo fechado mais a diagonal (marca de imagem/OLE).</summary>
    private static WM.StreamGeometry Molduras(List<WPoint[]> lista)
    {
        var geo = new WM.StreamGeometry();
        using (WM.StreamGeometryContext ctx = geo.Open())
        {
            foreach (WPoint[] pts in lista)
            {
                if (pts.Length < 4)
                    continue;
                ctx.BeginFigure(new SW.Point(pts[0].X, -pts[0].Y), true, true);
                ctx.PolyLineTo(pts.Skip(1).Select(p => new SW.Point(p.X, -p.Y)).ToList(), true, false);
                ctx.BeginFigure(new SW.Point(pts[0].X, -pts[0].Y), false, false);
                ctx.LineTo(new SW.Point(pts[2].X, -pts[2].Y), true, false);
            }
        }
        geo.Freeze();
        return geo;
    }

    private static WM.DrawingGroup? Todos(SheetData folha, Cache c) =>
        c.TextosTodos ??= Textos(folha, c, folha.Textos);

    /// <summary>Para os cartoes da vista "Todos": so os textos grandes (titulos, numero da prancha).</summary>
    private static WM.DrawingGroup? Grandes(SheetData folha, Cache c)
    {
        if (c.TextosGrandes is not null)
            return c.TextosGrandes;
        double minimo = Math.Min(folha.Largura, folha.Altura) * 0.009;
        c.TextosGrandes = Textos(folha, c, folha.Textos.Where(t => t.Altura >= minimo).ToList());
        return c.TextosGrandes;
    }

    private static WM.DrawingGroup? Textos(SheetData folha, Cache c, IReadOnlyCollection<SheetText> textos)
    {
        if (textos.Count == 0)
            return null;

        var grupo = new WM.DrawingGroup();
        using (WM.DrawingContext dc = grupo.Open())
        {
            // Moldura transparente: os limites do desenho ficam iguais aos da folha.
            dc.DrawRectangle(WM.Brushes.Transparent, null, c.Limites);
            foreach (SheetText t in textos)
            {
                try
                {
                    Texto(dc, t, c.TextosNoPapel ? TintaTexto : ToolTheme.GraphicText);
                }
                catch (ArgumentException)
                {
                    // Texto com tamanho fora do aceito pelo WPF: ignora.
                }
            }
        }
        grupo.ClipGeometry = new WM.RectangleGeometry(c.Limites);
        grupo.Freeze();
        return grupo;
    }

    private static void Texto(WM.DrawingContext dc, SheetText t, WM.Brush cor)
    {
        double em = t.Altura / 0.716; // altura de maiuscula da Arial ~ 0,716 em
        var ft = new WM.FormattedText(t.Texto, CultureInfo.InvariantCulture, SW.FlowDirection.LeftToRight,
            Fonte, Math.Min(em, 30000), cor, 1.0);

        double dx;
        if (t.Multilinha)
        {
            ft.LineHeight = t.Altura * 5.0 / 3.0;
            if (t.LarguraMax > t.Altura)
            {
                ft.MaxTextWidth = t.LarguraMax;
                ft.TextAlignment = t.H switch { 1 => SW.TextAlignment.Center, 2 => SW.TextAlignment.Right, _ => SW.TextAlignment.Left };
                dx = t.H switch { 1 => -t.LarguraMax / 2, 2 => -t.LarguraMax, _ => 0 };
            }
            else
            {
                dx = t.H switch { 1 => -ft.Width / 2, 2 => -ft.Width, _ => 0 };
            }
        }
        else
        {
            dx = t.H switch { 1 => -ft.Width / 2, 2 => -ft.Width, _ => 0 };
        }

        double dy = t.Multilinha
            ? t.V switch { 2 => -(ft.Baseline - t.Altura), 1 => -ft.Height / 2, _ => -ft.Height }
            : t.V switch { 1 => -ft.Baseline + t.Altura / 2, 2 => -ft.Baseline + t.Altura, 3 => -ft.Height, _ => -ft.Baseline };

        dc.PushTransform(new WM.TranslateTransform(t.Pos.X, -t.Pos.Y));
        dc.PushTransform(new WM.RotateTransform(-t.RotacaoGraus));
        dc.DrawText(ft, new SW.Point(dx, dy));
        dc.Pop();
        dc.Pop();
    }
}
