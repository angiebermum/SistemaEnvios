using System.Text.Json;
using System.Security.Cryptography;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

internal sealed record DirectoryMigrationResult(string? BackupDirectory, string? Warning);

internal sealed class DirectoryMigrationService
{
    private const string SeedRelativePath = "Data/correos-iniciales.v3.json";
    private readonly AppDataPaths _paths;
    private readonly FileLogger _logger;
    private readonly AtomicJsonFile _json;

    public DirectoryMigrationService(AppDataPaths paths, FileLogger logger)
    {
        _paths = paths;
        _logger = logger;
        _json = new AtomicJsonFile(logger);
    }

    public DirectoryMigrationResult ApplyIfNeeded(AppConfiguration configuration)
    {
        var seed = LoadSeed();
        ValidateSeed(seed);
        Normalize(configuration);
        if (!RequiresApply(configuration, seed))
        {
            return new DirectoryMigrationResult(null, null);
        }

        var backupDirectory = CreateBackupIfPresent(seed.SeedVersion);
        ApplyPackagedDefaults(configuration, seed);
        var splitBrokerIds = SplitIncorrectlyMergedBrokers(configuration);
        RemoveRetiredSeededBrokers(configuration, seed);
        MergeSeededDirectory(configuration, seed);

        string? splitWarning = null;
        if (splitBrokerIds.Count > 0)
        {
            splitWarning = MarkSplitSessionsForReview(splitBrokerIds);
        }

        SyncCurrentSessionDefaults(seed);
        configuration.EmailDirectorySeedVersion = seed.SeedVersion;
        configuration.EmailDirectorySeedId = seed.SeedId;
        _json.Save(_paths.ConfigurationFile, configuration);
        var warning = $"El directorio y la configuración inicial versión {seed.SeedVersion} se importaron correctamente" +
                      (string.IsNullOrWhiteSpace(backupDirectory) ? "." : $". Respaldo: {backupDirectory}.");
        if (!string.IsNullOrWhiteSpace(splitWarning))
        {
            warning += Environment.NewLine + splitWarning;
        }

        _logger.Info($"Migración de datos iniciales completada. Semilla={seed.SeedVersion}; Id={seed.SeedId}; sembrados={seed.Brokers.Count}; asistentes={seed.Brokers.Sum(value => value.Assistants.Count)}; totalConfigurado={configuration.Brokers.Count}.");
        return new DirectoryMigrationResult(backupDirectory, warning);
    }

