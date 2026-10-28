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
- `Add-TampaEInsercao.ps1 -Path <caixa.ipt> [-Prefixo BLDC]`: depois do `Add-BldcFeatures.ps1`, cria a laje
  de topo (Tampa_E) e move a inserção para a face da guia (0, −USWidth/2, 0) pelo `ConnectorInnerSketch`.
- `Add-BlscAbertura.ps1 -Path <caixa.ipt>`: numa cópia da caixa da BLDC, troca as duas aberturas por uma
  central de 800 × 150 e põe USLength 1200.
- `Add-PvCone.ps1 -Path <caixa.ipt>`: na cópia da caixa `d2eb77e5` (recipe `ccaec438`), chaminé em tronco
  de cone (`ConeBaseD = SPDiameter + 600`), 2800 × 2800, paredes 200, SP 1200.
- `Sync-ConectoresXml.ps1`: copia a posição dos work points para o `CenterPoint` do XML
  `ContentBuilderAttributeSets` e salva (os três scripts acima chamam).
- `New-TabelaIPE.ps1`: gera a tabela da peça (.xml + .jpg) a partir da tabela Autodesk do recipe base.
  As medidas de projeto vão em faixa (`-Faixas`: SIL/SIW/WTh/FTh/SRS com os contextos StructInnerLength etc.,
  como nas Part Builder) para a peça ser editável pelo PartData e pelo painel HYDRO; o resto sai em `-Calc`
  (reordenado por dependência) ou `-Valores`.
- `Gerar-TabelasIPE.ps1`: gera as tabelas da BLDC_IPE, BLSC_IPE e PV_DNIT_IPE com esse padrão.

Volumes de conferência (só a caixa): BLDC 0,893 m³ em 2200 × 800 × 1100; BLSC 0,548 m³ em 1200 × 800 × 1100;
PV 4,071 m³ em 2800 × 2800 com SBSH 1600 (USHeight 1500, base 700, chaminé 800).

Sequência completa num PC novo: copiar os recipes `ccf8165e` e `ccaec438` com os ids de
`docs/CATALOGO-PONTOS-CONEXAO.md` (trocar o id em `index.json` e `param.xml`); BLDC = parâmetros da peça +
`Add-BldcFeatures` + `Add-TampaEInsercao`; BLSC = cópia da caixa da BLDC + `Add-BlscAbertura`; PV =
`Add-PvCone`; ajustar os Value do `param.xml` (em cm); `New-TabelaIPE`; `Instalar-PecaFDX` (PV com `-Forma Round`).
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
