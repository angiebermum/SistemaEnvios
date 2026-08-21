using Grpc.Core;

namespace ECSCommissionsMailer.FirestoreMigration;

public static class FirestoreAuthenticationFailureDetector
{
    private static readonly string[] CredentialMessageMarkers =
    [
        "Application Default Credentials",
        "GOOGLE_APPLICATION_CREDENTIALS",
        "default credentials are not available",
        "default credentials were not found",
        "error reading credential file",
        "error creating credential from JSON"
    ];

    public static bool IsAuthenticationFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is RpcException { StatusCode: StatusCode.Unauthenticated } ||
                string.Equals(
                    current.GetType().FullName,
                    "Google.Apis.Auth.OAuth2.Responses.TokenResponseException",
                    StringComparison.Ordinal))
            {
                return true;
            }

            if (CredentialMessageMarkers.Any(marker =>
                    current.Message.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return false;
    }
}
