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
    public const string Title = "TOTAL DE PRIMAS";
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

        var styles = EnsureStyles(workbookPart);
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(
            new Columns(
                new Column { Min = 1U, Max = 1U, Width = 4D, CustomWidth = true },
                new Column { Min = 2U, Max = 2U, Width = 34D, CustomWidth = true },
                new Column { Min = 3U, Max = 3U, Width = 21D, CustomWidth = true },
                new Column { Min = 4U, Max = 4U, Width = 4D, CustomWidth = true }),
            new SheetData(
                new Row(
                    InlineTextCell("B2", Title, styles.Title),
                    InlineTextCell("C2", string.Empty, styles.Title)) { RowIndex = 2U, Height = 26D, CustomHeight = true },
                new Row(
                    InlineTextCell("B3", CrcLabel, styles.Label),
                    FormulaCell("C3", plan.CrcFormula, styles.CrcAmount)) { RowIndex = 3U, Height = 22D, CustomHeight = true },
                new Row(
                    InlineTextCell("B4", UsdLabel, styles.Label),
                    FormulaCell("C4", plan.UsdFormula, styles.UsdAmount)) { RowIndex = 4U, Height = 22D, CustomHeight = true }),
            new MergeCells(new MergeCell { Reference = "B2:C2" }));
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

    private static TotalsStyles EnsureStyles(WorkbookPart workbookPart)
    {
        var stylesPart = workbookPart.WorkbookStylesPart ?? workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet ??= new Stylesheet();
        var stylesheet = stylesPart.Stylesheet;
        stylesheet.Fonts ??= new Fonts(new Font());
        stylesheet.Fills ??= new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }));
        stylesheet.Borders ??= new Borders(new Border());
        stylesheet.CellStyleFormats ??= new CellStyleFormats(new CellFormat());
        stylesheet.CellFormats ??= new CellFormats(new CellFormat());

        var titleFont = Append(stylesheet.Fonts, new Font(
            new Bold(),
            new FontSize { Val = 12D },
            new Color { Rgb = "FFFFFFFF" },
            new FontName { Val = "Calibri" }));
        var labelFont = Append(stylesheet.Fonts, new Font(
            new Bold(),
            new FontSize { Val = 11D },
            new Color { Rgb = "FF1F2937" },
            new FontName { Val = "Calibri" }));
        var amountFont = Append(stylesheet.Fonts, new Font(
            new FontSize { Val = 11D },
            new Color { Rgb = "FF1F2937" },
            new FontName { Val = "Calibri" }));
        var titleFill = Append(stylesheet.Fills, new Fill(new PatternFill(
            new ForegroundColor { Rgb = "FF2F6F73" },
            new BackgroundColor { Indexed = 64U }) { PatternType = PatternValues.Solid }));
        var valueFill = Append(stylesheet.Fills, new Fill(new PatternFill(
            new ForegroundColor { Rgb = "FFE8F3F3" },
            new BackgroundColor { Indexed = 64U }) { PatternType = PatternValues.Solid }));
        var border = Append(stylesheet.Borders, new Border(
            new LeftBorder { Style = BorderStyleValues.Thin, Color = new Color { Rgb = "FF7A7A7A" } },
            new RightBorder { Style = BorderStyleValues.Thin, Color = new Color { Rgb = "FF7A7A7A" } },
            new TopBorder { Style = BorderStyleValues.Thin, Color = new Color { Rgb = "FF7A7A7A" } },
            new BottomBorder { Style = BorderStyleValues.Thin, Color = new Color { Rgb = "FF7A7A7A" } },
            new DiagonalBorder()));

        if (stylesheet.NumberingFormats is null)
        {
            var numberingFormats = new NumberingFormats();
            if (stylesheet.Fonts is { } firstFonts)
                stylesheet.InsertBefore(numberingFormats, firstFonts);
            else
                stylesheet.PrependChild(numberingFormats);
        }
        var numberingFormatsCollection = stylesheet.NumberingFormats!;
        var maxFormatId = numberingFormatsCollection.Elements<NumberingFormat>()
            .Select(format => format.NumberFormatId?.Value ?? 163U)
            .DefaultIfEmpty(163U)
            .Max();
        var crcFormatId = Math.Max(164U, maxFormatId + 1U);
        var usdFormatId = crcFormatId + 1U;
        numberingFormatsCollection.Append(
            new NumberingFormat
            {
                NumberFormatId = crcFormatId,
                FormatCode = "[$₡-es-CR]#,##0.00;[Red]-[$₡-es-CR]#,##0.00"
            },
            new NumberingFormat
            {
                NumberFormatId = usdFormatId,
                FormatCode = "[$$-en-US]#,##0.00;[Red]-[$$-en-US]#,##0.00"
            });
        var title = Append(stylesheet.CellFormats, Format(
            titleFont, titleFill, border, 0U, HorizontalAlignmentValues.Center));
        var label = Append(stylesheet.CellFormats, Format(
            labelFont, valueFill, border, 0U, HorizontalAlignmentValues.Left));
        var crcAmount = Append(stylesheet.CellFormats, Format(
            amountFont, valueFill, border, crcFormatId, HorizontalAlignmentValues.Right));
        var usdAmount = Append(stylesheet.CellFormats, Format(
            amountFont, valueFill, border, usdFormatId, HorizontalAlignmentValues.Right));

        stylesheet.Fonts.Count = (uint)stylesheet.Fonts.ChildElements.Count;
        stylesheet.Fills.Count = (uint)stylesheet.Fills.ChildElements.Count;
        stylesheet.Borders.Count = (uint)stylesheet.Borders.ChildElements.Count;
        numberingFormatsCollection.Count = (uint)numberingFormatsCollection.ChildElements.Count;
        stylesheet.CellFormats.Count = (uint)stylesheet.CellFormats.ChildElements.Count;
        stylesheet.Save();
        return new TotalsStyles(title, label, crcAmount, usdAmount);
    }

    private static CellFormat Format(
        uint fontId,
        uint fillId,
        uint borderId,
        uint numberFormatId,
        HorizontalAlignmentValues alignment) => new()
    {
        FontId = fontId,
        FillId = fillId,
        BorderId = borderId,
        NumberFormatId = numberFormatId,
        ApplyFont = true,
        ApplyFill = true,
        ApplyBorder = true,
        ApplyNumberFormat = numberFormatId > 0U,
        ApplyAlignment = true,
        Alignment = new Alignment { Horizontal = alignment, Vertical = VerticalAlignmentValues.Center }
    };

    private static uint Append(OpenXmlCompositeElement parent, OpenXmlElement child)
    {
        parent.Append(child);
        return checked((uint)parent.ChildElements.Count - 1U);
    }

    private static Cell InlineTextCell(string reference, string value, uint styleIndex) => new()
    {
        CellReference = reference,
        StyleIndex = styleIndex,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })
    };

    private static Cell FormulaCell(string reference, string formula, uint styleIndex) => new()
    {
        CellReference = reference,
        StyleIndex = styleIndex,
        CellFormula = new CellFormula(formula)
    };

    private sealed record TotalsStyles(uint Title, uint Label, uint CrcAmount, uint UsdAmount);
}
