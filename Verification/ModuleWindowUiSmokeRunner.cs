using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services;
using ECS.CommissionsMailer.Services.Expirations;
using ECS.CommissionsMailer.Views;

namespace ECS.CommissionsMailer.Verification;

internal static class ModuleWindowUiSmokeRunner
{
    public static bool Run()
    {
        var bindingLogPath = Path.Combine(
            Path.GetTempPath(),
            "ECSCommissionsMailer-module-ui-binding-errors.log");
        var resultPath = Path.Combine(
            Path.GetTempPath(),
            "ECSCommissionsMailer-module-ui-smoke-result.txt");
        File.WriteAllText(bindingLogPath, string.Empty);
        using var bindingListener = new TextWriterTraceListener(bindingLogPath);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
        var previousShutdownMode = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var user = new AppUser
            {
                Uid = "ui-smoke",
                Email = "ui-smoke@example.test",
                DisplayName = "Usuario de prueba visual",
                Role = AppUserRole.Admin,
                IsActive = true,
                CanUseCommissions = true,
                CanUseExpirations = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            var selectorPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-module-selection-ui-smoke.png");
            var expirationsInitialPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-initial-ui-smoke.png");
            var expirationsReadyPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-ready-ui-smoke.png");
            var expirationsPendingPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-pending-ui-smoke.png");
            var expirationsSinglePermissionPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-single-permission-ui-smoke.png");
            var resolutionDialogPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-resolution-ui-smoke.png");
            var selectionDialogPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-selection-ui-smoke.png");
            var premiumSelectionDialogPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-premium-selection-ui-smoke.png");
            var brokerManagementPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-broker-management-ui-smoke.png");
            var routingAdministrationPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-routing-administration-ui-smoke.png");
            var exclusionsPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-exclusions-ui-smoke.png");
            var associationEditorPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-association-editor-ui-smoke.png");
            var defaultProfilePath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-default-profile-ui-smoke.png");
            var configuredProfilePath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-configured-profile-ui-smoke.png");
            var assistantEditorPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-assistant-editor-ui-smoke.png");
            var missingEmailProfilePath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-missing-email-profile-ui-smoke.png");
            var generationBusyPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-generation-busy-ui-smoke.png");
            var generationCompletedPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-generation-completed-ui-smoke.png");
            var nextMonthReadyPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-next-month-ready-ui-smoke.png");
            var nextMonthNoPeriodPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-next-month-no-period-ui-smoke.png");
            var emailSettingsPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-email-settings-ui-smoke.png");
            var generatedFilesPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-associated-files-ui-smoke.png");
            var sendReviewNormalPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-send-review-normal-ui-smoke.png");
            var sendReviewSpecialPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-send-review-special-ui-smoke.png");
            var sendReviewBusyPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-send-review-busy-ui-smoke.png");
            var sendReviewResultPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-send-review-result-ui-smoke.png");
            var sendHistoryMixedPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-send-history-mixed-ui-smoke.png");
            var sendHistoryInProgressPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-send-history-in-progress-ui-smoke.png");

            Render(new ModuleSelectionWindow(user), selectorPath);
            RenderAndValidateExpirationsShell(
                ExpirationsWindow(user, InitialSnapshot()),
                expirationsInitialPath,
                expectModuleSwitch: true);
            var singlePermissionUser = new AppUser
            {
                Uid = "ui-smoke-single",
                Email = "ui-smoke-single@example.test",
                DisplayName = "Usuario solo Vencimientos",
                Role = AppUserRole.Operator,
                IsActive = true,
                CanUseCommissions = false,
                CanUseExpirations = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            RenderAndValidateExpirationsShell(
                ExpirationsWindow(singlePermissionUser, InitialSnapshot()),
                expirationsSinglePermissionPath,
                expectModuleSwitch: false);
            var readySnapshot = ReadySnapshot();
            Render(
                ExpirationsWindow(user, readySnapshot),
                expirationsReadyPath);
            var pendingSnapshot = PendingSnapshot();
            Render(
                ExpirationsWindow(user, pendingSnapshot),
                expirationsPendingPath);
            Render(
                new ExpirationsBrokerResolutionWindow(
                    pendingSnapshot.PendingIssues[0],
                    pendingSnapshot.Catalog),
                resolutionDialogPath);
            Render(
                new ExpirationsWorkbookSelectionWindow(Inspection()),
                selectionDialogPath);
            Render(
                new ExpirationsPremiumColumnSelectionWindow(Inspection(), "Hoja2", 1U),
                premiumSelectionDialogPath);
            var configurationItems = ConfigurationItems();
            var configurationService = new SmokeConfigurationService(configurationItems);
            var routingService = new SmokeRoutingAdministrationService(configurationItems);
            Render(
                new ExpirationsBrokerManagementWindow(
                    configurationService,
                    routingService,
                    ExpirationsProcess.PreviousMonth),
                brokerManagementPath);
            Render(
                new ExpirationsRoutingAdministrationWindow(
                    routingService,
                    configurationService),
                routingAdministrationPath);
            Render(new ExpirationsExclusionsWindow(routingService), exclusionsPath);
            Render(new ExpirationsAssociationEditorWindow(configurationItems[0]), associationEditorPath);
            Render(
                new ExpirationsBrokerProfileWindow(
                    configurationService,
                    configurationItems[0],
                    ExpirationsProcess.PreviousMonth),
                defaultProfilePath);
            Render(
                new ExpirationsBrokerProfileWindow(
                    configurationService,
                    configurationItems[1],
                    ExpirationsProcess.NextMonth),
                configuredProfilePath);
            Render(
                new ExpirationsAssistantEditorWindow(
                    new ExpirationsAssistantValidationService(),
                    configurationItems[1].Assistants[0]),
                assistantEditorPath);
            Render(
                new ExpirationsBrokerProfileWindow(configurationService, configurationItems[2]),
                missingEmailProfilePath);
            var busyWindow = ExpirationsWindow(user, ReadySnapshot());
            ((ExpirationsWindowState)busyWindow.DataContext).SetBusy(true, "Generando archivo 1 de 2...");
            RenderAtElement(busyWindow, "GenerationSection", generationBusyPath);
            var completedWindow = ExpirationsWindow(user, ReadySnapshot());
            RenderAtElement(
                completedWindow,
                "GenerationSection",
                generationCompletedPath,
                shownWindow => ((ExpirationsWindowState)shownWindow.DataContext).ApplyGenerationBatch(
                    new ExpirationsGenerationBatch
                    {
                        OutputDirectory = @"C:\Pruebas\Pendientes mes anterior - 20260821-120000",
                        Files = [new ExpirationsGeneratedFile(), new ExpirationsGeneratedFile()],
                        Warnings = ["El corredor 'Corredor sin correo' no tiene correo principal válido."]
                    }));
            RenderAtElement(
                ExpirationsWindow(user, ReadySnapshot(ExpirationsProcess.NextMonth)),
                "GenerationSection",
                nextMonthNoPeriodPath);
            var nextMonthWindow = ExpirationsWindow(user, ReadySnapshot(ExpirationsProcess.NextMonth));
            var nextMonthState = (ExpirationsWindowState)nextMonthWindow.DataContext;
            nextMonthState.SelectedMonthOption = nextMonthState.MonthOptions.Single(option => option.Month == 8);
            nextMonthState.NextMonthYearText = "2026";
            var nextMonthBatch = new ExpirationsGenerationBatch
            {
                OutputDirectory = @"C:\Pruebas\Vencimientos mes siguiente - 2026-08 - 20260821-120000",
                Files =
                [
                    new ExpirationsGeneratedFile { BrokerId = Guid.Parse("11111111-1111-1111-1111-111111111111") },
                    new ExpirationsGeneratedFile { BrokerId = Guid.Parse("22222222-2222-2222-2222-222222222222"), Variant = ExpirationsGeneratedFileVariant.FelixAlphabetical },
                    new ExpirationsGeneratedFile { BrokerId = Guid.Parse("22222222-2222-2222-2222-222222222222"), Variant = ExpirationsGeneratedFileVariant.FelixExpirationDate }
                ]
            };
            RenderAtElement(
                nextMonthWindow,
                "GenerationSection",
                nextMonthReadyPath,
                shownWindow =>
                {
                    var state = (ExpirationsWindowState)shownWindow.DataContext;
                    state.SetGenerationReadiness(
                        true,
                        "Configuración especial, período y primas validados. Listo para generar.",
                        false);
                    state.ApplyGenerationBatch(nextMonthBatch);
                });
            var smokeDataPaths = new AppDataPaths(Path.Combine(
                Path.GetTempPath(),
                $"ECSCommissionsMailer-expirations-files-smoke-{Guid.NewGuid():N}"));
            var smokeLogger = new FileLogger(smokeDataPaths);
            IReadOnlyList<ExpirationsGeneratedFile> smokeFiles = nextMonthBatch.Files;
            Render(
                new ExpirationsGeneratedFilesWindow(
                    "Corredor de prueba",
                    () => smokeFiles,
                    new GeneratedFileViewerService(new GeneratedFileProcessLauncher(), smokeDataPaths, smokeLogger),
                    new AssociatedWorkbookEditService(smokeDataPaths, smokeLogger),
                    (_, _) => { },
                    file => smokeFiles = smokeFiles.Where(candidate => !ReferenceEquals(candidate, file)).ToList(),
                    _ => new ExpirationsManualFileAddResult(nextMonthBatch, null, "Smoke sin selección interactiva."),
                    () => Task.CompletedTask),
                generatedFilesPath);
            var settingsWindow = new ExpirationsEmailSettingsWindow(
                new SmokeEmailSettingsService(),
                ExpirationsProcess.PreviousMonth);
            settingsWindow.State.Load(SettingsSnapshot(ExpirationsProcess.PreviousMonth));
            Render(settingsWindow, emailSettingsPath);

            Render(
                new ExpirationsSendReviewWindow(
                    ReviewPreparation(special: false),
                    new SmokeOutlookSender(
                        new OutlookConnectionInfo(
                            true,
                            "Seleccione explícitamente una cuenta.",
                            null,
                            ["operaciones@example.test", "vencimientos@example.test"])),
                    new SmokeSendHistoryRepository()),
                sendReviewNormalPath);
            Render(
                new ExpirationsSendReviewWindow(
                    ReviewPreparation(special: true),
                    new SmokeOutlookSender(),
                    new SmokeSendHistoryRepository()),
                sendReviewSpecialPath);
            var busyReview = new ExpirationsSendReviewWindow(
                ReviewPreparation(special: false),
                new SmokeOutlookSender(),
                new SmokeSendHistoryRepository());
            busyReview.State.ApplyOutlook(AvailableOutlook());
            busyReview.State.SetBusy(true, "Enviando correo 1 de 2...");
            Render(busyReview, sendReviewBusyPath);
            var resultPreparation = ReviewPreparation(special: false, brokerCount: 2);
            var resultReview = new ExpirationsSendReviewWindow(
                resultPreparation,
                new SmokeOutlookSender(),
                new SmokeSendHistoryRepository());
            resultReview.State.ApplyOutlook(AvailableOutlook());
            resultReview.State.ApplyResults(new ExpirationsSendExecutionResult
            {
                SendingAccount = "vencimientos@example.test",
                Items =
                [
                    new ExpirationsSendResultItem(
                        resultPreparation.Requests[0].RequestId,
                        resultPreparation.Requests[0].BrokerId,
                        resultPreparation.Requests[0].BrokerName,
                        true,
                        string.Empty),
                    new ExpirationsSendResultItem(
                        resultPreparation.Requests[1].RequestId,
                        resultPreparation.Requests[1].BrokerId,
                        resultPreparation.Requests[1].BrokerName,
                        false,
                        "Outlook rechazó el destinatario")
                ]
            });
            Render(resultReview, sendReviewResultPath);

            var historyDirectory = Path.Combine(Path.GetTempPath(), "ECSCommissionsMailer-expirations-history-files");
            Directory.CreateDirectory(historyDirectory);
            var successfulPath = Path.Combine(historyDirectory, "Corredor exitoso.xlsx");
            var failedPath = Path.Combine(historyDirectory, "Corredor fallido.xlsx");
            CreateSmokeWorkbook(successfulPath, "smoke-success");
            CreateSmokeWorkbook(failedPath, "smoke-failure");
            var hashService = new GeneratedFileHashService();
            var historyBatch = new ExpirationsGenerationBatch
            {
                Process = ExpirationsProcess.PreviousMonth,
                OutputDirectory = historyDirectory,
                Files =
                [
                    new ExpirationsGeneratedFile
                    {
                        BrokerId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        BrokerName = "Corredor exitoso",
                        OutputPath = successfulPath,
                        Sha256 = hashService.ComputeSha256(successfulPath)
                    },
                    new ExpirationsGeneratedFile
                    {
                        BrokerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        BrokerName = "Corredor fallido",
                        OutputPath = failedPath,
                        Sha256 = hashService.ComputeSha256(failedPath)
                    }
                ]
            };
            var mixedOperation = SmokeHistoryOperation(
                ExpirationsSendOperationStatus.Completed,
                total: 2,
                successes: 1,
                failures: 1);
            var mixedItems = new[]
            {
                SmokeHistoryItem(historyBatch.Files[0], ExpirationsSendItemStatus.Succeeded),
                SmokeHistoryItem(historyBatch.Files[1], ExpirationsSendItemStatus.Failed)
            };
            var mixedHistoryWindow = new ExpirationsSendHistoryWindow(
                new SmokeSendHistoryRepository([mixedOperation], mixedItems),
                new ExpirationsRetryPreparationService(),
                historyBatch,
                new SmokeOutlookSender());
            Render(mixedHistoryWindow, sendHistoryMixedPath);
            if (!mixedHistoryWindow.State.CanRetry || mixedHistoryWindow.State.Items.Count != 2)
                throw new InvalidOperationException("El smoke no habilitó el retry del historial mixto.");
            var incompleteOperation = SmokeHistoryOperation(
                ExpirationsSendOperationStatus.InProgress,
                total: 1,
                successes: 0,
                failures: 0);
            var incompleteHistoryWindow = new ExpirationsSendHistoryWindow(
                new SmokeSendHistoryRepository(
                    [incompleteOperation],
                    [SmokeHistoryItem(historyBatch.Files[1], ExpirationsSendItemStatus.Pending)]),
                new ExpirationsRetryPreparationService(),
                historyBatch,
                new SmokeOutlookSender());
            Render(incompleteHistoryWindow, sendHistoryInProgressPath);
            if (incompleteHistoryWindow.State.CanRetry)
                throw new InvalidOperationException("El smoke habilitó retry para una operación InProgress.");
            bindingListener.Flush();
            var hasBindingErrors = new FileInfo(bindingLogPath).Length > 0;
            File.WriteAllText(
                resultPath,
                string.Join(Environment.NewLine,
                    "VENTANAS_MODULO_INICIADAS=SI",
                    $"ERRORES_BINDING={(hasBindingErrors ? "SI" : "NO")}",
                    $"SELECTOR={selectorPath}",
                    $"VENCIMIENTOS_INICIAL={expirationsInitialPath}",
                    $"VENCIMIENTOS_UN_SOLO_PERMISO={expirationsSinglePermissionPath}",
                    $"VENCIMIENTOS_LISTO={expirationsReadyPath}",
                    $"VENCIMIENTOS_PENDIENTE={expirationsPendingPath}",
                    $"DIALOGO_RESOLUCION={resolutionDialogPath}",
                    $"DIALOGO_SELECCION={selectionDialogPath}",
                    $"DIALOGO_SELECCION_PRIMA_MONEDA={premiumSelectionDialogPath}",
                    $"CONFIGURACION_CORREDORES={brokerManagementPath}",
                    $"ADMINISTRACION_ASOCIACIONES={routingAdministrationPath}",
                    $"EXCLUSIONES_VENCIMIENTOS={exclusionsPath}",
                    $"EDITOR_ASOCIACION={associationEditorPath}",
                    $"PERFIL_SIN_DOCUMENTO={defaultProfilePath}",
                    $"PERFIL_CONFIGURADO={configuredProfilePath}",
                    $"EDITOR_ASISTENTE={assistantEditorPath}",
                    $"PERFIL_SIN_CORREO={missingEmailProfilePath}",
                    $"GENERACION_BUSY={generationBusyPath}",
                    $"GENERACION_COMPLETADA_CON_WARNINGS={generationCompletedPath}",
                    $"NEXT_MONTH_SIN_PERIODO={nextMonthNoPeriodPath}",
                    $"NEXT_MONTH_LISTO_NORMAL_MAS_ESPECIAL={nextMonthReadyPath}",
                    $"CONFIGURACION_CORREO={emailSettingsPath}",
                    $"ARCHIVOS_ASOCIADOS={generatedFilesPath}",
                    $"REVISION_NORMAL_SELECCION_CUENTA={sendReviewNormalPath}",
                    $"REVISION_ESPECIAL_DOS_ADJUNTOS={sendReviewSpecialPath}",
                    $"ENVIO_BUSY={sendReviewBusyPath}",
                    $"RESULTADO_SUCCESS_FAILURE={sendReviewResultPath}",
                    $"HISTORIAL_COMPLETED_MIXTO_RETRY={sendHistoryMixedPath}",
                    $"HISTORIAL_IN_PROGRESS_SIN_RETRY={sendHistoryInProgressPath}",
                    $"LOG_BINDINGS={bindingLogPath}"));
            return !hasBindingErrors;
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                resultPath,
                string.Join(Environment.NewLine,
                    "VENTANAS_MODULO_INICIADAS=NO",
                    "ERRORES_BINDING=NO",
                    $"EXCEPCION={ex}"));
            return false;
        }
        finally
        {
            Application.Current.ShutdownMode = previousShutdownMode;
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
        }
    }

