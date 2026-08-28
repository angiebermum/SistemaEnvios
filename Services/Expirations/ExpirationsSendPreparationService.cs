using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsSendPreparationService
{
    public const int MaximumAttachmentsPerBroker = 6;
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

    public Task<ExpirationsSendPreparationResult> PrepareAsync(
        ExpirationsGenerationBatch batch,
        CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(batch, null, null, cancellationToken);

    public Task<ExpirationsSendPreparationResult> PrepareAsync(
        ExpirationsGenerationBatch batch,
        string? signatureImagePath,
        CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(batch, signatureImagePath, null, cancellationToken);

    public Task<ExpirationsSendPreparationResult> PrepareSelectedAsync(
        ExpirationsGenerationBatch batch,
        IReadOnlyCollection<Guid> brokerIds,
        string? signatureImagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brokerIds);
        return PrepareCoreAsync(batch, signatureImagePath, brokerIds, cancellationToken);
    }

    private async Task<ExpirationsSendPreparationResult> PrepareCoreAsync(
        ExpirationsGenerationBatch batch,
        string? signatureImagePath,
        IReadOnlyCollection<Guid>? selectedBrokerIds,
        CancellationToken cancellationToken)
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
            errors.Add("Guarde la plantilla de correo antes de enviar.");
        var processSettings = settingsDocument?.Value;
        if (processSettings is not null)
        {
            if (string.IsNullOrWhiteSpace(processSettings.DefaultSubject))
                errors.Add("El asunto configurado es obligatorio.");
            if (string.IsNullOrWhiteSpace(processSettings.DefaultMessage))
                errors.Add("El mensaje configurado es obligatorio.");
        }
        var settingsAreValid = errors.Count == 0;

        var catalogById = catalog.Items
            .GroupBy(item => item.BrokerId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var filesByBroker = batch.Files
            .GroupBy(file => file.BrokerId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var participatingBrokerIds = batch.ParticipatingBrokerIds.Count > 0
            ? batch.ParticipatingBrokerIds.ToHashSet()
            : batch.RequiredAttachmentSlots.Count > 0
                ? batch.RequiredAttachmentSlots.Select(slot => slot.BrokerId).ToHashSet()
            : batch.Files.Select(file => file.BrokerId).ToHashSet();
        var targets = selectedBrokerIds is null
            ? participatingBrokerIds.OrderBy(id => id).ToList()
            : selectedBrokerIds.Distinct().OrderBy(id => id).ToList();
        var requests = new List<EmailSendRequest>();
        var preparedItems = new List<ExpirationsPreparedSendItem>();
        var eligibleBrokerIds = new HashSet<Guid>();

        foreach (var brokerId in targets)
        {
            var itemErrors = new List<string>();
            if (!catalogById.TryGetValue(brokerId, out var brokerMatches) || brokerMatches.Count != 1)
            {
                errors.Add($"El corredor del lote '{brokerId:D}' no existe de forma única en Vencimientos.");
                continue;
            }

            var broker = brokerMatches[0];
            if (!broker.IsActive)
            {
                errors.Add($"El corredor '{broker.Name}' está inactivo en Vencimientos.");
                continue;
            }

            var files = filesByBroker.GetValueOrDefault(brokerId) ?? [];
            var isParticipant = participatingBrokerIds.Contains(brokerId);
            var eligibilityErrors = new List<string>();
            if (isParticipant)
            {
                ValidateEligibilityFiles(batch, broker, files, eligibilityErrors);
                if (eligibilityErrors.Count == 0)
                    eligibleBrokerIds.Add(brokerId);
                ValidateRequiredFiles(batch, broker, files, itemErrors);
            }
            else
            {
                ValidateManualOnlyFiles(broker, files, itemErrors);
            }
            ValidateAttachments(batch, files, itemErrors);
            if (itemErrors.Contains(ChangedAttachmentMessage, StringComparer.Ordinal))
                errors.Add(ChangedAttachmentMessage);
            var resolution = _recipients.Resolve(
                broker,
                processSettings?.CommonCcAddresses ?? [],
                batch.Process);
            itemErrors.AddRange(resolution.Errors);
            if (itemErrors.Count > 0)
            {
                errors.AddRange(itemErrors.Select(error =>
                    error.StartsWith($"{broker.Name} ", StringComparison.CurrentCultureIgnoreCase)
                        ? error
                        : $"{broker.Name}: {error}"));
                continue;
            }
            if (!settingsAreValid)
                continue;

            var orderedFiles = files
                .OrderBy(AttachmentOrder)
                .ThenBy(file => file.OutputPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var requiresReview = orderedFiles.Any(file => file.RequiresReview || file.IsManuallyEdited);
            var reviewNotes = orderedFiles
                .Where(file => file.RequiresReview || file.IsManuallyEdited)
                .Select(file => file.IsManuallyEdited
                    ? $"{Path.GetFileName(file.OutputPath)} fue editado manualmente mediante ECS."
                    : $"{Path.GetFileName(file.OutputPath)} es un adjunto manual.")
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var request = new EmailSendRequest
            {
                BrokerId = broker.BrokerId,
                BrokerName = broker.Name,
                BrokerPrimaryRecipients = resolution.BrokerPrimaryRecipients,
                AssistantRecipients = resolution.AssistantRecipients,
                ToRecipients = resolution.ToRecipients,
                CcRecipients = resolution.CcRecipients,
                Subject = processSettings!.DefaultSubject.Trim(),
                Body = processSettings.DefaultMessage.Trim(),
                AttachmentPaths = orderedFiles
                    .Select(file => TryGetFullPath(file.OutputPath) ?? file.OutputPath)
                    .ToList(),
                RequiresReview = requiresReview,
                ReviewNote = string.Join(Environment.NewLine, reviewNotes),
                ReviewConfirmed = true,
                PaymentGenerationId = null,
                ResendOfRecordId = null,
                SignatureImagePath = signatureImagePath
            };
            requests.Add(request);
            preparedItems.Add(new ExpirationsPreparedSendItem
            {
                RequestId = request.RequestId,
                Attachments = orderedFiles.Select(file => new ExpirationsPreparedAttachment(
                    TryGetFullPath(file.OutputPath) ?? file.OutputPath,
                    Path.GetFileName(file.OutputPath),
                    file.Sha256,
                    file.Variant)).ToList()
            });
        }

        if (batch.Files.Count == 0)
            errors.Add("El lote actual no contiene archivos asociados.");
        if (selectedBrokerIds is { Count: 0 })
            errors.Add("Seleccione al menos un corredor.");

        return new ExpirationsSendPreparationResult
        {
            Process = batch.Process,
            Settings = processSettings is null ? null : Copy(processSettings),
            Requests = requests,
            PreparedItems = preparedItems,
            EligibleBrokerIds = eligibleBrokerIds,
            Errors = errors.Distinct(StringComparer.Ordinal).ToList(),
            Warnings = warnings
        };
    }

    private static void ValidateManualOnlyFiles(
        ExpirationsBrokerCatalogItem broker,
        IReadOnlyList<ExpirationsGeneratedFile> files,
        ICollection<string> errors)
    {
        if (!files.Any(file => file.Variant == ExpirationsGeneratedFileVariant.Manual))
        {
            errors.Add($"{broker.Name} no participa en el reporte actual y no tiene archivos manuales asociados.");
        }
        if (files.Any(file => file.Variant != ExpirationsGeneratedFileVariant.Manual))
            errors.Add("Un corredor que no participa sólo puede enviarse con archivos manuales asociados.");
    }

    private void ValidateAttachments(
        ExpirationsGenerationBatch batch,
        IReadOnlyList<ExpirationsGeneratedFile> files,
        ICollection<string> errors)
    {
        if (files.Count > MaximumAttachmentsPerBroker)
            errors.Add($"El correo supera el máximo de {MaximumAttachmentsPerBroker} archivos asociados.");
        var outputDirectory = TryGetFullPath(batch.OutputDirectory);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var file in files)
        {
            var fullPath = TryGetFullPath(file.OutputPath);
            if (fullPath is null || outputDirectory is null || !IsInsideDirectory(fullPath, outputDirectory))
            {
                errors.Add("Uno o más archivos no pertenecen al batch actual.");
                continue;
            }
            if (!seenPaths.Add(fullPath))
            {
                errors.Add($"El archivo '{Path.GetFileName(fullPath)}' está repetido en el batch actual.");
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
                if (!ExpirationsBatchFileAssociationService.IsValidWorkbook(fullPath))
                {
                    errors.Add($"El archivo '{Path.GetFileName(fullPath)}' no es un libro .xlsx válido.");
                    continue;
                }
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

    private static void ValidateRequiredFiles(
        ExpirationsGenerationBatch batch,
        ExpirationsBrokerCatalogItem broker,
        IReadOnlyList<ExpirationsGeneratedFile> files,
        ICollection<string> errors)
    {
        var required = RequiredSlots(batch, broker);
        var usePersistedSlotNames = batch.RequiredAttachmentSlots.Count > 0;
        foreach (var slot in required)
        {
            var automaticCount = files.Count(file => IsAutomaticForSlot(file, slot.Key));
            var replacementCount = files.Count(file => IsReplacementForSlot(file, slot.Key));
            if (automaticCount + replacementCount == 0)
                errors.Add(MissingSlotMessage(slot, usePersistedSlotNames));
            if (automaticCount > 1 || replacementCount > 1)
                errors.Add($"El archivo {SlotName(slot, usePersistedSlotNames)} está duplicado.");
        }
        foreach (var generated in files.Where(file => file.Variant != ExpirationsGeneratedFileVariant.Manual))
        {
            if (!required.Any(slot => IsAutomaticForSlot(generated, slot.Key)))
                errors.Add($"El archivo {ExpirationsDestinationGroups.DisplayName(generated.DestinationGroup)} " +
                           "no corresponde a un slot requerido del corredor en este proceso.");
        }
        foreach (var replacement in files.Where(file => file.ReplacesSlot.HasValue))
        {
            if (!required.Any(slot => slot.Key == replacement.ReplacesSlot!.Value))
                errors.Add("Un archivo manual intenta reemplazar un slot que no pertenece al batch actual.");
        }
    }

    private void ValidateEligibilityFiles(
        ExpirationsGenerationBatch batch,
        ExpirationsBrokerCatalogItem broker,
        IReadOnlyList<ExpirationsGeneratedFile> files,
        ICollection<string> errors)
    {
        var usePersistedSlotNames = batch.RequiredAttachmentSlots.Count > 0;
        foreach (var slot in RequiredSlots(batch, broker))
        {
            var automatic = files.Where(file => IsAutomaticForSlot(file, slot.Key)).ToList();
            var replacements = files.Where(file => IsReplacementForSlot(file, slot.Key)).ToList();
            if (automatic.Count + replacements.Count == 0 || automatic.Count > 1 || replacements.Count > 1)
            {
                errors.Add(automatic.Count + replacements.Count == 0
                    ? MissingSlotMessage(slot, usePersistedSlotNames)
                    : $"El archivo {SlotName(slot, usePersistedSlotNames)} está duplicado.");
                continue;
            }
            ValidateAttachments(batch, automatic.Concat(replacements).ToList(), errors);
        }
    }

    private static IReadOnlyList<ExpirationsRequiredAttachmentSlot> RequiredSlots(
        ExpirationsGenerationBatch batch,
        ExpirationsBrokerCatalogItem broker)
    {
        var persisted = batch.RequiredAttachmentSlots
            .Where(slot => slot.BrokerId == broker.BrokerId)
            .DistinctBy(slot => slot.Key)
            .ToList();
        return persisted.Count > 0
            ? persisted
            : RequiredVariants(batch.Process, broker)
                .Select(variant => new ExpirationsRequiredAttachmentSlot(
                    broker.BrokerId,
                    ExpirationsDestinationGroup.Principal,
                    variant))
                .ToList();
    }

    private static bool IsAutomaticForSlot(
        ExpirationsGeneratedFile file,
        ExpirationsAttachmentSlotKey slot) =>
        file.Variant == slot.ExpectedVariant &&
        file.BrokerId == slot.BrokerId &&
        file.DestinationGroup == slot.DestinationGroup &&
        !file.ReplacesSlot.HasValue;

    private static bool IsReplacementForSlot(
        ExpirationsGeneratedFile file,
        ExpirationsAttachmentSlotKey slot) =>
        file.Variant == ExpirationsGeneratedFileVariant.Manual && file.ReplacesSlot == slot;

    private static string SlotName(
        ExpirationsRequiredAttachmentSlot slot,
        bool usePersistedSlotNames)
    {
        var group = ExpirationsDestinationGroups.DisplayName(slot.DestinationGroup);
        return slot.ExpectedVariant == ExpirationsGeneratedFileVariant.Standard
            ? slot.DestinationGroup == ExpirationsDestinationGroup.Principal && !usePersistedSlotNames
                ? "Standard"
                : group
            : $"{group} ({VariantName(slot.ExpectedVariant)})";
    }

    private static string MissingSlotMessage(
        ExpirationsRequiredAttachmentSlot slot,
        bool usePersistedSlotNames) =>
        slot.DestinationGroup == ExpirationsDestinationGroup.Principal && !usePersistedSlotNames
            ? $"Falta el archivo obligatorio {SlotName(slot, usePersistedSlotNames)}."
            : $"Falta el archivo {SlotName(slot, usePersistedSlotNames)}.";

    private static IReadOnlyList<ExpirationsGeneratedFileVariant> RequiredVariants(
        ExpirationsProcess process,
        ExpirationsBrokerCatalogItem broker) =>
        process == ExpirationsProcess.NextMonth &&
        broker.NextMonthGenerationMode == ExpirationsNextMonthGenerationMode.SpecialDualSorted
            ?
            [
                ExpirationsGeneratedFileVariant.FelixAlphabetical,
                ExpirationsGeneratedFileVariant.FelixExpirationDate
            ]
            : [ExpirationsGeneratedFileVariant.Standard];

    private static int AttachmentOrder(ExpirationsGeneratedFile file) => file.Variant switch
    {
        ExpirationsGeneratedFileVariant.Standard => 0,
        ExpirationsGeneratedFileVariant.FelixAlphabetical => 1,
        ExpirationsGeneratedFileVariant.FelixExpirationDate => 2,
        ExpirationsGeneratedFileVariant.Manual => 3,
        _ => int.MaxValue
    };

    private static string VariantName(ExpirationsGeneratedFileVariant variant) => variant switch
    {
        ExpirationsGeneratedFileVariant.Standard => "Standard",
        ExpirationsGeneratedFileVariant.FelixAlphabetical => "FelixAlphabetical",
        ExpirationsGeneratedFileVariant.FelixExpirationDate => "FelixExpirationDate",
        ExpirationsGeneratedFileVariant.Manual => "Manual",
        _ => variant.ToString()
    };

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
