using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsOutlookSender
{
    Task<OutlookConnectionInfo> CheckAvailabilityAsync();
    string SelectSendingAccount(string emailAddress);
    Task<IReadOnlyList<EmailSendResult>> SendBatchAsync(
        IReadOnlyList<EmailSendRequest> requests,
        IProgress<OutlookSendProgress>? progress = null);
}

public sealed class ExpirationsOutlookSender(OutlookEmailService service) : IExpirationsOutlookSender
{
    private readonly OutlookEmailService _service = service ?? throw new ArgumentNullException(nameof(service));

    public Task<OutlookConnectionInfo> CheckAvailabilityAsync() => _service.CheckAvailabilityAsync();

    public string SelectSendingAccount(string emailAddress) => _service.SelectSendingAccount(emailAddress);

    public Task<IReadOnlyList<EmailSendResult>> SendBatchAsync(
        IReadOnlyList<EmailSendRequest> requests,
        IProgress<OutlookSendProgress>? progress = null) =>
        _service.SendBatchAsync(requests, progress);
}

public sealed class ExpirationsSendExecutionService
{
    public const string InitialHistoryFailureMessage =
        "No fue posible registrar el historial inicial. No se llamó a Outlook y no se envió ningún correo.";
    public const string IncompleteHistoryMessage =
        "Outlook procesó el lote, pero el resultado del historial quedó incompleto. No reintente los elementos pendientes: su resultado no está confirmado.";

    private readonly IExpirationsOutlookSender _sender;
    private readonly IExpirationsSendHistoryRepository _history;
    private readonly TimeProvider _timeProvider;

