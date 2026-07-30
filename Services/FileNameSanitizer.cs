using System.Text;
using System.Text.RegularExpressions;

namespace ECS.CommissionsMailer.Services;

public sealed partial class FileNameSanitizer
{
    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);

    public List<string> ValidatePeriod(string? period)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(period))
        {
            errors.Add("El nombre de la quincena o periodo es obligatorio.");
            return errors;
        }

        if (period.Length > 80)
        {
            errors.Add("El nombre del periodo no puede superar 80 caracteres.");
        }

        if (period.EndsWith(' ') || period.EndsWith('.'))
        {
            errors.Add("El nombre del periodo no puede terminar con espacios ni puntos.");
        }

        if (period.Any(value => value < 32 || "<>:\"/\\|?*".Contains(value)))
        {
            errors.Add("El nombre del periodo contiene caracteres no permitidos por Windows.");
        }

        var baseName = period.Trim().Split('.')[0];
        if (ReservedNames.Contains(baseName))
        {
            errors.Add("El nombre del periodo está reservado por Windows.");
        }

        return errors;
    }

    public string CreatePaymentFileName(string brokerName, string worksheetName, string period)
    {
        var baseName = $"Detalle de pago - {SanitizePart(brokerName)} - {SanitizePart(worksheetName)} - {SanitizePart(period)}";
        if (baseName.Length > 190)
        {
            baseName = baseName[..190].TrimEnd(' ', '.', '-');
        }

        return baseName + ".xlsx";
    }

    public string SanitizePart(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(character < 32 || "<>:\"/\\|?*".Contains(character) ? '-' : character);
        }

        var sanitized = RepeatedWhitespace().Replace(builder.ToString(), " ").TrimEnd(' ', '.');
        return sanitized.Length == 0 ? "Sin nombre" : sanitized;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex RepeatedWhitespace();
}
