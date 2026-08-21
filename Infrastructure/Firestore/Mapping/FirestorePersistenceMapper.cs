using System.Collections.ObjectModel;
using ECS.CommissionsMailer.Infrastructure.Firestore;
using ECS.CommissionsMailer.Infrastructure.Firestore.Models;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Infrastructure.Firestore.Mapping;

/// <summary>
/// Adaptador puro entre el dominio JSON actual y los documentos futuros. No lee ni
/// escribe archivos, no abre conexiones y no está conectado al runtime WPF.
/// </summary>
public static class FirestorePersistenceMapper
{
    public static FirestoreBrokerDocument ToFirestore(Broker source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new FirestoreBrokerDocument
        {
            Id = source.Id,
            SeedKey = source.SeedKey,
            Name = source.Name ?? string.Empty,
            PrimaryEmailAddresses = [.. source.PrimaryEmailAddresses ?? []],
            Assistants = (source.Assistants ?? []).Select(ToFirestore).ToList(),
            AssociatedWorksheetNames = [.. source.AssociatedWorksheetNames ?? []],
            Deductions = (source.Deductions ?? []).Select(ToFirestore).ToList(),
            IsActive = source.IsActive,
            RequiresReview = source.RequiresReview,
            ReviewNote = source.ReviewNote
        };
    }

    public static Broker ToDomain(FirestoreBrokerDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Broker
        {
            Id = source.Id,
            SeedKey = source.SeedKey,
            Name = source.Name ?? string.Empty,
            PrimaryEmailAddresses = [.. source.PrimaryEmailAddresses ?? []],
            Assistants = (source.Assistants ?? []).Select(ToDomain).ToList(),
            AssociatedWorksheetNames = [.. source.AssociatedWorksheetNames ?? []],
            Deductions = (source.Deductions ?? []).Select(ToDomain).ToList(),
            IsActive = source.IsActive,
            RequiresReview = source.RequiresReview,
            ReviewNote = source.ReviewNote
        };
    }

    public static ConfigurationPersistencePartition Partition(AppConfiguration source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ConfigurationPersistencePartition(
            new FirestoreCommissionsSettingsDocument
            {
                DefaultSubject = source.DefaultSubject ?? string.Empty,
                DefaultMessage = source.DefaultMessage ?? string.Empty,
                DataSchemaVersion = source.DataSchemaVersion,
                EmailDirectorySeedVersion = source.EmailDirectorySeedVersion,
                EmailDirectorySeedId = source.EmailDirectorySeedId,
                CommonCcAddresses = [.. source.CommonCcAddresses ?? []]
            },
            (source.Brokers ?? []).Select(ToFirestore).ToList(),
            new ConfigurationLocalState(source.SignatureImagePath));
    }

    public static AppConfiguration ToDomain(ConfigurationPersistencePartition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var shared = source.SharedSettings;
        return new AppConfiguration
        {
            DefaultSubject = shared.DefaultSubject ?? string.Empty,
            DefaultMessage = shared.DefaultMessage ?? string.Empty,
            SignatureImagePath = source.Local.SignatureImagePath,
            DataSchemaVersion = shared.DataSchemaVersion,
            EmailDirectorySeedVersion = shared.EmailDirectorySeedVersion,
            EmailDirectorySeedId = shared.EmailDirectorySeedId,
            CommonCcAddresses = [.. shared.CommonCcAddresses ?? []],
            Brokers = (source.SharedBrokers ?? []).Select(ToDomain).ToList()
        };
    }

