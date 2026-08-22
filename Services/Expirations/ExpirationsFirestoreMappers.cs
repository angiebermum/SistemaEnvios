using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

internal static class ExpirationsFirestoreFields
{
    public static FirestoreRestValue Guid(Guid value) => FirestoreRestValue.String(value.ToString("D"));

    public static Guid ReadGuid(FirestoreRestValue value, string name) =>
        System.Guid.TryParse(value.RequireString(name), out var parsed)
            ? parsed
            : throw new InvalidDataException($"El campo '{name}' no contiene un UUID válido.");

    public static FirestoreRestValue Strings(IEnumerable<string>? values) =>
        FirestoreRestValue.Array((values ?? []).Select(FirestoreRestValue.String));

    public static List<string> ReadStrings(FirestoreRestValue value, string name) =>
        value.RequireArray(name)
            .Select((item, index) => item.RequireString($"{name}[{index}]"))
            .ToList();

    public static FirestoreRestValue Assistant(ExpirationsAssistant value) =>
        FirestoreRestValue.Map(new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["id"] = Guid(value.Id),
            ["name"] = FirestoreRestValue.String(value.Name),
            ["email"] = FirestoreRestValue.String(value.Email),
            ["isActive"] = FirestoreRestValue.Boolean(value.IsActive)
        });

    public static ExpirationsAssistant ReadAssistant(FirestoreRestValue value, string name)
    {
        var fields = value.RequireMap(name);
        return new ExpirationsAssistant
        {
            Id = ReadGuid(fields.Required("id"), $"{name}.id"),
            Name = fields.Required("name").RequireString($"{name}.name"),
            Email = fields.Required("email").RequireString($"{name}.email"),
            IsActive = fields.Required("isActive").RequireBoolean($"{name}.isActive")
        };
    }
}

internal sealed class ExpirationsBrokerDirectoryMapper : IFirestoreEntityMapper<ExpirationsBrokerDirectoryEntry>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(ExpirationsBrokerDirectoryEntry value) =>
        throw new NotSupportedException("El directorio compartido de corredores para Vencimientos es de solo lectura.");

    public ExpirationsBrokerDirectoryEntry FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) => new()
    {
        BrokerId = ExpirationsFirestoreFields.ReadGuid(fields.Required("id"), "id"),
        Name = fields.Required("name").RequireString("name"),
        PrimaryEmailAddresses = ExpirationsFirestoreFields.ReadStrings(
            fields.Required("primaryEmailAddresses"),
            "primaryEmailAddresses")
    };
}

internal sealed class ExpirationsBrokerProfileMapper : IFirestoreEntityMapper<ExpirationsBrokerProfile>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(ExpirationsBrokerProfile value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["brokerId"] = ExpirationsFirestoreFields.Guid(value.BrokerId),
            ["isActive"] = FirestoreRestValue.Boolean(value.IsActive),
            ["nextMonthGenerationMode"] = FirestoreRestValue.String(WriteNextMonthGenerationMode(value.NextMonthGenerationMode)),
            ["assistants"] = FirestoreRestValue.Array(value.Assistants.Select(ExpirationsFirestoreFields.Assistant)),
            ["createdAtUtc"] = FirestoreRestValue.Timestamp(value.CreatedAtUtc),
            ["updatedAtUtc"] = FirestoreRestValue.Timestamp(value.UpdatedAtUtc)
        };

    public ExpirationsBrokerProfile FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) => new()
    {
        BrokerId = ExpirationsFirestoreFields.ReadGuid(fields.Required("brokerId"), "brokerId"),
        IsActive = fields.Required("isActive").RequireBoolean("isActive"),
        NextMonthGenerationMode = ReadNextMonthGenerationMode(fields),
        Assistants = fields.Required("assistants").RequireArray("assistants")
            .Select((item, index) => ExpirationsFirestoreFields.ReadAssistant(item, $"assistants[{index}]"))
            .ToList(),
        CreatedAtUtc = fields.Required("createdAtUtc").RequireTimestamp("createdAtUtc"),
        UpdatedAtUtc = fields.Required("updatedAtUtc").RequireTimestamp("updatedAtUtc")
    };

    private static ExpirationsNextMonthGenerationMode ReadNextMonthGenerationMode(
        IReadOnlyDictionary<string, FirestoreRestValue> fields)
    {
        if (!fields.TryGetValue("nextMonthGenerationMode", out var value))
            return ExpirationsNextMonthGenerationMode.Standard;
        return value.RequireString("nextMonthGenerationMode") switch
        {
            nameof(ExpirationsNextMonthGenerationMode.Standard) =>
                ExpirationsNextMonthGenerationMode.Standard,
            nameof(ExpirationsNextMonthGenerationMode.SpecialDualSorted) =>
                ExpirationsNextMonthGenerationMode.SpecialDualSorted,
            _ => throw new InvalidDataException(
                "El campo 'nextMonthGenerationMode' contiene un modo no permitido.")
        };
    }

    private static string WriteNextMonthGenerationMode(ExpirationsNextMonthGenerationMode value) => value switch
    {
        ExpirationsNextMonthGenerationMode.Standard => nameof(ExpirationsNextMonthGenerationMode.Standard),
        ExpirationsNextMonthGenerationMode.SpecialDualSorted =>
            nameof(ExpirationsNextMonthGenerationMode.SpecialDualSorted),
        _ => throw new InvalidDataException("El modo de generación de mes siguiente no está permitido.")
    };
}

