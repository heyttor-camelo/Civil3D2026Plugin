## 2.45.0 - Visualizador de secao v0.2.0 / comparar, vista 3D, destacar e hachurar, contorno do corredor

- **Comparar secoes** (categoria nova): botao **Fixar na comparacao** guarda a secao mostrada (corte ou estaca); as fixadas aparecem sobrepostas, cada uma com sua cor e legenda, alinhadas pelo **eixo** (estacas), pelo **centro** ou pelo **inicio**. Duplo clique mostra/oculta; botao direito: so esta, todas, cor, remover. Nas Propriedades, a **diferenca de topo** entre as duas primeiras visiveis (maior e media, a cada 5 cm). As fixadas sao copias: nao sao recalculadas;
- **Vista 3D** (categoria nova): isometrica dentro da janela, camera ortografica, com superficies amostradas numa grade alinhada a caixa (TIN: uma amostragem por linha), o corredor entre as estacas que tocam a caixa, tubos, estruturas, feature lines, polilinhas 3D e pontos, o plano do corte translucido e o contorno da caixa. Arrastar orbita, botao direito move, roda aproxima, duplo clique volta a isometrica; exagero vertical vale tambem. So e montada com a categoria aberta;
- **destacar / hachurar / ocultar** qualquer item: botao direito na vista ou na lista (ou botoes nas Propriedades). Hachura nas regioes fechadas do item (contorno do corredor, solidos, estruturas, tubos). A escolha fica guardada por item e vale tambem no desenho exportado (destaque = espessura 0,50, hachura ANSI31);
- **corredor so pelo contorno (boundary)**: na aba Conteudo, corredor com todos os links, so o contorno (envoltoria superior e inferior, com as paredes do meio-fio) ou so os links com os codigos dados (ex.: TOP, DATUM). O contorno e sempre calculado, entao o corredor pode ser hachurado em qualquer modo;
- **textos na exportacao**: altura pelo papel - escala do desenho 1:N e texto em mm (padrao 1:100, 2 mm); o passo das cotas e das distancias se ajusta para os textos nao encavalarem; rotulos de pontos e linhas sobem quando colidem. `C3DSECAODESENHAR` aceita [Escala/Texto/eXagero] e, na comparacao, desenha as secoes visiveis sobrepostas com legenda;
- **desempenho da vista**: cada item vira uma geometria congelada em coordenadas do mundo, posicionada por transformacao (zoom e pan nao refazem a geometria); grade num unico traco. A janela roda na mesma thread do Civil, entao desenhar menos objetos WPF tambem deixa o desenho livre mais rapido.
## 2.44.3 - MFREBAIXO v1.7.2 / menu Textos abre com um clique

- o menu **Textos ▾** abria no apertar do botao e o soltar (fora do menu) ja o fechava; agora abre ao soltar e fica aberto ate clicar fora. Clicar de novo em Textos fecha.

## 2.44.2 - MFREBAIXO v1.7.1 / escolher os textos da vista

- botao **Textos ▾** na barra da vista do painel: liga/desliga cada texto - etiqueta do rebaixo (codigo, nome, estacas, em todos ou so no selecionado, linha de chamada), planta (nome do corredor, nome das baselines, marcos S0-S3, legenda, escala) e barra de baixo (resumo, o que esta sob o cursor). Atalhos Tudo / Nada / Padrao; a escolha fica salva;
- por padrao as **estacas nao aparecem mais** na etiqueta nem na leitura do cursor (eram os numeros que pareciam coordenadas); o rebaixo sem nome mostra so o codigo.

## 2.43.1 - Visualizador de secao v0.1.1 / desempenho

- **corredores lidos uma vez**: a leitura estaca a estaca (a parte cara) fica em cache e so e refeita quando o corredor muda (rebuild); antes era refeita a cada grip ou mudanca de profundidade;
- **objetos nao sao reabertos**: tipo e caixa envolvente de cada objeto do model space ficam guardados; nos recalculos seguintes so abre o que esta perto da caixa (objetos modificados/apagados saem do cache). Superficies tambem passam pelo filtro de extents;
- **sem REGEN**: criar a caixa e mudar frente/atras/cotas nao regenera mais o desenho inteiro (a polilinha alterada ja e redesenhada); a lista de cortes e atualizada so para as polilinhas que mudaram, sem varrer o model space;
- o diagnostico mostra quantos objetos foram abertos e quantos descartados pela caixa.

## 2.44.0 - MFREBAIXO v1.7.0 / painel sem 3D, planta geral com nomes

- **saiu a vista 3D** do painel (`MFREBAIXOPAINEL`) e as cameras no desenho (Camera 3D / Vista de cima); ficam so as plantas: geral e do rebaixo;
- **rebaixos com codigo e nome**: cada rebaixo ganha um codigo (R01, R02... por corredor, baseline e estaca) e pode receber um **nome** na aba Propriedades (botao Salvar nome). O nome vai no comentario do Transition Set ("REBAIXO MF | nome") e e mantido ao editar;
- **planta geral reorganizada**: etiqueta colorida em cada rebaixo com codigo + nome (clicavel, com linha de chamada), rotulos sem sobreposicao, nome do corredor ao longo do eixo (nome da baseline ao aproximar), corredor selecionado em destaque, legenda e escala com espaco reservado;
- a barra de baixo mostra **o que esta sob o cursor** (rebaixo / corredor, ou estaca e trecho na planta do rebaixo) em vez de coordenadas E/N;
- lista e avisos identificam o rebaixo pelo codigo e nome ("R03 Garagem 12 (Corredor)"), com colunas Rebaixo e Estacas.

## 2.43.0 - Visualizador de secao v0.1.0 (C3DSECAO) - primeira versao

- **C3DSECAO**: janela propria, no molde do explorador do viario (categorias, barra de icones, vista grafica escura, lista e abas), que mostra a secao de uma **caixa de corte** sem ocupar a viewport: pode ficar em outro monitor enquanto o desenho segue livre;
- **caixa de corte** (`C3DSECAOCORTE`, botao Nova caixa): linha em planta por pontos ou polilinha existente (um ou mais trechos) + profundidade a frente e atras + faixa de cotas (automatica ou fixa) + exagero vertical. Fica numa polilinha na layer SECAO-CORTE com os parametros em XData; em planta aparece o contorno da caixa (tracejado) e uma seta no sentido de quem olha; mover os grips refaz a secao quando o AutoCAD fica livre. Inverter sentido, excluir, "Cotas da vista atual";
- **o que entra no corte** (traco cheio): superficies (amostradas ao longo da linha), **corredores** (fitas entre estacas vizinhas cortadas pelo plano, interpolacao igual a da superficie do corredor), tubos (circulo externo/interno onde o eixo cruza), solidos 3D (secao pelo plano), feature lines e polilinhas 3D (marca onde cruzam);
- **o que esta na profundidade** (fantasmas, tracejado apagado): estacas do corredor dentro da caixa (links projetados), tubos e estruturas, feature lines, polilinhas 3D e pontos COGO; cada classe liga/desliga na aba Conteudo, superficies uma a uma na categoria Configuracao;
- **Corredor por estaca**: lista das regioes (corredor/baseline/regiao/estacas); ao selecionar, a linha de corte vira a perpendicular ao eixo na estaca e a janela funciona como o viewport do Section Editor (setas e slider trocam de estaca, vizinhas na profundidade como fantasma, parametros e subassemblies nas Propriedades);
- vista com zoom na roda, pan arrastando, duplo clique enquadra, reguas de cota e distancia, grade, rotulos, leitura S/Z e do objeto sob o cursor; conteudo em lista (tipo, nome, layer, cortado/na profundidade, afastamento) com propriedades do item;
- **C3DSECAODESENHAR** (botao Desenhar): leva a secao mostrada para o model space como polilinhas 2D (moldura, cotas, distancias, exagero), na layer SECAO-DESENHO;
- limites desta versao: entre estacas o corredor e interpolado linearmente; solidos nao entram como fantasma; a vista e de consulta (nao edita o corredor).
## 2.42.2 - MFREBAIXO v1.6.2 / remover = desligar o rebaixo

