using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

            Render(new ModuleSelectionWindow(user), selectorPath);
            Render(
                ExpirationsWindow(user, new ExpirationsAnalysisSessionSnapshot()),
                expirationsInitialPath);
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
            Render(
                new ExpirationsBrokerManagementWindow(configurationService),
                brokerManagementPath);
            Render(
                new ExpirationsBrokerProfileWindow(configurationService, configurationItems[0]),
                defaultProfilePath);
            Render(
                new ExpirationsBrokerProfileWindow(configurationService, configurationItems[1]),
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
                            ["operaciones@example.test", "vencimientos@example.test"]))),
                sendReviewNormalPath);
            Render(
                new ExpirationsSendReviewWindow(
                    ReviewPreparation(special: true),
                    new SmokeOutlookSender()),
                sendReviewSpecialPath);
            var busyReview = new ExpirationsSendReviewWindow(
                ReviewPreparation(special: false),
                new SmokeOutlookSender());
            busyReview.State.ApplyOutlook(AvailableOutlook());
            busyReview.State.SetBusy(true, "Enviando correo 1 de 2...");
            Render(busyReview, sendReviewBusyPath);
            var resultPreparation = ReviewPreparation(special: false, brokerCount: 2);
            var resultReview = new ExpirationsSendReviewWindow(resultPreparation, new SmokeOutlookSender());
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
            bindingListener.Flush();
            var hasBindingErrors = new FileInfo(bindingLogPath).Length > 0;
            File.WriteAllText(
                resultPath,
                string.Join(Environment.NewLine,
                    "VENTANAS_MODULO_INICIADAS=SI",
                    $"ERRORES_BINDING={(hasBindingErrors ? "SI" : "NO")}",
                    $"SELECTOR={selectorPath}",
                    $"VENCIMIENTOS_INICIAL={expirationsInitialPath}",
                    $"VENCIMIENTOS_LISTO={expirationsReadyPath}",
                    $"VENCIMIENTOS_PENDIENTE={expirationsPendingPath}",
                    $"DIALOGO_RESOLUCION={resolutionDialogPath}",
                    $"DIALOGO_SELECCION={selectionDialogPath}",
                    $"DIALOGO_SELECCION_PRIMA_MONEDA={premiumSelectionDialogPath}",
                    $"CONFIGURACION_CORREDORES={brokerManagementPath}",
                    $"PERFIL_SIN_DOCUMENTO={defaultProfilePath}",
                    $"PERFIL_CONFIGURADO={configuredProfilePath}",
                    $"EDITOR_ASISTENTE={assistantEditorPath}",
                    $"PERFIL_SIN_CORREO={missingEmailProfilePath}",
                    $"GENERACION_BUSY={generationBusyPath}",
                    $"GENERACION_COMPLETADA_CON_WARNINGS={generationCompletedPath}",
                    $"NEXT_MONTH_SIN_PERIODO={nextMonthNoPeriodPath}",
                    $"NEXT_MONTH_LISTO_NORMAL_MAS_ESPECIAL={nextMonthReadyPath}",
                    $"CONFIGURACION_CORREO={emailSettingsPath}",
                    $"REVISION_NORMAL_SELECCION_CUENTA={sendReviewNormalPath}",
                    $"REVISION_ESPECIAL_DOS_ADJUNTOS={sendReviewSpecialPath}",
                    $"ENVIO_BUSY={sendReviewBusyPath}",
                    $"RESULTADO_SUCCESS_FAILURE={sendReviewResultPath}",
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

    private static ExpirationsWindow ExpirationsWindow(
        AppUser user,
        ExpirationsAnalysisSessionSnapshot snapshot)
    {
        var settingsRepository = new SmokeProcessSettingsRepository();
        var catalog = new ExpirationsBrokerCatalogService(
            new SmokeDirectoryRepository(),
            new SmokeProfileRepository());
        return new ExpirationsWindow(
            user,
            new NonOperationalAppUserRepository(),
            new SmokeCoordinator(snapshot),
            new SmokeConfigurationService(),
            new SmokeGenerationService(),
            new SmokeEmailSettingsService(),
            new ExpirationsSendPreparationService(settingsRepository, catalog),
            new SmokeOutlookSender());
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
