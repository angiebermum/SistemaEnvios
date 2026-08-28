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
        if (process is not (ExpirationsProcess.PreviousMonth or ExpirationsProcess.Cancellations))
            throw new ExpirationsGenerationException("La generación de Vencimientos del mes siguiente todavía no está habilitada.");

        var prefix = process == ExpirationsProcess.Cancellations
            ? "Cancelaciones"
            : "Pendientes mes anterior";

        var candidates = brokers
            .OrderBy(broker => broker.BrokerId)
            .Select(broker => new
            {
                broker.BrokerId,
                BaseName = Limit($"{prefix} - {_sanitizer.SanitizePart(broker.Name)}")
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

    public IReadOnlyDictionary<ExpirationsDestinationKey, string> CreatePreviousMonthFileNames(
        IReadOnlyList<ExpirationsGeneratedFileNameRequest> files) =>
        CreateStandardFileNames(ExpirationsProcess.PreviousMonth, files);

    public IReadOnlyDictionary<ExpirationsDestinationKey, string> CreateStandardFileNames(
        ExpirationsProcess process,
        IReadOnlyList<ExpirationsGeneratedFileNameRequest> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var prefix = process switch
        {
            ExpirationsProcess.PreviousMonth => "Pendientes mes anterior",
            ExpirationsProcess.Cancellations => "Cancelaciones",
            _ => throw new ArgumentOutOfRangeException(nameof(process))
        };
        var candidates = files.Select(file => new
        {
            Key = new ExpirationsDestinationKey(file.BrokerId, file.DestinationGroup),
            BaseName = Limit($"{prefix} - {GroupFileIdentity(file.BrokerName, file.DestinationGroup)}")
        }).ToList();
        if (candidates.Select(item => item.Key).Distinct().Count() != candidates.Count)
            throw new ExpirationsGenerationException("La generación contiene un grupo de archivo duplicado.");
        var collisions = candidates.GroupBy(item => item.BaseName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.ToDictionary(
            item => item.Key,
            item => collisions.Contains(item.BaseName)
                ? CreateCollidingName(item.BaseName, item.Key.BrokerId)
                : $"{item.BaseName}.xlsx");
    }

    public IReadOnlyDictionary<ExpirationsGeneratedFileNameKey, string> CreateNextMonthFileNames(
        ExpirationsPeriod period,
        IReadOnlyList<ExpirationsGeneratedFileNameRequest> files)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(files);
        var candidates = files
            .Select(file => new
            {
                Key = new ExpirationsGeneratedFileNameKey(file.BrokerId, file.Variant, file.DestinationGroup),
                file.BrokerId,
                BrokerName = GroupFileIdentity(file.BrokerName, file.DestinationGroup),
                file.Variant
            })
            .OrderBy(item => item.BrokerId)
            .ThenBy(item => item.Key.Variant)
            .ToList();
        if (candidates.Select(item => item.Key).Distinct().Count() != candidates.Count)
            throw new ExpirationsGenerationException("La generación contiene una variante de archivo duplicada.");
        var collisions = candidates
            .GroupBy(
                item => CreateNextMonthName(item.BrokerName, item.Variant, period, null),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.ToDictionary(
            item => item.Key,
            item =>
            {
                var baseName = CreateNextMonthName(item.BrokerName, item.Variant, period, null);
                return collisions.Contains(baseName)
                    ? CreateNextMonthName(item.BrokerName, item.Variant, period, item.BrokerId)
                    : baseName;
            });
    }

    public string CreateBatchDirectoryName(
        ExpirationsProcess process,
        DateTimeOffset localTime,
        ExpirationsPeriod? period = null)
    {
        return process switch
        {
            ExpirationsProcess.PreviousMonth => $"Pendientes mes anterior - {localTime:yyyyMMdd-HHmmss}",
            ExpirationsProcess.NextMonth when period is not null =>
                $"Vencimientos mes siguiente - {period.FileToken} - {localTime:yyyyMMdd-HHmmss}",
            ExpirationsProcess.NextMonth => throw new ExpirationsGenerationException(
                "Seleccione el período de mes siguiente."),
            ExpirationsProcess.Cancellations => $"Cancelaciones - {localTime:yyyyMMdd-HHmmss}",
            _ => throw new ArgumentOutOfRangeException(nameof(process))
        };
    }

    private static string VariantSuffix(ExpirationsGeneratedFileVariant variant) => variant switch
    {
        ExpirationsGeneratedFileVariant.Standard => string.Empty,
        ExpirationsGeneratedFileVariant.FelixAlphabetical => " - ORDENADO ALFABETICAMENTE",
        ExpirationsGeneratedFileVariant.FelixExpirationDate => " - ORDENADO POR VENCIMIENTO",
        _ => throw new ArgumentOutOfRangeException(nameof(variant))
    };

    private string GroupFileIdentity(string brokerName, ExpirationsDestinationGroup group)
    {
        var safeBroker = _sanitizer.SanitizePart(brokerName);
        if (group == ExpirationsDestinationGroup.Principal)
            return safeBroker;
        if (group is ExpirationsDestinationGroup.ContadoCoriMotors or
            ExpirationsDestinationGroup.VariosCoriMotors)
        {
            return "Cori Motors";
        }
        var safeGroup = _sanitizer.SanitizePart(ExpirationsDestinationGroups.DisplayName(group));
        return group == ExpirationsDestinationGroup.HernanVarela ||
               group is ExpirationsDestinationGroup.PcGuanacaste or
                   ExpirationsDestinationGroup.ContadoCoriMotors or
                   ExpirationsDestinationGroup.VariosCoriMotors
            ? safeGroup
            : $"{safeBroker} - {safeGroup}";
    }

    private static string CreateNextMonthName(
        string sanitizedBrokerName,
        ExpirationsGeneratedFileVariant variant,
        ExpirationsPeriod period,
        Guid? collisionBrokerId)
    {
        const string prefix = "Vencimientos mes siguiente - ";
        var identity = collisionBrokerId is { } brokerId
            ? $" - {brokerId.ToString("N")[..8]}"
            : string.Empty;
        var tail = $"{identity} - {period.FileToken}{VariantSuffix(variant)}";
        const int extensionLength = 5;
        var availableBrokerLength = MaximumBaseNameLength - extensionLength - prefix.Length - tail.Length;
        if (availableBrokerLength <= 0)
            throw new ExpirationsGenerationException("El nombre de archivo de mes siguiente excede el límite permitido.");
        var brokerName = sanitizedBrokerName.Length <= availableBrokerLength
            ? sanitizedBrokerName
            : sanitizedBrokerName[..availableBrokerLength].TrimEnd(' ', '.', '-');
        return $"{prefix}{brokerName}{tail}.xlsx";
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