- confirmado no Civil 3D: a API nao apaga Transition Set (`SetTransitions` e `ImportTransitions` so acrescentam/atualizam pelo nome). Remover no painel agora **desliga** o rebaixo: os dois sets ficam com o mesmo nome, as mesmas estacas e valores neutros (REB = 0, DESTAQUE = 0; no rebaixo antigo por AMF_LIVRE, altura constante), comentario "REMOVIDO"; as secoes criadas saem. Se o Civil nao aceitar a troca, nada e gravado;
- o painel nao lista mais os sets desligados e avisa quantos ha para apagar a mao no Edit Corridor Transition;
- saiu a tentativa pelo CSV (Export/ImportTransitions), que tambem nao apaga.

## 2.42.1 - MFREBAIXO v1.6.1 / remover rebaixo pelo CSV das transitions

- o `SetTransitions` do Civil so acrescenta/atualiza sets (nao apaga). Para remover, o painel agora exporta as transitions da baseline em CSV, tira as linhas do rebaixo e importa de volta (`Baseline.ExportTransitions/ImportTransitions`, por reflexao). Os CSVs ficam em `%TEMP%\MFREBAIXO`;
- trava de seguranca: se depois disso a quantidade dos outros Transition Sets mudar, nada e gravado (a transacao e desfeita);
- se nao der, a mensagem aponta o caminho manual: Edit Corridor Transition.

## 2.42.0 - MFREBAIXO v1.6.0 / planta geral, cameras e remover rebaixo

- **Planta geral** no painel (`MFREBAIXOPAINEL`): baselines de todos os corredores e todos os rebaixos (vermelho; laranja com aviso; amarelo o selecionado). Clique num rebaixo seleciona, duplo clique abre a planta do rebaixo. Botoes de modo: Planta geral / Planta do rebaixo / 3D;
- **Cameras**: no 3D do painel, "da rua" e "da calcada" (olho a 1,6 m olhando o rebaixo) e "orbita"; no desenho, **Camera 3D no desenho** poe a vista em perspectiva (estilo Realistic) olhando o rebaixo, e **Vista de cima** volta a vista anterior;
- **Remover rebaixo**: se o Civil mantiver o Transition Set depois do `SetTransitions`, o painel refaz a lista com copias dos sets que ficam (como o CORRSPLIT). A mensagem diz quantos sets e secoes sairam; se ainda assim nao sair, explica e aponta o Corridor Transitions.

## 2.41.0 - PEDs v1.3.0 / ficha em multileader, tudo nativo do AutoCAD

- a ficha (tabela) com a **foto e o mapa** passa a ser um bloco por PED (`PED_FICHA_código`) usado como conteúdo de um **multileader** com a seta na borda do símbolo: arrastar o grip move a ficha e a linha de chamada acompanha. Reimportar refaz o conteúdo mas mantém onde a ficha foi deixada;
- **layers por PED**: `PED-FICHA-código` (multileader + tabela) e `PED-FOTO-código` (imagens). Esconder um PED: LAYOFF/LAYFRZ clicando na ficha; todos: filtros de layer "PED - fichas" e "PED - fotos" (criados pelo plugin). Nada disso precisa do plugin;
- o bloco `PED` volta a ser só o símbolo (com os dados em atributos invisíveis); o da 1.2 (tabela dentro) é redesenhado e as imagens soltas/grupos da 1.2 são removidos no próximo `PEDIMPORTAR`/`PEDATUALIZAR`;
- `PEDVISTA` agora liga/desliga as layers de cada PED; `PEDLOCAR` leva a seta da ficha junto ao mover/girar o símbolo;
- sai o `PEDMODELO` (não há mais bloco dinâmico).

## 2.40.0 - MFREBAIXO v1.5.0 / painel de rebaixos (MFREBAIXOPAINEL)

- **`MFREBAIXOPAINEL`**: explorador dos rebaixos no molde do painel viario (mesmo tema claro/escuro):
  - categorias **Rebaixos**, **Corredores**, **Avisos (NBR 9050)** e **Configuracao**, com contagem e Localizar;
  - barra de comandos: novo rebaixo, editar, remover, zoom no desenho, secoes, selecionar corredor, Rebuild, atualizar, ajuda;
  - vista grafica em **planta** (faixas do corredor entre secoes, coloridas pelos codes como a hachura: REBAIXO, meio-fio, sarjeta, calcada, pista; marcos S0..S3, baseline, escala, zoom/arrasto) ou **3D** (malha do trecho com exagero vertical 1x/2x/5x, girar/arrastar/zoom) com trecho de +3/+6/+15 m;
  - aba **Propriedades**: dados do rebaixo e edicao de S1, S2 e altura livre final com reaplicacao (o antigo e removido; se a nova falhar, o antigo volta);
  - aba **Secoes**: REB, DESTAQUE, altura exposta, AMF_LIVRE, Rampa OK e faixa livre em cada secao do trecho;
  - aba **Avisos**: rampa > 8,33%, rebaixo < 1,50 m, desnivel final, rampa da calcada que nao cabe, faixa livre < 1,20 m, rebaixos sobrepostos, falta do destaque, rebaixo antigo por AMF_LIVRE;
  - atualiza sozinho quando MFREBAIXO, UNDO/REDO ou comandos de corredor terminam;
- remover rebaixo tira os dois Transition Sets e as secoes que o comando criou;
- o calculo do rebaixo foi separado num nucleo (`ApplyRebaixoCore`) usado pelo comando e pelo painel.

## 2.39.0 - PEDs v1.2.0 / ficha em tabela no bloco, foto e mapa, PEDVISTA e PEDMODELO

- o bloco `PED` gerado passa a ter a **ficha em tabela** ao lado do símbolo, no arranjo da aba "Ficha para desenhos" (cabeçalho, larguras atual x proposta com total, informações complementares, embarques, distâncias, obs). Linhas e textos na layer `PED-FICHA`; os valores são atributos (continuam legíveis sem o plugin). Blocos `PED` da 1.0/1.1 são redesenhados e os inseridos ressincronizados no próximo `PEDIMPORTAR`/`PEDATUALIZAR`;
- **foto do local e mapa** como imagens raster ao lado da ficha (layer `PED-FOTO`), com XData do PED e num grupo `PED_código` junto com o bloco (mover o bloco leva as imagens). `PEDLOCAR` recoloca as imagens ao mover/girar;
- `PEDVISTA`: Ponto / Ficha / Foto para todos ou os selecionados. A tabela troca pelo estado de visibilidade do bloco dinâmico (a API .NET não cria parâmetros dinâmicos: a visibilidade é criada uma vez no BEDIT); as imagens ligam/desligam por objeto;
- `PEDMODELO`: guarda o bloco `PED` do desenho (já com a visibilidade) em `%APPDATA%\Civil3D2026Plugin\PED_modelo.dwg`; desenhos sem o bloco recebem o modelo no `PEDIMPORTAR`/`PEDLOCAR`. Bloco dinâmico nunca é redesenhado pelo plugin.

## 2.38.0 - PEDs v1.1.0 / dados da ficha dentro do bloco

