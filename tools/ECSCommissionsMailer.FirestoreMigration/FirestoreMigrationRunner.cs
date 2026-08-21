using ECS.CommissionsMailer.Infrastructure.Firestore;
using ECS.CommissionsMailer.Infrastructure.Firestore.Bootstrap;

namespace ECSCommissionsMailer.FirestoreMigration;

public sealed record MigrationRunOptions(
    string Mode,
    string SourceDirectory,
    string? ProjectId,
    string DatabaseId);

public sealed class FirestoreMigrationRunner
{
    public async Task<int> RunAsync(MigrationRunOptions options, CancellationToken cancellationToken = default)
    {
        MigrationSource source;
        MigrationPlan plan;
        try
        {
            source = MigrationSourceLoader.Load(options.SourceDirectory);
            plan = MigrationPlanBuilder.Build(source);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Migración cancelada durante lectura local: {exception.Message}");
            return 2;
        }

        PrintDryRun(source, plan);
        if (plan.Errors.Count > 0)
        {
            Console.Error.WriteLine("DETENIDO: Errors > 0. No se permite --apply.");
            return 2;
        }

        if (string.Equals(options.Mode, "dry-run", StringComparison.Ordinal))
        {
            Console.WriteLine("DRY-RUN completado. No se abrió una conexión Firestore y no se modificó ningún JSON.");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(options.ProjectId))
        {
            Console.Error.WriteLine("ProjectId es obligatorio para --apply/--verify/--finalize-state. Use --project-id o ECS_FIRESTORE_PROJECT_ID.");
            Console.Error.WriteLine("Después configure ADC externamente con 'gcloud auth application-default login'; no agregue credenciales al repositorio.");
            return 2;
        }

        return options.Mode switch
        {
            "verify" => await VerifyOnlyAsync(options, source, plan, cancellationToken).ConfigureAwait(false),
            "finalize-state" => await FinalizeStateOnlyAsync(options, source, plan, cancellationToken).ConfigureAwait(false),
            "apply" => await ApplyAsync(options, source, plan, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Modo de migración desconocido: {options.Mode}.", nameof(options))
        };
    }

    private static async Task<int> ApplyAsync(
        MigrationRunOptions options,
        MigrationSource source,
        MigrationPlan plan,
        CancellationToken cancellationToken)
    {
        var report = CreateReport("apply", options, source, plan);
        MigrationBackup? backup = null;
        FirestoreMigrationStore? store = null;
        var verificationStarted = false;
        try
        {
            backup = MigrationBackupService.Create(source, options.ProjectId!, options.DatabaseId);
            report.MigrationId = backup.MigrationId;
            report.BackupDirectory = backup.Directory;
            Console.WriteLine($"Backup validado: {backup.Directory}");
            Console.WriteLine($"Manifest SHA256: {backup.ManifestSha256}");

            var database = CreateDatabase(options);
            store = new FirestoreMigrationStore(database);
            var bootstrap = await new FirestoreSchemaBootstrapper(database).PrepareAsync(cancellationToken).ConfigureAwait(false);
            var bootstrapState = await store.ReadBootstrapStateAsync(cancellationToken).ConfigureAwait(false);
            report.BootstrapSchemaExists = bootstrapState.SchemaExists;
            report.BootstrapMigrationStateExists = bootstrapState.MigrationStateExists;
            if (!bootstrapState.SchemaExists || !bootstrapState.MigrationStateExists)
            {
                throw new InvalidOperationException("El bootstrap no pudo verificarse mediante lectura.");
            }

            Console.WriteLine(bootstrap.SchemaCreated ? "Bootstrap creado: system/schema" : "Bootstrap idéntico/existente: system/schema");
            Console.WriteLine(bootstrap.MigrationStateCreated ? "Bootstrap creado: system/migrationState" : "Bootstrap idéntico/existente: system/migrationState");

            var existing = await store.ReadOperationalDocumentsAsync(cancellationToken).ConfigureAwait(false);
            var preflight = MigrationPreflightAnalyzer.Analyze(plan.Documents, existing);
            CopyPreflight(report, preflight);
            report.FirestoreCounts = CountFirestore(existing);
            PrintPreflight(preflight);
            if (preflight.Conflicts.Count > 0)
            {
                report.Conflicts.AddRange(preflight.Conflicts);
                report.Status = "conflict";
                await store.SetMigrationFailedAsync(
                    $"Preflight detectó {preflight.Conflicts.Count} conflictos.", cancellationToken).ConfigureAwait(false);
                WriteReport(report, backup.Directory);
                return 3;
            }

            await store.SetMigrationInProgressAsync(
                backup.ManifestSha256,
                source.Configuration.DataSchemaVersion,
                cancellationToken).ConfigureAwait(false);

            foreach (var document in plan.Documents)
            {
                LocalOnlyFieldGuard.ThrowIfForbiddenFieldsExist(document.ExpectedFields);
                var created = await store.CreateOrConfirmIdenticalAsync(document, cancellationToken).ConfigureAwait(false);
                if (created)
                {
                    report.Created.Add(document.Path);
                }
                else
                {
                    report.SkippedIdentical.Add(document.Path);
                }
            }

            verificationStarted = true;
            var after = await store.ReadOperationalDocumentsAsync(cancellationToken).ConfigureAwait(false);
            var verification = MigrationPreflightAnalyzer.Analyze(plan.Documents, after);
            ApplyVerification(report, plan, after, verification);
            report.IdempotentRerun = IsFullyIdentical(verification, plan.Documents.Count);
            if (report.MismatchDetails.Count > 0 || report.IdempotentRerun != true)
            {
                var summary =
                    $"La verificación encontró {report.MismatchDetails.Count} mismatches; no se declara completada.";
                report.Errors.Add(summary);
                report.Status = "failed";
                await store.SetMigrationFailedAsync(summary, cancellationToken).ConfigureAwait(false);
                var reportPaths = WriteReport(report, backup.Directory);
                PrintVerificationFailure(report.MismatchDetails.Count, reportPaths.TextPath);
                return 4;
            }

            await store.SetMigrationCompletedAsync(plan.Counts, cancellationToken).ConfigureAwait(false);
            report.Status = "completed";
            WriteReport(report, backup.Directory);
            Console.WriteLine($"APPLY completado: creados {report.Created.Count}, idénticos {report.SkippedIdentical.Count}.");
            Console.WriteLine("VERIFY completado: mismatches 0. Simulación idempotente: 0 documentos nuevos.");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            report.Errors.Add(exception.Message);
            report.Status = "failed";
            if (store is not null)
            {
                try
                {
                    await store.SetMigrationFailedAsync(exception.Message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception stateException)
                {
                    report.Errors.Add($"No fue posible registrar migrationState=failed: {stateException.Message}");
                }
            }

            (string JsonPath, string TextPath)? reportPaths = null;
            if (backup is not null)
            {
                reportPaths = WriteReport(report, backup.Directory);
            }

            if (verificationStarted)
            {
                PrintVerificationFailure(report.MismatchDetails.Count, reportPaths?.TextPath);
            }

            PrintFailure(exception);
            return 4;
        }
    }

    private static async Task<int> VerifyOnlyAsync(
        MigrationRunOptions options,
        MigrationSource source,
        MigrationPlan plan,
        CancellationToken cancellationToken)
    {
        var report = CreateReport("verify", options, source, plan);
        report.MigrationId = $"verify-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}";
        var reportDirectory = Path.Combine(source.Directory, "migration-reports", report.MigrationId);
        try
        {
            var database = CreateDatabase(options);
            var store = new FirestoreMigrationStore(database);
            var bootstrap = await store.ReadBootstrapStateAsync(cancellationToken).ConfigureAwait(false);
            report.BootstrapSchemaExists = bootstrap.SchemaExists;
            report.BootstrapMigrationStateExists = bootstrap.MigrationStateExists;
            var existing = await store.ReadOperationalDocumentsAsync(cancellationToken).ConfigureAwait(false);
            var verification = MigrationPreflightAnalyzer.Analyze(plan.Documents, existing);
            CopyPreflight(report, verification);
            PrintPreflight(verification);
            ApplyVerification(report, plan, existing, verification);
            report.IdempotentRerun = IsFullyIdentical(verification, plan.Documents.Count);
            report.Status = report.MismatchDetails.Count == 0 ? "verified" : "mismatch";
            var reportPaths = WriteReport(report, reportDirectory);
            if (report.MismatchDetails.Count > 0)
            {
                Console.Error.WriteLine(
                    $"VERIFY: mismatches {report.MismatchDetails.Count}. Reporte TXT: {reportPaths.TextPath}");
            }
            else
            {
                Console.WriteLine("VERIFY: mismatches 0.");
            }

            return report.MismatchDetails.Count == 0 ? 0 : 5;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            report.Errors.Add(exception.Message);
            report.Status = "failed";
            WriteReport(report, reportDirectory);
            PrintFailure(exception);
            return 4;
        }
    }

    private static async Task<int> FinalizeStateOnlyAsync(
        MigrationRunOptions options,
        MigrationSource source,
        MigrationPlan plan,
        CancellationToken cancellationToken)
    {
        var report = CreateReport("finalize-state", options, source, plan);
        report.MigrationId = $"finalize-state-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}";
        var reportDirectory = Path.Combine(source.Directory, "migration-reports", report.MigrationId);
        try
        {
            var database = CreateDatabase(options);
            var store = new FirestoreMigrationStore(database);
            var bootstrap = await store.ReadBootstrapStateAsync(cancellationToken).ConfigureAwait(false);
            report.BootstrapSchemaExists = bootstrap.SchemaExists;
            report.BootstrapMigrationStateExists = bootstrap.MigrationStateExists;
            if (!bootstrap.SchemaExists || !bootstrap.MigrationStateExists)
            {
                throw new InvalidOperationException(
                    "No se puede finalizar: system/schema y system/migrationState deben existir previamente.");
            }

            var existing = await store.ReadOperationalDocumentsAsync(cancellationToken).ConfigureAwait(false);
            var verification = MigrationPreflightAnalyzer.Analyze(plan.Documents, existing);
            CopyPreflight(report, verification);
            PrintPreflight(verification);
            ApplyVerification(report, plan, existing, verification);
            report.IdempotentRerun = IsFullyIdentical(verification, plan.Documents.Count);
            if (report.MismatchDetails.Count > 0 || report.IdempotentRerun != true)
            {
                report.Status = "mismatch";
                var reportPaths = WriteReport(report, reportDirectory);
                Console.Error.WriteLine(
                    $"FINALIZE-STATE cancelado: {report.MismatchDetails.Count} mismatches. " +
                    $"No se modificó migrationState. Reporte TXT: {reportPaths.TextPath}");
                return 5;
            }

            await store.SetMigrationCompletedAsync(plan.Counts, cancellationToken).ConfigureAwait(false);
            report.Status = "completed";
            WriteReport(report, reportDirectory);
            Console.WriteLine(
                "FINALIZE-STATE completado: se actualizó únicamente system/migrationState; " +
                "no se escribieron documentos operativos.");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            report.Errors.Add(exception.Message);
            report.Status = "failed";
            WriteReport(report, reportDirectory);
            PrintFailure(exception);
            return 4;
        }
    }

    private static Google.Cloud.Firestore.FirestoreDb CreateDatabase(MigrationRunOptions options) =>
        new FirestoreConnectionFactory().Create(new FirestoreOptions
        {
            ProjectId = options.ProjectId,
            DatabaseId = options.DatabaseId,
            Enabled = true
        });

    private static MigrationReport CreateReport(
        string mode,
        MigrationRunOptions options,
        MigrationSource source,
        MigrationPlan plan) => new()
        {
            MigrationId = $"{mode}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}",
            StartUtc = DateTimeOffset.UtcNow,
            Mode = mode,
            SourceDirectory = source.Directory,
            ProjectId = options.ProjectId ?? string.Empty,
            DatabaseId = options.DatabaseId,
            SourceFileHashes = source.Files.ToDictionary(value => value.Name, value => value.Sha256, StringComparer.Ordinal),
            LocalCounts = plan.Counts,
            LocalOnlyFieldsExcluded = new Dictionary<string, int>(plan.LocalOnlyExcluded, StringComparer.Ordinal),
            DecimalPrecisionValid = plan.Errors.Count == 0,
            IdsValid = plan.Errors.Count == 0,
            ReferencesValid = plan.Errors.Count == 0
        };

    private static void CopyPreflight(MigrationReport report, MigrationPreflightResult preflight)
    {
        report.PreflightToCreate = preflight.ToCreate.Count;
        report.PreflightIdentical = preflight.Identical.Count;
        report.PreflightConflicts = preflight.Conflicts.Count;
    }

    private static bool IsFullyIdentical(MigrationPreflightResult verification, int expectedDocumentCount) =>
        verification.ToCreate.Count == 0 && verification.Conflicts.Count == 0 &&
        verification.Identical.Count == expectedDocumentCount;

    private static void ApplyVerification(
        MigrationReport report,
        MigrationPlan plan,
        IReadOnlyList<ExistingFirestoreDocument> existing,
        MigrationPreflightResult verification)
    {
        report.FirestoreCounts = CountFirestore(existing);
        report.MismatchDetails = MigrationPreflightAnalyzer.ToVerificationMismatches(verification).ToList();
        foreach (var category in Enum.GetValues<MigrationDocumentCategory>())
        {
            var paths = plan.Documents
                .Where(value => value.Category == category)
                .Select(value => FirestoreDocumentPath.Normalize(value.Path))
                .ToHashSet(StringComparer.Ordinal);
            report.Verification[category.ToString()] = report.MismatchDetails.All(value =>
            {
                var canonicalPath = FirestoreDocumentPath.Normalize(value.Path);
                return !paths.Contains(canonicalPath) && !PathBelongsToCategory(canonicalPath, category);
            });
        }
    }

    private static bool PathBelongsToCategory(string path, MigrationDocumentCategory category)
    {
        var canonicalPath = FirestoreDocumentPath.Normalize(path);
        return category switch
        {
            MigrationDocumentCategory.Settings => canonicalPath.StartsWith("settings/", StringComparison.Ordinal),
            MigrationDocumentCategory.Broker => canonicalPath.StartsWith("brokers/", StringComparison.Ordinal),
            MigrationDocumentCategory.Session => canonicalPath.StartsWith("sessions/", StringComparison.Ordinal) &&
                                                 !canonicalPath.Contains("/brokerItems/", StringComparison.Ordinal),
            MigrationDocumentCategory.SessionBrokerItem => canonicalPath.StartsWith("sessions/", StringComparison.Ordinal) &&
                                                           canonicalPath.Contains("/brokerItems/", StringComparison.Ordinal),
            MigrationDocumentCategory.RecentSend => canonicalPath.StartsWith("recentSends/", StringComparison.Ordinal),
            MigrationDocumentCategory.PaymentGeneration => canonicalPath.StartsWith("paymentGenerations/", StringComparison.Ordinal) &&
                                                           !canonicalPath.Contains("/files/", StringComparison.Ordinal),
            MigrationDocumentCategory.PaymentGenerationFile => canonicalPath.StartsWith("paymentGenerations/", StringComparison.Ordinal) &&
                                                               canonicalPath.Contains("/files/", StringComparison.Ordinal),
            _ => false
        };
    }

    public static MigrationCounts CountFirestore(IEnumerable<ExistingFirestoreDocument> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var paths = existing.Select(value => FirestoreDocumentPath.Normalize(value.Path)).ToList();
        return new MigrationCounts(
            paths.Count(path => path.StartsWith("settings/", StringComparison.Ordinal) && path.Count(character => character == '/') == 1),
            paths.Count(path => path.StartsWith("brokers/", StringComparison.Ordinal) && path.Count(character => character == '/') == 1),
            paths.Count(path => path.StartsWith("sessions/", StringComparison.Ordinal) && path.Count(character => character == '/') == 1),
            paths.Count(path => path.StartsWith("sessions/", StringComparison.Ordinal) && path.Contains("/brokerItems/", StringComparison.Ordinal)),
            paths.Count(path => path.StartsWith("recentSends/", StringComparison.Ordinal) && path.Count(character => character == '/') == 1),
            paths.Count(path => path.StartsWith("paymentGenerations/", StringComparison.Ordinal) && path.Count(character => character == '/') == 1),
            paths.Count(path => path.StartsWith("paymentGenerations/", StringComparison.Ordinal) && path.Contains("/files/", StringComparison.Ordinal)));
    }

    private static void PrintDryRun(MigrationSource source, MigrationPlan plan)
    {
        var counts = plan.Counts;
        Console.WriteLine("DRY-RUN local obligatorio:");
        Console.WriteLine($"Source directory: {source.Directory}");
        Console.WriteLine("Settings:");
        Console.WriteLine($"  documentos a crear: {counts.Settings}");
        Console.WriteLine("Brokers:");
        Console.WriteLine($"  documentos a crear: {counts.Brokers}");
        Console.WriteLine("Session:");
        Console.WriteLine($"  documentos a crear: {counts.Session}");
        Console.WriteLine("Session BrokerItems:");
        Console.WriteLine($"  documentos a crear: {counts.SessionBrokerItems}");
        Console.WriteLine("Recent Sends:");
        Console.WriteLine($"  documentos a crear: {counts.RecentSends}");
        Console.WriteLine("Payment Generations:");
        Console.WriteLine($"  documentos a crear: {counts.PaymentGenerations}");
        Console.WriteLine("Payment Generation Files:");
        Console.WriteLine($"  documentos a crear: {counts.PaymentGenerationFiles}");
        Console.WriteLine("Local-only values excluded:");
        Console.WriteLine($"  cantidad: {plan.LocalOnlyExcluded.Values.Sum()}");
        Console.WriteLine("Errors:");
        Console.WriteLine($"  cantidad: {plan.Errors.Count}");
        Console.WriteLine("Warnings:");
        Console.WriteLine($"  cantidad: {plan.Warnings.Count}");
        foreach (var error in plan.Errors)
        {
            Console.Error.WriteLine($"ERROR: {error}");
        }

        foreach (var warning in plan.Warnings)
        {
            Console.WriteLine($"WARNING: {warning}");
        }
    }

    private static void PrintPreflight(MigrationPreflightResult preflight)
    {
        Console.WriteLine("Preflight Firestore:");
        Console.WriteLine($"  crear: {preflight.ToCreate.Count}");
        Console.WriteLine($"  identical/already migrated: {preflight.Identical.Count}");
        Console.WriteLine($"  conflicts: {preflight.Conflicts.Count}");
    }

    private static (string JsonPath, string TextPath) WriteReport(MigrationReport report, string directory)
    {
        var paths = MigrationReportWriter.Write(report, directory);
        Console.WriteLine($"Reporte JSON: {paths.JsonPath}");
        Console.WriteLine($"Reporte TXT: {paths.TextPath}");
        return paths;
    }

    private static void PrintVerificationFailure(int mismatchCount, string? reportPath)
    {
        Console.Error.WriteLine("La migración escribió los documentos, pero la verificación falló.");
        Console.Error.WriteLine(reportPath is null
            ? $"Mismatches: {mismatchCount}."
            : $"Mismatches: {mismatchCount}. Reporte TXT: {reportPath}");
    }

    private static void PrintFailure(Exception exception)
    {
        Console.Error.WriteLine($"Migración detenida: {exception.Message}");
        if (FirestoreAuthenticationFailureDetector.IsAuthenticationFailure(exception))
        {
            Console.Error.WriteLine("Para habilitar ADC externamente: instale Google Cloud CLI, ejecute 'gcloud auth application-default login', seleccione el proyecto autorizado y vuelva a ejecutar con --project-id <PROJECT_ID>. No agregue credenciales al repositorio.");
        }
    }
}
