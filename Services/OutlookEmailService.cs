using System.Runtime.InteropServices;
using ECS.CommissionsMailer.Helpers;
using ECS.CommissionsMailer.Models;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ECS.CommissionsMailer.Services;

public sealed class OutlookEmailService
{
    private readonly FileLogger _logger;
    private readonly OutlookApplicationFactory _applicationFactory;
    private readonly object _accountSync = new();
    private string? _connectedAccountEmail;

    public OutlookEmailService(FileLogger logger)
    {
        _logger = logger;
        _applicationFactory = new OutlookApplicationFactory(logger);
    }

    public Task<OutlookConnectionInfo> CheckAvailabilityAsync() =>
        StaTaskRunner.RunAsync(CheckAvailability);

    public Task<OutlookDiagnosticResult> TestConnectionAsync(
        bool closeDraftAfterDisplay,
        string? signatureImagePath = null,
        string? body = null,
        string? subject = null) =>
        StaTaskRunner.RunAsync(() => TestConnection(closeDraftAfterDisplay, signatureImagePath, body, subject));

    public Task<IReadOnlyList<EmailSendResult>> SendBatchAsync(
        IReadOnlyList<EmailSendRequest> requests,
        IProgress<OutlookSendProgress>? progress = null) =>
        StaTaskRunner.RunAsync(() => SendBatch(requests, progress));

    public Task<OutlookDiagnosticResult> DisplayDraftAsync(EmailSendRequest request, bool closeAfterDisplay) =>
        StaTaskRunner.RunAsync(() => DisplayDraft(request, closeAfterDisplay));

    private OutlookConnectionInfo CheckAvailability()
    {
        var environment = InspectAndLogEnvironment("comprobación de disponibilidad");
        Outlook.Application? application = null;
        Outlook.NameSpace? session = null;
        Outlook.Accounts? accounts = null;
        Outlook.Recipient? currentUser = null;
        try
        {
            OutlookApplicationFactory.EnsureSta();
            application = _applicationFactory.CreateValidatedApplication();
            session = application.Session;
            accounts = session.Accounts;
            var emailAddress = GetDefaultAccountEmail(accounts);
            if (string.IsNullOrWhiteSpace(emailAddress))
            {
                currentUser = session.CurrentUser;
                var currentUserAddress = currentUser?.Address;
                if (!string.IsNullOrWhiteSpace(currentUserAddress) && currentUserAddress.Contains('@'))
                {
                    emailAddress = currentUserAddress;
                }
            }

            var message = string.IsNullOrWhiteSpace(emailAddress)
                ? "Outlook clásico está conectado, pero no fue posible identificar el correo de la cuenta predeterminada."
                : "Outlook clásico detectado y conectado.";
            ConnectedAccountEmail = emailAddress;
            if (!string.IsNullOrWhiteSpace(emailAddress))
            {
                _logger.Info($"Cuenta de envío conectada: {emailAddress}.");
            }

            return new OutlookConnectionInfo(true, message, emailAddress);
        }
        catch (Exception ex)
        {
            ConnectedAccountEmail = null;
            var failure = OutlookFailureClassifier.Classify(ex, environment, _logger.LogFilePath);
            _logger.Error($"No fue posible comprobar Outlook clásico. Clasificación={failure.Reason}.", ex);
            return new OutlookConnectionInfo(false, failure.UserMessage);
        }
        finally
        {
            ComObjectHelper.FinalRelease(currentUser);
            ComObjectHelper.FinalRelease(accounts);
            ComObjectHelper.FinalRelease(session);
            ComObjectHelper.FinalRelease(application);
        }
    }

    private static string? GetDefaultAccountEmail(Outlook.Accounts accounts)
    {
        for (var index = 1; index <= accounts.Count; index++)
        {
            Outlook.Account? account = null;
            try
            {
                account = accounts[index];
                var smtpAddress = account.SmtpAddress;
                if (!string.IsNullOrWhiteSpace(smtpAddress))
                {
                    return smtpAddress.Trim();
                }

                var displayName = account.DisplayName;
                if (!string.IsNullOrWhiteSpace(displayName) && displayName.Contains('@'))
                {
                    return displayName.Trim();
                }
            }
            finally
            {
                ComObjectHelper.FinalRelease(account);
            }
        }

        return null;
    }