- o bloco `PED` ganha **atributos invisíveis** com os campos da ficha (via, referência, quadra, sentido, larguras atual/proposta de cada componente e total, abrigo, faixa exclusiva, ciclovia, alterações, embarques PM/PT, PED anterior/posterior e distâncias, obs, avaliação, trecho, escalonado, remanejamento, arquivos da foto e do mapa). Quem abrir o DWG **sem o plugin** vê tudo no Properties, no duplo clique (EATTEDIT), no ATTOUT e na Data Extraction;
- **hyperlink** no bloco para a foto do local (Ctrl+clique abre). O caminho é relativo ao DWG (`PEDs_img\...`) quando a pasta está ao lado dele: copiar o DWG junto com a pasta `PEDs_img` mantém o link;
- `PEDIMPORTAR`/`PEDATUALIZAR` criam os atributos que faltarem na definição do bloco (inclusive num bloco `PED` próprio do desenho) e completam os blocos já inseridos; `PEDLOCAR` preenche o bloco novo.

## 2.37.2 - MFREBAIXO v1.4.3 / MFSARJETA v1.21

- MFSARJETA v1.21: o fundo do piso da calçada passa a ter o code `Calcada_Fundo` (antes `Calcada`). Em planta ele fica embaixo da rampa e a hachura do `Calcada` cobria a do `REBAIXO`;

- seções extras a +-1 mm de S0 e S3 (antes 2 cm); `DESTAQUE` começa 0,5 mm antes de S0.

## 2.37.1 - MFREBAIXO v1.4.2 / seções a +-2 cm de S0 e S3

- o comando cria seções extras 2 cm antes e depois de S0 e de S3. A troca de code (DESTAQUE e rampa da calçada) fica num trecho de 2 cm, então a hachura em planta começa e termina em S0 e S3, qualquer que seja a seção que o Civil 3D usa para colorir a faixa;
- `subassemblies/MFSARJETA`: PKT v1.20 (volta a tirar o `Calcada` do link da rampa no rebaixo, para o `REBAIXO` ficar por cima).

## 2.37.0 - PEDs v1.0.0 / locação de PEDs com ficha ao clicar

Módulo novo `Modules/PEDs` (categoria **PEDs** na Ribbon), para a planilha de proposta de PEDs (BRT) e o KMZ com os pontos:

