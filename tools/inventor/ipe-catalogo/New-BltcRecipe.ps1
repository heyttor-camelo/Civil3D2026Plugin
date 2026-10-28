# BLTC (boca de lobo tripla, boca na guia): copia o recipe da BLDC_IPE com id novo e acrescenta a abertura
# central. As laterais passam a ficar a Ab_Gap da central (Ab_Vao = Ab_L / 2 + Ab_Gap, calculado no .ipt).
# Medidas do Part Builder (Drawing2): 3 aberturas de 950 x 150, folga de 175, caixa interna 3200 x 600.
param([string]$Origem='C:\Temp\BLDC-IPE\recipes\fc\dbd57a-c7c8-4459-86a8-75b3d439f67d',[string]$Id=([guid]::NewGuid().ToString()),
      [string]$Raiz='C:\Temp\BLDC-IPE\recipes')
$ErrorActionPreference='Stop'
$BF=[Reflection.BindingFlags]; $kCut=20482; $kPos=20993; $kNeg=20994; $kH=19201; $kV=19202
$dst=Join-Path $Raiz "$($Id.Substring(0,2))\$($Id.Substring(2))"; New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "$Origem\*" $dst -Force
$old=(Split-Path (Split-Path $Origem -Parent) -Leaf)+(Split-Path $Origem -Leaf)
foreach($f in 'index.json','param.xml'){ $p="$dst\$f"; [IO.File]::WriteAllText($p,[IO.File]::ReadAllText($p).Replace($old,$Id)) }

$inv=[Runtime.InteropServices.Marshal]::GetActiveObject('Inventor.Application'); $inv.SilentOperation=$true; $tg=$inv.TransientGeometry
# 1. caixa: comprimento, folga e abertura central
$ipt=(Get-ChildItem $dst -Filter '*_UndergroundStructure.ipt').FullName
$doc=$inv.Documents.Open($ipt,$false); $def=$doc.ComponentDefinition; $P=$def.Parameters
foreach($par in @(@('USLength','3400 mm'),@('SPLength','3400 mm'),@('Ab_L','950 mm'))){ $P.Item($par[0]).Expression=$par[1] }
$null=$P.UserParameters.AddByExpression('Ab_Gap','175 mm','mm'); $P.Item('Ab_Vao').Expression='Ab_L / 2 + Ab_Gap'
foreach($f in $def.Features){ if($f.Name -like 'BLDC_*'){ $f.Name=$f.Name.Replace('BLDC_','BLTC_') } }
foreach($s in $def.Sketches){ if($s.Name -like '*BLDC_*'){ $s.Name=$s.Name.Replace('BLDC_','BLTC_') } }
$doc.Update()
$te=$P.Item('Tampa_E').Value; $ah=$P.Item('Ab_H').Value; $al=$P.Item('Ab_L').Value
$sk=$def.Sketches.Add($def.WorkPlanes.Item(2)); $sk.Name='Esboco_BLTC_Abertura_Central'; $o=$sk.AddByProjectingEntity($def.WorkPoints.Item(1))
$pa=$sk.ModelToSketchSpace($tg.CreatePoint(-$al/2,0,-$te)); $pb=$sk.ModelToSketchSpace($tg.CreatePoint($al/2,0,-$te-$ah))
$pts=@(@($sk.SketchLines.AddAsTwoPointRectangle($pa,$pb)) | % { $_.StartSketchPoint })
function Nearest($t){ $best=$null; $bd=1e99; foreach($s in $pts){ $dd=$s.Geometry.DistanceTo($t); if($dd -lt $bd){ $bd=$dd; $best=$s } }; $best }
foreach($pair in @(@((Nearest $pa),$pa,@('Ab_L / 2','Tampa_E')),@((Nearest $pb),$pb,@('Ab_L / 2','Tampa_E + Ab_H')))){
    $corner,$geo,$expr=$pair
    for($i=0;$i -lt 2;$i++){ $or= if($i -eq 0){$kH}else{$kV}; $dim=$sk.DimensionConstraints.AddTwoPointDistance($o,$corner,$or,$tg.CreatePoint2d($geo.X/2+1,$geo.Y/2+1)); $dim.Parameter.Expression=$expr[$i] } }
$ed=$def.Features.ExtrudeFeatures.CreateExtrudeDefinition($sk.Profiles.AddForSolid(),$kCut); $ed.SetDistanceExtent('USWidth / 2 + 1 cm',$kNeg)
$f=$def.Features.ExtrudeFeatures.Add($ed); $f.Name='BLTC_Abertura_Central'
$hit=$false; $yF=-$P.Item('USWidth').Value/2+5; foreach($fc in $f.Faces){ if($fc.PointOnFace.Y -lt $yF){ $hit=$true } }; if(-not $hit){ $f.Definition.SetDistanceExtent('USWidth / 2 + 1 cm',$kPos) }
$doc.Update(); & (Join-Path $PSScriptRoot 'Sync-ConectoresXml.ps1') -Doc $doc
"BLTC caixa: volume $([math]::Round($def.MassProperties.Volume/1e6,4)) m3; erros: $($inv.ErrorManager.HasErrors)"; $doc.Close($true)

# 2. geom.iam: a regra deixa de mandar Ab_Vao (agora calculado) e passa Ab_Gap
$ad=$inv.ApplicationAddIns.ItemById('{3bdd8d79-2179-4b11-8a5a-257b1c0263ac}'); if(-not $ad.Activated){ $ad.Activate() }
$auto=[System.__ComObject].InvokeMember('Automation',$BF::GetProperty,$null,$ad,$null)
$a=$inv.Documents.Open("$dst\geom.iam",$false); $rule=[System.__ComObject].InvokeMember('GetRule',$BF::InvokeMethod,$null,$auto,@($a,'UndergroundStructure'))
$txt=[System.__ComObject].InvokeMember('Text',$BF::GetProperty,$null,$rule,$null)
$txt=($txt -split "`r?`n" | ? { $_ -notmatch 'Ab_Vao' }) -join "`r`n"; [System.__ComObject].InvokeMember('Text',$BF::SetProperty,$null,$rule,@($txt))
try{ $a.ComponentDefinition.Parameters.UserParameters.Item('UndergroundStructure_Ab_Vao').Delete() }catch{}
$a.Save(); $a.Close($true)
[xml]$x=[IO.File]::ReadAllText("$dst\param.xml"); $n=$x.SelectSingleNode("//Param[@Name='UndergroundStructure_Ab_Vao']"); if($n){ $null=$n.ParentNode.RemoveChild($n) }; $x.Save("$dst\param.xml")
& (Join-Path $PSScriptRoot 'Add-ParametrosRecipe.ps1') -Recipe $dst -Params @{ Ab_Gap=@('175 mm','Folga entre as aberturas'); Ab_L=@('950 mm','Comprimento de cada abertura') }
"recipe BLTC: $Id"
