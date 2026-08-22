using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services;
using ECS.CommissionsMailer.Services.Expirations;
using ECS.CommissionsMailer.Views;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsUatCompletionTests
{
    private static readonly Guid BrokerOne = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BrokerTwo = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BrokerThree = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task StandardPlusManualIsEligibleManualOnlyIsNotAndGeneratedComesFirst()
    {
        using var files = new ExpirationsTestFiles();
        var standard = files.File(BrokerOne, "standard.xlsx", ExpirationsGeneratedFileVariant.Standard);
        var manualOne = files.File(BrokerOne, "manual-one.xlsx", ExpirationsGeneratedFileVariant.Manual);
        var manualOnly = files.File(BrokerTwo, "manual-only.xlsx", ExpirationsGeneratedFileVariant.Manual);
        var service = PreparationService(
            [Directory(BrokerOne, "Uno"), Directory(BrokerTwo, "Dos")],
            []);
        var batch = files.Batch(ExpirationsProcess.PreviousMonth, standard, manualOne, manualOnly);

        var eligibility = await service.PrepareAsync(batch, TestContext.Current.CancellationToken);

        Assert.Equal([BrokerOne], eligibility.EligibleBrokerIds);
        Assert.Contains(eligibility.Errors, error => error.Contains("Dos: Falta el archivo obligatorio Standard"));
        var selected = await service.PrepareSelectedAsync(
            batch,
            [BrokerOne],
            signatureImagePath: null,
            TestContext.Current.CancellationToken);
        Assert.True(selected.CanSend);
        var request = Assert.Single(selected.Requests);
        Assert.Equal([standard.OutputPath, manualOne.OutputPath], request.AttachmentPaths);
        Assert.True(request.RequiresReview);
        Assert.Equal(
            [ExpirationsGeneratedFileVariant.Standard, ExpirationsGeneratedFileVariant.Manual],
            Assert.Single(selected.PreparedItems).Attachments.Select(item => item.Variant));
    }

    [Fact]
    public async Task FelixRequiresBothGeneratedFilesAndManualNeverCompletesThePair()
    {
        using var files = new ExpirationsTestFiles();
        var alphabetical = files.File(BrokerOne, "alphabetical.xlsx", ExpirationsGeneratedFileVariant.FelixAlphabetical);
        var expiration = files.File(BrokerOne, "expiration.xlsx", ExpirationsGeneratedFileVariant.FelixExpirationDate);
        var manual = files.File(BrokerOne, "manual.xlsx", ExpirationsGeneratedFileVariant.Manual);
        var service = PreparationService(
            [Directory(BrokerOne, "Félix")],
            [Profile(BrokerOne, ExpirationsNextMonthGenerationMode.SpecialDualSorted)]);

        var completeBatch = files.Batch(ExpirationsProcess.NextMonth, alphabetical, expiration, manual);
        var complete = await service.PrepareSelectedAsync(
            completeBatch,
            [BrokerOne],
            null,
            TestContext.Current.CancellationToken);
        Assert.True(complete.CanSend);
        Assert.Equal(3, Assert.Single(complete.Requests).AttachmentPaths.Count);
        Assert.Single(complete.Requests);

        var incompleteBatch = new ExpirationsBatchFileAssociationService().Remove(completeBatch, expiration);
        var incomplete = await service.PrepareSelectedAsync(
            incompleteBatch,
            [BrokerOne],
            null,
            TestContext.Current.CancellationToken);
        Assert.False(incomplete.CanSend);
        Assert.Empty(incomplete.EligibleBrokerIds);
        Assert.Contains(incomplete.Errors, error => error.Contains("FelixExpirationDate"));
    }

    [Fact]
    public void SelectionAutoSelectsEligibleSupportsIndividualAllNoneAndPartialState()
    {
        var state = new ExpirationsWindowState(ExpirationsUser());
        state.ApplySnapshot(new ExpirationsAnalysisSessionSnapshot
        {
            Process = ExpirationsProcess.PreviousMonth,
            Catalog =
            [
                Catalog(BrokerOne, "Uno"),
                Catalog(BrokerTwo, "Dos"),
                Catalog(BrokerThree, "Tres")
            ],
            Distribution =
            [
                Preview(BrokerOne, "Uno"),
                Preview(BrokerTwo, "Dos"),
                Preview(BrokerThree, "Tres")
            ]
        });
        state.ApplyGenerationBatch(new ExpirationsGenerationBatch
        {
            Id = Guid.NewGuid(),
            Files =
            [
                new ExpirationsGeneratedFile { BrokerId = BrokerOne },
                new ExpirationsGeneratedFile { BrokerId = BrokerTwo },
                new ExpirationsGeneratedFile { BrokerId = BrokerThree }
            ]
        });
        state.ApplySendPreparation(new ExpirationsSendPreparationResult
        {
            EligibleBrokerIds = new HashSet<Guid> { BrokerOne, BrokerTwo, BrokerThree },
            Requests =
            [
                Request(BrokerOne),
                Request(BrokerTwo),
                Request(BrokerThree)
            ]
        });

        Assert.True(state.AreAllEligibleSelected);
        Assert.Equal(3, state.SelectedBrokerIds.Count);
        var rows = state.BrokerRowsView.Cast<ExpirationsBrokerRow>().ToList();
        rows.Single(row => row.BrokerId == BrokerTwo).IsSelected = false;
        Assert.Null(state.AreAllEligibleSelected);
        Assert.Equal([BrokerOne, BrokerThree], state.SelectedBrokerIds.OrderBy(value => value));
        state.AreAllEligibleSelected = false;
        Assert.False(state.AreAllEligibleSelected);
        Assert.Empty(state.SelectedBrokerIds);
        state.AreAllEligibleSelected = true;
        Assert.True(state.AreAllEligibleSelected);
        Assert.Equal(3, state.SelectedBrokerIds.Count);
    }

    [Theory]
    [InlineData(ExpirationsProcess.PreviousMonth)]
    [InlineData(ExpirationsProcess.NextMonth)]
    public async Task ActiveNonparticipantSupportsExplicitManualOnlySendWithoutEnteringSendAll(
        ExpirationsProcess process)
    {
        using var files = new ExpirationsTestFiles();
        var standard = files.File(BrokerOne, "participante.xlsx", ExpirationsGeneratedFileVariant.Standard);
        var batch = files.BatchWithParticipants(process, [BrokerOne], standard);
        var service = PreparationService(
            [Directory(BrokerOne, "Participante"), Directory(BrokerThree, "Angie prueba")],
            []);
        var state = new ExpirationsWindowState(ExpirationsUser());
        state.ApplySnapshot(new ExpirationsAnalysisSessionSnapshot
        {
            Process = process,
            Catalog = [Catalog(BrokerOne, "Participante"), Catalog(BrokerThree, "Angie prueba")],
            Distribution = [Preview(BrokerOne, "Participante")],
            Analysis = new ExpirationsWorkbookAnalysisResult
            {
                TotalRows = 1,
                ResolvedRows = 1,
                CanGenerate = true
            }
        });
        state.ApplyGenerationBatch(batch);
        var automatic = await service.PrepareAsync(batch, TestContext.Current.CancellationToken);
        state.ApplySendPreparation(automatic);

        Assert.Equal([BrokerOne], automatic.EligibleBrokerIds);
        Assert.Equal([BrokerOne], automatic.Requests.Select(request => request.BrokerId));
        var rows = state.BrokerRowsView.Cast<ExpirationsBrokerRow>().ToList();
        Assert.True(rows.Single(row => row.BrokerId == BrokerOne).IsSelected);
        var angie = rows.Single(row => row.BrokerId == BrokerThree);
        Assert.False(angie.IsSelected);
        Assert.True(angie.CanSelectForSend);
        Assert.True(angie.CanViewFiles);
        Assert.Equal("No participa en el Excel", angie.StatusText);
        Assert.Contains("Agregue al menos un archivo manual", angie.SelectionHint, StringComparison.Ordinal);

        angie.IsSelected = true;
        Assert.Contains(BrokerThree, state.SelectedBrokerIds);
        state.ApplySendPreparation(automatic);
        Assert.Contains(BrokerThree, state.SelectedBrokerIds);

        var withoutManual = await service.PrepareSelectedAsync(
            batch,
            [BrokerThree],
            null,
            TestContext.Current.CancellationToken);
        Assert.False(withoutManual.CanSend);
        Assert.Contains(
            "Angie prueba no participa en el reporte actual y no tiene archivos manuales asociados.",
            withoutManual.Errors);

        var association = new ExpirationsBatchFileAssociationService();
        var added = association.AddManual(
            batch,
            BrokerThree,
            "Angie prueba",
            files.CreateWorkbook("prueba.xlsx"));
        Assert.True(added.Succeeded, added.ErrorMessage);
        Assert.Equal([BrokerOne], added.Batch.ParticipatingBrokerIds);
        Assert.Equal(ExpirationsGeneratedFileVariant.Manual, added.File!.Variant);
        Assert.True(added.File.RequiresReview);
        state.ReplaceGenerationBatch(added.Batch);
        var afterManualAutomatic = await service.PrepareAsync(
            added.Batch,
            TestContext.Current.CancellationToken);
        state.ApplySendPreparation(afterManualAutomatic);
        Assert.Contains(BrokerThree, state.SelectedBrokerIds);
        Assert.DoesNotContain(BrokerThree, afterManualAutomatic.EligibleBrokerIds);
        Assert.DoesNotContain(afterManualAutomatic.Requests, request => request.BrokerId == BrokerThree);

        var selectedManual = await service.PrepareSelectedAsync(
            added.Batch,
            [BrokerThree],
            null,
            TestContext.Current.CancellationToken);
        Assert.True(selectedManual.CanSend, string.Join(Environment.NewLine, selectedManual.Errors));
        var manualRequest = Assert.Single(selectedManual.Requests);
        Assert.Equal(BrokerThree, manualRequest.BrokerId);
        Assert.True(manualRequest.RequiresReview);
        Assert.Single(manualRequest.AttachmentPaths);
    }

    [Fact]
    public async Task ManualOnlyNonparticipantStillHonorsMaximumAttachmentCount()
    {
        using var files = new ExpirationsTestFiles();
        var manuals = Enumerable.Range(1, ExpirationsSendPreparationService.MaximumAttachmentsPerBroker + 1)
            .Select(index => files.File(
                BrokerThree,
                $"manual-{index}.xlsx",
                ExpirationsGeneratedFileVariant.Manual))
            .ToArray();
        var batch = files.BatchWithParticipants(
            ExpirationsProcess.PreviousMonth,
            [BrokerOne],
            manuals);
        var result = await PreparationService(
                [Directory(BrokerThree, "Angie prueba")],
                [])
            .PrepareSelectedAsync(
                batch,
                [BrokerThree],
                null,
                TestContext.Current.CancellationToken);

        Assert.False(result.CanSend);
        Assert.Contains(result.Errors, error => error.Contains(
            $"máximo de {ExpirationsSendPreparationService.MaximumAttachmentsPerBroker}",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task InactiveNonparticipantCannotBeSelectedOrPrepared()
    {
        using var files = new ExpirationsTestFiles();
        var manual = files.File(BrokerThree, "manual.xlsx", ExpirationsGeneratedFileVariant.Manual);
        var batch = files.BatchWithParticipants(
            ExpirationsProcess.PreviousMonth,
            [BrokerOne],
            manual);
        var inactiveProfile = Profile(BrokerThree);
        inactiveProfile.IsActive = false;
        var service = PreparationService(
            [Directory(BrokerThree, "Angie prueba")],
            [inactiveProfile]);
        var preparation = await service.PrepareSelectedAsync(
            batch,
            [BrokerThree],
            null,
            TestContext.Current.CancellationToken);
        Assert.False(preparation.CanSend);
        Assert.Contains(preparation.Errors, error => error.Contains(
            "está inactivo",
            StringComparison.CurrentCultureIgnoreCase));

        var state = new ExpirationsWindowState(ExpirationsUser());
        state.ApplySnapshot(new ExpirationsAnalysisSessionSnapshot
        {
            Process = ExpirationsProcess.PreviousMonth,
            Catalog =
            [
                new ExpirationsBrokerCatalogItem
                {
                    BrokerId = BrokerThree,
                    Name = "Angie prueba",
                    IsActive = false
                }
            ]
        });
        state.ApplyGenerationBatch(batch);
        Assert.Empty(state.BrokerRowsView.Cast<ExpirationsBrokerRow>());
    }

    [Fact]
    public async Task SelectedPreparationUsesMarkedBrokerIdsWhileAllUsesEveryEligibleBroker()
    {
        using var files = new ExpirationsTestFiles();
        var batch = files.Batch(
            ExpirationsProcess.PreviousMonth,
            files.File(BrokerOne, "one.xlsx", ExpirationsGeneratedFileVariant.Standard),
            files.File(BrokerTwo, "two.xlsx", ExpirationsGeneratedFileVariant.Standard),
            files.File(BrokerThree, "three.xlsx", ExpirationsGeneratedFileVariant.Standard));
        var service = PreparationService(
            [Directory(BrokerOne, "Uno"), Directory(BrokerTwo, "Dos"), Directory(BrokerThree, "Tres")],
            []);

        var selected = await service.PrepareSelectedAsync(
            batch,
            [BrokerOne, BrokerThree],
            null,
            TestContext.Current.CancellationToken);
        var all = await service.PrepareSelectedAsync(
            batch,
            [BrokerOne, BrokerTwo, BrokerThree],
            null,
            TestContext.Current.CancellationToken);

        Assert.True(selected.CanSend);
        Assert.Equal([BrokerOne, BrokerThree], selected.Requests.Select(request => request.BrokerId).OrderBy(value => value));
        Assert.True(all.CanSend);
        Assert.Equal(3, all.Requests.Count);
    }

    [Fact]
    public async Task ManualIsCopiedIntoBatchRemovalKeepsPhysicalFileAndAuthorizedEditUpdatesSha()
    {
        using var files = new ExpirationsTestFiles();
        var sourceManual = files.CreateWorkbook("external-manual.xlsx");
        var standard = files.File(BrokerOne, "standard.xlsx", ExpirationsGeneratedFileVariant.Standard);
        var batch = files.BatchWithParticipants(ExpirationsProcess.PreviousMonth, [BrokerOne], standard);
        var service = new ExpirationsBatchFileAssociationService();

        var added = service.AddManual(batch, BrokerOne, "Uno", sourceManual);

        Assert.True(added.Succeeded, added.ErrorMessage);
        var manual = Assert.IsType<ExpirationsGeneratedFile>(added.File);
        Assert.Equal(ExpirationsGeneratedFileVariant.Manual, manual.Variant);
        Assert.Equal(0, manual.RowCount);
        Assert.Empty(manual.SourceRowNumbers);
        Assert.True(manual.RequiresReview);
        Assert.StartsWith(Path.Combine(files.Directory, "Manual"), manual.OutputPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(manual.OutputPath));
        var withoutStandard = service.Remove(added.Batch, standard);
        Assert.Equal([BrokerOne], withoutStandard.ParticipatingBrokerIds);
        Assert.DoesNotContain(withoutStandard.Files, file => file.Variant == ExpirationsGeneratedFileVariant.Standard);
        Assert.True(File.Exists(standard.OutputPath));
        var eligibility = await PreparationService([Directory(BrokerOne, "Uno")], [])
            .PrepareSelectedAsync(
                withoutStandard,
                [BrokerOne],
                null,
                TestContext.Current.CancellationToken);
        Assert.False(eligibility.CanSend);
        Assert.Empty(eligibility.EligibleBrokerIds);

        var newSha = new string('b', 64);
        var edited = service.UpdateAuthorizedReplacement(added.Batch, standard, newSha);
        var updated = edited.Files.Single(file => file.Variant == ExpirationsGeneratedFileVariant.Standard);
        Assert.Equal(newSha, updated.Sha256);
        Assert.True(updated.IsManuallyEdited);
        Assert.True(updated.RequiresReview);
    }

    [Fact]
    public void ManualRoundTripsInHistoryWithoutPathAndRetryResolvesByMetadata()
    {
        using var files = new ExpirationsTestFiles();
        var standard = files.File(BrokerOne, "standard.xlsx", ExpirationsGeneratedFileVariant.Standard);
        var manual = files.File(BrokerOne, "manual.xlsx", ExpirationsGeneratedFileVariant.Manual);
        var item = new ExpirationsSendHistoryItem
        {
            ItemId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            BrokerId = BrokerOne,
            BrokerName = "Uno",
            ToRecipients = ["uno@example.test"],
            Attachments =
            [
                Attachment(standard),
                Attachment(manual)
            ],
            Status = ExpirationsSendItemStatus.Failed,
            ErrorMessage = "Falló Outlook"
        };
        var mapper = new ExpirationsSendHistoryItemMapper();
        var fields = mapper.ToFields(item);
        var roundTrip = mapper.FromFields(fields);

        Assert.Equal(ExpirationsGeneratedFileVariant.Manual, roundTrip.Attachments[1].Variant);
        Assert.DoesNotContain(fields.Keys, key => key.Contains("path", StringComparison.OrdinalIgnoreCase));
        var retry = new ExpirationsRetryPreparationService().Prepare(
            new ExpirationsSendOperation
            {
                OperationId = Guid.NewGuid(),
                Process = ExpirationsProcess.PreviousMonth,
                Status = ExpirationsSendOperationStatus.Completed,
                Subject = "Asunto",
                Body = "Mensaje"
            },
            [item],
            files.Batch(ExpirationsProcess.PreviousMonth, standard, manual));
        Assert.True(retry.CanRetry, string.Join(Environment.NewLine, retry.Errors));
        Assert.Equal([standard.OutputPath, manual.OutputPath], Assert.Single(retry.Preparation!.Requests).AttachmentPaths);
        Assert.Equal(ExpirationsGeneratedFileVariant.Manual,
            Assert.Single(retry.Preparation.PreparedItems).Attachments[1].Variant);
    }

    [Fact]
    public async Task SharedMasterChangesRefreshIdentityAddDefaultsAndIgnoreOrphans()
    {
        var directory = new MutableDirectoryRepository
        {
            Values = [Directory(BrokerOne, "Nombre anterior", "old@example.test")]
        };
        var profiles = new FakeProfileRepository(
            [Profile(BrokerOne), Profile(BrokerTwo)]);
        var catalog = new ExpirationsBrokerCatalogService(directory, profiles);
        var initial = await catalog.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Nombre anterior", Assert.Single(initial.Items).Name);

        directory.Values =
        [
            Directory(BrokerOne, "Nombre actualizado", "new@example.test"),
            Directory(BrokerThree, "Nuevo corredor", "third@example.test")
        ];
        var refreshed = await catalog.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("new@example.test", refreshed.Items.Single(item => item.BrokerId == BrokerOne).PrimaryEmailAddresses[0]);
        Assert.True(refreshed.Items.Single(item => item.BrokerId == BrokerThree).IsActive);
        Assert.DoesNotContain(refreshed.Items, item => item.BrokerId == BrokerTwo);
        Assert.Contains(refreshed.Warnings, warning => warning.Contains(BrokerTwo.ToString("D")));
    }

    internal static void CreateValidWorkbook(string path, string value = "dato")
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData(
            new Row(new Cell { DataType = CellValues.String, CellValue = new CellValue(value) })));
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Datos"
        });
        workbookPart.Workbook.Save();
    }

    private static ExpirationsSendPreparationService PreparationService(
        IReadOnlyList<ExpirationsBrokerDirectoryEntry> directory,
        IReadOnlyList<ExpirationsBrokerProfile> profiles) => new(
            new FakeSettingsRepository(),
            new ExpirationsBrokerCatalogService(
                new FakeDirectoryRepository(directory),
                new FakeProfileRepository(profiles)));

    private static ExpirationsBrokerDirectoryEntry Directory(
        Guid id,
        string name,
        string? email = null) => new()
    {
        BrokerId = id,
        Name = name,
        PrimaryEmailAddresses = [email ?? $"{name.ToLowerInvariant().Replace(' ', '.')}@example.test"]
    };

    private static ExpirationsBrokerProfile Profile(
        Guid id,
        ExpirationsNextMonthGenerationMode mode = ExpirationsNextMonthGenerationMode.Standard) => new()
    {
        BrokerId = id,
        IsActive = true,
        NextMonthGenerationMode = mode,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    private static ExpirationsBrokerCatalogItem Catalog(Guid id, string name) => new()
    {
        BrokerId = id,
        Name = name,
        PrimaryEmailAddresses = [$"{name.ToLowerInvariant().Replace(' ', '.')}@example.test"],
        IsActive = true
    };

    private static ExpirationsDistributionPreviewItem Preview(Guid id, string name) => new()
    {
        BrokerId = id,
        BrokerName = name,
        RowCount = 1
    };

    private static AppUser ExpirationsUser() => new()
    {
        Uid = "uat",
        DisplayName = "UAT",
        Email = "uat@example.test",
        Role = AppUserRole.Operator,
        IsActive = true,
        CanUseExpirations = true
    };

    private static ECS.CommissionsMailer.Models.EmailSendRequest Request(Guid brokerId) => new()
    {
        BrokerId = brokerId,
        BrokerName = brokerId.ToString("D"),
        ToRecipients = ["to@example.test"],
        Subject = "Asunto",
        Body = "Mensaje",
        AttachmentPaths = ["file.xlsx"]
    };

    private static ExpirationsSendAttachment Attachment(ExpirationsGeneratedFile file) => new(
        Path.GetFileName(file.OutputPath),
        file.Sha256,
        file.Variant);

    private sealed class ExpirationsTestFiles : IDisposable
    {
        private readonly GeneratedFileHashService _hash = new();

        public ExpirationsTestFiles()
        {
            Directory = Path.Combine(Path.GetTempPath(), $"ecs-exp-uat-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
        }

        public string Directory { get; }

        public string CreateWorkbook(string fileName)
        {
            var path = Path.Combine(Directory, fileName);
            CreateValidWorkbook(path, fileName);
            return path;
        }

        public ExpirationsGeneratedFile File(
            Guid brokerId,
            string fileName,
            ExpirationsGeneratedFileVariant variant)
        {
            var path = CreateWorkbook(fileName);
            return new ExpirationsGeneratedFile
            {
                BrokerId = brokerId,
                BrokerName = brokerId.ToString("D"),
                OutputPath = path,
                Variant = variant,
                Sha256 = _hash.ComputeSha256(path),
                RequiresReview = variant == ExpirationsGeneratedFileVariant.Manual
            };
        }

        public ExpirationsGenerationBatch Batch(
            ExpirationsProcess process,
            params ExpirationsGeneratedFile[] generatedFiles) => new()
        {
            Id = Guid.NewGuid(),
            Process = process,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OutputDirectory = Directory,
            Files = generatedFiles
        };

        public ExpirationsGenerationBatch BatchWithParticipants(
            ExpirationsProcess process,
            IEnumerable<Guid> participatingBrokerIds,
            params ExpirationsGeneratedFile[] generatedFiles) => new()
        {
            Id = Guid.NewGuid(),
            Process = process,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OutputDirectory = Directory,
            ParticipatingBrokerIds = participatingBrokerIds.ToHashSet(),
            Files = generatedFiles
        };

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class FakeSettingsRepository : IExpirationsProcessSettingsRepository
    {
        public Task<FirestoreStoredDocument<ExpirationsProcessSettings>?> GetAsync(
            ExpirationsProcess process,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FirestoreStoredDocument<ExpirationsProcessSettings>?>(new(
                new ExpirationsProcessSettings
                {
                    DefaultSubject = "Asunto",
                    DefaultMessage = "Mensaje",
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                },
                $"modules/vencimientos/settings/{process}",
                "version"));

        public Task<FirestoreStoredDocument<ExpirationsProcessSettings>> CreateAsync(
            ExpirationsProcess process,
            ExpirationsProcessSettings value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FirestoreStoredDocument<ExpirationsProcessSettings>> UpdateAsync(
            ExpirationsProcess process,
            ExpirationsProcessSettings value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private class FakeDirectoryRepository(IEnumerable<ExpirationsBrokerDirectoryEntry> values)
        : IExpirationsBrokerDirectoryRepository
    {
        protected IReadOnlyList<ExpirationsBrokerDirectoryEntry> CurrentValues { get; set; } = values.ToList();

        public Task<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentValues.Where(value => value.BrokerId == brokerId).Select(Store).SingleOrDefault());

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>>(
                CurrentValues.Select(Store).ToList());

        private static FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry> Store(
            ExpirationsBrokerDirectoryEntry value) => new(value, $"brokers/{value.BrokerId:D}", "version");
    }

    private sealed class MutableDirectoryRepository() : FakeDirectoryRepository([])
    {
        public IReadOnlyList<ExpirationsBrokerDirectoryEntry> Values
        {
            get => CurrentValues;
            set => CurrentValues = value;
        }
    }

    private sealed class FakeProfileRepository(IEnumerable<ExpirationsBrokerProfile> values)
        : IExpirationsBrokerProfileRepository
    {
        private readonly IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>> _values = values
            .Select(value => new FirestoreStoredDocument<ExpirationsBrokerProfile>(
                value, $"modules/vencimientos/brokerProfiles/{value.BrokerId:D}", "version"))
            .ToList();

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.SingleOrDefault(value => value.Value.BrokerId == brokerId));

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>> ListAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(_values);

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> CreateAsync(
            ExpirationsBrokerProfile value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> UpdateAsync(
            ExpirationsBrokerProfile value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
