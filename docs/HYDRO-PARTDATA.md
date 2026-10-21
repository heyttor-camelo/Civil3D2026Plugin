# HYDRO — medidas expostas pelo PartData (catálogos de estruturas)

Gerado em 2026-10-21 19:20 a partir dos `.xml` das famílias em `C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog` (máquina CAMELO). Só leitura: nada no catálogo nem no plugin foi alterado.

## Como o XML vira PartData

Cada coluna do `.xml` da família (Part Builder) vira um `PartDataField` em `Part.PartData.GetAllDataFields()`:

| XML | `PartDataField` |
|---|---|
| atributo `name` (SIL, SIW, AC...) | `Name` (nome interno; `$SIL` nas fórmulas) |
| atributo `desc` | `Description` |
| atributo `context` | `ContextString` / `Context` (`PartContextType`); `GetDataFieldBy(ctx)` busca por ele |
| atributo `unit` | `Units` |
| `ColumnRangeList` (Min/Max/Default) | `IsFromRange = true`, `ValueRange.RangeMin/RangeMax` — aceita qualquer valor na faixa |
| `ColumnConstList` (Itens) | `IsFromList = true` — só os valores da lista (cada tamanho da parts list escolhe um) |
| `ColumnConst` | valor fixo da família |
| `ColumnCalc` | calculado pela fórmula a partir dos outros campos |

O HYDRO (`HydroReader`) mostra/edita só os campos com `IsFromRange` ou `IsFromList`; as tabelas abaixo destacam esses na coluna "Origem" (`faixa`/`lista`). Colunas de catálogo (`Catalog_*`, versão, flags) e as distâncias ocultas usadas só para desenhar o modelo são omitidas; os `Geometry_ModelParameter` ocultos ficam porque guardam o valor de referência do `.dwg` do modelo.

> **O catálogo "FDX Metric Structures" não está nesta máquina (CAMELO).** Ele é montado por `tools/Merge-PartCatalogs.ps1` (Country Kit BRA + CSBIM saneado, capítulos `CSBIM-*`; ver `DIARIO.md` 2026-10-21) e fica na máquina do trabalho e no Drive (`1 FDX ENGENHARIA\...\FDX-Catalogo-2026-10-21`). No FDX as famílias CSBIM são as mesmas (`CSBIM-ALAS\BDCC` = `ALAS\BDCC`, `CSBIM-DRENAGEM\PV DNIT` = `DRENAGEM\PV DNIT`), mas já saneadas: as listas dimensionais viram faixas. Para listar o FDX: `tools\Export-PartData.ps1 -Catalogs 'FDX Metric Structures'` numa máquina com ele instalado.

> **O CSBIM desta máquina não está saneado**: não há `_saneamento_CSBIM_*.md` na pasta `Pipes Catalog` e as medidas dimensionais ainda são listas (ex.: SIL/SIW/AC do `PV DNIT`). As tabelas mostram o estado atual, antes do `tools/Sanitize-CsbimCatalog.ps1 -Apply`; depois do saneamento as listas dimensionais viram faixas (ver `HYDRO-CATALOGO.md`).

## CSBIM — estruturas

Pasta `U3929348-IRIWUEH-393J4J2-DIJFI` · .apc: name="Generic Drainage with Pipe Connections - Metric", id `F1FEBE2D-D589-4f85-BF8E-650ED06A5EA5`, Desc "U3929348-IRIWUEH-393J4J2-DIJFI", .apc de 2025-08-04

### Capítulo ACESSIBILIDADE (1 família(s))

#### RAMPA

Arquivo `ACESSIBILIDADE\RAMPA.xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "RAMPA" · PartID `F1085FC8-38E8-4670-BA82-EFBBCB1D37B1` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD" + " " + FormatNumber($SIL,0) + " x " + FormatNumber($SBSW,0)`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 150..150 (padrão 150) | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1000, 1200, 1500, 1800, 2000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 1000, 1200, 1500, 1800, 2000 | sim |
| `SSTh` | Slab Thickness | StructSlabThickness | lista | mm | lista: 500, 800, 1000, 1200, 1500 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 20 | sim |
| `SFW` | Frame Width | StructFrameWidth | const | mm | 150 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 0.0001 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + ( 2 * $SSTh )` | sim |

_+ 26 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

### Capítulo ALAS (8 família(s))

#### BDCC

Arquivo `ALAS\BDCC.xml` · tipo `Struct_InletOutlet` / subtipo `ALA` · desc. "RETANGULAR DUPLA" · PartID `C7F65350-3BA7-4707-A61F-7D013BCE32EC` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD" + " " + FormatNumber($SID,0) + " mm - BOCA " + FormatNumber($SHBTh,0) + " mm"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBH` | LARGURA DA PAREDE DA BASE | StructBarrelHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SBPC` | ALTURA DOS DENTES ENRROCAMENTO | StructBarrelPipeClearance | lista | mm | lista: 0, 50, 100, 150, 200, 250, 300 | sim |
| `SCH` | ALTURA ACIMA DO TUBO | StructConeHeight | lista | mm | lista: 250, 300, 350, 400, 450, 500 | sim |
| `SHBTh` | LARGURA DA BASE FINAL | StructHwBaseThickness | lista | mm | lista: 0, 500, 1000, 2000, 2500, 3000, 3500 | sim |
| `SHBW` | DISTANCIA DA BASE | StructHwBaseWidth | lista | mm | lista: 1000, 1500, 2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000 | sim |
| `SID` | DIAMETRO DO TUBO | StructInnerDiameter | lista | mm | lista: 400, 500, 600, 800, 1000, 1200, 1500, 1800, 2000, 2500, 3000 | sim |
| `SIL` | LARGURA DA PARADE MAIOR | StructInnerLength | lista | mm | lista: 100, 150, 200, 250 | sim |
| `SIW` | ESPESSURA DAS PAREDES | StructInnerWidth | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SSTh` | ALTURA DO ACABAMENTO DA BASE FINAL | StructSlabThickness | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SVPC` | ALTURA DA PAREDE MENOR | StructVertPipeClearance | lista | mm | lista: 50, 100, 150, 200, 250 | sim |
| `Between` | Between | Entre | const | mm | 10 | sim |
| `DIST` | Distância entre Tubos | Geometry_ModelParameter | const |  | 1 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SID + $SCH` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SHBTh + $SID` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SHBW` | sim |

_+ 45 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BDTC-DNIT2

Arquivo `ALAS\BDTC-DNIT2.xml` · tipo `Struct_InletOutlet` / subtipo `ALA` · desc. "BDTC-DNIT" · PartID `9DF15942-4E84-4999-BFB1-485EBCC2879D` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SABTh` | Altura do degrau | StructApnBackThickness | lista | mm | lista: 400, 410, 420, 430, 440, 450, 460, 470, 480, 490, 500, 510, 520, 530, 540, 550, 560, 570, 580, 590, 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800, 810, 820, 830, 840, 850, 860, 870, 880, 890, 900, 910, 920, 930, 940, 950, 960, 970, 980, 990, 1000 | não |
| `SAFTh` | Comprimento Piso | StructApnFrontThickness | lista | mm | lista: 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800, 810, 820, 830, 840, 850, 860, 870, 880, 890, 900, 910, 920, 930, 940, 950, 960, 970, 980, 990, 1000 | não |
| `SAL` | (Diametro do tubo) Largura 1 | StructApnLength | lista | mm | lista: 400, 500, 600, 800, 1000, 1200, 1500, 1800, 2000 | não |
| `SAW` | (a) Largura 2 | StructApnWidth | lista | mm | lista: 0 | não |
| `SBPC` | Estrutura auxiliar | StructBarrelPipeClearance | lista | mm | lista: 0.1, 2000, 2500, 3000, 3500, 4000 | não |
| `SFH` | (d) Parede 2 | StructFrameHeight | lista | mm | lista: 0 | não |
| `SHBTh` | (c) Largura da base final | StructHwBaseThickness | lista | mm | lista: 900, 1250, 1450, 1650, 1800, 2600 | não |
| `SHBW` | (d) Largura da parede da base | StructHwBaseWidth | lista | mm | lista: 200, 210, 220, 230, 240, 250, 260, 270, 280, 290, 300, 310, 320, 330, 340, 350, 360, 370, 380, 390, 400, 410, 420, 430, 440, 450, 460, 470, 480, 490, 500, 510, 520, 530, 540, 550, 560, 570, 580, 590, 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800 | não |
| `SHTTh` | Degrau_UN ou Declividade_% | StructHwTopThickness | lista | mm | lista: -0.25, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 | não |
| `SHTW` | (g) Altura acima do tubo | StructHwTopWidth | lista | mm | lista: 200, 300 | não |
| `SIL` | (f) Altura do Interior da estrutura | StructInnerLength | lista | mm | lista: 100, 150, 200, 250, 300 | não |
| `SWFH` | (e) Altura 2 | StructWngFrontHeight | lista | mm | lista: 150, 250, 300, 350, 400, 450, 500, 550, 600, 650, 700, 750, 800 | não |
| `SWL` | (L) Distancia da base | StructWingLength | lista | mm | lista: 230, 900, 910, 920, 930, 940, 950, 960, 970, 980, 990, 1000, 1010, 1020, 1030, 1040, 1050, 1060, 1070, 1080, 1090, 1100, 1110, 1120, 1130, 1140, 1150, 1160, 1170, 1180, 1190, 1200, 1210, 1220, 1230, 1240, 1250, 1300, 1350, 1400, 1450, 1500, 1550, 1600, 1650, 1700, 1750, 1800, 1900, 1950, 2000, 2050, 2100, 2150, 2200, 2250, 2350, 2400, 2450, 2500, 2550, 2600, 2650, 2700, 2750, 2800, 2850, 2900, 2950, 3000, 3050, 3100, 3150, 3200, 3250, 3300, 3350, 3400, 3450, 3500, 3550, 3650, 3700, 3750, 3800, 3850, 3900, 3950, 4000, 4050, 4100, 4150, 4200, 4250, 4300, 4350, 4400, 4450, 4500, 4550, 4600, 4650, 4700, 4750, 4800, 4850, 4900, 4950, 5000, 5050, 5100, 5150, 5200, 5250, 5300, 5350, 5400, 5450, 5500, 5550, 5600, 5650, 5700, 5750, 5800, 5850, 5900, 5950, 6000, 6050, 6100, 6150, 6200, 6250, 6300, 6350, 6400, 6450, 6500, 6550, 6600, 6650, 6700, 6750, 6800, 6850, 6900, 6950, 7000, 7050, 7100, 7150, 7200, 7250, 7300, 7350, 7400, 7450, 7500 (fora de ordem no XML) | não |
| `SWTh` | (k) Distancia entre tubos | StructWngThickness | lista | mm | lista: 50, 80, 100, 150, 200 | sim |
| `SBH` | Altura da chamine | StructBarrelHeight | const | mm | 0 | não |
| `SIW` | (m) Largura do Interior da estrutura | StructInnerWidth | const | mm | 200 | não |
| `SRZ` | (p) Altura da base | StructRiserHeight | const | mm | 200 | sim |
| `SSTh` | Largura da base | StructSlabThickness | const | mm | 150 | não |
| `SVPC` | (k) Altura do Tubo de Queda | StructVertPipeClearance | const | mm | 50 | não |
| `SWBH` | (n) Altura 1 | StructWngBackHeight | const | mm | 200 | não |
| `WTh` | (b) Parede 1 | WallThickness | const | mm | 200 | sim |
| `SBSH` | Altura total | StructHeight | calc | mm | `$SAL+$SHTW` | não |
| `SBSL` | Comprimento total | StructLength | calc | mm | `$SHBTh` | não |
| `SBSW` | Largura total | StructWidth | calc | mm | `$SAL+(2*$SHBW)+(2*$SWTh)` | não |

_+ 65 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BSCC

Arquivo `ALAS\BSCC.xml` · tipo `Struct_InletOutlet` / subtipo `ALA` · desc. "RETANGULAR SIMPLES" · PartID `628FC1A2-CD2C-4E04-94E0-719B6C023AB8` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD" + " " + FormatNumber($SID,0) + " mm - BOCA " + FormatNumber($SHBTh,0) + " mm"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBH` | LARGURA DA PAREDE DA BASE | StructBarrelHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SBPC` | ALTURA DOS DENTES ENRROCAMENTO | StructBarrelPipeClearance | lista | mm | lista: 0, 50, 100, 150, 200, 250, 300 | sim |
| `SCH` | ALTURA ACIMA DO TUBO | StructConeHeight | lista | mm | lista: 250, 300, 350, 400, 450, 500 | sim |
| `SHBTh` | LARGURA DA BASE FINAL | StructHwBaseThickness | lista | mm | lista: 0, 500, 1000, 2000, 2500, 3000, 3500 | sim |
| `SHBW` | DISTANCIA DA BASE | StructHwBaseWidth | lista | mm | lista: 1000, 1500, 2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000 | sim |
| `SID` | DIAMETRO DO TUBO | StructInnerDiameter | lista | mm | lista: 400, 500, 600, 800, 1000, 1200, 1500, 1800, 2000, 2500, 3000 | sim |
| `SIL` | LARGURA DA PARADE MAIOR | StructInnerLength | lista | mm | lista: 100, 150, 200, 250 | sim |
| `SIW` | ESPESSURA DAS PAREDES | StructInnerWidth | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SSTh` | ALTURA DO ACABAMENTO DA BASE FINAL | StructSlabThickness | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SVPC` | ALTURA DA PAREDE MENOR | StructVertPipeClearance | lista | mm | lista: 50, 100, 150, 200, 250 | sim |
| `Between` | Between | Entre | const | mm | 10 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SID + $SCH` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SHBTh + $SID` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SHBW` | sim |

