## 2.9.6 - CORRSPLIT v1.6.0 / selecao grafica de regiao ao estilo Civil 3D

- Seletor de regioes refeito: usa geometria de AppliedAssembly.Points e os limites de offset por estaca, sem a tolerancia arbitraria de 35 unidades ao eixo.
- Contorno azul temporario da regiao sob o cursor com tooltip do nome e baseline durante a selecao no DWG.
- Clique dentro da faixa da regiao marca/desmarca e devolve o foco a janela; ESC cancela a escolha atual.
- Clique na linha da tabela realca visualmente o contorno; duplo clique marca/desmarca.
- As regioes marcadas ficam realcadas por TransientManager ate fechar a janela, sem criar polylines permanentes.
- Regioes sem secoes calculadas devem ser selecionadas na tabela; em sobreposicoes usa a menor area geometrica.
- Motor CORRSPLIT v1.5, comprovado pelo usuario em Copiar e Transferir, nao foi modificado. MFREBAIXO tambem nao foi alterado.
- Nota: a silhueta e calculada pelas secoes amostradas, nao a selecao nativa privada do Civil 3D. Compilacao e teste da nova interface pendentes.

## 2.9.5 - CORRSPLIT v1.5.0 / estacas adicionais apos Rebuild

- Corrige erro de verificacao "Estacas adicionais divergiram" na regiao TRECHO - _CURB - 210: agora frequencias e estacas adicionais sao verificadas separadamente.
- Em vez de definir AdditionalAppliedAssemblies durante criacao da regiao, restaura usando ClearAdditionalStations + AddStation apos o primeiro Rebuild.
- Valida sem depender da ordem das estacas, mantendo estacas e descricoes (comparacao de estacas com tolerancia 1e-6).
- Se o Rebuild apos targets alterar a lista, tenta uma restauracao e rebuild adicionais, com no maximo uma tentativa.
- Divergencias persistentes ainda bloqueiam a transferencia, mas mensagem passa a listar contagem, estacas e descricoes de origem/destino.
- MFREBAIXO nao alterado. Teste local e compilacao Autodesk ainda pendentes.

## 2.9.4 - CORRSPLIT v1.4.0 / checar tipo do target em UseSameSideTarget

- O erro em PAV_ESQ / Outside Elevation (Elevation, 2 TargetIds) mostrou que UseSameSideTarget exige nao so >=2 IDs, mas tambem exclusivamente o TargetType Offset; OffsetPipe tambem e rejeitado.
- Novo predicado CanReadSameSideTarget protege todos os GET/SET de UseSameSideTarget (CopyTargets e verificacoes imediata/pos-rebuild), exigindo Offset nos dois lados e >=2 TargetIds em ambos.
- Continuam sendo copiados os TargetIds de Elevation e demais tipos, sem consultar UseSameSideTarget.
- MFREBAIXO nao foi modificado. Compilacao e ensaio com Civil 3D pendentes.

## 2.9.3 - CORRSPLIT v1.3.0 / corrigir getter UseSameSideTarget com 1 ID

- O stack trace no parâmetro 'PAV_DIR / Lane Width' revelou que a API Autodesk valida TargetIds.Count >= 2 também no **getter** UseSameSideTarget;
- CopyTargets não lê nem grava UseSameSideTarget com menos de 2 IDs no parâmetro de origem ou destino;
- corrigidas as comparações após SetTargets e no VerifyTargets depois do Rebuild, que também invocavam o getter sem checar a quantidade;
- TargetToOption também passa a conferir a contagem >= 2 nas duas coleções antes de ler;
- preserva TargetIds de parâmetros com apenas 1 ID (um target horizontal e um vertical são parâmetros independentes);
- sem alterações no MFREBAIXO; compilação e ensaio no Civil 3D 2026.2 pendentes.

## 2.9.2 - CORRSPLIT v1.2.0 / targets por parametro e validacao por etapas

