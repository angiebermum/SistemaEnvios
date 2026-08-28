using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

/// <summary>
/// Owns the Vencimientos-only distinction between the email recipient and the
/// business group/file to which a source identifier is routed.
/// </summary>
public sealed class ExpirationsDestinationRoutingPolicy
{
    private const string AndresName = "ANDRES STEIMBERG AGENT FOR ESSENTIALGROUPLA";
    private const string AndresLegacyName = "ANDRES STEIMBERG SEGURU";
    private const string AndresShortName = "ANDRES STEIMBERG";
    private const string AndresAlternateSpelling = "ANDRES STEINBERG";
    private const string AlbertoName = "ALBERTO VOLIO S";
    private const string JavierName = "JAVIER MARTINEZ";
    private const string PcGuanacasteName = "PC GUANACASTE";
    private const string ContadoCoriMotorsName = "CONTADO CORI MOTORS";
    private const string VariosCoriMotorsName = "VARIOS CORI MOTORS";
    private const string HernanVarelaName = "HERNAN VARELA";

    private static readonly IReadOnlyList<ExpirationsDestinationGroup> AndresGroups =
    [
        ExpirationsDestinationGroup.Personales,
        ExpirationsDestinationGroup.Generales,
        ExpirationsDestinationGroup.Agencias
    ];

    private static readonly IReadOnlyList<ExpirationsDestinationGroup> AlbertoGroups =
    [
        ExpirationsDestinationGroup.Principal,
        ExpirationsDestinationGroup.PcGuanacaste,
        ExpirationsDestinationGroup.ContadoCoriMotors,
        ExpirationsDestinationGroup.VariosCoriMotors
    ];

    private static readonly IReadOnlyList<ExpirationsDestinationGroup> JavierGroups =
    [
        ExpirationsDestinationGroup.Principal,
        ExpirationsDestinationGroup.HernanVarela
    ];

    private readonly ExpirationsBrokerNormalizer _normalizer;
    private readonly Guid? _andresId;
    private readonly Guid? _albertoId;
    private readonly Guid? _javierId;

    public ExpirationsDestinationRoutingPolicy(
        IEnumerable<ExpirationsBrokerCatalogItem> catalog,
        ExpirationsBrokerNormalizer? normalizer = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _normalizer = normalizer ?? new ExpirationsBrokerNormalizer();
        var items = catalog.ToList();
        _andresId = FindPreferredUnique(
            items,
            AndresName,
            AndresLegacyName,
            AndresShortName,
            AndresAlternateSpelling);
        _albertoId = FindUnique(items, AlbertoName);
        _javierId = FindUnique(items, JavierName);
    }

    public IReadOnlyList<ExpirationsDestinationGroup> AvailableGroups(Guid brokerId)
    {
        if (_andresId == brokerId)
            return AndresGroups;
        if (_albertoId == brokerId)
            return AlbertoGroups;
        if (_javierId == brokerId)
            return JavierGroups;
        return [ExpirationsDestinationGroup.Principal];
    }

    public static IReadOnlyList<ExpirationsDestinationGroup> AvailableGroupsForBrokerName(
        string brokerName,
        ExpirationsBrokerNormalizer? normalizer = null)
    {
        var normalized = (normalizer ?? new ExpirationsBrokerNormalizer()).Normalize(brokerName);
        if (normalized is AndresName or AndresLegacyName or AndresShortName or AndresAlternateSpelling)
            return AndresGroups;
        if (normalized == AlbertoName)
            return AlbertoGroups;
        if (normalized == JavierName)
            return JavierGroups;
        return [ExpirationsDestinationGroup.Principal];
    }

    public bool IsAllowed(Guid brokerId, ExpirationsDestinationGroup group) =>
        group == ExpirationsDestinationGroup.Principal || AvailableGroups(brokerId).Contains(group);

    public ExpirationsDestinationGroup ConsolidateOutputGroup(
        Guid brokerId,
        ExpirationsDestinationGroup group) =>
        brokerId == _andresId || brokerId == _albertoId
            ? ExpirationsDestinationGroup.Principal
            : group;

