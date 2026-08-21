using System.Collections.ObjectModel;
using ECS.CommissionsMailer.Infrastructure.Firestore.Models;
using ECS.CommissionsMailer.Models;
using ECSCommissionsMailer.FirestoreMigration;
using Grpc.Core;

namespace ECS.CommissionsMailer.Tests;

public sealed class FirestoreMigrationTests
{
    [Theory]
    [InlineData(
        "projects/essential-4eccd/databases/(default)/documents/brokers/ABC",
        "brokers/ABC")]
    [InlineData("brokers/ABC", "brokers/ABC")]
    [InlineData(
        "projects/test-project/databases/test-db/documents/sessions/current",
        "sessions/current")]
    public void FirestoreDocumentPathsUseCanonicalRelativeIdentity(string input, string expected)
    {
        Assert.Equal(expected, FirestoreDocumentPath.Normalize(input));
    }

    [Fact]
    public void FirestoreDocumentPathNormalizationPreservesIdsAndNormalizesSlashes()
    {
        const string input =
            @"\projects\test-project\databases\(default)\documents\brokers\Ab C%2F\";

        Assert.Equal("brokers/Ab C%2F", FirestoreDocumentPath.Normalize(input));
    }

    [Fact]
    public void FullResourceNameMatchesTheSameRelativeExpectedDocument()
    {
        var expected = PlannedBroker("brokers/ABC");
        var preflight = MigrationPreflightAnalyzer.Analyze(
            [expected],
            [ExistingWithFullResourceName(expected)]);

        Assert.Empty(preflight.ToCreate);
        Assert.Empty(preflight.Conflicts);
        Assert.Equal(["brokers/ABC"], preflight.Identical);
    }

    [Fact]
    public void DifferentContentAtTheSameCanonicalPathRemainsDifferent()
    {
        var expected = PlannedBroker("brokers/ABC");
        var actual = expected.ExpectedFields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        actual["name"] = "contenido diferente";

        var preflight = MigrationPreflightAnalyzer.Analyze(
            [expected],
            [new ExistingFirestoreDocument(FullResourceName(expected.Path), actual)]);

        var conflict = Assert.Single(preflight.Conflicts);
        Assert.Equal("brokers/ABC", conflict.Path);
        Assert.Equal("DIFFERENT", conflict.State);
        Assert.Empty(preflight.ToCreate);
    }

    [Fact]
    public void TrulyMissingExpectedDocumentRemainsMissing()
    {
        var preflight = MigrationPreflightAnalyzer.Analyze(
            [PlannedBroker("brokers/ABC")],
            Array.Empty<ExistingFirestoreDocument>());

        Assert.Equal(["brokers/ABC"], preflight.ToCreate);
        Assert.Empty(preflight.Conflicts);
        var mismatch = Assert.Single(MigrationPreflightAnalyzer.ToVerificationMismatches(preflight));
        Assert.Equal("brokers/ABC", mismatch.Path);
        Assert.Equal("MISSING", mismatch.State);
    }

    [Fact]
    public void TrulyExtraActualDocumentRemainsExtra()
    {
        var preflight = MigrationPreflightAnalyzer.Analyze(
            Array.Empty<PlannedFirestoreDocument>(),
            [new ExistingFirestoreDocument(
                FullResourceName("brokers/EXTRA"),
                new Dictionary<string, object?>())]);

        var conflict = Assert.Single(preflight.Conflicts);
        Assert.Equal("brokers/EXTRA", conflict.Path);
        Assert.Equal("EXTRA", conflict.State);
        Assert.Empty(preflight.ToCreate);
    }

    [Fact]
    public void FirestoreCountsAcceptFullResourceNames()
    {
        var existing = OperationalPlan1365().Select(ExistingWithFullResourceName).ToList();

        var counts = FirestoreMigrationRunner.CountFirestore(existing);

        Assert.Equal(1, counts.Settings);
        Assert.Equal(67, counts.Brokers);
        Assert.Equal(1, counts.Session);
        Assert.Equal(67, counts.SessionBrokerItems);
        Assert.Equal(0, counts.RecentSends);
        Assert.Equal(18, counts.PaymentGenerations);
        Assert.Equal(1211, counts.PaymentGenerationFiles);
    }

