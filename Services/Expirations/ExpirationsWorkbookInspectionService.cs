using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsWorkbookInspectionService
{
    ExpirationsWorkbookInspection Inspect(string sourcePath);
}

public sealed class ExpirationsWorkbookInspectionService : IExpirationsWorkbookInspectionService
{
    public ExpirationsWorkbookInspection Inspect(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new InvalidOperationException("El archivo de Vencimientos no existe.");
        if (!string.Equals(Path.GetExtension(sourcePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Solo se admite inspección de archivos .xlsx.");

        var fullPath = Path.GetFullPath(sourcePath);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("El archivo no contiene una estructura de libro válida.");
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(item => item.InnerText)
            .ToList() ?? [];
        var worksheets = new List<ExpirationsWorksheetInspection>();
        foreach (var sheet in workbookPart.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not { } relationshipId ||
                workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                continue;
            }

            var rows = new List<ExpirationsHeaderRowInspection>();
            foreach (var row in worksheetPart.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>() ?? [])
            {
                var rowNumber = row.RowIndex?.Value ?? 0;
                if (rowNumber == 0 || rowNumber > 50)
                    continue;
                var columns = row.Elements<Cell>()
                    .Select(cell => ReadColumn(cell, sharedStrings))
                    .Where(column => column.ColumnIndex > 0)
                    .OrderBy(column => column.ColumnIndex)
                    .ToList();
                if (columns.Count == 0 || columns.All(column => string.IsNullOrWhiteSpace(column.HeaderText)))
                    continue;
                rows.Add(new ExpirationsHeaderRowInspection
                {
                    RowNumber = rowNumber,
                    Columns = columns
                });
            }

            worksheets.Add(new ExpirationsWorksheetInspection
            {
                WorksheetName = sheet.Name?.Value ?? string.Empty,
                HeaderRows = rows
            });
        }

        return new ExpirationsWorkbookInspection
        {
            SourcePath = fullPath,
            Worksheets = worksheets
        };
    }

    private static ExpirationsColumnInspection ReadColumn(
        Cell cell,
        IReadOnlyList<string> sharedStrings)
    {
        var reference = new string((cell.CellReference?.Value ?? string.Empty)
            .TakeWhile(char.IsLetter)
            .ToArray());
        var raw = cell.DataType?.Value == CellValues.InlineString
            ? cell.InlineString?.InnerText ?? string.Empty
            : cell.CellValue?.Text ?? string.Empty;
        var display = raw;
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
            index >= 0 && index < sharedStrings.Count)
        {
            display = sharedStrings[index];
        }

        return new ExpirationsColumnInspection
        {
            ColumnIndex = ColumnIndex(reference),
            ColumnReference = reference,
            HeaderText = display
        };
    }

    private static int ColumnIndex(string reference)
    {
        var result = 0;
        foreach (var character in reference.ToUpperInvariant())
        {
            if (character is < 'A' or > 'Z')
                return 0;
            result = checked(result * 26 + character - 'A' + 1);
        }
        return result;
    }
}
