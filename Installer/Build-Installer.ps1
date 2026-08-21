[CmdletBinding()]
param(
    [string]$InstallerDirectoryOverride
)

$ErrorActionPreference = 'Stop'
$versionFilePath = $null
$originalVersionFileBytes = $null
$previousVersion = $null
$newVersion = $null
$versionWasUpdated = $false
$expectedMsiPath = $null
$versionTemporaryPath = $null

function Invoke-NativeStep {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Description,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Command
    )

    & $Command
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Description Código de salida: $exitCode"
    }
}

function Resolve-SafeRepositoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$RepositoryPrefix
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($RepositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "La ruta calculada está fuera del repositorio y no se puede limpiar: $resolved"
    }

    return $resolved
}

function Get-NormalizedProductVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $value = [string][Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
    if ($value.Contains('+')) {
        $value = $value.Split('+', 2)[0]
    }

    return $value
}

function Get-MsiProductVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $installer = $null
    $database = $null
    $view = $null
    $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.GetType().InvokeMember(
            'OpenDatabase', 'InvokeMethod', $null, $installer, @($Path, 0))
        $view = $database.GetType().InvokeMember(
            'OpenView', 'InvokeMethod', $null, $database,
            @("SELECT `Value` FROM `Property` WHERE `Property`='ProductVersion'"))
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) {
            throw "El MSI no contiene la propiedad ProductVersion: $Path"
        }

        return [string]$record.GetType().InvokeMember(
            'StringData', 'GetProperty', $null, $record, @(1))
    }
    finally {
        foreach ($comObject in @($record, $view, $database, $installer)) {
            if ($null -ne $comObject -and [Runtime.InteropServices.Marshal]::IsComObject($comObject)) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject)
            }
        }
    }
}

