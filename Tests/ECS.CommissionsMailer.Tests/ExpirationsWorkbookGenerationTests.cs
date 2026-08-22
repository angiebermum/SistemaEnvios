using System.IO.Compression;
using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsWorkbookGenerationTests
{
    [Theory]
    [InlineData(21)]
    [InlineData(35)]
    public void PreservesOriginalSchemaFormattingAndPrivacyWithoutChangingSource(int columnCount)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "source.xlsx");
        var output = Path.Combine(directory.Path, "broker-a.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source, columnCount);
        var sourceHash = Sha(source);

        new ExpirationsStandardWorkbookGenerator().Generate(new ExpirationsStandardWorkbookGenerationRequest(
            source,
            output,
            ExpirationsGenerationTestWorkbook.ReportSheetName,
            7,
            [8, 10]), TestContext.Current.CancellationToken);

        Assert.Equal(sourceHash, Sha(source));
        using var document = SpreadsheetDocument.Open(output, false);
        var workbookPart = document.WorkbookPart!;
        var sheet = Assert.Single(workbookPart.Workbook!.Sheets!.Elements<Sheet>());
        Assert.Equal(ExpirationsGenerationTestWorkbook.ReportSheetName, sheet.Name!.Value);
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
        var worksheet = worksheetPart.Worksheet!;
        var rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToList();
        Assert.Equal(Enumerable.Range(1, 9).Select(value => (uint)value), rows.Select(row => row.RowIndex!.Value));
        Assert.Equal(columnCount, rows.Single(row => row.RowIndex!.Value == 8).Elements<Cell>().Count());
        Assert.Equal(columnCount, rows.Single(row => row.RowIndex!.Value == 9).Elements<Cell>().Count());
        Assert.Equal((uint)2, rows.Single(row => row.RowIndex!.Value == 8).Elements<Cell>().Last().StyleIndex!.Value);
        Assert.Equal((uint)1, rows.Single(row => row.RowIndex!.Value == 8).Elements<Cell>().ElementAt(2).StyleIndex!.Value);
        Assert.Equal("1234.50", rows.Single(row => row.RowIndex!.Value == 8).Elements<Cell>().ElementAt(2).CellValue!.InnerText);
        Assert.Equal((uint)3, rows.Single(row => row.RowIndex!.Value == 8).Elements<Cell>().ElementAt(3).StyleIndex!.Value);
        Assert.Equal("45292", rows.Single(row => row.RowIndex!.Value == 8).Elements<Cell>().ElementAt(3).CellValue!.InnerText);
        Assert.Equal(22D, rows.Single(row => row.RowIndex!.Value == 8).Height!.Value);
        Assert.Equal(24D, rows.Single(row => row.RowIndex!.Value == 9).Height!.Value);
        Assert.Equal(columnCount, worksheet.GetFirstChild<Columns>()!.Elements<Column>().Count());
        Assert.Equal(14D, worksheet.GetFirstChild<Columns>()!.Elements<Column>().Last().Width!.Value);
        Assert.Equal("Campo Nuevo 2027", CellText(workbookPart, rows[6].Elements<Cell>().ElementAt(columnCount - 1)));
        Assert.Equal("VALOR_DESCONOCIDO_A_2", CellText(workbookPart, rows[8].Elements<Cell>().ElementAt(columnCount - 1)));
        Assert.NotNull(rows[0].Descendants<CellFormula>().SingleOrDefault());
        Assert.Contains(worksheet.Descendants<MergeCell>(), merge => merge.Reference?.Value == "A1:C1");
        Assert.EndsWith("9", worksheet.GetFirstChild<AutoFilter>()!.Reference!.Value, StringComparison.Ordinal);
        Assert.EndsWith("9", worksheet.GetFirstChild<SheetDimension>()!.Reference!.Value, StringComparison.Ordinal);

        var packageText = ReadPackageText(output);
        Assert.Contains("SECRETO_UNICO_BROKER_A_123", packageText, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETO_UNICO_BROKER_B_456", packageText, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETO_OTRA_HOJA_789", packageText, StringComparison.Ordinal);
        var sharedItems = workbookPart.SharedStringTablePart!.SharedStringTable!.Elements<SharedStringItem>().ToList();
        Assert.Equal(
            worksheet.Descendants<Cell>()
                .Where(cell => cell.DataType?.Value == CellValues.SharedString)
                .Select(cell => int.Parse(cell.CellValue!.InnerText))
                .Distinct()
                .Count(),
            sharedItems.Count);

        var brokerBOutput = Path.Combine(directory.Path, "broker-b.xlsx");
        new ExpirationsStandardWorkbookGenerator().Generate(new ExpirationsStandardWorkbookGenerationRequest(
            source,
            brokerBOutput,
            ExpirationsGenerationTestWorkbook.ReportSheetName,
            7,
            [9]), TestContext.Current.CancellationToken);
        var brokerBPackageText = ReadPackageText(brokerBOutput);
        Assert.Contains("SECRETO_UNICO_BROKER_B_456", brokerBPackageText, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETO_UNICO_BROKER_A_123", brokerBPackageText, StringComparison.Ordinal);
        Assert.Equal(sourceHash, Sha(source));
    }

    [Fact]
    public void HeaderFormulaIsPreservedButSelectedDataFormulaBlocksGeneration()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "formula.xlsx");
        var output = Path.Combine(directory.Path, "blocked.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source, 21, formulaDataRow: 9);

        var exception = Assert.Throws<ExpirationsGenerationException>(() =>
            new ExpirationsStandardWorkbookGenerator().Generate(new ExpirationsStandardWorkbookGenerationRequest(
                source,
                output,
                ExpirationsGenerationTestWorkbook.ReportSheetName,
                7,
                [9]), TestContext.Current.CancellationToken));

        Assert.Contains("fórmulas en filas de datos", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
        using var document = SpreadsheetDocument.Open(source, false);
        var reportSheet = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>()
            .Single(sheet => sheet.Name!.Value == ExpirationsGenerationTestWorkbook.ReportSheetName);
        var row = ((WorksheetPart)document.WorkbookPart.GetPartById(reportSheet.Id!)).Worksheet!
            .GetFirstChild<SheetData>()!.Elements<Row>().Single(value => value.RowIndex!.Value == 9);
        Assert.NotNull(row.Descendants<CellFormula>().SingleOrDefault());
    }

    [Fact]
    public void RemapsSingleDataRowMergeAndBlocksAmbiguousMultiRowMerge()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "merge.xlsx");
        var output = Path.Combine(directory.Path, "merge-output.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source, 21, dataMerge: "B10:C10");

        new ExpirationsStandardWorkbookGenerator().Generate(new ExpirationsStandardWorkbookGenerationRequest(
            source, output, ExpirationsGenerationTestWorkbook.ReportSheetName, 7, [8, 10]),
            TestContext.Current.CancellationToken);

        using (var generated = SpreadsheetDocument.Open(output, false))
        {
            Assert.Contains(
                generated.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<MergeCell>(),
                merge => merge.Reference?.Value == "B9:C9");
        }

        var ambiguousSource = Path.Combine(directory.Path, "merge-ambiguous.xlsx");
        ExpirationsGenerationTestWorkbook.Create(ambiguousSource, 21, dataMerge: "B8:C9");
        Assert.Throws<ExpirationsGenerationException>(() =>
            new ExpirationsStandardWorkbookGenerator().Generate(new ExpirationsStandardWorkbookGenerationRequest(
                ambiguousSource,
                Path.Combine(directory.Path, "ambiguous-output.xlsx"),
                ExpirationsGenerationTestWorkbook.ReportSheetName,
                7,
                [8]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void UpdatesWorksheetAndTableFiltersToLastGeneratedRow()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "table.xlsx");
        var output = Path.Combine(directory.Path, "table-output.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source, 21, includeTable: true);

        new ExpirationsStandardWorkbookGenerator().Generate(new ExpirationsStandardWorkbookGenerationRequest(
            source, output, ExpirationsGenerationTestWorkbook.ReportSheetName, 7, [8, 10]),
            TestContext.Current.CancellationToken);

        using var document = SpreadsheetDocument.Open(output, false);
        var worksheetPart = document.WorkbookPart!.WorksheetParts.Single();
        Assert.Equal("A7:U9", worksheetPart.Worksheet!.GetFirstChild<AutoFilter>()!.Reference!.Value);
        var table = Assert.Single(worksheetPart.TableDefinitionParts).Table!;
        Assert.Equal("A7:U9", table.Reference!.Value);
        Assert.Equal("A7:U9", table.AutoFilter!.Reference!.Value);
    }

    [Fact]
    public void SourceCanRemainLockedReadOnlyForTheWholeGeneration()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "read-only-source.xlsx");
        var output = Path.Combine(directory.Path, "read-only-output.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source, 21);
        using var sourceLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);

        new ExpirationsStandardWorkbookGenerator().Generate(new ExpirationsStandardWorkbookGenerationRequest(
            source, output, ExpirationsGenerationTestWorkbook.ReportSheetName, 7, [8]),
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(output));
        Assert.True(sourceLock.CanRead);
        Assert.False(sourceLock.CanWrite);
    }

    [Fact]
    public void UnsupportedHyperlinkBlocksInsteadOfRetainingHiddenBrokerData()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "hyperlink.xlsx");
        var output = Path.Combine(directory.Path, "hyperlink-output.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source, 21);
        using (var document = SpreadsheetDocument.Open(source, true))
        {
            var sheet = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>()
                .Single(value => value.Name!.Value == ExpirationsGenerationTestWorkbook.ReportSheetName);
            var worksheet = ((WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!)).Worksheet!;
            worksheet.Append(new Hyperlinks(new Hyperlink { Reference = "A8", Location = "A9" }));
            worksheet.Save();
        }

        var exception = Assert.Throws<ExpirationsGenerationException>(() =>
            new ExpirationsStandardWorkbookGenerator().Generate(new ExpirationsStandardWorkbookGenerationRequest(
                source, output, ExpirationsGenerationTestWorkbook.ReportSheetName, 7, [8]),
                TestContext.Current.CancellationToken));

        Assert.Contains("comentarios o hipervínculos", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TenAuthoritativeSourceRowsProduceExactlyTenCompactRecords()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "ten-rows.xlsx");
        var output = Path.Combine(directory.Path, "ten-rows-output.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source, 21, extraDataRows: 7);

        new ExpirationsStandardWorkbookGenerator().Generate(new ExpirationsStandardWorkbookGenerationRequest(
            source,
            output,
            ExpirationsGenerationTestWorkbook.ReportSheetName,
            7,
            Enumerable.Range(8, 10).Select(value => (uint)value).ToArray()),
            TestContext.Current.CancellationToken);

        using var document = SpreadsheetDocument.Open(output, false);
        var rows = document.WorkbookPart!.WorksheetParts.Single().Worksheet!
            .GetFirstChild<SheetData>()!.Elements<Row>();
        Assert.Equal(10, rows.Count(row => row.RowIndex!.Value > 7));
        Assert.Equal(Enumerable.Range(8, 10).Select(value => (uint)value),
            rows.Where(row => row.RowIndex!.Value > 7).Select(row => row.RowIndex!.Value));
    }

    private static string CellText(WorkbookPart workbookPart, Cell cell)
    {
        if (cell.DataType?.Value == CellValues.SharedString)
            return workbookPart.SharedStringTablePart!.SharedStringTable!
                .Elements<SharedStringItem>().ElementAt(int.Parse(cell.CellValue!.InnerText)).InnerText;
        return cell.InlineString?.InnerText ?? cell.CellValue?.InnerText ?? string.Empty;
    }

    private static string Sha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string ReadPackageText(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return string.Join("\n", archive.Entries.Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(entry =>
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }));
    }
}