    public bool TryResolveDirect(string normalizedIdentifier, out ExpirationsDestinationKey destination)
    {
        destination = default;
        if (_andresId is { } andres &&
            InferDeterministicGroup(andres, normalizedIdentifier, out _) is { } andresGroup)
        {
            destination = new(andres, andresGroup);
            return true;
        }
        if (normalizedIdentifier == PcGuanacasteName && _albertoId is { } albertoPc)
        {
            destination = new(albertoPc, ExpirationsDestinationGroup.PcGuanacaste);
            return true;
        }
        if (normalizedIdentifier == ContadoCoriMotorsName && _albertoId is { } albertoContado)
        {
            destination = new(albertoContado, ExpirationsDestinationGroup.ContadoCoriMotors);
            return true;
        }
        if (normalizedIdentifier == VariosCoriMotorsName && _albertoId is { } albertoVarios)
        {
            destination = new(albertoVarios, ExpirationsDestinationGroup.ContadoCoriMotors);
            return true;
        }
        if (normalizedIdentifier == HernanVarelaName && _javierId is { } javier)
        {
            destination = new(javier, ExpirationsDestinationGroup.HernanVarela);
            return true;
        }
        return false;
    }

    public bool TryResolveDirectConflict(
        string normalizedIdentifier,
        out Guid brokerId,
        out string diagnostic)
    {
        brokerId = default;
        diagnostic = string.Empty;
        if (_andresId is not { } andres)
            return false;
        _ = InferDeterministicGroup(andres, normalizedIdentifier, out var inferredDiagnostic);
        if (string.IsNullOrWhiteSpace(inferredDiagnostic))
            return false;
        brokerId = andres;
        diagnostic = inferredDiagnostic;
        return true;
    }

    public ExpirationsDestinationGroup? ResolveGroup(
        Guid brokerId,
        string normalizedIdentifier,
        IEnumerable<ExpirationsBrokerAssociation> matchingAssociations,
        out string? diagnostic)
    {
        diagnostic = null;
        var allowed = AvailableGroups(brokerId);
        var brokerAssociations = matchingAssociations
            .Where(association => association.BrokerId == brokerId)
            .ToList();
        if (_andresId == brokerId)
        {
            var deterministicAndresGroup = InferDeterministicGroup(
                brokerId,
                normalizedIdentifier,
                out diagnostic);
            if (deterministicAndresGroup.HasValue)
            {
                var conflictingExactGroup = brokerAssociations
                    .Where(association =>
                        association.DestinationGroup.HasValue &&
                        _normalizer.Normalize(association.NormalizedValue.Length > 0
                            ? association.NormalizedValue
                            : association.Value) == normalizedIdentifier)
                    .Select(association => association.DestinationGroup!.Value)
                    .Distinct()
                    .Any(group => group != ExpirationsDestinationGroup.Principal &&
                                  group != deterministicAndresGroup.Value);
                if (conflictingExactGroup)
                {
                    diagnostic = "La asociación específica de Andrés contradice el archivo destino determinado por la identificación.";
                    return null;
                }
                return deterministicAndresGroup;
            }
            if (!string.IsNullOrWhiteSpace(diagnostic))
                return null;
        }

        var persistedGroups = brokerAssociations
            .Where(association => association.DestinationGroup.HasValue)
            .Select(association => association.DestinationGroup!.Value)
            .Distinct()
            .ToList();
        if (persistedGroups.Any(group => !IsAllowed(brokerId, group)))
        {
            diagnostic = "La asociación usa un archivo destino que no corresponde al corredor.";
            return null;
        }
        var explicitGroups = persistedGroups
            .Select(Canonicalize)
            .Distinct()
            .ToList();
        if (explicitGroups.Count > 1)
        {
            diagnostic = "Existen asociaciones activas incompatibles para el mismo corredor y archivo destino.";
            return null;
        }
        if (explicitGroups.Count == 1)
            return explicitGroups[0];

        var inferred = InferDeterministicGroup(brokerId, normalizedIdentifier, out diagnostic);
        if (inferred.HasValue)
            return inferred;
        if (allowed.Count == 1)
            return allowed[0];

        diagnostic ??= "La asociación existente no indica el archivo destino; seleccione uno antes de generar.";
        return null;
    }

