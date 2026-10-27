# Scripts do catálogo com pontos de conexão

Método completo em `docs/CATALOGO-PONTOS-CONEXAO.md`. Aqui ficam os scripts usados para montar as
peças BLDC_IPE, BLSC_IPE e PV_DNIT_IPE no catálogo FDX (out/2026).

- `Add-BldcFeatures.ps1 -Path <caixa.ipt>`: na cópia da caixa retangular da Autodesk
  (`a8fe511d…_UndergroundStructure.ipt`, recipe `ccf8165e`), cria os parâmetros Tampa_E, Base_Aba,
  Ab_H, Ab_L, Ab_Vao, a aba da laje de fundo e as duas aberturas na parede da frente (−Y). Mantém
  os Reference Points.
- `Instalar-PecaFDX.ps1 -Recipe <pasta> -Tabela <xml> -Nome <parte>`: copia o recipe para
  `ContentLibrary\Templates\<id>`, a tabela e a imagem para o capítulo do FDX, e registra a parte
  no `.apc` (com os ParameterString que faltarem). Rodar com o Civil 3D fechado.
- `Limpar-Catalogos-Teste.ps1`: move (não apaga) catálogos de teste para `C:\Temp` e instala o PV.

Passos feitos direto pela API, sem script próprio:

- **Inserção na face da guia** (BLDC, BLSC): no `ConnectorInnerSketch` da caixa, ponto novo em
  (0, −USWidth/2), cotado contra a origem; `UndergroundStructurePort_wp.SetByPoint(ponto)`. Não use
  `WidthCenterOffset`, que também desloca o corte `ConnectorInnerBody`. Atualizar o `CenterPoint`
  do conector no XML `ContentBuilderAttributeSets`.
- **Laje de topo** (BLDC, BLSC): extrusão do retângulo USLength × USWidth a partir de Z = 0, Tampa_E
  para baixo.
- **BLSC**: apagar as duas aberturas da BLDC e cortar uma central de Ab_L (800) × Ab_H.
- **PV DNIT** (caixa `d2eb77e5`, recipe `ccaec438`): parâmetro `ConeBaseD = SPDiameter + 600 mm` e
  ângulo de saída `atan((ConeBaseD − SPDiameter) / (2·USRiserHeight))` em `RiserBody` e
  `RiserInnerBody`.
- **param.xml do recipe**: Value em cm (o IPE e o Civil 3D leem assim; em mm a peça sai 10× maior).
