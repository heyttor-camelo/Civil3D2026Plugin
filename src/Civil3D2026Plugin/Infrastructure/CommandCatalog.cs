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
                    new CommandDefinition(C3DCommands.Corridor.SolidAtualizar, "Atualizar array", "Reconstrui um conjunto apos alteracao do Corridor."),
                    new CommandDefinition(C3DCommands.Corridor.SolidEditar, "Editar conjunto", "Edita offsets do conjunto."),
                    new CommandDefinition(C3DCommands.Corridor.SolidItem, "Editar item", "Edita, desassocia ou reassocia um unico solido."),
                    new CommandDefinition(C3DCommands.Corridor.SolidHelp, "Ajuda array", "Mostra a ajuda do C3DSOLIDARRAY.")
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

    public static IEnumerable<ModuleDefinition> RibbonModules =>
        Modules.Where(module =>
            module.ShowInRibbon &&
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
