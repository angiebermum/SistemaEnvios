using System.Net;
using System.Text;
using System.Text.Json;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authentication;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Configuration;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

#pragma warning disable xUnit1051 // Explicit cancellation behavior is itself exercised by these HTTP tests.

namespace ECS.CommissionsMailer.Tests;

public sealed class FirebaseRuntimeTests
{
    [Fact]
    public async Task AuthenticationLoginReturnsUserTokensWithoutPersistingPassword()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.OK,
            """{"localId":"uid-1","email":"user@example.com","idToken":"id-1","refreshToken":"refresh-1","expiresIn":"3600"}"""));
        var store = new MemoryTokenStore();
        var service = Auth(handler, store);

        var result = await service.SignInAsync("user@example.com", "NeverPersistThisPassword!");

        Assert.Equal("uid-1", result.Uid);
        Assert.Equal("id-1", result.IdToken);
        Assert.Equal("refresh-1", await store.LoadAsync());
        Assert.DoesNotContain("NeverPersistThisPassword!", store.SavedValues);
    }

    [Fact]
    public async Task AuthenticationMapsWrongPasswordToSafeFailure()
    {
        var service = Auth(new QueueHandler(Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"INVALID_LOGIN_CREDENTIALS"}}""")), new MemoryTokenStore());
        var error = await Assert.ThrowsAsync<FirebaseAuthenticationException>(() =>
            service.SignInAsync("user@example.com", "wrong"));
        Assert.Equal(FirebaseAuthenticationFailure.InvalidCredentials, error.Failure);
        Assert.DoesNotContain("INVALID_LOGIN_CREDENTIALS", error.Message);
    }

    [Fact]
    public async Task AuthenticationMapsDisabledUser()
    {
        var service = Auth(new QueueHandler(Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"USER_DISABLED"}}""")), new MemoryTokenStore());
        var error = await Assert.ThrowsAsync<FirebaseAuthenticationException>(() =>
            service.SignInAsync("user@example.com", "password"));
        Assert.Equal(FirebaseAuthenticationFailure.DisabledUser, error.Failure);
    }

    [Fact]
    public async Task RefreshTokenReplacesExpiredSessionAndPersistsOnlyNewRefreshToken()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, """{"localId":"uid-1","email":"user@example.com","idToken":"old","refreshToken":"refresh-old","expiresIn":"0"}"""),
            Json(HttpStatusCode.OK, """{"user_id":"uid-1","id_token":"new-id","refresh_token":"refresh-new","expires_in":"3600"}"""),
            Json(HttpStatusCode.OK, """{"users":[{"email":"user@example.com"}]}"""));
        var store = new MemoryTokenStore();
        var service = Auth(handler, store);
        await service.SignInAsync("user@example.com", "password");

        var token = await ((IFirebaseTokenProvider)service).GetIdTokenAsync();

        Assert.Equal("new-id", token);
        Assert.Equal("refresh-new", await store.LoadAsync());
    }

    [Fact]
    public async Task LogoutClearsMemoryAndProtectedTokenAbstraction()
    {
        var store = new MemoryTokenStore();
        var service = Auth(new QueueHandler(Json(HttpStatusCode.OK,
            """{"localId":"uid","email":"u@e.test","idToken":"id","refreshToken":"refresh","expiresIn":"3600"}""")), store);
        await service.SignInAsync("u@e.test", "password");
        await service.SignOutAsync();
        Assert.Null(service.GetCurrentSession());
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task DpapiRefreshTokenStoreNeverWritesPlaintext()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ecs-dpapi-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "token.dat");
        try
        {
            var store = new ProtectedRefreshTokenStore(path);
            const string token = "sensitive-refresh-token-value";
            await store.SaveAsync(token);
            Assert.Equal(token, await store.LoadAsync());
            Assert.DoesNotContain(token, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
            await store.ClearAsync();
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(null, "No existe")]
    [InlineData(false, "inactivo")]
    public void AuthorizationBlocksMissingOrInactiveProfile(bool? active, string expected)
    {
        var user = active.HasValue ? User(isActive: active.Value) : null;
        var error = Assert.Throws<AppUserAuthorizationException>(() =>
            AppUserAuthorization.DemandCommissionsAccess(user));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizationBlocksUserWithoutCommissionsPermission()
    {
        var error = Assert.Throws<AppUserAuthorizationException>(() =>
            AppUserAuthorization.DemandCommissionsAccess(User(canUseCommissions: false)));
        Assert.Contains("permiso", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizationAcceptsOperatorAndAdminButOnlyAdminCanAdminister()
    {
        AppUserAuthorization.DemandCommissionsAccess(User(role: AppUserRole.Operator));
        AppUserAuthorization.DemandCommissionsAccess(User(role: AppUserRole.Admin));
        Assert.Throws<AppUserAuthorizationException>(() => AppUserAuthorization.DemandAdmin(User()));
        AppUserAuthorization.DemandAdmin(User(role: AppUserRole.Admin));
    }

    [Fact]
    public async Task AppUserRepositoryCreatesDocumentIdAndPayloadFromTheSameUid()
    {
        var user = User(role: AppUserRole.Operator);
        user.Uid = "uid-new-user";
        user.CreatedAtUtc = new DateTimeOffset(2026, 8, 10, 1, 2, 3, TimeSpan.Zero);
        user.UpdatedAtUtc = user.CreatedAtUtc;
        var responseFields = new AppUserMapper().ToFields(user)
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
        var handler = new QueueHandler(Json(HttpStatusCode.OK, JsonSerializer.Serialize(
            new FirestoreRestDocument
            {
                Name = "projects/demo-project/databases/(default)/documents/appUsers/uid-new-user",
                Fields = responseFields,
                CreateTime = "2026-08-10T01:02:03Z",
                UpdateTime = "2026-08-10T01:02:03Z"
            }, FirestoreRestJson.Options)));

        var stored = await new AppUserRepository(Client(handler)).CreateAsync(user);

        Assert.Equal(HttpMethod.Post, handler.RequestMethods.Single());
        Assert.Contains("documentId=uid-new-user", handler.RequestUris.Single()!.Query, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(handler.RequestBodies.Single()!);
        Assert.Equal("uid-new-user", payload.RootElement.GetProperty("fields")
            .GetProperty("uid").GetProperty("stringValue").GetString());
        Assert.Equal("appUsers/uid-new-user", stored.DocumentPath);
        Assert.Equal(user.Uid, stored.Value.Uid);
    }

    [Fact]
    public async Task FirestoreAddsBearerAuthorizationHeader()
    {
        var handler = new QueueHandler(DocumentResponse("brokers/b1"));
        var client = Client(handler, new FakeTokenProvider("user-token"));
        _ = await client.GetDocumentAsync("brokers/b1");
        Assert.Equal("Bearer", handler.Requests.Single().Authorization?.Scheme);
        Assert.Equal("user-token", handler.Requests.Single().Authorization?.Parameter);
    }

    [Fact]
    public async Task Firestore401RefreshesAndRetriesExactlyOnce()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.Unauthorized, """{"error":{"status":"UNAUTHENTICATED"}}"""),
            DocumentResponse("brokers/b1"));
        var tokens = new FakeTokenProvider("old", "new");
        var client = Client(handler, tokens);
        _ = await client.GetDocumentAsync("brokers/b1");
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, tokens.ForcedRefreshes);
        Assert.Equal("new", handler.Requests[1].Authorization?.Parameter);
    }

    [Fact]
    public async Task Firestore403DoesNotRetry()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.Forbidden, """{"error":{"status":"PERMISSION_DENIED"}}"""));
        var error = await Assert.ThrowsAsync<FirestoreRestException>(() =>
            Client(handler).GetDocumentAsync("brokers/b1"));
        Assert.Equal(FirestoreFailureKind.PermissionDenied, error.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Firestore412RaisesOptimisticConcurrencyConflictWithoutOverwrite()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.PreconditionFailed, """{"error":{"status":"FAILED_PRECONDITION"}}"""));
        var error = await Assert.ThrowsAsync<FirestoreConcurrencyException>(() =>
            Client(handler).UpdateDocumentAsync(
                "settings/commissions",
                new Dictionary<string, FirestoreRestValue> { ["x"] = FirestoreRestValue.String("changed") },
                "2026-08-10T00:00:00Z"));
        Assert.Contains("otro usuario", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Firestore400FailedPreconditionIsAlsoMappedToOptimisticConcurrencyConflict()
    {
        var handler = new QueueHandler(Json(
            HttpStatusCode.BadRequest,
            """{"error":{"status":"FAILED_PRECONDITION"}}"""));

        var error = await Assert.ThrowsAsync<FirestoreConcurrencyException>(() =>
            Client(handler).UpdateDocumentAsync(
                "sessions/current",
                new Dictionary<string, FirestoreRestValue> { ["subject"] = FirestoreRestValue.String("changed") },
                "stale-update-time"));

        Assert.Equal("sessions/current", error.DocumentPath);
        Assert.Equal("stale-update-time", error.ExpectedUpdateTime);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FirestoreTimeoutIsDistinctFromCallerCancellation()
    {
        var timeoutHandler = new DelegateHandler((_, _) => throw new TaskCanceledException());
        var timeout = await Assert.ThrowsAsync<FirestoreRestException>(() =>
            Client(timeoutHandler).GetDocumentAsync("brokers/b1"));
        Assert.Equal(FirestoreFailureKind.Timeout, timeout.Kind);

        var cancellationHandler = new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return DocumentResponse("brokers/b1");
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(cancellationHandler).GetDocumentAsync("brokers/b1", cancellation.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FirestoreRetriesTransientReadButNotIndefinitely(HttpStatusCode status)
    {
        var handler = new QueueHandler(
            Json(status, """{"error":{"status":"TRANSIENT"}}"""),
            DocumentResponse("brokers/b1"));
        var client = Client(handler, delay: (_, _) => Task.CompletedTask);
        Assert.NotNull(await client.GetDocumentAsync("brokers/b1"));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void RestValueSerializationSupportsAllRequiredTypesAndCanonicalFinancialDecimals()
    {
        var timestamp = new DateTimeOffset(2026, 8, 10, 1, 2, 3, TimeSpan.Zero).AddTicks(9);
        var fields = new Dictionary<string, FirestoreRestValue>
        {
            ["string"] = FirestoreRestValue.String("text"),
            ["bool"] = FirestoreRestValue.Boolean(true),
            ["integer"] = FirestoreRestValue.Integer(42),
            ["null"] = FirestoreRestValue.Null(),
            ["timestamp"] = FirestoreRestValue.Timestamp(timestamp),
            ["array"] = FirestoreRestValue.Array([FirestoreRestValue.String("a")]),
            ["map"] = FirestoreRestValue.Map(new Dictionary<string, FirestoreRestValue> { ["nested"] = FirestoreRestValue.Boolean(false) }),
            ["amount"] = FirestoreRestValue.Decimal(615524.44m)
        };
        var json = FirestoreRestJson.SerializeFields(fields);
        var document = JsonSerializer.Deserialize<FirestoreRestDocument>(json, FirestoreRestJson.Options)!;
        Assert.Equal("text", document.Fields["string"].RequireString("string"));
        Assert.True(document.Fields["bool"].RequireBoolean("bool"));
        Assert.Equal(42, document.Fields["integer"].RequireInteger("integer"));
        Assert.Equal(615524.44m, document.Fields["amount"].RequireDecimal("amount"));
        Assert.Equal(0, document.Fields["timestamp"].RequireTimestamp("timestamp").Ticks % 10);
    }

    [Fact]
    public void SharedMappersNeverSendLocalOnlyPaths()
    {
        const string path = @"C:\private\local.xlsx";
        var configuration = new AppConfiguration { SignatureImagePath = path };
        var session = new CurrentSession
        {
            GeneralWorkbookPath = path,
            GeneratedOutputDirectory = @"C:\private\output",
            BrokerItems = [new BrokerSendItem
            {
                BrokerId = Guid.NewGuid(),
                AttachmentPaths = [path],
                GeneratedAttachmentPaths = [path]
            }]
        };
        var generation = new PaymentGenerationBatch
        {
            Id = Guid.NewGuid(),
            SourceWorkbookPath = path,
            OutputDirectory = @"C:\private\output",
            Files = [new GeneratedPaymentFile
            {
                BrokerId = Guid.NewGuid(),
                WorksheetName = "A",
                Sha256 = "hash",
                OutputPath = path,
                Crc = new PaymentCurrencyCalculation { Currency = DeductionCurrency.CRC },
                Usd = new PaymentCurrencyCalculation { Currency = DeductionCurrency.USD }
            }]
        };

        var payloads = new[]
        {
            FirestoreRestJson.SerializeFields(new FirestoreSettingsMapper().ToFields(configuration)),
            FirestoreRestJson.SerializeFields(new FirestoreCurrentSessionMapper().ToFields(session)),
            FirestoreRestJson.SerializeFields(new FirestoreBrokerSendItemMapper().ToFields(session.BrokerItems[0])),
            FirestoreRestJson.SerializeFields(new FirestorePaymentGenerationMapper().ToFields(generation)),
            FirestoreRestJson.SerializeFields(new FirestorePaymentGenerationFileMapper().ToFields(
                new FirestoreGenerationFile(RuntimeDeterministicDocumentIds.ForGenerationFile(generation.Id, generation.Files[0]), generation.Files[0])))
        };
        Assert.All(payloads, payload => Assert.DoesNotContain(path, payload, StringComparison.OrdinalIgnoreCase));
        Assert.All(payloads, payload => Assert.DoesNotContain("outputPath", payload, StringComparison.Ordinal));
        Assert.All(payloads, payload => Assert.DoesNotContain("signatureImagePath", payload, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuntimeModeDefaultsToProductionAndHasCompleteAuthenticatedConfiguration()
    {
        var options = new FirebaseClientOptions();

        Assert.Equal(FirebaseClientOptions.ProductionProjectId, options.ProjectId);
        Assert.Equal(FirebaseClientOptions.DefaultDatabaseId, options.DatabaseId);
        Assert.Equal(RuntimeDataMode.FirestorePrimary, options.RuntimeDataMode);
        Assert.False(string.IsNullOrWhiteSpace(options.FirebaseApiKey));
        options.ValidateForAuthenticatedMode();
    }

    [Fact]
    public void FinancialSnapshotRoundTripThroughRestMapperPreservesExactBusinessResults()
    {
        var original = new GeneratedPaymentFile
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Broker",
            WorksheetName = "Sheet",
            Sha256 = "abc",
            AnalyzerName = "standard",
            GeneratedAt = DateTimeOffset.UtcNow,
            Crc = Currency(DeductionCurrency.CRC, 214336.00m, 615524.44m),
            Usd = Currency(DeductionCurrency.USD, 3589.15m, 15000m)
        };
        var mapper = new FirestorePaymentGenerationFileMapper();
        var mapped = mapper.FromFields(mapper.ToFields(new FirestoreGenerationFile(Guid.NewGuid(), original))).Value;
        Assert.Equal(original.Crc.GrossCommissionOriginal, mapped.Crc.GrossCommissionOriginal);
        Assert.Equal(original.Crc.DepositedAmount, mapped.Crc.DepositedAmount);
        Assert.Equal(original.Usd.GrossCommissionOriginal, mapped.Usd.GrossCommissionOriginal);
        Assert.Equal(original.Usd.DepositedAmount, mapped.Usd.DepositedAmount);
        Assert.Equal(original.Crc.Warnings, mapped.Crc.Warnings);
        Assert.Equal(original.Crc.Observation, mapped.Crc.Observation);
    }

    [Fact]
    public void PcWithoutLocalGenerationPathReadsMetadataAndGetsControlledAvailabilityError()
    {
        var brokerId = Guid.NewGuid();
        var batch = new PaymentGenerationBatch
        {
            Files = [new GeneratedPaymentFile
            {
                BrokerId = brokerId,
                WorksheetName = "Detalle",
                OutputPath = string.Empty,
                Sha256 = "shared-hash"
            }]
        };
        var directory = Path.Combine(Path.GetTempPath(), $"ecs-no-local-path-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppDataPaths(directory);
            var errors = new GenerationHistoryService(paths, new FileLogger(paths))
                .ValidateGeneratedAttachments(batch, brokerId, []);
            Assert.Contains(errors, value => value.Contains("no está disponible en este equipo", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static PaymentCurrencyCalculation Currency(DeductionCurrency currency, decimal gross, decimal deposited) => new()
    {
        Currency = currency,
        HasCommission = true,
        GrossCommissionOriginal = gross,
        AdjustedGrossCommission = gross,
        InvoiceAmount = gross,
        DepositedAmount = deposited,
        Observation = "snapshot exacto",
        Warnings = ["warning"]
    };

    private static AppUser User(
        AppUserRole role = AppUserRole.Operator,
        bool isActive = true,
        bool canUseCommissions = true) => new()
    {
        Uid = "uid",
        Email = "u@example.test",
        DisplayName = "User",
        Role = role,
        IsActive = isActive,
        CanUseCommissions = canUseCommissions,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    private static FirebaseAuthenticationService Auth(HttpMessageHandler handler, IRefreshTokenStore store) =>
        new(new HttpClient(handler), Options(), store);

    private static FirestoreRestClient Client(
        HttpMessageHandler handler,
        IFirebaseTokenProvider? tokens = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(new HttpClient(handler), Options(), tokens ?? new FakeTokenProvider("token"), delay: delay);

    private static FirebaseClientOptions Options() => new()
    {
        ProjectId = "demo-project",
        DatabaseId = "(default)",
        FirebaseApiKey = "test-api-key",
        RuntimeDataMode = RuntimeDataMode.FirestoreShadowRead
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage DocumentResponse(string path) => Json(HttpStatusCode.OK,
        JsonSerializer.Serialize(new
        {
            name = $"projects/demo-project/databases/(default)/documents/{path}",
            fields = new { name = new { stringValue = "test" } },
            createTime = "2026-08-10T00:00:00Z",
            updateTime = "2026-08-10T00:00:00Z"
        }));

    private sealed class MemoryTokenStore : IRefreshTokenStore
    {
        private string? _value;
        public List<string> SavedValues { get; } = [];
        public Task SaveAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            _value = refreshToken;
            SavedValues.Add(refreshToken);
            return Task.CompletedTask;
        }
        public Task<string?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_value);
        public Task ClearAsync(CancellationToken cancellationToken = default) { _value = null; return Task.CompletedTask; }
    }

    private sealed class FakeTokenProvider(string token, string? refreshed = null) : IFirebaseTokenProvider
    {
        public int ForcedRefreshes { get; private set; }
        public FirebaseUserSession? CurrentSession => null;
        public Task<string> GetIdTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            if (forceRefresh) ForcedRefreshes++;
            return Task.FromResult(forceRefresh ? refreshed ?? token : token);
        }
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<System.Net.Http.Headers.HttpRequestHeaders> Requests { get; } = [];
        public List<HttpMethod> RequestMethods { get; } = [];
        public List<Uri?> RequestUris { get; } = [];
        public List<string?> RequestBodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneHeaders(request.Headers));
            RequestMethods.Add(request.Method);
            RequestUris.Add(request.RequestUri);
            RequestBodies.Add(request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.Dequeue();
        }
        private static System.Net.Http.Headers.HttpRequestHeaders CloneHeaders(System.Net.Http.Headers.HttpRequestHeaders source)
        {
            var clone = new HttpRequestMessage().Headers;
            foreach (var header in source) clone.TryAddWithoutValidation(header.Key, header.Value);
            return clone;
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
