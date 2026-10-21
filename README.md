# Civil3D2026Plugin

Projeto unificado para Civil 3D 2026.2 (**.NET 8 / x64**) e Civil 3D 2027 (**.NET 10 / x64**), mesmo codigo-fonte.

## Abrir e compilar

Abra `Civil3D2026Plugin.sln` no Visual Studio e use **Recompilar Solucao**.

A DLL resultante fica sempre em um caminho curto e fixo:

```text
NETLOAD\Civil3D2026Plugin.dll        (Civil 3D 2026 - configuracoes Debug/Release)
NETLOAD\2027\Civil3D2026Plugin.dll   (Civil 3D 2027 - configuracoes Debug2027/Release2027)
```

### Civil 3D 2027

O 2027 nao carrega a DLL do 2026 (mudou para .NET 10 e nao ha compatibilidade binaria). O
alvo e escolhido pela propriedade `Acad` do `.csproj`: configuracao `Debug2027`/`Release2027`
no Visual Studio, `tools\Build.ps1 -Acad 2027`, ou `dotnet build -p:Acad=2027`. O alvo 2027
procura as DLLs em `C:\Program Files\Autodesk\AutoCAD 2027\` e exige **Visual Studio 2026**
(o VS 2022 nao compila .NET 10). No codigo, `#if ACAD2027` / `#if ACAD2026` separam o que
mudar de API entre versoes.

No Civil 3D, execute `NETLOAD` nessa DLL. Ao carregar, o plugin mostra:

- versao geral;
- caminho exato da DLL carregada;
- arvore atual dos modulos e comandos;
- versoes dos modulos versionados;
- aba **C3D Tools** na Ribbon.

Use `C3DHELP` a qualquer momento para repetir a arvore, com descricoes.

## CORRSPLIT v1.6.0 (teste)

Execute CORRSPLIT e selecione o corredor de origem. Na janela em arvore, marque as regioes por baseline ou use Selecionar regiao no desenho, que agora desenha o contorno temporario da regiao sob o cursor e permite clicar dentro da faixa para seleciona-la,
informe o novo nome e escolha entre Copiar e Transferir. O comando cria so as
baselines necessarias e tenta reproduzir assemblies, frequencias, estacas adicionais,
targets e transitions compativeis. No modo Transferir, remove as regioes da origem
somente apos criar e validar o corredor novo.

A versao atual nao replica Corridor Surfaces, boundaries, overrides e offset
baselines. Consulte Modules/CorridorTools/CorridorSplit/README.md para limitacoes
e roteiro de testes. Teste primeiro em uma copia do DWG e use Copiar.

## Ribbon

A Ribbon e criada automaticamente apos o `NETLOAD`.

Comandos de manutencao:

```text
C3DRIBBON       -> cria/ativa a aba C3D Tools
C3DRIBBONRESET  -> remove e recria a aba a partir do catalogo central
```

Os botoes, grupos e tooltips sao gerados de `Infrastructure/CommandCatalog.cs`.
O primeiro comando de cada modulo e exibido como botao grande e os demais como botoes padrao.

Os icones sao vetoriais, gerados em memoria (sem arquivos externos): o desenho fica em `UI/Common/IconArt.cs`, o mesmo para os paineis e a Ribbon, gerado de `tools/icones` (fonte em Python com previa em PNG; ver `tools/icones/LEIAME.md`). Na Ribbon, o pictograma vai direto no fundo (cores pelo tema do AutoCAD, detalhes na cor do modulo); variantes (ajuda, configurar, atualizar...) ganham um selo ambar quadrado.

## Estrutura

```text
C3DPlugin/
├─ Civil3D2026Plugin.sln
├─ NETLOAD/                     <- DLL pronta para NETLOAD apos compilar
├─ src/
│  └─ Civil3D2026Plugin/
│     ├─ Infrastructure/        <- nomes, versoes, registro, catalogo e inicializacao
│     ├─ Modules/
│     │  ├─ CorridorTools/
│     │  ├─ Drenagem/
│     │  ├─ FeatureLines/
│     │  ├─ Geometry/
│     │  ├─ QTO/
│     │  └─ Surfaces/
│     ├─ Diagnostics/
│     └─ UI/
│        └─ Ribbon/             <- Ribbon e base para futuras interfaces
├─ docs/
└─ tools/
```