- 0/1 TargetId tratado por parametro de subassembly (horizontal e vertical sao parametros distintos);
- targets aplicados somente depois da criacao de todas as baselines e regioes do destino;
- rebuild inicial sem targets, aplicacao dos mapeamentos e rebuild final antes de remover qualquer regiao de origem;
- evita chamadas desnecessarias a TargetToOption e SetTargets para parametros sem alteracoes;
- ao falhar, informa baseline/region, subassembly, nome e tipo de parametro, numero de IDs e etapa exata com stack trace;
- selecao grafica e highlighing ainda carecem de aperfeicoamento; prioridade desta revisao e validacao do motor de copia.
- requer recompilar e reiniciar Civil 3D para carregar a DLL atualizada e fazer primeiro teste no modo Copiar em copia do DWG.

## 2.9.1 - CORRSPLIT v1.1.0 / TargetIds e interface semelhante a Corridor Properties

- corrige atribuicao de TargetToOption quando TargetIds.Count < 2, prevenindo InvalidOperationException;
- acrescenta janela hierarquica de baselines/regioes com colunas de eixo horizontal/vertical, assembly, estacas e resumo de targets;
- selecao por regiao, baseline, todas, inverter, expandir/recolher, inclusive selecao pela proximidade de um clique no desenho;
- pictograma CORRSPLIT e estilo de icones CAD na Ribbon, preservando estrutura e comandos anteriores;
- CORRSPLIT compilacao e ensaio no Civil 3D ainda pendentes.

## 2.9.0 - CORRSPLIT v1.0.0 (primeira versao para testes)

- novo comando CORRSPLIT para copiar ou transferir regioes selecionadas de Corridor entre corredores no mesmo DWG;
- baselines necessarias criadas conforme origem (Alignment/Profile e Feature Line);
- recriacao de Assembly, frequencias, estacas adicionais, targets e transitions compativeis;
- interface visual com selecao multipla, modo copiar e modo transferir;
- protecao para transitions que cruzam regioes e para overrides e offset baselines;
- copia e exclusao, quando solicitada, na mesma transacao, apos validacoes e rebuild do destino;
- Corridor Surfaces e outras configuracoes avancadas nao sao clonadas nesta primeira versao;
- MFREBAIXO mantido integralmente sem alteracoes de codigo; sua versao declarada nao foi alterada.
- exige build e testes em ambiente com Civil 3D 2026.2 antes do uso em producao.

# Changelog

## 2.8.0 - PASSAGEM v1.1.0 / duas vias + canteiro + Property Set

- PASSAGEM passa de 2 para 4 Feature Lines: FL1 externa VIA 1, FL2 interna VIA 1, FL3 interna VIA 2 e FL4 externa VIA 2;
- gera VIA 1 e VIA 2 com rampas veiculares, ligadas por um trecho sobre o canteiro sem rampas;
- o conector central inicia depois das extensoes internas configuradas;
- resultado final e um unico `Solid3d`, obtido por lofts + `BooleanOperation(BoolUnite)`;
- base usa as cotas locais das quatro Feature Lines e topo = base + H;
- H passa a ser manual como regra principal e fica persistido por passagem;
- Property Set AEC `C3D_PASSAGEM` criado/anexado ao Solid3d final;
- Property Set registra ID, versao, H, plato, rampa, extensao, handles FL1-FL4, cotas FL1-FL4 no CG e larguras VIA1/CANTEIRO/VIA2 no CG;
- `PASSAGEMEDITAR`, `PASSAGEMGRUPO` e `PASSAGEMATUALIZAR` atualizam a geometria e o Property Set;
- registros antigos de PASSAGEM com apenas duas Feature Lines continuam legiveis, mas precisam ser recriados para usar a geometria v1.1.0.

## 2.7.0 - passagem semi-elevada parametrizada

- adiciona o modulo `CorridorTools/RaisedCrossing` v1.0.0;
- novo `PASSAGEM`: Solid3d entre duas Feature Lines, com `Meio/Inicio/Fim`, comprimento por clique ou extensao digitada e acrescimo lateral nos dois lados;
- topo usa os Z reais das Feature Lines; fundo permanece paralelo ao topo e H abaixo em vertical;
- loft longitudinal amostrado para acompanhar curvas, com fallbacks internos;
- leitura automatica de `DIST_P4_P1_Y` em `Subassembly.MF` na AppliedAssembly mais proxima da estaca do CG;
- resolucao de Corridor/Baseline por identidade da AutoCorridorFeatureLine e proximidade, com escolha pelo usuario quando ambiguo;
- fallback para altura manual quando nao houver contexto de Corridor/Subassembly utilizavel;
- persistencia no DWG para edicao e atualizacao futuras;
- comandos `PASSAGEMEDITAR`, `PASSAGEMGRUPO`, `PASSAGEMATUALIZAR`, `PASSAGEMCFG` e `PASSAGEMHELP`;
- modulo integrado ao `CommandNames`, `CommandCatalog`, banner, C3DHELP e Ribbon `C3D Tools`;
- versao exibida do MFREBAIXO sincronizada com o codigo atual: v1.1.3.