    public static CurrentSessionPersistencePartition Partition(CurrentSession source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var brokerItems = source.BrokerItems ?? [];
        return new CurrentSessionPersistencePartition(
            new FirestoreCurrentSessionDocument
            {
                Subject = source.Subject ?? string.Empty,
                Message = source.Message ?? string.Empty,
                CommonCcText = source.CommonCcText ?? string.Empty,
                ActivePaymentGenerationId = source.ActivePaymentGenerationId,
                GeneratedPeriod = source.GeneratedPeriod ?? string.Empty,
                SavedAtUtc = FirestoreTimestampPrecision.Normalize(source.SavedAt)
            },
            brokerItems.Select(ToFirestore).ToList(),
            new CurrentSessionLocalState(
                source.GeneralWorkbookPath ?? string.Empty,
                source.GeneratedOutputDirectory ?? string.Empty),
            brokerItems.Select(item => new BrokerSendItemLocalState(
                item.BrokerId,
                [.. item.AttachmentPaths ?? []],
                [.. item.GeneratedAttachmentPaths ?? []])).ToList());
    }

    public static CurrentSession ToDomain(CurrentSessionPersistencePartition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var localItems = (source.LocalBrokerItems ?? [])
            .GroupBy(item => item.BrokerId)
            .ToDictionary(group => group.Key, group => group.First());
        return new CurrentSession
        {
            Subject = source.SharedSession.Subject ?? string.Empty,
            Message = source.SharedSession.Message ?? string.Empty,
            CommonCcText = source.SharedSession.CommonCcText ?? string.Empty,
            GeneralWorkbookPath = source.Local.GeneralWorkbookPath ?? string.Empty,
            ActivePaymentGenerationId = source.SharedSession.ActivePaymentGenerationId,
            GeneratedOutputDirectory = source.Local.GeneratedOutputDirectory ?? string.Empty,
            GeneratedPeriod = source.SharedSession.GeneratedPeriod ?? string.Empty,
            SavedAt = source.SharedSession.SavedAtUtc,
            BrokerItems = (source.SharedBrokerItems ?? []).Select(item =>
            {
                localItems.TryGetValue(item.BrokerId, out var local);
                return ToDomain(item, local);
            }).ToList()
        };
    }

    public static RecentSendPersistencePartition Partition(SentEmailRecord source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new RecentSendPersistencePartition(
            new FirestoreRecentSendDocument
            {
                Id = source.Id,
                BrokerId = source.BrokerId,
                BrokerName = source.BrokerName ?? string.Empty,
                BrokerPrimaryRecipients = [.. source.BrokerPrimaryRecipients ?? []],
                AssistantRecipients = [.. source.AssistantRecipients ?? []],
                ToRecipients = [.. source.ToRecipients ?? []],
                CcRecipients = [.. source.CcRecipients ?? []],
                Subject = source.Subject ?? string.Empty,
                Body = source.Body ?? string.Empty,
                SentAtUtc = FirestoreTimestampPrecision.Normalize(source.SentAt),
                WasSuccessful = source.WasSuccessful,
                ErrorMessage = source.ErrorMessage ?? string.Empty,
                ResendOfRecordId = source.ResendOfRecordId,
                PaymentGenerationId = source.PaymentGenerationId
            },
            new RecentSendLocalState(source.Id, [.. source.ArchivedAttachmentPaths ?? []]));
    }

    public static SentEmailRecord ToDomain(RecentSendPersistencePartition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new SentEmailRecord
        {
            Id = source.Shared.Id,
            BrokerId = source.Shared.BrokerId,
            BrokerName = source.Shared.BrokerName ?? string.Empty,
            BrokerPrimaryRecipients = [.. source.Shared.BrokerPrimaryRecipients ?? []],
            AssistantRecipients = [.. source.Shared.AssistantRecipients ?? []],
            ToRecipients = [.. source.Shared.ToRecipients ?? []],
            CcRecipients = [.. source.Shared.CcRecipients ?? []],
            Subject = source.Shared.Subject ?? string.Empty,
            Body = source.Shared.Body ?? string.Empty,
            SentAt = source.Shared.SentAtUtc,
            ArchivedAttachmentPaths = [.. source.Local.ArchivedAttachmentPaths ?? []],
            WasSuccessful = source.Shared.WasSuccessful,
            ErrorMessage = source.Shared.ErrorMessage ?? string.Empty,
            ResendOfRecordId = source.Shared.ResendOfRecordId,
            PaymentGenerationId = source.Shared.PaymentGenerationId
        };
    }

