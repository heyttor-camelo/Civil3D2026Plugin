# HYDRO — roteiro de teste manual no Civil 3D

Versao: HYDRO v0.5.0 (fases H0 a H4), plugin 2.60.4. Nada disto foi executado dentro do Civil 3D:
o codigo compila sem avisos novos e as classes de calculo passam nos testes, mas a interacao com
a API da Pipe Network so se confirma no CAD. Siga na ordem; cada passo diz o que conferir.

## 0. Preparo (5 min)

1. Compile: `tools\Build.ps1` (ou `tools\Build.ps1 -AcadDir <pasta com as DLLs .NET 8>` se o
   Civil 3D instalado roda em .NET 10). `NETLOAD` em `NETLOAD\Civil3D2026Plugin.dll`.
2. Confira no banner: `Hydro` com cinco modulos (`Apoio a Pipe Network`, `Edicao no model`,
   `Bacias`, `Calculo e verificacao`, `Lancamento`) e a versao 0.5.0. Na Ribbon, aba C3D Tools,
   paineis `Hydro · ...` com os pictogramas (ciano/branco). Se a Ribbon ja existia, `C3DRIBBONRESET`.
3. Abra um desenho do template do Civil (metrico) que tenha uma **parts list** com tubos de
   concreto e estruturas (PV/caixa) e, de preferencia, uma **superficie**. Sem parts list nada
   do HYDRO cria objetos.
4. `C3DLOG` aberto ao lado (ou Ctrl+Shift+L no painel): tudo que falhar aparece la.

## 1. H4 primeiro: lancar uma rede de teste (10 min)

1. `HYLANCAR`. Sem rede no desenho ele cria uma com a primeira parts list (confira a mensagem).
   Escolha a familia/tamanho da estrutura e do tubo pelos numeros; declividade 0,5 %; profundidade 1,5.
2. Clique 5 pontos em linha (PV1..PV5, uns 40 m entre eles). A cada clique deve aparecer a
   linha elastica desde o ultimo PV e a mensagem com fundo e tampa. Teste `D` (Desfazer) uma vez
   e continue. Enter termina.
   - Conferir no Prospector: 5 estruturas e 4 tubos na rede; cada tubo com Start em um PV e End
     no seguinte (Pipe Properties > Part Properties: Start/End Structure), Flow Direction
     Start to End, inverts descendo 0,5 %.
   - Conferir que **um unico UNDO** desfaz a cadeia inteira.
   - Se a rede nao tem superficie: tampa do primeiro PV = 100,000 (avisado na linha de comando).
3. `DRENNUM` para numerar (opcional) e `C3DHYDRO`.

## 2. H0: painel (10 min)

1. `C3DHYDRO`: janela "HYDRO - Pipe Network [desenho]". Categorias com contagem: Redes (1),
   Estruturas (5), Tubos (4), Bacias (0), Calculo, Verificacao (n), Saidas.
2. Redes: a lista mostra a rede, parts list, superficie e "ok" (ou "N tubo(s) sem sentido").
   Monitor: planta esquematica com circulos (PVs), tubos com seta no sentido do fluxo, nomes.
   Roda = zoom, arrastar = pan, duplo clique = enquadrar de novo. Passe o mouse sobre um tubo:
   a barra do monitor mostra nome, D, L, i.
3. Estruturas: planilha de montante para jusante (tampa, fundo, prof., tubos "0>1", "1>1"...).
   Clique PV3: Propriedades a direita; abas Conexoes (tubos entrando/saindo), Hidraulica, Regras.
   Marque "Perfil do caminho" na barra do monitor: perfil de PV3 para jusante (terreno verde,
   tubos em ciano, PVs em branco). Desmarque.
4. Tubos: planilha com de > para, D, L, i, fundos, recobrimento. Duplo clique numa linha = zoom
   no desenho. Botao direito > Selecionar no desenho.
5. Preferencias... > Tema escuro: a janela refaz no tema escuro (menus legiveis). Esc **nao**
   fecha a janela.