try {
    $installerDirectory = if ([string]::IsNullOrWhiteSpace($InstallerDirectoryOverride)) {
        $PSScriptRoot
    }
    else {
        [IO.Path]::GetFullPath($InstallerDirectoryOverride)
    }
    $repositoryDirectory = Split-Path -Parent $installerDirectory
    $applicationProjectPath = Join-Path $repositoryDirectory 'ECS.CommissionsMailer.csproj'
    $testProjectPath = Join-Path $repositoryDirectory 'Tests\ECS.CommissionsMailer.Tests\ECS.CommissionsMailer.Tests.csproj'
    $installerProjectPath = Join-Path $installerDirectory 'ECS.EnvioDeCorreos.Installer.wixproj'
    $versionFilePath = Join-Path $repositoryDirectory 'Directory.Build.props'
    $outputDirectory = Join-Path $repositoryDirectory 'artifacts\installer'
    $publishDirectory = Join-Path $repositoryDirectory 'artifacts\publish\win-x64'
    $seedRelativePath = 'Data\correos-iniciales.v3.json'
    $seedPath = Join-Path $repositoryDirectory $seedRelativePath
    $buildStartedAt = Get-Date

    $resolvedRepository = [IO.Path]::GetFullPath($repositoryDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $repositoryPrefix = $resolvedRepository + [IO.Path]::DirectorySeparatorChar
    $resolvedPublish = Resolve-SafeRepositoryPath $publishDirectory $repositoryPrefix
    $resolvedOutput = Resolve-SafeRepositoryPath $outputDirectory $repositoryPrefix

    if (-not (Test-Path -LiteralPath $versionFilePath -PathType Leaf)) {
        throw "No se encontró la fuente central de versión: $versionFilePath"
    }

    $originalVersionFileBytes = [IO.File]::ReadAllBytes($versionFilePath)
    $originalVersionFileText = Get-Content -Raw -LiteralPath $versionFilePath -Encoding UTF8
    $versionPattern = '(?<prefix><EcsApplicationVersion>\s*)(?<value>[^<]*?)(?<suffix>\s*</EcsApplicationVersion>)'
    $versionMatches = [regex]::Matches($originalVersionFileText, $versionPattern)
    if ($versionMatches.Count -ne 1) {
        throw 'Directory.Build.props debe contener exactamente un elemento EcsApplicationVersion.'
    }

    $previousVersion = $versionMatches[0].Groups['value'].Value.Trim()
    if ($previousVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "EcsApplicationVersion debe usar el formato X.Y.Z. Valor encontrado: '$previousVersion'"
    }

    $versionParts = $previousVersion.Split('.')
    $major = 0
    $minor = 0
    $patch = 0
    if (-not [int]::TryParse($versionParts[0], [ref]$major) -or
        -not [int]::TryParse($versionParts[1], [ref]$minor) -or
        -not [int]::TryParse($versionParts[2], [ref]$patch) -or
        $major -lt 0 -or $minor -lt 0 -or $patch -lt 0) {
        throw "EcsApplicationVersion contiene un componente numérico no válido: '$previousVersion'"
    }
    if ($patch -eq [int]::MaxValue) {
        throw "No es posible incrementar el componente PATCH de '$previousVersion'."
    }

    $newVersion = "$major.$minor.$($patch + 1)"
    $valueGroup = $versionMatches[0].Groups['value']
    $updatedVersionFileText = $originalVersionFileText.Remove($valueGroup.Index, $valueGroup.Length).Insert($valueGroup.Index, $newVersion)
    $hasUtf8Bom = $originalVersionFileBytes.Length -ge 3 -and
        $originalVersionFileBytes[0] -eq 0xEF -and
        $originalVersionFileBytes[1] -eq 0xBB -and
        $originalVersionFileBytes[2] -eq 0xBF
    $utf8Encoding = New-Object Text.UTF8Encoding($hasUtf8Bom)
    $versionTemporaryPath = Join-Path (Split-Path -Parent $versionFilePath) ".Directory.Build.props.$([Guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($versionTemporaryPath, $updatedVersionFileText, $utf8Encoding)
    $versionWasUpdated = $true
    Move-Item -LiteralPath $versionTemporaryPath -Destination $versionFilePath -Force
    $version = $newVersion
    $expectedMsiPath = Join-Path $resolvedOutput "ECS Envío de Correos - Instalador $version.msi"

    Write-Host "Versión anterior: $previousVersion"
    Write-Host "Nueva versión preparada: $newVersion"
    Write-Host ''

    $wixCommand = Get-Command wix -ErrorAction SilentlyContinue
    if ($null -eq $wixCommand) {
        throw 'No se encontró WiX Toolset. Instale la herramienta global wix 5.0.2.'
    }

    $installedWixVersion = (& wix --version).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $installedWixVersion.StartsWith('5.0.2')) {
        throw "Se requiere WiX 5.0.2. Versión encontrada: $installedWixVersion"
    }

    if (-not (Test-Path -LiteralPath $seedPath -PathType Leaf)) {
        throw "No se encontró la semilla versionada requerida: $seedPath"
    }

    $seed = Get-Content -Raw -LiteralPath $seedPath -Encoding UTF8 | ConvertFrom-Json
    $approvedDataFiles = @($seedRelativePath)
    $signatureRelativePath = [string]$seed.signatureFile
    if (-not [string]::IsNullOrWhiteSpace($signatureRelativePath)) {
        $signatureRelativePath = $signatureRelativePath.Replace('/', '\')
        $signaturePath = Resolve-SafeRepositoryPath (Join-Path $repositoryDirectory $signatureRelativePath) $repositoryPrefix
        if (-not (Test-Path -LiteralPath $signaturePath -PathType Leaf)) {
            throw "No se encontró la firma inicial versionada requerida: $signaturePath"
        }

        $approvedDataFiles += $signatureRelativePath
    }

    $gitCommand = Get-Command git -ErrorAction SilentlyContinue
    if ($null -eq $gitCommand) {
        Write-Warning 'Git no está disponible. Se continuará sin información de Git.'
    }
    else {
        foreach ($relativePath in $approvedDataFiles) {
            & $gitCommand.Source -C $repositoryDirectory ls-files --error-unmatch -- $relativePath | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "El archivo de datos del instalador no está versionado: $relativePath"
            }
        }

        $statusArguments = @('-C', $repositoryDirectory, 'status', '--porcelain', '--untracked-files=all', '--') + $approvedDataFiles
        $dataStatus = @(& $gitCommand.Source @statusArguments)
        if ($LASTEXITCODE -ne 0) {
            throw 'No fue posible comprobar el estado Git de los datos del instalador.'
        }

        if ($dataStatus.Count -ne 0) {
            throw "Los datos del instalador tienen cambios locales sin aprobar:`n$($dataStatus -join [Environment]::NewLine)"
        }
    }

    Write-Host '[1/7] Limpiando salidas anteriores...'
    $directoriesToClean = @(
        'bin\Release',
        'obj\Release',
        'Infrastructure\FirebaseClient\bin\Release',
        'Infrastructure\FirebaseClient\obj\Release',
        'Infrastructure\Firestore.Common\bin\Release',
        'Infrastructure\Firestore.Common\obj\Release',
        'Installer\bin\Release',
        'Installer\obj\Release',
        'artifacts\publish\win-x64',
        'artifacts\installer',
        'publish\win-x64',
        'publish\win-x64-self-contained'
    )

    foreach ($relativeDirectory in $directoriesToClean) {
        $cleanPath = Resolve-SafeRepositoryPath (Join-Path $repositoryDirectory $relativeDirectory) $repositoryPrefix
        if (Test-Path -LiteralPath $cleanPath) {
            Remove-Item -LiteralPath $cleanPath -Recurse -Force
        }
    }

    New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null

    Write-Host '[2/7] Restaurando pruebas e instalador...'
    Invoke-NativeStep 'No fue posible restaurar las pruebas.' {
        dotnet restore $testProjectPath --nologo
    }
    Invoke-NativeStep 'No fue posible restaurar el instalador.' {
        dotnet restore $installerProjectPath --nologo
    }

    Write-Host '[3/7] Ejecutando pruebas automatizadas...'
    Invoke-NativeStep 'Las pruebas automatizadas fallaron.' {
        dotnet test $testProjectPath `
            --configuration Release `
            --no-restore `
            --nologo
    }

    Write-Host '[4/7] Compilando Release win-x64 autocontenido...'
    Invoke-NativeStep 'No fue posible restaurar la aplicación para win-x64.' {
        dotnet restore $applicationProjectPath --runtime win-x64 --nologo
    }
    Invoke-NativeStep 'No fue posible compilar la aplicación.' {
        dotnet build $applicationProjectPath `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            --no-restore `
            --nologo
    }

    Write-Host '[5/7] Publicando win-x64 autocontenido...'
    Invoke-NativeStep 'No fue posible publicar la aplicación.' {
        dotnet publish $applicationProjectPath `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            --no-restore `
            --output $resolvedPublish `
            --property:PublishTrimmed=false `
            --property:PublishSingleFile=false `
            --nologo
    }

    $publishedExecutable = Join-Path $resolvedPublish 'ECS Envío de Correos.exe'
    $publishedAssembly = Join-Path $resolvedPublish 'ECS Envío de Correos.dll'
    $publishedSeed = Join-Path $resolvedPublish $seedRelativePath
    $publishedSignature = if ([string]::IsNullOrWhiteSpace($signatureRelativePath)) {
        $null
    }
    else {
        Join-Path $resolvedPublish $signatureRelativePath
    }

    foreach ($requiredPath in @($publishedExecutable, $publishedAssembly, $publishedSeed, $publishedSignature)) {
        if (-not [string]::IsNullOrWhiteSpace($requiredPath) -and
            -not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "La publicación está incompleta. Falta: $requiredPath"
        }
    }

    $publishedExecutableVersion = Get-NormalizedProductVersion $publishedExecutable
    $publishedAssemblyVersion = Get-NormalizedProductVersion $publishedAssembly
    if (-not [string]::Equals($publishedExecutableVersion, $version, [StringComparison]::OrdinalIgnoreCase)) {
        throw "El EXE publicado no tiene la versión esperada. Esperada=$version; Encontrada=$publishedExecutableVersion"
    }
    if (-not [string]::Equals($publishedAssemblyVersion, $version, [StringComparison]::OrdinalIgnoreCase)) {
        throw "El DLL publicado no tiene la versión esperada. Esperada=$version; Encontrada=$publishedAssemblyVersion"
    }

    $forbiddenNames = @(
        'firebase-runtime.json',
        'firebase-refresh-token.dat',
        'firebase-session.json',
        'firebase-auth.json',
        'user-session.json',
        'refresh-token.json',
        'application_default_credentials.json',
        'credentials.json',
        'adc.json',
        'service-account.json',
        'service_account.json',
        'firebase-adminsdk.json'
    )
    $forbiddenPublishedFiles = @(Get-ChildItem -LiteralPath $resolvedPublish -Recurse -File | Where-Object {
        $forbiddenNames -contains $_.Name.ToLowerInvariant() -or
        $_.Name -match '(?i)(service[-_]?account|adminsdk|refresh[-_]?token|firebase[-_]?session|user[-_]?session).*\.(json|dat|p12|pfx|pem|key)$' -or
        $_.Extension -eq '.dpapi'
    })
    if ($forbiddenPublishedFiles.Count -ne 0) {
        throw "La publicación contiene archivos sensibles prohibidos:`n$($forbiddenPublishedFiles.FullName -join [Environment]::NewLine)"
    }

    Write-Host '[6/7] Generando el MSI con WiX 5.0.2...'
    $installerOutputArgument = "--property:OutputPath=$resolvedOutput$([IO.Path]::DirectorySeparatorChar)"
    Invoke-NativeStep 'No fue posible construir el instalador.' {
        dotnet build $installerProjectPath `
            --configuration Release `
            --no-restore `
            $installerOutputArgument `
            --property:SkipApplicationPublish=true `
            --nologo
    }

    Write-Host '[7/7] Verificando el instalador generado...'
    $msi = Get-Item -LiteralPath $expectedMsiPath -ErrorAction SilentlyContinue
    if ($null -eq $msi) {
        throw "La compilación terminó sin producir el MSI esperado: $expectedMsiPath"
    }

    if ($msi.LastWriteTime -lt $buildStartedAt) {
        throw "El MSI encontrado no fue generado durante esta ejecución: $($msi.FullName)"
    }

    $msiProductVersion = Get-MsiProductVersion $msi.FullName
    if (-not [string]::Equals($msiProductVersion, $version, [StringComparison]::OrdinalIgnoreCase)) {
        throw "El MSI no tiene la versión esperada. Esperada=$version; Encontrada=$msiProductVersion"
    }

    $msiHash = (Get-FileHash -LiteralPath $msi.FullName -Algorithm SHA256).Hash

    Write-Host ''
    Write-Host '========================================'
    Write-Host 'INSTALADOR ACTUALIZADO CORRECTAMENTE'
    Write-Host '========================================'
    Write-Host ''
    Write-Host 'Versión anterior:'
    Write-Host $previousVersion
    Write-Host ''
    Write-Host 'Nueva versión:'
    Write-Host $newVersion
    Write-Host ''
    Write-Host 'Tests: OK'
    Write-Host 'Build: OK'
    Write-Host 'Publish: OK'
    Write-Host 'Installer: OK'
    Write-Host 'Ruta:'
    Write-Host $msi.FullName
    Write-Host ''
    Write-Host 'SHA256:'
    Write-Host $msiHash
    Write-Host ''
    Write-Host '========================================'
}
catch {
    $buildError = $_.Exception.Message
    $versionRestored = $false
    if ($versionWasUpdated -and $null -ne $originalVersionFileBytes -and -not [string]::IsNullOrWhiteSpace($versionFilePath)) {
        try {
            [IO.File]::WriteAllBytes($versionFilePath, $originalVersionFileBytes)
            $versionRestored = $true
        }
        catch {
            Write-Host "ERROR CRÍTICO: No fue posible restaurar $versionFilePath. $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($expectedMsiPath)) {
        try {
            if (Test-Path -LiteralPath $expectedMsiPath -PathType Leaf) {
                Remove-Item -LiteralPath $expectedMsiPath -Force
            }
            $incompleteSymbolsPath = [IO.Path]::ChangeExtension($expectedMsiPath, '.wixpdb')
            if (Test-Path -LiteralPath $incompleteSymbolsPath -PathType Leaf) {
                Remove-Item -LiteralPath $incompleteSymbolsPath -Force
            }
        }
        catch {
            Write-Warning "No fue posible eliminar por completo el instalador incompleto. $($_.Exception.Message)"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($versionTemporaryPath) -and
        (Test-Path -LiteralPath $versionTemporaryPath -PathType Leaf)) {
        Remove-Item -LiteralPath $versionTemporaryPath -Force -ErrorAction SilentlyContinue
    }

    Write-Host ''
    Write-Host '========================================' -ForegroundColor Red
    Write-Host 'NO SE GENERÓ EL INSTALADOR' -ForegroundColor Red
    Write-Host '========================================' -ForegroundColor Red
    Write-Host $buildError -ForegroundColor Red
    if ($versionRestored) {
        Write-Host "La versión fue restaurada a $previousVersion." -ForegroundColor Yellow
    }
    exit 1
}
