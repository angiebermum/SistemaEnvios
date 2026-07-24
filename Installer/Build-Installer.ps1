[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installerDirectory = $PSScriptRoot
$repositoryDirectory = Split-Path -Parent $installerDirectory
$projectPath = Join-Path $installerDirectory 'ECS.EnvioDeCorreos.Installer.wixproj'
$outputDirectory = Join-Path $repositoryDirectory 'artifacts\installer'
$publishDirectory = Join-Path $repositoryDirectory 'artifacts\publish\win-x64'
$dataSyncScript = Join-Path $installerDirectory 'Sync-InstallerData.ps1'

$installedWixVersion = (& wix --version).Trim()
if (-not $installedWixVersion.StartsWith('5.0.2')) {
    throw "Se requiere WiX 5.0.2. Versión encontrada: $installedWixVersion"
}

& $dataSyncScript

$resolvedRepository = [IO.Path]::GetFullPath($repositoryDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$resolvedPublish = [IO.Path]::GetFullPath($publishDirectory)
if (-not $resolvedPublish.StartsWith($resolvedRepository, [StringComparison]::OrdinalIgnoreCase)) {
    throw "La carpeta de publicación calculada está fuera del repositorio: $resolvedPublish"
}

if (Test-Path -LiteralPath $resolvedPublish) {
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

dotnet build $projectPath `
    --configuration Release `
    --property:OutputPath="$outputDirectory\" `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "No fue posible construir el instalador. Código de salida: $LASTEXITCODE"
}

$msi = Get-ChildItem -LiteralPath $outputDirectory -Filter '*.msi' -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $msi) {
    throw 'La compilación terminó sin producir un archivo MSI.'
}

Write-Output "Instalador creado: $($msi.FullName)"