- `PEDIMPORTAR`: lê a planilha (.xlsx, aba com cabeçalho "Código" na coluna A, valores em cache das fórmulas) e o KMZ/KML. Grava os dados no DWG (NOD `C3D_PED_V1`), extrai as imagens "dentro da célula" (foto do local e mapa) para `PEDs_img\` ao lado do DWG em JPEG reduzido, e insere o bloco `PED` (8 x 3, hachura 45°, texto "PED XX" → "PED TC01") na layer `PED` em cada ponto. Lon/lat vão para o desenho pela geolocalização, pelo sistema de coordenadas do Civil 3D (fuso UTM) ou por fuso informado;
- `PEDFICHA`: janela que acompanha a seleção: clicar num bloco PED mostra a ficha no arranjo da aba "Ficha para desenhos" (larguras atual x proposta, abrigo, faixa exclusiva, ciclovia, alterações, embarques, PED anterior/posterior, obs), a avaliação, a foto e o mapa; aba "Todos os campos" com as ~60 colunas; zoom e abrir imagem;
- `PEDATUALIZAR`: relê a planilha gravada (nova revisão) sem mover os blocos;
- `PEDLOCAR`: insere ou move um PED clicando (com rotação opcional);
- `GeoConversion.GeographicToUtm` (lat/lon → UTM, Snyder/GRS80).

Se o DWG já tiver um bloco `PED`, ele é usado: atributos com "XX" no valor padrão recebem o código.

## 2.36.2 - MFSARJETA v1.16/v1.17 / hachura do rebaixo nas abas e na rampa da calçada

- v1.19: desfaz a v1.17 (o link da rampa volta a ter sempre `Calcada`). Trocar o code de um link entre seções muda a ordem dos links com aquele code e a planta liga links diferentes (gravata). Só os links de sobreposição variam de code;
- v1.18: a rampa da calçada (e o resto dela) nunca fica com menos de 5 cm. Com 1 mm fora do rebaixo, o link sumia na planta e a ligação entre S0/S3 e a seção vizinha saía trocada (gravata na hachura);
- v1.17: no rebaixo, o link da rampa da calçada perde o code `Calcada` (a hachura da calçada era desenhada por cima da do `REBAIXO`); continua com `Top`/`TOPO`.

- o berço passa a ser gerado por último no PKT. Ele muda a quantidade de links quando o MF desce (camada dentro/abaixo do MF); vindo antes, os links da calçada e de sobreposição mudavam de posição entre S0 e S1, e em planta o Civil 3D ligava links diferentes entre as duas seções (hachura errada nas abas, mesmo com o 3D certo).

## 2.36.1 - MFREBAIXO v1.4.1 / aba de entrada sem hachura

- na estaca exata em que a Transition começa, o Civil 3D usa o valor de antes: a seção S0 ficava com `DESTAQUE` = 0 e o trecho S0-S1 (aba de entrada do MF, sarjeta e rampa da calçada) não recebia o code `REBAIXO`. O `DESTAQUE` agora começa 1 cm antes de S0.

## 2.36.0 - MFREBAIXO v1.4.0 / calçada com rampa no MFSARJETA

- `subassemblies/MFSARJETA`: PKT v1.15 (a 1.14 não compilava no Composer: IF misturando Grade e Double):
  - **calçada** no mesmo PKT (`Calçada` = Yes): largura, declividade, espessura do piso e da base, alvos opcionais de offset e cota na borda externa. A borda externa fica na cota normal; no rebaixo, a rampa sai do topo rebaixado do MF com a declividade máxima (`iRampa`) até encontrar a calçada normal. Saídas `Rampa OK` e `Faixa livre`;
  - parâmetro **`REB`** (profundidade do rebaixo): o MF desce `REB` a partir da altura normal, e a calçada sabe onde fica o topo normal;
  - code `REBAIXO` agora vai em **links de sobreposição** (criados por último, por cima do topo da sarjeta, do MF e da rampa da calçada) e nos **shapes** da sarjeta, do MF e da rampa da calçada. Os links originais não levam mais o `REBAIXO`;
- MFREBAIXO: com o PKT 1.14+ (parâmetro `REB`) grava a Transition na profundidade (0 -> d -> d -> 0) em vez da altura livre. PKTs antigos continuam pela altura livre.

## 2.35.3 - MFREBAIXO v1.3.3 / um code só para o rebaixo

- `subassemblies/MFSARJETA`: PKT v1.13. Volta o `DESTAQUE` numérico (o `CODE_REB` de texto da v1.12 saiu) e agora há **um code só**, `REBAIXO`, no topo do MF e da sarjeta, de S0 a S3: exatamente o mesmo intervalo da geometria (rampa de entrada + trecho rebaixado + rampa de saída);
- MFREBAIXO: mensagens atualizadas (as Transitions continuam as mesmas).

## 2.35.2 - MFREBAIXO v1.3.2 / lado da MF automático em assembly de um lado só

- no modo AUTO, se a Assembly da Region só tem MF de um lado (caso da baseline de feature line do meio-fio), o comando usa esse lado, mesmo com o clique do outro lado ou em cima da baseline. Antes dava "Não foi encontrada a subassembly MF ... no lado ESQUERDO";
- quando a MF não é achada, a mensagem lista as subassemblies da Assembly com classe, lado e se foram reconhecidas como MF;
- `subassemblies/MFSARJETA`: PKT v1.12 (Kassel com dimensões fixas; o `DESTAQUE` numérico virou o parâmetro de texto `CODE_REB`: o texto digitado é um code único nos links do topo do MF e da sarjeta).

## 2.35.1 - MFREBAIXO v1.3.1 / baselines de feature line

- o clique passa a achar estaca e offset também em **baselines de feature line** (antes eram ignoradas e o comando dizia "Não foi encontrada Baseline/Region válida"). A estaca vem da inversão numérica de `Baseline.StationOffsetElevationToXYZ` (varredura de ~0,5 m + refino), a mesma função que o CorridorSplit já usa nesse tipo de baseline; offset positivo à direita do sentido da baseline;
- a identidade da baseline entre o 1º e o 2º clique deixou de depender do `AlignmentId` (que feature line não tem).

## 2.35.0 - MFREBAIXO v1.3.0 / suporte ao MFSARJETA e destaque do rebaixo em planta

- **MFREBAIXO reconhece o MFSARJETA** (classe `Subassembly.MFSARJETA`, ou pela assinatura AMF_LIVRE/LS/ESar/EMB) além do PKT antigo `Subassembly.MF`;
- altura livre nominal: `AMF_LIVRE_NORMAL` quando existe; sem ele, o valor de projeto de `AMF_LIVRE`. Altura efetiva também pela saída `EXPOSTO`;
- sem `iRampa` na subassembly, as rampas do MF usam 8,33% (com aviso);
- **destaque em planta**: se a subassembly tiver o parâmetro `DESTAQUE`, o comando cria um segundo Corridor Transition (1->2 aba de entrada, 2->3 rebaixo pleno, 3->4 aba de saída) nas mesmas estacas S0..S3. As faixas crescem sempre, então a estaca de emenda já recebe o code do trecho que começa e a hachura não atrasa uma seção;
- `subassemblies/MFSARJETA`: PKT v1.10 (parâmetros `DESTAQUE` e `iRampa`; codes `MF_ABA`/`MF_REB`/`SJ_ABA`/`SJ_REB` nos links do topo) e o gerador em Python.

## 2.34.3 - C3DVIARIO v0.7.11 / editar o arco do eixo concordante; botao Concordancia com desvincular

- **arco manda quando e ele que foi editado**: num eixo com concordancia vinculada, arrastar o grip do meio do arco (ou mudar o bulge por PEDIT/propriedades) era desfeito - a cadeia refazia o arco pelas tangentes. Agora, quando so o arco mudou (pontas e demais vertices no lugar), o arco vira a concordancia entre as duas retas vizinhas com o raio que poe o meio do arco onde ele foi solto: as retas mantem a direcao (mesmo PI) e os pontos de tangencia andam sobre elas; o resto do eixo nao muda. Arco no fim do eixo ou seguido de outro arco: mantem o inicio e a tangente de chegada, troca o raio e preserva o angulo central. Mover vertice continua como antes (as tangentes mandam). Vale no preview do grip e na atualizacao;
- **botao Concordancia** (antigo Reconcordar) abre um menu: Vincular concordancia (selecionar eixos), **Desvincular concordancia** (selecionar eixos), modo dos proximos eixos (concordantes/livres) e Reconcordar trecho. Na linha de comando, `C3DVIARIOEIXO > COncordancia` ganhou [VIncular/DEsvincular]. Na aba Propriedades do eixo continua a escolha Concordancia mantida/livre.

## 2.34.2 - C3DVIARIO v0.7.10 / arco nao passa do arco da esquina vizinha; alvo Civil 3D 2027

- **esquinas vizinhas num ramo curto** (alca): o arco de uma esquina podia passar do arco da esquina do outro no na mesma face - a face ficava cruzando a si mesma (1 m sobreposto), o que atrapalhava a hachura e sairia errado nos alinhamentos. O FitsInLeg limitava so pelo comprimento do ramo; agora a tangencia tambem nao pode passar da tangencia ja colocada pelo no vizinho (o raio reduz e o motivo aparece: "o arco se sobreporia ao arco da esquina vizinha");
- **compilacao para Civil 3D 2027**: `.csproj` com alvo por propriedade `Acad` (2026 = .NET 8, pastas `AutoCAD 2026`, saida `NETLOAD\`; 2027 = .NET 10, pastas `AutoCAD 2027`, saida `NETLOAD\2027\`), configuracoes `Debug2027`/`Release2027` na solucao, `tools\Build.ps1 -Acad 2027`, simbolos `ACAD2026`/`ACAD2027`, erro claro se a pasta do AutoCAD do alvo nao existir. O 2027 exige VS 2026; nada muda para quem compila o 2026.

## 2.34.1 - C3DVIARIO v0.7.9 / menus no tema escuro, hachura por contorno com conferencia de area

- **menus do painel no tema escuro**: o cromado padrao do WPF traz cores fixas (coluna de icones branca, submenus em cinza claro, texto desabilitado #707070), e as listas de estilo/escala/padrao ficavam ilegiveis. ContextMenu, MenuItem (com marca, seta e submenu) e Separator ganharam modelos proprios pintados com o tema;
- **hachura do pavimento**: uma HATCH por contorno externo com os seus furos (antes um objeto unico com todos os lacos: quando dois contornos se tocam o AutoCAD perde a paridade e some com regioes). Lacos que cruzam a si mesmos - duas faces de meio-fio que se encostam, dois arcos de esquina sobrepostos - sao desatados no cruzamento: a lasca e descartada e as partes seguem como lacos simples (duas vias tangentes passam a dividir a quadra em dois furos). Vertices repetidos e trechos nulos sao limpos;
- **conferencia de boundary**: depois de avaliar cada hachura, a area obtida e comparada com a esperada (contorno menos furos); diferencas, contornos abertos e cruzamentos saem na linha de comando com a coordenada do problema ("[HACHURA] ..."); sem problemas, o comando diz isso.

## 2.34.0 - Layouts v1.0.0 / explorador de layouts (C3DLAYOUT)

- **novo modulo Layouts** (`Modules/Layouts`), no mesmo molde do explorador da rede viaria: barra de categorias com contagem (Layouts, Viewports, Numeracao, Layer state, Carimbo), barra de comandos com icones e dica, vista grafica escura com a miniatura do layout e as viewports clicaveis, lista em colunas com caixa de marcacao e abas de detalhe (Propriedades, Renomear, Escala, Numeracao, Aplicar, Carimbo);
- **Layouts**: abrir, renomear em lote (prefixo, base, numero sequencial, sufixo, previa), mover para a esquerda/direita, ordenar pelo nome (ordem natural), copiar, excluir (com confirmacao);
- **Viewports**: travar/destravar, ligar/desligar, escala 1:N (com escala de anotacao quando existe no desenho); marcacao na miniatura, na lista ou pelo "repetir nos demais";
- **Numeracao de pranchas** (ex-NP, em C#): `NP [Configurar/Atualizar]` na linha de comando e a aba Numeracao no painel; textos e atributos das layers PRANCHA_NUMERO/PRANCHA_TOTAL, ordem das abas;
- **Layer state nas viewports**: o LSLAYOUTS agora abre o explorador nessa categoria (`C3DLAYOUTLS`);
- **Carimbo em lote** (`C3DLAYOUTCARIMBO`): lista os blocos com atributos dos layouts, mostra as tags e escreve os valores preenchidos em todos os layouts marcados (substitui o JOBEPLACE sem valores fixos no codigo);
- comandos: `C3DLAYOUT`, `C3DLAYOUTVP`, `C3DLAYOUTLS`, `C3DLAYOUTCARIMBO`, `NP`; `NPRANCHAS.LSP` sai da DLL;
- `ToolTheme` e `ToolIcons` movidos para `UI/Common` (compartilhados entre os paineis);
- plotagem em PDF (`LayoutOps.PlotarPdf`) pronta no codigo, sem botao ate a v2.
## 2.33.0 - C3DMODULOS / escolha dos modulos na Ribbon

- **C3DMODULOS** (botao "Modulos" no painel Plugin da Ribbon): janela com todos os modulos do catalogo, por categoria, para marcar quais aparecem na Ribbon e na lista de comandos (C3DHELP). Aplicar grava em `%APPDATA%\Civil3D2026Plugin\modulos.txt` e reconstroi a Ribbon na hora. Modulos desmarcados tambem deixam de iniciar seus servicos de fundo (reatores do viario, tooltip dos estais, solidos so no 3D, autoload das LISPs) na proxima carga da DLL. Os comandos continuam registrados - digitar o nome funciona - porque a DLL .NET nao descarrega por partes; o modulo Geral (ajuda, Ribbon, modulos) e fixo.

## 2.32.8 - C3DVIARIO v0.7.8 / hachura do pavimento, raio da alca, contorno nas esquinas obliquas

- **C3DVIARIOHACHURA** (tambem `C3DVIARIO > HAchura` e botao com menu no painel): uma HATCH unica so da regiao de pavimento - contorno pelas faces de meio-fio, tampas nas pontas livres dos eixos, quadras fechadas, canteiros, ilhas e ilha central de rotatoria como furos (estilo par/impar). Padrao (ANSI31...), escala, angulo ou SOLID com transparencia, gravados no desenho (Config); layer VIARIO-PAVIMENTO; [Ocultar/Mostrar/Remover]; acompanha a rede a cada atualizacao; o autoteste confere;
- **raio da esquina ao lado de uma alca** (caso do DXF enviado): o ramo passou a ser medido por lado - um no em que outro eixo so chega pelo lado oposto nao interrompe a face deste lado, entao o arco pode passar dele. A esquina que ficava em 3,3 m aceita 17 m;
- **contorno das quadras em cruzamentos obliquos**: quando o bordo da via transversal cruza a face alem do no, as duas tangencias caem do mesmo lado e a regra "do no ate a tangencia" apagava um pedaco do contorno; a remocao passou a ser entre as duas tangencias. Corrige o buraco no contorno interno do anel ao lado da diagonal.

## 2.32.7 - C3DVIARIO v0.7.7 / busca do centro da esquina mais robusta

- o arco da esquina testa todos os encontros das paralelas (do mais perto ao mais longe da esquina) e fica com o primeiro que tangencia as duas linhas depois da esquina e cabe nos ramos; antes so o mais proximo era considerado, o que em alcas e curvas podia descartar o certo. Os motivos de "nao coube" ficaram mais especificos.

## 2.32.6 - C3DVIARIO v0.7.6 / reconcordar pela tangente, ocultar sinalizacao

- **Reconcordar trechos** ganhou a opcao **[Arco/Tangente]**: Arco refaz o arco pela direcao da reta (como antes); **Tangente** mantem o arco (mesmo circulo) e move a reta vizinha ate tangencia-lo - o vertice vai para o ponto de tangencia tirado da ponta fixa da reta e o arco e encurtado/prolongado no mesmo circulo. Entre dois arcos continua Seguinte/Anterior;
- **sinalizacao**: `C3DVIARIOSINAL` tem [Ocultar/Mostrar] (desliga/liga as layers VIARIO-SH-*; o bloco fica acompanhando a rede) e o botao do painel abre um menu: gerar/atualizar, ocultar, mostrar, remover, opcoes.

## 2.32.5 - C3DVIARIO v0.7.5 / menu do botao Cotar faixas, cotas sem sobreposicao, remover por clique

- **botao Cotar faixas** no painel abre um menu para clicar: Cotar faixas..., estilo de cota (lista dos estilos do desenho), escala DIMSCALE (lista: do estilo, 0.25 ... 1000, Outro... em Config), sarjeta e meio-fio separados, remover cota (clique perto) e remover todas; layer VIARIO-COTAS;
- **cotas sobrepostas**: a altura do texto passou a vir do estilo de cota escolhido (DIMTXT x DIMSCALE, ou a escala de anotacao corrente se o estilo for anotativo), e a largura total fica uma fileira inteira acima da cadeia;
- **[Remover]** do C3DVIARIOCOTAR: clique perto da cadeia (apaga eixo + estaca daquela cadeia) ou [Todas].

## 2.32.4 - C3DVIARIO v0.7.4 / por que o raio nao coube

- quando o raio pedido numa esquina nao cabe, o aviso (linha de comando, `C3DVIARIORAIO` e painel > Esquina > Limite) diz o motivo: "a curva do eixo X se afasta da esquina: nao existe arco tangente aos dois com esse raio" (alca que ja sai curvando do no - estique a tangente antes do no ou aumente o raio da alca) ou "a tangente do arco passaria do no vizinho (ramo curto)". O `C3DVIARIORAIO` tambem informa, logo apos aplicar, o raio que de fato ficou.

## 2.32.3 - C3DVIARIO v0.7.3 / inverter eixo, taper nos dois sentidos, reconcordar, cotas interativas, textos e zoom do corte

- **Inverter sentido do eixo** (`C3DVIARIOEIXO > Inverter`, botao no painel): geometria revertida, elementos reancorados (estaca L - s, lado do no trocado) e raios/faixas livres das esquinas reescritos; avisa quando a secao tem lados diferentes (esquerda/direita trocam);
- **taper nos dois sentidos**: alargamento e conversao perguntam onde fica o taper - Longe do no (saida, como antes), Junto ao no (o alargamento comeca na intersecao e cresce) ou Ambos; editavel no painel (Elementos > Taper fica);
- **reconcordar trechos** (`C3DVIARIOEIXO > COncordancia > Vertice`, botao Reconcordar): clique perto de um vertice e o arco vizinho e refeito tangente ao outro trecho (Seguinte/Anterior); entre duas retas, Raio no vertice insere o arco;
- **C3DVIARIOCOTAR interativo**: a cadeia de cotas segue o cursor ao longo do eixo antes de clicar; opcoes [Estilo] (lista os estilos de cota do desenho) e [Escala] (DIMSCALE), gravadas no desenho e no painel (Config > Cotas das faixas); layer VIARIO-COTAS criada com as demais; botao "Cotar faixas..." nas propriedades do eixo;
- explorador: **Textos** virou lista do que mostrar (nomes dos eixos, estacas, PF, reguas, escala e norte, titulo, nomes dos componentes, textos das cotas), lembrada entre sessoes; **zoom e arrasto no corte da secao** (roda/arrastar, offset sob o cursor); **escala grafica cartografica** (duas fileiras alternadas, primeiro trecho subdividido, valores nos tracos maiores).

## 2.32.2 - C3DVIARIO v0.7.2 / narizes de ilha em bicos agudos

- **bicos agudos** (ramos que se separam em angulo pequeno, como os narizes das ilhas entre pistas que se bifurcam) ficavam em canto vivo e nao aceitavam raio: a esquina fica longe do no (a 3,8 m / sen(angulo/2)) e a regra "metade do ramo" nao deixava o arco caber. Agora o ramo cede ate o que o outro no precisa, o centro e procurado onde um bico agudo o poe, e a busca do raio e mais fina (reduz de 20 em 20%);
- **raio de nariz**: bicos com menos de 35 graus recebem por padrao 1,00 m (IPR-718, narizes de ilha 0,5-1,0 m) em vez do raio de esquina da secao; `C3DVIARIORAIO` continua valendo por esquina. Com o nariz arredondado, as linhas de sarjeta/pista deixam de formar o "espeto" que parecia uma sarjeta maior.

## 2.32.1 - C3DVIARIO v0.7.1 / cotar faixas com um clique

- **C3DVIARIOCOTAR** (tambem `C3DVIARIO > COtar` e no painel): clique sobre o eixo e a secao e cotada naquela estaca, em planta, com cotas alinhadas perpendiculares ao eixo em cadeia (calcada, meio-fio+sarjeta, cada faixa, canteiro) e a largura total numa segunda linha. As posicoes vem das linhas geradas na estaca (alargamentos entram); as faixas, da secao. Estilo de cota corrente, layer VIARIO-COTAS; [Detalhe] separa sarjeta e meio-fio; [Remover] apaga todas as cotas do viario.

## 2.32.0 - C3DVIARIO v0.7.0 / eixo tipo Vehicle Tracking, preview nos grips, secao por lado, monitor tecnico

- **C3DVIARIOEIXO no modelo do Vehicle Tracking**: `Tangent` (reta na direcao corrente, o clique da o comprimento), `Arc` (arco tangente que passa pelo ponto), `Undo`; clicar em cima do ultimo vertice desfaz o ultimo trecho; todos os trechos concordantes. `Free`/`Chain` alterna o lancamento livre (como antes). Eixo lancado assim fica com **concordancia mantida**: mover um vertice (grip, STRETCH, MOVE) refaz a cadeia a jusante; `C3DVIARIOEIXO > COncordancia` (Manter/Liberar, tambem por selecao) e a propriedade do eixo no painel liberam/religam;
- **preview durante o arraste de grips**: ao mover um vertice do eixo a rede recalculada (e o eixo concordado) aparece antes de soltar;
- **pontas quase encostadas**: ponta de eixo a ate a distancia de captura de outro eixo (Config, 1 m) vale como ligada - cobre EXTEND/grip que para antes ou passa um pouco do eixo transversal;
- **secao tipo por lado**: sarjeta, meio-fio e calcada em ambos os lados, so a esquerda, so a direita ou nenhum (no sentido do eixo); linha de comando (`Secao > Nova/Editar`: Ambos/Esquerda/Direita/Detalhar), painel (Sarjeta em / Meio-fio em / Calcada em) e corte;
- **monitor tecnico**: reguas de coordenadas E/N, grade maior/menor, estacas de 20 m ao longo dos eixos (14+15.00, PF na ponta), escala grafica, norte e escala aproximada; corte da secao com cotas de desenho (linhas de chamada, tracos obliquos, texto na cota, cadeia por componente / meia pista / total), regua de offsets, nomes dos componentes, LE/LD e altura do meio-fio;
- explorador: fontes Tahoma (interface) e Consolas (numeros, coordenadas, cotas); colunas numericas alinhadas a direita com unidade no cabecalho; aba **Geometria** do eixo (trecho, tipo, comprimento, raio, estacas e coordenadas iniciais/finais); colunas Est. final, R min, Concord. nos eixos; Lados nas secoes;
- autoteste: linha "Concordancia mantida" (confere a continuidade) e lados das secoes.

## 2.31.0 - Utilitarios v1.1.0 / LSLAYOUTS com janela, ALLTORGB com layers

- **LSLAYOUTS** sem digitacao: janela com a lista dos layer states do desenho (nome e descricao) e uma miniatura de cada layout (margem, carimbo e viewports, desenhados a partir do papel). Clique nas viewports para marcar; por layout, "todas" e "repetir nos demais" (marca nos outros layouts as viewports na mesma posicao e tamanho, para pranchas do mesmo modelo); filtro de layouts com curinga; Todas/Nenhuma. Aplicar restaura o layer state como override nas marcadas e a janela continua aberta para aplicar outro layer state em outras viewports; a miniatura mostra o layer state aplicado. REGENALL ao fechar;
- **ALLTORGB**: opcao [Objetos/Layers/Ambos]; em Layers converte a cor das layers dos objetos selecionados (layers de xref sao ignoradas; layer travada pode ter a cor convertida).

## 2.30.0 - Utilitarios v1.0.0 (LISPs leves em C#) / LispTools v1.1.0 (lista com icones)

- **8 LISPs convertidas para C#** (`Modules/Utilitarios`), com o mesmo nome de comando: ALLTORGB, TLEN, JOBECOUNT, SMARTHATCH, LSLAYOUTS, MOFF/MOFFSET, SEPARA_CURVAS_RAIO, KB/PP. Os `.lsp` sairam da DLL (ficam no historico do git);
  - TLEN soma qualquer curva (inclui polilinha 3D e feature line);
  - SMARTHATCH cria a hachura com as ilhas e permite mudar o gap ([Tolerancia]);
  - KB nao faz mais PURGE de layers nem reinicia a lista de escalas do desenho;
  - Enter repete os ultimos valores (distancia, quantidade, raio, largura, layer state, layouts);
- **C3DLISP** virou uma lista unica (nativas e LISPs): icone pequeno, nome em destaque, comando e uma linha de descricao; duplo clique ou Enter executa, a dica mostra o passo a passo, botao direito escolhe entre varios comandos; as que ainda sao LISP tem a marca "LISP";
- cabecalho `;;@ICONE` nas LISPs (icones vetoriais em `ToolIcons.cs`).

## 2.29.1 - LispTools v1.0.1 / sem aviso de seguranca

- a pasta das LISPs (`%LOCALAPPDATA%\Civil3D2026Plugin\Lisp`) passa a ser incluida em TRUSTEDPATHS ao carregar o plugin; antes o SECURELOAD mostrava "Unsigned Executable File" para o MALHA.VLX ao abrir o desenho e para cada LISP no primeiro uso. So essa pasta e acrescentada; o resto da variavel fica como estava.

## 2.29.0 - LispTools v1.0.0 / LISPs distribuidas dentro da DLL

- **novo modulo LispTools** (`Modules/LispTools`): 24 LISPs passam a ir dentro da DLL e ficam disponiveis em todo desenho, sem APPLOAD nem Startup Suite;
- **C3DLISP**: janela com a lista por categoria (hachuras, polilinhas, vias e meio-fio, pranchas e layouts, blocos e textos, tabelas, 3D), explicacao de cada rotina, passo a passo e botao para executar cada comando; localizar por nome, comando ou descricao; segue o tema claro/escuro do painel viario;
- **C3DLISPLIST** (mesma lista na linha de comando) e **C3DLISPCARREGAR** (regrava e registra de novo);
- carregamento sob demanda com `autoload`: a rotina so e carregada no primeiro uso do comando (evita, por exemplo, que o NP crie as layers PRANCHA_* em todo desenho aberto); VLX/FAS carregados inteiros;
- a arvore do banner/C3DHELP lista as LISPs por categoria; aba LISPs na Ribbon;
- novo: **LSLAYOUTS**, restaura um layer state como override de viewport em todos os viewports dos layouts escolhidos;
- `VIAU.lsp` (v0.1) ficou de fora: o `VIAU_v0.4.lsp` define os mesmos comandos e funcoes.

## 2.28.3 - C3DVIARIO v0.6.3 / sinalizacao redesenha certo, botoes no tema escuro

- **sinalizacao com desenho antigo na tela**: depois de UNDO o bloco tinha as marcas certas, mas a insercao mostrava a versao anterior (faixas de pedestre dentro do canteiro). A insercao agora e recriada a cada atualizacao e fica por baixo das linhas da rede;
- explorador: botoes com modelo proprio (o hover azul claro do Windows apagava o texto no tema escuro); recomendacoes "info" com icone proprio; botao "Remover da rede".

## 2.28.2 - C3DVIARIO v0.6.2 / sinalizacao volta a gerar

- **sinalizacao nao gerava** (eNoDatabase): a transparencia da layer VIARIO-SH-CONTORNO era aplicada antes de a layer entrar no desenho;
- secao criada/editada pela linha de comando: ao mudar pista ou canteiro, o numero de faixas volta ao automatico;
- explorador: selecao das listas legivel no tema escuro, barra de comandos sem as setas de excesso, rotulos dos eixos sem sobreposicao.

## 2.28.1 - C3DVIARIO v0.6.1 / explorador no molde do Project Explorer, tema escuro, consulta DNIT

- C3DVIARIOPAINEL no molde do Project Explorer do Civil: barra de categorias com contagem (secoes, eixos, elementos, intersecoes, esquinas, ilhas, sinalizacao, Civil, recomendacoes, consulta, config), barra de comandos com Localizar, vista grafica escura com grade, coordenadas, zoom pela roda e arrastar, lista de objetos em colunas e abas (Propriedades, Recomendacoes e itens relacionados);
- **tema escuro** (Preferencias...), fonte Arial;
- **consulta**: a fonte de cada recomendacao abre a tabela resumida do manual (raio de esquina, faixa de conversao, armazenamento, taper, ilhas, canteiro, aberturas/retornos, giro livre, angulo, rotatorias, larguras de faixa e passeio, sinalizacao MBST); categoria Consulta lista todas;
- zoom das recomendacoes corrigido (converte para as coordenadas da vista) e aviso sem local faz zoom no eixo/secao;
- **Soltar** passa a se chamar **Desvincular** (e Restaurar -> **Revincular**): `C3DVIARIO > DEsvincular`.

## 2.28.0 - C3DVIARIO v0.6.0 / explorador, recomendacoes DNIT, sinalizacao vazada

- **C3DVIARIOPAINEL** refeito no estilo do Project Explorer: barra de ferramentas com os icones (dica com ilustracao e passos), arvore (Secoes, Eixos > elementos, Intersecoes > esquinas, Ilhas, Sinalizacao, Civil, Config), grafico do item em fundo branco com grade (corte transversal cotado para secoes, planta com destaque para eixos, esquinas, ilhas e elementos), propriedades editaveis e acoes do item;
- **recomendacoes dos manuais do DNIT** (IPR-718 Intersecoes e IPR-740 Travessias Urbanas), discretas como no TQS: largura de faixa por tipo de via, sarjeta, calcada, canteiro, velocidade, raio de esquina pelo angulo de giro (Tab.39), faixas de conversao (largura, taper, armazenamento, sobra de canteiro), gota, abertura/retorno, faixa livre (largura x raio, Tab.45), area minima de ilha, angulo entre vias, intersecoes muito proximas, rotatoria. Aparecem no painel (por item, com zoom) e, quando um comando cria algo novo fora da recomendacao, uma linha [DNIT] na linha de comando; o autoteste lista tambem;
- sinalizacao: opcao **Vazada** (so contornos cinza com transparencia, nada pintado, layer VIARIO-SH-CONTORNO); divisao de faixas continua antes da retencao (LMS-1); LRE comeca depois da divisoria/gota e vai ate o bordo; listras da faixa de pedestres centradas em cada meia pista; entradas de rotatoria com linha de 'De a preferencia' e travessia a >= 20 m.

## 2.27.1 - C3DVIARIO v0.5.1 / ajustes do painel e da sinalizacao (testado no Civil)

- painel: o desenho e redesenhado logo apos editar secoes/eixos (antes so atualizava ao mexer no zoom); mapa da aba Rede nao corta mais a parte de baixo; lista de secoes sem rolagem lateral;
- sinalizacao: ramo curto entre dois nos (ou ate a ponta) nao recebe faixa de pedestres nem retencao sobrepostas; listras que cairiam dentro de uma gota nao sao pintadas.

## 2.27.0 - C3DVIARIO v0.5.0 / painel flutuante, sinalizacao previa, secoes com tipo

- **C3DVIARIOPAINEL**: janela flutuante (nao modal) com abas:
  - Comandos: todos os comandos da rede com icone, ilustracao grande, descricao e passo a passo; clique roda o comando;
  - Secoes: lista, edicao (nome, tipo de via, pista, faixas, canteiro, sarjeta, meio-fio, calcada, raio, velocidade), nova/duplicar/excluir/corrente, aplicar aos eixos selecionados e corte transversal ao vivo;
  - Rede: mapa da rede, lista de eixos (nome do Civil, secao, comprimento, raio, elementos), zoom/selecionar, trocar secao, tirar da rede, remover elementos;
  - Sinalizacao: opcoes com preview ao vivo, gerar/atualizar e remover o bloco;
  - Civil: gerar/remover/autoteste; Config: tolerancias.
- **C3DVIARIOSINAL**: sinalizacao horizontal previa (divisoria, divisao de faixas, bordo, faixa de pedestres, retencao) num bloco unico que acompanha a rede;
- secoes com **tipo de via, faixas e velocidade**; listas mostram o nome (o codigo P1... fica interno); desenho novo traz Local, Coletora e Arterial com canteiro;
- icones vetoriais proprios para os comandos da rede viaria (painel e Ribbon - use C3DRIBBONRESET para atualizar a aba);
- autoteste confere tambem o bloco de sinalizacao.

## 2.26.7 - C3DVIARIO v0.4.7 / conversao no canteiro do lado certo, alinhamentos sem site

- **C3DVIARIOCONVERSAO**: a baia de conversao a esquerda cortava o canteiro do lado da pista oposta; agora corta junto da pista de quem chega (os modelos com conversao no canteiro tambem). Desenhos com conversao ja lancada mudam de lado ao regenerar;
- alinhamentos do Civil (eixos, bordos, curb returns) criados **sem site**: no site "VIARIO" eles fechavam as quadras e o Civil criava lotes (parcels) com rotulo de area. Para limpar um desenho antigo: C3DVIARIOCIVIL > Remover e Gerar de novo;
- roteiro `teste_comandos.scr` rodado no Civil: 16 etapas, 0 falhas.

## 2.26.6 - C3DVIARIO v0.4.6 / roteiro de teste dos demais comandos

- `tests/viario/teste_comandos.scr`: secao nova/corrente/editar/excluir, conversao no canteiro, abertura, remover elemento, raio de esquina e Limpar, atribuir secao, soltar/restaurar linhas, remover/adicionar eixo, config, atualizar e desfazer;
- C3DVIARIOTESTE confere tambem as linhas desenhadas contra a rede (faltando/sobrando) e resume elementos, secoes, faixas livres, raios e linhas soltas.

## 2.26.5 - C3DVIARIO v0.4.5 / raio em vertice do eixo, eliminar ilhas

- **C3DVIARIORAIO** tambem concorda vertices do proprio eixo (canto de quadra, curva de uma via em polilinha): clique perto do vertice, do lado que interessa; o raio digitado/arrastado e o da face do meio-fio desse lado (dentro da curva: eixo = R + meia largura; fora: R - meia largura). A polilinha do eixo ganha o arco (os bordos ficam concentricos e os alinhamentos do Civil acompanham). Clicar de novo edita o raio; [Padrao] ou 0 volta ao canto vivo;
- **C3DVIARIOILHA** (e ELementos > Ilha): clique dentro de uma ilha (anel fechado de meio-fio, canteiro ou gota) e ela some - vira pavimento; [Restaurar] traz de volta a ilha clicada, [Todas] restaura todas. Anel grande (> 5000 m2, cara de quadra) pede confirmacao. Gravado no DWG (ILHASOFF);
- C3DVIARIORAIO > Ilha tambem concorda os cantos do proprio eixo em volta da ilha (quadra desenhada como polilinha fechada), alem das esquinas dos nos; [Padrao] volta esses cantos a canto vivo;
- **raio dos cantos das ilhas canalizadoras** (rosa: gota e ilha triangular da faixa livre): em C3DVIARIORAIO clique dentro da ilha (ou Ilha > clique dentro) e arraste/digite o raio; todos os cantos da ilha sao concordados pelo lado de dentro (raio que nao cabe e reduzido). [Padrao] volta a cantos vivos. Gravado no DWG (ILHARAIO);
- script de teste cobre o raio em vertice, o raio da ilha, o raio da gota e eliminar/restaurar ilha.

## 2.26.4 - C3DVIARIO v0.4.4 / testado no Civil 3D (roteiro automatico)

Rodado no Civil 3D 2026 com `tests/viario/teste_rede.scr` (cruzamento, T, eixo curvo, rotatoria com ramos, quadra fechada; alargamento, gota, raio, mover eixo, faixa livre, apagar, desfazer, remover e gerar de novo), conferido por `C3DVIARIOTESTE`:

- **C3DVIARIOTESTE**: autoteste somente leitura; cada eixo, bordo e curb return sai OK/FALHA (trechos fora do eixo, bordo fora da rede nos patamares, desvio maximo nos tapers, arco do curb return, objetos sobrando). Relatorio em `%TEMP%\C3DVIARIOTESTE.txt` e, com a DLL na pasta NETLOAD do repositorio, em `tests/viario/out/`;
- curb return conectado: o Civil exige sobreposicao > 0 (antes todos caiam em "simples"); agora 0,5 m e todos saem conectados aos bordos;
- alargamento/gota: transicoes do offset gravadas por estaca inicial/final (o Civil cria com comprimento zero = degrau); regiao inicial/final sem transicao de entrada/saida tratada; taper suave = curva reversa simetrica (desvio de poucos cm do taper do motor);
- motor: esquina perto de um no que e so continuacao reta (dois eixos alinhados) pode usar o ramo inteiro - antes o raio era reduzido sem necessidade;
- nomes de curb return nao repetem numero quando parte deles e mantida;
- conferencia ignora vertices de polilinha sem curva (o offset do lado de dentro e cortado ali).

## 2.26.3 - C3DVIARIO v0.4.3 / raio de esquina em eixos que se cruzam mais de uma vez

- quando dois eixos se encontram em mais de um no com a mesma orientacao (ex.: via em curva que cruza outra duas vezes), as esquinas tinham a mesma chave e o editor de raio mudava as duas juntas. Agora a segunda ocorrencia em diante recebe sufixo (`#2`, `#3`, pela ordem ao longo do eixo); chaves que nao se repetem continuam iguais, entao raios ja gravados em desenhos antigos nao mudam.

