using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsSendPreparationService
{
    public const string ChangedAttachmentMessage =
        "Uno o más archivos generados cambiaron después de la generación. Genere nuevamente antes de enviar.";

    private readonly IExpirationsProcessSettingsRepository _settings;
    private readonly ExpirationsBrokerCatalogService _catalog;
    private readonly ExpirationsRecipientService _recipients;
    private readonly GeneratedFileHashService _hashService;

    public ExpirationsSendPreparationService(
        IExpirationsProcessSettingsRepository settings,
        ExpirationsBrokerCatalogService catalog,
        ExpirationsRecipientService? recipients = null,
        GeneratedFileHashService? hashService = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _recipients = recipients ?? new ExpirationsRecipientService();
        _hashService = hashService ?? new GeneratedFileHashService();
    }

    public async Task<ExpirationsSendPreparationResult> PrepareAsync(
        ExpirationsGenerationBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var errors = new List<string>();
        var settingsTask = _settings.GetAsync(batch.Process, cancellationToken);
        var catalogTask = _catalog.LoadAsync(cancellationToken);
        await Task.WhenAll(settingsTask, catalogTask);
        var settingsDocument = await settingsTask;
        var catalog = await catalogTask;
        var warnings = batch.Warnings
            .Concat(catalog.Warnings)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (settingsDocument is null)
            errors.Add("Configure el asunto, mensaje y CC generales antes de enviar.");
        var processSettings = settingsDocument?.Value;
        if (processSettings is not null)
        {
            if (string.IsNullOrWhiteSpace(processSettings.DefaultSubject))
                errors.Add("El asunto configurado es obligatorio.");
            if (string.IsNullOrWhiteSpace(processSettings.DefaultMessage))
                errors.Add("El mensaje configurado es obligatorio.");
        }

        ValidateAttachments(batch, errors);
        var catalogById = catalog.Items
            .GroupBy(item => item.BrokerId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var requests = new List<EmailSendRequest>();
        foreach (var fileGroup in batch.Files
                     .GroupBy(file => file.BrokerId)
                     .OrderBy(group => group.First().BrokerName, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(group => group.Key))
        {
            if (!catalogById.TryGetValue(fileGroup.Key, out var brokerMatches) || brokerMatches.Count != 1)
            {
                errors.Add($"El corredor del lote '{fileGroup.Key:D}' no existe de forma única en Vencimientos.");
                continue;
            }

            var broker = brokerMatches[0];
            if (!broker.IsActive)
            {
                errors.Add($"El corredor '{broker.Name}' está inactivo en Vencimientos.");
                continue;
            }

            var resolution = _recipients.Resolve(
                broker,
                processSettings?.CommonCcAddresses ?? []);
            errors.AddRange(resolution.Errors);
            requests.Add(new EmailSendRequest
            {
                BrokerId = broker.BrokerId,
                BrokerName = broker.Name,
                BrokerPrimaryRecipients = resolution.BrokerPrimaryRecipients,
                AssistantRecipients = resolution.AssistantRecipients,
                ToRecipients = resolution.ToRecipients,
                CcRecipients = resolution.CcRecipients,
                Subject = processSettings?.DefaultSubject.Trim() ?? string.Empty,
                Body = processSettings?.DefaultMessage.Trim() ?? string.Empty,
                AttachmentPaths = fileGroup
                    .OrderBy(file => file.Variant)
                    .Select(file => TryGetFullPath(file.OutputPath) ?? file.OutputPath)
                    .ToList(),
                RequiresReview = false,
                ReviewNote = string.Empty,
                ReviewConfirmed = true,
                PaymentGenerationId = null,
                ResendOfRecordId = null,
                SignatureImagePath = null
            });
        }

        if (batch.Files.Count == 0)
            errors.Add("El lote actual no contiene archivos generados.");

        return new ExpirationsSendPreparationResult
        {
            Process = batch.Process,
            Settings = processSettings is null ? null : Copy(processSettings),
            Requests = requests,
            Errors = errors.Distinct(StringComparer.Ordinal).ToList(),
            Warnings = warnings
        };
    }

    private void ValidateAttachments(ExpirationsGenerationBatch batch, ICollection<string> errors)
    {
        var outputDirectory = TryGetFullPath(batch.OutputDirectory);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var file in batch.Files)
        {
            var fullPath = TryGetFullPath(file.OutputPath);
            if (fullPath is null || outputDirectory is null || !IsInsideDirectory(fullPath, outputDirectory))
            {
                errors.Add("Uno o más archivos no pertenecen al lote de generación actual.");
                continue;
            }
            if (!seenPaths.Add(fullPath))
            {
                errors.Add($"El archivo '{Path.GetFileName(fullPath)}' está repetido en el lote actual.");
                continue;
            }
            if (!Path.GetExtension(fullPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"El archivo '{Path.GetFileName(fullPath)}' no es un archivo .xlsx.");
                continue;
            }
            if (!File.Exists(fullPath) || string.IsNullOrWhiteSpace(file.Sha256))
            {
                changed = true;
                continue;
            }

            try
            {
                var actualHash = _hashService.ComputeSha256(fullPath);
                if (!actualHash.Equals(file.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    changed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                changed = true;
            }
        }

        if (changed)
            errors.Add(ChangedAttachmentMessage);
    }

    private static string? TryGetFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative.Length > 0 &&
            !Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static ExpirationsProcessSettings Copy(ExpirationsProcessSettings source) => new()
    {
        DefaultSubject = source.DefaultSubject,
        DefaultMessage = source.DefaultMessage,
        CommonCcAddresses = source.CommonCcAddresses.ToList(),
        UpdatedAtUtc = source.UpdatedAtUtc
    };
}
