using System.Globalization;

namespace ECS.CommissionsMailer.Infrastructure.Firestore;

/// <summary>
/// Firestore has no decimal primitive. Financial values are represented as
/// invariant canonical strings to avoid any binary floating-point conversion.
/// </summary>
public static class FirestoreCanonicalDecimal
{
    public static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static decimal Parse(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new FormatException("El valor no es un decimal canónico de Firestore.");
        }

        return parsed;
    }
}
