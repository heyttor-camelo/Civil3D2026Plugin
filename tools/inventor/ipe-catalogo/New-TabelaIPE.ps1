# Gera a tabela (.xml + .jpg) de uma peca IPE a partir de uma tabela Autodesk do mesmo recipe base.
# Fica so a primeira linha. Para a peca ser editavel pelo PartData (Part Properties e painel HYDRO),
# as medidas de projeto vao em faixa, como nas familias Part Builder:
#   -Faixas  @{ SIL = @('StructInnerLength', min, max, padrao); ... }  -> ColumnRangeList (substitui a coluna, se existir)
#   -Calc    @{ SBSL = '$SIL + 2 * $WTh'; ... }                       -> ColumnCalc (substitui Column ou ColumnCalc)
#   -Valores @{ SFH = 50; ... }                                        -> valor fixo de Column
param([Parameter(Mandatory)][string]$Base,[Parameter(Mandatory)][string]$Saida,[Parameter(Mandatory)][string]$Nome,
      [Parameter(Mandatory)][string]$Recipe,[string]$Tamanho,[hashtable]$Valores=@{},[hashtable]$Calc=@{},
      [hashtable]$Faixas=@{},[string]$PartId=([guid]::NewGuid().ToString()))
$ErrorActionPreference='Stop'
[xml]$x=[IO.File]::ReadAllText($Base); $lp=$x.LandPart
$lp.ColumnConstView.Recipe=$Recipe; $null=$lp.ColumnConstView.Images.Image.URL.SetAttribute('href','http://www.w3.org/1999/xlink',"$Nome.jpg")
$u=$lp.ColumnUnique; @($u.RowUnique) | Select -Skip 1 | % { $null=$u.RemoveChild($_) }; $u.RowUnique.InnerText=[guid]::NewGuid().ToString()
$seq=0; function NovoId($p){ $script:seq++; "$p$($script:seq)X" }
function Copia($de,$tag){ $n=$x.CreateElement($tag); foreach($a in 'desc','dataType','unit','name','id','visible','context','index'){ if($de.HasAttribute($a)){ $n.SetAttribute($a,$de.GetAttribute($a)) } }; $n }

foreach($c in @($lp.Column)){
    @($c.Row) | Select -Skip 1 | % { $null=$c.RemoveChild($_) }
    if($c.name -eq 'PrtSN' -and $Tamanho){ $c.Row.InnerText=$Tamanho }
    elseif($Valores.ContainsKey($c.name)){ $c.Row.InnerText=[string]$Valores[$c.name] }
}
# faixas: substituem Column/ColumnConst/ColumnRangeList de mesmo nome, ou entram novas
foreach($k in $Faixas.Keys){
    $ctx,$mn,$mx,$df=$Faixas[$k]
    $old=@($lp.ChildNodes | ? { $_.name -eq $k }) | Select -First 1
    $n=$x.CreateElement('ColumnRangeList')
    foreach($a in @(@('desc',$k),@('dataType','Float'),@('unit','mm'),@('name',$k),@('id',(NovoId 'CRL')),@('visible','1'),@('context',$ctx),@('index','0'))){ $n.SetAttribute($a[0],$a[1]) }
    if($old -and $old.HasAttribute('desc')){ $n.SetAttribute('desc',$old.desc) }
    $i=0; foreach($rv in @(@('Min',$mn),@('Max',$mx),@('Default',$df))){ $it=$x.CreateElement('Item'); $it.SetAttribute('id',"i$i"); $it.SetAttribute('rangeVal',$rv[0]); $it.InnerText=[string]$rv[1]; $null=$n.AppendChild($it); $i++ }
    if($old){ $null=$lp.ReplaceChild($n,$old) } else { $null=$lp.InsertBefore($n,$lp.ColumnConst[0]) }
}
# calculos: no fim, depois dos que ja existem (SBSH etc.)
foreach($k in $Calc.Keys){
    $old=@($lp.ChildNodes | ? { $_.name -eq $k }) | Select -First 1
    if($old){ $n=Copia $old 'ColumnCalc'; $null=$lp.RemoveChild($old) }
    else { $n=$x.CreateElement('ColumnCalc'); foreach($a in @(@('desc',$k),@('dataType','Float'),@('unit','mm'),@('name',$k),@('id',(NovoId 'CCA')),@('visible','0'),@('context','Geometry_ModelDistanceParameter'),@('index','0'))){ $n.SetAttribute($a[0],$a[1]) } }
    if($k -eq 'PrtSN'){ $n.SetAttribute('dataType','String'); $n.SetAttribute('unit','') }
    $n.InnerText=$Calc[$k]; $null=$lp.AppendChild($n)
}
# reordena todos os ColumnCalc por dependencia ($X antes de quem usa $X), no fim da tabela
$calcs=@($lp.ColumnCalc); $nomes=$calcs | % name; $feito=@{}; $ordem=New-Object Collections.ArrayList
function Visita($c){ if($feito[$c.name]){ return }; $feito[$c.name]=$true
    foreach($r in [regex]::Matches($c.InnerText,'\$(\w+)') | % { $_.Groups[1].Value }){ if($r -in $nomes -and $r -ne $c.name){ Visita ($calcs | ? name -eq $r) } }
    $null=$ordem.Add($c) }
foreach($c in $calcs){ Visita $c }
foreach($c in $ordem){ $null=$lp.RemoveChild($c); $null=$lp.AppendChild($c) }
foreach($c in $lp.ColumnConst){ switch($c.name){ 'PrtNm'{ $c.InnerText=$Nome } 'PrtD'{ $c.InnerText=$Nome } 'PrtID'{ $c.InnerText=$PartId } } }
$xs=New-Object Xml.XmlWriterSettings; $xs.Indent=$true; $xs.Encoding=New-Object Text.UTF8Encoding $false
$w=[Xml.XmlWriter]::Create($Saida,$xs); $x.Save($w); $w.Close()
Copy-Item ([IO.Path]::ChangeExtension($Base,'.jpg')) ([IO.Path]::ChangeExtension($Saida,'.jpg')) -Force
"$Nome -> $Saida (PrtID $PartId)"
