<#
.SYNOPSIS
  Saneia o catálogo de peças CSBIM (Part Builder) do Civil 3D: medidas travadas viram faixas,
  contextos fora do significado viram parâmetros de modelo, listas são ordenadas, DNs faltantes
  são incluídos e arquivos órfãos vão para quarentena. Tudo com backup e relatório.

.DESCRIPTION
  O que faz, por arquivo .xml de família de peça:
  - LISTAS -> FAIXAS: parâmetros dimensionais (float, mm) definidos como lista fechada
    (ColumnConstList) passam a faixa (ColumnRangeList Min/Max/Default), que aceita qualquer
    valor. Ficam como lista: diâmetro de tubo (PipeInnerDiameter), diâmetro de tampão
    (StructFrameDiameter) e Manning, que são itens discretos.
  - CONTEXTOS (só peças Struct_InletOutlet, as bocas/alas): parâmetros de lista que usam
    contextos de poço de visita/cabeceira da Autodesk com outro significado ("espessura das
    paredes" em StructInnerWidth, "altura da parede menor" em StructVertPipeClearance...)
    passam a Geometry_ModelParameter, o contexto genérico de parâmetro de modelo. Só
    StructInnerDiameter é mantido. Listas ocultas com mais de um valor ficam visíveis.
  - LISTAS ORDENADAS e sem repetição (ex.: PV DNIT tinha 1000, 2000, 3000, 4000, 1200...).
  - DN FALTANTES: nas listas de PipeInnerDiameter entram os DN padrão (300..1200 de 100 em
    100) que caibam entre o menor e o maior valor existentes (ex.: 700 e 900).
  - QUARENTENA: *.bak, *.dwl, *.dwl2, *_recover.dwg, *.dwg sem .xml e TESTE_* vão para
    <catálogo>\_quarentena\<capítulo>\ (nada é apagado).
  Depois de aplicar, rode PARTCATALOGREGEN no Civil 3D para reindexar o catálogo (.apc).
  O modelo .dwg de cada peça não é alterado: só o .xml, que é o que o Civil 3D lê para
  oferecer tamanhos e parâmetros.

.PARAMETER Catalogs
  Pastas de catálogo a sanear. Padrão: os dois catálogos CSBIM instalados no Civil 3D 2026.
.PARAMETER Apply
  Sem -Apply o script só simula e imprime o plano.
.PARAMETER NoBackup
  Não gerar o .zip de backup (não recomendado).
#>
[CmdletBinding()]
param(
    [string[]]$Catalogs = @(
        "$env:ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog\U3929348-IRIWUEH-393J4J2-DIJFI",
        "$env:ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog\93920319-9R9OIJSD-OI3NJ3-IJFOE"
    ),
    [switch]$Apply,
    [switch]$NoBackup
)

$ErrorActionPreference = 'Stop'
$script:log = New-Object System.Collections.Generic.List[string]
$script:stats = @{ faixas = 0; contextos = 0; visiveis = 0; ordenadas = 0; dns = 0; quarentena = 0; arquivos = 0 }

# Contextos que continuam como lista (itens discretos de mercado).
$KeepAsList = @('PipeInnerDiameter', 'StructFrameDiameter', 'FlowAnalysis_Manning', 'PipeWallThickness')
# DNs padrão a completar nas listas de diâmetro de tubo (mm).
$StandardDn = @(300, 400, 500, 600, 700, 800, 900, 1000, 1200)
$ModelContext = 'Geometry_ModelParameter'

function Log([string]$text) { $script:log.Add($text); Write-Host $text }

function Format-Num([double]$v) { $v.ToString('0.0000', [Globalization.CultureInfo]::InvariantCulture) }

function Parse-Num([string]$s) { [double]::Parse($s.Trim(), [Globalization.CultureInfo]::InvariantCulture) }

function Round-Nice([double]$v, [int]$step) { [math]::Round($v / $step) * $step }

# Próximo id livre com o prefixo dado (CCR1, CCR2...) entre todos os ids do arquivo.
function New-FreeColumnId([xml]$xml, [string]$prefix) {
    $used = @{}
    foreach ($e in $xml.SelectNodes('//*[@id]')) { $used[$e.GetAttribute('id')] = $true }
    $n = 1
    while ($used.ContainsKey("$prefix$n")) { $n++ }
    return "$prefix$n"
}

# Converte uma ColumnConstList dimensional em ColumnRangeList.
function Convert-ToRange([xml]$xml, [System.Xml.XmlElement]$col, [double[]]$values, [string]$file) {
    $min = ($values | Measure-Object -Minimum).Minimum
    $max = ($values | Measure-Object -Maximum).Maximum
    $sorted = $values | Sort-Object
    $default = $sorted[[math]::Floor(($sorted.Count - 1) / 2)]
    if ($values.Count -eq 1) {
        $min = Round-Nice ($min * 0.5) 10
        $max = Round-Nice ($max * 2.0) 10
    } else {
        if ($min -gt 0) { $min = [math]::Max(0, (Round-Nice ($min * 0.5) 10)) }
        $max = Round-Nice ($max * 1.5) 10
    }

    $range = $xml.CreateElement('ColumnRangeList')
    foreach ($a in $col.Attributes) { $range.SetAttribute($a.Name, $a.Value) | Out-Null }
    # id CCR livre no arquivo: trocar CCLn por CCRn colidia com faixas já existentes (ex.: SIL
    # virava CCR1 igual ao SRS) e o Civil passava a ler/gravar a coluna errada.
    $range.SetAttribute('id', (New-FreeColumnId $xml 'CCR'))
    $i = 0
    foreach ($pair in @(@('Min', $min), @('Max', $max), @('Default', $default))) {
        $item = $xml.CreateElement('Item')
        $item.SetAttribute('id', "i$i")
        $item.SetAttribute('rangeVal', $pair[0])
        $item.InnerText = Format-Num $pair[1]
        $range.AppendChild($item) | Out-Null
        $i++
    }
    $col.ParentNode.ReplaceChild($range, $col) | Out-Null
    $script:stats.faixas++
    Log ("    faixa    {0,-8} '{1}': {2} -> {3}..{4} (padrão {5})" -f $col.GetAttribute('name'), $col.GetAttribute('desc'),
        (($sorted | ForEach-Object { [string][int]$_ }) -join ','), [int]$min, [int]$max, [int]$default)
    return $range
}

function Process-Xml([string]$file, [string]$catalog) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.PreserveWhitespace = $true
    $xml.Load($file)
    $root = $xml.DocumentElement
    if ($root.LocalName -ne 'LandPart') { return }

    $cols = @($root.ChildNodes | Where-Object { $_.NodeType -eq 'Element' -and $_.LocalName -like 'Column*' })
    $partType = ($cols | Where-Object { $_.GetAttribute('name') -eq 'PTyp' } | Select-Object -First 1).InnerText
    $isInletOutlet = $partType -eq 'Struct_InletOutlet'
    $changed = $false
    $rel = $file.Substring($catalog.Length + 1)
    Log "  $rel  [$partType]"

    foreach ($col in @($cols | Where-Object { $_.LocalName -eq 'ColumnConstList' })) {
        $name = $col.GetAttribute('name'); $ctx = $col.GetAttribute('context')
        $isFloat = $col.GetAttribute('dataType') -eq 'float'
        $items = @($col.ChildNodes | Where-Object { $_.NodeType -eq 'Element' -and $_.LocalName -eq 'Item' })
        if (-not $isFloat -or $items.Count -eq 0) { continue }
        $values = @($items | ForEach-Object { Parse-Num $_.InnerText })

        # 1. listas ordenadas e sem repetição (+ DNs faltantes para diâmetros de tubo)
        $wanted = @($values | Sort-Object -Unique)
        if ($ctx -eq 'PipeInnerDiameter') {
            $min = $wanted[0]; $max = $wanted[-1]
            $missing = @($StandardDn | Where-Object { $_ -ge $min -and $_ -le $max -and ($wanted -notcontains $_) })
            if ($missing.Count -gt 0) {
                $wanted = @(($wanted + $missing) | Sort-Object -Unique)
                $script:stats.dns += $missing.Count
                Log ("    DN       {0,-8} inclui {1}" -f $name, ($missing -join ', '))
            }
        }
        $same = ($wanted.Count -eq $values.Count)
        if ($same) { for ($k = 0; $k -lt $wanted.Count; $k++) { if ($wanted[$k] -ne $values[$k]) { $same = $false; break } } }
        if (-not $same) {
            foreach ($it in $items) { $col.RemoveChild($it) | Out-Null }
            # remove quebras de linha soltas que sobraram
            foreach ($ws in @($col.ChildNodes | Where-Object { $_.NodeType -eq 'Whitespace' })) { $col.RemoveChild($ws) | Out-Null }
            $k = 0
            foreach ($v in $wanted) {
                $col.AppendChild($xml.CreateWhitespace("`r`n  ")) | Out-Null
                $it = $xml.CreateElement('Item'); $it.SetAttribute('id', "i$k"); $it.InnerText = Format-Num $v
                $col.AppendChild($it) | Out-Null; $k++
            }
            $col.AppendChild($xml.CreateWhitespace("`r`n ")) | Out-Null
            if ($ctx -ne 'PipeInnerDiameter' -or $missing.Count -eq 0) { $script:stats.ordenadas++; Log ("    ordenada {0,-8} '{1}'" -f $name, $col.GetAttribute('desc')) }
            $changed = $true
            $values = $wanted
        }

        # 2. listas -> faixas (menos itens discretos). Lista de um valor só vira faixa quando é
        #    visível e diferente de zero; oculta com um valor é constante de projeto e fica como está.
        $single = $values.Count -eq 1
        $skip = $single -and ([math]::Abs($values[0]) -lt 1 -or $col.GetAttribute('visible') -ne '1')
        if (-not $skip -and $KeepAsList -notcontains $ctx -and $col.GetAttribute('unit') -eq 'mm') {
            $target = $col
            if ($isInletOutlet -and $ctx -ne 'StructInnerDiameter' -and $ctx -ne $ModelContext) {
                # 3. contexto fora do significado -> parâmetro de modelo
                Log ("    contexto {0,-8} '{1}': {2} -> {3}" -f $name, $col.GetAttribute('desc'), $ctx, $ModelContext)
                $col.SetAttribute('context', $ModelContext); $script:stats.contextos++
            }
            if ($col.GetAttribute('visible') -ne '1' -and $values.Count -gt 1) {
                Log ("    visível  {0,-8} '{1}' (era oculto com {2} opções)" -f $name, $col.GetAttribute('desc'), $values.Count)
                $col.SetAttribute('visible', '1'); $script:stats.visiveis++
            }
            Convert-ToRange $xml $col $values $file | Out-Null
            $changed = $true
        }
    }

    if ($changed) {
        $script:stats.arquivos++
        if ($Apply) {
            $settings = New-Object System.Xml.XmlWriterSettings
            $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
            $settings.Indent = $false
            $w = [System.Xml.XmlWriter]::Create($file, $settings)
            try { $xml.Save($w) } finally { $w.Close() }
        }
    }
}