## 2.26.2 - C3DVIARIO v0.4.2 / bordos inteiros e ligados ao eixo

- bordos: um alinhamento offset por lado (`<eixo> - LD` e `- LE`) no comprimento inteiro do eixo, como o Civil faz; os curb returns passam por cima nas esquinas (antes eram varios trechos cortados);
- bordo e sempre offset dinamico do Civil (ligado ao eixo, sem PIs proprios). A geometria fixa amostrada (muitos PIs) foi removida; se o alargamento nao conferir, o offset fica assim mesmo e o console avisa;
- conferencia de arcos usa as propriedades reais do Civil (centro, raio, sentido, deflexao). Antes a leitura do comprimento falhava e o arco caia no recurso de "duas metades", criando PIs extras;
- curb returns: com os bordos inteiros, os pais do alinhamento conectado se cruzam de fato (condicao do Civil).

## 2.26.1 - C3DVIARIO v0.4.1 / correcoes dos alinhamentos do Civil

- arcos saiam espelhados (curva para o lado contrario): a sobrecarga usada pelo Civil toma a direcao no ponto final. Agora cada arco e criado pela forma "direcao no segundo ponto", conferido (centro e comprimento) e, se nao bater, desfeito e refeito por outra forma (direcao inicial, raio + sentido, ou duas metades). Vale para eixos, curb returns simples e bordos fixos; o console avisa se algum trecho do alinhamento nao confere com o eixo;
- alargamentos/tapers: o bordo LD/LE ganha regiao de alargamento do proprio offset (AddWidening) com transicoes no comprimento do taper (linear, ou curva reversa no taper suave). O plugin confere o bordo nos patamares e trechos normais; se o Civil nao representar (forma do valor, ou taper cortado pela esquina), cria o bordo com geometria fixa igual a da rede e avisa;
- curb returns encostados em trecho alargado usam a distancia real (com alargamento) nas tentativas de alinhamento conectado;
- bordos sao refeitos quando o alargamento muda.