_+ 42 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BSTC-DNIT2

Arquivo `ALAS\BSTC-DNIT2.xml` · tipo `Struct_InletOutlet` / subtipo `ALA` · desc. "BSTC-DNIT" · PartID `781200E4-46B4-4E27-98FD-0B9C93686804` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SABTh` | Altura do degrau | StructApnBackThickness | lista | mm | lista: 400, 410, 420, 430, 440, 450, 460, 470, 480, 490, 500, 510, 520, 530, 540, 550, 560, 570, 580, 590, 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800, 810, 820, 830, 840, 850, 860, 870, 880, 890, 900, 910, 920, 930, 940, 950, 960, 970, 980, 990, 1000 | não |
| `SAFTh` | Comprimento Piso | StructApnFrontThickness | lista | mm | lista: 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800, 810, 820, 830, 840, 850, 860, 870, 880, 890, 900, 910, 920, 930, 940, 950, 960, 970, 980, 990, 1000 | não |
| `SAL` | (Diametro do tubo) Largura 1 | StructApnLength | lista | mm | lista: 400, 500, 600, 800, 1000, 1200, 1500, 1800, 2000 | não |
| `SAW` | (a) Largura 2 | StructApnWidth | lista | mm | lista: 0 | não |
| `SBPC` | Estrutura auxiliar | StructBarrelPipeClearance | lista | mm | lista: 0.1, 2000, 2500, 3000, 3500, 4000 | não |
| `SFH` | (d) Parede 2 | StructFrameHeight | lista | mm | lista: 0 | não |
| `SHBTh` | (c) Largura da base final | StructHwBaseThickness | lista | mm | lista: 900, 1250, 1450, 1650, 1800, 2600 | não |
| `SHBW` | (d) Largura da parede da base | StructHwBaseWidth | lista | mm | lista: 200, 210, 220, 230, 240, 250, 260, 270, 280, 290, 300, 310, 320, 330, 340, 350, 360, 370, 380, 390, 400, 410, 420, 430, 440, 450, 460, 470, 480, 490, 500, 510, 520, 530, 540, 550, 560, 570, 580, 590, 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800 | não |
| `SHTTh` | Degrau_UN ou Declividade_% | StructHwTopThickness | lista | mm | lista: -0.25, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 | não |
| `SHTW` | (g) Altura acima do tubo | StructHwTopWidth | lista | mm | lista: 200, 300 | não |
| `SIL` | (f) Altura do Interior da estrutura | StructInnerLength | lista | mm | lista: 100, 150, 200, 250, 300 | não |
| `SWFH` | (e) Altura 2 | StructWngFrontHeight | lista | mm | lista: 150, 250, 300, 350, 400, 450, 500, 550, 600, 650, 700, 750, 800 | não |
| `SWL` | (L) Distancia da base | StructWingLength | lista | mm | lista: 900, 910, 920, 930, 940, 950, 960, 970, 980, 990, 1000, 1010, 1020, 1030, 1040, 1050, 1060, 1070, 1080, 1090, 1100, 1110, 1120, 1130, 1140, 1150, 1160, 1170, 1180, 1190, 1200, 1210, 1220, 1230, 1240, 1250 | não |
| `SBH` | Altura da chamine | StructBarrelHeight | const | mm | 0 | não |
| `SIW` | (m) Largura do Interior da estrutura | StructInnerWidth | const | mm | 200 | não |
| `SRZ` | (p) Altura da base | StructRiserHeight | const | mm | 200 | sim |
| `SSTh` | Largura da base | StructSlabThickness | const | mm | 150 | não |
| `SVPC` | (k) Altura do Tubo de Queda | StructVertPipeClearance | const | mm | 50 | não |
| `SWBH` | (n) Altura 1 | StructWngBackHeight | const | mm | 200 | não |
| `SWTh` | (k) Distancia entre tubos | StructWngThickness | const | mm | 50 | não |
| `WTh` | (b) Parede 1 | WallThickness | const | mm | 200 | sim |
| `SBSH` | Altura total | StructHeight | calc | mm | `$SAL+$SHTW` | não |
| `SBSL` | Comprimento total | StructLength | calc | mm | `$SHBTh` | não |
| `SBSW` | Largura total | StructWidth | calc | mm | `$SAL+(2*$SHBW)+(2*$SWTh)` | não |

_+ 61 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BTCC

Arquivo `ALAS\BTCC.xml` · tipo `Struct_InletOutlet` / subtipo `ALA` · desc. "RETANGULAR TRIPLA" · PartID `3D958805-1F13-485F-AF72-B64A6CB0632B` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD" + " " + FormatNumber($SID,0) + " mm - BOCA " + FormatNumber($SHBTh,0) + " mm"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBH` | LARGURA DA PAREDE DA BASE | StructBarrelHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SBPC` | ALTURA DOS DENTES ENRROCAMENTO | StructBarrelPipeClearance | lista | mm | lista: 0, 50, 100, 150, 200, 250, 300 | sim |
| `SCH` | ALTURA ACIMA DO TUBO | StructConeHeight | lista | mm | lista: 250, 300, 350, 400, 450, 500 | sim |
| `SHBTh` | LARGURA DA BASE FINAL | StructHwBaseThickness | lista | mm | lista: 0, 500, 1000, 2000, 2500, 3000, 3500 | sim |
| `SHBW` | DISTANCIA DA BASE | StructHwBaseWidth | lista | mm | lista: 1000, 1500, 2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000 | sim |
| `SID` | TOTAL | StructInnerDiameter | lista | mm | lista: 1725, 2475, 3362.5, 3975, 4725, 6225 | sim |
| `SIL` | LARGURA DA PARADE MAIOR | StructInnerLength | lista | mm | lista: 100, 150, 200, 250 | sim |
| `SIW` | ESPESSURA DAS PAREDES | StructInnerWidth | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SSTh` | ALTURA DO ACABAMENTO DA BASE FINAL | StructSlabThickness | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SVPC` | ALTURA DA PAREDE MENOR | StructVertPipeClearance | lista | mm | lista: 50, 100, 150, 200, 250 | sim |
| `Between` | Between | Entre | const | mm | 10 | sim |
| `DIST` | Distância entre Tubos | Geometry_ModelParameter | const |  | 1 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `LenA1` | TUBO | Geometry_Distance2D | calc | mm | `(($SID*2)/3)-150` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SID + $SCH` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SHBTh + $SID` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SHBW` | sim |

_+ 44 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BTTC-DNIT2

Arquivo `ALAS\BTTC-DNIT2.xml` · tipo `Struct_InletOutlet` / subtipo `ALA` · desc. "BTTC-DNIT" · PartID `5C331AA6-914F-42F4-8444-FAD52E71B31F` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SABTh` | Altura do degrau | StructApnBackThickness | lista | mm | lista: 400, 410, 420, 430, 440, 450, 460, 470, 480, 490, 500, 510, 520, 530, 540, 550, 560, 570, 580, 590, 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800, 810, 820, 830, 840, 850, 860, 870, 880, 890, 900, 910, 920, 930, 940, 950, 960, 970, 980, 990, 1000 | não |
| `SAFTh` | Comprimento Piso | StructApnFrontThickness | lista | mm | lista: 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800, 810, 820, 830, 840, 850, 860, 870, 880, 890, 900, 910, 920, 930, 940, 950, 960, 970, 980, 990, 1000 | não |
| `SAL` | (Diametro do tubo) Largura 1 | StructApnLength | lista | mm | lista: 400, 500, 600, 800, 1000, 1200, 1500, 1800, 2000 | não |
| `SAW` | (a) Largura 2 | StructApnWidth | lista | mm | lista: 0 | não |
| `SBPC` | Estrutura auxiliar | StructBarrelPipeClearance | lista | mm | lista: 0.1, 2000, 2500, 3000, 3500, 4000 | não |
| `SFH` | (d) Parede 2 | StructFrameHeight | lista | mm | lista: 0 | não |
| `SHBTh` | (c) Largura da base final | StructHwBaseThickness | lista | mm | lista: 900, 1250, 1450, 1650, 1800, 2600 | não |
| `SHBW` | (d) Largura da parede da base | StructHwBaseWidth | lista | mm | lista: 200, 210, 220, 230, 240, 250, 260, 270, 280, 290, 300, 310, 320, 330, 340, 350, 360, 370, 380, 390, 400, 410, 420, 430, 440, 450, 460, 470, 480, 490, 500, 510, 520, 530, 540, 550, 560, 570, 580, 590, 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800 | não |
| `SHTTh` | Degrau_UN ou Declividade_% | StructHwTopThickness | lista | mm | lista: -0.25, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 | não |
| `SHTW` | (g) Altura acima do tubo | StructHwTopWidth | lista | mm | lista: 200, 300 | não |
| `SIL` | (f) Altura do Interior da estrutura | StructInnerLength | lista | mm | lista: 100, 150, 200, 250, 300 | não |
| `SWFH` | (e) Altura 2 | StructWngFrontHeight | lista | mm | lista: 150, 250, 300, 350, 400, 450, 500, 550, 600, 650, 700, 750, 800 | não |
| `SWL` | (L) Distancia da base | StructWingLength | lista | mm | lista: 230, 900, 910, 920, 930, 940, 950, 960, 970, 980, 990, 1000, 1010, 1020, 1030, 1040, 1050, 1060, 1070, 1080, 1090, 1100, 1110, 1120, 1130, 1140, 1150, 1160, 1170, 1180, 1190, 1200, 1210, 1220, 1230, 1240, 1250, 1300, 1350, 1400, 1450, 1500, 1550, 1600, 1650, 1700, 1750, 1800, 1900, 1950, 2000, 2050, 2100, 2150, 2200, 2250, 2350, 2400, 2450, 2500, 2550, 2600, 2650, 2700, 2750, 2800, 2850, 2900, 2950, 3000, 3050, 3100, 3150, 3200, 3250, 3300, 3350, 3400, 3450, 3500, 3550, 3650, 3700, 3750, 3800, 3850, 3900, 3950, 4000, 4050, 4100, 4150, 4200, 4250, 4300, 4350, 4400, 4450, 4500, 4550, 4600, 4650, 4700, 4750, 4800, 4850, 4900, 4950, 5000, 5050, 5100, 5150, 5200, 5250, 5300, 5350, 5400, 5450, 5500, 5550, 5600, 5650, 5700, 5750, 5800, 5850, 5900, 5950, 6000, 6050, 6100, 6150, 6200, 6250, 6300, 6350, 6400, 6450, 6500, 6550, 6600, 6650, 6700, 6750, 6800, 6850, 6900, 6950, 7000, 7050, 7100, 7150, 7200, 7250, 7300, 7350, 7400, 7450, 7500 (fora de ordem no XML) | não |
| `SWTh` | (k) Distancia entre tubos | StructWngThickness | lista | mm | lista: 50, 80, 100, 150, 200 | sim |
| `SBH` | Altura da chamine | StructBarrelHeight | const | mm | 0 | não |
| `SIW` | (m) Largura do Interior da estrutura | StructInnerWidth | const | mm | 200 | não |
| `SRZ` | (p) Altura da base | StructRiserHeight | const | mm | 200 | sim |
| `SSTh` | Largura da base | StructSlabThickness | const | mm | 150 | não |
| `SVPC` | (k) Altura do Tubo de Queda | StructVertPipeClearance | const | mm | 50 | não |
| `SWBH` | (n) Altura 1 | StructWngBackHeight | const | mm | 200 | não |
| `WTh` | (b) Parede 1 | WallThickness | const | mm | 200 | sim |
| `SBSH` | Altura total | StructHeight | calc | mm | `$SAL+$SHTW` | não |
| `SBSL` | Comprimento total | StructLength | calc | mm | `$SHBTh` | não |
| `SBSW` | Largura total | StructWidth | calc | mm | `$SAL+(2*$SHBW)+(2*$SWTh)` | não |

_+ 67 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### PETERKA CIRCULAR DUPLA

Arquivo `ALAS\PETERKA CIRCULAR DUPLA.xml` · tipo `Struct_InletOutlet` / subtipo `ALA` · desc. "PETERKA CIRCULAR DUPLA" · PartID `11788990-48FA-4585-B814-CABFA27C3C1A` · forma `BoundingShape_Box`

Nome do tamanho: `"PETERKA DUPLA " + FormatNumber($SIL,0) + " mm"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SHBTh` | Headwall Base Thickness | StructHwBaseThickness | lista | mm | lista: 150, 200, 250, 300 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 600, 800, 1000, 1200, 1500, 1800, 2000 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | lista | mm | lista: 200, 300, 400, 500, 600 | sim |
| `Between` | Between | Entre | const | mm | 10 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 1 | sim |
| `SHBW` | Headwall Base Width | StructHwBaseWidth | const | mm | 100 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 1 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SIL + $SVPC` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL * 2.5` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIL * 3.5` | sim |