6. Feche e abra de novo com `C3DHYDROTUBOS`: abre direto em Tubos.

## 3. H1: edicao em lote e no model (20 min)

Painel, Estruturas:
1. Clique PV2, mude "Fundo (sump)" para o valor atual - 0,30, Enter. Mensagem "Editar PV2...".
   Conferir: fundo do PV2 mudou; os dois tubos ligados mantiveram a outra ponta (declividade
   mudou). UNDO desfaz tudo de uma vez.
2. Marque PV3, PV4 e PV5 (caixinhas). As Propriedades viram "Edicao em lote: 3 estrutura(s)".
   Tampa `+0.10`, Aplicar nos marcados: as tres tampas sobem 0,10. Prefixo `PV-` + Aplicar:
   renomeia PV-01, PV-02, PV-03.
3. Barra de comandos: "Editar em lote ▾" > "Aplicar regras do Civil" nos marcados (so confere
   que nao da erro; o rule set pode mudar fundos).

Painel, Tubos:
4. Clique um tubo, "Declividade [%]" = 1,0, Enter: a ponta de montante fica, a de jusante desce.
5. Marque dois tubos > "Inverter sentido": as setas do monitor invertem; desfaca (Inverter de novo).

No model (botao direito sobre o objeto abre o menu **HYDRO**; ou digite o comando):
6. `HYFUNDO` num PV: mostra tampa/fundo, pergunta Outraponta/Declividade e o novo fundo.
   Teste `Declividade`: os tubos ligados sobem/descem inteiros.
7. `HYTAMPA` em varios PVs: `Superficie` (precisa de superficie de referencia na rede; senao
   avisa), depois `Rebaixar` 0,05.
8. `HYDECLIV` num tubo segurando `Jusante`: a ponta de jusante fica e a de montante sobe.
9. `HYTROCAR` em dois tubos: escolha outra familia/tamanho pelos numeros. Conferir no
   Prospector que a familia mudou, as conexoes continuam e os fundos ficaram iguais.
10. `HYIGUALAR`: origem um tubo, destino os outros, `Declividade`: todos com a mesma declividade.
11. `HYCONECTAR`: clique perto da ponta final do ultimo tubo, Enter (desliga); depois de novo e
    clique o PV (religa). Conferir Start/End Structure no Prospector.
12. `HYMOVER` no PV3, `Fundo`, clique um ponto deslocado 10 m: os dois tubos acompanham e os
    fundos ficam; com `Declividade` os fundos mudam e as declividades ficam.
13. `HYINSERIR`: clique o tubo PV1-PV2 e um ponto no meio dele; aceite a familia padrao.
    Conferir: tubo dividido em dois, PV novo no meio com fundo no fundo dos tubos, sentido do
    fluxo igual ao original, declividade igual.
14. `HYREMOVER` nesse PV novo: o tubo que entra e esticado ate PV2 e o outro apagado.
15. `HYCAMINHO`: clique PV1 e PV5: os tubos e PVs do caminho ficam selecionados e a extensao
    aparece na linha de comando.
16. Dica no cursor: pare o mouse sobre um tubo e sobre um PV (sem comando ativo): tooltip
    "HYDRO nome ..." com fundos, D, i, recobrimento (tubo) ou tampa/fundo/profundidade (PV).

## 4. H2: bacias (10 min)

1. Desenhe 3 polilinhas fechadas (LWPOLYLINE) de uns 50 x 60 m perto de PV1, PV2 e PV4.
2. `HYBACIA`: selecione as tres, grupo `HYDRO`, C 0,80, Tc 10, Enter na estrutura.
   Conferir: catchments Bacia-01..03 no Prospector (Catchment Groups > HYDRO), com Runoff
   Coefficient 0,80 e Time of Concentration 10 (User-Defined).