    public ExpirationsSendExecutionService(
        IExpirationsOutlookSender sender,
        IExpirationsSendHistoryRepository history,
        TimeProvider? timeProvider = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ExpirationsSendExecutionResult> SendAsync(
        ExpirationsSendPreparationResult preparation,
        string sendingAccount,
        Func<int, string, bool> confirm,
        IProgress<OutlookSendProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentException.ThrowIfNullOrWhiteSpace(sendingAccount);
        ArgumentNullException.ThrowIfNull(confirm);
        if (!preparation.CanSend)
            throw new InvalidOperationException("El lote no superó el preflight de envío.");

        var preparedByRequest = ValidateAndIndexPreparedItems(preparation);

        var selectedAccount = _sender.SelectSendingAccount(sendingAccount);
        if (!confirm(preparation.Requests.Count, selectedAccount))
        {
            return new ExpirationsSendExecutionResult
            {
                WasCancelled = true,
                SendingAccount = selectedAccount
            };
        }

        FirestoreStoredDocument<ExpirationsSendOperation> storedOperation;
        IReadOnlyDictionary<Guid, FirestoreStoredDocument<ExpirationsSendHistoryItem>> storedItems;
        try
        {
            (storedOperation, storedItems) = await CreateInitialHistoryAsync(
                preparation,
                selectedAccount,
                preparedByRequest);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(InitialHistoryFailureMessage, ex);
        }

        var historyProgress = new ExpirationsHistoryProgress(
            _history,
            storedOperation.Value.OperationId,
            storedItems,
            progress);
        Exception? senderException = null;
        IReadOnlyList<EmailSendResult> rawResults = [];
        try
        {
            rawResults = await _sender.SendBatchAsync(preparation.Requests, historyProgress);
        }
        catch (Exception ex)
        {
            senderException = ex;
        }

        await historyProgress.DrainAsync();
        foreach (var result in rawResults)
            historyProgress.PersistIfMissing(result);
        await historyProgress.DrainAsync();

        var resultsById = historyProgress.Results;
        var items = preparation.Requests.Select(request =>
        {
            if (resultsById.TryGetValue(request.RequestId, out var result))
            {
                return new ExpirationsSendResultItem(
                    request.RequestId,
                    request.BrokerId,
                    request.BrokerName,
                    result.WasSuccessful,
                    result.ErrorMessage);
            }

            return new ExpirationsSendResultItem(
                request.RequestId,
                request.BrokerId,
                request.BrokerName,
                false,
                "Outlook no devolvió un resultado confirmado para este corredor.",
                IsConfirmed: false);
        }).ToList();

        var warnings = new List<string>();
        if (senderException is not null)
        {
            warnings.Add(
                $"Outlook interrumpió el lote sin confirmar todos los resultados: {senderException.Message}");
        }
        if (items.Any(item => !item.IsConfirmed))
            warnings.Add("Uno o más correos quedaron con resultado desconocido.");
        if (historyProgress.Errors.Count > 0)
            warnings.Add(IncompleteHistoryMessage);

        var canComplete = senderException is null &&
            items.All(item => item.IsConfirmed) &&
            historyProgress.Errors.Count == 0;
        if (canComplete)
        {
            var completed = new ExpirationsSendOperation
            {
                OperationId = storedOperation.Value.OperationId,
                Process = storedOperation.Value.Process,
                StartedAtUtc = storedOperation.Value.StartedAtUtc,
                CompletedAtUtc = _timeProvider.GetUtcNow(),
                SendingAccountEmail = storedOperation.Value.SendingAccountEmail,
                Subject = storedOperation.Value.Subject,
                Body = storedOperation.Value.Body,
                Status = ExpirationsSendOperationStatus.Completed,
                TotalCount = storedOperation.Value.TotalCount,
                SuccessCount = items.Count(item => item.WasSuccessful),
                FailureCount = items.Count(item => !item.WasSuccessful),
                RetryOfOperationId = storedOperation.Value.RetryOfOperationId
            };
            try
            {
                _ = await _history.UpdateOperationAsync(
                    completed,
                    storedOperation.UpdateTime);
            }
            catch
            {
                warnings.Add(IncompleteHistoryMessage);
            }
        }

        return new ExpirationsSendExecutionResult
        {
            SendingAccount = selectedAccount,
            OperationId = storedOperation.Value.OperationId,
            Items = items,
            HistoryWarning = string.Join(
                Environment.NewLine,
                warnings.Distinct(StringComparer.Ordinal))
        };
    }

    private static IReadOnlyDictionary<Guid, ExpirationsPreparedSendItem> ValidateAndIndexPreparedItems(
        ExpirationsSendPreparationResult preparation)
    {
        var byRequest = preparation.PreparedItems
            .GroupBy(item => item.RequestId)
            .ToDictionary(group => group.Key, group => group.ToList());
        if (byRequest.Count != preparation.Requests.Count ||
            preparation.Requests.Any(request =>
                !byRequest.TryGetValue(request.RequestId, out var matches) ||
                matches.Count != 1 ||
                matches[0].Attachments.Count == 0 ||
                matches[0].Attachments.Count != request.AttachmentPaths.Count ||
                matches[0].Attachments.Zip(request.AttachmentPaths).Any(pair =>
                    string.IsNullOrWhiteSpace(pair.First.FileName) ||
                    string.IsNullOrWhiteSpace(pair.First.Sha256) ||
                    !PathsEqual(pair.First.Path, pair.Second))) ||
            preparation.Requests.Any(request =>
                !string.Equals(request.Subject, preparation.Requests[0].Subject, StringComparison.Ordinal) ||
                !string.Equals(request.Body, preparation.Requests[0].Body, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "El snapshot de adjuntos no coincide de forma exacta con el preview de envío.");
        }

        return byRequest.ToDictionary(pair => pair.Key, pair => pair.Value[0]);
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return Path.GetFullPath(first).Equals(
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private async Task<(
        FirestoreStoredDocument<ExpirationsSendOperation> Operation,
        IReadOnlyDictionary<Guid, FirestoreStoredDocument<ExpirationsSendHistoryItem>> Items)>
        CreateInitialHistoryAsync(
            ExpirationsSendPreparationResult preparation,
            string selectedAccount,
            IReadOnlyDictionary<Guid, ExpirationsPreparedSendItem> preparedByRequest)
    {
        var operation = new ExpirationsSendOperation
        {
            OperationId = Guid.NewGuid(),
            Process = preparation.Process,
            StartedAtUtc = _timeProvider.GetUtcNow(),
            SendingAccountEmail = selectedAccount,
            Subject = preparation.Requests[0].Subject,
            Body = preparation.Requests[0].Body,
            Status = ExpirationsSendOperationStatus.InProgress,
            TotalCount = preparation.Requests.Count,
            RetryOfOperationId = preparation.RetryOfOperationId
        };
        var storedOperation = await _history.CreateOperationAsync(operation);
        var storedItems = new Dictionary<Guid, FirestoreStoredDocument<ExpirationsSendHistoryItem>>();
        foreach (var request in preparation.Requests)
        {
            var prepared = preparedByRequest[request.RequestId];
            var item = new ExpirationsSendHistoryItem
            {
                ItemId = Guid.NewGuid(),
                RequestId = request.RequestId,
                BrokerId = request.BrokerId,
                BrokerName = request.BrokerName,
                ToRecipients = request.ToRecipients.ToList(),
                CcRecipients = request.CcRecipients.ToList(),
                Attachments = prepared.Attachments.Select(attachment => new ExpirationsSendAttachment(
                    attachment.FileName,
                    attachment.Sha256,
                    attachment.Variant)).ToList(),
                Status = ExpirationsSendItemStatus.Pending,
                RetryOfItemId = prepared.RetryOfItemId
            };
            storedItems.Add(
                request.RequestId,
                await _history.CreateItemAsync(operation.OperationId, item));
        }
        return (storedOperation, storedItems);
    }

    private sealed class ExpirationsHistoryProgress : IProgress<OutlookSendProgress>
    {
        private readonly object _sync = new();
        private readonly IExpirationsSendHistoryRepository _history;
        private readonly Guid _operationId;
        private readonly IReadOnlyDictionary<Guid, FirestoreStoredDocument<ExpirationsSendHistoryItem>> _items;
        private readonly IProgress<OutlookSendProgress>? _outer;
        private readonly Dictionary<Guid, EmailSendResult> _results = [];
        private readonly List<string> _errors = [];
        private Task _pending = Task.CompletedTask;

        public ExpirationsHistoryProgress(
            IExpirationsSendHistoryRepository history,
            Guid operationId,
            IReadOnlyDictionary<Guid, FirestoreStoredDocument<ExpirationsSendHistoryItem>> items,
            IProgress<OutlookSendProgress>? outer)
        {
            _history = history;
            _operationId = operationId;
            _items = items;
            _outer = outer;
        }

        public IReadOnlyDictionary<Guid, EmailSendResult> Results
        {
            get
            {
                lock (_sync)
                    return new Dictionary<Guid, EmailSendResult>(_results);
            }
        }

        public IReadOnlyList<string> Errors
        {
            get
            {
                lock (_sync)
                    return _errors.ToList();
            }
        }

        public void Report(OutlookSendProgress value)
        {
            _outer?.Report(value);
            if (value.Stage == OutlookProgressStage.Completed && value.Result is { } result)
                PersistIfMissing(result);
        }

        public void PersistIfMissing(EmailSendResult result)
        {
            if (!result.WasSuccessful && string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                result = new EmailSendResult
                {
                    RequestId = result.RequestId,
                    WasSuccessful = false,
                    ErrorMessage = "Outlook reportó un fallo sin detalle adicional."
                };
            }
            lock (_sync)
            {
                if (_results.ContainsKey(result.RequestId))
                    return;
                _results.Add(result.RequestId, result);
                _pending = _pending.ContinueWith(
                        _ => PersistAsync(result),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default)
                    .Unwrap();
            }
        }

        public async Task DrainAsync()
        {
            Task pending;
            lock (_sync)
                pending = _pending;
            await pending;
        }

        private async Task PersistAsync(EmailSendResult result)
        {
            if (!_items.TryGetValue(result.RequestId, out var stored))
            {
                AddError("Outlook devolvió un RequestId que no pertenece a la operación.");
                return;
            }
            var original = stored.Value;
            var updated = new ExpirationsSendHistoryItem
            {
                ItemId = original.ItemId,
                RequestId = original.RequestId,
                BrokerId = original.BrokerId,
                BrokerName = original.BrokerName,
                ToRecipients = original.ToRecipients,
                CcRecipients = original.CcRecipients,
                Attachments = original.Attachments,
                Status = result.WasSuccessful
                    ? ExpirationsSendItemStatus.Succeeded
                    : ExpirationsSendItemStatus.Failed,
                ErrorMessage = result.WasSuccessful ? string.Empty : result.ErrorMessage,
                RetryOfItemId = original.RetryOfItemId
            };
            try
            {
                _ = await _history.UpdateItemAsync(
                    _operationId,
                    updated,
                    stored.UpdateTime);
            }
            catch (Exception ex)
            {
                AddError(ex.Message);
            }
        }

        private void AddError(string error)
        {
            lock (_sync)
                _errors.Add(error);
        }
    }
}
