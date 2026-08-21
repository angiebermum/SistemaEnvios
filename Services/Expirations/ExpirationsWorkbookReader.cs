using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsWorkbookReader
{
    ExpirationsWorkbookReadResult Read(
        string sourcePath,
        ExpirationsWorkbookReadOptions? options = null);
}

public sealed class ExpirationsWorkbookReader(ExpirationsBrokerNormalizer? normalizer = null)
    : IExpirationsWorkbookReader
{
    private const uint MaximumAutomaticHeaderRow = 50;
    private readonly ExpirationsBrokerNormalizer _normalizer = normalizer ?? new ExpirationsBrokerNormalizer();

    public ExpirationsWorkbookReadResult Read(
        string sourcePath,
        ExpirationsWorkbookReadOptions? options = null)
    {
        options ??= new ExpirationsWorkbookReadOptions();
        var validation = ValidateRequest(sourcePath, options);
        if (validation is not null)
            return validation;

        try
        {
            var fullPath = Path.GetFullPath(sourcePath);
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = SpreadsheetDocument.Open(stream, false);
            var workbookPart = document.WorkbookPart
                ?? throw new InvalidDataException("El archivo no contiene una estructura de libro válida.");
            var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?
                .Elements<SharedStringItem>()
                .Select(item => item.InnerText)
                .ToList() ?? [];
            var sheets = GetWorksheets(workbookPart);
            if (sheets.Count == 0)
                return Invalid("El archivo .xlsx no contiene hojas de cálculo compatibles.");

            var selectedSheets = string.IsNullOrWhiteSpace(options.WorksheetName)
                ? sheets
                : sheets.Where(sheet => string.Equals(
                        sheet.Name,
                        options.WorksheetName.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
            if (selectedSheets.Count == 0)
            {
                return new ExpirationsWorkbookReadResult
                {
                    Status = ExpirationsWorkbookReadStatus.RequiresManualSelection,
                    Messages = [$"No existe la hoja indicada: '{options.WorksheetName}'."]
                };
            }

            var candidates = FindCandidates(selectedSheets, sharedStrings, options);
            if (candidates.Count == 0)
            {
                return new ExpirationsWorkbookReadResult
                {
                    Status = ExpirationsWorkbookReadStatus.HeaderNotFound,
                    Messages =
                    [
                        "No se encontró inequívocamente el encabezado 'Corredor' en las primeras 50 filas."
                    ]
                };
            }

            if (candidates.Count > 1)
            {
                return new ExpirationsWorkbookReadResult
                {
                    Status = ExpirationsWorkbookReadStatus.RequiresManualSelection,
                    Candidates = candidates.Select(candidate => candidate.Selection).ToList(),
                    Messages =
                    [
                        "Se encontraron varias hojas o columnas compatibles con 'Corredor'; se requiere selección manual."
                    ]
                };
            }

            var selected = candidates[0];
            var rows = ReadDataRows(
                selected.Sheet,
                sharedStrings,
                selected.Selection.HeaderRowNumber,
                selected.Selection.BrokerColumnIndex);
            return new ExpirationsWorkbookReadResult
            {
                Status = ExpirationsWorkbookReadStatus.Success,
                Workbook = new ExpirationsSourceWorkbook
                {
                    SourcePath = fullPath,
                    WorksheetName = selected.Sheet.Name,
                    HeaderRowNumber = selected.Selection.HeaderRowNumber,
                    BrokerColumnIndex = selected.Selection.BrokerColumnIndex,
                    Rows = rows
                },
                Candidates = [selected.Selection]
            };
        }
        catch (OpenXmlPackageException ex)
        {
            return Invalid($"El archivo no es un libro .xlsx válido: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   ArgumentException or NotSupportedException)
        {
            return Invalid($"No se pudo leer el libro .xlsx: {ex.Message}");
        }
    }

    private static ExpirationsWorkbookReadResult? ValidateRequest(
        string sourcePath,
        ExpirationsWorkbookReadOptions options)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return Invalid("El archivo de Vencimientos no existe.");
        if (!string.Equals(Path.GetExtension(sourcePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return Invalid("Solo se admite el reporte general en formato .xlsx.");
        if (options.HeaderRowNumber == 0)
            return Invalid("HeaderRowNumber debe ser mayor que cero.");
        if (options.BrokerColumnIndex <= 0)
            return Invalid("BrokerColumnIndex debe ser mayor que cero.");
        return null;
    }

    private List<ReaderCandidate> FindCandidates(
        IReadOnlyList<ReaderSheet> sheets,
        IReadOnlyList<string> sharedStrings,
        ExpirationsWorkbookReadOptions options)
    {
        if (options.HeaderRowNumber is { } explicitRow && options.BrokerColumnIndex is { } explicitColumn)
        {
            return sheets
                .Where(sheet => HasCell(sheet, explicitRow, explicitColumn))
                .Select(sheet => new ReaderCandidate(
                    sheet,
                    new ExpirationsWorkbookSelectionCandidate
                    {
                        WorksheetName = sheet.Name,
                        HeaderRowNumber = explicitRow,
                        BrokerColumnIndex = explicitColumn,
                        ColumnReference = ToColumnReference(explicitColumn)
                    }))
                .ToList();
        }

        var candidates = new List<ReaderCandidate>();
        foreach (var sheet in sheets)
        {
            var rows = sheet.Part.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>() ?? [];
            foreach (var row in rows)
            {
                var rowNumber = row.RowIndex?.Value ?? 0;
                if (rowNumber == 0 ||
                    (options.HeaderRowNumber is null && rowNumber > MaximumAutomaticHeaderRow) ||
                    (options.HeaderRowNumber is { } requiredRow && rowNumber != requiredRow))
                {
                    continue;
                }

                var parsedCells = ReadCells(row, sharedStrings);
                foreach (var parsed in parsedCells)
                {
                    if (options.BrokerColumnIndex is { } requiredColumn &&
                        parsed.Cell.ColumnIndex != requiredColumn)
                    {
                        continue;
                    }

                    if (_normalizer.Normalize(parsed.Cell.DisplayText) != "CORREDOR")
                        continue;

                    candidates.Add(new ReaderCandidate(
                        sheet,
                        new ExpirationsWorkbookSelectionCandidate
                        {
                            WorksheetName = sheet.Name,
                            HeaderRowNumber = rowNumber,
                            BrokerColumnIndex = parsed.Cell.ColumnIndex,
                            ColumnReference = parsed.Cell.ColumnReference
                        }));
                }
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Sheet.Order)
            .ThenBy(candidate => candidate.Selection.HeaderRowNumber)
            .ThenBy(candidate => candidate.Selection.BrokerColumnIndex)
            .ToList();
    }

    private static IReadOnlyList<ExpirationsSourceRow> ReadDataRows(
        ReaderSheet sheet,
        IReadOnlyList<string> sharedStrings,
        uint headerRowNumber,
        int brokerColumnIndex)
    {
        var rows = sheet.Part.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>() ?? [];
        return rows
            .Where(row => (row.RowIndex?.Value ?? 0) > headerRowNumber)
            .Select(row => new
            {
                RowNumber = row.RowIndex?.Value ?? 0,
                Cells = ReadCells(row, sharedStrings)
            })
            .Where(row => row.RowNumber > 0 && row.Cells.Any(cell => cell.HasSourceContent))
            .OrderBy(row => row.RowNumber)
            .Select(row => new ExpirationsSourceRow
            {
                RowNumber = row.RowNumber,
                RawBrokerValue = row.Cells
                    .FirstOrDefault(cell => cell.Cell.ColumnIndex == brokerColumnIndex)?
                    .Cell.DisplayText ?? string.Empty,
                Cells = row.Cells.Select(cell => cell.Cell).ToList()
            })
            .ToList();
    }

    private static List<ParsedCell> ReadCells(Row row, IReadOnlyList<string> sharedStrings)
    {
        var cells = new List<ParsedCell>();
        var fallbackColumnIndex = 1;
        foreach (var cell in row.Elements<Cell>())
        {
            var reference = cell.CellReference?.Value ?? string.Empty;
            var columnReference = GetColumnReference(reference);
            var columnIndex = GetColumnIndex(columnReference);
            if (columnIndex <= 0)
            {
                columnIndex = fallbackColumnIndex;
                columnReference = ToColumnReference(columnIndex);
            }

            fallbackColumnIndex = columnIndex + 1;
            var rawText = GetRawText(cell);
            var displayText = GetDisplayText(cell, rawText, sharedStrings);
            cells.Add(new ParsedCell(
                new ExpirationsSourceCell
                {
                    ColumnIndex = columnIndex,
                    ColumnReference = columnReference,
                    RawText = rawText,
                    DisplayText = displayText
                },
                !string.IsNullOrWhiteSpace(rawText) ||
                !string.IsNullOrWhiteSpace(displayText) ||
                cell.CellFormula is not null));
        }

        return cells.OrderBy(cell => cell.Cell.ColumnIndex).ToList();
    }

    private static string GetRawText(Cell cell)
    {
        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? string.Empty;
        return cell.CellValue?.Text ?? string.Empty;
    }

    private static string GetDisplayText(
        Cell cell,
        string rawText,
        IReadOnlyList<string> sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(rawText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
            index >= 0 && index < sharedStrings.Count)
        {
            return sharedStrings[index];
        }

        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? string.Empty;
        return rawText;
    }

    private static List<ReaderSheet> GetWorksheets(WorkbookPart workbookPart)
    {
        var sheets = workbookPart.Workbook?.Sheets?.Elements<Sheet>() ?? [];
        var result = new List<ReaderSheet>();
        var order = 0;
        foreach (var sheet in sheets)
        {
            order++;
            if (sheet.Id?.Value is not { } relationshipId ||
                workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                continue;
            }

            result.Add(new ReaderSheet(sheet.Name?.Value ?? string.Empty, worksheetPart, order));
        }

        return result;
    }

    private static bool HasCell(ReaderSheet sheet, uint rowNumber, int columnIndex) =>
        sheet.Part.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>()
            .Where(row => row.RowIndex?.Value == rowNumber)
            .SelectMany(row => row.Elements<Cell>())
            .Any(cell => GetColumnIndex(GetColumnReference(cell.CellReference?.Value ?? string.Empty)) == columnIndex) ==
        true;

    private static string GetColumnReference(string cellReference) =>
        new(cellReference.TakeWhile(char.IsLetter).ToArray());

    private static int GetColumnIndex(string columnReference)
    {
        var result = 0;
        foreach (var character in columnReference.ToUpperInvariant())
        {
            if (character is < 'A' or > 'Z')
                return 0;
            result = checked(result * 26 + character - 'A' + 1);
        }
        return result;
    }

    private static string ToColumnReference(int columnIndex)
    {
        var result = string.Empty;
        while (columnIndex > 0)
        {
            columnIndex--;
            result = (char)('A' + columnIndex % 26) + result;
            columnIndex /= 26;
        }
        return result;
    }

    private static ExpirationsWorkbookReadResult Invalid(string message) => new()
    {
        Status = ExpirationsWorkbookReadStatus.InvalidFile,
        Messages = [message]
    };

    private sealed record ReaderSheet(string Name, WorksheetPart Part, int Order);
    private sealed record ReaderCandidate(
        ReaderSheet Sheet,
        ExpirationsWorkbookSelectionCandidate Selection);
    private sealed record ParsedCell(ExpirationsSourceCell Cell, bool HasSourceContent);
}
