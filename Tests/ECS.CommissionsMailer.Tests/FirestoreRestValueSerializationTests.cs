using System.Net;
using System.Text;
using System.Text.Json;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authentication;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Configuration;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Infrastructure.Firestore;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class FirestoreRestValueSerializationTests
{
    [Fact]
    public void DeserializeExplicitJsonNullProducesNullKind()
    {
        var value = DeserializeValue("""{"nullValue":null}""");

        Assert.Equal(FirestoreRestValueKind.Null, value.Kind);
        Assert.True(value.IsNull);
    }

    [Fact]
    public void SerializeNullFactoryProducesFirestoreJsonNull()
    {
        Assert.Equal("""{"nullValue":null}""", SerializeValue(FirestoreRestValue.Null()));
    }

    [Fact]
    public void SerializeNullStringProducesFirestoreJsonNull()
    {
        Assert.Equal("""{"nullValue":null}""", SerializeValue(FirestoreRestValue.String(null)));
    }

    [Fact]
    public void EmptyObjectIsInvalidAndNeverBecomesNull()
    {
        var error = Assert.Throws<JsonException>(() => DeserializeValue("{}"));

        Assert.Contains("'{}' es inválido", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueWithTwoUnionMembersIsInvalid()
    {
        var error = Assert.Throws<JsonException>(() =>
            DeserializeValue("""{"stringValue":"x","booleanValue":true}"""));

        Assert.Contains("más de un miembro", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stringValue", error.Message, StringComparison.Ordinal);
        Assert.Contains("booleanValue", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StringValueRoundTripsThroughFirestoreOptions()
    {
        var value = RoundTrip(FirestoreRestValue.String("texto"));

        Assert.Equal(FirestoreRestValueKind.String, value.Kind);
        Assert.Equal("texto", value.RequireString("field"));
    }

    [Fact]
    public void BooleanValueRoundTripsThroughFirestoreOptions()
    {
        var value = RoundTrip(FirestoreRestValue.Boolean(false));

        Assert.Equal(FirestoreRestValueKind.Boolean, value.Kind);
        Assert.False(value.RequireBoolean("field"));
    }

    [Fact]
    public void IntegerValueRoundTripsThroughFirestoreOptions()
    {
        var value = RoundTrip(FirestoreRestValue.Integer(9_223_372_036_854_775_000));

        Assert.Equal(FirestoreRestValueKind.Integer, value.Kind);
        Assert.Equal(9_223_372_036_854_775_000, value.RequireInteger("field"));
    }

    [Fact]
    public void TimestampValueRoundTripsThroughFirestoreOptions()
    {
        var timestamp = new DateTimeOffset(2026, 8, 10, 17, 30, 15, 123, TimeSpan.Zero)
            .AddTicks(4_560);

        var value = RoundTrip(FirestoreRestValue.Timestamp(timestamp));

        Assert.Equal(FirestoreRestValueKind.Timestamp, value.Kind);
        Assert.Equal(FirestoreTimestampPrecision.Normalize(timestamp), value.RequireTimestamp("field"));
    }

    [Fact]
    public void ArrayValueRoundTripsWithNestedJsonNull()
    {
        var json = SerializeValue(FirestoreRestValue.Array([
            FirestoreRestValue.String("item"),
            FirestoreRestValue.Null()
        ]));

        var value = DeserializeValue(json);

        Assert.Contains("\"nullValue\":null", json, StringComparison.Ordinal);
        Assert.Equal(FirestoreRestValueKind.Null, value.RequireArray("field")[1].Kind);
    }

    [Fact]
    public void MapValueRoundTripsWithNestedJsonNull()
    {
        var json = SerializeValue(FirestoreRestValue.Map(
            new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
            {
                ["reviewNote"] = FirestoreRestValue.Null()
            }));

        var value = DeserializeValue(json);

        Assert.Contains("\"nullValue\":null", json, StringComparison.Ordinal);
        Assert.Equal(FirestoreRestValueKind.Null, value.RequireMap("field")["reviewNote"].Kind);
    }

    [Fact]
    public void ComparisonCanonicalizesExplicitNullWithoutTolerance()
    {
        var fields = new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["reviewNote"] = DeserializeValue("""{"nullValue":null}""")
        };

        var canonical = FirestoreComparisonService.CanonicalFields(fields, "brokers/b1");

        Assert.Equal("{k10:reviewNote;null;};", canonical);
    }

    [Fact]
    public void RealisticBrokerDocumentAcceptsNullReviewNote()
    {
        const string json = """
            {
              "name":"projects/demo/databases/(default)/documents/brokers/b1",
              "fields":{
                "id":{"stringValue":"11111111-1111-1111-1111-111111111111"},
                "seedKey":{"nullValue":null},
                "name":{"stringValue":"Broker Uno"},
                "primaryEmailAddresses":{"arrayValue":{"values":[]}},
                "assistants":{"arrayValue":{"values":[]}},
                "associatedWorksheetNames":{"arrayValue":{"values":[]}},
                "deductions":{"arrayValue":{"values":[]}},
                "isActive":{"booleanValue":true},
                "requiresReview":{"booleanValue":false},
                "reviewNote":{"nullValue":null}
              }
            }
            """;

        var document = DeserializeDocument(json);
        var broker = new FirestoreBrokerMapper().FromFields(document.Fields);

        Assert.Equal(FirestoreRestValueKind.Null, document.Fields["reviewNote"].Kind);
        Assert.Null(broker.ReviewNote);
        Assert.Contains("null;", FirestoreComparisonService.CanonicalFields(
            document.Fields, "brokers/b1"), StringComparison.Ordinal);
    }

    [Fact]
    public void RealisticSessionBrokerItemAcceptsNullLastError()
    {
        const string json = """
            {
              "name":"projects/demo/databases/(default)/documents/sessions/current/brokerItems/b1",
              "fields":{
                "brokerId":{"stringValue":"11111111-1111-1111-1111-111111111111"},
                "brokerName":{"stringValue":"Broker Uno"},
                "seedKey":{"nullValue":null},
                "primaryRecipients":{"arrayValue":{"values":[]}},
                "assistants":{"arrayValue":{"values":[]}},
                "requiresReview":{"booleanValue":false},
                "reviewNote":{"stringValue":""},
                "requiresBatchReview":{"booleanValue":false},
                "batchReviewNote":{"stringValue":""},
                "isSelected":{"booleanValue":true},
                "status":{"stringValue":"Pending"},
                "lastError":{"nullValue":null}
              }
            }
            """;

        var document = DeserializeDocument(json);
        var brokerItem = new FirestoreBrokerSendItemMapper().FromFields(document.Fields);

        Assert.Equal(FirestoreRestValueKind.Null, document.Fields["lastError"].Kind);
        Assert.Equal(string.Empty, brokerItem.LastError);
        Assert.Contains("null;", FirestoreComparisonService.CanonicalFields(
            document.Fields, "sessions/current/brokerItems/b1"), StringComparison.Ordinal);
    }

    [Fact]
    public void RealisticRecentSendAcceptsNullErrorMessage()
    {
        const string json = """
            {
              "name":"projects/demo/databases/(default)/documents/recentSends/r1",
              "fields":{
                "id":{"stringValue":"22222222-2222-2222-2222-222222222222"},
                "brokerId":{"stringValue":"11111111-1111-1111-1111-111111111111"},
                "brokerName":{"stringValue":"Broker Uno"},
                "brokerPrimaryRecipients":{"arrayValue":{"values":[]}},
                "assistantRecipients":{"arrayValue":{"values":[]}},
                "toRecipients":{"arrayValue":{"values":[]}},
                "ccRecipients":{"arrayValue":{"values":[]}},
                "subject":{"stringValue":"Asunto"},
                "body":{"stringValue":"Mensaje"},
                "sentAtUtc":{"timestampValue":"2026-08-10T17:30:15.123456Z"},
                "wasSuccessful":{"booleanValue":true},
                "errorMessage":{"nullValue":null},
                "resendOfRecordId":{"nullValue":null},
                "paymentGenerationId":{"nullValue":null}
              }
            }
            """;

        var document = DeserializeDocument(json);
        var send = new FirestoreRecentSendMapper().FromFields(document.Fields);

        Assert.Equal(FirestoreRestValueKind.Null, document.Fields["errorMessage"].Kind);
        Assert.Equal(string.Empty, send.ErrorMessage);
        Assert.Contains("null;", FirestoreComparisonService.CanonicalFields(
            document.Fields, "recentSends/r1"), StringComparison.Ordinal);
    }

    [Fact]
    public void FinancialDecimalRemainsCanonicalStringValue()
    {
        var json = SerializeValue(FirestoreRestValue.Decimal(615_524.44m));
        var value = DeserializeValue(json);

        Assert.Equal("""{"stringValue":"615524.44"}""", json);
        Assert.DoesNotContain("doubleValue", json, StringComparison.Ordinal);
        Assert.Equal(615_524.44m, value.RequireDecimal("amount"));
    }

    [Fact]
    public void TimestampFactoryStillNormalizesToMicroseconds()
    {
        var timestamp = new DateTimeOffset(2026, 8, 10, 17, 30, 15, TimeSpan.Zero).AddTicks(1_239);
        var json = SerializeValue(FirestoreRestValue.Timestamp(timestamp));
        var value = DeserializeValue(json).RequireTimestamp("timestamp");

        Assert.Equal(0, value.Ticks % 10);
        Assert.Equal(FirestoreTimestampPrecision.Normalize(timestamp), value);
    }

    [Fact]
    public void UnsupportedRestValueTypeFailsExplicitly()
    {
        var error = Assert.Throws<JsonException>(() => DeserializeValue("""{"doubleValue":1.5}"""));

        Assert.Contains("tipo REST no soportado 'doubleValue'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestClientDiagnosticIncludesDocumentFieldAndUnsupportedType()
    {
        const string json = """
            {
              "name":"projects/demo-project/databases/(default)/documents/brokers/b1",
              "fields":{"amount":{"doubleValue":1.5}},
              "updateTime":"2026-08-10T17:30:15Z"
            }
            """;
        var client = new FirestoreRestClient(
            new HttpClient(new StaticJsonHandler(json)),
            Options(),
            new StaticTokenProvider());

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetDocumentAsync("brokers/b1", TestContext.Current.CancellationToken));

        Assert.Contains("brokers/b1", error.Message, StringComparison.Ordinal);
        Assert.Contains("amount", error.Message, StringComparison.Ordinal);
        Assert.Contains("doubleValue", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FirestoreRestValue DeserializeValue(string json) =>
        JsonSerializer.Deserialize<FirestoreRestValue>(json, FirestoreRestJson.Options)
        ?? throw new InvalidDataException("La prueba no obtuvo un FirestoreRestValue.");

    private static FirestoreRestDocument DeserializeDocument(string json) =>
        JsonSerializer.Deserialize<FirestoreRestDocument>(json, FirestoreRestJson.Options)
        ?? throw new InvalidDataException("La prueba no obtuvo un FirestoreRestDocument.");

    private static string SerializeValue(FirestoreRestValue value) =>
        JsonSerializer.Serialize(value, FirestoreRestJson.Options);

    private static FirestoreRestValue RoundTrip(FirestoreRestValue value) =>
        DeserializeValue(SerializeValue(value));

    private static FirebaseClientOptions Options() => new()
    {
        ProjectId = "demo-project",
        DatabaseId = "(default)",
        FirebaseApiKey = "test-api-key",
        RuntimeDataMode = RuntimeDataMode.FirestoreShadowRead
    };

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class StaticTokenProvider : IFirebaseTokenProvider
    {
        public FirebaseUserSession? CurrentSession => null;
        public Task<string> GetIdTokenAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default) => Task.FromResult("test-token");
    }
}
