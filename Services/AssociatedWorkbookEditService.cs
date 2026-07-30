using System.Diagnostics;
using DocumentFormat.OpenXml.Packaging;

namespace ECS.CommissionsMailer.Services;

public enum WorkbookEditStartStatus
{
    Started,
    OriginalNotFound,
    UnsupportedExtension,
    FileLocked,
    InvalidWorkbook,
    OpenFailed
}

public enum WorkbookChangeCheckStatus
{
    ChangesDetected,
    NoChanges,
    TemporaryFileNotFound,
    OriginalNotFound,
    UnsupportedExtension,
    FileLocked,
    InvalidWorkbook,
    OriginalChanged,
    CheckFailed
}

public enum WorkbookReplaceStatus
{
    Replaced,
    TemporaryFileNotFound,
    OriginalNotFound,
    UnsupportedExtension,
    FileLocked,
    InvalidWorkbook,
    OriginalChanged,
    ReplacementFailedOriginalPreserved,
    RecoveryFailed
}

public sealed record AssociatedWorkbookEditSession(
    string OriginalPath,
    string TemporaryDirectory,
    string TemporaryPath,
    string InitialSha256);

public sealed record WorkbookEditStartResult(
    WorkbookEditStartStatus Status,
    AssociatedWorkbookEditSession? Session = null,
    string ErrorMessage = "")
{
    public bool Succeeded => Status == WorkbookEditStartStatus.Started && Session is not null;
}

public sealed record WorkbookChangeCheckResult(
    WorkbookChangeCheckStatus Status,
    string EditedSha256 = "",
    string ErrorMessage = "")
{
    public bool HasChanges => Status == WorkbookChangeCheckStatus.ChangesDetected;
}

public sealed record WorkbookReplaceResult(
    WorkbookReplaceStatus Status,
    string Sha256 = "",
    string ErrorMessage = "")
{
    public bool Succeeded => Status == WorkbookReplaceStatus.Replaced;
}

public interface IAssociatedWorkbookAtomicReplacer
{
    void Replace(string preparedPath, string destinationPath, string backupPath);
}

public sealed class AssociatedWorkbookAtomicReplacer : IAssociatedWorkbookAtomicReplacer
{
    public void Replace(string preparedPath, string destinationPath, string backupPath) =>
        File.Replace(preparedPath, destinationPath, backupPath, true);
}

public sealed class AssociatedWorkbookEditService
{
    private static readonly TimeSpan StaleEditAge = TimeSpan.FromDays(7);
    private readonly AppDataPaths _paths;
    private readonly FileLogger _logger;
    private readonly IGeneratedFileProcessLauncher _processLauncher;
    private readonly GeneratedFileHashService _hashService;
    private readonly IAssociatedWorkbookAtomicReplacer _atomicReplacer;

    public AssociatedWorkbookEditService(
        AppDataPaths paths,
        FileLogger logger,
        IGeneratedFileProcessLauncher? processLauncher = null,
        GeneratedFileHashService? hashService = null,
        IAssociatedWorkbookAtomicReplacer? atomicReplacer = null)
    {
        _paths = paths;
        _logger = logger;
        _processLauncher = processLauncher ?? new GeneratedFileProcessLauncher();
        _hashService = hashService ?? new GeneratedFileHashService();
        _atomicReplacer = atomicReplacer ?? new AssociatedWorkbookAtomicReplacer();
        CleanupStaleTemporaryEdits();
    }