function Quarantine([string]$catalog) {
    $q = Join-Path $catalog '_quarentena'
    $chapters = Get-ChildItem $catalog -Directory | Where-Object { $_.Name -notin 'Validate', '_quarentena' -and -not $_.Name.StartsWith('_') }
    foreach ($ch in $chapters) {
        $files = Get-ChildItem $ch.FullName -File | Where-Object {
            $_.Extension -in '.bak', '.dwl', '.dwl2' -or $_.Name -like '*.xml.bak' -or $_.Name -like '*_recover.dwg' -or $_.Name -like 'TESTE_*' -or
            ($_.Extension -eq '.dwg' -and -not (Test-Path (Join-Path $ch.FullName ($_.BaseName + '.xml'))))
        }
        foreach ($f in $files) {
            $script:stats.quarentena++
            Log "  quarentena: $($ch.Name)\$($f.Name)"
            if ($Apply) {
                $dest = Join-Path $q $ch.Name
                New-Item -ItemType Directory -Force $dest | Out-Null
                Move-Item $f.FullName (Join-Path $dest $f.Name) -Force
            }
        }
    }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
foreach ($catalog in $Catalogs) {
    if (-not (Test-Path $catalog)) { throw "catálogo não encontrado: $catalog" }
    $catalog = (Resolve-Path $catalog).Path
    Log "=== $catalog"
    if ($Apply -and -not $NoBackup) {
        $zip = Join-Path (Split-Path $catalog) ("_backup_" + (Split-Path $catalog -Leaf) + "_$stamp.zip")
        Compress-Archive -Path "$catalog\*" -DestinationPath $zip -CompressionLevel Optimal
        Log "  backup: $zip"
    }
    $xmls = Get-ChildItem $catalog -Recurse -Filter *.xml | Where-Object {
        $_.FullName -notmatch '\\_quarentena\\' -and $_.Name -notlike '*.bak' -and $_.Name -notin 'AecbIDrop.xml', 'AeccSharedPropertyLists.xml'
    }
    foreach ($x in $xmls) { Process-Xml $x.FullName $catalog }
    Quarantine $catalog
}

Log ''
Log ("Resumo: {0} arquivos alterados; {1} listas viraram faixas; {2} contextos corrigidos; {3} parâmetros expostos; {4} listas ordenadas; {5} DN incluídos; {6} arquivos em quarentena." -f
    $stats.arquivos, $stats.faixas, $stats.contextos, $stats.visiveis, $stats.ordenadas, $stats.dns, $stats.quarentena)
if ($Apply) {
    $report = Join-Path (Split-Path $Catalogs[0]) "_saneamento_CSBIM_$stamp.md"
    "# Saneamento do catálogo CSBIM — $stamp`r`n`r`n" + ($script:log -join "`r`n") | Set-Content $report -Encoding UTF8
    Log "Relatório: $report"
    Log 'Agora, no Civil 3D: comando PARTCATALOGREGEN (reindexa o catálogo) e teste uma boca e um PV.'
} else {
    Log 'Simulação: nada foi alterado. Rode com -Apply para aplicar (gera backup .zip e relatório).'
}