## 2.6.1 - correção de namespaces WPF/WinForms

- Corrige CS0104 em `RibbonIconFactory.cs` para `Color`, `Point`, `FlowDirection`, `FontFamily` e `Brushes`.
- A Ribbon passa a usar aliases WPF explícitos, mantendo WinForms e WPF no mesmo projeto sem colisões.
- Remove `FrameworkReference` explícito para `Microsoft.WindowsDesktop.App`, que era redundante com `UseWPF`/`UseWindowsForms` e gerava `NETSDK1086`.
- Reforça o validador para alertar sobre tipos WPF potencialmente ambíguos em futuras alterações da Ribbon.

# CHANGELOG

## 2.6.0 - Ribbon C3D Tools

- criada a aba `C3D Tools` automaticamente apos `NETLOAD`;
- Ribbon gerada diretamente de `CommandCatalog`, sem duplicar lista de comandos;
- agrupamento por modulo, com primeiro comando em destaque;
- tooltips simples e expandidos com titulo, descricao, comando, grupo, modulo e versao;
- icones gerados em memoria, sem arquivos externos;
- comandos `C3DRIBBON` e `C3DRIBBONRESET`;
- `AdWindows.dll` adicionada como referencia Autodesk com `Private=False`;
- WPF habilitado no projeto para a camada de UI;
- infraestrutura pronta para trocar os icones por recursos proprios e para receber futuras PaletteSet/janelas WPF.

## 2.5.1 - correção estrutural de namespace

- Renomeado o agrupamento interno `Modules.Corridor` para `Modules.CorridorTools`.
- Elimina a colisão entre o namespace do projeto e o tipo `Autodesk.Civil.DatabaseServices.Corridor`.
- QTO passa a usar alias explícito `CivilCorridor` nos pontos em que recebe o tipo Corridor.
- Validador reforçado para impedir namespace de módulo chamado `Corridor` e detectar colisões críticas semelhantes.
- Nenhum nome de comando do Civil 3D foi alterado.
- Nenhuma lógica funcional de QTO, MFREBAIXO ou C3DSOLIDARRAY foi alterada.

# Changelog

## 2.5.0 - reorganização estrutural

- raiz do projeto reduzida para `C3DPlugin/`;
- removidos do pacote `.vs`, `bin`, `obj` e históricos soltos;
- saída de compilação fixada em `NETLOAD\Civil3D2026Plugin.dll`;
- solução simplificada para `x64`;
- módulos reorganizados em `Modules/Drenagem`, `Modules/Corridor`, `Modules/QTO`, `Modules/FeatureLines`, `Modules/Surfaces` e `Modules/Geometry`;
- namespaces internos antigos (`ClassLibrary2`, `Civil3DTools`, etc.) normalizados;
- um único `IExtensionApplication` em `Infrastructure/PluginApplication.cs`;
- registro das classes de comandos centralizado em `CommandRegistration.cs`;
- nomes públicos dos comandos centralizados em `CommandNames.cs`;
- versões exibidas centralizadas em `PluginVersions.cs`;
- árvore de comandos centralizada em `CommandCatalog.cs` e exibida no `NETLOAD`;
- banner passa a mostrar o caminho exato da DLL carregada;
- criado `C3DHELP` para repetir a árvore de comandos;
- catálogo preparado para servir de fonte à futura Ribbon/GUI;
- aliases globais `AcadDb` e `CivilDb` adicionados como padrão para novos módulos;
- `FLDESCARREGAR` deixou de afirmar incorretamente que descarrega uma DLL .NET;
- adicionados scripts de build, limpeza e validação estrutural.

Nenhuma regra funcional dos módulos DRENEXCEL, DRENNUM, QTO, MFREBAIXO ou C3DSOLIDARRAY foi intencionalmente alterada nesta reorganização.
