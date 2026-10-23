# Placas de sinalizacao vertical no Viario (C3DVIARIOPLACAS)

Modulo: `Modules/Viario/ViarioPlacas.cs` (modelo, leitura do catalogo e amarracao, C# puro) e
`Modules/Viario/ViarioPlacaSync.cs` (blocos, atributos, XData, comando). Viario v0.11.0.

## O que o SinC insere (observado num DWG gerado pelo SinC, out/2026)

Para cada placa, tres blocos:

| Bloco | Layer | Conteudo |
|---|---|---|
| `T-xxx_SV` | `sinC_VERTICALP` | geometria do `C:\sinC\Local\Placas\Placas 2D\T-xxx_SV.dwg` (hachuras; **escala 5**: placa de 4,00 x 1,65 m desenhada com 20 x 8,25 unidades, origem no canto superior esquerdo) + 15 `ATTDEF` na layer `sinC_ATT_Placa` |
| `sinC_LDC` | `sinC_LinhaDeChamada` | polilinha (0,0) -> (0,-0,5) -> (5,657; -6,157): linha de chamada |
| `<suporte>_SVS` (ex.: `Coluna Dupla_SVS`, bloco dinamico) | `sinC_VERTICALS` | simbolo do suporte + atributos Altura, Tipo, Nort, Est, Eixo, Estaca/KM, Lado, Material, Secao, Situacao (`sinC_ATT_Suporte`) |

Atributos do bloco da placa (altura 1,0; `W` = largura do bloco em unidades):

- visiveis, a direita da placa: `Código` (W+1; -0,9), `Situação` (W+1; -2,7), `Nort` (W+1; -4,5), `Est` (W+1; -6,3);
- invisiveis em (-15; -5): `Área` (dm2: 5,1 x 1,8 m -> `918`), `Dimensão` (`L = 5,1x1,8m`), `Eixo`, `Estaca/KM`, `Lado`,
  `Película_Fundo` (`Tipo III`), `Película_Orla` (`Tipo X`), `SI` (`Não`), `Substrato` (`Aço`), `Suporte` (`Coluna Dupla`),
  `Handle_Suporte` (handle do bloco do suporte).

Posicoes relativas (no sistema local girado pela rotacao da via; `P` = ponto de implantacao, onde fica o suporte):

- `sinC_LDC` em `P + (0; -0,275)`;
- placa (origem do bloco = canto superior esquerdo) em `P + (5,657; -6,432)`, isto e, na ponta da linha de chamada;
- todos com a mesma rotacao (alinhamento "Perpendicular" a via) e escala 1 (campo "Escala da Placa").

O modulo reproduz os dois primeiros. O suporte fica para o SinC (`Suporte` recebe o nome a partir do codigo do
projeto, `Handle_Suporte` fica vazio). Nao se sabe se o SinC exige algum registro alem dos atributos para
tratar a placa como sua; conferir no SinC depois de inserir.

## Amarracao ao eixo

- Eixo mais proximo (`GPath.Project`) ate a distancia maxima (padrao 60 m).
- Estaca de 20 m a partir do inicio do eixo (`12+5.30`), km = s / 1000.
- Lado pelo sentido do eixo: `Cross(tangente, P - pe) >= 0` = esquerda (LE), senao direita (LD).
- Rotacao do bloco: direcao do eixo - 90 graus (placa a direita) ou + 90 (a esquerda); assim o eixo X local aponta
  para fora da pista e a linha de chamada sai do ponto para o lado da placa.
- `ATualizar` refaz eixo, estaca, lado e os atributos Eixo/Estaca/KM/Lado sem mover a geometria.
- `NOmear`: nome do eixo = rota(s) + " - " + descricao do trecho das placas amarradas (so eixos sem nome, ou todos).

XData do bloco da placa: RegApp `C3DVIARIO`, strings `PLACA` + campos separados por `~|~` em pedacos de 250
caracteres (id, codigo SinC, trecho, rota, sentido, item, E, N, largura e altura SinC, chave do eixo, s, afastamento,
lado, rotacao, suporte, dimensao e area do projeto, descricao do trecho, mensagens). A linha de chamada leva
`PLACALDC` + id. O `ViarioSync` ignora blocos, entao esses objetos nao entram na rede.

## Catalogo (entrada)

JSON (gerado da planilha do projeto; `placas_chapada.json`):

```json
{
  "crs": "SIRGAS 2000 / UTM 21S (EPSG:31981)",
  "pastaSinC": "C:\\sinC\\Local\\Placas\\Placas 2D",
  "fonte": "Projeto Executivo Volume I-C",
  "placas": [
    {
      "Id": "T01-S1-01", "Trecho": "T01", "Rota": "MT-020/251", "TrechoDescricao": "Trevo da Guia acesso ao Entr. MT-351",
      "Sentido": "S1", "Item": 1, "PaginaPdf": 16, "E": 605477.6411, "N": 8310515.0715,
      "Linhas": [ { "Mensagem": "Chapada dos Guimarães", "Traducao": "", "Orientacao": "Em Frente" } ],
      "DimensoesProjeto": "3,573 x 1,31 m", "AreaProjetoM2": "4,68", "Suporte": "CD2", "AlturaSuporte": "2,80",
      "SinC": { "Arquivo": "T-001", "Dwg": "T-001_SV.dwg", "LarguraM": 4.0, "AlturaM": 1.65 }
    }
  ]
}
```

CSV `;` com cabecalho (`placas_projeto_x_sinc.csv`): `id_projeto`, `trecho`, `rota`, `sentido_item`, `sinc`,
`dim_sinc`, `dim_projeto`, `area_m2`, `E`, `N`, `mensagens_projeto`, `pag_pdf` (numeros com virgula aceitos).

A pasta dos `T-xxx_SV.dwg` e pedida no comando (padrao: `pastaSinC` do JSON). Cada arquivo e importado uma vez
como definicao de bloco (`Database.Insert`), e os atributos sao acrescentados se o bloco ainda nao os tiver.

## Suportes

Tipo pela area da placa no SinC (`Opcoes.AreaSimples` = 2,00 m2, `AreaDuplo` = 6,00 m2, `LarguraPortico` = 6 m):
coluna simples, coluna dupla, semi-portico ou portico. Blocos gerados em codigo (`EnsureSupportBlock`) no molde do
`Coluna Dupla_SVS` observado: geometria em `sinC_Suporte` (postes = circulo + hachura solida, barra = polilinha fechada),
atributos Altura (1; 0), Tipo (1; -1,8), Nort (1; -3,6), Est (1; -5,4) visiveis e Eixo, Estaca/KM, Lado, Material,
Secao, Situacao invisiveis em (-15; -5), layer `sinC_ATT_Suporte`. O bloco e inserido no ponto de implantacao com a
rotacao da placa e XData `PLACASUP` + id; a placa recebe `Handle_Suporte` (handle do suporte) e `Suporte` (tipo).
Portico: vao = 2 x afastamento da placa ao eixo (arredondado), um bloco por vao (`Portico_SVS_<vao>`).

Nort/Est (placa e suporte): `N: 8334137,17` / `E: 622959,92`.

## Importar da pasta do SinC (`PAsta`)

`ViarioPlacas.LoadSinCFolder`: cada `.pla` e um zip com `INFO.json` (Dimension "4x1.65", Description "Trecho: ... /
Local: E (E), N (N)") e `COD.placa` (XML; `<Texto Texto="...">` sao as mensagens, `<Placa Nome="T-001">`). Sem
coordenada na descricao, a placa e completada pelo catalogo do projeto (`Merge`, pelo codigo SinC); um arquivo que
serve a varias placas do projeto vira uma copia por placa. Placas ainda sem coordenada ficam de fora.

## Atrativos turisticos (`C3DVIARIOATRATIVOS`, `ViarioAtrativos.cs`)

KML/KMZ -> `AtrativoItem` (nome sem o DMS colado, categoria = pasta do KML, lat/lon). Conversao para E/N com
`PedGeo` (geolocalizacao do desenho, sistema do Civil ou fuso informado). Bloco `VIARIO-ATRATIVO`: circulo r = 1,
hachura SOLID 70% transparente, ponto central, atributos NOME (visivel, altura 0,35), CATEGORIA, LAT, LON, FONTE
(invisiveis); inserido com escala = raio (m) na layer `VIARIO-ATRATIVOS` (cor 6), XData `ATRATIVO`. O painel le a
posicao e o raio do proprio bloco (mover/escalar no desenho vale).
