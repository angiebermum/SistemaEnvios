using System.Collections;
using System.Reflection;

namespace ECSCommissionsMailer.FirestoreMigration;

public static class LocalOnlyFieldGuard
{
    public static void ThrowIfForbiddenFieldsExist(object? value)
    {
        var findings = FindForbiddenFields(value);
        if (findings.Count > 0)
        {
            throw new InvalidDataException(
                "Se intentó serializar un campo LOCAL_ONLY en Firestore: " + string.Join(", ", findings));
        }
    }

    public static IReadOnlyList<string> FindForbiddenFields(object? value)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        Inspect(value, "$", findings, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return findings.ToList();
    }

    private static void Inspect(
        object? value,
        string path,
        ISet<string> findings,
        ISet<object> visited)
    {
        if (value is null || IsScalar(value.GetType()))
        {
            return;
        }

        if (!value.GetType().IsValueType && !visited.Add(value))
        {
            return;
        }

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                var name = Convert.ToString(entry.Key) ?? string.Empty;
                var childPath = $"{path}.{name}";
                if (MigrationConstants.ForbiddenFirestoreFields.Contains(name))
                {
                    findings.Add(childPath);
                }

                Inspect(entry.Value, childPath, findings, visited);
            }

            return;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                Inspect(item, $"{path}[{index++}]", findings, visited);
            }

            return;
        }

        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var childPath = $"{path}.{property.Name}";
            if (MigrationConstants.ForbiddenFirestoreFields.Contains(property.Name))
            {
                findings.Add(childPath);
            }

            Inspect(property.GetValue(value), childPath, findings, visited);
        }
    }

    private static bool IsScalar(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
        type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
        type == typeof(TimeSpan);
}
