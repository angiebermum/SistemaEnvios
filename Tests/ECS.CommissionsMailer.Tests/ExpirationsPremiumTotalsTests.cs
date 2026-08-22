using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsPremiumTotalsTests
{
    [Theory]
    [InlineData("CRC", ExpirationsCurrency.Crc)]
    [InlineData(" crc ", ExpirationsCurrency.Crc)]
    [InlineData("Colón", ExpirationsCurrency.Crc)]
    [InlineData("COLONES", ExpirationsCurrency.Crc)]
    [InlineData("Colones   costarricenses", ExpirationsCurrency.Crc)]
    [InlineData("₡", ExpirationsCurrency.Crc)]
    [InlineData("USD", ExpirationsCurrency.Usd)]
    [InlineData(" usd ", ExpirationsCurrency.Usd)]
    [InlineData("Dólar", ExpirationsCurrency.Usd)]
    [InlineData("DÓLARES", ExpirationsCurrency.Usd)]
    [InlineData("Dólar estadounidense", ExpirationsCurrency.Usd)]
    [InlineData("US$", ExpirationsCurrency.Usd)]
    [InlineData("$", ExpirationsCurrency.Usd)]
    [InlineData("EUR", ExpirationsCurrency.Unsupported)]
    [InlineData("CAD", ExpirationsCurrency.Unsupported)]
    [InlineData("MONEDA DESCONOCIDA", ExpirationsCurrency.Unsupported)]
    [InlineData("", ExpirationsCurrency.Unsupported)]
    [InlineData("   ", ExpirationsCurrency.Unsupported)]
    public void CurrencyClassifierRecognizesOnlyExplicitLabels(
        string raw,
        ExpirationsCurrency expected)
    {
        Assert.Equal(expected, new ExpirationsCurrencyClassifier().Classify(raw));
    }

    [Theory]
    [InlineData(8, 9, 12)]
    [InlineData(3, 18, 20)]
    [InlineData(18, 3, 20)]
    [InlineData(33, 35, 36)]
    public void ResolvesPremiumAndCurrencyAtDynamicPositions(
        int premiumIndex,
        int currencyIndex,
        int columnCount)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "headers.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            source,
            "Reporte",
            7,
            ExpirationsPremiumTestWorkbook.Headers(columnCount, premiumIndex, currencyIndex),
            premiumIndex,
            currencyIndex,
            []);

        var result = new ExpirationsPremiumColumnsService().Resolve(source, "Reporte", 7);

        Assert.True(result.IsSuccess);
        Assert.Equal(premiumIndex, result.PremiumColumnIndex);
        Assert.Equal(ExpirationsPremiumTestWorkbook.ColumnName(premiumIndex), result.PremiumColumnReference);
        Assert.Equal(currencyIndex, result.CurrencyColumnIndex);
        Assert.Equal(ExpirationsPremiumTestWorkbook.ColumnName(currencyIndex), result.CurrencyColumnReference);
    }

    [Theory]
    [InlineData("Prima", "Moneda")]
    [InlineData("PRIMA", "MONEDA")]
    [InlineData(" Príma ", " Monéda ")]
    [InlineData("Prima:", "[Moneda]")]
    public void HeaderNormalizationIsCaseAccentSpaceAndMinorPunctuationInsensitive(
        string premiumHeader,
        string currencyHeader)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "normalized.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            source,
            "Reporte",
            4,
            ["Broker", premiumHeader, currencyHeader, "Privado"],
            2,
            3,
            []);

        var result = new ExpirationsPremiumColumnsService().Resolve(source, "Reporte", 4);

        Assert.True(result.IsSuccess);
        Assert.Equal("B", result.PremiumColumnReference);
        Assert.Equal("C", result.CurrencyColumnReference);
    }

    [Fact]
    public void MissingAndAmbiguousHeadersReturnExplicitStatuses()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var service = new ExpirationsPremiumColumnsService();

        var missingPremium = Path.Combine(directory.Path, "missing-premium.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            missingPremium, "Reporte", 1, ["Broker", "Monto", "Moneda"], 2, 3, []);
        Assert.Equal(
            ExpirationsPremiumColumnResolutionStatus.MissingPremiumColumn,
            service.Resolve(missingPremium, "Reporte", 1).Status);

        var missingCurrency = Path.Combine(directory.Path, "missing-currency.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            missingCurrency, "Reporte", 1, ["Broker", "Prima", "Divisa"], 2, 3, []);
        Assert.Equal(
            ExpirationsPremiumColumnResolutionStatus.MissingCurrencyColumn,
            service.Resolve(missingCurrency, "Reporte", 1).Status);

        var twoPremium = Path.Combine(directory.Path, "two-premium.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            twoPremium, "Reporte", 1, ["Broker", "Prima", "Moneda", "PRÍMA"], 2, 3, []);
        Assert.Equal(
            ExpirationsPremiumColumnResolutionStatus.AmbiguousPremiumColumn,
            service.Resolve(twoPremium, "Reporte", 1).Status);

        var twoCurrency = Path.Combine(directory.Path, "two-currency.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            twoCurrency, "Reporte", 1, ["Broker", "Prima", "Moneda", "MONÉDA"], 2, 3, []);
        Assert.Equal(
            ExpirationsPremiumColumnResolutionStatus.AmbiguousCurrencyColumn,
            service.Resolve(twoCurrency, "Reporte", 1).Status);
    }

    [Fact]
    public void ValidOverridesUseExistingHeaderColumnsAndInventedIndexesAreRejected()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "overrides.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            source, "Reporte", 1, ["Broker", "Monto", "Divisa", "Privado"], 2, 3, []);
        var service = new ExpirationsPremiumColumnsService();

        var valid = service.Resolve(
            source,
            "Reporte",
            1,
            new ExpirationsPremiumColumnOptions(PremiumColumnIndex: 2, CurrencyColumnIndex: 3));
        var invalid = service.Resolve(
            source,
            "Reporte",
            1,
            new ExpirationsPremiumColumnOptions(PremiumColumnIndex: 30, CurrencyColumnIndex: 3));

        Assert.True(valid.IsSuccess);
        Assert.Equal("B", valid.PremiumColumnReference);
        Assert.Equal("C", valid.CurrencyColumnReference);
        Assert.Equal(ExpirationsPremiumColumnResolutionStatus.InvalidPremiumColumnOverride, invalid.Status);
    }

    [Fact]
    public void NumericIntegerDecimalZeroAndNegativePremiumsAreValidOpenXmlNumbers()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "numeric.xlsx");
        var rows = new[]
        {
            new PremiumTestRow(2, "100", "CRC", "A"),
            new PremiumTestRow(3, "25.50", "CRC", "B"),
            new PremiumTestRow(4, "0", "USD", "C"),
            new PremiumTestRow(5, "-20", "USD", "D")
        };
        ExpirationsPremiumTestWorkbook.Create(
            source, "Reporte", 1, ["Broker", "Prima", "Moneda", "Privado"], 2, 3, rows);
        var columns = new ExpirationsPremiumColumnsService().Resolve(source, "Reporte", 1);
        var inspections = new ExpirationsPremiumDataInspectionService().Inspect(
            source, "Reporte", [2, 3, 4, 5], "B", "C", TestContext.Current.CancellationToken);

        var plan = new ExpirationsPremiumTotalsPlanner().CreatePlan(
            "Reporte", 1, [2, 3, 4, 5], columns, inspections);

        Assert.All(inspections, row => Assert.Equal(ExpirationsPremiumCellKind.Numeric, row.PremiumCellKind));
        Assert.Equal(105.5D, inspections.Sum(row => row.NumericPremiumValue!.Value), 6);
        Assert.Equal(4, plan.RowCount);
        Assert.Equal((uint)2, plan.DataFirstRow);
        Assert.Equal((uint)5, plan.DataLastRow);
    }

    [Theory]
    [InlineData(PremiumTestStorage.Missing, "", "vacía")]
    [InlineData(PremiumTestStorage.SharedString, "100", "numérico de Excel")]
    [InlineData(PremiumTestStorage.InlineString, "100", "numérico de Excel")]
    [InlineData(PremiumTestStorage.SharedString, "N/A", "numérico de Excel")]
    [InlineData(PremiumTestStorage.Formula, "100", "fórmula")]
    public void MissingTextAndFormulaPremiumsBlockBeforeDestinationIsCreated(
        PremiumTestStorage storage,
        string value,
        string expectedMessage)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, $"premium-{storage}.xlsx");
        var output = Path.Combine(directory.Path, $"premium-{storage}-output.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            source,
            "Reporte",
            1,
            ["Broker", "Prima", "Moneda", "Privado"],
            2,
            3,
            [new PremiumTestRow(2, value, "CRC", "SECRETO", storage)]);

        var exception = Assert.Throws<ExpirationsGenerationException>(() =>
            new ExpirationsNextMonthStandardWorkbookGenerator().Generate(
                new ExpirationsNextMonthStandardWorkbookGenerationRequest(
                    source, output, "Reporte", 1, [2]),
                TestContext.Current.CancellationToken));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }
}
