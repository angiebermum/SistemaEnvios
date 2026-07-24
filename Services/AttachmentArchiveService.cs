using System.Text;

namespace ECS.CommissionsMailer.Services;

public sealed class AttachmentArchiveService
{
    private readonly AppDataPaths _paths;
    private readonly FileLogger _logger;

    public AttachmentArchiveService(AppDataPaths paths, FileLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public List<string> Archive(string brokerName, IEnumerable<string> sourcePaths)
    {
        var timestampFolder = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff");
        var brokerFolder = SanitizeFolderName(brokerName);
        var destinationDirectory = Path.Combine(_paths.SentAttachmentsDirectory, timestampFolder, brokerFolder);
        Directory.CreateDirectory(destinationDirectory);

        var archived = new List<string>();
        foreach (var sourcePath in sourcePaths)
        {
            try
            {
                var destinationPath = GetAvailableDestination(destinationDirectory, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, destinationPath, false);
                archived.Add(destinationPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Error($"No se pudo archivar {sourcePath} para {brokerName}.", ex);
                throw new IOException($"El correo fue enviado, pero no se pudo archivar '{Path.GetFileName(sourcePath)}': {ex.Message}", ex);
            }
        }

        return archived;
    }

    internal static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder();
        foreach (var character in name.Trim())
        {
            if (invalid.Contains(character))
            {
                continue;
            }

            builder.Append(char.IsWhiteSpace(character) ? '_' : character);
        }

        var sanitized = builder.ToString().Trim('.', '_');
        return string.IsNullOrWhiteSpace(sanitized) ? "Corredor" : sanitized;
    }

    private static string GetAvailableDestination(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var counter = 2; ; counter++)
        {
            candidate = Path.Combine(directory, $"{baseName}_{counter}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
