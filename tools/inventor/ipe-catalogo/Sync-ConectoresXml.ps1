# Copia a posicao atual (cm) de cada work point para o CenterPoint do conector no XML
# ContentBuilderAttributeSets da caixa e salva o documento.
param($Doc)
$ErrorActionPreference = 'Stop'
$def = $Doc.ComponentDefinition
$attr = $Doc.AttributeSets.Item('ContentBuilderAttributeSets').Item('ContentBuilderAttribute')
[xml]$x = $attr.Value
foreach ($c in $x.SelectNodes('//Connector')) {
    $wp = $def.WorkPoints.Item($c.WorkPointName); $cp = $c.CenterPoint
    $cp.x = [string][math]::Round($wp.Point.X, 6); $cp.y = [string][math]::Round($wp.Point.Y, 6); $cp.z = [string][math]::Round($wp.Point.Z, 6)
}
$sw = New-Object IO.StringWriter; $xs = New-Object Xml.XmlWriterSettings; $xs.Indent = $true
$xw = [Xml.XmlWriter]::Create($sw, $xs); $x.Save($xw); $xw.Close()
$attr.Value = $sw.ToString().Replace(' encoding="utf-16"', '')
$Doc.Save()
