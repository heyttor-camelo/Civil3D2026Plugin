param([string]$Path)
$ErrorActionPreference = 'Stop'
$kJoin = 20481; $kCut = 20482; $kPos = 20993; $kNeg = 20994
$kHorizontalDim = 19201; $kVerticalDim = 19202

$inv = [Runtime.InteropServices.Marshal]::GetActiveObject('Inventor.Application')
$doc = $null
foreach ($d in $inv.Documents) { if ($d.FullFileName -eq $Path) { $doc = $d } }
if (-not $doc) { $doc = $inv.Documents.Open($Path, $true) }
$def = $doc.ComponentDefinition
$tg = $inv.TransientGeometry
$up = $def.Parameters.UserParameters
$em = $inv.ErrorManager

foreach ($kv in @(@('Tampa_E', '100 mm'), @('Base_Aba', '100 mm'), @('Ab_H', '150 mm'), @('Ab_L', '900 mm'), @('Ab_Vao', '50 mm'))) {
    $null = $up.AddByExpression($kv[0], $kv[1], 'mm')
}
function cm([double]$mm) { $mm / 10.0 }
$P = @{}; foreach ($n in 'USLength', 'USWidth', 'USHeight', 'WallTh', 'BaseTh', 'Tampa_E', 'Base_Aba', 'Ab_H', 'Ab_L', 'Ab_Vao') { $P[$n] = $def.Parameters.Item($n).Value * 10 }

$xy = $def.WorkPlanes.Item(3); $xz = $def.WorkPlanes.Item(2); $origin = $def.WorkPoints.Item(1)

# retângulo cotado contra a origem projetada; cantos em mm do modelo, expressões com valor absoluto
function Add-Rect($sk, [double[]]$a, [double[]]$b, $exprA, $exprB) {
    $o = $sk.AddByProjectingEntity($origin)
    $pa = $sk.ModelToSketchSpace($tg.CreatePoint((cm $a[0]), (cm $a[1]), (cm $a[2])))
    $pb = $sk.ModelToSketchSpace($tg.CreatePoint((cm $b[0]), (cm $b[1]), (cm $b[2])))
    $lines = @($sk.SketchLines.AddAsTwoPointRectangle($pa, $pb))
    $pts = @($lines | % { $_.StartSketchPoint })
    function Nearest($t) { $best = $null; $bd = 1e99; foreach ($s in $pts) { $dd = $s.Geometry.DistanceTo($t); if ($dd -lt $bd) { $bd = $dd; $best = $s } }; $best }
    foreach ($pair in @(@((Nearest $pa), $exprA, $pa), @((Nearest $pb), $exprB, $pb))) {
        $corner, $expr, $geo = $pair
        for ($i = 0; $i -lt 2; $i++) {
            $orient = if ($i -eq 0) { $kHorizontalDim } else { $kVerticalDim }
            $tp = $tg.CreatePoint2d(($geo.X + $o.Geometry.X) / 2 + 1, ($geo.Y + $o.Geometry.Y) / 2 + 1)
            $dim = $sk.DimensionConstraints.AddTwoPointDistance($o, $corner, $orient, $tp)
            $dim.Parameter.Expression = $expr[$i]
        }
    }
    return , $sk.Profiles.AddForSolid()
}

# 1. Aba da laje de fundo: plano no fundo (Z = -USHeight), extrudado BaseTh para cima
$wpF = $def.WorkPlanes.AddByPlaneAndOffset($xy, '-USHeight')
if ([math]::Abs($wpF.Plane.RootPoint.Z * 10 + $P.USHeight) -gt 0.01) { $wpF.Definition.Offset.Expression = 'USHeight' }
$wpF.Name = 'BLDC_Plano_Fundo'; $wpF.Visible = $false
$sk = $def.Sketches.Add($wpF); $sk.Name = 'BLDC_Esboco_Aba'
$L2 = $P.USLength / 2 + $P.Base_Aba; $W2 = $P.USWidth / 2 + $P.Base_Aba
$prof = Add-Rect $sk @(-$L2, -$W2, -$P.USHeight) @($L2, $W2, -$P.USHeight) @('USLength / 2 + Base_Aba', 'USWidth / 2 + Base_Aba') @('USLength / 2 + Base_Aba', 'USWidth / 2 + Base_Aba')
$ed = $def.Features.ExtrudeFeatures.CreateExtrudeDefinition($prof, $kJoin); $ed.SetDistanceExtent('BaseTh', $kPos)
$f = $def.Features.ExtrudeFeatures.Add($ed); $f.Name = 'BLDC_Aba'
$r = $def.SurfaceBodies.Item(1).RangeBox
if ($r.MinPoint.Z * 10 -lt -$P.USHeight - 1) { $f.Definition.SetDistanceExtent('BaseTh', $kNeg) }   # sentido errado: inverte

# 2. Aberturas na parede da frente (Y negativo), corte a partir do plano XZ
$x1 = $P.Ab_Vao; $x2 = $P.Ab_Vao + $P.Ab_L; $z1 = -$P.Tampa_E; $z2 = -$P.Tampa_E - $P.Ab_H
foreach ($side in 1, -1) {
    $nome = if ($side -gt 0) { 'BLDC_Abertura_Direita' } else { 'BLDC_Abertura_Esquerda' }
    $sk = $def.Sketches.Add($xz); $sk.Name = "Esboco_$nome"
    $prof = Add-Rect $sk @(($side * $x1), 0, $z1) @(($side * $x2), 0, $z2) @('Ab_Vao', 'Tampa_E') @('Ab_Vao + Ab_L', 'Tampa_E + Ab_H')
    $vol0 = $def.SurfaceBodies.Item(1).Volume(0.01)
    $ed = $def.Features.ExtrudeFeatures.CreateExtrudeDefinition($prof, $kCut); $ed.SetDistanceExtent('USWidth / 2 + 1 cm', $kNeg)
    $f = $def.Features.ExtrudeFeatures.Add($ed); $f.Name = $nome
    # confere se cortou a parede da frente (Y < 0); se cortou a de trás, inverte o sentido
    $hit = $false
    foreach ($fc in $f.Faces) { if ($fc.PointOnFace.Y -lt -($P.USWidth / 20) + 0.5) { $hit = $true } }
    if (-not $hit) { $f.Definition.SetDistanceExtent('USWidth / 2 + 1 cm', $kPos) }
}
$doc.Update()
"erros: $($em.HasErrors) $($em.LastMessage)"
