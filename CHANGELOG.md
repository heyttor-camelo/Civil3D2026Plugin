## 2.24.0 - C3DVIARIO v0.2.0 / secao completa, canteiro, rotatoria, raios ao vivo

- "perfil" vira **secao**: pista, canteiro central, sarjeta, meio-fio e calcada, alem do raio de esquina (medido na face do meio-fio, demais linhas concentricas). Perfis da v0.1 sao lidos como secoes sem sarjeta/meio-fio;
- uma linha por elemento, cada uma no seu layer e cor: `VIARIO-PISTA` (7), `VIARIO-MF-FACE` (4), `VIARIO-MF-COSTAS` (3), `VIARIO-ALINHAMENTO` (8), `VIARIO-CANTEIRO` (30); componente com largura 0 nao gera linha;
- canteiro central: ilhas entre os nos, cortadas antes da pista transversal (recuo configuravel, padrao 1 m) com nariz semicircular;
- eixos fechados (circulo e polilinha fechada) no motor; opcao **Rotatoria** cria o anel pelo centro e raio da ilha central; os ramos encostados formam as entradas sozinhos;
- **C3DVIARIORAIO**: raio de uma esquina (clique perto), de todas as esquinas de uma ilha (clique dentro) ou de um eixo, arrastando o mouse (distancia = raio) ou digitando, com a rede recalculada ao vivo; [Padrao] volta ao raio da secao; Limpar apaga os especificos. Prioridade: esquina > eixo > secao;
- C3DVIARIOEIXO: opcao [Arco/Linha] para trechos em arco tangente;
- **Soltar**: linhas geradas viram polilinhas comuns e o plugin nao as recria (Soltar > Restaurar desfaz);
- linhas geradas fora do layer do seu elemento sao refeitas no layer certo;
- compilacao e teste no Civil 3D pendentes.

## 2.23.0 - C3DVIARIO v0.1.0 / rede viaria (etapa 1)

- novo modulo `Modules/Viario`: comandos `C3DVIARIOEIXO` (lancar eixo) e `C3DVIARIO` [Eixo/Adicionar/Perfil/Atribuir/Remover/Atualizar/Config];
- perfis de via (pista, calcada de cada lado, raio de esquina) guardados no DWG (`C3D_VIARIO_V1`); perfil padrao "Local" 7 / 2,5 / 6 m;
- eixos identificados por XData `C3DVIARIO` (EIXO + perfil): polilinha com retas e arcos, linha ou arco; copia de eixo ja entra na rede;
- motor 2D proprio (sem AutoCAD): paralelas com juntas, intersecoes reta/arco, deteccao de nos (cruzamento e ponta encostada, tolerancia configuravel), ramos ordenados por angulo, esquinas concavas concordadas com o raio do perfil (alinhamento concentrico), setores maiores que 180 graus em canto vivo, continuacao reta entre eixos, sobra de eixo que passou do outro ignorada, raio reduzido automaticamente quando nao cabe (com aviso), encadeamento em polilinhas (quadras fechadas);
- preview ao lancar o eixo (jig): bordos, alinhamentos e esquinas que vao mudar aparecem a cada movimento do mouse; pontas capturadas no eixo vizinho (distancia configuravel);
- edicao associativa: eventos do DWG marcam a rede como pendente; atualizacao ao fim do comando (ou no Idle para a paleta de propriedades), so cria/apaga as linhas cuja geometria mudou; UNDO/REDO nao disparam atualizacao;
- testado fora do Civil 3D (cruz, T, Y, L, 5 ramos, eixos curvos e em arco, malha, nos proximos, angulos agudos, sobra de eixo); malha 15 x 15 (225 nos) recalculada em ~25 ms;
- compilacao e teste no Civil 3D pendentes.

## 2.22.0 - C3DQUADRORUAS v1.0.0 / quadro de ruas em .xlsx

