using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsDataFoundationTests
{
    private static readonly Guid BrokerOne = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BrokerTwo = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FelixBroker = Guid.Parse("6059c919-7198-45b9-a2c3-c6eab96a1503");
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DirectoryMapperReadsOnlySharedIdentityFieldsAndIgnoresCommissionsFields()
    {
        var fields = new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["id"] = FirestoreRestValue.String(BrokerOne.ToString("D")),
            ["name"] = FirestoreRestValue.String("Corredor compartido"),
            ["primaryEmailAddresses"] = FirestoreRestValue.Array([
                FirestoreRestValue.String("broker@example.test")
            ]),
            ["assistants"] = FirestoreRestValue.String("tipo irrelevante para Vencimientos"),
            ["deductions"] = FirestoreRestValue.Boolean(true),
            ["associatedWorksheetNames"] = FirestoreRestValue.Integer(7),
            ["isActive"] = FirestoreRestValue.String("campo de Comisiones"),
            ["requiresReview"] = FirestoreRestValue.Null(),
            ["reviewNote"] = FirestoreRestValue.Array([])
        };

        var mapped = new ExpirationsBrokerDirectoryMapper().FromFields(fields);

        Assert.Equal(BrokerOne, mapped.BrokerId);
        Assert.Equal("Corredor compartido", mapped.Name);
        Assert.Equal(["broker@example.test"], mapped.PrimaryEmailAddresses);
        Assert.Throws<NotSupportedException>(() =>
            new ExpirationsBrokerDirectoryMapper().ToFields(mapped));
    }

    [Fact]
    public async Task DirectoryRepositoryReadsExactSharedBrokerPath()
    {
        var client = new InMemoryFirestoreRestClient();
        await client.CreateDocumentAsync(
            string.Empty,
            "brokers",
            BrokerOne.ToString("D"),
            SharedBrokerFields(BrokerOne, "Directorio", "directory@example.test"),
            TestContext.Current.CancellationToken);
        var repository = new ExpirationsBrokerDirectoryRepository(client);

        var stored = await repository.GetAsync(BrokerOne, TestContext.Current.CancellationToken);
        var listed = await repository.ListAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal($"brokers/{BrokerOne:D}", stored.DocumentPath);
        Assert.Equal("Directorio", stored.Value.Name);
        Assert.Equal(["directory@example.test"], stored.Value.PrimaryEmailAddresses);
        Assert.Single(listed);
    }

    [Fact]
    public async Task BrokerWithoutProfileDefaultsToActiveAndNoAssistants()
    {
        var catalog = await Catalog([Directory(BrokerOne, "Nuevo", "new@example.test")], [])
            .LoadAsync(TestContext.Current.CancellationToken);

        var item = Assert.Single(catalog.Items);
        Assert.True(item.IsActive);
        Assert.Empty(item.Assistants);
    }

    [Fact]
    public async Task CatalogPersistentlyInitializesRealFelixBrokerAsNextMonthSpecialOnlyOnce()
    {
        var profiles = new FakeProfileRepository([]);
        var service = new ExpirationsBrokerCatalogService(
            new FakeDirectoryRepository([Directory(FelixBroker, "Felix Lara", "felix@example.test")]),
            profiles);

        var first = Assert.Single((await service.LoadAsync(TestContext.Current.CancellationToken)).Items);
        var second = Assert.Single((await service.LoadAsync(TestContext.Current.CancellationToken)).Items);

        Assert.Equal(ExpirationsNextMonthGenerationMode.SpecialDualSorted, first.NextMonthGenerationMode);
        Assert.Equal(ExpirationsNextMonthGenerationMode.SpecialDualSorted, second.NextMonthGenerationMode);
        Assert.Single(profiles.Documents);
    }

    [Fact]
    public async Task BrokerProfileSuppliesOnlyExpirationsStateAndAssistants()
    {
        var assistant = Assistant();
        var profile = Profile(BrokerOne, isActive: false, assistants: [assistant]);
        var catalog = await Catalog(
            [Directory(BrokerOne, "Nombre maestro", "master@example.test")],
            [profile]).LoadAsync(TestContext.Current.CancellationToken);

        var item = Assert.Single(catalog.Items);
        Assert.Equal("Nombre maestro", item.Name);
        Assert.Equal(["master@example.test"], item.PrimaryEmailAddresses);
        Assert.False(item.IsActive);
        Assert.Equal(assistant.Email, Assert.Single(item.Assistants).Email);
        var profileFields = new ExpirationsBrokerProfileMapper().ToFields(profile);
        Assert.DoesNotContain("name", profileFields.Keys);
        Assert.DoesNotContain("primaryEmailAddresses", profileFields.Keys);
    }

    [Fact]
    public async Task SharedBrokerEmailChangeIsReflectedWithoutProfileCopy()
    {
        var directoryEntry = Directory(BrokerOne, "Corredor", "old@example.test");
        var directory = new FakeDirectoryRepository([directoryEntry]);
        var service = new ExpirationsBrokerCatalogService(
            directory,
            new FakeProfileRepository([Profile(BrokerOne)]));

        var before = Assert.Single((await service.LoadAsync(TestContext.Current.CancellationToken)).Items);
        directoryEntry.PrimaryEmailAddresses = ["new@example.test"];
        var after = Assert.Single((await service.LoadAsync(TestContext.Current.CancellationToken)).Items);

        Assert.Equal(["old@example.test"], before.PrimaryEmailAddresses);
        Assert.Equal(["new@example.test"], after.PrimaryEmailAddresses);
        Assert.DoesNotContain(
            typeof(ExpirationsBrokerProfile).GetProperties(),
            property => property.Name == "PrimaryEmailAddresses");
    }

    [Fact]
    public async Task NewSharedBrokerAppearsWithoutCreatingProfile()
    {
        var directory = new FakeDirectoryRepository([Directory(BrokerOne, "Uno", "one@example.test")]);
        var profiles = new FakeProfileRepository([]);
        var service = new ExpirationsBrokerCatalogService(directory, profiles);
        _ = await service.LoadAsync(TestContext.Current.CancellationToken);

        directory.Documents.Add(Stored(
            Directory(BrokerTwo, "Dos", "two@example.test"),
            $"brokers/{BrokerTwo:D}"));
        var updated = await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, updated.Items.Count);
        Assert.Contains(updated.Items, item => item.BrokerId == BrokerTwo && item.IsActive);
        Assert.Empty(profiles.Documents);
    }

    [Fact]
    public async Task OrphanProfileDoesNotCreateFictitiousBroker()
    {
        var catalog = await Catalog(
            [Directory(BrokerOne, "Válido", "valid@example.test")],
            [Profile(BrokerTwo)]).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Single(catalog.Items);
        Assert.DoesNotContain(catalog.Items, item => item.BrokerId == BrokerTwo);
        Assert.Contains(catalog.Warnings, warning => warning.Contains(BrokerTwo.ToString("D"), StringComparison.Ordinal));
    }

    [Fact]
    public void ProfileMapperRoundTripsIndependentAssistants()
    {
        var cancellationAssistant = new ExpirationsAssistant
        {
            Id = Guid.NewGuid(),
            Name = "Asistente Cancelaciones",
            Email = "cancelaciones@example.test",
            IsActive = true
        };
        var profile = Profile(
            BrokerOne,
            isActive: false,
            assistants: [Assistant()],
            cancellationAssistants: [cancellationAssistant]);
        var mapper = new ExpirationsBrokerProfileMapper();

        var mapped = mapper.FromFields(mapper.ToFields(profile));

        Assert.Equal(profile.BrokerId, mapped.BrokerId);
        Assert.False(mapped.IsActive);
        var assistant = Assert.Single(mapped.Assistants);
        Assert.Equal("Asistente Vencimientos", assistant.Name);
        Assert.Equal("assistant@example.test", assistant.Email);
        Assert.False(assistant.IsActive);
        Assert.Equal(
            "cancelaciones@example.test",
            Assert.Single(mapped.CancellationAssistants).Email);
    }

    [Fact]
    public void ProfileMapperDefaultsMissingCancellationAssistantsToEmptyWithoutFallback()
    {
        var mapper = new ExpirationsBrokerProfileMapper();
        var fields = mapper.ToFields(Profile(BrokerOne, assistants: [Assistant()]))
            .ToDictionary(item => item.Key, item => item.Value);
        Assert.True(fields.Remove("cancellationAssistants"));

        var mapped = mapper.FromFields(fields);

        Assert.Single(mapped.Assistants);
        Assert.Empty(mapped.CancellationAssistants);
    }

    [Fact]
    public void ProfileMapperDefaultsOldDocumentsToStandardAndRoundTripsAllowedModes()
    {
        var mapper = new ExpirationsBrokerProfileMapper();
        var oldFields = mapper.ToFields(Profile(BrokerOne)).ToDictionary(item => item.Key, item => item.Value);
        Assert.True(oldFields.Remove("nextMonthGenerationMode"));

        Assert.Equal(
            ExpirationsNextMonthGenerationMode.Standard,
            mapper.FromFields(oldFields).NextMonthGenerationMode);
        foreach (var mode in Enum.GetValues<ExpirationsNextMonthGenerationMode>())
        {
            var profile = Profile(BrokerOne, nextMonthGenerationMode: mode);
            var mapped = mapper.FromFields(mapper.ToFields(profile));
            Assert.Equal(mode, mapped.NextMonthGenerationMode);
        }
    }

    [Fact]
    public async Task BrokerProfileRepositoryUsesExactModulePath()
    {
        var repository = new ExpirationsBrokerProfileRepository(new InMemoryFirestoreRestClient());

        var created = await repository.CreateAsync(
            Profile(BrokerOne),
            TestContext.Current.CancellationToken);
        var loaded = await repository.GetAsync(BrokerOne, TestContext.Current.CancellationToken);

        Assert.Equal($"modules/vencimientos/brokerProfiles/{BrokerOne:D}", created.DocumentPath);
        Assert.NotNull(loaded);
        Assert.Equal(BrokerOne, loaded.Value.BrokerId);
    }

    [Fact]
    public async Task AssociationsAllowSameNormalizedValueForDifferentBrokersAndIds()
    {
        var client = new InMemoryFirestoreRestClient();
        var repository = new ExpirationsBrokerAssociationRepository(client);
        var first = Association(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), BrokerOne, true);
        var second = Association(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), BrokerTwo, true);

        var storedFirst = await repository.CreateAsync(first, TestContext.Current.CancellationToken);
        var storedSecond = await repository.CreateAsync(second, TestContext.Current.CancellationToken);

        Assert.Equal("AMA", first.NormalizedValue);
        Assert.Equal(first.NormalizedValue, second.NormalizedValue);
        Assert.NotEqual(storedFirst.DocumentPath, storedSecond.DocumentPath);
        Assert.EndsWith(first.Id.ToString("D"), storedFirst.DocumentPath, StringComparison.Ordinal);
        Assert.EndsWith(second.Id.ToString("D"), storedSecond.DocumentPath, StringComparison.Ordinal);
    }

    [Fact]
    public void InactiveAssociationRoundTripsWithoutDeletion()
    {
        var association = Association(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), BrokerOne, false);
        var mapper = new ExpirationsBrokerAssociationMapper();

        var mapped = mapper.FromFields(mapper.ToFields(association));

        Assert.False(mapped.IsActive);
        Assert.Equal("AMA", mapped.NormalizedValue);
        Assert.Equal(ExpirationsAssociationKind.Code, mapped.Kind);
        Assert.Equal(ExpirationsAssociationOrigin.Confirmed, mapped.Origin);
    }

    [Fact]
    public void LegacyAssociationWithoutOriginMapsAsConfirmed()
    {
        var mapper = new ExpirationsBrokerAssociationMapper();
        var fields = mapper.ToFields(Association(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            BrokerOne,
            true)).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        fields.Remove("origin");

        Assert.Equal(ExpirationsAssociationOrigin.Confirmed, mapper.FromFields(fields).Origin);
    }

    [Fact]
    public async Task AllExpirationProcessesUseIndependentSettingsDocuments()
    {
        var client = new InMemoryFirestoreRestClient();
        var repository = new ExpirationsProcessSettingsRepository(client);
        var previous = Settings("Mes anterior");
        var next = Settings("Próximo mes");
        var cancellations = Settings("Cancelaciones");

        await repository.CreateAsync(
            ExpirationsProcess.PreviousMonth,
            previous,
            TestContext.Current.CancellationToken);
        await repository.CreateAsync(
            ExpirationsProcess.NextMonth,
            next,
            TestContext.Current.CancellationToken);
        await repository.CreateAsync(
            ExpirationsProcess.Cancellations,
            cancellations,
            TestContext.Current.CancellationToken);
        var storedPrevious = await repository.GetAsync(
            ExpirationsProcess.PreviousMonth,
            TestContext.Current.CancellationToken);
        var storedNext = await repository.GetAsync(
            ExpirationsProcess.NextMonth,
            TestContext.Current.CancellationToken);
        var storedCancellations = await repository.GetAsync(
            ExpirationsProcess.Cancellations,
            TestContext.Current.CancellationToken);

        Assert.Equal("Mes anterior", storedPrevious!.Value.DefaultSubject);
        Assert.Equal("Próximo mes", storedNext!.Value.DefaultSubject);
        Assert.Equal("Cancelaciones", storedCancellations!.Value.DefaultSubject);
        Assert.EndsWith("/previousMonth", storedPrevious.DocumentPath, StringComparison.Ordinal);
        Assert.EndsWith("/nextMonth", storedNext.DocumentPath, StringComparison.Ordinal);
        Assert.EndsWith("/cancellations", storedCancellations.DocumentPath, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryInterfacesDoNotExposeDeleteAndDirectoryIsReadOnly()
    {
        AssertPublicMethods<IExpirationsBrokerDirectoryRepository>("GetAsync", "ListAsync");
        AssertPublicMethods<IExpirationsBrokerProfileRepository>("GetAsync", "ListAsync", "CreateAsync", "UpdateAsync");
        AssertPublicMethods<IExpirationsBrokerAssociationRepository>("GetAsync", "ListAsync", "CreateAsync", "UpdateAsync", "DeleteAsync");
        AssertPublicMethods<IExpirationsObservedIdentifierRepository>("GetAsync", "ListAsync", "CreateAsync", "UpdateAsync");
        AssertPublicMethods<IExpirationsProcessSettingsRepository>("GetAsync", "CreateAsync", "UpdateAsync");
    }

    private static ExpirationsBrokerCatalogService Catalog(
        IEnumerable<ExpirationsBrokerDirectoryEntry> directory,
        IEnumerable<ExpirationsBrokerProfile> profiles) =>
        new(new FakeDirectoryRepository(directory), new FakeProfileRepository(profiles));

    private static ExpirationsBrokerDirectoryEntry Directory(
        Guid brokerId,
        string name,
        string email) => new()
    {
        BrokerId = brokerId,
        Name = name,
        PrimaryEmailAddresses = [email]
    };

    private static ExpirationsBrokerProfile Profile(
        Guid brokerId,
        bool isActive = true,
        List<ExpirationsAssistant>? assistants = null,
        List<ExpirationsAssistant>? cancellationAssistants = null,
        ExpirationsNextMonthGenerationMode nextMonthGenerationMode =
            ExpirationsNextMonthGenerationMode.Standard) => new()
    {
        BrokerId = brokerId,
        IsActive = isActive,
        NextMonthGenerationMode = nextMonthGenerationMode,
        Assistants = assistants ?? [],
        CancellationAssistants = cancellationAssistants ?? [],
        CreatedAtUtc = Timestamp,
        UpdatedAtUtc = Timestamp
    };

    private static ExpirationsAssistant Assistant() => new()
    {
        Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Name = "Asistente Vencimientos",
        Email = "assistant@example.test",
        IsActive = false
    };

    private static ExpirationsBrokerAssociation Association(Guid id, Guid brokerId, bool isActive) => new()
    {
        Id = id,
        BrokerId = brokerId,
        Kind = ExpirationsAssociationKind.Code,
        Value = "AMA",
        NormalizedValue = "AMA",
        IsActive = isActive,
        CreatedAtUtc = Timestamp,
        UpdatedAtUtc = Timestamp
    };

    private static ExpirationsProcessSettings Settings(string subject) => new()
    {
        DefaultSubject = subject,
        DefaultMessage = $"Mensaje {subject}",
        CommonCcAddresses = [$"{subject.Replace(" ", string.Empty, StringComparison.Ordinal)}@example.test"],
        UpdatedAtUtc = Timestamp
    };

    private static IReadOnlyDictionary<string, FirestoreRestValue> SharedBrokerFields(
        Guid brokerId,
        string name,
        string email) => new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
    {
        ["id"] = FirestoreRestValue.String(brokerId.ToString("D")),
        ["name"] = FirestoreRestValue.String(name),
        ["primaryEmailAddresses"] = FirestoreRestValue.Array([FirestoreRestValue.String(email)]),
        ["assistants"] = FirestoreRestValue.Array([]),
        ["deductions"] = FirestoreRestValue.Array([]),
        ["associatedWorksheetNames"] = FirestoreRestValue.Array([])
    };

    private static FirestoreStoredDocument<T> Stored<T>(T value, string path) =>
        new(value, path, "2026-08-10T12:00:00Z");

    private static void AssertPublicMethods<T>(params string[] expected) =>
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            typeof(T).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal));

    private sealed class FakeDirectoryRepository(IEnumerable<ExpirationsBrokerDirectoryEntry> entries)
        : IExpirationsBrokerDirectoryRepository
    {
        public List<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>> Documents { get; } =
            entries.Select(entry => Stored(entry, $"brokers/{entry.BrokerId:D}")).ToList();

        public Task<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Documents.FirstOrDefault(document => document.Value.BrokerId == brokerId));

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>>(Documents);
    }

    private sealed class FakeProfileRepository(IEnumerable<ExpirationsBrokerProfile> profiles)
        : IExpirationsBrokerProfileRepository
    {
        public List<FirestoreStoredDocument<ExpirationsBrokerProfile>> Documents { get; } =
            profiles.Select(profile => Stored(
                profile,
                $"modules/vencimientos/brokerProfiles/{profile.BrokerId:D}")).ToList();

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Documents.FirstOrDefault(document => document.Value.BrokerId == brokerId));

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>>(Documents);

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> CreateAsync(
            ExpirationsBrokerProfile value,
            CancellationToken cancellationToken = default)
        {
            var stored = Stored(value, $"modules/vencimientos/brokerProfiles/{value.BrokerId:D}");
            Documents.Add(stored);
            return Task.FromResult(stored);
        }

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> UpdateAsync(
            ExpirationsBrokerProfile value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Stored(value, $"modules/vencimientos/brokerProfiles/{value.BrokerId:D}"));
    }

    private sealed class InMemoryFirestoreRestClient : IFirestoreRestClient
    {
        private readonly Dictionary<string, FirestoreRestDocument> _documents = new(StringComparer.Ordinal);
        private int _version;

        public Task<FirestoreRestDocument?> GetDocumentAsync(
            string documentPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_documents.GetValueOrDefault(documentPath));

        public Task<IReadOnlyList<FirestoreRestDocument>> ListDocumentsAsync(
            string parentPath,
            string collectionId,
            int pageSize = 200,
            CancellationToken cancellationToken = default)
        {
            var prefix = string.IsNullOrEmpty(parentPath)
                ? $"{collectionId}/"
                : $"{parentPath}/{collectionId}/";
            var documents = _documents
                .Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                               !item.Key[prefix.Length..].Contains('/'))
                .Select(item => item.Value)
                .ToList();
            return Task.FromResult<IReadOnlyList<FirestoreRestDocument>>(documents);
        }

        public Task<FirestoreRestDocument> CreateDocumentAsync(
            string parentPath,
            string collectionId,
            string documentId,
            IReadOnlyDictionary<string, FirestoreRestValue> fields,
            CancellationToken cancellationToken = default)
        {
            var path = string.IsNullOrEmpty(parentPath)
                ? $"{collectionId}/{documentId}"
                : $"{parentPath}/{collectionId}/{documentId}";
            return Task.FromResult(Store(path, fields));
        }

        public Task<FirestoreRestDocument> UpdateDocumentAsync(
            string documentPath,
            IReadOnlyDictionary<string, FirestoreRestValue> fields,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Store(documentPath, fields));

        public Task DeleteDocumentAsync(
            string documentPath,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            _documents.Remove(documentPath);
            return Task.CompletedTask;
        }

        private FirestoreRestDocument Store(
            string path,
            IReadOnlyDictionary<string, FirestoreRestValue> fields)
        {
            _version++;
            var document = new FirestoreRestDocument
            {
                Name = $"projects/demo/databases/(default)/documents/{path}",
                Fields = new Dictionary<string, FirestoreRestValue>(fields, StringComparer.Ordinal),
                UpdateTime = $"2026-08-10T12:00:{_version:00}Z"
            };
            _documents[path] = document;
            return document;
        }
    }
}
