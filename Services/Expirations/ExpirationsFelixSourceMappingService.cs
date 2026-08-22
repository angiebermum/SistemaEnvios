using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsFelixSourceMappingService
{
    ExpirationsFelixGenerationPlan CreatePlan(
        ExpirationsGenerationContext context,
        ExpirationsPeriod period,
        IReadOnlyList<uint> sourceRowNumbers,
        ExpirationsPremiumColumnResolution premiumColumns,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsFelixSourceMappingService(
    ExpirationsBrokerNormalizer? normalizer = null,
    IExpirationsCurrencyClassifier? currencyClassifier = null)
    : IExpirationsFelixSourceMappingService
{
    private readonly ExpirationsBrokerNormalizer _normalizer = normalizer ?? new ExpirationsBrokerNormalizer();
    private readonly IExpirationsCurrencyClassifier _currencyClassifier =
        currencyClassifier ?? new ExpirationsCurrencyClassifier();

    public ExpirationsFelixGenerationPlan CreatePlan(
        ExpirationsGenerationContext context,
        ExpirationsPeriod period,
        IReadOnlyList<uint> sourceRowNumbers,
        ExpirationsPremiumColumnResolution premiumColumns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(sourceRowNumbers);
        ArgumentNullException.ThrowIfNull(premiumColumns);
        if (!premiumColumns.IsSuccess)
            throw new ExpirationsGenerationException("No se resolvieron las columnas Prima y Moneda.");

        var selectedRows = sourceRowNumbers.Distinct().Order().ToArray();
        if (selectedRows.Length == 0)
            throw new ExpirationsGenerationException("No existen filas para crear el formato especial.");

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
        if (rows.Any(item => item.Value.Count != 1))
            throw new ExpirationsGenerationException("La hoja contiene números de fila duplicados.");
        if (!rows.TryGetValue(context.SourceWorkbook.HeaderRowNumber, out var headerMatches))
            throw new ExpirationsGenerationException("La fila de encabezado analizada ya no existe.");

        var headerCells = headerMatches[0].Elements<Cell>()
            .Select(cell => new HeaderCell(ColumnIndex(cell), CellText(cell, sharedStrings)))
            .Where(item => item.ColumnIndex > 0)
            .ToList();
        var mappedColumns = ExpirationsFelixTemplateDefinition.VisibleColumns
            .Where(column => !string.Equals(column.TargetHeader, "Prima", StringComparison.Ordinal))
            .ToDictionary(
                column => column.TargetHeader,
                column => ResolveRequiredColumn(headerCells, column.SourceHeader));
        mappedColumns["Prima"] = premiumColumns.PremiumColumnIndex;

        var result = new List<ExpirationsFelixRow>(selectedRows.Length);
        foreach (var rowNumber in selectedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rows.TryGetValue(rowNumber, out var rowMatches))
                throw new ExpirationsGenerationException($"La fila fuente {rowNumber} ya no existe.");
            var row = rowMatches[0];
            var insurer = RequiredText(row, mappedColumns["Aseguradora"], rowNumber, "Aseguradora", sharedStrings);
            var currency = RequiredText(
                row,
                premiumColumns.CurrencyColumnIndex,
                rowNumber,
                "Moneda",
                sharedStrings);
            if (_currencyClassifier.Classify(currency) == ExpirationsCurrency.Unsupported)
            {
                throw new ExpirationsGenerationException(
                    $"Fila {rowNumber}: la moneda '{currency}' no está reconocida.");
            }

            var expirationCell = CellAt(row, mappedColumns["Vencimiento"]);
            var expirationSerial = RequiredNumber(expirationCell, rowNumber, "Vencimiento");
            DateTime expirationDate;
            try
            {
                expirationDate = DateTime.FromOADate(expirationSerial);
            }
            catch (ArgumentException)
            {
                throw new ExpirationsGenerationException(
                    $"Fila {rowNumber}: Vencimiento no contiene una fecha numérica válida de Excel.");
            }

            result.Add(new ExpirationsFelixRow
            {
                SourceRowNumber = rowNumber,
                PolicyNumber = CellText(CellAt(row, mappedColumns["Número de Póliza"]), sharedStrings),
                InsuredName = CellText(CellAt(row, mappedColumns["Nombre del Asegurado"]), sharedStrings),
                Insurer = insurer,
                ExpirationDateSerial = expirationSerial,
                ExpirationDate = expirationDate,
                Premium = RequiredNumber(CellAt(row, mappedColumns["Prima"]), rowNumber, "Prima"),
                PaymentPeriod = CellText(CellAt(row, mappedColumns["Período Pago"]), sharedStrings),
                Plate = CellText(CellAt(row, mappedColumns["Placa"]), sharedStrings),
                Currency = currency,
                IsIns = string.Equals(_normalizer.Normalize(insurer), "INS", StringComparison.Ordinal)
            });
        }

        return new ExpirationsFelixGenerationPlan
        {
            Period = period,
            CanonicalRows = result.OrderBy(row => row.SourceRowNumber).ToArray()
        };
    }

    private int ResolveRequiredColumn(IReadOnlyList<HeaderCell> headerCells, string sourceHeader)
    {
        var expected = _normalizer.Normalize(sourceHeader);
        var matches = headerCells
            .Where(cell => string.Equals(_normalizer.Normalize(cell.Text), expected, StringComparison.Ordinal))
            .Select(cell => cell.ColumnIndex)
            .Distinct()
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExpirationsGenerationException(
                $"Falta el encabezado requerido '{sourceHeader}' para el formato especial."),
            _ => throw new ExpirationsGenerationException(
                $"El encabezado requerido '{sourceHeader}' aparece duplicado y es ambiguo para el formato especial.")
        };
    }

    private static string RequiredText(
        Row row,
        int columnIndex,
        uint rowNumber,
        string fieldName,
        IReadOnlyList<string> sharedStrings)
    {
        var value = CellText(CellAt(row, columnIndex), sharedStrings);
        if (string.IsNullOrWhiteSpace(value))
            throw new ExpirationsGenerationException($"Fila {rowNumber}: {fieldName} está vacío.");
        return value;
    }

    private static double RequiredNumber(Cell? cell, uint rowNumber, string fieldName)
    {
        if (cell?.CellFormula is not null)
            throw new ExpirationsGenerationException($"Fila {rowNumber}: {fieldName} contiene una fórmula.");
        if (cell is null)
            throw new ExpirationsGenerationException($"Fila {rowNumber}: {fieldName} está vacío.");
        if (cell.DataType?.Value is not null && cell.DataType.Value != CellValues.Number)
            throw new ExpirationsGenerationException($"Fila {rowNumber}: {fieldName} no es un valor numérico de Excel.");
        var raw = cell.CellValue?.InnerText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            throw new ExpirationsGenerationException($"Fila {rowNumber}: {fieldName} está vacío.");
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            double.IsNaN(value) ||
            double.IsInfinity(value))
        {
            throw new ExpirationsGenerationException($"Fila {rowNumber}: {fieldName} no es un valor numérico de Excel.");
        }
        return value;
    }

    private static Cell? CellAt(Row row, int columnIndex) => row.Elements<Cell>().SingleOrDefault(cell =>
        ColumnIndex(cell) == columnIndex);

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
            index < 0 ||
            index >= sharedStrings.Count)
        {
            throw new ExpirationsGenerationException("El workbook contiene un índice sharedStrings inválido.");
        }
        return sharedStrings[index];
    }

    private static int ColumnIndex(Cell cell)
    {
        var reference = cell.CellReference?.Value ?? string.Empty;
        var result = 0;
        foreach (var character in reference.TakeWhile(char.IsLetter).Select(char.ToUpperInvariant))
        {
            if (character is < 'A' or > 'Z')
                return 0;
            result = checked(result * 26 + character - 'A' + 1);
        }
        return result;
    }

    private sealed record HeaderCell(int ColumnIndex, string Text);
}
