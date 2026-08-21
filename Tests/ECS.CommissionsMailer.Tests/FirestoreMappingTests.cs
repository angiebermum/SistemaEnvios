using System.Collections.ObjectModel;
using ECS.CommissionsMailer.Infrastructure.Firestore;
using ECS.CommissionsMailer.Infrastructure.Firestore.Converters;
using ECS.CommissionsMailer.Infrastructure.Firestore.Mapping;
using ECS.CommissionsMailer.Infrastructure.Firestore.Models;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Tests;

public sealed class FirestoreMappingTests
{
    [Fact]
    public void BrokerRoundTripPreservesSharedModelAndStableId()
    {
        var broker = CompleteBroker();

        var document = FirestorePersistenceMapper.ToFirestore(broker);
        var roundTrip = FirestorePersistenceMapper.ToDomain(document);

        Assert.Equal(broker.Id, roundTrip.Id);
        Assert.Equal(broker.SeedKey, roundTrip.SeedKey);
        Assert.Equal(broker.Name, roundTrip.Name);
        Assert.Equal(broker.PrimaryEmailAddresses, roundTrip.PrimaryEmailAddresses);
        Assert.Equal(broker.AssociatedWorksheetNames, roundTrip.AssociatedWorksheetNames);
        Assert.Equal(broker.IsActive, roundTrip.IsActive);
        Assert.Equal(broker.RequiresReview, roundTrip.RequiresReview);
        Assert.Equal(broker.ReviewNote, roundTrip.ReviewNote);
    }

    [Fact]
    public void AssistantsRoundTripPreservesEveryFieldAndId()
    {
        var broker = CompleteBroker();

        var roundTrip = FirestorePersistenceMapper.ToDomain(
            FirestorePersistenceMapper.ToFirestore(broker));

        var assistant = Assert.Single(roundTrip.Assistants);
        Assert.Equal(broker.Assistants[0].Id, assistant.Id);
        Assert.Equal("Asistente", assistant.Name);
        Assert.Equal("assistant@example.com", assistant.Email);
        Assert.False(assistant.IsActive);
    }

    [Fact]
    public void DeductionsRoundTripPreservesEveryFieldDecimalAndId()
    {
        var broker = CompleteBroker();

        var roundTrip = FirestorePersistenceMapper.ToDomain(
            FirestorePersistenceMapper.ToFirestore(broker));

        var deduction = Assert.Single(roundTrip.Deductions);
        Assert.Equal(broker.Deductions[0].Id, deduction.Id);
        Assert.Equal("Rebajo exacto", deduction.Description);
        Assert.Equal(615524.44m, deduction.Amount);
        Assert.Equal(DeductionCurrency.CRC, deduction.Currency);
        Assert.Equal(DeductionApplicationType.PayableAmount, deduction.ApplicationType);
        Assert.Equal("SYN", deduction.TargetWorksheetName);
        Assert.Equal(7, deduction.DisplayOrder);
    }

