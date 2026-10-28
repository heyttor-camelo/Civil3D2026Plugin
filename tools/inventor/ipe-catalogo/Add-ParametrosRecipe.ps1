# Leva parametros novos da tabela ate a geometria de um recipe:
#  geom.iam (parametro de usuario <Componente>_<Nome>) -> regra iLogic <Componente> -> .ipt do componente.
# Tambem registra cada um no param.xml (Value em cm) e tira a expressao do .ipt (passa a ser valor dirigido).
# -Params @{ Ab_L = @('900 mm', 'Comprimento de cada abertura'); ... }   (Inventor aberto)
param([Parameter(Mandatory)][string]$Recipe,[string]$Componente='UndergroundStructure',[Parameter(Mandatory)][hashtable]$Params)
$ErrorActionPreference='Stop'
$BF=[Reflection.BindingFlags]
function Get($o,$n){ [System.__ComObject].InvokeMember($n,$BF::GetProperty,$null,$o,$null) }
$inv=[Runtime.InteropServices.Marshal]::GetActiveObject('Inventor.Application'); $inv.SilentOperation=$true
$ad=$inv.ApplicationAddIns.ItemById('{3bdd8d79-2179-4b11-8a5a-257b1c0263ac}'); if(-not $ad.Activated){ $ad.Activate() }
$auto=Get $ad 'Automation'

# 1. .ipt: o parametro ja existe (criado pelos scripts Add-*); vira valor puro
$ipt=Get-ChildItem $Recipe -Filter "*_$Componente.ipt" | Select -First 1
$p=$inv.Documents.Open($ipt.FullName,$false); $pp=$p.ComponentDefinition.Parameters
foreach($k in $Params.Keys){ $par=$pp.Item($k); $par.Expression="$([math]::Round($par.Value*10,4)) mm" }
$p.Save(); $p.Close($true)

# 2. geom.iam: parametro de usuario + linha na regra do componente
$a=$inv.Documents.Open((Join-Path $Recipe 'geom.iam'),$false); $up=$a.ComponentDefinition.Parameters.UserParameters
$rule=[System.__ComObject].InvokeMember('GetRule',$BF::InvokeMethod,$null,$auto,@($a,$Componente)); $txt=Get $rule 'Text'
foreach($k in $Params.Keys){
    $n="${Componente}_$k"; $ok=$true; try{ $null=$up.Item($n) }catch{ $ok=$false }
    if(-not $ok){ $null=$up.AddByExpression($n,$Params[$k][0],'mm') } else { $up.Item($n).Expression=$Params[$k][0] }
    $linha="Parameter(""$Componente"", ""$k"") = $n"; if($txt -notmatch [regex]::Escape($linha)){ $txt=$txt.TrimEnd() + "`r`n" + $linha }
}
[System.__ComObject].InvokeMember('Text',$BF::SetProperty,$null,$rule,@($txt))
$a.Update(); $a.Save(); $a.Close($true)

# 3. param.xml
$px=Join-Path $Recipe 'param.xml'; [xml]$x=[IO.File]::ReadAllText($px); $ps=$x.SelectSingleNode('//Params')
foreach($k in $Params.Keys){
    $n="${Componente}_$k"; $e=$ps.SelectSingleNode("Param[@Name='$n']"); if(-not $e){ $e=$x.CreateElement('Param'); $null=$ps.AppendChild($e) }
    $mm=[double]($Params[$k][0] -replace '[^\d\.\-]','')
    $e.SetAttribute('Name',$n); $e.SetAttribute('Value',[string]($mm/10)); $e.SetAttribute('Type','LENGTH'); $e.SetAttribute('LDesId',$Params[$k][1]); $e.SetAttribute('IsReadOnly','true')
}
$x.Save($px)
"$(Split-Path $Recipe -Leaf): $($Params.Keys -join ', ') ligados ate o $Componente"