- novo modulo `Modules/Quadros`: comando `C3DQUADRORUAS` [Adicionar/Listar/Remover/Exportar/Config] para atender ao item "quadro de ruas" (denominacao, dimensoes e coordenadas geograficas DMS);
- ruas guardadas no DWG (`C3D_QUADRO_RUAS_V1`), ligadas aos eixos; a exportacao rele a geometria atual;
- eixo: polilinha, linha, arco, spline, polilinha 3D, feature line ou alinhamento (alinhamento usa as proprias estacas); sentido do estaqueamento escolhido com marcador temporario [Manter/Inverter];
- nome: TEXT/MTEXT selecionado (relido na exportacao) ou digitado;
- largura da pista: cotas selecionadas (somadas, relidas na exportacao, gravadas como formula `=a+b`) ou valor/expressao digitada (ex.: `3.2*2`, gravada como formula);
- estaqueamento automatico pela extensao em planta (intervalo configuravel, padrao 20 m), estaca inicial em `E+m` ou metros;
- coordenadas geograficas de inicio e fim em DD°MM'SS.ss": pela geolocalizacao do desenho; senao, UTM pelo fuso do sistema de coordenadas do Civil 3D; senao, fuso informado (padrao 21S). Conversao UTM->geografica propria (GRS80/SIRGAS 2000), validada contra PROJ: erro maximo 0,18 mm no fuso 21; pontos da planilha modelo reproduzidos exatamente;
- .xlsx gerado sem dependencias externas (`StreetTableXlsx`), no layout da planilha modelo (colunas D..Q, mesclagens, bordas, formulas de extensao pelas estacas e area = largura x extensao), com linha de TOTAL e bloco de assinatura do responsavel tecnico opcionais, pagina A4 paisagem ajustada a largura;
- hemisferio exibido por formato de texto (`"S"@`, `"W"@`); no modelo original o formato `"S"General` nao aparecia em celulas de texto;
- compilacao e teste no Civil 3D pendentes.

## 2.21.0 - C3DSOLIDARRAY v1.3.0 / C3DSOLIDLIGAR v1.4.0 / conjuntos agrupados

- cada conjunto vira um GROUP nativo do AutoCAD: `C3DARRAY_xxxxxxxx` (array) e `C3DLIGAR_xxxxxxxx` (estais), com descricao (comando, material, diametro, quantidade);
- grupo e nao bloco: cada solido continua sendo um objeto proprio (property sets, tooltip, atualizacao no proprio objeto, apagar individual);
- membros sincronizados a cada criacao/edicao/atualizacao (`SaveSet`): apagados e desassociados (C3DSOLIDITEM) saem; recriados entram; grupo desfeito com UNGROUP e recriado na proxima atualizacao;
- grupo selecionavel: com PICKSTYLE ligado um clique seleciona o conjunto; Ctrl+H alterna para selecao individual;
- falha no agrupamento nunca cancela a operacao principal (`SolidSetGroup`);
- conjuntos existentes ganham o grupo na proxima edicao/atualizacao;
- compilacao e teste no Civil 3D pendentes.

## 2.20.0 - C3DOCULTARPLANTA v1.5.0 / sem redesenho do proprio objeto (crash na orbita)

- causa provavel do crash na orbita (nos modos Ocultar e Exato): fora da planta o overrule redesenhava o proprio solido de dentro do callback de desenho dele (`Geometry.Draw(self)`); com a vista mudando a cada quadro, o cache grafico nativo podia entrar em ciclo. Esse redesenho foi eliminado:
  - estilos visuais 3D (qualquer regeneracao diferente de 2D Wireframe): o plugin nao interfere, o solido e desenhado nativamente e a orbita fica tao leve quanto sem o plugin;
  - 2D Wireframe em planta: oculto (Ocultar) ou arestas visiveis (Exato), como antes;
  - 2D Wireframe fora da planta: arestas do solido desenhadas a partir de um cache em memoria (montado no Idle a partir do Brep);
  - insercoes de bloco: conteudo desenhado pelo proprio AutoCAD; o plugin so acrescenta as linhas de planta; os solidos internos se tratam sozinhos (alvo pelo proprio layer ou pela insercao em layer-alvo, via `PlanLayerIndex`);
- a atualizacao automatica e a montagem de caches nunca rodam durante interacao: exige editor ocioso, `CMDACTIVE = 0` e nenhum botao do mouse pressionado (o log mostrava uma atualizacao com Regen no meio da orbita);
- durante o arraste (MOVE/COPY) em 2D Wireframe os solidos-alvo nao aparecem na pre-visualizacao;
- compilacao e teste no Civil 3D pendentes.

## 2.19.0 - C3DOCULTARPLANTA v1.4.0 / correcao de crash na orbita 3D

