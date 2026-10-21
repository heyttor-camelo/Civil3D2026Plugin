<#
.SYNOPSIS
  Cria um catálogo de peças NOVO juntando dois catálogos existentes do Civil 3D (Part Builder):
  um catálogo base (ex.: Country Kit "BRA Metric Structures") mais um catálogo adicional
  (ex.: CSBIM saneado). Os originais não são tocados.

.DESCRIPTION
  - Copia o catálogo base inteiro para a pasta nova e renomeia .apc/.htm para o nome novo.
  - Copia as famílias do catálogo adicional SEM sobrescrever arquivos que já existam.
  - Mescla o índice .apc: capítulos novos são acrescentados; num capítulo que exista nos dois,
    entram só as peças cujo id ainda não exista. Os conjuntos de tipos/subtipos (regras do
    catálogo) viram a união dos dois.
  - O catálogo novo ganha id (GUID) e nome próprios.
  - Confere no fim que todo TableRef do .apc aponta para um .xml existente.
  Depois, no Civil 3D: PARTCATALOGREGEN e SETPIPENETWORKCATALOG apontando para a pasta nova.

.EXAMPLE
  .\Merge-PartCatalogs.ps1 -Base "BRA Metric Structures" -Add "U3929348-IRIWUEH-393J4J2-DIJFI" -Target "FDX Metric Structures" -Prefix "CSBIM-" -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Base,
    [Parameter(Mandatory)][string]$Add,
    [Parameter(Mandatory)][string]$Target,
    [string]$Root = "$env:ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog",
    [string]$Description,
    # Prefixo para os capítulos vindos do catálogo adicional (vazio = manter o nome)
    [string]$Prefix = '',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
function Rewrite-Href($node, [string]$from, [string]$to) {
    foreach ($u in $node.SelectNodes('.//URL')) {
        $h = $u.GetAttribute('href', 'http://www.w3.org/1999/xlink')
        if ($h -and $h.StartsWith("$from")) { $u.SetAttribute('href', 'http://www.w3.org/1999/xlink', $to + $h.Substring($from.Length)) | Out-Null }
    }
}
function Log([string]$t) { Write-Host $t }
function Resolve-Catalog([string]$name) {
    $p = if (Test-Path $name) { (Resolve-Path $name).Path } else { Join-Path $Root $name }
    if (-not (Test-Path $p)) { throw "catálogo não encontrado: $p" }
    $apc = Get-ChildItem $p -Filter *.apc | Where-Object { $_.BaseName -eq (Split-Path $p -Leaf) } | Select-Object -First 1
    if (-not $apc) { $apc = Get-ChildItem $p -Filter *.apc | Select-Object -First 1 }
    if (-not $apc) { throw "sem .apc em $p" }
    [pscustomobject]@{ Path = $p; Apc = $apc.FullName; Name = Split-Path $p -Leaf }
}

$b = Resolve-Catalog $Base
$a = Resolve-Catalog $Add
$t = Join-Path $Root $Target
if (Test-Path $t) { throw "a pasta de destino já existe: $t (apague-a ou escolha outro nome)" }
Log "base:     $($b.Path)"
Log "adicional: $($a.Path)"
Log "novo:     $t"

# ---------- índice .apc
$bx = New-Object System.Xml.XmlDocument; $bx.PreserveWhitespace = $true; $bx.Load($b.Apc)
$ax = New-Object System.Xml.XmlDocument; $ax.PreserveWhitespace = $true; $ax.Load($a.Apc)
$doc = $bx.DocumentElement
$ns = New-Object System.Xml.XmlNamespaceManager($bx.NameTable)

$existingIds = @{}
foreach ($p in $doc.SelectNodes('//Part')) { $existingIds[$p.GetAttribute('id')] = $p.GetAttribute('name') }
$chapters = @{}
foreach ($c in $doc.SelectNodes('/pXML/Chapter')) { $chapters[$c.GetAttribute('name')] = $c }

$added = 0; $skipped = 0; $newChapters = 0; $copyPlan = @()
foreach ($chapter in $ax.DocumentElement.SelectNodes('/pXML/Chapter')) {
    $srcName = $chapter.GetAttribute('name')
    $dstName = $Prefix + $srcName
    $parts = @($chapter.SelectNodes('Part'))
    $toAdd = @($parts | Where-Object { -not $existingIds.ContainsKey($_.GetAttribute('id')) })
    $dup = $parts.Count - $toAdd.Count
    if ($dup -gt 0) { Log ("  {0}: {1} peça(s) já existem no base (mantidas as do base): {2}" -f $srcName, $dup, (($parts | Where-Object { $existingIds.ContainsKey($_.GetAttribute('id')) } | ForEach-Object { $_.GetAttribute('name') }) -join ', ')) }
    $skipped += $dup
    if ($toAdd.Count -eq 0) { continue }

    # os hrefs do capítulo apontam para "<pasta>\arquivo"; com prefixo a pasta muda de nome
    $copyPlan += [pscustomobject]@{ Src = Join-Path $a.Path $srcName; Dst = Join-Path $t $dstName; Parts = @($toAdd | ForEach-Object { $_.GetAttribute('name') }) }

    if ($chapters.ContainsKey($dstName)) {
        $target = $chapters[$dstName]
        foreach ($p in $toAdd) {
            $imp = $bx.ImportNode($p, $true)
            if ($Prefix) { Rewrite-Href $imp $srcName $dstName }
            $target.AppendChild($imp) | Out-Null
            $existingIds[$p.GetAttribute('id')] = $p.GetAttribute('name')
        }
        Log ("  {0}: +{1} peça(s) no capítulo existente" -f $dstName, $toAdd.Count)
    } else {
        $imp = $bx.ImportNode($chapter, $true)
        $imp.SetAttribute('name', $dstName)
        # texto solto e <Desc> do capítulo levam o nome
        foreach ($n in @($imp.ChildNodes)) { if ($n.NodeType -eq 'Text') { $n.Value = $dstName } }
        $d = $imp.SelectSingleNode('Desc'); if ($d) { $d.InnerText = $dstName }
        foreach ($p in @($imp.SelectNodes('Part'))) {
            if ($existingIds.ContainsKey($p.GetAttribute('id'))) { $imp.RemoveChild($p) | Out-Null; continue }
            if ($Prefix) { Rewrite-Href $p $srcName $dstName }
            $existingIds[$p.GetAttribute('id')] = $p.GetAttribute('name')
        }
        $doc.AppendChild($bx.CreateWhitespace("`r`n`t")) | Out-Null
        $doc.AppendChild($imp) | Out-Null
        $chapters[$dstName] = $imp
        $newChapters++
        Log ("  {0}: capítulo novo com {1} peça(s)" -f $dstName, $toAdd.Count)
    }
    $added += $toAdd.Count
}


