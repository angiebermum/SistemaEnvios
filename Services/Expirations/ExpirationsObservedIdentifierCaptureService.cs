using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsObservedIdentifierCaptureService
{
    Task CaptureAsync(
        ExpirationsWorkbookAnalysisResult analysis,
        IReadOnlyList<ExpirationsBrokerCatalogItem> catalog,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsObservedIdentifierCaptureService : IExpirationsObservedIdentifierCaptureService
{
    private static readonly Regex StrictCodePattern = new(
        @"(?:^|/)\s*(?<letters>[A-Za-z]{2,10})\s*-\s*(?<digits>[0-9]{1,6})\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly IExpirationsObservedIdentifierRepository _repository;
    private readonly ExpirationsBrokerNormalizer _normalizer;
    private readonly TimeProvider _timeProvider;

    public ExpirationsObservedIdentifierCaptureService(
        IExpirationsObservedIdentifierRepository repository,
        ExpirationsBrokerNormalizer? normalizer = null,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _normalizer = normalizer ?? new ExpirationsBrokerNormalizer();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task CaptureAsync(
        ExpirationsWorkbookAnalysisResult analysis,
        IReadOnlyList<ExpirationsBrokerCatalogItem> catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(catalog);
        var masterNames = catalog
            .GroupBy(item => item.BrokerId)
            .ToDictionary(group => group.Key, group => _normalizer.Normalize(group.First().Name));
        var candidates = new Dictionary<(Guid BrokerId, string NormalizedValue), ExpirationsObservedIdentifier>();
        var now = _timeProvider.GetUtcNow();

        foreach (var component in analysis.RowResolutions
                     .SelectMany(row => row.Components)
                     .Where(value => value.CanBeObservedAutomatically &&
                         value.Status == ExpirationsBrokerResolutionStatus.Resolved &&
                         value.ResolvedBrokerId is not null))
        {
            var brokerId = component.ResolvedBrokerId!.Value;
            var raw = component.RawValue.Trim();
            var normalized = _normalizer.Normalize(raw);
            if (raw.Length == 0 || normalized.Length == 0)
                continue;

            if (!masterNames.TryGetValue(brokerId, out var masterName) || normalized != masterName)
                AddCandidate(candidates, brokerId, ExpirationsAssociationKind.Alias, raw, normalized, now);

            var match = StrictCodePattern.Match(raw);
            if (!match.Success)
                continue;
            var code = $"{match.Groups["letters"].Value.ToUpperInvariant()} - {match.Groups["digits"].Value}";
            var normalizedCode = _normalizer.Normalize(code);
            AddCandidate(candidates, brokerId, ExpirationsAssociationKind.Code, code, normalizedCode, now);
        }

        foreach (var candidate in candidates.Values
                     .OrderBy(value => value.BrokerId)
                     .ThenBy(value => value.NormalizedValue, StringComparer.Ordinal))
        {
            await UpsertAsync(candidate, cancellationToken);
        }
    }

    internal static Guid DeterministicId(Guid brokerId, string normalizedValue)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{brokerId:D}|{normalizedValue}"));
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }

    private static void AddCandidate(
        IDictionary<(Guid BrokerId, string NormalizedValue), ExpirationsObservedIdentifier> candidates,
        Guid brokerId,
        ExpirationsAssociationKind kind,
        string value,
        string normalizedValue,
        DateTimeOffset now)
    {
        if (normalizedValue.Length == 0)
            return;
        candidates[(brokerId, normalizedValue)] = new ExpirationsObservedIdentifier
        {
            Id = DeterministicId(brokerId, normalizedValue),
            BrokerId = brokerId,
            Kind = kind,
            Value = value,
            NormalizedValue = normalizedValue,
            FirstSeenAtUtc = now,
            LastSeenAtUtc = now,
            IsIgnored = false
        };
    }

    private async Task UpsertAsync(
        ExpirationsObservedIdentifier candidate,
        CancellationToken cancellationToken)
    {
        var stored = await _repository.GetAsync(candidate.Id, cancellationToken);
        if (stored is null)
        {
            try
            {
                await _repository.CreateAsync(candidate, cancellationToken);
                return;
            }
            catch (FirestoreRestException ex) when (ex.Kind == FirestoreFailureKind.Conflict)
            {
                stored = await _repository.GetAsync(candidate.Id, cancellationToken);
                if (stored is null)
                    throw;
            }
        }

        if (stored.Value.IsIgnored)
            return;
        var updated = Copy(stored.Value);
        updated.LastSeenAtUtc = candidate.LastSeenAtUtc;
        try
        {
            await _repository.UpdateAsync(updated, stored.UpdateTime, cancellationToken);
        }
        catch (FirestoreConcurrencyException)
        {
            var latest = await _repository.GetAsync(candidate.Id, cancellationToken);
            if (latest is null || latest.Value.IsIgnored)
                return;
            updated = Copy(latest.Value);
            updated.LastSeenAtUtc = candidate.LastSeenAtUtc;
            await _repository.UpdateAsync(updated, latest.UpdateTime, cancellationToken);
        }
    }

    internal static ExpirationsObservedIdentifier Copy(ExpirationsObservedIdentifier value) => new()
    {
        Id = value.Id,
        BrokerId = value.BrokerId,
        Kind = value.Kind,
        Value = value.Value,
        NormalizedValue = value.NormalizedValue,
        FirstSeenAtUtc = value.FirstSeenAtUtc,
        LastSeenAtUtc = value.LastSeenAtUtc,
        IsIgnored = value.IsIgnored
    };
}