    private OutlookDiagnosticResult TestConnection(
        bool closeDraftAfterDisplay,
        string? signatureImagePath,
        string? body,
        string? subject)
    {
        var environment = InspectAndLogEnvironment("prueba de conexión y Display");
        Outlook.Application? application = null;
        Outlook.NameSpace? session = null;
        Outlook.Recipient? currentUser = null;
        Outlook.Account? sendingAccount = null;
        Outlook.MailItem? mailItem = null;
        Outlook.Attachments? attachments = null;
        Outlook.Attachment? inlineSignature = null;
        Outlook.PropertyAccessor? propertyAccessor = null;
        try
        {
            OutlookApplicationFactory.EnsureSta();
            application = _applicationFactory.CreateValidatedApplication();
            session = application.Session;
            currentUser = session.CurrentUser;
            var currentUserName = currentUser?.Name;
            sendingAccount = ResolveSendingAccount(application, out var sendingAccountEmail);

            mailItem = (Outlook.MailItem)application.CreateItem(Outlook.OlItemType.olMailItem);
            SetAndVerifySendingAccount(mailItem, sendingAccount, sendingAccountEmail);
            mailItem.Subject = string.IsNullOrWhiteSpace(subject)
                ? "PRUEBA DE CONEXIÓN - ECS ENVÍO DE CORREOS"
                : subject;
            ConfigureHtmlBody(
                mailItem,
                string.IsNullOrWhiteSpace(body) ? "Borrador de prueba de conexión con Outlook Classic." : body,
                signatureImagePath,
                ref attachments,
                ref inlineSignature,
                ref propertyAccessor);
            mailItem.Display(false);
            if (closeDraftAfterDisplay)
            {
                mailItem.Close(Outlook.OlInspectorClose.olDiscard);
            }

            var userText = $" Cuenta de envío: {sendingAccountEmail}." +
                (string.IsNullOrWhiteSpace(currentUserName) ? string.Empty : $" Usuario: {currentUserName}.");
            var draftText = closeDraftAfterDisplay
                ? " El borrador de prueba se mostró y se cerró sin guardarlo."
                : " Se abrió un borrador de prueba; puede cerrarlo manualmente sin enviarlo.";
            var message = $"Outlook clásico detectado y conectado.{userText}{draftText}";
            _logger.Info($"Prueba de conexión con Outlook completada. Display=correcto; BorradorCerrado={closeDraftAfterDisplay}.");
            return new OutlookDiagnosticResult(true, message);
        }
        catch (Exception ex)
        {
            var failure = OutlookFailureClassifier.Classify(ex, environment, _logger.LogFilePath);
            _logger.Error($"Falló la prueba de conexión con Outlook. Clasificación={failure.Reason}.", ex);
            return new OutlookDiagnosticResult(false, failure.UserMessage, failure.Reason, failure.HResult);
        }
        finally
        {
            ComObjectHelper.FinalRelease(propertyAccessor);
            ComObjectHelper.FinalRelease(inlineSignature);
            ComObjectHelper.FinalRelease(attachments);
            ComObjectHelper.FinalRelease(mailItem);
            ComObjectHelper.FinalRelease(sendingAccount);
            ComObjectHelper.FinalRelease(currentUser);
            ComObjectHelper.FinalRelease(session);
            ComObjectHelper.FinalRelease(application);
        }
    }