- corrige fechamento do Civil 3D ao orbitar o modelo com a funcao ligada. O overrule roda dentro do motor grafico nativo, e tres pontos eram frageis nesse cenario:
  - excecoes dentro de SetAttributes/WorldDraw/ViewportDraw podiam escapar para o codigo nativo; agora todo callback e protegido e, em erro, o objeto simplesmente nao e desenhado naquela vista;
  - o filtro lia `entity.Layer`, que abre a tabela de layers do DWG durante o desenho (inseguro nas threads de graficos). Agora usa `LayerId` contra um indice de layers (`PlanLayerIndex`) montado fora da regeneracao e mantido pelos eventos de layer (criar/renomear/apagar) e de abertura/fechamento de desenhos;
  - a trava de recursao do "desenhar normalmente" era por thread; agora e por objeto nativo e vale entre threads;
- linhas da representacao desenhadas como segmentos (`WorldLine`);
- compilacao e teste no Civil 3D pendentes.

## 2.18.0 - C3DOCULTARPLANTA v1.3.0 / correcao de travamento ao trocar layers

- corrige travamento ao trocar o layer de varios solidos com o modo Exato ativo:
  - a regiao incremental era UMA caixa envolvendo todas as alteracoes; com solidos espalhados (ex.: ao longo do corredor) virava o desenho inteiro e recalculava tudo, sem possibilidade de cancelar. Agora cada alteracao tem a propria caixa e so os vizinhos reais entram;
  - a regeneracao da tela nao abre mais o DWG: o overrule le apenas o cache em memoria, carregado no Idle (antes, uma transacao era aberta durante o desenho a cada solido ainda nao lido);
- atualizacao automatica com limite: acima de 300 solidos envolvidos ela e adiada com aviso (pendentes aparecem completos ate `C3DPLANTAATUALIZAR`);
- ESC cancela qualquer calculo (manual ou automatico), com barra de progresso; nada e gravado ao cancelar;
- Idle so atua com o editor ocioso (`IsQuiescent`) e regenera dentro do bloqueio do documento;
- solidos cujo layer sai dos prefixos tem a representacao removida (antes ficava gravada);
- malha com tolerancia proporcional ao tamanho do solido (limita triangulos em lofts/sweeps grandes);
- segmentos longos processados em trechos do tamanho da grade (consultas locais); resultado identico ao anterior no teste de 3000 solidos;
- compilacao e teste no Civil 3D pendentes.

## 2.17.0 - C3DOCULTARPLANTA v1.2.0 / solidos dentro de blocos

- insercoes de bloco (BlockReference) que contem Solid3d/Body, inclusive em blocos aninhados, passam a ser alvos;
- cada insercao tem a propria representacao (mesmo bloco em lugares diferentes tem vizinhos diferentes); os solidos internos entram com a transformacao acumulada da insercao, e as silhuetas sao calculadas ja em WCS;
- filtro: entram todos os solidos do bloco se o layer da insercao casar com os prefixos; senao, so os solidos internos cujo layer casa;
- em planta o bloco desenha as linhas visiveis dos solidos e o restante do conteudo (linhas, textos, atributos) normalmente, com os solidos internos suprimidos; no modo Ocultar, idem sem as linhas;
- mover/copiar/apagar insercoes no model space dispara a atualizacao automatica; editar a definicao do bloco (BEDIT/REFEDIT) pede `C3DPLANTAATUALIZAR`;
- compilacao e teste no Civil 3D pendentes.

## 2.16.0 - C3DOCULTARPLANTA v1.1.0 / modo Exato: linhas ocultas em 2D Wireframe

