using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.EditorInput;

namespace Civil3D2026Plugin.Infrastructure;

/// <summary>
/// Catalogo central dos modulos e comandos.
///
/// Esta e a fonte de dados compartilhada por:
/// - arvore exibida no NETLOAD/C3DHELP;
/// - Ribbon gerada automaticamente;
/// - futuras interfaces graficas.
///
/// Ao adicionar um comando novo, cadastre-o aqui uma unica vez.
/// </summary>
public static class CommandCatalog
{
    public sealed record CommandDefinition(
        string Name,
        string Title,
        string Description,
        bool ShowInRibbon = true);

    public sealed record ModuleDefinition(
        string Category,
        string Name,
        string? Version,
        IReadOnlyList<CommandDefinition> Commands,
        bool ShowInRibbon = true);

    public static IReadOnlyList<ModuleDefinition> Modules { get; } =
        new List<ModuleDefinition>
        {
            new(
                "Geral",
                "Plugin",
                C3DVersions.Plugin,
                new[]
                {
                    new CommandDefinition(
                        C3DCommands.Core.Help,
                        "Ajuda / comandos",
                        "Mostra a arvore atual de modulos, comandos, versoes e DLL carregada."),

                    new CommandDefinition(
                        C3DCommands.Core.Modulos,
                        "Modulos",
                        "Marque quais modulos aparecem na Ribbon e na lista de comandos (e iniciam seus servicos na proxima carga)."),

                    new CommandDefinition(
                        C3DCommands.Core.Log,
                        "Log do plugin",
                        "Janela com o log de todos os plugins (comandos, avisos, erros e tempos) para copiar e mandar; nas janelas dos plugins, Ctrl+Shift+L."),

                    new CommandDefinition(
                        C3DCommands.Core.Ribbon,
                        "Mostrar Ribbon",
                        "Cria ou reativa a aba C3D Tools da Ribbon.",
                        ShowInRibbon: false),

                    new CommandDefinition(
                        C3DCommands.Core.RibbonReset,
                        "Reconstruir Ribbon",
                        "Remove e recria a aba C3D Tools usando o catalogo atual.",
                        ShowInRibbon: false)
                }),

            new(
                "Drenagem",
                "Excel de drenagem",
                C3DVersions.DrenExcel,
                new[]
                {
                    new CommandDefinition(C3DCommands.Drenagem.Excel, "Aplicar Excel", "Le e aplica o dimensionamento da planilha."),
                    new CommandDefinition(C3DCommands.Drenagem.ExcelPreview, "Previa Excel", "Le e valida sem alterar o desenho."),
                    new CommandDefinition(C3DCommands.Drenagem.ExcelHelp, "Ajuda Excel", "Mostra a ajuda do modulo DRENEXCEL.")
                }),

            new(
                "Drenagem",
                "Numeracao hidraulica",
                C3DVersions.DrenNum,
                new[]
                {
                    new CommandDefinition(C3DCommands.Drenagem.Num, "Numerar rede", "Numera estruturas e tubos pela hierarquia hidraulica."),
                    new CommandDefinition(C3DCommands.Drenagem.NumPreview, "Previa numeracao", "Mostra a numeracao sem aplicar."),
                    new CommandDefinition(C3DCommands.Drenagem.NumHelp, "Ajuda numeracao", "Mostra a ajuda do modulo DRENNUM.")
                }),

            new(
                "QTO",
                "QTO",
                C3DVersions.Qto,
                new[]
                {
                    new CommandDefinition(C3DCommands.Qto.Estudo, "Estudo QTO", "Executa o estudo/relatorio QTO por estaca."),
                    new CommandDefinition(C3DCommands.Qto.GeoTeste, "Teste geometrico QTO", "Executa diagnostico geometrico do QTO."),
                    new CommandDefinition(C3DCommands.Qto.Teste, "Teste QTO", "Executa teste de carga do modulo QTO."),
                    new CommandDefinition(C3DCommands.Qto.Help, "Ajuda QTO", "Mostra a ajuda do modulo QTO.")
                }),

            new(
                "Corridor",
                "Divisao de Corridor",
                C3DVersions.CorridorSplit,
                new[]
                {
                    new CommandDefinition(C3DCommands.Corridor.Split, "Dividir corredor",
                        "Copia ou transfere regioes entre corredores, preservando baselines, frequencias, targets e transitions compativeis.")
                }),

            new(
                "Corridor",
                "Rebaixos de meio-fio",
                C3DVersions.MfRebaixo,
                new[]
                {
                    new CommandDefinition(C3DCommands.Corridor.MfRebaixoPainel, "Painel de rebaixos", "Explorador dos rebaixos: planta geral com codigo/nome, lista, edicao, nome, avisos da NBR 9050 e secoes do corredor."),
                    new CommandDefinition(C3DCommands.Corridor.MfRebaixo, "Rebaixo de meio-fio", "Cria Transition Sets de rebaixo no Corridor."),
                    new CommandDefinition(C3DCommands.Corridor.MfRebaixoCfg, "Configurar rebaixo", "Configura presets do MFREBAIXO."),
                    new CommandDefinition(C3DCommands.Corridor.MfRebaixoHelp, "Ajuda rebaixo", "Mostra a ajuda do MFREBAIXO.")
                }),

            new(
                "Corridor",
                "Array associativo de solidos",
                C3DVersions.SolidArray,
                new[]
                {
                    new CommandDefinition(C3DCommands.Corridor.SolidArray, "Array de solidos", "Distribui Solid3d nos vertices de AutoCorridorFeatureLine."),
                    new CommandDefinition(C3DCommands.Corridor.SolidCfg, "Configurar offsets", "Define offsets XYZ padrao do array."),
                    new CommandDefinition(C3DCommands.Corridor.SolidAtualizar, "Atualizar array", "Atualiza os solidos existentes no proprio objeto (apagados continuam fora) ou recria o conjunto."),
                    new CommandDefinition(C3DCommands.Corridor.SolidEditar, "Editar conjunto", "Edita offsets do conjunto."),
                    new CommandDefinition(C3DCommands.Corridor.SolidItem, "Editar item", "Edita, desassocia ou reassocia um unico solido."),
                    new CommandDefinition(C3DCommands.Corridor.SolidHelp, "Ajuda array", "Mostra a ajuda do C3DSOLIDARRAY.")
                }),

            new(
                "Corridor",
                "Ligacao tubular entre linhas",
                C3DVersions.SolidLink,
                new[]
                {
                    new CommandDefinition(C3DCommands.Corridor.SolidLigar, "Ligar linhas", "Liga N pontos de duas linhas com estais Solid3d: desconto % nas pontas, material, layer proprio e Property Set C3D_ESTAI."),
                    new CommandDefinition(C3DCommands.Corridor.SolidLigarEditar, "Editar ligacao", "Altera pontos, descontos, diametro, material, area, layer ou sentido de um conjunto e recria os estais."),
                    new CommandDefinition(C3DCommands.Corridor.SolidLigarAtualizar, "Atualizar ligacoes", "Atualiza os estais existentes no proprio objeto (apagados continuam fora) ou recria o conjunto."),
                    new CommandDefinition(C3DCommands.Corridor.SolidLigarTooltip, "Tooltip estais", "Liga ou desliga o tooltip com os dados do Property Set C3D_ESTAI ao passar o cursor."),
                    new CommandDefinition(C3DCommands.Corridor.SolidLigarHelp, "Ajuda ligacao", "Mostra a ajuda do C3DSOLIDLIGAR.")
                }),

            new(
                "Corridor",
                "Solidos em planta / 3D",
                C3DVersions.PlanVisibility,
                new[]
                {
                    new CommandDefinition(C3DCommands.Corridor.OcultarPlanta, "Solidos em planta", "Em planta, solidos dos layers configurados ficam ocultos ou so com arestas visiveis (linhas ocultas removidas, mesmo em 2D Wireframe e na plotagem); normais no 3D."),
                    new CommandDefinition(C3DCommands.Corridor.PlantaAtualizar, "Atualizar planta", "Recalcula as arestas visiveis em planta de todos os solidos (modo Exato).")
                }),

            new(
                "Corridor",
                "Passagem semi-elevada",
                C3DVersions.RaisedCrossing,
                new[]
                {
                    new CommandDefinition(C3DCommands.Corridor.Passagem, "Criar passagem", "Cria um unico Solid3d para duas vias com canteiro: 4 Feature Lines, rampas nas vias e conector central sem rampas."),
                    new CommandDefinition(C3DCommands.Corridor.PassagemEditar, "Editar passagem", "Edita plato, extensao, rampas, altura H, posicao ou ancoragem e atualiza o Property Set."),
                    new CommandDefinition(C3DCommands.Corridor.PassagemGrupo, "Editar em grupo", "Aplica parâmetros comuns e atualiza várias passagens selecionadas."),
                    new CommandDefinition(C3DCommands.Corridor.PassagemAtualizar, "Atualizar passagens", "Reconstrói passagens após alteração das 4 Feature Lines e atualiza o Property Set C3D_PASSAGEM."),
                    new CommandDefinition(C3DCommands.Corridor.PassagemCfg, "Configurar passagem", "Configura extensão transversal, comprimento padrão das rampas e passo de amostragem."),
                    new CommandDefinition(C3DCommands.Corridor.PassagemHelp, "Ajuda passagem", "Mostra as regras e comandos do módulo de passagem semi-elevada.")
                }),

            new(
                "Quadros",
                "Quadro de ruas",
                C3DVersions.QuadroRuas,
                new[]
                {
                    new CommandDefinition(C3DCommands.Quadros.Ruas, "Quadro de ruas",
                        "Exporta o quadro de ruas (.xlsx): nome, estaqueamento, extensao, largura, area e coordenadas geograficas DMS de inicio e fim, a partir dos eixos.")
                }),

            new(
                "Viario",
                "Rede viaria",
                C3DVersions.Viario,
                new[]
                {
                    new CommandDefinition(C3DCommands.Viario.Painel, "Painel viario",
                        "Janela flutuante com todos os comandos da rede viaria (icones e ilustracoes), secoes com corte transversal, lista de eixos com mapa, sinalizacao previa, Civil e configuracao."),
                    new CommandDefinition(C3DCommands.Viario.Eixo, "Lancar eixo",
                        "Lanca eixos (retas e arcos) com preview: bordos, meio-fio, calcada, canteiro e esquinas aparecem enquanto desenha."),
                    new CommandDefinition(C3DCommands.Viario.Raio, "Raio de esquina",
                        "Muda o raio de uma esquina, de uma ilha inteira ou de todas as esquinas de um eixo, arrastando o mouse com preview."),
                    new CommandDefinition(C3DCommands.Viario.Modelos, "Modelos de intersecao",
                        "Janela com intersecoes parametrizadas (cruz, T, Y, gotas, faixas livres, conversao, rotatorias) e insercao com ponto e rotacao."),
                    new CommandDefinition(C3DCommands.Viario.Alargar, "Alargamento / taper",
                        "Faixa adicional (conversao a direita/esquerda) na aproximacao, com armazenamento arrastavel e taper reto ou suave."),
                    new CommandDefinition(C3DCommands.Viario.Gota, "Gota",
                        "Ilha em gota na aproximacao (Tipo II), com alargamento da pista em volta."),
                    new CommandDefinition(C3DCommands.Viario.Conversao, "Conversao no canteiro",
                        "Faixa de conversao a esquerda tirada do canteiro central (Tipo IV)."),
                    new CommandDefinition(C3DCommands.Viario.Abertura, "Abertura de canteiro",
                        "Abertura do canteiro central para retorno, no meio da quadra."),
                    new CommandDefinition(C3DCommands.Viario.Livre, "Faixa livre a direita",
                        "Esquina com faixa livre de conversao a direita e ilha triangular (Tipo III)."),
                    new CommandDefinition(C3DCommands.Viario.ElemRemover, "Remover elemento",
                        "Remove alargamento, gota, conversao, abertura ou faixa livre clicando perto dele."),
                    new CommandDefinition(C3DCommands.Viario.Rede, "Rede viaria",
                        "Secoes (pista, canteiro, sarjeta, meio-fio, calcada, raio), rotatoria, adicionar/atribuir/remover eixos, desvincular/revincular linhas e atualizar."),
                    new CommandDefinition(C3DCommands.Viario.Civil, "Objetos do Civil",
                        "Gera e mantem alinhamentos do Civil: eixos, bordos LD/LE por trecho e curb returns (sem site)."),
                    new CommandDefinition(C3DCommands.Viario.Ilha, "Eliminar ilha",
                        "Clique dentro de uma ilha (anel de meio-fio, canteiro ou gota) para elimina-la; Restaurar/Todas desfaz."),
                    new CommandDefinition(C3DCommands.Viario.Sinal, "Sinalizacao horizontal",
                        "Sinalizacao horizontal previa num bloco unico (divisoria, faixas, faixas de pedestres, retencao) que acompanha a rede."),
                    new CommandDefinition(C3DCommands.Viario.Hachura, "Hachura do pavimento",
                        "Hachura unica so da regiao de pavimento (contorno pelas faces de meio-fio; quadras, canteiros e ilhas como furos), que acompanha a rede."),
                    new CommandDefinition(C3DCommands.Viario.Cotar, "Cotar faixas",
                        "Um clique sobre o eixo cria a cadeia de cotas da secao naquela estaca (faixas, canteiro, passeio) e a largura total."),
                    new CommandDefinition(C3DCommands.Viario.Teste, "Autoteste",
                        "Confere eixos, bordos e curb returns do Civil contra a rede e grava relatorio em %TEMP%."),
                    new CommandDefinition(C3DCommands.Viario.Diag, "Diagnostico API Civil",
                        "Lista por reflexao o que a API do Civil expoe para alinhamentos, perfis offset, intersecoes e curb returns (somente leitura).")
                }),

            new(
                "Feature Lines",
                "Feature Lines",
                null,
                new[]
                {
                    new CommandDefinition(C3DCommands.FeatureLines.ZSet, "Ajustar Z", "Ferramenta de elevacao de Feature Line."),
                    new CommandDefinition(C3DCommands.FeatureLines.SetPv, "Editar vertices", "Seleciona e altera elevacoes de vertices."),
                    new CommandDefinition(C3DCommands.FeatureLines.RenomearCorte, "Renomear por corte", "Renomeia Feature Lines selecionadas por ordem de corte.")
                }),

            new(
                "Superficies",
                "Superficies",
                null,
                new[]
                {
                    new CommandDefinition(C3DCommands.Superficies.ReadScheme, "Ler esquema", "Le o esquema de analise da superficie."),
                    new CommandDefinition(C3DCommands.Superficies.ColorAuto, "Cores automaticas", "Aplica faixas de cor de corte/aterro."),
                    new CommandDefinition(C3DCommands.Superficies.SlopeFilter, "Filtro de talude", "Filtra a superficie pela inclinacao."),
                    new CommandDefinition(C3DCommands.Superficies.TrimLines, "Recortar linhas", "Recorta linhas usando a geometria da superficie."),
                    new CommandDefinition(C3DCommands.Superficies.Cortar1Mm, "Fresta 1 mm", "Cria boundary de 1 mm para corte localizado.")
                }),

            new(
                "Geometria",
                "Diagnostico geometrico",
                null,
                new[]
                {
                    new CommandDefinition(C3DCommands.Geometria.PointNormalDebug, "Normal de plano", "Diagnostica o vetor normal definido por tres pontos.")
                }),

            new(
                "Corridor",
                "Visualizador de secao",
                C3DVersions.Secao,
                new[]
                {
                    new CommandDefinition(C3DCommands.Secao.Painel, "Visualizador de secao",
                        "Janela propria (fora da viewport) com o corte de uma caixa - linha em planta, profundidades e faixa de elevacao - por superficies, corredores, tubos, solidos, blocos, feature lines e pontos, com faixa de vista por elemento; secao do corredor estaca a estaca, comparacao e vista 3D recortada na caixa."),
                    new CommandDefinition(C3DCommands.Secao.Corte, "Nova caixa de corte",
                        "Linha de corte por pontos ou polilinha existente, com profundidades a frente e atras; a polilinha (layer SECAO-CORTE) guarda a caixa e a janela acompanha os grips."),
                    new CommandDefinition(C3DCommands.Secao.Desenhar, "Desenhar secao no model",
                        "Leva a secao mostrada na janela para o desenho como polilinhas 2D, com moldura, elevacoes e distancias.")
                }),

            new(
                "Layouts",
                "Explorador de layouts",
                C3DVersions.Layouts,
                new[]
                {
                    new CommandDefinition(C3DCommands.Layouts.Painel, "Explorador de layouts",
                        "Janela no molde do Project Explorer: layouts com miniatura e viewports, renomear/reordenar/copiar/excluir, viewports (travar, ligar, escala), numeracao de pranchas, layer state nas viewports e carimbo em lote."),
                    new CommandDefinition(C3DCommands.Layouts.Viewports, "Viewports", "Abre o explorador na categoria Viewports."),
                    new CommandDefinition(C3DCommands.Layouts.LayerState, "Layer state nas viewports", "Abre o explorador na categoria Layer state."),
                    new CommandDefinition(C3DCommands.Layouts.Carimbo, "Carimbo em lote", "Abre o explorador na categoria Carimbo."),
                    new CommandDefinition(C3DCommands.Layouts.PageSetup, "Page setup em lote",
                        "Abre o explorador na categoria Page setup: aplicar page setup nomeado ou plotter/papel/CTB em varios layouts, importar de DWG/DWT."),
                    new CommandDefinition(C3DCommands.Layouts.MapaChave, "Mapa-chave",
                        "[Base/Area/Gerar/Atualizar/Remover]: mapa-chave em cada layout com contorno e numero das folhas e hachura da folha atual, atualizado sozinho."),
                    new CommandDefinition(C3DCommands.Layouts.Pranchas, "Pranchas do eixo",
                        "[Painel/Atualizar/PontoPerfis]: frames ao longo do eixo no tamanho da viewport de planta, uma prancha por frame e um perfil por folha no tamanho da viewport de perfil; mover o frame atualiza tudo."),
                    new CommandDefinition(C3DCommands.Layouts.Prancha, "Inserir prancha",
                        "Bloco de prancha (A0 a A4 ou livre) com estendimentos, margens e dobras, na origem ou no ponto clicado; configuracao na aba Prancha do explorador."),
                    new CommandDefinition(C3DCommands.Layouts.Numerar, "Numerar pranchas",
                        "[Configurar/Atualizar]: numero e total nos textos marcados (busca com filtros no explorador ou clique no Configurar), na ordem das abas.")
                }),

            new(
                "PEDs",
                "Locacao de PEDs",
                C3DVersions.Peds,
                new[]
                {
                    new CommandDefinition(C3DCommands.Peds.Ficha, "Ficha do PED",
                        "Janela que acompanha a selecao: clique num bloco PED e veja a ficha (larguras atual x proposta, abrigo, embarques, distancias, obs, foto do local e mapa)."),
                    new CommandDefinition(C3DCommands.Peds.Importar, "Importar PEDs",
                        "Le a planilha de proposta (.xlsx) e o KMZ: insere o bloco PED em cada ponto (dados em atributos) e a ficha em tabela com foto e mapa num multileader preso ao simbolo (arrasta pelo grip, layers por PED)."),
                    new CommandDefinition(C3DCommands.Peds.Atualizar, "Atualizar da planilha",
                        "Rele a planilha gravada (nova revisao) e as imagens, sem mexer na posicao dos blocos ja inseridos."),
                    new CommandDefinition(C3DCommands.Peds.Locar, "Locar / mover PED",
                        "Insere ou move um PED clicando no ponto (para os que nao estao no KMZ), com rotacao opcional."),
                    new CommandDefinition(C3DCommands.Peds.Vista, "Ponto / ficha / foto",
                        "Troca a vista dos PEDs (todos ou selecionados): so o simbolo, simbolo + ficha, ou ficha + foto e mapa, ligando/desligando as layers de cada PED (o mesmo que se faz sem o plugin).")
                }),

            new(
                "LISPs",
                "Utilitarios (ex-LISP)",
                C3DVersions.Utilitarios,
                new[]
                {
                    new CommandDefinition(C3DCommands.Utilitarios.AllToRgb, "Cores ACI para RGB", "Converte a cor ACI para RGB: dos objetos, das layers deles ou dos dois."),
                    new CommandDefinition(C3DCommands.Utilitarios.Tlen, "Comprimento total", "Soma o comprimento das curvas selecionadas."),
                    new CommandDefinition(C3DCommands.Utilitarios.JobeCount, "Numerar textos", "Acrescenta numero sequencial aos textos, na ordem de leitura."),
                    new CommandDefinition(C3DCommands.Utilitarios.SmartHatch, "Hachura ignorando gaps", "Hachura SOLID clicando dentro de area com aberturas no contorno."),
                    new CommandDefinition(C3DCommands.Utilitarios.LsLayouts, "Layer state em layouts", "Abre o explorador de layouts na categoria Layer state."),
                    new CommandDefinition(C3DCommands.Utilitarios.Moff, "Varios offsets", "Varios offsets em polilinhas fechadas, para dentro ou para fora."),
                    new CommandDefinition(C3DCommands.Utilitarios.MoffSet, "Varios offsets (alias)", "Mesmo que MOFF.", ShowInRibbon: false),
                    new CommandDefinition(C3DCommands.Utilitarios.SeparaCurvas, "Separar curvas por raio", "Quebra polilinhas em retos, curvas acima e ate o raio limite."),
                    new CommandDefinition(C3DCommands.Utilitarios.Kb, "Marcacao de meio-fio", "Face do meio-fio e linha central tracejada na layer Kerb Line."),
                    new CommandDefinition(C3DCommands.Utilitarios.Pp, "Unir polilinhas", "PEDIT Join com tolerancia 0,01.")
                },
                ShowInRibbon: false),

            new(
                "LISPs",
                "LISPs embutidas",
                C3DVersions.LispTools,
                new[]
                {
                    new CommandDefinition(C3DCommands.LispTools.Painel, "Ferramentas e LISPs",
                        "Lista de todas as ferramentas (nativas e LISPs) com icone, nome, comando e descricao; duplo clique executa."),
                    new CommandDefinition(C3DCommands.LispTools.Lista, "Lista na linha de comando",
                        "Mostra as LISPs embutidas com explicacao e modo de uso na linha de comando.",
                        ShowInRibbon: false),
                    new CommandDefinition(C3DCommands.LispTools.Carregar, "Recarregar LISPs",
                        "Regrava as LISPs na pasta local e registra os comandos de novo no desenho atual.",
                        ShowInRibbon: false)
                }),

            new(
                "Diagnostico",
                "Plugin / sessao",
                null,
                new[]
                {
                    new CommandDefinition(C3DCommands.Diagnostico.TesteDll, "Teste DLL", "Confirma o carregamento basico da DLL."),
                    new CommandDefinition(C3DCommands.FeatureLines.Descarregar, "Aviso de recarga", "Comando legado; explica que a DLL .NET nao e descarregada na sessao atual.")
                },
                ShowInRibbon: false)
        };

