using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;
using ECS.CommissionsMailer.Services.Expirations;
using ECS.CommissionsMailer.Verification;
using ECS.CommissionsMailer.Views;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authentication;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Configuration;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
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
            if (e.Args.Any(argument =>
                    string.Equals(argument, "--module-ui-smoke-test", StringComparison.OrdinalIgnoreCase)))
            {
                var result = ModuleWindowUiSmokeRunner.Run();
                Environment.ExitCode = result ? 0 : 6;
                Shutdown(Environment.ExitCode);
                return;
            }

            var isUiSmokeTest = e.Args.Any(argument =>
                string.Equals(argument, "--ui-smoke-test", StringComparison.OrdinalIgnoreCase));
            var runCutoverPreflight = CutoverPreflightStartupPolicy.IsManualCommandRequested(e.Args);
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
            await ShowRuntimeWindowAsync(paths, logger, isUiSmokeTest, bindingListener, runCutoverPreflight);
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

    private async Task ShowRuntimeWindowAsync(
        AppDataPaths paths,
        FileLogger logger,
        bool isUiSmokeTest,
        TextWriterTraceListener? bindingListener,
        bool runCutoverPreflight = false,
        AuthenticatedModuleContext? authenticatedContext = null,
        ApplicationModule? requestedModule = null)
    {
        var options = authenticatedContext?.Options ?? FirebaseClientOptions.Load(paths.FirebaseRuntimeConfigurationFile);
        logger.Info($"RuntimeDataMode={options.RuntimeDataMode}.");
        IRuntimeDataService runtimeData;
        IFirebaseAuthenticationService? authentication = authenticatedContext?.Authentication;
        IAppUserRepository? appUsers = authenticatedContext?.AppUsers;
        var moduleContext = authenticatedContext;

        if (options.RuntimeDataMode == RuntimeDataMode.JsonOnly)
        {
            if (runCutoverPreflight)
            {
                throw new InvalidOperationException(
                    "El Cutover Preflight requiere RuntimeDataMode=FirestoreShadowRead para autenticar y leer Firestore.");
            }
            if (requestedModule == ApplicationModule.Expirations)
                throw new InvalidOperationException("Vencimientos requiere el runtime autenticado de Firebase.");
            runtimeData = new JsonOnlyRuntimeDataService(paths, logger);
        }
        else
        {
            options.ValidateForAuthenticatedMode();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ApplicationModule selectedModule;
            if (moduleContext is null)
            {
                var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var clientLog = new FirebaseClientLogAdapter(logger);
                authentication = new FirebaseAuthenticationService(
                    httpClient,
                    options,
                    new ProtectedRefreshTokenStore(paths.ProtectedRefreshTokenFile),
                    clientLog);

                FirebaseUserSession? session = null;
                try
                {
                    session = await authentication.RestoreSessionAsync();
                }
                catch (FirebaseAuthenticationException ex) when (
                    ex.Failure is FirebaseAuthenticationFailure.SessionExpired or FirebaseAuthenticationFailure.InvalidCredentials)
                {
                    logger.Info("No se recuperó una sesión Firebase válida; se mostrará el login.");
                }

                if (session is null)
                {
                    var login = new LoginWindow(authentication);
                    if (login.ShowDialog() != true)
                    {
                        Shutdown();
                        return;
                    }
                    session = login.Session!;
                }

                var firestoreClient = new FirestoreRestClient(
                    httpClient,
                    options,
                    (IFirebaseTokenProvider)authentication,
                    clientLog);
                appUsers = new AppUserRepository(firestoreClient);
                var profile = await appUsers.GetAsync(session.Uid);
                ModuleAccessResolution moduleResolution;
                try
                {
                    if (runCutoverPreflight)
                    {
                        AppUserAuthorization.DemandCommissionsAccess(profile?.Value);
                        moduleResolution = new ModuleAccessResolution(ApplicationModule.Commissions, RequiresSelection: false);
                    }
                    else
                    {
                        moduleResolution = ModuleAccessResolver.Resolve(profile?.Value);
                    }
                }
                catch
                {
                    await authentication.SignOutAsync();
                    throw;
                }

                ApplicationModule? initialModule = requestedModule ?? moduleResolution.DirectModule;
                if (requestedModule is null && moduleResolution.RequiresSelection)
                {
                    var selector = new ModuleSelectionWindow(profile!.Value);
                    _ = selector.ShowDialog();
                    if (selector.LogoutRequested)
                    {
                        await authentication.SignOutAsync();
                        await ShowRuntimeWindowAsync(paths, logger, isUiSmokeTest, bindingListener);
                        return;
                    }
                    initialModule = ModuleAccessResolver.ValidateSelection(profile.Value, selector.SelectedModule);
                    if (initialModule is null)
                    {
                        DisposeBindingListener(bindingListener);
                        Shutdown();
                        return;
                    }
                }

                selectedModule = initialModule ?? throw new InvalidOperationException("No se seleccionó un módulo.");
                try
                {
                    ModuleAccessResolver.DemandModuleAccess(profile!.Value, selectedModule);
                }
                catch
                {
                    await authentication.SignOutAsync();
                    throw;
                }
                moduleContext = new AuthenticatedModuleContext(
                    options,
                    authentication,
                    firestoreClient,
                    appUsers,
                    profile.Value);
            }
            else
            {
                selectedModule = requestedModule ?? throw new InvalidOperationException(
                    "La navegación autenticada requiere indicar el módulo destino.");
                ModuleAccessResolver.DemandModuleAccess(moduleContext.CurrentUser, selectedModule);
                authentication = moduleContext.Authentication;
                appUsers = moduleContext.AppUsers;
            }

            if (!ModuleAccessResolver.UsesCommissionsRuntime(selectedModule))
            {
                var directoryRepository = new ExpirationsBrokerDirectoryRepository(moduleContext.FirestoreClient);
                var profileRepository = new ExpirationsBrokerProfileRepository(moduleContext.FirestoreClient);
                var associationRepository = new ExpirationsBrokerAssociationRepository(moduleContext.FirestoreClient);
                var exclusionRepository = new ExpirationsExclusionRepository(moduleContext.FirestoreClient);
                var settingsRepository = new ExpirationsProcessSettingsRepository(moduleContext.FirestoreClient);
                var sendHistoryRepository = new ExpirationsSendHistoryRepository(moduleContext.FirestoreClient);
                var catalogService = new ExpirationsBrokerCatalogService(
                    directoryRepository,
                    profileRepository);
                var configurationService = new ExpirationsBrokerConfigurationService(
                    directoryRepository,
                    profileRepository);
                var coordinator = new ExpirationsAnalysisCoordinator(
                    catalogService,
                    associationRepository,
                    exclusions: exclusionRepository);
                var routingAdministrationService = new ExpirationsRoutingAdministrationService(
                    associationRepository,
                    exclusionRepository,
                    configurationService);
                var generationService = new ExpirationsGenerationService();
                var emailSettingsService = new ExpirationsEmailSettingsService(settingsRepository);
                var sendPreparationService = new ExpirationsSendPreparationService(
                    settingsRepository,
                    catalogService);
                var outlookSender = new ExpirationsOutlookSender(new OutlookEmailService(paths, logger));
                ShowExpirationsWindow(
                    paths,
                    logger,
                    isUiSmokeTest,
                    bindingListener,
                    moduleContext,
                    coordinator,
                    configurationService,
                    generationService,
                    emailSettingsService,
                    sendPreparationService,
                    outlookSender,
                    sendHistoryRepository,
                    routingAdministrationService);
                return;
            }

            var json = new JsonOnlyRuntimeDataService(paths, logger);
            var legacySnapshot = await json.LoadAsync();
            var comparison = new FirestoreComparisonService(moduleContext.FirestoreClient, paths, logger);
            var runtimeState = options.RuntimeDataMode == RuntimeDataMode.FirestorePrimary
                ? new FirestoreRuntimeStateStore(paths, logger).Load()
                : null;
            var preflightExecution = CutoverPreflightStartupPolicy.Determine(
                runCutoverPreflight,
                options,
                runtimeState);

            if (preflightExecution == CutoverPreflightExecution.Manual)
            {
                var report = await comparison.RunAsync(legacySnapshot, true);
                MessageBox.Show(
                    $"Cutover Preflight completado.\n\nLocal: {report.LocalCount}\nFirestore: {report.FirestoreCount}\n" +
                    $"Idénticos funcionalmente: {report.Identical}\n" +
                    $"Metadata no bloqueante: {report.NonBlockingMetadataDifferences}\n" +
                    $"Faltan en Firestore: {report.MissingInFirestore}\n" +
                    $"Faltan localmente: {report.MissingLocally}\nDiferentes: {report.Different}\n\n" +
                    $"Resultado: {(report.CanCutOver ? "LIMPIO" : "BLOQUEADO")}\n\nReporte: {report.ReportPath}",
                    "Cutover Preflight",
                    MessageBoxButton.OK,
                    report.CanCutOver ? MessageBoxImage.Information : MessageBoxImage.Warning);
                Shutdown(report.CanCutOver ? 0 : 5);
                return;
            }

            if (options.RuntimeDataMode == RuntimeDataMode.FirestoreShadowRead)
            {
                runtimeData = new ShadowReadRuntimeDataService(json, comparison, moduleContext.CurrentUser);
            }
            else
            {
                if (preflightExecution == CutoverPreflightExecution.RequiredLegacyTransition)
                {
                    var preflight = await comparison.RunAsync(legacySnapshot, true);
                    CutoverPreflightStartupPolicy.DemandLegacyPreflightPassed(
                        preflightExecution,
                        preflight.CanCutOver,
                        preflight.ReportPath);
                }

                runtimeData = new FirestorePrimaryRuntimeDataService(
                    moduleContext.FirestoreClient, moduleContext.CurrentUser, legacySnapshot, paths, logger);
            }
        }

        var snapshot = await runtimeData.LoadAsync();
        var mainWindow = new MainWindow(
            paths,
            logger,
            runtimeData,
            snapshot,
            authentication,
            appUsers,
            isUiSmokeTest);
        MainWindow = mainWindow;
        var restarting = false;
        if (authentication is not null)
        {
            mainWindow.LogoutRequested += (_, _) =>
            {
                restarting = true;
                _ = Dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        await ShowRuntimeWindowAsync(paths, logger, isUiSmokeTest, null);
                    }
                    catch (Exception ex)
                    {
                        logger.Error("No fue posible volver al login después del logout.", ex);
                        MessageBox.Show(ex.Message, "ECS Envío de Correos", MessageBoxButton.OK, MessageBoxImage.Error);
                        Shutdown(1);
                    }
                });
            };
            if (moduleContext is not null)
            {
                mainWindow.ModuleSwitchRequested += (_, _) =>
                {
                    restarting = true;
                    _ = Dispatcher.BeginInvoke(async () =>
                    {
                        try
                        {
                            await ShowRuntimeWindowAsync(
                                paths,
                                logger,
                                isUiSmokeTest,
                                null,
                                authenticatedContext: moduleContext,
                                requestedModule: ApplicationModule.Expirations);
                        }
                        catch (Exception ex)
                        {
                            logger.Error("No fue posible cambiar al módulo de Vencimientos.", ex);
                            MessageBox.Show(ex.Message, "Cambiar módulo", MessageBoxButton.OK, MessageBoxImage.Error);
                            Shutdown(1);
                        }
                    });
                };
            }
        }

        mainWindow.Closed += (_, _) =>
        {
            if (bindingListener is not null)
            {
                bindingListener.Flush();
                PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
                bindingListener.Dispose();
            }
            if (ShutdownMode == ShutdownMode.OnExplicitShutdown && !restarting)
                Shutdown();
        };
        mainWindow.Show();
    }

    private void ShowExpirationsWindow(
        AppDataPaths paths,
        FileLogger logger,
        bool isUiSmokeTest,
        TextWriterTraceListener? bindingListener,
        AuthenticatedModuleContext moduleContext,
        IExpirationsAnalysisCoordinator coordinator,
        IExpirationsBrokerConfigurationService configurationService,
        IExpirationsGenerationService generationService,
        IExpirationsEmailSettingsService emailSettingsService,
        ExpirationsSendPreparationService sendPreparationService,
        IExpirationsOutlookSender outlookSender,
        IExpirationsSendHistoryRepository sendHistory,
        IExpirationsRoutingAdministrationService routingAdministrationService)
    {
        var expirationsWindow = new ExpirationsWindow(
            moduleContext.CurrentUser,
            moduleContext.AppUsers,
            coordinator,
            configurationService,
            generationService,
            emailSettingsService,
            sendPreparationService,
            outlookSender,
            sendHistory,
            paths,
            logger,
            routingAdministrationService: routingAdministrationService);
        MainWindow = expirationsWindow;
        var restarting = false;
        expirationsWindow.LogoutRequested += (_, _) =>
        {
            restarting = true;
            _ = Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await moduleContext.Authentication.SignOutAsync();
                    await ShowRuntimeWindowAsync(paths, logger, isUiSmokeTest, null);
                }
                catch (Exception ex)
                {
                    logger.Error("No fue posible volver al login después del logout.", ex);
                    MessageBox.Show(ex.Message, "ECS Envío de Correos", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1);
                }
            });
        };

        expirationsWindow.ModuleSwitchRequested += (_, _) =>
        {
            restarting = true;
            _ = Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await ShowRuntimeWindowAsync(
                        paths,
                        logger,
                        isUiSmokeTest,
                        null,
                        authenticatedContext: moduleContext,
                        requestedModule: ApplicationModule.Commissions);
                }
                catch (Exception ex)
                {
                    logger.Error("No fue posible cambiar al módulo de Comisiones.", ex);
                    MessageBox.Show(ex.Message, "Cambiar módulo", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1);
                }
            });
        };

        expirationsWindow.Closed += (_, _) =>
        {
            DisposeBindingListener(bindingListener);
            if (ShutdownMode == ShutdownMode.OnExplicitShutdown && !restarting)
                Shutdown();
        };
        expirationsWindow.Show();
    }

    private static void DisposeBindingListener(TextWriterTraceListener? bindingListener)
    {
        if (bindingListener is null) return;
        bindingListener.Flush();
        PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
        bindingListener.Dispose();
    }

    private sealed record AuthenticatedModuleContext(
        FirebaseClientOptions Options,
        IFirebaseAuthenticationService Authentication,
        IFirestoreRestClient FirestoreClient,
        IAppUserRepository AppUsers,
        AppUser CurrentUser);
}