## Pontos centrais do projeto

- **`Infrastructure/CommandNames.cs`**: unica fonte dos nomes publicos dos comandos.
- **`Infrastructure/PluginVersions.cs`**: versoes exibidas dos modulos.
- **`Infrastructure/CommandCatalog.cs`**: fonte unica de titulo, descricao, agrupamento e visibilidade na Ribbon.
- **`Infrastructure/CommandRegistration.cs`**: classes que expoem comandos ao AutoCAD.
- **`Infrastructure/PluginApplication.cs`**: unico `IExtensionApplication`; controla banner e inicializacao da UI.
- **`Infrastructure/GlobalUsings.cs`**: aliases padronizados (`AcadDb`, `CivilDb`) para evitar colisao de namespaces.
- **`UI/Ribbon/RibbonUiManager.cs`**: gera a aba C3D Tools a partir do catalogo.
- **`UI/Ribbon/RibbonIconFactory.cs`**: monta os icones da Ribbon (pictograma de `IconArt` nas cores do tema + selo).
- **`UI/Ribbon/RibbonCommandHandler.cs`**: envia o clique dos botoes para os comandos normais do Civil 3D.

Consulte `docs/DEVELOPMENT.md` antes de adicionar um novo modulo.


## Passagem semi-elevada (v1.1.0)

Modulo em `Modules/CorridorTools/RaisedCrossing`. A PASSAGEM gera **um unico `Solid3d`** para uma travessia sobre duas vias separadas por canteiro central.

Ordem transversal de selecao:

```text
FL1 externa VIA 1 -> FL2 interna VIA 1 -> FL3 interna VIA 2 -> FL4 externa VIA 2
```

Regras principais:

- cada via recebe `rampa + plato + rampa`;
- o canteiro recebe apenas o plato, sem rampa veicular;
- o conector central inicia depois da extensao interna da VIA 1 e termina antes da extensao interna da VIA 2;
- a base acompanha as cotas das quatro Feature Lines;
- o topo e `base + H`, sendo `H` informado pelo usuario e constante;
- o comprimento informado e apenas o plato;
- o comprimento padrao de cada rampa veicular e 1,00 m;
- a geometria tenta acompanhar as Feature Lines por loft amostrado;
- VIA 1 + canteiro + VIA 2 sao unidos por Boolean Unite em um unico Solid3d.

```text
PASSAGEM            -> cria a passagem
PASSAGEMEDITAR      -> edicao individual
PASSAGEMGRUPO       -> edicao/atualizacao de varias passagens
PASSAGEMATUALIZAR   -> reconstrucao apos alterar Feature Lines
PASSAGEMCFG         -> extensao, rampa e amostragem do loft
PASSAGEMHELP        -> ajuda do modulo
```

O Solid3d final recebe, quando a API AEC estiver disponivel, o Property Set `C3D_PASSAGEM`, com parametros geometricos, handles FL1-FL4, cotas das quatro Feature Lines no CG e larguras VIA1/CANTEIRO/VIA2 no CG. Falha na camada de Property Set nao cancela a geracao geometrica.

## Ligacao tubular entre linhas (C3DSOLIDLIGAR v1.3.0)

Classe `C3DSolidLinkCommands`, em `Modules/CorridorTools/SolidArray`. Liga duas linhas quaisquer (Line, Arc, Polyline, Polyline 2D/3D, Spline, Feature Line ou AutoCorridorFeatureLine) com barras tubulares `Solid3d`.

```text
C3DSOLIDLIGAR           -> linha 1, linha 2, N pontos, diametro, previa [Inverter/Aceitar]
C3DSOLIDLIGAREDITAR     -> altera N, diametro ou sentido de um conjunto
C3DSOLIDLIGARATUALIZAR  -> [Existentes] atualiza no proprio objeto, apagados ficam fora | [Recriar] recria tudo
C3DSOLIDLIGARTOOLTIP    -> liga/desliga o tooltip com os dados do estai sob o cursor
C3DSOLIDLIGARHELP       -> ajuda do comando
```

