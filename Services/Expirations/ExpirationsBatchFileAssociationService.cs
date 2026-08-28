using DocumentFormat.OpenXml.Packaging;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed record ExpirationsManualFileAddResult(
    ExpirationsGenerationBatch Batch,
    ExpirationsGeneratedFile? File,
    string ErrorMessage = "")
{
    public bool Succeeded => File is not null && ErrorMessage.Length == 0;
}

public sealed class ExpirationsBatchFileAssociationService
{
    private readonly GeneratedFileHashService _hashService;

    public ExpirationsBatchFileAssociationService(GeneratedFileHashService? hashService = null)
    {
        _hashService = hashService ?? new GeneratedFileHashService();
    }

    public ExpirationsGenerationBatch Remove(
        ExpirationsGenerationBatch batch,
        ExpirationsGeneratedFile file)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(file);
        var files = batch.Files.Where(candidate => !SameSlot(candidate, file)).ToList();
        if (files.Count == batch.Files.Count)
            return batch;

        var reducedBatch = CopyBatch(batch, files);
        if (file.Variant == ExpirationsGeneratedFileVariant.Manual ||
            file.DestinationGroup != ExpirationsDestinationGroup.Principal ||
            file.ReplacesSlot.HasValue)
        {
            return reducedBatch;
        }

        var missingPrincipalSlot = InferMissingPrincipalSlot(reducedBatch, file.BrokerId);
        if (missingPrincipalSlot is not { } replacementSlot)
            return reducedBatch;
        var candidates = files
            .Where(candidate =>
                candidate.BrokerId == file.BrokerId &&
                candidate.Variant == ExpirationsGeneratedFileVariant.Manual &&
                !candidate.ReplacesSlot.HasValue)
            .ToList();
        if (candidates.Count != 1)
            return reducedBatch;

