# BLSC: parte de uma copia da caixa da BLDC (ja com aba, laje e insercao). Troca as duas aberturas
# por uma central de Ab_L x Ab_H na parede da frente (-Y) e ajusta USLength.
param([string]$Path,[string]$USLength='1200 mm',[string]$AbL='800 mm')
$ErrorActionPreference = 'Stop'
$kCut = 20482; $kPos = 20993; $kNeg = 20994; $kHorizontalDim = 19201; $kVerticalDim = 19202

$inv = [Runtime.InteropServices.Marshal]::GetActiveObject('Inventor.Application')
$doc = $inv.Documents.Open($Path, $true); $def = $doc.ComponentDefinition; $tg = $inv.TransientGeometry
$def.Parameters.Item('USLength').Expression = $USLength; $def.Parameters.Item('SPLength').Expression = $USLength
$def.Parameters.Item('Ab_L').Expression = $AbL; $doc.Update()
foreach ($n in 'BLDC_Abertura_Direita', 'BLDC_Abertura_Esquerda') { $def.Features.Item($n).Delete($false, $false, $false) }
foreach ($f in $def.Features) { if ($f.Name -like 'BLDC_*') { $f.Name = $f.Name.Replace('BLDC_', 'BLSC_') } }
foreach ($s in $def.Sketches) { if ($s.Name -like 'BLDC_*') { $s.Name = $s.Name.Replace('BLDC_', 'BLSC_') } }

$P = @{}; foreach ($n in 'Tampa_E', 'Ab_H', 'Ab_L') { $P[$n] = $def.Parameters.Item($n).Value }   # cm
$sk = $def.Sketches.Add($def.WorkPlanes.Item(2)); $sk.Name = 'Esboco_BLSC_Abertura_Central'
$o = $sk.AddByProjectingEntity($def.WorkPoints.Item(1))
$pa = $sk.ModelToSketchSpace($tg.CreatePoint(-$P.Ab_L / 2, 0, -$P.Tampa_E))
$pb = $sk.ModelToSketchSpace($tg.CreatePoint($P.Ab_L / 2, 0, -$P.Tampa_E - $P.Ab_H))
$pts = @(@($sk.SketchLines.AddAsTwoPointRectangle($pa, $pb)) | % { $_.StartSketchPoint })
function Nearest($t) { $best = $null; $bd = 1e99; foreach ($s in $pts) { $dd = $s.Geometry.DistanceTo($t); if ($dd -lt $bd) { $bd = $dd; $best = $s } }; $best }
foreach ($pair in @(@((Nearest $pa), $pa, @('Ab_L / 2', 'Tampa_E')), @((Nearest $pb), $pb, @('Ab_L / 2', 'Tampa_E + Ab_H')))) {
    $corner, $geo, $expr = $pair
    for ($i = 0; $i -lt 2; $i++) {
        $orient = if ($i -eq 0) { $kHorizontalDim } else { $kVerticalDim }
        $dim = $sk.DimensionConstraints.AddTwoPointDistance($o, $corner, $orient, $tg.CreatePoint2d($geo.X / 2 + 1, $geo.Y / 2 + 1))
        $dim.Parameter.Expression = $expr[$i]
    }
}
$ed = $def.Features.ExtrudeFeatures.CreateExtrudeDefinition($sk.Profiles.AddForSolid(), $kCut); $ed.SetDistanceExtent('USWidth / 2 + 1 cm', $kNeg)
$f = $def.Features.ExtrudeFeatures.Add($ed); $f.Name = 'BLSC_Abertura_Central'
# confere se cortou a parede da frente (Y < 0); se cortou a de tras, inverte o sentido
$hit = $false; $yFrente = -$def.Parameters.Item('USWidth').Value / 2 + 5
foreach ($fc in $f.Faces) { if ($fc.PointOnFace.Y -lt $yFrente) { $hit = $true } }
if (-not $hit) { $f.Definition.SetDistanceExtent('USWidth / 2 + 1 cm', $kPos) }
$doc.Update()
& (Join-Path $PSScriptRoot 'Sync-ConectoresXml.ps1') -Doc $doc
"volume: $([math]::Round($def.MassProperties.Volume / 1e6, 4)) m3; erros: $($inv.ErrorManager.HasErrors)"
