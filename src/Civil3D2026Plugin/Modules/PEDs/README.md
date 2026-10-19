# PEDs - locação de pontos de embarque e desembarque

Leva a planilha de proposta de PEDs (ex.: `(BRT Amazonas) Proposta PEDs Barreiro_rev4`)
para a planta: um bloco `PED` em cada ponto e uma ficha que abre ao clicar.

## Uso

1. Salve o DWG (as imagens vão para `PEDs_img\` ao lado dele).
2. `PEDIMPORTAR` → escolha o `.xlsx`, depois o `.kmz` (ou `Nenhum`), escala do bloco.
3. `PEDFICHA` → deixe a janela aberta e clique nos blocos PED no desenho.
4. Planilha revisada? `PEDATUALIZAR` (não mexe na posição dos blocos).
5. PED sem ponto no KMZ, ou para ajustar posição/rotação: `PEDLOCAR`.
6. Trocar a vista: `PEDVISTA` → Ponto / Ficha / Foto (todos ou selecionados), ou pelas layers.
7. Escolher as linhas da ficha (e foto/mapa): `PEDCAMPOS`.

## Na planta (sem precisar do plugin)

- **Símbolo** `PED` (layer `PED`): placa de ponto de ônibus com o código; atributos **N** e **E** ao lado (campos com o ponto de inserção: atualizam ao mover + REGEN); hyperlink para a foto (Ctrl+clique).
- **Ficha**: multileader com seta no símbolo; o conteúdo é o bloco `PED_FICHA_código` (tabela + foto + mapa). Arraste o grip do multileader para mover a ficha: a linha de chamada acompanha.
- **Esconder um PED**: `LAYOFF` (ou `LAYFRZ`) clicando na ficha → desliga `PED-FICHA-código`; clicando na foto → só `PED-FOTO-código`.
- **Esconder todos**: gerenciador de layers → filtro **PED - fichas** (ou **PED - fotos**) → selecionar tudo → desligar.
- Com o plugin, `PEDVISTA` faz o mesmo (Ponto / Ficha / Foto, todos ou selecionados).

## Layers

| Layer | Conteúdo |
|---|---|
| `PED` | símbolo |
| `PED-FICHA-código` | multileader e tabela daquele PED |
| `PED-FOTO-código` | foto e mapa daquele PED |

## Arquivos

| Arquivo | Papel |
|---|---|
| `PedXlsxReader.cs` | Lê o .xlsx sem dependências: shared strings, valores em cache, imagens na célula (`xl/metadata.xml` + `xl/richData`). |
| `PedKml.cs` | Pontos do .kmz/.kml (nome + lon/lat), ignora placemarks repetidos. |
| `PedFicha.cs` | **Mapa de colunas da ficha** (letras da aba de dados). Se a planilha mudar de layout, ajuste aqui. |
| `PedStore.cs` | Dados no DWG: NOD `C3D_PED_V1` (META, COLS, P_código) e XData `C3DPED` no bloco. |
| `PedBlock.cs` | Bloco `PED` (placa de ônibus), atributos PED / N / E (campos). |
| `PedFieldsWindow.cs` | Janela do `PEDCAMPOS`. |
| `PedFichaBlock.cs` | Bloco da ficha por PED (tabela + raster) e multileader; layers e filtros. |
| `PedPhotos.cs` | Definições das imagens; limpeza do que a 1.2 criava. |
| `PedGeo.cs` | lon/lat → desenho (geolocalização, sistema do Civil ou fuso UTM). |
| `PedImages.cs` | Extrai as imagens para JPEG (máx. 1600 px). |
| `PedPanel.cs` | Janela da ficha (segue a seleção do desenho). |
| `PedCommands.cs` | Comandos. |

## Notas

- Para levar o projeto, copie o DWG **junto com a pasta `PEDs_img`** (imagens das fichas).

- A importação confere os cabeçalhos das colunas usadas pela ficha e avisa se o layout mudou.
- O vínculo bloco → PED é o XData `C3DPED` com o código: copiar o bloco copia o vínculo.
- PEDs que saem da planilha são removidos da base no `PEDATUALIZAR`; os blocos ficam (sem ficha).