- novo modo `Exato`: em planta os solidos aparecem so com as arestas visiveis de cima, com remocao de linhas ocultas entre TODOS os solidos-alvo, continuando em 2D Wireframe; como o viewport do layout usa o mesmo desenho, a prancha plota igual;
- motor proprio (`PlanHiddenLineEngine`): Brep -> malha (Mesh2d) -> grade XY de triangulos; candidatas = arestas do Brep + silhuetas da malha (cilindros/curvas); cada segmento e cortado nos cruzamentos com arestas de triangulos e nos atravessamentos de plano, com teste do ponto medio (exato nas transicoes);
- representacao gravada no DWG (`C3D_PLANREP_V1` no NOD, uma Xrecord por handle), sem modificar os solidos; cache em memoria por desenho;
- solidos criados/alterados/apagados ficam pendentes (aparecem completos em planta ate atualizar); com `Auto` ligado, ao ficar ocioso o plugin recalcula os pendentes e os vizinhos da regiao afetada e redesenha;
- prefixo `*` = todos os solidos e bodies;
- `C3DOCULTARPLANTA` ganhou [Modo/Auto/Atualizar]; novo `C3DPLANTAATUALIZAR` (recalculo completo);
- modo Pintor (mascara por cota) avaliado e descartado: a mascara nao tem como acompanhar a cor de fundo da tela e do papel;
- compilacao e teste no Civil 3D pendentes.

## 2.15.0 - C3DOCULTARPLANTA v1.0.0 / solidos so no 3D

- novo modulo `CorridorTools/PlanVisibility`: `DrawableOverrule` para `Solid3d` e `Body` (links extraidos do corredor saem como Body);
- objetos cujo layer comeca com um dos prefixos configurados nao sao desenhados em viewports com camera olhando para baixo (planta, tolerancia ~1 grau) e sao desenhados normalmente em qualquer vista 3D;
- decisao por viewport: planta e 3D lado a lado no model space funcionam ao mesmo tempo; vale tambem para viewports de layout e plotagem;
- nao altera o DWG: sem o plugin (ou em Navisworks/IFC) os solidos aparecem normalmente;
- comando `C3DOCULTARPLANTA` [Ligar/Desligar/Prefixos]; varios prefixos separados por `;`; configuracao salva no registro do usuario (HKCU) e reativada ao carregar o plugin;
- os objetos filtrados deixam de usar o cache grafico do AutoCAD (desenho dependente da vista): em modelos muito grandes a orbita 3D pode ficar mais lenta;
- compilacao e teste no Civil 3D pendentes.

## 2.14.0 - C3DSOLIDARRAY v1.2.0 / atualizar somente solidos existentes

- `C3DSOLIDATUALIZAR` pergunta `[Existentes/Recriar] <Existentes>`;
- Existentes: cada solido restante e reposicionado no proprio objeto. O solido novo e gerado do modelo e assume, via `SwapIdWith`, o handle, o xdata e o dicionario de extensao (tag do array e Property Sets) do antigo; layer, cor, material etc. sao copiados com `SetPropertiesFrom`. Solidos apagados manualmente continuam fora;
- Recriar: comportamento anterior (apaga e recria; apagados voltam);
- se a feature line mudar a quantidade de vertices (rebuild do Corridor), a numeracao dos itens deixa de corresponder e o conjunto e recriado com aviso. A contagem passa a ser gravada no conjunto (`#POINTCOUNT#`); conjuntos antigos sem contagem sao atualizados por indice na primeira vez;
- `C3DSOLIDEDITAR` (offsets gerais) atualiza somente os existentes;
- `C3DSOLIDITEM` (Editar/Resetar/Reassociar) mantem o mesmo objeto em vez de apagar e criar;
- validacao antes de alterar: layer travado cancela a atualizacao do conjunto sem mudancas;
- corrige leitura de conjuntos v1.0 com solido apagado em `EnsureLegacyItemTags`;
- compilacao e teste no Civil 3D pendentes.

## 2.13.0 - C3DSOLIDLIGAR v1.3.0 / tooltip dos estais

- ao passar o cursor sobre um estai, o tooltip do AutoCAD mostra os principais campos do Property Set `C3D_ESTAI`: codigo, material, diametro, area metalica, comprimento, massa, inclinacao e cargas de escoamento/ruptura;
- implementado em `C3DSolidLinkTooltip.cs` via `Editor.PointMonitor`, registrado em todos os documentos abertos/novos a partir do `PluginApplication` (sem novo `IExtensionApplication`);
- leitura por `OpenCloseTransaction`, cache do ultimo estai sob o cursor limpo ao fim de cada comando; falhas sao silenciosas;
- novo comando `C3DSOLIDLIGARTOOLTIP` [Ligar/Desligar] (ligado por padrao a cada sessao);
- compilacao e teste no Civil 3D pendentes.

## 2.12.0 - C3DSOLIDLIGAR v1.2.0 / atualizar somente estais existentes