    /// <summary>Modulos que vao para a Ribbon: marcados pelo usuario (C3DMODULOS) e com comandos de Ribbon.</summary>
    public static IEnumerable<ModuleDefinition> RibbonModules =>
        Modules.Where(module =>
            module.ShowInRibbon &&
            ModuleSettings.IsEnabled(module) &&
            module.Commands.Any(command => command.ShowInRibbon));

    public static void WriteTree(Editor ed, bool includeDescriptions = false)
    {
        foreach (IGrouping<string, ModuleDefinition> category in Modules.GroupBy(m => m.Category))
        {
            ed.WriteMessage($"\n+ {category.Key}");

            foreach (ModuleDefinition module in category)
            {
                string version = string.IsNullOrWhiteSpace(module.Version)
                    ? string.Empty
                    : $" v{module.Version}";

                if (!ModuleSettings.IsEnabled(module))
                {
                    ed.WriteMessage($"\n  |-- {module.Name}{version}  (desativado em {C3DCommands.Core.Modulos})");
                    continue;
                }

                ed.WriteMessage($"\n  |-- {module.Name}{version}");

                foreach (CommandDefinition command in module.Commands)
                {
                    ed.WriteMessage($"\n  |   |-- {command.Name}");
                    if (includeDescriptions)
                        ed.WriteMessage($" - {command.Description}");
                }
            }
        }
    }
}
