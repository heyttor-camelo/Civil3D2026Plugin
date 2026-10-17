using System;
using System.Collections.Generic;

using WM = System.Windows.Media;

namespace Civil3D2026Plugin.UI.Common;

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
        // Modulo Layouts
        ["layouts"] = new("M2,3 H14 V13 H2 Z", "M4,5 H9 V8.5 H4 Z M10.5,5 H12 M10.5,7 H12 M4,10.5 H12"),
        ["renomear"] = new("M2,11.5 H14 M3,9 L10,2 L12.5,4.5 L5.5,11.5 H3 Z", "M8.5,3.5 L11,6"),
        ["esquerda"] = new("M13,8 H4", "M7.5,4.5 L4,8 L7.5,11.5"),
        ["direita"] = new("M3,8 H12", "M8.5,4.5 L12,8 L8.5,11.5"),
        ["ordenar"] = new("M3,4 H13 M3,8 H10 M3,12 H7", "M12,7 V13 M10,11 L12,13 L14,11"),
        ["copiar"] = new("M5.5,5.5 H13.5 V13.5 H5.5 Z", "M2.5,10.5 V2.5 H10.5"),
        ["excluir"] = new("M3,4.5 H13 M6,4.5 V3 H10 V4.5 M4,4.5 L5,13.5 H11 L12,4.5", "M7,7 V11 M9,7 V11"),
        ["travar"] = new("M3.5,7.5 H12.5 V13.5 H3.5 Z", "M5.5,7.5 V5 A2.5,2.5 0 0 1 10.5,5 V7.5"),
        ["destravar"] = new("M3.5,7.5 H12.5 V13.5 H3.5 Z", "M5.5,7.5 V5 A2.5,2.5 0 0 1 10.5,5"),
        ["ligar"] = new("M2,8 C5,3 11,3 14,8 C11,13 5,13 2,8 Z", "M8,8 m-2,0 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0"),
        ["desligar"] = new("M2,8 C5,3 11,3 14,8 C11,13 5,13 2,8 Z", "M3,13 L13,3"),
        ["escala"] = new("M2,13 L13,2 M2,13 V9 M2,13 H6", "M5,10 L6.5,11.5 M7.5,7.5 L9,9 M10,5 L11.5,6.5"),
        ["pdf"] = new("M3.5,1.5 H10 L13,4.5 V14.5 H3.5 Z M10,1.5 V4.5 H13", "M5.5,11.5 V7.5 H7 A1.3,1.3 0 0 1 7,10 H5.5 M8.5,11.5 V7.5 H10 A2,2 0 0 1 10,11.5 Z"),
        ["abrir"] = new("M2,4 H6 L7.5,5.5 H14 V13 H2 Z", "M5,8.5 H11 M8,8.5 V11.5"),
        ["atualizar"] = new("M12.5,8 A4.5,4.5 0 1 1 11,4.6", "M11,2 V5 H8"),
        // Modulo MFREBAIXO (painel de rebaixos)
        ["rebaixo"] = new("M1,5 H5 L6.5,9.5 H9.5 L11,5 H15", "M4,13 H12 M6,11.5 L4,13 L6,14.5"),
        ["zoom"] = new("M2.5,7 A4.5,4.5 0 1 0 11.5,7 A4.5,4.5 0 1 0 2.5,7", "M10.3,10.3 L14,14 M5,7 H9 M7,5 V9"),
        ["corredor"] = new("M1,11 C4,8 8,6 12,2 M4,15 C8,12 12,10 15,7", "M2.5,13 C6,10 10,8 13.5,4.5"),
        ["aviso"] = new("M8,2 L14.5,13.5 H1.5 Z", "M8,6 V9.5 M8,11.4 V11.6", 1.6),
        ["ajuda"] = new("M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8", "M6,6.4 A2,2 0 1 1 8.6,8.2 C8,8.6 8,9 8,10 M8,11.9 V12.1"),
        ["config"] = new("M4.5,8 A3.5,3.5 0 1 0 11.5,8 A3.5,3.5 0 1 0 4.5,8", "M8,1.5 V3.5 M8,12.5 V14.5 M1.5,8 H3.5 M12.5,8 H14.5 M3.4,3.4 L4.8,4.8 M11.2,11.2 L12.6,12.6 M3.4,12.6 L4.8,11.2 M11.2,4.8 L12.6,3.4"),
        ["reconstruir"] = new("M12.5,8 A4.5,4.5 0 1 1 11,4.6", "M6,8 H10 M8,6 V10"),
        ["secoes"] = new("M2,12.5 H14", "M3,12.5 L5,8.5 H11 L13,12.5 M5,8.5 V5 M11,8.5 V5"),
        ["planta"] = new("M2,3 H14 V13 H2 Z", "M2,9.5 C6,8.5 10,6.5 14,5.5"),
        // Modulo Secao (visualizador de secao)
        ["corte"] = new("M2,13 L6,7 L10,10 L14,4", "M2,3 H14 M8,1.5 V4.5"),
        ["inverter"] = new("M3,6 H13 M3,10 H13", "M10,3.5 L13,6 L10,8.5 M6,7.5 L3,10 L6,12.5"),
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
