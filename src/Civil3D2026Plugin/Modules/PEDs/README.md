# PEDs - locação de pontos de embarque e desembarque

Leva a planilha de proposta de PEDs (ex.: `(BRT Amazonas) Proposta PEDs Barreiro_rev4`)
para a planta: um bloco `PED` em cada ponto e uma ficha que abre ao clicar.

## Uso

1. Salve o DWG (as imagens vão para `PEDs_img\` ao lado dele).
2. `PEDIMPORTAR` → escolha o `.xlsx`, depois o `.kmz` (ou `Nenhum`), escala do bloco.
3. `PEDFICHA` → deixe a janela aberta e clique nos blocos PED no desenho.
4. Planilha revisada? `PEDATUALIZAR` (não mexe na posição dos blocos).
5. PED sem ponto no KMZ, ou para ajustar posição/rotação: `PEDLOCAR`.

## Arquivos

| Arquivo | Papel |
|---|---|
| `PedXlsxReader.cs` | Lê o .xlsx sem dependências: shared strings, valores em cache, imagens na célula (`xl/metadata.xml` + `xl/richData`). |
| `PedKml.cs` | Pontos do .kmz/.kml (nome + lon/lat), ignora placemarks repetidos. |
| `PedFicha.cs` | **Mapa de colunas da ficha** (letras da aba de dados). Se a planilha mudar de layout, ajuste aqui. |
| `PedStore.cs` | Dados no DWG: NOD `C3D_PED_V1` (META, COLS, P_código) e XData `C3DPED` no bloco. |
| `PedBlock.cs` | Bloco `PED` (cria se não existir) e atributos. |
| `PedGeo.cs` | lon/lat → desenho (geolocalização, sistema do Civil ou fuso UTM). |
| `PedImages.cs` | Extrai as imagens para JPEG (máx. 1600 px). |
| `PedPanel.cs` | Janela da ficha (segue a seleção do desenho). |
| `PedCommands.cs` | Comandos. |

## Notas

- Os dados da ficha ficam também em **atributos invisíveis** do bloco (tags `VIA`, `CALCADA_ATUAL`, `CALCADA_PROP`, ..., `FOTO`, `MAPA`) e há um hyperlink para a foto: quem não tem o plugin vê no Properties / duplo clique / Data Extraction. Para levar o projeto, copie o DWG **junto com a pasta `PEDs_img`**.

- A importação confere os cabeçalhos das colunas usadas pela ficha e avisa se o layout mudou.
- O vínculo bloco → PED é o XData `C3DPED` com o código: copiar o bloco copia o vínculo.
- PEDs que saem da planilha são removidos da base no `PEDATUALIZAR`; os blocos ficam (sem ficha).
