using System.Security.Cryptography;
using System.Text;

namespace ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authentication;

/// <summary>DPAPI CurrentUser storage. The plaintext token never reaches disk.</summary>
public sealed class ProtectedRefreshTokenStore(string path) : IRefreshTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ECSCommissionsMailer.FirebaseRefreshToken.v1");

    public async Task SaveAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("La ruta del token protegido no tiene directorio.");
        Directory.CreateDirectory(directory);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(refreshToken), Entropy, DataProtectionScope.CurrentUser);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (CryptographicException)
        {
            await ClearAsync(cancellationToken);
            return null;
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
