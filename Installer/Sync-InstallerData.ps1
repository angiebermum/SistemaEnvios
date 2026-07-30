[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ConfigurationPath,
    [int]$SeedVersion = 3
)

$ErrorActionPreference = 'Stop'
$installerDirectory = $PSScriptRoot
$repositoryDirectory = Split-Path -Parent $installerDirectory
$dataDirectory = Join-Path $repositoryDirectory 'Data'
$seedPath = Join-Path $dataDirectory 'correos-iniciales.v3.json'

if (-not (Test-Path -LiteralPath $ConfigurationPath -PathType Leaf)) {
    throw "No se encontró la configuración activa que debe incorporarse al instalador: $ConfigurationPath"
}

$configuration = Get-Content -Raw -LiteralPath $ConfigurationPath -Encoding UTF8 | ConvertFrom-Json
if ($null -eq $configuration.Brokers -or @($configuration.Brokers).Count -eq 0) {
    throw 'La configuración activa no contiene corredores. Se canceló la creación del instalador para evitar publicar datos vacíos.'
}

New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null

$signatureFile = $null
$signatureSha256 = $null
$signaturePath = [string]$configuration.SignatureImagePath
$allowedSignatureExtensions = @('.png', '.jpg', '.jpeg')
$managedSignaturePaths = $allowedSignatureExtensions | ForEach-Object {
    Join-Path $dataDirectory ("firma-inicial" + $_)
}

foreach ($candidate in $managedSignaturePaths) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        Remove-Item -LiteralPath $candidate -Force
    }
}

if (-not [string]::IsNullOrWhiteSpace($signaturePath)) {
    if (-not (Test-Path -LiteralPath $signaturePath -PathType Leaf)) {
        throw "La firma configurada no existe y no puede incorporarse al instalador: $signaturePath"
    }

    $extension = [IO.Path]::GetExtension($signaturePath).ToLowerInvariant()
    if ($extension -notin $allowedSignatureExtensions) {
        throw "La firma configurada debe ser PNG, JPG o JPEG: $signaturePath"
    }

    $signatureFileName = "firma-inicial$extension"
    $signatureDestination = Join-Path $dataDirectory $signatureFileName
    Copy-Item -LiteralPath $signaturePath -Destination $signatureDestination -Force
    $signatureFile = "Data/$signatureFileName"
    $signatureSha256 = (Get-FileHash -LiteralPath $signatureDestination -Algorithm SHA256).Hash.ToLowerInvariant()
}

$commonCc = @($configuration.CommonCcAddresses | ForEach-Object {
    [ordered]@{
        name = ''
        email = ([string]$_).Trim()
    }
})

$brokers = @($configuration.Brokers | ForEach-Object {
    $broker = $_
    $brokerId = [Guid]$broker.Id
    if ($brokerId -eq [Guid]::Empty) {
        throw "El corredor '$($broker.Name)' no tiene un identificador válido."
    }

    $seedKey = [string]$broker.SeedKey
    if ([string]::IsNullOrWhiteSpace($seedKey)) {
        $seedKey = "configured-$($brokerId.ToString('N'))"
    }

    $assistants = @($broker.Assistants | ForEach-Object {
        $assistant = $_
        $assistantId = [Guid]$assistant.Id
        if ($assistantId -eq [Guid]::Empty) {
            throw "Un asistente de '$($broker.Name)' no tiene un identificador válido."
        }

        [ordered]@{
            id = $assistantId
            name = [string]$assistant.Name
            email = ([string]$assistant.Email).Trim()
            isActive = [bool]$assistant.IsActive
        }
    })

    [ordered]@{
        id = $brokerId
        seedKey = $seedKey.Trim()
        name = [string]$broker.Name
        primaryEmails = @($broker.PrimaryEmailAddresses | ForEach-Object { ([string]$_).Trim() })
        assistants = $assistants
        isActive = [bool]$broker.IsActive
        requiresReview = [bool]$broker.RequiresReview
        reviewNote = if ($null -eq $broker.ReviewNote) { $null } else { [string]$broker.ReviewNote }
    }
})

$payload = [ordered]@{
    seedVersion = $SeedVersion
    defaultSubject = [string]$configuration.DefaultSubject
    defaultMessage = [string]$configuration.DefaultMessage
    signatureFile = $signatureFile
    signatureSha256 = $signatureSha256
    organizationContact = [ordered]@{
        name = 'Contabilidad Essential Corredora de Seguros'
        email = 'conta@essentialgroupla.com'
        usage = 'metadata-only'
    }
    commonCc = $commonCc
    brokers = $brokers
}

$payloadJson = $payload | ConvertTo-Json -Depth 12
$utf8 = New-Object System.Text.UTF8Encoding($false)
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $payloadBytes = $utf8.GetBytes($payloadJson)
    $seedId = ([BitConverter]::ToString($sha256.ComputeHash($payloadBytes))).Replace('-', '').ToLowerInvariant()
}
finally {
    $sha256.Dispose()
}

$seed = [ordered]@{
    seedVersion = $SeedVersion
    seedId = $seedId
    defaultSubject = $payload.defaultSubject
    defaultMessage = $payload.defaultMessage
    signatureFile = $payload.signatureFile
    signatureSha256 = $payload.signatureSha256
    organizationContact = $payload.organizationContact
    commonCc = $payload.commonCc
    brokers = $payload.brokers
}

$seedJson = ($seed | ConvertTo-Json -Depth 12) + [Environment]::NewLine
[IO.File]::WriteAllText($seedPath, $seedJson, $utf8)

$primaryEmailCount = @($configuration.Brokers.PrimaryEmailAddresses).Count
$assistantCount = @($configuration.Brokers.Assistants).Count
Write-Output "Datos del instalador sincronizados: $seedPath"
Write-Output "Semilla=$SeedVersion; Id=$seedId; Corredores=$(@($configuration.Brokers).Count); Correos=$primaryEmailCount; Asistentes=$assistantCount; CC=$(@($configuration.CommonCcAddresses).Count); Firma=$(-not [string]::IsNullOrWhiteSpace($signatureFile))"
