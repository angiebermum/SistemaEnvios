using System.Net;

namespace ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;

public enum FirestoreFailureKind
{
    Offline,
    Timeout,
    AuthenticationExpired,
    PermissionDenied,
    NotFound,
    Conflict,
    RateLimited,
    Server,
    Validation,
    Unknown
}

public class FirestoreRestException : InvalidOperationException
{
    public FirestoreRestException(
        FirestoreFailureKind kind,
        string safeMessage,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(safeMessage, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public FirestoreFailureKind Kind { get; }
    public HttpStatusCode? StatusCode { get; }
}

public sealed class FirestoreConcurrencyException(string documentPath, string expectedUpdateTime)
    : FirestoreRestException(
        FirestoreFailureKind.Conflict,
        "Los datos fueron modificados por otro usuario. Recargue la información antes de guardar nuevamente.",
        HttpStatusCode.PreconditionFailed)
{
    public string DocumentPath { get; } = documentPath;
    public string ExpectedUpdateTime { get; } = expectedUpdateTime;
}
