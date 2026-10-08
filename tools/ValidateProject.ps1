$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src\Civil3D2026Plugin'
$errors = @()
$warnings = @()

$csFiles = Get-ChildItem $src -Filter '*.cs' -Recurse

# Nomes de comandos devem vir de CommandNames.cs.
foreach ($file in $csFiles) {
    $matches = Select-String -Path $file.FullName -Pattern 'CommandMethod\s*\(\s*"' -AllMatches
    if ($matches) {
        $errors += "CommandMethod com string literal: $($file.FullName)"
    }
}

# Apenas um ponto de entrada da extensão.
$extensionApps = @()
foreach ($file in $csFiles) {
    if (Select-String -Path $file.FullName -Pattern 'IExtensionApplication' -Quiet) {
        $extensionApps += $file.FullName
    }
}
if ($extensionApps.Count -ne 1) {
    $errors += "Esperado exatamente 1 IExtensionApplication; encontrados $($extensionApps.Count): $($extensionApps -join ', ')"
}


# Namespaces internos de modulo nao podem usar nomes que colidam com tipos centrais
# do AutoCAD/Civil 3D. Ex.: Modules.Corridor faz "Corridor" virar namespace e pode
# sombrear Autodesk.Civil.DatabaseServices.Corridor em outros modulos.
$reservedModuleNames = @(
    'Corridor', 'Alignment', 'Profile', 'FeatureLine', 'Surface',
    'Pipe', 'Structure', 'Assembly', 'Subassembly',
    'Entity', 'DBObject', 'Database', 'Document', 'Transaction'
)
foreach ($file in $csFiles) {
    $text = Get-Content $file.FullName -Raw
    $nsMatches = [regex]::Matches(
        $text,
        'namespace\s+Civil3D2026Plugin\.Modules\.([A-Za-z_][A-Za-z0-9_]*)'
    )
    foreach ($m in $nsMatches) {
        $segment = $m.Groups[1].Value
        if ($reservedModuleNames -contains $segment) {
            $errors += "Namespace de modulo reservado/ambíguo '$segment': $($file.FullName). Use um nome de agrupamento como '${segment}Tools'."
        }
    }
}

# Alerta para arquivos que ainda importam os dois DatabaseServices amplamente.
foreach ($file in $csFiles) {
    $text = Get-Content $file.FullName -Raw
    if ($text -match 'using Autodesk\.AutoCAD\.DatabaseServices;' -and
        $text -match 'using Autodesk\.Civil\.DatabaseServices;') {
        $warnings += "Namespace duplo (revisar ao editar): $($file.FullName)"
    }
}



# WinForms + WPF coexistem no projeto. Na UI WPF, tipos homonimos devem ser
# explicitamente qualificados/aliased para evitar CS0104 (Color, Point etc.).
$ribbonFiles = Get-ChildItem (Join-Path $src 'UI\Ribbon') -Filter '*.cs' -Recurse -ErrorAction SilentlyContinue
$collisionProneWpfTypes = @('Color', 'Point', 'FlowDirection', 'FontFamily', 'Brushes')
foreach ($file in $ribbonFiles) {
    $text = Get-Content $file.FullName -Raw
    foreach ($typeName in $collisionProneWpfTypes) {
        # Ignora referencias ja qualificadas/aliased (WpfColor, System.Windows.Point etc.).
        if ($text -match "(?m)(?<![A-Za-z0-9_.])$typeName\\b" -and
            $text -notmatch "using\\s+Wpf$typeName\\s*=") {
            $warnings += "Tipo WPF potencialmente ambiguo '$typeName' em: $($file.FullName). Prefira alias Wpf$typeName ou nome totalmente qualificado."
        }
    }
}

# Ribbon/UI: referencias e arquivos estruturais esperados.
$csprojPath = Join-Path $src 'Civil3D2026Plugin.csproj'
$csprojText = Get-Content $csprojPath -Raw

if ($csprojText -notmatch '<UseWPF>true</UseWPF>') {
    $errors += 'Ribbon ativa, mas UseWPF=true nao foi encontrado no .csproj.'
}
if ($csprojText -notmatch '<Reference Include="AdWindows">') {
    $errors += 'Ribbon ativa, mas a referencia AdWindows.dll nao foi encontrada no .csproj.'
}

$ribbonManager = Join-Path $src 'UI\Ribbon\RibbonUiManager.cs'
if (-not (Test-Path $ribbonManager)) {
    $errors += 'RibbonUiManager.cs nao encontrado.'
}

Write-Host '=== VALIDACAO DO PROJETO ==='
if ($warnings.Count -gt 0) {
    Write-Host ''
    Write-Host 'Avisos:'
    $warnings | ForEach-Object { Write-Host "  - $_" }
}

if ($errors.Count -gt 0) {
    Write-Host ''
    Write-Host 'Erros:'
    $errors | ForEach-Object { Write-Host "  - $_" }
    exit 1
}

Write-Host ''
Write-Host 'Estrutura básica OK.'
exit 0
