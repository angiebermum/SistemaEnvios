using System.Globalization;

namespace ECS.CommissionsMailer.Models.Expirations;

public sealed record ExpirationsPeriod
{
    public ExpirationsPeriod(int year, int month)
    {
        if (year is < 2000 or > 2100)
            throw new ArgumentOutOfRangeException(nameof(year), "El año debe estar entre 2000 y 2100.");
        if (month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month), "El mes debe estar entre 1 y 12.");
        Year = year;
        Month = month;
    }

    public int Year { get; }
    public int Month { get; }
    public string FileToken => $"{Year:D4}-{Month:D2}";
    public string SpanishMonthName
    {
        get
        {
            var name = CultureInfo.GetCultureInfo("es-CR").DateTimeFormat.GetMonthName(Month);
            return CultureInfo.GetCultureInfo("es-CR").TextInfo.ToTitleCase(name);
        }
    }
}
