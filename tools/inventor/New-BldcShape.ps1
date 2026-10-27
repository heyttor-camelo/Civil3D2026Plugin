<#
.SYNOPSIS
  Modela no Inventor a boca de lobo dupla BLDC (CSBIM) como peça paramétrica (.ipt), base
  para publicar no Infrastructure Parts Editor (IPE) com ponto de conexão próprio.

.DESCRIPTION
  A BLDC do catálogo FDX foi feita no Part Builder, onde o tubo sempre mira o ponto de
  inserção. No IPE a estrutura tem pontos de conexão separados, mas a forma tem que vir do
  Inventor. Este script cria essa forma pela API do Inventor (COM), com os mesmos
  parâmetros da família original (CSBIM-DRENAGEM\BLDC.xml):

    SIL  comprimento interno (2000)      WTh  parede (100)
    SIW  largura interna (600)           FTh  fundo (100)
    SRS  tampa ao fundo interno (1000)

  O conversor do IPE para o Civil 3D exige que a forma subterrânea tenha os parâmetros com
  os nomes padrão da biblioteca (senão o Publish falha com "does not have property"):

    USLength = SIL + 2 WTh   comprimento externo    WallTh = WTh
    USWidth  = SIW + 2 WTh   largura externa        BaseTh = FTh
    USHeight = SRS + FTh     altura total

  Esses cinco são os parâmetros-chave (IsKey) e aparecem na tabela de tamanhos do IPE; os
  do CSBIM derivam deles por expressão, para a geometria continuar escrita como no Part Builder.

  e alguns da geometria fixa do Part Builder, agora editáveis:

    Tampa_E  espessura da laje do topo (100)
    Base_Aba aba da laje de fundo além da parede (100; no Part Builder SIL+400 com WTh 100)
    Ab_H     altura da abertura na guia (150)
    Ab_L     comprimento de cada abertura (900)
    Ab_Vao   meio vão entre as duas aberturas, a partir do centro (50)

  Origem (0,0,0) = PONTO DE INSERÇÃO: meio da face da guia (face frontal externa), no topo.
    X ao longo da guia, Y para dentro da caixa, Z para cima.
  Fundo interno (sump) em Z = -SRS. Laje de fundo de Z = -SRS a Z = -(SRS + FTh).
  As duas aberturas ficam na parede frontal (Y = 0 a WTh), logo abaixo da laje do topo.

  Pontos de trabalho criados para o Shape Utilities (pontos de conexão dos tubos):
    Conexao_Centro      centro da caixa na cota do fundo
    Conexao_Fundo       meio da parede do fundo (Y = SIW + 2 WTh)
    Conexao_Esquerda    meio da parede lateral X-
    Conexao_Direita     meio da parede lateral X+

  Só cria um documento novo no Inventor; não mexe em catálogo nenhum.
  Rodar no Windows PowerShell 5.1 (usa GetActiveObject), com o Inventor instalado.

.PARAMETER Save
  Caminho do .ipt para salvar. Sem ele, a peça fica aberta sem salvar.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\inventor\New-BldcShape.ps1 -Save C:\Temp\BLDC.ipt
#>
[CmdletBinding()]
param([string]$Save)

$ErrorActionPreference = 'Stop'

# Enums da API do Inventor
$kPartDocumentObject   = 12290
$kMetricSystemOfMeasure = 8195
$kJoinOperation        = 20481
$kCutOperation         = 20482
$kNegativeExtent       = 20994
$kSymmetricExtent      = 20995
$kHorizontalDim        = 19201
$kVerticalDim          = 19202

function Get-Inventor {
    try { return [Runtime.InteropServices.Marshal]::GetActiveObject('Inventor.Application') }
    catch {
        $app = New-Object -ComObject Inventor.Application
        $app.Visible = $true
        return $app
    }
}

