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

    public static FirestoreRestValue NullableGuid(Guid? value) =>
        value is { } id ? Guid(id) : FirestoreRestValue.Null();

    public static Guid? ReadNullableGuid(FirestoreRestValue value, string name) =>
        value.IsNull ? null : ReadGuid(value, name);

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

internal sealed class ExpirationsSendOperationMapper : IFirestoreEntityMapper<ExpirationsSendOperation>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(ExpirationsSendOperation value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["operationId"] = ExpirationsFirestoreFields.Guid(value.OperationId),
            ["process"] = FirestoreRestValue.String(value.Process.ToString()),
            ["startedAtUtc"] = FirestoreRestValue.Timestamp(value.StartedAtUtc),
            ["completedAtUtc"] = value.CompletedAtUtc is { } completed
                ? FirestoreRestValue.Timestamp(completed)
                : FirestoreRestValue.Null(),
            ["sendingAccountEmail"] = FirestoreRestValue.String(value.SendingAccountEmail),
            ["subject"] = FirestoreRestValue.String(value.Subject),
            ["body"] = FirestoreRestValue.String(value.Body),
            ["status"] = FirestoreRestValue.String(value.Status.ToString()),
            ["totalCount"] = FirestoreRestValue.Integer(value.TotalCount),
            ["successCount"] = FirestoreRestValue.Integer(value.SuccessCount),
            ["failureCount"] = FirestoreRestValue.Integer(value.FailureCount),
            ["retryOfOperationId"] = ExpirationsFirestoreFields.NullableGuid(value.RetryOfOperationId)
        };

    public ExpirationsSendOperation FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) => new()
    {
        OperationId = ExpirationsFirestoreFields.ReadGuid(fields.Required("operationId"), "operationId"),
        Process = ReadProcess(fields.Required("process").RequireString("process")),
        StartedAtUtc = fields.Required("startedAtUtc").RequireTimestamp("startedAtUtc"),
        CompletedAtUtc = fields.Required("completedAtUtc").IsNull
            ? null
            : fields.Required("completedAtUtc").RequireTimestamp("completedAtUtc"),
        SendingAccountEmail = fields.Required("sendingAccountEmail").RequireString("sendingAccountEmail"),
        Subject = fields.Required("subject").RequireString("subject"),
        Body = fields.Required("body").RequireString("body"),
        Status = ReadStatus(fields.Required("status").RequireString("status")),
        TotalCount = checked((int)fields.Required("totalCount").RequireInteger("totalCount")),
        SuccessCount = checked((int)fields.Required("successCount").RequireInteger("successCount")),
        FailureCount = checked((int)fields.Required("failureCount").RequireInteger("failureCount")),
        RetryOfOperationId = ExpirationsFirestoreFields.ReadNullableGuid(
            fields.Required("retryOfOperationId"),
            "retryOfOperationId")
    };

    private static ExpirationsProcess ReadProcess(string value) => value switch
    {
        nameof(ExpirationsProcess.PreviousMonth) => ExpirationsProcess.PreviousMonth,
        nameof(ExpirationsProcess.NextMonth) => ExpirationsProcess.NextMonth,
        nameof(ExpirationsProcess.Cancellations) => ExpirationsProcess.Cancellations,
        _ => throw new InvalidDataException("El proceso del historial de Vencimientos no está permitido.")
    };

    private static ExpirationsSendOperationStatus ReadStatus(string value) => value switch
    {
        nameof(ExpirationsSendOperationStatus.InProgress) => ExpirationsSendOperationStatus.InProgress,
        nameof(ExpirationsSendOperationStatus.Completed) => ExpirationsSendOperationStatus.Completed,
        _ => throw new InvalidDataException("El estado de la operación de Vencimientos no está permitido.")
    };
}

