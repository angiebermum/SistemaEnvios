namespace ECS.CommissionsMailer.Services;

public sealed class FileLogger
{
    private readonly string _logFile;
    private readonly object _sync = new();

    public FileLogger(AppDataPaths paths) => _logFile = paths.LogFile;

    public string LogFilePath => _logFile;

    public void Info(string message) => Write("INFO", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            var exceptionText = exception is null
                ? string.Empty
                : $"{Environment.NewLine}{FormatException(exception)}";
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{exceptionText}{Environment.NewLine}";
            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
                File.AppendAllText(_logFile, line);
            }
        }
        catch
        {
            // El registro nunca debe interrumpir el funcionamiento principal.
        }
    }

    internal static string FormatException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var lines = new List<string>();
        var current = exception;
        var depth = 0;
        while (current is not null)
        {
            var prefix = depth == 0 ? "Excepción" : $"Excepción interna {depth}";
            lines.Add($"{prefix}.Tipo: {current.GetType().FullName}");
            lines.Add($"{prefix}.Mensaje: {current.Message}");
            lines.Add($"{prefix}.HResult: {current.HResult} (0x{unchecked((uint)current.HResult):X8})");
            lines.Add($"{prefix}.Source: {current.Source ?? "(sin datos)"}");
            lines.Add($"{prefix}.TargetSite: {current.TargetSite?.ToString() ?? "(sin datos)"}");
            lines.Add($"{prefix}.StackTrace:{Environment.NewLine}{current.StackTrace ?? "(sin datos)"}");
            current = current.InnerException;
            depth++;
        }

        return string.Join(Environment.NewLine, lines);
    }
}
