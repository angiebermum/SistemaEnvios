using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsBrokerConfigurationServiceTests
{
    private static readonly Guid BrokerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherBrokerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Created = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BrokerWithoutProfileUsesDefaultsWithoutCreatingDocument()
    {
        var profiles = new FakeProfileRepository([]);
        var service = Service(profiles);

        var item = Assert.Single(await service.ListAsync(TestContext.Current.CancellationToken));
        var result = await service.SaveAsync(item, TestContext.Current.CancellationToken);

        Assert.True(item.IsActive);
        Assert.Empty(item.Assistants);
        Assert.False(item.HasExplicitProfile);
        Assert.Equal(ExpirationsBrokerConfigurationSaveOutcome.NoChanges, result.Outcome);
        Assert.Equal(0, profiles.CreateCalls);
    }

    [Fact]
    public async Task InactivatingBrokerWithoutProfileCreatesProfile()
    {
        var profiles = new FakeProfileRepository([]);
        var service = Service(profiles);
        var item = With(await service.GetAsync(BrokerId, TestContext.Current.CancellationToken), isActive: false);

        var result = await service.SaveAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsBrokerConfigurationSaveOutcome.Created, result.Outcome);
        Assert.False(Assert.Single(profiles.Created).IsActive);
        Assert.Equal(Now, profiles.Created[0].CreatedAtUtc);
        Assert.Equal(Now, profiles.Created[0].UpdatedAtUtc);
    }

    [Fact]
    public async Task AddingAssistantWithoutProfileCreatesProfile()
    {
        var profiles = new FakeProfileRepository([]);
        var service = Service(profiles);
        var assistant = new ExpirationsAssistant
        {
            Id = Guid.NewGuid(), Name = "Ana", Email = " ana@example.test ", IsActive = true
        };
        var item = With(await service.GetAsync(BrokerId, TestContext.Current.CancellationToken), assistants: [assistant]);

        var result = await service.SaveAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsBrokerConfigurationSaveOutcome.Created, result.Outcome);
        Assert.Equal("ana@example.test", Assert.Single(profiles.Created[0].Assistants).Email);
    }

    [Fact]
    public async Task ExistingProfileUpdatesWithTokenAndPreservesCreationTimestamp()
    {
        var profiles = new FakeProfileRepository([Stored(Profile(false), "version-7")]);
        var service = Service(profiles);
        var item = With(await service.GetAsync(BrokerId, TestContext.Current.CancellationToken), isActive: true);

        var result = await service.SaveAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsBrokerConfigurationSaveOutcome.Updated, result.Outcome);
        var update = Assert.Single(profiles.Updated);
        Assert.Equal("version-7", update.ExpectedUpdateTime);
        Assert.Equal(Created, update.Profile.CreatedAtUtc);
        Assert.Equal(Now, update.Profile.UpdatedAtUtc);
        Assert.Equal(0, profiles.CreateCalls);
    }

    [Fact]
    public async Task DirectoryAlwaysSuppliesNameAndPrimaryEmails()
    {
        var profiles = new FakeProfileRepository([Stored(Profile(false), "version-1")]);
        var service = Service(profiles);
        var tampered = With(await service.GetAsync(BrokerId, TestContext.Current.CancellationToken), name: "Nombre falso", emails: ["fake@example.test"]);

        var result = await service.SaveAsync(tampered, TestContext.Current.CancellationToken);

        Assert.Equal("Nombre maestro", result.Configuration.Name);
        Assert.Equal(["master@example.test", "second@example.test"], result.Configuration.PrimaryEmailAddresses);
        Assert.DoesNotContain("name", new ExpirationsBrokerProfileMapper().ToFields(profiles.Updated[0].Profile).Keys);
        Assert.DoesNotContain("primaryEmailAddresses", new ExpirationsBrokerProfileMapper().ToFields(profiles.Updated[0].Profile).Keys);
    }

    [Fact]
    public async Task UpdateConflictDoesNotOverwriteAndReturnsReloadedConfiguration()
    {
        var profiles = new FakeProfileRepository([Stored(Profile(true), "old")])
        {
            UpdateConflict = true,
            ConflictReplacement = Stored(Profile(false), "remote")
        };
        var service = Service(profiles);
        var item = With(await service.GetAsync(BrokerId, TestContext.Current.CancellationToken), assistants: [Assistant("Local", "local@example.test")]);

        var result = await service.SaveAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsBrokerConfigurationSaveOutcome.ConcurrencyConflict, result.Outcome);
        Assert.False(result.Configuration.IsActive);
        Assert.Equal("remote", result.Configuration.ProfileUpdateTime);
        Assert.Empty(profiles.Updated);
    }

    [Fact]
    public async Task CreateConflictDoesNotOverwriteAndReturnsReloadedConfiguration()
    {
        var profiles = new FakeProfileRepository([])
        {
            CreateConflict = true,
            ConflictReplacement = Stored(Profile(false), "created-remotely")
        };
        var service = Service(profiles);
        var item = With(await service.GetAsync(BrokerId, TestContext.Current.CancellationToken), isActive: false);

        var result = await service.SaveAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsBrokerConfigurationSaveOutcome.ConcurrencyConflict, result.Outcome);
        Assert.True(result.Configuration.HasExplicitProfile);
        Assert.Equal("created-remotely", result.Configuration.ProfileUpdateTime);
        Assert.Empty(profiles.Created);
    }

    [Fact]
    public async Task SavingExpirationsProfileNeverTouchesCommissionsBrokerState()
    {
        var commissionsAssistant = new BrokerAssistant
        {
            Id = Guid.NewGuid(), Name = "Comisiones", Email = "commissions@example.test", IsActive = true
        };
        var commissionsBroker = new Broker
        {
            Id = BrokerId,
            Name = "Comisiones original",
            PrimaryEmailAddresses = ["commissions-broker@example.test"],
            Assistants = [commissionsAssistant],
            IsActive = true
        };
        var profiles = new FakeProfileRepository([]);
        var service = Service(profiles);
        var item = With(
            await service.GetAsync(BrokerId, TestContext.Current.CancellationToken),
            isActive: false,
            assistants: [Assistant("Vencimientos", "expiration@example.test")]);

        _ = await service.SaveAsync(item, TestContext.Current.CancellationToken);

        Assert.True(commissionsBroker.IsActive);
        Assert.Same(commissionsAssistant, Assert.Single(commissionsBroker.Assistants));
        Assert.Equal("Comisiones", commissionsBroker.Assistants[0].Name);
    }

    [Fact]
    public async Task ExistingDefaultProfileIsUpdatedAndNeverDeleted()
    {
        var profiles = new FakeProfileRepository([Stored(Profile(false), "version-2")]);
        var service = Service(profiles);
        var item = With(await service.GetAsync(BrokerId, TestContext.Current.CancellationToken), isActive: true, assistants: []);

        var result = await service.SaveAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsBrokerConfigurationSaveOutcome.Updated, result.Outcome);
        Assert.Single(profiles.Updated);
        Assert.True(profiles.Documents.Single().Value.IsActive);
    }

    [Fact]
    public async Task SavingSpecialCreatesProfileAndExistingProfileUsesOptimisticUpdate()
    {
        var profiles = new FakeProfileRepository([]);
        var service = Service(profiles);
        var special = With(
            await service.GetAsync(BrokerId, TestContext.Current.CancellationToken),
            mode: ExpirationsNextMonthGenerationMode.SpecialDualSorted);

        var created = await service.SaveAsync(special, TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsBrokerConfigurationSaveOutcome.Created, created.Outcome);
        Assert.Equal(
            ExpirationsNextMonthGenerationMode.SpecialDualSorted,
            Assert.Single(profiles.Created).NextMonthGenerationMode);
        var backToStandard = With(
            created.Configuration,
            assistants: [Assistant("Conservado", "kept@example.test")],
            mode: ExpirationsNextMonthGenerationMode.Standard);
        var updated = await service.SaveAsync(backToStandard, TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsBrokerConfigurationSaveOutcome.Updated, updated.Outcome);
        Assert.Equal(created.Configuration.ProfileUpdateTime, Assert.Single(profiles.Updated).ExpectedUpdateTime);
        Assert.Equal(ExpirationsNextMonthGenerationMode.Standard, profiles.Updated[0].Profile.NextMonthGenerationMode);
        Assert.Equal("kept@example.test", Assert.Single(profiles.Updated[0].Profile.Assistants).Email);
        Assert.True(profiles.Updated[0].Profile.IsActive);
    }

    [Fact]
    public async Task ServiceRejectsSecondSpecialWithoutChangingExistingProfile()
    {
        var other = new ExpirationsBrokerProfile
        {
            BrokerId = OtherBrokerId,
            IsActive = true,
            NextMonthGenerationMode = ExpirationsNextMonthGenerationMode.SpecialDualSorted,
            CreatedAtUtc = Created,
            UpdatedAtUtc = Created
        };
        var profiles = new FakeProfileRepository([Stored(other, "other-version")]);
        var service = Service(profiles);
        var special = With(
            await service.GetAsync(BrokerId, TestContext.Current.CancellationToken),
            mode: ExpirationsNextMonthGenerationMode.SpecialDualSorted);

        var result = await service.SaveAsync(special, TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsBrokerConfigurationSaveOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(
            "Ya existe un corredor configurado con el formato especial de mes siguiente.",
            result.Message);
        Assert.Empty(profiles.Created);
        Assert.Empty(profiles.Updated);
        Assert.Equal(
            ExpirationsNextMonthGenerationMode.SpecialDualSorted,
            profiles.Documents.Single().Value.NextMonthGenerationMode);
    }

    private static ExpirationsBrokerConfigurationService Service(FakeProfileRepository profiles) => new(
        new FakeDirectoryRepository([Directory()]),
        profiles,
        timeProvider: new FixedTimeProvider(Now));

    private static ExpirationsBrokerConfigurationItem With(
        ExpirationsBrokerConfigurationItem? source,
        bool? isActive = null,
        IReadOnlyList<ExpirationsAssistant>? assistants = null,
        string? name = null,
        IReadOnlyList<string>? emails = null,
        ExpirationsNextMonthGenerationMode? mode = null)
    {
        source = Assert.IsType<ExpirationsBrokerConfigurationItem>(source);
        return new ExpirationsBrokerConfigurationItem
        {
            BrokerId = source.BrokerId,
            Name = name ?? source.Name,
            PrimaryEmailAddresses = emails ?? source.PrimaryEmailAddresses,
            IsActive = isActive ?? source.IsActive,
            NextMonthGenerationMode = mode ?? source.NextMonthGenerationMode,
            Assistants = assistants ?? source.Assistants,
            HasExplicitProfile = source.HasExplicitProfile,
            ProfileUpdateTime = source.ProfileUpdateTime,
            ProfileCreatedAtUtc = source.ProfileCreatedAtUtc,
            ProfileUpdatedAtUtc = source.ProfileUpdatedAtUtc
        };
    }

    private static ExpirationsBrokerDirectoryEntry Directory() => new()
    {
        BrokerId = BrokerId,
        Name = "Nombre maestro",
        PrimaryEmailAddresses = ["master@example.test", "second@example.test"]
    };

    private static ExpirationsBrokerProfile Profile(bool active) => new()
    {
        BrokerId = BrokerId,
        IsActive = active,
        Assistants = [],
        CreatedAtUtc = Created,
        UpdatedAtUtc = Created
    };

    private static ExpirationsAssistant Assistant(string name, string email, bool active = true) => new()
    {
        Id = Guid.NewGuid(), Name = name, Email = email, IsActive = active
    };

    private static FirestoreStoredDocument<ExpirationsBrokerProfile> Stored(
        ExpirationsBrokerProfile profile,
        string updateTime) => new(
        profile,
        $"modules/vencimientos/brokerProfiles/{profile.BrokerId:D}",
        updateTime);

    private sealed class FakeDirectoryRepository(IEnumerable<ExpirationsBrokerDirectoryEntry> entries)
        : IExpirationsBrokerDirectoryRepository
    {
        private readonly List<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>> _documents = entries
            .Select(value => new FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>(
                value, $"brokers/{value.BrokerId:D}", "directory-version"))
            .ToList();

        public Task<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_documents.FirstOrDefault(value => value.Value.BrokerId == brokerId));

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>>(_documents);
    }

    private sealed class FakeProfileRepository(
        IEnumerable<FirestoreStoredDocument<ExpirationsBrokerProfile>> documents)
        : IExpirationsBrokerProfileRepository
    {
        private int _version = 10;
        public List<FirestoreStoredDocument<ExpirationsBrokerProfile>> Documents { get; } = documents.ToList();
        public List<ExpirationsBrokerProfile> Created { get; } = [];
        public List<UpdateCall> Updated { get; } = [];
        public int CreateCalls { get; private set; }
        public bool CreateConflict { get; init; }
        public bool UpdateConflict { get; init; }
        public FirestoreStoredDocument<ExpirationsBrokerProfile>? ConflictReplacement { get; init; }

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Documents.FirstOrDefault(value => value.Value.BrokerId == brokerId));

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>>(Documents.ToList());

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> CreateAsync(
            ExpirationsBrokerProfile value,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            if (CreateConflict)
            {
                if (ConflictReplacement is not null)
                    Documents.Add(ConflictReplacement);
                throw new FirestoreRestException(FirestoreFailureKind.Conflict, "Conflicto de prueba.");
            }
            Created.Add(value);
            var stored = Stored(value, $"version-{++_version}");
            Documents.Add(stored);
            return Task.FromResult(stored);
        }

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> UpdateAsync(
            ExpirationsBrokerProfile value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            if (UpdateConflict)
            {
                if (ConflictReplacement is not null)
                {
                    Documents.Clear();
                    Documents.Add(ConflictReplacement);
                }
                throw new FirestoreConcurrencyException(
                    $"modules/vencimientos/brokerProfiles/{value.BrokerId:D}",
                    expectedUpdateTime);
            }
            Updated.Add(new UpdateCall(value, expectedUpdateTime));
            var stored = Stored(value, $"version-{++_version}");
            Documents[Documents.FindIndex(document => document.Value.BrokerId == value.BrokerId)] = stored;
            return Task.FromResult(stored);
        }
    }

    private sealed record UpdateCall(ExpirationsBrokerProfile Profile, string ExpectedUpdateTime);
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
