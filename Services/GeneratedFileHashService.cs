using System.Security.Cryptography;

namespace ECS.CommissionsMailer.Services;

public sealed class GeneratedFileHashService
{
    public string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
