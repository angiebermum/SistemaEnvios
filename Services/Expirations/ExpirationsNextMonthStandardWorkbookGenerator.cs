using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsNextMonthStandardWorkbookGenerator
{
    void Generate(
        ExpirationsNextMonthStandardWorkbookGenerationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsNextMonthStandardWorkbookGenerator(
    IExpirationsStandardWorkbookGenerator? standardWorkbookGenerator = null,
    IExpirationsPremiumColumnsService? premiumColumnsService = null,
    IExpirationsPremiumDataInspectionService? premiumDataInspectionService = null,
    IExpirationsPremiumTotalsPlanner? premiumTotalsPlanner = null,
    IExpirationsPremiumTotalsSheetService? premiumTotalsSheetService = null)
    : IExpirationsNextMonthStandardWorkbookGenerator
{
    public const string DataSheetName = "Detalle";
    private readonly IExpirationsStandardWorkbookGenerator _standardWorkbookGenerator =
        standardWorkbookGenerator ?? new ExpirationsStandardWorkbookGenerator();
    private readonly IExpirationsPremiumColumnsService _premiumColumnsService =
        premiumColumnsService ?? new ExpirationsPremiumColumnsService();
    private readonly IExpirationsPremiumDataInspectionService _premiumDataInspectionService =
        premiumDataInspectionService ?? new ExpirationsPremiumDataInspectionService();
    private readonly IExpirationsPremiumTotalsPlanner _premiumTotalsPlanner =
        premiumTotalsPlanner ?? new ExpirationsPremiumTotalsPlanner();
    private readonly IExpirationsPremiumTotalsSheetService _premiumTotalsSheetService =
        premiumTotalsSheetService ?? new ExpirationsPremiumTotalsSheetService();

    public void Generate(
        ExpirationsNextMonthStandardWorkbookGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceWorkbookPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationWorkbookPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorksheetName);
        if (request.HeaderRowNumber == 0)
            throw new ExpirationsGenerationException("La fila de encabezado no es válida.");
        if (string.Equals(
                request.WorksheetName,
                ExpirationsPremiumTotalsSheetService.TotalsSheetName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ExpirationsGenerationException("La hoja de datos ya se llama 'Total de primas'.");
        }

        var sourceRows = request.SourceRowNumbers.Distinct().Order().ToArray();
        if (sourceRows.Length == 0)
            throw new ExpirationsGenerationException("No existen filas para materializar en el archivo del corredor.");
        if (sourceRows.Any(rowNumber => rowNumber <= request.HeaderRowNumber))
            throw new ExpirationsGenerationException("La distribución contiene una fila que no pertenece a la zona de datos.");

        var destinationPath = Path.GetFullPath(request.DestinationWorkbookPath);
        if (File.Exists(destinationPath))
            throw new IOException($"El archivo staged ya existe: '{destinationPath}'.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = request.PrecomputedTotalsPlan ?? CreateTotalsPlan(request, sourceRows, cancellationToken);
            DemandCompatiblePrecomputedPlan(plan, request.HeaderRowNumber, sourceRows.Length);

            _standardWorkbookGenerator.Generate(new ExpirationsStandardWorkbookGenerationRequest(
                request.SourceWorkbookPath,
                destinationPath,
                request.WorksheetName,
                request.HeaderRowNumber,
                sourceRows), cancellationToken);
            RenameDataWorksheet(destinationPath, request.WorksheetName);
            _premiumTotalsSheetService.AddTotalsSheet(destinationPath, plan, cancellationToken);
            ValidateGeneratedWorkbook(destinationPath, plan);
        }
        catch (OperationCanceledException)
        {
            TryDelete(destinationPath);
            throw;
        }
        catch (ExpirationsGenerationException)
        {
            TryDelete(destinationPath);
            throw;
        }
        catch (Exception exception)
        {
            TryDelete(destinationPath);
            throw new ExpirationsGenerationException(
                "No fue posible generar el archivo de Vencimientos del mes siguiente.",
                exception);
        }
    }

    private ExpirationsPremiumTotalsPlan CreateTotalsPlan(
        ExpirationsNextMonthStandardWorkbookGenerationRequest request,
        IReadOnlyList<uint> sourceRows,
        CancellationToken cancellationToken)
    {
        var columns = _premiumColumnsService.Resolve(
            request.SourceWorkbookPath,
            request.WorksheetName,
            request.HeaderRowNumber,
            request.PremiumColumnOptions);
        DemandResolvedColumns(columns);
        var inspectedRows = _premiumDataInspectionService.Inspect(
            request.SourceWorkbookPath,
            request.WorksheetName,
            sourceRows,
            columns.PremiumColumnReference,
            columns.CurrencyColumnReference,
            cancellationToken);
        return _premiumTotalsPlanner.CreatePlan(
            DataSheetName,
            request.HeaderRowNumber,
            sourceRows,
            columns,
            inspectedRows);
    }

    private static void DemandCompatiblePrecomputedPlan(
        ExpirationsPremiumTotalsPlan plan,
        uint headerRowNumber,
        int rowCount)
    {
        if (!string.Equals(plan.WorksheetName, DataSheetName, StringComparison.Ordinal) ||
            plan.HeaderRowNumber != headerRowNumber ||
            plan.RowCount != rowCount ||
            plan.DataFirstRow != headerRowNumber + 1 ||
            plan.DataLastRow != headerRowNumber + (uint)rowCount ||
            string.IsNullOrWhiteSpace(plan.PremiumColumnReference) ||
            string.IsNullOrWhiteSpace(plan.CurrencyColumnReference))
        {
            throw new ExpirationsGenerationException(
                "El plan de primas preparado no coincide con las filas del corredor.");
        }
    }

    private static void RenameDataWorksheet(string path, string expectedSourceName)
    {
        using var document = SpreadsheetDocument.Open(path, true);
        var workbook = document.WorkbookPart?.Workbook ??
            throw new ExpirationsGenerationException("El archivo generado no contiene la definición del workbook.");
        var sheets = workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
        if (sheets.Count != 1 ||
            !string.Equals(sheets[0].Name?.Value, expectedSourceName, StringComparison.Ordinal))
        {
            throw new ExpirationsGenerationException("El archivo estándar no contiene la hoja de datos esperada.");
        }
        sheets[0].Name = DataSheetName;
        workbook.Save();
    }

    private static void DemandResolvedColumns(ExpirationsPremiumColumnResolution resolution)
    {
        if (resolution.IsSuccess)
            return;
        var message = resolution.Status switch
        {
            ExpirationsPremiumColumnResolutionStatus.WorksheetNotFound => "La hoja analizada ya no existe en el archivo.",
            ExpirationsPremiumColumnResolutionStatus.HeaderRowNotFound => "La fila de encabezado analizada ya no existe en el archivo.",
            ExpirationsPremiumColumnResolutionStatus.MissingPremiumColumn => "No se encontró una columna con encabezado Prima.",
            ExpirationsPremiumColumnResolutionStatus.MissingCurrencyColumn => "No se encontró una columna con encabezado Moneda.",
            ExpirationsPremiumColumnResolutionStatus.AmbiguousPremiumColumn => "Existen varias columnas con encabezado Prima.",
            ExpirationsPremiumColumnResolutionStatus.AmbiguousCurrencyColumn => "Existen varias columnas con encabezado Moneda.",
            ExpirationsPremiumColumnResolutionStatus.InvalidPremiumColumnOverride => "El override de la columna Prima no existe en el encabezado.",
            ExpirationsPremiumColumnResolutionStatus.InvalidCurrencyColumnOverride => "El override de la columna Moneda no existe en el encabezado.",
            ExpirationsPremiumColumnResolutionStatus.ConflictingColumnOverrides => "Prima y Moneda no pueden usar la misma columna.",
            _ => "No fue posible resolver las columnas Prima y Moneda."
        };
        throw new ExpirationsGenerationException(message);
    }

    private static void ValidateGeneratedWorkbook(string path, ExpirationsPremiumTotalsPlan plan)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new ExpirationsGenerationException("El archivo generado está vacío.");
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart ??
            throw new ExpirationsGenerationException("El archivo generado no contiene un workbook válido.");
        var workbook = workbookPart.Workbook ??
            throw new ExpirationsGenerationException("El archivo generado no contiene la definición del workbook.");
        var sheets = workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
        if (sheets.Count != 2 ||
            !string.Equals(sheets[0].Name?.Value, plan.WorksheetName, StringComparison.Ordinal) ||
            !string.Equals(
                sheets[1].Name?.Value,
                ExpirationsPremiumTotalsSheetService.TotalsSheetName,
                StringComparison.Ordinal))
        {
            throw new ExpirationsGenerationException("El archivo NextMonth no contiene exactamente las dos hojas esperadas.");
        }

        var dataWorksheet = GetWorksheet(workbookPart, sheets[0]);
        var dataRows = dataWorksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToList() ?? [];
        var generatedDataRows = dataRows
            .Where(row => (row.RowIndex?.Value ?? 0U) > plan.HeaderRowNumber)
            .ToList();
        if (generatedDataRows.Count != plan.RowCount ||
            !generatedDataRows.Select(row => row.RowIndex!.Value).SequenceEqual(
                Enumerable.Range(0, plan.RowCount).Select(index => plan.DataFirstRow + (uint)index)))
        {
            throw new ExpirationsGenerationException("La hoja de datos no conserva las filas compactas esperadas.");
        }

        var totalsWorksheet = GetWorksheet(workbookPart, sheets[1]);
        var totalsRows = totalsWorksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToList() ?? [];
        if (totalsRows.Count != 3 ||
            totalsRows[0].RowIndex?.Value != 2U ||
            totalsRows[1].RowIndex?.Value != 3U ||
            totalsRows[2].RowIndex?.Value != 4U)
        {
            throw new ExpirationsGenerationException("La hoja Total de primas contiene filas inesperadas.");
        }
        var cells = totalsRows.SelectMany(row => row.Elements<Cell>()).ToList();
        var expectedReferences = new[] { "B2", "C2", "B3", "C3", "B4", "C4" };
        if (cells.Count != expectedReferences.Length ||
            !cells.Select(cell => cell.CellReference?.Value).SequenceEqual(expectedReferences))
        {
            throw new ExpirationsGenerationException("La hoja Total de primas contiene celdas inesperadas.");
        }
        if (!string.Equals(cells[0].InlineString?.InnerText, ExpirationsPremiumTotalsSheetService.Title, StringComparison.Ordinal) ||
            !string.Equals(cells[2].InlineString?.InnerText, ExpirationsPremiumTotalsSheetService.CrcLabel, StringComparison.Ordinal) ||
            !string.Equals(cells[4].InlineString?.InnerText, ExpirationsPremiumTotalsSheetService.UsdLabel, StringComparison.Ordinal) ||
            !string.Equals(cells[3].CellFormula?.Text, plan.CrcFormula, StringComparison.Ordinal) ||
            !string.Equals(cells[5].CellFormula?.Text, plan.UsdFormula, StringComparison.Ordinal) ||
            cells.Any(cell => cell.StyleIndex?.Value is null or 0U))
        {
            throw new ExpirationsGenerationException("La hoja Total de primas no contiene las etiquetas y fórmulas esperadas.");
        }

        var calculation = workbook.CalculationProperties;
        if (calculation?.CalculationMode?.Value != CalculateModeValues.Auto ||
            calculation.CalculationOnSave?.Value != true ||
            calculation.ForceFullCalculation?.Value != true ||
            calculation.FullCalculationOnLoad?.Value != true ||
            workbookPart.CalculationChainPart is not null)
        {
            throw new ExpirationsGenerationException("El workbook no quedó configurado para recalcular las primas.");
        }
    }

    private static Worksheet GetWorksheet(WorkbookPart workbookPart, Sheet sheet)
    {
        if (sheet.Id?.Value is not { Length: > 0 } relationshipId ||
            workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart ||
            worksheetPart.Worksheet is null)
        {
            throw new ExpirationsGenerationException("El archivo generado contiene una hoja inválida.");
        }
        return worksheetPart.Worksheet;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
        catch
        {
            // La capa batch puede volver a intentar limpiar su staging completo.
        }
    }
}
