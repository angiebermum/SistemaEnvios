using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services;
using ECS.CommissionsMailer.Services.Expirations;
using ECS.CommissionsMailer.Views;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsEmailSendingTests
{
    private static readonly Guid BrokerOne = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void PrimaryAndOnlyActiveExpirationsAssistantsBecomeToRecipients()
    {
        var broker = Broker(
            primary: ["PRIMARY@example.test", "primary@example.test"],
            assistants: [Assistant("Activa", "assistant@example.test", true)]);

        var result = new ExpirationsRecipientService().Resolve(broker, []);

        Assert.True(result.IsValid);
        Assert.Equal(["PRIMARY@example.test", "assistant@example.test"], result.ToRecipients);
        Assert.Equal(["assistant@example.test"], result.AssistantRecipients);
    }

    [Fact]
    public void InactiveExpirationsAssistantIsExcluded()
    {
        var result = new ExpirationsRecipientService().Resolve(
            Broker(
                primary: ["primary@example.test"],
                assistants: [Assistant("Inactiva", "inactive@example.test", false)]),
            []);

        Assert.DoesNotContain("inactive@example.test", result.ToRecipients);
        Assert.Empty(result.AssistantRecipients);
    }

    [Fact]
    public void ActiveAssistantAloneIsAValidToRecipient()
    {
        var result = new ExpirationsRecipientService().Resolve(
            Broker(
                primary: [],
                assistants: [Assistant("Activa", "assistant@example.test", true)]),
            []);

        Assert.True(result.IsValid);
        Assert.Equal(["assistant@example.test"], result.ToRecipients);
    }

    [Fact]
    public void CommissionAssistantsAreNeverPartOfExpirationsResolution()
    {
        var commissionsBroker = new Broker
        {
            Id = BrokerOne,
            Name = "Compartido",
            PrimaryEmailAddresses = ["primary@example.test"],
            Assistants = [new BrokerAssistant { Name = "Comisiones", Email = "commission@example.test", IsActive = true }]
        };
        var expirationsBroker = Broker(primary: commissionsBroker.PrimaryEmailAddresses, assistants: []);

        var result = new ExpirationsRecipientService().Resolve(expirationsBroker, []);

        Assert.Equal(["primary@example.test"], result.ToRecipients);
        Assert.DoesNotContain("commission@example.test", result.ToRecipients);
    }

    [Fact]
    public void CcDuplicatedAgainstToIsRemovedCaseInsensitively()
    {
        var result = new ExpirationsRecipientService().Resolve(
            Broker(
                primary: ["primary@example.test"],
                assistants: [Assistant("Activa", "assistant@example.test", true)]),
            ["PRIMARY@example.test", "cc@example.test", "CC@example.test"]);

        Assert.Equal(["cc@example.test"], result.CcRecipients);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task BatchWithoutAnyValidToIsBlocked()
    {
        using var files = GeneratedFiles.Create(ExpirationsGeneratedFileVariant.Standard);
        var service = Preparation(
            files.Batch,
            Broker(primary: [], assistants: [Assistant("Inactiva", "inactive@example.test", false)]));

        var result = await service.PrepareAsync(files.Batch, TestContext.Current.CancellationToken);

        Assert.False(result.CanSend);
        Assert.Contains(result.Errors, error => error.Contains("ningún destinatario Para válido", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SpecialTwoFilesProduceOneRequestWithTwoAttachments()
    {
        using var files = GeneratedFiles.Create(
            ExpirationsGeneratedFileVariant.FelixAlphabetical,
            ExpirationsGeneratedFileVariant.FelixExpirationDate);
        var service = Preparation(files.Batch, Broker());

        var result = await service.PrepareAsync(files.Batch, TestContext.Current.CancellationToken);

        var request = Assert.Single(result.Requests);
        var prepared = Assert.Single(result.PreparedItems);
        Assert.True(result.CanSend);
        Assert.Equal(2, request.AttachmentPaths.Count);
        Assert.Equal(
            [ExpirationsGeneratedFileVariant.FelixAlphabetical, ExpirationsGeneratedFileVariant.FelixExpirationDate],
            prepared.Attachments.Select(attachment => attachment.Variant));
    }

    [Fact]
    public async Task StandardFileProducesOneRequestWithOneAttachment()
    {
        using var files = GeneratedFiles.Create(ExpirationsGeneratedFileVariant.Standard);
        var service = Preparation(files.Batch, Broker());

        var result = await service.PrepareAsync(files.Batch, TestContext.Current.CancellationToken);

        Assert.True(result.CanSend);
        Assert.Single(result.Requests);
        Assert.Single(result.Requests[0].AttachmentPaths);
    }

    [Fact]
    public async Task ModifiedAttachmentBlocksWholePreflight()
    {
        using var files = GeneratedFiles.Create(ExpirationsGeneratedFileVariant.Standard);
        File.AppendAllText(files.Batch.Files[0].OutputPath, "changed");
        var service = Preparation(files.Batch, Broker());

        var result = await service.PrepareAsync(files.Batch, TestContext.Current.CancellationToken);

        Assert.False(result.CanSend);
        Assert.Contains(ExpirationsSendPreparationService.ChangedAttachmentMessage, result.Errors);
    }

    [Fact]
    public async Task SettingsCreateUpdateAndConcurrencyReloadAreOptimistic()
    {
        var repository = new FakeSettingsRepository();
        var service = new ExpirationsEmailSettingsService(repository);
        var empty = await service.LoadAsync(ExpirationsProcess.PreviousMonth, TestContext.Current.CancellationToken);

        var created = await service.SaveAsync(
            empty,
            "  Asunto A  ",
            " Mensaje A ",
            "CC@example.test; cc@example.test",
            TestContext.Current.CancellationToken);
        var updated = await service.SaveAsync(
            created.Snapshot,
            "Asunto B",
            "Mensaje B",
            string.Empty,
            TestContext.Current.CancellationToken);
        repository.ConcurrentReplacement = Settings("Remoto", "Mensaje remoto", "remote@example.test");
        var conflict = await service.SaveAsync(
            updated.Snapshot,
            "Local",
            "Mensaje local",
            string.Empty,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsEmailSettingsSaveOutcome.Created, created.Outcome);
        Assert.Equal(["CC@example.test"], created.Snapshot.Settings.CommonCcAddresses);
        Assert.Equal(ExpirationsEmailSettingsSaveOutcome.Updated, updated.Outcome);
        Assert.Equal(ExpirationsEmailSettingsSaveOutcome.ConcurrencyConflict, conflict.Outcome);
        Assert.Equal("Remoto", conflict.Snapshot.Settings.DefaultSubject);
        Assert.Equal(1, repository.CreateCount);
        Assert.Equal(2, repository.UpdateCount);
    }

    [Fact]
    public async Task PreviousAndNextSettingsRemainIndependentWhenOneChanges()
    {
        var repository = new FakeSettingsRepository();
        var service = new ExpirationsEmailSettingsService(repository);
        var cancellationToken = TestContext.Current.CancellationToken;
        var previous = await service.SaveAsync(
            await service.LoadAsync(ExpirationsProcess.PreviousMonth, cancellationToken),
            "Anterior", "Mensaje anterior", "anterior@example.test", cancellationToken);
        _ = await service.SaveAsync(
            await service.LoadAsync(ExpirationsProcess.NextMonth, cancellationToken),
            "Siguiente", "Mensaje siguiente", "siguiente@example.test", cancellationToken);
        _ = await service.SaveAsync(
            previous.Snapshot,
            "Anterior actualizado",
            "Mensaje A2",
            string.Empty,
            cancellationToken);

        var loadedPrevious = await service.LoadAsync(ExpirationsProcess.PreviousMonth, cancellationToken);
        var loadedNext = await service.LoadAsync(ExpirationsProcess.NextMonth, cancellationToken);

        Assert.Equal("Anterior actualizado", loadedPrevious.Settings.DefaultSubject);
        Assert.Equal("Siguiente", loadedNext.Settings.DefaultSubject);
        Assert.Equal(["siguiente@example.test"], loadedNext.Settings.CommonCcAddresses);
    }

    [Fact]
    public async Task InvalidSettingsAreNotPersisted()
    {
        var repository = new FakeSettingsRepository();
        var service = new ExpirationsEmailSettingsService(repository);
        var cancellationToken = TestContext.Current.CancellationToken;

        var result = await service.SaveAsync(
            await service.LoadAsync(ExpirationsProcess.PreviousMonth, cancellationToken),
            " ",
            string.Empty,
            "invalid-address",
            cancellationToken);

        Assert.Equal(ExpirationsEmailSettingsSaveOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(0, repository.CreateCount);
        Assert.Contains(result.Errors, error => error.Contains("asunto", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("mensaje", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("no es válida", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CancelledConfirmationDoesNotCallSender()
    {
        var request = Request(BrokerOne, "Uno");
        var sender = new FakeOutlookSender([]);
        var service = new ExpirationsSendExecutionService(sender, new FakeSendHistoryRepository());

        var result = await service.SendAsync(
            ValidPreparation(request),
            "sender@example.test",
            (_, _) => false);

        Assert.True(result.WasCancelled);
        Assert.Equal(0, sender.SendCalls);
    }

    [Fact]
    public async Task MixedSenderResultIsKeptWithoutRetry()
    {
        var first = Request(BrokerOne, "Uno");
        var second = Request(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Dos");
        var sender = new FakeOutlookSender(
        [
            new EmailSendResult { RequestId = first.RequestId, WasSuccessful = true },
            new EmailSendResult { RequestId = second.RequestId, WasSuccessful = false, ErrorMessage = "Falló Outlook" }
        ]);
        var service = new ExpirationsSendExecutionService(sender, new FakeSendHistoryRepository());

        var result = await service.SendAsync(
            ValidPreparation(first, second),
            "sender@example.test",
            (_, _) => true);

        Assert.Equal(1, result.SuccessfulCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal("Falló Outlook", result.Items.Single(item => !item.WasSuccessful).ErrorMessage);
        Assert.Equal(1, sender.SendCalls);
    }

    [Fact]
    public void ReviewRequiresAvailableAccountAndDisablesSelectionWhileSending()
    {
        var state = new ExpirationsSendReviewState(ValidPreparation(Request(BrokerOne, "Uno")));
        state.ApplyOutlook(new OutlookConnectionInfo(
            true,
            "Seleccione una cuenta.",
            null,
            ["one@example.test", "two@example.test"]));

        Assert.False(state.CanContinue);
        state.SelectedAccount = "two@example.test";
        Assert.True(state.CanContinue);
        state.SetBusy(true, "Enviando correo 1 de 1...");
        Assert.False(state.CanContinue);
        Assert.False(state.CanSelectAccount);
        Assert.False(state.CanCancel);
    }

    [Fact]
    public void ReviewShowsUnconfirmedResultAsUnknownInsteadOfFailed()
    {
        var request = Request(BrokerOne, "Uno");
        var item = new ExpirationsSendReviewItem(request);

        item.ApplyResult(new ExpirationsSendResultItem(
            request.RequestId,
            request.BrokerId,
            request.BrokerName,
            WasSuccessful: false,
            "Outlook no devolvió un resultado confirmado.",
            IsConfirmed: false));

        Assert.StartsWith("? ", item.ResultText, StringComparison.Ordinal);
        Assert.Contains("resultado no confirmado", item.ResultText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("✗", item.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsChangeInvalidatesOnlyPreviewWhileProcessChangeClearsSendResult()
    {
        var state = new ExpirationsWindowState(new AppUser
        {
            Uid = "user",
            Email = "user@example.test",
            DisplayName = "Usuario",
            Role = AppUserRole.Operator,
            IsActive = true,
            CanUseExpirations = true
        });
        state.ApplySnapshot(new ExpirationsAnalysisSessionSnapshot
        {
            Process = ExpirationsProcess.PreviousMonth
        });
        state.ApplyGenerationBatch(new ExpirationsGenerationBatch
        {
            Process = ExpirationsProcess.PreviousMonth,
            Files = [new ExpirationsGeneratedFile()]
        });
        state.ApplySendPreparation(ValidPreparation(Request(BrokerOne, "Uno")));
        state.ApplySendResult(new ExpirationsSendExecutionResult
        {
            Items = [new ExpirationsSendResultItem(Guid.NewGuid(), BrokerOne, "Uno", true, string.Empty)]
        });

        state.InvalidateSendPreview();

        Assert.NotNull(state.GenerationBatch);
        Assert.Equal(System.Windows.Visibility.Visible, state.SendResultVisibility);
        Assert.False(state.CanReviewAndSend);

        state.SelectedProcessOption = state.ProcessOptions.Single(option =>
            option.Value == ExpirationsProcess.NextMonth);

        Assert.Null(state.GenerationBatch);
        Assert.Equal(System.Windows.Visibility.Collapsed, state.SendResultVisibility);
    }

    private static ExpirationsSendPreparationService Preparation(
        ExpirationsGenerationBatch batch,
        ExpirationsBrokerCatalogItem broker)
    {
        var settings = new FakeSettingsRepository();
        settings.Seed(batch.Process, Settings("Asunto", "Mensaje", "cc@example.test"));
        return new ExpirationsSendPreparationService(
            settings,
            new ExpirationsBrokerCatalogService(
                new FakeDirectoryRepository(
                [
                    new ExpirationsBrokerDirectoryEntry
                    {
                        BrokerId = broker.BrokerId,
                        Name = broker.Name,
                        PrimaryEmailAddresses = broker.PrimaryEmailAddresses.ToList()
                    }
                ]),
                new FakeProfileRepository(
                [
                    new ExpirationsBrokerProfile
                    {
                        BrokerId = broker.BrokerId,
                        IsActive = broker.IsActive,
                        Assistants = broker.Assistants.ToList(),
                        NextMonthGenerationMode = broker.NextMonthGenerationMode
                    }
                ])));
    }

    private static ExpirationsBrokerCatalogItem Broker(
        IEnumerable<string>? primary = null,
        IEnumerable<ExpirationsAssistant>? assistants = null) => new()
    {
        BrokerId = BrokerOne,
        Name = "Corredor Uno",
        PrimaryEmailAddresses = (primary ?? ["primary@example.test"]).ToList(),
        Assistants = (assistants ?? []).ToList(),
        IsActive = true
    };

    private static ExpirationsAssistant Assistant(string name, string email, bool active) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Email = email,
        IsActive = active
    };

    private static ExpirationsProcessSettings Settings(string subject, string message, string cc) => new()
    {
        DefaultSubject = subject,
        DefaultMessage = message,
        CommonCcAddresses = string.IsNullOrWhiteSpace(cc) ? [] : [cc],
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    private static EmailSendRequest Request(Guid brokerId, string name) => new()
    {
        BrokerId = brokerId,
        BrokerName = name,
        ToRecipients = ["to@example.test"],
        Subject = "Asunto",
        Body = "Mensaje",
        AttachmentPaths = ["file.xlsx"],
        ReviewConfirmed = true
    };

    private static ExpirationsSendPreparationResult ValidPreparation(params EmailSendRequest[] requests) => new()
    {
        Process = ExpirationsProcess.PreviousMonth,
        Settings = Settings("Asunto", "Mensaje", string.Empty),
        Requests = requests,
        PreparedItems = requests.Select(request => new ExpirationsPreparedSendItem
        {
            RequestId = request.RequestId,
            Attachments = request.AttachmentPaths.Select(path => new ExpirationsPreparedAttachment(
                path,
                Path.GetFileName(path),
                new string('a', 64),
                ExpirationsGeneratedFileVariant.Standard)).ToList()
        }).ToList()
    };

    private sealed class GeneratedFiles : IDisposable
    {
        private GeneratedFiles(string directory, ExpirationsGenerationBatch batch)
        {
            Directory = directory;
            Batch = batch;
        }

        public string Directory { get; }
        public ExpirationsGenerationBatch Batch { get; }

        public static GeneratedFiles Create(params ExpirationsGeneratedFileVariant[] variants)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"ecs-exp-send-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var hashService = new GeneratedFileHashService();
            var files = variants.Select((variant, index) =>
            {
                var path = Path.Combine(directory, $"generated-{index + 1}.xlsx");
                File.WriteAllText(path, $"file-{index + 1}");
                return new ExpirationsGeneratedFile
                {
                    BrokerId = BrokerOne,
                    BrokerName = "Corredor Uno",
                    OutputPath = path,
                    Variant = variant,
                    Sha256 = hashService.ComputeSha256(path)
                };
            }).ToList();
            return new GeneratedFiles(directory, new ExpirationsGenerationBatch
            {
                Id = Guid.NewGuid(),
                Process = variants.Any(variant => variant != ExpirationsGeneratedFileVariant.Standard)
                    ? ExpirationsProcess.NextMonth
                    : ExpirationsProcess.PreviousMonth,
                OutputDirectory = directory,
                Files = files
            });
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class FakeSettingsRepository : IExpirationsProcessSettingsRepository
    {
        private readonly Dictionary<ExpirationsProcess, FirestoreStoredDocument<ExpirationsProcessSettings>> _values = [];
        private int _version;

        public int CreateCount { get; private set; }
        public int UpdateCount { get; private set; }
        public ExpirationsProcessSettings? ConcurrentReplacement { get; set; }

        public void Seed(ExpirationsProcess process, ExpirationsProcessSettings settings) =>
            _values[process] = Stored(process, settings);

        public Task<FirestoreStoredDocument<ExpirationsProcessSettings>?> GetAsync(
            ExpirationsProcess process,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.GetValueOrDefault(process));

        public Task<FirestoreStoredDocument<ExpirationsProcessSettings>> CreateAsync(
            ExpirationsProcess process,
            ExpirationsProcessSettings value,
            CancellationToken cancellationToken = default)
        {
            CreateCount++;
            var stored = Stored(process, value);
            _values.Add(process, stored);
            return Task.FromResult(stored);
        }

        public Task<FirestoreStoredDocument<ExpirationsProcessSettings>> UpdateAsync(
            ExpirationsProcess process,
            ExpirationsProcessSettings value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            if (ConcurrentReplacement is { } replacement)
            {
                _values[process] = Stored(process, replacement);
                ConcurrentReplacement = null;
                throw new FirestoreConcurrencyException($"settings/{process}", expectedUpdateTime);
            }
            var current = _values[process];
            if (!current.UpdateTime.Equals(expectedUpdateTime, StringComparison.Ordinal))
                throw new FirestoreConcurrencyException(current.DocumentPath, expectedUpdateTime);
            var stored = Stored(process, value);
            _values[process] = stored;
            return Task.FromResult(stored);
        }

        private FirestoreStoredDocument<ExpirationsProcessSettings> Stored(
            ExpirationsProcess process,
            ExpirationsProcessSettings value) => new(
                value,
                $"modules/vencimientos/settings/{process}",
                $"version-{++_version}");
    }

    private sealed class FakeDirectoryRepository(IEnumerable<ExpirationsBrokerDirectoryEntry> values)
        : IExpirationsBrokerDirectoryRepository
    {
        private readonly IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>> _documents = values
            .Select(value => new FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>(
                value,
                $"brokers/{value.BrokerId:D}",
                "version"))
            .ToList();

        public Task<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_documents.SingleOrDefault(document => document.Value.BrokerId == brokerId));

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>> ListAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(_documents);
    }

    private sealed class FakeProfileRepository(IEnumerable<ExpirationsBrokerProfile> values)
        : IExpirationsBrokerProfileRepository
    {
        private readonly IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>> _documents = values
            .Select(value => new FirestoreStoredDocument<ExpirationsBrokerProfile>(
                value,
                $"profiles/{value.BrokerId:D}",
                "version"))
            .ToList();

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_documents.SingleOrDefault(document => document.Value.BrokerId == brokerId));

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>> ListAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(_documents);

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> CreateAsync(
            ExpirationsBrokerProfile value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> UpdateAsync(
            ExpirationsBrokerProfile value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeOutlookSender(IReadOnlyList<EmailSendResult> results) : IExpirationsOutlookSender
    {
        public int SendCalls { get; private set; }

        public Task<OutlookConnectionInfo> CheckAvailabilityAsync() =>
            Task.FromResult(new OutlookConnectionInfo(true, "Disponible", "sender@example.test", ["sender@example.test"]));

        public string SelectSendingAccount(string emailAddress) => emailAddress;

        public Task<IReadOnlyList<EmailSendResult>> SendBatchAsync(
            IReadOnlyList<EmailSendRequest> requests,
            IProgress<OutlookSendProgress>? progress = null)
        {
            SendCalls++;
            return Task.FromResult(results);
        }
    }

    private sealed class FakeSendHistoryRepository : IExpirationsSendHistoryRepository
    {
        private int _version;

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendOperation>>> ListOperationsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendOperation>>>([]);

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendHistoryItem>>> ListItemsAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendHistoryItem>>>([]);

        public Task<FirestoreStoredDocument<ExpirationsSendOperation>> CreateOperationAsync(
            ExpirationsSendOperation value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Store(value, $"modules/vencimientos/sendOperations/{value.OperationId:D}"));

        public Task<FirestoreStoredDocument<ExpirationsSendHistoryItem>> CreateItemAsync(
            Guid operationId,
            ExpirationsSendHistoryItem value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Store(
                value,
                $"modules/vencimientos/sendOperations/{operationId:D}/items/{value.ItemId:D}"));

        public Task<FirestoreStoredDocument<ExpirationsSendOperation>> UpdateOperationAsync(
            ExpirationsSendOperation value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Store(value, $"modules/vencimientos/sendOperations/{value.OperationId:D}"));

        public Task<FirestoreStoredDocument<ExpirationsSendHistoryItem>> UpdateItemAsync(
            Guid operationId,
            ExpirationsSendHistoryItem value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Store(
                value,
                $"modules/vencimientos/sendOperations/{operationId:D}/items/{value.ItemId:D}"));

        private FirestoreStoredDocument<T> Store<T>(T value, string path) =>
            new(value, path, $"version-{++_version}");
    }
}