internal sealed class ExpirationsBrokerAssociationMapper : IFirestoreEntityMapper<ExpirationsBrokerAssociation>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(ExpirationsBrokerAssociation value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["id"] = ExpirationsFirestoreFields.Guid(value.Id),
            ["brokerId"] = ExpirationsFirestoreFields.Guid(value.BrokerId),
            ["kind"] = FirestoreRestValue.String(value.Kind.ToString()),
            ["value"] = FirestoreRestValue.String(value.Value),
            ["normalizedValue"] = FirestoreRestValue.String(value.NormalizedValue),
            ["isActive"] = FirestoreRestValue.Boolean(value.IsActive),
            ["createdAtUtc"] = FirestoreRestValue.Timestamp(value.CreatedAtUtc),
            ["updatedAtUtc"] = FirestoreRestValue.Timestamp(value.UpdatedAtUtc)
        };

    public ExpirationsBrokerAssociation FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields)
    {
        var kindText = fields.Required("kind").RequireString("kind");
        var kind = kindText switch
        {
            nameof(ExpirationsAssociationKind.Name) => ExpirationsAssociationKind.Name,
            nameof(ExpirationsAssociationKind.Alias) => ExpirationsAssociationKind.Alias,
            nameof(ExpirationsAssociationKind.Code) => ExpirationsAssociationKind.Code,
            _ => throw new InvalidDataException(
                "El campo 'kind' contiene un tipo de asociación no permitido.")
        };

        return new ExpirationsBrokerAssociation
        {
            Id = ExpirationsFirestoreFields.ReadGuid(fields.Required("id"), "id"),
            BrokerId = ExpirationsFirestoreFields.ReadGuid(fields.Required("brokerId"), "brokerId"),
            Kind = kind,
            Value = fields.Required("value").RequireString("value"),
            NormalizedValue = fields.Required("normalizedValue").RequireString("normalizedValue"),
            IsActive = fields.Required("isActive").RequireBoolean("isActive"),
            CreatedAtUtc = fields.Required("createdAtUtc").RequireTimestamp("createdAtUtc"),
            UpdatedAtUtc = fields.Required("updatedAtUtc").RequireTimestamp("updatedAtUtc")
        };
    }
}

internal sealed class ExpirationsProcessSettingsMapper : IFirestoreEntityMapper<ExpirationsProcessSettings>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(ExpirationsProcessSettings value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["defaultSubject"] = FirestoreRestValue.String(value.DefaultSubject),
            ["defaultMessage"] = FirestoreRestValue.String(value.DefaultMessage),
            ["commonCcAddresses"] = ExpirationsFirestoreFields.Strings(value.CommonCcAddresses),
            ["updatedAtUtc"] = FirestoreRestValue.Timestamp(value.UpdatedAtUtc)
        };

    public ExpirationsProcessSettings FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) => new()
    {
        DefaultSubject = fields.Required("defaultSubject").RequireString("defaultSubject"),
        DefaultMessage = fields.Required("defaultMessage").RequireString("defaultMessage"),
        CommonCcAddresses = ExpirationsFirestoreFields.ReadStrings(
            fields.Required("commonCcAddresses"),
            "commonCcAddresses"),
        UpdatedAtUtc = fields.Required("updatedAtUtc").RequireTimestamp("updatedAtUtc")
    };
}
