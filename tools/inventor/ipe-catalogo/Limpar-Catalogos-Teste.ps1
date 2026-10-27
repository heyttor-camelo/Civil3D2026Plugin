# Limpa os catalogos de teste (move para C:\Temp, nao apaga) e instala o PV_DNIT_IPE no FDX.
# Rodar com o Civil 3D FECHADO.
$ErrorActionPreference='Stop'
if(Get-Process acad -EA SilentlyContinue){ throw 'Feche o Civil 3D antes de rodar.' }
$pc='C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog'; $dst='C:\Temp\BLDC-IPE\catalogos-teste-removidos'
New-Item -ItemType Directory -Force $dst | Out-Null
foreach($n in 'FDX-BLD','FDX-IPE','U3929348-IRIWUEH-393J4J2-DIJFI','93920319-9R9OIJSD-OI3NJ3-IJFOE'){ if(Test-Path "$pc\$n"){ Move-Item "$pc\$n" "$dst\$n"; "movido: $n -> $dst\$n" } else { "ja nao existe: $n" } }
& 'C:\Temp\BLDC-IPE\install-fdx\Instalar-PV-FDX.ps1'
'Pronto. Abra o Civil 3D e use o catalogo FDX Metric Structures.'