- N pontos igualmente espacados pelo comprimento 3D de cada linha, incluindo inicio e fim;
- ponto i da linha 1 liga ao ponto i da linha 2 (ou ao oposto, se invertido na previa);
- desconto % no inicio e no fim de cada linha: os pontos ficam no trecho util restante;
- um `Solid3d` por estai, em layer proprio (padrao `C3D-ESTAIS`), persistido como conjunto no DWG;
- material de catalogo (CP190, A416, CA50, ST85, INOX) ou personalizado, e area metalica efetiva;
- Property Set `C3D_ESTAI` com codigo, material, comprimento, massa, cargas de escoamento/ruptura, inclinacao e cotas.

## Solidos so no 3D (C3DOCULTARPLANTA v1.0.0)

Modulo em `Modules/CorridorTools/PlanVisibility`. Solidos e bodies cujo layer comeca com um dos prefixos configurados (ex.: solidos extraidos do corredor) nao sao desenhados em vistas de planta e aparecem normalmente nas vistas 3D, viewport por viewport.

```text
C3DOCULTARPLANTA  -> [Ligar/Desligar/Prefixos]; prefixos separados por ;
```

Combine com um estilo de corredor que mostre links/shapes/feature lines apenas na aba Plan para ter o corredor em planta e os solidos no 3D.

## Quadro de ruas (C3DQUADRORUAS v1.0.0)

