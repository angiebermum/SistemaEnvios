using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsNextMonthGenerationPreflightService
{
    ExpirationsNextMonthPreflightResult Validate(
        ExpirationsGenerationContext context,
        ExpirationsPeriod? period,
        ExpirationsPremiumColumnOptions? premiumColumnOptions = null,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsNextMonthGenerationPreflightService(
    IExpirationsPremiumColumnsService? premiumColumnsService = null,
    IExpirationsPremiumDataInspectionService? premiumDataInspectionService = null,
    IExpirationsPremiumTotalsPlanner? premiumTotalsPlanner = null,
    IExpirationsFelixSourceMappingService? felixSourceMappingService = null)
    : IExpirationsNextMonthGenerationPreflightService
{
    public const string MissingSpecialConfigurationMessage =
        "No hay un corredor configurado con el formato especial de mes siguiente. " +
        "Revise la configuración de corredores antes de generar.";

    private readonly IExpirationsPremiumColumnsService _premiumColumnsService =
        premiumColumnsService ?? new ExpirationsPremiumColumnsService();
    private readonly IExpirationsPremiumDataInspectionService _premiumDataInspectionService =
        premiumDataInspectionService ?? new ExpirationsPremiumDataInspectionService();
    private readonly IExpirationsPremiumTotalsPlanner _premiumTotalsPlanner =
        premiumTotalsPlanner ?? new ExpirationsPremiumTotalsPlanner();
    private readonly IExpirationsFelixSourceMappingService _felixSourceMappingService =
        felixSourceMappingService ?? new ExpirationsFelixSourceMappingService();

    public ExpirationsNextMonthPreflightResult Validate(
        ExpirationsGenerationContext context,
        ExpirationsPeriod? period,
        ExpirationsPremiumColumnOptions? premiumColumnOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Process != ExpirationsProcess.NextMonth)
            throw new ExpirationsGenerationException("El preflight corresponde únicamente al mes siguiente.");
        if (period is null)
            throw new ExpirationsGenerationException("Seleccione un mes y año válidos para generar el mes siguiente.");

        var specialBrokers = context.BrokerCatalog
            .Where(broker => broker.NextMonthGenerationMode ==
                             ExpirationsNextMonthGenerationMode.SpecialDualSorted)
            .GroupBy(broker => broker.BrokerId)
            .Select(group => group.Single())
            .ToList();
        if (specialBrokers.Count == 0)
            throw new ExpirationsGenerationException(MissingSpecialConfigurationMessage);
        if (specialBrokers.Count > 1)
        {
            throw new ExpirationsGenerationException(
                "Existe más de un corredor configurado con el formato especial de mes siguiente.");
        }

        var columns = _premiumColumnsService.Resolve(
            context.SourceWorkbookPath,
            context.SourceWorkbook.WorksheetName,
            context.SourceWorkbook.HeaderRowNumber,
            premiumColumnOptions);
        DemandResolvedColumns(columns);

        foreach (var target in context.Analysis.ResolvedRowNumbersByBroker)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRows = target.Value.Distinct().Order().ToArray();
            if (sourceRows.Length == 0)
                continue;
            var inspections = _premiumDataInspectionService.Inspect(
                context.SourceWorkbookPath,
                context.SourceWorkbook.WorksheetName,
                sourceRows,
                columns.PremiumColumnReference,
                columns.CurrencyColumnReference,
                cancellationToken);
            _ = _premiumTotalsPlanner.CreatePlan(
                context.SourceWorkbook.WorksheetName,
                context.SourceWorkbook.HeaderRowNumber,
                sourceRows,
                columns,
                inspections);
        }

        var special = specialBrokers.Single();
        ExpirationsFelixGenerationPlan? felixPlan = null;
        if (context.Analysis.ResolvedRowNumbersByBroker.TryGetValue(special.BrokerId, out var specialRows) &&
            specialRows.Count > 0)
        {
            felixPlan = _felixSourceMappingService.CreatePlan(
                context,
                period,
                specialRows.Distinct().Order().ToArray(),
                columns,
                cancellationToken);
        }

        return new ExpirationsNextMonthPreflightResult
        {
            PremiumColumns = columns,
            SpecialBrokerId = special.BrokerId,
            FelixPlan = felixPlan
        };
    }

    private static void DemandResolvedColumns(ExpirationsPremiumColumnResolution resolution)
    {
        if (resolution.IsSuccess)
            return;
        var message = resolution.Status switch
        {
            ExpirationsPremiumColumnResolutionStatus.WorksheetNotFound =>
                "La hoja analizada ya no existe en el archivo.",
            ExpirationsPremiumColumnResolutionStatus.HeaderRowNotFound =>
                "La fila de encabezado analizada ya no existe en el archivo.",
            ExpirationsPremiumColumnResolutionStatus.MissingPremiumColumn =>
                "No se encontró una columna con encabezado Prima.",
            ExpirationsPremiumColumnResolutionStatus.MissingCurrencyColumn =>
                "No se encontró una columna con encabezado Moneda.",
            ExpirationsPremiumColumnResolutionStatus.AmbiguousPremiumColumn =>
                "Existen varias columnas con encabezado Prima.",
            ExpirationsPremiumColumnResolutionStatus.AmbiguousCurrencyColumn =>
                "Existen varias columnas con encabezado Moneda.",
            ExpirationsPremiumColumnResolutionStatus.InvalidPremiumColumnOverride =>
                "La selección de la columna Prima no existe en el encabezado.",
            ExpirationsPremiumColumnResolutionStatus.InvalidCurrencyColumnOverride =>
                "La selección de la columna Moneda no existe en el encabezado.",
            ExpirationsPremiumColumnResolutionStatus.ConflictingColumnOverrides =>
                "Prima y Moneda deben usar columnas distintas.",
            _ => "No fue posible resolver las columnas Prima y Moneda."
        };
        throw new ExpirationsPremiumColumnResolutionException(message, resolution);
    }
}
