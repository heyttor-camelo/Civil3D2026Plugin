# PV DNIT: na copia da caixa d2eb77e5 (recipe ccaec438), chamine em tronco de cone.
# ConeBaseD = SPDiameter + 600 mm na base; mesmo angulo no RiserBody e no RiserInnerBody (parede constante).
# Insercao continua no centro. Valores padrao da peca instalada: 2800 x 2800, paredes 200, SP 1200.
param([string]$Path)
$ErrorActionPreference = 'Stop'
$inv = [Runtime.InteropServices.Marshal]::GetActiveObject('Inventor.Application')
$doc = $inv.Documents.Open($Path, $true); $def = $doc.ComponentDefinition; $P = $def.Parameters
foreach ($kv in @(@('USLength', '2800 mm'), @('USWidth', '2800 mm'), @('WallTh', '200 mm'), @('BaseTh', '200 mm'), @('SPDiameter', '1200 mm'),
                  @('USHeight', '1500 mm'), @('USBaseHeight', '700 mm'), @('USRiserHeight', 'USHeight - USBaseHeight'))) { $P.Item($kv[0]).Expression = $kv[1] }
$null = $P.UserParameters.AddByExpression('ConeBaseD', 'SPDiameter + 600 mm', 'mm')
foreach ($n in 'RiserBody', 'RiserInnerBody') {
    $def.Features.Item($n).Definition.TaperAngle.Expression = 'atan((ConeBaseD - SPDiameter) / (2 * USRiserHeight))'
}
$doc.Update()
& (Join-Path $PSScriptRoot 'Sync-ConectoresXml.ps1') -Doc $doc
# 2800 x 2800, SBSH 1600 (USHeight 1500, base 700, chamine 800): 4,071 m3
"volume: $([math]::Round($def.MassProperties.Volume / 1e6, 4)) m3; erros: $($inv.ErrorManager.HasErrors)"
