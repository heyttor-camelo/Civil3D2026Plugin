<#
  Copia a DLL compilada para a pasta "dll" da FDX no Google Drive (pedido do Heyvid, 29/10/2026).
  A pasta compartilhada aparece em caminhos diferentes em cada PC:
    - G:\Meu Drive\1 FDX ENGENHARIA\2 - Arquivos de Engenharia\dll           (notebook)
    - G:\.shortcut-targets-by-id\<id>\1 FDX ENGENHARIA\2 - Arquivos de Engenharia\dll  (atalho compartilhado)
  O script procura sozinho; se nao achar, so avisa (nao falha o build).
  Uso: tools\Copy-DllFdx.ps1 -Dll <caminho da dll> -Version 2.x.y [-Name C3DPassagemRampa]
  -Name: nome base da DLL na pasta (padrao Civil3D2026Plugin; a avulsa usa C3DPassagemRampa).
#>
param(
    [Parameter(Mandatory = $true)][string]$Dll,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Name = 'Civil3D2026Plugin'
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
Copy-Item $Dll (Join-Path $dest "$Name-$Version.dll") -Force
# No G: o Windows nao trava a DLL carregada: com o Civil aberto, trocar/apagar a DLL em uso corrompe o plugin
# ("invalid program", 11/11/2026). Entao so a versionada; a sem numero e as antigas ficam.
if (Get-Process acad -ErrorAction SilentlyContinue) {
    Write-Host "AVISO: Civil 3D aberto: em $dest so foi gravada a $Name-$Version.dll (a $Name.dll e as antigas ficam ate a proxima entrega com o Civil fechado)."
    exit 0
}
try {
    Copy-Item $Dll (Join-Path $dest "$Name.dll") -Force -ErrorAction Stop
} catch {
    Write-Host "AVISO: $Name.dll em uso em $dest; ficou a $Name-$Version.dll."
}
# So a versionada mais nova fica (as antigas confundem).
Get-ChildItem $dest -Filter "$Name-*.dll" | Sort-Object LastWriteTime -Descending | Select-Object -Skip 1 | Remove-Item -ErrorAction SilentlyContinue
Write-Host "$Name $Version copiada para $dest"