3. `HYLIGARBACIA`: Bacia-01 > PV1, Bacia-02 > PV2, Bacia-03 > PV4, Enter. No painel, Bacias
   mostra a estrutura de cada uma; o monitor liga cada poligono ao PV com linha tracejada.
   Estruturas e Tubos mostram "A acum. [ha]" somando a montante (PV5: as 3 bacias).
4. Painel > Bacias > marque Bacia-02 > "Calcular Tc ▾" > Kirpich: `HYTC` pede comprimento do
   talvegue e desnivel (ex.: 120 m e 2 m): Tc calculado e gravado (com o minimo de projeto).
5. Na aba Propriedades de uma bacia, mude C para 0,9 e Enter; mude a "Estrutura de destino"
   pelo menu e Enter: o catchment acompanha.

## 5. H3: calculo e verificacao (15 min)

1. Painel > Calculo: escolha a IDF (menu da primeira linha) ou digite K, a, b, c; Tr 10; Tc min
   10; diametros `300, 400, 500, 600, 800`; "Salvar no desenho". O monitor mostra as curvas IDF.
   Feche e reabra o painel: os parametros voltam (NOD).
2. `HYCALCULAR` (ou "Calcular tudo" em Redes). Linha de comando: resumo com a vazao do ultimo
   trecho em m3/s. Conferir:
   - Tubos: colunas Q, y/D, V preenchidas e crescendo para jusante; aba Hidraulica com a matriz
     diametro x declividade (verde = atende, amarelo = lamina/velocidade fora, vermelho = cheio);
   - Pipe Properties nativo (Part Properties > Hydraulic): Flow Rate, HGL Up/Down, EGL Up/Down
     preenchidos; Structure Properties: HGL/EGL;
   - Property Set `HYDRO` no tubo (Properties palette > Extended Data): Q_m3s, Area_ha, C,
     Tc_min, I_mmh, Lamina_yD, V_ms, Qplena_m3s, Avisos;
   - tooltip do tubo agora com Q, A, y/D e V.
3. Verificacao: lista de violacoes (se os tubos estao com 0,5 % e D pequeno, deve haver
   lamina/velocidade). Clique uma > "Ir para" (zoom) e "Corrigir ▾" > "Trocar pelo menor diametro
   que atende": o tubo muda de tamanho mantendo os fundos; recalcule e a violacao some.
4. `HYDIMENSIONAR`: previa por trecho (ok / AUMENTAR / pode reduzir), `Aumentar`: troca e
   recalcula.
5. Saidas: planilha de dimensionamento; "Exportar ▾" > XLSX (abre no Excel com duas abas) e
   HTML; "Rotulos ▾" > tubos e estruturas (rotulos nativos com o estilo padrao da rede);
   `HYTABELA` com altura 2,5 e um ponto livre: tabela AutoCAD com a planilha.
6. UNDO depois do calculo: desfaz vazoes, HGL e Property Sets de uma vez.

## 5b. H5: catalogo CSBIM (15 min)

Roteiro completo em `docs/HYDRO-CATALOGO.md` §4: `HYCATALOGO` (listar, Definir, Regenerar,
Importar para a parts list CSBIM por capitulo), parametros de faixa por estrutura e em lote,
`HYLANCAR` com as familias CSBIM e o aviso "CATALOGO: ..." no painel.

## 5c. §9: medidas das estruturas como variaveis e regras associativas (20 min)

Use uma rede com PV DNIT (ou qualquer estrutura com medidas de faixa/lista) e tubos de Ø diferentes.

1. `C3DHYDRO` > Estruturas, selecione um PV. Em Propriedades, "Medidas da peca": SIL, SIW, AC...
   em **metros** (1,200, nao 1200), com a faixa ou a lista no rotulo.
2. **Valor fixo:** SIW = `1,4` + Enter. A peca muda no desenho na hora; um `U` desfaz.
   `+0,2` soma ao atual. Valor fora da lista/faixa: mensagem na barra de status, nada gravado.
3. **Regra:** SIW = `=teto(dmax + 2*0,15; 0,2)` + Enter. O campo fica destacado e o rotulo mostra
   o valor. SIL = `=siw`. Conferir no desenho as duas medidas.
