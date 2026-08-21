using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsAssistantValidationService
{
    private const string DuplicateMessage = "Este correo ya pertenece al corredor o a otro asistente.";

    public ExpirationsAssistantEditResult CreateNew(
        string? name,
        string? email,
        bool isActive = true) =>
        ValidateSingle(Guid.NewGuid(), name, email, isActive);

    public ExpirationsAssistantEditResult Edit(
        Guid id,
        string? name,
        string? email,
        bool isActive) =>
        ValidateSingle(id, name, email, isActive);

    public ExpirationsAssistantValidationResult ValidateAndNormalize(
        IEnumerable<string> primaryEmailAddresses,
        IEnumerable<ExpirationsAssistant> assistants)
    {
        ArgumentNullException.ThrowIfNull(primaryEmailAddresses);
        ArgumentNullException.ThrowIfNull(assistants);
        var errors = new List<string>();
        var normalized = new List<ExpirationsAssistant>();
        var knownEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var primaryEmail in primaryEmailAddresses)
        {
            if (EmailValidationService.TryNormalizeAddress(primaryEmail, out var address))
                knownEmails.Add(address);
            else if (!string.IsNullOrWhiteSpace(primaryEmail))
                knownEmails.Add(primaryEmail.Trim());
        }

        foreach (var source in assistants)
        {
            var single = ValidateSingle(source.Id, source.Name, source.Email, source.IsActive);
            if (!single.IsValid || single.Assistant is null)
            {
                errors.AddRange(single.Errors);
                continue;
            }

            if (!knownEmails.Add(single.Assistant.Email))
            {
                errors.Add(DuplicateMessage);
                continue;
            }
            normalized.Add(single.Assistant);
        }

        return new ExpirationsAssistantValidationResult
        {
            Assistants = normalized
                .OrderBy(assistant => assistant.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(assistant => assistant.Email, StringComparer.OrdinalIgnoreCase)
                .ThenBy(assistant => assistant.Id)
                .ToList(),
            Errors = errors.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private static ExpirationsAssistantEditResult ValidateSingle(
        Guid id,
        string? name,
        string? email,
        bool isActive)
    {
        var errors = new List<string>();
        var normalizedName = name?.Trim() ?? string.Empty;
        if (normalizedName.Length == 0)
            errors.Add("El nombre del asistente es obligatorio.");

        var candidateEmail = email?.Trim() ?? string.Empty;
        string normalizedEmail = string.Empty;
        if (candidateEmail.Length == 0)
            errors.Add("El correo del asistente es obligatorio.");
        else if (!EmailValidationService.TryNormalizeAddress(candidateEmail, out normalizedEmail))
            errors.Add("El correo del asistente no es válido.");

        if (errors.Count > 0)
            return new ExpirationsAssistantEditResult { Errors = errors };

        return new ExpirationsAssistantEditResult
        {
            Assistant = new ExpirationsAssistant
            {
                Id = id == Guid.Empty ? Guid.NewGuid() : id,
                Name = normalizedName,
                Email = normalizedEmail,
                IsActive = isActive
            }
        };
    }
}