# união das regras (tipos, subtipos, formas, conexões)
$unionCount = 0
foreach ($setName in 'TypeSet', 'SubTypeSet', 'ShapeSet', 'ConnectionTypeSet') {
    $bs = $doc.SelectSingleNode("//AeccCIRules/$setName"); $as = $ax.DocumentElement.SelectSingleNode("//AeccCIRules/$setName")
    if (-not $bs -or -not $as) { continue }
    $have = @($bs.ChildNodes | Where-Object { $_.NodeType -eq 'Element' } | ForEach-Object { $_.InnerText })
    foreach ($e in @($as.ChildNodes | Where-Object { $_.NodeType -eq 'Element' })) {
        if ($have -notcontains $e.InnerText) { $bs.AppendChild($bx.ImportNode($e, $true)) | Out-Null; $unionCount++; Log "  regra: $setName += $($e.InnerText)" }
    }
}
$bd = $doc.SelectSingleNode('//AeccCIRules/DomainHierarchy/Domain'); $ad = $ax.DocumentElement.SelectSingleNode('//AeccCIRules/DomainHierarchy/Domain')
if ($bd -and $ad) {
    $have = @($bd.SelectNodes('Type/TypeEnum') | ForEach-Object { $_.InnerText })
    foreach ($ty in @($ad.SelectNodes('Type'))) { if ($have -notcontains $ty.SelectSingleNode('TypeEnum').InnerText) { $bd.AppendChild($bx.ImportNode($ty, $true)) | Out-Null; $unionCount++ } }
}

# identidade do catálogo novo
$doc.SetAttribute('id', ([guid]::NewGuid().ToString().ToUpper()))
$doc.SetAttribute('name', $Target)
if (-not $Description) { $Description = "$Target = $($b.Name) + $($a.Name)" }
$desc = $doc.SelectSingleNode('/pXML/Desc'); if ($desc) { $desc.InnerText = $Description }

Log ("Resumo: {0} peça(s) acrescentada(s) em {1} capítulo(s) novo(s); {2} duplicada(s) ignorada(s); {3} regra(s) unida(s); total no novo: {4} peças." -f $added, $newChapters, $skipped, $unionCount, $existingIds.Count)
if (-not $Apply) { Log 'Simulação: nada criado. Rode com -Apply.'; return }

# ---------- arquivos
New-Item -ItemType Directory -Force $t | Out-Null
$null = robocopy $b.Path $t /E /XD _quarentena /XF desktop.ini /R:2 /W:2 /NFL /NDL /NJH /NJS /NP
if ($LASTEXITCODE -ge 8) { throw "robocopy falhou ao copiar o base (código $LASTEXITCODE)" }
Remove-Item (Join-Path $t (Split-Path $b.Apc -Leaf)) -Force
$oldHtm = Join-Path $t ($b.Name + '.htm')
if (Test-Path $oldHtm) {
    (Get-Content $oldHtm -Raw).Replace($b.Name + '.apc', $Target + '.apc').Replace($b.Name, $Target) | Set-Content (Join-Path $t ($Target + '.htm')) -Encoding UTF8
    Remove-Item $oldHtm -Force
}
foreach ($cp in $copyPlan) {
    $null = robocopy $cp.Src $cp.Dst /E /XC /XN /XO /XF desktop.ini /R:2 /W:2 /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -ge 8) { throw "robocopy falhou em $($cp.Src) (código $LASTEXITCODE)" }
}
$settings = New-Object System.Xml.XmlWriterSettings; $settings.Encoding = New-Object System.Text.UTF8Encoding($false); $settings.Indent = $false
$w = [System.Xml.XmlWriter]::Create((Join-Path $t ($Target + '.apc')), $settings); try { $bx.Save($w) } finally { $w.Close() }

# ---------- conferência
$missing = @()
foreach ($u in $bx.SelectNodes('//TableRef/URL')) {
    $h = $u.GetAttribute('href', 'http://www.w3.org/1999/xlink')
    if (-not (Test-Path (Join-Path $t $h))) { $missing += $h }
}
$ids = @($bx.SelectNodes('//Part') | ForEach-Object { $_.GetAttribute('id') })
$dupIds = @($ids | Group-Object | Where-Object Count -gt 1)
Log ("Criado: {0}  ({1} peças no índice; {2} referência(s) quebrada(s); {3} id(s) duplicado(s))" -f $t, $ids.Count, $missing.Count, $dupIds.Count)
foreach ($m in $missing) { Log "  FALTA: $m" }
Log 'Agora, no Civil 3D: PARTCATALOGREGEN e SETPIPENETWORKCATALOG apontando para a pasta nova.'