    private IReadOnlyList<EmailSendResult> SendBatch(
        IReadOnlyList<EmailSendRequest> requests,
        IProgress<OutlookSendProgress>? progress)
    {
        var environment = InspectAndLogEnvironment("envío por lotes");
        var results = new List<EmailSendResult>(requests.Count);
        Outlook.Application? application = null;
        Outlook.Account? sendingAccount = null;

        try
        {
            OutlookApplicationFactory.EnsureSta();
            try
            {
                application = _applicationFactory.CreateValidatedApplication();
            }
            catch (Exception ex)
            {
                var failure = OutlookFailureClassifier.Classify(ex, environment, _logger.LogFilePath);
                _logger.Error($"No fue posible iniciar Outlook para el envío. Clasificación={failure.Reason}.", ex);
                return CreateFailureResults(requests, progress, failure.UserMessage);
            }

            sendingAccount = ResolveSendingAccount(application, out var sendingAccountEmail);
            _logger.Info($"La cuenta {sendingAccountEmail} se usará explícitamente para el lote de {requests.Count} correo(s).");

            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                progress?.Report(new OutlookSendProgress
                {
                    Request = request,
                    Current = index + 1,
                    Total = requests.Count,
                    Stage = OutlookProgressStage.Starting
                });

                _logger.Info($"Inicio de envío para corredor {request.BrokerName}.");
                EmailSendResult result;
                try
                {
                    ProcessOne(
                        application,
                        request,
                        sendingAccount,
                        sendingAccountEmail,
                        displayOnly: false,
                        closeAfterDisplay: false);
                    result = new EmailSendResult { RequestId = request.RequestId, WasSuccessful = true };
                    _logger.Info($"Envío completado para corredor {request.BrokerName}.");
                }
                catch (Exception ex)
                {
                    var failure = OutlookFailureClassifier.Classify(ex, environment, _logger.LogFilePath);
                    result = new EmailSendResult
                    {
                        RequestId = request.RequestId,
                        WasSuccessful = false,
                        ErrorMessage = failure.UserMessage
                    };
                    _logger.Error($"Falló el envío para corredor {request.BrokerName}. Clasificación={failure.Reason}.", ex);
                }

                results.Add(result);
                ReportCompleted(progress, request, index + 1, requests.Count, result);
            }

            return results;
        }
        catch (Exception ex)
        {
            var failure = OutlookFailureClassifier.Classify(ex, environment, _logger.LogFilePath);
            _logger.Error($"Falló la integración general con Outlook. Clasificación={failure.Reason}.", ex);
            return CreateFailureResults(requests, progress, failure.UserMessage);
        }
        finally
        {
            ComObjectHelper.FinalRelease(sendingAccount);
            ComObjectHelper.FinalRelease(application);
        }
    }

    private OutlookDiagnosticResult DisplayDraft(EmailSendRequest request, bool closeAfterDisplay)
    {
        var environment = InspectAndLogEnvironment("prueba controlada de borrador completo");
        Outlook.Application? application = null;
        Outlook.Account? sendingAccount = null;
        try
        {
            OutlookApplicationFactory.EnsureSta();
            application = _applicationFactory.CreateValidatedApplication();
            sendingAccount = ResolveSendingAccount(application, out var sendingAccountEmail);
            ProcessOne(application, request, sendingAccount, sendingAccountEmail, displayOnly: true, closeAfterDisplay);
            var message = closeAfterDisplay
                ? "El borrador completo se mostró en Outlook y se cerró sin guardarlo ni enviarlo."
                : "El borrador completo se mostró en Outlook y quedó abierto para revisión; no se envió.";
            _logger.Info($"Prueba de borrador completada. Cerrado={closeAfterDisplay}; Para={request.ToRecipients.Count}; CC={request.CcRecipients.Count}; Excel={request.AttachmentPaths.Count}; Firma={!string.IsNullOrWhiteSpace(request.SignatureImagePath)}.");
            return new OutlookDiagnosticResult(true, message);
        }
        catch (Exception ex)
        {
            var failure = OutlookFailureClassifier.Classify(ex, environment, _logger.LogFilePath);
            _logger.Error($"Falló la prueba controlada de borrador. Clasificación={failure.Reason}.", ex);
            return new OutlookDiagnosticResult(false, failure.UserMessage, failure.Reason, failure.HResult);
        }
        finally
        {
            ComObjectHelper.FinalRelease(sendingAccount);
            ComObjectHelper.FinalRelease(application);
        }
    }

    private static void ProcessOne(
        Outlook.Application application,
        EmailSendRequest request,
        Outlook.Account sendingAccount,
        string sendingAccountEmail,
        bool displayOnly,
        bool closeAfterDisplay)
    {
        Outlook.MailItem? mailItem = null;
        Outlook.Recipients? recipients = null;
        Outlook.Attachments? attachments = null;
        Outlook.Attachment? inlineSignature = null;
        Outlook.PropertyAccessor? propertyAccessor = null;
        try
        {
            OutlookApplicationFactory.EnsureSta();
            mailItem = (Outlook.MailItem)application.CreateItem(Outlook.OlItemType.olMailItem);
            SetAndVerifySendingAccount(mailItem, sendingAccount, sendingAccountEmail);
            mailItem.Subject = request.Subject;
            recipients = mailItem.Recipients;

            foreach (var address in request.ToRecipients)
            {
                AddRecipient(recipients, address, Outlook.OlMailRecipientType.olTo);
            }

            foreach (var address in request.CcRecipients)
            {
                AddRecipient(recipients, address, Outlook.OlMailRecipientType.olCC);
            }

            if (!recipients.ResolveAll())
            {
                throw new OutlookIntegrationException(
                    OutlookFailureReason.RecipientResolution,
                    "Outlook no pudo resolver uno o más destinatarios.");
            }

            attachments = mailItem.Attachments;
            foreach (var path in request.AttachmentPaths)
            {
                Outlook.Attachment? attachment = null;
                try
                {
                    attachment = attachments.Add(path, Outlook.OlAttachmentType.olByValue);
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
                {
                    throw new OutlookIntegrationException(
                        OutlookFailureReason.AttachmentFailure,
                        $"No se pudo adjuntar '{Path.GetFileName(path)}'.",
                        ex);
                }
                finally
                {
                    ComObjectHelper.FinalRelease(attachment);
                }
            }

            ConfigureHtmlBody(
                mailItem,
                request.Body,
                request.SignatureImagePath,
                ref attachments,
                ref inlineSignature,
                ref propertyAccessor);

            if (displayOnly)
            {
                mailItem.Display(false);
                if (closeAfterDisplay)
                {
                    mailItem.Close(Outlook.OlInspectorClose.olDiscard);
                }
            }
            else
            {
                mailItem.Send();
            }
        }
        finally
        {
            ComObjectHelper.FinalRelease(propertyAccessor);
            ComObjectHelper.FinalRelease(inlineSignature);
            ComObjectHelper.FinalRelease(attachments);
            ComObjectHelper.FinalRelease(recipients);
            ComObjectHelper.FinalRelease(mailItem);
        }
    }

    private static void ConfigureHtmlBody(
        Outlook.MailItem mailItem,
        string plainText,
        string? signatureImagePath,
        ref Outlook.Attachments? attachments,
        ref Outlook.Attachment? inlineSignature,
        ref Outlook.PropertyAccessor? propertyAccessor)
    {
        OutlookApplicationFactory.EnsureSta();
        SignatureImageInfo? signature = null;
        string? contentId = null;
        if (!string.IsNullOrWhiteSpace(signatureImagePath))
        {
            try
            {
                signature = SignatureImageService.ValidateFile(signatureImagePath);
                contentId = $"ecs-signature-{Guid.NewGuid():N}@local";
                attachments ??= mailItem.Attachments;
                inlineSignature = attachments.Add(
                    signature.Path,
                    Outlook.OlAttachmentType.olByValue,
                    Type.Missing,
                    "Firma");
                propertyAccessor = inlineSignature.PropertyAccessor;
                propertyAccessor.SetProperty(
                    "http://schemas.microsoft.com/mapi/proptag/0x3712001F",
                    contentId);
                propertyAccessor.SetProperty(
                    "http://schemas.microsoft.com/mapi/proptag/0x7FFE000B",
                    true);
                propertyAccessor.SetProperty(
                    "http://schemas.microsoft.com/mapi/proptag/0x370E001F",
                    signature.MimeType);
                propertyAccessor.SetProperty(
                    "http://schemas.microsoft.com/mapi/proptag/0x3713001F",
                    contentId);
                var storedContentId = Convert.ToString(propertyAccessor.GetProperty(
                    "http://schemas.microsoft.com/mapi/proptag/0x3712001F"));
                var storedHidden = Convert.ToBoolean(propertyAccessor.GetProperty(
                    "http://schemas.microsoft.com/mapi/proptag/0x7FFE000B"));
                var storedMime = Convert.ToString(propertyAccessor.GetProperty(
                    "http://schemas.microsoft.com/mapi/proptag/0x370E001F"));
                if (!string.Equals(storedContentId, contentId, StringComparison.Ordinal) ||
                    !storedHidden ||
                    !string.Equals(storedMime, signature.MimeType, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SignatureImageException("Outlook no confirmó las propiedades MAPI de la firma inline.");
                }
            }
            catch (Exception ex) when (ex is SignatureImageException or COMException or IOException or UnauthorizedAccessException)
            {
                throw new OutlookIntegrationException(
                    OutlookFailureReason.SignatureFailure,
                    $"La firma configurada no se pudo insertar: {ex.Message}",
                    ex);
            }
        }

        mailItem.BodyFormat = Outlook.OlBodyFormat.olFormatHTML;
        mailItem.HTMLBody = EmailBodyBuilder.BuildHtml(plainText, signature, contentId);
    }

    private static void AddRecipient(Outlook.Recipients recipients, string address, Outlook.OlMailRecipientType type)
    {
        Outlook.Recipient? recipient = null;
        try
        {
            recipient = recipients.Add(address);
            recipient.Type = (int)type;
        }
        finally
        {
            ComObjectHelper.FinalRelease(recipient);
        }
    }

    private string? ConnectedAccountEmail
    {
        get
        {
            lock (_accountSync)
            {
                return _connectedAccountEmail;
            }
        }
        set
        {
            lock (_accountSync)
            {
                _connectedAccountEmail = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }
    }

    private Outlook.Account ResolveSendingAccount(Outlook.Application application, out string emailAddress)
    {
        Outlook.NameSpace? session = null;
        Outlook.Accounts? accounts = null;
        Outlook.Account? selectedAccount = null;
        var expectedEmail = ConnectedAccountEmail;
        emailAddress = string.Empty;
        try
        {
            OutlookApplicationFactory.EnsureSta();
            session = application.Session;
            accounts = session.Accounts;
            for (var index = 1; index <= accounts.Count; index++)
            {
                Outlook.Account? candidate = null;
                try
                {
                    candidate = accounts[index];
                    var candidateEmail = GetAccountEmail(candidate);
                    if (string.IsNullOrWhiteSpace(candidateEmail) ||
                        (!string.IsNullOrWhiteSpace(expectedEmail) &&
                         !candidateEmail.Equals(expectedEmail, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    selectedAccount = candidate;
                    candidate = null;
                    emailAddress = candidateEmail;
                    break;
                }
                finally
                {
                    ComObjectHelper.FinalRelease(candidate);
                }
            }

            if (selectedAccount is null)
            {
                var detail = string.IsNullOrWhiteSpace(expectedEmail)
                    ? "No se encontró una cuenta de Outlook con dirección SMTP."
                    : $"La cuenta conectada {expectedEmail} ya no está disponible en Outlook.";
                throw new OutlookIntegrationException(
                    OutlookFailureReason.SendingAccountUnavailable,
                    $"{detail} No se envió ningún correo para evitar usar otra cuenta.");
            }

            ConnectedAccountEmail = emailAddress;
            return selectedAccount;
        }
        catch
        {
            ComObjectHelper.FinalRelease(selectedAccount);
            throw;
        }
        finally
        {
            ComObjectHelper.FinalRelease(accounts);
            ComObjectHelper.FinalRelease(session);
        }
    }

    private static void SetAndVerifySendingAccount(
        Outlook.MailItem mailItem,
        Outlook.Account sendingAccount,
        string expectedEmail)
    {
        Outlook.Account? assignedAccount = null;
        try
        {
            mailItem.SendUsingAccount = sendingAccount;
            assignedAccount = mailItem.SendUsingAccount;
            var assignedEmail = assignedAccount is null ? null : GetAccountEmail(assignedAccount);
            if (!string.Equals(assignedEmail, expectedEmail, StringComparison.OrdinalIgnoreCase))
            {
                throw new OutlookIntegrationException(
                    OutlookFailureReason.SendingAccountUnavailable,
                    $"Outlook no confirmó la cuenta de envío {expectedEmail}. " +
                    "No se envió ningún correo para evitar usar otra cuenta.");
            }
        }
        finally
        {
            ComObjectHelper.FinalRelease(assignedAccount);
        }
    }

    private static string? GetAccountEmail(Outlook.Account account)
    {
        var smtpAddress = account.SmtpAddress;
        if (!string.IsNullOrWhiteSpace(smtpAddress))
        {
            return smtpAddress.Trim();
        }

        var displayName = account.DisplayName;
        return !string.IsNullOrWhiteSpace(displayName) && displayName.Contains('@')
            ? displayName.Trim()
            : null;
    }

    private OutlookEnvironmentInfo InspectAndLogEnvironment(string operation)
    {
        var environment = OutlookEnvironmentInspector.Inspect();
        _logger.Info($"Inicio de {operation}.{Environment.NewLine}{environment.ToTechnicalText()}");
        return environment;
    }

    private static void ReportCompleted(
        IProgress<OutlookSendProgress>? progress,
        EmailSendRequest request,
        int current,
        int total,
        EmailSendResult result) =>
        progress?.Report(new OutlookSendProgress
        {
            Request = request,
            Current = current,
            Total = total,
            Stage = OutlookProgressStage.Completed,
            Result = result
        });

    private static IReadOnlyList<EmailSendResult> CreateFailureResults(
        IReadOnlyList<EmailSendRequest> requests,
        IProgress<OutlookSendProgress>? progress,
        string errorMessage)
    {
        var results = new List<EmailSendResult>(requests.Count);
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            progress?.Report(new OutlookSendProgress
            {
                Request = request,
                Current = index + 1,
                Total = requests.Count,
                Stage = OutlookProgressStage.Starting
            });
            var result = new EmailSendResult
            {
                RequestId = request.RequestId,
                WasSuccessful = false,
                ErrorMessage = errorMessage
            };
            results.Add(result);
            ReportCompleted(progress, request, index + 1, requests.Count, result);
        }

        return results;
    }
}