        var candidateToPromote = candidates[0];
        return CopyBatch(batch, files.Select(candidate =>
            ReferenceEquals(candidate, candidateToPromote)
                ? CopyFile(
                    candidate,
                    destinationGroup: replacementSlot.DestinationGroup,
                    replacesSlot: replacementSlot)
                : candidate).ToList());
    }

    public ExpirationsGenerationBatch UpdateAuthorizedReplacement(
        ExpirationsGenerationBatch batch,
        ExpirationsGeneratedFile file,
        string sha256)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        var normalizedHash = sha256.Trim().ToLowerInvariant();
        if (normalizedHash.Length != 64 || normalizedHash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("El SHA-256 actualizado no es válido.", nameof(sha256));

        var matchCount = batch.Files.Count(candidate => SameSlot(candidate, file));
        if (matchCount != 1)
            throw new InvalidOperationException("El archivo ya no pertenece de forma única al batch actual.");
        var files = batch.Files.Select(candidate =>
        {
            if (!SameSlot(candidate, file))
                return candidate;
            return CopyFile(
                candidate,
                sha256: normalizedHash,
                isManuallyEdited: true,
                requiresReview: true);
        }).ToList();
        return CopyBatch(batch, files);
    }

    public ExpirationsManualFileAddResult AddManual(
        ExpirationsGenerationBatch batch,
        Guid brokerId,
        string brokerName,
        string sourcePath,
        ExpirationsAttachmentSlotKey? replacesSlot = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerName);
        if (batch.Id == Guid.Empty || string.IsNullOrWhiteSpace(batch.OutputDirectory))
            return new ExpirationsManualFileAddResult(batch, null, "Primero genere un batch de Vencimientos.");
        if (replacesSlot is { } replacement &&
            (replacement.BrokerId != brokerId ||
             !batch.RequiredAttachmentSlots.Any(slot => slot.Key == replacement)))
        {
            return new ExpirationsManualFileAddResult(
                batch,
                null,
                "El archivo destino seleccionado no corresponde a un slot requerido del batch actual.");
        }

        string sourceFullPath;
        string outputDirectory;
        try
        {
            sourceFullPath = Path.GetFullPath(sourcePath);
            outputDirectory = Path.GetFullPath(batch.OutputDirectory);
        }
        catch
        {
            return new ExpirationsManualFileAddResult(batch, null, "La ruta seleccionada no es válida.");
        }

        if (!File.Exists(sourceFullPath))
            return new ExpirationsManualFileAddResult(batch, null, "El archivo seleccionado ya no existe.");
        if (!Path.GetExtension(sourceFullPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            return new ExpirationsManualFileAddResult(batch, null, "Solo se pueden agregar archivos .xlsx.");
        if (!IsValidWorkbook(sourceFullPath))
            return new ExpirationsManualFileAddResult(batch, null, "El archivo seleccionado no es un libro .xlsx válido.");

        var effectiveReplacementSlot = replacesSlot ?? InferMissingPrincipalSlot(batch, brokerId);
        var manualDirectory = Path.Combine(outputDirectory, "Manual");
        string? destinationPath = null;
        try
        {
            Directory.CreateDirectory(manualDirectory);
            destinationPath = AvailableDestination(
                manualDirectory,
                Path.GetFileName(sourceFullPath),
                batch.Files.Select(file => Path.GetFileName(file.OutputPath)));
            File.Copy(sourceFullPath, destinationPath, overwrite: false);
            if (!IsValidWorkbook(destinationPath))
                throw new InvalidDataException("La copia controlada no es un libro .xlsx válido.");
            var file = new ExpirationsGeneratedFile
            {
                BrokerId = brokerId,
                BrokerName = brokerName.Trim(),
                OutputPath = destinationPath,
                Variant = ExpirationsGeneratedFileVariant.Manual,
                DestinationGroup = effectiveReplacementSlot?.DestinationGroup ?? ExpirationsDestinationGroup.Principal,
                ReplacesSlot = effectiveReplacementSlot,
                RowCount = 0,
                SourceRowNumbers = [],
                Sha256 = _hashService.ComputeSha256(destinationPath),
                RequiresReview = true
            };
            return new ExpirationsManualFileAddResult(
                CopyBatch(batch, batch.Files.Append(file).ToList()),
                file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (!string.IsNullOrWhiteSpace(destinationPath) && IsDirectChild(destinationPath, manualDirectory))
            {
                try
                {
                    if (File.Exists(destinationPath))
                        File.Delete(destinationPath);
                }
                catch
                {
                    // La copia incompleta se intentará limpiar manualmente; nunca se asocia al batch.
                }
            }
            return new ExpirationsManualFileAddResult(
                batch,
                null,
                $"No fue posible copiar el archivo al batch actual: {ex.Message}");
        }
    }

    public static bool IsValidWorkbook(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = SpreadsheetDocument.Open(stream, false);
            var workbook = document.WorkbookPart?.Workbook;
            return workbook?.Sheets is { ChildElements.Count: > 0 };
        }
        catch
        {
            return false;
        }
    }

    private static string AvailableDestination(
        string directory,
        string fileName,
        IEnumerable<string> reservedFileNames)
    {
        var safeFileName = Path.GetFileName(fileName);
        var stem = Path.GetFileNameWithoutExtension(safeFileName);
        var extension = Path.GetExtension(safeFileName);
        var reserved = reservedFileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = Path.Combine(directory, safeFileName);
        for (var suffix = 2; File.Exists(candidate) || reserved.Contains(Path.GetFileName(candidate)); suffix++)
            candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
        return candidate;
    }

    private static ExpirationsAttachmentSlotKey? InferMissingPrincipalSlot(
        ExpirationsGenerationBatch batch,
        Guid brokerId)
    {
        var missingSlots = batch.RequiredAttachmentSlots
            .Where(slot => slot.BrokerId == brokerId)
            .Where(slot => !batch.Files.Any(file =>
                (file.BrokerId == brokerId &&
                 file.Variant == slot.ExpectedVariant &&
                 file.DestinationGroup == slot.DestinationGroup &&
                 !file.ReplacesSlot.HasValue) ||
                file.ReplacesSlot == slot.Key))
            .DistinctBy(slot => slot.Key)
            .ToList();
        return missingSlots.Count == 1 &&
               missingSlots[0].DestinationGroup == ExpirationsDestinationGroup.Principal
            ? missingSlots[0].Key
            : null;
    }

    private static bool SameSlot(ExpirationsGeneratedFile left, ExpirationsGeneratedFile right) =>
        left.BrokerId == right.BrokerId &&
        left.Variant == right.Variant &&
        PathsEqual(left.OutputPath, right.OutputPath);

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return left.Equals(right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsDirectChild(string path, string directory)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path))?.Equals(
                Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    private static ExpirationsGenerationBatch CopyBatch(
        ExpirationsGenerationBatch source,
        IReadOnlyList<ExpirationsGeneratedFile> files) => new()
    {
        Id = source.Id,
        Process = source.Process,
        CreatedAtUtc = source.CreatedAtUtc,
        SourceWorkbookPath = source.SourceWorkbookPath,
        SourceWorkbookSha256 = source.SourceWorkbookSha256,
        OutputDirectory = source.OutputDirectory,
        ParticipatingBrokerIds = source.ParticipatingBrokerIds.ToHashSet(),
        RequiredAttachmentSlots = source.RequiredAttachmentSlots.ToList(),
        Files = files,
        Warnings = source.Warnings.ToList()
    };

    private static ExpirationsGeneratedFile CopyFile(
        ExpirationsGeneratedFile source,
        string? sha256 = null,
        bool? isManuallyEdited = null,
        bool? requiresReview = null,
        ExpirationsDestinationGroup? destinationGroup = null,
        ExpirationsAttachmentSlotKey? replacesSlot = null) => new()
    {
        BrokerId = source.BrokerId,
        BrokerName = source.BrokerName,
        OutputPath = source.OutputPath,
        Variant = source.Variant,
        DestinationGroup = destinationGroup ?? source.DestinationGroup,
        ReplacesSlot = replacesSlot ?? source.ReplacesSlot,
        RowCount = source.RowCount,
        Sha256 = sha256 ?? source.Sha256,
        SourceRowNumbers = source.SourceRowNumbers.ToList(),
        Warnings = source.Warnings.ToList(),
        IsManuallyEdited = isManuallyEdited ?? source.IsManuallyEdited,
        RequiresReview = requiresReview ?? source.RequiresReview
    };
}
