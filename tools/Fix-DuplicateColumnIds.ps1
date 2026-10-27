<#
.SYNOPSIS
  Corrige ids de coluna duplicados nos .xml de um catálogo de peças (Part Builder) do Civil 3D.

.DESCRIPTION
  Cada coluna (ColumnRangeList, ColumnConstList, ColumnConst, ColumnCalc...) tem um id que
  precisa ser único no arquivo. Com id repetido (ex.: SIL e SRS ambos CCR1, efeito da conversão
  lista -> faixa antiga) o Civil 3D mapeia o campo errado: a altura tampa-fundo vai parar no
  comprimento interno, o Project Explorer acusa erro na junção etc.

  Por arquivo: acha os ids repetidos; o primeiro da repetição fica com o id, exceto quando um
  dos repetidos é o SRS (StructRimToSumpHeight), que mantém o id original do catálogo; os demais
  recebem o próximo id livre com o mesmo prefixo (CCR1 -> CCR5...). Só o atributo id muda: o
  resto do arquivo fica byte a byte igual (mesma codificação, BOM e quebras de linha). Os ids
  não são referenciados em nenhum outro lugar do .xml (fórmulas usam o name).

  Padrão: só relatório. Para gravar use -Apply; antes de gravar é feito um backup .zip da pasta
  do catálogo ao lado dela. -WhatIf força só relatório mesmo com -Apply.
  Depois de aplicar, rode PARTCATALOGREGEN no Civil 3D.

.PARAMETER Catalog
  Pasta do catálogo (ou de um capítulo). Padrão: FDX Metric Structures do Civil 3D 2026.
.PARAMETER Apply
  Grava as correções (com backup). Sem ele o script só lista o que mudaria.
.PARAMETER NoBackup
  Não gerar o .zip de backup (não recomendado).
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Catalog = "$env:ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog\FDX Metric Structures",
    [switch]$Apply,
    [switch]$NoBackup
)

$ErrorActionPreference = 'Stop'
$write = $Apply -and -not $WhatIfPreference

function Read-Text([string]$file) {
    $bytes = [IO.File]::ReadAllBytes($file)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $start = if ($bom) { 3 } else { 0 }
    try {
        $enc = New-Object System.Text.UTF8Encoding($bom, $true)
        $text = $enc.GetString($bytes, $start, $bytes.Length - $start)
    } catch {
        $enc = [Text.Encoding]::GetEncoding(1252)
        $text = $enc.GetString($bytes)
    }
    return @{ Text = $text; Encoding = $enc; Bom = $bom }
}

function Write-Text([string]$file, $doc, [string]$text) {
    $body = $doc.Encoding.GetBytes($text)
    if ($doc.Bom) { $body = [byte[]](0xEF, 0xBB, 0xBF) + $body }
    [IO.File]::WriteAllBytes($file, $body)
}

# Correções de um arquivo: lista de @{ Id; Occurrence; Name; Context; NewId }.
function Get-Fixes([string]$text) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.LoadXml($text)
    $cols = @($xml.DocumentElement.ChildNodes | Where-Object { $_.NodeType -eq 'Element' -and $_.HasAttribute('id') })
    $used = @{}
    foreach ($e in $xml.SelectNodes('//*[@id]')) { $used[$e.GetAttribute('id')] = $true }

    $fixes = @()
    foreach ($group in ($cols | Group-Object { $_.GetAttribute('id') } | Where-Object { $_.Count -gt 1 })) {
        $members = @($group.Group)
        $keep = @($members | Where-Object { $_.GetAttribute('context') -eq 'StructRimToSumpHeight' } | Select-Object -First 1)
        $keeper = if ($keep.Count -gt 0) { $keep[0] } else { $members[0] }
        $prefix = $group.Name -replace '\d+$', ''
        for ($k = 0; $k -lt $members.Count; $k++) {
            $m = $members[$k]
            if ([object]::ReferenceEquals($m, $keeper)) { continue }
            $n = 1
            while ($used.ContainsKey("$prefix$n")) { $n++ }
            $used["$prefix$n"] = $true
            $fixes += @{ Id = $group.Name; Occurrence = $k; Name = $m.GetAttribute('name'); Context = $m.GetAttribute('context'); NewId = "$prefix$n"
                Keeper = $keeper.GetAttribute('name') }
        }
    }
    return $fixes
}

# Troca a n-ésima ocorrência (0 = primeira) de id="X" no texto.
function Replace-Occurrence([string]$text, [string]$id, [int]$occurrence, [string]$newId) {
    $needle = 'id="' + $id + '"'
    $pos = -1
    for ($i = 0; $i -le $occurrence; $i++) {
        $pos = $text.IndexOf($needle, $pos + 1, [StringComparison]::Ordinal)
        if ($pos -lt 0) { throw "ocorrência $occurrence de $needle não encontrada" }
    }
    return $text.Substring(0, $pos) + 'id="' + $newId + '"' + $text.Substring($pos + $needle.Length)
}

if (-not (Test-Path $Catalog)) { throw "catálogo não encontrado: $Catalog" }
$Catalog = (Resolve-Path $Catalog).Path
Write-Host "=== $Catalog"

$xmls = Get-ChildItem $Catalog -Recurse -Filter *.xml | Where-Object {
    $_.FullName -notmatch '\\_quarentena\\' -and $_.Name -notin 'AecbIDrop.xml', 'AeccSharedPropertyLists.xml'
}

$plan = @()
foreach ($x in $xmls) {
    try {
        $doc = Read-Text $x.FullName
        if ($doc.Text -notmatch '<LandPart') { continue }
        $fixes = @(Get-Fixes $doc.Text)
    } catch {
        Write-Warning "$($x.FullName): não lido ($($_.Exception.Message))"
        continue
    }
    if ($fixes.Count -eq 0) { continue }
    $plan += @{ File = $x.FullName; Doc = $doc; Fixes = $fixes }
    Write-Host ('  ' + $x.FullName.Substring($Catalog.Length + 1))
    foreach ($f in $fixes) {
        Write-Host ("    {0,-6} {1,-8} {2,-28} -> {3}   (fica com {0}: {4})" -f $f.Id, $f.Name, $f.Context, $f.NewId, $f.Keeper)
    }
}

$total = ($plan | ForEach-Object { $_.Fixes.Count } | Measure-Object -Sum).Sum
Write-Host ''
Write-Host ("Resumo: {0} arquivos com id duplicado; {1} ids a renomear." -f $plan.Count, [int]$total)
if ($plan.Count -eq 0) { return }

if (-not $write) {
    Write-Host 'Só relatório: nada foi alterado. Rode com -Apply para gravar (gera backup .zip antes).'
    return
}

if (-not $NoBackup) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $zip = Join-Path (Split-Path $Catalog) ("_backup_" + (Split-Path $Catalog -Leaf) + "_ids_$stamp.zip")
    Compress-Archive -Path "$Catalog\*" -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Backup: $zip"
}

foreach ($p in $plan) {
    $text = $p.Doc.Text
    # de trás para frente: renomear uma ocorrência não muda a contagem das anteriores
    foreach ($f in ($p.Fixes | Sort-Object { $_.Occurrence } -Descending)) {
        $text = Replace-Occurrence $text $f.Id $f.Occurrence $f.NewId
    }
    if (@(Get-Fixes $text).Count -gt 0) { throw "$($p.File): ainda há id duplicado depois da correção" }
    Write-Text $p.File $p.Doc $text
}
Write-Host "Gravado. Agora, no Civil 3D: PARTCATALOGREGEN."
