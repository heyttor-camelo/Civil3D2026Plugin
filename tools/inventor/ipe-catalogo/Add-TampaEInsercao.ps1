# BLDC/BLSC: laje de topo (Tampa_E) e insercao na face da guia (0, -USWidth/2, 0).
# Rodar depois do Add-BldcFeatures.ps1, com a caixa aberta no Inventor. Atualiza o CenterPoint
# de todos os conectores no XML ContentBuilderAttributeSets e salva.
param([string]$Path,[string]$Prefixo='BLDC')
$ErrorActionPreference = 'Stop'
$kJoin = 20481; $kPos = 20993; $kNeg = 20994; $kHorizontalDim = 19201; $kVerticalDim = 19202

$inv = [Runtime.InteropServices.Marshal]::GetActiveObject('Inventor.Application')
$doc = $null
foreach ($d in $inv.Documents) { if ($d.FullFileName -eq $Path) { $doc = $d } }
if (-not $doc) { $doc = $inv.Documents.Open($Path, $true) }
$def = $doc.ComponentDefinition; $tg = $inv.TransientGeometry
$origin = $def.WorkPoints.Item(1)
$L2 = $def.Parameters.Item('USLength').Value / 2; $W2 = $def.Parameters.Item('USWidth').Value / 2

# 1. Laje de topo: retangulo USLength x USWidth no plano XY, extrudado Tampa_E para baixo
$sk = $def.Sketches.Add($def.WorkPlanes.Item(3)); $sk.Name = "${Prefixo}_Esboco_Tampa"
$o = $sk.AddByProjectingEntity($origin)
$pa = $tg.CreatePoint2d(-$L2, -$W2); $pb = $tg.CreatePoint2d($L2, $W2)
$pts = @(@($sk.SketchLines.AddAsTwoPointRectangle($pa, $pb)) | % { $_.StartSketchPoint })
function Nearest($t) { $best = $null; $bd = 1e99; foreach ($s in $pts) { $dd = $s.Geometry.DistanceTo($t); if ($dd -lt $bd) { $bd = $dd; $best = $s } }; $best }
foreach ($pair in @(@((Nearest $pa), $pa), @((Nearest $pb), $pb))) {
    $corner, $geo = $pair
    $expr = @('USLength / 2', 'USWidth / 2')
    for ($i = 0; $i -lt 2; $i++) {
        $orient = if ($i -eq 0) { $kHorizontalDim } else { $kVerticalDim }
        $dim = $sk.DimensionConstraints.AddTwoPointDistance($o, $corner, $orient, $tg.CreatePoint2d($geo.X / 2 + 1, $geo.Y / 2 + 1))
        $dim.Parameter.Expression = $expr[$i]
    }
}
$ed = $def.Features.ExtrudeFeatures.CreateExtrudeDefinition($sk.Profiles.AddForSolid(), $kJoin); $ed.SetDistanceExtent('Tampa_E', $kNeg)
$f = $def.Features.ExtrudeFeatures.Add($ed); $f.Name = "${Prefixo}_Laje_Topo"
if ($def.SurfaceBodies.Item(1).RangeBox.MaxPoint.Z -gt 0.01) { $f.Definition.SetDistanceExtent('Tampa_E', $kPos) }   # sentido errado: inverte

# 2. Insercao: ponto (0, -USWidth/2) no ConnectorInnerSketch, cotado contra a origem; o work point da porta passa a usa-lo.
# Nao usar WidthCenterOffset: ele tambem desloca o corte ConnectorInnerBody.
$cs = $def.Sketches.Item('ConnectorInnerSketch'); $o = $cs.AddByProjectingEntity($origin)
$p2 = $cs.ModelToSketchSpace($tg.CreatePoint(0, -$W2, 0)); $sp = $cs.SketchPoints.Add($p2, $false)
$d1 = $cs.DimensionConstraints.AddTwoPointDistance($o, $sp, $kVerticalDim, $tg.CreatePoint2d($p2.X + 3, $p2.Y / 2)); $d1.Parameter.Expression = 'USWidth / 2'
$d2 = $cs.DimensionConstraints.AddTwoPointDistance($o, $sp, $kHorizontalDim, $tg.CreatePoint2d($p2.X + 1, $p2.Y - 3)); $d2.Parameter.Expression = '0 mm'
$def.WorkPoints.Item('UndergroundStructurePort_wp').SetByPoint($sp)
$doc.Update()

& (Join-Path $PSScriptRoot 'Sync-ConectoresXml.ps1') -Doc $doc
"volume: $([math]::Round($def.MassProperties.Volume / 1e6, 4)) m3; erros: $($inv.ErrorManager.HasErrors)"