Modulo em `Modules/Quadros`. Exporta o quadro de ruas em .xlsx (layout da planilha RTA/TCE): rua, estaqueamento inicial/final, extensao, largura da pista, area e coordenadas geograficas (DD°MM'SS.ss") de inicio e fim.

```text
C3DQUADRORUAS  -> [Adicionar/Listar/Remover/Exportar/Config]
```

As ruas ficam guardadas no DWG; a exportacao rele eixos, textos e cotas. Coordenadas pela geolocalizacao do desenho ou UTM SIRGAS 2000 (fuso do Civil 3D ou informado).

## Modulos na Ribbon (C3DMODULOS)

`C3DMODULOS` (ou o botao Modulos no painel Plugin) abre a lista dos modulos por categoria; so os marcados aparecem na Ribbon e na arvore do `C3DHELP`, e so eles iniciam seus servicos de fundo na proxima carga da DLL (reatores do viario, tooltip dos estais, solidos so no 3D, autoload das LISPs). A escolha fica em `%APPDATA%\Civil3D2026Plugin\modulos.txt`. Os comandos continuam registrados (digitar o nome funciona): a DLL .NET nao descarrega por partes.

## Log do plugin (C3DLOG)

Log comum a todos os modulos: comandos do plugin (inicio, fim, falha e tempo), avisos, erros com a pilha e o tempo de cada etapa das operacoes longas. Fica na memoria (ultimas 8000 linhas) e num arquivo por dia em `%LOCALAPPDATA%\Civil3D2026Plugin\logs` (guardados por 14 dias).

- `C3DLOG` abre a janela do log; em qualquer janela do plugin, **Ctrl+Shift+L** abre o log daquele modulo (no explorador de layouts, liga uma aba Log escondida);
- **Copiar** leva o log filtrado, com as versoes (plugin, AutoCAD, Civil, .NET) no cabecalho, para colar numa conversa ou num chamado; tambem da para salvar em .txt ou abrir a pasta;
- **Diagnostico detalhado** registra tambem as excecoes tratadas dentro do plugin (falhas silenciosas): ligue para reproduzir um problema e desligue depois;
- erro nao tratado numa janela do plugin e registrado e a janela continua aberta.

Operacoes longas mostram uma janela de progresso (etapa, tempo, estimativa) que continua desenhando enquanto o AutoCAD trabalha; **Cancelar** para depois da etapa atual e o que ja foi feito fica.

## HYDRO - apoio a Pipe Network (C3DHYDRO v0.6.0)

Modulo que deixa a Pipe Network nativa facil de editar, ligar, calcular e verificar; os objetos
continuam sendo os do Civil (Network, Pipe, Structure, Catchment). Plano em `docs/HYDRO-PLANO.md`,
roteiro de teste em `docs/HYDRO-TESTE.md`.

```text
C3DHYDRO              -> painel (Redes, Estruturas, Tubos, Bacias, Calculo, Verificacao, Saidas)
C3DHYDROREDES ...     -> abre o painel direto numa categoria (C3DHYDROESTRUTURAS, C3DHYDROTUBOS,
                         C3DHYDROBACIAS, C3DHYDROCALCULO, C3DHYDROVERIFICAR, C3DHYDROSAIDAS)
HYAPLICAR             -> interno: executa as edicoes pedidas pelo painel (um UNDO por operacao)
HYFUNDO / HYTAMPA     -> fundo (tubos seguram a outra ponta ou a declividade) e tampa (superficie, valor, rebaixar)
HYDECLIV              -> declividade do tubo segurando montante ou jusante
HYTROCAR / HYIGUALAR  -> familia/tamanho pela parts list (conexoes mantidas); match properties de rede
HYCONECTAR / HYINVERTER -> ligar/desligar ponta do tubo; inverter sentido
HYMOVER / HYINSERIR / HYREMOVER -> mover estrutura, inserir estrutura no tubo, remover e emendar
HYCAMINHO             -> seleciona o caminho entre duas estruturas
HYBACIA / HYLIGARBACIA -> bacia por polilinha fechada; ligar bacia a estrutura de engolimento
HYTC                  -> tempo de concentracao (Kirpich, Ven Te Chow, Dooge, Kerby) com minimo de projeto
HYCALCULAR            -> racional acumulado + Manning; grava FlowRate, HGL/EGL e o Property Set HYDRO
HYVERIFICAR           -> violacoes (lamina, V, recobrimento, degrau, declividade, sentido) e abre o painel
HYDIMENSIONAR         -> menor diametro comercial que atende, com previa; aplica e recalcula
HYTABELA              -> tabela AutoCAD com a planilha de dimensionamento
HYLANCAR              -> lancar PV > tubo > PV em cadeia (linha elastica, [Desfazer], Enter termina)
HYCATALOGO            -> catalogos de pecas: ativos, apontar para os CSBIM, regenerar, importar para parts list
```

Catalogo CSBIM saneado (`tools/Sanitize-CsbimCatalog.ps1`): ver `docs/HYDRO-CATALOGO.md`.

Botao direito sobre tubo/estrutura abre o menu **HYDRO** com esses comandos; a dica no cursor mostra
nome, fundos, diametro, declividade, recobrimento e os resultados do calculo.

- O painel le o desenho sem travar o documento e so escreve por comandos.
- Vazoes sempre em **m3/s**; areas das bacias em ha nas planilhas; fundos = geratriz inferior
  interna (eixo da ponta do tubo - InnerHeight/2, como no DRENEXCEL).
- Calculo puro (sem AutoCAD) em `Modules/Hydro/Calc`, testado com `dotnet test tests/Hydro.Calc.Tests`.
- Para compilar numa maquina cujo Civil 3D 2026 roda em .NET 10: `tools\Build.ps1 -AcadDir <pasta com as DLLs .NET 8>`.

## Rede viaria (C3DVIARIO v0.9.0)

Modulo em `Modules/Viario`. Linhas da secao (bordo da pista, face e costas do meio-fio, alinhamento, canteiro central, ilhas) geradas a partir dos eixos, com intersecoes automaticas, esquinas concordadas, rotatorias e elementos de canalizacao.

```text
C3DVIARIOPAINEL      -> explorador (estilo Project Explorer): ferramentas, arvore de objetos, grafico, propriedades e recomendacoes DNIT
C3DVIARIOSINAL       -> [Gerar/Remover/Ocultar/Mostrar/Painel] sinalizacao horizontal previa num bloco unico (VIARIO-SINALIZACAO)
C3DVIARIOHACHURA     -> [Gerar/Remover/PAdrao/Escala/SOlido/Ocultar/Mostrar] hachura unica so do pavimento (furos: quadras, canteiros, ilhas); acompanha a rede
C3DVIARIOCOTAR       -> cadeia de cotas da secao segue o cursor sobre o eixo; clique grava (faixas, canteiro, passeio e largura total); [Detalhe/Estilo/Escala/Remover]
C3DVIARIOMODELOS     -> janela com intersecoes parametrizadas + insercao (ponto e rotacao, com preview)
C3DVIARIOEIXO        -> lanca eixos como no Vehicle Tracking: [Tangent/Arc/Undo] (T reta na direcao corrente, A arco tangente pelo ponto, U desfaz; clicar no ultimo vertice desfaz); [Free/Chain] alterna lancamento livre; [COncordancia] Manter/Liberar/Selecionar/Vertice (reconcordar dois trechos); [Inverter] inverte o sentido do eixo
C3DVIARIORAIO        -> raio de uma esquina ou de um vertice do eixo (clique perto), de uma [Ilha] ou de um [Eixo]; arraste ou digite
C3DVIARIOALARGAR     -> faixa adicional (conversao a direita/esquerda) com armazenamento arrastavel e taper [Longe/Junto/Ambos] do no
C3DVIARIOGOTA        -> ilha em gota na aproximacao (Tipo II)
C3DVIARIOCONVERSAO   -> conversao a esquerda tirada do canteiro (Tipo IV)
C3DVIARIOABERTURA    -> abertura de canteiro para retorno
C3DVIARIOLIVRE       -> faixa livre a direita com ilha triangular (Tipo III)
C3DVIARIOELEMREMOVER -> remove um desses elementos
C3DVIARIOCIVIL       -> [Gerar/Remover] alinhamentos do Civil: eixos, bordos LD/LE por trecho e curb returns
C3DVIARIOILHA        -> elimina a ilha clicada (vira pavimento); [Restaurar/Todas] desfaz
C3DVIARIOTESTE       -> autoteste: confere eixos, bordos e curb returns do Civil contra a rede (relatorio em %TEMP% e tests/viario/out)
C3DVIARIODIAG        -> diagnostico (somente leitura) da API do Civil instalada
C3DVIARIO            -> [Eixo/MOdelos/ADicionar/Secao/ATribuir/Raio/ROtatoria/ELementos/DEsvincular/REmover/ATUalizar/AUtomatico/CIvil/SInalizacao/COtar/HAchura/PAinel/Config]
```

- secao: pista, canteiro central, sarjeta, meio-fio, calcada e raio de esquina (na face do meio-fio; demais linhas concentricas); sarjeta, meio-fio e calcada podem existir so a esquerda, so a direita ou nos dois lados (no sentido do eixo);
- concordancia: eixo lancado com T/A guarda a marca "concordancia mantida"; ao mover um vertice (grip, STRETCH, MOVE) a cadeia e refeita a jusante (reta segue a direcao, arco tangente passa pelo vertice) e o preview da rede aparece durante o arraste; "Liberar" (comando ou painel) volta a edicao livre;
- pontas: ponta de eixo a ate a distancia de captura (Config) de outro eixo vale como ligada (EXTEND/grip que para um pouco antes ou passa um pouco);
- layers por elemento: `VIARIO-EIXO`, `VIARIO-PISTA`, `VIARIO-MF-FACE`, `VIARIO-MF-COSTAS`, `VIARIO-ALINHAMENTO`, `VIARIO-CANTEIRO`, `VIARIO-ILHA`;
- elementos gravados no eixo (XData) e ancorados no no mais proximo: acompanham o eixo quando ele e editado;
- modelos: cruzamento, T, Y, T/cruz com gotas, T/cruz com faixas livres, cruz com conversao a esquerda (canteiro ou alargamento), T com faixas de conversao, rotatorias de 3, 4 e 5 ramos com gotas;
- raio: faixa livre > especifico da esquina > especifico do eixo > da secao;
- associativo: mexeu, copiou ou apagou um eixo, a rede se refaz ao fim do comando (so as linhas que mudaram);
- atualizacao automatica liga/desliga por desenho (botao "Auto" no explorador ou `C3DVIARIO > AUtomatico`): em redes grandes (centenas de vias de quilometros), desligue; os eixos mudam sem refazer a rede, que fica pendente ate "Atualizar rede" (`C3DVIARIO > ATUalizar`). A ultima rede calculada fica guardada enquanto nada muda (o explorador e os comandos nao recalculam a mesma rede);
- explorador (`C3DVIARIOPAINEL`), categoria Eixos: caixinhas para marcar varios eixos; com marcados (ou varios selecionados) as propriedades viram edicao em lote (secao, raio, concordancia, nome, inverter, selecionar, remover da rede);
- nome do eixo (XData `C3DVIARIONOME`): editavel no explorador ou tirado dos TEXT/MTEXT proximos ("Nomear pelos textos...": filtro por layer e distancia, preferencia por texto alinhado com o eixo, previa antes de aplicar); vai para os alinhamentos do Civil.

Objetos do Civil (etapa 1): `C3DVIARIOCIVIL > Gerar` cria (sem site, para nao formar lotes) um alinhamento por eixo (`Eixo VIARIO n`, layer `VIARIO-C3D-EIXO`), um alinhamento offset dinamico por lado, no eixo inteiro, na face do meio-fio (`<eixo> - LD`, `- LE`, layer `VIARIO-C3D-BORDO`; alargamentos viram regioes do offset) e um curb return por esquina (`<eixo A> x <eixo B> - CR1`, layer `VIARIO-C3D-CR`) - alinhamento conectado do Civil quando a geometria confere, senao alinhamento tipo CurbReturn com o arco fixo. Depois disso os alinhamentos acompanham a rede sozinhos: o que nao mudou fica, o que mudou e refeito. Os nomes podem ser trocados no Civil. Perfis, amarracao de greides e ilhas como feature lines vem nas proximas etapas.

Secoes tem nome, tipo de via (Local, Coletora, Arterial, Transito rapido), faixas e velocidade; desenho novo comeca com Local, Coletora e Arterial com canteiro. O codigo P1, P2... e so interno.

Recomendacoes: o modulo confere a rede contra o DNIT IPR-718 (Manual de Projeto de Intersecoes) e IPR-740 (Travessias Urbanas) e mostra avisos discretos (atencao/informacao) com a fonte, no painel e na linha de comando quando um comando cria algo fora da recomendacao. Sao valores de referencia; nao substituem o projeto.

Sinalizacao previa (Manual Brasileiro de Sinalizacao de Transito, Vol. IV): divisoria amarela LFO (seccionada, continua ou dupla, continua antes da retencao), divisao de faixas LMS, linha de bordo LBO opcional, faixa de pedestres zebrada FTP-1 em cada aproximacao com esquina e linha de retencao LRE a 1,60 m. Cadencia pela velocidade da secao (< 60 km/h: 2/4 m; 60-80: 3/6 m; >= 80: 4/12 m e 0,15 m). Tudo num bloco na origem, layers VIARIO-SH-BRANCA e VIARIO-SH-AMARELA; o bloco acompanha a rede.

O motor geometrico (`ViarioGeometry`, `ViarioEngine`, `ViarioElements`, `ViarioTemplates`, `ViarioSignage`) e C# puro, testavel fora do Civil 3D.

## Visualizador de secao (C3DSECAO v0.3.0)

Janela propria (nao ocupa a viewport) com a secao de uma **caixa de corte**: linha em
planta (um ou mais trechos) + profundidade a frente/atras + faixa de elevacao + exagero.

- `C3DSECAOCORTE` (ou botao Nova caixa): clique os pontos da linha, informe as
  profundidades. A caixa fica numa polilinha na layer SECAO-CORTE (XData); em planta
  aparece o contorno e uma seta no sentido de quem olha. Mover os grips refaz a secao.
- No corte (traco cheio): superficies, corredores (interpolados entre estacas), tubos,
  solidos 3D, corpos/malhas/superficies do AutoCAD, blocos com solidos, feature lines e
  polilinhas 3D. Na profundidade (fantasmas): estacas e linhas longitudinais do corredor,
  tubos/estruturas, solidos e blocos (arestas + silhuetas), linhas e pontos COGO.
- **Faixa de vista por elemento** (aba Conteudo): Corte, Faixa (profundidade da caixa) ou
  Tudo (ignora a profundidade), por classe.
- Categoria **Corredor por estaca**: a janela vira o viewport do Section Editor
  (setas/slider trocam de estaca), sem prender o desenho.
- `C3DSECAODESENHAR`: desenha a secao mostrada no model (polilinhas 2D, layer SECAO-DESENHO),
  com texto na escala do desenho (1:N e mm no papel) e reguas sem encavalar.
- **Textos ▾**: escolhe o que aparece escrito (nomes, objetos, linhas, pontos, elevacao,
  fantasmas, reguas, titulo, legenda); **Ctrl + arrastar** move um rotulo (salvo por secao,
  com linha de chamada, tambem no desenho exportado).
- **Comparar secoes**: fixe secoes e sobreponha (alinhadas pelo eixo, centro ou inicio),
  com a diferenca de topo entre duas.
- **Vista 3D**: conteudo da caixa recortado nela (section box), com solidos e blocos,
  vistas prontas, estilos (sombreado, arestas, raio-x), perspectiva, objetos liga/desliga
  e imagem (copiar/salvar PNG).
- Botao direito num item: destacar, hachurar (regioes fechadas) ou ocultar. Corredor pode
  ser mostrado so pelo contorno (boundary verdadeiro dos links) ou por codigos de link.

Convencao: o inicio da linha fica a esquerda da tela; "frente" e o lado esquerdo da
linha (alem do plano), "atras" o direito. Inverter sentido troca os dois.

## Explorador de layouts (C3DLAYOUT, Layouts v1.8.1)

Modulo em `Modules/Layouts`, no mesmo molde do explorador da rede viaria. Categorias: Layouts (abrir, renomear em lote, reordenar, ordenar pelo nome, copiar, excluir), Viewports (travar, ligar, escala), Page setup (nomeado ou direto, importar de DWG/DWT), Numeracao de pranchas (ex-NP), Layer state nas viewports (ex-LSLAYOUTS), Carimbo em lote, Mapa-chave e Pranchas do eixo.

Vista grafica: um layout (miniatura com textos, cotas, tabelas, hachuras e blocos, inclusive dinamicos, sobre a folha branca do page setup com a area de impressao; zoom e pan) ou todos os layouts como cartoes. Clique no cabecalho de uma coluna da lista para filtrar/ordenar (como no Excel). Clique nas viewports para marcar; "repetir nos demais" marca a mesma posicao nos outros layouts; botao direito na viewport abre informacoes e edicoes rapidas (travar, ligar, escala, layer state, zoom, selecionar). Escalas no formato Custom scale do AutoCAD (2 = 1:500 em metros). Zoom na vista "Todos" com Ctrl+roda.

Numeracao: busca de textos (TEXT/MTEXT/atributos, com filtros); selecione na lista os textos que numeram as pranchas (Shift/Ctrl) e marque a caixinha. A aba Numerar usa os escolhidos, na ordem das abas, com um texto modelo: "PRANCHA: {000}/{T000}" vira "PRANCHA: 008/012" ({000} = numero com 3 algarismos, {T000} = total). O formato fica no desenho (o NP e as sheets usam o mesmo). Edicao de texto em lote. Prancha: bloco AX com estendimentos guardados por formato, inserido nos layouts marcados sem abri-los (`C3DPRANCHA` para clicar o ponto). Modo leve: desliga/religa as viewports dos layouts para montar as pranchas sem regenerar; opcionalmente congela layers (ex.: F-VT-NOTAVEL) e oculta as labels do Civil 3D no Model, e "Religar todas" desfaz so o que o plugin mexeu. Mapa-chave com hachura configuravel e rotulo como na LISP. Ctrl+Shift+L liga a aba Log.

Pranchas do eixo / Plan & Profile (`C3DPRANCHAS`): escolha o layout base (do desenho ou importado de DWG/DWT; viewport de planta e de perfil, layer states e page setup), o alignment e o trecho (lista de estacas com pontos notaveis) e o estilo do profile view; o plugin cria frames no tamanho da viewport de planta, uma prancha por frame e um profile view por folha no tamanho da viewport de perfil (calibrado pelo estilo; a origem do grafico fica a 20 mm da borda esquerda, ajustavel no passo 3, para caberem os titulos das bandas), com linhas de corte. No passo 3 escolhe-se quais profiles aparecem (Draw), o label set de cada um e quem e Profile1/Profile2 nas bandas; vale para os profile views criados pelo plugin, e "Aplicar o passo 3 nos profile views existentes" corrige os ja gerados (inclusive estilo e band set). Mover, girar ou esticar um frame no Model, ou editar o eixo, atualiza planta, estacas e cotas do perfil e as linhas de corte. A geracao mostra o progresso e pode ser cancelada ("Completar" termina depois).

```text
C3DLAYOUT          -> explorador de layouts
C3DLAYOUTVP        -> explorador na categoria Viewports
C3DLAYOUTPS        -> explorador na categoria Page setup
C3DLAYOUTLS        -> explorador na categoria Layer state (LSLAYOUTS faz o mesmo)
C3DLAYOUTCARIMBO   -> explorador na categoria Carimbo
C3DMAPACHAVE       -> [Base/Area/Inserir/Gerar/Atualizar/Remover/Painel] mapa-chave automatico
C3DPRANCHAS        -> [Painel/Atualizar/PontoPerfis] pranchas planta e perfil ao longo do eixo (frames proprios)
C3DPRANCHA         -> insere bloco de prancha (A0..A4 ou livre, com estendimentos) na origem ou no ponto clicado
C3DNORTE           -> [Tamanho/Girar] seta do norte no layout, girada pela viewport (Girar acerta todas)
ESCALAGRAF         -> escala grafica pela escala da viewport (ex-ESCALAGRAF_V2.LSP, mesmo fluxo)
NP                 -> [Configurar/Atualizar] numeracao de pranchas na linha de comando
```

## Utilitarios e LISPs (C3DLISP, LispTools v1.1.0, Utilitarios v1.0.0)

`Modules/Utilitarios`: rotinas que eram LISP e foram convertidas para C#, com o mesmo comando (ALLTORGB, TLEN, JOBECOUNT, SMARTHATCH, LSLAYOUTS, MOFF/MOFFSET, SEPARA_CURVAS_RAIO, KB/PP), mais o `C3DOBJLAYERS` (copia so a aba Object Layers das Drawing Settings de outro DWG/DWT). As maiores (QHPE, JPLUS, MAPACHAVE...) continuam como LISP embutida ate serem convertidas.

`C3DLISP` mostra tudo numa lista: icone, nome, comando e descricao; duplo clique executa.

### LISPs embutidas

Modulo em `Modules/LispTools`. As LISPs de `Modules/LispTools/Lisp` vao dentro da DLL (EmbeddedResource). Ao carregar o plugin elas sao gravadas em `%LOCALAPPDATA%\Civil3D2026Plugin\Lisp` e, em cada desenho, os comandos sao registrados com o `autoload` do AutoCAD: cada rotina so e carregada no primeiro uso do comando (rotinas que executam codigo ao carregar, como o NP, nao mexem em desenhos onde nao foram usadas). Arquivos compilados (VLX/FAS) sao carregados inteiros. A pasta e incluida em TRUSTEDPATHS para o SECURELOAD nao pedir confirmacao a cada arquivo.

```text
C3DLISP          -> lista de ferramentas (nativas e LISPs) com icone; duplo clique executa
C3DLISPLIST      -> a mesma lista na linha de comando
C3DLISPCARREGAR  -> regrava as LISPs e registra os comandos de novo no desenho atual
```

Para incluir uma LISP: copie o arquivo para `Modules/LispTools/Lisp`, coloque o cabecalho `;;@` (veja o README da pasta) e recompile. A lista, a arvore do C3DHELP e o carregamento se atualizam sozinhos.

## Base oficial de desenvolvimento

A partir da v2.8.0, use esta estrutura como base para todas as novas alteracoes.

Nao crie agrupamentos internos com nomes de tipos Autodesk (por exemplo `Modules.Corridor`).
O agrupamento de ferramentas de corredor e `Modules/CorridorTools`.

Antes de entregar/compilar uma atualizacao, execute `tools/ValidateProject.ps1`.

A DLL de saida continua centralizada em:

```text
NETLOAD\Civil3D2026Plugin.dll
```
