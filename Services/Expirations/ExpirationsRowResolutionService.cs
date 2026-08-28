using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsRowResolutionService(
    ExpirationsBrokerCellParser parser,
    ExpirationsBrokerResolver resolver)
{
    public ExpirationsRowResolution Resolve(
        ExpirationsSourceRow row,
        bool allowMultipleDestinationGroupsForSameBroker = false)
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
        var conflictingBrokerIds = resolutions
            .Where(component => component.Status == ExpirationsBrokerResolutionStatus.Resolved &&
                                component.ResolvedBrokerId.HasValue &&
                                component.DestinationGroup.HasValue)
            .GroupBy(component => component.ResolvedBrokerId!.Value)
            .Where(group => group.Select(component => component.DestinationGroup!.Value).Distinct().Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        if (!allowMultipleDestinationGroupsForSameBroker && conflictingBrokerIds.Count > 0)
        {
            resolutions = resolutions.Select(component =>
            {
                if (component.ResolvedBrokerId is not { } brokerId || !conflictingBrokerIds.Contains(brokerId))
                    return component;
                return new ExpirationsBrokerComponentResolution
                {
                    RawValue = component.RawValue,
                    NormalizedValue = component.NormalizedValue,
                    Status = ExpirationsBrokerResolutionStatus.Ambiguous,
                    CandidateBrokerIds = [brokerId],
                    MatchedAssociationIds = component.MatchedAssociationIds,
                    Diagnostics = ["La fila asigna el mismo corredor a más de un archivo destino."]
                };
            }).ToList();
        }
        var destinations = resolutions
            .Where(component => component.Status == ExpirationsBrokerResolutionStatus.Resolved)
            .Select(component => component.ResolvedBrokerId)
            .OfType<Guid>()
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        var destinationKeys = resolutions
            .Where(component => component.Status == ExpirationsBrokerResolutionStatus.Resolved &&
                                component.ResolvedBrokerId.HasValue &&
                                component.DestinationGroup.HasValue)
            .Select(component => new ExpirationsDestinationKey(
                component.ResolvedBrokerId!.Value,
                component.DestinationGroup!.Value))
            .Distinct()
            .OrderBy(key => key.BrokerId)
            .ThenBy(key => key.DestinationGroup)
            .ToList();
        return new ExpirationsRowResolution
        {
            RowNumber = row.RowNumber,
            RawBrokerValue = row.RawBrokerValue,
            Components = resolutions,
            DistinctDestinationBrokerIds = destinations,
            DistinctDestinationKeys = destinationKeys,
            HasBlockingIssues = resolutions.Any(
                component => component.Status is not (
                    ExpirationsBrokerResolutionStatus.Resolved or
                    ExpirationsBrokerResolutionStatus.Excluded))
        };
    }
}