## 2.26.0 - C3DVIARIO v0.4.0 / objetos do Civil (etapa 1: alinhamentos)

- **C3DVIARIOCIVIL** (e C3DVIARIO > CIvil): gera alinhamentos do Civil a partir da rede, no site `VIARIO`:
  - eixo: alinhamento com retas e arcos fixos (`Eixo VIARIO n`, layer `VIARIO-C3D-EIXO`);
  - bordos: alinhamentos offset dinamicos na face do meio-fio, um por trecho entre esquinas (`<eixo> - LD1/LE1...`, layer `VIARIO-C3D-BORDO`); anel de rotatoria quebrado na estaca zero quando preciso;
  - curb returns: tenta alinhamento conectado do Civil (pais = bordos ou eixos com offset) e confere a geometria; se nao conferir, cria alinhamento tipo CurbReturn com o arco fixo (layer `VIARIO-C3D-CR`). O console informa quantos ficaram de cada tipo e por que;
  - [Remover] apaga tudo o que foi gerado no Civil (a rede em polilinhas continua);
- atualizacao automatica: depois de gerados, os alinhamentos acompanham a rede a cada edicao. Eixo alterado tem as entidades refeitas no proprio alinhamento (mesmo objeto, perfis continuam ligados); trechos de bordo e curb returns sao mantidos quando nao mudaram e refeitos quando mudaram;
- objetos identificados por XData `C3DVIARIO`/`CIVIL`; nomes podem ser trocados no Civil;
- planejador puro (`ViarioCivilPlan`) testado fora do Civil (cruz, T, L, Y, curvas, canteiro, rotatoria, quadra fechada, malha, angulo agudo);
- C3DVIARIODIAG passa a listar tambem entidades de alinhamento (linhas/arcos) para a proxima etapa;
- limitacoes desta etapa: alargamentos/baias ainda nao viram regioes de alargamento no offset (o bordo segue a largura nominal); bordos do canteiro central ainda nao sao gerados; trecho de bordo editado a mao e refeito quando o eixo muda;
- compilacao e teste no Civil 3D pendentes (checado contra stubs com as assinaturas do C3DVIARIODIAG).

