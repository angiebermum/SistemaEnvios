using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsRecipientService
{
    public ExpirationsRecipientResolution Resolve(
        ExpirationsBrokerCatalogItem broker,
        IEnumerable<string> commonCcAddresses)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(commonCcAddresses);
        var errors = new List<string>();
        var primary = Normalize(
            broker.PrimaryEmailAddresses,
            $"Correo principal de {broker.Name}",
            errors);
        var toSet = new HashSet<string>(primary, StringComparer.OrdinalIgnoreCase);
        var assistants = new List<string>();
        foreach (var assistant in broker.Assistants.Where(value => value.IsActive))
        {
            if (!EmailValidationService.TryNormalizeAddress(assistant.Email, out var normalized))
            {
                errors.Add($"El correo del asistente activo '{assistant.Name}' de {broker.Name} no es válido.");
                continue;
            }

            if (toSet.Add(normalized))
                assistants.Add(normalized);
        }

        var cc = Normalize(commonCcAddresses, "CC general", errors)
            .Where(address => !toSet.Contains(address))
            .ToList();
        if (toSet.Count == 0)
            errors.Add($"El corredor '{broker.Name}' no tiene ningún destinatario Para válido.");

        return new ExpirationsRecipientResolution
        {
            BrokerPrimaryRecipients = primary,
            AssistantRecipients = assistants,
            ToRecipients = [.. primary, .. assistants],
            CcRecipients = cc,
            Errors = errors.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private static List<string> Normalize(
        IEnumerable<string> values,
        string fieldName,
        ICollection<string> errors)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!EmailValidationService.TryNormalizeAddress(value, out var normalized))
            {
                errors.Add($"{fieldName}: la dirección '{value}' no es válida.");
                continue;
            }

            if (seen.Add(normalized))
                result.Add(normalized);
        }

        return result;
    }
}
