namespace ECS.CommissionsMailer.Services;

public sealed class AppDataPaths
{
    public AppDataPaths(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ECSCommissionsMailer");
        ConfigurationFile = Path.Combine(RootDirectory, "configuracion.json");
        CurrentSessionFile = Path.Combine(RootDirectory, "sesion-actual.json");
        RecentSendsFile = Path.Combine(RootDirectory, "envios-recientes.json");
        PaymentGenerationHistoryFile = Path.Combine(RootDirectory, "generaciones-detalles-pago.json");
        FirebaseRuntimeConfigurationFile = Path.Combine(RootDirectory, "firebase-runtime.json");
        OutlookPreferencesFile = Path.Combine(RootDirectory, "outlook-preferences.json");
        ProtectedRefreshTokenFile = Path.Combine(RootDirectory, "firebase-refresh-token.dat");
        LocalWorkspaceStateFile = Path.Combine(RootDirectory, "local-workspace.json");
        ExpirationsLocalSettingsFile = Path.Combine(RootDirectory, "vencimientos-local.json");
        FirestoreRuntimeStateFile = Path.Combine(RootDirectory, "firestore-runtime-state.json");
        FirestoreShadowReportsDirectory = Path.Combine(RootDirectory, "firestore-shadow-reports");
        FirestoreCutoverReportsDirectory = Path.Combine(RootDirectory, "firestore-cutover-reports");
        LogsDirectory = Path.Combine(RootDirectory, "Logs");
        LogFile = Path.Combine(LogsDirectory, "app.log");
        SentAttachmentsDirectory = Path.Combine(RootDirectory, "ArchivosEnviados");
        AssetsDirectory = Path.Combine(RootDirectory, "Assets");
        SignatureDirectory = Path.Combine(AssetsDirectory, "Firma");
        ExpirationsSignatureDirectory = Path.Combine(AssetsDirectory, "FirmaVencimientos");
        BackupsDirectory = Path.Combine(RootDirectory, "Backups");
        TemporaryEditsDirectory = Path.Combine(RootDirectory, "TempEdits");
        TemporaryViewsDirectory = Path.Combine(RootDirectory, "TempView");
        EnsureDirectories();
    }

    public string RootDirectory { get; }
    public string ConfigurationFile { get; }
    public string CurrentSessionFile { get; }
    public string RecentSendsFile { get; }
    public string PaymentGenerationHistoryFile { get; }
    public string FirebaseRuntimeConfigurationFile { get; }
    public string OutlookPreferencesFile { get; }
    public string ProtectedRefreshTokenFile { get; }
    public string LocalWorkspaceStateFile { get; }
    public string ExpirationsLocalSettingsFile { get; }
    public string FirestoreRuntimeStateFile { get; }
    public string FirestoreShadowReportsDirectory { get; }
    public string FirestoreCutoverReportsDirectory { get; }
    public string LogsDirectory { get; }
    public string LogFile { get; }
    public string SentAttachmentsDirectory { get; }
    public string AssetsDirectory { get; }
    public string SignatureDirectory { get; }
    public string ExpirationsSignatureDirectory { get; }
    public string BackupsDirectory { get; }
    public string TemporaryEditsDirectory { get; }
    public string TemporaryViewsDirectory { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(SentAttachmentsDirectory);
        Directory.CreateDirectory(SignatureDirectory);
        Directory.CreateDirectory(ExpirationsSignatureDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(TemporaryEditsDirectory);
    }
}
