using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsPremiumDataInspectionService
{
    IReadOnlyList<ExpirationsPremiumRowInspection> Inspect(
        string sourceWorkbookPath,
        string worksheetName,
        IReadOnlyList<uint> sourceRowNumbers,
        string premiumColumnReference,
        string currencyColumnReference,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsPremiumDataInspectionService : IExpirationsPremiumDataInspectionService
{
    public IReadOnlyList<ExpirationsPremiumRowInspection> Inspect(
        string sourceWorkbookPath,
        string worksheetName,
        IReadOnlyList<uint> sourceRowNumbers,
        string premiumColumnReference,
        string currencyColumnReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceWorkbookPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(worksheetName);
        ArgumentNullException.ThrowIfNull(sourceRowNumbers);
        DemandColumnReference(premiumColumnReference, nameof(premiumColumnReference));
        DemandColumnReference(currencyColumnReference, nameof(currencyColumnReference));

        var rowsToInspect = sourceRowNumbers.Distinct().Order().ToArray();
        var fullPath = Path.GetFullPath(sourceWorkbookPath);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart ??
            throw new ExpirationsGenerationException("El archivo no contiene un workbook válido.");
        var sheet = workbookPart.Workbook?.Sheets?.Elements<Sheet>().SingleOrDefault(item =>
            string.Equals(item.Name?.Value, worksheetName, StringComparison.Ordinal));
        if (sheet?.Id?.Value is not { Length: > 0 } relationshipId ||
            workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
        {
            throw new ExpirationsGenerationException($"La hoja analizada '{worksheetName}' ya no existe en el archivo.");
        }

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(item => item.InnerText)
            .ToList() ?? [];
        var sheetRows = worksheetPart.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>()
            .Where(row => row.RowIndex?.Value is not null)
            .GroupBy(row => row.RowIndex!.Value)
            .ToDictionary(group => group.Key, group => group.ToList()) ?? [];
        if (sheetRows.Any(item => item.Value.Count != 1))
            throw new ExpirationsGenerationException("La hoja contiene números de fila duplicados y no puede inspeccionarse con seguridad.");

        var result = new List<ExpirationsPremiumRowInspection>(rowsToInspect.Length);
        foreach (var rowNumber in rowsToInspect)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sheetRows.TryGetValue(rowNumber, out var matches))
                throw new ExpirationsGenerationException($"La fila fuente {rowNumber} ya no existe en la hoja analizada.");
            var row = matches[0];
            var premiumCell = FindCell(row, premiumColumnReference, rowNumber);
            var currencyCell = FindCell(row, currencyColumnReference, rowNumber);
            var rawPremium = CellText(premiumCell, sharedStrings);
            var premiumKind = PremiumKind(premiumCell, rawPremium, out var numericValue);
            result.Add(new ExpirationsPremiumRowInspection
            {
                RowNumber = rowNumber,
                RawCurrencyText = CellText(currencyCell, sharedStrings),
                CurrencyHasFormula = currencyCell?.CellFormula is not null,
                PremiumCellKind = premiumKind,
                RawPremiumText = rawPremium,
                NumericPremiumValue = numericValue
            });
        }
        return result;
    }

    private static Cell? FindCell(Row row, string columnReference, uint rowNumber)
    {
        var expected = $"{columnReference.ToUpperInvariant()}{rowNumber}";
        return row.Elements<Cell>().SingleOrDefault(cell =>
            string.Equals(cell.CellReference?.Value, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static ExpirationsPremiumCellKind PremiumKind(
        Cell? cell,
        string rawText,
        out double? numericValue)
    {
        numericValue = null;
        if (cell?.CellFormula is not null)
            return ExpirationsPremiumCellKind.Formula;
        if (cell is null || string.IsNullOrWhiteSpace(rawText))
            return ExpirationsPremiumCellKind.Missing;
        if (cell.DataType?.Value is not null && cell.DataType.Value != CellValues.Number)
            return ExpirationsPremiumCellKind.Text;
        if (!double.TryParse(rawText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            return ExpirationsPremiumCellKind.Text;
        }
        numericValue = parsed;
        return ExpirationsPremiumCellKind.Numeric;
    }

    private static string CellText(Cell? cell, IReadOnlyList<string> sharedStrings)
    {
        if (cell is null)
            return string.Empty;
        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? string.Empty;
        var raw = cell.CellValue?.InnerText ?? string.Empty;
        if (cell.DataType?.Value != CellValues.SharedString)
            return raw;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
            index < 0 || index >= sharedStrings.Count)
        {
            throw new ExpirationsGenerationException("El workbook contiene un índice de sharedStrings no válido.");
        }
        return sharedStrings[index];
    }

    private static void DemandColumnReference(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("La referencia de columna no es válida.", parameterName);
    }
}
