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
        var rowNumbers = new Dictionary<Guid, HashSet<uint>>();
        var detectedValues = new Dictionary<Guid, Dictionary<string, string>>();

        foreach (var row in analysis.RowResolutions.OrderBy(row => row.RowNumber))
        {
            foreach (var brokerId in row.DistinctDestinationBrokerIds)
            {
                if (!catalogById.ContainsKey(brokerId))
                    continue;
                if (!rowNumbers.TryGetValue(brokerId, out var brokerRows))
                {
                    brokerRows = [];
                    rowNumbers[brokerId] = brokerRows;
                }
                brokerRows.Add(row.RowNumber);
            }

            foreach (var component in row.Components.Where(component =>
                         component.Status == ExpirationsBrokerResolutionStatus.Resolved &&
                         component.ResolvedBrokerId.HasValue))
            {
                var brokerId = component.ResolvedBrokerId!.Value;
                if (!catalogById.ContainsKey(brokerId))
                    continue;
                if (!detectedValues.TryGetValue(brokerId, out var values))
                {
                    values = new Dictionary<string, string>(StringComparer.Ordinal);
                    detectedValues[brokerId] = values;
                }
                var key = component.NormalizedValue.Length > 0
                    ? component.NormalizedValue
                    : component.RawValue;
                values.TryAdd(key, component.RawValue);
            }
        }

        return rowNumbers.Keys
            .Select(brokerId =>
            {
                var broker = catalogById[brokerId];
                return new ExpirationsDistributionPreviewItem
                {
                    BrokerId = brokerId,
                    BrokerName = broker.Name,
                    PrimaryEmail = broker.PrimaryEmailAddresses.FirstOrDefault(
                        email => !string.IsNullOrWhiteSpace(email)) ?? string.Empty,
                    RowCount = rowNumbers[brokerId].Count,
                    DetectedValues = detectedValues.GetValueOrDefault(brokerId)?.Values
                        .Order(StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(value => value, StringComparer.Ordinal)
                        .ToList() ?? []
                };
            })
            .OrderBy(item => item.BrokerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.BrokerId)
            .ToList();
    }
}