_+ 39 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### PETERKA CIRCULAR SIMPLES

Arquivo `ALAS\PETERKA CIRCULAR SIMPLES.xml` · tipo `Struct_InletOutlet` / subtipo `ALA` · desc. "PETERKA CIRCULAR SIMPLES" · PartID `8C87BAD9-E42D-463F-B313-9EB295A92C33` · forma `BoundingShape_Box`

Nome do tamanho: `"PETERKA SIMPLES " + FormatNumber($SIL,0) + " mm"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SHBTh` | Headwall Base Thickness | StructHwBaseThickness | lista | mm | lista: 150, 200, 250, 300 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 600, 800, 1000, 1200, 1500, 1800, 2000 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | lista | mm | lista: 200, 300, 400, 500, 600 | sim |
| `Between` | Between | Entre | const | mm | 10 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 1 | sim |
| `SHBW` | Headwall Base Width | StructHwBaseWidth | const | mm | 100 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 1 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SIL + $SVPC` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL * 2.5` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIL * 3.5` | sim |

_+ 34 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

### Capítulo DRENAGEM (28 família(s))

#### BLDC - SUL

Arquivo `DRENAGEM\BLDC - SUL.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLDC - SUL" · PartID `F3AC2883-A69D-49E2-B9D1-FEB32399D48B` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100, 150 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 2000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 600 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 100, 150 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 77 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLDC

Arquivo `DRENAGEM\BLDC.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLDC" · PartID `38BC1863-A4BF-4D0D-9100-E09B2E9B4B8A` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 2000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 600 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 100, 150 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 100 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 77 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLDG - SUL

Arquivo `DRENAGEM\BLDG - SUL.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLDG - SUL" · PartID `EB7D2611-ABEC-4D92-8821-868D0541FE0E` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 2540 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 920 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 200 | sim |
| `BASE` | Model Parameter 1 | Geometry_ModelParameter | const |  | 200 | não |
| `C_GRELHA` | Model Distance Parameter 2 | Geometry_ModelDistanceParameter | const | mm | 440 | não |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 100 | sim |
| `L_GRELHA` | Model Distance Parameter 1 | Geometry_ModelDistanceParameter | const | mm | 40 | não |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 132 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLDG_

Arquivo `DRENAGEM\BLDG_.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLDG" · PartID `3DBEAF8F-62C1-4C29-A587-FE6D72A14D2D` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 2540 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 920 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 200 | sim |
| `BASE` | Model Parameter 1 | Geometry_ModelParameter | const |  | 200 | não |
| `C_GRELHA` | Model Distance Parameter 2 | Geometry_ModelDistanceParameter | const | mm | 440 | não |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 100 | sim |
| `L_GRELHA` | Model Distance Parameter 1 | Geometry_ModelDistanceParameter | const | mm | 40 | não |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 132 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLDM

Arquivo `DRENAGEM\BLDM.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLDM" · PartID `2559AFF5-79AC-4783-AF05-68467CB87DF4` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100, 150 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 2000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 600 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 100, 150 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 167 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLDM_SUL

Arquivo `DRENAGEM\BLDM_SUL.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLDM_SUL" · PartID `BD843DCA-E27B-4A9B-BE34-6585DC8ABD57` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100, 150 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 2000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 600 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 100, 150 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 167 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLSC - SUL

Arquivo `DRENAGEM\BLSC - SUL.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLSC - SUL" · PartID `0FCB4E47-EDB9-464C-B7EF-B979623DC511` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100, 150 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 600 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 100, 150 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 54 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLSC_0

Arquivo `DRENAGEM\BLSC_0.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLSC" · PartID `E3AF1640-5434-44D0-9571-1EF2E8838F8F` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100, 150 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 600 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 100, 150 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 54 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLSG_

Arquivo `DRENAGEM\BLSG_.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLSG" · PartID `C7533C86-2F57-4983-85FA-D24583407EBA` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `C_GRELHA` | Model Distance Parameter 2 | Geometry_ModelDistanceParameter | lista | mm | lista: 440, 500 | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100 | sim |
| `L_GRELHA` | Model Distance Parameter 1 | Geometry_ModelDistanceParameter | lista | mm | lista: 40, 50 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1350 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 920 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 200 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 76 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLSG_SUL

Arquivo `DRENAGEM\BLSG_SUL.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLSG_SUL" · PartID `49FE70FB-733E-4B06-8EEE-05ABBD3F9B19` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `C_GRELHA` | Model Distance Parameter 2 | Geometry_ModelDistanceParameter | lista | mm | lista: 440, 500 | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100 | sim |
| `L_GRELHA` | Model Distance Parameter 1 | Geometry_ModelDistanceParameter | lista | mm | lista: 40, 50 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1350 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 920 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 200 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 76 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLSM

Arquivo `DRENAGEM\BLSM.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLSM" · PartID `DB832DC1-3E26-42CF-BEFB-10BAF64A5420` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100, 150 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 600 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 100, 150 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 94 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLSM_SUL

Arquivo `DRENAGEM\BLSM_SUL.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLSM_SUL" · PartID `6B633A5F-B0ED-409C-B627-E8E1415E9E6D` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100, 150 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 600 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 100, 150 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 94 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLTC

Arquivo `DRENAGEM\BLTC.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLTC" · PartID `2CF0BBA7-EF84-42A6-A294-D22EB17CA320` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100, 150 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 3200 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 600 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 100, 150 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 85 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLTC_SUL

Arquivo `DRENAGEM\BLTC_SUL.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLTC_SUL" · PartID `35D9E6AB-E92B-4656-8895-2C6F8C3D2EBA` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100, 150 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 3200 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 600 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 100, 150 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 85 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLTG

Arquivo `DRENAGEM\BLTG.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLTG" · PartID `B31BC634-00D7-4545-BCD1-7E0A69F5658F` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 3810 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 920 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 200 | sim |
| `BASE` | Model Parameter 1 | Geometry_ModelParameter | const |  | 200 | não |
| `C_GRELHA` | Model Distance Parameter 2 | Geometry_ModelDistanceParameter | const | mm | 440 | não |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 100 | sim |
| `L_GRELHA` | Model Distance Parameter 1 | Geometry_ModelDistanceParameter | const | mm | 40 | não |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 168 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BLTG_SUL

Arquivo `DRENAGEM\BLTG_SUL.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BLTG_SUL" · PartID `32147B28-5D29-4E4F-A57B-1332FFADF87C` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 3810 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 920 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 200 | sim |
| `BASE` | Model Parameter 1 | Geometry_ModelParameter | const |  | 200 | não |
| `C_GRELHA` | Model Distance Parameter 2 | Geometry_ModelDistanceParameter | const | mm | 440 | não |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 100 | sim |
| `L_GRELHA` | Model Distance Parameter 1 | Geometry_ModelDistanceParameter | const | mm | 40 | não |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 168 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### BL-TST-CAMPINAS

Arquivo `DRENAGEM\BL-TST-CAMPINAS.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "BL-TST-CAMPINAS" · PartID `19B896C2-F97C-4A07-91DF-6417D973F6CF` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 500..100000 (padrão 1000) | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | lista | mm | lista: 0.001, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 0 | sim |
| `SBSH` | Structure Height | StructHeight | const | mm | 1000 | sim |
| `SBSL` | Structure Length | StructLength | const | mm | 2200 | sim |
| `SBSW` | Structure Width | StructWidth | const | mm | 1000 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 200 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | calc | mm | `$SBSL` | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | calc | mm | `$SBSW` | sim |

_+ 113 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### CL

Arquivo `DRENAGEM\CL.xml` · tipo `Struct_Junction` / subtipo `CX` · desc. "CX - FIXO" · PartID `474D7C29-FF26-41E5-9C9E-B15C1B5950F5` · forma `BoundingShape_Box`

Nome do tamanho: `CP Ø 600 mmCP Ø 800 mmCP Ø 1000 mmCP Ø 1200 mmCP Ø 1500 mmCP Ø 1800 mmCP 2X Ø 600 mmCP 2X Ø 800 mmCP 2X Ø 1000 mmCP 2X Ø 1200 mmCP2X Ø 1500 mmCP2X Ø 1800 mm`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SIL` | Inner Structure Length | StructInnerLength |  | mm |  | sim |
| `SIW` | Inner Structure Width | StructInnerWidth |  | mm |  | sim |
| `SRS` | Rim to Sump Height | StructRimToSumpHeight |  | mm |  | sim |
| `UUID` | Primary Key |  |  |  |  | não |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 200 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 200 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL + ( 2* $WTh )` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + ( 2 * $WTh )` | sim |

_+ 15 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### CLV

Arquivo `DRENAGEM\CLV.xml` · tipo `Struct_Junction` / subtipo `CX` · desc. "CX - VARÍAVEL" · PartID `E54299A2-9779-4C06-B241-17C587F8309E` · forma `BoundingShape_Box`

Nome do tamanho: `"CV " + FormatNumber($SIL,0) + " mm x " + FormatNumber($SIW,0) + " mm"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 400..100000 (padrão 1000) | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 350, 400, 600, 800, 1000, 1200, 1400, 1500, 1600, 1800, 2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000 (fora de ordem no XML) | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 350, 400, 600, 800, 1000, 1200, 1400, 1500, 1600, 1800, 2000, 2200, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000 (fora de ordem no XML) | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 100, 200 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 200 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL + ( 2* $WTh )` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + ( 2 * $WTh )` | sim |

_+ 15 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### EDA

Arquivo `DRENAGEM\EDA.xml` · tipo `Struct_InletOutlet` / subtipo `Undefined` · desc. "EDA" · PartID `D7786E8E-6530-4B6E-B0D4-910BFC964CE9` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD" + " " + FormatNumber($SIL,0) + " x " + FormatNumber($WTh,0)`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SWL` | (L) Distancia da base | StructWingLength | faixa | mm | faixa 1000..30000 (padrão 5000) | sim |
| `LenB5` | OCULTA_ESQ | Geometry_Distance3D | lista | mm | lista: 0.0001, 220 | sim |
| `LenB6` | BARREIRA_ESQ | Geometry_Distance3D | lista | mm | lista: 0.0001, 220 | sim |
| `LenB7` | OCULTA_DIR | Geometry_Distance3D | lista | mm | lista: 0.0001, 220 | sim |
| `LenB8` | BARREIRA_DIR | Geometry_Distance3D | lista | mm | lista: 0.0001, 220 | sim |
| `SAW` | (a) | StructApnWidth | lista | mm | lista: 120 | sim |
| `SIL` | (f) | StructInnerLength | lista | mm | lista: 1000, 2000, 3000 | sim |
| `SVPC` | (e) | StructVertPipeClearance | lista | mm | lista: 660, 760 | sim |
| `WTh` | (b) | WallThickness | lista | mm | lista: 400, 500, 600, 800, 1000, 1200 | sim |
| `SBSH` | Altura total | StructHeight | const | mm | 100 | não |
| `SRZ` | (p) Altura da base | StructRiserHeight | const | mm | 4000 | sim |
| `SWFH` | Vazio | StructWngFrontHeight | const | mm | 0 | não |
| `SBSL` | Comprimento total | StructLength | calc | mm | `$LenB2` | não |
| `SBSW` | Largura total | StructWidth | calc | mm | `$LenB2` | não |
| `SHBTh` | (c) | StructHwBaseThickness | calc | mm | `$LenB2` | não |
| `SHBW` | Largura da parede da base | StructHwBaseWidth | calc | mm | `$LenB1` | não |

_+ 71 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### ESCADA

