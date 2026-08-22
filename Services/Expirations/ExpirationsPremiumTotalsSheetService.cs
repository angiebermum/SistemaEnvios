using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsPremiumTotalsSheetService
{
    void AddTotalsSheet(
        string destinationWorkbookPath,
        ExpirationsPremiumTotalsPlan plan,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsPremiumTotalsSheetService : IExpirationsPremiumTotalsSheetService
{
    public const string TotalsSheetName = "Total de primas";
    public const string CrcLabel = "Total de primas en colones";
    public const string UsdLabel = "Total de primas en dólares";

    public void AddTotalsSheet(
        string destinationWorkbookPath,
        ExpirationsPremiumTotalsPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationWorkbookPath);
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        using var document = SpreadsheetDocument.Open(Path.GetFullPath(destinationWorkbookPath), true);
        var workbookPart = document.WorkbookPart ??
            throw new ExpirationsGenerationException("El archivo generado no contiene un workbook válido.");
        var workbook = workbookPart.Workbook ??
            throw new ExpirationsGenerationException("El archivo generado no contiene la definición del workbook.");
        var sheets = workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
        if (sheets.Count != 1 ||
            !string.Equals(sheets[0].Name?.Value, plan.WorksheetName, StringComparison.Ordinal))
        {
            throw new ExpirationsGenerationException("El archivo estándar no contiene exactamente la hoja de datos esperada.");
        }
        if (string.Equals(plan.WorksheetName, TotalsSheetName, StringComparison.OrdinalIgnoreCase))
            throw new ExpirationsGenerationException("La hoja de datos ya se llama 'Total de primas'.");
        if (sheets.Any(sheet => string.Equals(sheet.Name?.Value, TotalsSheetName, StringComparison.OrdinalIgnoreCase)))
            throw new ExpirationsGenerationException("El workbook ya contiene una hoja llamada 'Total de primas'.");

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(
            new Columns(
                new Column { Min = 1U, Max = 1U, Width = 32D, CustomWidth = true },
                new Column { Min = 2U, Max = 2U, Width = 20D, CustomWidth = true }),
            new SheetData(
                new Row(
                    InlineTextCell("A1", CrcLabel),
                    FormulaCell("B1", plan.CrcFormula)) { RowIndex = 1U },
                new Row(
                    InlineTextCell("A2", UsdLabel),
                    FormulaCell("B2", plan.UsdFormula)) { RowIndex = 2U }));
        worksheetPart.Worksheet.Save();

        var nextSheetId = checked(sheets.Max(sheet => sheet.SheetId?.Value ?? 0U) + 1U);
        workbook.Sheets!.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = nextSheetId,
            Name = TotalsSheetName
        });

        var calculation = workbook.CalculationProperties ?? new CalculationProperties();
        calculation.CalculationMode = CalculateModeValues.Auto;
        calculation.CalculationOnSave = true;
        calculation.ForceFullCalculation = true;
        calculation.FullCalculationOnLoad = true;
        if (calculation.Parent is null)
            workbook.Append(calculation);
        if (workbookPart.CalculationChainPart is not null)
            workbookPart.DeletePart(workbookPart.CalculationChainPart);
        workbook.Save();
    }

    private static Cell InlineTextCell(string reference, string value) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })
    };

    private static Cell FormulaCell(string reference, string formula) => new()
    {
        CellReference = reference,
        CellFormula = new CellFormula(formula)
    };
}
