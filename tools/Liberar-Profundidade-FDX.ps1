# Libera a profundidade (SRS, StructRimToSumpHeight) nas familias de estrutura do FDX Metric Structures:
#  - SRS calculado a partir de uma coluna (ex. DER/SP: SRS = $ACX - $FTh): inverte a dependencia. O SRS vira
#    faixa livre (padrao = valor atual) e a coluna que mandava vira calculo a partir dele (ACX = $SRS + $FTh).
#    So para formulas lineares que comecam pela coluna de entrada ($A, $A-$B, $A+$B+1200, $A+2*$B...);
#    as outras ficam como estao e aparecem na lista "nao tratadas".
#  - SRS por linha (Column) ou lista: vira faixa livre, com o valor da primeira linha como padrao.
#  - Medidas de projeto em lista (ColumnConstList: comprimento, largura, diametro, parede...): viram faixa livre.
#  - Familias sem SRS (alas, bocas, cabeceiras, descidas, dissipadores): ficam como estao (altura fixa ou
#    dada por outra medida); sao listadas no fim.
# Faixa livre = 1 .. 100000 mm. Mostra a previa, pede confirmacao (ou -Sim) e faz backup. Civil 3D FECHADO.
# Depois, no Civil: PARTCATALOGREGEN e HYTROCAR (mesma familia) nas pecas ja inseridas.
param([switch]$SoPrevia, [switch]$Sim)
$ErrorActionPreference = 'Stop'
$cat = 'C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog\FDX Metric Structures'
$inv = [Globalization.CultureInfo]::InvariantCulture
$projeto = '^(StructInner\w*|Wall\w*|Floor\w*|StructSlab\w*|StructFrame(Height|Diameter|Width|Length)|StructConeHeight|StructBarrelHeight|StructHeight|StructLength|StructWidth|StructDiameter|Struct[A-Z]\w*|Geometry_Model(Distance)?Parameter)$'
$fora = '^(StructBoundingShape|StructFrame|StructGrate|StructCover|StructBenchType|StructVertPipeClearance|StructBarrelPipeClearance)$'

if (-not $SoPrevia -and (Get-Process acad -ErrorAction SilentlyContinue)) { throw 'Feche o Civil 3D antes (ou rode com -SoPrevia para so ver).' }

function Fator($unit) { switch -regex (($unit + '').ToLowerInvariant()) { '^mm' { 1 } '^cm' { 0.1 } '^(m|meter|meters)$' { 0.001 } default { 1 } } }
function Txt([double]$v) { $v.ToString('0.######', $inv) }
function Primeiro($n) {
    switch ($n.LocalName) {
        'ColumnRangeList' { $d = $n.SelectSingleNode("Item[@rangeVal='Default']"); if ($d) { return $d.InnerText } }
        'ColumnConstList' { $i = $n.SelectSingleNode('Item'); if ($i) { return $i.InnerText } }
        'Column' { $r = $n.SelectSingleNode('Row'); if ($r) { return $r.InnerText } }
        'ColumnConst' { return $n.InnerText }
    }
    $null
}
function ParaFaixa($x, $n, [string]$padrao) {
    $k = Fator $n.GetAttribute('unit'); $d = [double]::Parse($padrao, $inv)
    $r = $x.CreateElement('ColumnRangeList')
    foreach ($a in $n.Attributes) { $r.SetAttribute($a.Name, $a.Value) }
    $i = 0
    foreach ($p in @(@('Min', (Txt ([math]::Min(1 * $k, $d)))), @('Max', (Txt ([math]::Max(100000 * $k, $d)))), @('Default', (Txt $d)))) {
        $it = $x.CreateElement('Item'); $it.SetAttribute('id', "i$i"); $it.SetAttribute('rangeVal', $p[0]); $it.InnerText = $p[1]; $null = $r.AppendChild($it); $i++
    }
    $null = $n.ParentNode.ReplaceChild($r, $n)
}
# Valor padrao de uma formula simples ($A, numeros, + - *), com os padroes das colunas de entrada.
function Avalia($x, [string]$expr) {
    $e = [regex]::Replace($expr, '\$(\w+)', {
        param($m) $c = $x.DocumentElement.ChildNodes | ? { $_.NodeType -eq 'Element' -and $_.GetAttribute('name') -eq $m.Groups[1].Value } | Select -First 1
        if (-not $c) { throw "coluna $($m.Groups[1].Value)" }
        if ($c.LocalName -eq 'ColumnCalc') { "(" + (Avalia $x $c.InnerText) + ")" } else { (Primeiro $c) } })
    if ($e -notmatch '^[\d\.\s\+\-\*/\(\)]+$') { throw "formula $expr" }
    [double](New-Object Data.DataTable).Compute($e, '')
}

