using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsDistributionPreviewService
{
    public IReadOnlyList<ExpirationsDistributionPreviewItem> Build(
        ExpirationsWorkbookAnalysisResult analysis,
        IEnumerable<ExpirationsBrokerCatalogItem> catalog)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(catalog);
        var catalogById = catalog
            .GroupBy(item => item.BrokerId)
            .ToDictionary(group => group.Key, group => group.First());
        var rowNumbers = new Dictionary<ExpirationsDestinationKey, HashSet<uint>>();
        var detectedValues = new Dictionary<ExpirationsDestinationKey, Dictionary<string, string>>();

        foreach (var row in analysis.RowResolutions.OrderBy(row => row.RowNumber))
        {
            foreach (var destination in row.DistinctDestinationKeys)
            {
                if (!catalogById.ContainsKey(destination.BrokerId))
                    continue;
                if (!rowNumbers.TryGetValue(destination, out var brokerRows))
                {
                    brokerRows = [];
                    rowNumbers[destination] = brokerRows;
                }
                brokerRows.Add(row.RowNumber);
            }

            foreach (var component in row.Components.Where(component =>
                         component.Status == ExpirationsBrokerResolutionStatus.Resolved &&
                         component.ResolvedBrokerId.HasValue && component.DestinationGroup.HasValue))
            {
                var destination = new ExpirationsDestinationKey(
                    component.ResolvedBrokerId!.Value,
                    component.DestinationGroup!.Value);
                if (!catalogById.ContainsKey(destination.BrokerId))
                    continue;
                if (!detectedValues.TryGetValue(destination, out var values))
                {
                    values = new Dictionary<string, string>(StringComparer.Ordinal);
                    detectedValues[destination] = values;
                }
                var key = component.NormalizedValue.Length > 0
                    ? component.NormalizedValue
                    : component.RawValue;
                values.TryAdd(key, component.RawValue);
            }
        }

        return rowNumbers.Keys
            .Select(destination =>
            {
                var broker = catalogById[destination.BrokerId];
                return new ExpirationsDistributionPreviewItem
                {
                    BrokerId = destination.BrokerId,
                    BrokerName = broker.Name,
                    DestinationGroup = destination.DestinationGroup,
                    PrimaryEmail = broker.PrimaryEmailAddresses.FirstOrDefault(
                        email => !string.IsNullOrWhiteSpace(email)) ?? string.Empty,
                    RowCount = rowNumbers[destination].Count,
                    DetectedValues = detectedValues.GetValueOrDefault(destination)?.Values
                        .Order(StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(value => value, StringComparer.Ordinal)
                        .ToList() ?? []
                };
            })
            .OrderBy(item => item.BrokerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.BrokerId)
            .ThenBy(item => item.DestinationGroup)
            .ToList();
    }
}