Arquivo `DRENAGEM\ESCADA.xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "ESCADA" · PartID `4B05C049-76A8-4698-878A-9103203F9975` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 6000) | sim |
| `SBH` | Barrel Height | StructBarrelHeight | lista | mm | lista: 600, 800, 1000, 1200, 1500, 2000 | sim |
| `SCH` | Cone Height | StructConeHeight | lista | mm | lista: 2000, 3000, 4000, 5000, 6000, 7000, 8000, 9000, 10000, 11000, 12000, 13000, 14000, 15000, 16000, 17000, 18000, 19000, 20000, 21000, 22000, 23000, 24000, 25000, 26000, 27000, 28000, 29000, 30000, 31000, 32000, 33000, 34000, 35000, 36000, 37000, 38000, 39000, 40000, 41000, 42000, 43000, 44000, 45000, 46000, 47000, 48000, 49000, 50000, 51000, 52000, 53000, 54000, 55000, 56000, 57000, 58000, 59000, 60000, 61000, 62000, 63000, 64000, 65000, 66000, 67000, 68000, 69000 | sim |
| `SFW` | Frame Width | StructFrameWidth | lista | mm | lista: 5000 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 600, 800, 1000, 1200, 1500, 2000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 1000, 2000, 3000 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 0 | sim |
| `SFL` | Frame Length | StructFrameLength | const | mm | 8000 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 5 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 150 | sim |
| `DEG` | Model Parameter 4 | Geometry_ModelParameter | calc |  | `$HIP / 10` | não |
| `H` | Model Parameter 1 | Geometry_ModelParameter | calc |  | `$SCH` | sim |
| `HIP` | Model Parameter 3 | Geometry_ModelParameter | calc |  | `sqr( ( $V * $V ) + ( $H * $H ) )` | não |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SBH` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW` | sim |
| `SFH` | Frame Height | StructFrameHeight | calc | mm | `$SBH / 2` | sim |
| `V` | Model Parameter 2 | Geometry_ModelParameter | calc |  | `$SRS - $SBH` | sim |

_+ 70 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### EXTRAVASOR

Arquivo `DRENAGEM\EXTRAVASOR.xml` · tipo `Struct_Junction` / subtipo `EXTRAVASOR` · desc. "EXTRAVASOR" · PartID `545B5BCD-FAE9-4098-BABF-4278C94061CA` · forma `BoundingShape_Box`

Nome do tamanho: `"CL " + FormatNumber($SIL,0) + " x " + FormatNumber($SIW,0)`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 500..50000 (padrão 4000) | sim |
| `ALTCX` | Profundidade da Lagoa | Geometry_ModelParameter | lista |  | lista: 500, 600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400, 1500, 1600, 1700, 1800, 1900, 2000, 2100, 2200, 2300, 2400, 2500, 2600, 2700, 2800, 2900, 3000, 3100, 3200, 3300, 3400, 3500, 3600, 3700, 3800, 3900, 4000, 4100, 4200, 4300, 4400, 4500, 4600, 4700, 4800, 4900, 5000 | não |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1000, 2000, 3000, 4000, 5000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 1000, 2000, 3000, 4000, 5000 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 150 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 150 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL + ( 2* $WTh )` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + ( 2 * $WTh )` | sim |

_+ 28 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### INTERSECAO CANALETA

Arquivo `DRENAGEM\INTERSECAO CANALETA.xml` · tipo `Struct_Junction` / subtipo `CANALETA` · desc. "INTERSECAO CANALETA" · PartID `D81B1870-3F1A-4ECC-905A-1F959A56E63F` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `FTh` | Floor Thickness | FloorThickness | const | mm | 0 | sim |
| `SBSH` | Structure Height | StructHeight | const | mm | 200 | sim |
| `SBSL` | Structure Length | StructLength | const | mm | 200 | sim |
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | const | mm | 200 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 0 | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `0.0000` | sim |

_+ 3 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### PV DNIT

Arquivo `DRENAGEM\PV DNIT.xml` · tipo `Struct_Junction` / subtipo `PV` · desc. "PV DNIT" · PartID `BBD972E2-A547-48E8-8E04-2A72DC838C36` · forma `BoundingShape_Box`

Nome do tamanho: `"PV " + FormatNumber($SIL,0) + " mm x " + FormatNumber($SIW,0) + " mm"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1400) | sim |
| `AC` | Cone Height | StructConeHeight | lista | mm | lista: 400, 600, 800, 1000, 1200 | sim |
| `DC` | Frame Diameter | StructFrameDiameter | lista | mm | lista: 600, 800, 1000 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1000, 1200, 1400, 1600, 1800, 2000, 2200, 2400, 2600, 2800, 3000, 3200, 3400, 3600, 3800, 4000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 1000, 1200, 1400, 1600, 1800, 2000, 2200, 2400, 2600, 2800, 3000, 3200, 3400, 3600, 3800, 4000 (fora de ordem no XML) | sim |
| `AC0` | Cone Height | Geometry_ModelParameter | const |  | 800 | não |
| `DC0` | Frame Diameter | Geometry_ModelParameter | const |  | 800 | não |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 200 | sim |
| `SIL0` | Inner Structure Length | Geometry_ModelParameter | const |  | 2000 | não |
| `SIW0` | Inner Structure Width | Geometry_ModelParameter | const |  | 2000 | não |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 200 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL + ( 2 * $WTh )` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + ( 2 * $WTh )` | sim |

_+ 29 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### PVBL

Arquivo `DRENAGEM\PVBL.xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "PVBL" · PartID `ACABD28C-8EFF-4847-907D-C570F8E27C67` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 500..100000 (padrão 1000) | sim |
| `SCH` | Cone Height | StructConeHeight | lista | mm | lista: 500, 800, 1000 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1000, 2000, 3000 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 150 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | const | mm | 1000 | sim |
| `SSTh` | Slab Thickness | StructSlabThickness | const | mm | 400 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 150 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW` | sim |

_+ 44 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### PVD

Arquivo `DRENAGEM\PVD.xml` · tipo `Struct_Junction` / subtipo `PV` · desc. "PV" · PartID `8F282B08-5C8D-43E9-A817-F5123E6E7690` · forma `BoundingShape_Box`

Nome do tamanho: `PV - P/TUBOS Ø600mmPV - P/TUBOS Ø800mmPV - P/TUBOS Ø1000mmPV - P/TUBOS Ø1200mmPV - P/TUBOS Ø1500mmPV - P/TUBOS Ø2000mmPV - P/TUBOS Ø2500mmPV - P/TUBOS Ø3000mmPV - P/TUBOS Ø500mm`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SIL` | (f) Altura do Interior da estrutura | StructInnerLength |  | mm |  | sim |
| `SIW` | (m) Largura do Interior da estrutura | StructInnerWidth |  | mm |  | sim |
| `UUID` | Primary Key |  |  |  |  | não |
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1400) | sim |
| `SRZ` | (p) Altura da base | StructRiserHeight | faixa | mm | faixa 1000..10000 (padrão 1600) | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 200 | sim |
| `SFD` | Frame Diameter | StructFrameDiameter | const | mm | 600 | não |
| `SIL0` | Inner Structure Length | Geometry_ModelParameter | const |  | 2000 | não |
| `SIW0` | Inner Structure Width | Geometry_ModelParameter | const |  | 2000 | não |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 200 | sim |
| `BARRIL` | BARRIL | Geometry_ModelDistanceParameter | calc | mm | `$SRZ` | não |
| `LenB1` | 3D Distance 1 | Geometry_Distance3D | calc | mm | `int((($SRS+499) / 500)) * 500` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL + ( 2 * $WTh )` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + ( 2 * $WTh )` | sim |
| `SCH` | Cone Height | StructConeHeight | calc | mm | `(int((($SRS+499) / 500)) * 500) - $BARRIL` | sim |

_+ 30 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### TCC-01

Arquivo `DRENAGEM\TCC-01.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "TCC-01" · PartID `015D8CFC-7719-4AE2-AE10-910AC66E4FA1` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD" + " " + FormatNumber($SIW,0) + " mm x " + FormatNumber($SIL,0) + " mm"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 800..100000 (padrão 1000) | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1000, 1500, 2000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 1250, 2500 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 200 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 200 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL + ( 2* $WTh )` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + ( 2 * $WTh )` | sim |

_+ 49 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### TCC-02

Arquivo `DRENAGEM\TCC-02.xml` · tipo `Struct_Junction` / subtipo `BL` · desc. "TCC-02" · PartID `CC8F8A18-D067-4A54-8BA3-BBA54D81005F` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD" + " " + FormatNumber($SIW,0) + " mm x " + FormatNumber($SIL,0) + " mm"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 800..100000 (padrão 1000) | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1000, 1500, 2000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 1250, 2500 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 200 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 200 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL + ( 2* $WTh )` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + ( 2 * $WTh )` | sim |

_+ 51 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

### Capítulo ELÉTRICA (2 família(s))

#### CX

Arquivo `ELÉTRICA\CX.xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "CX" · PartID `BF18BEFE-8D06-43C5-98A4-FA5CE16DCEBB` · forma `BoundingShape_Box`

Nome do tamanho: `"CL " + FormatNumber($SIL,0) + " x " + FormatNumber($SIW,0)`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 200, 300, 400, 500 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 200, 300, 400, 500 | sim |
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | lista | mm | lista: 200, 300, 400, 500 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 50 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 50 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL + ( 2* $WTh )` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + ( 2 * $WTh )` | sim |

_+ 15 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### POSTE_A

Arquivo `ELÉTRICA\POSTE_A.xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "POSTE_A" · PartID `05CB4455-238C-4443-A5B9-4881A08A2B6A` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 400..1000 (padrão 400) | sim |
| `ALT` | Model Parameter 3 | Geometry_ModelParameter | lista |  | lista: 7500, 8500 | sim |
| `SIL` | Model Parameter 1 | Geometry_ModelParameter | const |  | 200 | não |
| `SIW` | Model Parameter 2 | Geometry_ModelParameter | const |  | 200 | não |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `FTh` | Floor Thickness | FloorThickness | calc | mm | `$WTh` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS+$Fth` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*$Wth)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*$Wth)` | sim |

_+ 28 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

### Capítulo ESGOTO (8 família(s))

#### CCV

Arquivo `ESGOTO\CCV.xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "CAIXA CIRCULAR VARIÁVEL" · PartID `5B63F17B-9598-4457-8888-04A53AC2218B` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 250..100000 (padrão 500) | sim |
| `SBSD` | Structure Diameter | StructDiameter | lista | mm | lista: 400, 600, 800, 1000 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 80 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $Wth + $Fth` | sim |

_+ 22 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### PI (PLASTICO NTS025)

Arquivo `ESGOTO\PI (PLASTICO NTS025).xml` · tipo `Struct_Junction` / subtipo `PV` · desc. "PI (PLASTICO NTS-025)" · PartID `E545A2A9-7715-4D4E-95A0-4F3F3E44792B` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"PI " + FormatNumber($SID,0) + " mm"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 500..100000 (padrão 2000) | sim |
| `SCH` | Cone Height | StructConeHeight | lista | mm | lista: 0.1 | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 600 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 100 | sim |
| `SFD` | Frame Diameter | StructFrameDiameter | const | mm | 600 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |

_+ 30 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### POÇO DE INSPECAO

Arquivo `ESGOTO\POÇO  DE INSPECAO.xml` · tipo `Struct_Junction` / subtipo `PV` · desc. "POÇO DE INSPECAO" · PartID `6190DE27-B9C5-4FDA-8E93-6D89329FFBE1` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..8000 (padrão 1500) | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | lista | mm | lista: 0.001, 200 | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 600, 800, 1000 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | lista | mm | lista: 600, 650, 700, 750, 800, 850, 900, 950, 1000, 1050, 1100, 1150, 1200, 1250, 1300, 1350, 1400, 1450, 1500, 1550, 1600, 1650, 1700, 1750, 1800, 1900, 2000, 2050, 2100, 2150, 2200, 2250, 2300, 2350, 2400, 2450, 2500, 2550, 2600, 2650, 2700, 2750, 2800, 2850, 2900, 2950, 3000, 3050, 3100, 3150, 3200, 3250, 3300, 3350, 3400, 3450, 3500, 3550, 3600, 3650, 3700, 3750, 3800, 3850, 3900, 4000, 4050, 4100, 4150, 4200, 4250, 4300, 4350, 4400, 4450, 4500, 4550, 4600, 4650, 4700, 4750, 4800, 4850, 4900, 4950, 5000, 5050, 5100, 5150, 5200, 5250, 5300, 5350, 5400, 5450, 5500, 5550, 5600, 5650, 5700, 5750, 5800, 5850, 5900, 5950, 6000, 6050, 6100, 6150, 6200, 6250, 6300, 6350, 6400, 6450, 6500, 6550, 6600, 6650, 6700, 6750, 6800, 6850, 6900, 6950, 7000 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 200 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID + (2*$Wth)` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $Fth` | sim |

_+ 71 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### PV (ANEL ATE 1.50)

Arquivo `ESGOTO\PV (ANEL ATE 1.50).xml` · tipo `Struct_Junction` / subtipo `PV` · desc. "PV (ANEL ATE 1.50)" · PartID `22778D13-C2C1-4482-BB03-6C97534597D7` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1500) | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 600, 800, 1000, 1200, 1500 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 100 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 35 | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID+$WTh` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS+$Fth` | sim |

_+ 29 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### PV (PLASTICO NTS-025)