internal sealed class ExpirationsSendHistoryItemMapper : IFirestoreEntityMapper<ExpirationsSendHistoryItem>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(ExpirationsSendHistoryItem value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["itemId"] = ExpirationsFirestoreFields.Guid(value.ItemId),
            ["requestId"] = ExpirationsFirestoreFields.Guid(value.RequestId),
            ["brokerId"] = ExpirationsFirestoreFields.Guid(value.BrokerId),
            ["brokerName"] = FirestoreRestValue.String(value.BrokerName),
            ["toRecipients"] = ExpirationsFirestoreFields.Strings(value.ToRecipients),
            ["ccRecipients"] = ExpirationsFirestoreFields.Strings(value.CcRecipients),
            ["attachments"] = FirestoreRestValue.Array(value.Attachments.Select(WriteAttachment)),
            ["status"] = FirestoreRestValue.String(value.Status.ToString()),
            ["errorMessage"] = FirestoreRestValue.String(value.ErrorMessage),
            ["retryOfItemId"] = ExpirationsFirestoreFields.NullableGuid(value.RetryOfItemId)
        };

    public ExpirationsSendHistoryItem FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) => new()
    {
        ItemId = ExpirationsFirestoreFields.ReadGuid(fields.Required("itemId"), "itemId"),
        RequestId = ExpirationsFirestoreFields.ReadGuid(fields.Required("requestId"), "requestId"),
        BrokerId = ExpirationsFirestoreFields.ReadGuid(fields.Required("brokerId"), "brokerId"),
        BrokerName = fields.Required("brokerName").RequireString("brokerName"),
        ToRecipients = ExpirationsFirestoreFields.ReadStrings(fields.Required("toRecipients"), "toRecipients"),
        CcRecipients = ExpirationsFirestoreFields.ReadStrings(fields.Required("ccRecipients"), "ccRecipients"),
        Attachments = fields.Required("attachments").RequireArray("attachments")
            .Select((value, index) => ReadAttachment(value, index))
            .ToList(),
        Status = ReadStatus(fields.Required("status").RequireString("status")),
        ErrorMessage = fields.Required("errorMessage").RequireString("errorMessage"),
        RetryOfItemId = ExpirationsFirestoreFields.ReadNullableGuid(
            fields.Required("retryOfItemId"),
            "retryOfItemId")
    };

    private static FirestoreRestValue WriteAttachment(ExpirationsSendAttachment value) =>
        FirestoreRestValue.Map(new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["fileName"] = FirestoreRestValue.String(value.FileName),
            ["sha256"] = FirestoreRestValue.String(value.Sha256),
            ["variant"] = FirestoreRestValue.String(value.Variant.ToString())
        });

    private static ExpirationsSendAttachment ReadAttachment(FirestoreRestValue value, int index)
    {
        var name = $"attachments[{index}]";
        var fields = value.RequireMap(name);
        return new ExpirationsSendAttachment(
            fields.Required("fileName").RequireString($"{name}.fileName"),
            fields.Required("sha256").RequireString($"{name}.sha256"),
            ReadVariant(fields.Required("variant").RequireString($"{name}.variant")));
    }

    private static ExpirationsGeneratedFileVariant ReadVariant(string value) => value switch
    {
        nameof(ExpirationsGeneratedFileVariant.Standard) => ExpirationsGeneratedFileVariant.Standard,
        nameof(ExpirationsGeneratedFileVariant.FelixAlphabetical) => ExpirationsGeneratedFileVariant.FelixAlphabetical,
        nameof(ExpirationsGeneratedFileVariant.FelixExpirationDate) => ExpirationsGeneratedFileVariant.FelixExpirationDate,
        nameof(ExpirationsGeneratedFileVariant.Manual) => ExpirationsGeneratedFileVariant.Manual,
        _ => throw new InvalidDataException("La variante de adjunto del historial no está permitida.")
    };

    private static ExpirationsSendItemStatus ReadStatus(string value) => value switch
    {
        nameof(ExpirationsSendItemStatus.Pending) => ExpirationsSendItemStatus.Pending,
        nameof(ExpirationsSendItemStatus.Succeeded) => ExpirationsSendItemStatus.Succeeded,
        nameof(ExpirationsSendItemStatus.Failed) => ExpirationsSendItemStatus.Failed,
        _ => throw new InvalidDataException("El estado del item de Vencimientos no está permitido.")
    };
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
            ["cancellationAssistants"] = FirestoreRestValue.Array(
                value.CancellationAssistants.Select(ExpirationsFirestoreFields.Assistant)),
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
        CancellationAssistants = fields.TryGetValue("cancellationAssistants", out var cancellationAssistants)
            ? cancellationAssistants.RequireArray("cancellationAssistants")
                .Select((item, index) => ExpirationsFirestoreFields.ReadAssistant(
                    item,
                    $"cancellationAssistants[{index}]"))
                .ToList()
            : [],
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
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(ExpirationsBrokerAssociation value)
    {
        var fields = new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["id"] = ExpirationsFirestoreFields.Guid(value.Id),
            ["brokerId"] = ExpirationsFirestoreFields.Guid(value.BrokerId),
            ["kind"] = FirestoreRestValue.String(value.Kind.ToString()),
            ["value"] = FirestoreRestValue.String(value.Value),
            ["normalizedValue"] = FirestoreRestValue.String(value.NormalizedValue),
            ["origin"] = FirestoreRestValue.String(value.Origin.ToString()),
            ["isActive"] = FirestoreRestValue.Boolean(value.IsActive),
            ["createdAtUtc"] = FirestoreRestValue.Timestamp(value.CreatedAtUtc),
            ["updatedAtUtc"] = FirestoreRestValue.Timestamp(value.UpdatedAtUtc)
        };
        if (value.DestinationGroup is { } destinationGroup)
            fields["destinationGroup"] = FirestoreRestValue.String(destinationGroup.ToString());
        return fields;
    }

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
            Origin = ReadAssociationOrigin(fields),
            DestinationGroup = ReadDestinationGroup(fields),
            IsActive = fields.Required("isActive").RequireBoolean("isActive"),
            CreatedAtUtc = fields.Required("createdAtUtc").RequireTimestamp("createdAtUtc"),
            UpdatedAtUtc = fields.Required("updatedAtUtc").RequireTimestamp("updatedAtUtc")
        };
    }

    private static ExpirationsDestinationGroup? ReadDestinationGroup(
        IReadOnlyDictionary<string, FirestoreRestValue> fields)
    {
        if (!fields.TryGetValue("destinationGroup", out var value))
            return null;
        return value.RequireString("destinationGroup") switch
        {
            nameof(ExpirationsDestinationGroup.Principal) => ExpirationsDestinationGroup.Principal,
            nameof(ExpirationsDestinationGroup.Personales) => ExpirationsDestinationGroup.Personales,
            nameof(ExpirationsDestinationGroup.Generales) => ExpirationsDestinationGroup.Generales,
            nameof(ExpirationsDestinationGroup.Agencias) => ExpirationsDestinationGroup.Agencias,
            nameof(ExpirationsDestinationGroup.PcGuanacaste) => ExpirationsDestinationGroup.PcGuanacaste,
            nameof(ExpirationsDestinationGroup.ContadoCoriMotors) => ExpirationsDestinationGroup.ContadoCoriMotors,
            nameof(ExpirationsDestinationGroup.VariosCoriMotors) => ExpirationsDestinationGroup.VariosCoriMotors,
            nameof(ExpirationsDestinationGroup.HernanVarela) => ExpirationsDestinationGroup.HernanVarela,
            _ => throw new InvalidDataException("El campo 'destinationGroup' contiene un destino no permitido.")
        };
    }

    private static ExpirationsAssociationOrigin ReadAssociationOrigin(
        IReadOnlyDictionary<string, FirestoreRestValue> fields)
    {
        if (!fields.TryGetValue("origin", out var value))
            return ExpirationsAssociationOrigin.Confirmed;
        return value.RequireString("origin") switch
        {
            nameof(ExpirationsAssociationOrigin.Confirmed) => ExpirationsAssociationOrigin.Confirmed,
            nameof(ExpirationsAssociationOrigin.Imported) => ExpirationsAssociationOrigin.Imported,
            nameof(ExpirationsAssociationOrigin.ManuallyConfirmed) => ExpirationsAssociationOrigin.ManuallyConfirmed,
            nameof(ExpirationsAssociationOrigin.ManuallyAdded) => ExpirationsAssociationOrigin.ManuallyAdded,
            _ => throw new InvalidDataException("El campo 'origin' contiene un origen no permitido.")
        };
    }
}

