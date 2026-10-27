# Instala uma peca (recipe + tabela) no catalogo FDX Metric Structures. Rodar com o Civil 3D fechado.
param([Parameter(Mandatory)][string]$Recipe,[Parameter(Mandatory)][string]$Tabela,[Parameter(Mandatory)][string]$Nome,[string]$Capitulo='CSBIM-DRENAGEM',[string]$Forma='Rectangular')
$ErrorActionPreference='Stop'
$pc='C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog'; $fdx="$pc\FDX Metric Structures"; $apcF="$fdx\FDX Metric Structures.apc"
$rid=Split-Path $Recipe -Leaf; $rid=(Split-Path (Split-Path $Recipe -Parent) -Leaf)+$rid
$rdst="C:\ProgramData\Autodesk\C3D 2026\enu\ContentLibrary\Templates\$($rid.Substring(0,2))\$($rid.Substring(2))"
if(-not (Test-Path $rdst)){ New-Item -ItemType Directory -Force $rdst | Out-Null; Copy-Item "$Recipe\*" $rdst -Exclude OldVersions }
$jpg=[IO.Path]::ChangeExtension($Tabela,'.jpg'); Copy-Item $Tabela,$jpg "$fdx\$Capitulo\"
$partId=([xml][IO.File]::ReadAllText($Tabela)).LandPart.ColumnConst | ? { $_.name -eq 'PrtID' } | % { $_.InnerText }
$bytes=[IO.File]::ReadAllBytes($apcF); $bom=($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB); $t=[IO.File]::ReadAllText($apcF,[Text.Encoding]::UTF8)
if($t -match "name=""$Nome"""){ "$Nome ja esta no apc"; return }
$nl= if($t -match "`r`n"){"`r`n"}else{"`n"}
$need=[regex]::Matches([IO.File]::ReadAllText($Tabela),' name="([^"]+)"') | % { $_.Groups[1].Value } | Select -Unique
$have=[regex]::Matches($t,'<ParameterString>([^<]+)</ParameterString>') | % { $_.Groups[1].Value }
$ins=($need | ? { $_ -notin $have -and $_ -ne 'UUID' } | % { "    <ParameterString>$_</ParameterString>" }) -join $nl
if($ins){ $t=$t.Replace('</ParameterSet>',"$ins$nl   </ParameterSet>") }
$base=[IO.Path]::GetFileNameWithoutExtension($Tabela)
$part="  <Part name=""$Nome"" id=""$partId"">$nl   <Desc>$Nome</Desc>$nl   <Images>$nl    <Image>$nl     <URL xlink:title=""$Nome"" xlink:href=""$Capitulo\$base.jpg"" xlink:show=""embed"" xlink:actuate=""onLoad""/>$nl    </Image>$nl   </Images>$nl   <TableRef>$nl    <URL xlink:href=""$Capitulo\$base.xml""/>$nl   </TableRef>$nl   <Extrinsics>$nl    <Extrinsic name=""AeccPartSearchAttribList"">$nl     <AeccPartSearchAttribList domain=""Structure_Domain"" part_type=""Struct_Junction"" part_subtype=""Undefined"" part_shape=""$Forma"" placeholder=""False"" hidepart=""False""/>$nl    </Extrinsic>$nl   </Extrinsics>$nl  </Part>$nl"
$m=[regex]::Match($t,"<Chapter name=""$([regex]::Escape($Capitulo))"">[\s\S]*?</Chapter>"); if(-not $m.Success){ throw "capitulo $Capitulo nao encontrado" }
$chap=$m.Value; $t=$t.Replace($chap, $chap.Substring(0,$chap.LastIndexOf('</Chapter>')) + $part + ' </Chapter>')
[xml]$null=$t; [IO.File]::WriteAllText($apcF,$t,(New-Object Text.UTF8Encoding $bom))
"$Nome instalada (PrtID $partId, recipe $rid)."
