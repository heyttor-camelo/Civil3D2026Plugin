param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('2026','2027')]
    [string]$Acad = '2026',
    # Pasta com as DLLs da Autodesk para compilar (acdbmgd.dll, C3D\AeccDbMgd.dll, ACA\...).
    # Util quando o Civil 3D instalado roda em outro .NET (ex.: 2026 build 25.1.179 em .NET 10):
    # aponte para uma copia .NET 8 das DLLs, fora do repositorio. Vazio = instalacao padrao.
    [string]$AcadDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'Civil3D2026Plugin.sln'

Write-Host "Compilando $sln [$Configuration|x64] para Civil 3D $Acad..."
if ($AcadDir) {
    dotnet build $sln -c $Configuration -p:Platform=x64 -p:Acad=$Acad -p:AcadDir="$AcadDir"
} else {
    dotnet build $sln -c $Configuration -p:Platform=x64 -p:Acad=$Acad
}

$dll = if ($Acad -eq '2027') { Join-Path $root 'NETLOAD\2027\Civil3D2026Plugin.dll' } else { Join-Path $root 'NETLOAD\Civil3D2026Plugin.dll' }
if (Test-Path $dll) {
    Write-Host ""
    Write-Host "DLL pronta para NETLOAD:"
    Write-Host $dll
} else {
    throw "Build terminou, mas a DLL esperada não foi encontrada em $dll"
}
