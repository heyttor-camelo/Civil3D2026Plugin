# Bocas de lobo de grelha (G: BLDG/BLSG/BLTG) e mistas (M: BLDM/BLSM): copia o recipe da BLSC_IPE
# (aba, laje de topo, conectores), tira a abertura da guia, abre N vaos de grelha na laje e move a
# insercao para a face da guia, que fica a Frente da face externa da pista (G: atras da caixa, Frente = USWidth).
# Medidas do Part Builder (Drawing2), em mm:
#   BLDG 2540 x 920, 2 vaos 1050 x 620, folga 150   BLSG 1350 x 920, 1 vao   BLTG 3810 x 920, 3 vaos, folga 180
#   BLDM 2200 x 1200, frente 400, 2 vaos 1000 x 335, folga 50   BLSM 1200 x 1200, 1 vao 1040 x 315
param([Parameter(Mandatory)][string]$Nome,[Parameter(Mandatory)][int]$N,[double]$L,[double]$W,[double]$WTh=100,
      [double]$GrL,[double]$GrW,[double]$GrGap=150,[double]$GrY,[double]$Frente,[double]$H=1100,
      [string]$Origem='C:\Temp\BLDC-IPE\recipes\e0\34d699-3aec-46d6-9475-15afe8c9bbb7',[string]$Id=([guid]::NewGuid().ToString()),
      [string]$Raiz='C:\Temp\BLDC-IPE\recipes')
$ErrorActionPreference='Stop'
$BF=[Reflection.BindingFlags]; $kCut=20482; $kPos=20993; $kNeg=20994; $kHor=19201; $kVer=19202
$dst=Join-Path $Raiz "$($Id.Substring(0,2))\$($Id.Substring(2))"; New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "$Origem\*" $dst -Force
$old=(Split-Path (Split-Path $Origem -Parent) -Leaf)+(Split-Path $Origem -Leaf)
foreach($f in 'index.json','param.xml'){ $p="$dst\$f"; [IO.File]::WriteAllText($p,[IO.File]::ReadAllText($p).Replace($old,$Id)) }

$inv=[Runtime.InteropServices.Marshal]::GetActiveObject('Inventor.Application'); $inv.SilentOperation=$true; $tg=$inv.TransientGeometry
$ipt=(Get-ChildItem $dst -Filter '*_UndergroundStructure.ipt').FullName
$doc=$inv.Documents.Open($ipt,$false); $def=$doc.ComponentDefinition; $P=$def.Parameters; $U=$P.UserParameters
foreach($par in @(@('USLength',"$L mm"),@('SPLength',"$L mm"),@('USWidth',"$W mm"),@('SPWidth',"$W mm"),@('WallTh',"$WTh mm"),@('USHeight',"$H mm"))){ $P.Item($par[0]).Expression=$par[1] }
foreach($par in @(@('Gr_L',$GrL),@('Gr_W',$GrW),@('Gr_Gap',$GrGap),@('Gr_Y',$GrY),@('Frente',$Frente))){ $null=$U.AddByExpression($par[0],"$($par[1]) mm",'mm') }
# sem abertura na guia
$def.Features.Item('BLSC_Abertura_Central').Delete($false,$false,$false)
foreach($f in $def.Features){ if($f.Name -like 'BLSC_*'){ $f.Name=$f.Name.Replace('BLSC_',"$($Nome)_") } }
foreach($s in $def.Sketches){ if($s.Name -like '*BLSC_*'){ $s.Name=$s.Name.Replace('BLSC_',"$($Nome)_") } }
$doc.Update()
# insercao na face da guia: y = -USWidth/2 + Frente (cotado contra a origem, sempre do mesmo lado)
$cs=$def.Sketches.Item('ConnectorInnerSketch'); $o=$cs.AddByProjectingEntity($def.WorkPoints.Item(1))
$yIns=(-$W/2+$Frente)/10; $p2=$cs.ModelToSketchSpace($tg.CreatePoint(0,$yIns,0)); $sp=$cs.SketchPoints.Add($p2,$false)
if([math]::Abs($yIns) -gt 1e-6){ $d1=$cs.DimensionConstraints.AddTwoPointDistance($o,$sp,$kVer,$tg.CreatePoint2d($p2.X+3,$p2.Y/2)); $d1.Parameter.Expression='abs(USWidth / 2 - Frente)' }
else { $null=$cs.GeometricConstraints.AddCoincident($sp,$o) }
$d2=$cs.DimensionConstraints.AddTwoPointDistance($o,$sp,$kHor,$tg.CreatePoint2d($p2.X+1,$p2.Y-3)); $d2.Parameter.Expression='0 mm'
$def.WorkPoints.Item('UndergroundStructurePort_wp').SetByPoint($sp); $doc.Update()
# N vaos de grelha na laje de topo (plano XY, corte Tampa_E + 1 cm para baixo)
$sk=$def.Sketches.Add($def.WorkPlanes.Item(3)); $sk.Name="Esboco_$($Nome)_Grelhas"; $o=$sk.AddByProjectingEntity($def.WorkPoints.Item(1))
$tot=$N*$GrL+($N-1)*$GrGap
for($i=0;$i -lt $N;$i++){
    $x1=-$tot/2+$i*($GrL+$GrGap); $x2=$x1+$GrL; $yc=-$W/2+$GrY; $y1=$yc-$GrW/2; $y2=$yc+$GrW/2
    $ex1="abs($i * (Gr_L + Gr_Gap) - ($N * Gr_L + $($N-1) * Gr_Gap) / 2)"; $ex2="abs($i * (Gr_L + Gr_Gap) - ($N * Gr_L + $($N-1) * Gr_Gap) / 2 + Gr_L)"
    $ey1='abs(-USWidth / 2 + Gr_Y - Gr_W / 2)'; $ey2='abs(-USWidth / 2 + Gr_Y + Gr_W / 2)'
    $pa=$tg.CreatePoint2d($x1/10,$y1/10); $pb=$tg.CreatePoint2d($x2/10,$y2/10)
    $pts=@(@($sk.SketchLines.AddAsTwoPointRectangle($pa,$pb)) | % { $_.StartSketchPoint })
    foreach($par in @(@($pa,$ex1,$ey1),@($pb,$ex2,$ey2))){
        $geo=$par[0]; $best=$null; $bd=1e99; foreach($s in $pts){ $dd=$s.Geometry.DistanceTo($geo); if($dd -lt $bd){ $bd=$dd; $best=$s } }
        foreach($j in 0,1){ $or= if($j -eq 0){$kHor}else{$kVer}; $e= if($j -eq 0){$par[1]}else{$par[2]}
            $vx= if($j -eq 0){ $geo.X } else { 0 }; $vy= if($j -eq 0){ 0 } else { $geo.Y }
            if([math]::Abs($vx)+[math]::Abs($vy) -lt 1e-6){ continue }   # canto em cima do eixo: sem cota
            $dim=$sk.DimensionConstraints.AddTwoPointDistance($o,$best,$or,$tg.CreatePoint2d($geo.X/2+1,$geo.Y/2+1)); $dim.Parameter.Expression=$e } }
}
$ed=$def.Features.ExtrudeFeatures.CreateExtrudeDefinition($sk.Profiles.AddForSolid(),$kCut); $ed.SetDistanceExtent('Tampa_E + 1 cm',$kNeg)
$f=$def.Features.ExtrudeFeatures.Add($ed); $f.Name="$($Nome)_Vaos_Grelha"; $doc.Update()
if($def.SurfaceBodies.Item(1).RangeBox.MaxPoint.Z -gt 0.01){ $f.Definition.SetDistanceExtent('Tampa_E + 1 cm',$kPos) }
$doc.Update(); & (Join-Path $PSScriptRoot 'Sync-ConectoresXml.ps1') -Doc $doc
$furos=0; foreach($fc in $def.SurfaceBodies.Item(1).Faces){ if($fc.SurfaceType -eq 5890 -and $fc.Geometry.Normal.Z -gt 0.99 -and [math]::Abs($fc.PointOnFace.Z) -lt 1e-4){ $furos+=@($fc.EdgeLoops | ? { -not $_.IsOuterEdgeLoop }).Count } }
"$Nome caixa: vaos no topo=$furos insercao y=$([math]::Round($def.WorkPoints.Item('UndergroundStructurePort_wp').Point.Y*10)) volume=$([math]::Round($def.MassProperties.Volume/1e6,4)) m3 erros=$($inv.ErrorManager.HasErrors)"
$doc.Close($true)

