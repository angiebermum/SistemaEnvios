using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsRowResolutionService(
    ExpirationsBrokerCellParser parser,
    ExpirationsBrokerResolver resolver)
{
    public ExpirationsRowResolution Resolve(ExpirationsSourceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var components = parser.Parse(row.RawBrokerValue);
        if (components.Count == 0)
        {
            components =
            [
                new ExpirationsBrokerComponent
                {
                    RawValue = row.RawBrokerValue,
                    NormalizedValue = string.Empty
                }
            ];
        }

        var resolutions = components.Select(resolver.Resolve).ToList();
        var destinations = resolutions
            .Where(component => component.Status == ExpirationsBrokerResolutionStatus.Resolved)
            .Select(component => component.ResolvedBrokerId)
            .OfType<Guid>()
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        return new ExpirationsRowResolution
        {
            RowNumber = row.RowNumber,
            RawBrokerValue = row.RawBrokerValue,
            Components = resolutions,
            DistinctDestinationBrokerIds = destinations,
            HasBlockingIssues = resolutions.Any(
                component => component.Status != ExpirationsBrokerResolutionStatus.Resolved)
        };
    }
}