Arquivo `ESGOTO\PV (PLASTICO NTS-025).xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "PV (PLASTICO NTS-025)" · PartID `7F2605C3-E51F-435D-872B-D3AF3E420B4F` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"PVP " + FormatNumber($SID,0) + " mm"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 500..100000 (padrão 1000) | sim |
| `SFD` | Frame Diameter | StructFrameDiameter | lista | mm | lista: 600, 800 | sim |
| `SFH` | Frame Height | StructFrameHeight | lista | mm | lista: 400, 600, 800, 1000 | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 1000, 1500, 2000 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 100 | sim |
| `SCH` | Cone Height | StructConeHeight | const | mm | 200 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 20 | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |

_+ 29 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### PV BALAO E CHAMINE (NTS0025)

Arquivo `ESGOTO\PV BALAO E CHAMINE (NTS0025).xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "PV BALAO E CHAMINE (NTS0025)" · PartID `A6F4D6CB-5781-44C0-A6E6-ABFD5FEB19DC` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1000..100000 (padrão 1000) | sim |
| `LenB6` | Chamine | Geometry_Distance3D | lista | mm | lista: 200, 500, 600, 800, 1000 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | lista | mm | lista: 0.001, 200 | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 600, 800, 1000 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | lista | mm | lista: 600, 650, 700, 750, 800, 850, 900, 950, 1000, 1050, 1100, 1150, 1200, 1250, 1300, 1350, 1400, 1450, 1500, 1550, 1600, 1650, 1700, 1750, 1800, 1900, 2000, 2050, 2100, 2150, 2200, 2250, 2300, 2350, 2400, 2450, 2500, 2550, 2600, 2650, 2700, 2750, 2800, 2850, 2900, 2950, 3000, 3050, 3100, 3150, 3200, 3250, 3300, 3350, 3400, 3450, 3500, 3550, 3600, 3650, 3700, 3750, 3800, 3850, 3900, 4000, 4050, 4100, 4150, 4200, 4250, 4300, 4350, 4400, 4450, 4500, 4550, 4600, 4650, 4700, 4750, 4800, 4850, 4900, 4950, 5000, 5050, 5100, 5150, 5200, 5250, 5300, 5350, 5400, 5450, 5500, 5550, 5600, 5650, 5700, 5750, 5800, 5850, 5900, 5950, 6000, 6050, 6100, 6150, 6200, 6250, 6300, 6350, 6400, 6450, 6500, 6550, 6600, 6650, 6700, 6750, 6800, 6850, 6900, 6950, 7000 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 200 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID + (2*$Wth)` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $Fth` | sim |

_+ 83 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### TI

Arquivo `ESGOTO\TI.xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "TI" · PartID `90D4544F-9EA8-419D-A170-3DA60E0F20C0` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"TI " + FormatNumber($SID,0) + " mm"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 500..100000 (padrão 1000) | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 100, 150, 200 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 0 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 20 | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |

_+ 5 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### TL

Arquivo `ESGOTO\TL.xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "TL" · PartID `E370D753-0220-4BDF-8A0F-3ECBEFFFEA84` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"TL " + FormatNumber($SID,0) + " mm"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 500..100000 (padrão 1000) | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 100, 150, 200 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 0 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 0 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 20 | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS` | sim |

_+ 8 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

### Capítulo Simple Shapes (6 família(s))

#### AeccSimpleBox_Metric

Arquivo `Simple Shapes\AeccSimpleBox_Metric.xml` · tipo `Struct_General` / subtipo `Undefined` · desc. "Simple Box -- Metric" · PartID `91CDFDA4-A109-4BAE-AD8D-85E7938B28A5` · forma `BoundingShape_Box`

Nome do tamanho: `FormatNumber($SBSL,0) + " x " + FormatNumber($SBSW,0) + " x " + FormatNumber($SBSH,0) + " Simple Box"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBSH` | Structure Height | StructHeight | faixa | mm | faixa 300..200000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 150 | sim |
| `SBSL` | Structure Length | StructLength | const | mm | 800 | sim |
| `SBSW` | Structure Width | StructWidth | const | mm | 600 | sim |
| `SFH` | Frame Height | StructFrameHeight | const | mm | 100 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 150 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `LenA1` | Box Width | Geometry_Distance2D | calc | mm | `$SBSW` | sim |
| `LenA2` | Box Length | Geometry_Distance2D | calc | mm | `$SBSL` | sim |
| `LenB1` | Box Height | Geometry_Distance3D | calc | mm | `$SBSH` | sim |

#### AeccSimpleCylinder_Metric

Arquivo `Simple Shapes\AeccSimpleCylinder_Metric.xml` · tipo `Struct_General` / subtipo `Undefined` · desc. "Simple Cylinder -- Metric" · PartID `9293D95D-75EF-4847-9BDA-7410F1C8C3EE` · forma `BoundingShape_Cylinder`

Nome do tamanho: `FormatNumber($SBSD,0) + " Simple Cylinder"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBSH` | Structure Height | StructHeight | faixa | mm | faixa 300..200000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 150 | sim |
| `SBSD` | Structure Diameter | StructDiameter | const | mm | 1600 | sim |
| `SFH` | Frame Height | StructFrameHeight | const | mm | 100 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 300 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `LenB1` | Cylinder Body Height | Geometry_Distance3D | calc | mm | `$SBSH` | sim |

_+ 1 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### CX TEMPLATE

Arquivo `Simple Shapes\CX TEMPLATE.xml` · tipo `Struct_InletOutlet` / subtipo `Undefined` · desc. "CX TEMPLATE" · PartID `08DB4A49-A912-4F0F-910E-7EE74EC1D4AB` · forma `BoundingShape_Box`

Nome do tamanho: `"CL " + FormatNumber($SBSL,0) + " x " + FormatNumber($SBSW,0)`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | Geometry_ModelParameter | faixa |  | faixa 2000..2000 (padrão 2000) | não |
| `LARGURA` | LARGURA DA CAIXA |  | lista | mm | lista: 1000, 2000 | sim |
| `SBSW` | Structure Width | StructWidth | lista | mm | lista: 1000, 2000 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | lista | mm | lista: 300, 400, 500, 600, 800, 1000, 1200, 1500, 1800, 2000 | sim |
| `Between` | Between | Entre | const | mm | 10 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 0 | sim |
| `LARGURA` | LARGURA DA CAIXA | Geometry_ModelDistanceParameter | const | mm | 2000 | não |
| `SBSH` | Structure Height | StructHeight | calc | mm | `1000.0000` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$LARGURA` | sim |
| `SHBTh` | Headwall Base Thickness | StructHwBaseThickness | calc | mm | `$SBSW` | sim |
| `SHBW` | Headwall Base Width | StructHwBaseWidth | calc | mm | `$SBSH` | sim |
| `SIL` | Inner Structure Length | StructInnerLength | calc | mm | `$SBSL` | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | calc | mm | `$SBSW` | sim |

_+ 10 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### PV ATÉ 1000_

Arquivo `Simple Shapes\PV ATÉ 1000_.xml` · tipo `Struct_Junction` / subtipo `PV` · desc. "PV ATÉ 1000" · PartID `CF229D36-7F09-4386-B5E1-77C6C35B248E` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1400..14000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100, 150, 200 | sim |
| `SCH` | Cone Height | StructConeHeight | lista | mm | lista: 400, 600, 800 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1900 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 1900 | não |
| `SFD` | Frame Diameter | StructFrameDiameter | const | mm | 1140 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 12 | não |
| `WTh` | Wall Thickness | WallThickness | const | mm | 200 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS-$SCH` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 79 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### PV ATÉ 1500_

Arquivo `Simple Shapes\PV ATÉ 1500_.xml` · tipo `Struct_Junction` / subtipo `PV` · desc. "PV ATÉ 1500" · PartID `E4CF9212-0D14-4AE9-BA5D-7831004D18AF` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1400..14000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 200 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 2400 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 2400 | não |
| `SCH` | Cone Height | StructConeHeight | const | mm | 400 | sim |
| `SFD` | Frame Diameter | StructFrameDiameter | const | mm | 1140 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 12 | não |
| `WTh` | Wall Thickness | WallThickness | const | mm | 200 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS-$SCH` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 79 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### PV ATÉ 2000_

Arquivo `Simple Shapes\PV ATÉ 2000_.xml` · tipo `Struct_Junction` / subtipo `PV` · desc. "PV ATÉ 2000" · PartID `9AE9B858-4EB8-4F10-B04F-546267AF620A` · forma `BoundingShape_Box`