# geom.iam: padrao da familia, sem os parametros de abertura da guia; novos parametros da grelha
$ad=$inv.ApplicationAddIns.ItemById('{3bdd8d79-2179-4b11-8a5a-257b1c0263ac}'); if(-not $ad.Activated){ $ad.Activate() }
$auto=[System.__ComObject].InvokeMember('Automation',$BF::GetProperty,$null,$ad,$null)
$a=$inv.Documents.Open("$dst\geom.iam",$false); $up=$a.ComponentDefinition.Parameters.UserParameters
$rule=[System.__ComObject].InvokeMember('GetRule',$BF::InvokeMethod,$null,$auto,@($a,'UndergroundStructure'))
$txt=[System.__ComObject].InvokeMember('Text',$BF::GetProperty,$null,$rule,$null); $txt=($txt -split "`r?`n" | ? { $_ -notmatch 'Ab_(L|H|Vao)' }) -join "`r`n"
[System.__ComObject].InvokeMember('Text',$BF::SetProperty,$null,$rule,@($txt))
foreach($nm in 'UndergroundStructure_Ab_L','UndergroundStructure_Ab_H','UndergroundStructure_Ab_Vao'){ try{ $up.Item($nm).Delete() }catch{} }
$sfL=$L-200; $v=[ordered]@{UndergroundStructure_USLength=$L;UndergroundStructure_USWidth=$W;UndergroundStructure_WallTh=$WTh;SurfaceStructure_WallTh=$WTh;SurfaceStructure_SSLength=$sfL;SurfaceStructure_SPLength=$L;SurfaceStructure_CGLength=$sfL-2*$WTh;GrateOrCover_CGLength=$sfL-2*$WTh;StructureAssemblyHeight=$H+100}
foreach($k in $v.Keys){ try{ $up.Item($k).Expression="$($v[$k]) mm" }catch{} }
$a.Save(); $a.Close($true)
[xml]$x=[IO.File]::ReadAllText("$dst\param.xml"); foreach($pn in @($x.SelectNodes('//Param'))){ if($pn.Name -match 'UndergroundStructure_Ab_'){ $null=$pn.ParentNode.RemoveChild($pn) } elseif($v.Contains($pn.Name)){ $pn.Value=[string]($v[$pn.Name]/10.0) } }; $x.Save("$dst\param.xml")
& (Join-Path $PSScriptRoot 'Add-ParametrosRecipe.ps1') -Recipe $dst -Params @{ Gr_L=@("$GrL mm",'Comprimento do vao da grelha'); Gr_W=@("$GrW mm",'Largura do vao da grelha'); Gr_Gap=@("$GrGap mm",'Folga entre grelhas'); Gr_Y=@("$GrY mm",'Centro das grelhas a partir da face da pista'); Frente=@("$Frente mm",'Distancia da face da pista ate a face da guia') }
"recipe $($Nome): $Id"
