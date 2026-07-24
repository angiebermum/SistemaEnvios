using System.Text.Json;
using System.Text.Json.Serialization;

namespace ECS.CommissionsMailer.Services;

internal sealed class AtomicJsonFile
{
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly FileLogger _logger;
    private readonly object _sync = new();

    public AtomicJsonFile(FileLogger logger) => _logger = logger;

    public T? Load<T>(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, _options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var brokenPath = $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmssfff}";
            try
            {
                File.Move(path, brokenPath, false);
                warning = $"El archivo {Path.GetFileName(path)} estaba dañado y fue renombrado como {Path.GetFileName(brokenPath)}. Se cargaron valores seguros.";
            }
            catch (Exception renameException)
            {
                warning = $"No se pudo leer {Path.GetFileName(path)}. Se cargaron valores seguros.";
                _logger.Error($"No se pudo apartar el archivo JSON inválido {path}.", renameException);
            }

            _logger.Error($"Error al cargar {path}.", ex);
            return default;
        }
    }

    public void Save<T>(string path, T value)
    {
        lock (_sync)
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var json = JsonSerializer.Serialize(value, _options);
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.Error($"Error al guardar {path}.", ex);
                throw new IOException($"No fue posible guardar {Path.GetFileName(path)}: {ex.Message}", ex);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                    // Un temporal residual no invalida el archivo final ya guardado.
                }
            }
        }
    }
}
