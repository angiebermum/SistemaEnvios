using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

internal sealed class OutlookIntegrationException : Exception
{
    public OutlookIntegrationException(OutlookFailureReason reason, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
        if (innerException is not null)
        {
            HResult = innerException.HResult;
        }
    }

    public OutlookFailureReason Reason { get; }
}
