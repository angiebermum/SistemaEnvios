using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsWorkbookAnalysisService
{
    public ExpirationsWorkbookAnalysisResult Analyze(
        ExpirationsWorkbookReadResult readResult,
        IEnumerable<ExpirationsBrokerCatalogItem> catalog,
        IEnumerable<ExpirationsBrokerAssociation> associations)
    {
        ArgumentNullException.ThrowIfNull(readResult);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(associations);
        if (!readResult.IsSuccess)
        {
            return new ExpirationsWorkbookAnalysisResult
            {
                ReadStatus = readResult.Status,
                Messages = readResult.Messages,
                CanGenerate = false
            };
        }

        var resolver = new ExpirationsBrokerResolver(catalog, associations);
        var rowService = new ExpirationsRowResolutionService(
            new ExpirationsBrokerCellParser(),
            resolver);
        var rows = readResult.Workbook!.Rows
            .OrderBy(row => row.RowNumber)
            .Select(rowService.Resolve)
            .ToList();
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
}
