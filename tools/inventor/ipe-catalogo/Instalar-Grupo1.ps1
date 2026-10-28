# Instala as bocas de lobo do grupo 1 (_IPE) no FDX Metric Structures, capitulo CSBIM-DRENAGEM.
# Faz backup do FDX e das receitas antes; no .apc so acrescenta. Rodar com o Civil 3D FECHADO.
# Sem -Executar so mostra o que faria.
param([switch]$Executar,[string]$Recipes='C:\Temp\BLDC-IPE\recipes',[string]$Tabelas='C:\Temp\BLDC-IPE\tabelas-grupo1')
$ErrorActionPreference='Stop'
$pc='C:\ProgramData\Autodesk\C3D 2026\enu'; $fdx="$pc\Pipes Catalog\FDX Metric Structures"; $T="$pc\ContentLibrary\Templates"
if($Executar -and (Get-Process acad -EA SilentlyContinue)){ throw 'Feche o Civil 3D antes de instalar.' }
$pecas=Get-ChildItem $Tabelas -Filter '*_IPE.xml'
$plano=foreach($t in $pecas){ $rid=([xml](Get-Content $t.FullName -Raw)).LandPart.ColumnConstView.Recipe; $src=Join-Path $Recipes "$($rid.Substring(0,2))\$($rid.Substring(2))"
    [pscustomobject]@{ Peca=$t.BaseName; Recipe=$rid; Origem=$src; OrigemOk=(Test-Path $src); JaNoApc=([IO.File]::ReadAllText("$fdx\FDX Metric Structures.apc") -match "name=""$($t.BaseName)""") } }
$plano | ft -AutoSize | Out-String -Width 200
if(-not $Executar){ 'Simulacao. Rode com -Executar para instalar.'; return }
$bk="C:\Temp\backup-FDX-Structures-$(Get-Date -f yyyyMMdd-HHmmss)"; robocopy $fdx $bk /E /NFL /NDL /NJH /NJS /NP | Out-Null
foreach($p in $plano){ if(-not $p.OrigemOk){ throw "receita nao encontrada: $($p.Origem)" }
    $dst="$T\$($p.Recipe.Substring(0,2))\$($p.Recipe.Substring(2))"
    if(Test-Path $dst){ robocopy $dst "$bk\_recipes\$($p.Recipe)" /E /NFL /NDL /NJH /NJS /NP | Out-Null } else { New-Item -ItemType Directory -Force $dst | Out-Null }
    Get-ChildItem $p.Origem -File | Copy-Item -Destination "$dst\" -Force
    & (Join-Path $PSScriptRoot 'Instalar-PecaFDX.ps1') -Recipe $p.Origem -Tabela (Join-Path $Tabelas "$($p.Peca).xml") -Nome $p.Peca }
"Instalado. Backup: $bk. No Civil: PARTCATALOGREGEN e Parts List."