$mudas = @(); $semSrs = @(); $naoTratadas = @()
foreach ($f in Get-ChildItem $cat -Recurse -Filter *.xml -File) {
    if ($f.Name -like 'Aecc*Shared*') { continue }
    $x = New-Object Xml.XmlDocument; $x.PreserveWhitespace = $true
    try { $x.Load($f.FullName) } catch { continue }
    $lp = $x.DocumentElement; if (-not $lp -or $lp.LocalName -ne 'LandPart') { continue }
    $rel = $f.FullName.Substring($cat.Length + 1); $acoes = @()
    $els = @($lp.ChildNodes | ? { $_.NodeType -eq 'Element' })
    $srs = $els | ? { $_.GetAttribute('context') -eq 'StructRimToSumpHeight' } | Select -First 1
    if (-not $srs) { $semSrs += $rel }
    elseif ($srs.LocalName -in 'Column', 'ColumnConstList', 'ColumnConst') {
        $p = Primeiro $srs; if ($p) { $acoes += [pscustomobject]@{ T = 'faixa'; N = $srs; Padrao = $p; Txt = "SRS ($($srs.LocalName)) -> faixa livre (padrao $p)" } }
    }
    elseif ($srs.LocalName -eq 'ColumnCalc') {
        $expr = $srs.InnerText.Trim(); $m = [regex]::Match($expr, '^\$(\w+)(.*)$')
        $ok = $false
        if ($m.Success) {
            $aNome = $m.Groups[1].Value; $resto = $m.Groups[2].Value.Trim()
            $a = $els | ? { $_.GetAttribute('name') -eq $aNome } | Select -First 1
            $linear = ($resto -eq '' -or $resto -match '^[\+\-]') -and ($resto -notmatch '[\*/]\s*\$' + $aNome) -and ($resto -notmatch "\`$$aNome\b")
            if ($a -and $a.LocalName -ne 'ColumnCalc' -and $linear -and $resto -notmatch '^\s*[\*/]') {
                try {
                    $padrao = Txt (Avalia $x $expr)
                    # "$SRS - (0-$FTh)": sem menos unario dentro dos parenteses (o avaliador do Part Builder pode nao aceitar).
                    $nova = if ($resto -eq '') { '$' + $srs.GetAttribute('name') } else { '$' + $srs.GetAttribute('name') + ' - (0' + $resto + ')' }
                    $acoes += [pscustomobject]@{ T = 'inverte'; N = $srs; A = $a; Padrao = $padrao; Nova = $nova; Txt = "SRS = $expr  ->  SRS faixa livre (padrao $padrao); $aNome = $nova" }
                    $ok = $true
                } catch { }
            }
        }
        if (-not $ok) { $naoTratadas += "$rel  (SRS = $expr)" }
    }
    foreach ($n in $els | ? { $_.LocalName -eq 'ColumnConstList' -and $_.GetAttribute('context') -match $projeto -and $_.GetAttribute('context') -notmatch $fora -and $_.GetAttribute('dataType') -match '^(Float|Double)$' -and $_.GetAttribute('visible') -ne '0' }) {
        if ($srs -and $n -eq $srs) { continue }
        $p = Primeiro $n; if ($p) { $acoes += [pscustomobject]@{ T = 'faixa'; N = $n; Padrao = $p; Txt = "$($n.GetAttribute('name')) (lista) -> faixa livre (padrao $p)" } }
    }
    if ($acoes.Count -gt 0) { $mudas += [pscustomobject]@{ Arq = $f.FullName; Rel = $rel; Doc = $x; Acoes = $acoes } }
}

foreach ($m in $mudas) { "`n$($m.Rel)"; foreach ($a in $m.Acoes) { "   $($a.Txt)" } }
"`n$($mudas.Count) familia(s) mudam; $(($mudas | % { $_.Acoes.Count } | Measure-Object -Sum).Sum) alteracao(oes)."
if ($naoTratadas.Count) { "`nSRS calculado por formula que nao da para inverter com seguranca ($($naoTratadas.Count)), ficam como estao:"; $naoTratadas | % { "   $_" } }
"`nSem SRS ($($semSrs.Count)): alas, bocas, cabeceiras, descidas e dissipadores - a altura e fixa ou dada por outra medida; ficam como estao."
if ($SoPrevia) { 'So previa (-SoPrevia): nada foi gravado.'; return }
if (-not $Sim) { $r = Read-Host 'Aplicar? [S/N]'; if ($r -notmatch '^\s*[sS]') { "Cancelado, nada foi gravado (resposta: '$r'). Para aplicar sem pergunta, rode com -Sim."; return } }

$bk = "C:\Temp\_arquivo-catalogo-ipe-20261028\backup-profundidade-$(Get-Date -f yyyyMMdd-HHmmss)"
robocopy $cat "$bk\FDX Metric Structures" /E /NFL /NDL /NJH /NJS /NP | Out-Null
foreach ($m in $mudas) {
    $x = $m.Doc
    foreach ($a in $m.Acoes) {
        if ($a.T -eq 'inverte') {
            # A coluna de entrada vira calculo a partir do SRS (no fim, depois do SRS); o SRS vira faixa.
            $calc = $x.CreateElement('ColumnCalc'); foreach ($at in $a.A.Attributes) { $calc.SetAttribute($at.Name, $at.Value) }
            $calc.InnerText = $a.Nova
            $null = $a.A.ParentNode.RemoveChild($a.A); $null = $x.DocumentElement.AppendChild($calc)
            ParaFaixa $x $a.N $a.Padrao
        }
        else { ParaFaixa $x $a.N $a.Padrao }
    }
    $x.Save($m.Arq)
}
"Gravado. Backup: $bk"
'Agora abra o Civil, rode PARTCATALOGREGEN e troque as pecas ja inseridas pela mesma familia (HYTROCAR).'
