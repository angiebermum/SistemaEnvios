namespace ECS.CommissionsMailer.Services;

internal static class OutlookSendingAccountAssignment
{
    public static void AssignAndVerify<TAccount>(
        TAccount sendingAccount,
        string expectedEmail,
        Action<TAccount> assign,
        Func<TAccount?> readAssigned,
        Func<TAccount, string?> getEmail,
        Action<TAccount?> releaseAssigned)
        where TAccount : class
    {
        ArgumentNullException.ThrowIfNull(sendingAccount);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedEmail);
        ArgumentNullException.ThrowIfNull(assign);
        ArgumentNullException.ThrowIfNull(readAssigned);
        ArgumentNullException.ThrowIfNull(getEmail);
        ArgumentNullException.ThrowIfNull(releaseAssigned);

        TAccount? assignedAccount = null;
        try
        {
            assign(sendingAccount);
            assignedAccount = readAssigned();
            var assignedEmail = assignedAccount is null ? null : getEmail(assignedAccount);
            if (!string.Equals(assignedEmail, expectedEmail, StringComparison.OrdinalIgnoreCase))
            {
                throw new OutlookIntegrationException(
                    Models.OutlookFailureReason.SendingAccountUnavailable,
                    $"Outlook no confirmó la cuenta de envío {expectedEmail}. " +
                    "No se envió ningún correo para evitar usar otra cuenta.");
            }
        }
        finally
        {
            releaseAssigned(assignedAccount);
        }
    }
}
