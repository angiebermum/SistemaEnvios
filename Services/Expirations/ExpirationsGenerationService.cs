using System.Net.Mail;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsGenerationService
{
    Task<ExpirationsGenerationBatch> GenerateAsync(
        ExpirationsGenerationRequest request,
        IProgress<ExpirationsGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsGenerationService : IExpirationsGenerationService
{
    private const string SourceChangedMessage =
        "El archivo seleccionado cambió después del análisis.\nAnalícelo nuevamente antes de generar.";
    private readonly IExpirationsStandardWorkbookGenerator _workbookGenerator;
    private readonly ExpirationsFileNameService _fileNameService;
    private readonly GeneratedFileHashService _hashService;
    private readonly TimeProvider _timeProvider;

    public ExpirationsGenerationService(
        IExpirationsStandardWorkbookGenerator? workbookGenerator = null,
        ExpirationsFileNameService? fileNameService = null,
        GeneratedFileHashService? hashService = null,
        TimeProvider? timeProvider = null)
    {
        _workbookGenerator = workbookGenerator ?? new ExpirationsStandardWorkbookGenerator();
        _fileNameService = fileNameService ?? new ExpirationsFileNameService();
        _hashService = hashService ?? new GeneratedFileHashService();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ExpirationsGenerationBatch> GenerateAsync(
        ExpirationsGenerationRequest request,
        IProgress<ExpirationsGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputParentDirectory);
        if (request.Context.Process != ExpirationsProcess.PreviousMonth)
        {
            throw new ExpirationsGenerationException(
                "La generación de Vencimientos del mes siguiente todavía no está habilitada.");
        }

        var parentDirectory = Path.GetFullPath(request.OutputParentDirectory);
        if (!Directory.Exists(parentDirectory))
            throw new DirectoryNotFoundException($"La carpeta de salida no existe: '{parentDirectory}'.");
        DemandUnchangedSource(request.Context);

        var targets = BuildTargets(request.Context);
        var fileNames = _fileNameService.CreateFileNames(
            request.Context.Process,
            targets.Select(target => target.Broker).ToList());
        var batchId = Guid.NewGuid();
        var stagingRoot = Path.Combine(parentDirectory, $".ECS-expirations-generation-{batchId:N}");
        var stagedDirectory = Path.Combine(stagingRoot, "staged");
        var stagedFiles = new List<StagedFile>(targets.Count);
        Directory.CreateDirectory(stagedDirectory);

        try
        {
            for (var index = 0; index < targets.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = targets[index];
                progress?.Report(new ExpirationsGenerationProgress(index + 1, targets.Count, target.Broker.Name));
                var outputPath = Path.Combine(stagedDirectory, fileNames[target.Broker.BrokerId]);
                var materialization = new ExpirationsStandardWorkbookGenerationRequest(
                    request.Context.SourceWorkbookPath,
                    outputPath,
                    request.Context.SourceWorkbook.WorksheetName,
                    request.Context.SourceWorkbook.HeaderRowNumber,
                    target.RowNumbers);
                await Task.Run(
                    () => _workbookGenerator.Generate(materialization, cancellationToken),
                    cancellationToken);
                var warnings = BuildEmailWarnings(target.Broker);
                stagedFiles.Add(new StagedFile(
                    target.Broker,
                    outputPath,
                    target.RowNumbers,
                    _hashService.ComputeSha256(outputPath),
                    warnings));
            }

            cancellationToken.ThrowIfCancellationRequested();
            DemandUnchangedSource(request.Context);
            var finalDirectory = GetUniqueFinalDirectory(parentDirectory, request.Context.Process);
            Directory.Move(stagedDirectory, finalDirectory);
            TryDeleteDirectory(stagingRoot);

            var files = stagedFiles.Select(file => new ExpirationsGeneratedFile
            {
                BrokerId = file.Broker.BrokerId,
                BrokerName = file.Broker.Name,
                OutputPath = Path.Combine(finalDirectory, Path.GetFileName(file.StagedPath)),
                RowCount = file.RowNumbers.Count,
                Sha256 = file.Sha256,
                SourceRowNumbers = file.RowNumbers,
                Warnings = file.Warnings
            }).ToList();
            return new ExpirationsGenerationBatch
            {
                Id = batchId,
                Process = request.Context.Process,
                CreatedAtUtc = _timeProvider.GetUtcNow(),
                SourceWorkbookPath = request.Context.SourceWorkbookPath,
                SourceWorkbookSha256 = request.Context.SourceWorkbookSha256,
                OutputDirectory = finalDirectory,
                Files = files,
                Warnings = files.SelectMany(file => file.Warnings).Distinct().ToList()
            };
        }
        catch
        {
            TryDeleteDirectory(stagingRoot);
            throw;
        }
    }

    private void DemandUnchangedSource(ExpirationsGenerationContext context)
    {
        var currentHash = _hashService.ComputeSha256(context.SourceWorkbookPath);
        if (!string.Equals(currentHash, context.SourceWorkbookSha256, StringComparison.OrdinalIgnoreCase))
            throw new ExpirationsGenerationException(SourceChangedMessage);
    }

    private static IReadOnlyList<GenerationTarget> BuildTargets(ExpirationsGenerationContext context)
    {
        var catalog = context.BrokerCatalog
            .GroupBy(item => item.BrokerId)
            .ToDictionary(group => group.Key, group => group.Single());
        return context.Analysis.ResolvedRowNumbersByBroker
            .Select(item => new GenerationTarget(
                catalog[item.Key],
                item.Value.Distinct().Order().ToArray()))
            .Where(target => target.RowNumbers.Count > 0)
            .OrderBy(target => target.Broker.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(target => target.Broker.BrokerId)
            .ToList();
    }

    private static IReadOnlyList<string> BuildEmailWarnings(ExpirationsBrokerCatalogItem broker)
    {
        if (broker.PrimaryEmailAddresses.Any(address => MailAddress.TryCreate(address, out _)))
            return [];
        return
        [
            $"El corredor '{broker.Name}' no tiene correo principal válido. El archivo se generó, pero el envío deberá permanecer bloqueado."
        ];
    }

    private string GetUniqueFinalDirectory(string parentDirectory, ExpirationsProcess process)
    {
        var baseName = _fileNameService.CreateBatchDirectoryName(process, _timeProvider.GetLocalNow());
        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var name = suffix == 0 ? baseName : $"{baseName} - {suffix + 1}";
            var candidate = Path.Combine(parentDirectory, name);
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }
        throw new IOException("No fue posible reservar un nombre único para la carpeta de generación.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Limpieza best-effort; nunca se toca contenido fuera del staging propio del batch.
        }
    }

    private sealed record GenerationTarget(
        ExpirationsBrokerCatalogItem Broker,
        IReadOnlyList<uint> RowNumbers);

    private sealed record StagedFile(
        ExpirationsBrokerCatalogItem Broker,
        string StagedPath,
        IReadOnlyList<uint> RowNumbers,
        string Sha256,
        IReadOnlyList<string> Warnings);
}
