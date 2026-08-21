using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Configuration;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Diagnostics;

namespace ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authentication;

public sealed class FirebaseAuthenticationService : IFirebaseAuthenticationService, IFirebaseTokenProvider
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);
    private readonly HttpClient _httpClient;
    private readonly FirebaseClientOptions _options;
    private readonly IRefreshTokenStore _tokenStore;
    private readonly IFirebaseClientLog _log;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private FirebaseUserSession? _session;

    public FirebaseAuthenticationService(
        HttpClient httpClient,
        FirebaseClientOptions options,
        IRefreshTokenStore tokenStore,
        IFirebaseClientLog? log = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _log = log ?? NullFirebaseClientLog.Instance;
    }

    public FirebaseUserSession? CurrentSession => _session;
    public FirebaseUserSession? GetCurrentSession() => _session;

    public async Task<FirebaseUserSession> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        EnsureConfigured();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={Uri.EscapeDataString(_options.FirebaseApiKey!)}")
        {
            Content = JsonContent.Create(new { email = email.Trim(), password, returnSecureToken = true })
        };

        var payload = await SendAsync<SignInResponse>(request, cancellationToken);
        _session = new FirebaseUserSession(
            payload.LocalId,
            payload.Email,
            payload.IdToken,
            payload.RefreshToken,
            ExpiresAt(payload.ExpiresIn));
        await PersistRefreshTokenIfEnabledAsync(_session.RefreshToken, cancellationToken);
        _log.Info($"Inicio de sesión Firebase exitoso. uid={_session.Uid}; email={_session.Email}.");
        return _session;
    }

    public async Task<FirebaseUserSession> RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        var refreshToken = await _tokenStore.LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new FirebaseAuthenticationException(
                FirebaseAuthenticationFailure.SessionExpired,
                "No existe una sesión guardada válida.");
        }

        _session = new FirebaseUserSession(string.Empty, string.Empty, string.Empty, refreshToken, DateTimeOffset.MinValue);
        try
        {
            return await RefreshTokenAsync(cancellationToken);
        }
        catch
        {
            _session = null;
            await _tokenStore.ClearAsync(cancellationToken);
            throw;
        }
    }

    public async Task<FirebaseUserSession> RefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (_session is null || string.IsNullOrWhiteSpace(_session.RefreshToken))
            {
                throw new FirebaseAuthenticationException(
                    FirebaseAuthenticationFailure.SessionExpired,
                    "La sesión expiró. Inicie sesión nuevamente.");
            }

            EnsureConfigured();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://securetoken.googleapis.com/v1/token?key={Uri.EscapeDataString(_options.FirebaseApiKey!)}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = _session.RefreshToken
                })
            };
            var payload = await SendAsync<RefreshResponse>(request, cancellationToken);
            var identity = await LookupIdentityAsync(payload.IdToken, cancellationToken);
            _session = new FirebaseUserSession(
                payload.UserId,
                identity.Email,
                payload.IdToken,
                payload.RefreshToken,
                ExpiresAt(payload.ExpiresIn));
            await PersistRefreshTokenIfEnabledAsync(_session.RefreshToken, cancellationToken);
            _log.Info($"Token Firebase renovado correctamente. uid={_session.Uid}.");
            return _session;
        }
        catch (Exception ex)
        {
            _log.Warning($"No fue posible renovar la sesión Firebase: {SafeExceptionName(ex)}.");
            throw;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<string> GetIdTokenAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new FirebaseAuthenticationException(
            FirebaseAuthenticationFailure.SessionExpired,
            "La sesión expiró. Inicie sesión nuevamente.");
        if (forceRefresh || session.ExpiresAtUtc <= DateTimeOffset.UtcNow.Add(RefreshSkew))
        {
            session = await RefreshTokenAsync(cancellationToken);
        }

        return session.IdToken;
    }

    public async Task ChangePasswordAsync(string newPassword, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPassword);
        var token = await GetIdTokenAsync(cancellationToken: cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://identitytoolkit.googleapis.com/v1/accounts:update?key={Uri.EscapeDataString(_options.FirebaseApiKey!)}")
        {
            Content = JsonContent.Create(new { idToken = token, password = newPassword, returnSecureToken = true })
        };
        var payload = await SendAsync<SignInResponse>(request, cancellationToken);
        _session = new FirebaseUserSession(
            payload.LocalId,
            payload.Email,
            payload.IdToken,
            payload.RefreshToken,
            ExpiresAt(payload.ExpiresIn));
        await PersistRefreshTokenIfEnabledAsync(_session.RefreshToken, cancellationToken);
    }

    public async Task SendPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        EnsureConfigured();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://identitytoolkit.googleapis.com/v1/accounts:sendOobCode?key={Uri.EscapeDataString(_options.FirebaseApiKey!)}")
        {
            Content = JsonContent.Create(new { requestType = "PASSWORD_RESET", email = email.Trim() })
        };
        _ = await SendAsync<JsonElement>(request, cancellationToken);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _session = null;
        await _tokenStore.ClearAsync(cancellationToken);
        _log.Info("Cierre de sesión Firebase completado.");
    }

    private async Task<LookupUser> LookupIdentityAsync(string idToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://identitytoolkit.googleapis.com/v1/accounts:lookup?key={Uri.EscapeDataString(_options.FirebaseApiKey!)}")
        {
            Content = JsonContent.Create(new { idToken })
        };
        var payload = await SendAsync<LookupResponse>(request, cancellationToken);
        return payload.Users.SingleOrDefault()
            ?? throw new FirebaseAuthenticationException(
                FirebaseAuthenticationFailure.SessionExpired,
                "La sesión ya no corresponde a un usuario válido.");
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FirebaseAuthenticationException(
                FirebaseAuthenticationFailure.Network,
                "La conexión con Firebase agotó el tiempo de espera.");
        }
        catch (HttpRequestException ex)
        {
            throw new FirebaseAuthenticationException(
                FirebaseAuthenticationFailure.Network,
                "No fue posible conectar con Firebase.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var code = await ReadErrorCodeAsync(response, cancellationToken);
                throw MapAuthenticationError(response.StatusCode, code);
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
                ?? throw new FirebaseAuthenticationException(
                    FirebaseAuthenticationFailure.Unknown,
                    "Firebase devolvió una respuesta vacía.");
        }
    }

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            return json.RootElement.GetProperty("error").GetProperty("message").GetString();
        }
        catch
        {
            return null;
        }
    }

    private static FirebaseAuthenticationException MapAuthenticationError(HttpStatusCode status, string? code) =>
        code switch
        {
            "EMAIL_NOT_FOUND" or "INVALID_PASSWORD" or "INVALID_LOGIN_CREDENTIALS" => new(
                FirebaseAuthenticationFailure.InvalidCredentials,
                "El correo o la contraseña son incorrectos."),
            "USER_DISABLED" => new(
                FirebaseAuthenticationFailure.DisabledUser,
                "Esta cuenta fue deshabilitada en Firebase Authentication."),
            "TOKEN_EXPIRED" or "INVALID_ID_TOKEN" or "INVALID_REFRESH_TOKEN" or "USER_NOT_FOUND" => new(
                FirebaseAuthenticationFailure.SessionExpired,
                "La sesión expiró. Inicie sesión nuevamente."),
            "TOO_MANY_ATTEMPTS_TRY_LATER" => new(
                FirebaseAuthenticationFailure.TooManyRequests,
                "Firebase bloqueó temporalmente los intentos. Intente más tarde."),
            _ when status == HttpStatusCode.TooManyRequests => new(
                FirebaseAuthenticationFailure.TooManyRequests,
                "Firebase está limitando temporalmente las solicitudes."),
            _ => new(
                FirebaseAuthenticationFailure.Unknown,
                "Firebase no pudo completar la autenticación.")
        };

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.FirebaseApiKey))
        {
            throw new FirebaseAuthenticationException(
                FirebaseAuthenticationFailure.Configuration,
                "FirebaseApiKey no está configurado.");
        }
    }

    private async Task PersistRefreshTokenIfEnabledAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (_options.RememberSession)
        {
            await _tokenStore.SaveAsync(refreshToken, cancellationToken);
        }
        else
        {
            await _tokenStore.ClearAsync(cancellationToken);
        }
    }

    private static DateTimeOffset ExpiresAt(string secondsText) =>
        DateTimeOffset.UtcNow.AddSeconds(long.TryParse(secondsText, out var seconds) ? seconds : 3600);

    private static string SafeExceptionName(Exception exception) => exception.GetType().Name;

    private sealed record SignInResponse(
        [property: JsonPropertyName("localId")] string LocalId,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("idToken")] string IdToken,
        [property: JsonPropertyName("refreshToken")] string RefreshToken,
        [property: JsonPropertyName("expiresIn")] string ExpiresIn);

    private sealed record RefreshResponse(
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("id_token")] string IdToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] string ExpiresIn);

    private sealed record LookupResponse([property: JsonPropertyName("users")] List<LookupUser> Users);
    private sealed record LookupUser([property: JsonPropertyName("email")] string Email);
}
