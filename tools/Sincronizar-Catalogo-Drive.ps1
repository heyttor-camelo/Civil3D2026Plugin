# Leva o catalogo FDX instalado (FDX Metric Structures e FDX Metric Pipes) para a copia no Google Drive.
# Antes, guarda as pastas do Drive num backup ao lado (_backup-AAAAMMDD-HHMMSS; ficam os 2 mais novos).
# A origem (ProgramData) so e lida. Grava VERSAO.txt na pasta do Drive. Chamado pelo Ampliar-Faixas-FDX e
# pelo Liberar-Profundidade-FDX depois de gravar; tambem pode rodar sozinho.
param([string]$Origem = 'C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog',
      [string]$Drive = 'G:\.shortcut-targets-by-id\1isURYWt1NUxgdpMOKuYI3pKPFYNMLV6Z\1 FDX ENGENHARIA\2 - Arquivos de Engenharia\Windows\c0ca45ed-e735-4ea1-873d-8097e3c3982e\FDX-Catalogo-2026-10-21',
      [string]$Resumo = 'faixas livres (Ampliar-Faixas-FDX) e profundidade liberada (Liberar-Profundidade-FDX)')
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Drive)) { throw "Pasta do Drive nao encontrada: $Drive" }
$cats = @('FDX Metric Structures', 'FDX Metric Pipes')
$bk = Join-Path $Drive "_backup-$(Get-Date -f yyyyMMdd-HHmmss)"
New-Item -ItemType Directory -Force $bk | Out-Null
foreach ($c in $cats) { if (Test-Path "$Drive\$c") { Move-Item "$Drive\$c" "$bk\$c" } }
Get-ChildItem $Drive -Directory -Filter '_backup-*' | Sort-Object Name -Descending | Select-Object -Skip 2 | Remove-Item -Recurse -Force
$total = 0
foreach ($c in $cats) {
    robocopy "$Origem\$c" "$Drive\$c" /E /XF *.bak *.dwl *.dwl2 /NFL /NDL /NJH /NJS /NP | Out-Null
    $total += (Get-ChildItem "$Drive\$c" -Recurse -File).Count
}
# Conferencia por hash dos .xml/.apc/.dwg
$dif = 0
foreach ($c in $cats) {
    foreach ($f in Get-ChildItem "$Origem\$c" -Recurse -File | Where-Object { $_.Extension -in '.xml', '.apc', '.dwg' }) {
        $d = "$Drive\$c" + $f.FullName.Substring("$Origem\$c".Length)
        if (-not (Test-Path $d) -or (Get-FileHash $d -Algorithm MD5).Hash -ne (Get-FileHash $f.FullName -Algorithm MD5).Hash) { $dif++ }
    }
}
"Catalogo FDX sincronizado em $(Get-Date -f 'yyyy-MM-dd HH:mm') a partir de $env:COMPUTERNAME ($Origem).`r`nInclui: $Resumo.`r`nBackup anterior: $bk" | Set-Content "$Drive\VERSAO.txt" -Encoding utf8
"$total arquivo(s) no Drive; diferencas por hash: $dif; backup: $bk"
