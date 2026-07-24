using System.Runtime.InteropServices;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

internal sealed record OutlookFailure(OutlookFailureReason Reason, string UserMessage, int HResult);

internal static class OutlookFailureClassifier
{
    private const int ClassNotRegistered = unchecked((int)0x80040154);
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int OperationAborted = unchecked((int)0x80004004);

    public static OutlookFailure Classify(Exception exception, OutlookEnvironmentInfo environment, string logPath)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var integrationException = FindIntegrationException(exception);
        var relevantException = integrationException?.InnerException ?? integrationException ?? exception;
        var hResult = relevantException.HResult;
        var reason = integrationException?.Reason ?? ClassifyCore(relevantException, environment);
        var message = CreateUserMessage(reason, hResult, environment, relevantException);
        return new OutlookFailure(reason, $"{message}{Environment.NewLine}Consulte el registro técnico en: {logPath}", hResult);
    }

    private static OutlookFailureReason ClassifyCore(Exception exception, OutlookEnvironmentInfo environment)
    {
        if (exception is BadImageFormatException && environment.ArchitectureMismatch)
        {
            return OutlookFailureReason.ArchitectureMismatch;
        }

        if (exception is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
        {
            return OutlookFailureReason.IntegrationAssemblyMissing;
        }

        if (exception is COMException { HResult: ClassNotRegistered })
        {
            return OutlookFailureReason.ComClassNotRegistered;
        }

        if (environment.ElevationMismatch && exception is COMException)
        {
            return OutlookFailureReason.ElevationMismatch;
        }

        if (exception.Message.Contains("dialog box is open", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("cuadro de diálogo", StringComparison.OrdinalIgnoreCase))
        {
            return OutlookFailureReason.ModalDialog;
        }

        if (exception.HResult is AccessDenied or OperationAborted)
        {
            return OutlookFailureReason.SecurityOrPolicyRestriction;
        }

        return exception is COMException ? OutlookFailureReason.ComFailure : OutlookFailureReason.UnexpectedError;
    }

    private static string CreateUserMessage(
        OutlookFailureReason reason,
        int hResult,
        OutlookEnvironmentInfo environment,
        Exception exception)
    {
        var code = $"0x{unchecked((uint)hResult):X8}";
        return reason switch
        {
            OutlookFailureReason.ProgIdNotRegistered =>
                "No se encontró el registro COM de Outlook clásico (Outlook.Application).",
            OutlookFailureReason.ComClassNotRegistered =>
                $"No fue posible crear la instancia COM de Outlook porque la clase no está registrada. Código: {code}.",
            OutlookFailureReason.IntegrationAssemblyMissing =>
                $"No fue posible cargar los tipos de interoperabilidad de Outlook. Código: {code}.",
            OutlookFailureReason.ArchitectureMismatch =>
                $"La aplicación ({environment.ProcessArchitecture}) y Outlook ({environment.OutlookArchitecture}) usan arquitecturas incompatibles.",
            OutlookFailureReason.ProfileUnavailable =>
                "Outlook inició, pero no se encontró un perfil o una cuenta de correo configurada.",
            OutlookFailureReason.SendingAccountUnavailable =>
                exception.Message,
            OutlookFailureReason.ElevationMismatch =>
                "Outlook está abierto con un nivel de permisos diferente. Cierre ambos programas y ábralos normalmente, sin Ejecutar como administrador.",
            OutlookFailureReason.StaViolation =>
                "La automatización de Outlook se ejecutó fuera de un hilo STA.",
            OutlookFailureReason.RecipientResolution =>
                "Outlook no pudo resolver uno o más destinatarios. El correo no fue enviado.",
            OutlookFailureReason.AttachmentFailure =>
                $"No fue posible agregar uno de los archivos adjuntos. {exception.Message}",
            OutlookFailureReason.SignatureFailure =>
                $"No fue posible agregar la firma del correo. {exception.Message}",
            OutlookFailureReason.SecurityOrPolicyRestriction =>
                $"Outlook o una directiva de seguridad bloqueó la operación. Código: {code}.",
            OutlookFailureReason.ModalDialog =>
                "Outlook tiene un cuadro de diálogo abierto. Ciérrelo y vuelva a intentar la prueba.",
            OutlookFailureReason.ComFailure =>
                $"Outlook devolvió un error de automatización COM. Código: {code}.",
            _ => $"Ocurrió un error inesperado al automatizar Outlook. Código: {code}."
        };
    }

    private static OutlookIntegrationException? FindIntegrationException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is OutlookIntegrationException integrationException)
            {
                return integrationException;
            }
        }

        return null;
    }
}
