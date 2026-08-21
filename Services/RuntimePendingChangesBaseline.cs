using System.Text.Json;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

internal sealed record RuntimePendingChangesBaseline(
    string ConfigurationFingerprint,
    string SessionFingerprint,
    string RecentSendsFingerprint,
    string PaymentGenerationsFingerprint)
{
    public static RuntimePendingChangesBaseline Capture(
        AppConfiguration configuration,
        CurrentSession? session,
        IEnumerable<SentEmailRecord> recentSends,
        IEnumerable<PaymentGenerationBatch> paymentGenerations) =>
        new(
            Serialize(configuration),
            FingerprintSession(session),
            SerializeOrdered(recentSends, value => value.Id),
            SerializeOrdered(paymentGenerations, value => value.Id));

    public bool HasChanges(
        AppConfiguration configuration,
        CurrentSession? session,
        IEnumerable<SentEmailRecord> recentSends,
        IEnumerable<PaymentGenerationBatch> paymentGenerations) =>
        HasConfigurationChanges(configuration) ||
        HasSessionChanges(session) ||
        HasRecentSendsChanges(recentSends) ||
        HasPaymentGenerationChanges(paymentGenerations);

    public bool HasConfigurationChanges(AppConfiguration configuration) =>
        !string.Equals(ConfigurationFingerprint, Serialize(configuration), StringComparison.Ordinal);

    public bool HasSessionChanges(CurrentSession? session) =>
        !string.Equals(SessionFingerprint, FingerprintSession(session), StringComparison.Ordinal);

    public bool HasRecentSendsChanges(IEnumerable<SentEmailRecord> recentSends) =>
        !string.Equals(RecentSendsFingerprint, SerializeOrdered(recentSends, value => value.Id), StringComparison.Ordinal);

    public bool HasPaymentGenerationChanges(IEnumerable<PaymentGenerationBatch> paymentGenerations) =>
        !string.Equals(
            PaymentGenerationsFingerprint,
            SerializeOrdered(paymentGenerations, value => value.Id),
            StringComparison.Ordinal);

    public RuntimePendingChangesBaseline WithConfiguration(AppConfiguration configuration) =>
        this with { ConfigurationFingerprint = Serialize(configuration) };

    public RuntimePendingChangesBaseline WithSession(CurrentSession? session) =>
        this with { SessionFingerprint = FingerprintSession(session) };

    public RuntimePendingChangesBaseline WithRecentSends(IEnumerable<SentEmailRecord> recentSends) =>
        this with { RecentSendsFingerprint = SerializeOrdered(recentSends, value => value.Id) };

    public RuntimePendingChangesBaseline WithPaymentGenerations(
        IEnumerable<PaymentGenerationBatch> paymentGenerations) =>
        this with
        {
            PaymentGenerationsFingerprint = SerializeOrdered(paymentGenerations, value => value.Id)
        };

    private static string FingerprintSession(CurrentSession? session)
    {
        if (session is null)
        {
            return "null";
        }

        return Serialize(new
        {
            session.Subject,
            session.Message,
            session.CommonCcText,
            session.GeneralWorkbookPath,
            session.ActivePaymentGenerationId,
            session.GeneratedOutputDirectory,
            session.GeneratedPeriod,
            BrokerItems = session.BrokerItems
                .OrderBy(value => value.BrokerId)
                .Select(Serialize)
                .ToList()
        });
    }

    private static string SerializeOrdered<T, TKey>(IEnumerable<T> values, Func<T, TKey> keySelector) =>
        Serialize(values.OrderBy(keySelector).Select(Serialize).ToList());

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
}
