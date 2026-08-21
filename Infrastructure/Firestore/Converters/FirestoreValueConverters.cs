using ECS.CommissionsMailer.Infrastructure.Firestore;
using Google.Cloud.Firestore;

namespace ECS.CommissionsMailer.Infrastructure.Firestore.Converters;

public sealed class GuidStringConverter : IFirestoreConverter<Guid>
{
    public object ToFirestore(Guid value) => value.ToString("D");

    public Guid FromFirestore(object value) => value switch
    {
        string text when Guid.TryParseExact(text, "D", out var parsed) => parsed,
        string text when Guid.TryParse(text, out var parsed) => parsed,
        null => throw new ArgumentNullException(nameof(value)),
        _ => throw new ArgumentException($"No se puede convertir {value.GetType().Name} a Guid.", nameof(value))
    };
}

/// <summary>
/// Firestore no ofrece un tipo decimal: solo enteros de 64 bits y double. Se usa
/// una cadena decimal canónica e invariante para no perder precisión ni escala.
/// </summary>
public sealed class DecimalStringConverter : IFirestoreConverter<decimal>
{
    public object ToFirestore(decimal value) => FirestoreCanonicalDecimal.Format(value);

    public decimal FromFirestore(object value) => value switch
    {
        string text => FirestoreCanonicalDecimal.Parse(text),
        long integer => integer,
        int integer => integer,
        null => throw new ArgumentNullException(nameof(value)),
        _ => throw new ArgumentException(
            $"El valor Firestore de tipo {value.GetType().Name} no es un decimal canónico.",
            nameof(value))
    };
}

/// <summary>
/// Persists temporal fields as native Firestore Timestamp values using the exact
/// microsecond precision supported by the database.
/// </summary>
public sealed class FirestoreTimestampPrecisionConverter : IFirestoreConverter<DateTimeOffset>
{
    public object ToFirestore(DateTimeOffset value) =>
        Timestamp.FromDateTimeOffset(FirestoreTimestampPrecision.Normalize(value));

    public DateTimeOffset FromFirestore(object value) => value switch
    {
        Timestamp timestamp => FirestoreTimestampPrecision.Normalize(timestamp.ToDateTimeOffset()),
        DateTimeOffset timestamp => FirestoreTimestampPrecision.Normalize(timestamp),
        DateTime timestamp => new DateTimeOffset(FirestoreTimestampPrecision.Normalize(timestamp)),
        null => throw new ArgumentNullException(nameof(value)),
        _ => throw new ArgumentException(
            $"El valor Firestore de tipo {value.GetType().Name} no es un timestamp válido.",
            nameof(value))
    };
}
