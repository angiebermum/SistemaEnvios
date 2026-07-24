using System.Windows.Media.Imaging;

namespace ECS.CommissionsMailer.Services;

public sealed record SignatureImageInfo(
    string Path,
    string FileName,
    string MimeType,
    int PixelWidth,
    int PixelHeight)
{
    public int DisplayWidth => Math.Min(600, PixelWidth);
}

public sealed class SignatureImageException : Exception
{
    public SignatureImageException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class SignatureImageService
{
    public const long MaximumFileSize = 5 * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg"
    };

    private readonly AppDataPaths _paths;

    public SignatureImageService(AppDataPaths paths) => _paths = paths;

    public string Import(string sourcePath)
    {
        var source = ValidateFile(sourcePath);
        Directory.CreateDirectory(_paths.SignatureDirectory);
        var extension = Path.GetExtension(source.Path).ToLowerInvariant();
        var fileName = $"firma-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension}";
        var destinationPath = Path.Combine(_paths.SignatureDirectory, fileName);
        var temporaryPath = Path.Combine(_paths.SignatureDirectory, $".{fileName}.tmp");
        try
        {
            File.Copy(source.Path, temporaryPath, false);
            _ = ValidateFile(temporaryPath, extension);
            File.Move(temporaryPath, destinationPath, false);
            return destinationPath;
        }
        catch (SignatureImageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SignatureImageException($"No fue posible guardar la firma en la carpeta administrada: {ex.Message}", ex);
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
                // Un temporal residual no invalida la copia administrada.
            }
        }
    }

    public BitmapSource LoadPreview(string path)
    {
        _ = ValidateFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void DeleteIfManaged(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var candidate = Path.GetFullPath(path);
        var managedRoot = Path.GetFullPath(_paths.SignatureDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (candidate.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(candidate);
        }
    }

    public static SignatureImageInfo ValidateFile(string path, string? expectedExtension = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new SignatureImageException("No se indicó un archivo de firma.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SignatureImageException("La ruta de la firma no es válida.", ex);
        }

        var extension = expectedExtension ?? Path.GetExtension(fullPath);
        if (!AllowedExtensions.Contains(extension))
        {
            throw new SignatureImageException("La firma debe ser una imagen PNG, JPG o JPEG.");
        }

        if (!File.Exists(fullPath))
        {
            throw new SignatureImageException($"No se encontró la firma configurada: {fullPath}");
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length <= 0)
        {
            throw new SignatureImageException("El archivo de firma está vacío.");
        }

        if (fileInfo.Length > MaximumFileSize)
        {
            throw new SignatureImageException("La firma supera el tamaño máximo permitido de 5 MB.");
        }

        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault()
                ?? throw new SignatureImageException("El archivo no contiene una imagen válida.");
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
            {
                throw new SignatureImageException("Las dimensiones de la firma no son válidas.");
            }

            var mimeType = extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg";
            return new SignatureImageInfo(fullPath, Path.GetFileName(fullPath), mimeType, frame.PixelWidth, frame.PixelHeight);
        }
        catch (SignatureImageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            throw new SignatureImageException($"No se pudo abrir la firma como una imagen válida: {ex.Message}", ex);
        }
    }
}
