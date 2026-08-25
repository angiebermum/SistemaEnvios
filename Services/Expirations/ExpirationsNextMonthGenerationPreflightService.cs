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

        var rowsByDestination = context.Analysis.ResolvedRowNumbersByDestination.Count > 0
            ? context.Analysis.ResolvedRowNumbersByDestination
            : context.Analysis.ResolvedRowNumbersByBroker.ToDictionary(
                item => new ExpirationsDestinationKey(item.Key, ExpirationsDestinationGroup.Principal),
                item => item.Value);
        var targetRows = rowsByDestination
            .Select(target => new
            {
                target.Key,
                Rows = target.Value.Distinct().Order().ToArray()
            })
            .Where(target => target.Rows.Length > 0)
            .ToList();
        var allSourceRows = targetRows
            .SelectMany(target => target.Rows)
            .Distinct()
            .Order()
            .ToArray();
        var allInspections = allSourceRows.Length == 0
            ? []
            : _premiumDataInspectionService.Inspect(
                context.SourceWorkbookPath,
                context.SourceWorkbook.WorksheetName,
                allSourceRows,
                columns.PremiumColumnReference,
                columns.CurrencyColumnReference,
                cancellationToken);
        var inspectionsByRow = allInspections.ToDictionary(row => row.RowNumber);
        var totalsPlans = new Dictionary<ExpirationsDestinationKey, ExpirationsPremiumTotalsPlan>();
        foreach (var target in targetRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inspections = target.Rows.Select(rowNumber => inspectionsByRow[rowNumber]).ToArray();
            totalsPlans[target.Key] = _premiumTotalsPlanner.CreatePlan(
                ExpirationsNextMonthStandardWorkbookGenerator.DataSheetName,
                context.SourceWorkbook.HeaderRowNumber,
                target.Rows,
                columns,
                inspections);
        }

        var special = specialBrokers.Single();
        ExpirationsFelixGenerationPlan? felixPlan = null;
        var specialRows = rowsByDestination
            .Where(item => item.Key.BrokerId == special.BrokerId)
            .SelectMany(item => item.Value)
            .Distinct()
            .Order()
            .ToArray();
        if (specialRows.Length > 0)
        {
            felixPlan = _felixSourceMappingService.CreatePlan(
                context,
                period,
                specialRows,
                columns,
                cancellationToken);
        }

        return new ExpirationsNextMonthPreflightResult
        {
            PremiumColumns = columns,
            PremiumTotalsPlansByBrokerId = totalsPlans
                .Where(item => item.Key.DestinationGroup == ExpirationsDestinationGroup.Principal)
                .ToDictionary(item => item.Key.BrokerId, item => item.Value),
            PremiumTotalsPlansByDestination = totalsPlans,
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
