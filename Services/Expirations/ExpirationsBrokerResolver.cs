using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsBrokerResolver
{
    private readonly IReadOnlyList<ExpirationsBrokerCatalogItem> _catalog;
    private readonly IReadOnlyList<ExpirationsBrokerAssociation> _associations;
    private readonly ExpirationsBrokerNormalizer _normalizer;
    private readonly IReadOnlyDictionary<Guid, bool> _brokerActivity;

    public ExpirationsBrokerResolver(
        IEnumerable<ExpirationsBrokerCatalogItem> catalog,
        IEnumerable<ExpirationsBrokerAssociation> associations,
        ExpirationsBrokerNormalizer? normalizer = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(associations);
        _catalog = catalog.OrderBy(item => item.BrokerId).ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
        _associations = associations
            .OrderBy(association => association.Id)
            .ThenBy(association => association.BrokerId)
            .ToList();
        _normalizer = normalizer ?? new ExpirationsBrokerNormalizer();
        _brokerActivity = _catalog
            .GroupBy(item => item.BrokerId)
            .ToDictionary(group => group.Key, group => group.All(item => item.IsActive));
    }

    public ExpirationsBrokerComponentResolution Resolve(ExpirationsBrokerComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var normalizedComponent = _normalizer.Normalize(component.NormalizedValue.Length > 0
            ? component.NormalizedValue
            : component.RawValue);
        if (normalizedComponent.Length == 0)
        {
            return new ExpirationsBrokerComponentResolution
            {
                RawValue = component.RawValue,
                NormalizedValue = normalizedComponent,
                Status = ExpirationsBrokerResolutionStatus.MissingBroker,
                Diagnostics = ["La fila contiene datos, pero no identifica un corredor."]
            };
        }

        var candidateBrokerIds = new HashSet<Guid>();
        var unknownBrokerIds = new HashSet<Guid>();
        var matchedAssociationIds = new HashSet<Guid>();

        foreach (var broker in _catalog)
        {
            if (ContainsDelimitedPhrase(normalizedComponent, _normalizer.Normalize(broker.Name)))
                candidateBrokerIds.Add(broker.BrokerId);
        }

        foreach (var association in _associations.Where(association => association.IsActive))
        {
            var comparableValue = _normalizer.Normalize(
                association.NormalizedValue.Length > 0
                    ? association.NormalizedValue
                    : association.Value);
            var isMatch = association.Kind switch
            {
                ExpirationsAssociationKind.Name or
                ExpirationsAssociationKind.Alias or
                ExpirationsAssociationKind.Code =>
                    ContainsDelimitedPhrase(normalizedComponent, comparableValue),
                _ => false
            };
            if (!isMatch)
                continue;

            matchedAssociationIds.Add(association.Id);
            if (_brokerActivity.ContainsKey(association.BrokerId))
                candidateBrokerIds.Add(association.BrokerId);
            else
                unknownBrokerIds.Add(association.BrokerId);
        }

        var orderedCandidates = candidateBrokerIds.OrderBy(id => id).ToList();
        var orderedUnknown = unknownBrokerIds.OrderBy(id => id).ToList();
        var orderedAssociations = matchedAssociationIds.OrderBy(id => id).ToList();
        if (orderedUnknown.Count > 0)
        {
            return new ExpirationsBrokerComponentResolution
            {
                RawValue = component.RawValue,
                NormalizedValue = normalizedComponent,
                Status = ExpirationsBrokerResolutionStatus.Unresolved,
                CandidateBrokerIds = orderedCandidates,
                MatchedAssociationIds = orderedAssociations,
                UnknownCatalogBrokerIds = orderedUnknown,
                Diagnostics = orderedUnknown
                    .Select(id => $"Una asociación coincidente apunta al corredor inexistente {id:D}.")
                    .ToList()
            };
        }

        if (orderedCandidates.Count == 0)
        {
            return new ExpirationsBrokerComponentResolution
            {
                RawValue = component.RawValue,
                NormalizedValue = normalizedComponent,
                Status = ExpirationsBrokerResolutionStatus.Unresolved,
                MatchedAssociationIds = orderedAssociations,
                Diagnostics = ["No existe una coincidencia configurada e inequívoca para el componente."]
            };
        }

        if (orderedCandidates.Count > 1)
        {
            return new ExpirationsBrokerComponentResolution
            {
                RawValue = component.RawValue,
                NormalizedValue = normalizedComponent,
                Status = ExpirationsBrokerResolutionStatus.Ambiguous,
                CandidateBrokerIds = orderedCandidates,
                MatchedAssociationIds = orderedAssociations,
                Diagnostics = ["El componente coincide con más de un corredor distinto."]
            };
        }

        var brokerId = orderedCandidates[0];
        var isActive = _brokerActivity[brokerId];
        return new ExpirationsBrokerComponentResolution
        {
            RawValue = component.RawValue,
            NormalizedValue = normalizedComponent,
            Status = isActive
                ? ExpirationsBrokerResolutionStatus.Resolved
                : ExpirationsBrokerResolutionStatus.InactiveBroker,
            CandidateBrokerIds = orderedCandidates,
            ResolvedBrokerId = brokerId,
            MatchedAssociationIds = orderedAssociations,
            Diagnostics = isActive ? [] : ["La coincidencia apunta a un corredor inactivo en Vencimientos."]
        };
    }

    internal static bool ContainsDelimitedPhrase(string normalizedComponent, string normalizedCandidate)
    {
        if (normalizedComponent.Length == 0 || normalizedCandidate.Length == 0)
            return false;

        var componentTokens = normalizedComponent.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidateTokens = normalizedCandidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (candidateTokens.Length > componentTokens.Length)
            return false;

        for (var start = 0; start <= componentTokens.Length - candidateTokens.Length; start++)
        {
            var matches = true;
            for (var offset = 0; offset < candidateTokens.Length; offset++)
            {
                if (string.Equals(
                        componentTokens[start + offset],
                        candidateTokens[offset],
                        StringComparison.Ordinal))
                {
                    continue;
                }

                matches = false;
                break;
            }

            if (matches)
                return true;
        }

        return false;
    }
}
