using System.IO.Compression;
using System.Diagnostics;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsNextMonthBatchGenerationTests
{
    private static readonly Guid Special = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Standard = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherSpecial = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly ExpirationsPeriod Period = new(2026, 8);

    [Fact]
    public async Task NextMonthPerformanceMeasurementUsesFixedThreeFileFixture()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "performance-next-month.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        var context = Context(source, [2U, 3U, 4U, 6U], [5U]);
        var service = new ExpirationsGenerationService();
        _ = await service.GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path, Period),
            cancellationToken: TestContext.Current.CancellationToken);
        var measurements = new List<long>();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            var batch = await service.GenerateAsync(
                new ExpirationsGenerationRequest(context, directory.Path, Period),
                cancellationToken: TestContext.Current.CancellationToken);
            stopwatch.Stop();
            Assert.Equal(3, batch.Files.Count);
            measurements.Add(stopwatch.ElapsedMilliseconds);
        }
        measurements.Sort();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"PERFORMANCE_NEXTMONTH_MEDIAN_MS={measurements[measurements.Count / 2]};" +
            $"FILES=3;SAMPLES={string.Join(',', measurements)}");
    }

    [Fact]
    public async Task PreflightInspectsPremiumsOnceAndReusesEveryBrokerTotalsPlan()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "single-premium-inspection.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        var columns = new CountingPremiumColumnsService();
        var inspections = new CountingPremiumInspectionService();
        var planner = new CountingPremiumTotalsPlanner();
        var preflight = new ExpirationsNextMonthGenerationPreflightService(
            columns,
            inspections,
            planner);
        var standardGenerator = new ExpirationsNextMonthStandardWorkbookGenerator(
            premiumColumnsService: columns,
            premiumDataInspectionService: inspections,
            premiumTotalsPlanner: planner);
        var service = new ExpirationsGenerationService(
            nextMonthWorkbookGenerator: standardGenerator,
            nextMonthPreflightService: preflight);

        var batch = await service.GenerateAsync(
            new ExpirationsGenerationRequest(
                Context(source, [2U, 3U, 4U, 6U], [5U]),
                directory.Path,
                Period),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, batch.Files.Count);
        Assert.Equal(1, columns.Calls);
        Assert.Equal(1, inspections.Calls);
        Assert.Equal(2, planner.Calls);
    }

    [Fact]
    public async Task ReusedPlansAndLegacyRecalculationProduceEquivalentLogicalWorkbooks()
    {
        using var optimizedDirectory = new ExpirationsGenerationTestDirectory();
        using var legacyDirectory = new ExpirationsGenerationTestDirectory();
        var optimizedSource = Path.Combine(optimizedDirectory.Path, "equivalent.xlsx");
        var legacySource = Path.Combine(legacyDirectory.Path, "equivalent.xlsx");
        ExpirationsFelixTestWorkbook.Create(optimizedSource);
        File.Copy(optimizedSource, legacySource);
        var optimized = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(
                Context(optimizedSource, [2U, 3U, 4U, 6U], [5U]),
                optimizedDirectory.Path,
                Period),
            cancellationToken: TestContext.Current.CancellationToken);
        var legacy = await new ExpirationsGenerationService(
            nextMonthPreflightService: new TotalsPlanStrippingPreflightService()).GenerateAsync(
            new ExpirationsGenerationRequest(
                Context(legacySource, [2U, 3U, 4U, 6U], [5U]),
                legacyDirectory.Path,
                Period),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, optimized.Files.Count);
        foreach (var optimizedFile in optimized.Files)
        {
            var legacyFile = legacy.Files.Single(file =>
                file.BrokerId == optimizedFile.BrokerId && file.Variant == optimizedFile.Variant);
            Assert.Equal(optimizedFile.SourceRowNumbers, legacyFile.SourceRowNumbers);
            Assert.Equal(DetailPolicies(optimizedFile.OutputPath), DetailPolicies(legacyFile.OutputPath));
            Assert.Equal(TotalFormulas(optimizedFile.OutputPath), TotalFormulas(legacyFile.OutputPath));
            Assert.Empty(Validate(optimizedFile.OutputPath));
            Assert.Empty(Validate(legacyFile.OutputPath));
        }
    }

    [Fact]
    public async Task MixedBatchCreatesOneStandardAndExactlyTwoSpecialFilesAtomically()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "next-month.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        var context = Context(source, [2U, 3U, 4U, 6U], [5U]);

        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path, Period),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsProcess.NextMonth, batch.Process);
        Assert.Equal(3, batch.Files.Count);
        Assert.Equal(2, batch.Files.Select(file => file.BrokerId).Distinct().Count());
        var standard = Assert.Single(batch.Files, file => file.BrokerId == Standard);
        Assert.Equal(ExpirationsGeneratedFileVariant.Standard, standard.Variant);
        Assert.Contains("Vencimientos mes siguiente - Corredor normal - 2026-08.xlsx", standard.OutputPath);
        Assert.Empty(Validate(standard.OutputPath));
        using (var standardDocument = SpreadsheetDocument.Open(standard.OutputPath, false))
        {
            var standardWorkbook = standardDocument.WorkbookPart?.Workbook ??
                throw new InvalidDataException("Workbook sintético inválido.");
            Assert.Equal(["Detalle", "Total de primas"],
                (standardWorkbook.Sheets ?? throw new InvalidDataException("Sheets sintéticas inválidas."))
                .Elements<Sheet>().Select(sheet => sheet.Name!.Value));
        }
        var standardXml = ZipXml(standard.OutputPath);
        Assert.Contains("SECRETO_OTRO", standardXml, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETO_FELIX", standardXml, StringComparison.Ordinal);
        var special = batch.Files.Where(file => file.BrokerId == Special).OrderBy(file => file.Variant).ToArray();
        Assert.Equal(2, special.Length);
        Assert.Equal(
            [ExpirationsGeneratedFileVariant.FelixAlphabetical, ExpirationsGeneratedFileVariant.FelixExpirationDate],
            special.Select(file => file.Variant));
        Assert.All(special, file =>
        {
            Assert.Equal(4, file.RowCount);
            Assert.Equal([2U, 3U, 4U, 6U], file.SourceRowNumbers);
            Assert.True(File.Exists(file.OutputPath));
            Assert.Empty(Validate(file.OutputPath));
            using var document = SpreadsheetDocument.Open(file.OutputPath, false);
            Assert.Equal(["Detalle", "Total de primas"],
                document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().Select(sheet => sheet.Name!.Value));
        });
        Assert.DoesNotContain(batch.Files, file =>
            string.Equals(
                Path.GetFileName(file.OutputPath),
                "Vencimientos mes siguiente - Excepción configurada - 2026-08.xlsx",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ORDENADO ALFABETICAMENTE", special[0].OutputPath, StringComparison.Ordinal);
        Assert.Contains("ORDENADO POR VENCIMIENTO", special[1].OutputPath, StringComparison.Ordinal);
        Assert.StartsWith("Vencimientos mes siguiente - 2026-08 - ", Path.GetFileName(batch.OutputDirectory));
    }

    [Fact]
    public async Task SpecialVariantsUseVerifiedSortsSamePoliciesTotalsAndPrivacy()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "sorts.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(Context(source, [2U, 3U, 4U, 6U], [5U]), directory.Path, Period),
            cancellationToken: TestContext.Current.CancellationToken);
        var alphabetical = batch.Files.Single(file =>
            file.Variant == ExpirationsGeneratedFileVariant.FelixAlphabetical);
        var expiration = batch.Files.Single(file =>
            file.Variant == ExpirationsGeneratedFileVariant.FelixExpirationDate);

        Assert.Equal(["Álvaro", "Álvaro", "Beatriz", "Carlos"], DetailText(alphabetical.OutputPath, "B"));
        Assert.Equal([3, 3, 15, 20], DetailDates(expiration.OutputPath).Select(date => date.Day));
        Assert.Equal(["POL-ALVARO-1", "POL-ALVARO-2"], DetailPolicies(alphabetical.OutputPath).Take(2));
        Assert.Equal(DetailPolicies(alphabetical.OutputPath).Order(), DetailPolicies(expiration.OutputPath).Order());
        Assert.Equal(TotalFormulas(alphabetical.OutputPath), TotalFormulas(expiration.OutputPath));
        Assert.All(new[] { alphabetical.OutputPath, expiration.OutputPath }, path =>
        {
            var xml = ZipXml(path);
            Assert.Contains("SECRETO_FELIX", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRETO_OTRO", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("OBSERVACION_PRIVADA", xml, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task SpecialWorkbookReproducesVerifiedFunctionalLayoutAndStyles()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "layout.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(Context(source, [2U, 3U, 4U, 6U], [5U]), directory.Path, Period),
            cancellationToken: TestContext.Current.CancellationToken);
        var path = batch.Files.Single(file =>
            file.Variant == ExpirationsGeneratedFileVariant.FelixAlphabetical).OutputPath;
        var qaOutputPath = Environment.GetEnvironmentVariable("ECS_FELIX_QA_OUTPUT");
        if (!string.IsNullOrWhiteSpace(qaOutputPath))
            File.Copy(path, qaOutputPath, overwrite: true);

        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("Workbook sintético inválido.");
        var workbook = workbookPart.Workbook ?? throw new InvalidDataException("Workbook sintético inválido.");
        var sheets = (workbook.Sheets ?? throw new InvalidDataException("Sheets sintéticas inválidas."))
            .Elements<Sheet>().ToArray();
        Assert.Equal(["Detalle", "Total de primas"], sheets.Select(sheet => sheet.Name!.Value));
        var worksheet = ((WorksheetPart)workbookPart.GetPartById(sheets[0].Id!.Value!)).Worksheet ??
            throw new InvalidDataException("Worksheet sintética inválida.");
        var rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();
        Assert.Equal("Vencimientos Mes de\nAgosto, 2026", rows[0].Elements<Cell>().First().InlineString!.InnerText);
        Assert.Equal("ORDENADO ALFABETICAMENTE", rows[0].Elements<Cell>().Single(cell => cell.CellReference == "F1").InlineString!.InnerText);
        Assert.Equal(
            ExpirationsFelixTemplateDefinition.VisibleColumns.Select(column => column.TargetHeader),
            rows[1].Elements<Cell>().Take(7).Select(cell => cell.InlineString!.InnerText));
        Assert.Equal(["A1:E1", "F1:G1"], worksheet.Elements<MergeCells>().Single().Elements<MergeCell>()
            .Select(cell => cell.Reference!.Value));
        var columns = worksheet.GetFirstChild<Columns>()!.Elements<Column>().ToArray();
        Assert.Equal([31.11D, 76.78D, 20.11D, 18.78D, 18.67D, 13.11D, 20.78D],
            columns.Take(7).Select(column => column.Width!.Value), new DoublePrecisionComparer(0.001));
        Assert.True(columns[7].Hidden!.Value);
        Assert.Equal("Moneda", rows[1].Elements<Cell>().Single(cell => cell.CellReference == "H2").InlineString!.InnerText);
        var insRow = rows.First(row => row.Elements<Cell>().Any(cell => cell.InlineString?.InnerText == "INS"));
        var otherRow = rows.Single(row => row.Elements<Cell>().Any(cell => cell.InlineString?.InnerText == "LAFISE"));
        Assert.Equal(9U, insRow.Elements<Cell>().Single(cell => cell.CellReference!.Value!.StartsWith('A')).StyleIndex!.Value);
        Assert.Equal(9U, insRow.Elements<Cell>().Single(cell => cell.CellReference!.Value!.StartsWith('C')).StyleIndex!.Value);
        Assert.Equal(10U, otherRow.Elements<Cell>().Single(cell => cell.CellReference!.Value!.StartsWith('A')).StyleIndex!.Value);
        Assert.Equal(10U, otherRow.Elements<Cell>().Single(cell => cell.CellReference!.Value!.StartsWith('C')).StyleIndex!.Value);
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet ??
            throw new InvalidDataException("Stylesheet sintético inválido.");
        var fills = stylesheet.Fills ?? throw new InvalidDataException("Fills sintéticos inválidos.");
        var numberFormats = stylesheet.NumberingFormats ??
            throw new InvalidDataException("Formatos sintéticos inválidos.");
        Assert.Equal("FFFFC000", fills.Elements<Fill>().ElementAt(2).PatternFill!.ForegroundColor!.Rgb!.Value);
        Assert.Equal("FF00B0F0", fills.Elements<Fill>().ElementAt(3).PatternFill!.ForegroundColor!.Rgb!.Value);
        Assert.Equal("d/m/yyyy", numberFormats.Elements<NumberingFormat>().Single(format => format.NumberFormatId?.Value == 164U).FormatCode!.Value);
        Assert.Equal("#,##0.00", numberFormats.Elements<NumberingFormat>().Single(format => format.NumberFormatId?.Value == 165U).FormatCode!.Value);
        Assert.All(TotalFormulas(path), formula =>
        {
            Assert.Contains("$E:$E", formula, StringComparison.Ordinal);
            Assert.Contains("$H:$H", formula, StringComparison.Ordinal);
            Assert.DoesNotContain("$E$3:$E$6", formula, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ExactlyOneSpecialIsRequiredButSpecialWithoutRowsCreatesNoEmptyFiles()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "special-config.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        var noSpecial = Context(source, [2U], [5U], specialMode: ExpirationsNextMonthGenerationMode.Standard);
        var missing = await Assert.ThrowsAsync<ExpirationsGenerationException>(() =>
            new ExpirationsGenerationService().GenerateAsync(
                new ExpirationsGenerationRequest(noSpecial, directory.Path, Period),
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(
            ExpirationsNextMonthGenerationPreflightService.MissingSpecialConfigurationMessage,
            missing.Message);
        Assert.Empty(Directory.EnumerateDirectories(directory.Path));

        var twoSpecial = Context(
            source,
            [2U],
            [5U],
            specialMode: ExpirationsNextMonthGenerationMode.SpecialDualSorted,
            secondSpecial: true);
        await Assert.ThrowsAsync<ExpirationsGenerationException>(() =>
            new ExpirationsGenerationService().GenerateAsync(
                new ExpirationsGenerationRequest(twoSpecial, directory.Path, Period),
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateDirectories(directory.Path));

        var withoutRows = Context(source, [], [5U]);
        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(withoutRows, directory.Path, Period),
            cancellationToken: TestContext.Current.CancellationToken);
        var file = Assert.Single(batch.Files);
        Assert.Equal(Standard, file.BrokerId);
        Assert.Equal(ExpirationsGeneratedFileVariant.Standard, file.Variant);

        withoutRows.BrokerCatalog.Single(broker => broker.BrokerId == Special).IsActive = false;
        var inactiveWithoutRowsBatch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(withoutRows, directory.Path, Period),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Standard, Assert.Single(inactiveWithoutRowsBatch.Files).BrokerId);
    }

    [Fact]
    public async Task MissingTemplateFieldFailsPreflightBeforeCreatingStaging()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "missing-template.xlsx");
        ExpirationsFelixTestWorkbook.Create(source, omitPlateHeader: true);

        var exception = await Assert.ThrowsAsync<ExpirationsGenerationException>(() =>
            new ExpirationsGenerationService().GenerateAsync(
                new ExpirationsGenerationRequest(Context(source, [2U], [5U]), directory.Path, Period),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Placa/Folio", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(directory.Path));
        Assert.Equal([Path.GetFileName(source)], Directory.EnumerateFiles(directory.Path).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("invalid-date", "no es un valor numérico")]
    [InlineData("formula-date", "Vencimiento contiene una fórmula")]
    [InlineData("empty-insurer", "Aseguradora está vacío")]
    [InlineData("unknown-currency", "no está reconocida")]
    [InlineData("text-premium", "no es un valor numérico")]
    [InlineData("standard-text-premium", "Fila 5")]
    [InlineData("duplicate-policy-header", "duplicado")]
    public async Task InvalidNextMonthDataFailsPreflightWithoutCreatingOutput(
        string fault,
        string expectedMessage)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, $"{fault}.xlsx");
        ExpirationsFelixTestWorkbook.Create(source, fault: fault);

        var exception = await Assert.ThrowsAsync<ExpirationsGenerationException>(() =>
            new ExpirationsGenerationService().GenerateAsync(
                new ExpirationsGenerationRequest(Context(source, [2U], [5U]), directory.Path, Period),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(directory.Path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailureInEitherSpecialVariantRollsBackWholeMixedBatch(int failAtCall)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "rollback-special.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        var service = new ExpirationsGenerationService(
            felixWorkbookGenerator: new FailingFelixGenerator(failAtCall));

        await Assert.ThrowsAsync<ExpirationsGenerationException>(() => service.GenerateAsync(
            new ExpirationsGenerationRequest(Context(source, [2U, 3U], [5U]), directory.Path, Period),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateDirectories(directory.Path));
        Assert.Equal([Path.GetFileName(source)], Directory.EnumerateFiles(directory.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task FailureAddingSpecialTotalsRollsBackWholeMixedBatch()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "rollback-special-totals.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        var service = new ExpirationsGenerationService(
            felixWorkbookGenerator: new ExpirationsFelixWorkbookGenerator(
                premiumTotalsSheetService: new ThrowingTotalsSheetService()));

        await Assert.ThrowsAsync<ExpirationsGenerationException>(() => service.GenerateAsync(
            new ExpirationsGenerationRequest(Context(source, [2U], [5U]), directory.Path, Period),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateDirectories(directory.Path));
        Assert.Equal([Path.GetFileName(source)], Directory.EnumerateFiles(directory.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task SpecialOpenXmlValidationFailureRollsBackWholeMixedBatch()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "rollback-special-validation.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        var service = new ExpirationsGenerationService(
            felixWorkbookGenerator: new ExpirationsFelixWorkbookGenerator(
                premiumTotalsSheetService: new CorruptingTotalsSheetService()));

        var exception = await Assert.ThrowsAsync<ExpirationsGenerationException>(() => service.GenerateAsync(
            new ExpirationsGenerationRequest(Context(source, [2U], [5U]), directory.Path, Period),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("OpenXML válido", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(directory.Path));
    }

    [Fact]
    public async Task ManualPremiumAndCurrencyColumnsRetryPreflightForCurrentSession()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "manual-premium-columns.xlsx");
        ExpirationsFelixTestWorkbook.Create(source, fault: "manual-headers");
        var context = Context(source, [2U], [5U]);
        var service = new ExpirationsGenerationService();

        var automatic = await Assert.ThrowsAsync<ExpirationsPremiumColumnResolutionException>(() =>
            service.GenerateAsync(
                new ExpirationsGenerationRequest(context, directory.Path, Period),
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(
            ExpirationsPremiumColumnResolutionStatus.MissingPremiumColumn,
            automatic.Resolution.Status);
        Assert.Empty(Directory.EnumerateDirectories(directory.Path));

        var batch = await service.GenerateAsync(
            new ExpirationsGenerationRequest(
                context,
                directory.Path,
                Period,
                new ExpirationsPremiumColumnOptions(7, 8)),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, batch.Files.Count);
    }

    [Fact]
    public async Task ChangedSourceHashBlocksNextMonthBeforePreflightWritesAnything()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "changed-next.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        var context = Context(source, [2U], [5U]);
        File.AppendAllText(source, "changed-after-analysis");

        var exception = await Assert.ThrowsAsync<ExpirationsGenerationException>(() =>
            new ExpirationsGenerationService().GenerateAsync(
                new ExpirationsGenerationRequest(context, directory.Path, Period),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("cambió después del análisis", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(directory.Path));
    }

    [Fact]
    public async Task SharedPolicyIsCompleteInEachDestinationAndRepeatedAssociationsDoNotTripleCount()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "shared-policy.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        var context = Context(source, [2U, 2U, 2U], [2U]);

        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path, Period),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, batch.Files.Count);
        Assert.All(batch.Files, file =>
        {
            Assert.Equal(1, file.RowCount);
            Assert.Equal([2U], file.SourceRowNumbers);
        });
        Assert.All(batch.Files.Where(file => file.BrokerId == Special), file =>
            Assert.Equal(["POL-CARLOS"], DetailPolicies(file.OutputPath)));
        var standardFile = batch.Files.Single(file => file.BrokerId == Standard);
        Assert.Contains("POL-CARLOS", ZipXml(standardFile.OutputPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingSpecialEmailWarnsOnBothFilesButDoesNotBlockGeneration()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "missing-email.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        var context = Context(source, [2U], [5U]);
        context.BrokerCatalog.Single(broker => broker.BrokerId == Special).PrimaryEmailAddresses = [];

        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path, Period),
            cancellationToken: TestContext.Current.CancellationToken);

        var specialFiles = batch.Files.Where(file => file.BrokerId == Special).ToArray();
        Assert.Equal(2, specialFiles.Length);
        Assert.All(specialFiles, file => Assert.Single(file.Warnings));
        Assert.Single(batch.Warnings);
        Assert.Contains("no tiene correo principal válido", batch.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviousMonthIgnoresSpecialModeAndRemainsSingleStandardFile()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "previous-special.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source);
        var broker = Broker(Special, "Excepción configurada", ExpirationsNextMonthGenerationMode.SpecialDualSorted);
        var context = ExpirationsGenerationTestWorkbook.Context(
            source,
            ExpirationsProcess.PreviousMonth,
            [broker],
            new Dictionary<Guid, IReadOnlyList<uint>> { [Special] = [8U] });

        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path),
            cancellationToken: TestContext.Current.CancellationToken);

        var file = Assert.Single(batch.Files);
        Assert.Equal(ExpirationsGeneratedFileVariant.Standard, file.Variant);
        Assert.StartsWith("Pendientes mes anterior - ", Path.GetFileName(file.OutputPath));
        using var document = SpreadsheetDocument.Open(file.OutputPath, false);
        var workbook = document.WorkbookPart?.Workbook ?? throw new InvalidDataException("Workbook inválido.");
        Assert.Single((workbook.Sheets ?? throw new InvalidDataException("Sheets inválidas.")).Elements<Sheet>());
    }

    [Fact]
    public void NextMonthFileNamesResolveSanitizedCollisionsDeterministically()
    {
        var service = new ExpirationsFileNameService();
        var names = service.CreateNextMonthFileNames(Period,
        [
            new(Special, " Juan  Pérez:*? ", ExpirationsGeneratedFileVariant.Standard),
            new(Standard, "juan pérez---", ExpirationsGeneratedFileVariant.Standard),
            new(Special, " Juan  Pérez:*? ", ExpirationsGeneratedFileVariant.FelixAlphabetical),
            new(Special, new string('X', 300), ExpirationsGeneratedFileVariant.FelixExpirationDate)
        ]);

        Assert.Equal(4, names.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(Special.ToString("N")[..8],
            names[new(Special, ExpirationsGeneratedFileVariant.Standard)], StringComparison.Ordinal);
        Assert.EndsWith(" - ORDENADO ALFABETICAMENTE.xlsx",
            names[new(Special, ExpirationsGeneratedFileVariant.FelixAlphabetical)], StringComparison.Ordinal);
        Assert.EndsWith(" - ORDENADO POR VENCIMIENTO.xlsx",
            names[new(Special, ExpirationsGeneratedFileVariant.FelixExpirationDate)], StringComparison.Ordinal);
        Assert.All(names.Values, name => Assert.True(name.Length <= 180));
    }

    private static ExpirationsGenerationContext Context(
        string source,
        IReadOnlyList<uint> specialRows,
        IReadOnlyList<uint> standardRows,
        ExpirationsNextMonthGenerationMode specialMode = ExpirationsNextMonthGenerationMode.SpecialDualSorted,
        bool secondSpecial = false)
    {
        var read = new ExpirationsWorkbookReader().Read(source);
        var sourceWorkbook = Assert.IsType<ExpirationsSourceWorkbook>(read.Workbook);
        var rows = new Dictionary<Guid, IReadOnlyList<uint>>();
        if (specialRows.Count > 0)
            rows[Special] = specialRows;
        if (standardRows.Count > 0)
            rows[Standard] = standardRows;
        var catalog = new List<ExpirationsBrokerCatalogItem>
        {
            Broker(Special, "Excepción configurada", specialMode),
            Broker(Standard, "Corredor normal", ExpirationsNextMonthGenerationMode.Standard)
        };
        if (secondSpecial)
            catalog.Add(Broker(OtherSpecial, "Otra excepción", ExpirationsNextMonthGenerationMode.SpecialDualSorted));
        var uniqueRows = rows.Values.SelectMany(value => value).Distinct().Count();
        return new ExpirationsGenerationContext(
            ExpirationsProcess.NextMonth,
            source,
            new GeneratedFileHashService().ComputeSha256(source),
            sourceWorkbook,
            new ExpirationsWorkbookAnalysisResult
            {
                ReadStatus = ExpirationsWorkbookReadStatus.Success,
                TotalRows = uniqueRows,
                ResolvedRows = uniqueRows,
                CanGenerate = true,
                ResolvedRowNumbersByBroker = rows
            },
            catalog);
    }

    private static ExpirationsBrokerCatalogItem Broker(
        Guid id,
        string name,
        ExpirationsNextMonthGenerationMode mode) => new()
    {
        BrokerId = id,
        Name = name,
        PrimaryEmailAddresses = [$"{id:N}@example.test"],
        IsActive = true,
        NextMonthGenerationMode = mode
    };

    private static IReadOnlyList<string> Validate(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return new OpenXmlValidator().Validate(document, TestContext.Current.CancellationToken)
            .Select(error => error.Description)
            .ToArray();
    }

    private static IReadOnlyList<string> DetailText(string path, string column)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return DetailRows(document).Select(row => row.Elements<Cell>().Single(cell =>
            cell.CellReference!.Value!.StartsWith(column, StringComparison.Ordinal)).InlineString!.InnerText).ToArray();
    }

    private static IReadOnlyList<string> DetailPolicies(string path) => DetailText(path, "A");

    private static IReadOnlyList<DateTime> DetailDates(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return DetailRows(document).Select(row => DateTime.FromOADate(double.Parse(
            row.Elements<Cell>().Single(cell => cell.CellReference!.Value!.StartsWith("D", StringComparison.Ordinal))
                .CellValue!.InnerText,
            System.Globalization.CultureInfo.InvariantCulture))).ToArray();
    }

    private static IReadOnlyList<Row> DetailRows(SpreadsheetDocument document)
    {
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("Workbook inválido.");
        var workbook = workbookPart.Workbook ?? throw new InvalidDataException("Workbook inválido.");
        var sheet = (workbook.Sheets ?? throw new InvalidDataException("Sheets inválidas."))
            .Elements<Sheet>().First();
        var worksheet = ((WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet ??
            throw new InvalidDataException("Worksheet inválida.");
        return worksheet
            .GetFirstChild<SheetData>()!.Elements<Row>().Where(row => row.RowIndex!.Value >= 3U).ToArray();
    }

    private static IReadOnlyList<string> TotalFormulas(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("Workbook inválido.");
        var workbook = workbookPart.Workbook ?? throw new InvalidDataException("Workbook inválido.");
        var sheet = (workbook.Sheets ?? throw new InvalidDataException("Sheets inválidas."))
            .Elements<Sheet>().Single(item => item.Name == "Total de primas");
        var worksheet = ((WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet ??
            throw new InvalidDataException("Worksheet inválida.");
        return worksheet
            .Descendants<CellFormula>().Select(formula => formula.Text).ToArray();
    }

    private static string ZipXml(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return string.Join("\n", archive.Entries
            .Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(entry =>
            {
                using var reader = new StreamReader(entry.Open());
                return reader.ReadToEnd();
            }));
    }

    private sealed class CountingPremiumColumnsService : IExpirationsPremiumColumnsService
    {
        private readonly ExpirationsPremiumColumnsService _inner = new();
        public int Calls { get; private set; }

        public ExpirationsPremiumColumnResolution Resolve(
            string sourceWorkbookPath,
            string worksheetName,
            uint headerRowNumber,
            ExpirationsPremiumColumnOptions? options = null)
        {
            Calls++;
            return _inner.Resolve(sourceWorkbookPath, worksheetName, headerRowNumber, options);
        }
    }

    private sealed class CountingPremiumInspectionService : IExpirationsPremiumDataInspectionService
    {
        private readonly ExpirationsPremiumDataInspectionService _inner = new();
        public int Calls { get; private set; }

        public IReadOnlyList<ExpirationsPremiumRowInspection> Inspect(
            string sourceWorkbookPath,
            string worksheetName,
            IReadOnlyList<uint> sourceRowNumbers,
            string premiumColumnReference,
            string currencyColumnReference,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return _inner.Inspect(
                sourceWorkbookPath,
                worksheetName,
                sourceRowNumbers,
                premiumColumnReference,
                currencyColumnReference,
                cancellationToken);
        }
    }

    private sealed class CountingPremiumTotalsPlanner : IExpirationsPremiumTotalsPlanner
    {
        private readonly ExpirationsPremiumTotalsPlanner _inner = new();
        public int Calls { get; private set; }

        public ExpirationsPremiumTotalsPlan CreatePlan(
            string worksheetName,
            uint headerRowNumber,
            IReadOnlyList<uint> sourceRowNumbers,
            ExpirationsPremiumColumnResolution columns,
            IReadOnlyList<ExpirationsPremiumRowInspection> rows)
        {
            Calls++;
            return _inner.CreatePlan(worksheetName, headerRowNumber, sourceRowNumbers, columns, rows);
        }
    }

    private sealed class TotalsPlanStrippingPreflightService : IExpirationsNextMonthGenerationPreflightService
    {
        private readonly ExpirationsNextMonthGenerationPreflightService _inner = new();

        public ExpirationsNextMonthPreflightResult Validate(
            ExpirationsGenerationContext context,
            ExpirationsPeriod? period,
            ExpirationsPremiumColumnOptions? premiumColumnOptions = null,
            CancellationToken cancellationToken = default)
        {
            var result = _inner.Validate(context, period, premiumColumnOptions, cancellationToken);
            return new ExpirationsNextMonthPreflightResult
            {
                PremiumColumns = result.PremiumColumns,
                SpecialBrokerId = result.SpecialBrokerId,
                FelixPlan = result.FelixPlan
            };
        }
    }

    private sealed class FailingFelixGenerator(int failAtCall) : IExpirationsFelixWorkbookGenerator
    {
        private readonly ExpirationsFelixWorkbookGenerator _inner = new();
        private int _calls;

        public void Generate(
            ExpirationsFelixWorkbookGenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            _inner.Generate(request, cancellationToken);
            if (_calls == failAtCall)
                throw new ExpirationsGenerationException("Fallo sintético en variante especial.");
        }
    }

    private sealed class ThrowingTotalsSheetService : IExpirationsPremiumTotalsSheetService
    {
        public void AddTotalsSheet(
            string destinationWorkbookPath,
            ExpirationsPremiumTotalsPlan plan,
            CancellationToken cancellationToken = default) =>
            throw new ExpirationsGenerationException("Fallo sintético agregando Total de primas.");
    }

    private sealed class CorruptingTotalsSheetService : IExpirationsPremiumTotalsSheetService
    {
        private readonly ExpirationsPremiumTotalsSheetService _inner = new();

        public void AddTotalsSheet(
            string destinationWorkbookPath,
            ExpirationsPremiumTotalsPlan plan,
            CancellationToken cancellationToken = default)
        {
            _inner.AddTotalsSheet(destinationWorkbookPath, plan, cancellationToken);
            using var document = SpreadsheetDocument.Open(destinationWorkbookPath, true);
            var stylesPart = document.WorkbookPart?.WorkbookStylesPart ??
                throw new InvalidDataException("Styles sintéticos inválidos.");
            var stylesheet = stylesPart.Stylesheet ??
                throw new InvalidDataException("Stylesheet sintético inválido.");
            var color = (stylesheet.Fills ?? throw new InvalidDataException("Fills sintéticos inválidos."))
                .Elements<Fill>().ElementAt(2).PatternFill!.ForegroundColor!;
            color.Rgb = "FFC000";
            stylesheet.Save();
        }
    }

    private sealed class DoublePrecisionComparer(double precision) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= precision;
        public int GetHashCode(double obj) => 0;
    }
}

internal static class ExpirationsFelixTestWorkbook
{
    public const string WorksheetName = "Reporte";

    public static void Create(
        string path,
        bool omitPlateHeader = false,
        string? fault = null)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        var headers = new[]
        {
            "Corredor", "Aseguradora", "Número de Póliza",
            fault == "duplicate-policy-header" ? "Número de Póliza" : "Observación",
            "Nombre del Tomador", "Fecha Hasta",
            fault == "manual-headers" ? "Importe" : "Prima",
            fault == "manual-headers" ? "Divisa" : "Moneda",
            "Período Pago", omitPlateHeader ? "Campo sin placa" : "Placa/Folio"
        };
        var header = new Row { RowIndex = 1U };
        for (var index = 0; index < headers.Length; index++)
            header.Append(TextCell(index + 1, 1U, headers[index]));
        sheetData.Append(header);
        AddRow(sheetData, 2U, "Especial", "INS", "POL-CARLOS", "Carlos", 46254D, 1000D, "CRC", "ANUAL", "SECRETO_FELIX-C", "OBSERVACION_PRIVADA");
        AddRow(sheetData, 3U, "Especial", "LAFISE", "POL-ALVARO-1", "Álvaro", 46237D, 50D, "USD", "SEMESTRAL", "SECRETO_FELIX-A1", "OBSERVACION_PRIVADA");
        AddRow(sheetData, 4U, "Especial", "INS", "POL-BEATRIZ", "Beatriz", 46249D, 25D, "COLONES", "MENSUAL", "SECRETO_FELIX-B", "OBSERVACION_PRIVADA");
        AddRow(sheetData, 5U, "Normal", "INS", "POL-OTRO", "SECRETO_OTRO", 46244D, 500D, "CRC", "ANUAL", "PLACA-OTRO", "SECRETO_OTRO");
        AddRow(sheetData, 6U, "Especial", "MNK SEGUROS", "POL-ALVARO-2", "Álvaro", 46237D, 75D, "DOLARES", "TRIMESTRAL", "SECRETO_FELIX-A2", "OBSERVACION_PRIVADA");
        var faultRow = sheetData.Elements<Row>().Single(row => row.RowIndex!.Value == 2U);
        switch (fault)
        {
            case "invalid-date":
                ReplaceWithText(faultRow.Elements<Cell>().Single(cell => cell.CellReference == "F2"), "fecha inválida");
                break;
            case "formula-date":
                faultRow.Elements<Cell>().Single(cell => cell.CellReference == "F2").CellFormula =
                    new CellFormula("1+1");
                break;
            case "empty-insurer":
                ReplaceWithText(faultRow.Elements<Cell>().Single(cell => cell.CellReference == "B2"), string.Empty);
                break;
            case "unknown-currency":
                ReplaceWithText(faultRow.Elements<Cell>().Single(cell => cell.CellReference == "H2"), "EUR");
                break;
            case "text-premium":
                ReplaceWithText(faultRow.Elements<Cell>().Single(cell => cell.CellReference == "G2"), "mil");
                break;
            case "standard-text-premium":
                ReplaceWithText(
                    sheetData.Elements<Row>().Single(row => row.RowIndex!.Value == 5U)
                        .Elements<Cell>().Single(cell => cell.CellReference == "G5"),
                    "mil");
                break;
            case "duplicate-policy-header":
            case "manual-headers":
                break;
            case null:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }
        worksheetPart.Worksheet = new Worksheet(sheetData);
        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.Sheets!.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = WorksheetName
        });
        workbookPart.Workbook.Save();
    }

    private static void ReplaceWithText(Cell cell, string value)
    {
        cell.RemoveAllChildren();
        cell.DataType = CellValues.InlineString;
        cell.InlineString = new InlineString(new Text(value));
    }

    private static void AddRow(
        SheetData sheetData,
        uint rowNumber,
        string broker,
        string insurer,
        string policy,
        string name,
        double expiration,
        double premium,
        string currency,
        string paymentPeriod,
        string plate,
        string observation)
    {
        var row = new Row { RowIndex = rowNumber };
        row.Append(
            TextCell(1, rowNumber, broker),
            TextCell(2, rowNumber, insurer),
            TextCell(3, rowNumber, policy),
            TextCell(4, rowNumber, observation),
            TextCell(5, rowNumber, name),
            NumberCell(6, rowNumber, expiration),
            NumberCell(7, rowNumber, premium),
            TextCell(8, rowNumber, currency),
            TextCell(9, rowNumber, paymentPeriod),
            TextCell(10, rowNumber, plate));
        sheetData.Append(row);
    }

    private static Cell TextCell(int column, uint row, string value) => new()
    {
        CellReference = $"{ColumnName(column)}{row}",
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })
    };

    private static Cell NumberCell(int column, uint row, double value) => new()
    {
        CellReference = $"{ColumnName(column)}{row}",
        DataType = CellValues.Number,
        CellValue = new CellValue(value)
    };

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
