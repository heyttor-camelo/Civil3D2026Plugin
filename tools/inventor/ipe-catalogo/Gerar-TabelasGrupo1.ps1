# Tabelas por linha (tamanhos fixos) das bocas de lobo do grupo 1. Pecas de receita nao aceitam faixa
# (docs/CATALOGO-PONTOS-CONEXAO.md), entao cada combinacao util vira uma linha:
#   SIL de 100 em 100 dentro da faixa da familia, SIW nas larguras comuns, o resto derivado por linha.
# Medidas de projeto que o plugin deve enxergar (AbL, AbH, TampaE, Aba, grelhas) vao como coluna com
# contexto de estrutura (contexto Geometry_* nao aparece no PartData das pecas de receita).
param([string]$Saida='C:\Temp\BLDC-IPE\tabelas-grupo1',[string]$Ids='C:\Temp\BLDC-IPE\recipes\ids-grupo1.txt')
$ErrorActionPreference='Stop'
$s=Join-Path $PSScriptRoot 'New-TabelaIPE.ps1'
$base='C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog\Generic Drainage with Pipe Connections - Metric\Inlet\ab3ca14c-3aa5-4f3e-aec0-91ccfebb93ce.xml'
$inst='C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog\FDX Metric Structures\CSBIM-DRENAGEM'
New-Item -ItemType Directory -Force $Saida | Out-Null
function Pid($n){ if(Test-Path "$inst\$n.xml"){ ([xml](Get-Content "$inst\$n.xml" -Raw)).LandPart.ColumnConst | ? name -eq 'PrtID' | % InnerText } else { [guid]::NewGuid().ToString() } }
$rid=@{ BLDC='fcdbd57a-c7c8-4459-86a8-75b3d439f67d'; BLSC='e034d699-3aec-46d6-9475-15afe8c9bbb7'; BLTC='4af44777-0819-4424-a55c-5d75b35cae4b' }
if(Test-Path $Ids){ Get-Content $Ids | % { $k,$v=$_ -split '='; if($v){ $rid[$k]=$v } } }

$ctx=@{ SIL=@('StructInnerLength','Comprimento interno'); SIW=@('StructInnerWidth','Largura interna'); WTh=@('WallThickness','Espessura da parede')
        AbL=@('StructAberturaComprimento','Comprimento da abertura'); AbH=@('StructAberturaAltura','Altura da abertura'); AbVao=@('StructAberturaVao','Afastamento das aberturas ao centro')
        AbGap=@('StructAberturaFolga','Folga entre aberturas'); TampaE=@('StructSlabThickness','Espessura da laje de topo'); Aba=@('StructAbaFundo','Aba da laje de fundo')
        GrL=@('StructGrelhaComprimento','Comprimento do vao da grelha'); GrW=@('StructGrelhaLargura','Largura do vao da grelha'); GrGap=@('StructGrelhaFolga','Folga entre grelhas')
        GrY=@('StructGrelhaPosicao','Centro das grelhas a partir da face da pista'); Frente=@('StructFrentePista','Distancia da face da pista ate a guia') }
$faixas=@{ FTh=@('FloorThickness',50,250,100,'Espessura do fundo'); SRS=@('StructRimToSumpHeight',1000,100000,1100,'Profundidade (topo ao fundo)'); SFH=@('StructFrameHeight',0,300,50,'Altura do marcador (moldura)') }
$fixo=@{ SurfaceStructure_CGHeight=50; SurfaceStructure_FlangeTh=25; GrateOrCover_CGHeight=50; SurfaceStructure_CGWidth=50; GrateOrCover_CGWidth=50 }
$calcBase=@{ SBSL='$SIL + 2 * $WTh'; SBSW='$SIW + 2 * $WTh'; SurfaceStructure_WallTh='$WTh'; SFL='$SBSL - 200'; SFW='2 * $WTh + 50'
    SurfaceStructure_SPLength='$SBSL'; SurfaceStructure_SPWidth='$SFW + 50'; SurfaceStructure_CGLength='$SFL - 2 * $WTh'; GrateOrCover_CGLength='$SurfaceStructure_CGLength'
    UndergroundStructure_Tampa_E='$TampaE'; UndergroundStructure_Base_Aba='$Aba' }

