using System.IO.Compression;
using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsNextMonthWorkbookGenerationTests
{
    [Fact]
    public void GeneratesExactlyDataAndTotalsSheetsWithLiveFormulasPrivacyAndImmutableSource()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        const string sheetName = "Reporte 'Agosto'";
        var source = Path.Combine(directory.Path, "source.xlsx");
        var output = Path.Combine(directory.Path, "next-month.xlsx");
        var headers = ExpirationsPremiumTestWorkbook.Headers(21, 8, 9);
        ExpirationsPremiumTestWorkbook.Create(
            source,
            sheetName,
            7,
            headers,
            8,
            9,
            [
                new PremiumTestRow(8, "100", "CRC", "SECRETO_A_1"),
                new PremiumTestRow(9, "999", "USD", "SECRETO_B_NO_SELECCIONADO"),
                new PremiumTestRow(12, "200", "CRC", "SECRETO_A_2"),
                new PremiumTestRow(35, "50", "USD", "SECRETO_A_3")
            ]);
        var sourceSha = Sha(source);

        new ExpirationsNextMonthStandardWorkbookGenerator().Generate(
            new ExpirationsNextMonthStandardWorkbookGenerationRequest(
                source, output, sheetName, 7, [8, 12, 35]),
            TestContext.Current.CancellationToken);

        Assert.Equal(sourceSha, Sha(source));
        using (var document = SpreadsheetDocument.Open(output, false))
        {
            var validationErrors = new OpenXmlValidator()
                .Validate(document, TestContext.Current.CancellationToken)
                .ToList();
            Assert.True(
                validationErrors.Count == 0,
                string.Join(Environment.NewLine, validationErrors.Select(error =>
                    $"{error.Description} | {error.Node?.LocalName} | {error.Path?.XPath}")));
            var workbookPart = document.WorkbookPart!;
            var sheets = workbookPart.Workbook!.Sheets!.Elements<Sheet>().ToList();
            Assert.Equal(2, sheets.Count);
            Assert.Equal(ExpirationsNextMonthStandardWorkbookGenerator.DataSheetName, sheets[0].Name!.Value);
            Assert.Equal(ExpirationsPremiumTotalsSheetService.TotalsSheetName, sheets[1].Name!.Value);

            var data = ((WorksheetPart)workbookPart.GetPartById(sheets[0].Id!)).Worksheet!;
            Assert.Equal([8U, 9U, 10U], data.GetFirstChild<SheetData>()!.Elements<Row>()
                .Where(row => row.RowIndex!.Value > 7)
                .Select(row => row.RowIndex!.Value));

            var totals = ((WorksheetPart)workbookPart.GetPartById(sheets[1].Id!)).Worksheet!;
            var cells = totals.Descendants<Cell>().ToList();
            Assert.Equal(["B2", "C2", "B3", "C3", "B4", "C4"], cells.Select(cell => cell.CellReference!.Value));
            Assert.Equal(ExpirationsPremiumTotalsSheetService.Title, cells[0].InlineString!.InnerText);
            Assert.Equal(ExpirationsPremiumTotalsSheetService.CrcLabel, cells[2].InlineString!.InnerText);
            Assert.Equal(ExpirationsPremiumTotalsSheetService.UsdLabel, cells[4].InlineString!.InnerText);
            Assert.Contains("'Detalle'!$I:$I", cells[3].CellFormula!.Text, StringComparison.Ordinal);
            Assert.Contains("'Detalle'!$H:$H", cells[3].CellFormula!.Text, StringComparison.Ordinal);
            Assert.Contains("\"CRC\"", cells[3].CellFormula!.Text, StringComparison.Ordinal);
            Assert.Contains("\"USD\"", cells[5].CellFormula!.Text, StringComparison.Ordinal);
            Assert.Equal(3, totals.GetFirstChild<SheetData>()!.Elements<Row>().Count());
            Assert.Equal("B2:C2", Assert.Single(totals.Elements<MergeCells>().Single().Elements<MergeCell>()).Reference!.Value);
            Assert.All(cells, cell => Assert.True(cell.StyleIndex?.Value > 0U));
            var stylesheet = workbookPart.WorkbookStylesPart!.Stylesheet!;
            var formats = stylesheet.CellFormats!.Elements<CellFormat>().ToList();
            Assert.All(cells, cell => Assert.True(formats[(int)cell.StyleIndex!.Value].BorderId?.Value > 0U));
            var crcFormatId = formats[(int)cells[3].StyleIndex!.Value].NumberFormatId!.Value;
            var usdFormatId = formats[(int)cells[5].StyleIndex!.Value].NumberFormatId!.Value;
            Assert.NotEqual(crcFormatId, usdFormatId);
            var numberingFormats = stylesheet.NumberingFormats!.Elements<NumberingFormat>().ToList();
            Assert.Contains(numberingFormats, format =>
                format.NumberFormatId?.Value == crcFormatId &&
                format.FormatCode?.Value?.Contains("₡", StringComparison.Ordinal) == true);
            Assert.Contains(numberingFormats, format =>
                format.NumberFormatId?.Value == usdFormatId &&
                format.FormatCode?.Value?.Contains("$", StringComparison.Ordinal) == true);

            var calculation = workbookPart.Workbook.CalculationProperties!;
            Assert.Equal(CalculateModeValues.Auto, calculation.CalculationMode!.Value);
            Assert.True(calculation.CalculationOnSave!.Value);
            Assert.True(calculation.ForceFullCalculation!.Value);
            Assert.True(calculation.FullCalculationOnLoad!.Value);
            Assert.Null(workbookPart.CalculationChainPart);
        }

        var package = ReadPackageText(output);
        Assert.Contains("SECRETO_A_1", package, StringComparison.Ordinal);
        Assert.Contains("SECRETO_A_2", package, StringComparison.Ordinal);
        Assert.Contains("SECRETO_A_3", package, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETO_B_NO_SELECCIONADO", package, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETO_OTRA_HOJA_FASE8", package, StringComparison.Ordinal);

        using (var document = SpreadsheetDocument.Open(output, true))
        {
            var dataSheet = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().First();
            var data = ((WorksheetPart)document.WorkbookPart.GetPartById(dataSheet.Id!)).Worksheet!;
            var premium = data.Descendants<Cell>().Single(cell => cell.CellReference?.Value == "H8");
            premium.CellValue = new CellValue("300");
            data.Save();
        }
        using (var document = SpreadsheetDocument.Open(output, false))
        {
            var totals = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().Last();
            var formula = ((WorksheetPart)document.WorkbookPart.GetPartById(totals.Id!)).Worksheet!
                .Descendants<Cell>().Single(cell => cell.CellReference?.Value == "C3").CellFormula!.Text;
            Assert.Contains("$H:$H", formula, StringComparison.Ordinal);
            Assert.DoesNotContain("$H$8:$H$10", formula, StringComparison.Ordinal);
            Assert.DoesNotContain("300", formula, StringComparison.Ordinal);
            Assert.DoesNotContain("500", formula, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("CRC")]
    [InlineData("USD")]
    public void AlwaysCreatesBothDynamicFormulaCellsForAllRecognizedLabels(
        string currency)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, $"only-{currency}.xlsx");
        var output = Path.Combine(directory.Path, $"only-{currency}-output.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            source,
            "Reporte",
            1,
            ["Broker", "Prima", "Moneda", "Privado"],
            2,
            3,
            [new PremiumTestRow(2, "125", currency, "SECRETO")]);

        new ExpirationsNextMonthStandardWorkbookGenerator().Generate(
            new ExpirationsNextMonthStandardWorkbookGenerationRequest(
                source, output, "Reporte", 1, [2]),
            TestContext.Current.CancellationToken);

        using var document = SpreadsheetDocument.Open(output, false);
        var totalsSheet = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().Last();
        var totals = ((WorksheetPart)document.WorkbookPart.GetPartById(totalsSheet.Id!)).Worksheet!;
        var crcFormula = totals.Descendants<Cell>().Single(cell => cell.CellReference?.Value == "C3").CellFormula;
        var usdFormula = totals.Descendants<Cell>().Single(cell => cell.CellReference?.Value == "C4").CellFormula;
        Assert.NotNull(crcFormula);
        Assert.NotNull(usdFormula);
        Assert.Contains("\"CRC\"", crcFormula!.Text, StringComparison.Ordinal);
        Assert.Contains("\"USD\"", usdFormula!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("$B$2:$B$2", crcFormula.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("$B$2:$B$2", usdFormula.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleRawLabelsAreDeterministicDeduplicatedAndGroupedByClassifiedCurrency()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "labels.xlsx");
        var output = Path.Combine(directory.Path, "labels-output.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            source,
            "Hoja con espacios",
            1,
            ExpirationsPremiumTestWorkbook.Headers(20, 3, 18),
            3,
            18,
            [
                new PremiumTestRow(2, "10", "CRC", "A"),
                new PremiumTestRow(3, "20", "Colones", "B"),
                new PremiumTestRow(4, "30", "₡", "C"),
                new PremiumTestRow(5, "40", "CRC", "D"),
                new PremiumTestRow(6, "50", "USD", "E"),
                new PremiumTestRow(7, "60", "Dólares", "F"),
                new PremiumTestRow(8, "70", "$", "G"),
                new PremiumTestRow(9, "80", "crc", "H"),
                new PremiumTestRow(10, "90", "usd", "I")
            ]);

        new ExpirationsNextMonthStandardWorkbookGenerator().Generate(
            new ExpirationsNextMonthStandardWorkbookGenerationRequest(
                source, output, "Hoja con espacios", 1, [2, 3, 4, 5, 6, 7, 8, 9, 10]),
            TestContext.Current.CancellationToken);

        using var document = SpreadsheetDocument.Open(output, false);
        var totalsSheet = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().Last();
        var totals = ((WorksheetPart)document.WorkbookPart.GetPartById(totalsSheet.Id!)).Worksheet!;
        var crc = totals.Descendants<Cell>().Single(cell => cell.CellReference?.Value == "C3").CellFormula!.Text;
        var usd = totals.Descendants<Cell>().Single(cell => cell.CellReference?.Value == "C4").CellFormula!.Text;
        Assert.Contains("\"CRC\"", crc, StringComparison.Ordinal);
        Assert.Contains("\"COLONES\"", crc, StringComparison.Ordinal);
        Assert.Contains("\"₡\"", crc, StringComparison.Ordinal);
        Assert.Contains("\"USD\"", usd, StringComparison.Ordinal);
        Assert.Contains("\"DÓLARES\"", usd, StringComparison.Ordinal);
        Assert.Contains("\"$\"", usd, StringComparison.Ordinal);
        Assert.Contains("'Detalle'!$R:$R", crc, StringComparison.Ordinal);
        Assert.Contains("'Detalle'!$C:$C", usd, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("EUR", "EUR")]
    [InlineData("", "vacía")]
    public void UnknownOrBlankCurrencyBlocksAllTotalsBeforeMaterialization(
        string invalidCurrency,
        string expectedMessage)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "currency-invalid.xlsx");
        var output = Path.Combine(directory.Path, "currency-invalid-output.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            source,
            "Reporte",
            1,
            ["Broker", "Prima", "Moneda", "Privado"],
            2,
            3,
            [
                new PremiumTestRow(2, "100", "CRC", "A"),
                new PremiumTestRow(3, "50", "USD", "B"),
                new PremiumTestRow(4, "75", invalidCurrency, "C")
            ]);

        var exception = Assert.Throws<ExpirationsGenerationException>(() =>
            new ExpirationsNextMonthStandardWorkbookGenerator().Generate(
                new ExpirationsNextMonthStandardWorkbookGenerationRequest(
                    source, output, "Reporte", 1, [2, 3, 4]),
                TestContext.Current.CancellationToken));

        Assert.Contains("4", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void SparseSourceRowsAndDuplicateInputBecomeOneCompactRangePerBroker()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "sparse.xlsx");
        var output = Path.Combine(directory.Path, "sparse-output.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            source,
            "Reporte",
            1,
            ["Broker", "Prima", "Moneda", "Privado"],
            2,
            3,
            [
                new PremiumTestRow(2, "100", "CRC", "A"),
                new PremiumTestRow(10, "200", "CRC", "B"),
                new PremiumTestRow(35, "300", "CRC", "C")
            ]);

        new ExpirationsNextMonthStandardWorkbookGenerator().Generate(
            new ExpirationsNextMonthStandardWorkbookGenerationRequest(
                source, output, "Reporte", 1, [2, 10, 10, 10, 35]),
            TestContext.Current.CancellationToken);

        using var document = SpreadsheetDocument.Open(output, false);
        var sheets = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().ToList();
        var data = ((WorksheetPart)document.WorkbookPart.GetPartById(sheets[0].Id!)).Worksheet!;
        Assert.Equal([2U, 3U, 4U], data.GetFirstChild<SheetData>()!.Elements<Row>()
            .Where(row => row.RowIndex!.Value > 1)
            .Select(row => row.RowIndex!.Value));
        var totals = ((WorksheetPart)document.WorkbookPart.GetPartById(sheets[1].Id!)).Worksheet!;
        var formula = totals.Descendants<Cell>().Single(cell => cell.CellReference?.Value == "C3").CellFormula!.Text;
        Assert.Contains("$B:$B", formula, StringComparison.Ordinal);
        Assert.Contains("$C:$C", formula, StringComparison.Ordinal);
        Assert.DoesNotContain("$B$2:$B$35", formula, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(formula, "SUMPRODUCT"));
    }

    [Fact]
    public void SharedRowIsCountedFullyInEachIndependentBrokerWorkbook()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "shared.xlsx");
        var anaOutput = Path.Combine(directory.Path, "ana.xlsx");
        var jerrikaOutput = Path.Combine(directory.Path, "jerrika.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            source,
            "Reporte",
            1,
            ["Broker", "Prima", "Moneda", "Privado"],
            2,
            3,
            [new PremiumTestRow(10, "500", "CRC", "ANA + JERRIKA", BrokerValue: "Ana Luisa + Jerrika")]);
        var generator = new ExpirationsNextMonthStandardWorkbookGenerator();

        generator.Generate(new ExpirationsNextMonthStandardWorkbookGenerationRequest(
            source, anaOutput, "Reporte", 1, [10]), TestContext.Current.CancellationToken);
        generator.Generate(new ExpirationsNextMonthStandardWorkbookGenerationRequest(
            source, jerrikaOutput, "Reporte", 1, [10]), TestContext.Current.CancellationToken);

        Assert.Equal(TotalsFormula(anaOutput, "C3"), TotalsFormula(jerrikaOutput, "C3"));
        Assert.Contains("$B:$B", TotalsFormula(anaOutput, "C3"), StringComparison.Ordinal);
        Assert.Contains("ANA + JERRIKA", ReadPackageText(anaOutput), StringComparison.Ordinal);
        Assert.Contains("ANA + JERRIKA", ReadPackageText(jerrikaOutput), StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingRoutingRowsAreConsumedWithoutNameRulesInPremiumServices()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "routing.xlsx");
        var albertoOutput = Path.Combine(directory.Path, "alberto.xlsx");
        var javierOutput = Path.Combine(directory.Path, "javier.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            source,
            "Reporte",
            1,
            ["Broker", "Prima", "Moneda", "Privado"],
            2,
            3,
            [
                new PremiumTestRow(2, "100", "CRC", "PC→ALBERTO", BrokerValue: "PC Guanacaste"),
                new PremiumTestRow(3, "200", "USD", "HERNÁN→JAVIER", BrokerValue: "Hernán")
            ]);
        var generator = new ExpirationsNextMonthStandardWorkbookGenerator();

        generator.Generate(new ExpirationsNextMonthStandardWorkbookGenerationRequest(
            source, albertoOutput, "Reporte", 1, [2]), TestContext.Current.CancellationToken);
        generator.Generate(new ExpirationsNextMonthStandardWorkbookGenerationRequest(
            source, javierOutput, "Reporte", 1, [3]), TestContext.Current.CancellationToken);

        Assert.Contains("PC→ALBERTO", ReadPackageText(albertoOutput), StringComparison.Ordinal);
        Assert.Contains("HERNÁN→JAVIER", ReadPackageText(javierOutput), StringComparison.Ordinal);
        Assert.Contains("\"USD\"", TotalsFormula(albertoOutput, "C4"), StringComparison.Ordinal);
        Assert.Contains("\"CRC\"", TotalsFormula(javierOutput, "C3"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PostProcessingFailureOrInvalidFinalWorkbookDeletesPartialDestination(bool throwsDuringTotals)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "cleanup.xlsx");
        var output = Path.Combine(directory.Path, "cleanup-output.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            source,
            "Reporte",
            1,
            ["Broker", "Prima", "Moneda", "Privado"],
            2,
            3,
            [new PremiumTestRow(2, "100", "CRC", "A")]);
        IExpirationsPremiumTotalsSheetService totals = throwsDuringTotals
            ? new ThrowingTotalsService()
            : new NoOpTotalsService();
        var generator = new ExpirationsNextMonthStandardWorkbookGenerator(
            premiumTotalsSheetService: totals);

        Assert.Throws<ExpirationsGenerationException>(() => generator.Generate(
            new ExpirationsNextMonthStandardWorkbookGenerationRequest(
                source, output, "Reporte", 1, [2]),
            TestContext.Current.CancellationToken));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public void DataWorksheetNamedTotalsBlocksWithoutRenamingOrCreatingAFile()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "name-conflict.xlsx");
        var output = Path.Combine(directory.Path, "name-conflict-output.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            source,
            ExpirationsPremiumTotalsSheetService.TotalsSheetName,
            1,
            ["Broker", "Prima", "Moneda", "Privado"],
            2,
            3,
            [new PremiumTestRow(2, "100", "CRC", "A")]);

        var exception = Assert.Throws<ExpirationsGenerationException>(() =>
            new ExpirationsNextMonthStandardWorkbookGenerator().Generate(
                new ExpirationsNextMonthStandardWorkbookGenerationRequest(
                    source, output, ExpirationsPremiumTotalsSheetService.TotalsSheetName, 1, [2]),
                TestContext.Current.CancellationToken));

        Assert.Contains("ya se llama", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    private static int Occurrences(string value, string fragment) =>
        (value.Length - value.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) / fragment.Length;

    private static string TotalsFormula(string path, string reference)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var totalsSheet = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().Last();
        return ((WorksheetPart)document.WorkbookPart.GetPartById(totalsSheet.Id!)).Worksheet!
            .Descendants<Cell>().Single(cell => cell.CellReference?.Value == reference).CellFormula!.Text;
    }

    private static string Sha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string ReadPackageText(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return string.Join("\n", archive.Entries
            .Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(entry =>
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }));
    }

    private sealed class ThrowingTotalsService : IExpirationsPremiumTotalsSheetService
    {
        public void AddTotalsSheet(
            string destinationWorkbookPath,
            ExpirationsPremiumTotalsPlan plan,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Fallo controlado de postprocesamiento.");
    }

    private sealed class NoOpTotalsService : IExpirationsPremiumTotalsSheetService
    {
        public void AddTotalsSheet(
            string destinationWorkbookPath,
            ExpirationsPremiumTotalsPlan plan,
            CancellationToken cancellationToken = default)
        {
        }
    }
}
