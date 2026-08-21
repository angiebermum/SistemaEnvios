using System.Globalization;
using System.Text;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsBrokerNormalizer
{
    public string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var needsSeparator = false;
        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (Rune.IsLetterOrDigit(rune))
            {
                if (needsSeparator && builder.Length > 0)
                    builder.Append(' ');
                builder.Append(rune.ToString());
                needsSeparator = false;
            }
            else
            {
                needsSeparator = builder.Length > 0;
            }
        }

        return builder.ToString().ToUpperInvariant();
    }
}