function Linhas($silMin,$silMax,$silPadrao,[double[]]$siws,[scriptblock]$extra){
    $sils=@(); for($v=[math]::Ceiling($silMin/100)*100; $v -le $silMax; $v+=100){ $sils+=$v }; if($sils -notcontains $silPadrao){ $sils+=$silPadrao }
    $out=@(); foreach($w in $siws){ foreach($l in ($sils | Sort)){ $r=@{ SIL=$l; SIW=$w }; & $extra $r; $out+=$r } }; $out }

function Gera($nome,$linhas,$calcExtra){
    $c=$calcBase.Clone(); foreach($k in $calcExtra.Keys){ $c[$k]=$calcExtra[$k] }
    $c.PrtSN='"' + $nome + ' " + FormatNumber($SIL,0) + " x " + FormatNumber($SIW,0) + " mm (interno)"'
    $lc=@{}; foreach($k in ($linhas[0].Keys)){ if($ctx.ContainsKey($k)){ $lc[$k]=$ctx[$k] } }
    & $s -Base $base -Saida "$Saida\$($nome)_IPE.xml" -Nome "$($nome)_IPE" -Recipe $rid[$nome] -Valores $fixo -Faixas $faixas -Calc $c -Linhas $linhas -LinhasCtx $lc -PartId (Pid "$($nome)_IPE") | Out-Null
    "$($nome)_IPE: $($linhas.Count) linhas, recipe $($rid[$nome])"
}

# C (boca na guia): aberturas 150 de altura; AbL acompanha SIL
$ab={ param($r) $r.WTh=100; $r.AbH=150; $r.TampaE=100; $r.Aba=100 }
Gera 'BLDC' (Linhas 1000 4000 2000 @(600,800,1000) { param($r) & $ab $r; $r.AbVao=50; $r.AbL=$r.SIL/2-50 }) @{ UndergroundStructure_Ab_L='$AbL'; UndergroundStructure_Ab_H='$AbH'; UndergroundStructure_Ab_Vao='$AbVao' }
Gera 'BLSC' (Linhas 500 2000 1000 @(600,800,1000) { param($r) & $ab $r; $r.AbL=$r.SIL-50 }) @{ UndergroundStructure_Ab_L='$AbL'; UndergroundStructure_Ab_H='$AbH' }
Gera 'BLTC' (Linhas 1600 6400 3200 @(600,800,1000) { param($r) & $ab $r; $r.AbGap=175; $r.AbL=[math]::Round(($r.SIL-350)/3) }) @{ UndergroundStructure_Ab_L='$AbL'; UndergroundStructure_Ab_H='$AbH'; UndergroundStructure_Ab_Gap='$AbGap' }
# G (grelha, caixa do lado da pista) e M (mista): grelhas fixas, SIL a partir do que cabe
$gr={ param($r,[double]$wth,[double]$gl,[double]$gw,[double]$gg,[double]$fr,[double]$gy) $r.WTh=$wth; $r.TampaE=100; $r.Aba=100; $r.GrL=$gl; $r.GrW=$gw; $r.GrGap=$gg; $r.Frente= if($fr -lt 0){ $r.SIW+2*$wth } else { $fr }; $r.GrY= if($gy -lt 0){ ($r.SIW+2*$wth)/2 } else { $gy } }
$cg=@{ UndergroundStructure_Gr_L='$GrL'; UndergroundStructure_Gr_W='$GrW'; UndergroundStructure_Gr_Gap='$GrGap'; UndergroundStructure_Gr_Y='$GrY'; UndergroundStructure_Frente='$Frente' }
Gera 'BLDG' (Linhas 2000 4700 2140 @(520,720,920) { param($r) & $gr $r 200 1050 620 150 (-1) (-1) }) $cg
Gera 'BLSG' (Linhas 800 2300 950 @(520,720,920) { param($r) & $gr $r 200 1050 620 150 (-1) (-1) }) $cg
Gera 'BLTG' (Linhas 3200 7200 3410 @(520,720,920) { param($r) & $gr $r 200 1050 620 180 (-1) (-1) }) $cg
Gera 'BLDM' (Linhas 2000 4000 2000 @(1000) { param($r) & $gr $r 100 1000 335 50 400 232.5 }) $cg
Gera 'BLSM' (Linhas 1000 2000 1000 @(1000) { param($r) & $gr $r 100 1040 315 50 400 242.5 }) $cg
