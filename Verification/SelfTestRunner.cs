using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ECS.CommissionsMailer.Helpers;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Verification;

internal static class SelfTestRunner
{
    public static bool Run()
    {
        var resultFile = Path.Combine(Path.GetTempPath(), "ECSCommissionsMailer-self-test-result.txt");
        var testRoot = Path.Combine(Path.GetTempPath(), $"ECSCommissionsMailer-tests-{Guid.NewGuid():N}");
        var migrationRoot = Path.Combine(Path.GetTempPath(), $"ECSCommissionsMailer-migration-{Guid.NewGuid():N}");
        var results = new List<string>();
        var passed = true;

        void Check(bool condition, string description)
        {
            results.Add($"{(condition ? "OK" : "ERROR")} - {description}");
            passed &= condition;
        }

        try
        {
            RunCoreTests(testRoot, Check);
            RunMigrationTests(migrationRoot, Check);
            RunPaymentAutomationTests(Check);
        }
        catch (Exception ex)
        {
            passed = false;
            results.Add($"ERROR INESPERADO - {ex}");
        }
        finally
        {
            DeleteTestDirectory(testRoot, results);
            DeleteTestDirectory(migrationRoot, results);
        }

        results.Insert(0, passed ? "RESULTADO: CORRECTO" : "RESULTADO: CON ERRORES");
        File.WriteAllLines(resultFile, results);
        Console.WriteLine(string.Join(Environment.NewLine, results));
        return passed;
    }

