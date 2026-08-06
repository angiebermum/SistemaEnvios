using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class SpecialRebateSheetTests
{
    [Theory]
    [InlineData("andres-steimberg-seguru", "Nombre editado", " ASW ", true)]
    [InlineData("andres-steimberg-seguru", "Nombre editado", "asw", true)]
    [InlineData("andres-steimberg-seguru", "Nombre editado", "SIG", false)]
    [InlineData("andres-steimberg-seguru", "Nombre editado", "VEINSA", false)]
    [InlineData("andres-steimberg-seguru", "Nombre editado", "ASW SIG", false)]
    [InlineData("andres-steimberg-seguru", "Nombre editado", "ASW Veinsa", false)]
    [InlineData("andres-steimberg-seguru", "Nombre editado", "ASW extra", false)]
    [InlineData("roberto-merino-yahoo", "Nombre editado", "RMB", true)]
    [InlineData("roberto-merino-yahoo", "Nombre editado", "RMB extra", false)]
    [InlineData("sylvia-serrano-essential", "Nombre editado", "SSM", true)]
    [InlineData("sylvia-serrano-essential", "Nombre editado", "SSM extra", false)]
    [InlineData("luis-arturo-quesada-essential", "Nombre editado", "AQO", true)]
    [InlineData("luis-arturo-quesada-essential", "Nombre editado", "AQM (HC)", false)]
    [InlineData("corredor-normal", "Roberto Merino", "RMB", false)]
    [InlineData("corredor-normal", "Andrés Steimberg - Agent for EssentialGroupLA", "ASW", false)]
    public void UsesExactBrokerAndWorksheetActivationMatrix(
        string seedKey,
        string brokerName,
        string worksheetName,
        bool expected)
    {
        var broker = Broker(brokerName, seedKey, worksheetName);
        var service = new SpecialRebateSheetService(TemplateDirectory());

        var result = service.TryGetDefinition(broker, worksheetName, out var definition);

        Assert.Equal(expected, result);
        Assert.Equal(expected, definition is not null);
    }

    [Fact]
    public void BrokerNameFallbackDoesNotUsePartialMatches()
    {
        var broker = Broker("Roberto Merino adicional", null, "RMB");
        var service = new SpecialRebateSheetService(TemplateDirectory());

        Assert.False(service.TryGetDefinition(broker, "RMB", out _));
    }

    [Theory]
    [InlineData("2000000", "2200000", "2000000", "200000", "434.78260869565217391304347826", "4782.6086956521739130434782609", "0")]
    [InlineData("2200000", "2200000", "2200000", "0", "0", "4782.6086956521739130434782609", "0")]
    [InlineData("2500000", "2200000", "2200000", "0", "0", "4782.6086956521739130434782609", "300000")]
    public void CalculatesAndresAmountsWithoutNegativeResults(
        string grossText,
        string totalText,
        string appliedText,
        string pendingText,
        string usdText,
        string equivalentUsdText,
        string remainingText)
    {
        var result = SpecialRebateSheetService.CalculateAmounts(
            Decimal(grossText),
            Decimal(totalText),
            SpecialRebateSheetService.FixedExchangeRate);

        Assert.Equal(Decimal(appliedText), result.AppliedRebateCrc);
        Assert.Equal(Decimal(pendingText), result.PendingBalanceCrc);
        Assert.Equal(Decimal(usdText), result.InformationalRebateUsd);
        Assert.Equal(Decimal(equivalentUsdText), result.EquivalentTotalUsd);
        Assert.Equal(Decimal(remainingText), result.RemainingPayableCrc);
        Assert.All(
            new[]
            {
                result.GrossAmountCrc,
                result.TotalRebateCrc,
                result.AppliedRebateCrc,
                result.PendingBalanceCrc,
                result.InformationalRebateUsd,
                result.EquivalentTotalUsd,
                result.RemainingPayableCrc
            },
            value => Assert.True(value >= 0m));
    }

    [Theory]
    [InlineData(
        "394406.57", "2200499.89", false,
        null, "394406.57", null, null, null, null, null)]
    [InlineData(
        "2200000", "2200000", false,
        null, "2200000", null, null, null, null, null)]
    [InlineData(
        "2500000", "2200000", true,
        "2200000", "300000", "39000", "339000", "6000", "0", "333000")]
    public void CalculatesAndresAswCrcInvoiceCases(
        string grossText,
        string totalText,
        bool requiresInvoice,
        string? adjustmentText,
        string adjustedGrossText,
        string? vatText,
        string? invoiceText,
        string? withholdingText,
        string? deductionsText,
        string? depositedText)
    {
        var result = SpecialRebateSheetService.CalculateAndresAswCrcInvoiceAmounts(
            Decimal(grossText),
            Decimal(totalText));

        Assert.Equal(requiresInvoice, result.RequiresInvoice);
        Assert.Equal(OptionalDecimal(adjustmentText), result.GrossAdjustmentCrc);
        Assert.Equal(Decimal(adjustedGrossText), result.AdjustedGrossAmountCrc);
        Assert.Equal(OptionalDecimal(vatText), result.VatCrc);
        Assert.Equal(OptionalDecimal(invoiceText), result.InvoiceAmountCrc);
        Assert.Equal(OptionalDecimal(withholdingText), result.WithholdingCrc);
        Assert.Equal(OptionalDecimal(deductionsText), result.DeductionsCrc);
        Assert.Equal(OptionalDecimal(depositedText), result.DepositedAmountCrc);
        Assert.All(
            new decimal?[]
            {
                result.GrossAmountCrc,
                result.TotalRebateCrc,
                result.GrossAdjustmentCrc,
                result.AdjustedGrossAmountCrc,
                result.VatCrc,
                result.InvoiceAmountCrc,
                result.WithholdingCrc,
                result.DeductionsCrc,
                result.DepositedAmountCrc
            }.Where(value => value.HasValue),
            value => Assert.True(value >= 0m));
    }

    [Fact]
    public void GeneratesOnlyTheConfiguredSpecialSheetsAndChangesOnlyAndresAswCrcBlock()
    {
        using var scope = new TestDirectory();
        var source = scope.File("general.xlsx");
        var worksheets = new[]
        {
            "ASW", "SIG", "VEINSA", "OTRA", "RMB", "SSM", "AQO", "AQM (HC)", "NORMAL"
        };
        CreateStandardWorkbook(source, worksheets, 100_000m);
        var sourceHash = new GeneratedFileHashService().ComputeSha256(source);
        var brokers = new[]
        {
            Broker(
                "Andrés Steimberg - Agent for EssentialGroupLA",
                "andres-steimberg-seguru",
                "ASW", "SIG", "VEINSA", "OTRA"),
            Broker("Roberto Merino", "roberto-merino-yahoo", "RMB"),
            Broker("Sylvia Serrano", "sylvia-serrano-essential", "SSM"),
            Broker("Arturo Quesada Ovares", "luis-arturo-quesada-essential", "AQO", "AQM (HC)"),
            Broker("Corredor normal", "corredor-normal", "NORMAL")
        };

        var batch = Generate(scope, source, brokers);

        Assert.Equal(sourceHash, new GeneratedFileHashService().ComputeSha256(source));
        Assert.Equal(worksheets.Length, batch.Files.Count);
        AssertSheetNames(batch, "ASW", "Detalle", "Monto de factura", "REBAJO");
        AssertSheetNames(batch, "SIG", "Detalle", "Monto de factura");
        AssertSheetNames(batch, "VEINSA", "Detalle", "Monto de factura");
        AssertSheetNames(batch, "OTRA", "Detalle", "Monto de factura");
        AssertSheetNames(batch, "RMB", "Detalle", "Monto de factura", "REBAJO");
        AssertSheetNames(batch, "SSM", "Detalle", "Monto de factura", "REBAJO");
        AssertSheetNames(batch, "AQO", "Detalle", "Monto de factura", "REBAJO");
        AssertSheetNames(batch, "AQM (HC)", "Detalle", "Monto de factura");
        AssertSheetNames(batch, "NORMAL", "Detalle", "Monto de factura");

        var aswPath = FileFor(batch, "ASW");
        var normalPath = FileFor(batch, "NORMAL");
        Assert.Equal(460m, ReadDecimal(aswPath, "REBAJO", "F1"));
        Assert.NotEqual(
            ReadWorksheetXml(normalPath, "Monto de factura"),
            ReadWorksheetXml(aswPath, "Monto de factura"));
        Assert.Equal(
            ReadWorksheetXml(normalPath, "Monto de factura"),
            ReadWorksheetXml(FileFor(batch, "SIG"), "Monto de factura"));
        Assert.Equal(
            ReadWorksheetXml(normalPath, "Monto de factura"),
            ReadWorksheetXml(FileFor(batch, "VEINSA"), "Monto de factura"));
        Assert.Equal(
            ReadWorksheetXml(normalPath, "Monto de factura"),
            ReadWorksheetXml(FileFor(batch, "OTRA"), "Monto de factura"));
        Assert.Contains("'Detalle'!B7", ReadFormulas(aswPath, "Monto de factura"));
        Assert.Contains("'Detalle'!B10", ReadFormulas(aswPath, "Monto de factura"));
        Assert.Contains(
            ReadFormulas(aswPath, "Monto de factura"),
            value => value.Contains("'REBAJO'!D8", StringComparison.Ordinal));
        foreach (var reference in new[] { "E2", "F2", "E3", "F3", "E4", "F4", "E5", "F5",
                     "E6", "F6", "E7", "F7", "E8", "F8", "E9", "F9", "E10", "F10" })
        {
            Assert.Equal(
                ReadCellXml(normalPath, "Monto de factura", reference),
                ReadCellXml(aswPath, "Monto de factura", reference));
        }

        foreach (var reference in new[] { "B2", "C2", "B3", "C3", "B4", "C4", "B5", "C5",
                     "B6", "C6", "B7", "C7", "B8", "C8", "B9", "C9", "B10", "C10" })
        {
            Assert.Equal(
                ReadStyleIndex(normalPath, "Monto de factura", reference),
                ReadStyleIndex(aswPath, "Monto de factura", reference));
        }

        var robertoPath = FileFor(batch, "RMB");
        Assert.Equal(460m, ReadDecimal(robertoPath, "REBAJO", "F1"));
        Assert.Contains("Yerika Vega", ReadTexts(robertoPath, "REBAJO"));

        var sylviaPath = FileFor(batch, "SSM");
        Assert.Contains("Hanzel Espinoza", ReadTexts(sylviaPath, "REBAJO"));
        AssertNoExchangeRate(sylviaPath);
        Assert.Contains("₡", ReadNumberFormatCode(sylviaPath, "REBAJO", "D7"));

        var arturoPath = FileFor(batch, "AQO");
        AssertNoExchangeRate(arturoPath);
        Assert.DoesNotContain("$", ReadNumberFormatCode(arturoPath, "REBAJO", "D7"));
        Assert.DoesNotContain("$", ReadNumberFormatCode(arturoPath, "REBAJO", "F6"));
        Assert.DoesNotContain("$", ReadNumberFormatCode(arturoPath, "REBAJO", "H6"));
        Assert.Contains("50% salario Maria José actualizado", ReadTexts(arturoPath, "REBAJO"));
        Assert.Equal(304_890m, ReadDecimal(arturoPath, "REBAJO", "C19"));
        Assert.Equal(123_782m, ReadDecimal(arturoPath, "REBAJO", "C20"));
        Assert.Equal(428_672m, ReadDecimal(arturoPath, "REBAJO", "C21"));

        foreach (var file in batch.Files)
        {
            AssertWorkbookOpensWithoutFormulaErrors(file.OutputPath);
        }
    }

    [Theory]
    [InlineData("394406.57")]
    [InlineData("2200499.89")]
    [InlineData("2500000")]
    public void AndresGeneratedSheetHandlesGrossAmountCasesInCrcBlock(string grossText)
    {
        using var scope = new TestDirectory();
        var source = scope.File("general.xlsx");
        var gross = Decimal(grossText);
        CreateStandardWorkbook(source, ["ASW"], gross);
        var andres = Broker(
            "Andrés Steimberg - Agent for EssentialGroupLA",
            "andres-steimberg-seguru",
            "ASW");
        var batch = Generate(
            scope,
            source,
            [andres]);
        var generated = Assert.Single(batch.Files).OutputPath;

        var total = ReadDecimal(generated, "REBAJO", "D8");
        var expectedRebate = SpecialRebateSheetService.CalculateAmounts(
            gross,
            total,
            SpecialRebateSheetService.FixedExchangeRate);
        var expectedInvoice = SpecialRebateSheetService.CalculateAndresAswCrcInvoiceAmounts(
            gross,
            total);

        Assert.Equal(expectedRebate.AppliedRebateCrc, ReadDecimal(generated, "REBAJO", "D10"));
        Assert.Equal(expectedRebate.PendingBalanceCrc, ReadDecimal(generated, "REBAJO", "D11"));
        Assert.Equal(expectedRebate.InformationalRebateUsd, ReadDecimal(generated, "REBAJO", "D12"));
        Assert.Equal(expectedRebate.RemainingPayableCrc, ReadDecimal(generated, "REBAJO", "D13"));
        Assert.Equal(460m, ReadDecimal(generated, "REBAJO", "F1"));
        Assert.Equal(expectedInvoice.GrossAmountCrc, ReadOptionalDecimal(
            generated, "Monto de factura", "C3"));
        Assert.Equal(expectedInvoice.GrossAdjustmentCrc, ReadOptionalDecimal(
            generated, "Monto de factura", "C4"));
        Assert.Equal(expectedInvoice.AdjustedGrossAmountCrc, ReadOptionalDecimal(
            generated, "Monto de factura", "C5"));
        Assert.Equal(expectedInvoice.VatCrc, ReadOptionalDecimal(
            generated, "Monto de factura", "C6"));
        Assert.Equal(expectedInvoice.InvoiceAmountCrc, ReadOptionalDecimal(
            generated, "Monto de factura", "C7"));
        Assert.Equal(
            expectedInvoice.WithholdingCrc.HasValue
                ? -expectedInvoice.WithholdingCrc.Value
                : null,
            ReadOptionalDecimal(
                generated, "Monto de factura", "C8"));
        Assert.Null(ReadOptionalDecimal(
            generated, "Monto de factura", "C9"));
        Assert.Equal(expectedInvoice.DepositedAmountCrc, ReadOptionalDecimal(
            generated, "Monto de factura", "C10"));
        Assert.Contains("'Detalle'!B7", ReadFormulas(generated, "Monto de factura"));
        Assert.Contains(
            ReadFormulas(generated, "Monto de factura"),
            value => value.Contains("'REBAJO'!D8", StringComparison.Ordinal));
        Assert.All(
            new[] { "D8", "D9", "D10", "D11", "D12", "D13" },
            reference => Assert.True(ReadDecimal(generated, "REBAJO", reference) >= 0m));
        Assert.Contains(
            ReadFormulas(generated, "REBAJO"),
            value => value.Contains("'Monto de factura'!C3", StringComparison.Ordinal));
        Assert.Empty(andres.Deductions);
        AssertWorkbookOpensWithoutFormulaErrors(generated);
    }

    [Fact]
    public void AndresAswKeepsDeductionHeaderEmptyAndAddsIndividualNegativeDeduction()
    {
        using var scope = new TestDirectory();
        var source = scope.File("general.xlsx");
        CreateStandardWorkbook(source, ["ASW"], 2_500_000m);
        var andres = Broker(
            "Andrés Steimberg - Agent for EssentialGroupLA",
            "andres-steimberg-seguru",
            "ASW");
        andres.Deductions.Add(new BrokerDeduction
        {
            Description = "Ahorro",
            Amount = 15_000m,
            Currency = DeductionCurrency.CRC,
            ApplicationType = DeductionApplicationType.PayableAmount,
            TargetWorksheetName = "ASW"
        });

        var generated = Assert.Single(Generate(scope, source, [andres]).Files).OutputPath;
        var totalRebate = ReadDecimal(generated, "REBAJO", "D8");
        var expected = SpecialRebateSheetService.CalculateAndresAswCrcInvoiceAmounts(
            2_500_000m,
            totalRebate,
            15_000m);

        Assert.Equal(
            expected.WithholdingCrc.HasValue ? -expected.WithholdingCrc.Value : null,
            ReadOptionalDecimal(generated, "Monto de factura", "C8"));
        Assert.Null(ReadOptionalDecimal(generated, "Monto de factura", "C9"));
        Assert.Equal(-15_000m, ReadDecimal(generated, "Monto de factura", "C10"));
        Assert.Equal(
            expected.DepositedAmountCrc,
            ReadOptionalDecimal(generated, "Monto de factura", "C11"));
        Assert.Equal(
            "IF(C3>'REBAJO'!D8,MAX(C7+C8+SUM(C10:C10),0),\"\")",
            ReadFormula(generated, "Monto de factura", "C11"));
        AssertWorkbookOpensWithoutFormulaErrors(generated);
    }

    [Fact]
    public void OtherBrokerWithAswKeepsTheNormalPaymentSheet()
    {
        using var scope = new TestDirectory();
        var source = scope.File("general.xlsx");
        CreateStandardWorkbook(source, ["ASW", "NORMAL"], 2500000m);
        var broker = Broker("Corredor normal", "corredor-normal", "ASW", "NORMAL");

        var batch = Generate(scope, source, [broker]);

        Assert.Equal(
            ReadWorksheetXml(FileFor(batch, "NORMAL"), "Monto de factura"),
            ReadWorksheetXml(FileFor(batch, "ASW"), "Monto de factura"));
        AssertSheetNames(batch, "ASW", "Detalle", "Monto de factura");
        Assert.Empty(broker.Deductions);
    }

    private static PaymentGenerationBatch Generate(
        TestDirectory scope,
        string source,
        IReadOnlyList<Broker> brokers)
    {
        var paths = new AppDataPaths(scope.Directory("appdata"));
        var logger = new FileLogger(paths);
        var historyService = new GenerationHistoryService(paths, logger);
        var hash = new GeneratedFileHashService();
        var analysis = new WorkbookAnalysisService(hashService: hash).Analyze(source);
        Assert.True(
            analysis.IsValid,
            string.Join(" ", analysis.Worksheets.SelectMany(value => value.Errors)));
        var mapping = new WorksheetBrokerMappingService().Resolve(
            analysis.Worksheets.Select(value => value.WorksheetName),
            brokers);
        Assert.True(mapping.IsValid);
        var service = new PaymentWorkbookGenerationService(
            new PaymentCalculationService(),
            new FileNameSanitizer(),
            hash,
            historyService,
            logger,
            new SpecialRebateSheetService(TemplateDirectory()));
        return service.Generate(new PaymentGenerationRequest
        {
            SourceWorkbookPath = source,
            Period = "IQ prueba rebajo 2026",
            OutputDirectory = scope.Directory("salida"),
            Analysis = analysis,
            Assignments = mapping.Assignments
        }, historyService.Load());
    }

    private static Broker Broker(
        string name,
        string? seedKey,
        params string[] worksheetNames) => new()
    {
        Id = Guid.NewGuid(),
        SeedKey = seedKey,
        Name = name,
        PrimaryEmailAddresses = ["test@example.com"],
        AssociatedWorksheetNames = [.. worksheetNames]
    };

    private static void CreateStandardWorkbook(
        string path,
        IReadOnlyList<string> worksheetNames,
        decimal crcCommission)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = new Stylesheet(
            new Fonts(new Font()),
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 })),
            new Borders(new Border()),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(new CellFormat()),
            new CellStyles(new CellStyle
            {
                Name = "Normal",
                FormatId = 0U,
                BuiltinId = 0U
            }));
        stylesPart.Stylesheet.Save();

        uint sheetId = 1U;
        foreach (var name in worksheetNames)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(new SheetData(
                new Row(TextCell("A1", "Reporte sintético")) { RowIndex = 1U },
                new Row(
                    TextCell("A2", "Moneda"),
                    TextCell("B2", "Comisión corredor"))
                { RowIndex = 2U },
                new Row(
                    TextCell("A3", "CRC"),
                    NumberCell("B3", crcCommission))
                { RowIndex = 3U },
                new Row(
                    TextCell("A4", "USD"),
                    NumberCell("B4", 100m))
                { RowIndex = 4U },
                new Row(TextCell("A6", "COLONES")) { RowIndex = 6U },
                new Row(
                    TextCell("A7", "Monto bruto comisión"),
                    NumberCell("B7", crcCommission))
                { RowIndex = 7U },
                new Row(TextCell("A9", "DÓLARES")) { RowIndex = 9U },
                new Row(
                    TextCell("A10", "Monto bruto comisión"),
                    NumberCell("B10", 100m))
                { RowIndex = 10U }));
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

    private static Cell TextCell(string reference, string text) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(text))
    };

    private static Cell NumberCell(string reference, decimal value) => new()
    {
        CellReference = reference,
        DataType = CellValues.Number,
        CellValue = new CellValue(
            value.ToString("0.############################", CultureInfo.InvariantCulture))
    };

    private static void AssertSheetNames(
        PaymentGenerationBatch batch,
        string worksheetName,
        params string[] expectedNames)
    {
        var path = FileFor(batch, worksheetName);
        using var document = SpreadsheetDocument.Open(path, false);
        var actual = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>()
            .Select(value => value.Name!.Value!)
            .ToList();
        Assert.Equal(expectedNames, actual);
    }

    private static string FileFor(PaymentGenerationBatch batch, string worksheetName) =>
        batch.Files.Single(value => value.WorksheetName == worksheetName).OutputPath;

    private static decimal ReadDecimal(string path, string worksheetName, string reference)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var cell = GetWorksheetPart(document, worksheetName).Worksheet!
            .Descendants<Cell>()
            .Single(value =>
                string.Equals(
                    value.CellReference?.Value,
                    reference,
                    StringComparison.OrdinalIgnoreCase));
        Assert.True(
            decimal.TryParse(
                cell.CellValue?.Text,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var value),
            $"{worksheetName}!{reference} no contiene un valor decimal.");
        return value;
    }

    private static decimal? ReadOptionalDecimal(
        string path,
        string worksheetName,
        string reference)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var cell = GetWorksheetPart(document, worksheetName).Worksheet!
            .Descendants<Cell>()
            .Single(value =>
                string.Equals(
                    value.CellReference?.Value,
                    reference,
                    StringComparison.OrdinalIgnoreCase));
        return decimal.TryParse(
            cell.CellValue?.Text,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static string? ReadFormula(
        string path,
        string worksheetName,
        string reference)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return GetWorksheetPart(document, worksheetName).Worksheet!
            .Descendants<Cell>()
            .Single(value => string.Equals(
                value.CellReference?.Value,
                reference,
                StringComparison.OrdinalIgnoreCase))
            .CellFormula?.Text;
    }

    private static IReadOnlyList<string> ReadTexts(string path, string worksheetName)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return GetWorksheetPart(document, worksheetName).Worksheet!
            .Descendants<Cell>()
            .Select(value => value.InlineString?.InnerText ?? value.CellValue?.Text ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToList();
    }

    private static string ReadNumberFormatCode(
        string path,
        string worksheetName,
        string reference)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart!;
        var cell = GetWorksheetPart(document, worksheetName).Worksheet!
            .Descendants<Cell>()
            .Single(value =>
                string.Equals(
                    value.CellReference?.Value,
                    reference,
                    StringComparison.OrdinalIgnoreCase));
        var styleIndex = (int)(cell.StyleIndex?.Value ?? 0U);
        var format = workbookPart.WorkbookStylesPart!.Stylesheet!.CellFormats!
            .Elements<CellFormat>()
            .ElementAt(styleIndex);
        var numberFormatId = format.NumberFormatId?.Value ?? 0U;
        return workbookPart.WorkbookStylesPart.Stylesheet.NumberingFormats?
                   .Elements<NumberingFormat>()
                   .FirstOrDefault(value => value.NumberFormatId?.Value == numberFormatId)
                   ?.FormatCode?.Value
               ?? numberFormatId.ToString(CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> ReadFormulas(string path, string worksheetName)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return GetWorksheetPart(document, worksheetName).Worksheet!
            .Descendants<CellFormula>()
            .Select(value => value.Text ?? string.Empty)
            .ToList();
    }

    private static string ReadWorksheetXml(string path, string worksheetName)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        using var stream = GetWorksheetPart(document, worksheetName)
            .GetStream(FileMode.Open, FileAccess.Read);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ReadCellXml(
        string path,
        string worksheetName,
        string reference)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return GetWorksheetPart(document, worksheetName).Worksheet!
            .Descendants<Cell>()
            .Single(value =>
                string.Equals(
                    value.CellReference?.Value,
                    reference,
                    StringComparison.OrdinalIgnoreCase))
            .OuterXml;
    }

    private static uint ReadStyleIndex(
        string path,
        string worksheetName,
        string reference)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return GetWorksheetPart(document, worksheetName).Worksheet!
                   .Descendants<Cell>()
                   .Single(value =>
                       string.Equals(
                           value.CellReference?.Value,
                           reference,
                           StringComparison.OrdinalIgnoreCase))
                   .StyleIndex?.Value
               ?? 0U;
    }

    private static void AssertNoExchangeRate(string path)
    {
        var texts = ReadTexts(path, "REBAJO");
        Assert.DoesNotContain(
            texts,
            value => value.Contains("DOLARES", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("DÓLARES", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("T.C", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("TC FIJO", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            ReadFormulas(path, "REBAJO"),
            value => value.Contains("/F1", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertWorkbookOpensWithoutFormulaErrors(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var errorTokens = new[] { "#REF!", "#VALUE!", "#DIV/0!", "#NAME?", "#N/A" };
        foreach (var worksheetPart in document.WorkbookPart!.WorksheetParts)
        {
            var contents = worksheetPart.Worksheet!.Descendants<Cell>()
                .SelectMany(value => new[]
                {
                    value.CellFormula?.Text ?? string.Empty,
                    value.CellValue?.Text ?? string.Empty,
                    value.InlineString?.InnerText ?? string.Empty
                });
            Assert.DoesNotContain(
                contents,
                value => errorTokens.Any(token =>
                    value.Contains(token, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static WorksheetPart GetWorksheetPart(
        SpreadsheetDocument document,
        string worksheetName)
    {
        var workbookPart = document.WorkbookPart!;
        var sheet = workbookPart.Workbook!.Sheets!.Elements<Sheet>()
            .Single(value =>
                string.Equals(value.Name?.Value, worksheetName, StringComparison.Ordinal));
        return (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
    }

    private static string TemplateDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "RebateTemplates"),
            Path.Combine(Directory.GetCurrentDirectory(), "Data", "RebateTemplates")
        };
        var directory = candidates.FirstOrDefault(Directory.Exists);
        Assert.NotNull(directory);
        return directory!;
    }

    private static decimal Decimal(string value) =>
        decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static decimal? OptionalDecimal(string? value) =>
        value is null ? null : Decimal(value);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ECS-SpecialRebateTests-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public string Directory(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Path))
                {
                    System.IO.Directory.Delete(Path, true);
                }
            }
            catch
            {
                // Los temporales de una prueba fallida pueden eliminarse manualmente.
            }
        }
    }
}