    [Fact]
    public void ConfigurationPartitionKeepsSignatureLocalAndSettingsShared()
    {
        var configuration = new AppConfiguration
        {
            DefaultSubject = "Asunto",
            DefaultMessage = "Mensaje",
            SignatureImagePath = @"C:\Users\local\firma.png",
            DataSchemaVersion = 3,
            EmailDirectorySeedVersion = 3,
            EmailDirectorySeedId = "seed",
            CommonCcAddresses = ["cc@example.com"],
            Brokers = [CompleteBroker()]
        };

        var partition = FirestorePersistenceMapper.Partition(configuration);
        var roundTrip = FirestorePersistenceMapper.ToDomain(partition);

        Assert.Equal("Asunto", partition.SharedSettings.DefaultSubject);
        Assert.Equal("Mensaje", partition.SharedSettings.DefaultMessage);
        Assert.Single(partition.SharedBrokers);
        Assert.Equal(configuration.SignatureImagePath, partition.Local.SignatureImagePath);
        Assert.DoesNotContain(
            typeof(FirestoreCommissionsSettingsDocument).GetProperties(),
            property => property.Name.Contains("Signature", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(configuration.SignatureImagePath, roundTrip.SignatureImagePath);
    }

    [Fact]
    public void CurrentSessionPartitionKeepsFilesystemPathsLocal()
    {
        var brokerId = Guid.NewGuid();
        var generationId = Guid.NewGuid();
        var session = new CurrentSession
        {
            Subject = "Asunto compartido",
            Message = "Mensaje compartido",
            CommonCcText = "cc@example.com",
            GeneralWorkbookPath = @"C:\local\comisiones.xlsx",
            ActivePaymentGenerationId = generationId,
            GeneratedOutputDirectory = @"C:\local\salida",
            GeneratedPeriod = "2026-08",
            SavedAt = new DateTimeOffset(2026, 8, 9, 12, 30, 0, TimeSpan.FromHours(-6)),
            BrokerItems =
            [
                new BrokerSendItem
                {
                    BrokerId = brokerId,
                    BrokerName = "Corredor",
                    SeedKey = "seed",
                    PrimaryRecipients = ["broker@example.com"],
                    Assistants = [new BrokerAssistant { Id = Guid.NewGuid(), Name = "A", Email = "a@example.com" }],
                    RequiresReview = true,
                    ReviewNote = "Revisar",
                    RequiresBatchReview = true,
                    BatchReviewNote = "Revisar lote",
                    IsSelected = false,
                    Status = SendStatus.ReviewRequired,
                    LastError = "Error persistido",
                    AttachmentPaths = new ObservableCollection<string>([@"C:\local\manual.xlsx"]),
                    GeneratedAttachmentPaths = new ObservableCollection<string>([@"C:\local\generado.xlsx"])
                }
            ]
        };

        var partition = FirestorePersistenceMapper.Partition(session);
        var roundTrip = FirestorePersistenceMapper.ToDomain(partition);

        Assert.Equal(generationId, partition.SharedSession.ActivePaymentGenerationId);
        Assert.Equal(@"C:\local\comisiones.xlsx", partition.Local.GeneralWorkbookPath);
        Assert.Equal(@"C:\local\salida", partition.Local.GeneratedOutputDirectory);
        Assert.Equal([@"C:\local\manual.xlsx"], partition.LocalBrokerItems[0].AttachmentPaths);
        Assert.Equal([@"C:\local\generado.xlsx"], partition.LocalBrokerItems[0].GeneratedAttachmentPaths);
        AssertNoPathProperty(typeof(FirestoreCurrentSessionDocument));
        AssertNoPathProperty(typeof(FirestoreBrokerSendItemDocument));
        Assert.Equal(session.GeneralWorkbookPath, roundTrip.GeneralWorkbookPath);
        Assert.Equal(session.GeneratedOutputDirectory, roundTrip.GeneratedOutputDirectory);
        Assert.Equal(session.BrokerItems[0].AttachmentPaths, roundTrip.BrokerItems[0].AttachmentPaths);
        Assert.Equal(session.BrokerItems[0].GeneratedAttachmentPaths, roundTrip.BrokerItems[0].GeneratedAttachmentPaths);
        Assert.Equal("Error persistido", roundTrip.BrokerItems[0].LastError);
    }

    [Fact]
    public void GenerationPartitionKeepsPathsLocalAndResultsShared()
    {
        var generation = CompleteGeneration();
        var futureFileId = Guid.NewGuid();

        var partition = FirestorePersistenceMapper.Partition(generation, _ => futureFileId);
        var roundTrip = FirestorePersistenceMapper.ToDomain(partition);

        Assert.Equal(generation.Id, partition.SharedGeneration.Id);
        Assert.Equal(generation.SourceWorkbookPath, partition.Local.SourceWorkbookPath);
        Assert.Equal(generation.OutputDirectory, partition.Local.OutputDirectory);
        Assert.Equal(futureFileId, partition.SharedFiles[0].Id);
        Assert.Equal(generation.Files[0].OutputPath, partition.LocalFiles[0].OutputPath);
        AssertNoPathProperty(typeof(FirestorePaymentGenerationDocument));
        AssertNoPathProperty(typeof(FirestorePaymentGenerationFileDocument));
        Assert.Equal(generation.SourceWorkbookPath, roundTrip.SourceWorkbookPath);
        Assert.Equal(generation.OutputDirectory, roundTrip.OutputDirectory);
        Assert.Equal(generation.Files[0].OutputPath, roundTrip.Files[0].OutputPath);
        Assert.Equal(generation.SourceWorkbookSha256, roundTrip.SourceWorkbookSha256);
    }

    [Fact]
    public void RecentSendPartitionKeepsArchivedFilesLocalAndHistoryShared()
    {
        var send = new SentEmailRecord
        {
            Id = Guid.NewGuid(),
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            BrokerPrimaryRecipients = ["broker@example.com"],
            AssistantRecipients = ["assistant@example.com"],
            ToRecipients = ["broker@example.com", "assistant@example.com"],
            CcRecipients = ["cc@example.com"],
            Subject = "Asunto histórico",
            Body = "Mensaje histórico",
            SentAt = new DateTimeOffset(2026, 8, 9, 15, 0, 0, TimeSpan.FromHours(-6)),
            ArchivedAttachmentPaths = [@"C:\local\ArchivosEnviados\detalle.xlsx"],
            WasSuccessful = false,
            ErrorMessage = "Error histórico",
            ResendOfRecordId = Guid.NewGuid(),
            PaymentGenerationId = Guid.NewGuid()
        };

        var partition = FirestorePersistenceMapper.Partition(send);
        var roundTrip = FirestorePersistenceMapper.ToDomain(partition);

        Assert.Equal("Asunto histórico", partition.Shared.Subject);
        Assert.Equal("Error histórico", partition.Shared.ErrorMessage);
        Assert.Equal(send.ArchivedAttachmentPaths, partition.Local.ArchivedAttachmentPaths);
        AssertNoPathProperty(typeof(FirestoreRecentSendDocument));
        Assert.Equal(send.ArchivedAttachmentPaths, roundTrip.ArchivedAttachmentPaths);
        Assert.Equal(send.Id, roundTrip.Id);
    }

    [Fact]
    public void CrcCalculationRoundTripPreservesCompleteFinancialResult()
    {
        var generation = CompleteGeneration();

        var roundTrip = FirestorePersistenceMapper.ToDomain(
            FirestorePersistenceMapper.Partition(generation, _ => Guid.NewGuid()));

        AssertCalculation(generation.Files[0].Crc, roundTrip.Files[0].Crc);
    }

    [Fact]
    public void UsdCalculationRoundTripPreservesCompleteFinancialResult()
    {
        var generation = CompleteGeneration();

        var roundTrip = FirestorePersistenceMapper.ToDomain(
            FirestorePersistenceMapper.Partition(generation, _ => Guid.NewGuid()));

        AssertCalculation(generation.Files[0].Usd, roundTrip.Files[0].Usd);
    }

    [Theory]
    [InlineData("15000")]
    [InlineData("3589.15")]
    [InlineData("214336.00")]
    [InlineData("615524.44")]
    public void DecimalConverterPreservesPrecisionAndScale(string text)
    {
        var expected = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var converter = new DecimalStringConverter();

        var stored = converter.ToFirestore(expected);
        var roundTrip = converter.FromFirestore(stored);

        Assert.IsType<string>(stored);
        Assert.Equal(text, stored);
        Assert.Equal(decimal.GetBits(expected), decimal.GetBits(roundTrip));
    }

    [Fact]
    public void NullHandlingPreservesOptionalValuesWithoutInventingData()
    {
        var broker = new Broker
        {
            Id = Guid.NewGuid(),
            SeedKey = null,
            Name = "Sin opcionales",
            ReviewNote = null,
            Deductions =
            [
                new BrokerDeduction
                {
                    Id = Guid.NewGuid(),
                    Description = "Sin pestaña",
                    TargetWorksheetName = null
                }
            ]
        };

        var roundTrip = FirestorePersistenceMapper.ToDomain(
            FirestorePersistenceMapper.ToFirestore(broker));

        Assert.Null(roundTrip.SeedKey);
        Assert.Null(roundTrip.ReviewNote);
        Assert.Null(roundTrip.Deductions[0].TargetWorksheetName);
    }

    [Fact]
    public void EmptyArraysRemainEmptyAcrossAllPartitions()
    {
        var configuration = FirestorePersistenceMapper.Partition(new AppConfiguration
        {
            CommonCcAddresses = [],
            Brokers = []
        });
        var session = FirestorePersistenceMapper.Partition(new CurrentSession { BrokerItems = [] });
        var generation = FirestorePersistenceMapper.Partition(
            new PaymentGenerationBatch { Files = [], SentBrokerIds = [], FailedBrokerIds = [], Warnings = [] },
            _ => throw new InvalidOperationException("No debe solicitar IDs para una lista vacía."));

        Assert.Empty(configuration.SharedSettings.CommonCcAddresses);
        Assert.Empty(configuration.SharedBrokers);
        Assert.Empty(session.SharedBrokerItems);
        Assert.Empty(session.LocalBrokerItems);
        Assert.Empty(generation.SharedFiles);
        Assert.Empty(generation.LocalFiles);
    }

    [Fact]
    public void ExistingIdsArePreservedAndNoMapperGeneratesReplacementIds()
    {
        var broker = CompleteBroker();
        var generation = CompleteGeneration();
        var fileId = Guid.NewGuid();
        var send = new SentEmailRecord
        {
            Id = Guid.NewGuid(),
            BrokerId = broker.Id,
            ResendOfRecordId = Guid.NewGuid(),
            PaymentGenerationId = generation.Id
        };

        var brokerRoundTrip = FirestorePersistenceMapper.ToDomain(
            FirestorePersistenceMapper.ToFirestore(broker));
        var generationPartition = FirestorePersistenceMapper.Partition(generation, _ => fileId);
        var sendRoundTrip = FirestorePersistenceMapper.ToDomain(
            FirestorePersistenceMapper.Partition(send));

        Assert.Equal(broker.Id, brokerRoundTrip.Id);
        Assert.Equal(broker.Assistants[0].Id, brokerRoundTrip.Assistants[0].Id);
        Assert.Equal(broker.Deductions[0].Id, brokerRoundTrip.Deductions[0].Id);
        Assert.Equal(generation.Id, generationPartition.SharedGeneration.Id);
        Assert.Equal(fileId, generationPartition.SharedFiles[0].Id);
        Assert.Equal(send.Id, sendRoundTrip.Id);
        Assert.Equal(send.ResendOfRecordId, sendRoundTrip.ResendOfRecordId);
        Assert.Equal(send.PaymentGenerationId, sendRoundTrip.PaymentGenerationId);
        Assert.Equal(broker.Id.ToString("D"), FirestorePaths.GuidDocumentId(broker.Id));
    }

    [Fact]
    public void FirestoreIsDisabledByDefaultAndRequiresExternalProjectId()
    {
        var options = new FirestoreOptions();

        Assert.False(options.Enabled);
        Assert.Equal("(default)", options.DatabaseId);
        Assert.Null(options.ProjectId);
        Assert.Throws<InvalidOperationException>(options.ValidateForConnection);
    }

    private static Broker CompleteBroker() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SeedKey = "broker-seed",
        Name = "Corredor sintético",
        PrimaryEmailAddresses = ["broker@example.com", "broker2@example.com"],
        Assistants =
        [
            new BrokerAssistant
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = "Asistente",
                Email = "assistant@example.com",
                IsActive = false
            }
        ],
        AssociatedWorksheetNames = ["SYN", "SYN USD"],
        Deductions =
        [
            new BrokerDeduction
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Description = "Rebajo exacto",
                Amount = 615524.44m,
                Currency = DeductionCurrency.CRC,
                ApplicationType = DeductionApplicationType.PayableAmount,
                TargetWorksheetName = "SYN",
                DisplayOrder = 7
            }
        ],
        IsActive = true,
        RequiresReview = true,
        ReviewNote = "Validar datos"
    };

    private static PaymentGenerationBatch CompleteGeneration()
    {
        var brokerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        return new PaymentGenerationBatch
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Period = "2026-08",
            CreatedAt = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.FromHours(-6)),
            SourceWorkbookPath = @"C:\local\origen.xlsx",
            SourceWorkbookSha256 = "SOURCE-HASH",
            OutputDirectory = @"C:\local\salida",
            Status = PaymentGenerationStatus.PartialSend,
            SentBrokerIds = [brokerId],
            FailedBrokerIds = [Guid.Parse("55555555-5555-5555-5555-555555555555")],
            Warnings = ["Warning de generación"],
            Files =
            [
                new GeneratedPaymentFile
                {
                    BrokerId = brokerId,
                    BrokerName = "Corredor sintético",
                    WorksheetName = "SYN",
                    OutputPath = @"C:\local\salida\SYN.xlsx",
                    Sha256 = "FILE-HASH",
                    AnalyzerName = "Standard",
                    GeneratedAt = new DateTimeOffset(2026, 8, 9, 10, 5, 0, TimeSpan.FromHours(-6)),
                    Crc = Calculation(DeductionCurrency.CRC, 214336.00m),
                    Usd = Calculation(DeductionCurrency.USD, 3589.15m)
                }
            ]
        };
    }

    private static PaymentCurrencyCalculation Calculation(DeductionCurrency currency, decimal gross) => new()
    {
        Currency = currency,
        HasCommission = true,
        MinimumApplied = true,
        MinimumAmount = 15000m,
        GrossCommissionOriginal = gross,
        GrossDeductions = 100.25m,
        AdjustedGrossCommission = gross - 100.25m,
        Vat = 27.75m,
        InvoiceAmount = gross + 27.75m,
        Withholding = 11.50m,
        PayableBeforeFinalDeductions = gross - 11.50m,
        FinalDeductions = 42.42m,
        DepositedAmount = gross - 53.92m,
        Observation = "Resultado calculado",
        Deductions =
        [
            new AppliedDeductionSnapshot
            {
                Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                Description = "Aplicado",
                ConfiguredAmount = 42.42m,
                AppliedAmount = 42.42m,
                Currency = currency,
                ApplicationType = DeductionApplicationType.PayableAmount,
                TargetWorksheetName = "SYN",
                DisplayOrder = 1
            }
        ],
        Warnings = ["Warning calculado"],
        Errors = ["Error persistido"]
    };

    private static void AssertCalculation(
        PaymentCurrencyCalculation expected,
        PaymentCurrencyCalculation actual)
    {
        Assert.Equal(expected.Currency, actual.Currency);
        Assert.Equal(expected.HasCommission, actual.HasCommission);
        Assert.Equal(expected.MinimumApplied, actual.MinimumApplied);
        Assert.Equal(expected.MinimumAmount, actual.MinimumAmount);
        Assert.Equal(expected.GrossCommissionOriginal, actual.GrossCommissionOriginal);
        Assert.Equal(expected.GrossDeductions, actual.GrossDeductions);
        Assert.Equal(expected.AdjustedGrossCommission, actual.AdjustedGrossCommission);
        Assert.Equal(expected.Vat, actual.Vat);
        Assert.Equal(expected.InvoiceAmount, actual.InvoiceAmount);
        Assert.Equal(expected.Withholding, actual.Withholding);
        Assert.Equal(expected.PayableBeforeFinalDeductions, actual.PayableBeforeFinalDeductions);
        Assert.Equal(expected.FinalDeductions, actual.FinalDeductions);
        Assert.Equal(expected.DepositedAmount, actual.DepositedAmount);
        Assert.Equal(expected.Observation, actual.Observation);
        Assert.Equal(expected.Warnings, actual.Warnings);
        Assert.Equal(expected.Errors, actual.Errors);
        var expectedDeduction = Assert.Single(expected.Deductions);
        var actualDeduction = Assert.Single(actual.Deductions);
        Assert.Equal(expectedDeduction.Id, actualDeduction.Id);
        Assert.Equal(expectedDeduction.Description, actualDeduction.Description);
        Assert.Equal(expectedDeduction.ConfiguredAmount, actualDeduction.ConfiguredAmount);
        Assert.Equal(expectedDeduction.AppliedAmount, actualDeduction.AppliedAmount);
        Assert.Equal(expectedDeduction.Currency, actualDeduction.Currency);
        Assert.Equal(expectedDeduction.ApplicationType, actualDeduction.ApplicationType);
        Assert.Equal(expectedDeduction.TargetWorksheetName, actualDeduction.TargetWorksheetName);
        Assert.Equal(expectedDeduction.DisplayOrder, actualDeduction.DisplayOrder);
    }

    private static void AssertNoPathProperty(Type documentType) =>
        Assert.DoesNotContain(
            documentType.GetProperties(),
            property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Directory", StringComparison.OrdinalIgnoreCase));
}
