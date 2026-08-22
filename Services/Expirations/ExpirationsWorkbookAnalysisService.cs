using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsWorkbookAnalysisService
{
    public ExpirationsWorkbookAnalysisResult Analyze(
        ExpirationsWorkbookReadResult readResult,
        IEnumerable<ExpirationsBrokerCatalogItem> catalog,
        IEnumerable<ExpirationsBrokerAssociation> associations) =>
        Analyze(readResult, catalog, associations, [], []);

    public ExpirationsWorkbookAnalysisResult Analyze(
        ExpirationsWorkbookReadResult readResult,
        IEnumerable<ExpirationsBrokerCatalogItem> catalog,
        IEnumerable<ExpirationsBrokerAssociation> associations,
        IEnumerable<ExpirationsManualResolutionOverride> manualOverrides) =>
        Analyze(readResult, catalog, associations, manualOverrides, []);

    public ExpirationsWorkbookAnalysisResult Analyze(
        ExpirationsWorkbookReadResult readResult,
        IEnumerable<ExpirationsBrokerCatalogItem> catalog,
        IEnumerable<ExpirationsBrokerAssociation> associations,
        IEnumerable<ExpirationsManualResolutionOverride> manualOverrides,
        IEnumerable<ExpirationsExclusion> exclusions)
    {
        ArgumentNullException.ThrowIfNull(readResult);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(associations);
        ArgumentNullException.ThrowIfNull(manualOverrides);
        ArgumentNullException.ThrowIfNull(exclusions);
        if (!readResult.IsSuccess)
        {
            return new ExpirationsWorkbookAnalysisResult
            {
                ReadStatus = readResult.Status,
                Messages = readResult.Messages,
                CanGenerate = false
            };
        }

        var catalogItems = catalog.ToList();
        var associationItems = associations.ToList();
        var exclusionItems = exclusions.ToList();
        var resolver = new ExpirationsBrokerResolver(
            catalogItems,
            associationItems,
            exclusions: exclusionItems);
        var rowService = new ExpirationsRowResolutionService(
            new ExpirationsBrokerCellParser(),
            resolver);
        var rows = readResult.Workbook!.Rows
            .OrderBy(row => row.RowNumber)
            .Select(rowService.Resolve)
            .ToList();
        rows = ApplyManualOverrides(rows, catalogItems, manualOverrides);
        var blockingRows = rows.Count(row => row.HasBlockingIssues);
        var components = rows.SelectMany(row => row.Components).ToList();
        var distribution = rows
            .Where(row => !row.HasBlockingIssues)
            .SelectMany(row => row.DistinctDestinationBrokerIds.Select(brokerId => new
            {
                BrokerId = brokerId,
                row.RowNumber
            }))
            .GroupBy(item => item.BrokerId)
            .OrderBy(group => group.Key)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<uint>)group.Select(item => item.RowNumber).Distinct().Order().ToList());
        return new ExpirationsWorkbookAnalysisResult
        {
            ReadStatus = readResult.Status,
            TotalRows = rows.Count,
            ResolvedRows = rows.Count - blockingRows,
            ExcludedRows = rows.Count(row =>
                row.Components.Count > 0 &&
                row.Components.All(component => component.Status == ExpirationsBrokerResolutionStatus.Excluded)),
            ExcludedComponents = components.Count(
                component => component.Status == ExpirationsBrokerResolutionStatus.Excluded),
            RowsWithBlockingIssues = blockingRows,
            UnresolvedComponents = components.Count(
                component => component.Status == ExpirationsBrokerResolutionStatus.Unresolved),
            AmbiguousComponents = components.Count(
                component => component.Status == ExpirationsBrokerResolutionStatus.Ambiguous),
            InactiveBrokerComponents = components.Count(
                component => component.Status == ExpirationsBrokerResolutionStatus.InactiveBroker),
            MissingBrokerComponents = components.Count(
                component => component.Status == ExpirationsBrokerResolutionStatus.MissingBroker),
            RowResolutions = rows,
            ResolvedRowNumbersByBroker = distribution,
            Messages = readResult.Messages,
            CanGenerate = blockingRows == 0
        };
    }

    private static List<ExpirationsRowResolution> ApplyManualOverrides(
        IReadOnlyList<ExpirationsRowResolution> rows,
        IReadOnlyList<ExpirationsBrokerCatalogItem> catalog,
        IEnumerable<ExpirationsManualResolutionOverride> manualOverrides)
    {
        var activeBrokerIds = catalog
            .GroupBy(item => item.BrokerId)
            .Where(group => group.All(item => item.IsActive))
            .Select(group => group.Key)
            .ToHashSet();
        var overrides = manualOverrides
            .GroupBy(value => (value.RowNumber, value.ComponentIndex))
            .ToDictionary(group => group.Key, group => group.Last());
        if (overrides.Count == 0)
            return rows.ToList();

        return rows.Select(row =>
        {
            var components = row.Components.ToList();
            for (var index = 0; index < components.Count; index++)
            {
                if (!overrides.TryGetValue((row.RowNumber, index), out var manualOverride) ||
                    !activeBrokerIds.Contains(manualOverride.BrokerId) ||
                    components[index].Status is not (
                        ExpirationsBrokerResolutionStatus.Resolved or
                        ExpirationsBrokerResolutionStatus.Ambiguous or
                        ExpirationsBrokerResolutionStatus.Unresolved))
                {
                    continue;
                }

                var original = components[index];
                components[index] = new ExpirationsBrokerComponentResolution
                {
                    RawValue = original.RawValue,
                    NormalizedValue = original.NormalizedValue,
                    Status = ExpirationsBrokerResolutionStatus.Resolved,
                    CandidateBrokerIds = [manualOverride.BrokerId],
                    ResolvedBrokerId = manualOverride.BrokerId,
                    MatchedAssociationIds = original.MatchedAssociationIds,
                    UnknownCatalogBrokerIds = original.UnknownCatalogBrokerIds,
                    Diagnostics = ["Resolución manual aplicada únicamente a esta aparición del archivo actual."]
                };
            }

            return new ExpirationsRowResolution
            {
                RowNumber = row.RowNumber,
                RawBrokerValue = row.RawBrokerValue,
                Components = components,
                DistinctDestinationBrokerIds = components
                    .Where(component => component.Status == ExpirationsBrokerResolutionStatus.Resolved)
                    .Select(component => component.ResolvedBrokerId)
                    .OfType<Guid>()
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList(),
                HasBlockingIssues = components.Any(
                    component => component.Status is not (
                        ExpirationsBrokerResolutionStatus.Resolved or
                        ExpirationsBrokerResolutionStatus.Excluded))
            };
        }).ToList();
    }
}