Nome do tamanho: `"$PrtD"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 1400..14000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 200 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 2900 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 2900 | não |
| `SCH` | Cone Height | StructConeHeight | const | mm | 400 | sim |
| `SFD` | Frame Diameter | StructFrameDiameter | const | mm | 1140 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 12 | não |
| `WTh` | Wall Thickness | WallThickness | const | mm | 200 | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS-$SCH` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL+(2*WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW+(2*WTh)` | sim |

_+ 79 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

## Autodesk — Metric Structures

Pasta `Metric Structures` · .apc: name="Metric Structures", id `F1FEBE2D-D589-4f85-BF8E-650ED06A5EA5`, Desc "Metric Structure Catalog", .apc de 2025-04-07

### Capítulo Inlet-Outlets (6 família(s))

#### AeccStructRecEndSectionforRoundCMP_Metric

Arquivo `Inlet-Outlets\AeccStructRecEndSectionforRoundCMP_Metric.xml` · tipo `Struct_InletOutlet` / subtipo `Undefined` · desc. "CMP Rectangular End Section SI" · PartID `90063979-D72B-4557-9374-46EE68B26DD6` · forma `BoundingShape_Box`

Nome do tamanho: `FormatNumber($SBSL,0) + " x " + FormatNumber($SHBTh,0) + " x " + FormatNumber($SBSH,0) + " mm CMP Rectangular End Section"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBSH` | Structure Height | StructHeight | lista | mm | lista: 450, 525, 600, 750, 900, 1050, 1200, 1350, 1500, 1800 | sim |
| `SBSL` | Structure Length | StructLength | lista | mm | lista: 1300, 1500 | sim |
| `SBSW` | Structure Width | StructWidth | lista | mm | lista: 3860, 4190 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 25 | sim |
| `WPOf1` | Workplane Offset 1 | Geometry_WorkplaneOffset | const | mm | 13 | não |
| `SHBTh` | Headwall Base Thickness | StructHwBaseThickness | calc | mm | `$SBSW` | sim |
| `SHBW` | Headwall Base Width | StructHwBaseWidth | calc | mm | `$SBSL` | sim |
| `WPOf2` | Workplane Offset 2 | Geometry_WorkplaneOffset | calc | mm | `$WPOf1` | não |
| `WPOf3` | Workplane Offset 3 | Geometry_WorkplaneOffset | calc | mm | `$SBSL` | não |

_+ 6 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccStructRectBoxCulvertHeadwall_Metric

Arquivo `Inlet-Outlets\AeccStructRectBoxCulvertHeadwall_Metric.xml` · tipo `Struct_InletOutlet` / subtipo `Undefined` · desc. "Concrete Rectangular Box Culvert Headwall SI" · PartID `1FCC7A73-9334-4BBB-967B-6A946EF5DC27` · forma `BoundingShape_Box`

Nome do tamanho: `FormatNumber($SHBW,0) + " x " + FormatNumber($SHBTh,0) + " x " + FormatNumber($SBSH,0) + " mm Concrete Rectangular Box Culvert Headwall"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `LenA1` | Back Wall Width | Geometry_Distance2D | lista | mm | lista: 740, 800, 1200, 1775, 2015 | sim |
| `LenA3` | Wall Thickness | Geometry_Distance2D | lista | mm | lista: 80, 90, 100, 120, 140 (fora de ordem no XML) | sim |
| `LenA9` | Winged Wall Height | Geometry_Distance2D | lista | mm | lista: 387, 467, 507, 520, 667, 700, 780, 1100 (fora de ordem no XML) | sim |
| `SBSH` | Structure Height | StructHeight | lista | mm | lista: 840, 900, 1000, 1040, 1120, 1280, 1410, 1520 (fora de ordem no XML) | sim |
| `SHBTh` | Headwall Base Thickness | StructHwBaseThickness | lista | mm | lista: 650, 660, 700, 1010, 1620, 2020 (fora de ordem no XML) | sim |
| `SHBW` | Headwall Base Width | StructHwBaseWidth | lista | mm | lista: 1575, 1670, 1995, 2351, 2599, 3915 | sim |
| `WPOf2` | Back Wall Thickness | Geometry_WorkplaneOffset | lista | mm | lista: 80, 90, 120 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 450 | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SHBW` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SHBTh` | sim |
| `WPOf1` | Workplane Offset 1 | Geometry_WorkplaneOffset | calc | mm | `0.5*$WPOf2` | não |
| `WPOf3` | Workplane Offset 3 | Geometry_WorkplaneOffset | calc | mm | `$SHBTh` | não |

_+ 6 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccStructRectFlaredEndSection_Metric

Arquivo `Inlet-Outlets\AeccStructRectFlaredEndSection_Metric.xml` · tipo `Struct_InletOutlet` / subtipo `Undefined` · desc. "Concrete Flared End Section SI" · PartID `23B2AB2E-344C-444D-BCF5-F1107668A7BC` · forma `BoundingShape_Box`

Nome do tamanho: `FormatNumber($SBSH,0) + " mm Flared End Section  " + FormatNumber($SBSW,0) + " x " + FormatNumber($SBSL,0)`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBSH` | Pipe Diameter | StructHeight | lista | mm | lista: 350, 450, 525, 600, 750, 900, 1050, 1200, 1350, 1500, 1800 | sim |
| `SBSL` | Structure Length | StructLength | lista | mm | lista: 1880, 2490 | sim |
| `SBSW` | Structure Width | StructWidth | lista | mm | lista: 1700, 1870 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 1 | sim |
| `SHBTh` | Headwall Base Thickness | StructHwBaseThickness | calc | mm | `$SBSW` | sim |
| `SHBW` | Headwall Base Width | StructHwBaseWidth | calc | mm | `$SBSL` | sim |
| `WPOf1` | Workplane Offset 1 | Geometry_WorkplaneOffset | calc | mm | `$SBSL` | não |

_+ 5 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccStructRectHeadwall_Metric

Arquivo `Inlet-Outlets\AeccStructRectHeadwall_Metric.xml` · tipo `Struct_InletOutlet` / subtipo `Undefined` · desc. "Concrete Rectangular Headwall SI" · PartID `122E7F3A-B4D4-44E0-9326-0BCE8EF4FD3D` · forma `BoundingShape_Box`

Nome do tamanho: `FormatNumber($SHBW,0) + " x " + FormatNumber($SHBTh,0) + " x " + FormatNumber($SBSH,0) + " mm Concrete Rectangular Headwall"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBSH` | Structure Height | StructHeight | lista | mm | lista: 2794, 2946, 3099, 3251, 3404, 3556, 3708, 3861, 4013, 4166 | sim |
| `SHBW` | Headwall Base Width | StructHwBaseWidth | lista | mm | lista: 2668, 2896, 3124, 3353, 3581, 3810, 4039, 4267, 4496, 4724 | sim |
| `SHBTh` | Headwall Base Thickness | StructHwBaseThickness | const | mm | 305 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 200 | sim |
| `LenA1` | Box Width | Geometry_Distance2D | calc | mm | `$SHBTh` | sim |
| `LenA2` | Box Length | Geometry_Distance2D | calc | mm | `$SHBW` | sim |
| `LenB1` | Box Height | Geometry_Distance3D | calc | mm | `$SBSH` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SHBW` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SHBTh` | sim |

#### AeccStructRectHeadwallVarHt_Metric

Arquivo `Inlet-Outlets\AeccStructRectHeadwallVarHt_Metric.xml` · tipo `Struct_InletOutlet` / subtipo `Undefined` · desc. "Rectangular Headwall Variable Height SI" · PartID `F26EF489-FE01-4546-9397-53B1C5F9E8E5` · forma `BoundingShape_Box`

Nome do tamanho: `FormatNumber($SHBW,0) + " x " + FormatNumber($SHBTh,0) + " x " + FormatNumber($SBSH,0) + " mm Concrete Rectangular Headwall VH"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBSH` | Structure Height | StructHeight | faixa | mm | faixa 300..20000 (padrão 900) | sim |
| `SHBW` | Headwall Base Width | StructHwBaseWidth | lista | mm | lista: 2668, 2896, 3124, 3353, 3581, 3810, 4039, 4267, 4496, 4724 | sim |
| `SHBTh` | Headwall Base Thickness | StructHwBaseThickness | const | mm | 300 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 150 | sim |
| `LenA1` | Box Width | Geometry_Distance2D | calc | mm | `$SHBTh` | sim |
| `LenA2` | Box Length | Geometry_Distance2D | calc | mm | `$SHBW` | sim |
| `LenB1` | Box Height | Geometry_Distance3D | calc | mm | `$SBSH` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SHBW` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SHBTh` | sim |

#### AeccStructRectWingedHeadwall_Metric

Arquivo `Inlet-Outlets\AeccStructRectWingedHeadwall_Metric.xml` · tipo `Struct_InletOutlet` / subtipo `Undefined` · desc. "Concrete Rectangular Winged Headwall SI" · PartID `667FFEAD-A0C4-4752-8002-3F82271CB353` · forma `BoundingShape_Box`

Nome do tamanho: `FormatNumber($SHBW,0) + " x " + FormatNumber($SHBTh,0) + " x " + FormatNumber($SBSH,0) + " mm Concrete Rectangular Winged Headwall"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBSH` | Structure Height | StructHeight | lista | mm | lista: 1143, 1397, 1753, 1956, 2108 | sim |
| `SHBTh` | Headwall Base Thickness | StructHwBaseThickness | lista | mm | lista: 305 | sim |
| `SHBW` | Headwall Base Width | StructHwBaseWidth | lista | mm | lista: 965, 1372, 1778 | sim |
| `PathA1` | Path Angle 1 | PathAngle | const | deg | 120 | não |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 150 | sim |
| `WPOf1` | Distance between Top and Winged Wall Top | Geometry_WorkplaneOffset | const | mm | 380 | não |
| `WPOf4` | Distance between Base Top and Base Bottom | Geometry_WorkplaneOffset | const | mm | 300 | não |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SHBW` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SHBTh` | sim |
| `WPOf3` | Distance between Top and Base Top | Geometry_WorkplaneOffset | calc | mm | `$SBSH-$WPOf4` | não |

_+ 10 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

### Capítulo Junction Structures with Frames (12 família(s))

#### AeccStructConcentricCylinder_Metric

Arquivo `Junction Structures with Frames\AeccStructConcentricCylinder_Metric.xml` · tipo `Struct_Junction` / subtipo `Concentric` · desc. "Concentric Cylindrical Structure SI" · PartID `F4657229-FA05-4718-9B1A-9230101749E6` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"Concentric Structure " + FormatNumber($SID,0) + " dia " + FormatNumber($SFD,0) + " frame " + FormatNumber($SCH,0) + " cone " + FormatNumber($WTh,0) + " wall " + FormatNumber($FTh,0) + " floor"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..2000000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 150, 170, 200, 250 | sim |
| `SCH` | Cone Height | StructConeHeight | lista | mm | lista: 600, 900, 1200 | sim |
| `SFD` | Frame Diameter | StructFrameDiameter | lista | mm | lista: 450, 500, 530, 550, 600 | sim |
| `SFH` | Frame Height | StructFrameHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 1200, 1350, 1500, 1800, 2050, 2300, 2550 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 125, 150, 170, 200 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | const | mm | 500 | sim |
| `LenB1` | Frame Cylinder Height | Geometry_Distance3D | calc | mm | `$SFH` | sim |
| `LenB2` | Barrel Cylinder Height | Geometry_Distance3D | calc | mm | `$SRS + $FTh - ($SCH + $SFH)` | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID + (2.0 * $WTh)` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | calc | mm | `$SFH + $SCH + $SBPC` | sim |
| `WPOf1` | Workplane Offset  1 | Geometry_WorkplaneOffset | calc | mm | `$SFH` | sim |
| `WPOf2` | Workplane Offset  2 | Geometry_WorkplaneOffset | calc | mm | `$SFH + $SCH` | sim |

_+ 3 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccStructConcentricCylinderRectFrame_Metric

Arquivo `Junction Structures with Frames\AeccStructConcentricCylinderRectFrame_Metric.xml` · tipo `Struct_Junction` / subtipo `Concentric` · desc. "Concentric Cylindrical Structure Rectangular Frame SI" · PartID `A0ADFF70-5730-4F3F-B2F6-9C39DCA817E0` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"Concentric Structure " + FormatNumber($SID,0) + " dia " + FormatNumber($SFL,0) + " x " + FormatNumber($SFW,0) + "  frame " + FormatNumber($SCH,0) + " cone " + FormatNumber($WTh,0) + " wall " + FormatNumber($FTh,0) + " floor"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..2000000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 150, 170, 200, 250 | sim |
| `SCH` | Cone Height | StructConeHeight | lista | mm | lista: 600, 900, 1200 | sim |
| `SFH` | Frame Height | StructFrameHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SFL` | Frame Length | StructFrameLength | lista | mm | lista: 450, 500, 530, 550, 600 | sim |
| `SFW` | Frame Width | StructFrameWidth | lista | mm | lista: 450, 500, 530, 550, 600 | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 1200, 1350, 1500, 1800, 2050, 2300, 2550 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 125, 150, 170, 200 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | const | mm | 500 | sim |
| `LenB2` | Barrel Cylinder Height | Geometry_Distance3D | calc | mm | `$SRS + $FTh - ($SCH + $SFH)` | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID + (2.0 * $WTh)` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | calc | mm | `$SFH + $SCH + $SBPC` | sim |
| `WPOf1` | Frame Height | Geometry_WorkplaneOffset | calc | mm | `$SFH` | sim |
| `WPOf2` | Workplane Offset  2 | Geometry_WorkplaneOffset | calc | mm | `$SFH + $SCH` | sim |

_+ 4 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccStructCylSlabTopCylFrame_Metric

Arquivo `Junction Structures with Frames\AeccStructCylSlabTopCylFrame_Metric.xml` · tipo `Struct_Junction` / subtipo `Slab Top` · desc. "Cylindrical Structure Slab Top Circular Frame SI" · PartID `D0F590F0-0250-436D-B660-347AD681DC3C` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"Slab Top Cylindrical Structure " + FormatNumber($SID,0) + " dia " + FormatNumber($SFD,0) + " dia Frm " + FormatNumber($SFH,0) + " FrHt " + FormatNumber($SSTh,0) + " slab " + FormatNumber($WTh,0) + " wall " + FormatNumber($FTh,0) + " floor"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..200000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 150, 170, 200, 250 | sim |
| `SFD` | Frame Diameter | StructFrameDiameter | lista | mm | lista: 400, 450, 500, 530, 550, 600 | sim |
| `SFH` | Frame Height | StructFrameHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 300, 450, 600, 750, 900, 1050, 1200, 1350, 1500, 1800, 2050, 2300, 2550, 3060 | sim |
| `SSTh` | Slab Thickness | StructSlabThickness | lista | mm | lista: 150, 200, 250, 300 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 65, 90, 100, 115, 130, 145, 175, 190, 210, 270, 330, 350 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | const | mm | 300 | sim |
| `LenB1` | Frame Cylinder Height | Geometry_Distance3D | calc | mm | `$SFH` | sim |
| `LenB2` | Barrel Cylinder Height | Geometry_Distance3D | calc | mm | `$SRS + $FTh - $SFH` | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID + (2.0 * $WTh)` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | calc | mm | `$SFH + $SBPC` | sim |
| `WPOf1` | Workplane Offset  1 | Geometry_WorkplaneOffset | calc | mm | `$SFH` | sim |

_+ 2 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccStructCylSlabTopRectFrame_Metric

Arquivo `Junction Structures with Frames\AeccStructCylSlabTopRectFrame_Metric.xml` · tipo `Struct_Junction` / subtipo `Slab Top` · desc. "Cylindrical Structure Slab Top Rectangular Frame SI" · PartID `EBF51975-14DC-4AAA-8ECA-9BA3F9672CFE` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"Slab Top Cylindrical Structure " + FormatNumber($SID,0) + " dia " + FormatNumber($SFL,0) + " x " + FormatNumber($SFW,0) + "  Frm " + FormatNumber($SFH,0) + " FrHt " + FormatNumber($SSTh,0) + " slab " + FormatNumber($WTh,0) + " wall " + FormatNumber($FTh,0) + " floor"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..200000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 150, 170, 200, 250 | sim |
| `SFH` | Frame Height | StructFrameHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SFL` | Frame Length | StructFrameLength | lista | mm | lista: 400, 450, 500, 530, 550, 600 | sim |
| `SFW` | Frame Width | StructFrameWidth | lista | mm | lista: 400, 450, 500, 530, 550, 600 | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 300, 450, 600, 750, 900, 1050, 1200, 1350, 1500, 1800, 2050, 2300, 2550, 3060 | sim |
| `SSTh` | Slab Thickness | StructSlabThickness | lista | mm | lista: 150, 200, 250, 300 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 65, 90, 100, 115, 130, 145, 175, 190, 210, 270, 330, 350 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | const | mm | 300 | sim |
| `LenB2` | Barrel Cylinder Height | Geometry_Distance3D | calc | mm | `$SRS + $FTh - $SFH` | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID + (2.0 * $WTh)` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | calc | mm | `$SFH + $SBPC` | sim |
| `WPOf1` | Frame Height | Geometry_WorkplaneOffset | calc | mm | `$SFH` | sim |

_+ 3 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccStructEccentricCylinder_Metric

Arquivo `Junction Structures with Frames\AeccStructEccentricCylinder_Metric.xml` · tipo `Struct_Junction` / subtipo `Eccentric` · desc. "Eccentric Cylindrical Structure SI" · PartID `A160595E-3B24-4B41-AA42-5183FB59E938` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"Eccentric Structure " + FormatNumber($SID,0) + " dia " + FormatNumber($SFD,0) + " frame " + FormatNumber($SCH,0) + " cone " + FormatNumber($WTh,0) + " wall " + FormatNumber($FTh,0) + " floor"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..2000000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 150, 170, 200, 250 | sim |
| `SCH` | Cone Height | StructConeHeight | lista | mm | lista: 600, 900, 1200 | sim |
| `SFD` | Frame Diameter | StructFrameDiameter | lista | mm | lista: 450, 500, 530, 550, 600 | sim |
| `SFH` | Frame Height | StructFrameHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 1200, 1350, 1500, 1800, 2050, 2300, 2550 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 125, 150, 170, 200 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | const | mm | 600 | sim |
| `LenA1` | Eccentric Offset Distance | Geometry_Distance2D | calc | mm | `($BdyD3 - $BdyD2) * 0.50` | sim |
| `LenB1` | Frame Cylinder Height | Geometry_Distance3D | calc | mm | `$SFH` | sim |
| `LenB2` | Barrel Cylinder Height | Geometry_Distance3D | calc | mm | `$SRS + $FTh - ($SCH + $SFH)` | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID + (2.0 * $WTh)` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | calc | mm | `$SFH + $SCH + $SBPC` | sim |
| `WPOf1` | Workplane Offset  1 | Geometry_WorkplaneOffset | calc | mm | `$SFH` | sim |
| `WPOf2` | Workplane Offset  2 | Geometry_WorkplaneOffset | calc | mm | `$SFH + $SCH` | sim |

_+ 3 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccStructEccentricCylinderRectFrame_Metric

Arquivo `Junction Structures with Frames\AeccStructEccentricCylinderRectFrame_Metric.xml` · tipo `Struct_Junction` / subtipo `Eccentric` · desc. "Eccentric Cylindrical Structure Rectangular Frame SI" · PartID `80CCEF6A-5E7C-421B-A3C4-373173516AB0` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"Eccentric Structure " + FormatNumber($SID,0) + " dia " + FormatNumber($SFL,0) + " x " + FormatNumber($SFW,0) + " frame " + FormatNumber($SCH,0) + " cone " + FormatNumber($WTh,0) + " wall " + FormatNumber($FTh,0) + " floor"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..2000000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 150, 170, 200, 250 | sim |
| `SCH` | Cone Height | StructConeHeight | lista | mm | lista: 600, 900, 1200 | sim |
| `SFH` | Frame Height | StructFrameHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SFL` | Frame Length | StructFrameLength | lista | mm | lista: 450, 500, 530, 550, 600 | sim |
| `SFW` | Frame Width | StructFrameWidth | lista | mm | lista: 450, 500, 530, 550, 600 | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 1200, 1350, 1500, 1800, 2050, 2300, 2550 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 125, 150, 170, 200 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | const | mm | 600 | sim |
| `LenA1` | Eccentric Offset Distance | Geometry_Distance2D | calc | mm | `($BdyD3 - $BdyD2) * 0.50` | sim |
| `LenB2` | Barrel Cylinder Height | Geometry_Distance3D | calc | mm | `$SRS + $FTh - ($SCH + $SFH)` | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID + (2.0 * $WTh)` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | calc | mm | `$SFH + $SCH + $SBPC` | sim |
| `WPOf1` | Frame Height | Geometry_WorkplaneOffset | calc | mm | `$SFH` | sim |
| `WPOf2` | Workplane Offset  2 | Geometry_WorkplaneOffset | calc | mm | `$SFH + $SCH` | sim |

_+ 4 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccStructRectSlabTopCylFrame_Metric

Arquivo `Junction Structures with Frames\AeccStructRectSlabTopCylFrame_Metric.xml` · tipo `Struct_Junction` / subtipo `Slab Top` · desc. "Rectangular Structure Slab Top Circular Frame SI" · PartID `4432305F-A745-445A-9CD9-2FB8E1EDD048` · forma `BoundingShape_Box`

Nome do tamanho: `FormatNumber($SIL,0) + " x " + FormatNumber($SIW,0) + " Rect Structure " + FormatNumber($SFD,0) + " dia Frm " + FormatNumber($SFH,0) + " FmHt " + FormatNumber($SSTh,0) + " Slab " + FormatNumber($WTh,0) + " Wall " + FormatNumber($FTh,0) + " Floor"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..200000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100, 125, 150, 175, 200, 225, 250, 300, 350 | sim |
| `SFD` | Frame Diameter | StructFrameDiameter | lista | mm | lista: 400, 450, 500, 530, 550, 600 | sim |
| `SFH` | Frame Height | StructFrameHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1500, 1750, 1875, 2000, 2250, 2400, 2500, 2700, 2750, 3000, 3300, 3500, 3600, 4000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 750, 1000, 1200, 1250, 1500, 1750, 1800, 2000, 2500, 2750, 3000, 3500, 4000 | sim |
| `SSTh` | Slab Thickness | StructSlabThickness | lista | mm | lista: 100, 125, 150, 175, 200, 225, 250, 300, 350 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 75, 100, 125, 150, 175, 200, 225, 250, 300, 350 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | const | mm | 300 | sim |
| `LenA1` | Structure Width | Geometry_Distance2D | calc | mm | `$SIW + (2.0 * $WTh)` | sim |
| `LenA2` | Structure Length | Geometry_Distance2D | calc | mm | `$SIL + (2.0 * $WTh)` | sim |
| `LenB1` | Slab To Bottom Dist | Geometry_Distance3D | calc | mm | `$SRS + $FTh - $SFH` | sim |
| `LenB2` | Frame Cyl Height | Geometry_Distance3D | calc | mm | `$SFH` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL + (2.0 * $WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + (2.0 * $WTh)` | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | calc | mm | `$SSTh + $SBPC` | sim |
| `WPOf1` | Slab Top Plane | Geometry_WorkplaneOffset | calc | mm | `$SFH` | sim |

_+ 1 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccStructRectSlabTopRectFrame_Metric

Arquivo `Junction Structures with Frames\AeccStructRectSlabTopRectFrame_Metric.xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "Rectangular Structure Slab Top Rectangular Frame SI" · PartID `A90411C9-3E16-48CA-BBFA-5FED1B7BC242` · forma `BoundingShape_Box`

Nome do tamanho: `FormatNumber($SIL,0) + " x " + FormatNumber($SIW,0) + " Rect Structure " + FormatNumber($SFL,0) + " x " + FormatNumber($SFW,0) + " Frm " + FormatNumber($SFH,0) + " FmHt " + FormatNumber($SSTh,0) + " Slab " + FormatNumber($WTh,0) + " Wall " + FormatNumber($FTh,0) + " Floor"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..200000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 100, 125, 150, 175, 200, 225, 250, 300, 350 | sim |
| `SFH` | Frame Height | StructFrameHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SFL` | Frame Length | StructFrameLength | lista | mm | lista: 300, 350, 400, 450, 500, 530, 550, 600, 650, 700, 800, 900 | sim |
| `SFW` | Frame Width | StructFrameWidth | lista | mm | lista: 300, 350, 400, 450, 500, 530, 550, 600, 650, 700, 800, 900 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1500, 1750, 1875, 2000, 2250, 2400, 2500, 2700, 2750, 3000, 3300, 3500, 3600, 4000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 750, 1000, 1200, 1250, 1500, 1750, 1800, 2000, 2500, 2750, 3000, 3500, 4000 | sim |
| `SSTh` | Slab Thickness | StructSlabThickness | lista | mm | lista: 100, 125, 150, 175, 200, 225, 250, 300, 350 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 75, 100, 125, 150, 175, 200, 225, 250, 300, 350 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | const | mm | 200 | sim |
| `LenA1` | Structure Width Len1 | Geometry_Distance2D | calc | mm | `$SIW + (2.0 * $WTh)` | sim |
| `LenA2` | Structure Length Len2 | Geometry_Distance2D | calc | mm | `$SIL + (2.0 * $WTh)` | sim |
| `LenA3` | Frame Width Len3 | Geometry_Distance2D | calc | mm | `$SFW` | sim |
| `LenA4` | Frame Length Len4 | Geometry_Distance2D | calc | mm | `$SFL` | sim |
| `LenB1` | Slab To Bottom Dist | Geometry_Distance3D | calc | mm | `$SRS + $FTh - $SFH` | sim |
| `LenB2` | Frame Top To Slap Top Dist | Geometry_Distance3D | calc | mm | `$SFH` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL + (2.0 * $WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + (2.0 * $WTh)` | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | calc | mm | `$SSTh + $SBPC` | sim |
| `WPOf1` | Slab Top Plane | Geometry_WorkplaneOffset | calc | mm | `$SFH` | sim |

#### AeccStructTwoTierEccentricCyl_Metric

Arquivo `Junction Structures with Frames\AeccStructTwoTierEccentricCyl_Metric.xml` · tipo `Struct_Junction` / subtipo `Eccentric` · desc. "Eccentric Cylindrical 2-Tier Circular Frame SI" · PartID `F4CD9C5C-0EAF-4C3A-BC34-BD8713278B68` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"Eccentric Two Tier Structure " + FormatNumber($SID,0) + " dia " + FormatNumber($SFD,0) + " frame " + FormatNumber($SCH,0) + " cone " + FormatNumber($WTh,0) + " wall " + FormatNumber($FTh,0) + " floor"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBH` | Barrel Height | StructBarrelHeight | faixa | mm | faixa 150..200000 (padrão 800) | sim |
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..200000 (padrão 1800) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 170, 200, 250 | sim |
| `SCH` | Cone Height | StructConeHeight | lista | mm | lista: 600, 900, 1200 | sim |
| `SFD` | Frame Diameter | StructFrameDiameter | lista | mm | lista: 400, 450, 500, 530, 550, 600 | sim |
| `SFH` | Frame Height | StructFrameHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 1800, 2300, 2550, 3060 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 170, 200, 250 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | const | mm | 300 | sim |
| `LenA1` | Eccentric Offset Distance | Geometry_Distance2D | calc | mm | `($SID + (2.0 * $WTh) - $BdyD2) * 0.50` | sim |
| `LenB1` | Frame Cyl Height | Geometry_Distance3D | calc | mm | `$SFH` | sim |
| `LenB2` | 3D Distance  2 | Geometry_Distance3D | calc | mm | `$SBH + $FTh` | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID + (2.0 * $WTh)` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `STest1` | Part Size Validation Test 1 | PartSizeValidation_Test | calc |  | `$SID > $BdyD2` | não |
| `STest2` | Part Size Validation Test 2 | PartSizeValidation_Test | calc |  | `$SRS > ($SBH + $SCH)` | não |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | calc | mm | `$SBPC + ($SRS - $SBH)` | sim |
| `WPOf1` | Workplane Offset  1 | Geometry_WorkplaneOffset | calc | mm | `$SRS - ($SBH + $SCH)` | sim |
| `WPOf2` | Workplane Offset  2 | Geometry_WorkplaneOffset | calc | mm | `$SRS - $SBH` | sim |
| `WPOf3` | Workplane Offset  3 | Geometry_WorkplaneOffset | calc | mm | `$SFH` | sim |

_+ 4 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccStructTwoTierEccentricCylRectFrame_Metric

Arquivo `Junction Structures with Frames\AeccStructTwoTierEccentricCylRectFrame_Metric.xml` · tipo `Struct_Junction` / subtipo `Eccentric` · desc. "Eccentric Cylindrical 2-Tier Rectangular Frame SI" · PartID `607AFA32-8E22-4F76-B99E-DEB8E1D38CCE` · forma `BoundingShape_Cylinder`

Nome do tamanho: `"Eccentric Two Tier Structure " + FormatNumber($SID,0) + " dia " + FormatNumber($SFL,0) + " x " + FormatNumber($SFW,0) + " frame " + FormatNumber($SCH,0) + " cone " + FormatNumber($WTh,0) + " wall " + FormatNumber($FTh,0) + " floor"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBH` | Barrel Height | StructBarrelHeight | faixa | mm | faixa 150..200000 (padrão 800) | sim |
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..200000 (padrão 1800) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 170, 200, 250 | sim |
| `SCH` | Cone Height | StructConeHeight | lista | mm | lista: 600, 900, 1200 | sim |
| `SFH` | Frame Height | StructFrameHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SFL` | Frame Length | StructFrameLength | lista | mm | lista: 400, 450, 500, 530, 550, 600 | sim |
| `SFW` | Frame Width | StructFrameWidth | lista | mm | lista: 400, 450, 500, 530, 550, 600 | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 1800, 2300, 2550, 3060 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 170, 200, 250 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | const | mm | 300 | sim |
| `LenA1` | Eccentric Offset Distance | Geometry_Distance2D | calc | mm | `($SID + (2.0 * $WTh) - $BdyD2) * 0.50` | sim |
| `LenB2` | 3D Distance  2 | Geometry_Distance3D | calc | mm | `$SBH + $FTh` | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID + (2.0 * $WTh)` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `STest1` | Part Size Validation Test 1 | PartSizeValidation_Test | calc |  | `$SID > $BdyD2` | não |
| `STest2` | Part Size Validation Test 2 | PartSizeValidation_Test | calc |  | `$SRS > ($SBH + $SCH)` | não |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | calc | mm | `$SBPC + ($SRS - $SBH)` | sim |
| `WPOf1` | Workplane Offset  1 | Geometry_WorkplaneOffset | calc | mm | `$SRS - ($SBH + $SCH)` | sim |
| `WPOf2` | Workplane Offset  2 | Geometry_WorkplaneOffset | calc | mm | `$SRS - $SBH` | sim |
| `WPOf3` | Frame Height | Geometry_WorkplaneOffset | calc | mm | `$SFH` | sim |

_+ 5 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccStructTwoTierRectBase_Metric

Arquivo `Junction Structures with Frames\AeccStructTwoTierRectBase_Metric.xml` · tipo `Struct_Junction` / subtipo `Rectangular` · desc. "Rectangular Structure 2-Tier Circular Frame SI" · PartID `69E31713-3BEA-4BB3-9BAF-3AD51780D5C5` · forma `BoundingShape_Box`

Nome do tamanho: `FormatNumber($SIL,0) + " x " + FormatNumber($SIW,0) + " Rect Two Tier Structure " + FormatNumber($SFD,0) + " dia Frm " + FormatNumber($SFH,0) + " FmHt " + FormatNumber($SSTh,0) + " Slab " + FormatNumber($WTh,0) + " Wall " + FormatNumber($FTh,0) + " Floor"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBH` | Barrel Height | StructBarrelHeight | faixa | mm | faixa 300..200000 (padrão 800) | sim |
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..200000 (padrão 1800) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 200, 250, 300 | sim |
| `SFD` | Frame Diameter | StructFrameDiameter | lista | mm | lista: 400, 450, 500, 530, 550, 600 | sim |
| `SFH` | Frame Height | StructFrameHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1500, 1750, 1800, 2000, 2500, 2750, 3000, 3500, 4000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 1500, 1750, 1800, 2000, 2500, 2750, 3000, 3500, 4000 | sim |
| `SSTh` | Slab Thickness | StructSlabThickness | lista | mm | lista: 175, 200, 225, 250, 300, 350 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 175, 190, 210, 270, 330, 350 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | const | mm | 300 | sim |
| `LenA1` | Structure Width | Geometry_Distance2D | calc | mm | `$SIW + (2.0 * $WTh)` | sim |
| `LenA2` | Structure Length | Geometry_Distance2D | calc | mm | `$SIL + (2.0 * $WTh)` | sim |
| `LenB1` | Rectangular Base Height | Geometry_Distance3D | calc | mm | `$SBH + $FTh` | sim |
| `LenB2` | Frame Cyl Height | Geometry_Distance3D | calc | mm | `$SFH` | sim |
| `LenB3` | Access Cyl Height | Geometry_Distance3D | calc | mm | `$SRS - $SBH - $SFH` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL + (2.0 * $WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + (2.0 * $WTh)` | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | calc | mm | `$SBPC + ($SRS - $SBH)` | sim |
| `WPOf1` | Base Top Plane Offset | Geometry_WorkplaneOffset | calc | mm | `$SRS - $SBH` | sim |
| `WPOf2` | Access Cyl Top Plane Offset | Geometry_WorkplaneOffset | calc | mm | `$SFH` | sim |

_+ 2 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccStructTwoTierRectBaseRectFrame_Metric

Arquivo `Junction Structures with Frames\AeccStructTwoTierRectBaseRectFrame_Metric.xml` · tipo `Struct_Junction` / subtipo `Rectangular` · desc. "Rectangular Structure 2-Tier Rectangular Frame SI" · PartID `A1BF7AC5-F7B7-4230-B5FB-F628476E1886` · forma `BoundingShape_Box`

Nome do tamanho: `FormatNumber($SIL,0) + " x " + FormatNumber($SIW,0) + " Rect Two Tier Structure " + FormatNumber($SFL,0) + " x " + FormatNumber($SFW,0) + " Frm " + FormatNumber($SFH,0) + " FmHt " + FormatNumber($SSTh,0) + " Slab " + FormatNumber($WTh,0) + " Wall " + FormatNumber($FTh,0) + " Floor"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBH` | Barrel Height | StructBarrelHeight | faixa | mm | faixa 300..200000 (padrão 800) | sim |
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..200000 (padrão 1800) | sim |
| `FTh` | Floor Thickness | FloorThickness | lista | mm | lista: 200, 250, 300 | sim |
| `SFH` | Frame Height | StructFrameHeight | lista | mm | lista: 100, 150, 200, 250, 300 | sim |
| `SFL` | Frame Length | StructFrameLength | lista | mm | lista: 400, 450, 500, 530, 550, 600 | sim |
| `SFW` | Frame Width | StructFrameWidth | lista | mm | lista: 400, 450, 500, 530, 550, 600 | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 1500, 1750, 1800, 2000, 2500, 2750, 3000, 3500, 4000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 1500, 1750, 1800, 2000, 2500, 2750, 3000, 3500, 4000 | sim |
| `SSTh` | Slab Thickness | StructSlabThickness | lista | mm | lista: 175, 200, 225, 250, 300, 350 | sim |
| `WTh` | Wall Thickness | WallThickness | lista | mm | lista: 175, 190, 210, 270, 330, 350 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | const | mm | 300 | sim |
| `LenA1` | Structure Width | Geometry_Distance2D | calc | mm | `$SIW + (2.0 * $WTh)` | sim |
| `LenA2` | Structure Length | Geometry_Distance2D | calc | mm | `$SIL + (2.0 * $WTh)` | sim |
| `LenB1` | Rectangular Base Height | Geometry_Distance3D | calc | mm | `$SBH + $FTh` | sim |
| `LenB3` | Access Cyl Height | Geometry_Distance3D | calc | mm | `$SRS - $SBH - $SFH` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL + (2.0 * $WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + (2.0 * $WTh)` | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | calc | mm | `$SBPC + ($SRS - $SBH)` | sim |
| `WPOf1` | Base Top Plane Offset | Geometry_WorkplaneOffset | calc | mm | `$SRS - $SBH` | sim |
| `WPOf2` | Frame Height | Geometry_WorkplaneOffset | calc | mm | `$SFH` | sim |

_+ 3 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

### Capítulo Junction Structures without Frames (3 família(s))

#### AeccCylinderJunctionStructureNF_Metric

Arquivo `Junction Structures without Frames\AeccCylinderJunctionStructureNF_Metric.xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "Cylindrical Junction Structure NF SI" · PartID `068A9DC3-FBB1-47B6-AAFE-67AA722BF948` · forma `BoundingShape_Cylinder`

Nome do tamanho: `FormatNumber($SID,0) + " mm Cylindrical Structure"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..200000 (padrão 1400) | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 1200, 1400, 1600, 1800 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 100 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 200 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `LenB1` | Cylinder Body Height | Geometry_Distance3D | calc | mm | `$SBSH` | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID + (2.0 * $WTh)` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |

_+ 1 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

#### AeccRectangularJunctionStructureNF_Metric

Arquivo `Junction Structures without Frames\AeccRectangularJunctionStructureNF_Metric.xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "Rectangular Junction Structure NF SI" · PartID `1CF61C61-F401-48C2-B423-7EFEDA40B164` · forma `BoundingShape_Box`

Nome do tamanho: `FormatNumber($SIW,0) + " x " + FormatNumber($SIL,0) + " mm Rectangular Structure"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..20000 (padrão 1200) | sim |
| `SIL` | Inner Structure Length | StructInnerLength | lista | mm | lista: 750, 1000, 1200, 1500, 1750, 1800, 2000, 2500, 2750, 3000 | sim |
| `SIW` | Inner Structure Width | StructInnerWidth | lista | mm | lista: 750, 1000, 1200, 1500, 1750, 1800, 2000, 2500, 2750, 3000 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 100 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 150 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `LenA1` | Box Width | Geometry_Distance2D | calc | mm | `$SIW + (2.0 * $WTh)` | sim |
| `LenA2` | Box Length | Geometry_Distance2D | calc | mm | `$SIL + (2.0 * $WTh)` | sim |
| `LenB1` | Box Height | Geometry_Distance3D | calc | mm | `$SRS + $FTh` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SBSL` | Structure Length | StructLength | calc | mm | `$SIL + (2.0 * $WTh)` | sim |
| `SBSW` | Structure Width | StructWidth | calc | mm | `$SIW + (2.0 * $WTh)` | sim |

#### AeccStructConcentricCylinderNF_Metric

Arquivo `Junction Structures without Frames\AeccStructConcentricCylinderNF_Metric.xml` · tipo `Struct_Junction` / subtipo `Undefined` · desc. "Concentric Cylindrical Structure NF SI" · PartID `9D1237FA-055D-459C-AAF0-ECD78CC03743` · forma `BoundingShape_Cylinder`

Nome do tamanho: `FormatNumber($SID,0) + " mm dia " + FormatNumber($SCH,0) + " mm cone Concentric Structure"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SRS` | Rim to Sump Height | StructRimToSumpHeight | faixa | mm | faixa 300..200000 (padrão 1400) | sim |
| `SCH` | Cone Height | StructConeHeight | lista | mm | lista: 600, 900, 1200 | sim |
| `SID` | Inner Structure Diameter | StructInnerDiameter | lista | mm | lista: 1200, 1400, 1600, 1800 | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 100 | sim |
| `SBPC` | Barrel Pipe Clearance | StructBarrelPipeClearance | const | mm | 200 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `LenB2` | Barrel Cylinder Height | Geometry_Distance3D | calc | mm | `$SRS + $FTh - $SCH` | sim |
| `SBSD` | Structure Diameter | StructDiameter | calc | mm | `$SID + (2.0 * $WTh)` | sim |
| `SBSH` | Structure Height | StructHeight | calc | mm | `$SRS + $FTh` | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | calc | mm | `$SCH + $SBPC` | sim |
| `WPOf2` | Workplane Offset  2 | Geometry_WorkplaneOffset | calc | mm | `$SCH` | sim |

_+ 2 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

### Capítulo Simple Shapes (2 família(s))

#### AeccSimpleBox_Metric

Arquivo `Simple Shapes\AeccSimpleBox_Metric.xml` · tipo `Struct_General` / subtipo `Undefined` · desc. "Simple Box -- Metric" · PartID `91CDFDA4-A109-4BAE-AD8D-85E7938B28A5` · forma `BoundingShape_Box`

Nome do tamanho: `FormatNumber($SBSL,0) + " x " + FormatNumber($SBSW,0) + " x " + FormatNumber($SBSH,0) + " Simple Box"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBSH` | Structure Height | StructHeight | faixa | mm | faixa 300..200000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 150 | sim |
| `SBSL` | Structure Length | StructLength | const | mm | 800 | sim |
| `SBSW` | Structure Width | StructWidth | const | mm | 600 | sim |
| `SFH` | Frame Height | StructFrameHeight | const | mm | 100 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 150 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `LenA1` | Box Width | Geometry_Distance2D | calc | mm | `$SBSW` | sim |
| `LenA2` | Box Length | Geometry_Distance2D | calc | mm | `$SBSL` | sim |
| `LenB1` | Box Height | Geometry_Distance3D | calc | mm | `$SBSH` | sim |

#### AeccSimpleCylinder_Metric

Arquivo `Simple Shapes\AeccSimpleCylinder_Metric.xml` · tipo `Struct_General` / subtipo `Undefined` · desc. "Simple Cylinder -- Metric" · PartID `9293D95D-75EF-4847-9BDA-7410F1C8C3EE` · forma `BoundingShape_Cylinder`

Nome do tamanho: `FormatNumber($SBSD,0) + " Simple Cylinder"`

| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |
|---|---|---|---|---|---|---|
| `SBSH` | Structure Height | StructHeight | faixa | mm | faixa 300..200000 (padrão 1400) | sim |
| `FTh` | Floor Thickness | FloorThickness | const | mm | 150 | sim |
| `SBSD` | Structure Diameter | StructDiameter | const | mm | 1600 | sim |
| `SFH` | Frame Height | StructFrameHeight | const | mm | 100 | sim |
| `SVPC` | Vertical Pipe Clearance | StructVertPipeClearance | const | mm | 300 | sim |
| `WTh` | Wall Thickness | WallThickness | const | mm | 100 | sim |
| `LenB1` | Cylinder Body Height | Geometry_Distance3D | calc | mm | `$SBSH` | sim |

_+ 1 coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._

