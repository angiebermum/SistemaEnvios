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
        LogsDirectory = Path.Combine(RootDirectory, "Logs");
        LogFile = Path.Combine(LogsDirectory, "app.log");
        SentAttachmentsDirectory = Path.Combine(RootDirectory, "ArchivosEnviados");
        AssetsDirectory = Path.Combine(RootDirectory, "Assets");
        SignatureDirectory = Path.Combine(AssetsDirectory, "Firma");
        BackupsDirectory = Path.Combine(RootDirectory, "Backups");
        EnsureDirectories();
    }

    public string RootDirectory { get; }
    public string ConfigurationFile { get; }
    public string CurrentSessionFile { get; }
    public string RecentSendsFile { get; }
    public string PaymentGenerationHistoryFile { get; }
    public string LogsDirectory { get; }
    public string LogFile { get; }
    public string SentAttachmentsDirectory { get; }
    public string AssetsDirectory { get; }
    public string SignatureDirectory { get; }
    public string BackupsDirectory { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(SentAttachmentsDirectory);
        Directory.CreateDirectory(SignatureDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}