internal sealed class ExpirationsObservedIdentifierMapper : IFirestoreEntityMapper<ExpirationsObservedIdentifier>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(ExpirationsObservedIdentifier value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["id"] = ExpirationsFirestoreFields.Guid(value.Id),
            ["brokerId"] = ExpirationsFirestoreFields.Guid(value.BrokerId),
            ["kind"] = FirestoreRestValue.String(value.Kind.ToString()),
            ["value"] = FirestoreRestValue.String(value.Value),
            ["normalizedValue"] = FirestoreRestValue.String(value.NormalizedValue),
            ["firstSeenAtUtc"] = FirestoreRestValue.Timestamp(value.FirstSeenAtUtc),
            ["lastSeenAtUtc"] = FirestoreRestValue.Timestamp(value.LastSeenAtUtc),
            ["isIgnored"] = FirestoreRestValue.Boolean(value.IsIgnored)
        };

    public ExpirationsObservedIdentifier FromFields(
        IReadOnlyDictionary<string, FirestoreRestValue> fields) => new()
    {
        Id = ExpirationsFirestoreFields.ReadGuid(fields.Required("id"), "id"),
        BrokerId = ExpirationsFirestoreFields.ReadGuid(fields.Required("brokerId"), "brokerId"),
        Kind = fields.Required("kind").RequireString("kind") switch
        {
            nameof(ExpirationsAssociationKind.Name) => ExpirationsAssociationKind.Name,
            nameof(ExpirationsAssociationKind.Alias) => ExpirationsAssociationKind.Alias,
            nameof(ExpirationsAssociationKind.Code) => ExpirationsAssociationKind.Code,
            _ => throw new InvalidDataException("El campo 'kind' contiene un tipo observado no permitido.")
        },
        Value = fields.Required("value").RequireString("value"),
        NormalizedValue = fields.Required("normalizedValue").RequireString("normalizedValue"),
        FirstSeenAtUtc = fields.Required("firstSeenAtUtc").RequireTimestamp("firstSeenAtUtc"),
        LastSeenAtUtc = fields.Required("lastSeenAtUtc").RequireTimestamp("lastSeenAtUtc"),
        IsIgnored = fields.Required("isIgnored").RequireBoolean("isIgnored")
    };
}

internal sealed class ExpirationsExclusionMapper : IFirestoreEntityMapper<ExpirationsExclusion>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(ExpirationsExclusion value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["id"] = ExpirationsFirestoreFields.Guid(value.Id),
            ["value"] = FirestoreRestValue.String(value.Value),
            ["normalizedValue"] = FirestoreRestValue.String(value.NormalizedValue),
            ["isActive"] = FirestoreRestValue.Boolean(value.IsActive),
            ["createdAtUtc"] = FirestoreRestValue.Timestamp(value.CreatedAtUtc),
            ["updatedAtUtc"] = FirestoreRestValue.Timestamp(value.UpdatedAtUtc)
        };

    public ExpirationsExclusion FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) => new()
    {
        Id = ExpirationsFirestoreFields.ReadGuid(fields.Required("id"), "id"),
        Value = fields.Required("value").RequireString("value"),
        NormalizedValue = fields.Required("normalizedValue").RequireString("normalizedValue"),
        IsActive = fields.Required("isActive").RequireBoolean("isActive"),
        CreatedAtUtc = fields.Required("createdAtUtc").RequireTimestamp("createdAtUtc"),
        UpdatedAtUtc = fields.Required("updatedAtUtc").RequireTimestamp("updatedAtUtc")
    };
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
