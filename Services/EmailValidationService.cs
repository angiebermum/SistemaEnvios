using System.Net.Mail;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public sealed class EmailValidationService
{
    private static readonly char[] AddressSeparators = [';', ',', '\r', '\n'];
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".xlsx", ".xls" };

    public bool TryParseAddresses(
        string? input,
        bool required,
        out List<string> addresses,
        out List<string> errors,
        bool rejectDuplicates = false)
    {
        addresses = [];
        errors = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in (input ?? string.Empty).Split(AddressSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryNormalizeAddress(candidate, out var normalized))
            {
                errors.Add($"La dirección '{candidate}' no es válida.");
                continue;
            }

            if (seen.Add(normalized))
            {
                addresses.Add(normalized);
            }
            else if (rejectDuplicates)
            {
                errors.Add($"La dirección '{normalized}' está duplicada.");
            }
        }

        if (required && addresses.Count == 0 && errors.Count == 0)
        {
            errors.Add("Debe indicar al menos una dirección de correo.");
        }

        return errors.Count == 0;
    }

    public List<string> ValidateBroker(string name, string addressesText, out List<string> addresses)
        => ValidateBroker(name, addressesText, [], false, null, out addresses);

    public List<string> ValidateBroker(
        string name,
        string addressesText,
        IEnumerable<BrokerAssistant> assistants,
        bool requiresReview,
        string? reviewNote,
        out List<string> addresses,
        bool requirePrimaryEmail = true)
    {
        var errors = new List<string>();
        addresses = [];
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("El nombre del corredor es obligatorio.");
        }

        TryParseAddresses(addressesText, requirePrimaryEmail && !requiresReview,
            out addresses, out var addressErrors, rejectDuplicates: true);
        errors.AddRange(addressErrors);

        var knownAddresses = new HashSet<string>(addresses, StringComparer.OrdinalIgnoreCase);
        foreach (var assistant in assistants)
        {
            if (string.IsNullOrWhiteSpace(assistant.Name))
            {
                errors.Add("El nombre de cada asistente es obligatorio.");
            }

            if (!TryNormalizeAddress(assistant.Email, out var normalizedAssistant))
            {
                errors.Add($"El correo del asistente '{assistant.Name}' no es válido.");
                continue;
            }

            if (!knownAddresses.Add(normalizedAssistant))
            {
                errors.Add($"La dirección '{normalizedAssistant}' está duplicada entre el corredor y sus asistentes.");
            }
        }

        if (requiresReview && string.IsNullOrWhiteSpace(reviewNote))
        {
            errors.Add("Indique una nota cuando el corredor requiere revisión.");
        }

        return errors;
    }

    public RecipientResolutionResult ResolveRecipients(Broker broker, string? commonCcText)
    {
        var errors = new List<string>();
        TryParseAddresses(string.Join(";", broker.PrimaryEmailAddresses), true,
            out var primaryRecipients, out var primaryErrors, rejectDuplicates: true);
        errors.AddRange(primaryErrors.Select(error => $"Correo principal: {error}"));

        var assistantRecipients = new List<string>();
        var toSet = new HashSet<string>(primaryRecipients, StringComparer.OrdinalIgnoreCase);
        foreach (var assistant in broker.Assistants.Where(value => value.IsActive))
        {
            if (string.IsNullOrWhiteSpace(assistant.Name))
            {
                errors.Add("Hay un asistente activo sin nombre.");
                continue;
            }

            if (!TryNormalizeAddress(assistant.Email, out var normalized))
            {
                errors.Add($"El correo del asistente '{assistant.Name}' no es válido.");
                continue;
            }

            if (!toSet.Add(normalized))
            {
                errors.Add($"La dirección '{normalized}' está duplicada entre el corredor y sus asistentes.");
                continue;
            }

            assistantRecipients.Add(normalized);
        }

        TryParseAddresses(commonCcText, false, out var parsedCc, out var ccErrors);
        errors.AddRange(ccErrors.Select(error => $"Copias generales: {error}"));
        var finalCc = parsedCc
            .Where(address => !toSet.Contains(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RecipientResolutionResult
        {
            BrokerPrimaryRecipients = primaryRecipients,
            AssistantRecipients = assistantRecipients,
            ToRecipients = [.. primaryRecipients, .. assistantRecipients],
            CcRecipients = finalCc,
            Errors = errors
        };
    }

    public List<string> ValidateRequest(EmailSendRequest request)
    {
        var errors = new List<string>();
        if (request.ToRecipients.Count == 0)
        {
            errors.Add("Debe existir al menos un destinatario principal.");
        }

        ValidateAddressList(request.ToRecipients, "Para", errors);
        ValidateAddressList(request.CcRecipients, "CC", errors);

        var toSet = new HashSet<string>(request.ToRecipients, StringComparer.OrdinalIgnoreCase);
        foreach (var duplicate in request.CcRecipients.Where(toSet.Contains).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"La dirección '{duplicate}' está repetida entre Para y CC.");
        }

        if (string.IsNullOrWhiteSpace(request.Subject))
        {
            errors.Add("El asunto no puede estar vacío.");
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            errors.Add("El mensaje no puede estar vacío.");
        }

        if (request.RequiresReview && !request.ReviewConfirmed)
        {
            errors.Add(string.IsNullOrWhiteSpace(request.ReviewNote)
                ? "El corredor requiere revisión antes de enviar."
                : $"El corredor requiere revisión: {request.ReviewNote}");
        }

        if (!string.IsNullOrWhiteSpace(request.SignatureImagePath))
        {
            try
            {
                _ = SignatureImageService.ValidateFile(request.SignatureImagePath);
            }
            catch (SignatureImageException ex)
            {
                errors.Add($"Firma del correo: {ex.Message}");
            }
        }

        if (request.AttachmentPaths.Count == 0)
        {
            errors.Add("Debe adjuntar al menos un archivo de Excel.");
        }

        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in request.AttachmentPaths)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                errors.Add($"La ruta del archivo '{path}' no es válida.");
                continue;
            }

            if (!seenFiles.Add(fullPath))
            {
                errors.Add($"El archivo '{Path.GetFileName(path)}' está adjunto más de una vez.");
                continue;
            }

            if (!AllowedExtensions.Contains(Path.GetExtension(fullPath)))
            {
                errors.Add($"El archivo '{Path.GetFileName(path)}' no es un archivo Excel permitido (.xlsx o .xls).");
                continue;
            }

            if (!File.Exists(fullPath))
            {
                errors.Add($"No existe el archivo adjunto '{Path.GetFileName(path)}'.");
                continue;
            }

            try
            {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _ = stream.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"No se puede leer el archivo '{Path.GetFileName(path)}': {ex.Message}");
            }
        }

        return errors;
    }

    public static bool IsAllowedExcelFile(string path) => AllowedExtensions.Contains(Path.GetExtension(path));

    public static bool TryNormalizeAddress(string candidate, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            var trimmed = candidate.Trim();
            var parsed = new MailAddress(trimmed);
            if (!string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            normalized = parsed.Address;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidateAddressList(IReadOnlyList<string> addresses, string fieldName, ICollection<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in addresses)
        {
            if (!TryNormalizeAddress(address, out var normalized))
            {
                errors.Add($"La dirección '{address}' de {fieldName} no es válida.");
            }
            else if (!seen.Add(normalized))
            {
                errors.Add($"La dirección '{address}' está duplicada en {fieldName}.");
            }
        }
    }
}
