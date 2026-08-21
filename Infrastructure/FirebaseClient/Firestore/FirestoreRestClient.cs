using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authentication;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Configuration;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Diagnostics;

namespace ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;

public interface IFirestoreRestClient
{
    Task<FirestoreRestDocument?> GetDocumentAsync(string documentPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FirestoreRestDocument>> ListDocumentsAsync(string parentPath, string collectionId, int pageSize = 200, CancellationToken cancellationToken = default);
    Task<FirestoreRestDocument> CreateDocumentAsync(string parentPath, string collectionId, string documentId, IReadOnlyDictionary<string, FirestoreRestValue> fields, CancellationToken cancellationToken = default);
    Task<FirestoreRestDocument> UpdateDocumentAsync(string documentPath, IReadOnlyDictionary<string, FirestoreRestValue> fields, string expectedUpdateTime, CancellationToken cancellationToken = default);
    Task DeleteDocumentAsync(string documentPath, string expectedUpdateTime, CancellationToken cancellationToken = default);
}

public sealed class FirestoreRestClient : IFirestoreRestClient
{
    private const int MaxReadAttempts = 3;
    private readonly HttpClient _httpClient;
    private readonly IFirebaseTokenProvider _tokens;
    private readonly IFirebaseClientLog _log;
    private readonly string _documentsBase;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public FirestoreRestClient(
        HttpClient httpClient,
        FirebaseClientOptions options,
        IFirebaseTokenProvider tokens,
        IFirebaseClientLog? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokens);
        options.ValidateForAuthenticatedMode();
        _httpClient = httpClient;
        _tokens = tokens;
        _log = log ?? NullFirebaseClientLog.Instance;
        _delay = delay ?? Task.Delay;
        _documentsBase = "https://firestore.googleapis.com/v1/projects/" +
            Uri.EscapeDataString(options.ProjectId!) + "/databases/" +
            Uri.EscapeDataString(options.DatabaseId) + "/documents";
    }

    public async Task<FirestoreRestDocument?> GetDocumentAsync(
        string documentPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendReadAsync<FirestoreRestDocument>(
                token => Request(HttpMethod.Get, DocumentUrl(documentPath), token),
                $"documento '{documentPath}'",
                cancellationToken);
        }
        catch (FirestoreRestException ex) when (ex.Kind == FirestoreFailureKind.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<FirestoreRestDocument>> ListDocumentsAsync(
        string parentPath,
        string collectionId,
        int pageSize = 200,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var result = new List<FirestoreRestDocument>();
        string? pageToken = null;
        do
        {
            var url = CollectionUrl(parentPath, collectionId) + $"?pageSize={pageSize}";
            if (!string.IsNullOrEmpty(pageToken))
            {
                url += "&pageToken=" + Uri.EscapeDataString(pageToken);
            }

            var page = await SendReadAsync<FirestoreListResponse>(
                token => Request(HttpMethod.Get, url, token),
                $"colección '{(string.IsNullOrEmpty(parentPath) ? collectionId : $"{parentPath}/{collectionId}")}'",
                cancellationToken);
            result.AddRange(page.Documents);
            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return result;
    }

    public Task<FirestoreRestDocument> CreateDocumentAsync(
        string parentPath,
        string collectionId,
        string documentId,
        IReadOnlyDictionary<string, FirestoreRestValue> fields,
        CancellationToken cancellationToken = default)
    {
        ValidateDocumentId(documentId);
        var url = CollectionUrl(parentPath, collectionId) + "?documentId=" + Uri.EscapeDataString(documentId);
        var createdPath = string.IsNullOrEmpty(parentPath)
            ? $"{collectionId}/{documentId}"
            : $"{parentPath}/{collectionId}/{documentId}";
        return SendWriteAsync<FirestoreRestDocument>(
            token => JsonRequest(HttpMethod.Post, url, token, fields),
            null,
            null,
            $"documento '{createdPath}'",
            cancellationToken);
    }

    public Task<FirestoreRestDocument> UpdateDocumentAsync(
        string documentPath,
        IReadOnlyDictionary<string, FirestoreRestValue> fields,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedUpdateTime);
        var url = DocumentUrl(documentPath) + "?currentDocument.updateTime=" + Uri.EscapeDataString(expectedUpdateTime);
        return SendWriteAsync<FirestoreRestDocument>(
            token => JsonRequest(HttpMethod.Patch, url, token, fields),
            documentPath,
            expectedUpdateTime,
            $"documento '{documentPath}'",
            cancellationToken);
    }

    public async Task DeleteDocumentAsync(
        string documentPath,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedUpdateTime);
        var url = DocumentUrl(documentPath) + "?currentDocument.updateTime=" + Uri.EscapeDataString(expectedUpdateTime);
        _ = await SendWriteAsync<JsonElement>(
            token => Request(HttpMethod.Delete, url, token),
            documentPath,
            expectedUpdateTime,
            $"documento '{documentPath}'",
            cancellationToken,
            allowEmpty: true);
    }