- `C3DSOLIDLIGARATUALIZAR` pergunta `[Existentes/Recriar] <Existentes>`;
- Existentes: a geometria de cada estai restante e refeita no proprio Solid3d (CreateFrustum substitui o corpo), preservando handle, cor e demais edicoes manuais; estais apagados manualmente nao sao recriados e o resumo informa quantos ficaram fora;
- Recriar: comportamento anterior, apaga e recria todos (os apagados voltam);
- `C3DSOLIDLIGAREDITAR` com o mesmo N atualiza somente os existentes (e aplica o layer escolhido); com N diferente avisa que a numeracao muda e recria o conjunto;
- Property Set `C3D_ESTAI` atualizado no mesmo objeto, mantendo o codigo E01... de cada posicao;
- em ATUALIZAR > Existentes o layer de cada estai nao e alterado (mudancas manuais de layer sao respeitadas);
- compilacao e teste no Civil 3D pendentes.

## 2.11.0 - C3DSOLIDLIGAR v1.1.0 / descontos nas pontas, material, layer e Property Set

- desconto percentual no inicio e no fim de cada linha, separado por linha (Enter na linha 2 repete a linha 1); os N pontos ficam no trecho util restante, incluindo seus extremos;
- material por catalogo (CP190, A416, CA50, ST85, INOX) ou `Outro` com nome, densidade, fyk, fuk e E; valores do catalogo sao nominais de referencia;
- area metalica efetiva por estai em cm2 (Enter/0 = pi.D2/4 do solido); para cordoalhas informe a area nominal real;
- layer proprio (padrao `C3D-ESTAIS`, ciano), criado se nao existir; atualizacao bloqueada com aviso se o layer estiver travado;
- Property Set AEC `C3D_ESTAI` em cada estai: codigo (E01...), material, diametro, area, comprimento, massa linear, massa, densidade, fyk, fuk, E, cargas de escoamento/ruptura, inclinacao, cotas, pontos de ancoragem, descontos e handles das linhas;
- conversao de unidades por INSUNITS (indefinido = metro); resumo com comprimento e massa totais ao final;
- falha de Property Set nao cancela a geometria (mesmo padrao do C3D_PASSAGEM);
- conjuntos e padroes da v1.0.0 continuam legiveis (registro V2 com leitura do V1);
- novo codigo em `C3DSolidLinkMaterials.cs` e `C3DSolidLinkPropertySet.cs`; compilacao e teste no Civil 3D pendentes.

## 2.10.0 - C3DSOLIDLIGAR v1.0.0 / barras tubulares entre duas linhas

- novo comando `C3DSOLIDLIGAR` no modulo `CorridorTools/SolidArray` (classe separada `C3DSolidLinkCommands`; `C3DSolidArrayCommands` nao foi alterado);
- seleciona duas linhas quaisquer: Line, Arc, Polyline, Polyline 2D/3D, Spline, Feature Line ou AutoCorridorFeatureLine; Alignments e demais objetos Civil 3D sao recusados;
- N pontos igualmente espacados pelo comprimento 3D de cada linha, incluindo inicio e fim; ponto i de uma linha liga ao ponto i da outra;
- previa temporaria das ligacoes (Transient Graphics, primeira ligacao em vermelho) com opcao `[Inverter/Aceitar]`; o sentido inicial sugerido evita barras cruzadas;
- um `Solid3d` por barra, cilindro de diametro informado com eixo entre os dois pontos; ligacoes de comprimento nulo sao ignoradas e avisadas;
- Feature Lines: usa a API de Curve somente se ela passar pelos vertices 3D (com cotas); caso contrario usa a polilinha 3D dos vertices;
- conjunto persistido no DWG (`C3D_SOLID_LIGAR_V1`) com handles das linhas, N, diametro e sentido; ultimos N/diametro viram padrao do desenho;
- `C3DSOLIDLIGAREDITAR` (N, diametro, sentido), `C3DSOLIDLIGARATUALIZAR` (selecao ou Todos) e `C3DSOLIDLIGARHELP`;
- cadastrado em `CommandNames`, `CommandRegistration`, `CommandCatalog`, `PluginVersions` e Ribbon (icone `LG`);
- compilacao e teste no Civil 3D 2026.2 pendentes.

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