internal static class ExpirationsGenerationTestWorkbook
{
    public const string ReportSheetName = "Reporte no estándar 2027";

    public static void Create(
        string path,
        int columnCount = 35,
        uint? formulaDataRow = null,
        string? dataMerge = null,
        bool includeTable = false,
        int extraDataRows = 0)
    {
        if (extraDataRows < 0)
            throw new ArgumentOutOfRangeException(nameof(extraDataRows));
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new BookViews(new WorkbookView()), new Sheets());
        AddStyles(workbookPart);
        var sharedPart = workbookPart.AddNewPart<SharedStringTablePart>();
        sharedPart.SharedStringTable = new SharedStringTable();
        var shared = new Dictionary<string, int>(StringComparer.Ordinal);
        int Shared(string value)
        {
            if (shared.TryGetValue(value, out var existing)) return existing;
            var index = shared.Count;
            shared[value] = index;
            sharedPart.SharedStringTable.Append(new SharedStringItem(new Text(value)
            {
                Space = SpaceProcessingModeValues.Preserve
            }));
            return index;
        }

        var reportPart = workbookPart.AddNewPart<WorksheetPart>();
        var columns = new Columns();
        for (uint index = 1; index <= columnCount; index++)
        {
            columns.Append(new Column
            {
                Min = index,
                Max = index,
                Width = index == columnCount ? 14D : 11D,
                CustomWidth = true,
                Hidden = index == 3
            });
        }
        var sheetData = new SheetData();
        for (uint rowNumber = 1; rowNumber <= 7; rowNumber++)
        {
            var row = new Row { RowIndex = rowNumber, Height = 18D, CustomHeight = true };
            if (rowNumber == 1)
            {
                row.Append(new Cell
                {
                    CellReference = "A1",
                    DataType = CellValues.Number,
                    CellFormula = new CellFormula("1+1"),
                    CellValue = new CellValue("2"),
                    StyleIndex = 2U
                });
            }
            else if (rowNumber == 7)
            {
                for (var column = 1; column <= columnCount; column++)
                {
                    var text = column == columnCount ? "Campo Nuevo 2027" : $"Columna {column}";
                    row.Append(SharedCell(column, rowNumber, Shared(text), 2U));
                }
            }
            else
            {
                row.Append(InlineCell(1, rowNumber, $"Título {rowNumber}", 2U));
            }
            sheetData.Append(row);
        }

