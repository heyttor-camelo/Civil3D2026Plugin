# Referencia: Network View do Project Explorer (perfil e planta)

Analise so de leitura (REA / metadados .NET, sem carregar nem copiar codigo) do Project Explorer for Civil 3D instalado nesta maquina, para orientar o monitor do HYDRO. Aqui ficam so nomes de classes, campos e recursos, e o que eles indicam sobre o desenho.

## Superficie de desenho

- WinForms + GDI+: um `PictureBox` (`pBoxNetworkView`) com barra de status (`ssNetworkView`, mostra estaca/cota sob o cursor e o comprimento do trecho selecionado, `lblNvShortestDistance`).
- A geometria nao e relida a cada pintura: fica em cache e so e refeita quando a rede ou a selecao muda (`updateNetworkViewGeometryCache`, `updateNetworkViewCacheAndPipeRunListView`). Campos do cache:
  - `networkView_PipeOutlines`, `StructureOutlines` (contornos reais das pecas no perfil);
  - `PipeCenterlines`, `StructureCenterlines`;
  - `ReferenceSurfacePoints` (terreno), `ComparedPoints` (perfil sobreposto);
  - `PipeCoverRuleRange` (faixa de recobrimento min/max), `pipeDropAtStructures` (degraus nas estruturas);
  - `PartIds`, `LengthEdgePad`;
  - estado da vista: estaca inicial/final, cota min/max, escala horizontal/vertical, `ZoomAffectsHoriz/VertAxis`, `DragCoords/DragStart`.

## Ordem de desenho (`redrawNetworkView`)

1. Grade
2. Superficie de referencia (terreno com sombreamento em gradiente: "Ground Shading")
3. Zona de recobrimento (faixa min/max de recobrimento do tubo)
4. Eixos
5. Pecas da rede (contornos de tubos e estruturas, primaria x secundaria)
6. Linhas de centro das pecas
7. Objeto comparado (perfil sobreposto)
8. Simbolos (marcador de realce no ponto)
9. Textos

## Ferramentas auxiliares (classe `CvxViewerTools`)

Nomes que mostram os recursos do perfil: ordenadas verticais ate a cota do ponto ou ate o topo do canvas, ticks verticais, simbolo de realce no ponto, preenchimento de poligono com gradiente, plotagem de estaca e de cota, zoom pela roda, conversao canvas -> espaco do modelo baseada em perfil, texto inclinado entre dois pontos (rotulo ao longo do tubo) e checagem de cota min/max da vista.

## Barra e menus

- Escala vertical em lista de valores pre-definidos (`ddNvScaleFactor`, `ViewerVerticalScaleFactors`).
- Modo do zoom: tudo, so estaca, so cota.
- Liga/desliga: grade, eixos, texto, zona de recobrimento, linhas de centro, ordenadas, ticks.
- Menu de contexto: copiar imagem, zoom extents, zoom na janela, centralizar, opcoes; "isolar tubo na vista", "travar perfil no tubo selecionado", mostrar crown/invert, degrau (pipe drop), perfis sobrepostos.

## Listas

`CvxListViewTools`: ListView em modo virtual, debounce da selecao por timer, nomes encurtados pela largura disponivel, linha de totais na barra de status, cores para valores editaveis e tooltip das violacoes.

## Preferencias (`NetworkView*`)

Cores (eixo, fundo, linhas de centro, superficie comparada, zona de recobrimento, grade, realce, peca primaria/secundaria, superficie de referencia, texto), espacamento da grade H/V, tamanho do marcador, exagero de escala, mostrar eixos/centros/grade/zona/texto, tamanho do tick, zoom afeta H/V, suavizacao e densidade minima da grade.

## O que o HYDRO adotou (v0.9.0)

| Project Explorer | HYDRO monitor |
|---|---|
| Ground Shading | solo sombreado abaixo do terreno (gradiente) |
| Pipe Cover Zone | faixa de recobrimento minimo (`Criteria.MinCover`) |
| Pipe/StructureOutlines | tubo com parede e interior; estrutura na largura real |
| Lista de escala vertical | exagero arredondado + Shift+roda |
| Ordenadas | ordenada da faixa de nomes ate a tampa |
| Texto inclinado entre pontos | rotulo do tubo ao longo da geratriz superior |
| Cache de geometria | lotes por cor, cache de texto, redesenho agrupado por quadro |
| Barra de status com estaca/cota | `dist`/`cota` reais sob o cursor |

A faixa de dados embaixo (Dist./Terreno/Tampa/Fundo/Prof.) e a lamina calculada / piezometrica sao do HYDRO (o Project Explorer nao calcula hidraulica).