    public ExpirationsDestinationGroup? InferDeterministicGroup(
        Guid brokerId,
        string normalizedIdentifier,
        out string? diagnostic)
    {
        diagnostic = null;
        if (_andresId == brokerId)
        {
            var tokens = normalizedIdentifier.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var candidates = new HashSet<ExpirationsDestinationGroup>();
            if (tokens.Any(token => token is "107" or "189"))
                candidates.Add(ExpirationsDestinationGroup.Personales);
            if (tokens.Any(token => token is "94" or "172"))
                candidates.Add(ExpirationsDestinationGroup.Generales);
            if (tokens.Any(token => token is "DA" or "CC" or "CM" or "QM") ||
                ContainsAdjacentTokens(tokens, "AGENCIAS", "DIRECTO"))
            {
                candidates.Add(ExpirationsDestinationGroup.Agencias);
            }
            if (candidates.Count == 1)
                return candidates.Single();
            if (candidates.Count > 1)
                diagnostic = "La identificación de Andrés contiene marcadores de más de un archivo destino.";
            return null;
        }

        if (_albertoId == brokerId)
        {
            var candidates = new HashSet<ExpirationsDestinationGroup>();
            if (ContainsTokenPhrase(normalizedIdentifier, PcGuanacasteName))
                candidates.Add(ExpirationsDestinationGroup.PcGuanacaste);
            if (ContainsTokenPhrase(normalizedIdentifier, ContadoCoriMotorsName))
                candidates.Add(ExpirationsDestinationGroup.ContadoCoriMotors);
            if (ContainsTokenPhrase(normalizedIdentifier, VariosCoriMotorsName))
                candidates.Add(ExpirationsDestinationGroup.ContadoCoriMotors);
            if (normalizedIdentifier == AlbertoName)
                candidates.Add(ExpirationsDestinationGroup.Principal);
            if (candidates.Count > 1)
            {
                diagnostic = "La identificación de Alberto contiene marcadores de más de un archivo destino.";
                return null;
            }
            return candidates.Count == 1 ? candidates.Single() : null;
        }

        if (_javierId == brokerId)
        {
            if (ContainsTokenPhrase(normalizedIdentifier, HernanVarelaName))
                return ExpirationsDestinationGroup.HernanVarela;
            if (normalizedIdentifier == JavierName)
                return ExpirationsDestinationGroup.Principal;
            return null;
        }

        return ExpirationsDestinationGroup.Principal;
    }

    private static ExpirationsDestinationGroup Canonicalize(ExpirationsDestinationGroup group) =>
        group == ExpirationsDestinationGroup.VariosCoriMotors
            ? ExpirationsDestinationGroup.ContadoCoriMotors
            : group;

    private Guid? FindPreferredUnique(
        IReadOnlyList<ExpirationsBrokerCatalogItem> catalog,
        string preferredName,
        params string[] fallbackNames)
    {
        var preferredMatches = catalog
            .Where(item => _normalizer.Normalize(item.Name) == preferredName)
            .Select(item => item.BrokerId)
            .Distinct()
            .ToList();
        if (preferredMatches.Count == 1)
            return preferredMatches[0];
        if (preferredMatches.Count > 1)
            return null;
        return FindUnique(catalog, fallbackNames);
    }

    private Guid? FindUnique(
        IReadOnlyList<ExpirationsBrokerCatalogItem> catalog,
        params string[] acceptedNames)
    {
        var accepted = acceptedNames.ToHashSet(StringComparer.Ordinal);
        var matches = catalog
            .Where(item => accepted.Contains(_normalizer.Normalize(item.Name)))
            .Select(item => item.BrokerId)
            .Distinct()
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool ContainsAdjacentTokens(IReadOnlyList<string> tokens, string first, string second)
    {
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (tokens[index] == first && tokens[index + 1] == second)
                return true;
        }
        return false;
    }

    private static bool ContainsTokenPhrase(string normalizedValue, string normalizedPhrase)
    {
        var valueTokens = normalizedValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var phraseTokens = normalizedPhrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var start = 0; start <= valueTokens.Length - phraseTokens.Length; start++)
        {
            if (phraseTokens.Select((token, offset) => valueTokens[start + offset] == token).All(match => match))
                return true;
        }
        return false;
    }
}
