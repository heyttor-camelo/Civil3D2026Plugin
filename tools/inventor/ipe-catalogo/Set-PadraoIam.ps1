# Ajusta os padroes do geom.iam (e do param.xml) para as medidas da peca. Salvar o geom.iam roda a regra
# iLogic, que devolve a caixa aos padroes do iam (os da Autodesk). Confere a caixa depois.
# Set-PadraoIam.ps1 -Recipe <pasta> -L 2200 -W 800 -WTh 100 -H 1100   (mm, Inventor aberto)
param([string]$Recipe,[double]$L,[double]$W,[double]$WTh,[double]$H,[double]$BaseTh=100)
$ErrorActionPreference='Stop'
$inv=[Runtime.InteropServices.Marshal]::GetActiveObject('Inventor.Application'); $inv.SilentOperation=$true
$a=$inv.Documents.Open((Join-Path $Recipe 'geom.iam'),$false); $up=$a.ComponentDefinition.Parameters.UserParameters
$ssH=$up.Item('SurfaceStructure_SSHeight').Value*10; $sfL=$L-200
$v=[ordered]@{ UndergroundStructure_USLength=$L; UndergroundStructure_USWidth=$W; SurfaceStructure_WallTh=$WTh; UndergroundStructure_BaseTh=$BaseTh
    SurfaceStructure_SSLength=$sfL; SurfaceStructure_SPLength=$L; SurfaceStructure_CGLength=$sfL-2*$WTh; StructureAssemblyHeight=$H+$ssH }
foreach($k in $v.Keys){ $up.Item($k).Expression="$($v[$k]) mm" }
$a.Update(); $a.Save(); $a.Close($true)
[xml]$x=[IO.File]::ReadAllText("$Recipe\param.xml"); foreach($pn in @($x.SelectNodes('//Param'))){ if($v.Contains($pn.Name)){ $pn.Value=[string]($v[$pn.Name]/10.0) } }; $x.Save("$Recipe\param.xml")
$ipt=(Get-ChildItem $Recipe -Filter '*_UndergroundStructure.ipt').FullName
$d=$inv.Documents.Open($ipt,$false); $pp=$d.ComponentDefinition.Parameters
"$(Split-Path $Recipe -Leaf): caixa $($pp.Item('USLength').Expression) x $($pp.Item('USWidth').Expression) x $($pp.Item('USHeight').Expression), parede $($pp.Item('WallTh').Expression), volume $([math]::Round($d.ComponentDefinition.MassProperties.Volume/1e6,4)) m3"
$d.Close($true)
