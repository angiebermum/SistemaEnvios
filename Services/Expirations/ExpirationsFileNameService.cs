using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsFileNameService
{
    private const int MaximumBaseNameLength = 180;
    private readonly FileNameSanitizer _sanitizer;

    public ExpirationsFileNameService(FileNameSanitizer? sanitizer = null)
    {
        _sanitizer = sanitizer ?? new FileNameSanitizer();
    }

    public IReadOnlyDictionary<Guid, string> CreateFileNames(
        ExpirationsProcess process,
        IReadOnlyList<ExpirationsBrokerCatalogItem> brokers)
    {
        ArgumentNullException.ThrowIfNull(brokers);
        if (process != ExpirationsProcess.PreviousMonth)
            throw new ExpirationsGenerationException("La generación de Vencimientos del mes siguiente todavía no está habilitada.");

        var candidates = brokers
            .OrderBy(broker => broker.BrokerId)
            .Select(broker => new
            {
                broker.BrokerId,
                BaseName = Limit($"Pendientes mes anterior - {_sanitizer.SanitizePart(broker.Name)}")
            })
            .ToList();
        var collisions = candidates
            .GroupBy(item => item.BaseName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidates.ToDictionary(
            item => item.BrokerId,
            item => collisions.Contains(item.BaseName)
                ? CreateCollidingName(item.BaseName, item.BrokerId)
                : $"{item.BaseName}.xlsx");
    }

    public string CreateBatchDirectoryName(ExpirationsProcess process, DateTimeOffset localTime)
    {
        if (process != ExpirationsProcess.PreviousMonth)
            throw new ExpirationsGenerationException("La generación de Vencimientos del mes siguiente todavía no está habilitada.");
        return $"Pendientes mes anterior - {localTime:yyyyMMdd-HHmmss}";
    }

    private static string Limit(string value, int suffixLength = 5)
    {
        var maximum = MaximumBaseNameLength - suffixLength;
        return value.Length <= maximum
            ? value
            : value[..maximum].TrimEnd(' ', '.', '-');
    }

    private static string CreateCollidingName(string baseName, Guid brokerId)
    {
        var limitedName = Limit(baseName, suffixLength: 11);
        var suffix = brokerId.ToString("N")[..8];
        return $"{limitedName} - {suffix}.xlsx";
    }
}
