# Como montar peças do catálogo com pontos de conexão (método validado em 27/10/2026)

Esse método foi validado no Civil 3D 2026 com a BLDC_IPE, a BLSC_IPE e o PV_DNIT_IPE, instalados no FDX Metric Structures, capítulo CSBIM-DRENAGEM. Ele não usa o IPE nem o Part Builder.

## Peças de uma estrutura
- **Recipe** (geometria): pasta `C:\ProgramData\Autodesk\C3D 2026\enu\ContentLibrary\Templates\<2 primeiros>\<resto do id>`. Ela contém:
  - `geom.iam`, com regras iLogic que repassam os parâmetros pelo nome (`UndergroundStructure_USHeight` etc.);
  - 3 `.ipt`: SurfaceStructure, que é a moldura; UndergroundStructure, que é a caixa; e GrateOrCover;
  - `param.xml`, com Value **em cm**;
  - `index.json`, `200.jpg` e `dim.jpg`.
- **Tabela da peça**: `Pipes Catalog\FDX Metric Structures\<capítulo>\<nome>.xml`, com o atributo Recipe e as colunas SBSL, SBSW, WTh, FTh, SRS, SP*, SS*, CG*. Vale usar ColumnRangeList para ter faixa livre.
- **Registro**: a Part dentro do capítulo, no `FDX Metric Structures.apc`, mais os ParameterStrings que faltarem.

## Regras que importam
- Os **pontos de conexão** do Civil vêm dos **Reference Points** dentro do .ipt da caixa. Pipe Connectors e Part Builder não servem para isso.
- O Civil insere pelo centro da moldura, e a moldura encaixa no AssemblyConnector da caixa (`UndergroundStructurePort_wp`). Para a inserção ficar na face da guia, esse work point foi redefinido para (0, −USWidth/2, 0) e o CenterPoint do XML foi ajustado. O `WidthCenterOffset` não serve para isso.
- Nomes padrão obrigatórios: USLength e USWidth (medidas externas), USHeight, WallTh, BaseTh, SPLength/SPWidth ou SPDiameter. O WallTh da caixa fica amarrado ao da moldura.
- Para começar, copie o recipe da Autodesk mais parecido (catálogo "Generic Drainage with Pipe Connections - Metric") com um **id novo**. Edite o .ipt da caixa pela API do Inventor, mantendo os Reference Points.
  - Caixa retangular: `ccf8165e`, com a caixa `a8fe511d` e 9 pontos de referência.
  - PV com chaminé: `ccaec438`, com a caixa `d2eb77e5`.
- A instalação só acrescenta: copia o recipe, copia a tabela e a imagem, e grava a Part no .apc. Faça **com o Civil fechado** e com backup. No notebook, o script genérico é `C:\Temp\BLDC-IPE\install-fdx\Instalar-PecaFDX.ps1 -Recipe -Tabela -Nome`.
- Em outro PC, copie também as pastas de recipe, não só o Pipes Catalog. Quem só usa o catálogo não precisa do Inventor.

## Recipes instalados
- BLDC_IPE: `fcdbd57a-c7c8-4459-86a8-75b3d439f67d`
- BLSC_IPE: `e034d699-3aec-46d6-9475-15afe8c9bbb7`
- PV_DNIT_IPE: `fe685653-c694-456e-ae8c-3d9a20d98f82`

Backups: `C:\Temp\BLDC-IPE\backup-FDX-1726` e `backup-PipesCatalog`. Os catálogos removidos estão em `C:\Temp\BLDC-IPE\catalogos-teste-removidos`.

No NOT (27/10/2026) as três peças foram recriadas com os mesmos ids de recipe, a partir dos scripts de `tools/inventor/ipe-catalogo`. Backup do FDX Metric Structures: `C:\Temp\backup-FDX-Structures-20261027-205645`.
