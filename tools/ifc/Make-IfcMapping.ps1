<#
.SYNOPSIS
  Gera a planilha de mapeamento IFC 4.3 da extensao IFC do Civil 3D 2026 com as classes das pecas do
  plugin (lombofaixa) e do corredor do Heyvid (code set "_PADRAO camelo"). Copia a planilha padrao da
  Autodesk e so muda/acrescenta linhas da aba Mapping (nada e apagado).

.USO
  tools\ifc\Make-IfcMapping.ps1 -Out 'G:\Meu Drive\Civil3D2026Plugin\ifc\IfcInfraExportMapping-IFC4X3_ADD2.xlsx'

  Para a extensao usar: copie para C:\ProgramData\Autodesk\Civil3D (vale para todos os desenhos) ou ao lado do
  DWG com o nome "<desenho>-IfcInfraExportMapping-IFC4X3_ADD2.xlsx" (so aquele desenho).
#>
param(
    [string]$Source = 'C:\Program Files\Autodesk\ApplicationPlugins\Civil3D_IFC-2026.bundle\Contents\Mapping\IfcInfraExportMapping-IFC4X3_ADD2.xlsx',
    [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# Mapping | Nome (sem diferenciar maiusculas, como a extensao) | Exportar | Classe IFC
$rows = @(
    # Pecas do plugin (por layer)
    @('LayerName', 'MF-TRAVESSIA-3D', $true, 'IfcPavement.RIGID'),
    @('LayerName', 'MF-TRAVESSIA-GRELHA', $true, 'IfcPlate.COVER_PLATE'),
    @('LayerName', 'MF-TRAVESSIA-TAMPA', $true, 'IfcPlate.COVER_PLATE'),
    @('LayerName', 'MF-TRAVESSIA-VIGA', $true, 'IfcFooting.FOOTING_BEAM'),
    @('LayerName', 'MF-TRAVESSIA-SINALIZACAO', $true, 'IfcSurfaceFeature.PAVEMENTSURFACEMARKING'),
    @('LayerName', 'MF-TRAVESSIA', $false, ''),
    @('LayerName', 'MF-REBAIXO', $false, ''),
    # Corredor: shapes (estilo de shape do code set)
    @('ShapeStyleName', 'GUIA', $true, 'IfcKerb'),
    @('ShapeStyleName', 'SARJETA', $true, 'IfcKerb'),
    @('ShapeStyleName', 'PAVIMENTO 1', $true, 'IfcPavement'),
    @('ShapeStyleName', 'PAVIMENTO 2', $true, 'IfcPavement'),
    @('ShapeStyleName', 'Ref_SubLeito', $true, 'IfcEarthworksFill.SUBGRADE'),
    @('ShapeStyleName', 'Basic', $false, ''),
    # Corredor: links sem volume (cascas por cima dos solidos): nao exportar
    @('LinkStyleName', 'BASICO', $false, ''),
    @('LinkStyleName', 'PISTA', $false, ''),
    @('LinkStyleName', 'SECAO DATUM', $false, ''),
    @('LinkStyleName', 'Uncoded', $false, '')
)

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
Copy-Item $Source $Out -Force
$zip = [System.IO.Compression.ZipFile]::Open($Out, 'Update')
try {
    $sr = New-Object IO.StreamReader($zip.GetEntry('xl/sharedStrings.xml').Open()); $shared = [xml]$sr.ReadToEnd(); $sr.Close()
    $script:strings = @($shared.sst.si | ForEach-Object { $_.InnerText })
    $entry = $zip.GetEntry('xl/worksheets/sheet1.xml')
    $reader = New-Object IO.StreamReader($entry.Open())
    $sheet = [xml]$reader.ReadToEnd()
    $reader.Close()
    $ns = New-Object Xml.XmlNamespaceManager($sheet.NameTable)
    $ns.AddNamespace('m', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
    $main = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
    $data = $sheet.SelectSingleNode('//m:sheetData', $ns)

    function CellText($c) { if ($null -eq $c) { return '' }; if ($c.t -eq 's') { return $script:strings[[int]$c.v] }; if ($c.t -eq 'inlineStr') { return $c.InnerText }; return [string]$c.v }
    function SetCell($row, $col, $kind, $value) {
        $ref = $col + $row.GetAttribute('r')
        $c = $row.SelectSingleNode("m:c[@r='$ref']", $ns)
        if ($null -eq $c) { $c = $sheet.CreateElement('c', $main); $c.SetAttribute('r', $ref); [void]$row.AppendChild($c) }
        $c.RemoveAll(); $c.SetAttribute('r', $ref); $c.SetAttribute('s', '2')
        if ($kind -eq 'b') { $c.SetAttribute('t', 'b'); $v = $sheet.CreateElement('v', $main); $v.InnerText = $(if ($value) { '1' } else { '0' }); [void]$c.AppendChild($v) }
        else {
            # Texto pela tabela compartilhada (o leitor da extensao ignora texto direto na celula).
            $idx = [Array]::IndexOf($script:strings, $value)
            if ($idx -lt 0) {
                $si = $shared.CreateElement('si', $main); $st = $shared.CreateElement('t', $main); $st.InnerText = $value
                [void]$si.AppendChild($st); [void]$shared.DocumentElement.AppendChild($si)
                $script:strings += $value; $idx = $script:strings.Count - 1
            }
            $c.SetAttribute('t', 's'); $v = $sheet.CreateElement('v', $main); $v.InnerText = [string]$idx; [void]$c.AppendChild($v)
        }
    }

    $existing = @{}
    foreach ($row in $data.SelectNodes('m:row', $ns)) {
        $a = CellText ($row.SelectSingleNode("m:c[starts-with(@r,'A')]", $ns))
        $b = CellText ($row.SelectSingleNode("m:c[starts-with(@r,'B')]", $ns))
        if ($a -and $b) { $existing[("$a|$b").ToLowerInvariant()] = $row }
    }
    # Linha livre depois da ultima com dados.
    $last = 0
    foreach ($row in $data.SelectNodes('m:row', $ns)) { if ($row.SelectSingleNode('m:c', $ns)) { $last = [Math]::Max($last, [int]$row.GetAttribute('r')) } }
    $changed = 0; $added = 0
    foreach ($r in $rows) {
        $key = ("$($r[0])|$($r[1])").ToLowerInvariant()
        if ($existing.ContainsKey($key)) { $row = $existing[$key]; $changed++ }
        else {
            $last++
            $row = $data.SelectSingleNode("m:row[@r='$last']", $ns)
            if ($null -eq $row) { $row = $sheet.CreateElement('row', $main); $row.SetAttribute('r', [string]$last); [void]$data.AppendChild($row) }
            SetCell $row 'A' 's' $r[0]
            SetCell $row 'B' 's' $r[1]
            $added++
        }
        SetCell $row 'C' 'b' $r[2]
        SetCell $row 'D' 's' $r[3]
    }
    $dim = $sheet.SelectSingleNode('//m:dimension', $ns)
    if ($dim) { $dim.SetAttribute('ref', "A1:H$([Math]::Max($last, 422))") }

    $shared.DocumentElement.SetAttribute('uniqueCount', [string]$script:strings.Count)
    $shared.DocumentElement.SetAttribute('count', [string]([int]$shared.DocumentElement.GetAttribute('count') + 64))
    $sEntry = $zip.GetEntry('xl/sharedStrings.xml'); $sEntry.Delete()
    $sw = New-Object IO.StreamWriter(($zip.CreateEntry('xl/sharedStrings.xml')).Open(), (New-Object Text.UTF8Encoding($false)))
    $shared.Save($sw); $sw.Close()
    $entry.Delete()
    $newEntry = $zip.CreateEntry('xl/worksheets/sheet1.xml')
    $w = New-Object IO.StreamWriter($newEntry.Open(), (New-Object Text.UTF8Encoding($false)))
    $sheet.Save($w); $w.Close()
    Write-Host "Mapeamento gerado: $Out ($changed linha(s) mudada(s), $added acrescentada(s))."
}
finally { $zip.Dispose() }
