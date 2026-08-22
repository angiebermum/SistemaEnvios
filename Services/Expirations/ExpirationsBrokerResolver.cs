using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsBrokerResolver
{
    private readonly IReadOnlyList<ExpirationsBrokerCatalogItem> _catalog;
    private readonly IReadOnlyList<ExpirationsBrokerAssociation> _associations;
    private readonly IReadOnlyList<ExpirationsExclusion> _exclusions;
    private readonly ExpirationsBrokerNormalizer _normalizer;
    private readonly IReadOnlyDictionary<Guid, bool> _brokerActivity;

    public ExpirationsBrokerResolver(
        IEnumerable<ExpirationsBrokerCatalogItem> catalog,
        IEnumerable<ExpirationsBrokerAssociation> associations,
        ExpirationsBrokerNormalizer? normalizer = null,
        IEnumerable<ExpirationsExclusion>? exclusions = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(associations);
        _catalog = catalog.OrderBy(item => item.BrokerId).ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
        _associations = associations
            .OrderBy(association => association.Id)
            .ThenBy(association => association.BrokerId)
            .ToList();
        _exclusions = (exclusions ?? [])
            .OrderBy(exclusion => exclusion.Id)
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

        var activeAssociations = _associations
            .Where(association => association.IsActive)
            .Select(association => new
            {
                Association = association,
                ComparableValue = _normalizer.Normalize(
                    association.NormalizedValue.Length > 0
                        ? association.NormalizedValue
                        : association.Value)
            })
            .Where(item => item.ComparableValue.Length > 0)
            .ToList();
        var exactAssociations = activeAssociations
            .Where(item => string.Equals(item.ComparableValue, normalizedComponent, StringComparison.Ordinal))
            .ToList();
        var isExcluded = _exclusions.Any(exclusion =>
            exclusion.IsActive &&
            string.Equals(
                _normalizer.Normalize(exclusion.NormalizedValue.Length > 0
                    ? exclusion.NormalizedValue
                    : exclusion.Value),
                normalizedComponent,
                StringComparison.Ordinal));
        if (isExcluded)
        {
            if (exactAssociations.Count > 0)
            {
                return new ExpirationsBrokerComponentResolution
                {
                    RawValue = component.RawValue,
                    NormalizedValue = normalizedComponent,
                    Status = ExpirationsBrokerResolutionStatus.Ambiguous,
                    CandidateBrokerIds = exactAssociations
                        .Where(item => _brokerActivity.ContainsKey(item.Association.BrokerId))
                        .Select(item => item.Association.BrokerId)
                        .Distinct()
                        .Order()
                        .ToList(),
                    MatchedAssociationIds = exactAssociations.Select(item => item.Association.Id).Order().ToList(),
                    Diagnostics = ["El valor tiene simultáneamente una asociación activa y una exclusión activa."]
                };
            }

            return new ExpirationsBrokerComponentResolution
            {
                RawValue = component.RawValue,
                NormalizedValue = normalizedComponent,
                Status = ExpirationsBrokerResolutionStatus.Excluded,
                Diagnostics = ["El valor está marcado como No distribuir para Vencimientos."]
            };
        }

        var exactCandidates = _catalog
            .Where(broker => string.Equals(
                _normalizer.Normalize(broker.Name),
                normalizedComponent,
                StringComparison.Ordinal))
            .Select(broker => broker.BrokerId)
            .Concat(exactAssociations
                .Where(item => _brokerActivity.ContainsKey(item.Association.BrokerId))
                .Select(item => item.Association.BrokerId))
            .ToHashSet();
        var exactUnknown = exactAssociations
            .Where(item => !_brokerActivity.ContainsKey(item.Association.BrokerId))
            .Select(item => item.Association.BrokerId)
            .ToHashSet();
        if (exactCandidates.Count > 0 || exactUnknown.Count > 0)
        {
            return BuildResolution(
                component.RawValue,
                normalizedComponent,
                exactCandidates,
                exactUnknown,
                exactAssociations.Select(item => item.Association.Id),
                "El componente coincide exactamente con más de un corredor distinto.");
        }

        var candidateBrokerIds = _catalog
            .Where(broker => ContainsDelimitedPhrase(normalizedComponent, _normalizer.Normalize(broker.Name)))
            .Select(broker => broker.BrokerId)
            .ToHashSet();
        var unknownBrokerIds = new HashSet<Guid>();
        var matchedAssociationIds = new HashSet<Guid>();
        foreach (var item in activeAssociations.Where(item =>
                     ContainsDelimitedPhrase(normalizedComponent, item.ComparableValue)))
        {
            matchedAssociationIds.Add(item.Association.Id);
            if (_brokerActivity.ContainsKey(item.Association.BrokerId))
                candidateBrokerIds.Add(item.Association.BrokerId);
            else
                unknownBrokerIds.Add(item.Association.BrokerId);
        }

        return BuildResolution(
            component.RawValue,
            normalizedComponent,
            candidateBrokerIds,
            unknownBrokerIds,
            matchedAssociationIds,
            "El componente coincide con más de un corredor distinto.");
    }

    private ExpirationsBrokerComponentResolution BuildResolution(
        string rawValue,
        string normalizedComponent,
        IEnumerable<Guid> candidateBrokerIds,
        IEnumerable<Guid> unknownBrokerIds,
        IEnumerable<Guid> matchedAssociationIds,
        string ambiguousDiagnostic)
    {
        var orderedCandidates = candidateBrokerIds.Distinct().Order().ToList();
        var orderedUnknown = unknownBrokerIds.Distinct().Order().ToList();
        var orderedAssociations = matchedAssociationIds.Distinct().Order().ToList();
        if (orderedUnknown.Count > 0)
        {
            return new ExpirationsBrokerComponentResolution
            {
                RawValue = rawValue,
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
                RawValue = rawValue,
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
                RawValue = rawValue,
                NormalizedValue = normalizedComponent,
                Status = ExpirationsBrokerResolutionStatus.Ambiguous,
                CandidateBrokerIds = orderedCandidates,
                MatchedAssociationIds = orderedAssociations,
                Diagnostics = [ambiguousDiagnostic]
            };
        }

        var brokerId = orderedCandidates[0];
        var isActive = _brokerActivity[brokerId];
        return new ExpirationsBrokerComponentResolution
        {
            RawValue = rawValue,
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
