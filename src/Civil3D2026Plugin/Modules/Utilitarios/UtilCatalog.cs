using System.Collections.Generic;

namespace Civil3D2026Plugin.Modules.Utilitarios;

/// <summary>Uma ferramenta nativa (C#) para a lista do C3DLISP.</summary>
public sealed record UtilTool(
    string Titulo,
    string Icone,
    string Descricao,
    string[] Comandos,
    string[] Uso,
    string Origem);

/// <summary>
/// Ferramentas que eram LISP e foram convertidas para C#.
/// Os comandos mantem o nome antigo.
/// </summary>
public static class UtilCatalog
{
    public static IReadOnlyList<UtilTool> Tools { get; } = new[]
    {
        new UtilTool("Cores ACI para RGB", "cor",
            "Converte a cor indice (ACI) para a cor RGB equivalente: dos objetos selecionados, das layers desses objetos ou dos dois. ByLayer e ByBlock ficam como estao.",
            new[] { C3DCommands.Utilitarios.AllToRgb },
            new[] { "Escolha [Objetos/Layers/Ambos] (Enter repete).", "Selecione os objetos; em Layers, as layers deles sao convertidas." },
            "ALL TO RGB.LSP (Lee Mac)"),

        new UtilTool("Comprimento total", "medida",
            "Soma o comprimento de linhas, arcos, circulos, polilinhas 2D/3D, splines, elipses e feature lines selecionados.",
            new[] { C3DCommands.Utilitarios.Tlen },
            new[] { "Selecione os objetos; o total aparece numa janela e na linha de comando." },
            "TLEN.LSP"),

        new UtilTool("Numerar textos em sequencia", "numero",
            "Acrescenta um numero sequencial ao final de textos, MTEXT e definicoes de atributo, na ordem de leitura: de cima para baixo e, na mesma linha, da esquerda para a direita.",
            new[] { C3DCommands.Utilitarios.JobeCount },
            new[] { "Numero inicial e incremento (Enter repete os ultimos).", "Selecione os textos." },
            "jobecount.lsp"),

        new UtilTool("Hachura ignorando gaps", "hachura",
            "Clique dentro de uma area com pequenas aberturas no contorno: o contorno e gerado com tolerancia de gap (HPGAPTOL), vira uma hachura SOLID com as ilhas e o contorno auxiliar e apagado.",
            new[] { C3DCommands.Utilitarios.SmartHatch },
            new[] { "Clique dentro da area.", "[Tolerancia] muda o gap (50 por padrao)." },
            "smart_hatch.lsp"),

        new UtilTool("Layer state em varios layouts", "camadas",
            "Janela com os layer states do desenho e a miniatura de cada layout: clique nas viewports, repita a selecao nos outros layouts pela posicao e aplique o layer state como override de viewport (VP Freeze, cor de VP etc.).",
            new[] { C3DCommands.Utilitarios.LsLayouts },
            new[] { "Escolha o layer state na lista.", "Clique nas viewports das miniaturas; \"repetir nos demais\" marca as da mesma posicao nos outros layouts.", "Aplicar; da para aplicar outro layer state em outras viewports sem fechar." },
            "LSLAYOUTS.lsp"),

        new UtilTool("Varios offsets em polilinhas fechadas", "offset",
            "Faz varios offsets de uma vez em polilinhas fechadas, para dentro ou para fora; o lado e descoberto pela area, sem depender do sentido da polilinha.",
            new[] { C3DCommands.Utilitarios.Moff, C3DCommands.Utilitarios.MoffSet },
            new[] { "Distancia, quantidade e direcao (Dentro/Fora).", "Selecione as polilinhas fechadas." },
            "MOFF_v1.0.lsp"),

        new UtilTool("Separar curvas por raio", "curva",
            "Quebra polilinhas em trechos retos, curvos com raio acima do limite e curvos com raio ate o limite (estes em magenta) e mostra quantidade e comprimento de cada grupo. Util para quantificar meio-fio curvo.",
            new[] { C3DCommands.Utilitarios.SeparaCurvas },
            new[] { "Raio limite.", "Selecione as polilinhas (a original e substituida pelos trechos)." },
            "mf curvo.lsp"),

        new UtilTool("Marcacao de meio-fio", "via",
            "A partir de uma polilinha, desenha a face do meio-fio na largura informada e uma linha central tracejada com espessura, tudo na layer Kerb Line. PP une linhas e polilinhas com PEDIT Join (tolerancia 0,01).",
            new[] { C3DCommands.Utilitarios.Kb, C3DCommands.Utilitarios.Pp },
            new[] { "Largura do meio-fio.", "Clique a polilinha e o lado do offset; Enter sai." },
            "Kerb Line.lsp (Bijoy Manoharan)")
    };
}
