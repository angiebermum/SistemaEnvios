using System.Collections;
using System.Globalization;
using System.Reflection;
using ECS.CommissionsMailer.Infrastructure.Firestore;
using Google.Cloud.Firestore;

namespace ECSCommissionsMailer.FirestoreMigration;

public static class FirestoreDocumentProjection
{
    public static IReadOnlyDictionary<string, object?> Project(object document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return (IReadOnlyDictionary<string, object?>)ProjectValue(document)!;
    }

    private static object? ProjectValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            decimal number => number,
            Guid id => id.ToString("D"),
            DateTimeOffset timestamp => FirestoreTimestampPrecision.Normalize(timestamp),
            DateTime timestamp => new DateTimeOffset(FirestoreTimestampPrecision.Normalize(timestamp)),
            Enum enumeration => enumeration.ToString(),
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double => value,
            IDictionary dictionary => ProjectDictionary(dictionary),
            IEnumerable enumerable => enumerable.Cast<object?>().Select(ProjectValue).ToList(),
            _ => ProjectFirestoreObject(value)
        };
    }

    private static IReadOnlyDictionary<string, object?> ProjectDictionary(IDictionary dictionary)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry item in dictionary)
        {
            result.Add(Convert.ToString(item.Key, CultureInfo.InvariantCulture) ?? string.Empty, ProjectValue(item.Value));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, object?> ProjectFirestoreObject(object value)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var attribute = property.GetCustomAttribute<FirestorePropertyAttribute>();
            if (attribute is null || !property.CanRead)
            {
                continue;
            }

            result.Add(attribute.Name, ProjectValue(property.GetValue(value)));
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException(
                $"El tipo {value.GetType().FullName} no tiene propiedades [FirestoreProperty].");
        }

        return result;
    }
}

public static class FirestoreSemanticComparer
{
    public static IReadOnlyList<string> Compare(
        IReadOnlyDictionary<string, object?> expected,
        IReadOnlyDictionary<string, object?> actual)
    {
        var differences = new List<string>();
        CompareValue(expected, actual, "$", differences);
        return differences;
    }

    private static void CompareValue(object? expected, object? actual, string path, ICollection<string> differences)
    {
        if (expected is null || actual is null)
        {
            if (expected is not null || actual is not null)
            {
                differences.Add($"{path}: esperado {Describe(expected)}, Firestore {Describe(actual)}");
            }

            return;
        }

        if (TryDictionary(expected, out var expectedDictionary) && TryDictionary(actual, out var actualDictionary))
        {
            foreach (var key in expectedDictionary.Keys.Union(actualDictionary.Keys).OrderBy(value => value, StringComparer.Ordinal))
            {
                var hasExpected = expectedDictionary.TryGetValue(key, out var expectedValue);
                var hasActual = actualDictionary.TryGetValue(key, out var actualValue);
                if (!hasExpected)
                {
                    differences.Add($"{path}.{key}: campo EXTRA en Firestore");
                }
                else if (!hasActual)
                {
                    differences.Add($"{path}.{key}: campo MISSING en Firestore");
                }
                else
                {
                    CompareValue(expectedValue, actualValue, $"{path}.{key}", differences);
                }
            }

            return;
        }

        if (TryList(expected, out var expectedList) && TryList(actual, out var actualList))
        {
            if (expectedList.Count != actualList.Count)
            {
                differences.Add($"{path}: largo esperado {expectedList.Count}, Firestore {actualList.Count}");
            }

            for (var index = 0; index < Math.Min(expectedList.Count, actualList.Count); index++)
            {
                CompareValue(expectedList[index], actualList[index], $"{path}[{index}]", differences);
            }

            return;
        }

        if (expected is decimal expectedDecimal)
        {
            if (!TryDecimal(actual, out var actualDecimal) || expectedDecimal != actualDecimal)
            {
                differences.Add($"{path}: decimal esperado {expectedDecimal.ToString(CultureInfo.InvariantCulture)}, Firestore {Describe(actual)}");
            }

            return;
        }

        if (TryTimestamp(expected, out var expectedTimestamp))
        {
            var canonicalExpected = FirestoreTimestampPrecision.Normalize(expectedTimestamp);
            if (!TryTimestamp(actual, out var actualTimestamp) ||
                canonicalExpected != FirestoreTimestampPrecision.Normalize(actualTimestamp))
            {
                differences.Add($"{path}: timestamp esperado {expectedTimestamp:O}, Firestore {Describe(actual)}");
            }

            return;
        }

        if (IsIntegral(expected) && IsIntegral(actual))
        {
            if (Convert.ToDecimal(expected, CultureInfo.InvariantCulture) != Convert.ToDecimal(actual, CultureInfo.InvariantCulture))
            {
                differences.Add($"{path}: esperado {expected}, Firestore {actual}");
            }

            return;
        }

        if (!Equals(expected, actual))
        {
            differences.Add($"{path}: esperado {Describe(expected)}, Firestore {Describe(actual)}");
        }
    }

    private static bool TryDictionary(object value, out IReadOnlyDictionary<string, object?> dictionary)
    {
        if (value is IReadOnlyDictionary<string, object?> readOnly)
        {
            dictionary = readOnly;
            return true;
        }

        if (value is IDictionary<string, object> typed)
        {
            dictionary = typed.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);
            return true;
        }

        if (value is IDictionary nonGeneric)
        {
            dictionary = nonGeneric.Cast<DictionaryEntry>().ToDictionary(
                pair => Convert.ToString(pair.Key, CultureInfo.InvariantCulture) ?? string.Empty,
                pair => pair.Value,
                StringComparer.Ordinal);
            return true;
        }

        dictionary = null!;
        return false;
    }

    private static bool TryList(object value, out IReadOnlyList<object?> list)
    {
        if (value is IEnumerable enumerable && value is not string && value is not IDictionary)
        {
            list = enumerable.Cast<object?>().ToList();
            return true;
        }

        list = null!;
        return false;
    }

    private static bool TryDecimal(object value, out decimal result) => value switch
    {
        decimal number => Assign(number, out result),
        string text => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out result),
        long number => Assign(number, out result),
        int number => Assign(number, out result),
        _ => Assign(0m, out result, false)
    };

    private static bool TryTimestamp(object value, out DateTimeOffset result)
    {
        switch (value)
        {
            case Timestamp timestamp:
                result = timestamp.ToDateTimeOffset();
                return true;
            case DateTimeOffset timestamp:
                result = timestamp;
                return true;
            case DateTime timestamp:
                result = new DateTimeOffset(timestamp.ToUniversalTime());
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static bool Assign(decimal value, out decimal result, bool success = true)
    {
        result = value;
        return success;
    }

    private static bool IsIntegral(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong;

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => $"'{text}'",
        DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        Timestamp timestamp => timestamp.ToDateTimeOffset().ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name
    };
}
