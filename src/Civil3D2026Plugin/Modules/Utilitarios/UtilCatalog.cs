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
            "Abre o explorador de layouts (C3DLAYOUT) na categoria Layer state: lista dos layer states e miniatura de cada layout com as viewports clicaveis.",
            new[] { C3DCommands.Utilitarios.LsLayouts },
            new[] { "Escolha o layer state na lista.", "Marque as viewports na miniatura; \"repetir nos demais\" marca a mesma posicao nos outros layouts.", "Aplicar." },
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
            "Kerb Line.lsp (Bijoy Manoharan)"),

        new UtilTool("Copiar Object Layers de outro desenho", "camadas",
            "Copia so a aba Object Layers das Drawing Settings de outro DWG ou DWT: layer, modifier, value e locked de cada tipo de objeto e a opcao de controle independente de layer. Layers que faltam sao criadas com as propriedades da origem; as existentes nao mudam. Unidades, zona e demais abas ficam como estao.",
            new[] { C3DCommands.Utilitarios.ObjLayers },
            new[] { "Escolha o DWG/DWT de origem (pode estar aberto).", "Objetos ja desenhados nao mudam de layer; vale para os proximos." },
            "nativo"),

        new UtilTool("Copiar ordem de desenho de outro desenho", "ordenar",
            "Escolha o DWG ou DWT que tem a ordem de desenho (draw order) desejada e ela e aplicada no desenho atual. Por layer: cada layer vai para a posicao que tem na origem, entre quaisquer desenhos com os mesmos nomes de layer. Por objeto: cada objeto vai para a posicao exata do mesmo objeto na origem, so quando um desenho e copia do outro (Save As). Objetos sem correspondencia ficam onde estao.",
            new[] { C3DCommands.Utilitarios.OrdemDesenho },
            new[] { "Escolha o DWG/DWT de origem (pode estar aberto).", "Modo [Layers/Objetos] e espacos [Model/Layouts/Todos] (Enter repete).", "Um UNDO desfaz tudo." },
            "nativo")
    };
}
