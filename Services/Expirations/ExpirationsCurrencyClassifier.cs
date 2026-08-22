using System.Globalization;
using System.Text;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsCurrencyClassifier
{
    ExpirationsCurrency Classify(string? rawValue);
}

public sealed class ExpirationsCurrencyClassifier : IExpirationsCurrencyClassifier
{
    private static readonly HashSet<string> CrcLabels = new(StringComparer.Ordinal)
    {
        "CRC",
        "COLON",
        "COLONES",
        "COLON COSTARRICENSE",
        "COLONES COSTARRICENSES",
        "₡"
    };

    private static readonly HashSet<string> UsdLabels = new(StringComparer.Ordinal)
    {
        "USD",
        "DOLAR",
        "DOLARES",
        "DOLAR ESTADOUNIDENSE",
        "DOLARES ESTADOUNIDENSES",
        "US$",
        "$"
    };

    public ExpirationsCurrency Classify(string? rawValue)
    {
        var normalized = Normalize(rawValue);
        if (CrcLabels.Contains(normalized))
            return ExpirationsCurrency.Crc;
        if (UsdLabels.Contains(normalized))
            return ExpirationsCurrency.Usd;
        return ExpirationsCurrency.Unsupported;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var decomposed = collapsed.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                result.Append(char.ToUpperInvariant(character));
        }
        return result.ToString().Normalize(NormalizationForm.FormC);
    }
}
