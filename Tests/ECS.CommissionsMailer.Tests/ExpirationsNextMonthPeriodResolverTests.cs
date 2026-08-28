using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsNextMonthPeriodResolverTests
{
    private static readonly Guid Special = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Standard = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void RelevantExpirationDatesDetermineTheSingleWorkbookPeriod()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "automatic-period.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);

        var period = new ExpirationsNextMonthPeriodResolver().Resolve(
            Context(source, [2U, 3U, 4U, 6U], [5U]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2026, period.Year);
        Assert.Equal(8, period.Month);
        Assert.Equal("2026-08", period.FileToken);
    }

    [Fact]
    public void SharedPolicyRowsAreReadOnceWhenDeterminingThePeriod()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "shared-policy-period.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        var context = Context(source, [2U], [2U]);

        var period = new ExpirationsNextMonthPeriodResolver().Resolve(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal("2026-08", period.FileToken);
    }

    [Fact]
    public void DifferentRelevantExpirationMonthsBlockAutomaticPeriodSelection()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "mixed-period.xlsx");
        ExpirationsFelixTestWorkbook.Create(source);
        SetExpirationDate(source, "F5", new DateTime(2026, 9, 2));

        var exception = Assert.Throws<ExpirationsGenerationException>(() =>
            new ExpirationsNextMonthPeriodResolver().Resolve(
                Context(source, [2U], [5U]),
                TestContext.Current.CancellationToken));

        Assert.Contains("no tienen un único período", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026-08", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2026-09", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidRelevantExpirationDateBlocksAutomaticPeriodSelection()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "invalid-period.xlsx");
        ExpirationsFelixTestWorkbook.Create(source, fault: "invalid-date");

        var exception = Assert.Throws<ExpirationsGenerationException>(() =>
            new ExpirationsNextMonthPeriodResolver().Resolve(
                Context(source, [2U], []),
                TestContext.Current.CancellationToken));

        Assert.Contains("Fila 2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Fecha Hasta", exception.Message, StringComparison.Ordinal);
    }

    private static ExpirationsGenerationContext Context(
        string source,
        IReadOnlyList<uint> specialRows,
        IReadOnlyList<uint> standardRows)
    {
        var read = new ExpirationsWorkbookReader().Read(source);
        var workbook = Assert.IsType<ExpirationsSourceWorkbook>(read.Workbook);
        var rows = new Dictionary<Guid, IReadOnlyList<uint>>();
        if (specialRows.Count > 0)
            rows[Special] = specialRows;
        if (standardRows.Count > 0)
            rows[Standard] = standardRows;
        return new ExpirationsGenerationContext(
            ExpirationsProcess.NextMonth,
            source,
            new GeneratedFileHashService().ComputeSha256(source),
            workbook,
            new ExpirationsWorkbookAnalysisResult
            {
                ReadStatus = ExpirationsWorkbookReadStatus.Success,
                TotalRows = rows.Values.SelectMany(value => value).Distinct().Count(),
                ResolvedRows = rows.Values.SelectMany(value => value).Distinct().Count(),
                CanGenerate = true,
                ResolvedRowNumbersByBroker = rows
            },
            rows.Keys.Select((brokerId, index) => new ExpirationsBrokerCatalogItem
            {
                BrokerId = brokerId,
                Name = $"Corredor {index + 1}",
                IsActive = true
            }).ToArray());
    }

    private static void SetExpirationDate(string source, string cellReference, DateTime value)
    {
        using var document = SpreadsheetDocument.Open(source, true);
        var worksheet = document.WorkbookPart!.WorksheetParts.Single().Worksheet ??
            throw new InvalidDataException("Worksheet sintético faltante.");
        var cell = worksheet.Descendants<Cell>().Single(item => item.CellReference == cellReference);
        cell.RemoveAllChildren();
        cell.DataType = CellValues.Number;
        cell.CellValue = new CellValue(value.ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture));
        worksheet.Save();
    }
}
