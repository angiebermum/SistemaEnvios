[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installerDirectory = $PSScriptRoot
$repositoryDirectory = Split-Path -Parent $installerDirectory
$projectPath = Join-Path $installerDirectory 'ECS.EnvioDeCorreos.Installer.wixproj'
$outputDirectory = Join-Path $repositoryDirectory 'artifacts\installer'
$publishDirectory = Join-Path $repositoryDirectory 'artifacts\publish\win-x64'
$seedRelativePath = 'Data\correos-iniciales.v3.json'
$seedPath = Join-Path $repositoryDirectory $seedRelativePath
$buildStartedAt = Get-Date

$installedWixVersion = (& wix --version).Trim()
if (-not $installedWixVersion.StartsWith('5.0.2')) {
    throw "Se requiere WiX 5.0.2. Versión encontrada: $installedWixVersion"
}

$resolvedRepository = [IO.Path]::GetFullPath($repositoryDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$resolvedPublish = [IO.Path]::GetFullPath($publishDirectory)
$resolvedOutput = [IO.Path]::GetFullPath($outputDirectory)
if (-not $resolvedPublish.StartsWith($resolvedRepository, [StringComparison]::OrdinalIgnoreCase)) {
    throw "La carpeta de publicación calculada está fuera del repositorio: $resolvedPublish"
}

if (-not $resolvedOutput.StartsWith($resolvedRepository, [StringComparison]::OrdinalIgnoreCase)) {
    throw "La carpeta del instalador calculada está fuera del repositorio: $resolvedOutput"
}

if (-not (Test-Path -LiteralPath $seedPath -PathType Leaf)) {
    throw "No se encontró la semilla versionada requerida: $seedPath"
}

$seed = Get-Content -Raw -LiteralPath $seedPath -Encoding UTF8 | ConvertFrom-Json
$approvedDataFiles = @($seedRelativePath)
$signatureRelativePath = [string]$seed.signatureFile
if (-not [string]::IsNullOrWhiteSpace($signatureRelativePath)) {
    $signatureRelativePath = $signatureRelativePath.Replace('/', '\')
    $signaturePath = [IO.Path]::GetFullPath((Join-Path $repositoryDirectory $signatureRelativePath))
    if (-not $signaturePath.StartsWith($resolvedRepository, [StringComparison]::OrdinalIgnoreCase)) {
        throw "La firma inicial configurada está fuera del repositorio: $signaturePath"
    }

    if (-not (Test-Path -LiteralPath $signaturePath -PathType Leaf)) {
        throw "No se encontró la firma inicial versionada requerida: $signaturePath"
    }

    $approvedDataFiles += $signatureRelativePath
}

foreach ($relativePath in $approvedDataFiles) {
    & git -C $repositoryDirectory ls-files --error-unmatch -- $relativePath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "El archivo de datos del instalador no está versionado: $relativePath"
    }
}

$statusArguments = @('-C', $repositoryDirectory, 'status', '--porcelain', '--untracked-files=all', '--') + $approvedDataFiles
$dataStatus = @(& git @statusArguments)
if ($LASTEXITCODE -ne 0) {
    throw 'No fue posible comprobar el estado Git de los datos del instalador.'
}

if ($dataStatus.Count -ne 0) {
    throw "Los datos del instalador tienen cambios locales sin aprobar:`n$($dataStatus -join [Environment]::NewLine)"
}

if (Test-Path -LiteralPath $resolvedPublish) {
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}

if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null

dotnet restore $projectPath --nologo
if ($LASTEXITCODE -ne 0) {
    throw "No fue posible restaurar las dependencias del instalador. Código de salida: $LASTEXITCODE"
}

dotnet build $projectPath `
    --configuration Release `
    --property:OutputPath="$resolvedOutput\" `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "No fue posible construir el instalador. Código de salida: $LASTEXITCODE"
}

$projectXml = [xml](Get-Content -Raw -LiteralPath $projectPath -Encoding UTF8)
$outputName = [string]$projectXml.Project.PropertyGroup.OutputName
$expectedMsiPath = Join-Path $resolvedOutput "$outputName.msi"
$msi = Get-Item -LiteralPath $expectedMsiPath -ErrorAction SilentlyContinue

if ($null -eq $msi) {
    throw "La compilación terminó sin producir el MSI esperado: $expectedMsiPath"
}

if ($msi.LastWriteTime -lt $buildStartedAt) {
    throw "El MSI encontrado no fue generado durante esta ejecución: $($msi.FullName)"
}

Write-Output "Instalador creado: $($msi.FullName)"