4. **Associativo:** troque o maior tubo ligado por um Ø maior (`HYTROCAR` ou Pipe Properties).
   No fim do comando a linha de comando deve dizer "[HYDRO] regras: 1 estrutura(s) refeita(s)" e o
   PV deve crescer. `U` uma vez desfaz a troca e a peca volta junto (se nao voltar, anotar).
5. **Fundo e tampa por regra:** fundo = `=fundo_min - 0,05`; mude o fundo de um tubo ligado
   (`HYDECLIV` ou grip) e veja o fundo do PV acompanhar. tampa = `=sup + 0,02` com superficie.
6. **Lote:** marque 3 PVs, AC = `=se(prof > 2; 1; 0,6)` em "Medidas da peca comuns" > Aplicar.
7. **Modelo:** num PV com regras, "Salvar as regras como modelo" = `PV DNIT padrao` + Enter.
   Marque outros PVs > "Modelo de regras" > `PV DNIT padrao`: todos recalculam. A coluna
   **Regras** da lista mostra o modelo. Botao direito na lista > Colunas da peca > SIL e SIW.
8. **Desligar:** botao "Regras automaticas" (barra de Estruturas) ou `HYREGRASAUTO` > Desligar.
   Troque um tubo: nada muda. Botao "Atualizar regras" (ou `HYREGRAS`, Enter = todas) aplica.
9. **No desenho:** botao direito numa estrutura > "Regra de medida..." (`HYREGRA`): lista
   medidas, regras e variaveis; digite `SIW` e a regra. Regra com erro (`=dmax +`) e recusada.
10. Catalogo CSBIM **sem saneamento** (listas): a regra pega o menor valor da lista que atende e
    avisa "passa do maior valor da lista" se nao couber.

O que pode falhar aqui: `Part.PartData` com setter pode nao regenerar o solido (alternativa: trocar
o tamanho); o `ObjectModified` pode disparar em comandos que so leem (inofensivo: nada muda);
o UNDO da troca de tubo e do recalculo sao dois passos (o recalculo roda depois do comando).

## 5d. v0.8.0: controle total, lista editavel, peca 3D, monitor (15 min)

1. Abra uma rede grande. Arraste e role no monitor: deve ficar fluido. ZOOM/PAN no desenho nao devem mais "piscar" o painel.
2. Passe o mouse num tubo/estrutura do monitor: destaca e mostra os dados embaixo. Clique: a peca fica selecionada na lista.
3. Botao **Cor** > Lamina / Velocidade / Recobrimento / Declividade: tubos pintados e legenda no canto.
4. Estruturas: clique na celula **Tampa** de uma linha, digite `+0,10` e Enter: so ela sobe 10 cm. Marque 3 linhas, edite a celula **Prof.** de uma delas com `1,50`: as 3 ficam com 1,50 m.
5. Tubos: celula **i** = `1` (1 %) e celula **D** = `400`: confere no desenho.
6. Aba Propriedades de um PV: "Medidas fixas da familia" mostra WTh/FTh; troque WTh para `0,25` e Enter. Anote se o Civil aceitou (o solido muda) ou recusou (mensagem na linha de comando).
7. Abra "Todas as propriedades do objeto": troque o estilo, a camada e a rotacao (graus). Num tubo, troque o estilo e a superficie de referencia.
8. Botao **Peca 3D** num PV: gire e de zoom; clique numa cota (SIL/SIW...), digite um valor e veja a previa; "Aplicar no desenho".

## 5e. v0.9.0: monitor e perfil (10 min)

