<#
  Copia a DLL compilada para a pasta "dll" da FDX no Google Drive (pedido do Heyvid, 29/10/2026).
  A pasta compartilhada aparece em caminhos diferentes em cada PC:
    - G:\Meu Drive\1 FDX ENGENHARIA\2 - Arquivos de Engenharia\dll           (notebook)
    - G:\.shortcut-targets-by-id\<id>\1 FDX ENGENHARIA\2 - Arquivos de Engenharia\dll  (atalho compartilhado)
  O script procura sozinho; se nao achar, so avisa (nao falha o build).
  Uso: tools\Copy-DllFdx.ps1 -Dll <caminho da dll> -Version 2.x.y
#>
param(
    [Parameter(Mandatory = $true)][string]$Dll,
    [Parameter(Mandatory = $true)][string]$Version
)

$sub = '1 FDX ENGENHARIA\2 - Arquivos de Engenharia'
$bases = @()
foreach ($root in @('G:\Meu Drive', 'G:\My Drive')) {
    if (Test-Path (Join-Path $root $sub)) { $bases += (Join-Path $root $sub) }
}
if ($bases.Count -eq 0 -and (Test-Path 'G:\.shortcut-targets-by-id')) {
    Get-ChildItem 'G:\.shortcut-targets-by-id' -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $p = Join-Path $_.FullName $sub
        if (Test-Path $p) { $bases += $p }
    }
}

if ($bases.Count -eq 0) {
    Write-Host "AVISO: pasta '$sub' nao encontrada no G: (Google Drive). DLL nao copiada para a pasta dll da FDX."
    exit 0
}

$dest = Join-Path $bases[0] 'dll'
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $Dll (Join-Path $dest "Civil3D2026Plugin-$Version.dll") -Force
try {
    Copy-Item $Dll (Join-Path $dest 'Civil3D2026Plugin.dll') -Force -ErrorAction Stop
} catch {
    Write-Host "AVISO: Civil3D2026Plugin.dll em uso em $dest; ficou a Civil3D2026Plugin-$Version.dll."
}
# So a versionada mais nova fica (as antigas confundem).
Get-ChildItem $dest -Filter 'Civil3D2026Plugin-*.dll' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 1 | Remove-Item -ErrorAction SilentlyContinue
Write-Host "DLL $Version copiada para $dest"
