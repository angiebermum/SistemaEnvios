using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public sealed class PaymentCalculationService
{
    public const decimal CrcMinimum = 15_000m;
    public const decimal UsdMinimum = 30m;
    public const decimal VatRate = 0.13m;
    public const decimal WithholdingRate = 0.02m;

    public PaymentCalculationResult Calculate(
        CommissionWorksheetAnalysis analysis,
        IEnumerable<BrokerDeduction> deductions)
    {
        var ordered = deductions
            .OrderBy(value => value.DisplayOrder)
            .ThenBy(value => value.Description, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new PaymentCalculationResult
        {
            Crc = CalculateCurrency(analysis.Crc, ordered, CrcMinimum),
            Usd = CalculateCurrency(analysis.Usd, ordered, UsdMinimum)
        };
    }

    private static PaymentCurrencyCalculation CalculateCurrency(
        CommissionCurrencySummary summary,
        IReadOnlyList<BrokerDeduction> deductions,
        decimal minimum)
    {
        var result = new PaymentCurrencyCalculation
        {
            Currency = summary.Currency,
            HasCommission = summary.HasCommission,
            MinimumAmount = minimum,
            GrossCommissionOriginal = Round(summary.GrossCommission)
        };
        var matching = deductions.Where(value => value.Currency == summary.Currency).ToList();
        result.Deductions = matching.Select(value => new AppliedDeductionSnapshot
        {
            Id = value.Id,
            Description = value.Description,
            ConfiguredAmount = Round(value.Amount),
            AppliedAmount = 0m,
            Currency = value.Currency,
            ApplicationType = value.ApplicationType,
            DisplayOrder = value.DisplayOrder
        }).ToList();

        if (!summary.HasCommission)
        {
            result.Observation = "Sin comisiones en esta moneda.";
            result.Warnings.Add($"No existen comisiones en {summary.Currency}; el bloque se muestra en cero.");
            if (matching.Count > 0)
            {
                result.Warnings.Add(
                    $"Hay {matching.Count} rebajo(s) configurado(s) para {summary.Currency}, pero su importe aplicado es 0.00.");
            }

            return result;
        }

        if (summary.GrossCommission < 0)
        {
            result.Observation = "Comisión negativa: no se paga en esta moneda.";
            result.Warnings.Add(
                $"La comisión original en {summary.Currency} es negativa. El bloque financiero se muestra en cero.");
            return result;
        }

        var grossDeductionSnapshots = result.Deductions
            .Where(value => value.ApplicationType == DeductionApplicationType.GrossCommission)
            .ToList();
        var grossDeductions = Round(grossDeductionSnapshots.Sum(value => value.ConfiguredAmount));
        var adjusted = Round(result.GrossCommissionOriginal - grossDeductions);
        if (adjusted < 0)
        {
            result.Errors.Add(
                $"Los rebajos al monto bruto de {summary.Currency} superan la comisión original " +
                $"({grossDeductions:N2} > {result.GrossCommissionOriginal:N2}).");
            return result;
        }

        if (adjusted < minimum)
        {
            result.MinimumApplied = true;
            result.Observation = "No se paga: comisión inferior al mínimo establecido.";
            result.Warnings.Add(
                $"La comisión ajustada en {summary.Currency} ({adjusted:N2}) es inferior al mínimo ({minimum:N2}); " +
                "el bloque financiero se muestra en cero.");
            return result;
        }

        foreach (var deduction in result.Deductions)
        {
            deduction.AppliedAmount = deduction.ConfiguredAmount;
        }

        result.GrossDeductions = grossDeductions;
        result.AdjustedGrossCommission = adjusted;
        result.Vat = Round(adjusted * VatRate);
        result.InvoiceAmount = Round(adjusted + result.Vat);
        result.Withholding = Round(adjusted * WithholdingRate);
        result.PayableBeforeFinalDeductions = Round(result.InvoiceAmount - result.Withholding);
        result.FinalDeductions = Round(result.Deductions
            .Where(value => value.ApplicationType == DeductionApplicationType.PayableAmount)
            .Sum(value => value.AppliedAmount));
        result.DepositedAmount = Round(result.PayableBeforeFinalDeductions - result.FinalDeductions);
        if (result.DepositedAmount < 0)
        {
            result.Errors.Add(
                $"Los rebajos al monto a pagar de {summary.Currency} producen un monto depositado negativo " +
                $"({result.DepositedAmount:N2}). Corrija los rebajos antes de generar.");
        }

        return result;
    }

    public static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
