using System.Runtime.InteropServices;
using ECS.CommissionsMailer.Helpers;
using ECS.CommissionsMailer.Models;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ECS.CommissionsMailer.Services;

internal sealed class OutlookApplicationFactory
{
    private const int MonikerUnavailable = unchecked((int)0x800401E3);
    private readonly FileLogger _logger;

    public OutlookApplicationFactory(FileLogger logger) => _logger = logger;

    public Outlook.Application CreateValidatedApplication()
    {
        EnsureSta();
        var type = Type.GetTypeFromProgID("Outlook.Application", throwOnError: false);
        if (type is null)
        {
            throw new OutlookIntegrationException(
                OutlookFailureReason.ProgIdNotRegistered,
                "El ProgID Outlook.Application no está registrado.");
        }

        Outlook.Application? application = null;
        try
        {
            var source = "tabla de objetos en ejecución";
            application = TryGetActiveApplication(type.GUID);
            if (application is null)
            {
                source = "activación COM";
                application = (Outlook.Application)(Activator.CreateInstance(type) ??
                    throw new InvalidOperationException("Activator.CreateInstance devolvió null para Outlook.Application."));
            }

            ValidateSessionAndMailItem(application, source);
            return application;
        }
        catch
        {
            ComObjectHelper.FinalRelease(application);
            throw;
        }
    }

    internal static void EnsureSta()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new OutlookIntegrationException(
                OutlookFailureReason.StaViolation,
                $"Outlook requiere STA; el hilo actual es {Thread.CurrentThread.GetApartmentState()}.");
        }
    }

    private void ValidateSessionAndMailItem(Outlook.Application application, string source)
    {
        Outlook.NameSpace? session = null;
        Outlook.Accounts? accounts = null;
        Outlook.Recipient? currentUser = null;
        Outlook.NameSpace? mapi = null;
        Outlook.MailItem? testItem = null;
        var accountCount = 0;
        try
        {
            session = application.Session;
            accounts = session.Accounts;
            accountCount = accounts.Count;
            if (accountCount == 0)
            {
                throw new OutlookIntegrationException(
                    OutlookFailureReason.ProfileUnavailable,
                    "La sesión MAPI no contiene cuentas configuradas.");
            }

            currentUser = session.CurrentUser;
            if (currentUser is null)
            {
                throw new OutlookIntegrationException(
                    OutlookFailureReason.ProfileUnavailable,
                    "Session.CurrentUser no está disponible.");
            }
        }
        finally
        {
            ComObjectHelper.FinalRelease(currentUser);
            ComObjectHelper.FinalRelease(accounts);
            ComObjectHelper.FinalRelease(session);
        }

        try
        {
            mapi = application.GetNamespace("MAPI");
            if (mapi is null)
            {
                throw new OutlookIntegrationException(
                    OutlookFailureReason.ProfileUnavailable,
                    "GetNamespace(\"MAPI\") devolvió null.");
            }
        }
        finally
        {
            ComObjectHelper.FinalRelease(mapi);
        }

        try
        {
            testItem = (Outlook.MailItem)application.CreateItem(Outlook.OlItemType.olMailItem);
            testItem.Subject = "PRUEBA INTERNA DE INICIALIZACIÓN - ECS ENVÍO DE CORREOS";
            testItem.Close(Outlook.OlInspectorClose.olDiscard);
            _logger.Info($"Outlook inicializado por {source}. Sesión MAPI disponible; cuentas={accountCount}; MailItem=correcto.");
        }
        finally
        {
            ComObjectHelper.FinalRelease(testItem);
        }
    }

    private static Outlook.Application? TryGetActiveApplication(Guid clsid)
    {
        object? activeObject = null;
        try
        {
            GetActiveObject(ref clsid, IntPtr.Zero, out activeObject);
            return activeObject as Outlook.Application;
        }
        catch (COMException ex) when (ex.HResult == MonikerUnavailable)
        {
            return null;
        }
        finally
        {
            if (activeObject is not null && activeObject is not Outlook.Application)
            {
                ComObjectHelper.FinalRelease(activeObject);
            }
        }
    }

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid classId,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.Interface)] out object activeObject);
}