        AddDataRow(sheetData, 8, columnCount, "Broker A", "SECRETO_UNICO_BROKER_A_123", "VALOR_DESCONOCIDO_A_1", 22D, Shared, formulaDataRow == 8);
        AddDataRow(sheetData, 9, columnCount, "Broker B", "SECRETO_UNICO_BROKER_B_456", "VALOR_DESCONOCIDO_B", 23D, Shared, formulaDataRow == 9);
        AddDataRow(sheetData, 10, columnCount, "Broker A", "SECRETO_UNICO_BROKER_A_123", "VALOR_DESCONOCIDO_A_2", 24D, Shared, formulaDataRow == 10);
        for (uint rowNumber = 11; rowNumber <= 10U + (uint)extraDataRows; rowNumber++)
        {
            AddDataRow(
                sheetData,
                rowNumber,
                columnCount,
                "Broker A",
                $"SECRETO_A_{rowNumber}",
                $"VALOR_A_{rowNumber}",
                24D,
                Shared,
                formulaDataRow == rowNumber);
        }
        var lastSourceRow = 10 + extraDataRows;
        var lastColumn = ColumnName(columnCount);
        var worksheet = new Worksheet(
            new SheetDimension { Reference = $"A1:{lastColumn}{lastSourceRow}" },
            columns,
            sheetData,
            new AutoFilter { Reference = $"A7:{lastColumn}{lastSourceRow}" },
            new MergeCells(new MergeCell { Reference = "A1:C1" }));
        if (dataMerge is not null)
            worksheet.GetFirstChild<MergeCells>()!.Append(new MergeCell { Reference = dataMerge });
        reportPart.Worksheet = worksheet;

