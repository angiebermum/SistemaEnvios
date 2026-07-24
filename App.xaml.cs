using System.Diagnostics;
using System.Windows;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;
using ECS.CommissionsMailer.Verification;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(argument => string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            var result = SelfTestRunner.Run();
            Environment.ExitCode = result ? 0 : 1;
            Shutdown(Environment.ExitCode);
            return;
        }

        var draftTestIndex = Array.FindIndex(e.Args, argument =>
            string.Equals(argument, "--outlook-draft-test", StringComparison.OrdinalIgnoreCase));
        if (draftTestIndex >= 0)
        {
            var paths = new AppDataPaths();
            var logger = new FileLogger(paths);
            var resultFile = Path.Combine(Path.GetTempPath(), "ECSCommissionsMailer-outlook-draft-test-result.txt");
            OutlookDiagnosticResult result;
            if (draftTestIndex + 3 >= e.Args.Length)
            {
                result = new OutlookDiagnosticResult(false,
                    "Uso: --outlook-draft-test <correo-controlado> <firma.png|jpg> <prueba.xlsx>");
            }
            else
            {
                var request = new EmailSendRequest
                {
                    BrokerName = "Prueba controlada",
                    BrokerPrimaryRecipients = [e.Args[draftTestIndex + 1]],
                    ToRecipients = [e.Args[draftTestIndex + 1]],
                    Subject = "PRUEBA CONTROLADA — ECS ENVÍO DE CORREOS",
                    Body = "Prueba segura de formato HTML.\n\nLa firma debe aparecer inline al final del mensaje.",
                    AttachmentPaths = [e.Args[draftTestIndex + 3]],
                    SignatureImagePath = e.Args[draftTestIndex + 2]
                };
                var errors = new EmailValidationService().ValidateRequest(request);
                result = errors.Count > 0
                    ? new OutlookDiagnosticResult(false, string.Join(Environment.NewLine, errors))
                    : new OutlookEmailService(logger).DisplayDraftAsync(request, closeAfterDisplay: true)
                        .GetAwaiter().GetResult();
            }

            File.WriteAllText(resultFile, string.Join(Environment.NewLine,
                $"RESULTADO={(result.WasSuccessful ? "CORRECTO" : "ERROR")}",
                $"MENSAJE={result.Message}",
                "MAILITEM_SEND=NO",
                "BORRADOR_DESCARTADO=SI",
                $"REGISTRO={logger.LogFilePath}"));
            Environment.ExitCode = result.WasSuccessful ? 0 : 3;
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Any(argument => string.Equals(argument, "--outlook-diagnostic", StringComparison.OrdinalIgnoreCase)))
        {
            var paths = new AppDataPaths();
            var logger = new FileLogger(paths);
            logger.Info("Inicio del diagnóstico independiente de Outlook.");
            var result = new OutlookEmailService(logger)
                .TestConnectionAsync(closeDraftAfterDisplay: true)
                .GetAwaiter()
                .GetResult();
            var resultFile = Path.Combine(Path.GetTempPath(), "ECSCommissionsMailer-outlook-diagnostic-result.txt");
            File.WriteAllText(resultFile, string.Join(Environment.NewLine,
                $"RESULTADO={(result.WasSuccessful ? "CORRECTO" : "ERROR")}",
                $"MENSAJE={result.Message}",
                $"CLASIFICACION={result.FailureReason}",
                $"HRESULT={(result.HResult.HasValue ? $"0x{unchecked((uint)result.HResult.Value):X8}" : "(ninguno)")}",
                $"REGISTRO={logger.LogFilePath}"));
            Environment.ExitCode = result.WasSuccessful ? 0 : 2;
            Shutdown(Environment.ExitCode);
            return;
        }

        try
        {
            var isUiSmokeTest = e.Args.Any(argument =>
                string.Equals(argument, "--ui-smoke-test", StringComparison.OrdinalIgnoreCase));
            TextWriterTraceListener? bindingListener = null;
            if (isUiSmokeTest)
            {
                var bindingLog = Path.Combine(Path.GetTempPath(), "ECSCommissionsMailer-ui-binding-errors.log");
                File.WriteAllText(bindingLog, string.Empty);
                bindingListener = new TextWriterTraceListener(bindingLog);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
                PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
            }
            var paths = new AppDataPaths();
            var logger = new FileLogger(paths);
            logger.Info("Inicio de ECS Envío de Correos.");
            var mainWindow = new MainWindow(paths, logger, isUiSmokeTest);
            MainWindow = mainWindow;
            if (bindingListener is not null)
            {
                mainWindow.Closed += (_, _) =>
                {
                    bindingListener.Flush();
                    PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
                    bindingListener.Dispose();
                };
            }
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No fue posible iniciar la aplicación.\n\n{ex.Message}",
                "ECS Envío de Correos",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
