<#
.SYNOPSIS
  Lista, por família, as medidas que o PartData expõe (nome interno, descrição, contexto, unidade,
  faixa ou lista) a partir dos .xml dos catálogos de estruturas. Só leitura.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Export-PartData.ps1 -Out docs\HYDRO-PARTDATA.md
.EXAMPLE
  ... -Catalogs 'FDX Metric Structures' -All   (-All inclui as distâncias/diâmetros ocultos do modelo)
#>
param([string]$Root = 'C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog', [string]$Out = 'docs\HYDRO-PARTDATA.md',
      [string[]]$Catalogs = @('U3929348-IRIWUEH-393J4J2-DIJFI', 'Metric Structures'),
      [switch]$All)
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture

function Load-Xml($path) {
    $doc = New-Object Xml.XmlDocument
    try { $doc.Load($path) } catch {
        $txt = [IO.File]::ReadAllText($path, [Text.Encoding]::GetEncoding(1252))
        $doc.LoadXml(($txt -replace '^<\?xml[^>]*\?>', ''))
    }
    $doc
}
function Num($s) { $d = 0.0; if ([double]::TryParse($s, [Globalization.NumberStyles]::Float, $inv, [ref]$d)) { $d } else { $null } }
function Fmt($s) { $d = Num $s; if ($null -eq $d) { $s } else { $d.ToString('0.####', $inv) } }
function Esc($s) { ($s -replace '\|', '\|' -replace '\r?\n', ' ').Trim() }

# contextos ocultos so de desenho (resumidos por contagem)
$geomCtx = '^(Geometry_Distance2D|Geometry_Distance3D|BodyGeometry_Diameter|Geometry_Angle.*)$'

$sb = New-Object Text.StringBuilder
function W($line = '') { [void]$sb.AppendLine($line) }

function Family($xmlPath, $catDir) {
    $doc = Load-Xml $xmlPath
    $lp = $doc.LandPart
    $cols = @($lp.ChildNodes | Where-Object { $_.NodeType -eq 'Element' -and $_.LocalName -like 'Column*' -and $_.LocalName -ne 'ColumnConstView' })
    $byCtx = @{}; foreach ($c in $cols) { if ($c.context) { $byCtx[$c.context] = $c } }
    $val = { param($ctx) if ($byCtx[$ctx]) { $byCtx[$ctx].InnerText.Trim() } else { '' } }
    $rel = $xmlPath.Substring($catDir.Length + 1)
    $name = & $val 'Catalog_PartName'; if (-not $name) { $name = [IO.Path]::GetFileNameWithoutExtension($xmlPath) }
    $hasDwg = Test-Path ($xmlPath -replace '\.xml$', '.dwg')

    W "#### $name"
    W ''
    W ("Arquivo ``{0}`` · tipo ``{1}`` / subtipo ``{2}`` · desc. ""{3}"" · PartID ``{4}`` · forma ``{5}``{6}" -f $rel, (& $val 'Catalog_PartType'), (& $val 'Catalog_SubType'), (Esc (& $val 'Catalog_PartDesc')), (& $val 'Catalog_PartID'), (& $val 'StructBoundingShape'), $(if ($hasDwg) { '' } else { ' · **sem .dwg**' }))
    $sn = $byCtx['Catalog_PartSizeName']; if ($sn) { W ''; W ("Nome do tamanho: ``{0}``" -f (Esc $sn.InnerText)) }
    W ''
    W '| Nome | Descrição | Contexto | Origem | Unid. | Valores / faixa | Vis. |'
    W '|---|---|---|---|---|---|---|'
    $hidden = 0
    $rank = @{ ColumnRangeList = 0; ColumnConstList = 1; ColumnConst = 2; ColumnCalc = 3 }
    $rows = $cols | Where-Object { $_.context -notlike 'Catalog_*' -and $_.context -notin 'DataVersion', 'Content_Type', 'CustomPartSizing_Flag', 'StructBoundingShape' } |
        Sort-Object { $rank[$_.LocalName] }, { $_.name }
    foreach ($c in $rows) {
        $kind = $c.LocalName; $vis = $c.visible -eq '1'
        if (-not $All -and -not $vis -and $c.context -match $geomCtx) { $hidden++; continue }
        switch ($kind) {
            'ColumnRangeList' {
                $it = @{}; foreach ($i in $c.Item) { $it[$i.rangeVal] = Fmt $i.InnerText }
                $v = "faixa $($it['Min'])..$($it['Max']) (padrão $($it['Default']))"; $o = 'faixa' }
            'ColumnConstList' {
                $raw = @($c.Item | ForEach-Object { $_.InnerText.Trim() })
                $nums = @($raw | ForEach-Object { Num $_ })
                if ($nums -notcontains $null) {
                    $u = @($nums | Sort-Object -Unique)
                    $v = 'lista: ' + (($u | ForEach-Object { $_.ToString('0.####', $inv) }) -join ', ')
                    if ($u.Count -ne $raw.Count) { $v += " ($($raw.Count - $u.Count) repetido(s))" }
                    elseif (($raw -join '|') -ne (($u | ForEach-Object { $raw[[array]::IndexOf($nums, $_)] }) -join '|')) { $v += ' (fora de ordem no XML)' }
                } else { $v = 'lista: ' + ($raw -join ', ') }
                $o = 'lista' }
            'ColumnConst' { $v = Fmt $c.InnerText; $o = 'const' }
            'ColumnCalc'  { $v = '`' + (Esc $c.InnerText) + '`'; $o = 'calc' }
        }
        W ("| ``{0}`` | {1} | {2} | {3} | {4} | {5} | {6} |" -f $c.name, (Esc $c.desc), $c.context, $o, $c.unit, (Esc $v), $(if ($vis) { 'sim' } else { 'não' }))
    }
    W ''
    if ($hidden) { W "_+ $hidden coluna(s) oculta(s) de geometria do modelo (Geometry_Distance2D/3D, BodyGeometry_Diameter) omitidas._"; W '' }
}

