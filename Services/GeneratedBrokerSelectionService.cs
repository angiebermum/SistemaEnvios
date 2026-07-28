using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public sealed record GeneratedBrokerSelectionResult(
    bool Succeeded,
    int GeneratedFileCount,
    int BrokersWithGeneratedFilesCount,
    int SelectedBrokerCount,
    int ExcludedBrokerCount,
    string ErrorMessage = "");

public sealed class GeneratedBrokerSelectionService
{
    private readonly FileLogger _logger;
    private readonly Func<string, bool> _fileExists;

    public GeneratedBrokerSelectionService(
        FileLogger logger,
        Func<string, bool>? fileExists = null)
    {
        _logger = logger;
        _fileExists = fileExists ?? File.Exists;
    }

    public GeneratedBrokerSelectionResult SelectEligibleBrokers(
        IReadOnlyCollection<BrokerSendItem> brokerItems,
        IReadOnlyCollection<Broker> configuredBrokers,
        PaymentGenerationBatch? currentGeneration,
        string currentSourceWorkbookPath)
    {
        ArgumentNullException.ThrowIfNull(brokerItems);
        ArgumentNullException.ThrowIfNull(configuredBrokers);

        var items = brokerItems.ToList();
        try
        {
            if (!IsSuccessfulCurrentGeneration(currentGeneration, currentSourceWorkbookPath))
            {
                ApplySelection(items, new HashSet<Guid>());
                return CreateResult(currentGeneration, 0);
            }

            var activeBrokerIds = configuredBrokers
                .Where(broker => broker.IsActive)
                .Select(broker => broker.Id)
                .ToHashSet();
            var itemsByBrokerId = items
                .GroupBy(item => item.BrokerId)
                .ToDictionary(group => group.Key, group => group.First());
            var selectedBrokerIds = new HashSet<Guid>();

            foreach (var filesByBroker in currentGeneration!.Files.GroupBy(file => file.BrokerId))
            {
                if (!activeBrokerIds.Contains(filesByBroker.Key) ||
                    !itemsByBrokerId.TryGetValue(filesByBroker.Key, out var item))
                {
                    continue;
                }

                if (filesByBroker.Any(file => IsValidAssociatedGeneratedFile(item, file)))
                {
                    selectedBrokerIds.Add(filesByBroker.Key);
                }
            }

            ApplySelection(items, selectedBrokerIds);
            return CreateResult(currentGeneration, selectedBrokerIds.Count);
        }
        catch (Exception ex)
        {
            ApplySelection(items, new HashSet<Guid>());
            _logger.Error(
                "No fue posible seleccionar automáticamente los corredores con archivos de la generación actual.",
                ex);
            return new GeneratedBrokerSelectionResult(
                false,
                currentGeneration?.Files.Count ?? 0,
                currentGeneration?.Files.Select(file => file.BrokerId).Distinct().Count() ?? 0,
                0,
                currentGeneration?.Files.Select(file => file.BrokerId).Distinct().Count() ?? 0,
                "No fue posible completar la selección automática. Todos los corredores quedaron desmarcados.");
        }
    }

    public static void ClearSelection(IEnumerable<BrokerSendItem> brokerItems)
    {
        ArgumentNullException.ThrowIfNull(brokerItems);
        ApplySelection(brokerItems, new HashSet<Guid>());
    }

    private bool IsValidAssociatedGeneratedFile(BrokerSendItem item, GeneratedPaymentFile file)
    {
        var path = file.OutputPath;
        return !string.IsNullOrWhiteSpace(path) &&
               string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase) &&
               item.GeneratedAttachmentPaths.Any(associated => PathsEqual(associated, path)) &&
               item.AttachmentPaths.Any(associated => PathsEqual(associated, path)) &&
               _fileExists(path);
    }

    private static bool IsSuccessfulCurrentGeneration(
        PaymentGenerationBatch? generation,
        string currentSourceWorkbookPath) =>
        generation is not null &&
        generation.Status == PaymentGenerationStatus.ReadyToSend &&
        PathsEqual(generation.SourceWorkbookPath, currentSourceWorkbookPath);

    private static GeneratedBrokerSelectionResult CreateResult(
        PaymentGenerationBatch? generation,
        int selectedBrokerCount)
    {
        var generatedFileCount = generation?.Files.Count ?? 0;
        var brokersWithFilesCount = generation?.Files
            .Select(file => file.BrokerId)
            .Distinct()
            .Count() ?? 0;
        return new GeneratedBrokerSelectionResult(
            true,
            generatedFileCount,
            brokersWithFilesCount,
            selectedBrokerCount,
            Math.Max(0, brokersWithFilesCount - selectedBrokerCount));
    }

    private static void ApplySelection(
        IEnumerable<BrokerSendItem> brokerItems,
        IReadOnlySet<Guid> selectedBrokerIds)
    {
        foreach (var item in brokerItems)
        {
            item.IsSelected = selectedBrokerIds.Contains(item.BrokerId);
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }
}
