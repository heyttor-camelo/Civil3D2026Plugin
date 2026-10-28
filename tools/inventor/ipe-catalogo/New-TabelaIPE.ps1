# Gera a tabela (.xml + .jpg) de uma peca IPE a partir de uma tabela Autodesk do mesmo recipe base.
# Fica so a primeira linha; -Valores troca valores de Column, -Calc transforma Column em ColumnCalc.
param([Parameter(Mandatory)][string]$Base,[Parameter(Mandatory)][string]$Saida,[Parameter(Mandatory)][string]$Nome,
      [Parameter(Mandatory)][string]$Recipe,[Parameter(Mandatory)][string]$Tamanho,[hashtable]$Valores=@{},[hashtable]$Calc=@{},
      [double]$SrsMin=0,[string]$PartId=([guid]::NewGuid().ToString()))
$ErrorActionPreference='Stop'
[xml]$x=[IO.File]::ReadAllText($Base); $lp=$x.LandPart
$lp.ColumnConstView.Recipe=$Recipe; $null=$lp.ColumnConstView.Images.Image.URL.SetAttribute('href','http://www.w3.org/1999/xlink',"$Nome.jpg")
$u=$lp.ColumnUnique; @($u.RowUnique) | Select -Skip 1 | % { $null=$u.RemoveChild($_) }; $u.RowUnique.InnerText=[guid]::NewGuid().ToString()
foreach($c in @($lp.Column)){
    @($c.Row) | Select -Skip 1 | % { $null=$c.RemoveChild($_) }
    if($c.name -eq 'PrtSN'){ $c.Row.InnerText=$Tamanho }
    elseif($Valores.ContainsKey($c.name)){ $c.Row.InnerText=[string]$Valores[$c.name] }
    if($Calc.ContainsKey($c.name)){
        $n=$x.CreateElement('ColumnCalc'); foreach($a in 'desc','dataType','unit','name','id','visible','context','index'){ if($c.HasAttribute($a)){ $n.SetAttribute($a,$c.GetAttribute($a)) } }
        $n.InnerText=$Calc[$c.name]; $null=$lp.RemoveChild($c); $null=$lp.AppendChild($n)   # no fim, depois do SBSH
    }
}
if($SrsMin -gt 0){ foreach($i in $lp.ColumnRangeList | ? { $_.name -eq 'SRS' } | % { $_.Item }){ if($i.rangeVal -ne 'Max'){ $i.InnerText=[string]$SrsMin } } }
foreach($c in $lp.ColumnConst){ switch($c.name){ 'PrtNm'{ $c.InnerText=$Nome } 'PrtD'{ $c.InnerText=$Nome } 'PrtID'{ $c.InnerText=$PartId } } }
$xs=New-Object Xml.XmlWriterSettings; $xs.Indent=$true; $xs.Encoding=New-Object Text.UTF8Encoding $false
$w=[Xml.XmlWriter]::Create($Saida,$xs); $x.Save($w); $w.Close()
Copy-Item ([IO.Path]::ChangeExtension($Base,'.jpg')) ([IO.Path]::ChangeExtension($Saida,'.jpg')) -Force
"$Nome -> $Saida (PrtID $PartId)"