function Catalog($folder, $title) {
    $dir = Join-Path $Root $folder
    $apc = Get-ChildItem $dir -Filter *.apc | Select-Object -First 1
    $a = (Load-Xml $apc.FullName).pXML
    W "## $title"
    W ''
    W ("Pasta ``{0}`` · .apc: name=""{1}"", id ``{2}``, Desc ""{3}"", .apc de {4:yyyy-MM-dd}" -f $folder, $a.name, $a.id, $a.Desc, $apc.LastWriteTime)
    W ''
    $files = Get-ChildItem $dir -Recurse -Filter *.xml | Where-Object { $_.DirectoryName -ne $dir -and $_.Name -notmatch '\.bak' -and $_.FullName -notmatch '\\(_quarentena|Validate)\\' } | Sort-Object FullName
    foreach ($g in ($files | Group-Object { $_.Directory.Name })) {
        W "### Capítulo $($g.Name) ($($g.Count) família(s))"
        W ''
        foreach ($f in $g.Group) { Family $f.FullName $dir }
    }
}

W '# HYDRO — medidas expostas pelo PartData (catálogos de estruturas)'
W ''
W ('Gerado em {0:yyyy-MM-dd HH:mm} a partir dos `.xml` das famílias em `{1}` (máquina {2}). Só leitura: nada no catálogo nem no plugin foi alterado.' -f (Get-Date), $Root, $env:COMPUTERNAME)
W ''
W '## Como o XML vira PartData'
W ''
W 'Cada coluna do `.xml` da família (Part Builder) vira um `PartDataField` em `Part.PartData.GetAllDataFields()`:'
W ''
W '| XML | `PartDataField` |'
W '|---|---|'
W '| atributo `name` (SIL, SIW, AC...) | `Name` (nome interno; `$SIL` nas fórmulas) |'
W '| atributo `desc` | `Description` |'
W '| atributo `context` | `ContextString` / `Context` (`PartContextType`); `GetDataFieldBy(ctx)` busca por ele |'
W '| atributo `unit` | `Units` |'
W '| `ColumnRangeList` (Min/Max/Default) | `IsFromRange = true`, `ValueRange.RangeMin/RangeMax` — aceita qualquer valor na faixa |'
W '| `ColumnConstList` (Itens) | `IsFromList = true` — só os valores da lista (cada tamanho da parts list escolhe um) |'
W '| `ColumnConst` | valor fixo da família |'
W '| `ColumnCalc` | calculado pela fórmula a partir dos outros campos |'
W ''
W ('O HYDRO (`HydroReader`) mostra/edita só os campos com `IsFromRange` ou `IsFromList`; as tabelas abaixo destacam esses na coluna "Origem" (`faixa`/`lista`). Colunas de catálogo (`Catalog_*`, versão, flags) são omitidas' + $(if ($All) { '; todas as outras aparecem, inclusive as distâncias e diâmetros ocultos usados para desenhar o modelo (`Geometry_Distance2D/3D`, `BodyGeometry_Diameter`).' } else { ', assim como as distâncias ocultas usadas só para desenhar o modelo; os `Geometry_ModelParameter` ocultos ficam porque guardam o valor de referência do `.dwg` do modelo.' }))
W ''
if (-not (Test-Path (Join-Path $Root 'FDX Metric Structures'))) {
    W ('> **O catálogo "FDX Metric Structures" não está nesta máquina ({0}).** Ele é montado por `tools/Merge-PartCatalogs.ps1` (Country Kit BRA + CSBIM saneado, capítulos `CSBIM-*`; ver `DIARIO.md` 2026-10-21) e fica na máquina do trabalho e no Drive (`1 FDX ENGENHARIA\...\FDX-Catalogo-2026-10-21`). No FDX as famílias CSBIM são as mesmas (`CSBIM-ALAS\BDCC` = `ALAS\BDCC`, `CSBIM-DRENAGEM\PV DNIT` = `DRENAGEM\PV DNIT`), mas já saneadas: as listas dimensionais viram faixas. Para listar o FDX: `tools\Export-PartData.ps1 -Catalogs ''FDX Metric Structures''` numa máquina com ele instalado.' -f $env:COMPUTERNAME)
    W ''
}
if ($Catalogs -contains 'U3929348-IRIWUEH-393J4J2-DIJFI' -and -not (Get-ChildItem $Root -Filter '_saneamento_CSBIM_*.md' -ErrorAction SilentlyContinue)) {
    W '> **O CSBIM desta máquina não está saneado**: não há `_saneamento_CSBIM_*.md` na pasta `Pipes Catalog` e as medidas dimensionais ainda são listas (ex.: SIL/SIW/AC do `PV DNIT`). As tabelas mostram o estado atual, antes do `tools/Sanitize-CsbimCatalog.ps1 -Apply`; depois do saneamento as listas dimensionais viram faixas (ver `HYDRO-CATALOGO.md`).'
    W ''
}

$titles = @{ 'U3929348-IRIWUEH-393J4J2-DIJFI' = 'CSBIM — estruturas'; 'Metric Structures' = 'Autodesk — Metric Structures' }
foreach ($c in $Catalogs) { Catalog $c $(if ($titles[$c]) { $titles[$c] } else { $c }) }

$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out), $sb.ToString(), $utf8)
"ok: $Out"
