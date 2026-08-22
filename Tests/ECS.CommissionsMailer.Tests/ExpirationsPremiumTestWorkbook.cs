using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ECS.CommissionsMailer.Tests;

public enum PremiumTestStorage
{
    Number,
    SharedString,
    InlineString,
    Missing,
    Formula
}

internal sealed record PremiumTestRow(
    uint RowNumber,
    string PremiumValue,
    string Currency,
    string Secret,
    PremiumTestStorage PremiumStorage = PremiumTestStorage.Number,
    string BrokerValue = "Broker");

internal static class ExpirationsPremiumTestWorkbook
{
    public static void Create(
        string path,
        string worksheetName,
        uint headerRowNumber,
        IReadOnlyList<string> headers,
        int premiumColumnIndex,
        int currencyColumnIndex,
        IReadOnlyList<PremiumTestRow>? rows = null,
        bool includePrivateWorksheet = true)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        var sharedPart = workbookPart.AddNewPart<SharedStringTablePart>();
        sharedPart.SharedStringTable = new SharedStringTable();
        var shared = new Dictionary<string, int>(StringComparer.Ordinal);

        int Shared(string value)
        {
            if (shared.TryGetValue(value, out var existing))
                return existing;
            var index = shared.Count;
            shared.Add(value, index);
            sharedPart.SharedStringTable.Append(new SharedStringItem(
                new Text(value) { Space = SpaceProcessingModeValues.Preserve }));
            return index;
        }

        var reportPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        var header = new Row { RowIndex = headerRowNumber };
        for (var index = 0; index < headers.Count; index++)
            header.Append(SharedCell(index + 1, headerRowNumber, Shared(headers[index])));
        sheetData.Append(header);

        foreach (var item in rows ?? [])
        {
            var cells = new List<(int Column, Cell Cell)>
            {
                (1, SharedCell(1, item.RowNumber, Shared(item.BrokerValue)))
            };
            if (item.PremiumStorage != PremiumTestStorage.Missing)
            {
                cells.Add((premiumColumnIndex, PremiumCell(
                    premiumColumnIndex,
                    item.RowNumber,
                    item.PremiumValue,
                    item.PremiumStorage,
                    Shared)));
            }
            cells.Add((currencyColumnIndex, SharedCell(
                currencyColumnIndex,
                item.RowNumber,
                Shared(item.Currency))));
            var secretColumn = headers.Count;
            if (secretColumn == premiumColumnIndex || secretColumn == currencyColumnIndex)
                secretColumn = Math.Max(premiumColumnIndex, currencyColumnIndex) + 1;
            cells.Add((secretColumn, SharedCell(secretColumn, item.RowNumber, Shared(item.Secret))));
            var row = new Row { RowIndex = item.RowNumber };
            foreach (var cell in cells
                .GroupBy(value => value.Column)
                .Select(group => group.Last())
                .OrderBy(value => value.Column))
            {
                row.Append(cell.Cell);
            }
            sheetData.Append(row);
        }
        reportPart.Worksheet = new Worksheet(sheetData);
        reportPart.Worksheet.Save();
        workbookPart.Workbook.Sheets!.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(reportPart),
            SheetId = 1U,
            Name = worksheetName
        });

        if (includePrivateWorksheet)
        {
            var privatePart = workbookPart.AddNewPart<WorksheetPart>();
            privatePart.Worksheet = new Worksheet(new SheetData(new Row(
                SharedCell(1, 1, Shared("SECRETO_OTRA_HOJA_FASE8"))) { RowIndex = 1U }));
            privatePart.Worksheet.Save();
            workbookPart.Workbook.Sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(privatePart),
                SheetId = 2U,
                Name = "Privada"
            });
        }

        sharedPart.SharedStringTable.Count = (uint)shared.Count;
        sharedPart.SharedStringTable.UniqueCount = (uint)shared.Count;
        sharedPart.SharedStringTable.Save();
        workbookPart.Workbook.Save();
    }

    public static IReadOnlyList<string> Headers(int count, int premiumIndex, int currencyIndex)
    {
        var result = Enumerable.Range(1, count).Select(index => $"Campo {index}").ToArray();
        result[premiumIndex - 1] = "Prima";
        result[currencyIndex - 1] = "Moneda";
        return result;
    }

    public static string ColumnName(int index)
    {
        var result = string.Empty;
        while (index > 0)
        {
            index--;
            result = (char)('A' + index % 26) + result;
            index /= 26;
        }
        return result;
    }

    private static Cell PremiumCell(
        int column,
        uint row,
        string value,
        PremiumTestStorage storage,
        Func<string, int> shared) => storage switch
    {
        PremiumTestStorage.Number => new Cell
        {
            CellReference = $"{ColumnName(column)}{row}",
            DataType = CellValues.Number,
            CellValue = new CellValue(value)
        },
        PremiumTestStorage.SharedString => SharedCell(column, row, shared(value)),
        PremiumTestStorage.InlineString => new Cell
        {
            CellReference = $"{ColumnName(column)}{row}",
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value))
        },
        PremiumTestStorage.Formula => new Cell
        {
            CellReference = $"{ColumnName(column)}{row}",
            DataType = CellValues.Number,
            CellFormula = new CellFormula("50+50"),
            CellValue = new CellValue(value)
        },
        _ => throw new ArgumentOutOfRangeException(nameof(storage))
    };

    private static Cell SharedCell(int column, uint row, int sharedIndex) => new()
    {
        CellReference = $"{ColumnName(column)}{row}",
        DataType = CellValues.SharedString,
        CellValue = new CellValue(sharedIndex.ToString())
    };
}
