param([string]$Path = 'C:\Temp\BLDC-IPE\BLDC.ipt')
$ErrorActionPreference = 'Stop'
$inv = [Runtime.InteropServices.Marshal]::GetActiveObject('Inventor.Application')
$doc = $null
foreach ($d in $inv.Documents) { if ($d.FullFileName -eq $Path) { $doc = $d } }
if (-not $doc) { $doc = $inv.Documents.Open($Path, $true) }
$def = $doc.ComponentDefinition

function Report($label) {
    $doc.Update()
    "== $label"
    "corpos: $($def.SurfaceBodies.Count)"
    foreach ($b in $def.SurfaceBodies) {
        $r = $b.RangeBox
        "  caixa (mm): X {0:N1}..{1:N1}  Y {2:N1}..{3:N1}  Z {4:N1}..{5:N1}" -f ($r.MinPoint.X*10),($r.MaxPoint.X*10),($r.MinPoint.Y*10),($r.MaxPoint.Y*10),($r.MinPoint.Z*10),($r.MaxPoint.Z*10)
        "  volume (m3): {0:N4}" -f ($b.Volume(0.01) / 1e6)
    }
    foreach ($f in $def.Features) { if ($f.HealthStatus -ne 11778) { "  feature {0}: health {1}" -f $f.Name, $f.HealthStatus } }
    foreach ($s in $def.Sketches) { if ($s.HealthStatus -ne 11778) { "  sketch {0}: health {1}" -f $s.Name, $s.HealthStatus } }
    foreach ($wp in $def.WorkPoints) {
        if ($wp.Name -like 'Conexao_*') { $g = $wp.Point; "  {0}: ({1:N1}, {2:N1}, {3:N1})" -f $wp.Name,($g.X*10),($g.Y*10),($g.Z*10) }
    }
    # aberturas: faces planas na parede frontal (Y = 0) que não estão no plano Y=0 só aparecem com furo
    foreach ($b in $def.SurfaceBodies) { "  faces: $($b.Faces.Count)" }
}

$up = $def.Parameters.UserParameters
Report 'padrao'
# SIL e SRS derivam dos parâmetros-chave do IPE: SIL = USLength - 2 WallTh, SRS = USHeight - BaseTh
$up.Item('USLength').Expression = '3200 mm'; $up.Item('USHeight').Expression = '1600 mm'
Report 'SIL=3000 SRS=1500'
$up.Item('USLength').Expression = '2200 mm'; $up.Item('USHeight').Expression = '1100 mm'
Report 'de volta ao padrao'
$doc.Save()
'salvo'
