namespace ECSCommissionsMailer.FirestoreMigration;

/// <summary>
/// Converts Firestore document resource names and relative document paths to the
/// relative form used as the migration document identity.
/// </summary>
public static class FirestoreDocumentPath
{
    private const string ResourceDocumentsSegment = "/documents/";

    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalized = path.Replace('\\', '/').Trim('/');
        var documentsIndex = normalized.IndexOf(ResourceDocumentsSegment, StringComparison.Ordinal);
        if (documentsIndex >= 0 && IsFirestoreResourcePrefix(normalized.AsSpan(0, documentsIndex)))
        {
            normalized = normalized[(documentsIndex + ResourceDocumentsSegment.Length)..].Trim('/');
        }

        if (normalized.Length == 0)
        {
            throw new ArgumentException("La ruta Firestore no contiene una ruta de documento.", nameof(path));
        }

        return normalized;
    }

    private static bool IsFirestoreResourcePrefix(ReadOnlySpan<char> prefix)
    {
        const string projectsSegment = "projects/";
        const string databasesSegment = "/databases/";

        if (!prefix.StartsWith(projectsSegment, StringComparison.Ordinal))
        {
            return false;
        }

        var projectAndDatabase = prefix[projectsSegment.Length..];
        var databaseIndex = projectAndDatabase.IndexOf(databasesSegment, StringComparison.Ordinal);
        if (databaseIndex <= 0 || databaseIndex + databasesSegment.Length >= projectAndDatabase.Length)
        {
            return false;
        }

        var projectId = projectAndDatabase[..databaseIndex];
        var databaseId = projectAndDatabase[(databaseIndex + databasesSegment.Length)..];
        return !projectId.Contains('/') && !databaseId.Contains('/');
    }
}