    private async Task<T> SendReadAsync<T>(
        Func<string, HttpRequestMessage> requestFactory,
        string responseContext,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendAuthenticatedAsync<T>(
                    requestFactory,
                    null,
                    null,
                    responseContext,
                    cancellationToken,
                    false);
            }
            catch (FirestoreRestException ex) when (
                attempt < MaxReadAttempts &&
                ex.Kind is FirestoreFailureKind.RateLimited or FirestoreFailureKind.Server or FirestoreFailureKind.Offline or FirestoreFailureKind.Timeout)
            {
                _log.Warning($"Lectura Firestore transitoria ({ex.Kind}); reintento {attempt}.");
                await _delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
            }
        }
    }

    private Task<T> SendWriteAsync<T>(
        Func<string, HttpRequestMessage> requestFactory,
        string? documentPath,
        string? expectedUpdateTime,
        string responseContext,
        CancellationToken cancellationToken,
        bool allowEmpty = false) =>
        SendAuthenticatedAsync<T>(
            requestFactory,
            documentPath,
            expectedUpdateTime,
            responseContext,
            cancellationToken,
            allowEmpty);

    private async Task<T> SendAuthenticatedAsync<T>(
        Func<string, HttpRequestMessage> requestFactory,
        string? documentPath,
        string? expectedUpdateTime,
        string responseContext,
        CancellationToken cancellationToken,
        bool allowEmpty)
    {
        for (var authAttempt = 0; authAttempt < 2; authAttempt++)
        {
            var token = await _tokens.GetIdTokenAsync(authAttempt == 1, cancellationToken);
            using var request = requestFactory(token);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FirestoreRestException(FirestoreFailureKind.Timeout, "La conexión con Firestore agotó el tiempo de espera.");
            }
            catch (HttpRequestException ex)
            {
                throw new FirestoreRestException(FirestoreFailureKind.Offline, "Sin conexión a Firestore.", innerException: ex);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && authAttempt == 0)
                {
                    _log.Warning("Firestore devolvió 401; se renovará el token una sola vez.");
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw await CreateExceptionAsync(response, documentPath, expectedUpdateTime, cancellationToken);
                }

                if (allowEmpty && response.Content.Headers.ContentLength == 0)
                {
                    return default!;
                }

                T? value;
                try
                {
                    value = await response.Content.ReadFromJsonAsync<T>(FirestoreRestJson.Options, cancellationToken);
                }
                catch (JsonException ex)
                {
                    var jsonPath = string.IsNullOrWhiteSpace(ex.Path)
                        ? string.Empty
                        : $", campo JSON '{ex.Path}'";
                    throw new InvalidDataException(
                        $"Firestore devolvió JSON REST inválido para {responseContext}{jsonPath}. {ex.Message}",
                        ex);
                }
                if (value is null && !allowEmpty)
                {
                    throw new FirestoreRestException(FirestoreFailureKind.Unknown, "Firestore devolvió una respuesta vacía.");
                }

                return value!;
            }
        }

        throw new FirestoreRestException(
            FirestoreFailureKind.AuthenticationExpired,
            "La sesión expiró. Inicie sesión nuevamente.",
            HttpStatusCode.Unauthorized);
    }

    private static async Task<FirestoreRestException> CreateExceptionAsync(
        HttpResponseMessage response,
        string? documentPath,
        string? expectedUpdateTime,
        CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        var firestoreStatus = await ReadSafeErrorStatusAsync(response, cancellationToken);
        if ((status is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed ||
             string.Equals(firestoreStatus, "FAILED_PRECONDITION", StringComparison.Ordinal)) &&
            documentPath is not null && expectedUpdateTime is not null)
        {
            return new FirestoreConcurrencyException(documentPath, expectedUpdateTime);
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => new FirestoreRestException(FirestoreFailureKind.AuthenticationExpired, "La sesión expiró. Inicie sesión nuevamente.", status),
            HttpStatusCode.Forbidden => new FirestoreRestException(FirestoreFailureKind.PermissionDenied, "No tiene permiso para realizar esta operación.", status),
            HttpStatusCode.NotFound => new FirestoreRestException(FirestoreFailureKind.NotFound, "El documento solicitado no existe.", status),
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => new FirestoreRestException(FirestoreFailureKind.Conflict, "Los datos fueron modificados por otro usuario.", status),
            HttpStatusCode.TooManyRequests => new FirestoreRestException(FirestoreFailureKind.RateLimited, "Firestore está limitando temporalmente las solicitudes.", status),
            >= HttpStatusCode.InternalServerError => new FirestoreRestException(FirestoreFailureKind.Server, "Firestore no está disponible temporalmente.", status),
            HttpStatusCode.BadRequest => new FirestoreRestException(FirestoreFailureKind.Validation, "Firestore rechazó los datos enviados.", status),
            _ => new FirestoreRestException(FirestoreFailureKind.Unknown, "Firestore no pudo completar la operación.", status)
        };
    }

    private static async Task<string?> ReadSafeErrorStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            return json.RootElement.TryGetProperty("error", out var error) &&
                   error.TryGetProperty("status", out var status)
                ? status.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage JsonRequest(
        HttpMethod method,
        string url,
        string token,
        IReadOnlyDictionary<string, FirestoreRestValue> fields)
    {
        var request = Request(method, url, token);
        request.Content = new StringContent(FirestoreRestJson.SerializeFields(fields), Encoding.UTF8, "application/json");
        return request;
    }

    private string DocumentUrl(string path) => _documentsBase + "/" + EscapePath(path);

    private string CollectionUrl(string parentPath, string collectionId)
    {
        ValidateSegment(collectionId, nameof(collectionId));
        var parent = string.IsNullOrWhiteSpace(parentPath) ? string.Empty : "/" + EscapePath(parentPath);
        return _documentsBase + parent + "/" + Uri.EscapeDataString(collectionId);
    }

    private static string EscapePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    }

    private static void ValidateDocumentId(string value) => ValidateSegment(value, nameof(value));

    private static void ValidateSegment(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/'))
        {
            throw new ArgumentException("El identificador Firestore debe ser un segmento no vacío.", parameter);
        }
    }
}