    public WorkbookEditStartResult BeginEdit(string path)
    {
        string? temporaryDirectory = null;
        try
        {
            if (!TryGetSupportedExistingPath(path, out var originalPath, out var status))
            {
                return new WorkbookEditStartResult(status);
            }

            if (!CanOpenExclusively(originalPath, requireWriteAccess: false, out var lockError))
            {
                return new WorkbookEditStartResult(
                    WorkbookEditStartStatus.FileLocked,
                    ErrorMessage: lockError);
            }

            ValidateWorkbook(originalPath);
            var initialHash = _hashService.ComputeSha256(originalPath);
            temporaryDirectory = Path.Combine(
                _paths.TemporaryEditsDirectory,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            var temporaryPath = Path.Combine(
                temporaryDirectory,
                $"Edición - {Path.GetFileName(originalPath)}");
            File.Copy(originalPath, temporaryPath, false);
            ValidateWorkbook(temporaryPath);

            var session = new AssociatedWorkbookEditSession(
                originalPath,
                temporaryDirectory,
                temporaryPath,
                initialHash);
            _processLauncher.Start(new ProcessStartInfo
            {
                FileName = temporaryPath,
                UseShellExecute = true
            });
            _logger.Info(
                $"Se abrió una copia temporal para editar '{originalPath}'. Temporal='{temporaryPath}'.");
            return new WorkbookEditStartResult(WorkbookEditStartStatus.Started, session);
        }
        catch (Exception ex) when (IsInvalidWorkbookException(ex))
        {
            TryDeleteTemporaryDirectory(temporaryDirectory);
            _logger.Error($"El archivo '{path}' no es un libro .xlsx válido.", ex);
            return new WorkbookEditStartResult(
                WorkbookEditStartStatus.InvalidWorkbook,
                ErrorMessage: ex.Message);
        }
        catch (Exception ex)
        {
            TryDeleteTemporaryDirectory(temporaryDirectory);
            _logger.Error($"No fue posible preparar la edición temporal de '{path}'.", ex);
            return new WorkbookEditStartResult(
                WorkbookEditStartStatus.OpenFailed,
                ErrorMessage: ex.Message);
        }
    }

    public WorkbookChangeCheckResult CheckChanges(AssociatedWorkbookEditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            if (!File.Exists(session.TemporaryPath))
            {
                return new WorkbookChangeCheckResult(WorkbookChangeCheckStatus.TemporaryFileNotFound);
            }

            if (!File.Exists(session.OriginalPath))
            {
                return new WorkbookChangeCheckResult(WorkbookChangeCheckStatus.OriginalNotFound);
            }

            if (!HasXlsxExtension(session.TemporaryPath) || !HasXlsxExtension(session.OriginalPath))
            {
                return new WorkbookChangeCheckResult(WorkbookChangeCheckStatus.UnsupportedExtension);
            }

            if (!CanOpenExclusively(session.TemporaryPath, requireWriteAccess: true, out var lockError))
            {
                return new WorkbookChangeCheckResult(
                    WorkbookChangeCheckStatus.FileLocked,
                    ErrorMessage: lockError);
            }

            if (!CanOpenExclusively(session.OriginalPath, requireWriteAccess: false, out lockError))
            {
                return new WorkbookChangeCheckResult(
                    WorkbookChangeCheckStatus.FileLocked,
                    ErrorMessage: lockError);
            }

            ValidateWorkbook(session.TemporaryPath);
            var currentOriginalHash = _hashService.ComputeSha256(session.OriginalPath);
            if (!string.Equals(
                    currentOriginalHash,
                    session.InitialSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new WorkbookChangeCheckResult(WorkbookChangeCheckStatus.OriginalChanged);
            }

            var editedHash = _hashService.ComputeSha256(session.TemporaryPath);
            return string.Equals(editedHash, currentOriginalHash, StringComparison.OrdinalIgnoreCase)
                ? new WorkbookChangeCheckResult(WorkbookChangeCheckStatus.NoChanges, editedHash)
                : new WorkbookChangeCheckResult(WorkbookChangeCheckStatus.ChangesDetected, editedHash);
        }
        catch (Exception ex) when (IsInvalidWorkbookException(ex))
        {
            _logger.Error($"La copia temporal '{session.TemporaryPath}' no es un libro .xlsx válido.", ex);
            return new WorkbookChangeCheckResult(
                WorkbookChangeCheckStatus.InvalidWorkbook,
                ErrorMessage: ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error($"No fue posible comprobar la copia temporal '{session.TemporaryPath}'.", ex);
            return new WorkbookChangeCheckResult(
                WorkbookChangeCheckStatus.CheckFailed,
                ErrorMessage: ex.Message);
        }
    }

    public WorkbookReplaceResult Replace(
        AssociatedWorkbookEditSession session,
        Action<string>? persistMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var originalDirectory = Path.GetDirectoryName(session.OriginalPath);
        var operationId = Guid.NewGuid().ToString("N");
        var preparedPath = string.IsNullOrWhiteSpace(originalDirectory)
            ? string.Empty
            : Path.Combine(
                originalDirectory,
                $".{Path.GetFileNameWithoutExtension(session.OriginalPath)}.{operationId}.ecs-replacement.xlsx");
        var backupPath = string.IsNullOrWhiteSpace(originalDirectory)
            ? string.Empty
            : Path.Combine(
                originalDirectory,
                $".{Path.GetFileNameWithoutExtension(session.OriginalPath)}.{operationId}.ecs-backup.xlsx");
        var recoveryPath = string.IsNullOrWhiteSpace(originalDirectory)
            ? string.Empty
            : Path.Combine(
                originalDirectory,
                $".{Path.GetFileNameWithoutExtension(session.OriginalPath)}.{operationId}.ecs-recovery.xlsx");
        var originalWasReplaced = false;

        try
        {
            var preflight = CheckChanges(session);
            switch (preflight.Status)
            {
                case WorkbookChangeCheckStatus.TemporaryFileNotFound:
                    return new WorkbookReplaceResult(WorkbookReplaceStatus.TemporaryFileNotFound);
                case WorkbookChangeCheckStatus.OriginalNotFound:
                    return new WorkbookReplaceResult(WorkbookReplaceStatus.OriginalNotFound);
                case WorkbookChangeCheckStatus.UnsupportedExtension:
                    return new WorkbookReplaceResult(WorkbookReplaceStatus.UnsupportedExtension);
                case WorkbookChangeCheckStatus.FileLocked:
                    return new WorkbookReplaceResult(
                        WorkbookReplaceStatus.FileLocked,
                        ErrorMessage: preflight.ErrorMessage);
                case WorkbookChangeCheckStatus.InvalidWorkbook:
                    return new WorkbookReplaceResult(
                        WorkbookReplaceStatus.InvalidWorkbook,
                        ErrorMessage: preflight.ErrorMessage);
                case WorkbookChangeCheckStatus.OriginalChanged:
                    return new WorkbookReplaceResult(WorkbookReplaceStatus.OriginalChanged);
                case WorkbookChangeCheckStatus.CheckFailed:
                    return new WorkbookReplaceResult(
                        WorkbookReplaceStatus.ReplacementFailedOriginalPreserved,
                        ErrorMessage: preflight.ErrorMessage);
                case WorkbookChangeCheckStatus.NoChanges:
                    return new WorkbookReplaceResult(
                        WorkbookReplaceStatus.ReplacementFailedOriginalPreserved,
                        ErrorMessage: "No se detectaron cambios para reemplazar.");
            }

            if (!CanOpenExclusively(session.OriginalPath, requireWriteAccess: true, out var lockError))
            {
                return new WorkbookReplaceResult(
                    WorkbookReplaceStatus.FileLocked,
                    ErrorMessage: lockError);
            }

            File.Copy(session.TemporaryPath, preparedPath, false);
            ValidateWorkbook(preparedPath);
            var preparedHash = _hashService.ComputeSha256(preparedPath);
            if (!string.Equals(preparedHash, preflight.EditedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("La copia preparada no coincide con el archivo editado validado.");
            }

            _atomicReplacer.Replace(preparedPath, session.OriginalPath, backupPath);
            originalWasReplaced = true;
            ValidateWorkbook(session.OriginalPath);
            var replacementHash = _hashService.ComputeSha256(session.OriginalPath);
            if (!string.Equals(replacementHash, preparedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("El archivo reemplazado no coincide con la versión editada validada.");
            }

            persistMetadata?.Invoke(replacementHash);
            TryDeleteFile(backupPath);
            TryDeleteFile(recoveryPath);
            TryDeleteTemporaryDirectory(session.TemporaryDirectory);
            _logger.Info(
                $"Se reemplazó de forma segura el archivo asociado '{session.OriginalPath}' " +
                "sin cambiar su ruta ni asociación.");
            return new WorkbookReplaceResult(
                WorkbookReplaceStatus.Replaced,
                replacementHash);
        }
        catch (Exception ex)
        {
            var recovered = !originalWasReplaced || TryRestoreOriginal(
                session.OriginalPath,
                backupPath,
                recoveryPath);
            TryDeleteFile(preparedPath);
            if (recovered)
            {
                TryDeleteFile(backupPath);
                TryDeleteFile(recoveryPath);
            }

            _logger.Error(
                recovered
                    ? $"No fue posible reemplazar '{session.OriginalPath}'. El archivo original fue conservado."
                    : $"Falló el reemplazo y no fue posible confirmar la recuperación de '{session.OriginalPath}'. " +
                      $"Respaldo='{backupPath}'.",
                ex);
            return new WorkbookReplaceResult(
                recovered
                    ? WorkbookReplaceStatus.ReplacementFailedOriginalPreserved
                    : WorkbookReplaceStatus.RecoveryFailed,
                ErrorMessage: ex.Message);
        }
    }

    public void Cancel(AssociatedWorkbookEditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        TryDeleteTemporaryDirectory(session.TemporaryDirectory);
    }

    private void CleanupStaleTemporaryEdits()
    {
        try
        {
            if (!Directory.Exists(_paths.TemporaryEditsDirectory))
            {
                return;
            }

            var threshold = DateTime.UtcNow - StaleEditAge;
            foreach (var directory in Directory.EnumerateDirectories(_paths.TemporaryEditsDirectory))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < threshold)
                    {
                        Directory.Delete(directory, true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Error($"La edición temporal pendiente '{directory}' no pudo limpiarse.", ex);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("No fue posible revisar las ediciones temporales pendientes.", ex);
        }
    }

    private static bool TryGetSupportedExistingPath(
        string path,
        out string fullPath,
        out WorkbookEditStartStatus status)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            status = WorkbookEditStartStatus.OriginalNotFound;
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            status = WorkbookEditStartStatus.OriginalNotFound;
            return false;
        }

        if (!File.Exists(fullPath))
        {
            status = WorkbookEditStartStatus.OriginalNotFound;
            return false;
        }

        if (!HasXlsxExtension(fullPath))
        {
            status = WorkbookEditStartStatus.UnsupportedExtension;
            return false;
        }

        status = WorkbookEditStartStatus.Started;
        return true;
    }

    private static bool HasXlsxExtension(string path) =>
        string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase);

    private static bool CanOpenExclusively(
        string path,
        bool requireWriteAccess,
        out string errorMessage)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                requireWriteAccess ? FileAccess.ReadWrite : FileAccess.Read,
                FileShare.None);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static void ValidateWorkbook(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = SpreadsheetDocument.Open(stream, false);
        _ = document.WorkbookPart?.Workbook
            ?? throw new InvalidDataException("El archivo no contiene una estructura de libro válida.");
    }

    private bool TryRestoreOriginal(
        string originalPath,
        string backupPath,
        string recoveryPath)
    {
        try
        {
            if (!File.Exists(backupPath))
            {
                return false;
            }

            if (File.Exists(originalPath))
            {
                File.Replace(backupPath, originalPath, recoveryPath, true);
            }
            else
            {
                File.Move(backupPath, originalPath, false);
            }

            return File.Exists(originalPath);
        }
        catch (Exception ex)
        {
            _logger.Error(
                $"No fue posible restaurar automáticamente '{originalPath}' desde '{backupPath}'.",
                ex);
            return false;
        }
    }

    private void TryDeleteTemporaryDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            var root = Path.GetFullPath(_paths.TemporaryEditsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Error($"Se rechazó limpiar una ruta temporal fuera del directorio controlado: '{directory}'.");
                return;
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(
                $"La copia temporal '{directory}' continúa abierta y se limpiará posteriormente.",
                ex);
        }
    }

    private void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"No fue posible limpiar el archivo temporal de reemplazo '{path}'.", ex);
        }
    }

    private static bool IsInvalidWorkbookException(Exception exception) =>
        exception is InvalidDataException or FileFormatException or OpenXmlPackageException;
}
