using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsRetryPreparationService
{
    public const string FilesUnavailableMessage =
        "Los archivos originales no se pueden resolver de forma segura en el lote actual. El historial permanece disponible, pero el reintento está deshabilitado.";
    public const string ChangedHashMessage =
        "Uno o más archivos originales cambiaron (SHA-256 no coincide). El reintento está bloqueado.";

    private readonly GeneratedFileHashService _hashService;
    private readonly string? _signatureImagePath;

    public ExpirationsRetryPreparationService(
        GeneratedFileHashService? hashService = null,
        string? signatureImagePath = null)
    {
        _hashService = hashService ?? new GeneratedFileHashService();
        _signatureImagePath = signatureImagePath;
    }

    public ExpirationsRetryPreparationResult Prepare(
        ExpirationsSendOperation operation,
        IReadOnlyList<ExpirationsSendHistoryItem> items,
        ExpirationsGenerationBatch? currentBatch)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(items);
        var errors = new List<string>();
        if (operation.Status != ExpirationsSendOperationStatus.Completed)
            errors.Add("La operación está incompleta o su resultado no está confirmado.");

        var failed = items.Where(item => item.Status == ExpirationsSendItemStatus.Failed).ToList();
        if (items.Any(item => item.Status == ExpirationsSendItemStatus.Pending))
            errors.Add("La operación contiene items pendientes con resultado desconocido.");
        if (failed.Count == 0)
            errors.Add("La operación no contiene correos fallidos que puedan reintentarse.");
        if (string.IsNullOrWhiteSpace(operation.Subject) || string.IsNullOrWhiteSpace(operation.Body))
            errors.Add("El snapshot histórico de asunto o mensaje no es válido.");
        if (currentBatch is null || currentBatch.Process != operation.Process)
            errors.Add(FilesUnavailableMessage);

        var requests = new List<EmailSendRequest>();
        var preparedItems = new List<ExpirationsPreparedSendItem>();
        var hashChanged = false;
        var unresolved = false;
        foreach (var item in failed)
        {
            ValidateRecipients(item, errors);
            var resolved = new List<ExpirationsPreparedAttachment>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attachment in item.Attachments)
            {
                var candidates = currentBatch?.Files.Where(file =>
                        file.BrokerId == item.BrokerId &&
                        file.Variant == attachment.Variant &&
                        string.Equals(
                            Path.GetFileName(file.OutputPath),
                            attachment.FileName,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList() ?? [];
                if (candidates.Count != 1)
                {
                    unresolved = true;
                    continue;
                }

                var file = candidates[0];
                if (!string.Equals(file.Sha256, attachment.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(file.OutputPath) ||
                    !Path.GetExtension(file.OutputPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                    !IsInsideBatch(file.OutputPath, currentBatch!.OutputDirectory) ||
                    !ExpirationsBatchFileAssociationService.IsValidWorkbook(file.OutputPath))
                {
                    hashChanged = true;
                    continue;
                }
                var fullPath = Path.GetFullPath(file.OutputPath);
                if (!seenPaths.Add(fullPath))
                {
                    unresolved = true;
                    continue;
                }
                try
                {
                    if (!_hashService.ComputeSha256(file.OutputPath)
                        .Equals(attachment.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        hashChanged = true;
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    hashChanged = true;
                    continue;
                }

                resolved.Add(new ExpirationsPreparedAttachment(
                    fullPath,
                    attachment.FileName,
                    attachment.Sha256,
                    attachment.Variant));
            }

            if (resolved.Count != item.Attachments.Count || item.Attachments.Count == 0)
                continue;
            var request = new EmailSendRequest
            {
                BrokerId = item.BrokerId,
                BrokerName = item.BrokerName,
                ToRecipients = item.ToRecipients.ToList(),
                CcRecipients = item.CcRecipients.ToList(),
                Subject = operation.Subject,
                Body = operation.Body,
                AttachmentPaths = resolved.Select(attachment => attachment.Path).ToList(),
                SignatureImagePath = _signatureImagePath,
                ReviewConfirmed = true
            };
            requests.Add(request);
            preparedItems.Add(new ExpirationsPreparedSendItem
            {
                RequestId = request.RequestId,
                RetryOfItemId = item.ItemId,
                Attachments = resolved
            });
        }

        if (hashChanged)
            errors.Add(ChangedHashMessage);
        if (unresolved)
            errors.Add(FilesUnavailableMessage);
        if (requests.Count != failed.Count)
            errors.Add("No se resolvieron exactamente todos los adjuntos de los correos fallidos.");

        errors = errors.Distinct(StringComparer.Ordinal).ToList();
        if (errors.Count > 0)
            return new ExpirationsRetryPreparationResult { Errors = errors };

        return new ExpirationsRetryPreparationResult
        {
            Preparation = new ExpirationsSendPreparationResult
            {
                Process = operation.Process,
                Settings = new ExpirationsProcessSettings
                {
                    DefaultSubject = operation.Subject,
                    DefaultMessage = operation.Body
                },
                Requests = requests,
                PreparedItems = preparedItems,
                RetryOfOperationId = operation.OperationId,
                Warnings =
                [
                    $"Reintento histórico: sólo {failed.Count} correo(s) fallido(s).",
                    $"Cuenta usada en el intento anterior (referencia): {operation.SendingAccountEmail}"
                ]
            }
        };
    }

    private static void ValidateRecipients(
        ExpirationsSendHistoryItem item,
        ICollection<string> errors)
    {
        if (item.ToRecipients.Count == 0)
            errors.Add($"El snapshot de {item.BrokerName} no contiene destinatarios Para.");
        foreach (var address in item.ToRecipients.Concat(item.CcRecipients))
        {
            if (!EmailValidationService.TryNormalizeAddress(address, out _))
                errors.Add($"El snapshot de {item.BrokerName} contiene el correo inválido '{address}'.");
        }
    }

    private static bool IsInsideBatch(string path, string outputDirectory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetFullPath(outputDirectory);
            var relative = Path.GetRelativePath(directory, fullPath);
            return relative.Length > 0 &&
                   !Path.IsPathRooted(relative) &&
                   !relative.Equals("..", StringComparison.Ordinal) &&
                   !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                   !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
