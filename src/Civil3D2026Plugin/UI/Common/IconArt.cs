using System;
using System.Collections.Generic;

using WM = System.Windows.Media;

namespace Civil3D2026Plugin.UI.Common;

/// <summary>
/// Desenho dos icones do plugin (grade 16x16), um so para os paineis e a Ribbon. Estilo
/// "prancheta": traco no tom do texto, detalhes na cor de destaque, a massa principal do simbolo
/// preenchida e uma marca ambar (o "piquete" da casa) onde faz sentido. Camadas de cada icone:
/// L = traco texto, A = traco destaque, F = preenchimento destaque, S = preenchimento ambar,
/// W = preenchimento texto. Gerado por tools/icones/gerar.py (fonte em tools/icones/icones.py e ribbon.py).
/// </summary>
internal static class IconArt
{
    public static readonly WM.Color Ambar = WM.Color.FromRgb(255, 194, 74);

    private static readonly Dictionary<string, (char Camada, string Dados)[]> Icones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ajuda"] = new (char, string)[] { ('L', "M8,1.8 A6.2,6.2 0 1 0 8.01,1.8 Z"), ('A', "M5.9,6.2 A2.2,2.2 0 1 1 9.3,8 C8.3,8.6 8,9.1 8,10.2"), ('S', "M7.05,11.9 h1.9 v1.9 h-1.9 Z") },
        ["config"] = new (char, string)[] { ('L', "M8,2 V3.6 M8,12.4 V14 M2,8 H3.6 M12.4,8 H14 M3.8,3.8 L4.9,4.9 M11.1,11.1 L12.2,12.2 M3.8,12.2 L4.9,11.1 M11.1,4.9 L12.2,3.8"), ('A', "M8,3.6 A4.4,4.4 0 1 0 8.01,3.6 Z"), ('F', "M8,6.2 A1.8,1.8 0 1 0 8.01,6.2 Z") },
        ["atualizar"] = new (char, string)[] { ('L', "M13,8 A5,5 0 0 1 4.2,11.3 M3,8 A5,5 0 0 1 11.8,4.7"), ('F', "M13.6,2.6 L13.6,6.6 L9.6,6.6 Z M2.4,13.4 L2.4,9.4 L6.4,9.4 Z") },
        ["desfazer"] = new (char, string)[] { ('L', "M3.4,6.2 H10 A3.4,3.4 0 0 1 10,13 H5.5"), ('F', "M1.8,6.2 L5.8,2.6 L5.8,9.8 Z") },
        ["reconstruir"] = new (char, string)[] { ('L', "M13,8 A5,5 0 1 1 11.8,4.7"), ('F', "M13.6,2.6 L13.6,6.6 L9.6,6.6 Z"), ('S', "M6.4,6.4 h3.2 v3.2 h-3.2 Z") },
        ["editar"] = new (char, string)[] { ('L', "M2.5,13.5 L3.2,10.4 L10.6,3 A1.4,1.4 0 0 1 12.6,3 L13,3.4 A1.4,1.4 0 0 1 13,5.4 L5.6,12.8 Z"), ('A', "M9.4,4.2 L11.8,6.6"), ('S', "M2.5,13.5 L3.2,10.4 L5.6,12.8 Z") },
        ["previa"] = new (char, string)[] { ('L', "M1.5,8 C4,3.8 12,3.8 14.5,8 C12,12.2 4,12.2 1.5,8 Z"), ('F', "M8,5.6 A2.4,2.4 0 1 0 8.01,5.6 Z"), ('S', "M8.6,6.4 A0.8,0.8 0 1 0 8.61,6.4 Z") },
        ["teste"] = new (char, string)[] { ('L', "M6,2 H10 M6.8,2 V6.6 L2.8,13 A0.9,0.9 0 0 0 3.6,14 H12.4 A0.9,0.9 0 0 0 13.2,13 L9.2,6.6 V2"), ('F', "M4.6,10.2 H11.4 L13.2,13 A0.9,0.9 0 0 1 12.4,14 H3.6 A0.9,0.9 0 0 1 2.8,13 Z"), ('S', "M7.2,11.2 h1.6 v1.6 h-1.6 Z") },
        ["diagnostico"] = new (char, string)[] { ('L', "M6.8,1.8 A4.9,4.9 0 1 0 6.81,1.8 Z M10.4,10.4 L14.2,14.2"), ('A', "M2.6,6.8 H4.4 L5.6,4.6 L7.4,9 L8.6,6.8 H11") },
        ["zoom"] = new (char, string)[] { ('L', "M6.8,1.8 A4.9,4.9 0 1 0 6.81,1.8 Z M10.4,10.4 L14.2,14.2"), ('F', "M6.05,4.3 h1.5 v1.75 h1.75 v1.5 h-1.75 v1.75 h-1.5 v-1.75 h-1.75 v-1.5 h1.75 Z") },
        ["aviso"] = new (char, string)[] { ('L', "M8,1.8 L14.6,13.6 H1.4 Z"), ('S', "M7.2,5.6 h1.6 l-0.3,4.6 h-1 Z M7.2,11.2 h1.6 v1.5 h-1.6 Z") },
        ["log"] = new (char, string)[] { ('L', "M2.5,2.5 H13.5 V13.5 H2.5 Z"), ('F', "M2.5,2.5 H13.5 V4.8 H2.5 Z"), ('A', "M4.6,7.2 L6.6,9 L4.6,10.8 M8,11 H11.4") },
        ["generico"] = new (char, string)[] { ('L', "M3,3 H13 V13 H3 Z"), ('S', "M6.5,6.5 h3 v3 h-3 Z") },
        ["modulos"] = new (char, string)[] { ('L', "M2.5,2.5 H7 V7 H2.5 Z M9,2.5 H13.5 V7 H9 Z M2.5,9 H7 V13.5 H2.5 Z"), ('F', "M9,9 H13.5 V13.5 H9 Z") },
        ["ribbon"] = new (char, string)[] { ('L', "M1.5,3 H14.5 V12.5 H1.5 Z M1.5,6 H14.5"), ('F', "M3.2,7.6 h3 v3.2 h-3 Z M7,7.6 h2.2 v1.4 h-2.2 Z M7,9.6 h2.2 v1.2 h-2.2 Z"), ('A', "M3,4.5 H6 M7.5,4.5 H9.5") },
        ["abrir"] = new (char, string)[] { ('L', "M1.8,4 H6 L7.4,5.4 H14.2 V13 H1.8 Z"), ('F', "M8,7.2 L10.8,10 H9 V12 H7 V10 H5.2 Z") },
        ["excel"] = new (char, string)[] { ('L', "M2.5,2.5 H13.5 V13.5 H2.5 Z M2.5,6.2 H13.5 M2.5,9.8 H13.5 M6.2,2.5 V13.5"), ('F', "M6.2,6.2 H13.5 V9.8 H6.2 Z") },
        ["tabela"] = new (char, string)[] { ('L', "M2,3 H14 V13 H2 Z M2,9.5 H14 M7,3 V13"), ('F', "M2,3 H14 V6.2 H2 Z") },
        ["texto"] = new (char, string)[] { ('L', "M3,3.5 H13 M8,3.5 V13"), ('A', "M5.5,13 H10.5"), ('S', "M11.6,9.6 h2 v3.4 h-2 Z") },
        ["layouts"] = new (char, string)[] { ('L', "M1.5,3 H14.5 V13 H1.5 Z"), ('A', "M10.2,3 V13 M10.2,9.6 H14.5"), ('F', "M3.3,4.8 H8.6 V11.2 H3.3 Z"), ('S', "M11.4,11 h2 v1 h-2 Z") },
        ["prancha"] = new (char, string)[] { ('L', "M3.5,1.5 H9.8 L12.8,4.5 V14.5 H3.5 Z M9.8,1.5 V4.5 H12.8"), ('A', "M5.4,7.4 H10.9 M5.4,9.6 H10.9"), ('F', "M8.4,11.6 H10.9 V13 H8.4 Z") },
        ["renomear"] = new (char, string)[] { ('L', "M1.5,4.5 H11 L14.5,8 L11,11.5 H1.5 Z"), ('A', "M4,8 H8.5"), ('S', "M11,7 h1.2 v2 h-1.2 Z") },
        ["esquerda"] = new (char, string)[] { ('L', "M14,8 H6"), ('F', "M2,8 L7.4,3.6 V12.4 Z") },
        ["direita"] = new (char, string)[] { ('L', "M2,8 H10"), ('F', "M14,8 L8.6,3.6 V12.4 Z") },
        ["ordenar"] = new (char, string)[] { ('L', "M2.5,3.5 H12 M2.5,7 H9.5 M2.5,10.5 H7 M2.5,14 H4.5"), ('F', "M13.2,7.6 V11.4 H14.8 L12.4,14.6 L10,11.4 H11.6 V7.6 Z") },
        ["copiar"] = new (char, string)[] { ('L', "M2.5,10.5 V2.5 H10.5"), ('A', "M5.5,5.5 H13.5 V13.5 H5.5 Z"), ('F', "M7.4,7.4 H11.6 V8.6 H7.4 Z M7.4,10 H10.4 V11.2 H7.4 Z") },
        ["excluir"] = new (char, string)[] { ('L', "M4,4.8 L5,14 H11 L12,4.8 M6.6,7 V11.8 M9.4,7 V11.8"), ('A', "M2.5,4.5 H13.5"), ('F', "M6.2,2 H9.8 V3.8 H6.2 Z") },
        ["travar"] = new (char, string)[] { ('L', "M5.2,7.5 V5.2 A2.8,2.8 0 0 1 10.8,5.2 V7.5"), ('F', "M3.2,7.3 H12.8 V14 H3.2 Z"), ('S', "M7.2,9.4 h1.6 v2.6 h-1.6 Z") },
        ["destravar"] = new (char, string)[] { ('L', "M5.2,7.5 V5.2 A2.8,2.8 0 0 1 10.6,4.2"), ('A', "M3.2,7.3 H12.8 V14 H3.2 Z"), ('S', "M7.2,9.4 h1.6 v2.6 h-1.6 Z") },
        ["ligar"] = new (char, string)[] { ('L', "M1.5,8 C4,3.8 12,3.8 14.5,8 C12,12.2 4,12.2 1.5,8 Z"), ('F', "M8,5.5 A2.5,2.5 0 1 0 8.01,5.5 Z") },
        ["desligar"] = new (char, string)[] { ('L', "M1.5,8 C4,3.8 12,3.8 14.5,8 C12,12.2 4,12.2 1.5,8 Z"), ('A', "M2.5,13.5 L13.5,2.5") },
        ["escala"] = new (char, string)[] { ('L', "M1.8,13.6 L14.2,2.4 V13.6 Z"), ('A', "M11.6,13.6 V11 M9,13.6 V12 M6.4,13.6 V11 M14.2,10.4 H12 M14.2,7.6 H12.8 M14.2,5 H12"), ('S', "M1.8,13.6 L4.6,11.1 L4.6,13.6 Z") },
        ["pdf"] = new (char, string)[] { ('L', "M3.5,1.5 H9.8 L12.8,4.5 V14.5 H3.5 Z M9.8,1.5 V4.5 H12.8"), ('F', "M2,8 H11.6 V12.4 H2 Z") },
        ["imprimir"] = new (char, string)[] { ('L', "M4.5,6 V2 H11.5 V6 M2.5,6 H13.5 V11 H2.5 Z"), ('F', "M4.5,9 H11.5 V14 H4.5 Z"), ('S', "M11,7.2 h1.2 v1.2 h-1.2 Z") },
        ["camadas"] = new (char, string)[] { ('L', "M2,11 L8,14 L14,11"), ('A', "M2,8 L8,11 L14,8"), ('F', "M8,2 L14,5 L8,8 L2,5 Z") },
        ["mapa"] = new (char, string)[] { ('L', "M1.8,3.2 L5.8,2 L10.2,3.4 L14.2,2.2 V12.8 L10.2,14 L5.8,12.6 L1.8,13.8 Z M5.8,2 V12.6 M10.2,3.4 V14"), ('F', "M5.8,2 L10.2,3.4 V14 L5.8,12.6 Z") },
        ["bloco"] = new (char, string)[] { ('L', "M4.5,2.5 H13.5 V11.5 H4.5 Z"), ('F', "M7,5 H11 V9 H7 Z"), ('S', "M2.6,13.4 m-1.4,0 a1.4,1.4 0 1 0 2.8,0 a1.4,1.4 0 1 0 -2.8,0 Z"), ('A', "M2.6,11.5 V9 M4.5,13.4 H7") },
        ["numero"] = new (char, string)[] { ('L', "M6.4,2 L5,14 M11,2 L9.6,14 M2.8,6 H14 M2,10.4 H13.2"), ('F', "M6.4,6 H10.5 L10.1,10.4 H5.9 Z") },
        ["planta"] = new (char, string)[] { ('L', "M2,3 H14 V13 H2 Z"), ('A', "M2,10.6 C6,9.6 10,6.6 14,5.6"), ('S', "M9.5,7.3 m-1,0 a1,1 0 1 0 2,0 a1,1 0 1 0 -2,0 Z") },
        ["viewframe"] = new (char, string)[] { ('L', "M1.5,9.5 L4,3.5 L8.5,5.4 L6,11.4 Z M8.5,5.4 L12,2 L14.8,5.6 L11.2,9"), ('A', "M1,13.6 C5,11 9,10.5 15,7") },
        ["norte"] = new (char, string)[] { ('L', "M8,1.2 L10.2,8 L8,14.8 L5.8,8 Z M1.2,8 L8,5.8 L14.8,8 L8,10.2 Z"), ('F', "M8,1.2 L10.2,8 L5.8,8 Z"), ('S', "M8,6.9 a1.1,1.1 0 1 0 0.01,0 Z") },
        ["escalagrafica"] = new (char, string)[] { ('L', "M1.5,7 H14.5 V11 H1.5 Z M1.5,9 H14.5"), ('F', "M1.5,7 H4.75 V9 H1.5 Z M4.75,9 H8 V11 H4.75 Z M8,7 H11.25 V9 H8 Z M11.25,9 H14.5 V11 H11.25 Z"), ('A', "M1.5,7 V4.5 M8,7 V5 M14.5,7 V4.5") },
        ["pranchas"] = new (char, string)[] { ('L', "M1.5,1.5 H9.5 V7 H1.5 Z M6.5,9 H14.5 V14.5 H6.5 Z"), ('A', "M3,5.4 C5,4.6 7,3.6 8.6,3.2 M8,12.8 C10,12 12,11 13.6,10.6"), ('S', "M3.5,8.4 L6.4,11.6 L3.5,11.6 Z") },
        ["corredor"] = new (char, string)[] { ('L', "M1,11 C4,8 8,6 12,2 M4,15 C8,12 12,10 15,7"), ('A', "M2.5,13 C6,10 10,8 13.5,4.5") },
        ["rebaixo"] = new (char, string)[] { ('L', "M1,5 H5 L6.5,9.5 H9.5 L11,5 H15"), ('F', "M5,5 L6.5,9.5 H9.5 L11,5 L11,6.4 L10.2,10.8 H5.8 L5,6.4 Z"), ('S', "M5.8,12.4 h4.4 v1.6 h-4.4 Z") },
        ["secoes"] = new (char, string)[] { ('L', "M1.5,12.5 L5,8.6 H11 L14.5,12.5"), ('F', "M4.6,8.2 H11.4 V6.6 H4.6 Z"), ('A', "M8,2.5 V5 M8,13.6 V14.5") },
        ["corte"] = new (char, string)[] { ('L', "M2,13 L6,8 L10,10.4 L14,5"), ('A', "M2.4,3 H13.6"), ('F', "M1,3 L4,1.2 V4.8 Z M15,3 L12,1.2 V4.8 Z") },
        ["inverter"] = new (char, string)[] { ('L', "M3,5.5 H11 M13,10.5 H5"), ('F', "M14.6,5.5 L10.6,2.6 V8.4 Z M1.4,10.5 L5.4,7.6 V13.4 Z") },
        ["solido3d"] = new (char, string)[] { ('L', "M3,5 L8,2.5 L13,5 V11 L8,13.5 L3,11 Z M8,7.5 V13.5"), ('F', "M3,5 L8,2.5 L13,5 L8,7.5 Z") },
        ["estais"] = new (char, string)[] { ('L', "M1.5,2.5 C5,3.5 11,3.5 14.5,2.5 M1.5,13.5 C5,12 11,12 14.5,13.5"), ('A', "M3,3 L4.5,12.8 M6.6,3.4 L7.4,12.4 M10,3.4 L10.4,12.4 M13.4,2.9 L13.2,13") },
        ["passagem"] = new (char, string)[] { ('L', "M1,12.5 H15"), ('F', "M3,12.5 L6,8.5 H10 L13,12.5 Z"), ('S', "M6.8,9.8 h0.9 v2.2 h-0.9 Z M8.3,9.8 h0.9 v2.2 h-0.9 Z") },
        ["dividir"] = new (char, string)[] { ('L', "M1,10 C3,9 5,8.6 6.8,8.2 M9.2,7.8 C11,7.4 13,7 15,6"), ('A', "M1,13.5 C3,12.5 5,12 6.8,11.6 M9.2,11.2 C11,10.8 13,10.4 15,9.5"), ('S', "M8,2.4 L9.6,5.4 L8,8.4 L6.4,5.4 Z") },
        ["estacas"] = new (char, string)[] { ('L', "M1,12 C5,10 11,10 15,6"), ('A', "M3,11.4 V8.4 M8,10.3 V7.3 M13,7.4 V4.4"), ('S', "M2.2,7 h1.6 v1.6 h-1.6 Z") },
        ["featureline"] = new (char, string)[] { ('L', "M1.8,12.8 L5.6,6 L10,9.4 L14.2,3.2"), ('F', "M4.4,4.8 h2.4 v2.4 h-2.4 Z M8.8,8.2 h2.4 v2.4 h-2.4 Z"), ('S', "M13,2 h2.4 v2.4 h-2.4 Z") },
        ["superficie"] = new (char, string)[] { ('L', "M1.5,12.5 L5.5,3.5 L10.5,6 L14.5,12.5 Z M5.5,3.5 L8,12.5 L10.5,6"), ('A', "M3,9.2 C5,8.2 8,8.6 13,9.6") },
        ["normal"] = new (char, string)[] { ('L', "M1.5,11 L6.5,8 L14.5,10.5 L9.5,14 Z"), ('A', "M8,11 V2.8"), ('F', "M8,1.2 L10.2,4.4 H5.8 Z"), ('S', "M8,10.2 a0.9,0.9 0 1 0 0.01,0 Z") },
        ["drenagem"] = new (char, string)[] { ('L', "M4.4,9.6 L11.6,6.4"), ('F', "M4,6.8 a2.8,2.8 0 1 0 0.01,0 Z M12,3.6 a2.8,2.8 0 1 0 0.01,0 Z"), ('A', "M1.5,14 H14.5"), ('S', "M3.2,12 h1.6 v1.2 h-1.6 Z") },
        ["qto"] = new (char, string)[] { ('L', "M2.5,2.5 H13.5 V13.5 H2.5 Z"), ('A', "M10.6,5 H5.4 L8.2,8 L5.4,11 H10.6") },
        ["ped"] = new (char, string)[] { ('L', "M8,14.6 C5,10.8 3.4,8.6 3.4,6.4 A4.6,4.6 0 0 1 12.6,6.4 C12.6,8.6 11,10.8 8,14.6 Z"), ('F', "M5.6,4.4 H10.4 V8.6 H5.6 Z"), ('S', "M6.4,5.2 h3.2 v1.4 h-3.2 Z") },
        ["lisp"] = new (char, string)[] { ('L', "M5,2 C2.4,4.6 2.4,11.4 5,14 M11,2 C13.6,4.6 13.6,11.4 11,14"), ('A', "M6,4 C7.6,4 7.6,5.6 8,7 L9.8,12"), ('F', "M7.6,8.4 L8.4,9.6 L6.4,12.2 L5.6,11.2 Z") },
        ["ruas"] = new (char, string)[] { ('L', "M2,3 H14 V13 H2 Z M2,6 H14"), ('A', "M5,8.4 H11 M5,10.8 H9"), ('S', "M2,3 H14 V6 H2 Z") },
        ["hachura"] = new (char, string)[] { ('L', "M2.5,2.5 H13.5 V13.5 H2.5 Z"), ('A', "M2.5,8 L8,2.5 M2.5,13.5 L13.5,2.5 M8,13.5 L13.5,8") },
        ["zebra"] = new (char, string)[] { ('L', "M2.5,2.5 H13.5 V13.5 H2.5 Z"), ('F', "M2.5,5 L5,2.5 H7.4 L2.5,7.4 Z M2.5,10.4 L10.4,2.5 H12.8 L2.5,12.8 Z M7.6,13.5 L13.5,7.6 V10 L10,13.5 Z") },
        ["polilinha"] = new (char, string)[] { ('L', "M2,13 L6,5 L10,10 L14,3"), ('F', "M4.8,3.8 h2.4 v2.4 h-2.4 Z M8.8,8.8 h2.4 v2.4 h-2.4 Z") },
        ["offset"] = new (char, string)[] { ('A', "M2.5,2.5 H13.5 V13.5 H2.5 Z"), ('L', "M5.5,5.5 H10.5 V10.5 H5.5 Z"), ('S', "M12.8,7 l1.6,1 l-1.6,1 Z") },
        ["curva"] = new (char, string)[] { ('L', "M2,13.5 H6 M13.5,6 V2"), ('A', "M6,13.5 A7.5,7.5 0 0 0 13.5,6"), ('S', "M6,13.5 m-1.2,0 a1.2,1.2 0 1 0 2.4,0 a1.2,1.2 0 1 0 -2.4,0 Z") },
        ["via"] = new (char, string)[] { ('L', "M3,2 V14 M13,2 V14"), ('S', "M7.2,2 h1.6 v2.6 h-1.6 Z M7.2,6.7 h1.6 v2.6 h-1.6 Z M7.2,11.4 h1.6 v2.6 h-1.6 Z") },
        ["malha"] = new (char, string)[] { ('L', "M2.5,2.5 H13.5 V13.5 H2.5 Z M6.2,2.5 V13.5 M9.8,2.5 V13.5 M2.5,6.2 H13.5 M2.5,9.8 H13.5"), ('F', "M6.2,6.2 H9.8 V9.8 H6.2 Z") },
        ["cor"] = new (char, string)[] { ('L', "M8.5,9.8 A3.1,3.1 0 1 0 8.51,9.8 Z"), ('A', "M5,4.4 A3.1,3.1 0 1 0 5.01,4.4 Z"), ('F', "M11,4.4 A3.1,3.1 0 1 0 11.01,4.4 Z") },
        ["medida"] = new (char, string)[] { ('L', "M1.5,5.5 H14.5 V10.5 H1.5 Z"), ('A', "M4.5,5.5 V8 M7.5,5.5 V9 M10.5,5.5 V8"), ('S', "M12.4,5.5 h2.1 v5 h-2.1 Z") },
        ["juntar"] = new (char, string)[] { ('L', "M2,13 L6.6,8.9 M9.4,7.1 L14,3"), ('F', "M8,6.4 a1.6,1.6 0 1 0 0.01,0 Z") },
        ["quebrar"] = new (char, string)[] { ('L', "M2,8 H6.5 M9.5,8 H14"), ('A', "M8,2 V14") },
        ["numerartexto"] = new (char, string)[] { ('L', "M2.5,3.5 H9.5 M6,3.5 V12.5"), ('F', "M10,8 h4.5 v5.5 h-4.5 Z"), ('A', "M11.6,9.4 L12.4,9 V12.4") },
    };

    /// <summary>Selos da Ribbon (variantes do comando: ajuda, configurar, atualizar...), no canto inferior direito.</summary>
    internal static readonly Dictionary<string, string> Selos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ajuda"] = "M11,11.3 A1.25,1.25 0 1 1 12.7,12.4 C12.3,12.6 12.2,12.8 12.2,13.3 M12.2,14.3 V14.4",
        ["config"] = "M12.2,10.1 V10.9 M12.2,13.5 V14.3 M10.1,12.2 H10.9 M13.5,12.2 H14.3 M12.2,11.3 A0.9,0.9 0 1 0 12.21,11.3 Z",
        ["atualizar"] = "M13.9,12.2 A1.7,1.7 0 1 1 13.2,10.8 M13.4,10 V11 H12.4",
        ["editar"] = "M10.6,13.9 L11,12.6 L13,10.6 L13.8,11.4 L11.8,13.4 Z",
        ["previa"] = "M10,12.2 C11,10.9 13.4,10.9 14.4,12.2 C13.4,13.5 11,13.5 10,12.2 Z M12.2,11.7 V12.7",
        ["teste"] = "M10.6,12.3 L11.8,13.5 L13.9,11",
        ["mais"] = "M12.2,10.4 V14 M10.4,12.2 H14",
        ["excel"] = "M10.5,10.6 H13.9 V13.8 H10.5 Z M10.5,12.2 H13.9 M12.2,10.6 V13.8",
        ["numero"] = "M11.6,10.4 L11.2,14 M13.2,10.4 L12.8,14 M10.4,11.6 H14 M10.3,12.8 H13.9",
        ["painel"] = "M10.4,10.6 H14 V13.8 H10.4 Z M10.4,11.6 H14",
        ["oculto"] = "M10,12.2 C11,10.9 13.4,10.9 14.4,12.2 C13.4,13.5 11,13.5 10,12.2 Z M10.4,14 L14,10.4",
        ["z"] = "M10.8,10.7 H13.6 L10.8,13.7 H13.6",
        ["renomear"] = "M10.4,12.2 H13.2 M12,11 L13.2,12.2 L12,13.4",
        ["aviso"] = "M12.2,10.4 V12.6 M12.2,13.7 V13.9",
        ["cor"] = "M12.2,10.4 A1.8,1.8 0 1 0 12.21,10.4 Z",
        ["filtro"] = "M10.3,10.7 H14.1 L12.8,12.3 V14 L11.6,13.4 V12.3 Z",
        ["corte"] = "M10.4,10.4 L14,14 M11.2,13.2 A0.8,0.8 0 1 0 11.21,13.2 Z M10.4,14 L12.2,12.2",
        ["fresta"] = "M10.4,10.6 V13.8 M11.6,10.6 V13.8 M12.8,10.6 V13.8 M14,10.6 V13.8",
        ["viewport"] = "M10.4,10.6 H14 V13.8 H10.4 Z M11.4,11.6 H13 V12.8 H11.4 Z",
        ["mover"] = "M12.2,10.2 V14.2 M10.2,12.2 H14.2",
        ["camadas"] = "M12.2,10.4 L14.2,11.4 L12.2,12.4 L10.2,11.4 Z M10.2,12.8 L12.2,13.8 L14.2,12.8",
        ["lista"] = "M10.6,10.9 H13.8 M10.6,12.2 H13.8 M10.6,13.5 H12.6",
    };

    /// <summary>Comando da Ribbon -> icone e selo.</summary>
    internal static readonly Dictionary<string, (string Icone, string? Selo)> Comandos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C3DHELP"] = ("ajuda", null),
        ["C3DMODULOS"] = ("modulos", null),
        ["C3DLOG"] = ("log", null),
        ["C3DRIBBON"] = ("ribbon", null),
        ["C3DRIBBONRESET"] = ("ribbon", "atualizar"),
        ["DRENEXCEL"] = ("drenagem", "excel"),
        ["DRENEXCELPREVIEW"] = ("drenagem", "previa"),
        ["DRENEXCELHELP"] = ("drenagem", "ajuda"),
        ["DRENNUM"] = ("drenagem", "numero"),
        ["DRENNUMPREVIEW"] = ("drenagem", "previa"),
        ["DRENNUMHELP"] = ("drenagem", "ajuda"),
        ["QTOESTUDO"] = ("qto", null),
        ["QTOGEOTESTE"] = ("qto", "teste"),
        ["TESTEQTO"] = ("qto", "teste"),
        ["QTOHELP"] = ("qto", "ajuda"),
        ["CORRSPLIT"] = ("dividir", null),
        ["MFREBAIXOPAINEL"] = ("rebaixo", "painel"),
        ["MFREBAIXO"] = ("rebaixo", null),
        ["MFREBAIXOCFG"] = ("rebaixo", "config"),
        ["MFREBAIXOHELP"] = ("rebaixo", "ajuda"),
        ["C3DSOLIDARRAY"] = ("solido3d", null),
        ["C3DSOLIDCFG"] = ("solido3d", "config"),
        ["C3DSOLIDATUALIZAR"] = ("solido3d", "atualizar"),
        ["C3DSOLIDEDITAR"] = ("solido3d", "editar"),
        ["C3DSOLIDITEM"] = ("solido3d", "editar"),
        ["C3DSOLIDHELP"] = ("solido3d", "ajuda"),
        ["C3DSOLIDLIGAR"] = ("estais", null),
        ["C3DSOLIDLIGAREDITAR"] = ("estais", "editar"),
        ["C3DSOLIDLIGARATUALIZAR"] = ("estais", "atualizar"),
        ["C3DSOLIDLIGARTOOLTIP"] = ("estais", "previa"),
        ["C3DSOLIDLIGARHELP"] = ("estais", "ajuda"),
        ["C3DOCULTARPLANTA"] = ("solido3d", "oculto"),
        ["C3DPLANTAATUALIZAR"] = ("solido3d", "atualizar"),
        ["PASSAGEM"] = ("passagem", null),
        ["PASSAGEMEDITAR"] = ("passagem", "editar"),
        ["PASSAGEMGRUPO"] = ("passagem", "editar"),
        ["PASSAGEMATUALIZAR"] = ("passagem", "atualizar"),
        ["PASSAGEMCFG"] = ("passagem", "config"),
        ["PASSAGEMHELP"] = ("passagem", "ajuda"),
        ["C3DQUADRORUAS"] = ("ruas", null),
        ["FLZSET"] = ("featureline", "z"),
        ["FLSETPV"] = ("featureline", "editar"),
        ["FLRENOMEARCORTE"] = ("featureline", "renomear"),
        ["FLDESCARREGAR"] = ("featureline", "aviso"),
        ["SURFREADSCHEME"] = ("superficie", "previa"),
        ["SURFCOLORAUTO"] = ("superficie", "cor"),
        ["SFSLOPEFILTER"] = ("superficie", "filtro"),
        ["TRIMSURFLINES"] = ("superficie", "corte"),
        ["CORTAR1MM"] = ("superficie", "fresta"),
        ["PTNORMALDBG"] = ("normal", null),
        ["C3DSECAO"] = ("secoes", null),
        ["C3DSECAOCORTE"] = ("corte", null),
        ["C3DSECAODESENHAR"] = ("secoes", "mais"),
        ["C3DLAYOUT"] = ("layouts", null),
        ["C3DLAYOUTVP"] = ("layouts", "viewport"),
        ["C3DLAYOUTLS"] = ("camadas", null),
        ["C3DLAYOUTCARIMBO"] = ("prancha", "editar"),
        ["C3DLAYOUTPS"] = ("imprimir", null),
        ["C3DMAPACHAVE"] = ("mapa", null),
        ["C3DPRANCHAS"] = ("pranchas", null),
        ["C3DPRANCHA"] = ("prancha", "mais"),
        ["NP"] = ("numero", null),
        ["C3DNORTE"] = ("norte", null),
        ["C3DMNE"] = ("malha", null),
        ["ESCALAGRAF"] = ("escalagrafica", null),
        ["PEDFICHA"] = ("ped", "previa"),
        ["PEDIMPORTAR"] = ("ped", "excel"),
        ["PEDATUALIZAR"] = ("ped", "atualizar"),
        ["PEDLOCAR"] = ("ped", "mover"),
        ["PEDVISTA"] = ("ped", "camadas"),
        ["PEDCAMPOS"] = ("ped", "lista"),
        ["ALLTORGB"] = ("cor", null),
        ["TLEN"] = ("medida", null),
        ["JOBECOUNT"] = ("numerartexto", null),
        ["SMARTHATCH"] = ("hachura", null),
        ["LSLAYOUTS"] = ("camadas", null),
        ["MOFF"] = ("offset", null),
        ["MOFFSET"] = ("offset", null),
        ["SEPARA_CURVAS_RAIO"] = ("curva", null),
        ["KB"] = ("via", null),
        ["PP"] = ("juntar", null),
        ["C3DOBJLAYERS"] = ("camadas", null),
        ["C3DORDEMDESENHO"] = ("ordenar", null),
        ["C3DCOPIARLAYERS"] = ("camadas", null),
        ["C3DUNIRHACHURAS"] = ("hachura", "juntar"),
        ["C3DLISP"] = ("lisp", null),
        ["C3DLISPLIST"] = ("lisp", "lista"),
        ["C3DLISPCARREGAR"] = ("lisp", "atualizar"),
        ["TESTEDLL"] = ("teste", null),
    };

    /// <summary>Placa da Ribbon por categoria: cor de cima, de baixo e tinta dos detalhes.</summary>
    internal static readonly Dictionary<string, (WM.Color Cima, WM.Color Baixo, WM.Color Tinta)> Categorias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Geral"] = (WM.Color.FromRgb(61, 74, 92), WM.Color.FromRgb(39, 49, 64), WM.Color.FromRgb(169, 196, 230)),
        ["Drenagem"] = (WM.Color.FromRgb(35, 92, 158), WM.Color.FromRgb(23, 62, 110), WM.Color.FromRgb(143, 208, 255)),
        ["QTO"] = (WM.Color.FromRgb(34, 112, 74), WM.Color.FromRgb(21, 74, 49), WM.Color.FromRgb(140, 235, 179)),
        ["Corridor"] = (WM.Color.FromRgb(58, 71, 150), WM.Color.FromRgb(37, 47, 104), WM.Color.FromRgb(174, 187, 255)),
        ["Quadros"] = (WM.Color.FromRgb(142, 69, 50), WM.Color.FromRgb(95, 44, 31), WM.Color.FromRgb(255, 184, 148)),
        ["Viario"] = (WM.Color.FromRgb(58, 66, 77), WM.Color.FromRgb(35, 41, 48), WM.Color.FromRgb(244, 196, 64)),
        ["Feature Lines"] = (WM.Color.FromRgb(24, 112, 110), WM.Color.FromRgb(15, 75, 74), WM.Color.FromRgb(125, 237, 224)),
        ["Superficies"] = (WM.Color.FromRgb(125, 88, 38), WM.Color.FromRgb(83, 58, 24), WM.Color.FromRgb(247, 206, 134)),
        ["Geometria"] = (WM.Color.FromRgb(90, 58, 146), WM.Color.FromRgb(59, 37, 100), WM.Color.FromRgb(210, 182, 255)),
        ["Layouts"] = (WM.Color.FromRgb(30, 93, 120), WM.Color.FromRgb(19, 62, 81), WM.Color.FromRgb(147, 221, 248)),
        ["PEDs"] = (WM.Color.FromRgb(142, 36, 51), WM.Color.FromRgb(95, 22, 33), WM.Color.FromRgb(255, 154, 166)),
        ["LISPs"] = (WM.Color.FromRgb(86, 106, 35), WM.Color.FromRgb(56, 70, 23), WM.Color.FromRgb(214, 240, 127)),
    };

    public static bool Has(string? key) => key is not null && Icones.ContainsKey(key);

    private static readonly Dictionary<string, WM.Geometry> Geometrias = new();

    private static WM.Geometry Geo(string dados)
    {
        if (!Geometrias.TryGetValue(dados, out WM.Geometry? g))
        {
            g = WM.Geometry.Parse(dados);
            g.Freeze();
            Geometrias[dados] = g;
        }
        return g;
    }

    /// <summary>Desenho do icone em 16x16 com as cores dadas (chave desconhecida = "generico").</summary>
    public static WM.DrawingGroup Desenho(string? key, WM.Brush traco, WM.Brush destaque, WM.Brush ambar, double espessura = 1.3)
    {
        if (key is null || !Icones.TryGetValue(key, out (char Camada, string Dados)[]? partes))
            partes = Icones["generico"];
        var grupo = new WM.DrawingGroup();
        var pTraco = new WM.Pen(traco, espessura) { StartLineCap = WM.PenLineCap.Round, EndLineCap = WM.PenLineCap.Round, LineJoin = WM.PenLineJoin.Round };
        var pDestaque = new WM.Pen(destaque, espessura) { StartLineCap = WM.PenLineCap.Round, EndLineCap = WM.PenLineCap.Round, LineJoin = WM.PenLineJoin.Round };
        var pMassa = new WM.Pen(destaque, 0.35) { LineJoin = WM.PenLineJoin.Round };
        foreach ((char camada, string dados) in partes)
        {
            WM.Geometry g = Geo(dados);
            switch (camada)
            {
                case 'L': grupo.Children.Add(new WM.GeometryDrawing(null, pTraco, g)); break;
                case 'A': grupo.Children.Add(new WM.GeometryDrawing(null, pDestaque, g)); break;
                case 'F': grupo.Children.Add(new WM.GeometryDrawing(destaque, pMassa, g)); break;
                case 'S': grupo.Children.Add(new WM.GeometryDrawing(ambar, null, g)); break;
                case 'W': grupo.Children.Add(new WM.GeometryDrawing(traco, null, g)); break;
            }
        }
        return grupo;
    }
}
