# HYDRO — módulo de apoio à Pipe Network (plano)

Escrito em 21/10/2026. Decisão do David: o HYDRO deixa de ser um modelador próprio de dispositivos
(o repositório `DeviceModeler` fica guardado como está) e passa a ser **um módulo deste plugin que
deixa a Pipe Network do Civil 3D tão fácil de usar quanto o SOLIDOS**. Os objetos continuam sendo os
nativos (Network, Pipe, Structure, Catchment); o HYDRO edita, liga, calcula, verifica e lança.

Responda e documente em **português**. Siga `docs/DEVELOPMENT.md` (§2 comandos, §8 padrão do painel).

## 1. O que o módulo faz (escopo da v1, tudo pedido pelo usuário)

1. **Editar tudo, em lote.** Uma planilha no painel com estruturas e tubos da rede escolhida, de
   montante para jusante: nome, família/tamanho, elevações de tampa e fundo, profundidade, diâmetro,
   declividade, comprimento, recobrimento, sentido do fluxo. Qualquer campo editável é editado na
   própria lista; com vários itens marcados, a edição vale para todos (ex.: "fundo = fundo − 0,30",
   "declividade = 0,5 %", "família = Concreto / 600 mm"). Trocar família/tamanho **mantém as
   conexões** (`Part.SwapPartFamilyAndSize` ou equivalente; confira na API).
2. **Bacias → dispositivos.** Ligar catchments às estruturas de engolimento (`Catchment.
   ReferencePipeNetworkStructureId`), com área, C e tempo de concentração por bacia; criar catchments a
   partir de polilinhas; mostrar na planta quem contribui para quem; somar as bacias a montante de
   cada trecho.
3. **Cálculo e verificação.** Método racional acumulado por trecho (Q em **m³/s**, nunca litros),
   IDF editável (biblioteca de equações, com a de Cuiabá se o usuário fornecer), tempo de
   concentração (fórmulas usuais + mínimo), Manning em seção circular (lâmina, velocidade, Q plena),
   verificações: lâmina máxima, velocidade mín/máx, recobrimento mín, degrau nas estruturas, declividade
   mín/máx. Os resultados são **gravados nos campos nativos** do Civil (vazão, HGL e EGL de cada
   tubo/estrutura; o que não tiver campo vai para um Property Set `HYDRO`), para rótulos, tabelas e
   perfis nativos mostrarem. As **regras da Pipe Network** (recobrimento, declividade, encaixe) são
   lidas/aplicadas pelo módulo (`ApplyRules`), e as violações aparecem na categoria Verificação.
   Dimensionamento assistido: "menor diâmetro que atende" por trecho e matriz diâmetro × declividade.
4. **Lançamento e numeração.** Lançar PV → tubo → PV em cadeia (jig com prévia), trocar o tipo de
   dispositivo mantendo as conexões, inverter sentido, renumerar pelo fluxo (absorver o DRENNUM como
   ação do painel; o comando continua existindo), importar planilha (DRENEXCEL vira ação do painel).

## 2. Sobre o "motor de cálculo do Civil" (decisão)

A Pipe Network não tem solver hidráulico; quem calcula é o Hydraflow Storm Sewers (programa à parte,
troca por arquivo .stm, sem API). Então o cálculo é **nosso**, mas os resultados vão para os campos
nativos (ver item 3), e as **regras** nativas são usadas. Se mais tarde valer a pena, exportar .stm.

## 3. Painel `C3DHYDRO` (molde do Viário — ver `Modules/Viario/ViarioPanel.cs`)

Categorias (contagem entre parênteses, aviso em destaque):

| Categoria | Lista | Monitor | Abas do item |
|---|---|---|---|
| **Redes** | redes do DWG: nº de estruturas/tubos, extensão, lista de peças, status | planta esquemática de todas | Propriedades (lista de peças, regras padrão, superfície de referência) |
| **Estruturas** | planilha da rede ativa (nome, família/tamanho, tampa, fundo, prof., nº de tubos, bacias ligadas, avisos) | planta da rede com setas do fluxo; perfil do caminho selecionado (alternável) | Propriedades (editáveis), Conexões (tubos de entrada/saída, bacias), Hidráulica (Q, HGL/EGL, degrau), Regras |
| **Tubos** | planilha de tubos (nome, de → para, família/tamanho, Ø, L, declividade, fundo montante/jusante, recobrimento mín, Q, lâmina, V, avisos) | idem | Propriedades, Hidráulica (Q, lâmina, V, Qplena, matriz Ø × i), Regras |
| **Bacias** | catchments: nome, grupo, área, C, Tc, estrutura de destino | polígonos + linha até a estrutura | Propriedades, Contribuição (a jusante) |
| **Cálculo** | parâmetros: IDF, período de retorno, Tc mínimo, critérios (lâmina, V, recobrimento) | curva IDF | — |
| **Verificação** | violações (item, regra, valor, limite, severidade) | marcadores na planta | Ir para / Corrigir |
| **Saídas** | planilha de dimensionamento, exportação XLSX/HTML, rótulos/tabelas nativas | — | — |