    private string? CreateBackupIfPresent(int seedVersion)
    {
        if (!File.Exists(_paths.ConfigurationFile))
        {
            return null;
        }

        var backupDirectory = Path.Combine(
            _paths.BackupsDirectory,
            $"DirectoryMigrationV{seedVersion}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupDirectory);
        CopyIfPresent(_paths.ConfigurationFile, backupDirectory);
        CopyIfPresent(_paths.CurrentSessionFile, backupDirectory);
        CopyIfPresent(_paths.RecentSendsFile, backupDirectory);
        File.WriteAllLines(Path.Combine(backupDirectory, "migracion.txt"),
        [
            $"Migración de datos iniciales a versión {seedVersion}",
            $"Fecha: {DateTimeOffset.Now:O}",
            $"Origen: {_paths.RootDirectory}"
        ]);
        _logger.Info($"Respaldo previo a la migración creado en {backupDirectory}.");
        return backupDirectory;
    }

    public static void Normalize(AppConfiguration configuration)
    {
        configuration.Brokers ??= [];
        configuration.CommonCcAddresses ??= [];
        configuration.DataSchemaVersion = AppConfiguration.CurrentDataSchemaVersion;
        foreach (var broker in configuration.Brokers)
        {
            if (broker.Id == Guid.Empty)
            {
                broker.Id = Guid.NewGuid();
            }

            broker.PrimaryEmailAddresses ??= [];
            broker.Assistants ??= [];
            broker.AssociatedWorksheetNames ??= [];
            broker.AssociatedWorksheetNames = broker.AssociatedWorksheetNames
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            broker.Deductions ??= [];
            foreach (var deduction in broker.Deductions)
            {
                if (deduction.Id == Guid.Empty)
                {
                    deduction.Id = Guid.NewGuid();
                }

                deduction.Description = deduction.Description?.Trim() ?? string.Empty;
            }

            foreach (var assistant in broker.Assistants)
            {
                if (assistant.Id == Guid.Empty)
                {
                    assistant.Id = Guid.NewGuid();
                }
            }
        }
    }

    private EmailDirectorySeed LoadSeed()
    {
        var path = Path.Combine(AppContext.BaseDirectory, SeedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No se encontró el directorio inicial de correos.", path);
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<EmailDirectorySeed>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("El directorio inicial de correos está vacío o no es válido.");
    }

    private static void ValidateSeed(EmailDirectorySeed seed)
    {
        if (seed.SeedVersion != AppConfiguration.CurrentEmailDirectorySeedVersion ||
            string.IsNullOrWhiteSpace(seed.SeedId) || seed.SeedId.Length != 64 ||
            string.IsNullOrWhiteSpace(seed.DefaultSubject) || string.IsNullOrWhiteSpace(seed.DefaultMessage) ||
            seed.Brokers.Count == 0)
        {
            throw new InvalidDataException("La semilla de datos iniciales no contiene la versión, el identificador o los datos mínimos esperados.");
        }

        var duplicateSeedKeys = seed.Brokers.GroupBy(value => value.SeedKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicateSeedKeys.Count > 0)
        {
            throw new InvalidDataException($"Hay claves de semilla duplicadas: {string.Join(", ", duplicateSeedKeys)}.");
        }

        var duplicateBrokerIds = seed.Brokers.GroupBy(value => value.Id).Where(group => group.Count() > 1).ToList();
        if (seed.Brokers.Any(value => value.Id == Guid.Empty || string.IsNullOrWhiteSpace(value.SeedKey) ||
                                      string.IsNullOrWhiteSpace(value.Name) || value.PrimaryEmails.Count == 0) ||
            duplicateBrokerIds.Count > 0 ||
            seed.Brokers.SelectMany(value => value.Assistants).Any(value =>
                value.Id == Guid.Empty || string.IsNullOrWhiteSpace(value.Name) || string.IsNullOrWhiteSpace(value.Email)))
        {
            throw new InvalidDataException("La semilla contiene corredores o asistentes incompletos o identificadores duplicados.");
        }

        if (seed.CommonCc.Any(value => string.IsNullOrWhiteSpace(value.Email)) ||
            seed.CommonCc.Select(value => value.Email).Distinct(StringComparer.OrdinalIgnoreCase).Count() != seed.CommonCc.Count)
        {
            throw new InvalidDataException("La semilla contiene direcciones CC vacías o duplicadas.");
        }

        var hasSignatureFile = !string.IsNullOrWhiteSpace(seed.SignatureFile);
        var hasSignatureHash = !string.IsNullOrWhiteSpace(seed.SignatureSha256);
        if (hasSignatureFile != hasSignatureHash)
        {
            throw new InvalidDataException("La firma inicial debe incluir tanto el archivo como su suma SHA-256.");
        }
    }

    private static bool RequiresApply(AppConfiguration configuration, EmailDirectorySeed seed)
    {
        if (configuration.EmailDirectorySeedVersion > seed.SeedVersion)
        {
            return false;
        }

        return configuration.EmailDirectorySeedVersion < seed.SeedVersion ||
               !string.Equals(configuration.EmailDirectorySeedId, seed.SeedId, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyPackagedDefaults(AppConfiguration configuration, EmailDirectorySeed seed)
    {
        configuration.DefaultSubject = seed.DefaultSubject;
        configuration.DefaultMessage = seed.DefaultMessage;
        configuration.CommonCcAddresses = seed.CommonCc.Select(value => value.Email.Trim()).ToList();

        if (string.IsNullOrWhiteSpace(seed.SignatureFile))
        {
            configuration.SignatureImagePath = null;
            return;
        }

        var sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            seed.SignatureFile.Replace('/', Path.DirectorySeparatorChar));
        _ = SignatureImageService.ValidateFile(sourcePath);
        var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant();
        if (!string.Equals(actualHash, seed.SignatureSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("La firma inicial no coincide con la suma SHA-256 declarada en la semilla.");
        }

        configuration.SignatureImagePath = new SignatureImageService(_paths).Import(sourcePath);
    }

    private static List<Guid> SplitIncorrectlyMergedBrokers(AppConfiguration configuration)
    {
        var affected = new List<Guid>();
        SplitPair(configuration,
            "egomez@essentialgroupla.com", "edgar-gomez-egomez-essential",
            "edgomez@essentialgroupla.com", "edgar-gomez-edgomez-essential",
            affected);
        SplitPair(configuration,
            "fernando@insura.cr", "fernando-cabada-insura",
            "fcabada@essentialgroupla.com", "fernando-cabada-essential",
            affected);
        return affected;
    }

    private static void SplitPair(
        AppConfiguration configuration,
        string firstEmail,
        string firstSeedKey,
        string secondEmail,
        string secondSeedKey,
        ICollection<Guid> affected)
    {
        var merged = configuration.Brokers.FirstOrDefault(broker =>
            broker.PrimaryEmailAddresses.Contains(firstEmail, StringComparer.OrdinalIgnoreCase) &&
            broker.PrimaryEmailAddresses.Contains(secondEmail, StringComparer.OrdinalIgnoreCase));
        if (merged is null)
        {
            return;
        }

        merged.PrimaryEmailAddresses = [firstEmail];
        merged.SeedKey = firstSeedKey;
        var split = new Broker
        {
            Id = Guid.NewGuid(),
            SeedKey = secondSeedKey,
            Name = merged.Name,
            PrimaryEmailAddresses = [secondEmail],
            Assistants = [],
            IsActive = merged.IsActive,
            RequiresReview = merged.RequiresReview,
            ReviewNote = merged.ReviewNote
        };
        configuration.Brokers.Add(split);
        affected.Add(merged.Id);
    }

    private static void MergeSeededDirectory(AppConfiguration configuration, EmailDirectorySeed seed)
    {
        foreach (var seedBroker in seed.Brokers)
        {
            var broker = FindSeedMatch(configuration.Brokers, seedBroker);
            if (broker is null)
            {
                broker = new Broker
                {
                    Id = seedBroker.Id
                };
                configuration.Brokers.Add(broker);
            }

            broker.SeedKey = seedBroker.SeedKey;
            broker.Name = seedBroker.Name;
            broker.PrimaryEmailAddresses = [.. seedBroker.PrimaryEmails];
            broker.IsActive = seedBroker.IsActive;
            broker.RequiresReview = seedBroker.RequiresReview;
            broker.ReviewNote = seedBroker.ReviewNote;

            var existingAssistants = broker.Assistants ?? [];
            var synchronizedAssistants = new List<BrokerAssistant>();
            foreach (var seedAssistant in seedBroker.Assistants)
            {
                var assistant = existingAssistants.FirstOrDefault(value =>
                    string.Equals(value.Email, seedAssistant.Email, StringComparison.OrdinalIgnoreCase));
                if (assistant is null)
                {
                    assistant = new BrokerAssistant { Id = seedAssistant.Id };
                }

                assistant.Name = seedAssistant.Name;
                assistant.Email = seedAssistant.Email;
                assistant.IsActive = seedAssistant.IsActive;
                synchronizedAssistants.Add(assistant);
            }

            broker.Assistants = synchronizedAssistants;
        }
    }

    private static void RemoveRetiredSeededBrokers(AppConfiguration configuration, EmailDirectorySeed seed)
    {
        var currentKeys = seed.Brokers.Select(value => value.SeedKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        configuration.Brokers.RemoveAll(value =>
            !string.IsNullOrWhiteSpace(value.SeedKey) && !currentKeys.Contains(value.SeedKey));
    }

    private static Broker? FindSeedMatch(IEnumerable<Broker> brokers, SeedBroker seedBroker)
    {
        var byKey = brokers.FirstOrDefault(value =>
            !string.IsNullOrWhiteSpace(value.SeedKey) &&
            string.Equals(value.SeedKey, seedBroker.SeedKey, StringComparison.OrdinalIgnoreCase));
        if (byKey is not null)
        {
            return byKey;
        }

        var unseeded = brokers.Where(value => string.IsNullOrWhiteSpace(value.SeedKey)).ToList();
        var expectedEmails = seedBroker.PrimaryEmails.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exactEmailSet = unseeded.FirstOrDefault(value =>
            value.PrimaryEmailAddresses.Count == expectedEmails.Count &&
            value.PrimaryEmailAddresses.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedEmails));
        if (exactEmailSet is not null)
        {
            return exactEmailSet;
        }

        if (seedBroker.PrimaryEmails.Count != 1)
        {
            return null;
        }

        var email = seedBroker.PrimaryEmails[0];
        return unseeded.FirstOrDefault(value =>
            value.PrimaryEmailAddresses.Any(current => string.Equals(current, email, StringComparison.OrdinalIgnoreCase)));
    }

    private void SyncCurrentSessionDefaults(EmailDirectorySeed seed)
    {
        var session = _json.Load<CurrentSession>(_paths.CurrentSessionFile, out _);
        if (session is null)
        {
            return;
        }

        session.Subject = seed.DefaultSubject;
        session.Message = seed.DefaultMessage;
        session.CommonCcText = string.Join(Environment.NewLine, seed.CommonCc.Select(value => value.Email));
        _json.Save(_paths.CurrentSessionFile, session);
    }

    private string? MarkSplitSessionsForReview(IReadOnlyCollection<Guid> brokerIds)
    {
        var session = _json.Load<CurrentSession>(_paths.CurrentSessionFile, out _);
        if (session is null)
        {
            return "Se separaron registros familiares que estaban combinados.";
        }

        session.BrokerItems ??= [];
        var marked = false;
        foreach (var item in session.BrokerItems.Where(value => brokerIds.Contains(value.BrokerId)))
        {
            item.AttachmentPaths ??= [];
            if (item.AttachmentPaths.Count == 0)
            {
                continue;
            }

            item.RequiresReview = true;
            item.ReviewNote = "La migración separó dos corredores que estaban combinados. Verifique a quién pertenecen los archivos adjuntos antes de enviar.";
            item.RequiresBatchReview = true;
            item.BatchReviewNote = item.ReviewNote;
            item.LastError = item.ReviewNote;
            item.Status = SendStatus.ReviewRequired;
            item.IsSelected = false;
            marked = true;
        }

        if (marked)
        {
            _json.Save(_paths.CurrentSessionFile, session);
            return "Se separaron registros familiares combinados. Los adjuntos cuya propiedad no pudo determinarse quedaron en una sola fila y requieren revisión.";
        }

        return "Se separaron registros familiares que estaban combinados.";
    }

    private static void CopyIfPresent(string sourcePath, string backupDirectory)
    {
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, Path.Combine(backupDirectory, Path.GetFileName(sourcePath)), false);
        }
    }

    private sealed class EmailDirectorySeed
    {
        public int SeedVersion { get; set; }
        public string SeedId { get; set; } = string.Empty;
        public string DefaultSubject { get; set; } = string.Empty;
        public string DefaultMessage { get; set; } = string.Empty;
        public string? SignatureFile { get; set; }
        public string? SignatureSha256 { get; set; }
        public SeedOrganizationContact OrganizationContact { get; set; } = new();
        public List<SeedContact> CommonCc { get; set; } = [];
        public List<SeedBroker> Brokers { get; set; } = [];
    }

    private sealed class SeedOrganizationContact : SeedContact
    {
        public string Usage { get; set; } = string.Empty;
    }

    private class SeedContact
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    private sealed class SeedAssistant : SeedContact
    {
        public Guid Id { get; set; }
        public bool IsActive { get; set; } = true;
    }

    private sealed class SeedBroker
    {
        public Guid Id { get; set; }
        public string SeedKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<string> PrimaryEmails { get; set; } = [];
        public List<SeedAssistant> Assistants { get; set; } = [];
        public bool IsActive { get; set; } = true;
        public bool RequiresReview { get; set; }
        public string? ReviewNote { get; set; }
    }
}
