using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public sealed class GenerationHistoryService
{
    private readonly AppDataPaths _paths;
    private readonly AtomicJsonFile _json;
    private readonly bool _persistenceEnabled;

    public GenerationHistoryService(AppDataPaths paths, FileLogger logger, bool persistenceEnabled = true)
    {
        _paths = paths;
        _json = new AtomicJsonFile(logger);
        _persistenceEnabled = persistenceEnabled;
    }

    public List<string> Warnings { get; } = [];

    public List<PaymentGenerationBatch> Load()
    {
        var batches = _json.Load<List<PaymentGenerationBatch>>(_paths.PaymentGenerationHistoryFile, out var warning) ?? [];
        if (!string.IsNullOrWhiteSpace(warning))
        {
            Warnings.Add(warning);
        }

        foreach (var batch in batches)
        {
            batch.Files ??= [];
            batch.Warnings ??= [];
            batch.SentBrokerIds ??= [];
            batch.FailedBrokerIds ??= [];
            foreach (var file in batch.Files)
            {
                file.Crc ??= new PaymentCurrencyCalculation { Currency = DeductionCurrency.CRC };
                file.Usd ??= new PaymentCurrencyCalculation { Currency = DeductionCurrency.USD };
            }
        }

        return batches.OrderByDescending(value => value.CreatedAt).ToList();
    }

    public void Save(IEnumerable<PaymentGenerationBatch> batches) =>
        _json.Save(_paths.PaymentGenerationHistoryFile, batches.OrderByDescending(value => value.CreatedAt).ToList());

    public void Upsert(ICollection<PaymentGenerationBatch> batches, PaymentGenerationBatch batch)
    {
        var existing = batches.FirstOrDefault(value => value.Id == batch.Id);
        if (existing is not null)
        {
            batches.Remove(existing);
        }

        batches.Add(batch);
        if (_persistenceEnabled)
        {
            Save(batches);
        }
    }

    public PaymentGenerationBatch? FindActive(
        IEnumerable<PaymentGenerationBatch> batches,
        Guid? generationId) =>
        generationId is null ? null : batches.FirstOrDefault(value => value.Id == generationId.Value);

    public List<string> ValidateGeneratedAttachments(
        PaymentGenerationBatch? batch,
        Guid brokerId,
        IEnumerable<string> generatedPaths)
    {
        var paths = generatedPaths.Select(Path.GetFullPath).ToList();
        var errors = new List<string>();
        if (batch is null)
        {
            if (paths.Count > 0)
            {
                errors.Add("Los archivos generados no pertenecen a una generación activa.");
            }

            return errors;
        }

        var expectedFiles = batch.Files.Where(value => value.BrokerId == brokerId).ToList();
        var expectedPaths = new List<(GeneratedPaymentFile File, string Path)>();
        foreach (var file in expectedFiles)
        {
            if (string.IsNullOrWhiteSpace(file.OutputPath) || !Path.IsPathFullyQualified(file.OutputPath))
            {
                errors.Add(
                    $"El archivo generado de la hoja '{file.WorksheetName}' no está disponible en este equipo.");
                continue;
            }

            try
            {
                expectedPaths.Add((file, Path.GetFullPath(file.OutputPath)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add(
                    $"El archivo generado de la hoja '{file.WorksheetName}' no está disponible en este equipo.");
            }
        }

        var expected = expectedPaths.Select(value => value.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var missing in expected.Where(value => !actual.Contains(value)))
        {
            errors.Add(
                $"Falta adjuntar el archivo generado '{Path.GetFileName(missing)}'. " +
                "El correo debe incluir todos los archivos del corredor.");
        }

        foreach (var path in paths)
        {
            if (!expected.Contains(path))
            {
                errors.Add($"El archivo generado '{Path.GetFileName(path)}' no corresponde al corredor en la generación activa.");
            }
            else if (!File.Exists(path))
            {
                errors.Add($"Falta el archivo generado '{Path.GetFileName(path)}'. Vuelva a generar antes de enviar.");
            }
            else
            {
                var expectedHash = expectedPaths.First(value =>
                    string.Equals(value.Path, path, StringComparison.OrdinalIgnoreCase)).File.Sha256;
                var actualHash = new GeneratedFileHashService().ComputeSha256(path);
                if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        $"El archivo generado '{Path.GetFileName(path)}' cambió después de la generación. " +
                        "Vuelva a generar antes de enviar.");
                }
            }
        }

        return errors;
    }
}
