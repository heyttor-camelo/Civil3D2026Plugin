param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'Civil3D2026Plugin.sln'

Write-Host "Compilando $sln [$Configuration|x64]..."
dotnet build $sln -c $Configuration -p:Platform=x64

$dll = Join-Path $root 'NETLOAD\Civil3D2026Plugin.dll'
if (Test-Path $dll) {
    Write-Host ""
    Write-Host "DLL pronta para NETLOAD:"
    Write-Host $dll
} else {
    throw "Build terminou, mas a DLL esperada não foi encontrada em $dll"
}
