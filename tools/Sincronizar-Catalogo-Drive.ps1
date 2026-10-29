# Leva o catalogo FDX instalado (FDX Metric Structures e FDX Metric Pipes) para o Google Drive, em dois lugares:
#  1. o catalogo compartilhado, para todos apontarem o SETPIPENETWORKCATALOG direto:
#     G:\Meu Drive\1 FDX ENGENHARIA\2 - Arquivos de Engenharia\C3D CATALOGO FDX
#     (backups numa pasta AO LADO, "C3D CATALOGO FDX - backups": dentro da raiz o Civil leria os .apc deles);
#  2. a copia de referencia ...\Windows\c0ca45ed-...\FDX-Catalogo-2026-10-21 (backup _backup-* dentro dela).
# Em cada destino: as pastas atuais vao para um backup com data e hora (ficam os 2 mais novos), copia por robocopy,
# confere os .xml/.apc/.dwg por hash e grava VERSAO.txt. A origem (ProgramData) so e lida.
# Chamado pelo Ampliar-Faixas-FDX e pelo Liberar-Profundidade-FDX depois de gravar; tambem pode rodar sozinho.
param([string]$Origem = 'C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog',
      [string]$Compartilhado = 'G:\Meu Drive\1 FDX ENGENHARIA\2 - Arquivos de Engenharia\C3D CATALOGO FDX',
      [string]$Referencia = 'G:\.shortcut-targets-by-id\1isURYWt1NUxgdpMOKuYI3pKPFYNMLV6Z\1 FDX ENGENHARIA\2 - Arquivos de Engenharia\Windows\c0ca45ed-e735-4ea1-873d-8097e3c3982e\FDX-Catalogo-2026-10-21',
      [string]$Resumo = 'faixas livres (Ampliar-Faixas-FDX) e profundidade liberada (Liberar-Profundidade-FDX)')
$ErrorActionPreference = 'Stop'
$cats = @('FDX Metric Structures', 'FDX Metric Pipes')

function Sincroniza([string]$Destino, [string]$Backups) {
    if (-not (Test-Path $Destino)) { return "nao encontrado: $Destino" }
    New-Item -ItemType Directory -Force $Backups | Out-Null
    $bk = Join-Path $Backups "_backup-$(Get-Date -f yyyyMMdd-HHmmss)"
    New-Item -ItemType Directory -Force $bk | Out-Null
    foreach ($c in $cats) { if (Test-Path "$Destino\$c") { Move-Item "$Destino\$c" "$bk\$c" } }
    Get-ChildItem $Backups -Directory -Filter '_backup-*' | Sort-Object Name -Descending | Select-Object -Skip 2 | Remove-Item -Recurse -Force
    $total = 0
    foreach ($c in $cats) {
        robocopy "$Origem\$c" "$Destino\$c" /E /XF *.bak *.dwl *.dwl2 /XD _backup* _quarentena /NFL /NDL /NJH /NJS /NP | Out-Null
        $total += (Get-ChildItem "$Destino\$c" -Recurse -File).Count
    }
    $dif = 0
    foreach ($c in $cats) {
        foreach ($f in Get-ChildItem "$Origem\$c" -Recurse -File | Where-Object { $_.Extension -in '.xml', '.apc', '.dwg' -and $_.FullName -notmatch '\\_backup|\\_quarentena' }) {
            $d = "$Destino\$c" + $f.FullName.Substring("$Origem\$c".Length)
            if (-not (Test-Path $d) -or (Get-FileHash $d -Algorithm MD5).Hash -ne (Get-FileHash $f.FullName -Algorithm MD5).Hash) { $dif++ }
        }
    }
    "Catalogo FDX sincronizado em $(Get-Date -f 'yyyy-MM-dd HH:mm') a partir de $env:COMPUTERNAME ($Origem).`r`nInclui: $Resumo.`r`nBackup anterior: $bk" | Set-Content "$Destino\VERSAO.txt" -Encoding utf8
    "${Destino}: $total arquivo(s); diferencas por hash: $dif; backup: $bk"
}

Sincroniza $Compartilhado ($Compartilhado + ' - backups')
Sincroniza $Referencia $Referencia