$inv = Get-Inventor
$tg = $inv.TransientGeometry
$template = $inv.FileManager.GetTemplateFile($kPartDocumentObject, $kMetricSystemOfMeasure)
$doc = $inv.Documents.Add($kPartDocumentObject, $template, $true)
$def = $doc.ComponentDefinition
# o template "métrico" da instalação em inglês vem em polegadas; o Export Template do Shape
# Utilities grava a unidade do documento (LengthUnit) no XML
$doc.UnitsOfMeasure.LengthUnits = 11269   # kMillimeterLengthUnits

# --- parâmetros (mm) ---
$userParams = $def.Parameters.UserParameters
# valores do CSBIM (usados abaixo para posicionar os esboços)
$params = [ordered]@{
    SIL = 2000; SIW = 600; WTh = 100; FTh = 100; SRS = 1000
    Tampa_E = 100; Base_Aba = 100; Ab_H = 150; Ab_L = 900; Ab_Vao = 50
}
# parâmetros-chave com os nomes que o IPE/Civil 3D exigem
$keyParams = [ordered]@{
    USLength = $params.SIL + 2 * $params.WTh
    USWidth  = $params.SIW + 2 * $params.WTh
    USHeight = $params.SRS + $params.FTh
    WallTh   = $params.WTh
    BaseTh   = $params.FTh
    Tampa_E = 100; Base_Aba = 100; Ab_H = 150; Ab_L = 900; Ab_Vao = 50
}
foreach ($name in $keyParams.Keys) {
    $p = $userParams.AddByExpression($name, "$($keyParams[$name]) mm", 'mm')
    try { $p.IsKey = $true } catch { }
}
# parâmetros do CSBIM derivados (não-chave)
$derived = [ordered]@{
    WTh = 'WallTh'; FTh = 'BaseTh'
    SIL = 'USLength - 2 * WallTh'; SIW = 'USWidth - 2 * WallTh'; SRS = 'USHeight - BaseTh'
}
foreach ($name in $derived.Keys) { $null = $userParams.AddByExpression($name, $derived[$name], 'mm') }

# API trabalha em cm
function cm([double]$mm) { $mm / 10.0 }

$xyPlane = $def.WorkPlanes.Item(3)
$xzPlane = $def.WorkPlanes.Item(2)
$originWp = $def.WorkPoints.Item(1)

# Retângulo amarrado à origem projetada. Cada canto é dado em coordenadas do modelo (mm) e
# cada coordenada leva a expressão do seu valor absoluto; $null = coordenada zero (aresta
# passa pela origem: restrição de coincidência em vez de cota).
function Add-Rect($sketch, [double[]]$a, [double[]]$b, $exprA, $exprB) {
    $origin = $sketch.AddByProjectingEntity($originWp)
    $pa = $sketch.ModelToSketchSpace($tg.CreatePoint((cm $a[0]), (cm $a[1]), (cm $a[2])))
    $pb = $sketch.ModelToSketchSpace($tg.CreatePoint((cm $b[0]), (cm $b[1]), (cm $b[2])))
    $lines = $sketch.SketchLines.AddAsTwoPointRectangle($pa, $pb)

    $pts = @()
    foreach ($l in $lines) { $pts += $l.StartSketchPoint }
    function Nearest($target) {
        $best = $null; $bd = [double]::MaxValue
        foreach ($sp in $pts) {
            $d = $sp.Geometry.DistanceTo($target)
            if ($d -lt $bd) { $bd = $d; $best = $sp }
        }
        $best
    }
    $ca = Nearest $pa
    $cb = Nearest $pb

    # exprA/exprB = @(exprX, exprY) no espaço do esboço
    $dims = $sketch.DimensionConstraints
    $gc = $sketch.GeometricConstraints
    foreach ($pair in @(@($ca, $exprA, $pa), @($cb, $exprB, $pb))) {
        $corner, $expr, $geo = $pair
        for ($i = 0; $i -lt 2; $i++) {
            $e = $expr[$i]
            $orient = if ($i -eq 0) { $kHorizontalDim } else { $kVerticalDim }
            if ($null -eq $e) {
                # aresta sobre o eixo: acha a linha do retângulo por esse canto, paralela ao outro eixo
                foreach ($l in $lines) {
                    $s = $l.StartSketchPoint.Geometry; $f = $l.EndSketchPoint.Geometry
                    $onAxis = if ($i -eq 0) { [math]::Abs($s.X - $f.X) -lt 1e-6 } else { [math]::Abs($s.Y - $f.Y) -lt 1e-6 }
                    if ($onAxis -and ($l.StartSketchPoint -eq $corner -or $l.EndSketchPoint -eq $corner)) {
                        # o Inventor pode já ter inferido a coincidência ao criar o retângulo: aí dá E_FAIL
                        try { $null = $gc.AddCoincident($l, $origin) } catch { }
                        break
                    }
                }
                continue
            }
            $tp = $tg.CreatePoint2d(($geo.X + $origin.Geometry.X) / 2 + 1, ($geo.Y + $origin.Geometry.Y) / 2 + 1)
            $dim = $dims.AddTwoPointDistance($origin, $corner, $orient, $tp)
            $dim.Parameter.Expression = $e
        }
    }
    # vírgula: o Profile é uma coleção e o PowerShell o desmontaria em ProfilePaths
    return , $sketch.Profiles.AddForSolid()
}

