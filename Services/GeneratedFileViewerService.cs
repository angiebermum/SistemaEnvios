using System.Diagnostics;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public enum GeneratedFileAvailability
{
    Available,
    NotFound
}

public enum GeneratedFileOpenStatus
{
    Opened,
    MissingPath,
    NonAbsolutePath,
    FileNotFound,
    UnsupportedExtension,
    NotAssociated,
    OpenFailed
}

public sealed record GeneratedFileOpenResult(GeneratedFileOpenStatus Status)
{
    public bool Succeeded => Status == GeneratedFileOpenStatus.Opened;
}

public interface IGeneratedFileProcessLauncher
{
    void Start(ProcessStartInfo startInfo);
}

public sealed class GeneratedFileProcessLauncher : IGeneratedFileProcessLauncher
{
    public void Start(ProcessStartInfo startInfo) => Process.Start(startInfo);
}

public sealed class GeneratedFileViewerService
{
    private readonly IGeneratedFileProcessLauncher _processLauncher;
    private readonly FileLogger _logger;

    public GeneratedFileViewerService(
        IGeneratedFileProcessLauncher processLauncher,
        FileLogger logger)
    {
        _processLauncher = processLauncher;
        _logger = logger;
    }

    public IReadOnlyList<GeneratedPaymentFile> GetFilesForBroker(
        PaymentGenerationBatch? batch,
        BrokerSendItem broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        if (batch is null)
        {
            return [];
        }

        var associatedPaths = broker.GeneratedAttachmentPaths
            .Select(NormalizeForComparison)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        associatedPaths.IntersectWith(
            broker.AttachmentPaths.Select(NormalizeForComparison));

        return batch.Files
            .Where(file =>
                file.BrokerId == broker.BrokerId &&
                associatedPaths.Contains(NormalizeForComparison(file.OutputPath)))
            .ToList();
    }

    public static GeneratedFileAvailability GetAvailability(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !Path.IsPathFullyQualified(path) ||
                !EmailValidationService.IsAllowedExcelFile(path))
            {
                return GeneratedFileAvailability.NotFound;
            }

            return File.Exists(path)
                ? GeneratedFileAvailability.Available
                : GeneratedFileAvailability.NotFound;
        }
        catch
        {
            return GeneratedFileAvailability.NotFound;
        }
    }

    public GeneratedFileOpenResult Open(
        GeneratedPaymentFile file,
        BrokerSendItem broker)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(broker);

        try
        {
            var path = file.OutputPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return Reject(file, GeneratedFileOpenStatus.MissingPath, "la ruta está vacía");
            }

            if (!Path.IsPathFullyQualified(path))
            {
                return Reject(file, GeneratedFileOpenStatus.NonAbsolutePath, "la ruta no es absoluta");
            }

            if (!File.Exists(path))
            {
                return Reject(file, GeneratedFileOpenStatus.FileNotFound, "el archivo no existe");
            }

            if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                return Reject(file, GeneratedFileOpenStatus.UnsupportedExtension, "la extensión no es .xlsx");
            }

            if (file.BrokerId != broker.BrokerId ||
                !ContainsPath(broker.GeneratedAttachmentPaths, path) ||
                !ContainsPath(broker.AttachmentPaths, path))
            {
                return Reject(
                    file,
                    GeneratedFileOpenStatus.NotAssociated,
                    "la ruta ya no pertenece a los adjuntos generados del corredor");
            }

            _processLauncher.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            return new GeneratedFileOpenResult(GeneratedFileOpenStatus.Opened);
        }
        catch (Exception ex)
        {
            _logger.Error(
                $"No fue posible abrir el archivo generado '{file.OutputPath}' para '{broker.BrokerName}'.",
                ex);
            return new GeneratedFileOpenResult(GeneratedFileOpenStatus.OpenFailed);
        }
    }

    public GeneratedFileOpenResult OpenAssociated(string path, BrokerSendItem broker)
    {
        ArgumentNullException.ThrowIfNull(broker);

        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Reject(path, GeneratedFileOpenStatus.MissingPath, "la ruta está vacía");
            }

            if (!Path.IsPathFullyQualified(path))
            {
                return Reject(path, GeneratedFileOpenStatus.NonAbsolutePath, "la ruta no es absoluta");
            }

            if (!File.Exists(path))
            {
                return Reject(path, GeneratedFileOpenStatus.FileNotFound, "el archivo no existe");
            }

            if (!EmailValidationService.IsAllowedExcelFile(path))
            {
                return Reject(
                    path,
                    GeneratedFileOpenStatus.UnsupportedExtension,
                    "la extensión no es un formato de Excel permitido");
            }

            if (!AssociatedFileAssociationService.IsAssociated(broker, path))
            {
                return Reject(
                    path,
                    GeneratedFileOpenStatus.NotAssociated,
                    "la ruta ya no pertenece a los adjuntos del corredor");
            }

            _processLauncher.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            return new GeneratedFileOpenResult(GeneratedFileOpenStatus.Opened);
        }
        catch (Exception ex)
        {
            _logger.Error(
                $"No fue posible abrir el archivo asociado '{path}' para '{broker.BrokerName}'.",
                ex);
            return new GeneratedFileOpenResult(GeneratedFileOpenStatus.OpenFailed);
        }
    }

    private GeneratedFileOpenResult Reject(
        GeneratedPaymentFile file,
        GeneratedFileOpenStatus status,
        string reason)
    {
        _logger.Error(
            $"Se rechazó la apertura del archivo generado '{file.OutputPath}': {reason}.");
        return new GeneratedFileOpenResult(status);
    }

    private GeneratedFileOpenResult Reject(
        string path,
        GeneratedFileOpenStatus status,
        string reason)
    {
        _logger.Error($"Se rechazó la apertura del archivo asociado '{path}': {reason}.");
        return new GeneratedFileOpenResult(status);
    }

    private static bool ContainsPath(IEnumerable<string> paths, string candidate)
    {
        var normalizedCandidate = NormalizeForComparison(candidate);
        return paths.Any(path =>
            string.Equals(
                NormalizeForComparison(path),
                normalizedCandidate,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeForComparison(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
