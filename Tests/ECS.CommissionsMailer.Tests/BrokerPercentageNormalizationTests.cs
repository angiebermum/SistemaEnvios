using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class BrokerPercentageNormalizationTests
{
    [Theory]
    [InlineData("80", true, false, "80")]
    [InlineData("80.00", true, false, "80")]
    [InlineData("80", false, false, "80")]
    [InlineData("80.00", false, false, "80")]
    [InlineData("80,00", false, false, "80")]
    [InlineData("80%", false, false, "80")]
    [InlineData("80.00%", false, false, "80")]
    [InlineData("  80,00 %  ", false, false, "80")]
    [InlineData("0.80", true, true, "80")]
    [InlineData("0.20", true, true, "20")]
    [InlineData("0.80", true, false, "0.80")]
    [InlineData("0.80%", false, false, "0.80")]
    [InlineData("0", true, false, "0")]
    [InlineData("0.5", true, false, "0.5")]
    [InlineData("100", true, false, "100")]
    public void NormalizeBrokerPercentageUsesZeroToOneHundredScale(
        string rawValue,
        bool isNumericValue,
        bool hasPercentageNumberFormat,
        string expected)
    {
        var normalized = BrokerPercentageNormalizer.NormalizeBrokerPercentage(
            rawValue,
            isNumericValue,
            hasPercentageNumberFormat);

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), normalized);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("100.01")]
    [InlineData("texto")]
    [InlineData("80..0")]
    [InlineData("#VALUE!")]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeBrokerPercentageRejectsInvalidValues(string rawValue)
    {
        Assert.ThrowsAny<Exception>(() =>
            BrokerPercentageNormalizer.NormalizeBrokerPercentage(rawValue, false, false));
    }

    [Fact]
    public void GeneratedWorkbookNormalizesDynamicPercentageColumnsAndDerivedCalculations()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general-porcentajes.xlsx");
        CreatePercentageWorkbook(source, "VB", invalidPercentage: null);
        var sourceHash = new GeneratedFileHashService().ComputeSha256(source);
        var sourceDetailXml = ReadWorksheetXml(source, "VB");

        var batch = Generate(scope, source, "VB");

        var output = Assert.Single(batch.Files).OutputPath;
        Assert.Equal(sourceHash, new GeneratedFileHashService().ComputeSha256(source));
        Assert.Equal(sourceDetailXml, ReadWorksheetXml(source, "VB"));
        using var document = SpreadsheetDocument.Open(output, false);
        var workbookPart = document.WorkbookPart!;
        var detail = GetWorksheetPart(document, "Detalle");
        var cells = detail.Worksheet!.Descendants<Cell>().ToDictionary(
            cell => cell.CellReference!.Value!,
            StringComparer.Ordinal);

        AssertNormalizedPercentage(workbookPart, cells["F2"], 80m);
        AssertNormalizedPercentage(workbookPart, cells["F3"], 80m);
        AssertNormalizedPercentage(workbookPart, cells["F4"], 20m);
        AssertNormalizedPercentage(workbookPart, cells["F12"], 50m);
        Assert.Equal("C2*F2/100", cells["H2"].CellFormula!.Text);
        Assert.Equal("C3*F3/100", cells["H3"].CellFormula!.Text);
        Assert.Equal("C4*F4/100", cells["H4"].CellFormula!.Text);
        Assert.Equal("C12*F12/100", cells["H12"].CellFormula!.Text);
        Assert.Equal("H2*2%", cells["J2"].CellFormula!.Text);
        Assert.Equal("H3*2%", cells["J3"].CellFormula!.Text);
        Assert.Equal("H4*2%", cells["J4"].CellFormula!.Text);
        Assert.Equal("H12*2%", cells["J12"].CellFormula!.Text);
        Assert.Equal("H2-J2", cells["L2"].CellFormula!.Text);
        Assert.Equal("H3-J3", cells["L3"].CellFormula!.Text);
        Assert.Equal("H4-J4", cells["L4"].CellFormula!.Text);
        Assert.Equal("H12-J12", cells["L12"].CellFormula!.Text);
        Assert.Equal(800m, ReadDecimal(cells["H2"]));
        Assert.Equal(800m, ReadDecimal(cells["H3"]));
        Assert.Equal(200m, ReadDecimal(cells["H4"]));
        Assert.Equal(500m, ReadDecimal(cells["H12"]));
        Assert.Equal(16m, ReadDecimal(cells["J2"]));
        Assert.Equal(16m, ReadDecimal(cells["J3"]));
        Assert.Equal(4m, ReadDecimal(cells["J4"]));
        Assert.Equal(10m, ReadDecimal(cells["J12"]));
        Assert.Equal(784m, ReadDecimal(cells["L2"]));
        Assert.Equal(784m, ReadDecimal(cells["L3"]));
        Assert.Equal(196m, ReadDecimal(cells["L4"]));
        Assert.Equal(490m, ReadDecimal(cells["L12"]));

        Assert.Null(cells["F5"].CellValue);
        Assert.Null(cells["F5"].InlineString);
        Assert.Equal(2U, cells["F5"].StyleIndex!.Value);
        Assert.Equal("   ", cells["F6"].InlineString!.InnerText);
        Assert.Equal(CellValues.InlineString, cells["F10"].DataType!.Value);
        Assert.Equal("% Corredor", cells["F10"].InlineString!.InnerText);
        Assert.Equal(CellValues.InlineString, cells["F11"].DataType!.Value);
        Assert.Equal(" %   Corredor ", cells["F11"].InlineString!.InnerText);
        Assert.NotNull(workbookPart.Workbook!.CalculationProperties);
        Assert.True(workbookPart.Workbook.CalculationProperties!.ForceFullCalculation!.Value);
        Assert.True(workbookPart.Workbook.CalculationProperties.FullCalculationOnLoad!.Value);
        Assert.Equal(
            ["Detalle", "Monto de factura"],
            workbookPart.Workbook.Sheets!.Elements<Sheet>().Select(sheet => sheet.Name!.Value).ToList());
    }

    [Theory]
    [InlineData("80..0", "80..0")]
    [InlineData("texto", "texto")]
    public void InvalidPercentageStopsOnlyThatFileWithWorksheetRowAndValue(
        string invalidPercentage,
        string expectedDisplayValue)
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general-invalido.xlsx");
        CreatePercentageWorkbook(source, "VB", invalidPercentage, headerRow: 10U, detailRow: 12U);

        var exception = Assert.Throws<InvalidDataException>(() => Generate(scope, source, "VB"));

        Assert.Contains("VB", exception.Message, StringComparison.Ordinal);
        Assert.Contains("F12", exception.Message, StringComparison.Ordinal);
        Assert.Contains("fila 12", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedDisplayValue, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Corrija el Excel general", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(scope.Path, "salida")));
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPercentageCellInCommissionRowIsIgnoredAndLeftUnchanged(string emptyPercentage)
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general-porcentaje-vacio.xlsx");
        CreatePercentageWorkbook(source, "AAV", emptyPercentage, headerRow: 10U, detailRow: 12U);

        var batch = Generate(scope, source, "AAV");

        var output = Assert.Single(batch.Files).OutputPath;
        using var document = SpreadsheetDocument.Open(output, false);
        var detail = GetWorksheetPart(document, "Detalle");
        var cells = detail.Worksheet!.Descendants<Cell>().ToDictionary(
            cell => cell.CellReference!.Value!,
            StringComparer.Ordinal);
        Assert.Equal(CellValues.InlineString, cells["F12"].DataType!.Value);
        Assert.Equal(emptyPercentage, cells["F12"].InlineString!.InnerText);
        Assert.Equal(2U, cells["F12"].StyleIndex!.Value);
        Assert.Equal("C12*F12%", cells["H12"].CellFormula!.Text);
        Assert.Equal(8m, ReadDecimal(cells["H12"]));
    }

    [Fact]
    public void FlmWithoutPercentageColumnKeepsItsDetailUnchanged()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "flm.xlsx");
        CreateFlmWorkbook(source);
        var originalHash = new GeneratedFileHashService().ComputeSha256(source);
        var originalXml = ReadWorksheetXml(source, "FLM");

        var batch = Generate(scope, source, "FLM");

        var output = Assert.Single(batch.Files).OutputPath;
        Assert.Equal(originalHash, new GeneratedFileHashService().ComputeSha256(source));
        Assert.Equal(originalXml, ReadWorksheetXml(output, "Detalle"));
    }

    private static void AssertNormalizedPercentage(WorkbookPart workbookPart, Cell cell, decimal expected)
    {
        Assert.Equal(CellValues.Number, cell.DataType?.Value);
        Assert.Equal(expected, ReadDecimal(cell));
        var format = workbookPart.WorkbookStylesPart!.Stylesheet!.CellFormats!
            .Elements<CellFormat>()
            .ElementAt((int)cell.StyleIndex!.Value);
        Assert.Equal(2U, format.NumberFormatId!.Value);
        Assert.True(format.ApplyNumberFormat!.Value);
        Assert.Equal(1U, format.FontId!.Value);
        Assert.Equal(2U, format.FillId!.Value);
        Assert.Equal(1U, format.BorderId!.Value);
        Assert.DoesNotContain(format.NumberFormatId!.Value, new uint[] { 5U, 6U, 7U, 8U, 9U, 10U });
    }

    private static PaymentGenerationBatch Generate(TestDirectory scope, string source, string worksheetName)
    {
        var paths = new AppDataPaths(Path.Combine(scope.Path, "appdata"));
        var logger = new FileLogger(paths);
        var historyService = new GenerationHistoryService(paths, logger);
        var history = historyService.Load();
        var hashService = new GeneratedFileHashService();
        var analysis = new WorkbookAnalysisService(hashService: hashService).Analyze(source);
        Assert.True(
            analysis.IsValid,
            string.Join(" ", analysis.Errors.Concat(analysis.Worksheets.SelectMany(sheet => sheet.Errors))));
        var broker = new Broker
        {
            Id = Guid.NewGuid(),
            Name = "Corredor sintético",
            PrimaryEmailAddresses = ["test@example.com"],
            AssociatedWorksheetNames = [worksheetName]
        };
        var mapping = new WorksheetBrokerMappingService().Resolve([worksheetName], [broker]);
        Assert.True(mapping.IsValid, string.Join(" ", mapping.Errors));
        var service = new PaymentWorkbookGenerationService(
            new PaymentCalculationService(),
            new FileNameSanitizer(),
            hashService,
            historyService,
            logger);
        return service.Generate(new PaymentGenerationRequest
        {
            SourceWorkbookPath = source,
            Period = "IQ prueba 2026",
            OutputDirectory = Path.Combine(scope.Path, "salida"),
            Analysis = analysis,
            Assignments = mapping.Assignments
        }, history);
    }

    private static void CreatePercentageWorkbook(
        string path,
        string worksheetName,
        string? invalidPercentage,
        uint headerRow = 1U,
        uint detailRow = 2U)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        AddStyles(workbookPart);
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        sheetData.Append(new Row(
            TextCell($"A{headerRow}", "Moneda"),
            TextCell($"C{headerRow}", "  COMISIÓN   BRUTA "),
            TextCell($"F{headerRow}", " %   Corredor "),
            TextCell($"H{headerRow}", "Comisión Corredor"),
            TextCell($"J{headerRow}", "2 %"),
            TextCell($"L{headerRow}", "Total Final"))
        { RowIndex = headerRow });
        if (invalidPercentage is not null)
        {
            sheetData.Append(CreateDetailRow(detailRow, "CRC", TextCell($"F{detailRow}", invalidPercentage, 2U)));
            sheetData.Append(
                new Row(TextCell($"A{detailRow + 1U}", "COLONES")) { RowIndex = detailRow + 1U },
                new Row(
                    TextCell($"A{detailRow + 2U}", "Monto bruto comisión"),
                    NumberCell($"C{detailRow + 2U}", 1_000m, 2U))
                { RowIndex = detailRow + 2U });
        }
        else
        {
            sheetData.Append(CreateDetailRow(2U, "CRC", TextCell("F2", " 80.00 ", 2U)));
            sheetData.Append(CreateDetailRow(3U, "USD", NumberCell("F3", 0.80m, 3U)));
            sheetData.Append(CreateDetailRow(4U, "CRC", NumberCell("F4", 0.20m, 3U)));
            sheetData.Append(new Row(
                TextCell("A5", "Monto bruto comisión"),
                NumberCell("C5", 3_000m, 2U),
                EmptyCell("F5", 2U))
            { RowIndex = 5U });
            sheetData.Append(new Row(
                TextCell("A6", "IVA"),
                NumberCell("C6", 390m, 2U),
                TextCell("F6", "   ", 2U))
            { RowIndex = 6U });
            sheetData.Append(new Row(
                TextCell("A7", "Monto factura"),
                NumberCell("C7", 3_390m, 2U))
            { RowIndex = 7U });
            sheetData.Append(new Row(
                TextCell("A8", "Retención"),
                NumberCell("C8", 60m, 2U))
            { RowIndex = 8U });
            sheetData.Append(new Row(
                TextCell("A9", "Monto depositado"),
                NumberCell("C9", 3_330m, 2U))
            { RowIndex = 9U });
            sheetData.Append(new Row(TextCell("F10", "% Corredor", 2U)) { RowIndex = 10U });
            sheetData.Append(new Row(
                TextCell("A11", "Moneda"),
                TextCell("C11", "COMISIÓN BRUTA"),
                TextCell("F11", " %   Corredor "),
                TextCell("H11", "Comisión Corredor"),
                TextCell("J11", "2 %"),
                TextCell("L11", "Total Final"))
            { RowIndex = 11U });
            sheetData.Append(CreateDetailRow(12U, "CRC", TextCell("F12", "50", 2U)));
        }

        worksheetPart.Worksheet = new Worksheet(sheetData);
        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.Sheets!.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = worksheetName
        });
        workbookPart.Workbook.Save();
    }

    private static Row CreateDetailRow(uint row, string currency, Cell percentage) => new(
        TextCell($"A{row}", currency),
        NumberCell($"C{row}", 1_000m, 2U),
        percentage,
        FormulaCell($"H{row}", $"C{row}*F{row}%", 8m, 2U),
        FormulaCell($"J{row}", $"H{row}*2%", 0.16m, 2U),
        FormulaCell($"L{row}", $"H{row}-J{row}", 7.84m, 2U))
    { RowIndex = row };

    private static void CreateFlmWorkbook(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        AddStyles(workbookPart);
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData(
            new Row(TextCell("A1", "COLONES")) { RowIndex = 1U },
            new Row(TextCell("A2", "Monto bruto comison"), NumberCell("B2", 25_000m, 2U))
                { RowIndex = 2U },
            new Row(TextCell("A5", "DÓLARES")) { RowIndex = 5U },
            new Row(TextCell("A6", "Monto bruto comisión"), NumberCell("B6", 50m, 2U))
                { RowIndex = 6U }));
        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.Sheets!.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "FLM"
        });
        workbookPart.Workbook.Save();
    }

    private static void AddStyles(WorkbookPart workbookPart)
    {
        var styles = workbookPart.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = new Stylesheet(
            new Fonts(
                new Font(),
                new Font(new Bold(), new Color { Rgb = "FF123456" }))
            { Count = 2U },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(
                    new ForegroundColor { Rgb = "FFFFFF00" },
                    new BackgroundColor { Indexed = 64U })
                { PatternType = PatternValues.Solid }))
            { Count = 3U },
            new Borders(
                new Border(),
                new Border(
                    new LeftBorder { Style = BorderStyleValues.Thin },
                    new RightBorder { Style = BorderStyleValues.Thin },
                    new TopBorder { Style = BorderStyleValues.Thin },
                    new BottomBorder { Style = BorderStyleValues.Thin },
                    new DiagonalBorder()))
            { Count = 2U },
            new CellStyleFormats(new CellFormat()) { Count = 1U },
            new CellFormats(
                new CellFormat(),
                new CellFormat { NumberFormatId = 10U, ApplyNumberFormat = true },
                new CellFormat
                {
                    FontId = 1U,
                    FillId = 2U,
                    BorderId = 1U,
                    NumberFormatId = 4U,
                    ApplyFont = true,
                    ApplyFill = true,
                    ApplyBorder = true,
                    ApplyNumberFormat = true
                },
                new CellFormat
                {
                    FontId = 1U,
                    FillId = 2U,
                    BorderId = 1U,
                    NumberFormatId = 10U,
                    ApplyFont = true,
                    ApplyFill = true,
                    ApplyBorder = true,
                    ApplyNumberFormat = true
                })
            { Count = 4U },
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0U, BuiltinId = 0U }) { Count = 1U });
        styles.Stylesheet.Save();
    }

    private static Cell TextCell(string reference, string value, uint style = 0U) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value)),
        StyleIndex = style
    };

    private static Cell NumberCell(string reference, decimal value, uint style = 0U) => new()
    {
        CellReference = reference,
        DataType = CellValues.Number,
        CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture)),
        StyleIndex = style
    };

    private static Cell EmptyCell(string reference, uint style = 0U) => new()
    {
        CellReference = reference,
        StyleIndex = style
    };

    private static Cell FormulaCell(string reference, string formula, decimal value, uint style) => new()
    {
        CellReference = reference,
        CellFormula = new CellFormula(formula),
        CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture)),
        StyleIndex = style
    };

    private static decimal ReadDecimal(Cell cell) =>
        decimal.Parse(cell.CellValue!.Text, CultureInfo.InvariantCulture);

    private static string ReadWorksheetXml(string path, string worksheetName)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return GetWorksheetPart(document, worksheetName).Worksheet!.OuterXml;
    }

    private static WorksheetPart GetWorksheetPart(SpreadsheetDocument document, string worksheetName)
    {
        var workbookPart = document.WorkbookPart!;
        var sheet = workbookPart.Workbook!.Sheets!.Elements<Sheet>()
            .Single(value => value.Name?.Value == worksheetName);
        return (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ECS-broker-percentage-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
