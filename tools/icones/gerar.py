"""Gera src/Civil3D2026Plugin/UI/Common/IconArt.cs a partir de icones.py e ribbon.py."""
import os
from icones import ICONES
from ribbon import COMANDOS, SELOS, CATEGORIAS, AMBAR

AQUI = os.path.dirname(os.path.abspath(__file__))
DESTINO = os.path.join(AQUI, '..', '..', 'src', 'Civil3D2026Plugin', 'UI', 'Common', 'IconArt.cs')

def cs(s):
    return '"' + s.replace('\\', '\\\\').replace('"', '\\"') + '"'

def rgb(h):
    h = h.lstrip('#')
    return f'WM.Color.FromRgb({int(h[0:2], 16)}, {int(h[2:4], 16)}, {int(h[4:6], 16)})'

MODELO = 'using System;\nusing System.Collections.Generic;\n\nusing WM = System.Windows.Media;\n\nnamespace Civil3D2026Plugin.UI.Common;\n\n/// <summary>\n/// Desenho dos icones do plugin (grade 16x16), um so para os paineis e a Ribbon. Estilo\n/// "prancheta": traco no tom do texto, detalhes na cor de destaque, a massa principal do simbolo\n/// preenchida e uma marca ambar (o "piquete" da casa) onde faz sentido. Camadas de cada icone:\n/// L = traco texto, A = traco destaque, F = preenchimento destaque, S = preenchimento ambar,\n/// W = preenchimento texto. Gerado por tools/icones/gerar.py (fonte em tools/icones/icones.py e ribbon.py).\n/// </summary>\ninternal static class IconArt\n{\n    public static readonly WM.Color Ambar = @@AMBAR@@;\n\n    private static readonly Dictionary<string, (char Camada, string Dados)[]> Icones = new(StringComparer.OrdinalIgnoreCase)\n    {\n@@ICONES@@\n    };\n\n    /// <summary>Selos da Ribbon (variantes do comando: ajuda, configurar, atualizar...), no canto inferior direito.</summary>\n    internal static readonly Dictionary<string, string> Selos = new(StringComparer.OrdinalIgnoreCase)\n    {\n@@SELOS@@\n    };\n\n    /// <summary>Comando da Ribbon -> icone e selo.</summary>\n    internal static readonly Dictionary<string, (string Icone, string? Selo)> Comandos = new(StringComparer.OrdinalIgnoreCase)\n    {\n@@COMANDOS@@\n    };\n\n    /// <summary>Placa da Ribbon por categoria: cor de cima, de baixo e tinta dos detalhes.</summary>\n    internal static readonly Dictionary<string, (WM.Color Cima, WM.Color Baixo, WM.Color Tinta)> Categorias = new(StringComparer.OrdinalIgnoreCase)\n    {\n@@CATEGORIAS@@\n    };\n\n    public static bool Has(string? key) => key is not null && Icones.ContainsKey(key);\n\n    private static readonly Dictionary<string, WM.Geometry> Geometrias = new();\n\n    private static WM.Geometry Geo(string dados)\n    {\n        if (!Geometrias.TryGetValue(dados, out WM.Geometry? g))\n        {\n            g = WM.Geometry.Parse(dados);\n            g.Freeze();\n            Geometrias[dados] = g;\n        }\n        return g;\n    }\n\n    /// <summary>Desenho do icone em 16x16 com as cores dadas (chave desconhecida = "generico").</summary>\n    public static WM.DrawingGroup Desenho(string? key, WM.Brush traco, WM.Brush destaque, WM.Brush ambar, double espessura = 1.3)\n    {\n        if (key is null || !Icones.TryGetValue(key, out (char Camada, string Dados)[]? partes))\n            partes = Icones["generico"];\n        var grupo = new WM.DrawingGroup();\n        var pTraco = new WM.Pen(traco, espessura) { StartLineCap = WM.PenLineCap.Round, EndLineCap = WM.PenLineCap.Round, LineJoin = WM.PenLineJoin.Round };\n        var pDestaque = new WM.Pen(destaque, espessura) { StartLineCap = WM.PenLineCap.Round, EndLineCap = WM.PenLineCap.Round, LineJoin = WM.PenLineJoin.Round };\n        var pMassa = new WM.Pen(destaque, 0.35) { LineJoin = WM.PenLineJoin.Round };\n        foreach ((char camada, string dados) in partes)\n        {\n            WM.Geometry g = Geo(dados);\n            switch (camada)\n            {\n                case \'L\': grupo.Children.Add(new WM.GeometryDrawing(null, pTraco, g)); break;\n                case \'A\': grupo.Children.Add(new WM.GeometryDrawing(null, pDestaque, g)); break;\n                case \'F\': grupo.Children.Add(new WM.GeometryDrawing(destaque, pMassa, g)); break;\n                case \'S\': grupo.Children.Add(new WM.GeometryDrawing(ambar, null, g)); break;\n                case \'W\': grupo.Children.Add(new WM.GeometryDrawing(traco, null, g)); break;\n            }\n        }\n        return grupo;\n    }\n}\n'

icones = '\n'.join(f'        [{cs(k)}] = new (char, string)[] {{ ' + ', '.join(f"('{c}', {cs(d)})" for c, d in p) + ' },' for k, p in ICONES.items())
selos = '\n'.join(f'        [{cs(k)}] = {cs(v)},' for k, v in SELOS.items())
comandos = '\n'.join(f'        [{cs(k)}] = ({cs(v[0])}, {"null" if v[1] is None else cs(v[1])}),' for k, v in COMANDOS.items())
categorias = '\n'.join(f'        [{cs(k)}] = ({rgb(a)}, {rgb(b)}, {rgb(c)}),' for k, (a, b, c) in CATEGORIAS.items())
saida = (MODELO.replace('@@ICONES@@', icones).replace('@@SELOS@@', selos)
         .replace('@@COMANDOS@@', comandos).replace('@@CATEGORIAS@@', categorias).replace('@@AMBAR@@', rgb(AMBAR)))
with open(DESTINO, 'w', encoding='utf-8', newline='\n') as f:
    f.write(saida)
print(f'{len(ICONES)} icones, {len(COMANDOS)} comandos -> {os.path.normpath(DESTINO)}')