        if (includeTable)
        {
            var tablePart = reportPart.AddNewPart<TableDefinitionPart>();
            var tableColumns = new TableColumns { Count = (uint)columnCount };
            for (uint index = 1; index <= columnCount; index++)
                tableColumns.Append(new TableColumn { Id = index, Name = index == columnCount ? "Campo Nuevo 2027" : $"Columna {index}" });
            tablePart.Table = new Table
            {
                Id = 1U,
                Name = "ReporteTabla",
                DisplayName = "ReporteTabla",
                Reference = $"A7:{lastColumn}{lastSourceRow}",
                TotalsRowShown = false
            };
            tablePart.Table.Append(new AutoFilter { Reference = $"A7:{lastColumn}{lastSourceRow}" }, tableColumns);
            tablePart.Table.Save();
            reportPart.Worksheet.Append(new TableParts(
                new TablePart { Id = reportPart.GetIdOfPart(tablePart) }) { Count = 1U });
        }
        reportPart.Worksheet.Save();

        var otherPart = workbookPart.AddNewPart<WorksheetPart>();
        otherPart.Worksheet = new Worksheet(new SheetData(new Row(
            SharedCell(1, 1, Shared("SECRETO_OTRA_HOJA_789"), 0U)) { RowIndex = 1U }));
        otherPart.Worksheet.Save();
        workbookPart.Workbook.Sheets!.Append(
            new Sheet { Id = workbookPart.GetIdOfPart(reportPart), SheetId = 1U, Name = ReportSheetName },
            new Sheet { Id = workbookPart.GetIdOfPart(otherPart), SheetId = 2U, Name = "Otra hoja privada" });
        sharedPart.SharedStringTable.Count = (uint)shared.Count;
        sharedPart.SharedStringTable.UniqueCount = (uint)shared.Count;
        sharedPart.SharedStringTable.Save();
        workbookPart.Workbook.Save();
    }

    public static ExpirationsGenerationContext Context(
        string sourcePath,
        ExpirationsProcess process,
        IReadOnlyList<ExpirationsBrokerCatalogItem> brokers,
        IReadOnlyDictionary<Guid, IReadOnlyList<uint>> rowsByBroker)
    {
        var allRows = rowsByBroker.Values.SelectMany(value => value).Distinct().Order().Select(row => new ExpirationsSourceRow
        {
            RowNumber = row,
            RawBrokerValue = "resuelto"
        }).ToList();
        return new ExpirationsGenerationContext(
            process,
            sourcePath,
            new ECS.CommissionsMailer.Services.GeneratedFileHashService().ComputeSha256(sourcePath),
            new ExpirationsSourceWorkbook
            {
                SourcePath = sourcePath,
                WorksheetName = ReportSheetName,
                HeaderRowNumber = 7,
                BrokerColumnIndex = 1,
                Rows = allRows
            },
            new ExpirationsWorkbookAnalysisResult
            {
                ReadStatus = ExpirationsWorkbookReadStatus.Success,
                TotalRows = allRows.Count,
                ResolvedRows = allRows.Count,
                CanGenerate = true,
                ResolvedRowNumbersByBroker = rowsByBroker
            },
            brokers);
    }

    private static void AddDataRow(
        SheetData sheetData,
        uint rowNumber,
        int columnCount,
        string broker,
        string secret,
        string lastValue,
        double height,
        Func<string, int> shared,
        bool formula)
    {
        var row = new Row { RowIndex = rowNumber, Height = height, CustomHeight = true };
        for (var column = 1; column <= columnCount; column++)
        {
            var value = column switch
            {
                1 => broker,
                2 => secret,
                _ when column == columnCount => lastValue,
                _ => $"Dato {rowNumber}-{column}"
            };
            var cell = SharedCell(column, rowNumber, shared(value), column == columnCount ? 2U : 1U);
            if (column == 3)
            {
                cell.DataType = CellValues.Number;
                cell.CellValue = new CellValue("1234.50");
                cell.StyleIndex = 1U;
            }
            else if (column == 4)
            {
                cell.DataType = CellValues.Number;
                cell.CellValue = new CellValue("45292");
                cell.StyleIndex = 3U;
            }
            if (formula && column == 3)
            {
                cell.DataType = CellValues.Number;
                cell.CellFormula = new CellFormula("1+1");
                cell.CellValue = new CellValue("2");
            }
            row.Append(cell);
        }
        sheetData.Append(row);
    }

    private static Cell SharedCell(int column, uint row, int sharedIndex, uint style) => new()
    {
        CellReference = $"{ColumnName(column)}{row}",
        DataType = CellValues.SharedString,
        CellValue = new CellValue(sharedIndex.ToString()),
        StyleIndex = style
    };

    private static Cell InlineCell(int column, uint row, string value, uint style) => new()
    {
        CellReference = $"{ColumnName(column)}{row}",
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value)),
        StyleIndex = style
    };

    private static void AddStyles(WorkbookPart workbookPart)
    {
        var styles = workbookPart.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = new Stylesheet(
            new Fonts(
                new Font(),
                new Font(new Bold(), new Color { Rgb = "FFFFFFFF" })) { Count = 2U },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FF1F4E78" }) { PatternType = PatternValues.Solid })) { Count = 3U },
            new Borders(
                new Border(),
                new Border(new LeftBorder { Style = BorderStyleValues.Thin }, new RightBorder { Style = BorderStyleValues.Thin })) { Count = 2U },
            new CellFormats(
                new CellFormat(),
                new CellFormat { NumberFormatId = 4U, ApplyNumberFormat = true },
                new CellFormat { FontId = 1U, FillId = 2U, BorderId = 1U, ApplyFill = true, ApplyBorder = true },
                new CellFormat { NumberFormatId = 14U, ApplyNumberFormat = true }) { Count = 4U });
        styles.Stylesheet.Save();
    }

    private static string ColumnName(int index)
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
}

internal sealed class ExpirationsGenerationTestDirectory : IDisposable
{
    public ExpirationsGenerationTestDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"ECS-expirations-generation-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, true);
    }
}