## 2.25.0 - C3DVIARIO v0.3.0 / modelos de intersecao, alargamentos, gotas, faixas livres, canteiro

- corrige o menu do C3DVIARIO: "Rotatoria" abria o editor de raio porque as opcoes tinham o mesmo atalho ("R"); atalhos unicos em todos os menus (Raio/ROtatoria/REmover, ADicionar/ATribuir/ATUalizar, Editar/eXcluir...);
- **C3DVIARIOMODELOS**: janela (estilo Vehicle Tracking) com 12 modelos parametrizados, secoes principal/secundaria/anel e pre-visualizacao desenhada pelo motor; insercao com ponto e rotacao e preview ao vivo da rede;
- largura variavel no motor: linhas com distancia variavel ao eixo (patamar + taper reto ou suave); as esquinas concordam com a borda real (alargada);
- elementos gravados no eixo e ancorados no no mais proximo: **alargamento** (C3DVIARIOALARGAR), **gota** (C3DVIARIOGOTA), **conversao no canteiro** (C3DVIARIOCONVERSAO), **abertura de canteiro** (C3DVIARIOABERTURA); comprimento arrastavel com preview;
- **faixa livre a direita** (C3DVIARIOLIVRE): meio-fio externo com raio grande e ilha triangular concentrica, raio arrastavel;
- C3DVIARIOELEMREMOVER remove qualquer elemento clicando perto; C3DVIARIO > ELementos reune os comandos;
- novo layer `VIARIO-ILHA` (cor 6);
- compilacao e teste no Civil 3D pendentes (janela WPF checada so contra stubs).

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
