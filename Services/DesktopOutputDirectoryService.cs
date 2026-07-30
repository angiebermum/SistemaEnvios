namespace ECS.CommissionsMailer.Services;

public sealed class DesktopOutputDirectoryService
{
    public string GetDesktopDirectory()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new DirectoryNotFoundException("Windows no devolvió una ruta válida para el escritorio del usuario.");
        }

        return Path.GetFullPath(path);
    }

    public string GetOutputDirectory(string period) => Path.Combine(GetDesktopDirectory(), period);
}
