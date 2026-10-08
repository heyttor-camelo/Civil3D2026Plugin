$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Get-ChildItem $root -Directory -Recurse -Force |
    Where-Object { $_.Name -in @('bin','obj','.vs') } |
    Sort-Object FullName -Descending |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Get-ChildItem (Join-Path $root 'NETLOAD') -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'README.txt' } |
    Remove-Item -Force

Write-Host 'Limpeza concluída.'
