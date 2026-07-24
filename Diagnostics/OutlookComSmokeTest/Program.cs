using System.Runtime.InteropServices;
using Outlook = Microsoft.Office.Interop.Outlook;

var exitCode = 1;
Exception? failure = null;
var thread = new Thread(() =>
{
    Outlook.Application? application = null;
    Outlook.NameSpace? session = null;
    Outlook.NameSpace? mapi = null;
    Outlook.Accounts? accounts = null;
    Outlook.Recipient? currentUser = null;
    Outlook.MailItem? mailItem = null;
    try
    {
        Console.WriteLine($"Thread={Environment.CurrentManagedThreadId}; Apartment={Thread.CurrentThread.GetApartmentState()}");
        var type = Type.GetTypeFromProgID("Outlook.Application", throwOnError: false);
        Console.WriteLine($"ProgID={(type is null ? "NO" : "SI")}; CLSID={type?.GUID:B}");
        if (type is null)
        {
            throw new InvalidOperationException("Outlook.Application no está registrado.");
        }

        application = (Outlook.Application)(Activator.CreateInstance(type) ??
            throw new InvalidOperationException("Activator.CreateInstance devolvió null."));
        session = application.Session;
        mapi = application.GetNamespace("MAPI");
        accounts = session.Accounts;
        currentUser = session.CurrentUser;
        Console.WriteLine($"Session=SI; MAPI=SI; Accounts={accounts.Count}; CurrentUser={(currentUser is null ? "NO" : "SI")}");

        mailItem = (Outlook.MailItem)application.CreateItem(Outlook.OlItemType.olMailItem);
        mailItem.Subject = "PRUEBA DE CONEXIÓN - ECS ENVÍO DE CORREOS";
        mailItem.Display(false);
        Console.WriteLine("Display=SI");
        mailItem.Close(Outlook.OlInspectorClose.olDiscard);
        Console.WriteLine("CloseWithoutSave=SI");
        exitCode = 0;
    }
    catch (Exception ex)
    {
        failure = ex;
    }
    finally
    {
        Release(mailItem);
        Release(currentUser);
        Release(accounts);
        Release(mapi);
        Release(session);
        Release(application);
    }
});

thread.Name = "Outlook COM smoke test STA";
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

if (failure is not null)
{
    Console.Error.WriteLine($"{failure.GetType().FullName}: {failure.Message}");
    Console.Error.WriteLine($"HResult={failure.HResult} (0x{unchecked((uint)failure.HResult):X8})");
    Console.Error.WriteLine(failure);
}

return exitCode;

static void Release(object? value)
{
    if (value is null || !Marshal.IsComObject(value))
    {
        return;
    }

    try
    {
        Marshal.FinalReleaseComObject(value);
    }
    catch
    {
        // La limpieza no debe sustituir la excepción original.
    }
}
