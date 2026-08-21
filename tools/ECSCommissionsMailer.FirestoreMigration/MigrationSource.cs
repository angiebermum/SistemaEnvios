using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ECS.CommissionsMailer.Models;

namespace ECSCommissionsMailer.FirestoreMigration;

public sealed record SourceFileDescriptor(
    string Name,
    string OriginalPath,
    long Size,
    string Sha256);

public sealed record LoadedRecentSend(SentEmailRecord Value, bool HadStableId);

public sealed class MigrationSource
{
    public required string Directory { get; init; }
    public required AppConfiguration Configuration { get; init; }
    public required CurrentSession Session { get; init; }
    public required IReadOnlyList<LoadedRecentSend> RecentSends { get; init; }
    public required IReadOnlyList<PaymentGenerationBatch> PaymentGenerations { get; init; }
    public required IReadOnlyList<SourceFileDescriptor> Files { get; init; }
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
}

public static class MigrationSourceLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static MigrationSource Load(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("--source-directory es obligatorio.", nameof(sourceDirectory));
        }

        var directory = Path.GetFullPath(sourceDirectory);
        if (!System.IO.Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"No existe el directorio fuente '{directory}'.");
        }

        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var descriptors = new List<SourceFileDescriptor>();
        foreach (var name in MigrationConstants.RequiredSourceFiles)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Falta el JSON productivo obligatorio '{name}'.", path);
            }

            var bytes = File.ReadAllBytes(path);
            contents[name] = System.Text.Encoding.UTF8.GetString(bytes);
            descriptors.Add(new SourceFileDescriptor(
                name,
                path,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes))));
        }

        try
        {
            var configurationDocument = JsonDocument.Parse(contents["configuracion.json"]);
            var sessionDocument = JsonDocument.Parse(contents["sesion-actual.json"]);
            var recentSendsDocument = JsonDocument.Parse(contents["envios-recientes.json"]);
            var generationsDocument = JsonDocument.Parse(contents["generaciones-detalles-pago.json"]);

            using (configurationDocument)
            using (sessionDocument)
            using (recentSendsDocument)
            using (generationsDocument)
            {
                var configuration = DeserializeRequired<AppConfiguration>(
                    contents["configuracion.json"], "configuracion.json");
                var session = DeserializeRequired<CurrentSession>(
                    contents["sesion-actual.json"], "sesion-actual.json");
                var recentSends = DeserializeRequired<List<SentEmailRecord>>(
                    contents["envios-recientes.json"], "envios-recientes.json");
                var generations = DeserializeRequired<List<PaymentGenerationBatch>>(
                    contents["generaciones-detalles-pago.json"], "generaciones-detalles-pago.json");

                var source = new MigrationSource
                {
                    Directory = directory,
                    Configuration = configuration,
                    Session = session,
                    RecentSends = BindRecentSendIds(recentSendsDocument.RootElement, recentSends),
                    PaymentGenerations = generations,
                    Files = descriptors
                };

                BindRequiredConfigurationIds(configurationDocument.RootElement, source);
                BindRequiredSessionIdentity(sessionDocument.RootElement, source);
                BindRequiredRecentSendIdentity(recentSendsDocument.RootElement, source);
                BindRequiredGenerationIdentity(generationsDocument.RootElement, source);
                return source;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Uno de los cuatro JSON productivos no es válido: {exception.Message}", exception);
        }
    }

    private static T DeserializeRequired<T>(string json, string name) where T : class =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException($"'{name}' contiene null en vez del objeto/arreglo requerido.");

    private static IReadOnlyList<LoadedRecentSend> BindRecentSendIds(
        JsonElement root,
        IReadOnlyList<SentEmailRecord> records)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != records.Count)
        {
            throw new InvalidDataException("envios-recientes.json debe ser un arreglo JSON.");
        }

        var result = new List<LoadedRecentSend>(records.Count);
        var index = 0;
        foreach (var element in root.EnumerateArray())
        {
            var record = records[index];
            var hasId = TryReadGuid(element, "Id", out var id, allowMissing: true);
            record.Id = hasId ? id : Guid.Empty;
            result.Add(new LoadedRecentSend(record, hasId));
            index++;
        }

        return result;
    }

    private static void BindRequiredConfigurationIds(JsonElement root, MigrationSource source)
    {
        if (!TryGetProperty(root, "Brokers", out var brokersElement) || brokersElement.ValueKind != JsonValueKind.Array)
        {
            source.Errors.Add("configuracion.json: Brokers debe ser un arreglo.");
            return;
        }

        var rawBrokers = brokersElement.EnumerateArray().ToList();
        var brokers = source.Configuration.Brokers ?? [];
        if (rawBrokers.Count != brokers.Count)
        {
            source.Errors.Add("configuracion.json: no fue posible alinear los Brokers deserializados.");
            return;
        }

        for (var brokerIndex = 0; brokerIndex < brokers.Count; brokerIndex++)
        {
            var broker = brokers[brokerIndex];
            broker.Id = ReadRequiredGuid(rawBrokers[brokerIndex], "Id", $"Brokers[{brokerIndex}]", source);
            BindChildIds(rawBrokers[brokerIndex], "Assistants", broker.Assistants, $"Brokers[{brokerIndex}].Assistants", source,
                (assistant, id) => assistant.Id = id);
            BindChildIds(rawBrokers[brokerIndex], "Deductions", broker.Deductions, $"Brokers[{brokerIndex}].Deductions", source,
                (deduction, id) => deduction.Id = id);
        }
    }

    private static void BindRequiredSessionIdentity(JsonElement root, MigrationSource source)
    {
        RequireProperty(root, "SavedAt", "sesion-actual.json", source);
        if (!TryGetProperty(root, "BrokerItems", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
        {
            source.Errors.Add("sesion-actual.json: BrokerItems debe ser un arreglo.");
            return;
        }

        var rawItems = itemsElement.EnumerateArray().ToList();
        var items = source.Session.BrokerItems ?? [];
        if (rawItems.Count != items.Count)
        {
            source.Errors.Add("sesion-actual.json: no fue posible alinear los BrokerItems deserializados.");
            return;
        }

        for (var index = 0; index < items.Count; index++)
        {
            items[index].BrokerId = ReadRequiredGuid(rawItems[index], "BrokerId", $"BrokerItems[{index}]", source);
        }
    }

    private static void BindRequiredGenerationIdentity(JsonElement root, MigrationSource source)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != source.PaymentGenerations.Count)
        {
            source.Errors.Add("generaciones-detalles-pago.json debe ser un arreglo.");
            return;
        }

        var rawGenerations = root.EnumerateArray().ToList();
        for (var generationIndex = 0; generationIndex < source.PaymentGenerations.Count; generationIndex++)
        {
            var generation = source.PaymentGenerations[generationIndex];
            var rawGeneration = rawGenerations[generationIndex];
            generation.Id = ReadRequiredGuid(rawGeneration, "Id", $"Generations[{generationIndex}]", source);
            RequireProperty(rawGeneration, "CreatedAt", $"Generations[{generationIndex}]", source);

            if (!TryGetProperty(rawGeneration, "Files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
            {
                source.Errors.Add($"Generations[{generationIndex}].Files debe ser un arreglo.");
                continue;
            }

            var rawFiles = filesElement.EnumerateArray().ToList();
            var files = generation.Files ?? [];
            if (rawFiles.Count != files.Count)
            {
                source.Errors.Add($"Generations[{generationIndex}]: no fue posible alinear Files.");
                continue;
            }

            for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
            {
                files[fileIndex].BrokerId = ReadRequiredGuid(
                    rawFiles[fileIndex], "BrokerId", $"Generations[{generationIndex}].Files[{fileIndex}]", source);
                RequireProperty(
                    rawFiles[fileIndex], "GeneratedAt", $"Generations[{generationIndex}].Files[{fileIndex}]", source);
            }
        }
    }

    private static void BindRequiredRecentSendIdentity(JsonElement root, MigrationSource source)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != source.RecentSends.Count)
        {
            source.Errors.Add("envios-recientes.json debe ser un arreglo.");
            return;
        }

        var index = 0;
        foreach (var rawSend in root.EnumerateArray())
        {
            RequireProperty(rawSend, "SentAt", $"RecentSends[{index}]", source);
            index++;
        }
    }

    private static void BindChildIds<T>(
        JsonElement parent,
        string propertyName,
        IReadOnlyList<T>? values,
        string path,
        MigrationSource source,
        Action<T, Guid> assign)
    {
        if (!TryGetProperty(parent, propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            if ((values?.Count ?? 0) > 0)
            {
                source.Errors.Add($"{path} debe ser un arreglo.");
            }

            return;
        }

        var rawValues = array.EnumerateArray().ToList();
        var typedValues = values ?? [];
        if (rawValues.Count != typedValues.Count)
        {
            source.Errors.Add($"{path}: no fue posible alinear los elementos deserializados.");
            return;
        }

        for (var index = 0; index < typedValues.Count; index++)
        {
            assign(typedValues[index], ReadRequiredGuid(rawValues[index], "Id", $"{path}[{index}]", source));
        }
    }

    private static Guid ReadRequiredGuid(JsonElement element, string propertyName, string path, MigrationSource source)
    {
        try
        {
            if (TryReadGuid(element, propertyName, out var id, allowMissing: false))
            {
                return id;
            }
        }
        catch (InvalidDataException exception)
        {
            source.Errors.Add($"{path}.{propertyName}: {exception.Message}");
            return Guid.Empty;
        }

        source.Errors.Add($"{path}.{propertyName}: falta un UUID estable no vacío.");
        return Guid.Empty;
    }

    private static bool TryReadGuid(JsonElement element, string name, out Guid id, bool allowMissing)
    {
        id = Guid.Empty;
        if (!TryGetProperty(element, name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
        {
            if (!allowMissing)
            {
                return false;
            }

            return false;
        }

        if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out id))
        {
            throw new InvalidDataException($"{name} no es un UUID válido no vacío.");
        }

        if (id == Guid.Empty)
        {
            if (allowMissing)
            {
                return false;
            }

            throw new InvalidDataException($"{name} no es un UUID válido no vacío.");
        }

        return true;
    }

    private static void RequireProperty(JsonElement element, string name, string path, MigrationSource source)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            source.Errors.Add($"{path}.{name}: falta el timestamp persistido.");
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