function Extrude($profile, $op, $extentExpr, $dir) {
    $ed = $def.Features.ExtrudeFeatures.CreateExtrudeDefinition($profile, $op)
    $ed.SetDistanceExtent($extentExpr, $dir)
    $def.Features.ExtrudeFeatures.Add($ed)
}

$p = $params
$Lo = $p.SIL + 2 * $p.WTh
$Wo = $p.SIW + 2 * $p.WTh

# 1. Corpo externo: topo em Z = 0, desce SRS
$sk = $def.Sketches.Add($xyPlane); $sk.Name = 'Esboco_Corpo'
$prof = Add-Rect $sk @((-$Lo / 2), 0, 0) @(($Lo / 2), $Wo, 0) @('(SIL + 2 * WTh) / 2', $null) @('(SIL + 2 * WTh) / 2', 'SIW + 2 * WTh')
$f = Extrude $prof $kJoinOperation 'SRS' $kNegativeExtent; $f.Name = 'Corpo'

# 2. Laje de fundo com aba
$wpBase = $def.WorkPlanes.AddByPlaneAndOffset($xyPlane, '-SRS'); $wpBase.Name = 'Plano_Fundo'; $wpBase.Visible = $false
$sk = $def.Sketches.Add($wpBase); $sk.Name = 'Esboco_Laje_Fundo'
$a = $p.Base_Aba
$prof = Add-Rect $sk @((-$Lo / 2 - $a), (-$a), (-$p.SRS)) @(($Lo / 2 + $a), ($Wo + $a), (-$p.SRS)) `
    @('(SIL + 2 * WTh) / 2 + Base_Aba', 'Base_Aba') @('(SIL + 2 * WTh) / 2 + Base_Aba', 'SIW + 2 * WTh + Base_Aba')
$f = Extrude $prof $kJoinOperation 'FTh' $kNegativeExtent; $f.Name = 'Laje_Fundo'

# 3. Vazio interno: da laje do topo até o fundo interno
$wpTop = $def.WorkPlanes.AddByPlaneAndOffset($xyPlane, '-Tampa_E'); $wpTop.Name = 'Plano_Tampa'; $wpTop.Visible = $false
$sk = $def.Sketches.Add($wpTop); $sk.Name = 'Esboco_Vazio'
$prof = Add-Rect $sk @((-$p.SIL / 2), $p.WTh, (-$p.Tampa_E)) @(($p.SIL / 2), ($p.WTh + $p.SIW), (-$p.Tampa_E)) `
    @('SIL / 2', 'WTh') @('SIL / 2', 'WTh + SIW')
$f = Extrude $prof $kCutOperation 'SRS - Tampa_E' $kNegativeExtent; $f.Name = 'Vazio'

