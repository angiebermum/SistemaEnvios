using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsObservedIdentifierTests
{
    private static readonly Guid BrokerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset FirstSeen = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SafeUniqueResolutionCapturesAliasAndStrictCodeIdempotently()
    {
        var repository = new FakeObservedRepository();
        var analysis = Analysis(Resolved("Adriana Arroyo/AAV - 90", canObserve: true));
        var catalog = Catalog();

        await Capture(repository, FirstSeen).CaptureAsync(analysis, catalog, TestContext.Current.CancellationToken);

        Assert.Equal(2, repository.Documents.Count);
        var alias = repository.Documents.Single(document => document.Value.Kind == ExpirationsAssociationKind.Alias);
        var code = repository.Documents.Single(document => document.Value.Kind == ExpirationsAssociationKind.Code);
        Assert.Equal("Adriana Arroyo/AAV - 90", alias.Value.Value);
        Assert.Equal("AAV - 90", code.Value.Value);
        Assert.Equal(
            ExpirationsObservedIdentifierCaptureService.DeterministicId(BrokerId, code.Value.NormalizedValue),
            code.Value.Id);

        var lastSeen = FirstSeen.AddHours(2);
        await Capture(repository, lastSeen).CaptureAsync(analysis, catalog, TestContext.Current.CancellationToken);
        Assert.Equal(2, repository.Documents.Count);
        Assert.All(repository.Documents, document =>
        {
            Assert.Equal(FirstSeen, document.Value.FirstSeenAtUtc);
            Assert.Equal(lastSeen, document.Value.LastSeenAtUtc);
        });
    }

    [Fact]
    public async Task UnsafeCodePatternStoresOnlyAliasAndManualOrAmbiguousValuesAreNeverCaptured()
    {
        var repository = new FakeObservedRepository();
        var analysis = Analysis(
            Resolved("Adriana Arroyo/AAV90", canObserve: true),
            Resolved("Manual", canObserve: false),
            new ExpirationsBrokerComponentResolution
            {
                RawValue = "Ambiguo",
                NormalizedValue = "AMBIGUO",
                Status = ExpirationsBrokerResolutionStatus.Ambiguous,
                CandidateBrokerIds = [BrokerId, Guid.NewGuid()]
            });

        await Capture(repository, FirstSeen).CaptureAsync(
            analysis,
            Catalog(),
            TestContext.Current.CancellationToken);

        var stored = Assert.Single(repository.Documents).Value;
        Assert.Equal(ExpirationsAssociationKind.Alias, stored.Kind);
        Assert.Equal("Adriana Arroyo/AAV90", stored.Value);
    }

    [Theory]
    [InlineData(ExpirationsBrokerResolutionStatus.Unresolved)]
    [InlineData(ExpirationsBrokerResolutionStatus.InactiveBroker)]
    [InlineData(ExpirationsBrokerResolutionStatus.Excluded)]
    [InlineData(ExpirationsBrokerResolutionStatus.MissingBroker)]
    [InlineData(ExpirationsBrokerResolutionStatus.Ambiguous)]
    public async Task UnsafeResolutionStatusesNeverCreateObservedIdentifiers(
        ExpirationsBrokerResolutionStatus status)
    {
        var repository = new FakeObservedRepository();
        var component = new ExpirationsBrokerComponentResolution
        {
            RawValue = "No seguro/AAV - 90",
            NormalizedValue = "NO SEGURO AAV 90",
            Status = status,
            CandidateBrokerIds = status == ExpirationsBrokerResolutionStatus.Ambiguous
                ? [BrokerId, Guid.Parse("22222222-2222-2222-2222-222222222222")]
                : [BrokerId],
            ResolvedBrokerId = status == ExpirationsBrokerResolutionStatus.InactiveBroker
                ? BrokerId
                : null,
            CanBeObservedAutomatically = false
        };

        await Capture(repository, FirstSeen).CaptureAsync(
            Analysis(component),
            Catalog(),
            TestContext.Current.CancellationToken);

        Assert.Empty(repository.Documents);
    }

    [Fact]
    public async Task IgnoredIdentifierIsNotRecreatedOrUpdatedWhenSeenAgain()
    {
        var repository = new FakeObservedRepository();
        var analysis = Analysis(Resolved("AAV - 90", canObserve: true));
        await Capture(repository, FirstSeen).CaptureAsync(
            analysis,
            Catalog(),
            TestContext.Current.CancellationToken);
        var stored = Assert.Single(repository.Documents);
        stored.Value.IsIgnored = true;
        var ignoredVersion = await repository.UpdateAsync(
            stored.Value,
            stored.UpdateTime,
            TestContext.Current.CancellationToken);

        await Capture(repository, FirstSeen.AddDays(1)).CaptureAsync(
            analysis,
            Catalog(),
            TestContext.Current.CancellationToken);

        var after = Assert.Single(repository.Documents);
        Assert.True(after.Value.IsIgnored);
        Assert.Equal(ignoredVersion.UpdateTime, after.UpdateTime);
        Assert.Equal(FirstSeen, after.Value.LastSeenAtUtc);
    }

    [Fact]
    public void ResolverMarksOnlyUniqueActiveAutomaticResolutionAsSafeToObserve()
    {
        var resolved = new ExpirationsBrokerResolver(Catalog(), []).Resolve(new ExpirationsBrokerComponent
        {
            RawValue = "Adriana Arroyo/AAV - 90",
            NormalizedValue = "ADRIANA ARROYO AAV 90"
        });

        Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved, resolved.Status);
        Assert.True(resolved.CanBeObservedAutomatically);
    }

    [Fact]
    public void ObservedMapperRoundTripsStrictAuditFields()
    {
        var value = new ExpirationsObservedIdentifier
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            BrokerId = BrokerId,
            Kind = ExpirationsAssociationKind.Code,
            Value = "AAV - 90",
            NormalizedValue = "AAV 90",
            FirstSeenAtUtc = FirstSeen,
            LastSeenAtUtc = FirstSeen.AddHours(1),
            IsIgnored = true
        };

        var mapper = new ExpirationsObservedIdentifierMapper();
        var roundTrip = mapper.FromFields(mapper.ToFields(value));

        Assert.Equal(value.Id, roundTrip.Id);
        Assert.Equal(value.BrokerId, roundTrip.BrokerId);
        Assert.Equal(value.Kind, roundTrip.Kind);
        Assert.Equal(value.Value, roundTrip.Value);
        Assert.Equal(value.NormalizedValue, roundTrip.NormalizedValue);
        Assert.Equal(value.FirstSeenAtUtc, roundTrip.FirstSeenAtUtc);
        Assert.Equal(value.LastSeenAtUtc, roundTrip.LastSeenAtUtc);
        Assert.True(roundTrip.IsIgnored);
    }

    private static ExpirationsObservedIdentifierCaptureService Capture(
        FakeObservedRepository repository,
        DateTimeOffset now) => new(repository, timeProvider: new FixedTimeProvider(now));

    private static ExpirationsWorkbookAnalysisResult Analysis(
        params ExpirationsBrokerComponentResolution[] components) => new()
    {
        RowResolutions = components.Select((component, index) => new ExpirationsRowResolution
        {
            RowNumber = (uint)(index + 2),
            Components = [component]
        }).ToList()
    };

    private static ExpirationsBrokerComponentResolution Resolved(string rawValue, bool canObserve) => new()
    {
        RawValue = rawValue,
        NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(rawValue),
        Status = ExpirationsBrokerResolutionStatus.Resolved,
        CandidateBrokerIds = [BrokerId],
        ResolvedBrokerId = BrokerId,
        CanBeObservedAutomatically = canObserve
    };

    private static IReadOnlyList<ExpirationsBrokerCatalogItem> Catalog() =>
    [
        new ExpirationsBrokerCatalogItem
        {
            BrokerId = BrokerId,
            Name = "Adriana Arroyo",
            PrimaryEmailAddresses = ["adriana@example.test"],
            IsActive = true
        }
    ];

    private sealed class FakeObservedRepository : IExpirationsObservedIdentifierRepository
    {
        private int _version;
        public List<FirestoreStoredDocument<ExpirationsObservedIdentifier>> Documents { get; } = [];

        public Task<FirestoreStoredDocument<ExpirationsObservedIdentifier>?> GetAsync(
            Guid identifierId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Documents.FirstOrDefault(document => document.Value.Id == identifierId));

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsObservedIdentifier>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsObservedIdentifier>>>(Documents.ToList());

        public Task<FirestoreStoredDocument<ExpirationsObservedIdentifier>> CreateAsync(
            ExpirationsObservedIdentifier value,
            CancellationToken cancellationToken = default)
        {
            if (Documents.Any(document => document.Value.Id == value.Id))
                throw new InvalidOperationException("Identificador duplicado.");
            var stored = Store(value);
            Documents.Add(stored);
            return Task.FromResult(stored);
        }

        public Task<FirestoreStoredDocument<ExpirationsObservedIdentifier>> UpdateAsync(
            ExpirationsObservedIdentifier value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            var index = Documents.FindIndex(document => document.Value.Id == value.Id);
            if (index < 0 || Documents[index].UpdateTime != expectedUpdateTime)
                throw new FirestoreConcurrencyException($"observedIdentifiers/{value.Id:D}", expectedUpdateTime);
            var stored = Store(value);
            Documents[index] = stored;
            return Task.FromResult(stored);
        }

        private FirestoreStoredDocument<ExpirationsObservedIdentifier> Store(
            ExpirationsObservedIdentifier value) => new(
            ExpirationsObservedIdentifierCaptureService.Copy(value),
            $"modules/vencimientos/observedIdentifiers/{value.Id:D}",
            $"o-{++_version}");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
