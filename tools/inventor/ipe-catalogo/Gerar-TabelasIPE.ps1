# Gera as tabelas editaveis (SIL/SIW/WTh/FTh/SRS em faixa, como nas Part Builder) das pecas _IPE.
# Reusa o PrtID da peca ja instalada, se houver, para nao quebrar desenhos que ja a usam.
param([string]$Saida='C:\Temp\BLDC-IPE\tabelas')
$ErrorActionPreference='Stop'
$s=Join-Path $PSScriptRoot 'New-TabelaIPE.ps1'
$g='C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog\Generic Drainage with Pipe Connections - Metric'
$inst='C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog\FDX Metric Structures\CSBIM-DRENAGEM'
New-Item -ItemType Directory -Force $Saida | Out-Null
function Pid($n){ if(Test-Path "$inst\$n.xml"){ ([xml](Get-Content "$inst\$n.xml" -Raw)).LandPart.ColumnConst | ? name -eq 'PrtID' | % InnerText } else { [guid]::NewGuid().ToString() } }

# Bocas de lobo (recipe ccf8165e): moldura de marcador na face da guia, larguras amarradas a WTh
$fixo=@{ SurfaceStructure_CGHeight=50; SurfaceStructure_FlangeTh=25; GrateOrCover_CGHeight=50; SFH=50; SurfaceStructure_CGWidth=50; GrateOrCover_CGWidth=50 }
function CalcBL($nome){ @{ SBSL='$SIL + 2 * $WTh'; SBSW='$SIW + 2 * $WTh'; SurfaceStructure_WallTh='$WTh'; SFL='$SBSL - 200'; SFW='2 * $WTh + 50'
    SurfaceStructure_SPLength='$SBSL'; SurfaceStructure_SPWidth='$SFW + 50'; SurfaceStructure_CGLength='$SFL - 2 * $WTh'; GrateOrCover_CGLength='$SurfaceStructure_CGLength'
    PrtSN=('"' + $nome + ' " + FormatNumber($SIL,0) + " x " + FormatNumber($SIW,0) + " mm (interno)"') } }
function FaixasBL($silMin,$silMax,$sil,$srs){ @{ SIL=@('StructInnerLength',$silMin,$silMax,$sil); SIW=@('StructInnerWidth',300,1200,600)
    WTh=@('WallThickness',50,220,100); FTh=@('FloorThickness',50,220,100); SRS=@('StructRimToSumpHeight',$srs,100000,$srs) } }
$bl="$g\Inlet\ab3ca14c-3aa5-4f3e-aec0-91ccfebb93ce.xml"
& $s -Base $bl -Saida "$Saida\BLDC_IPE.xml" -Nome 'BLDC_IPE' -Recipe 'fcdbd57a-c7c8-4459-86a8-75b3d439f67d' -Valores $fixo -Calc (CalcBL 'BLDC') -Faixas (FaixasBL 1000 4000 2000 1100) -PartId (Pid 'BLDC_IPE')
& $s -Base $bl -Saida "$Saida\BLSC_IPE.xml" -Nome 'BLSC_IPE' -Recipe 'e034d699-3aec-46d6-9475-15afe8c9bbb7' -Valores $fixo -Calc (CalcBL 'BLSC') -Faixas (FaixasBL 500 2000 1000 1000) -PartId (Pid 'BLSC_IPE')

# PV DNIT (recipe ccaec438): chamine de altura AC, paredes 200 fixas como na Part Builder
$p=@{ SFD=1200; SFH=100; SurfaceStructure_WallTh=200; SurfaceStructure_CGDiameter=800; SurfaceStructure_CGHeight=50; SurfaceStructure_SPDiameter=1200
      SurfaceStructure_FlangeTh=25; FTh=200; WTh=200; UndergroundStructure_SPDiameter=1200; GrateOrCover_CGHeight=50; GrateOrCover_CGDiameter=800 }
& $s -Base "$g\Access\3521d641-c5e9-4678-aef7-228b0bceaaf2.xml" -Saida "$Saida\PV_DNIT_IPE.xml" -Nome 'PV_DNIT_IPE' -Recipe 'fe685653-c694-456e-ae8c-3d9a20d98f82' -Valores $p -PartId (Pid 'PV_DNIT_IPE') `
    -Faixas @{ SIL=@('StructInnerLength',500,6000,2400); SIW=@('StructInnerWidth',500,6000,2400); AC=@('StructConeHeight',200,1800,800); SRS=@('StructRimToSumpHeight',1400,100000,1400) } `
    -Calc @{ SBSL='$SIL + 2 * $WTh'; SBSW='$SIW + 2 * $WTh'; UndergroundStructure_USBaseHeight='$SBSH - $SFH - $AC'; PrtSN='"PV DNIT " + FormatNumber($SIL,0) + " x " + FormatNumber($SIW,0) + " mm (interno)"' }
