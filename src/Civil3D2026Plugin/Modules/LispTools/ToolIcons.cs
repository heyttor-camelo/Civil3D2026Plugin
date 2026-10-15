using System;
using System.Collections.Generic;

using WM = System.Windows.Media;

namespace Civil3D2026Plugin.Modules.LispTools;

/// <summary>
/// Icones vetoriais de 16x16 da lista de ferramentas: um traco no tom do texto e um
/// detalhe na cor de destaque. Desenhados em codigo para a DLL continuar autocontida.
/// </summary>
internal static class ToolIcons
{
    private sealed record Icon(string Base, string Detalhe, double DetalheEspessura = 1.2);

    private static readonly Dictionary<string, Icon> Icons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hachura"] = new("M2.5,2.5 H13.5 V13.5 H2.5 Z", "M2.5,8 L8,2.5 M2.5,13.5 L13.5,2.5 M8,13.5 L13.5,8"),
        ["zebra"] = new("M2.5,2.5 H13.5 V13.5 H2.5 Z", "M2.5,6.5 L6.5,2.5 M2.5,12 L12,2.5 M7.5,13.5 L13.5,7.5", 2.0),
        ["diagnostico"] = new("M2.5,2.5 H10.5 V10.5 H2.5 Z M2.5,7 L7,2.5", "M8,10.5 A2.5,2.5 0 1 0 13,10.5 A2.5,2.5 0 1 0 8,10.5 M12.3,12.3 L14.5,14.5"),
        ["polilinha"] = new("M2,13 L6,5 L10,10 L14,3", "M5,4 H7 V6 H5 Z M9,9 H11 V11 H9 Z"),
        ["offset"] = new("M5.5,5.5 H10.5 V10.5 H5.5 Z", "M2.5,2.5 H13.5 V13.5 H2.5 Z"),
        ["curva"] = new("M2,13.5 H6 M13.5,6 V2", "M6,13.5 A7.5,7.5 0 0 0 13.5,6"),
        ["via"] = new("M3,2 V14 M13,2 V14", "M8,2 V4.5 M8,6.75 V9.25 M8,11.5 V14"),
        ["prancha"] = new("M3.5,1.5 H10 L13,4.5 V14.5 H3.5 Z M10,1.5 V4.5 H13", "M5.5,8.5 H10.5 M5.5,11 H10.5"),
        ["imprimir"] = new("M4.5,6 V2 H11.5 V6 M2.5,6 H13.5 V11 H2.5 Z", "M4.5,9 H11.5 V14 H4.5 Z"),
        ["camadas"] = new("M8,2 L14,5 L8,8 L2,5 Z", "M2,8 L8,11 L14,8 M2,11 L8,14 L14,11"),
        ["malha"] = new("M2.5,2.5 H13.5 V13.5 H2.5 Z", "M6.2,2.5 V13.5 M9.8,2.5 V13.5 M2.5,6.2 H13.5 M2.5,9.8 H13.5"),
        ["mapa"] = new("M2,3 L6,2 L10,3.5 L14,2.5 V13 L10,14 L6,12.5 L2,13.5 Z", "M6.5,6 H10.5 V10 H6.5 Z"),
        ["bloco"] = new("M4.5,3 H13.5 V11.5 H4.5 Z", "M1.5,11.5 H7.5 M4.5,8.5 V14.5"),
        ["numero"] = new("M6.5,2 L5,14 M11,2 L9.5,14", "M3,6 H14 M2,10.5 H13"),
        ["cor"] = new("M2.5,9.5 A3,3 0 1 0 8.5,9.5 A3,3 0 1 0 2.5,9.5 M7.5,9.5 A3,3 0 1 0 13.5,9.5 A3,3 0 1 0 7.5,9.5", "M5,5 A3,3 0 1 0 11,5 A3,3 0 1 0 5,5"),
        ["medida"] = new("M1.5,5.5 H14.5 V10.5 H1.5 Z", "M4.5,5.5 V8 M7.5,5.5 V9 M10.5,5.5 V8"),
        ["tabela"] = new("M2,3 H14 V13 H2 Z M2,9.5 H14 M7,3 V13", "M2,6.25 H14"),
        ["solido3d"] = new("M3,5 L8,2.5 L13,5 V11 L8,13.5 L3,11 Z", "M3,5 L8,7.5 L13,5 M8,7.5 V13.5"),
        ["juntar"] = new("M2,13 L6.6,8.9 M9.4,7.1 L14,3", "M6.5,8 A1.5,1.5 0 1 0 9.5,8 A1.5,1.5 0 1 0 6.5,8"),
        ["quebrar"] = new("M2,8 H6.5 M9.5,8 H14", "M8,2 V14"),
        ["generico"] = new("M3,3 H13 V13 H3 Z", "M5.5,8 H10.5")
    };

    /// <summary>Icone no tom do texto (traco) e do destaque (detalhe). Chave desconhecida usa "generico".</summary>
    public static WM.ImageSource Create(string? key, WM.Brush traco, WM.Brush destaque)
    {
        if (key is null || !Icons.TryGetValue(key, out Icon? icon))
            icon = Icons["generico"];

        var group = new WM.DrawingGroup();

        // Moldura transparente para o icone ocupar sempre 16x16.
        group.Children.Add(new WM.GeometryDrawing(WM.Brushes.Transparent, null, new WM.RectangleGeometry(new System.Windows.Rect(0, 0, 16, 16))));

        var pBase = new WM.Pen(traco, 1.2) { StartLineCap = WM.PenLineCap.Round, EndLineCap = WM.PenLineCap.Round, LineJoin = WM.PenLineJoin.Round };
        var pDetalhe = new WM.Pen(destaque, icon.DetalheEspessura) { StartLineCap = WM.PenLineCap.Round, EndLineCap = WM.PenLineCap.Round, LineJoin = WM.PenLineJoin.Round };
        group.Children.Add(new WM.GeometryDrawing(null, pBase, WM.Geometry.Parse(icon.Base)));
        group.Children.Add(new WM.GeometryDrawing(null, pDetalhe, WM.Geometry.Parse(icon.Detalhe)));

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