    public static PaymentGenerationPersistencePartition Partition(
        PaymentGenerationBatch source,
        Func<GeneratedPaymentFile, Guid> futureFileIdFactory)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(futureFileIdFactory);
        var sharedFiles = new List<FirestorePaymentGenerationFileDocument>();
        var localFiles = new List<PaymentGenerationFileLocalState>();
        var ids = new HashSet<Guid>();
        foreach (var file in source.Files ?? [])
        {
            var fileId = futureFileIdFactory(file);
            if (fileId == Guid.Empty || !ids.Add(fileId))
            {
                throw new InvalidOperationException(
                    "La futura migración debe asignar un ID no vacío y único a cada archivo generado.");
            }

            sharedFiles.Add(ToFirestore(file, fileId));
            localFiles.Add(new PaymentGenerationFileLocalState(fileId, file.OutputPath ?? string.Empty));
        }

        return new PaymentGenerationPersistencePartition(
            new FirestorePaymentGenerationDocument
            {
                Id = source.Id,
                Period = source.Period ?? string.Empty,
                CreatedAtUtc = FirestoreTimestampPrecision.Normalize(source.CreatedAt),
                SourceWorkbookSha256 = source.SourceWorkbookSha256 ?? string.Empty,
                Status = MapEnum<FirestorePaymentGenerationStatus>(source.Status),
                SentBrokerIds = (source.SentBrokerIds ?? []).Select(id => id.ToString("D")).ToList(),
                FailedBrokerIds = (source.FailedBrokerIds ?? []).Select(id => id.ToString("D")).ToList(),
                Warnings = [.. source.Warnings ?? []]
            },
            sharedFiles,
            new PaymentGenerationLocalState(
                source.Id,
                source.SourceWorkbookPath ?? string.Empty,
                source.OutputDirectory ?? string.Empty),
            localFiles);
    }

    public static PaymentGenerationBatch ToDomain(PaymentGenerationPersistencePartition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var localFiles = (source.LocalFiles ?? [])
            .GroupBy(file => file.FirestoreFileId)
            .ToDictionary(group => group.Key, group => group.First());
        return new PaymentGenerationBatch
        {
            Id = source.SharedGeneration.Id,
            Period = source.SharedGeneration.Period ?? string.Empty,
            CreatedAt = source.SharedGeneration.CreatedAtUtc,
            SourceWorkbookPath = source.Local.SourceWorkbookPath ?? string.Empty,
            SourceWorkbookSha256 = source.SharedGeneration.SourceWorkbookSha256 ?? string.Empty,
            OutputDirectory = source.Local.OutputDirectory ?? string.Empty,
            Status = MapEnum<PaymentGenerationStatus>(source.SharedGeneration.Status),
            Files = (source.SharedFiles ?? []).Select(file =>
            {
                localFiles.TryGetValue(file.Id, out var local);
                return ToDomain(file, local);
            }).ToList(),
            SentBrokerIds = ParseGuids(source.SharedGeneration.SentBrokerIds),
            FailedBrokerIds = ParseGuids(source.SharedGeneration.FailedBrokerIds),
            Warnings = [.. source.SharedGeneration.Warnings ?? []]
        };
    }

    private static FirestoreBrokerAssistant ToFirestore(BrokerAssistant source) => new()
    {
        Id = source.Id,
        Name = source.Name ?? string.Empty,
        Email = source.Email ?? string.Empty,
        IsActive = source.IsActive
    };

    private static BrokerAssistant ToDomain(FirestoreBrokerAssistant source) => new()
    {
        Id = source.Id,
        Name = source.Name ?? string.Empty,
        Email = source.Email ?? string.Empty,
        IsActive = source.IsActive
    };

    private static FirestoreBrokerDeduction ToFirestore(BrokerDeduction source) => new()
    {
        Id = source.Id,
        Description = source.Description ?? string.Empty,
        Amount = source.Amount,
        Currency = MapEnum<FirestoreDeductionCurrency>(source.Currency),
        ApplicationType = MapEnum<FirestoreDeductionApplicationType>(source.ApplicationType),
        TargetWorksheetName = source.TargetWorksheetName,
        DisplayOrder = source.DisplayOrder
    };

    private static BrokerDeduction ToDomain(FirestoreBrokerDeduction source) => new()
    {
        Id = source.Id,
        Description = source.Description ?? string.Empty,
        Amount = source.Amount,
        Currency = MapEnum<DeductionCurrency>(source.Currency),
        ApplicationType = MapEnum<DeductionApplicationType>(source.ApplicationType),
        TargetWorksheetName = source.TargetWorksheetName,
        DisplayOrder = source.DisplayOrder
    };

    private static FirestoreBrokerSendItemDocument ToFirestore(BrokerSendItem source) => new()
    {
        BrokerId = source.BrokerId,
        BrokerName = source.BrokerName ?? string.Empty,
        SeedKey = source.SeedKey,
        PrimaryRecipients = [.. source.PrimaryRecipients ?? []],
        Assistants = (source.Assistants ?? []).Select(ToFirestore).ToList(),
        RequiresReview = source.RequiresReview,
        ReviewNote = source.ReviewNote ?? string.Empty,
        RequiresBatchReview = source.RequiresBatchReview,
        BatchReviewNote = source.BatchReviewNote ?? string.Empty,
        IsSelected = source.IsSelected,
        Status = MapEnum<FirestoreSendStatus>(source.Status),
        LastError = source.LastError ?? string.Empty
    };

    private static BrokerSendItem ToDomain(
        FirestoreBrokerSendItemDocument source,
        BrokerSendItemLocalState? local) => new()
    {
        BrokerId = source.BrokerId,
        BrokerName = source.BrokerName ?? string.Empty,
        SeedKey = source.SeedKey,
        PrimaryRecipients = [.. source.PrimaryRecipients ?? []],
        Assistants = (source.Assistants ?? []).Select(ToDomain).ToList(),
        RequiresReview = source.RequiresReview,
        ReviewNote = source.ReviewNote ?? string.Empty,
        RequiresBatchReview = source.RequiresBatchReview,
        BatchReviewNote = source.BatchReviewNote ?? string.Empty,
        IsSelected = source.IsSelected,
        AttachmentPaths = new ObservableCollection<string>(local?.AttachmentPaths ?? []),
        GeneratedAttachmentPaths = new ObservableCollection<string>(local?.GeneratedAttachmentPaths ?? []),
        Status = MapEnum<SendStatus>(source.Status),
        LastError = source.LastError ?? string.Empty
    };

    private static FirestorePaymentGenerationFileDocument ToFirestore(
        GeneratedPaymentFile source,
        Guid fileId) => new()
    {
        Id = fileId,
        BrokerId = source.BrokerId,
        BrokerName = source.BrokerName ?? string.Empty,
        WorksheetName = source.WorksheetName ?? string.Empty,
        Sha256 = source.Sha256 ?? string.Empty,
        AnalyzerName = source.AnalyzerName ?? string.Empty,
        GeneratedAtUtc = FirestoreTimestampPrecision.Normalize(source.GeneratedAt),
        Crc = ToFirestore(source.Crc ?? new PaymentCurrencyCalculation { Currency = DeductionCurrency.CRC }),
        Usd = ToFirestore(source.Usd ?? new PaymentCurrencyCalculation { Currency = DeductionCurrency.USD })
    };

    private static GeneratedPaymentFile ToDomain(
        FirestorePaymentGenerationFileDocument source,
        PaymentGenerationFileLocalState? local) => new()
    {
        BrokerId = source.BrokerId,
        BrokerName = source.BrokerName ?? string.Empty,
        WorksheetName = source.WorksheetName ?? string.Empty,
        OutputPath = local?.OutputPath ?? string.Empty,
        Sha256 = source.Sha256 ?? string.Empty,
        AnalyzerName = source.AnalyzerName ?? string.Empty,
        GeneratedAt = source.GeneratedAtUtc,
        Crc = ToDomain(source.Crc ?? new FirestorePaymentCurrencyCalculation { Currency = FirestoreDeductionCurrency.CRC }),
        Usd = ToDomain(source.Usd ?? new FirestorePaymentCurrencyCalculation { Currency = FirestoreDeductionCurrency.USD })
    };

    private static FirestorePaymentCurrencyCalculation ToFirestore(PaymentCurrencyCalculation source) => new()
    {
        Currency = MapEnum<FirestoreDeductionCurrency>(source.Currency),
        HasCommission = source.HasCommission,
        MinimumApplied = source.MinimumApplied,
        MinimumAmount = source.MinimumAmount,
        GrossCommissionOriginal = source.GrossCommissionOriginal,
        GrossDeductions = source.GrossDeductions,
        AdjustedGrossCommission = source.AdjustedGrossCommission,
        Vat = source.Vat,
        InvoiceAmount = source.InvoiceAmount,
        Withholding = source.Withholding,
        PayableBeforeFinalDeductions = source.PayableBeforeFinalDeductions,
        FinalDeductions = source.FinalDeductions,
        DepositedAmount = source.DepositedAmount,
        Observation = source.Observation ?? string.Empty,
        Deductions = (source.Deductions ?? []).Select(ToFirestore).ToList(),
        Warnings = [.. source.Warnings ?? []],
        Errors = [.. source.Errors ?? []]
    };

    private static PaymentCurrencyCalculation ToDomain(FirestorePaymentCurrencyCalculation source) => new()
    {
        Currency = MapEnum<DeductionCurrency>(source.Currency),
        HasCommission = source.HasCommission,
        MinimumApplied = source.MinimumApplied,
        MinimumAmount = source.MinimumAmount,
        GrossCommissionOriginal = source.GrossCommissionOriginal,
        GrossDeductions = source.GrossDeductions,
        AdjustedGrossCommission = source.AdjustedGrossCommission,
        Vat = source.Vat,
        InvoiceAmount = source.InvoiceAmount,
        Withholding = source.Withholding,
        PayableBeforeFinalDeductions = source.PayableBeforeFinalDeductions,
        FinalDeductions = source.FinalDeductions,
        DepositedAmount = source.DepositedAmount,
        Observation = source.Observation ?? string.Empty,
        Deductions = (source.Deductions ?? []).Select(ToDomain).ToList(),
        Warnings = [.. source.Warnings ?? []],
        Errors = [.. source.Errors ?? []]
    };

    private static FirestoreAppliedDeductionSnapshot ToFirestore(AppliedDeductionSnapshot source) => new()
    {
        Id = source.Id,
        Description = source.Description ?? string.Empty,
        ConfiguredAmount = source.ConfiguredAmount,
        AppliedAmount = source.AppliedAmount,
        Currency = MapEnum<FirestoreDeductionCurrency>(source.Currency),
        ApplicationType = MapEnum<FirestoreDeductionApplicationType>(source.ApplicationType),
        TargetWorksheetName = source.TargetWorksheetName ?? string.Empty,
        DisplayOrder = source.DisplayOrder
    };

    private static AppliedDeductionSnapshot ToDomain(FirestoreAppliedDeductionSnapshot source) => new()
    {
        Id = source.Id,
        Description = source.Description ?? string.Empty,
        ConfiguredAmount = source.ConfiguredAmount,
        AppliedAmount = source.AppliedAmount,
        Currency = MapEnum<DeductionCurrency>(source.Currency),
        ApplicationType = MapEnum<DeductionApplicationType>(source.ApplicationType),
        TargetWorksheetName = source.TargetWorksheetName ?? string.Empty,
        DisplayOrder = source.DisplayOrder
    };

    private static List<Guid> ParseGuids(IEnumerable<string>? values) =>
        (values ?? []).Select(value => Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidDataException($"El identificador Firestore '{value}' no es un UUID válido."))
        .ToList();

    private static TTarget MapEnum<TTarget>(Enum source)
        where TTarget : struct, Enum => Enum.Parse<TTarget>(source.ToString());
}