    private static void RunCoreTests(string testRoot, Action<bool, string> check)
    {
        var paths = new AppDataPaths(testRoot);
        var logger = new FileLogger(paths);
        var configurationService = new ConfigurationService(paths, logger);
        var sessionService = new SessionService(paths, logger);
        var validationService = new EmailValidationService();
        var archiveService = new AttachmentArchiveService(paths, logger);
        var signatureService = new SignatureImageService(paths);

        var apartment = StaTaskRunner.RunAsync(() => Thread.CurrentThread.GetApartmentState()).GetAwaiter().GetResult();
        check(apartment == ApartmentState.STA, "Outlook: StaTaskRunner ejecuta trabajo en un hilo STA");

        var configuration = configurationService.Load();
        check(File.Exists(paths.ConfigurationFile) && configuration.EmailDirectorySeedVersion == 3 &&
              !string.IsNullOrWhiteSpace(configuration.EmailDirectorySeedId),
            "Migración: el primer inicio crea configuración con semilla versión 3 e identificador de contenido");
        check(configuration.Brokers.Count == 64 && configuration.CommonCcAddresses.Count == 1,
            "Directorio: se cargan los 64 corredores y la CC de la configuración oficial");
        check(configuration.Brokers.Sum(value => value.PrimaryEmailAddresses.Count) == 65 &&
              configuration.Brokers.Sum(value => value.Assistants.Count) == 10,
            "Directorio: se cargan los 65 correos principales y 10 asistentes corregidos");
        check(configuration.Brokers.All(value => !value.RequiresReview) &&
              configuration.Brokers.Any(value => value.SeedKey == "armando-molina-clarefacio" &&
                                                   value.Name == "Clare Facio" &&
                                                   value.PrimaryEmailAddresses.Contains("amolina@clarefacio.com")) &&
              configuration.Brokers.Any(value => value.SeedKey == "felix-unresolved" &&
                                                   value.Name == "Felix Lara" &&
                                                   value.PrimaryEmailAddresses.Contains("flaraseguros@gmail.com")),
            "Directorio: las correcciones manuales de Clare Facio y Felix Lara están incluidas");
        check(configuration.Brokers.Any(value => value.Name == "Gustavo" &&
                                                   value.SeedKey?.StartsWith("configured-", StringComparison.Ordinal) == true) &&
              !string.IsNullOrWhiteSpace(configuration.SignatureImagePath) &&
              File.Exists(configuration.SignatureImagePath),
            "Instantánea: incluye el corredor agregado manualmente y una copia portátil de la firma");

        var edgars = configuration.Brokers.Where(value => value.Name == "Edgar Gomez").ToList();
        var fernandos = configuration.Brokers.Where(value =>
            value.SeedKey is "fernando-cabada-essential" or "fernando-cabada-insura").ToList();
        check(edgars.Count == 2 && edgars.Select(value => value.Id).Distinct().Count() == 2 &&
              edgars.Select(value => value.SeedKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 &&
              edgars.All(value => value.PrimaryEmailAddresses.Count == 1),
            "Identidad: Edgar Gomez tiene dos registros, IDs, SeedKeys y correos independientes");
        check(fernandos.Count == 2 && fernandos.Select(value => value.Id).Distinct().Count() == 2 &&
              fernandos.Select(value => value.SeedKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 &&
              fernandos.All(value => value.PrimaryEmailAddresses.Count == 1),
            "Identidad: Fernando Cabada tiene dos registros, IDs, SeedKeys y correos independientes");

        var alberto = configuration.Brokers.Single(value => value.SeedKey == "alberto-volio-perfectcircle");
        var albertoRecipients = validationService.ResolveRecipients(alberto, string.Join(";", configuration.CommonCcAddresses));
        check(albertoRecipients.Errors.Count == 0 && albertoRecipients.ToRecipients.Count == 3 &&
              albertoRecipients.ToRecipients.Contains("avs@perfectcircle.life") &&
              albertoRecipients.ToRecipients.Contains("opsupport2@perfectcircle.life") &&
              albertoRecipients.ToRecipients.Contains("accounting1@perfectcircle.life"),
            "Asistentes: Alberto Volio genera un único TO final con principal y dos asistentes");

        var andres = configuration.Brokers.Single(value => value.SeedKey == "andres-steimberg-seguru");
        var andresRecipients = validationService.ResolveRecipients(andres, string.Join(";", configuration.CommonCcAddresses));
        check(andresRecipients.ToRecipients.SequenceEqual(
            ["andres@seguru.cr", "daniela@seguru.cr", "ejecutivo4@seguru.cr"], StringComparer.OrdinalIgnoreCase),
            "Asistentes: Andrés conserva principal y dos asistentes en el mismo MailItem");

        var fernandoEssential = configuration.Brokers.Single(value => value.SeedKey == "fernando-cabada-essential");
        var fernandoInsura = configuration.Brokers.Single(value => value.SeedKey == "fernando-cabada-insura");
        const string duplicateCcTest = "fcabada@essentialgroupla.com; copia@example.com";
        var essentialRecipients = validationService.ResolveRecipients(fernandoEssential, duplicateCcTest);
        var insuraRecipients = validationService.ResolveRecipients(fernandoInsura, duplicateCcTest);
        check(essentialRecipients.ToRecipients.Count(value => value.Equals("fcabada@essentialgroupla.com", StringComparison.OrdinalIgnoreCase)) == 1 &&
              !essentialRecipients.CcRecipients.Contains("fcabada@essentialgroupla.com", StringComparer.OrdinalIgnoreCase) &&
              insuraRecipients.CcRecipients.Contains("fcabada@essentialgroupla.com", StringComparer.OrdinalIgnoreCase),
            "Destinatarios: TO tiene precedencia sobre CC por MailItem sin modificar la CC global");

        var duplicateAssistantErrors = validationService.ValidateBroker(
            "Prueba", "uno@example.com",
            [new BrokerAssistant { Name = "Asistente", Email = "UNO@example.com" }],
            false, null, out _);
        check(duplicateAssistantErrors.Any(value => value.Contains("duplicada", StringComparison.OrdinalIgnoreCase)),
            "Validación: bloquea duplicados entre principal y asistente sin distinguir mayúsculas");

        var html = EmailBodyBuilder.BuildHtml("Hola <script>alert('x')</script>\nSegunda línea",
            new SignatureImageInfo("firma.png", "firma.png", "image/png", 1200, 400), "cid-prueba");
        check(!html.Contains("<script>", StringComparison.OrdinalIgnoreCase) &&
              html.Contains("&lt;script&gt;", StringComparison.Ordinal) && html.Contains("<br>", StringComparison.Ordinal) &&
              html.Contains("cid:cid-prueba", StringComparison.Ordinal) && html.Contains("width=\"600\"", StringComparison.Ordinal),
            "HTML: codifica texto, conserva saltos, usa CID y limita la firma a 600 px");

        var pngPath = Path.Combine(testRoot, "firma-origen.png");
        var jpegPath = Path.Combine(testRoot, "firma-origen.jpg");
        CreateTestImage(pngPath, new PngBitmapEncoder());
        CreateTestImage(jpegPath, new JpegBitmapEncoder());
        var managedPng = signatureService.Import(pngPath);
        var pngInfo = SignatureImageService.ValidateFile(managedPng);
        var preview = signatureService.LoadPreview(managedPng);
        check(File.Exists(managedPng) && pngInfo.MimeType == "image/png" && preview.PixelWidth == 8,
            "Firma: importa PNG administrado, valida dimensiones y carga preview sin bloquear");
        var managedJpeg = signatureService.Import(jpegPath);
        check(File.Exists(managedPng) && File.Exists(managedJpeg) &&
              SignatureImageService.ValidateFile(managedJpeg).MimeType == "image/jpeg",
            "Firma: la nueva copia JPEG se completa antes de retirar la firma anterior");
        signatureService.DeleteIfManaged(managedPng);
        check(!File.Exists(managedPng) && File.Exists(managedJpeg),
            "Firma: quitar una copia administrada no afecta la firma nueva");

        var firstExcel = Path.Combine(testRoot, "comisiones-1.xlsx");
        var secondExcel = Path.Combine(testRoot, "comisiones-2.xls");
        File.WriteAllBytes(firstExcel, [0x01]);
        File.WriteAllBytes(secondExcel, [0x02]);
        var validRequest = new EmailSendRequest
        {
            BrokerId = alberto.Id,
            BrokerName = alberto.Name,
            BrokerPrimaryRecipients = albertoRecipients.BrokerPrimaryRecipients,
            AssistantRecipients = albertoRecipients.AssistantRecipients,
            ToRecipients = albertoRecipients.ToRecipients,
            CcRecipients = albertoRecipients.CcRecipients,
            Subject = "PRUEBA",
            Body = "Mensaje de prueba",
            AttachmentPaths = [firstExcel, secondExcel],
            SignatureImagePath = managedJpeg
        };
        check(validationService.ValidateRequest(validRequest).Count == 0,
            "Validación: solicitud con asistentes, Excel y firma válida está lista");
        check(validRequest.AttachmentPaths.Count == 2,
            "Adjuntos: la firma no se agrega ni cuenta dentro de los archivos Excel");

        var sessionItem = new BrokerSendItem
        {
            BrokerId = alberto.Id,
            BrokerName = alberto.Name,
            PrimaryRecipients = [.. alberto.PrimaryEmailAddresses],
            Assistants = alberto.Assistants.Select(value => value.Clone()).ToList(),
            AttachmentPaths = new ObservableCollection<string>([firstExcel, secondExcel]),
            IsSelected = true,
            Status = SendStatus.Ready
        };
        sessionService.SaveCurrent(new CurrentSession
        {
            Subject = "Prueba",
            Message = "Mensaje",
            CommonCcText = "cc@example.com",
            BrokerItems = [sessionItem]
        });
        var reloadedSession = sessionService.LoadCurrent();
        check(reloadedSession?.BrokerItems.Single().AttachmentPaths.Count == 2 &&
              reloadedSession.BrokerItems.Single().Assistants.Count == 2,
            "Sesión: restaura adjuntos y relaciones de asistentes del corredor correcto");

        var batchSelection = EmailBatchSelection.GetSelected(
        [
            new BrokerSendItem { BrokerName = "Seleccionado 1", IsSelected = true },
            new BrokerSendItem { BrokerName = "No seleccionado", IsSelected = false },
            new BrokerSendItem { BrokerName = "Seleccionado 2", IsSelected = true }
        ]);
        check(batchSelection.Count == 2 &&
              batchSelection.Select(value => value.BrokerName).SequenceEqual(["Seleccionado 1", "Seleccionado 2"]),
            "Envío múltiple: incluye todos los corredores marcados y excluye los no seleccionados");

        var archived = archiveService.Archive(alberto.Name, validRequest.AttachmentPaths);
        check(archived.Count == 2 && archived.All(File.Exists),
            "Archivo: conserva exactamente los dos Excel, sin archivar la firma inline");

        var originalRecord = new SentEmailRecord
        {
            BrokerId = alberto.Id,
            BrokerName = alberto.Name,
            BrokerPrimaryRecipients = [.. validRequest.BrokerPrimaryRecipients],
            AssistantRecipients = [.. validRequest.AssistantRecipients],
            ToRecipients = [.. validRequest.ToRecipients],
            CcRecipients = [.. validRequest.CcRecipients],
            Subject = validRequest.Subject,
            Body = validRequest.Body,
            ArchivedAttachmentPaths = archived,
            WasSuccessful = true
        };
        var correctedRecord = new SentEmailRecord
        {
            BrokerId = originalRecord.BrokerId,
            BrokerName = originalRecord.BrokerName,
            BrokerPrimaryRecipients = [.. originalRecord.BrokerPrimaryRecipients],
            AssistantRecipients = [.. originalRecord.AssistantRecipients],
            ToRecipients = [.. originalRecord.ToRecipients],
            CcRecipients = [.. originalRecord.CcRecipients],
            Subject = "Asunto corregido",
            Body = originalRecord.Body,
            ArchivedAttachmentPaths = [archived[1]],
            WasSuccessful = true,
            ResendOfRecordId = originalRecord.Id,
            SentAt = originalRecord.SentAt.AddMinutes(1)
        };
        sessionService.SaveRecentSends([originalRecord, correctedRecord]);
        var records = sessionService.LoadRecentSends();
        check(records.Count == 2 && records.Any(value => value.ResendOfRecordId == originalRecord.Id) &&
              records.All(value => value.ToRecipients.Count == 3),
            "Historial: reenvío conserva original y destinatarios reales, incluidos asistentes");

        var missingSignatureRequest = new EmailSendRequest
        {
            BrokerName = "Prueba",
            ToRecipients = ["self@example.com"],
            Subject = "PRUEBA",
            Body = "Mensaje",
            AttachmentPaths = [firstExcel],
            SignatureImagePath = Path.Combine(testRoot, "firma-inexistente.png")
        };
        check(validationService.ValidateRequest(missingSignatureRequest)
                .Any(value => value.Contains("firma", StringComparison.OrdinalIgnoreCase)),
            "Firma: una ruta configurada faltante produce recuperación controlada");

        var outlookEnvironment = OutlookEnvironmentInspector.Inspect();
        check(outlookEnvironment.ApartmentState == ApartmentState.STA &&
              !string.IsNullOrWhiteSpace(outlookEnvironment.ProcessArchitecture),
            "Outlook: la inspección estática valida el entorno sin crear una instancia COM");
    }

    private static void RunMigrationTests(string migrationRoot, Action<bool, string> check)
    {
        var paths = new AppDataPaths(migrationRoot);
        var logger = new FileLogger(paths);
        var customBroker = new Broker
        {
            Id = Guid.NewGuid(), Name = "Corredor creado por usuario", PrimaryEmailAddresses = ["usuario@example.com"]
        };
        var mergedEdgar = new Broker
        {
            Id = Guid.NewGuid(), Name = "Edgar Gomez",
            PrimaryEmailAddresses = ["egomez@essentialgroupla.com", "edgomez@essentialgroupla.com"]
        };
        var mergedFernando = new Broker
        {
            Id = Guid.NewGuid(), Name = "Fernando Cabada",
            PrimaryEmailAddresses = ["fernando@insura.cr", "fcabada@essentialgroupla.com"]
        };
        var legacy = new AppConfiguration
        {
            DefaultSubject = "Asunto personalizado",
            DefaultMessage = "Mensaje personalizado",
            SignatureImagePath = "C:\\firma-personalizada.png",
            CommonCcAddresses = ["jarias@esentialgroupla.com", "usuario-cc@example.com"],
            Brokers = [customBroker, mergedEdgar, mergedFernando],
            EmailDirectorySeedVersion = 0
        };
        File.WriteAllText(paths.ConfigurationFile, JsonSerializer.Serialize(legacy));
        var edgarAttachment = Path.Combine(migrationRoot, "edgar.xlsx");
        var fernandoAttachment = Path.Combine(migrationRoot, "fernando.xlsx");
        File.WriteAllBytes(edgarAttachment, [0x01]);
        File.WriteAllBytes(fernandoAttachment, [0x02]);
        File.WriteAllText(paths.CurrentSessionFile, JsonSerializer.Serialize(new CurrentSession
        {
            BrokerItems =
            [
                new BrokerSendItem { BrokerId = mergedEdgar.Id, BrokerName = mergedEdgar.Name, AttachmentPaths = new ObservableCollection<string>([edgarAttachment]) },
                new BrokerSendItem { BrokerId = mergedFernando.Id, BrokerName = mergedFernando.Name, AttachmentPaths = new ObservableCollection<string>([fernandoAttachment]) }
            ]
        }));
        File.WriteAllText(paths.RecentSendsFile, "[]");

        var service = new ConfigurationService(paths, logger);
        var migrated = service.Load();
        var backups = Directory.GetDirectories(paths.BackupsDirectory, "DirectoryMigrationV3_*");
        check(backups.Length == 1 && File.Exists(Path.Combine(backups[0], "configuracion.json")) &&
              File.Exists(Path.Combine(backups[0], "sesion-actual.json")) &&
              File.Exists(Path.Combine(backups[0], "envios-recientes.json")),
            "Migración: crea respaldo único de configuración, sesión e historial antes de modificar");
        check(migrated.EmailDirectorySeedVersion == 3 && !string.IsNullOrWhiteSpace(migrated.EmailDirectorySeedId) &&
              migrated.Brokers.Count == 65 &&
               migrated.Brokers.Any(value => value.Id == customBroker.Id),
            "Migración: importa los 64 registros oficiales y preserva un corredor local ajeno a la semilla");
        check(migrated.DefaultSubject == AppConfiguration.InitialSubject &&
              migrated.DefaultMessage == AppConfiguration.InitialMessage &&
              !string.IsNullOrWhiteSpace(migrated.SignatureImagePath) &&
              File.Exists(migrated.SignatureImagePath) &&
              migrated.SignatureImagePath.StartsWith(paths.SignatureDirectory, StringComparison.OrdinalIgnoreCase),
            "Migración: aplica asunto, mensaje y firma portátiles de la instantánea oficial");
        check(migrated.CommonCcAddresses.SequenceEqual(["angiebermudez472@gmail.com"], StringComparer.OrdinalIgnoreCase),
            "Migración: aplica exactamente la CC de la instantánea oficial");
        check(migrated.Brokers.Count(value => value.Name == "Edgar Gomez") == 2 &&
              migrated.Brokers.Count(value =>
                  value.SeedKey is "fernando-cabada-essential" or "fernando-cabada-insura") == 2,
            "Migración: divide Edgar y Fernando combinados sin deduplicar por nombre");

        var session = new SessionService(paths, logger).LoadCurrent();
        check(session is not null && session.BrokerItems.Sum(value => value.AttachmentPaths.Count) == 2 &&
              session.BrokerItems.All(value => value.Status == SendStatus.ReviewRequired && !value.IsSelected &&
                                               value.RequiresBatchReview &&
                                               !string.IsNullOrWhiteSpace(value.BatchReviewNote)),
            "Migración: no duplica adjuntos ambiguos y marca sus filas para revisión");
        check(session is not null && session.Subject == AppConfiguration.InitialSubject &&
              session.Message == AppConfiguration.InitialMessage &&
              session.CommonCcText == "angiebermudez472@gmail.com",
            "Migración: sincroniza los valores visibles de la sesión sin eliminar sus adjuntos");

        var secondLoad = service.Load();
        check(secondLoad.Brokers.Count == migrated.Brokers.Count &&
              Directory.GetDirectories(paths.BackupsDirectory, "DirectoryMigrationV3_*").Length == 1,
            "Migración: ejecutar por segunda vez no duplica datos ni respaldos");

        var deliberatelyDeleted = secondLoad.Brokers.Single(value => value.SeedKey == "adriana-arroyo-essential");
        secondLoad.Brokers.Remove(deliberatelyDeleted);
        service.Save(secondLoad);
        var afterDelete = service.Load();
        check(afterDelete.Brokers.All(value => value.SeedKey != "adriana-arroyo-essential"),
            "Migración: un registro eliminado después de aplicar la misma instantánea no reaparece");

        File.WriteAllText(paths.ConfigurationFile, "{ json dañado");
        var recovered = new ConfigurationService(paths, logger).Load();
        check(recovered.EmailDirectorySeedVersion == 3 && recovered.Brokers.Count == 64 &&
              recovered.CommonCcAddresses.Count == 1 &&
               Directory.GetFiles(migrationRoot, "configuracion.json.broken-*").Length == 1,
            "Recuperación: JSON corrupto se respalda, se aparta y recibe una configuración segura");
    }

    private static void RunPaymentAutomationTests(Action<bool, string> check)
    {
        var broker = new Broker
        {
            Id = Guid.NewGuid(),
            Name = "Corredor sintético",
            PrimaryEmailAddresses = ["synthetic@example.com"],
            AssociatedWorksheetNames = ["SYN", "SYN2", "SYN3"],
            Deductions =
            [
                new BrokerDeduction
                {
                    Description = "Ajuste sintético",
                    Amount = 10_000m,
                    Currency = DeductionCurrency.CRC,
                    ApplicationType = DeductionApplicationType.GrossCommission
                },
                new BrokerDeduction
                {
                    Description = "Ahorro sintético",
                    Amount = 15_000m,
                    Currency = DeductionCurrency.CRC,
                    ApplicationType = DeductionApplicationType.PayableAmount
                }
            ]
        };
        var mappingService = new WorksheetBrokerMappingService();
        var mapping = mappingService.Resolve(["syn", "SYN2", "SYN3"], [broker]);
        check(mapping.IsValid && mapping.Assignments.Count == 3 &&
              mapping.Assignments.All(value => value.Broker.Id == broker.Id),
            "Automatización: tres pestañas exactas se agrupan por el identificador estable de un corredor");
        var partial = mappingService.Resolve(["SYN Especial"], [broker]);
        check(!partial.IsValid && partial.MissingWorksheetNames.SequenceEqual(["SYN Especial"]),
            "Automatización: no utiliza coincidencias parciales para asociar pestañas");

        var calculation = new PaymentCalculationService().Calculate(
            new CommissionWorksheetAnalysis
            {
                WorksheetName = "SYN",
                Crc = new CommissionCurrencySummary
                {
                    Currency = DeductionCurrency.CRC,
                    HasCommission = true,
                    GrossCommission = 100_000m
                },
                Usd = new CommissionCurrencySummary
                {
                    Currency = DeductionCurrency.USD,
                    HasCommission = false
                }
            },
            broker.Deductions);
        check(calculation.IsValid &&
              calculation.Crc.AdjustedGrossCommission == 90_000m &&
              calculation.Crc.Vat == 11_700m &&
              calculation.Crc.Withholding == 1_800m &&
              calculation.Crc.DepositedAmount == 84_900m,
            "Automatización: calcula rebajo bruto, IVA, retención y rebajo al pago en el orden definido");
        check(!calculation.Usd.HasCommission &&
              calculation.Usd.GrossCommissionOriginal == 0m &&
              calculation.Usd.Vat == 0m &&
              calculation.Usd.DepositedAmount == 0m,
            "Automatización: una moneda sin comisión conserva todo el bloque financiero en cero");

        var exactMinimum = new PaymentCalculationService().Calculate(
            new CommissionWorksheetAnalysis
            {
                Crc = new CommissionCurrencySummary
                {
                    Currency = DeductionCurrency.CRC,
                    HasCommission = true,
                    GrossCommission = PaymentCalculationService.CrcMinimum
                },
                Usd = new CommissionCurrencySummary { Currency = DeductionCurrency.USD }
            },
            []);
        check(!exactMinimum.Crc.MinimumApplied && exactMinimum.Crc.DepositedAmount > 0,
            "Automatización: CRC exactamente en el mínimo sí se paga");

        var sanitizer = new FileNameSanitizer();
        var fileName = sanitizer.CreatePaymentFileName(
            "Corredor / sintético", "SYN:1", "IQ prueba 2026");
        check(fileName == "Detalle de pago - Corredor - sintético - SYN-1 - IQ prueba 2026.xlsx" &&
              sanitizer.ValidatePeriod("periodo.").Count > 0,
            "Automatización: sanitiza nombres de archivo y bloquea periodos inválidos de Windows");
    }

    private static void CreateTestImage(string path, BitmapEncoder encoder)
    {
        var pixels = new byte[8 * 8 * 3];
        for (var index = 0; index < pixels.Length; index += 3)
        {
            pixels[index] = 0xA8;
            pixels[index + 1] = 0x65;
            pixels[index + 2] = 0x1F;
        }

        var source = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgr24, null, pixels, 8 * 3);
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static void DeleteTestDirectory(string path, ICollection<string> results)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex)
        {
            results.Add($"AVISO - No se pudo borrar el directorio temporal {path}: {ex.Message}");
        }
    }
}
