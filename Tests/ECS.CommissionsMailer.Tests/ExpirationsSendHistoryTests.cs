using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services;
using ECS.CommissionsMailer.Services.Expirations;
using ECS.CommissionsMailer.Views;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsSendHistoryTests
{
    private static readonly Guid BrokerOne = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BrokerTwo = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task InitialOperationAndEveryPendingItemExistBeforeSenderRuns()
    {
        var history = new MemoryHistoryRepository();
        var preparation = Preparation(
            ExpirationsProcess.PreviousMonth,
            Request(BrokerOne, "Uno", "uno.xlsx"),
            Request(BrokerTwo, "Dos", "dos.xlsx"));
        var sender = new RecordingSender(requests =>
        {
            Assert.Single(history.Operations);
            Assert.Equal(ExpirationsSendOperationStatus.InProgress, history.Operations[0].Status);
            Assert.Equal(2, history.Items.Count);
            Assert.All(history.Items, item => Assert.Equal(ExpirationsSendItemStatus.Pending, item.Status));
            return requests.Select(request => Result(request, true)).ToList();
        });

        var result = await new ExpirationsSendExecutionService(sender, history).SendAsync(
            preparation,
            "sender@example.test",
            (_, _) => true);

        Assert.Equal(2, result.SuccessfulCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(ExpirationsSendOperationStatus.Completed, history.Operations.Single().Status);
        Assert.All(history.Items, item => Assert.Equal(ExpirationsSendItemStatus.Succeeded, item.Status));
        Assert.All(
            history.Items.SelectMany(item => item.Attachments),
            attachment => Assert.Equal(Path.GetFileName(attachment.FileName), attachment.FileName));
        Assert.Equal(["operation:create", "item:create", "item:create"], history.Events.Take(3));
        Assert.Equal(1, sender.SendCalls);
    }

    [Fact]
    public async Task InitialHistoryFailureBlocksSenderCompletely()
    {
        var history = new MemoryHistoryRepository { FailItemCreate = true };
        var sender = new RecordingSender(requests => requests.Select(request => Result(request, true)).ToList());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ExpirationsSendExecutionService(sender, history).SendAsync(
                Preparation(ExpirationsProcess.PreviousMonth, Request(BrokerOne, "Uno", "uno.xlsx")),
                "sender@example.test",
                (_, _) => true));

        Assert.Contains("No se llamó a Outlook", exception.Message);
        Assert.Equal(0, sender.SendCalls);
        Assert.Single(history.Operations);
        Assert.Equal(ExpirationsSendOperationStatus.InProgress, history.Operations[0].Status);
    }

    [Fact]
    public async Task MixedResultsPersistTerminalItemsAndExactCounters()
    {
        var history = new MemoryHistoryRepository();
        var first = Request(BrokerOne, "Uno", "uno.xlsx");
        var second = Request(BrokerTwo, "Dos", "dos.xlsx");
        var sender = new RecordingSender(requests =>
        [
            Result(requests[0], true),
            Result(requests[1], false, "Falló Outlook")
        ], reportProgress: true);

        var result = await new ExpirationsSendExecutionService(sender, history).SendAsync(
            Preparation(ExpirationsProcess.NextMonth, first, second),
            "sender@example.test",
            (_, _) => true);

        var operation = history.Operations.Single();
        Assert.Equal((1, 1), (operation.SuccessCount, operation.FailureCount));
        Assert.Equal(ExpirationsSendOperationStatus.Completed, operation.Status);
        Assert.Equal(ExpirationsSendItemStatus.Succeeded,
            history.Items.Single(item => item.BrokerId == BrokerOne).Status);
        var failed = history.Items.Single(item => item.BrokerId == BrokerTwo);
        Assert.Equal(ExpirationsSendItemStatus.Failed, failed.Status);
        Assert.Equal("Falló Outlook", failed.ErrorMessage);
        Assert.True(result.HistoryIsComplete);
    }

    [Fact]
    public async Task ResultWriteFailureLeavesPendingAndInProgressWithoutResending()
    {
        var history = new MemoryHistoryRepository { FailItemUpdate = true };
        var sender = new RecordingSender(requests => requests.Select(request => Result(request, true)).ToList());

        var result = await new ExpirationsSendExecutionService(sender, history).SendAsync(
            Preparation(ExpirationsProcess.PreviousMonth, Request(BrokerOne, "Uno", "uno.xlsx")),
            "sender@example.test",
            (_, _) => true);

        Assert.Equal(1, sender.SendCalls);
        Assert.Equal(ExpirationsSendItemStatus.Pending, history.Items.Single().Status);
        Assert.Equal(ExpirationsSendOperationStatus.InProgress, history.Operations.Single().Status);
        Assert.Contains("historial quedó incompleto", result.HistoryWarning);
    }

    [Fact]
    public async Task RetryUsesOnlyFailedSnapshotAndCreatesLinkedImmutableAttempt()
    {
        using var files = RetryFiles.Create();
        var originalOperation = Operation(
            ExpirationsProcess.NextMonth,
            ExpirationsSendOperationStatus.Completed,
            total: 2,
            success: 1,
            failure: 1);
        var succeeded = HistoryItem(BrokerOne, "Exitoso", ExpirationsSendItemStatus.Succeeded,
            files.StandardAttachment, to: ["old-success@example.test"]);
        var failed = HistoryItem(BrokerTwo, "Fallido", ExpirationsSendItemStatus.Failed,
            files.SpecialAttachments, to: ["snapshot-to@example.test"], cc: ["snapshot-cc@example.test"]);
        var preparation = new ExpirationsRetryPreparationService().Prepare(
            originalOperation,
            [succeeded, failed],
            files.Batch);

        Assert.True(preparation.CanRetry);
        var retryRequest = Assert.Single(preparation.Preparation!.Requests);
        Assert.Equal(BrokerTwo, retryRequest.BrokerId);
        Assert.Equal(["snapshot-to@example.test"], retryRequest.ToRecipients);
        Assert.Equal(["snapshot-cc@example.test"], retryRequest.CcRecipients);
        Assert.Equal(originalOperation.Subject, retryRequest.Subject);
        Assert.Equal(originalOperation.Body, retryRequest.Body);
        Assert.Equal(2, retryRequest.AttachmentPaths.Count);

        var history = new MemoryHistoryRepository();
        history.Seed(originalOperation, [succeeded, failed]);
        var sender = new RecordingSender(requests => requests.Select(request => Result(request, true)).ToList());
        _ = await new ExpirationsSendExecutionService(sender, history).SendAsync(
            preparation.Preparation,
            "new-account@example.test",
            (_, _) => true);

        Assert.Single(sender.LastRequests);
        Assert.Equal(BrokerTwo, sender.LastRequests[0].BrokerId);
        Assert.Equal(2, history.Operations.Count);
        var retryOperation = history.Operations.Single(value => value.OperationId != originalOperation.OperationId);
        Assert.Equal(originalOperation.OperationId, retryOperation.RetryOfOperationId);
        Assert.Equal("new-account@example.test", retryOperation.SendingAccountEmail);
        Assert.Equal(ExpirationsSendOperationStatus.Completed, retryOperation.Status);
        Assert.Equal(ExpirationsSendItemStatus.Succeeded, succeeded.Status);
        Assert.Equal(ExpirationsSendItemStatus.Failed, failed.Status);
        var retryItem = history.Items.Single(value => value.RetryOfItemId == failed.ItemId);
        Assert.Equal(2, retryItem.Attachments.Count);
    }

    [Fact]
    public void PendingOrInProgressHistoryCanNeverBeRetried()
    {
        using var files = RetryFiles.Create();
        var pending = HistoryItem(BrokerOne, "Pendiente", ExpirationsSendItemStatus.Pending,
            files.StandardAttachment);
        var inProgress = new ExpirationsRetryPreparationService().Prepare(
            Operation(ExpirationsProcess.NextMonth, ExpirationsSendOperationStatus.InProgress, 1, 0, 0),
            [pending],
            files.Batch);
        var completedPending = new ExpirationsRetryPreparationService().Prepare(
            Operation(ExpirationsProcess.NextMonth, ExpirationsSendOperationStatus.Completed, 1, 0, 0),
            [pending],
            files.Batch);

        Assert.False(inProgress.CanRetry);
        Assert.False(completedPending.CanRetry);
        Assert.Contains(inProgress.Errors, error => error.Contains("no está confirmado", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(completedPending.Errors, error => error.Contains("no contiene correos fallidos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChangedAttachmentHashBlocksRetry()
    {
        using var files = RetryFiles.Create();
        var failed = HistoryItem(BrokerTwo, "Fallido", ExpirationsSendItemStatus.Failed,
            files.SpecialAttachments);
        File.AppendAllText(files.Batch.Files[1].OutputPath, "cambio");

        var result = new ExpirationsRetryPreparationService().Prepare(
            Operation(ExpirationsProcess.NextMonth, ExpirationsSendOperationStatus.Completed, 1, 0, 1),
            [failed],
            files.Batch);

        Assert.False(result.CanRetry);
        Assert.Contains(result.Errors, error => error.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PreviousAndNextOperationsAreShownIndependentlyAndIncompleteIsUnknown()
    {
        var previous = Operation(
            ExpirationsProcess.PreviousMonth,
            ExpirationsSendOperationStatus.Completed,
            1, 1, 0);
        var next = WithStarted(Operation(
            ExpirationsProcess.NextMonth,
            ExpirationsSendOperationStatus.InProgress,
            1, 0, 0), previous.StartedAtUtc.AddMinutes(1));
        var state = new ExpirationsSendHistoryState();

        state.ApplyOperations([previous, next]);

        Assert.Equal(["Mes siguiente", "Mes anterior"], state.Operations.Select(row => row.ProcessText));
        Assert.Equal("Incompleta / no confirmado", state.Operations[0].StatusText);
    }

    [Fact]
    public void CancellationOperationRoundTripsAndIsShownInHistory()
    {
        var operation = Operation(
            ExpirationsProcess.Cancellations,
            ExpirationsSendOperationStatus.Completed,
            1, 1, 0);
        var mapper = new ExpirationsSendOperationMapper();
        var mapped = mapper.FromFields(mapper.ToFields(operation));
        var state = new ExpirationsSendHistoryState();

        state.ApplyOperations([mapped]);

        Assert.Equal(ExpirationsProcess.Cancellations, mapped.Process);
        Assert.Equal("Cancelaciones", Assert.Single(state.Operations).ProcessText);
    }

    [Fact]
    public void FirestoreMappersRoundTripAuditSnapshotWithoutWindowsPaths()
    {
        var operation = Operation(
            ExpirationsProcess.NextMonth,
            ExpirationsSendOperationStatus.InProgress,
            1, 0, 0);
        var item = new ExpirationsSendHistoryItem
        {
            ItemId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            BrokerId = BrokerOne,
            BrokerName = "Uno",
            ToRecipients = ["to@example.test"],
            CcRecipients = ["cc@example.test"],
            Attachments =
            [
                new ExpirationsSendAttachment(
                    "reporte.xlsx",
                    new string('a', 64),
                    ExpirationsGeneratedFileVariant.Standard)
            ],
            Status = ExpirationsSendItemStatus.Pending
        };
        var operationMapper = new ExpirationsSendOperationMapper();
        var itemMapper = new ExpirationsSendHistoryItemMapper();

        var operationFields = operationMapper.ToFields(operation);
        var itemFields = itemMapper.ToFields(item);
        var roundTripOperation = operationMapper.FromFields(operationFields);
        var roundTripItem = itemMapper.FromFields(itemFields);

        Assert.Equal(operation.OperationId, roundTripOperation.OperationId);
        Assert.Equal(operation.Process, roundTripOperation.Process);
        Assert.Equal(item.ItemId, roundTripItem.ItemId);
        Assert.Equal("reporte.xlsx", Assert.Single(roundTripItem.Attachments).FileName);
        Assert.DoesNotContain(itemFields.Keys, key => key.Contains("path", StringComparison.OrdinalIgnoreCase));
    }

    private static ExpirationsSendOperation WithStarted(
        ExpirationsSendOperation value,
        DateTimeOffset startedAtUtc) => new()
    {
        OperationId = value.OperationId,
        Process = value.Process,
        StartedAtUtc = startedAtUtc,
        CompletedAtUtc = value.CompletedAtUtc,
        SendingAccountEmail = value.SendingAccountEmail,
        Subject = value.Subject,
        Body = value.Body,
        Status = value.Status,
        TotalCount = value.TotalCount,
        SuccessCount = value.SuccessCount,
        FailureCount = value.FailureCount,
        RetryOfOperationId = value.RetryOfOperationId
    };

    private static EmailSendRequest Request(Guid brokerId, string brokerName, params string[] paths) => new()
    {
        BrokerId = brokerId,
        BrokerName = brokerName,
        ToRecipients = [$"{brokerName.ToLowerInvariant()}@example.test"],
        CcRecipients = ["cc@example.test"],
        Subject = "Snapshot subject",
        Body = "Snapshot body",
        AttachmentPaths = paths,
        ReviewConfirmed = true
    };

    private static ExpirationsSendPreparationResult Preparation(
        ExpirationsProcess process,
        params EmailSendRequest[] requests) => new()
    {
        Process = process,
        Settings = new ExpirationsProcessSettings
        {
            DefaultSubject = "Snapshot subject",
            DefaultMessage = "Snapshot body"
        },
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

    private static EmailSendResult Result(
        EmailSendRequest request,
        bool success,
        string error = "") => new()
    {
        RequestId = request.RequestId,
        WasSuccessful = success,
        ErrorMessage = error
    };

    private static ExpirationsSendOperation Operation(
        ExpirationsProcess process,
        ExpirationsSendOperationStatus status,
        int total,
        int success,
        int failure) => new()
    {
        OperationId = Guid.NewGuid(),
        Process = process,
        StartedAtUtc = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero),
        CompletedAtUtc = status == ExpirationsSendOperationStatus.Completed
            ? new DateTimeOffset(2026, 8, 21, 12, 1, 0, TimeSpan.Zero)
            : null,
        SendingAccountEmail = "historical@example.test",
        Subject = "Original subject",
        Body = "Original body",
        Status = status,
        TotalCount = total,
        SuccessCount = success,
        FailureCount = failure
    };

    private static ExpirationsSendHistoryItem HistoryItem(
        Guid brokerId,
        string brokerName,
        ExpirationsSendItemStatus status,
        IReadOnlyList<ExpirationsSendAttachment> attachments,
        IReadOnlyList<string>? to = null,
        IReadOnlyList<string>? cc = null) => new()
    {
        ItemId = Guid.NewGuid(),
        RequestId = Guid.NewGuid(),
        BrokerId = brokerId,
        BrokerName = brokerName,
        ToRecipients = to ?? ["to@example.test"],
        CcRecipients = cc ?? [],
        Attachments = attachments,
        Status = status,
        ErrorMessage = status == ExpirationsSendItemStatus.Failed ? "Falló Outlook" : string.Empty
    };

    private sealed class RecordingSender(
        Func<IReadOnlyList<EmailSendRequest>, IReadOnlyList<EmailSendResult>> handler,
        bool reportProgress = false) : IExpirationsOutlookSender
    {
        public int SendCalls { get; private set; }
        public IReadOnlyList<EmailSendRequest> LastRequests { get; private set; } = [];
        public IReadOnlyList<string> Events { get; private set; } = [];

        public Task<OutlookConnectionInfo> CheckAvailabilityAsync() =>
            Task.FromResult(new OutlookConnectionInfo(true, "Disponible", "sender@example.test", ["sender@example.test"]));

        public string SelectSendingAccount(string emailAddress) => emailAddress;

        public Task<IReadOnlyList<EmailSendResult>> SendBatchAsync(
            IReadOnlyList<EmailSendRequest> requests,
            IProgress<OutlookSendProgress>? progress = null)
        {
            SendCalls++;
            Events = ["sender"];
            LastRequests = requests;
            var results = handler(requests);
            if (reportProgress)
            {
                for (var index = 0; index < requests.Count; index++)
                {
                    progress?.Report(new OutlookSendProgress
                    {
                        Request = requests[index],
                        Current = index + 1,
                        Total = requests.Count,
                        Stage = OutlookProgressStage.Completed,
                        Result = results[index]
                    });
                }
            }
            return Task.FromResult(results);
        }
    }

    private sealed class MemoryHistoryRepository : IExpirationsSendHistoryRepository
    {
        private readonly Dictionary<Guid, FirestoreStoredDocument<ExpirationsSendOperation>> _operations = [];
        private readonly Dictionary<Guid, Dictionary<Guid, FirestoreStoredDocument<ExpirationsSendHistoryItem>>> _items = [];
        private int _version;

        public bool FailItemCreate { get; init; }
        public bool FailItemUpdate { get; init; }
        public List<string> Events { get; } = [];
        public IReadOnlyList<ExpirationsSendOperation> Operations => _operations.Values.Select(value => value.Value).ToList();
        public IReadOnlyList<ExpirationsSendHistoryItem> Items => _items.Values.SelectMany(value => value.Values).Select(value => value.Value).ToList();

        public void Seed(
            ExpirationsSendOperation operation,
            IReadOnlyList<ExpirationsSendHistoryItem> items)
        {
            _operations[operation.OperationId] = Store(operation, OperationPath(operation.OperationId));
            _items[operation.OperationId] = items.ToDictionary(
                item => item.ItemId,
                item => Store(item, ItemPath(operation.OperationId, item.ItemId)));
        }

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendOperation>>> ListOperationsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendOperation>>>(
                _operations.Values.OrderByDescending(value => value.Value.StartedAtUtc).ToList());

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendHistoryItem>>> ListItemsAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendHistoryItem>>>(
                _items.GetValueOrDefault(operationId)?.Values.ToList() ?? []);

        public Task<FirestoreStoredDocument<ExpirationsSendOperation>> CreateOperationAsync(
            ExpirationsSendOperation value,
            CancellationToken cancellationToken = default)
        {
            Events.Add("operation:create");
            var stored = Store(value, OperationPath(value.OperationId));
            _operations.Add(value.OperationId, stored);
            _items.Add(value.OperationId, []);
            return Task.FromResult(stored);
        }

        public Task<FirestoreStoredDocument<ExpirationsSendHistoryItem>> CreateItemAsync(
            Guid operationId,
            ExpirationsSendHistoryItem value,
            CancellationToken cancellationToken = default)
        {
            Events.Add("item:create");
            if (FailItemCreate)
                throw new IOException("Firestore no disponible");
            var stored = Store(value, ItemPath(operationId, value.ItemId));
            _items[operationId].Add(value.ItemId, stored);
            return Task.FromResult(stored);
        }

        public Task<FirestoreStoredDocument<ExpirationsSendOperation>> UpdateOperationAsync(
            ExpirationsSendOperation value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            Events.Add("operation:update");
            var stored = Store(value, OperationPath(value.OperationId));
            _operations[value.OperationId] = stored;
            return Task.FromResult(stored);
        }

        public Task<FirestoreStoredDocument<ExpirationsSendHistoryItem>> UpdateItemAsync(
            Guid operationId,
            ExpirationsSendHistoryItem value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            Events.Add("item:update");
            if (FailItemUpdate)
                throw new IOException("No se confirmó el resultado");
            var stored = Store(value, ItemPath(operationId, value.ItemId));
            _items[operationId][value.ItemId] = stored;
            return Task.FromResult(stored);
        }

        private FirestoreStoredDocument<T> Store<T>(T value, string path) =>
            new(value, path, $"version-{++_version}");
        private static string OperationPath(Guid id) => $"modules/vencimientos/sendOperations/{id:D}";
        private static string ItemPath(Guid operationId, Guid itemId) =>
            $"{OperationPath(operationId)}/items/{itemId:D}";
    }

    private sealed class RetryFiles : IDisposable
    {
        private RetryFiles(
            string directory,
            ExpirationsGenerationBatch batch,
            IReadOnlyList<ExpirationsSendAttachment> standardAttachment,
            IReadOnlyList<ExpirationsSendAttachment> specialAttachments)
        {
            Directory = directory;
            Batch = batch;
            StandardAttachment = standardAttachment;
            SpecialAttachments = specialAttachments;
        }

        public string Directory { get; }
        public ExpirationsGenerationBatch Batch { get; }
        public IReadOnlyList<ExpirationsSendAttachment> StandardAttachment { get; }
        public IReadOnlyList<ExpirationsSendAttachment> SpecialAttachments { get; }

        public static RetryFiles Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"ecs-exp-history-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var definitions = new[]
            {
                (BrokerOne, "standard.xlsx", ExpirationsGeneratedFileVariant.Standard),
                (BrokerTwo, "alphabetical.xlsx", ExpirationsGeneratedFileVariant.FelixAlphabetical),
                (BrokerTwo, "expiration.xlsx", ExpirationsGeneratedFileVariant.FelixExpirationDate)
            };
            var hashService = new GeneratedFileHashService();
            var generated = definitions.Select((definition, index) =>
            {
                var path = Path.Combine(directory, definition.Item2);
                ExpirationsUatCompletionTests.CreateValidWorkbook(path, $"archivo-{index}");
                return new ExpirationsGeneratedFile
                {
                    BrokerId = definition.Item1,
                    BrokerName = definition.Item1 == BrokerOne ? "Uno" : "Dos",
                    OutputPath = path,
                    Variant = definition.Item3,
                    Sha256 = hashService.ComputeSha256(path)
                };
            }).ToList();
            var attachments = generated.Select(file => new ExpirationsSendAttachment(
                Path.GetFileName(file.OutputPath),
                file.Sha256,
                file.Variant)).ToList();
            return new RetryFiles(
                directory,
                new ExpirationsGenerationBatch
                {
                    Process = ExpirationsProcess.NextMonth,
                    OutputDirectory = directory,
                    Files = generated
                },
                [attachments[0]],
                [attachments[1], attachments[2]]);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
