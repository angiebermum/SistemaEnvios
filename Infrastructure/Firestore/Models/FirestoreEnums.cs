namespace ECS.CommissionsMailer.Infrastructure.Firestore.Models;

public enum FirestoreDeductionApplicationType
{
    GrossCommission,
    PayableAmount
}

public enum FirestoreDeductionCurrency
{
    CRC,
    USD
}

public enum FirestoreSendStatus
{
    Pending,
    Ready,
    Sending,
    Sent,
    Error,
    ReviewRequired
}

public enum FirestorePaymentGenerationStatus
{
    Generated,
    ReadyToSend,
    Sent,
    PartialSend,
    Failed
}
