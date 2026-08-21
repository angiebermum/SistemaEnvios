using System.Globalization;
using ECS.CommissionsMailer.Infrastructure.Firestore;
using ECS.CommissionsMailer.Infrastructure.Firestore.Converters;
using ECS.CommissionsMailer.Infrastructure.Firestore.Mapping;
using ECS.CommissionsMailer.Infrastructure.Firestore.Models;
using ECS.CommissionsMailer.Models;
using ECSCommissionsMailer.FirestoreMigration;
using Google.Cloud.Firestore;

namespace ECS.CommissionsMailer.Tests;

public sealed class FirestoreTimestampPrecisionTests
{
    [Theory]
    [InlineData("2026-08-08T00:48:13.5757891+00:00", "2026-08-08T00:48:13.5757890+00:00")]
    [InlineData("2026-08-07T13:57:45.9033768+00:00", "2026-08-07T13:57:45.9033760+00:00")]
    [InlineData("2026-08-08T00:48:13.5757890+00:00", "2026-08-08T00:48:13.5757890+00:00")]
    public void NormalizeTruncatesToMicrosecondsWithoutRounding(string input, string expected)
    {
        Assert.Equal(Parse(expected), FirestoreTimestampPrecision.Normalize(Parse(input)));
    }

    [Fact]
    public void NormalizeDateTimeReturnsExplicitUtcAtMicrosecondPrecision()
    {
        var input = new DateTime(2026, 8, 8, 0, 48, 13, DateTimeKind.Utc).AddTicks(5_757_891);

        var normalized = FirestoreTimestampPrecision.Normalize(input);

        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        Assert.Equal(new DateTime(2026, 8, 8, 0, 48, 13, DateTimeKind.Utc).AddTicks(5_757_890), normalized);
    }

    [Fact]
    public void NormalizeDateTimeOffsetConvertsTheSameInstantToUtcBeforeTruncating()
    {
        var input = new DateTimeOffset(2026, 8, 7, 7, 57, 45, TimeSpan.FromHours(-6)).AddTicks(9_033_768);

        var normalized = FirestoreTimestampPrecision.Normalize(input);

        Assert.Equal(Parse("2026-08-07T13:57:45.9033760+00:00"), normalized);
        Assert.Equal(TimeSpan.Zero, normalized.Offset);
    }

    [Fact]
    public void NormalizePreservesSecondsMillisecondsAndValidMicrosecondsExactly()
    {
        var input = Parse("2026-08-08T00:48:13.5757890+00:00");

        var normalized = FirestoreTimestampPrecision.Normalize(input);

        Assert.Equal(input, normalized);
        Assert.Equal(input.Second, normalized.Second);
        Assert.Equal(input.Millisecond, normalized.Millisecond);
        Assert.Equal(0, normalized.Ticks % 10);
    }

    [Theory]
    [InlineData("2026-08-08T00:48:13.5757891+00:00")]
    [InlineData("2026-08-08T00:48:13.5757899+00:00")]
    public void SemanticComparerMatchesOnlySubMicrosecondDifferences(string expectedText)
    {
        var expected = new Dictionary<string, object?> { ["timestamp"] = Parse(expectedText) };
        var actual = new Dictionary<string, object?>
        {
            ["timestamp"] = Timestamp.FromDateTimeOffset(Parse("2026-08-08T00:48:13.5757890+00:00"))
        };

        Assert.Empty(FirestoreSemanticComparer.Compare(expected, actual));
    }