    [Fact]
    public void FullResourceNamesAreIdempotentForAll1365ExistingDocuments()
    {
        var planned = OperationalPlan1365();
        var existing = planned.Select(ExistingWithFullResourceName).ToList();

        var preflight = MigrationPreflightAnalyzer.Analyze(planned, existing);

        Assert.Empty(preflight.ToCreate);
        Assert.Equal(1365, preflight.Identical.Count);
        Assert.Empty(preflight.Conflicts);
    }

    [Fact]
    public void GenericVerificationFailureDoesNotSuggestAdc()
    {
        var exception = new InvalidOperationException("La verificación encontró mismatches.");

        Assert.False(FirestoreAuthenticationFailureDetector.IsAuthenticationFailure(exception));
    }

    [Fact]
    public void UnauthenticatedRpcFailureIsRecognizedAsAuthenticationFailure()
    {
        var exception = new RpcException(new Status(StatusCode.Unauthenticated, "invalid credentials"));

        Assert.True(FirestoreAuthenticationFailureDetector.IsAuthenticationFailure(exception));
    }

    [Fact]
    public void RecentSendIdIsDeterministicAndExcludesLocalPaths()
    {
        var send = CompleteSend();
        var first = DeterministicDocumentIds.ForRecentSend(send);
        send.ArchivedAttachmentPaths = [@"D:\otra-pc\archivo.xlsx"];
        var second = DeterministicDocumentIds.ForRecentSend(send);

        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void GenerationFileIdIsDeterministicAndExcludesOutputPath()
    {
        var generationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var file = CompleteFile();
        var first = DeterministicDocumentIds.ForGenerationFile(generationId, file);
        file.OutputPath = @"D:\otra-pc\detalle.xlsx";
        var second = DeterministicDocumentIds.ForGenerationFile(generationId, file);

        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal(first, second);
        Assert.NotEqual(first, DeterministicDocumentIds.ForGenerationFile(Guid.NewGuid(), file));
    }

    [Fact]
    public void LocalOnlyFieldGuardRejectsEveryProtectedPathName()
    {
        foreach (var field in MigrationConstants.ForbiddenFirestoreFields)
        {
            var value = new Dictionary<string, object?> { [field] = @"C:\private\value" };
            Assert.Throws<InvalidDataException>(() => LocalOnlyFieldGuard.ThrowIfForbiddenFieldsExist(value));
        }
    }

    [Fact]
    public void EmptyRecentSendsProduceNoPlaceholder()
    {
        var plan = MigrationPlanBuilder.Build(ValidSource());

        Assert.Empty(plan.Errors);
        Assert.Equal(0, plan.Counts.RecentSends);
        Assert.DoesNotContain(plan.Documents, value => value.Path.StartsWith("recentSends/", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingRecentSendIdUsesTheCanonicalDeterministicId()
    {
        var send = CompleteSend();
        var expected = DeterministicDocumentIds.ForRecentSend(send);
        var plan = MigrationPlanBuilder.Build(WithRecentSend(send, hadStableId: false));
        var document = Assert.Single(plan.Documents, value => value.Category == MigrationDocumentCategory.RecentSend);

        Assert.Empty(plan.Errors);
        Assert.EndsWith(expected.ToString("D"), document.Path, StringComparison.Ordinal);
        Assert.Equal(expected, Assert.IsType<FirestoreRecentSendDocument>(document.Value).Id);
    }

    [Fact]
    public void ExistingRecentSendIdIsPreservedExactly()
    {
        var send = CompleteSend();
        send.Id = Guid.Parse("70000000-0000-0000-0000-000000000001");
        var plan = MigrationPlanBuilder.Build(WithRecentSend(send, hadStableId: true));
        var document = Assert.Single(plan.Documents, value => value.Category == MigrationDocumentCategory.RecentSend);

        Assert.EndsWith(send.Id.ToString("D"), document.Path, StringComparison.Ordinal);
        Assert.Equal(send.Id, Assert.IsType<FirestoreRecentSendDocument>(document.Value).Id);
    }

    [Fact]
    public void NullSeedKeyAndReviewNoteRemainNull()
    {
        var source = ValidSource();
        source.Configuration.Brokers[0].SeedKey = null;
        source.Configuration.Brokers[0].ReviewNote = null;

        var plan = MigrationPlanBuilder.Build(source);
        var broker = Assert.IsType<FirestoreBrokerDocument>(
            Assert.Single(plan.Documents, value => value.Category == MigrationDocumentCategory.Broker).Value);

        Assert.Null(broker.SeedKey);
        Assert.Null(broker.ReviewNote);
    }

    [Fact]
    public void DuplicateBrokerNamesWithDifferentIdsArePreserved()
    {
        var source = ValidSource();
        source.Configuration.Brokers.Add(new Broker
        {
            Id = Guid.Parse("20000000-0000-0000-0000-000000000002"),
            Name = source.Configuration.Brokers[0].Name
        });

        var plan = MigrationPlanBuilder.Build(source);

        Assert.Empty(plan.Errors);
        Assert.Equal(2, plan.Counts.Brokers);
        Assert.Equal(2, plan.Documents.Where(value => value.Category == MigrationDocumentCategory.Broker)
            .Select(value => value.Path).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AssistantsAndDeductionsAreCopiedWithoutRecalculation()
    {
        var plan = MigrationPlanBuilder.Build(ValidSource());
        var broker = Assert.IsType<FirestoreBrokerDocument>(
            Assert.Single(plan.Documents, value => value.Category == MigrationDocumentCategory.Broker).Value);

        Assert.Equal("assistant@example.com", Assert.Single(broker.Assistants).Email);
        var deduction = Assert.Single(broker.Deductions);
        Assert.Equal(615524.44m, deduction.Amount);
        Assert.Equal("Hoja inusual", deduction.TargetWorksheetName);
    }

    [Fact]
    public void CompleteSnapshotsKeepTimestampsWarningsErrorsAndDecimals()
    {
        var source = ValidSource(includeGeneration: true);
        var plan = MigrationPlanBuilder.Build(source);
        var file = Assert.IsType<FirestorePaymentGenerationFileDocument>(
            Assert.Single(plan.Documents, value => value.Category == MigrationDocumentCategory.PaymentGenerationFile).Value);

        Assert.Empty(plan.Errors);
        Assert.Equal(source.PaymentGenerations[0].Files[0].GeneratedAt.ToUniversalTime(), file.GeneratedAtUtc);
        Assert.Equal(["warning histórico"], file.Crc.Warnings);
        Assert.Equal(["error histórico"], file.Crc.Errors);
        Assert.Equal(214336.00m, file.Crc.GrossCommissionOriginal);
        Assert.Equal(3589.15m, file.Usd.GrossCommissionOriginal);
        Assert.Empty(file.Usd.Deductions);
    }

    [Fact]
    public void MissingSessionBrokerReferenceIsAnError()
    {
        var source = ValidSource();
        source.Session.BrokerItems[0].BrokerId = Guid.NewGuid();

        var plan = MigrationPlanBuilder.Build(source);

        Assert.Contains(plan.Errors, value => value.Contains("BrokerItem", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingActiveGenerationReferenceIsAnError()
    {
        var source = ValidSource();
        source.Session.ActivePaymentGenerationId = Guid.NewGuid();

        var plan = MigrationPlanBuilder.Build(source);

        Assert.Contains(plan.Errors, value => value.Contains("ActivePaymentGenerationId", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingGenerationBrokerReferenceIsAnError()
    {
        var source = ValidSource(includeGeneration: true);
        source.PaymentGenerations[0].Files[0].BrokerId = Guid.NewGuid();

        var plan = MigrationPlanBuilder.Build(source);

        Assert.Contains(plan.Errors, value => value.Contains("Broker inexistente", StringComparison.Ordinal));
    }

    [Fact]
    public void DifferentExistingFirestoreDocumentIsAConflict()
    {
        var plan = MigrationPlanBuilder.Build(ValidSource());
        var broker = Assert.Single(plan.Documents, value => value.Category == MigrationDocumentCategory.Broker);
        var actual = broker.ExpectedFields.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
        actual["name"] = "contenido diferente";

        var preflight = MigrationPreflightAnalyzer.Analyze(
            plan.Documents,
            [new ExistingFirestoreDocument(broker.Path, actual)]);

        Assert.Single(preflight.Conflicts, value => value.Path == broker.Path && value.State == "DIFFERENT");
    }

    [Fact]
    public void IdenticalRerunCreatesNothingAndReportsEverythingIdentical()
    {
        var plan = MigrationPlanBuilder.Build(ValidSource(includeGeneration: true));
        var existing = plan.Documents.Select(value =>
            new ExistingFirestoreDocument(value.Path, value.ExpectedFields)).ToList();

        var preflight = MigrationPreflightAnalyzer.Analyze(plan.Documents, existing);

        Assert.Empty(preflight.ToCreate);
        Assert.Empty(preflight.Conflicts);
        Assert.Equal(plan.Documents.Count, preflight.Identical.Count);
    }

    [Fact]
    public void ExtraFirestoreDocumentIsAConflictAndNeverDeleted()
    {
        var plan = MigrationPlanBuilder.Build(ValidSource());
        var preflight = MigrationPreflightAnalyzer.Analyze(
            plan.Documents,
            [new ExistingFirestoreDocument("brokers/extra", new Dictionary<string, object?>())]);

        Assert.Single(preflight.Conflicts, value => value.State == "EXTRA");
    }

    private static MigrationSource ValidSource(bool includeGeneration = false)
    {
        var brokerId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var generations = includeGeneration
            ? new List<PaymentGenerationBatch>
            {
                new()
                {
                    Id = Guid.Parse("30000000-0000-0000-0000-000000000001"),
                    Period = "2026-08",
                    CreatedAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.FromHours(-6)),
                    SourceWorkbookPath = @"C:\private\source.xlsx",
                    SourceWorkbookSha256 = "SOURCE",
                    OutputDirectory = @"C:\private\output",
                    Status = PaymentGenerationStatus.PartialSend,
                    Warnings = ["warning lote"],
                    Files = [CompleteFile(brokerId)]
                }
            }
            : [];

        return new MigrationSource
        {
            Directory = @"C:\source",
            Files = [],
            Configuration = new AppConfiguration
            {
                SignatureImagePath = @"C:\private\signature.png",
                Brokers =
                [
                    new Broker
                    {
                        Id = brokerId,
                        SeedKey = "seed",
                        Name = "Nombre duplicable",
                        PrimaryEmailAddresses = ["broker@example.com"],
                        Assistants =
                        [
                            new BrokerAssistant
                            {
                                Id = Guid.Parse("40000000-0000-0000-0000-000000000001"),
                                Name = "Assistant",
                                Email = "assistant@example.com",
                                IsActive = true
                            }
                        ],
                        AssociatedWorksheetNames = ["Hoja inusual"],
                        Deductions =
                        [
                            new BrokerDeduction
                            {
                                Id = Guid.Parse("50000000-0000-0000-0000-000000000001"),
                                Description = "Snapshot config",
                                Amount = 615524.44m,
                                Currency = DeductionCurrency.CRC,
                                ApplicationType = DeductionApplicationType.PayableAmount,
                                TargetWorksheetName = "Hoja inusual",
                                DisplayOrder = 8
                            }
                        ]
                    }
                ]
            },
            Session = new CurrentSession
            {
                Subject = "Subject",
                Message = "Message",
                CommonCcText = string.Empty,
                GeneralWorkbookPath = @"C:\private\source.xlsx",
                GeneratedOutputDirectory = @"C:\private\output",
                ActivePaymentGenerationId = includeGeneration ? generations[0].Id : null,
                SavedAt = new DateTimeOffset(2026, 8, 1, 13, 0, 0, TimeSpan.FromHours(-6)),
                BrokerItems =
                [
                    new BrokerSendItem
                    {
                        BrokerId = brokerId,
                        BrokerName = "Nombre duplicable",
                        PrimaryRecipients = ["broker@example.com"],
                        Assistants = [],
                        AttachmentPaths = new ObservableCollection<string>([@"C:\private\manual.xlsx"]),
                        GeneratedAttachmentPaths = new ObservableCollection<string>()
                    }
                ]
            },
            RecentSends = [],
            PaymentGenerations = generations
        };
    }

    private static SentEmailRecord CompleteSend() => new()
    {
        Id = Guid.Empty,
        BrokerId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
        BrokerName = "Broker",
        BrokerPrimaryRecipients = ["broker@example.com"],
        AssistantRecipients = ["assistant@example.com"],
        ToRecipients = ["broker@example.com", "assistant@example.com"],
        CcRecipients = [],
        Subject = "Subject",
        Body = "Body",
        SentAt = new DateTimeOffset(2026, 8, 1, 13, 0, 0, TimeSpan.FromHours(-6)),
        ArchivedAttachmentPaths = [@"C:\private\file.xlsx"],
        WasSuccessful = true
    };

    private static MigrationSource WithRecentSend(SentEmailRecord send, bool hadStableId)
    {
        var source = ValidSource();
        return new MigrationSource
        {
            Directory = source.Directory,
            Files = source.Files,
            Configuration = source.Configuration,
            Session = source.Session,
            PaymentGenerations = source.PaymentGenerations,
            RecentSends = [new LoadedRecentSend(send, hadStableId)]
        };
    }

    private static GeneratedPaymentFile CompleteFile(Guid? brokerId = null) => new()
    {
        BrokerId = brokerId ?? Guid.Parse("20000000-0000-0000-0000-000000000001"),
        BrokerName = "Broker",
        WorksheetName = "Hoja inusual",
        OutputPath = @"C:\private\detail.xlsx",
        Sha256 = "FILE-SHA",
        AnalyzerName = "Analyzer",
        GeneratedAt = new DateTimeOffset(2026, 8, 1, 12, 30, 0, TimeSpan.FromHours(-6)),
        Crc = Calculation(DeductionCurrency.CRC, 214336.00m, withSnapshot: true),
        Usd = Calculation(DeductionCurrency.USD, 3589.15m, withSnapshot: false)
        };

    private static PlannedFirestoreDocument PlannedBroker(string path) =>
        PlannedFirestoreDocument.Create(
            path,
            MigrationDocumentCategory.Broker,
            new FirestoreBrokerDocument
            {
                Id = Guid.Parse("20000000-0000-0000-0000-000000000001"),
                Name = "Broker"
            });

    private static ExistingFirestoreDocument ExistingWithFullResourceName(PlannedFirestoreDocument planned) =>
        new(FullResourceName(planned.Path), planned.ExpectedFields);

    private static List<PlannedFirestoreDocument> OperationalPlan1365()
    {
        var documents = new List<PlannedFirestoreDocument>
        {
            Planned("settings/commissions", MigrationDocumentCategory.Settings),
            Planned("sessions/current", MigrationDocumentCategory.Session)
        };
        documents.AddRange(Enumerable.Range(0, 67)
            .Select(index => Planned($"brokers/{index:D2}", MigrationDocumentCategory.Broker)));
        documents.AddRange(Enumerable.Range(0, 67)
            .Select(index => Planned(
                $"sessions/current/brokerItems/{index:D2}",
                MigrationDocumentCategory.SessionBrokerItem)));
        documents.AddRange(Enumerable.Range(0, 18)
            .Select(index => Planned(
                $"paymentGenerations/{index:D2}",
                MigrationDocumentCategory.PaymentGeneration)));
        documents.AddRange(Enumerable.Range(0, 1211)
            .Select(index => Planned(
                $"paymentGenerations/{index % 18:D2}/files/{index:D4}",
                MigrationDocumentCategory.PaymentGenerationFile)));
        return documents;
    }

    private static PlannedFirestoreDocument Planned(string path, MigrationDocumentCategory category) =>
        PlannedFirestoreDocument.Create(
            path,
            category,
            new FirestoreBrokerDocument
            {
                Id = Guid.Parse("20000000-0000-0000-0000-000000000001"),
                Name = path
            });

    private static string FullResourceName(string relativePath) =>
        $"projects/test-project/databases/(default)/documents/{relativePath}";

    private static PaymentCurrencyCalculation Calculation(
        DeductionCurrency currency,
        decimal gross,
        bool withSnapshot) => new()
        {
            Currency = currency,
            HasCommission = true,
            MinimumApplied = true,
            MinimumAmount = 15000m,
            GrossCommissionOriginal = gross,
            GrossDeductions = 10m,
            AdjustedGrossCommission = gross - 10m,
            Vat = 1m,
            InvoiceAmount = gross + 1m,
            Withholding = 2m,
            PayableBeforeFinalDeductions = gross - 2m,
            FinalDeductions = 3m,
            DepositedAmount = gross - 5m,
            Observation = "snapshot",
            Warnings = currency == DeductionCurrency.CRC ? ["warning histórico"] : [],
            Errors = currency == DeductionCurrency.CRC ? ["error histórico"] : [],
            Deductions = withSnapshot
                ?
                [
                    new AppliedDeductionSnapshot
                    {
                        Id = Guid.Parse("60000000-0000-0000-0000-000000000001"),
                        Description = "applied",
                        ConfiguredAmount = 3m,
                        AppliedAmount = 3m,
                        Currency = currency,
                        ApplicationType = DeductionApplicationType.PayableAmount,
                        TargetWorksheetName = "Hoja inusual",
                        DisplayOrder = 1
                    }
                ]
                : []
        };
}
