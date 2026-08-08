using System.Diagnostics;
using System.Globalization;
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
    TemporaryCopyFailed,
    OpenFailed
}

public sealed record GeneratedFileOpenResult(
    GeneratedFileOpenStatus Status,
    string? OpenedPath = null)
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
    private const string TemporaryViewPrefix = "ECSVista_";
    private static readonly TimeSpan StaleTemporaryViewAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan CleanupInitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupRetryWindow = TimeSpan.FromHours(8);

    private readonly IGeneratedFileProcessLauncher _processLauncher;
    private readonly AppDataPaths _paths;
    private readonly FileLogger _logger;

    public GeneratedFileViewerService(
        IGeneratedFileProcessLauncher processLauncher,
        AppDataPaths paths,
        FileLogger logger)
    {
        _processLauncher = processLauncher;
        _paths = paths;
        _logger = logger;
        CleanupStaleTemporaryViewCopies();
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

            if (!HasXlsxExtension(path))
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

            return OpenTemporaryCopy(Path.GetFullPath(path), broker.BrokerName);
        }
        catch (Exception ex)
        {
            _logger.Error(
                $"No fue posible preparar el archivo generado '{file.OutputPath}' para '{broker.BrokerName}'.",
                ex);
            return new GeneratedFileOpenResult(GeneratedFileOpenStatus.TemporaryCopyFailed);
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

            if (!HasXlsxExtension(path))
            {
                return Reject(
                    path,
                    GeneratedFileOpenStatus.UnsupportedExtension,
                    "la extensión no es .xlsx");
            }

            if (!AssociatedFileAssociationService.IsAssociated(broker, path))
            {
                return Reject(
                    path,
                    GeneratedFileOpenStatus.NotAssociated,
                    "la ruta ya no pertenece a los adjuntos del corredor");
            }

            return OpenTemporaryCopy(Path.GetFullPath(path), broker.BrokerName);
        }
        catch (Exception ex)
        {
            _logger.Error(
                $"No fue posible preparar el archivo asociado '{path}' para '{broker.BrokerName}'.",
                ex);
            return new GeneratedFileOpenResult(GeneratedFileOpenStatus.TemporaryCopyFailed);
        }
    }

    public void CleanupTemporaryViewCopies()
    {
        try
        {
            if (!Directory.Exists(_paths.TemporaryViewsDirectory))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(
                         _paths.TemporaryViewsDirectory,
                         $"{TemporaryViewPrefix}*.xlsx",
                         SearchOption.TopDirectoryOnly))
            {
                TryDeleteTemporaryViewCopy(path, logFailure: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("No fue posible revisar las copias temporales de visualización pendientes.", ex);
        }
    }

    private GeneratedFileOpenResult OpenTemporaryCopy(string originalPath, string brokerName)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(_paths.TemporaryViewsDirectory);
            temporaryPath = BuildTemporaryViewPath(originalPath);
            File.Copy(originalPath, temporaryPath, overwrite: false);
            RemoveReadOnlyAttribute(temporaryPath);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                TryDeleteTemporaryViewCopy(temporaryPath, logFailure: false);
            }

            _logger.Error(
                $"No se pudo preparar una copia temporal de '{originalPath}' para visualizarla. " +
                "El archivo original no fue modificado.",
                ex);
            return new GeneratedFileOpenResult(GeneratedFileOpenStatus.TemporaryCopyFailed);
        }

        try
        {
            _processLauncher.Start(new ProcessStartInfo
            {
                FileName = temporaryPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            TryDeleteTemporaryViewCopy(temporaryPath, logFailure: true);
            _logger.Error(
                $"No fue posible abrir la copia temporal '{temporaryPath}' del archivo asociado " +
                $"'{originalPath}' para '{brokerName}'. El archivo original no fue abierto ni modificado.",
                ex);
            return new GeneratedFileOpenResult(GeneratedFileOpenStatus.OpenFailed);
        }

        _logger.Info(
            $"Se abrió una copia temporal para visualizar '{originalPath}'. Temporal='{temporaryPath}'. " +
            "La ruta asociada original se conservó sin cambios.");
        _ = CleanupWhenAvailableAsync(temporaryPath);
        return new GeneratedFileOpenResult(GeneratedFileOpenStatus.Opened, temporaryPath);
    }

    private string BuildTemporaryViewPath(string originalPath)
    {
        var originalName = Path.GetFileNameWithoutExtension(originalPath);
        var safeName = string.IsNullOrWhiteSpace(originalName)
            ? "Archivo"
            : originalName.Length <= 80 ? originalName : originalName[..80];
        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"{TemporaryViewPrefix}{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}_{safeName}.xlsx");
        return Path.Combine(_paths.TemporaryViewsDirectory, fileName);
    }

    private async Task CleanupWhenAvailableAsync(string temporaryPath)
    {
        try
        {
            await Task.Delay(CleanupInitialDelay).ConfigureAwait(false);
            var retryUntil = DateTime.UtcNow + CleanupRetryWindow;
            while (File.Exists(temporaryPath) && DateTime.UtcNow < retryUntil)
            {
                if (TryDeleteTemporaryViewCopy(temporaryPath, logFailure: false))
                {
                    return;
                }

                await Task.Delay(CleanupRetryDelay).ConfigureAwait(false);
            }

            if (File.Exists(temporaryPath))
            {
                _logger.Info(
                    $"La copia temporal de visualización '{temporaryPath}' continúa en uso y " +
                    "queda pendiente para una limpieza posterior.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(
                $"No fue posible completar la limpieza programada de '{temporaryPath}'. " +
                "La aplicación puede continuar normalmente.",
                ex);
        }
    }

    private void CleanupStaleTemporaryViewCopies()
    {
        try
        {
            if (!Directory.Exists(_paths.TemporaryViewsDirectory))
            {
                return;
            }

            var threshold = DateTime.UtcNow - StaleTemporaryViewAge;
            foreach (var path in Directory.EnumerateFiles(
                         _paths.TemporaryViewsDirectory,
                         $"{TemporaryViewPrefix}*.xlsx",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < threshold)
                    {
                        TryDeleteTemporaryViewCopy(path, logFailure: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"No fue posible revisar la copia temporal '{path}'.", ex);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("No fue posible revisar las copias temporales de visualización antiguas.", ex);
        }
    }

    private bool TryDeleteTemporaryViewCopy(string path, bool logFailure)
    {
        if (!IsManagedTemporaryViewPath(path))
        {
            _logger.Error(
                $"Se rechazó limpiar una ruta fuera del directorio temporal de visualización: '{path}'.");
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (logFailure)
            {
                _logger.Info(
                    $"La copia temporal de visualización '{path}' continúa abierta y " +
                    "se limpiará posteriormente. Detalle: {ex.Message}");
            }

            return false;
        }
        catch (Exception ex)
        {
            if (logFailure)
            {
                _logger.Error($"No fue posible limpiar la copia temporal de visualización '{path}'.", ex);
            }

            return false;
        }
    }

    private bool IsManagedTemporaryViewPath(string path)
    {
        try
        {
            var root = Path.GetFullPath(_paths.TemporaryViewsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(path);
            return string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase) &&
                   IsManagedTemporaryViewFileName(Path.GetFileName(candidate)) &&
                   HasXlsxExtension(candidate);
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveReadOnlyAttribute(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static bool IsManagedTemporaryViewFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!name.StartsWith(TemporaryViewPrefix, StringComparison.Ordinal) ||
            name.Length <= TemporaryViewPrefix.Length + 49)
        {
            return false;
        }

        var suffix = name[TemporaryViewPrefix.Length..];
        return suffix[8] == '_' &&
               suffix[15] == '_' &&
               suffix[48] == '_' &&
               DateTime.TryParseExact(
                   suffix[..15],
                   "yyyyMMdd_HHmmss",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out _) &&
               Guid.TryParseExact(suffix.Substring(16, 32), "N", out _);
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

    private static bool HasXlsxExtension(string path) =>
        string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase);

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
