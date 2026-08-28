using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsNextMonthPeriodResolver
{
    ExpirationsPeriod Resolve(
        ExpirationsGenerationContext context,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsNextMonthPeriodResolver(
    ExpirationsBrokerNormalizer? normalizer = null) : IExpirationsNextMonthPeriodResolver
{
    private const string ExpirationHeader = "FECHA HASTA";
    private static readonly string[] AcceptedTextFormats =
    [
        "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "yyyy-MM-dd"
    ];
    private readonly ExpirationsBrokerNormalizer _normalizer =
        normalizer ?? new ExpirationsBrokerNormalizer();

    public ExpirationsPeriod Resolve(
        ExpirationsGenerationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Process != ExpirationsProcess.NextMonth)
            throw new ExpirationsGenerationException("El período automático corresponde únicamente al mes siguiente.");

        var relevantRows = (context.Analysis.ResolvedRowNumbersByDestination.Count > 0
                ? context.Analysis.ResolvedRowNumbersByDestination.Values
                : context.Analysis.ResolvedRowNumbersByBroker.Values)
            .SelectMany(rows => rows)
            .Distinct()
            .Order()
            .ToArray();
        if (relevantRows.Length == 0)
            throw new ExpirationsGenerationException("No existen pólizas distribuidas para determinar el período.");

        using var stream = new FileStream(
            context.SourceWorkbookPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart ??
            throw new ExpirationsGenerationException("El archivo no contiene un workbook válido.");
        var sheet = workbookPart.Workbook?.Sheets?.Elements<Sheet>().SingleOrDefault(item =>
            string.Equals(item.Name?.Value, context.SourceWorkbook.WorksheetName, StringComparison.Ordinal));
        if (sheet?.Id?.Value is not { Length: > 0 } relationshipId ||
            workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
        {
            throw new ExpirationsGenerationException(
                $"La hoja analizada '{context.SourceWorkbook.WorksheetName}' ya no existe en el archivo.");
        }

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(item => item.InnerText)
            .ToList() ?? [];
        var rows = worksheetPart.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>()
            .Where(row => row.RowIndex?.Value is not null)
            .GroupBy(row => row.RowIndex!.Value)
            .ToDictionary(group => group.Key, group => group.ToList()) ?? [];
        if (!rows.TryGetValue(context.SourceWorkbook.HeaderRowNumber, out var headerMatches) ||
            headerMatches.Count != 1)
        {
            throw new ExpirationsGenerationException("La fila de encabezado analizada no existe de forma única.");
        }

        var expirationColumns = headerMatches[0].Elements<Cell>()
            .Where(cell => _normalizer.Normalize(CellText(cell, sharedStrings)) == ExpirationHeader)
            .Select(ColumnIndex)
            .Where(index => index > 0)
            .Distinct()
            .ToArray();
        if (expirationColumns.Length != 1)
        {
            throw new ExpirationsGenerationException(expirationColumns.Length == 0
                ? "No se encontró la columna 'Fecha Hasta' para determinar el período de Mes siguiente."
                : "La columna 'Fecha Hasta' aparece duplicada y no permite determinar el período de Mes siguiente.");
        }

        var periods = new HashSet<(int Year, int Month)>();
        foreach (var rowNumber in relevantRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rows.TryGetValue(rowNumber, out var rowMatches) || rowMatches.Count != 1)
                throw new ExpirationsGenerationException($"La fila fuente {rowNumber} no existe de forma única.");
            var cell = rowMatches[0].Elements<Cell>()
                .SingleOrDefault(value => ColumnIndex(value) == expirationColumns[0]);
            var date = ReadExpirationDate(cell, rowNumber, sharedStrings);
            periods.Add((date.Year, date.Month));
        }

        if (periods.Count != 1)
        {
            var values = string.Join(", ", periods
                .OrderBy(value => value.Year)
                .ThenBy(value => value.Month)
                .Select(value => $"{value.Year:D4}-{value.Month:D2}"));
            throw new ExpirationsGenerationException(
                $"Las pólizas distribuidas no tienen un único período de vencimiento. Períodos encontrados: {values}.");
        }

        var period = periods.Single();
        try
        {
            return new ExpirationsPeriod(period.Year, period.Month);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ExpirationsGenerationException(
                $"El período de vencimiento {period.Year:D4}-{period.Month:D2} no es válido.");
        }
    }

    private static DateTime ReadExpirationDate(
        Cell? cell,
        uint rowNumber,
        IReadOnlyList<string> sharedStrings)
    {
        if (cell is null || cell.CellFormula is not null)
            throw InvalidDate(rowNumber);
        var value = CellText(cell, sharedStrings).Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            try
            {
                return DateTime.FromOADate(serial);
            }
            catch (ArgumentException)
            {
                throw InvalidDate(rowNumber);
            }
        }
        if (DateTime.TryParseExact(
                value,
                AcceptedTextFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed;
        }
        throw InvalidDate(rowNumber);
    }

    private static ExpirationsGenerationException InvalidDate(uint rowNumber) =>
        new($"Fila {rowNumber}: 'Fecha Hasta' no contiene una fecha de vencimiento válida.");

    private static string CellText(Cell cell, IReadOnlyList<string> sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? string.Empty;
        var raw = cell.CellValue?.Text ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
            index >= 0 && index < sharedStrings.Count)
        {
            return sharedStrings[index];
        }
        return raw;
    }

    private static int ColumnIndex(Cell cell)
    {
        var reference = cell.CellReference?.Value ?? string.Empty;
        var result = 0;
        foreach (var character in reference.TakeWhile(char.IsLetter).Select(char.ToUpperInvariant))
            result = checked(result * 26 + character - 'A' + 1);
        return result;
    }
}
