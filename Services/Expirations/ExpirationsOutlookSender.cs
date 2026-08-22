using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Models.Expirations;

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

public sealed class ExpirationsSendExecutionService(IExpirationsOutlookSender sender)
{
    private readonly IExpirationsOutlookSender _sender = sender ?? throw new ArgumentNullException(nameof(sender));

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

        var selectedAccount = _sender.SelectSendingAccount(sendingAccount);
        if (!confirm(preparation.Requests.Count, selectedAccount))
        {
            return new ExpirationsSendExecutionResult
            {
                WasCancelled = true,
                SendingAccount = selectedAccount
            };
        }

        var rawResults = await _sender.SendBatchAsync(preparation.Requests, progress);
        var resultsById = rawResults
            .GroupBy(result => result.RequestId)
            .ToDictionary(group => group.Key, group => group.Last());
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
                "Outlook no devolvió un resultado para este corredor.");
        }).ToList();
        return new ExpirationsSendExecutionResult
        {
            SendingAccount = selectedAccount,
            Items = items
        };
    }
}