    private static void Render(Window window, string path, string? minimumPath = null)
    {
        window.Show();
        window.UpdateLayout();
        Capture(window, path);
        if (minimumPath is not null)
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.UpdateLayout();
            Capture(window, minimumPath);
        }
        window.Close();
    }

    private static void RenderAtElement(
        Window window,
        string elementName,
        string path,
        Action<Window>? afterShow = null)
    {
        window.Show();
        afterShow?.Invoke(window);
        window.UpdateLayout();
        if (window.FindName(elementName) is FrameworkElement element)
        {
            element.BringIntoView();
            window.UpdateLayout();
        }
        Capture(window, path);
        window.Close();
    }

    private static void RenderAndValidateExpirationsShell(
        ExpirationsWindow window,
        string path,
        bool expectModuleSwitch)
    {
        window.Show();
        window.UpdateLayout();
        foreach (var requiredElement in new[]
                 {
                     "EmailTemplateCard",
                     "SignaturePanel",
                     "GenerationSection",
                     "BrokerGrid",
                     "BottomActionBar",
                     "NewSendButton",
                     "OutlookAccountSelector",
                     "ConnectOutlookButton",
                     "SendSelectedButton",
                     "SendAllButton",
                     "SwitchModuleButton"
                 })
        {
            if (window.FindName(requiredElement) is not FrameworkElement)
                throw new InvalidOperationException($"Falta el elemento UAT '{requiredElement}'.");
        }
        var switchButton = (FrameworkElement)window.FindName("SwitchModuleButton");
        var expectedVisibility = expectModuleSwitch ? Visibility.Visible : Visibility.Collapsed;
        if (switchButton.Visibility != expectedVisibility)
            throw new InvalidOperationException("La visibilidad de Cambiar módulo no corresponde a los permisos.");
        if (((ExpirationsWindowState)window.DataContext).BrokerRowsView.IsEmpty)
            throw new InvalidOperationException("La tabla principal no mostró el catálogo activo antes del análisis.");
        Capture(window, path);
        window.Close();
    }

    private static void Capture(Window window, string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            encoder.Save(stream);
    }

    private static void CreateSmokeWorkbook(string path, string value)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData(
            new Row(new Cell { DataType = CellValues.String, CellValue = new CellValue(value) })));
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Datos"
        });
        workbookPart.Workbook.Save();
    }

    private static ExpirationsAnalysisSessionSnapshot ReadySnapshot(
        ExpirationsProcess process = ExpirationsProcess.PreviousMonth)
    {
        var brokerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        return new ExpirationsAnalysisSessionSnapshot
        {
            Process = process,
            SourcePath = @"C:\Pruebas\vencimientos.xlsx",
            Analysis = new ExpirationsWorkbookAnalysisResult
            {
                ReadStatus = ExpirationsWorkbookReadStatus.Success,
                TotalRows = 12,
                ResolvedRows = 12,
                CanGenerate = true
            },
            Catalog =
            [
                new ExpirationsBrokerCatalogItem
                {
                    BrokerId = brokerId,
                    Name = "Corredor resuelto",
                    PrimaryEmailAddresses = ["corredor@example.test"],
                    IsActive = true,
                    Assistants =
                    [
                        new ExpirationsAssistant
                        {
                            Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
                            Name = "Asistente Vencimientos",
                            Email = "asistente@example.test",
                            IsActive = true
                        }
                    ]
                }
            ],
            Distribution =
            [
                new ExpirationsDistributionPreviewItem
                {
                    BrokerId = brokerId,
                    BrokerName = "Corredor resuelto",
                    PrimaryEmail = "corredor@example.test",
                    RowCount = 12,
                    DetectedValues = ["AS1", "AS5"]
                }
            ]
        };
    }

    private static ExpirationsAnalysisSessionSnapshot InitialSnapshot()
    {
        var ready = ReadySnapshot();
        return new ExpirationsAnalysisSessionSnapshot
        {
            Process = ExpirationsProcess.PreviousMonth,
            Catalog = ready.Catalog
        };
    }

    private static ExpirationsWindow ExpirationsWindow(
        AppUser user,
        ExpirationsAnalysisSessionSnapshot snapshot)
    {
        var settingsRepository = new SmokeProcessSettingsRepository();
        var catalog = new ExpirationsBrokerCatalogService(
            new SmokeDirectoryRepository(),
            new SmokeProfileRepository());
        var paths = new AppDataPaths(Path.Combine(Path.GetTempPath(), "ECSCommissionsMailer-module-ui-smoke"));
        var logger = new FileLogger(paths);
        return new ExpirationsWindow(
            user,
            new NonOperationalAppUserRepository(),
            new SmokeCoordinator(snapshot),
            new SmokeConfigurationService(),
            new SmokeGenerationService(),
            new SmokeEmailSettingsService(),
            new ExpirationsSendPreparationService(settingsRepository, catalog),
            new SmokeOutlookSender(),
            new SmokeSendHistoryRepository(),
            paths,
            logger);
    }

    private static ExpirationsEmailSettingsSnapshot SettingsSnapshot(ExpirationsProcess process) => new(
        process,
        new ExpirationsProcessSettings
        {
            DefaultSubject = process == ExpirationsProcess.PreviousMonth
                ? "Pendientes de pólizas — mes anterior"
                : "Vencimientos de pólizas — mes siguiente",
            DefaultMessage = "Buen día,\n\nAdjuntamos el reporte de pólizas correspondiente.",
            CommonCcAddresses = ["supervision@example.test"],
            UpdatedAtUtc = DateTimeOffset.UtcNow
        },
        "smoke-version");

    private static ExpirationsSendPreparationResult ReviewPreparation(
        bool special,
        int brokerCount = 1)
    {
        var process = special ? ExpirationsProcess.NextMonth : ExpirationsProcess.PreviousMonth;
        var requests = Enumerable.Range(1, brokerCount).Select(index => new EmailSendRequest
        {
            BrokerId = Guid.Parse($"{index:D8}-1111-1111-1111-111111111111"),
            BrokerName = index == 1 ? "Corredor Ejemplo" : "Corredor con fallo",
            BrokerPrimaryRecipients = [$"corredor{index}@example.test"],
            AssistantRecipients = [$"asistente{index}@example.test"],
            ToRecipients = [$"corredor{index}@example.test", $"asistente{index}@example.test"],
            CcRecipients = ["supervision@example.test"],
            Subject = SettingsSnapshot(process).Settings.DefaultSubject,
            Body = SettingsSnapshot(process).Settings.DefaultMessage,
            AttachmentPaths = special
                ? [@"C:\Generados\ORDENADO ALFABETICAMENTE.xlsx", @"C:\Generados\ORDENADO POR VENCIMIENTO.xlsx"]
                : [$@"C:\Generados\Corredor {index}.xlsx"],
            ReviewConfirmed = true
        }).ToList();
        return new ExpirationsSendPreparationResult
        {
            Process = process,
            Settings = SettingsSnapshot(process).Settings,
            Requests = requests,
            Warnings = special ? ["El formato especial incluye dos archivos en un solo correo."] : []
        };
    }

    private static OutlookConnectionInfo AvailableOutlook() => new(
        true,
        "Cuenta disponible.",
        "vencimientos@example.test",
        ["vencimientos@example.test"]);

    private static ExpirationsSendOperation SmokeHistoryOperation(
        ExpirationsSendOperationStatus status,
        int total,
        int successes,
        int failures) => new()
    {
        OperationId = Guid.NewGuid(),
        Process = ExpirationsProcess.PreviousMonth,
        StartedAtUtc = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero),
        CompletedAtUtc = status == ExpirationsSendOperationStatus.Completed
            ? new DateTimeOffset(2026, 8, 21, 12, 2, 0, TimeSpan.Zero)
            : null,
        SendingAccountEmail = "vencimientos@example.test",
        Subject = "Pendientes de pólizas — mes anterior",
        Body = "Buen día, adjuntamos el reporte.",
        Status = status,
        TotalCount = total,
        SuccessCount = successes,
        FailureCount = failures
    };

    private static ExpirationsSendHistoryItem SmokeHistoryItem(
        ExpirationsGeneratedFile file,
        ExpirationsSendItemStatus status) => new()
    {
        ItemId = Guid.NewGuid(),
        RequestId = Guid.NewGuid(),
        BrokerId = file.BrokerId,
        BrokerName = file.BrokerName,
        ToRecipients = [$"{file.BrokerName.Replace(" ", ".").ToLowerInvariant()}@example.test"],
        CcRecipients = ["supervision@example.test"],
        Attachments =
        [
            new ExpirationsSendAttachment(
                Path.GetFileName(file.OutputPath),
                file.Sha256,
                file.Variant)
        ],
        Status = status,
        ErrorMessage = status == ExpirationsSendItemStatus.Failed
            ? "Outlook rechazó el destinatario"
            : string.Empty
    };

    private static ExpirationsAnalysisSessionSnapshot PendingSnapshot()
    {
        var brokerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        return new ExpirationsAnalysisSessionSnapshot
        {
            Process = ExpirationsProcess.NextMonth,
            SourcePath = @"C:\Pruebas\vencimientos-pendientes.xlsx",
            Analysis = new ExpirationsWorkbookAnalysisResult
            {
                ReadStatus = ExpirationsWorkbookReadStatus.Success,
                TotalRows = 10,
                ResolvedRows = 9,
                RowsWithBlockingIssues = 1,
                UnresolvedComponents = 1,
                CanGenerate = false
            },
            Catalog =
            [
                new ExpirationsBrokerCatalogItem
                {
                    BrokerId = brokerId,
                    Name = "Corredor disponible",
                    PrimaryEmailAddresses = ["disponible@example.test"],
                    IsActive = true
                }
            ],
            Distribution =
            [
                new ExpirationsDistributionPreviewItem
                {
                    BrokerId = brokerId,
                    BrokerName = "Corredor disponible",
                    PrimaryEmail = "disponible@example.test",
                    RowCount = 9,
                    DetectedValues = ["CD1"]
                }
            ],
            PendingIssues =
            [
                new ExpirationsPendingIssue
                {
                    RowNumber = 10,
                    ComponentIndex = 0,
                    RawValue = "CODIGO NUEVO",
                    NormalizedValue = "CODIGO NUEVO",
                    Status = ExpirationsBrokerResolutionStatus.Unresolved,
                    StatusText = "No reconocido"
                }
            ]
        };
    }

    private static ExpirationsWorkbookInspection Inspection() => new()
    {
        SourcePath = @"C:\Pruebas\manual.xlsx",
        Worksheets =
        [
            new ExpirationsWorksheetInspection
            {
                WorksheetName = "Hoja2",
                HeaderRows =
                [
                    new ExpirationsHeaderRowInspection
                    {
                        RowNumber = 1,
                        Columns =
                        [
                            new ExpirationsColumnInspection
                            {
                                ColumnIndex = 1,
                                ColumnReference = "A",
                                HeaderText = "Intermediario"
                            },
                            new ExpirationsColumnInspection
                            {
                                ColumnIndex = 2,
                                ColumnReference = "B",
                                HeaderText = "Número de Póliza"
                            },
                            new ExpirationsColumnInspection
                            {
                                ColumnIndex = 3,
                                ColumnReference = "C",
                                HeaderText = "Prima"
                            },
                            new ExpirationsColumnInspection
                            {
                                ColumnIndex = 4,
                                ColumnReference = "D",
                                HeaderText = "Moneda"
                            }
                        ]
                    }
                ]
            }
        ]
    };

    private static IReadOnlyList<ExpirationsBrokerConfigurationItem> ConfigurationItems()
    {
        var activeAssistantId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var inactiveAssistantId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        return
        [
            new ExpirationsBrokerConfigurationItem
            {
                BrokerId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Arturo Quesada",
                PrimaryEmailAddresses = ["arturo@example.test"],
                IsActive = true,
                Assistants = [],
                HasExplicitProfile = false
            },
            new ExpirationsBrokerConfigurationItem
            {
                BrokerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = "Jerrika Hernández",
                PrimaryEmailAddresses = ["jerrika@example.test", "office@example.test"],
                IsActive = true,
                NextMonthGenerationMode = ExpirationsNextMonthGenerationMode.SpecialDualSorted,
                Assistants =
                [
                    new ExpirationsAssistant
                    {
                        Id = activeAssistantId,
                        Name = "Ana Luisa",
                        Email = "ana@example.test",
                        IsActive = true
                    },
                    new ExpirationsAssistant
                    {
                        Id = inactiveAssistantId,
                        Name = "Asistente anterior",
                        Email = "inactive@example.test",
                        IsActive = false
                    }
                ],
                HasExplicitProfile = true,
                ProfileUpdateTime = "smoke-version",
                ProfileCreatedAtUtc = DateTimeOffset.UtcNow,
                ProfileUpdatedAtUtc = DateTimeOffset.UtcNow
            },
            new ExpirationsBrokerConfigurationItem
            {
                BrokerId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                Name = "Corredor sin correo",
                PrimaryEmailAddresses = [],
                IsActive = false,
                Assistants = [],
                HasExplicitProfile = true,
                ProfileUpdateTime = "smoke-no-email",
                ProfileCreatedAtUtc = DateTimeOffset.UtcNow,
                ProfileUpdatedAtUtc = DateTimeOffset.UtcNow
            }
        ];
    }

    private sealed class SmokeCoordinator(ExpirationsAnalysisSessionSnapshot snapshot)
        : IExpirationsAnalysisCoordinator
    {
        public ExpirationsAnalysisSessionSnapshot Snapshot { get; private set; } = snapshot;

        public void ResetPreparation() => Snapshot = new ExpirationsAnalysisSessionSnapshot();
        public void SelectProcess(ExpirationsProcess? process) { }
        public void SelectFile(string sourcePath) { }

        public Task<ExpirationsAnalysisSessionSnapshot> AnalyzeAsync(
            ExpirationsWorkbookReadOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);

        public Task<ExpirationsAnalysisSessionSnapshot> RefreshCatalogAndReanalyzeAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);

        public Task<ExpirationsGenerationPreparationResult> PrepareGenerationAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExpirationsGenerationPreparationResult { Snapshot = Snapshot });

        public Task<ExpirationsGenerationPreparationResult> PrepareGenerationAsync(
            ExpirationsPeriod period,
            ExpirationsPremiumColumnOptions? premiumColumnOptions,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExpirationsGenerationPreparationResult
            {
                Snapshot = Snapshot,
                ErrorMessage = "Smoke: preflight de mes siguiente pendiente."
            });

        public Task<ExpirationsWorkbookInspection> InspectWorkbookAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Inspection());

        public ExpirationsManualOverrideResult ApplyManualOverride(
            uint rowNumber,
            int componentIndex,
            Guid brokerId) => new() { Snapshot = Snapshot };

        public Task<ExpirationsAssociationConfirmationResult> ConfirmAssociationAsync(
            ExpirationsAssociationConfirmation confirmation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExpirationsAssociationConfirmationResult { Snapshot = Snapshot });

        public Task<ExpirationsExclusionConfirmationResult> ExcludeAsync(
            uint rowNumber,
            int componentIndex,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExpirationsExclusionConfirmationResult { Snapshot = Snapshot });
    }

    private sealed class SmokeGenerationService : IExpirationsGenerationService
    {
        public Task<ExpirationsGenerationBatch> GenerateAsync(
            ExpirationsGenerationRequest request,
            IProgress<ExpirationsGenerationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExpirationsGenerationBatch());
    }

    private sealed class NonOperationalAppUserRepository : IAppUserRepository
    {
        public Task<FirestoreStoredDocument<AppUser>?> GetAsync(
            string uid,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("El smoke test visual no realiza operaciones Firestore.");

        public Task<IReadOnlyList<FirestoreStoredDocument<AppUser>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("El smoke test visual no realiza operaciones Firestore.");

        public Task<FirestoreStoredDocument<AppUser>> CreateAsync(
            AppUser value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("El smoke test visual no realiza operaciones Firestore.");

        public Task<FirestoreStoredDocument<AppUser>> UpdateAsync(
            AppUser value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("El smoke test visual no realiza operaciones Firestore.");
    }

    private sealed class SmokeConfigurationService(
        IReadOnlyList<ExpirationsBrokerConfigurationItem>? items = null)
        : IExpirationsBrokerConfigurationService
    {
        private readonly IReadOnlyList<ExpirationsBrokerConfigurationItem> _items = items ?? [];

        public Task<IReadOnlyList<ExpirationsBrokerConfigurationItem>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_items);

        public Task<ExpirationsBrokerConfigurationItem?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.FirstOrDefault(item => item.BrokerId == brokerId));

        public Task<ExpirationsBrokerConfigurationSaveResult> SaveAsync(
            ExpirationsBrokerConfigurationItem configuration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExpirationsBrokerConfigurationSaveResult
            {
                Outcome = ExpirationsBrokerConfigurationSaveOutcome.NoChanges,
                Configuration = configuration
            });
    }

    private sealed class SmokeRoutingAdministrationService(
        IReadOnlyList<ExpirationsBrokerConfigurationItem> brokers)
        : IExpirationsRoutingAdministrationService
    {
        public Task<IReadOnlyList<ExpirationsAssociationAdministrationItem>> ListAssociationsAsync(
            CancellationToken cancellationToken = default)
        {
            var broker = brokers.First();
            return Task.FromResult<IReadOnlyList<ExpirationsAssociationAdministrationItem>>(
            [
                new ExpirationsAssociationAdministrationItem
                {
                    Association = new ExpirationsBrokerAssociation
                    {
                        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        BrokerId = broker.BrokerId,
                        Kind = ExpirationsAssociationKind.Alias,
                        Value = "Alias de prueba",
                        NormalizedValue = "ALIAS DE PRUEBA",
                        IsActive = true,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    },
                    UpdateTime = "smoke-association",
                    BrokerName = broker.Name,
                    BrokerPrimaryEmail = broker.PrimaryEmailAddresses.FirstOrDefault() ?? string.Empty
                }
            ]);
        }

        public async Task<IReadOnlyList<ExpirationsKnownIdentifierAdministrationItem>> ListKnownIdentifiersAsync(
            CancellationToken cancellationToken = default)
        {
            var broker = brokers.First();
            var association = AssertSingle(await ListAssociationsAsync(cancellationToken));
            return
            [
                new ExpirationsKnownIdentifierAdministrationItem
                {
                    BrokerId = broker.BrokerId,
                    BrokerName = broker.Name,
                    BrokerPrimaryEmail = broker.PrimaryEmailAddresses.FirstOrDefault() ?? string.Empty,
                    Kind = ExpirationsAssociationKind.Name,
                    Value = broker.Name,
                    NormalizedValue = broker.Name.ToUpperInvariant(),
                    OriginText = "Maestro",
                    StatusText = "Activo",
                    IsMaster = true
                },
                new ExpirationsKnownIdentifierAdministrationItem
                {
                    BrokerId = broker.BrokerId,
                    BrokerName = broker.Name,
                    BrokerPrimaryEmail = broker.PrimaryEmailAddresses.FirstOrDefault() ?? string.Empty,
                    Kind = association.Association.Kind,
                    Value = association.Association.Value,
                    NormalizedValue = association.Association.NormalizedValue,
                    OriginText = association.OriginText,
                    StatusText = "Activo",
                    UpdatedAtUtc = association.Association.UpdatedAtUtc,
                    AssociationItem = association
                }
            ];
        }

        public Task<IReadOnlyList<ExpirationsExclusionAdministrationItem>> ListExclusionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExpirationsExclusionAdministrationItem>>(
            [
                new ExpirationsExclusionAdministrationItem
                {
                    Exclusion = new ExpirationsExclusion
                    {
                        Id = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                        Value = "Corredor histórico",
                        NormalizedValue = "CORREDOR HISTORICO",
                        IsActive = true,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    },
                    UpdateTime = "smoke-exclusion"
                }
            ]);

        public Task<ExpirationsRoutingAdministrationResult> SetAssociationActiveAsync(
            Guid associationId, bool isActive, string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ExpirationsRoutingAdministrationResult> CreateAssociationAsync(
            Guid brokerId, ExpirationsAssociationKind kind, string value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ExpirationsRoutingAdministrationResult> EditAssociationAsync(
            Guid associationId, ExpirationsAssociationKind kind, string value, string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ExpirationsRoutingAdministrationResult> ReassignAsync(
            Guid associationId, Guid destinationBrokerId, string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ExpirationsRoutingAdministrationResult> DeleteAssociationAsync(
            Guid associationId, string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ExpirationsRoutingAdministrationResult> SetExclusionActiveAsync(
            Guid exclusionId, bool isActive, string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ExpirationsRoutingAdministrationResult> ConfirmObservedIdentifierAsync(
            Guid identifierId, string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ExpirationsRoutingAdministrationResult> ReassignAndConfirmObservedIdentifierAsync(
            Guid identifierId, Guid destinationBrokerId, string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ExpirationsRoutingAdministrationResult> IgnoreObservedIdentifierAsync(
            Guid identifierId, string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private static T AssertSingle<T>(IReadOnlyList<T> values) => values.Count == 1
            ? values[0]
            : throw new InvalidOperationException("El smoke esperaba exactamente un elemento.");
    }

    private sealed class SmokeEmailSettingsService : IExpirationsEmailSettingsService
    {
        public Task<ExpirationsEmailSettingsSnapshot> LoadAsync(
            ExpirationsProcess process,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SettingsSnapshot(process));

        public Task<ExpirationsEmailSettingsSaveResult> SaveAsync(
            ExpirationsEmailSettingsSnapshot snapshot,
            string? subject,
            string? message,
            string? commonCcText,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExpirationsEmailSettingsSaveResult
            {
                Outcome = ExpirationsEmailSettingsSaveOutcome.Updated,
                Snapshot = snapshot,
                Message = "Configuración guardada."
            });
    }

    private sealed class SmokeOutlookSender(
        OutlookConnectionInfo? connection = null) : IExpirationsOutlookSender
    {
        private readonly OutlookConnectionInfo _connection = connection ?? AvailableOutlook();

        public Task<OutlookConnectionInfo> CheckAvailabilityAsync() => Task.FromResult(_connection);

        public string SelectSendingAccount(string emailAddress) => emailAddress;

        public Task<IReadOnlyList<EmailSendResult>> SendBatchAsync(
            IReadOnlyList<EmailSendRequest> requests,
            IProgress<OutlookSendProgress>? progress = null) =>
            Task.FromResult<IReadOnlyList<EmailSendResult>>(requests.Select(request => new EmailSendResult
            {
                RequestId = request.RequestId,
                WasSuccessful = true
            }).ToList());
    }

    private sealed class SmokeProcessSettingsRepository : IExpirationsProcessSettingsRepository
    {
        public Task<FirestoreStoredDocument<ExpirationsProcessSettings>?> GetAsync(
            ExpirationsProcess process,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FirestoreStoredDocument<ExpirationsProcessSettings>?>(new(
                SettingsSnapshot(process).Settings,
                $"modules/vencimientos/settings/{process}",
                "smoke-version"));

        public Task<FirestoreStoredDocument<ExpirationsProcessSettings>> CreateAsync(
            ExpirationsProcess process,
            ExpirationsProcessSettings value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FirestoreStoredDocument<ExpirationsProcessSettings>> UpdateAsync(
            ExpirationsProcess process,
            ExpirationsProcessSettings value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SmokeSendHistoryRepository(
        IReadOnlyList<ExpirationsSendOperation>? operations = null,
        IReadOnlyList<ExpirationsSendHistoryItem>? items = null) : IExpirationsSendHistoryRepository
    {
        private readonly IReadOnlyList<ExpirationsSendOperation> _operations = operations ?? [];
        private readonly IReadOnlyList<ExpirationsSendHistoryItem> _items = items ?? [];

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendOperation>>> ListOperationsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendOperation>>>(
                _operations.Select(value => new FirestoreStoredDocument<ExpirationsSendOperation>(
                    value,
                    $"modules/vencimientos/sendOperations/{value.OperationId:D}",
                    "smoke-version")).ToList());

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendHistoryItem>>> ListItemsAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendHistoryItem>>>(
                _items.Select(value => new FirestoreStoredDocument<ExpirationsSendHistoryItem>(
                    value,
                    $"modules/vencimientos/sendOperations/{operationId:D}/items/{value.ItemId:D}",
                    "smoke-version")).ToList());

        public Task<FirestoreStoredDocument<ExpirationsSendOperation>> CreateOperationAsync(
            ExpirationsSendOperation value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FirestoreStoredDocument<ExpirationsSendOperation>(
                value,
                $"modules/vencimientos/sendOperations/{value.OperationId:D}",
                "smoke-version"));

        public Task<FirestoreStoredDocument<ExpirationsSendHistoryItem>> CreateItemAsync(
            Guid operationId,
            ExpirationsSendHistoryItem value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FirestoreStoredDocument<ExpirationsSendHistoryItem>(
                value,
                $"modules/vencimientos/sendOperations/{operationId:D}/items/{value.ItemId:D}",
                "smoke-version"));

        public Task<FirestoreStoredDocument<ExpirationsSendOperation>> UpdateOperationAsync(
            ExpirationsSendOperation value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) =>
            CreateOperationAsync(value, cancellationToken);

        public Task<FirestoreStoredDocument<ExpirationsSendHistoryItem>> UpdateItemAsync(
            Guid operationId,
            ExpirationsSendHistoryItem value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) =>
            CreateItemAsync(operationId, value, cancellationToken);
    }

    private sealed class SmokeDirectoryRepository : IExpirationsBrokerDirectoryRepository
    {
        public Task<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) => Task.FromResult<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>?>(null);

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>>([]);
    }

    private sealed class SmokeProfileRepository : IExpirationsBrokerProfileRepository
    {
        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) => Task.FromResult<FirestoreStoredDocument<ExpirationsBrokerProfile>?>(null);

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>>([]);

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> CreateAsync(
            ExpirationsBrokerProfile value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> UpdateAsync(
            ExpirationsBrokerProfile value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
