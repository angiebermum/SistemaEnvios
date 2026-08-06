using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class WorkbookAutomationTests
{
    [Fact]
    public void StandardAnalyzerFindsCurrenciesAndDynamicCommissionRows()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["AR"], includeUsd: true);

        var result = new WorkbookAnalysisService().Analyze(source);

        Assert.True(result.IsValid, string.Join(" ", result.Worksheets.SelectMany(value => value.Errors)));
        var sheet = Assert.Single(result.Worksheets);
        Assert.Equal("Estándar", sheet.AnalyzerName);
        Assert.True(sheet.Crc.HasCommission);
        Assert.True(sheet.Usd.HasCommission);
        Assert.Equal(100_000m, sheet.Crc.GrossCommission);
        Assert.Equal(100m, sheet.Usd.GrossCommission);
        Assert.Equal(["B9"], sheet.Crc.SourceCells);
        Assert.Equal(["B12"], sheet.Usd.SourceCells);
    }

    [Fact]
    public void FlmStructuralAnalyzerAcceptsMisspelledGrossLabel()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "flm.xlsx");
        CreateFlmWorkbook(source);

        var result = new WorkbookAnalysisService().Analyze(source);

        Assert.True(result.IsValid, string.Join(" ", result.Worksheets.SelectMany(value => value.Errors)));
        var sheet = Assert.Single(result.Worksheets);
        Assert.Equal("FLM / resumen estructural", sheet.AnalyzerName);
        Assert.Equal(25_000m, sheet.Crc.GrossCommission);
        Assert.Equal(50m, sheet.Usd.GrossCommission);
    }

    [Fact]
    public void StandardAnalyzerPrefersGrossSummaryOverRawCommissionColumn()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "detalle-con-resumen.xlsx");
        CreateDetailedWorkbookWithGrossSummary(source);

        var result = new WorkbookAnalysisService().Analyze(source);

        Assert.True(result.IsValid, string.Join(" ", result.Worksheets.SelectMany(value => value.Errors)));
        var sheet = Assert.Single(result.Worksheets);
        Assert.Equal("Estándar", sheet.AnalyzerName);
        Assert.Equal(85_963.16m, sheet.Crc.GrossCommission);
        Assert.Equal(-8.25m, sheet.Usd.GrossCommission);
        Assert.Equal(["L4"], sheet.Crc.SourceCells);
        Assert.Equal(["L8"], sheet.Usd.SourceCells);

        var crc = new PaymentCalculationService().Calculate(sheet, []).Crc;
        Assert.Equal(85_963.16m, crc.GrossCommissionOriginal);
        Assert.Equal(11_175.21m, crc.Vat);
        Assert.Equal(97_138.37m, crc.InvoiceAmount);
        Assert.Equal(1_719.26m, crc.Withholding);
        Assert.Equal(95_419.11m, crc.DepositedAmount);
    }

    [Fact]
    public void GeneratesOneFilePerWorksheetAndGroupsThreeByBroker()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["AR", "AR2", "ANDRES"], includeUsd: true);
        var broker = Broker("Corredor sintético", "AR", "AR2", "ANDRES");

        var batch = Generate(scope, source, [broker]);

        Assert.Equal(3, batch.Files.Count);
        Assert.Single(batch.Files.Select(value => value.BrokerId).Distinct());
        Assert.All(batch.Files, value => Assert.True(File.Exists(value.OutputPath)));
        Assert.Equal(3, Directory.GetFiles(batch.OutputDirectory, "*.xlsx").Length);
        Assert.Contains(batch.Files, value => Path.GetFileName(value.OutputPath).Contains("AR2", StringComparison.Ordinal));
    }

    [Fact]
    public void DeductionIsGeneratedOnlyForItsSelectedWorksheet()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["AQO", "AQM (HC)"], includeUsd: true);
        var broker = Broker("Luis Arturo Quesada Ovares", "AQO", "AQM (HC)");
        broker.Deductions.Add(new BrokerDeduction
        {
            Description = "Ahorro",
            Amount = 10_000m,
            Currency = DeductionCurrency.CRC,
            ApplicationType = DeductionApplicationType.PayableAmount,
            TargetWorksheetName = "AQO"
        });

        var batch = Generate(scope, source, [broker]);

        var aqo = batch.Files.Single(value => value.WorksheetName == "AQO");
        var aqm = batch.Files.Single(value => value.WorksheetName == "AQM (HC)");
        Assert.Single(aqo.Crc.Deductions);
        Assert.Empty(aqm.Crc.Deductions);
        Assert.Equal(aqm.Crc.DepositedAmount - 10_000m, aqo.Crc.DepositedAmount);
    }

    [Fact]
    public void DifferentBrokersNeverMixGeneratedFiles()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["AAA", "BBB"], includeUsd: true);
        var first = Broker("Corredor A", "AAA");
        var second = Broker("Corredor B", "BBB");

        var batch = Generate(scope, source, [first, second]);

        Assert.Equal(2, batch.Files.Select(value => value.BrokerId).Distinct().Count());
        Assert.Single(batch.Files, value => value.BrokerId == first.Id);
        Assert.Single(batch.Files, value => value.BrokerId == second.Id);
        Assert.Equal("AAA", batch.Files.Single(value => value.BrokerId == first.Id).WorksheetName);
        Assert.Equal("BBB", batch.Files.Single(value => value.BrokerId == second.Id).WorksheetName);
    }

    [Fact]
    public void PreservesSelectedWorksheetXmlAndDoesNotModifySource()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["AR", "OTRA"], includeUsd: true);
        var originalHash = new GeneratedFileHashService().ComputeSha256(source);
        var originalXml = ReadWorksheetXml(source, "AR");

        var batch = Generate(scope, source, [Broker("Corredor sintético", "AR", "OTRA")]);
        var generated = batch.Files.Single(value => value.WorksheetName == "AR").OutputPath;

        Assert.Equal(originalHash, new GeneratedFileHashService().ComputeSha256(source));
        Assert.Equal(originalXml, ReadWorksheetXml(generated, "Detalle"));
        using var document = SpreadsheetDocument.Open(generated, false);
        var names = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>()
            .Select(value => value.Name!.Value).ToList();
        Assert.Equal(["Detalle", "Monto de factura"], names);
        Assert.DoesNotContain("OTRA", names);
    }

    [Fact]
    public void CreatesBothCurrencyBlocksAndZerosAbsentCurrency()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["SOLOCRC"], includeUsd: false);

        var batch = Generate(scope, source, [Broker("Corredor sintético", "SOLOCRC")]);
        var generated = Assert.Single(batch.Files).OutputPath;
        var cells = ReadCells(generated, "Monto de factura");
        var dollarTitle = cells.Single(value => value.Text == "DÓLARES");
        var dollarAmounts = cells.Where(value =>
                value.Reference.StartsWith('F') &&
                value.Row > dollarTitle.Row &&
                value.NumericValue.HasValue)
            .Select(value => value.NumericValue!.Value)
            .ToList();

        var colonesTitle = cells.Single(value => value.Text == "COLONES");
        Assert.Equal("B2", colonesTitle.Reference);
        Assert.Equal("E2", dollarTitle.Reference);
        Assert.Equal(dollarTitle.Row, colonesTitle.Row);
        Assert.NotEmpty(dollarAmounts);
        Assert.All(dollarAmounts, value => Assert.Equal(0m, value));
    }

    [Fact]
    public void PaymentSheetUsesBordersSpacingAndYellowInvoiceRows()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["AR"], includeUsd: true);

        var batch = Generate(scope, source, [Broker("Corredor sintético", "AR")]);
        var generated = Assert.Single(batch.Files).OutputPath;

        using var document = SpreadsheetDocument.Open(generated, false);
        var workbookPart = document.WorkbookPart!;
        var worksheet = GetWorksheetPart(document, "Monto de factura").Worksheet!;
        var cells = worksheet.Descendants<Cell>().ToDictionary(
            value => value.CellReference!.Value!,
            StringComparer.Ordinal);
        var columns = worksheet.Elements<Columns>().Single().Elements<Column>().ToList();

        Assert.Equal(4D, columns.Single(value => value.Min?.Value == 1U).Width!.Value);
        Assert.Equal(4D, columns.Single(value => value.Min?.Value == 4U).Width!.Value);
        Assert.Equal("COLONES", cells["B2"].InlineString!.InnerText);
        Assert.Equal("DÓLARES", cells["E2"].InlineString!.InnerText);
        Assert.DoesNotContain(cells.Keys, reference =>
            reference.StartsWith('A') || reference.EndsWith('1'));

        AssertVisibleBorder(workbookPart, cells["B3"]);
        AssertVisibleBorder(workbookPart, cells["C3"]);
        AssertVisibleBorder(workbookPart, cells["E3"]);
        AssertVisibleBorder(workbookPart, cells["F3"]);

        var colonesInvoiceLabel = cells.Values.Single(value =>
            value.CellReference?.Value?.StartsWith('B') == true &&
            value.InlineString?.InnerText == "Monto factura");
        var dollarInvoiceLabel = cells.Values.Single(value =>
            value.CellReference?.Value?.StartsWith('E') == true &&
            value.InlineString?.InnerText == "Monto factura");
        AssertYellowFill(workbookPart, colonesInvoiceLabel);
        AssertYellowFill(workbookPart, cells[$"C{colonesInvoiceLabel.CellReference!.Value![1..]}"]);
        AssertYellowFill(workbookPart, dollarInvoiceLabel);
        AssertYellowFill(workbookPart, cells[$"F{dollarInvoiceLabel.CellReference!.Value![1..]}"]);
    }

    [Fact]
    public void PaymentSheetLinksBothDetailTotalsAndUsesDynamicFormulas()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["AR"], includeUsd: true);

        var batch = Generate(scope, source, [Broker("Corredor sintético", "AR")]);
        var generated = Assert.Single(batch.Files).OutputPath;

        using var document = SpreadsheetDocument.Open(generated, false);
        var cells = GetWorksheetPart(document, "Monto de factura").Worksheet!
            .Descendants<Cell>()
            .ToDictionary(value => value.CellReference!.Value!, StringComparer.Ordinal);

        Assert.Equal("'Detalle'!B9", cells["C3"].CellFormula!.Text);
        Assert.Equal("0", cells["C4"].CellFormula!.Text);
        Assert.Equal("C3-C4", cells["C5"].CellFormula!.Text);
        Assert.Equal("C5*13%", cells["C6"].CellFormula!.Text);
        Assert.Equal("C5+C6", cells["C7"].CellFormula!.Text);
        Assert.Equal("C5*2%", cells["C8"].CellFormula!.Text);
        Assert.Equal("0", cells["C9"].CellFormula!.Text);
        Assert.Equal("C7-C8-C9", cells["C10"].CellFormula!.Text);

        Assert.Equal("'Detalle'!B12", cells["F3"].CellFormula!.Text);
        Assert.Equal("F3-F4", cells["F5"].CellFormula!.Text);
        Assert.Equal("F5*13%", cells["F6"].CellFormula!.Text);
        Assert.Equal("F5+F6", cells["F7"].CellFormula!.Text);
        Assert.Equal("F5*2%", cells["F8"].CellFormula!.Text);
        Assert.Equal("F7-F8-F9", cells["F10"].CellFormula!.Text);

        var calculation = document.WorkbookPart!.Workbook!.CalculationProperties;
        Assert.NotNull(calculation);
        Assert.Equal(CalculateModeValues.Auto, calculation!.CalculationMode!.Value);
        Assert.True(calculation.CalculationOnSave!.Value);
        Assert.True(calculation.ForceFullCalculation!.Value);
        Assert.True(calculation.FullCalculationOnLoad!.Value);
        Assert.Null(document.WorkbookPart.CalculationChainPart);
        Assert.DoesNotContain(
            cells.Values.SelectMany(value => new[]
            {
                value.CellFormula?.Text ?? string.Empty,
                value.CellValue?.Text ?? string.Empty
            }),
            value => value.Contains("#REF!", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("#VALUE!", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("#DIV/0!", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ManualDetailTotalChangeKeepsInvoiceDependencyAndForcesRecalculation()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["AR"], includeUsd: true);
        var generated = Assert.Single(
            Generate(scope, source, [Broker("Corredor sintético", "AR")]).Files).OutputPath;

        using (var document = SpreadsheetDocument.Open(generated, true))
        {
            var detailWorksheet = GetWorksheetPart(document, "Detalle").Worksheet!;
            var detailTotal = detailWorksheet
                .Descendants<Cell>()
                .Single(value => value.CellReference?.Value == "B9");
            detailTotal.CellFormula = null;
            detailTotal.DataType = CellValues.Number;
            detailTotal.CellValue = new CellValue("200000");
            detailWorksheet.Save();
        }

        using var reopened = SpreadsheetDocument.Open(generated, false);
        var gross = GetWorksheetPart(reopened, "Monto de factura").Worksheet!
            .Descendants<Cell>()
            .Single(value => value.CellReference?.Value == "C3");
        Assert.Equal("'Detalle'!B9", gross.CellFormula!.Text);
        Assert.Equal(CalculateModeValues.Auto,
            reopened.WorkbookPart!.Workbook!.CalculationProperties!.CalculationMode!.Value);
        Assert.True(reopened.WorkbookPart.Workbook.CalculationProperties.FullCalculationOnLoad!.Value);
    }

    [Fact]
    public void MissingDetailGrossTotalStopsOnlyThatWorkbookWithClearError()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general-sin-total.xlsx");
        CreateStandardWorkbook(
            source,
            ["AR"],
            includeUsd: true,
            includeGrossTotals: false);

        var exception = Assert.Throws<InvalidDataException>(() =>
            Generate(scope, source, [Broker("Corredor sintético", "AR")]));

        Assert.Contains("Monto de factura", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Monto bruto comisión", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Detalle", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(scope.Path, "salida")));
    }

    [Fact]
    public void DeductionHeadersAreRenamedAndDoNotRepeatDeductionAmounts()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["AR"], includeUsd: true);
        var broker = Broker("Adriana Arroyo", "AR");
        broker.Deductions.Add(new BrokerDeduction
        {
            Description = "Ajuste",
            Amount = 10_000m,
            Currency = DeductionCurrency.CRC,
            ApplicationType = DeductionApplicationType.GrossCommission,
            TargetWorksheetName = "AR"
        });
        broker.Deductions.Add(new BrokerDeduction
        {
            Description = "Ahorro",
            Amount = 15_000m,
            Currency = DeductionCurrency.CRC,
            ApplicationType = DeductionApplicationType.PayableAmount,
            TargetWorksheetName = "AR"
        });

        var batch = Generate(scope, source, [broker]);
        var cells = ReadCells(Assert.Single(batch.Files).OutputPath, "Monto de factura");
        var byReference = cells.ToDictionary(value => value.Reference, StringComparer.Ordinal);
        var grossHeader = cells.Single(value =>
            value.Reference.StartsWith('B') && value.Text == "Ajustes al monto bruto");
        var finalHeader = cells.Single(value =>
            value.Reference.StartsWith('B') && value.Text == "Deducciones");
        var grossDeduction = cells.Single(value => value.Text.Trim() == "Ajuste");
        var finalDeduction = cells.Single(value => value.Text.Trim() == "Ahorro");

        Assert.Equal(10_000m, byReference[$"C{grossHeader.Row}"].NumericValue);
        Assert.Equal(-10_000m, byReference[$"C{grossDeduction.Row}"].NumericValue);
        Assert.Equal(15_000m, byReference[$"C{finalHeader.Row}"].NumericValue);
        Assert.Equal(-15_000m, byReference[$"C{finalDeduction.Row}"].NumericValue);
        Assert.DoesNotContain(cells, value => value.Text == "Rebajos al monto bruto");
        Assert.DoesNotContain(cells, value => value.Text == "Rebajos al monto a pagar");

        using var document = SpreadsheetDocument.Open(Assert.Single(batch.Files).OutputPath, false);
        var formulaCells = GetWorksheetPart(document, "Monto de factura").Worksheet!
            .Descendants<Cell>()
            .ToDictionary(value => value.CellReference!.Value!, StringComparer.Ordinal);
        Assert.Equal("-SUM(C5:C5)", formulaCells["C4"].CellFormula!.Text);
        Assert.Equal("C3-C4", formulaCells["C6"].CellFormula!.Text);
        Assert.Equal("C6*13%", formulaCells["C7"].CellFormula!.Text);
        Assert.Equal("C6+C7", formulaCells["C8"].CellFormula!.Text);
        Assert.Equal("C6*2%", formulaCells["C9"].CellFormula!.Text);
        Assert.Equal("-SUM(C11:C11)", formulaCells["C10"].CellFormula!.Text);
        Assert.Equal("C8-C9-C10", formulaCells["C12"].CellFormula!.Text);
    }

    [Fact]
    public void MinimumAccumulationNoteIsBoldInBothCurrencyBlocks()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(
            source,
            ["AR"],
            includeUsd: true,
            crcCommission: 10_000m,
            usdCommission: 20m);

        var batch = Generate(scope, source, [Broker("Corredor sintético", "AR")]);
        var generated = Assert.Single(batch.Files).OutputPath;

        using var document = SpreadsheetDocument.Open(generated, false);
        var workbookPart = document.WorkbookPart!;
        var notes = GetWorksheetPart(document, "Monto de factura").Worksheet!
            .Descendants<Cell>()
            .Where(value =>
                value.InlineString?.InnerText == "Comisión acumulada por ser inferior al monto mínimo establecido.")
            .ToList();

        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, value => value.CellReference?.Value == "B11");
        Assert.Contains(notes, value => value.CellReference?.Value == "E11");
        Assert.All(notes, value => AssertBoldFont(workbookPart, value));
    }

    [Fact]
    public void NegativeCommissionNoteUsesRequestedTextAndIsBoldInBothCurrencyBlocks()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(
            source,
            ["AR"],
            includeUsd: true,
            crcCommission: -10m,
            usdCommission: -5m);

        var batch = Generate(scope, source, [Broker("Corredor sintético", "AR")]);
        var generated = Assert.Single(batch.Files).OutputPath;

        using var document = SpreadsheetDocument.Open(generated, false);
        var workbookPart = document.WorkbookPart!;
        var notes = GetWorksheetPart(document, "Monto de factura").Worksheet!
            .Descendants<Cell>()
            .Where(value =>
                value.InlineString?.InnerText ==
                "Comisión negativa: no procede el pago ni la facturación.")
            .ToList();

        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, value => value.CellReference?.Value == "B11");
        Assert.Contains(notes, value => value.CellReference?.Value == "E11");
        Assert.All(notes, value => AssertBoldFont(workbookPart, value));
    }

    [Fact]
    public void SnapshotDoesNotChangeWhenBrokerDeductionChangesLater()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["AR"], includeUsd: true);
        var broker = Broker("Corredor sintético", "AR");
        broker.Deductions.Add(new BrokerDeduction
        {
            Description = "Ahorro original",
            Amount = 10_000m,
            Currency = DeductionCurrency.CRC,
            ApplicationType = DeductionApplicationType.PayableAmount,
            TargetWorksheetName = "AR"
        });

        var batch = Generate(scope, source, [broker]);
        broker.Deductions[0].Description = "Editado posteriormente";
        broker.Deductions[0].Amount = 1m;

        var snapshot = Assert.Single(batch.Files).Crc.Deductions.Single();
        Assert.Equal("Ahorro original", snapshot.Description);
        Assert.Equal(10_000m, snapshot.ConfiguredAmount);
    }

    [Fact]
    public void MissingGeneratedFileBlocksSending()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["AR"], includeUsd: true);
        var broker = Broker("Corredor sintético", "AR");
        var batch = Generate(scope, source, [broker]);
        var file = Assert.Single(batch.Files);
        File.Delete(file.OutputPath);
        var paths = new AppDataPaths(Path.Combine(scope.Path, "appdata-validation"));
        var history = new GenerationHistoryService(paths, new FileLogger(paths));

        var errors = history.ValidateGeneratedAttachments(batch, broker.Id, [file.OutputPath]);

        Assert.Contains(errors, value => value.Contains("Falta el archivo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChangedGeneratedFileHashBlocksSending()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["AR"], includeUsd: true);
        var broker = Broker("Corredor sintético", "AR");
        var batch = Generate(scope, source, [broker]);
        var file = Assert.Single(batch.Files);
        using (var stream = new FileStream(file.OutputPath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.WriteByte(0);
        }
        var paths = new AppDataPaths(Path.Combine(scope.Path, "appdata-validation"));
        var history = new GenerationHistoryService(paths, new FileLogger(paths));

        var errors = history.ValidateGeneratedAttachments(batch, broker.Id, [file.OutputPath]);

        Assert.Contains(errors, value => value.Contains("cambió", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerationDoesNotInvokeOutlookAndOnlyCreatesFilesAndHistory()
    {
        using var scope = new TestDirectory();
        var source = Path.Combine(scope.Path, "general.xlsx");
        CreateStandardWorkbook(source, ["AR"], includeUsd: true);

        var batch = Generate(scope, source, [Broker("Corredor sintético", "AR")]);

        Assert.Equal(PaymentGenerationStatus.ReadyToSend, batch.Status);
        Assert.True(File.Exists(Path.Combine(scope.Path, "appdata", "generaciones-detalles-pago.json")));
        Assert.Empty(batch.SentBrokerIds);
    }

    private static PaymentGenerationBatch Generate(
        TestDirectory scope,
        string source,
        IReadOnlyList<Broker> brokers)
    {
        var paths = new AppDataPaths(Path.Combine(scope.Path, "appdata"));
        var logger = new FileLogger(paths);
        var historyService = new GenerationHistoryService(paths, logger);
        var history = historyService.Load();
        var hash = new GeneratedFileHashService();
        var analysis = new WorkbookAnalysisService(hashService: hash).Analyze(source);
        Assert.True(analysis.IsValid, string.Join(" ", analysis.Worksheets.SelectMany(value => value.Errors)));
        var mapping = new WorksheetBrokerMappingService().Resolve(
            analysis.Worksheets.Select(value => value.WorksheetName),
            brokers);
        Assert.True(mapping.IsValid);
        var service = new PaymentWorkbookGenerationService(
            new PaymentCalculationService(),
            new FileNameSanitizer(),
            hash,
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

    private static Broker Broker(string name, params string[] worksheetNames) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        PrimaryEmailAddresses = ["test@example.com"],
        AssociatedWorksheetNames = [.. worksheetNames]
    };

    private static void CreateStandardWorkbook(
        string path,
        IReadOnlyList<string> names,
        bool includeUsd,
        decimal crcCommission = 100_000m,
        decimal usdCommission = 100m,
        bool includeGrossTotals = true)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        AddStyles(workbookPart);
        uint sheetId = 1;
        foreach (var name in names)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData(
                new Row(
                    TextCell("A1", "Reporte sintético", 1U),
                    TextCell("B1", string.Empty, 1U),
                    TextCell("C1", string.Empty, 1U))
                { RowIndex = 1U, Height = 26D, CustomHeight = true },
                new Row(
                    TextCell("A2", "Moneda"),
                    TextCell("B2", "Comisión corredor"),
                    TextCell("C2", "IVA calculado"))
                { RowIndex = 2U },
                new Row(
                    TextCell("A3", "CRC"),
                    NumberCell("B3", crcCommission, 2U),
                    FormulaCell("C3", "B3*0.13", decimal.Round(crcCommission * 0.13m, 2), 2U))
                { RowIndex = 3U });
            if (includeUsd)
            {
                sheetData.Append(new Row(
                    TextCell("A4", "USD"),
                    NumberCell("B4", usdCommission, 3U),
                    FormulaCell("C4", "B4*0.13", decimal.Round(usdCommission * 0.13m, 2), 3U))
                { RowIndex = 4U });
            }

            sheetData.Append(new Row(TextCell("A6", "Fila oculta preservada")) { RowIndex = 6U, Hidden = true });
            if (includeGrossTotals)
            {
                sheetData.Append(
                    new Row(TextCell("A8", "COLONES")) { RowIndex = 8U },
                    new Row(
                        TextCell("A9", "Monto bruto comisión"),
                        FormulaCell("B9", "SUM(B3:B3)", crcCommission, 2U))
                    { RowIndex = 9U });
                if (includeUsd)
                {
                    sheetData.Append(
                        new Row(TextCell("A11", "DÓLARES")) { RowIndex = 11U },
                        new Row(
                            TextCell("A12", "Monto bruto comisión"),
                            FormulaCell("B12", "SUM(B4:B4)", usdCommission, 3U))
                        { RowIndex = 12U });
                }
            }

            worksheetPart.Worksheet = new Worksheet(
                new Columns(
                    new Column { Min = 1U, Max = 1U, Width = 18D, CustomWidth = true },
                    new Column { Min = 2U, Max = 3U, Width = 22D, CustomWidth = true }),
                sheetData,
                new MergeCells(new MergeCell { Reference = "A1:C1" }));
            worksheetPart.Worksheet.Save();
            workbookPart.Workbook.Sheets!.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = sheetId++,
                Name = name
            });
        }

        workbookPart.Workbook.Save();
    }

    private static void CreateFlmWorkbook(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        AddStyles(workbookPart);
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData(
            new Row(TextCell("A1", "COLONES")) { RowIndex = 1U },
            new Row(TextCell("A2", "Monto bruto comison"), NumberCell("B2", 25_000m, 2U)) { RowIndex = 2U },
            new Row(TextCell("A5", "DÓLARES")) { RowIndex = 5U },
            new Row(TextCell("A6", "Monto bruto comisión"), NumberCell("B6", 50m, 3U)) { RowIndex = 6U }));
        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.Sheets!.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "FLM"
        });
        workbookPart.Workbook.Save();
    }

    private static void CreateDetailedWorkbookWithGrossSummary(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        AddStyles(workbookPart);
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData(
            new Row(
                TextCell("H1", "Moneda"),
                TextCell("J1", "Comisión Bruta"),
                TextCell("L1", "Comisión Corredor"))
            { RowIndex = 1U },
            new Row(
                TextCell("H2", "Colones"),
                NumberCell("J2", 143_271.94m, 2U),
                NumberCell("L2", 85_963.16m, 2U))
            { RowIndex = 2U },
            new Row(
                TextCell("J4", "Monto bruto comison"),
                NumberCell("L4", 85_963.16m, 2U))
            { RowIndex = 4U },
            new Row(
                TextCell("H5", "Moneda"),
                TextCell("J5", "Comisión Bruta"),
                TextCell("L5", "Comisión Corredor"))
            { RowIndex = 5U },
            new Row(
                TextCell("H6", "Dólares"),
                NumberCell("J6", -13.75m, 3U),
                NumberCell("L6", -8.25m, 3U))
            { RowIndex = 6U },
            new Row(
                TextCell("J8", "Monto bruto comison"),
                NumberCell("L8", -8.25m, 3U))
            { RowIndex = 8U }));
        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.Sheets!.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "Adriana Ar AAV Vargas"
        });
        workbookPart.Workbook.Save();
    }

    private static void AddStyles(WorkbookPart workbookPart)
    {
        var styles = workbookPart.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = new Stylesheet(
            new Fonts(
                new Font(new FontName { Val = "Calibri" }),
                new Font(new Bold(), new Color { Rgb = "FFFFFFFF" }, new FontName { Val = "Calibri" }))
            { Count = 2U },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(
                    new ForegroundColor { Rgb = "FF2F6F73" },
                    new BackgroundColor { Indexed = 64U })
                { PatternType = PatternValues.Solid }))
            { Count = 3U },
            new Borders(new Border()) { Count = 1U },
            new CellStyleFormats(new CellFormat()) { Count = 1U },
            new CellFormats(
                new CellFormat(),
                new CellFormat { FontId = 1U, FillId = 2U, ApplyFont = true, ApplyFill = true },
                new CellFormat { NumberFormatId = 4U, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 7U, ApplyNumberFormat = true })
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

    private static Cell FormulaCell(
        string reference,
        string formula,
        decimal cachedValue,
        uint style = 0U) => new()
    {
        CellReference = reference,
        CellFormula = new CellFormula(formula),
        CellValue = new CellValue(cachedValue.ToString(CultureInfo.InvariantCulture)),
        StyleIndex = style
    };

    private static string ReadWorksheetXml(string path, string name)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var part = GetWorksheetPart(document, name);
        return part.Worksheet!.OuterXml;
    }

    private static List<ReadCell> ReadCells(string path, string name)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return GetWorksheetPart(document, name).Worksheet!.Descendants<Cell>().Select(cell =>
        {
            var reference = cell.CellReference?.Value ?? string.Empty;
            var digits = new string(reference.Where(char.IsDigit).ToArray());
            _ = uint.TryParse(digits, out var row);
            var numeric = cell.DataType?.Value == CellValues.Number &&
                          decimal.TryParse(cell.CellValue?.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                ? value
                : (decimal?)null;
            return new ReadCell(
                reference,
                row,
                cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? string.Empty,
                numeric);
        }).ToList();
    }

    private static void AssertVisibleBorder(WorkbookPart workbookPart, Cell cell)
    {
        var style = workbookPart.WorkbookStylesPart!.Stylesheet!.CellFormats!
            .Elements<CellFormat>()
            .ElementAt((int)(cell.StyleIndex?.Value ?? 0U));
        var border = workbookPart.WorkbookStylesPart.Stylesheet.Borders!
            .Elements<Border>()
            .ElementAt((int)(style.BorderId?.Value ?? 0U));

        Assert.Equal(BorderStyleValues.Thin, border.LeftBorder?.Style?.Value);
        Assert.Equal(BorderStyleValues.Thin, border.RightBorder?.Style?.Value);
        Assert.Equal(BorderStyleValues.Thin, border.TopBorder?.Style?.Value);
        Assert.Equal(BorderStyleValues.Thin, border.BottomBorder?.Style?.Value);
    }

    private static void AssertYellowFill(WorkbookPart workbookPart, Cell cell)
    {
        var style = workbookPart.WorkbookStylesPart!.Stylesheet!.CellFormats!
            .Elements<CellFormat>()
            .ElementAt((int)(cell.StyleIndex?.Value ?? 0U));
        var fill = workbookPart.WorkbookStylesPart.Stylesheet.Fills!
            .Elements<Fill>()
            .ElementAt((int)(style.FillId?.Value ?? 0U));

        Assert.Equal(PatternValues.Solid, fill.PatternFill?.PatternType?.Value);
        Assert.Equal("FFFFF2CC", fill.PatternFill?.ForegroundColor?.Rgb?.Value);
    }

    private static void AssertBoldFont(WorkbookPart workbookPart, Cell cell)
    {
        var style = workbookPart.WorkbookStylesPart!.Stylesheet!.CellFormats!
            .Elements<CellFormat>()
            .ElementAt((int)(cell.StyleIndex?.Value ?? 0U));
        var font = workbookPart.WorkbookStylesPart.Stylesheet.Fonts!
            .Elements<Font>()
            .ElementAt((int)(style.FontId?.Value ?? 0U));

        Assert.NotNull(font.Bold);
    }

    private static WorksheetPart GetWorksheetPart(SpreadsheetDocument document, string name)
    {
        var workbookPart = document.WorkbookPart!;
        var sheet = workbookPart.Workbook!.Sheets!.Elements<Sheet>()
            .Single(value => value.Name?.Value == name);
        return (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
    }

    private sealed record ReadCell(string Reference, uint Row, string Text, decimal? NumericValue);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ECS-payment-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (Environment.GetEnvironmentVariable("ECS_KEEP_PAYMENT_TEST_OUTPUT") == "1")
            {
                File.WriteAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ECS-payment-last-test-path.txt"),
                    Path);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Environment.GetEnvironmentVariable("ECS_KEEP_PAYMENT_TEST_OUTPUT") == "1")
            {
                return;
            }

            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
