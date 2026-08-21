using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ECS.CommissionsMailer.Infrastructure.Firestore;

namespace ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;

public sealed class FirestoreRestDocument
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public Dictionary<string, FirestoreRestValue> Fields { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("createTime")]
    public string? CreateTime { get; set; }

    [JsonPropertyName("updateTime")]
    public string? UpdateTime { get; set; }

    [JsonIgnore]
    public string DocumentId => Name[(Name.LastIndexOf('/') + 1)..];
}

public enum FirestoreRestValueKind
{
    Null,
    String,
    Boolean,
    Integer,
    Timestamp,
    Array,
    Map
}

[JsonConverter(typeof(FirestoreRestValueJsonConverter))]
public sealed class FirestoreRestValue
{
    private FirestoreRestValue(
        FirestoreRestValueKind kind,
        string? stringValue = null,
        bool? booleanValue = null,
        string? integerValue = null,
        string? timestampValue = null,
        FirestoreRestArray? arrayValue = null,
        FirestoreRestMap? mapValue = null)
    {
        Kind = kind;
        StringValue = stringValue;
        BooleanValue = booleanValue;
        IntegerValue = integerValue;
        TimestampValue = timestampValue;
        ArrayValue = arrayValue;
        MapValue = mapValue;
    }

    public FirestoreRestValueKind Kind { get; }
    public string? StringValue { get; }
    public bool? BooleanValue { get; }
    public string? IntegerValue { get; }
    public string? TimestampValue { get; }
    public FirestoreRestArray? ArrayValue { get; }
    public FirestoreRestMap? MapValue { get; }

    [JsonIgnore]
    public bool IsNull => Kind == FirestoreRestValueKind.Null;

    [JsonIgnore]
    public string RestTypeName => Kind switch
    {
        FirestoreRestValueKind.Null => "nullValue",
        FirestoreRestValueKind.String => "stringValue",
        FirestoreRestValueKind.Boolean => "booleanValue",
        FirestoreRestValueKind.Integer => "integerValue",
        FirestoreRestValueKind.Timestamp => "timestampValue",
        FirestoreRestValueKind.Array => "arrayValue",
        FirestoreRestValueKind.Map => "mapValue",
        _ => "unknown"
    };

    public static FirestoreRestValue Null() => new(FirestoreRestValueKind.Null);
    public static FirestoreRestValue String(string? value) =>
        value is null ? Null() : new(FirestoreRestValueKind.String, stringValue: value);
    public static FirestoreRestValue Boolean(bool value) =>
        new(FirestoreRestValueKind.Boolean, booleanValue: value);
    public static FirestoreRestValue Integer(long value) =>
        FromIntegerValue(value.ToString(CultureInfo.InvariantCulture));
    public static FirestoreRestValue Timestamp(DateTimeOffset value) =>
        FromTimestampValue(FirestoreTimestampPrecision.Normalize(value).ToString("O", CultureInfo.InvariantCulture));
    public static FirestoreRestValue Decimal(decimal value) => String(FirestoreCanonicalDecimal.Format(value));
    public static FirestoreRestValue Array(IEnumerable<FirestoreRestValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return FromArrayValue(new FirestoreRestArray { Values = values.ToList() });
    }
    public static FirestoreRestValue Map(IDictionary<string, FirestoreRestValue> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return FromMapValue(new FirestoreRestMap
        {
            Fields = new Dictionary<string, FirestoreRestValue>(fields, StringComparer.Ordinal)
        });
    }

    internal static FirestoreRestValue FromIntegerValue(string value) =>
        new(FirestoreRestValueKind.Integer, integerValue: value);
    internal static FirestoreRestValue FromTimestampValue(string value) =>
        new(FirestoreRestValueKind.Timestamp, timestampValue: value);
    internal static FirestoreRestValue FromArrayValue(FirestoreRestArray value) =>
        new(FirestoreRestValueKind.Array, arrayValue: value);
    internal static FirestoreRestValue FromMapValue(FirestoreRestMap value) =>
        new(FirestoreRestValueKind.Map, mapValue: value);

    public string RequireString(string fieldName)
    {
        if (Kind != FirestoreRestValueKind.String || StringValue is null) throw Invalid(fieldName, "string");
        return StringValue;
    }

    public string? OptionalString(string fieldName) => IsNull ? null : RequireString(fieldName);

    public bool RequireBoolean(string fieldName) => Kind == FirestoreRestValueKind.Boolean && BooleanValue.HasValue
        ? BooleanValue.Value
        : throw Invalid(fieldName, "bool");

    public long RequireInteger(string fieldName) =>
        Kind == FirestoreRestValueKind.Integer &&
        long.TryParse(IntegerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Invalid(fieldName, "integer");

    public decimal RequireDecimal(string fieldName) =>
        FirestoreCanonicalDecimal.Parse(RequireString(fieldName));

    public DateTimeOffset RequireTimestamp(string fieldName) =>
        Kind == FirestoreRestValueKind.Timestamp && DateTimeOffset.TryParse(
            TimestampValue,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? FirestoreTimestampPrecision.Normalize(value)
            : throw Invalid(fieldName, "timestamp");

    public IReadOnlyList<FirestoreRestValue> RequireArray(string fieldName) =>
        Kind == FirestoreRestValueKind.Array && ArrayValue is not null
            ? ArrayValue.Values
            : throw Invalid(fieldName, "array");

    public IReadOnlyDictionary<string, FirestoreRestValue> RequireMap(string fieldName) =>
        Kind == FirestoreRestValueKind.Map && MapValue is not null
            ? MapValue.Fields
            : throw Invalid(fieldName, "map");

    private static InvalidDataException Invalid(string name, string expected) =>
        new($"El campo Firestore '{name}' no contiene un valor {expected} válido.");
}

public sealed class FirestoreRestArray
{
    [JsonPropertyName("values")]
    public List<FirestoreRestValue> Values { get; set; } = [];
}

public sealed class FirestoreRestMap
{
    [JsonPropertyName("fields")]
    public Dictionary<string, FirestoreRestValue> Fields { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class FirestoreListResponse
{
    [JsonPropertyName("documents")]
    public List<FirestoreRestDocument> Documents { get; set; } = [];

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }
}

public static class FirestoreRestJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string SerializeFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) =>
        JsonSerializer.Serialize(new FirestoreRestDocument
        {
            Fields = new Dictionary<string, FirestoreRestValue>(fields, StringComparer.Ordinal)
        }, Options);
}

public static class FirestoreFieldExtensions
{
    public static FirestoreRestValue Required(
        this IReadOnlyDictionary<string, FirestoreRestValue> fields,
        string name) =>
        fields.TryGetValue(name, out var value)
            ? value
            : throw new InvalidDataException($"Falta el campo Firestore obligatorio '{name}'.");

    public static FirestoreRestValue? Optional(
        this IReadOnlyDictionary<string, FirestoreRestValue> fields,
        string name) => fields.TryGetValue(name, out var value) ? value : null;
}
