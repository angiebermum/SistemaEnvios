using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;

public sealed class FirestoreRestValueJsonConverter : JsonConverter<FirestoreRestValue>
{
    public override bool HandleNull => true;

    public override FirestoreRestValue Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Firestore Value REST debe ser un objeto con exactamente un miembro value_type.");

        FirestoreRestValue? value = null;
        string? activeType = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return value ?? throw new JsonException(
                    "Firestore Value REST no contiene ningún miembro value_type; '{}' es inválido.");
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Firestore Value REST contiene una estructura inválida.");

            var propertyName = reader.GetString() ?? string.Empty;
            if (!reader.Read())
                throw new JsonException($"Firestore Value REST terminó antes del valor de '{propertyName}'.");
            if (activeType is not null)
                throw new JsonException(
                    $"Firestore Value REST contiene más de un miembro del union: '{activeType}' y '{propertyName}'.");

            activeType = propertyName;
            value = propertyName switch
            {
                "nullValue" => ReadNull(ref reader),
                "stringValue" => FirestoreRestValue.String(ReadString(ref reader, propertyName)),
                "booleanValue" => FirestoreRestValue.Boolean(ReadBoolean(ref reader, propertyName)),
                "integerValue" => ReadInteger(ref reader),
                "timestampValue" => ReadTimestamp(ref reader),
                "arrayValue" => ReadArray(ref reader, options),
                "mapValue" => ReadMap(ref reader, options),
                _ => throw new JsonException(
                    $"Firestore Value contiene el tipo REST no soportado '{propertyName}'.")
            };
        }

        throw new JsonException("Firestore Value REST terminó antes de cerrar el objeto.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        FirestoreRestValue value,
        JsonSerializerOptions options)
    {
        if (value is null)
            throw new JsonException("Firestore Value REST no puede ser una referencia null; use FirestoreRestValue.Null().");

        writer.WriteStartObject();
        switch (value.Kind)
        {
            case FirestoreRestValueKind.Null:
                writer.WriteNull("nullValue");
                break;
            case FirestoreRestValueKind.String:
                writer.WriteString("stringValue", value.StringValue ?? throw MissingPayload(value));
                break;
            case FirestoreRestValueKind.Boolean:
                writer.WriteBoolean("booleanValue", value.BooleanValue ?? throw MissingPayload(value));
                break;
            case FirestoreRestValueKind.Integer:
                writer.WriteString("integerValue", value.IntegerValue ?? throw MissingPayload(value));
                break;
            case FirestoreRestValueKind.Timestamp:
                writer.WriteString("timestampValue", value.TimestampValue ?? throw MissingPayload(value));
                break;
            case FirestoreRestValueKind.Array:
                writer.WritePropertyName("arrayValue");
                JsonSerializer.Serialize(writer, value.ArrayValue ?? throw MissingPayload(value), options);
                break;
            case FirestoreRestValueKind.Map:
                writer.WritePropertyName("mapValue");
                JsonSerializer.Serialize(writer, value.MapValue ?? throw MissingPayload(value), options);
                break;
            default:
                throw new JsonException($"Firestore Value contiene un Kind no soportado: {(int)value.Kind}.");
        }
        writer.WriteEndObject();
    }

    private static FirestoreRestValue ReadNull(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Null)
            throw WrongToken("nullValue", "null", reader.TokenType);
        return FirestoreRestValue.Null();
    }

    private static string ReadString(ref Utf8JsonReader reader, string propertyName)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw WrongToken(propertyName, "string", reader.TokenType);
        return reader.GetString()!;
    }

    private static bool ReadBoolean(ref Utf8JsonReader reader, string propertyName)
    {
        if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
            throw WrongToken(propertyName, "boolean", reader.TokenType);
        return reader.GetBoolean();
    }

    private static FirestoreRestValue ReadInteger(ref Utf8JsonReader reader)
    {
        var raw = ReadString(ref reader, "integerValue");
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            throw new JsonException("Firestore Value 'integerValue' no contiene un int64 válido.");
        return FirestoreRestValue.FromIntegerValue(raw);
    }

    private static FirestoreRestValue ReadTimestamp(ref Utf8JsonReader reader)
    {
        var raw = ReadString(ref reader, "timestampValue");
        if (!DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
            throw new JsonException("Firestore Value 'timestampValue' no contiene un timestamp RFC3339 válido.");
        return FirestoreRestValue.FromTimestampValue(raw);
    }

    private static FirestoreRestValue ReadArray(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw WrongToken("arrayValue", "object", reader.TokenType);
        var array = JsonSerializer.Deserialize<FirestoreRestArray>(ref reader, options)
            ?? throw new JsonException("Firestore Value 'arrayValue' no contiene un objeto válido.");
        if (array.Values is null || array.Values.Any(value => value is null))
            throw new JsonException("Firestore Value 'arrayValue' contiene un elemento sin value_type.");
        return FirestoreRestValue.FromArrayValue(array);
    }

    private static FirestoreRestValue ReadMap(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw WrongToken("mapValue", "object", reader.TokenType);
        var map = JsonSerializer.Deserialize<FirestoreRestMap>(ref reader, options)
            ?? throw new JsonException("Firestore Value 'mapValue' no contiene un objeto válido.");
        if (map.Fields is null || map.Fields.Any(field => field.Value is null))
            throw new JsonException("Firestore Value 'mapValue' contiene un campo sin value_type.");
        return FirestoreRestValue.FromMapValue(map);
    }

    private static JsonException WrongToken(string propertyName, string expected, JsonTokenType actual) =>
        new($"Firestore Value '{propertyName}' requiere JSON {expected}; se recibió {actual}.");

    private static JsonException MissingPayload(FirestoreRestValue value) =>
        new($"Firestore Value de tipo '{value.RestTypeName}' no contiene su payload requerido.");
}