Barra de comandos por categoria (ícones 32×32 vetoriais em `HydroIcons`, estilo Viário; cores:
ciano = água/tubo, branco = estrutura, verde = construído, amarelo = ação, vermelho = erro):
- Redes: Nova rede ▾ · Lançar · Importar planilha · Numerar ▾ · Calcular tudo.
- Estruturas/Tubos: Editar em lote ▾ (fundo, tampa, família/tamanho, declividade, nome) · Trocar tipo ·
  Inverter · Conectar/Desconectar · Aplicar regras · Zoom · Selecionar no desenho.
- Bacias: Nova por polilinha · Ligar à estrutura · Calcular Tc ▾ · Agrupar.
- Cálculo: Calcular · Dimensionar (menor Ø) · Matriz Ø × i.
- Verificação: Verificar · Ir para · Corrigir ▾.
- Saídas: Planilha · Exportar ▾ · Rótulos ▾ · Tabela.

Regras do §8: janela não modal de instância única, Esc nunca fecha, log comum (`PluginLog`),
progresso (`Progresso`), tema claro/escuro, lógica separada da tela. O painel lê o desenho no Idle
e sem `LockDocument`; escreve só por comandos (`SendStringToExecute`) ou por uma transação própria
com o documento travado.

## 4. API do Civil 3D a usar (confira os nomes exatos com ILSpy/REA em `AeccDbMgd.dll`)

- `CivilDocument.GetPipeNetworkIds()`, `Network` (`PartsListId`, `GetPipeIds`, `GetStructureIds`,
  `AddLinePipe`, `AddStructure`), `PartsList` / `PartFamily` / `PartSize` (`SizeDataRecord`,
  `PartContextType.PipeInnerDiameter`), `Part.SwapPartFamilyAndSize`.
- `Pipe`: `StartPoint/EndPoint`, `StartStructureId/EndStructureId`, `Slope`, `SetSlopeHoldStart/End`,
  `InnerDiameterOrWidth`, `OuterDiameterOrWidth`, `Length2D/3D`, `FlowDirection`,
  `ConnectToStructure`, `Disconnect`, `CoverStart/End` ou `MinimumCover`, `HydraulicGradeLineUp/
  Down`, `EnergyGradeLineUp/Down`, `FlowRate` (ou `Flow`), `ApplyRules`, `RuleSetStyleId`.
- `Structure`: `Location`, `Rotation`, `RimElevation`, `SumpElevation`, `SumpDepth`, `ControlSumpBy`,
  `SurfaceAdjustmentValue`, `RefSurfaceId`, `ConnectedPipe(i)`, `ConnectedPipesCount`,
  `HydraulicGradeLine*`, `EnergyGradeLine*`, `ApplyRules`.
- `Catchment` / `CatchmentGroup`: `Create`, `Area`, `RunoffCoefficient`, `TimeOfConcentration`,
  `ReferencePipeNetworkStructureId`, `HydrologicalMethod`, `FlowSegments`.
- Property Sets (`AecPropDataMgd`): `PropertySetDefinition`, `PropertyDataServices`.
- Reatores: `ObjectModified` só enfileira; processar no `CommandEnded` com `LockDocument` (como o
  `DeviceWatcher` do DeviceModeler e o Viário fazem).
- O que já existe aqui e deve ser reaproveitado: `Modules/Drenagem/DrenExcelCommands.cs` (regras de
  montante/jusante, `FlowDirection`, `SetSlopeHoldStart`) e `DrenNumCommands.cs` (numeração).

## 5. Fases (cada uma: compila sem avisos novos, `tools/ValidateProject.ps1`, versão em
`PluginVersions.cs` (`Hydro`), CHANGELOG, commit pequeno em português no master, push)

- **H0 — Esqueleto.** `Modules/Hydro/`, `C3DCommands.Hydro`, painel `C3DHYDRO` com as 7 categorias,
  ícones, Ribbon (um botão por categoria), leitura das redes/estruturas/tubos nas listas, monitor com
  a planta e setas do fluxo, log e progresso. Nada escreve no desenho ainda.
- **H1 — Edição em lote.** Campos editáveis nas listas, edição em lote, trocar família/tamanho
  mantendo conexões, inverter, aplicar regras; desfazer em um UNDO por operação.
- **H2 — Bacias.** Categoria Bacias, criar por polilinha, ligar à estrutura, Tc, soma a montante.
- **H3 — Cálculo e verificação.** Racional + Manning (lógica em classe pura testável, sem AutoCAD),
  IDF, critérios, gravação nos campos nativos e Property Set, categoria Verificação, dimensionar,
  matriz Ø × i, planilha de dimensionamento e exportação.
- **H4 — Lançamento.** Jig em cadeia com prévia, trocar tipo, DRENNUM/DRENEXCEL como ações.

## 6. O que não fazer

- Não criar objetos próprios no lugar dos nativos (nada de bloco por dispositivo nesta linha).
- Não compilar código do usuário. Não usar litros. Não fechar janela com Esc.
- Não mexer no repositório `DeviceModeler`.
