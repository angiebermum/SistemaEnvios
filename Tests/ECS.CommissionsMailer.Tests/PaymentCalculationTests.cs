using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class PaymentCalculationTests
{
    private readonly PaymentCalculationService _service = new();

    [Fact]
    public void AppliesGrossAndPayableDeductionsInTheCorrectOrderForCrc()
    {
        var analysis = Analysis(crcAmount: 100_000m, hasCrc: true, usdAmount: 0m, hasUsd: false);
        var deductions = new[]
        {
            Deduction("Ajuste", 10_000m, DeductionCurrency.CRC, DeductionApplicationType.GrossCommission),
            Deduction("Ahorro", 15_000m, DeductionCurrency.CRC, DeductionApplicationType.PayableAmount),
            Deduction("Préstamo", 20_000m, DeductionCurrency.CRC, DeductionApplicationType.PayableAmount)
        };

        var result = _service.Calculate(analysis, deductions).Crc;

        Assert.True(result.IsValid);
        Assert.Equal(90_000m, result.AdjustedGrossCommission);
        Assert.Equal(11_700m, result.Vat);
        Assert.Equal(101_700m, result.InvoiceAmount);
        Assert.Equal(1_800m, result.Withholding);
        Assert.Equal(64_900m, result.DepositedAmount);
    }

    [Fact]
    public void CalculatesUsdIndependently()
    {
        var analysis = Analysis(0m, false, 100m, true);
        var deductions = new[]
        {
            Deduction("Ajuste", 10m, DeductionCurrency.USD, DeductionApplicationType.GrossCommission),
            Deduction("Adelanto", 20m, DeductionCurrency.USD, DeductionApplicationType.PayableAmount)
        };

        var result = _service.Calculate(analysis, deductions);

        Assert.Equal(0m, result.Crc.DepositedAmount);
        Assert.Equal(90m, result.Usd.AdjustedGrossCommission);
        Assert.Equal(11.70m, result.Usd.Vat);
        Assert.Equal(1.80m, result.Usd.Withholding);
        Assert.Equal(79.90m, result.Usd.DepositedAmount);
    }

    [Fact]
    public void AppliesOnlyDeductionsAssignedToTheAnalyzedWorksheet()
    {
        var deductions = new[]
        {
            Deduction(
                "Ahorro AQO",
                10_000m,
                DeductionCurrency.CRC,
                DeductionApplicationType.PayableAmount,
                "AQO"),
            Deduction(
                "Ahorro AQM",
                20_000m,
                DeductionCurrency.CRC,
                DeductionApplicationType.PayableAmount,
                "AQM (HC)")
        };

        var analysis = Analysis(100_000m, true, 0m, false);
        analysis.WorksheetName = "AQO";
        var result = _service.Calculate(analysis, deductions).Crc;

        var applied = Assert.Single(result.Deductions);
        Assert.Equal("Ahorro AQO", applied.Description);
        Assert.Equal("AQO", applied.TargetWorksheetName);
        Assert.Equal(101_000m, result.DepositedAmount);
    }

    [Theory]
    [InlineData(14999.99, true)]
    [InlineData(15000, false)]
    public void AppliesCrcMinimumAfterGrossDeductions(double adjustedAmount, bool shouldZero)
    {
        var original = 20_000m;
        var deduction = original - (decimal)adjustedAmount;

        var result = _service.Calculate(
            Analysis(original, true, 0m, false),
            [Deduction("Ajuste", deduction, DeductionCurrency.CRC, DeductionApplicationType.GrossCommission)]).Crc;

        Assert.Equal(shouldZero, result.MinimumApplied);
        Assert.Equal(shouldZero ? 0m : (decimal)adjustedAmount, result.AdjustedGrossCommission);
    }

    [Theory]
    [InlineData(29.99, true)]
    [InlineData(30, false)]
    public void AppliesUsdMinimumAfterGrossDeductions(double adjustedAmount, bool shouldZero)
    {
        var original = 50m;
        var deduction = original - (decimal)adjustedAmount;

        var result = _service.Calculate(
            Analysis(0m, false, original, true),
            [Deduction("Ajuste", deduction, DeductionCurrency.USD, DeductionApplicationType.GrossCommission)]).Usd;

        Assert.Equal(shouldZero, result.MinimumApplied);
        Assert.Equal(shouldZero ? 0m : (decimal)adjustedAmount, result.AdjustedGrossCommission);
    }

    [Fact]
    public void CurrencyWithoutCommissionKeepsEveryFinancialAmountAndAppliedDeductionAtZero()
    {
        var result = _service.Calculate(
            Analysis(100_000m, true, 0m, false),
            [Deduction("Préstamo", 25m, DeductionCurrency.USD, DeductionApplicationType.PayableAmount)]).Usd;

        Assert.False(result.HasCommission);
        Assert.Equal(0m, result.GrossCommissionOriginal);
        Assert.Equal(0m, result.GrossDeductions);
        Assert.Equal(0m, result.AdjustedGrossCommission);
        Assert.Equal(0m, result.Vat);
        Assert.Equal(0m, result.InvoiceAmount);
        Assert.Equal(0m, result.Withholding);
        Assert.Equal(0m, result.FinalDeductions);
        Assert.Equal(0m, result.DepositedAmount);
        Assert.Single(result.Deductions);
        Assert.Equal(0m, result.Deductions[0].AppliedAmount);
    }

    [Fact]
    public void PayableDeductionDoesNotReduceVatOrWithholdingBase()
    {
        var withoutDeduction = _service.Calculate(Analysis(100_000m, true, 0m, false), []).Crc;
        var withDeduction = _service.Calculate(
            Analysis(100_000m, true, 0m, false),
            [Deduction("Ahorro", 50_000m, DeductionCurrency.CRC, DeductionApplicationType.PayableAmount)]).Crc;

        Assert.Equal(withoutDeduction.Vat, withDeduction.Vat);
        Assert.Equal(withoutDeduction.Withholding, withDeduction.Withholding);
        Assert.Equal(withoutDeduction.DepositedAmount - 50_000m, withDeduction.DepositedAmount);
    }

    [Fact]
    public void NegativeDepositedAmountIsBlocking()
    {
        var result = _service.Calculate(
            Analysis(15_000m, true, 0m, false),
            [Deduction("Adelanto", 20_000m, DeductionCurrency.CRC, DeductionApplicationType.PayableAmount)]).Crc;

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, value => value.Contains("negativo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NegativeSourceCommissionIsWarnedAndZeroedWithoutCurrencyConversion()
    {
        var result = _service.Calculate(Analysis(-10m, true, 100m, true), []);

        Assert.True(result.IsValid);
        Assert.Equal(0m, result.Crc.DepositedAmount);
        Assert.Equal(
            "Comisión negativa: no procede el pago ni la facturación.",
            result.Crc.Observation);
        Assert.NotEmpty(result.Crc.Warnings);
        Assert.Equal(113m - 2m, result.Usd.DepositedAmount);
    }

    [Fact]
    public void RoundsAwayFromZeroToTwoDecimals()
    {
        var result = _service.Calculate(Analysis(15_000.005m, true, 0m, false), []).Crc;

        Assert.Equal(15_000.01m, result.GrossCommissionOriginal);
        Assert.Equal(1_950m, result.Vat);
    }

    private static BrokerDeduction Deduction(
        string description,
        decimal amount,
        DeductionCurrency currency,
        DeductionApplicationType type,
        string targetWorksheetName = "SYN") => new()
    {
        Description = description,
        Amount = amount,
        Currency = currency,
        ApplicationType = type,
        TargetWorksheetName = targetWorksheetName
    };

    private static CommissionWorksheetAnalysis Analysis(
        decimal crcAmount,
        bool hasCrc,
        decimal usdAmount,
        bool hasUsd) => new()
    {
        WorksheetName = "SYN",
        Crc = new CommissionCurrencySummary
        {
            Currency = DeductionCurrency.CRC,
            HasCommission = hasCrc,
            GrossCommission = crcAmount
        },
        Usd = new CommissionCurrencySummary
        {
            Currency = DeductionCurrency.USD,
            HasCommission = hasUsd,
            GrossCommission = usdAmount
        }
    };
}
