namespace ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authentication;

public sealed record FirebaseUserSession(
    string Uid,
    string Email,
    string IdToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc);

public enum FirebaseAuthenticationFailure
{
    InvalidCredentials,
    DisabledUser,
    SessionExpired,
    Network,
    TooManyRequests,
    Configuration,
    Unknown
}

public sealed class FirebaseAuthenticationException(
    FirebaseAuthenticationFailure failure,
    string safeMessage,
    Exception? innerException = null)
    : InvalidOperationException(safeMessage, innerException)
{
    public FirebaseAuthenticationFailure Failure { get; } = failure;
}

public interface IFirebaseAuthenticationService
{
    FirebaseUserSession? GetCurrentSession();
    Task<FirebaseUserSession> SignInAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<FirebaseUserSession> RestoreSessionAsync(CancellationToken cancellationToken = default);
    Task<FirebaseUserSession> RefreshTokenAsync(CancellationToken cancellationToken = default);
    Task ChangePasswordAsync(string newPassword, CancellationToken cancellationToken = default);
    Task SendPasswordResetAsync(string email, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

public interface IFirebaseTokenProvider
{
    FirebaseUserSession? CurrentSession { get; }
    Task<string> GetIdTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

public interface IRefreshTokenStore
{
    Task SaveAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