1. Planta de uma rede grande: nomes das estruturas com fundo, sem sobrepor; nomes dos tubos ao longo do tubo. De zoom: aparecem `h` das estruturas e D/i dos tubos longos.
2. Ligue **Perfil do caminho** e clique numa estrutura de montante: faixa de nomes no topo, faixa de dados embaixo (Dist./Terreno/Tampa/Fundo/Prof.), solo sombreado, faixa de recobrimento minimo, tubos com parede e lamina azul (depois do calculo), piezometrica tracejada.
3. Passe o mouse no perfil: a cota mostrada embaixo deve bater com a cota real (antes saia multiplicada pelo exagero). Hover destaca tubo/estrutura; clique seleciona na lista e o perfil continua no mesmo caminho.
4. **Shift+roda** no perfil: o exagero muda (aparece na barra). Shift+duplo clique: volta ao automatico.
5. Monitor baixo (arraste o divisor): a faixa de dados reduz para 3 linhas e some abaixo de ~200 px.
## 6. Regressao rapida

- `DRENNUM` e `DRENEXCEL` continuam funcionando pelos botoes de Redes.
- `C3DMODULOS`: desmarque Hydro, `C3DRIBBONRESET`: os paineis somem; na proxima carga a dica
  no cursor e o menu de contexto nao sao registrados.
- Abra um segundo desenho: o painel acompanha o desenho ativo (titulo muda, lista refaz).

## O que mais pode falhar no CAD (por ordem de probabilidade)

1. **`Network.AddLinePipe` / `AddStructure` / `BreakPipe`** (HYLANCAR, HYINSERIR): a assinatura
   foi conferida no ILSpy, mas o comportamento (sentido, conexao automatica, `applyRules=false`)
   so se ve rodando. Se o tubo nascer desligado, o `ConnectToStructure(..., force: true)` logo
   depois deve ligar; se nascer com elevacoes diferentes, o comando repoe Start/EndPoint.
2. **`SwapPartFamilyAndSize`** (HYTROCAR, HYIGUALAR, Corrigir): pode mudar o eixo do tubo; o
   plugin repoe os fundos logo depois. Conferir se as conexoes ficam.
3. **`SetSlopeHoldStart/End`**: o sinal segue o DRENEXCEL (negativo descendo quando a ponta
   segurada e a montante). Se a declividade sair invertida em algum caso, o lugar e
   `HydroEditor.SetSlope`/`EditPipe`.
4. **`FlowDirectionMethod`** para inverter: na API 2026 `FlowDirection` nao tem setter; se o
   Civil nao respeitar `StartToEnd`/`EndToStart` (ex.: rule set forcando "by slope"), inverter nao
   tera efeito visivel.
5. **Property Set HYDRO** em objetos do Civil: `SetAppliesToFilter` com os nomes de classe
   `AeccDbPipe`/`AeccDbStructure`; se o AEC recusar, o calculo continua (aviso no log) e so o
   Property Set falta.
6. **Setters de `FlowRate`, `HydraulicGradeLine*`, `EnergyGradeLine*`**: existem, mas o Civil pode
   ignorar/sobrescrever em algum caso (ex.: Storm Sewers migration). Cada gravacao e isolada
   (`Try`), com diagnostico no log.
7. **`Catchment.Create` com `styleId`/`surfaceId` nulos** (HYBACIA): se recusar, passar o estilo
   padrao de catchment (`cdoc.Styles.CatchmentStyles[0]`).
8. **`ReferenceDischargeObjectId`** (ligar bacia): substitui a propriedade obsoleta; se nao
   aceitar um Structure diretamente, o Prospector mostrara a bacia sem destino.
9. **Menu de contexto** (`AddObjectContextMenuExtension` nas classes do Civil): pode nao aparecer
   se o Civil registra o proprio menu por cima; os comandos continuam pelo teclado.
10. **Painel com o desenho fechando** (`BeginDocumentClose`): se der excecao, feche o painel
    antes de fechar o desenho e registre no log.
11. **Tabela** (`HYTABELA`): `Table.Cells[...]` e `GenerateLayout` dependem do estilo de tabela
    corrente; se der erro, troque o estilo (`TABLESTYLE`).
12. **Ribbon**: 5 paineis Hydro podem alargar a aba; se incomodar, juntar modulos no catalogo.
