using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ECS.CommissionsMailer.Infrastructure.Firestore;
using ECS.CommissionsMailer.Models;

namespace ECSCommissionsMailer.FirestoreMigration;

public static class DeterministicDocumentIds
{
    public static Guid ForRecentSend(SentEmailRecord send)
    {
        ArgumentNullException.ThrowIfNull(send);
        var canonical = new CanonicalText("ecs-recent-send-v1")
            .Guid("brokerId", send.BrokerId)
            .Text("brokerName", send.BrokerName)
            .Texts("brokerPrimaryRecipients", send.BrokerPrimaryRecipients)
            .Texts("assistantRecipients", send.AssistantRecipients)
            .Texts("toRecipients", send.ToRecipients)
            .Texts("ccRecipients", send.CcRecipients)
            .Text("subject", send.Subject)
            .Text("body", send.Body)
            .Text(
                "sentAtUtc",
                FirestoreTimestampPrecision.Normalize(send.SentAt).ToString("O", CultureInfo.InvariantCulture))
            .Text("wasSuccessful", send.WasSuccessful ? "true" : "false")
            .Text("errorMessage", send.ErrorMessage)
            .NullableGuid("resendOfRecordId", send.ResendOfRecordId)
            .NullableGuid("paymentGenerationId", send.PaymentGenerationId)
            .ToString();

        return Sha256Guid(canonical);
    }

    public static Guid ForGenerationFile(Guid generationId, GeneratedPaymentFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var canonical = new CanonicalText("ecs-generation-file-v1")
            .Guid("generationId", generationId)
            .Guid("brokerId", file.BrokerId)
            .Text("worksheetName", file.WorksheetName)
            .Text("sha256", file.Sha256)
            .ToString();

        return Sha256Guid(canonical);
    }

    public static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid Sha256Guid(string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var first128Bits = Convert.ToHexString(hash.AsSpan(0, 16));
        return Guid.ParseExact(first128Bits, "N");
    }

    private sealed class CanonicalText
    {
        private readonly StringBuilder _value = new();

        public CanonicalText(string format)
        {
            Append("format", format);
        }

        public CanonicalText Guid(string name, System.Guid value) => Text(name, value.ToString("D"));

        public CanonicalText NullableGuid(string name, System.Guid? value) =>
            Text(name, value?.ToString("D"));

        public CanonicalText Text(string name, string? value)
        {
            Append(name, value);
            return this;
        }

        public CanonicalText Texts(string name, IEnumerable<string>? values)
        {
            var materialized = (values ?? []).ToList();
            Append($"{name}.count", materialized.Count.ToString(CultureInfo.InvariantCulture));
            for (var index = 0; index < materialized.Count; index++)
            {
                Append($"{name}[{index}]", materialized[index]);
            }

            return this;
        }

        public override string ToString() => _value.ToString();

        private void Append(string name, string? value)
        {
            var byteLength = value is null ? -1 : Encoding.UTF8.GetByteCount(value);
            _value.Append(name)
                .Append('=')
                .Append(byteLength.ToString(CultureInfo.InvariantCulture))
                .Append(':');
            if (value is not null)
            {
                _value.Append(value);
            }

            _value.Append('\n');
        }
    }
}