# 4. Aberturas na guia (parede frontal), corte simétrico atravessando a parede
$x1 = $p.Ab_Vao; $x2 = $p.Ab_Vao + $p.Ab_L
$z1 = -$p.Tampa_E; $z2 = -$p.Tampa_E - $p.Ab_H
foreach ($side in @(1, -1)) {
    $sk = $def.Sketches.Add($xzPlane); $nome = if ($side -gt 0) { 'Abertura_Direita' } else { 'Abertura_Esquerda' }; $sk.Name = 'Esboco_' + $nome
    # sentido dos eixos do esboço no plano XZ depende do Inventor: as expressões são valores absolutos
    $prof = Add-Rect $sk @(($side * $x1), 0, $z1) @(($side * $x2), 0, $z2) @('Ab_Vao', 'Tampa_E') @('Ab_Vao + Ab_L', 'Tampa_E + Ab_H')
    $f = Extrude $prof $kCutOperation '2 * WTh' $kSymmetricExtent; $f.Name = $nome
}

# 5. Pontos de trabalho para os pontos de conexão, na interseção de planos de deslocamento
# (seguem os parâmetros). O sentido da normal dos planos de origem depende do Inventor: se o
# plano cair do lado errado, a expressão é negada.
$yzPlane = $def.WorkPlanes.Item(1)
function Add-OffsetPlane($name, $base, [string]$expr, [string]$axis, [double]$mm) {
    $wp = $def.WorkPlanes.AddByPlaneAndOffset($base, $expr)
    $rp = $wp.Plane.RootPoint
    $v = switch ($axis) { 'X' { $rp.X } 'Y' { $rp.Y } 'Z' { $rp.Z } }
    if ([math]::Abs($v - (cm $mm)) -gt 1e-6) { $wp.Definition.Offset.Expression = "-($expr)" }
    $wp.Name = $name; $wp.Visible = $false
    $wp
}
$wpMeioY  = Add-OffsetPlane 'Plano_Meio_Y'  $xzPlane '(SIW + 2 * WTh) / 2' 'Y' ($Wo / 2)
$wpFundoY = Add-OffsetPlane 'Plano_Fundo_Y' $xzPlane 'SIW + 2 * WTh' 'Y' $Wo
$wpEsq    = Add-OffsetPlane 'Plano_Esq'     $yzPlane '-(SIL + 2 * WTh) / 2' 'X' (-$Lo / 2)
$wpDir    = Add-OffsetPlane 'Plano_Dir'     $yzPlane '(SIL + 2 * WTh) / 2' 'X' ($Lo / 2)
function Add-Wp($name, $p1, $p2, $p3) {
    $wp = $def.WorkPoints.AddByThreePlanes($p1, $p2, $p3)
    $wp.Name = $name
}
Add-Wp 'Conexao_Centro'   $yzPlane $wpMeioY  $wpBase
Add-Wp 'Conexao_Fundo'    $yzPlane $wpFundoY $wpBase
Add-Wp 'Conexao_Esquerda' $wpEsq   $wpMeioY  $wpBase
Add-Wp 'Conexao_Direita'  $wpDir   $wpMeioY  $wpBase

# Conexão na face da guia: centro da face, a meia altura
$wpMeioZ = Add-OffsetPlane 'Plano_Meio_Z' $xyPlane '-SRS / 2' 'Z' (-$p.SRS / 2)
Add-Wp 'Conexao_Frente' $yzPlane $xzPlane $wpMeioZ

# Ponto de inserção para o Assembly Connector do Shape Utilities: na face da guia, no meio
# do vão entre as aberturas, na soleira (fundo das aberturas)
$wpSoleira = Add-OffsetPlane 'Plano_Soleira' $xyPlane '-(Tampa_E + Ab_H)' 'Z' (-($p.Tampa_E + $p.Ab_H))
Add-Wp 'Ponto_Insercao' $yzPlane $xzPlane $wpSoleira

$doc.Update()
if ($Save) {
    $doc.SaveAs($Save, $false)
    Write-Host "Salvo em $Save"
}
Write-Host 'BLDC modelada. Origem = ponto de inserção (meio da face da guia, no topo).'
