using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsFelixWorkbookGenerator
{
    void Generate(
        ExpirationsFelixWorkbookGenerationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsFelixWorkbookGenerator(
    ExpirationsBrokerNormalizer? normalizer = null,
    IExpirationsPremiumTotalsPlanner? premiumTotalsPlanner = null,
    IExpirationsPremiumTotalsSheetService? premiumTotalsSheetService = null)
    : IExpirationsFelixWorkbookGenerator
{
    private const uint HeaderRowNumber = 2U;
    private const uint FirstDataRowNumber = 3U;
    private const uint PremiumColumnIndex = 5U;
    private const uint CurrencyColumnIndex = 8U;
    private readonly ExpirationsBrokerNormalizer _normalizer = normalizer ?? new ExpirationsBrokerNormalizer();
    private readonly IExpirationsPremiumTotalsPlanner _premiumTotalsPlanner =
        premiumTotalsPlanner ?? new ExpirationsPremiumTotalsPlanner();
    private readonly IExpirationsPremiumTotalsSheetService _premiumTotalsSheetService =
        premiumTotalsSheetService ?? new ExpirationsPremiumTotalsSheetService();

    public void Generate(
        ExpirationsFelixWorkbookGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationWorkbookPath);
        if (request.Plan.CanonicalRows.Count == 0)
            throw new ExpirationsGenerationException("No existen filas para crear el formato especial.");
        if (request.Plan.CanonicalRows.Select(row => row.SourceRowNumber).Distinct().Count() !=
            request.Plan.CanonicalRows.Count)
        {
            throw new ExpirationsGenerationException("El conjunto canónico del formato especial contiene filas duplicadas.");
        }

        var destinationPath = Path.GetFullPath(request.DestinationWorkbookPath);
        if (File.Exists(destinationPath))
            throw new IOException($"El archivo staged ya existe: '{destinationPath}'.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var orderedRows = OrderRows(request.Plan.CanonicalRows, request.Variant);
            CreateDetailWorkbook(destinationPath, request.Plan.Period, request.Variant, orderedRows);
            var generatedRowNumbers = Enumerable.Range(0, orderedRows.Count)
                .Select(index => FirstDataRowNumber + (uint)index)
                .ToArray();
            var inspections = orderedRows.Select((row, index) => new ExpirationsPremiumRowInspection
            {
                RowNumber = FirstDataRowNumber + (uint)index,
                RawCurrencyText = row.Currency,
                PremiumCellKind = ExpirationsPremiumCellKind.Numeric,
                RawPremiumText = row.Premium.ToString("R", CultureInfo.InvariantCulture),
                NumericPremiumValue = row.Premium
            }).ToArray();
            var plan = _premiumTotalsPlanner.CreatePlan(
                ExpirationsFelixTemplateDefinition.WorksheetName,
                HeaderRowNumber,
                generatedRowNumbers,
                new ExpirationsPremiumColumnResolution
                {
                    Status = ExpirationsPremiumColumnResolutionStatus.Success,
                    PremiumColumnIndex = (int)PremiumColumnIndex,
                    PremiumColumnReference = "E",
                    CurrencyColumnIndex = (int)CurrencyColumnIndex,
                    CurrencyColumnReference = "H"
                },
                inspections);
            _premiumTotalsSheetService.AddTotalsSheet(destinationPath, plan, cancellationToken);
            ValidateGeneratedWorkbook(destinationPath, orderedRows.Count, plan, cancellationToken);
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
                "No fue posible generar el archivo especial de mes siguiente.",
                exception);
        }
    }

    private IReadOnlyList<ExpirationsFelixRow> OrderRows(
        IReadOnlyList<ExpirationsFelixRow> rows,
        ExpirationsFelixWorkbookVariant variant) => variant switch
    {
        ExpirationsFelixWorkbookVariant.Alphabetical => rows
            .OrderBy(row => _normalizer.Normalize(row.InsuredName), StringComparer.Ordinal)
            .ThenBy(row => row.SourceRowNumber)
            .ToArray(),
        ExpirationsFelixWorkbookVariant.ExpirationDate => rows
            .OrderBy(row => row.ExpirationDateSerial)
            .ThenBy(row => row.SourceRowNumber)
            .ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(variant))
    };

    private static void CreateDetailWorkbook(
        string path,
        ExpirationsPeriod period,
        ExpirationsFelixWorkbookVariant variant,
        IReadOnlyList<ExpirationsFelixRow> rows)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = CreateStylesheet();
        stylesPart.Stylesheet.Save();

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        sheetData.Append(CreateTitleRow(period, variant));
        sheetData.Append(CreateHeaderRow());
        for (var index = 0; index < rows.Count; index++)
            sheetData.Append(CreateDataRow(FirstDataRowNumber + (uint)index, rows[index]));

        var lastRow = FirstDataRowNumber + (uint)rows.Count - 1U;
        worksheetPart.Worksheet = new Worksheet(
            new SheetDimension { Reference = $"A1:H{lastRow}" },
            new SheetViews(new SheetView
            {
                WorkbookViewId = 0U,
                ShowGridLines = true,
                ZoomScale = 85U,
                ZoomScaleNormal = 85U
            }),
            new Columns(
                VisibleColumn(1U, 31.11D),
                VisibleColumn(2U, 76.78D),
                VisibleColumn(3U, 20.11D),
                VisibleColumn(4U, 18.78D),
                VisibleColumn(5U, 18.67D),
                VisibleColumn(6U, 13.11D),
                VisibleColumn(7U, 20.78D),
                new Column { Min = 8U, Max = 8U, Width = 12D, CustomWidth = true, Hidden = true }),
            sheetData,
            new MergeCells(
                new MergeCell { Reference = "A1:E1" },
                new MergeCell { Reference = "F1:G1" }),
            new PrintOptions { HorizontalCentered = true },
            new PageMargins
            {
                Left = 0.7D,
                Right = 0.7D,
                Top = 0.75D,
                Bottom = 0.75D,
                Header = 0.3D,
                Footer = 0.3D
            },
            new PageSetup { Orientation = OrientationValues.Landscape });
        worksheetPart.Worksheet.Save();

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = ExpirationsFelixTemplateDefinition.WorksheetName
        });
        workbookPart.Workbook.Save();
    }

    private static Row CreateTitleRow(
        ExpirationsPeriod period,
        ExpirationsFelixWorkbookVariant variant)
    {
        var row = new Row { RowIndex = 1U, Height = 119.3D, CustomHeight = true };
        row.Append(InlineCell("A1", $"Vencimientos Mes de\n{period.SpanishMonthName}, {period.Year}", 1U));
        for (var column = 'B'; column <= 'E'; column++)
            row.Append(InlineCell($"{column}1", string.Empty, 1U));
        row.Append(InlineCell(
            "F1",
            variant == ExpirationsFelixWorkbookVariant.Alphabetical
                ? ExpirationsFelixTemplateDefinition.AlphabeticalTitle
                : ExpirationsFelixTemplateDefinition.ExpirationDateTitle,
            2U));
        row.Append(InlineCell("G1", string.Empty, 2U));
        row.Append(InlineCell("H1", string.Empty, 4U));
        return row;
    }

    private static Row CreateHeaderRow()
    {
        var row = new Row { RowIndex = HeaderRowNumber, Height = 46.8D, CustomHeight = true };
        for (var index = 0; index < ExpirationsFelixTemplateDefinition.VisibleColumns.Count; index++)
        {
            row.Append(InlineCell(
                $"{ColumnName(index + 1)}{HeaderRowNumber}",
                ExpirationsFelixTemplateDefinition.VisibleColumns[index].TargetHeader,
                3U));
        }
        row.Append(InlineCell("H2", ExpirationsFelixTemplateDefinition.HiddenCurrencyHeader, 3U));
        return row;
    }

    private static Row CreateDataRow(uint rowNumber, ExpirationsFelixRow value)
    {
        var fillStyle = value.IsIns ? 9U : 10U;
        var row = new Row { RowIndex = rowNumber, Height = 19.5D, CustomHeight = true };
        row.Append(InlineCell($"A{rowNumber}", value.PolicyNumber, fillStyle));
        row.Append(InlineCell($"B{rowNumber}", value.InsuredName, 4U));
        row.Append(InlineCell($"C{rowNumber}", value.Insurer, fillStyle));
        row.Append(NumberCell($"D{rowNumber}", value.ExpirationDateSerial, 6U));
        row.Append(NumberCell($"E{rowNumber}", value.Premium, 7U));
        row.Append(InlineCell($"F{rowNumber}", value.PaymentPeriod, 5U));
        row.Append(InlineCell($"G{rowNumber}", value.Plate, 8U));
        row.Append(InlineCell($"H{rowNumber}", value.Currency, 5U));
        return row;
    }

    private static Stylesheet CreateStylesheet()
    {
        var fonts = new Fonts(
            Font(11D),
            Font(48D, bold: true, color: "FFFF0000"),
            Font(22D, bold: true, color: "FFFF0000"),
            Font(18D, bold: true, color: "FFFF0000"),
            Font(16D),
            Font(16D, bold: true)) { Count = 6U };
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            SolidFill(ExpirationsFelixTemplateDefinition.InsFillRgb),
            SolidFill(ExpirationsFelixTemplateDefinition.OtherInsurerFillRgb)) { Count = 4U };
        var thin = new Border(
            BorderSide<LeftBorder>(),
            BorderSide<RightBorder>(),
            BorderSide<TopBorder>(),
            BorderSide<BottomBorder>(),
            new DiagonalBorder());
        var borders = new Borders(new Border(), thin) { Count = 2U };
        var numberFormats = new NumberingFormats(
            new NumberingFormat { NumberFormatId = 164U, FormatCode = "d/m/yyyy" },
            new NumberingFormat { NumberFormatId = 165U, FormatCode = "#,##0.00" }) { Count = 2U };
        var formats = new CellFormats(
            new CellFormat(),
            Format(1U, 0U, horizontal: HorizontalAlignmentValues.Center, vertical: VerticalAlignmentValues.Center, wrap: true),
            Format(2U, 0U, horizontal: HorizontalAlignmentValues.Center, vertical: VerticalAlignmentValues.Center, wrap: true),
            Format(3U, 0U, horizontal: HorizontalAlignmentValues.Center, vertical: VerticalAlignmentValues.Center, wrap: true),
            Format(4U, 0U, horizontal: HorizontalAlignmentValues.Center, vertical: VerticalAlignmentValues.Center),
            Format(4U, 0U, horizontal: HorizontalAlignmentValues.Center, vertical: VerticalAlignmentValues.Bottom),
            Format(5U, 0U, 164U, HorizontalAlignmentValues.Center, VerticalAlignmentValues.Bottom),
            Format(4U, 0U, 165U, HorizontalAlignmentValues.Center, VerticalAlignmentValues.Bottom),
            Format(5U, 0U, horizontal: HorizontalAlignmentValues.Center, vertical: VerticalAlignmentValues.Bottom),
            Format(4U, 2U, horizontal: HorizontalAlignmentValues.Center, vertical: VerticalAlignmentValues.Center),
            Format(4U, 3U, horizontal: HorizontalAlignmentValues.Center, vertical: VerticalAlignmentValues.Center))
        { Count = 11U };
        return new Stylesheet(
            numberFormats,
            fonts,
            fills,
            borders,
            new CellStyleFormats(new CellFormat()) { Count = 1U },
            formats,
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0U, BuiltinId = 0U }) { Count = 1U });
    }

    private static Font Font(double size, bool bold = false, string? color = null)
        => bold
            ? new Font(
                new Bold(),
                new FontSize { Val = size },
                new Color { Rgb = color ?? "FF000000" },
                new FontName { Val = "Calibri" })
            : new Font(
                new FontSize { Val = size },
                new Color { Rgb = color ?? "FF000000" },
                new FontName { Val = "Calibri" });

    private static Fill SolidFill(string rgb) => new(new PatternFill(
        new ForegroundColor { Rgb = $"FF{rgb}" },
        new BackgroundColor { Indexed = 64U }) { PatternType = PatternValues.Solid });

    private static T BorderSide<T>() where T : BorderPropertiesType, new() => new()
    {
        Style = BorderStyleValues.Thin,
        Color = new Color { Rgb = "FF000000" }
    };

    private static CellFormat Format(
        uint fontId,
        uint fillId,
        uint numberFormatId = 0U,
        HorizontalAlignmentValues? horizontal = null,
        VerticalAlignmentValues? vertical = null,
        bool wrap = false) => new()
    {
        FontId = fontId,
        FillId = fillId,
        BorderId = 1U,
        NumberFormatId = numberFormatId,
        ApplyFont = true,
        ApplyFill = fillId != 0U,
        ApplyBorder = true,
        ApplyNumberFormat = numberFormatId != 0U,
        ApplyAlignment = true,
        Alignment = new Alignment
        {
            Horizontal = horizontal ?? HorizontalAlignmentValues.General,
            Vertical = vertical ?? VerticalAlignmentValues.Bottom,
            WrapText = wrap
        }
    };

    private static Column VisibleColumn(uint index, double width) => new()
    {
        Min = index,
        Max = index,
        Width = width,
        CustomWidth = true
    };

    private static Cell InlineCell(string reference, string value, uint styleIndex) => new()
    {
        CellReference = reference,
        StyleIndex = styleIndex,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })
    };

    private static Cell NumberCell(string reference, double value, uint styleIndex) => new()
    {
        CellReference = reference,
        StyleIndex = styleIndex,
        DataType = CellValues.Number,
        CellValue = new CellValue(value.ToString("R", CultureInfo.InvariantCulture))
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

    private static void ValidateGeneratedWorkbook(
        string path,
        int expectedRows,
        ExpirationsPremiumTotalsPlan plan,
        CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var validationErrors = new OpenXmlValidator().Validate(document, cancellationToken).Take(10).ToList();
        if (validationErrors.Count > 0)
        {
            throw new ExpirationsGenerationException(
                $"El archivo especial generado no es OpenXML válido: {validationErrors[0].Description} " +
                $"({validationErrors[0].Path?.XPath})");
        }
        var workbookPart = document.WorkbookPart ??
            throw new ExpirationsGenerationException("El archivo especial no contiene un workbook válido.");
        var sheets = workbookPart.Workbook?.Sheets?.Elements<Sheet>().ToList() ?? [];
        if (sheets.Count != 2 ||
            !string.Equals(sheets[0].Name?.Value, ExpirationsFelixTemplateDefinition.WorksheetName, StringComparison.Ordinal) ||
            !string.Equals(sheets[1].Name?.Value, ExpirationsPremiumTotalsSheetService.TotalsSheetName, StringComparison.Ordinal))
        {
            throw new ExpirationsGenerationException("El archivo especial no contiene exactamente las dos hojas esperadas.");
        }
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheets[0].Id!.Value!);
        var worksheet = worksheetPart.Worksheet ??
            throw new ExpirationsGenerationException("El archivo especial no contiene una hoja de detalle válida.");
        var dataRows = worksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToList() ?? [];
        if (dataRows.Count != expectedRows + 2)
            throw new ExpirationsGenerationException("El archivo especial no contiene todas las filas esperadas.");
        var hiddenCurrency = worksheet.GetFirstChild<Columns>()?.Elements<Column>()
            .SingleOrDefault(column => column.Min?.Value == CurrencyColumnIndex);
        if (hiddenCurrency?.Hidden?.Value != true)
            throw new ExpirationsGenerationException("La columna auxiliar Moneda no quedó oculta.");
        var workbook = workbookPart.Workbook ??
            throw new ExpirationsGenerationException("El archivo especial no contiene un workbook válido.");
        if (workbook.CalculationProperties?.CalculationMode?.Value != CalculateModeValues.Auto ||
            workbook.CalculationProperties.CalculationOnSave?.Value != true ||
            workbook.CalculationProperties.ForceFullCalculation?.Value != true ||
            workbook.CalculationProperties.FullCalculationOnLoad?.Value != true ||
            workbookPart.CalculationChainPart is not null ||
            string.IsNullOrWhiteSpace(plan.CrcFormula) ||
            string.IsNullOrWhiteSpace(plan.UsdFormula))
        {
            throw new ExpirationsGenerationException("El archivo especial no quedó configurado para recalcular las primas.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // La capa batch vuelve a limpiar su staging propio.
        }
    }
}
