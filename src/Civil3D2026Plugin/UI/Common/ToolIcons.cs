using System;
using System.Collections.Generic;

using WM = System.Windows.Media;

namespace Civil3D2026Plugin.UI.Common;

/// <summary>
/// Icones vetoriais de 16x16 dos paineis (desenho em IconArt, o mesmo da Ribbon). Desenhados em
/// codigo para a DLL continuar autocontida.
/// </summary>
internal static class ToolIcons
{
    private static readonly WM.Brush Ambar = Congelado(new WM.SolidColorBrush(IconArt.Ambar));

    private static WM.Brush Congelado(WM.SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>Icone no tom do texto (traco) e do destaque (detalhes e massa), com a marca ambar. Chave desconhecida usa "generico".</summary>
    public static WM.ImageSource Create(string? key, WM.Brush traco, WM.Brush destaque)
    {
        var group = new WM.DrawingGroup();
        // Moldura transparente para o icone ocupar sempre 16x16.
        group.Children.Add(new WM.GeometryDrawing(WM.Brushes.Transparent, null, new WM.RectangleGeometry(new System.Windows.Rect(0, 0, 16, 16))));
        group.Children.Add(IconArt.Desenho(key, traco, destaque, Ambar));
        var image = new WM.DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>Icone padrao de cada categoria das LISPs (quando o arquivo nao tem ;;@ICONE).</summary>
    public static string ForCategory(string categoria) => categoria switch
    {
        "Hachuras" => "hachura",
        "Polilinhas e geometria" => "polilinha",
        "Vias e meio-fio" => "via",
        "Pranchas e layouts" => "prancha",
        "Blocos, textos e cores" => "bloco",
        "Tabelas e Excel" => "tabela",
        "3D" => "solido3d",
        _ => "generico"
    };
}
