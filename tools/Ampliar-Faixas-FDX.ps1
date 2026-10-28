# Libera as medidas das familias Part Builder do FDX (estruturas e tubos): toda faixa de medida passa a
# 1 mm .. 100000 mm, e medida de projeto que hoje e valor fixo vira faixa livre (o valor atual vira o padrao).
# Nao mexe em campos calculados, em listas, nas distancias internas do desenho (Geometry_Distance*) nem nos ocultos.
# Mostra a previa familia por familia e pede confirmacao. Faz backup antes. Rodar com o Civil 3D FECHADO.
# Depois, no Civil: PARTCATALOGREGEN. As pecas ja inseridas mantem os valores atuais.
param([switch]$SoPrevia, [switch]$Sim)   # -Sim aplica sem perguntar
$ErrorActionPreference = 'Stop'
$pc = 'C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog'
$cats = @('FDX Metric Structures', 'FDX Metric Pipes')
$minMm = 1; $maxMm = 100000
$inv = [Globalization.CultureInfo]::InvariantCulture

# Contextos que sao medida de projeto (o resto: catalogo, desenho interno, textos).
$projeto = '^(Struct(Inner|Rim|Slab|Frame|Cone|Barrel|Height|Length|Width|Diameter|Vert|Bar)\w*|Wall\w*|Floor\w*|Pipe\w*|Geometry_Model(Distance)?Parameter|Struct[A-Z]\w*)$'
$fora = '^(Catalog_\w*|DataVersion|Content_Type|CustomPartSizing_Flag|Geometry_Engine|Geometry_Distance\w*|BodyGeometry_\w*|StructBoundingShape|StructFrame|StructGrate|StructCover|StructBenchType|Material_Type)$'

function Fator($unit) { switch -regex (($unit + '').ToLowerInvariant()) { '^mm' { 1 } '^cm' { 0.1 } '^(m|meter|meters)$' { 0.001 } '^(in|inch)' { 1 / 25.4 } '^(ft|foot|feet)' { 1 / 304.8 } default { 1 } } }
function Txt([double]$v) { $v.ToString('0.######', $inv) }

if (Get-Process acad -ErrorAction SilentlyContinue) { if (-not $SoPrevia) { throw 'Feche o Civil 3D antes (ou rode com -SoPrevia para so ver).' } }

$mudancas = @()
foreach ($cat in $cats) {
    $root = Join-Path $pc $cat
    if (-not (Test-Path $root)) { continue }
    foreach ($f in Get-ChildItem $root -Recurse -Filter *.xml -File) {
        if ($f.Name -like 'Aecc*SharedPropertyLists*') { continue }
        $x = New-Object Xml.XmlDocument; $x.PreserveWhitespace = $true
        try { $x.Load($f.FullName) } catch { Write-Warning "Nao lido (pulado): $($f.FullName)"; continue }
        $lp = $x.DocumentElement
        if (-not $lp -or $lp.LocalName -ne 'LandPart') { continue }
        $itens = @()
        foreach ($n in @($lp.ChildNodes)) {
            if ($n.NodeType -ne 'Element') { continue }
            $ctx = $n.GetAttribute('context'); $nome = $n.GetAttribute('name')
            if (-not $nome -or $ctx -match $fora -or $ctx -notmatch $projeto) { continue }
            if ($n.GetAttribute('visible') -eq '0') { continue }
            if ($n.GetAttribute('dataType') -notmatch '^(Float|Double)$') { continue }
            $k = Fator $n.GetAttribute('unit'); $mn = Txt ($minMm * $k); $mx = Txt ($maxMm * $k)
            if ($n.LocalName -eq 'ColumnRangeList') {
                $iMin = $n.SelectSingleNode("Item[@rangeVal='Min']"); $iMax = $n.SelectSingleNode("Item[@rangeVal='Max']")
                if (-not $iMin -or -not $iMax) { continue }
                $a = "$($iMin.InnerText)..$($iMax.InnerText)"
                $novoMin = [math]::Min([double]::Parse($iMin.InnerText, $inv), $minMm * $k)
                $novoMax = [math]::Max([double]::Parse($iMax.InnerText, $inv), $maxMm * $k)
                if ($a -eq "$(Txt $novoMin)..$(Txt $novoMax)") { continue }
                $itens += [pscustomobject]@{ No = $n; Tipo = 'faixa'; Nome = $nome; Antes = "faixa $a"; Depois = "faixa $(Txt $novoMin)..$(Txt $novoMax)"; Min = (Txt $novoMin); Max = (Txt $novoMax) }
            }
            elseif ($n.LocalName -eq 'ColumnConst' -and $cat -like '*Structures') {
                $v = $n.InnerText.Trim(); $d = 0.0
                if (-not [double]::TryParse($v, [Globalization.NumberStyles]::Float, $inv, [ref]$d)) { continue }
                $mn2 = Txt ([math]::Min($minMm * $k, $d)); $mx2 = Txt ([math]::Max($maxMm * $k, $d))
                $itens += [pscustomobject]@{ No = $n; Tipo = 'fixo'; Nome = $nome; Antes = "fixo $v"; Depois = "faixa $mn2..$mx2 (padrao $v)"; Min = $mn2; Max = $mx2; Padrao = $v }
            }
        }
        if ($itens.Count -gt 0) { $mudancas += [pscustomobject]@{ Arquivo = $f.FullName; Rel = $f.FullName.Substring($pc.Length + 1); Doc = $x; Itens = $itens } }
    }
}

