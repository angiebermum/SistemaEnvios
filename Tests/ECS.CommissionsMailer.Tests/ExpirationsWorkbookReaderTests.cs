using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsWorkbookReaderTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 9)]
    [InlineData(3, 7)]
    [InlineData(2, 27)]
    public void ReadsBrokerColumnWithoutFixedPositionOrderOrColumnCount(
        int brokerColumnIndex,
        int totalColumns)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "layout.xlsx");
        var headers = Enumerable.Range(1, totalColumns)
            .Select(column => Cell(column, column == brokerColumnIndex ? "Corredor" : $"Campo {column}"))
            .ToList();
        var values = Enumerable.Range(1, totalColumns)
            .Select(column => Cell(column, column == brokerColumnIndex ? "Broker Uno" : $"Valor {column}"))
            .ToList();
        CreateWorkbook(path, Sheet("Reporte", Row(3, headers), Row(4, values)));

        var result = new ExpirationsWorkbookReader().Read(path);

        Assert.True(result.IsSuccess);
        Assert.Equal((uint)3, result.Workbook!.HeaderRowNumber);
        Assert.Equal(brokerColumnIndex, result.Workbook.BrokerColumnIndex);
        var sourceRow = Assert.Single(result.Workbook.Rows);
        Assert.Equal((uint)4, sourceRow.RowNumber);
        Assert.Equal("Broker Uno", sourceRow.RawBrokerValue);
        Assert.Equal(totalColumns, sourceRow.Cells.Count);
    }

    [Theory]
    [InlineData("CORREDOR")]
    [InlineData(" corredor ")]
    [InlineData("Corredór")]
    [InlineData("Corredor.")]
    public void RecognizesConservativeHeaderVariants(string header)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "header.xlsx");
        CreateWorkbook(
            path,
            Sheet("Datos", Row(8, Cell(4, header)), Row(9, Cell(4, "Broker Uno"))));

        var result = new ExpirationsWorkbookReader().Read(path);

        Assert.True(result.IsSuccess);
        Assert.Equal((uint)8, result.Workbook!.HeaderRowNumber);
        Assert.Equal(4, result.Workbook.BrokerColumnIndex);
    }

    [Fact]
    public void MissingBrokerHeaderReturnsHeaderNotFound()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "missing.xlsx");
        CreateWorkbook(path, Sheet("Datos", Row(1, Cell(1, "Intermediario"))));

        var result = new ExpirationsWorkbookReader().Read(path);

        Assert.Equal(ExpirationsWorkbookReadStatus.HeaderNotFound, result.Status);
        Assert.Null(result.Workbook);
    }

    [Fact]
    public void TwoBrokerColumnsRequireManualSelection()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "columns.xlsx");
        CreateWorkbook(
            path,
            Sheet("Datos", Row(1, Cell(2, "Corredor"), Cell(6, "CORREDÓR"))));

        var result = new ExpirationsWorkbookReader().Read(path);

        Assert.Equal(ExpirationsWorkbookReadStatus.RequiresManualSelection, result.Status);
        Assert.Equal([2, 6], result.Candidates.Select(candidate => candidate.BrokerColumnIndex));
    }

    [Fact]
    public void MultipleCompatibleWorksheetsRequireManualSelection()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "sheets.xlsx");
        CreateWorkbook(
            path,
            Sheet("Uno", Row(1, Cell(1, "Corredor"))),
            Sheet("Dos", Row(4, Cell(7, "Corredor"))));

        var result = new ExpirationsWorkbookReader().Read(path);

        Assert.Equal(ExpirationsWorkbookReadStatus.RequiresManualSelection, result.Status);
        Assert.Equal(["Uno", "Dos"], result.Candidates.Select(candidate => candidate.WorksheetName));
    }

    [Fact]
    public void ExplicitWorksheetHeaderAndColumnOverrideWorksWithoutSemanticGuessing()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "override.xlsx");
        CreateWorkbook(
            path,
            Sheet("Ignorar", Row(1, Cell(1, "Corredor"))),
            Sheet(
                "Elegida",
                Row(4, Cell(1, "Póliza"), Cell(3, "Intermediario")),
                Row(5, Cell(1, "P-1"), Cell(3, "Broker Manual"))));

        var result = new ExpirationsWorkbookReader().Read(
            path,
            new ExpirationsWorkbookReadOptions
            {
                WorksheetName = "Elegida",
                HeaderRowNumber = 4,
                BrokerColumnIndex = 3
            });

        Assert.True(result.IsSuccess);
        Assert.Equal("Elegida", result.Workbook!.WorksheetName);
        Assert.Equal("Broker Manual", Assert.Single(result.Workbook.Rows).RawBrokerValue);
    }

    [Fact]
    public void KeepsDataRowsWithEmptyBrokerAndDropsCompletelyEmptyRows()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "empty-rows.xlsx");
        CreateWorkbook(
            path,
            Sheet(
                "Datos",
                Row(1, Cell(1, "Corredor"), Cell(2, "Póliza")),
                Row(2, Cell(1, string.Empty), Cell(2, "P-1")),
                Row(3, Cell(1, string.Empty), Cell(2, string.Empty)),
                Row(4, Cell(2, "P-2"))));

        var result = new ExpirationsWorkbookReader().Read(path);

        Assert.True(result.IsSuccess);
        Assert.Equal([(uint)2, (uint)4], result.Workbook!.Rows.Select(row => row.RowNumber));
        Assert.All(result.Workbook.Rows, row => Assert.Equal(string.Empty, row.RawBrokerValue));
    }

    [Fact]
    public void ReadsSharedInlineStringNumericAndFormulaCachedValuesWithoutChangingFile()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "values.xlsx");
        CreateWorkbook(
            path,
            Sheet(
                "Datos",
                Row(1, Cell(1, "Corredor", CellKind.Shared)),
                Row(
                    2,
                    Cell(1, "Broker Inline", CellKind.Inline),
                    Cell(2, "Texto compartido", CellKind.Shared),
                    Cell(3, "Texto String", CellKind.String),
                    Cell(4, "123.45", CellKind.Number),
                    new TestCell(5, "4", CellKind.Formula, "2+2"))));
        var before = ComputeSha256(path);

        var result = new ExpirationsWorkbookReader().Read(path);
        var after = ComputeSha256(path);

        Assert.True(result.IsSuccess);
        var cells = Assert.Single(result.Workbook!.Rows).Cells.ToDictionary(cell => cell.ColumnIndex);
        Assert.Equal("Broker Inline", cells[1].DisplayText);
        Assert.Equal("Texto compartido", cells[2].DisplayText);
        Assert.Equal("Texto String", cells[3].DisplayText);
        Assert.Equal("123.45", cells[4].DisplayText);
        Assert.Equal("4", cells[5].DisplayText);
        Assert.Equal(before, after);
    }

    [Fact]
    public void XlsExtensionIsRejectedWithoutOpeningAsXlsx()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "legacy.xls");
        File.WriteAllText(path, "not an xlsx");

        var result = new ExpirationsWorkbookReader().Read(path);

        Assert.Equal(ExpirationsWorkbookReadStatus.InvalidFile, result.Status);
        Assert.Contains(result.Messages, message => message.Contains(".xlsx", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadsAndResolvesFiveThousandTwoHundredFiftyRows()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "scale.xlsx");
        var rows = new List<TestRow>
        {
            Row(1, Cell(1, "Póliza"), Cell(7, "Corredor"), Cell(11, "Campo extra"))
        };
        for (uint rowNumber = 2; rowNumber <= 5_251; rowNumber++)
        {
            rows.Add(Row(
                rowNumber,
                Cell(1, $"P-{rowNumber - 1}"),
                Cell(7, "Scale Broker"),
                Cell(11, "Dato")));
        }
        CreateWorkbook(path, new TestSheet("Reporte", rows));
        var read = new ExpirationsWorkbookReader().Read(path);

        var analysis = new ExpirationsWorkbookAnalysisService().Analyze(
            read,
            [Broker(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Scale Broker")],
            []);

        Assert.True(read.IsSuccess);
        Assert.Equal(5_250, read.Workbook!.Rows.Count);
        Assert.Equal((uint)5_251, read.Workbook.Rows[^1].RowNumber);
        Assert.Equal(5_250, analysis.ResolvedRows);
        Assert.True(analysis.CanGenerate);
    }

    private static ExpirationsBrokerCatalogItem Broker(Guid id, string name) => new()
    {
        BrokerId = id,
        Name = name,
        IsActive = true
    };

    private static TestSheet Sheet(string name, params TestRow[] rows) => new(name, rows);
    private static TestRow Row(uint number, params TestCell[] cells) => new(number, cells);
    private static TestRow Row(uint number, IEnumerable<TestCell> cells) => new(number, cells.ToList());
    private static TestCell Cell(int column, string value, CellKind kind = CellKind.Inline) =>
        new(column, value, kind);

    private static void CreateWorkbook(string path, params TestSheet[] sheets)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        SharedStringTablePart? sharedPart = null;
        var sharedValues = new Dictionary<string, int>(StringComparer.Ordinal);

        int SharedIndex(string value)
        {
            if (sharedValues.TryGetValue(value, out var existing))
                return existing;
            sharedPart ??= workbookPart.AddNewPart<SharedStringTablePart>();
            sharedPart.SharedStringTable ??= new SharedStringTable();
            var index = sharedValues.Count;
            sharedPart.SharedStringTable.AppendChild(new SharedStringItem(PreservedText(value)));
            sharedValues[value] = index;
            return index;
        }

        uint sheetId = 1;
        foreach (var sheet in sheets)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            foreach (var rowDefinition in sheet.Rows.OrderBy(row => row.Number))
            {
                var row = new Row { RowIndex = rowDefinition.Number };
                foreach (var cellDefinition in rowDefinition.Cells.OrderBy(cell => cell.Column))
                {
                    var cell = new Cell
                    {
                        CellReference = $"{ColumnReference(cellDefinition.Column)}{rowDefinition.Number}"
                    };
                    switch (cellDefinition.Kind)
                    {
                        case CellKind.Inline:
                            cell.DataType = CellValues.InlineString;
                            cell.InlineString = new InlineString(PreservedText(cellDefinition.Value));
                            break;
                        case CellKind.Shared:
                            cell.DataType = CellValues.SharedString;
                            cell.CellValue = new CellValue(SharedIndex(cellDefinition.Value).ToString());
                            break;
                        case CellKind.String:
                            cell.DataType = CellValues.String;
                            cell.CellValue = new CellValue(cellDefinition.Value);
                            break;
                        case CellKind.Number:
                            cell.DataType = CellValues.Number;
                            cell.CellValue = new CellValue(cellDefinition.Value);
                            break;
                        case CellKind.Formula:
                            cell.DataType = CellValues.Number;
                            cell.CellFormula = new CellFormula(cellDefinition.Formula ?? string.Empty);
                            cell.CellValue = new CellValue(cellDefinition.Value);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                    row.Append(cell);
                }
                sheetData.Append(row);
            }

            worksheetPart.Worksheet = new Worksheet(sheetData);
            worksheetPart.Worksheet.Save();
            workbookPart.Workbook.Sheets!.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = sheetId++,
                Name = sheet.Name
            });
        }

        sharedPart?.SharedStringTable?.Save();
        workbookPart.Workbook.Save();
    }

    private static Text PreservedText(string value) => new(value)
    {
        Space = SpaceProcessingModeValues.Preserve
    };

    private static string ColumnReference(int index)
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

    private static string ComputeSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private enum CellKind
    {
        Inline,
        Shared,
        String,
        Number,
        Formula
    }

    private sealed record TestCell(int Column, string Value, CellKind Kind, string? Formula = null);
    private sealed record TestRow(uint Number, IReadOnlyList<TestCell> Cells);
    private sealed record TestSheet(string Name, IReadOnlyList<TestRow> Rows);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ECS-expirations-reader-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