    [Fact]
    public void SemanticComparerStillDetectsOneMicrosecondDifference()
    {
        var expected = new Dictionary<string, object?>
        {
            ["timestamp"] = Parse("2026-08-08T00:48:13.5757891+00:00")
        };
        var actual = new Dictionary<string, object?>
        {
            ["timestamp"] = Timestamp.FromDateTimeOffset(Parse("2026-08-08T00:48:13.5757880+00:00"))
        };

        var difference = Assert.Single(FirestoreSemanticComparer.Compare(expected, actual));

        Assert.Contains("timestamp", difference, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySharedTimestampMappingUsesCanonicalFirestorePrecision()
    {
        var input = Parse("2026-08-08T00:48:13.5757899+00:00");
        var expected = Parse("2026-08-08T00:48:13.5757890+00:00");
        var session = FirestorePersistenceMapper.Partition(new CurrentSession { SavedAt = input });
        var recentSend = FirestorePersistenceMapper.Partition(new SentEmailRecord { SentAt = input });
        var generation = FirestorePersistenceMapper.Partition(
            new PaymentGenerationBatch
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                CreatedAt = input,
                Files =
                [
                    new GeneratedPaymentFile
                    {
                        BrokerId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
                        GeneratedAt = input
                    }
                ]
            },
            _ => Guid.Parse("30000000-0000-0000-0000-000000000001"));

        Assert.Equal(expected, session.SharedSession.SavedAtUtc);
        Assert.Equal(expected, recentSend.Shared.SentAtUtc);
        Assert.Equal(expected, generation.SharedGeneration.CreatedAtUtc);
        Assert.Equal(expected, Assert.Single(generation.SharedFiles).GeneratedAtUtc);
    }

    [Fact]
    public void TimestampConverterPersistsNativeCanonicalFirestoreTimestamp()
    {
        var converter = new FirestoreTimestampPrecisionConverter();
        var input = Parse("2026-08-08T00:48:13.5757899+00:00");

        var stored = Assert.IsType<Timestamp>(converter.ToFirestore(input));
        var roundTrip = converter.FromFirestore(stored);

        Assert.Equal(Parse("2026-08-08T00:48:13.5757890+00:00"), roundTrip);
    }

    [Fact]
    public void RecentSendDeterministicIdUsesCanonicalFirestoreTimestampPrecision()
    {
        var canonical = Parse("2026-08-08T00:48:13.5757890+00:00");
        var first = new SentEmailRecord { SentAt = canonical.AddTicks(1) };
        var second = new SentEmailRecord { SentAt = canonical.AddTicks(9) };

        Assert.Equal(
            DeterministicDocumentIds.ForRecentSend(first),
            DeterministicDocumentIds.ForRecentSend(second));
    }

    [Fact]
    public void Real1365DocumentDistributionWith1026SubMicrosecondValuesIsFullyIdempotent()
    {
        var planned = OperationalTimestampPlan1365();
        var existing = planned.Select(ExistingAtFirestorePrecision).ToList();

        var preflight = MigrationPreflightAnalyzer.Analyze(planned, existing);
        var mismatches = MigrationPreflightAnalyzer.ToVerificationMismatches(preflight);
        var idempotent = preflight.ToCreate.Count == 0 && preflight.Conflicts.Count == 0 &&
                         preflight.Identical.Count == planned.Count;

        Assert.Equal(1365, planned.Count);
        Assert.Equal(1026, planned.Count(HasSubMicrosecondTimestamp));
        Assert.Equal(1, planned.Count(document =>
            document.Value is FirestoreCurrentSessionDocument session && session.SavedAtUtc.Ticks % 10 != 0));
        Assert.Equal(15, planned.Count(document =>
            document.Value is FirestorePaymentGenerationDocument generation &&
            generation.CreatedAtUtc.Ticks % 10 != 0));
        Assert.Equal(1010, planned.Count(document =>
            document.Value is FirestorePaymentGenerationFileDocument file &&
            file.GeneratedAtUtc.Ticks % 10 != 0));
        Assert.Empty(preflight.ToCreate);
        Assert.Equal(1365, preflight.Identical.Count);
        Assert.Empty(preflight.Conflicts);
        Assert.Empty(mismatches);
        Assert.True(idempotent);
    }

    private static List<PlannedFirestoreDocument> OperationalTimestampPlan1365()
    {
        var canonical = Parse("2026-08-08T00:48:13.5757890+00:00");
        var documents = new List<PlannedFirestoreDocument>
        {
            PlannedWithoutTimestamp("settings/commissions", MigrationDocumentCategory.Settings),
            PlannedFirestoreDocument.Create(
                "sessions/current",
                MigrationDocumentCategory.Session,
                new FirestoreCurrentSessionDocument { SavedAtUtc = canonical.AddTicks(1) })
        };
        documents.AddRange(Enumerable.Range(0, 67)
            .Select(index => PlannedWithoutTimestamp($"brokers/{index:D2}", MigrationDocumentCategory.Broker)));
        documents.AddRange(Enumerable.Range(0, 67)
            .Select(index => PlannedWithoutTimestamp(
                $"sessions/current/brokerItems/{index:D2}",
                MigrationDocumentCategory.SessionBrokerItem)));
        documents.AddRange(Enumerable.Range(0, 18)
            .Select(index => PlannedFirestoreDocument.Create(
                $"paymentGenerations/{index:D2}",
                MigrationDocumentCategory.PaymentGeneration,
                new FirestorePaymentGenerationDocument
                {
                    CreatedAtUtc = index < 15 ? canonical.AddTicks(index % 9 + 1) : canonical
                })));
        documents.AddRange(Enumerable.Range(0, 1211)
            .Select(index => PlannedFirestoreDocument.Create(
                $"paymentGenerations/{index % 18:D2}/files/{index:D4}",
                MigrationDocumentCategory.PaymentGenerationFile,
                new FirestorePaymentGenerationFileDocument
                {
                    GeneratedAtUtc = index < 1010 ? canonical.AddTicks(index % 9 + 1) : canonical
                })));
        return documents;
    }

    private static PlannedFirestoreDocument PlannedWithoutTimestamp(
        string path,
        MigrationDocumentCategory category) =>
        PlannedFirestoreDocument.Create(path, category, new FirestoreBrokerDocument { Name = path });

    private static ExistingFirestoreDocument ExistingAtFirestorePrecision(PlannedFirestoreDocument planned)
    {
        var actual = planned.ExpectedFields.ToDictionary(
            pair => pair.Key,
            pair => pair.Value is DateTimeOffset timestamp
                ? (object?)Timestamp.FromDateTimeOffset(FirestoreTimestampPrecision.Normalize(timestamp))
                : pair.Value,
            StringComparer.Ordinal);
        return new ExistingFirestoreDocument(
            $"projects/test-project/databases/(default)/documents/{planned.Path}",
            actual);
    }

    private static bool HasSubMicrosecondTimestamp(PlannedFirestoreDocument planned) => planned.Value switch
    {
        FirestoreCurrentSessionDocument session => session.SavedAtUtc.Ticks % 10 != 0,
        FirestorePaymentGenerationDocument generation => generation.CreatedAtUtc.Ticks % 10 != 0,
        FirestorePaymentGenerationFileDocument file => file.GeneratedAtUtc.Ticks % 10 != 0,
        _ => false
    };

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