if ($mudancas.Count -eq 0) { 'Nada a mudar: as faixas ja estao livres.'; return }
foreach ($m in $mudancas) {
    "`n$($m.Rel)"
    foreach ($i in $m.Itens) { "   {0,-10} {1,-28} -> {2}" -f $i.Nome, $i.Antes, $i.Depois }
}
"`n$($mudancas.Count) familia(s), $(($mudancas | % { $_.Itens.Count } | Measure-Object -Sum).Sum) medida(s)."
"Obs.: o desenho (.dwg) de cada familia do Part Builder nao tem limite proprio; quem limita e a faixa da tabela. Medidas muito grandes ou pequenas podem gerar solido estranho se o modelo tiver distancias internas fixas."
if ($SoPrevia) { 'So previa (-SoPrevia): nada foi gravado.'; return }
if (-not $Sim) {
    $r = Read-Host 'Aplicar? [S/N]'
    if ($r -notmatch '^\s*[sS]') { "Cancelado, nada foi gravado (resposta: '$r'). Para aplicar sem pergunta, rode com -Sim."; return }
}

$bk = "C:\Temp\_arquivo-catalogo-ipe-20261028\backup-faixas-$(Get-Date -f yyyyMMdd-HHmmss)"
foreach ($cat in $cats) { if (Test-Path "$pc\$cat") { robocopy "$pc\$cat" "$bk\$cat" /E /NFL /NDL /NJH /NJS /NP | Out-Null } }
foreach ($m in $mudancas) {
    $x = $m.Doc
    foreach ($i in $m.Itens) {
        $n = $i.No
        if ($i.Tipo -eq 'faixa') {
            $n.SelectSingleNode("Item[@rangeVal='Min']").InnerText = $i.Min
            $n.SelectSingleNode("Item[@rangeVal='Max']").InnerText = $i.Max
        }
        else {
            $r2 = $x.CreateElement('ColumnRangeList')
            foreach ($a in $n.Attributes) { $r2.SetAttribute($a.Name, $a.Value) }
            $k = 0
            foreach ($p in @(@('Min', $i.Min), @('Max', $i.Max), @('Default', $i.Padrao))) {
                $it = $x.CreateElement('Item'); $it.SetAttribute('id', "i$k"); $it.SetAttribute('rangeVal', $p[0]); $it.InnerText = $p[1]; $null = $r2.AppendChild($it); $k++
            }
            $null = $n.ParentNode.ReplaceChild($r2, $n)
        }
    }
    $x.Save($m.Arquivo)
}
"Gravado. Backup: $bk"
'Agora abra o Civil e rode PARTCATALOGREGEN. Pecas novas ja vem com as faixas livres.'
