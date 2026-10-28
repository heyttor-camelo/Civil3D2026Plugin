# Gera as tabelas editaveis das pecas _IPE: toda medida de projeto em faixa (como nas Part Builder),
# com descricao em portugues; o resto sai calculado. Reusa o PrtID da peca ja instalada.
# Os parametros internos da caixa (UndergroundStructure_*) chegam ao .ipt pelo Add-ParametrosRecipe.ps1.
param([string]$Saida='C:\Temp\BLDC-IPE\tabelas')
$ErrorActionPreference='Stop'
$s=Join-Path $PSScriptRoot 'New-TabelaIPE.ps1'
$g='C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog\Generic Drainage with Pipe Connections - Metric'
$inst='C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog\FDX Metric Structures\CSBIM-DRENAGEM'
New-Item -ItemType Directory -Force $Saida | Out-Null
function Pid($n){ if(Test-Path "$inst\$n.xml"){ ([xml](Get-Content "$inst\$n.xml" -Raw)).LandPart.ColumnConst | ? name -eq 'PrtID' | % InnerText } else { [guid]::NewGuid().ToString() } }
$geo='Geometry_ModelDistanceParameter'

# ---- Bocas de lobo (recipe ccf8165e): caixa + moldura de marcador na face da guia
$fixo=@{ SurfaceStructure_CGHeight=50; SurfaceStructure_FlangeTh=25; GrateOrCover_CGHeight=50; SurfaceStructure_CGWidth=50; GrateOrCover_CGWidth=50 }
function FaixasBL($silMin,$silMax,$sil,$srs,$abL){
    @{ SIL=@('StructInnerLength',$silMin,$silMax,$sil,'Comprimento interno')
       SIW=@('StructInnerWidth',300,1200,600,'Largura interna')
       WTh=@('WallThickness',50,220,100,'Espessura da parede')
       FTh=@('FloorThickness',50,220,100,'Espessura do fundo')
       SRS=@('StructRimToSumpHeight',$srs,100000,$srs,'Profundidade (topo ao fundo)')
       SFH=@('StructFrameHeight',0,300,50,'Altura do marcador (moldura)')
       AbL=@($geo,200,3000,$abL,'Comprimento da abertura')
       AbH=@($geo,50,400,150,'Altura da abertura')
       Aba=@($geo,0,300,100,'Aba da laje de fundo')
       TampaE=@($geo,50,300,100,'Espessura da laje de topo') } }
function CalcBL($nome){
    @{ SBSL='$SIL + 2 * $WTh'; SBSW='$SIW + 2 * $WTh'; SurfaceStructure_WallTh='$WTh'; SFL='$SBSL - 200'; SFW='2 * $WTh + 50'
       SurfaceStructure_SPLength='$SBSL'; SurfaceStructure_SPWidth='$SFW + 50'; SurfaceStructure_CGLength='$SFL - 2 * $WTh'; GrateOrCover_CGLength='$SurfaceStructure_CGLength'
       UndergroundStructure_Ab_L='$AbL'; UndergroundStructure_Ab_H='$AbH'; UndergroundStructure_Base_Aba='$Aba'; UndergroundStructure_Tampa_E='$TampaE'
       PrtSN=('"' + $nome + ' " + FormatNumber($SIL,0) + " x " + FormatNumber($SIW,0) + " mm (interno)"') } }
$bl="$g\Inlet\ab3ca14c-3aa5-4f3e-aec0-91ccfebb93ce.xml"
$f=FaixasBL 1000 4000 2000 1100 900; $f.AbVao=@($geo,0,1000,50,'Afastamento das aberturas ao centro'); $c=CalcBL 'BLDC'; $c.UndergroundStructure_Ab_Vao='$AbVao'
& $s -Base $bl -Saida "$Saida\BLDC_IPE.xml" -Nome 'BLDC_IPE' -Recipe 'fcdbd57a-c7c8-4459-86a8-75b3d439f67d' -Valores $fixo -Calc $c -Faixas $f -PartId (Pid 'BLDC_IPE')
& $s -Base $bl -Saida "$Saida\BLSC_IPE.xml" -Nome 'BLSC_IPE' -Recipe 'e034d699-3aec-46d6-9475-15afe8c9bbb7' -Valores $fixo -Calc (CalcBL 'BLSC') -Faixas (FaixasBL 500 2000 1000 1000 800) -PartId (Pid 'BLSC_IPE')

# ---- PV DNIT (recipe ccaec438): caixa + chamine em cone + tampao
$p=@{ SurfaceStructure_CGHeight=50; SurfaceStructure_FlangeTh=25; GrateOrCover_CGHeight=50 }
& $s -Base "$g\Access\3521d641-c5e9-4678-aef7-228b0bceaaf2.xml" -Saida "$Saida\PV_DNIT_IPE.xml" -Nome 'PV_DNIT_IPE' -Recipe 'fe685653-c694-456e-ae8c-3d9a20d98f82' -Valores $p -PartId (Pid 'PV_DNIT_IPE') `
    -Faixas @{ SIL=@('StructInnerLength',500,6000,2400,'Comprimento interno'); SIW=@('StructInnerWidth',500,6000,2400,'Largura interna')
               WTh=@('WallThickness',100,400,200,'Espessura da parede'); FTh=@('FloorThickness',100,400,200,'Espessura do fundo')
               SRS=@('StructRimToSumpHeight',1400,100000,1400,'Profundidade (topo ao fundo)'); AC=@('StructConeHeight',200,1800,800,'Altura da chamine')
               DC=@($geo,600,1000,800,'Diametro do tampao'); CB=@($geo,0,1200,600,'Acrescimo do diametro na base do cone')
               SFH=@('StructFrameHeight',50,300,100,'Altura do tampao (moldura)') } `
    -Calc @{ SBSL='$SIL + 2 * $WTh'; SBSW='$SIW + 2 * $WTh'; UndergroundStructure_USBaseHeight='$SBSH - $SFH - $AC'
             SurfaceStructure_WallTh='$WTh'; SurfaceStructure_CGDiameter='$DC'; GrateOrCover_CGDiameter='$DC'; SFD='$DC + 2 * $WTh'
             SurfaceStructure_SPDiameter='$SFD'; UndergroundStructure_SPDiameter='$SFD'; UndergroundStructure_ConeBaseD='$SFD + $CB'
             PrtSN='"PV DNIT " + FormatNumber($SIL,0) + " x " + FormatNumber($SIW,0) + " mm (interno)"' }
