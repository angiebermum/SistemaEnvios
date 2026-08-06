using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public sealed record SpecialRebateSheetDefinition(
    string BrokerSeedKey,
    IReadOnlyList<string> BrokerNames,
    string RequiredWorksheetName,
    IReadOnlyList<string> ExcludedWorksheetNames,
    string TemplateKey,
    decimal? FixedExchangeRate,
    bool UsesExchangeRate,
    SpecialRebateTemplateKind TemplateKind);

public enum SpecialRebateTemplateKind
{
    Andres,
    Roberto,
    Sylvia,
    Arturo
}

public sealed record InformationalRebateAmounts(
    decimal GrossAmountCrc,
    decimal TotalRebateCrc,
    decimal AppliedRebateCrc,
    decimal PendingBalanceCrc,
    decimal InformationalRebateUsd,
    decimal EquivalentTotalUsd,
    decimal RemainingPayableCrc);

internal sealed record AndresAswCrcInvoiceAmounts(
    decimal GrossAmountCrc,
    decimal TotalRebateCrc,
    decimal? GrossAdjustmentCrc,
    decimal AdjustedGrossAmountCrc,
    decimal? VatCrc,
    decimal? InvoiceAmountCrc,
    decimal? WithholdingCrc,
    decimal? DeductionsCrc,
    decimal? DepositedAmountCrc)
{
    public bool RequiresInvoice => GrossAmountCrc > TotalRebateCrc;
}

public sealed class SpecialRebateSheetService
{
    public const decimal FixedExchangeRate = 460m;

    private static readonly IReadOnlyList<SpecialRebateSheetDefinition> SpecialDefinitions =
    [
        new(
            "andres-steimberg-seguru",
            ["Andrés Steimberg - Agent for EssentialGroupLA"],
            "ASW",
            ["SIG", "VEINSA", "ASW SIG", "ASW Veinsa"],
            "andres-asw.xlsx",
            FixedExchangeRate,
            true,
            SpecialRebateTemplateKind.Andres),
        new(
            "roberto-merino-yahoo",
            ["Roberto Merino"],
            "RMB",
            [],
            "roberto-rmb.xlsx",
            FixedExchangeRate,
            true,
            SpecialRebateTemplateKind.Roberto),
        new(
            "sylvia-serrano-essential",
            ["Sylvia Serrano"],
            "SSM",
            [],
            "sylvia-ssm.xlsx",
            null,
            false,
            SpecialRebateTemplateKind.Sylvia),
        new(
            "luis-arturo-quesada-essential",
            ["Arturo Quesada Ovares", "Luis Arturo Quesada Ovares"],
            "AQO",
            ["AQM (HC)"],
            "arturo-aqo.xlsx",
            null,
            false,
            SpecialRebateTemplateKind.Arturo)
    ];

    private readonly string _templateDirectory;

    public SpecialRebateSheetService(string? templateDirectory = null)
    {
        _templateDirectory = templateDirectory ?? ResolveTemplateDirectory();
    }

    public static IReadOnlyList<SpecialRebateSheetDefinition> Definitions => SpecialDefinitions;

    public bool TryGetDefinition(
        Broker broker,
        string sourceWorksheetName,
        out SpecialRebateSheetDefinition? definition)
    {
        var normalizedWorksheetName = NormalizeExact(sourceWorksheetName);
        definition = SpecialDefinitions.FirstOrDefault(candidate =>
            BrokerMatches(candidate, broker) &&
            string.Equals(
                NormalizeExact(candidate.RequiredWorksheetName),
                normalizedWorksheetName,
                StringComparison.OrdinalIgnoreCase));
        return definition is not null;
    }

    public static InformationalRebateAmounts CalculateAmounts(
        decimal grossAmountCrc,
        decimal totalRebateCrc,
        decimal? exchangeRate)
    {
        var gross = Math.Max(grossAmountCrc, 0m);
        var total = Math.Max(totalRebateCrc, 0m);
        var applied = Math.Min(gross, total);
        var pending = Math.Max(total - gross, 0m);
        var remaining = Math.Max(gross - total, 0m);
        var informationalUsd = exchangeRate is > 0m ? pending / exchangeRate.Value : 0m;
        var equivalentTotalUsd = exchangeRate is > 0m ? total / exchangeRate.Value : 0m;
        return new InformationalRebateAmounts(
            gross,
            total,
            applied,
            pending,
            informationalUsd,
            equivalentTotalUsd,
            remaining);
    }

    internal static AndresAswCrcInvoiceAmounts CalculateAndresAswCrcInvoiceAmounts(
        decimal grossAmountCrc,
        decimal totalRebateCrc,
        decimal deductionsCrc = 0m)
    {
        var gross = Math.Max(PaymentCalculationService.Round(grossAmountCrc), 0m);
        var totalRebate = Math.Max(PaymentCalculationService.Round(totalRebateCrc), 0m);
        if (gross <= totalRebate)
        {
            return new AndresAswCrcInvoiceAmounts(
                gross,
                totalRebate,
                null,
                gross,
                null,
                null,
                null,
                null,
                null);
        }

        var adjustedGross = PaymentCalculationService.Round(gross - totalRebate);
        var vat = PaymentCalculationService.Round(adjustedGross * PaymentCalculationService.VatRate);
        var invoiceAmount = PaymentCalculationService.Round(adjustedGross + vat);
        var withholding = PaymentCalculationService.Round(
            adjustedGross * PaymentCalculationService.WithholdingRate);
        var deductions = Math.Max(PaymentCalculationService.Round(deductionsCrc), 0m);
        var depositedAmount = Math.Max(
            PaymentCalculationService.Round(invoiceAmount - withholding - deductions),
            0m);
        return new AndresAswCrcInvoiceAmounts(
            gross,
            totalRebate,
            totalRebate,
            adjustedGross,
            vat,
            invoiceAmount,
            withholding,
            deductions,
            depositedAmount);
    }

    internal bool AppendInformationalSheet(
        WorkbookPart workbookPart,
        Broker broker,
        string sourceWorksheetName,
        PaymentCalculationResult calculation)
    {
        if (!TryGetDefinition(broker, sourceWorksheetName, out var definition))
        {
            return false;
        }

        var templatePath = Path.Combine(_templateDirectory, definition!.TemplateKey);
        if (!File.Exists(templatePath))
        {
            throw new InvalidDataException(
                $"No se encontró la plantilla informativa '{definition.TemplateKey}'.");
        }

        using var templateDocument = SpreadsheetDocument.Open(templatePath, false);
        var templateWorkbookPart = templateDocument.WorkbookPart
            ?? throw new InvalidDataException(
                $"La plantilla '{definition.TemplateKey}' no contiene un libro válido.");
        var templateSheet = templateWorkbookPart.Workbook?.Sheets?.Elements<Sheet>()
            .FirstOrDefault(value =>
                string.Equals(
                    NormalizeExact(value.Name?.Value),
                    "REBAJO",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"La plantilla '{definition.TemplateKey}' no contiene la hoja REBAJO.");
        if (templateSheet.Id?.Value is not { } templateRelationshipId ||
            templateWorkbookPart.GetPartById(templateRelationshipId) is not WorksheetPart templateWorksheetPart)
        {
            throw new InvalidDataException(
                $"La hoja REBAJO de '{definition.TemplateKey}' no es compatible.");
        }

        RemoveExistingRebateSheet(workbookPart);
        var importedWorksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var templateWorksheet = templateWorksheetPart.Worksheet
            ?? throw new InvalidDataException(
                $"La hoja REBAJO de '{definition.TemplateKey}' no contiene datos.");
        importedWorksheetPart.Worksheet =
            (Worksheet)templateWorksheet.CloneNode(true);
        ConvertSharedStringsToInline(
            importedWorksheetPart.Worksheet,
            templateWorkbookPart.SharedStringTablePart);
        var styleMap = ImportStyles(templateWorkbookPart, workbookPart);
        RemapCellStyles(importedWorksheetPart.Worksheet, styleMap);
        CopyPrinterSettings(templateWorksheetPart, importedWorksheetPart);

        ApplyInformationalRules(
            workbookPart,
            importedWorksheetPart.Worksheet,
            definition,
            calculation);
        importedWorksheetPart.Worksheet.Save();

        var workbook = workbookPart.Workbook
            ?? throw new InvalidDataException("El archivo generado no contiene un libro válido.");
        workbook.Sheets ??= new Sheets();
        var nextSheetId = workbook.Sheets.Elements<Sheet>()
            .Select(value => value.SheetId?.Value ?? 0U)
            .DefaultIfEmpty(0U)
            .Max() + 1U;
        workbook.Sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(importedWorksheetPart),
            SheetId = nextSheetId,
            Name = "REBAJO"
        });
        return true;
    }

    private static bool BrokerMatches(SpecialRebateSheetDefinition definition, Broker broker)
    {
        if (!string.IsNullOrWhiteSpace(broker.SeedKey))
        {
            return string.Equals(
                NormalizeExact(broker.SeedKey),
                NormalizeExact(definition.BrokerSeedKey),
                StringComparison.OrdinalIgnoreCase);
        }

        var normalizedBrokerName = NormalizeExact(broker.Name);
        return definition.BrokerNames.Any(value =>
            string.Equals(
                NormalizeExact(value),
                normalizedBrokerName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeExact(string? value) => (value ?? string.Empty).Trim();

    private static string ResolveTemplateDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "RebateTemplates"),
            Path.Combine(AppContext.BaseDirectory, "RebateTemplates"),
            Path.Combine(Directory.GetCurrentDirectory(), "Data", "RebateTemplates")
        };
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    private static void RemoveExistingRebateSheet(WorkbookPart workbookPart)
    {
        var workbook = workbookPart.Workbook;
        var existingSheets = workbook?.Sheets?.Elements<Sheet>()
            .Where(value =>
                string.Equals(
                    NormalizeExact(value.Name?.Value),
                    "REBAJO",
                    StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        foreach (var existingSheet in existingSheets)
        {
            if (existingSheet.Id?.Value is { } relationshipId)
            {
                workbookPart.DeletePart(workbookPart.GetPartById(relationshipId));
            }

            existingSheet.Remove();
        }
    }

    private static void ConvertSharedStringsToInline(
        Worksheet worksheet,
        SharedStringTablePart? sharedStringsPart)
    {
        if (sharedStringsPart?.SharedStringTable is not { } sharedStringTable)
        {
            return;
        }

        var sharedStrings = sharedStringTable.Elements<SharedStringItem>().ToList();
        foreach (var cell in worksheet.Descendants<Cell>()
                     .Where(value => value.DataType?.Value == CellValues.SharedString))
        {
            if (!int.TryParse(
                    cell.CellValue?.Text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var index) ||
                index < 0 ||
                index >= sharedStrings.Count)
            {
                throw new InvalidDataException(
                    $"La plantilla REBAJO contiene una cadena compartida inválida en {cell.CellReference?.Value}.");
            }

            var inlineString = new InlineString();
            foreach (var child in sharedStrings[index].ChildElements)
            {
                inlineString.Append(child.CloneNode(true));
            }

            cell.CellValue = null;
            cell.DataType = CellValues.InlineString;
            cell.InlineString = inlineString;
        }
    }

    private static IReadOnlyDictionary<uint, uint> ImportStyles(
        WorkbookPart sourceWorkbookPart,
        WorkbookPart targetWorkbookPart)
    {
        var sourceStylesheet = sourceWorkbookPart.WorkbookStylesPart?.Stylesheet
            ?? throw new InvalidDataException("La plantilla REBAJO no contiene estilos.");
        var targetStylesPart = targetWorkbookPart.WorkbookStylesPart
            ?? targetWorkbookPart.AddNewPart<WorkbookStylesPart>();
        targetStylesPart.Stylesheet ??= new Stylesheet();
        var targetStylesheet = targetStylesPart.Stylesheet;

        EnsureStyleCollections(targetStylesheet);
        EnsureStyleCollections(sourceStylesheet);

        var numberFormatMap = ImportNumberFormats(sourceStylesheet, targetStylesheet);
        var fontOffset = AppendClones(sourceStylesheet.Fonts!, targetStylesheet.Fonts!);
        var fillOffset = AppendClones(sourceStylesheet.Fills!, targetStylesheet.Fills!);
        var borderOffset = AppendClones(sourceStylesheet.Borders!, targetStylesheet.Borders!);
        var styleFormatOffset = (uint)targetStylesheet.CellStyleFormats!.ChildElements.Count;
        foreach (var sourceFormat in sourceStylesheet.CellStyleFormats!.Elements<CellFormat>())
        {
            targetStylesheet.CellStyleFormats.Append(
                RemapFormat(
                    sourceFormat,
                    fontOffset,
                    fillOffset,
                    borderOffset,
                    0U,
                    numberFormatMap,
                    remapFormatId: false));
        }

        var styleMap = new Dictionary<uint, uint>();
        foreach (var sourceFormat in sourceStylesheet.CellFormats!.Elements<CellFormat>())
        {
            var sourceIndex = (uint)styleMap.Count;
            var targetIndex = (uint)targetStylesheet.CellFormats!.ChildElements.Count;
            targetStylesheet.CellFormats.Append(
                RemapFormat(
                    sourceFormat,
                    fontOffset,
                    fillOffset,
                    borderOffset,
                    styleFormatOffset,
                    numberFormatMap,
                    remapFormatId: true));
            styleMap[sourceIndex] = targetIndex;
        }

        SetStyleCounts(targetStylesheet);
        targetStylesheet.Save();
        return styleMap;
    }

    private static void EnsureStyleCollections(Stylesheet stylesheet)
    {
        stylesheet.Fonts ??= new Fonts(new Font());
        stylesheet.Fills ??= new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }));
        stylesheet.Borders ??= new Borders(new Border());
        stylesheet.CellStyleFormats ??= new CellStyleFormats(new CellFormat());
        stylesheet.CellFormats ??= new CellFormats(new CellFormat());
        stylesheet.CellStyles ??= new CellStyles(new CellStyle
        {
            Name = "Normal",
            FormatId = 0U,
            BuiltinId = 0U
        });
    }

    private static IReadOnlyDictionary<uint, uint> ImportNumberFormats(
        Stylesheet source,
        Stylesheet target)
    {
        var map = new Dictionary<uint, uint>();
        var sourceFormats = source.NumberingFormats?.Elements<NumberingFormat>().ToList() ?? [];
        if (sourceFormats.Count == 0)
        {
            return map;
        }

        target.NumberingFormats ??= new NumberingFormats();
        var targetFormats = target.NumberingFormats.Elements<NumberingFormat>().ToList();
        var usedIds = targetFormats
            .Select(value => value.NumberFormatId?.Value ?? 0U)
            .ToHashSet();
        var nextId = Math.Max(164U, usedIds.DefaultIfEmpty(163U).Max() + 1U);
        foreach (var sourceFormat in sourceFormats)
        {
            var sourceId = sourceFormat.NumberFormatId?.Value ?? 0U;
            var formatCode = sourceFormat.FormatCode?.Value ?? string.Empty;
            var matching = targetFormats.FirstOrDefault(value =>
                string.Equals(value.FormatCode?.Value, formatCode, StringComparison.Ordinal));
            if (matching?.NumberFormatId?.Value is { } matchingId)
            {
                map[sourceId] = matchingId;
                continue;
            }

            var targetId = sourceId >= 164U && !usedIds.Contains(sourceId)
                ? sourceId
                : NextAvailableNumberFormatId(usedIds, ref nextId);
            var clone = (NumberingFormat)sourceFormat.CloneNode(true);
            clone.NumberFormatId = targetId;
            target.NumberingFormats.Append(clone);
            targetFormats.Add(clone);
            usedIds.Add(targetId);
            map[sourceId] = targetId;
        }

        return map;
    }

    private static uint NextAvailableNumberFormatId(HashSet<uint> usedIds, ref uint nextId)
    {
        while (usedIds.Contains(nextId))
        {
            nextId++;
        }

        return nextId++;
    }

    private static uint AppendClones(OpenXmlCompositeElement source, OpenXmlCompositeElement target)
    {
        var offset = (uint)target.ChildElements.Count;
        foreach (var child in source.ChildElements)
        {
            target.Append(child.CloneNode(true));
        }

        return offset;
    }

    private static CellFormat RemapFormat(
        CellFormat source,
        uint fontOffset,
        uint fillOffset,
        uint borderOffset,
        uint styleFormatOffset,
        IReadOnlyDictionary<uint, uint> numberFormatMap,
        bool remapFormatId)
    {
        var clone = (CellFormat)source.CloneNode(true);
        clone.FontId = fontOffset + (source.FontId?.Value ?? 0U);
        clone.FillId = fillOffset + (source.FillId?.Value ?? 0U);
        clone.BorderId = borderOffset + (source.BorderId?.Value ?? 0U);
        if (source.NumberFormatId?.Value is { } numberFormatId &&
            numberFormatMap.TryGetValue(numberFormatId, out var targetNumberFormatId))
        {
            clone.NumberFormatId = targetNumberFormatId;
        }

        if (remapFormatId)
        {
            clone.FormatId = styleFormatOffset + (source.FormatId?.Value ?? 0U);
        }

        return clone;
    }

    private static void SetStyleCounts(Stylesheet stylesheet)
    {
        stylesheet.NumberingFormats?.Count =
            (uint)(stylesheet.NumberingFormats?.ChildElements.Count ?? 0);
        stylesheet.Fonts!.Count = (uint)stylesheet.Fonts.ChildElements.Count;
        stylesheet.Fills!.Count = (uint)stylesheet.Fills.ChildElements.Count;
        stylesheet.Borders!.Count = (uint)stylesheet.Borders.ChildElements.Count;
        stylesheet.CellStyleFormats!.Count = (uint)stylesheet.CellStyleFormats.ChildElements.Count;
        stylesheet.CellFormats!.Count = (uint)stylesheet.CellFormats.ChildElements.Count;
        stylesheet.CellStyles!.Count = (uint)stylesheet.CellStyles.ChildElements.Count;
    }

    private static void RemapCellStyles(
        Worksheet worksheet,
        IReadOnlyDictionary<uint, uint> styleMap)
    {
        foreach (var cell in worksheet.Descendants<Cell>())
        {
            var sourceStyleIndex = cell.StyleIndex?.Value ?? 0U;
            if (!styleMap.TryGetValue(sourceStyleIndex, out var targetStyleIndex))
            {
                throw new InvalidDataException(
                    $"La plantilla REBAJO contiene un estilo inválido en {cell.CellReference?.Value}.");
            }

            cell.StyleIndex = targetStyleIndex;
        }
    }

    private static void CopyPrinterSettings(
        WorksheetPart sourceWorksheetPart,
        WorksheetPart targetWorksheetPart)
    {
        foreach (var sourcePart in sourceWorksheetPart.SpreadsheetPrinterSettingsParts)
        {
            var relationshipId = sourceWorksheetPart.GetIdOfPart(sourcePart);
            var targetPart =
                targetWorksheetPart.AddNewPart<SpreadsheetPrinterSettingsPart>(relationshipId);
            using var sourceStream = sourcePart.GetStream(FileMode.Open, FileAccess.Read);
            using var targetStream = targetPart.GetStream(FileMode.Create, FileAccess.Write);
            sourceStream.CopyTo(targetStream);
        }
    }

    private static void ApplyInformationalRules(
        WorkbookPart workbookPart,
        Worksheet worksheet,
        SpecialRebateSheetDefinition definition,
        PaymentCalculationResult calculation)
    {
        switch (definition.TemplateKind)
        {
            case SpecialRebateTemplateKind.Andres:
                var amounts = ApplyAndresRules(
                    worksheet,
                    calculation.Crc.GrossCommissionOriginal);
                ApplyAndresAswCrcInvoiceRules(
                    workbookPart,
                    calculation.Crc,
                    amounts.TotalRebateCrc);
                break;
            case SpecialRebateTemplateKind.Roberto:
                ApplyRobertoRules(worksheet, calculation.Crc.GrossCommissionOriginal);
                break;
            case SpecialRebateTemplateKind.Sylvia:
                ApplySylviaRules(
                    workbookPart,
                    worksheet,
                    calculation.Crc.GrossCommissionOriginal);
                break;
            case SpecialRebateTemplateKind.Arturo:
                ApplyArturoRules(
                    workbookPart,
                    worksheet,
                    calculation.Crc.GrossCommissionOriginal);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(definition.TemplateKind),
                    definition.TemplateKind,
                    "Plantilla REBAJO desconocida.");
        }
    }

    private static InformationalRebateAmounts ApplyAndresRules(
        Worksheet worksheet,
        decimal grossAmountCrc)
    {
        var totalRebate = ReadNonNegativeDecimal(worksheet, "D8");
        var amounts = CalculateAmounts(grossAmountCrc, totalRebate, FixedExchangeRate);
        SetNumber(worksheet, "F1", FixedExchangeRate);
        SetText(worksheet, "G1", "TC FIJO");
        SetNumber(worksheet, "D8", amounts.TotalRebateCrc, "MAX(ROUND(D6-D7,2),0)");
        SetText(worksheet, "B9", "EQUIVALENTE TOTAL USD");
        SetNumber(worksheet, "D9", amounts.EquivalentTotalUsd, "D8/F1");
        SetNumber(
            worksheet,
            "D10",
            amounts.AppliedRebateCrc,
            "MIN(MAX(ROUND('Monto de factura'!C3,2),0),D8)");
        SetText(worksheet, "B11", "SALDO PENDIENTE");
        SetNumber(
            worksheet,
            "D11",
            amounts.PendingBalanceCrc,
            "MAX(D8-MAX(ROUND('Monto de factura'!C3,2),0),0)");
        SetText(worksheet, "B12", "REBAJO INFORMATIVO USD");
        SetNumber(worksheet, "D12", amounts.InformationalRebateUsd, "D11/F1");
        EnsureRowLike(worksheet, 13U, 11U);
        CopyStyle(worksheet, "B11", "B13");
        CopyStyle(worksheet, "C11", "C13");
        CopyStyle(worksheet, "D11", "D13");
        CopyStyle(worksheet, "E11", "E13");
        SetText(worksheet, "B13", "SOBRANTE POR PAGAR");
        SetText(worksheet, "C13", string.Empty);
        SetNumber(
            worksheet,
            "D13",
            amounts.RemainingPayableCrc,
            "MAX(MAX(ROUND('Monto de factura'!C3,2),0)-D8,0)");
        SetText(worksheet, "E13", string.Empty);
        return amounts;
    }

    private static void ApplyAndresAswCrcInvoiceRules(
        WorkbookPart workbookPart,
        PaymentCurrencyCalculation calculation,
        decimal totalRebateCrc)
    {
        var worksheet = GetWorksheet(workbookPart, "Monto de factura");
        var labelColumnIndex = FindCurrencyLabelColumn(worksheet, "COLONES");
        var amountColumnIndex = labelColumnIndex + 1U;
        var payableDeductions = calculation.Deductions
            .Where(value => value.ApplicationType == DeductionApplicationType.PayableAmount)
            .ToList();
        var finalDeductions = payableDeductions.Sum(value => value.AppliedAmount);
        var amounts = CalculateAndresAswCrcInvoiceAmounts(
            calculation.GrossCommissionOriginal,
            totalRebateCrc,
            finalDeductions);
        var grossReference = FindLabeledAmountReference(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "Monto bruto comisión");
        var adjustmentReference = FindLabeledAmountReference(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "Ajustes al monto bruto");
        var adjustedGrossReference = FindLabeledAmountReference(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "Monto bruto ajustado");
        var vatReference = FindLabeledAmountReference(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "IVA 13%");
        var invoiceReference = FindLabeledAmountReference(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "Monto factura");
        var withholdingReference = FindLabeledAmountReference(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "Retención 2%");
        var deductionsReference = FindLabeledAmountReference(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "Deducciones");
        var depositedReference = FindLabeledAmountReference(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "Monto depositado");
        const string rebateReference = "'REBAJO'!D8";
        var requiresInvoice = $"{grossReference}>{rebateReference}";

        SetLabeledFormula(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "Ajustes al monto bruto",
            $"IF({requiresInvoice},{rebateReference},\"\")",
            amounts.GrossAdjustmentCrc);
        ClearRowsBetween(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "Ajustes al monto bruto",
            "Monto bruto ajustado");
        SetLabeledFormula(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "Monto bruto ajustado",
            $"IF({requiresInvoice},{grossReference}-{adjustmentReference},{grossReference})",
            amounts.AdjustedGrossAmountCrc);
        SetLabeledFormula(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "IVA 13%",
            $"IF({requiresInvoice},{adjustedGrossReference}*13%,\"\")",
            amounts.VatCrc);
        SetLabeledFormula(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "Monto factura",
            $"IF({requiresInvoice},{adjustedGrossReference}+{vatReference},\"\")",
            amounts.InvoiceAmountCrc);
        SetLabeledFormula(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "Retención 2%",
            $"IF({requiresInvoice},-({adjustedGrossReference}*2%),\"\")",
            amounts.WithholdingCrc.HasValue ? -amounts.WithholdingCrc.Value : null);
        ClearCell(GetOrCreateCell(worksheet, deductionsReference));
        if (!amounts.RequiresInvoice)
        {
            ClearRowsBetween(
                worksheet,
                labelColumnIndex,
                amountColumnIndex,
                "Deducciones",
                "Monto depositado");
        }

        var deductionsRow = ParseRowIndex(deductionsReference);
        var depositedRow = ParseRowIndex(depositedReference);
        var individualDeductionsExpression = payableDeductions.Count == 0
            ? string.Empty
            : $"+SUM({ColumnName(amountColumnIndex)}{deductionsRow + 1U}:" +
              $"{ColumnName(amountColumnIndex)}{depositedRow - 1U})";

        SetLabeledFormula(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            "Monto depositado",
            $"IF({requiresInvoice},MAX({invoiceReference}+{withholdingReference}" +
            $"{individualDeductionsExpression},0),\"\")",
            amounts.DepositedAmountCrc);
        ValidateFormulaReference(grossReference, "Monto bruto comisión");
        ValidateFormulaReference(depositedReference, "Monto depositado");
        worksheet.Save();
    }

    private static Worksheet GetWorksheet(WorkbookPart workbookPart, string worksheetName)
    {
        var sheet = workbookPart.Workbook?.Sheets?.Elements<Sheet>()
            .SingleOrDefault(value =>
                string.Equals(
                    NormalizeExact(value.Name?.Value),
                    NormalizeExact(worksheetName),
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"El archivo generado no contiene la hoja '{worksheetName}'.");
        if (sheet.Id?.Value is not { } relationshipId ||
            workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart ||
            worksheetPart.Worksheet is null)
        {
            throw new InvalidDataException(
                $"La hoja '{worksheetName}' del archivo generado no es compatible.");
        }

        return worksheetPart.Worksheet;
    }

    private static uint FindCurrencyLabelColumn(Worksheet worksheet, string currencyTitle)
    {
        var titleCell = worksheet.Descendants<Cell>()
            .SingleOrDefault(value =>
                string.Equals(
                    NormalizeExact(GetText(value)),
                    NormalizeExact(currencyTitle),
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"La hoja Monto de factura no contiene el bloque '{currencyTitle}'.");
        return ParseColumnIndex(
            titleCell.CellReference?.Value
            ?? throw new InvalidDataException(
                $"El bloque '{currencyTitle}' no tiene una referencia de celda válida."));
    }

    private static string FindLabeledAmountReference(
        Worksheet worksheet,
        uint labelColumnIndex,
        uint amountColumnIndex,
        string label)
    {
        var labelCell = FindLabelCell(worksheet, labelColumnIndex, label);
        var rowIndex = ParseRowIndex(
            labelCell.CellReference?.Value
            ?? throw new InvalidDataException(
                $"El concepto '{label}' no tiene una referencia de celda válida."));
        return $"{ColumnName(amountColumnIndex)}{rowIndex}";
    }

    private static void SetLabeledFormula(
        Worksheet worksheet,
        uint labelColumnIndex,
        uint amountColumnIndex,
        string label,
        string formula,
        decimal? cachedAmount)
    {
        if (string.IsNullOrWhiteSpace(formula) || formula.Contains("#REF!", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"No se pudo escribir una fórmula válida para '{label}' en Monto de factura.");
        }

        var reference = FindLabeledAmountReference(
            worksheet,
            labelColumnIndex,
            amountColumnIndex,
            label);
        SetFormula(GetOrCreateCell(worksheet, reference), formula, cachedAmount);
    }

    private static void SetFormula(Cell cell, string formula, decimal? cachedAmount)
    {
        cell.InlineString = null;
        cell.DataType = null;
        cell.CellFormula = new CellFormula(formula);
        cell.CellValue = cachedAmount.HasValue
            ? new CellValue(cachedAmount.Value.ToString(
                "0.############################",
                CultureInfo.InvariantCulture))
            : null;
    }

    private static void ValidateFormulaReference(string reference, string label)
    {
        if (string.IsNullOrWhiteSpace(reference) ||
            reference.Any(character => !char.IsLetterOrDigit(character)))
        {
            throw new InvalidDataException(
                $"La referencia de '{label}' en Monto de factura no es válida.");
        }
    }

    private static void ClearRowsBetween(
        Worksheet worksheet,
        uint labelColumnIndex,
        uint amountColumnIndex,
        string firstLabel,
        string lastLabel)
    {
        var firstRow = ParseRowIndex(
            FindLabelCell(worksheet, labelColumnIndex, firstLabel).CellReference!.Value!);
        var lastRow = ParseRowIndex(
            FindLabelCell(worksheet, labelColumnIndex, lastLabel).CellReference!.Value!);
        for (var rowIndex = firstRow + 1U; rowIndex < lastRow; rowIndex++)
        {
            ClearCell(GetOrCreateCell(
                worksheet,
                $"{ColumnName(labelColumnIndex)}{rowIndex}"));
            ClearCell(GetOrCreateCell(
                worksheet,
                $"{ColumnName(amountColumnIndex)}{rowIndex}"));
        }
    }

    private static Cell FindLabelCell(
        Worksheet worksheet,
        uint labelColumnIndex,
        string label)
    {
        return worksheet.Descendants<Cell>()
                   .SingleOrDefault(value =>
                       ParseColumnIndex(value.CellReference?.Value ?? "A1") == labelColumnIndex &&
                       string.Equals(
                           NormalizeExact(GetText(value)),
                           NormalizeExact(label),
                           StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidDataException(
                   $"La hoja Monto de factura no contiene el concepto '{label}'.");
    }

    private static string GetText(Cell cell) =>
        cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? string.Empty;

    private static string ColumnName(uint columnIndex)
    {
        var characters = new Stack<char>();
        while (columnIndex > 0U)
        {
            columnIndex--;
            characters.Push((char)('A' + columnIndex % 26U));
            columnIndex /= 26U;
        }

        return new string(characters.ToArray());
    }

    private static void ClearCell(Cell cell)
    {
        cell.CellFormula = null;
        cell.CellValue = null;
        cell.InlineString = null;
        cell.DataType = null;
    }

    private static void ApplyRobertoRules(Worksheet worksheet, decimal grossAmountCrc)
    {
        var totalRebate = ReadNonNegativeDecimal(worksheet, "D6");
        var amounts = CalculateAmounts(grossAmountCrc, totalRebate, FixedExchangeRate);
        SetNumber(worksheet, "F1", FixedExchangeRate);
        SetText(worksheet, "G1", "TC FIJO");
        SetNumber(worksheet, "D6", amounts.TotalRebateCrc, "MAX(ROUND(D4-D5,2),0)");
        SetText(worksheet, "B7", "EQUIVALENTE TOTAL USD");
        SetNumber(worksheet, "D7", amounts.EquivalentTotalUsd, "D6/F1");
        SetNumber(
            worksheet,
            "D8",
            amounts.AppliedRebateCrc,
            "MIN(MAX(ROUND('Monto de factura'!C3,2),0),D6)");
        SetText(worksheet, "B9", "SALDO PENDIENTE");
        SetNumber(
            worksheet,
            "D9",
            amounts.PendingBalanceCrc,
            "MAX(D6-MAX(ROUND('Monto de factura'!C3,2),0),0)");
        SetText(worksheet, "B10", "REBAJO INFORMATIVO USD");
        SetNumber(worksheet, "D10", amounts.InformationalRebateUsd, "D9/F1");
    }

    private static void ApplySylviaRules(
        WorkbookPart workbookPart,
        Worksheet worksheet,
        decimal grossAmountCrc)
    {
        var totalRebate = ReadNonNegativeDecimal(worksheet, "D6");
        var amounts = CalculateAmounts(grossAmountCrc, totalRebate, null);
        SetText(worksheet, "F1", string.Empty);
        SetText(worksheet, "G1", "MONEDA: CRC");
        SetNumber(worksheet, "D6", amounts.TotalRebateCrc, "MAX(ROUND(D4-D5,2),0)");
        SetText(worksheet, "B7", "REBAJO EN COLONES");
        CopyNumberFormat(workbookPart, worksheet, "D8", "D7");
        SetNumber(
            worksheet,
            "D7",
            amounts.AppliedRebateCrc,
            "MIN(MAX(ROUND('Monto de factura'!C3,2),0),D6)");
        SetText(worksheet, "B8", "SALDO PENDIENTE");
        SetNumber(
            worksheet,
            "D8",
            amounts.PendingBalanceCrc,
            "MAX(D6-MAX(ROUND('Monto de factura'!C3,2),0),0)");
        SetText(worksheet, "B9", "SOBRANTE POR PAGAR");
        SetNumber(
            worksheet,
            "D9",
            amounts.RemainingPayableCrc,
            "MAX(MAX(ROUND('Monto de factura'!C3,2),0)-D6,0)");
        SetText(worksheet, "B10", string.Empty);
        SetText(worksheet, "D10", string.Empty);
    }

    private static void ApplyArturoRules(
        WorkbookPart workbookPart,
        Worksheet worksheet,
        decimal grossAmountCrc)
    {
        var totalRebate = ReadNonNegativeDecimal(worksheet, "D6");
        var amounts = CalculateAmounts(grossAmountCrc, totalRebate, null);
        SetText(worksheet, "F4", string.Empty);
        SetText(worksheet, "B7", "MONTO BRUTO EN COLONES");
        CopyNumberFormat(workbookPart, worksheet, "D6", "D7");
        SetNumber(
            worksheet,
            "D7",
            amounts.GrossAmountCrc,
            "MAX(ROUND('Monto de factura'!C3,2),0)");
        SetText(worksheet, "E5", "REBAJO EN COLONES");
        CopyNumberFormat(workbookPart, worksheet, "D6", "F6");
        SetNumber(
            worksheet,
            "F6",
            amounts.AppliedRebateCrc,
            "MIN(MAX(ROUND('Monto de factura'!C3,2),0),D6)");
        SetText(worksheet, "G5", "SALDO PENDIENTE");
        CopyNumberFormat(workbookPart, worksheet, "D6", "H6");
        SetNumber(
            worksheet,
            "H6",
            amounts.PendingBalanceCrc,
            "MAX(D6-MAX(ROUND('Monto de factura'!C3,2),0),0)");
    }

    private static decimal ReadNonNegativeDecimal(Worksheet worksheet, string reference)
    {
        var cell = GetOrCreateCell(worksheet, reference);
        return decimal.TryParse(
            cell.CellValue?.Text,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var value)
            ? Math.Max(PaymentCalculationService.Round(value), 0m)
            : 0m;
    }

    private static void SetText(Worksheet worksheet, string reference, string value)
    {
        var cell = GetOrCreateCell(worksheet, reference);
        cell.CellFormula = null;
        cell.CellValue = null;
        cell.DataType = CellValues.InlineString;
        cell.InlineString = new InlineString(
            new Text(value) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static void SetNumber(
        Worksheet worksheet,
        string reference,
        decimal value,
        string? formula = null)
    {
        SetNumber(GetOrCreateCell(worksheet, reference), value, formula);
    }

    private static void SetNumber(
        Cell cell,
        decimal value,
        string? formula = null)
    {
        cell.InlineString = null;
        cell.DataType = CellValues.Number;
        cell.CellFormula = formula is null ? null : new CellFormula(formula);
        cell.CellValue = new CellValue(
            value.ToString("0.############################", CultureInfo.InvariantCulture));
    }

    private static void CopyStyle(Worksheet worksheet, string sourceReference, string targetReference)
    {
        var source = GetOrCreateCell(worksheet, sourceReference);
        var target = GetOrCreateCell(worksheet, targetReference);
        target.StyleIndex = source.StyleIndex?.Value ?? 0U;
    }

    private static void CopyNumberFormat(
        WorkbookPart workbookPart,
        Worksheet worksheet,
        string sourceReference,
        string targetReference)
    {
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet
            ?? throw new InvalidDataException("El archivo generado no contiene estilos.");
        var cellFormats = stylesheet.CellFormats
            ?? throw new InvalidDataException("El archivo generado no contiene formatos de celda.");
        var formats = cellFormats.Elements<CellFormat>().ToList();
        var sourceCell = GetOrCreateCell(worksheet, sourceReference);
        var targetCell = GetOrCreateCell(worksheet, targetReference);
        var sourceIndex = (int)(sourceCell.StyleIndex?.Value ?? 0U);
        var targetIndex = (int)(targetCell.StyleIndex?.Value ?? 0U);
        if (sourceIndex >= formats.Count || targetIndex >= formats.Count)
        {
            throw new InvalidDataException("La plantilla REBAJO contiene un formato de celda inválido.");
        }

        var sourceFormat = formats[sourceIndex];
        var clone = (CellFormat)formats[targetIndex].CloneNode(true);
        clone.NumberFormatId = sourceFormat.NumberFormatId?.Value ?? 0U;
        clone.ApplyNumberFormat = sourceFormat.ApplyNumberFormat?.Value ??
                                  (clone.NumberFormatId?.Value ?? 0U) != 0U;
        var newStyleIndex = (uint)cellFormats.ChildElements.Count;
        cellFormats.Append(clone);
        cellFormats.Count = (uint)cellFormats.ChildElements.Count;
        stylesheet.Save();
        targetCell.StyleIndex = newStyleIndex;
    }

    private static void EnsureRowLike(Worksheet worksheet, uint targetIndex, uint sourceIndex)
    {
        var sheetData = worksheet.GetFirstChild<SheetData>()
            ?? throw new InvalidDataException("La plantilla REBAJO no contiene datos.");
        var target = GetOrCreateRow(sheetData, targetIndex);
        var source = sheetData.Elements<Row>()
            .FirstOrDefault(value => value.RowIndex?.Value == sourceIndex);
        if (source is null)
        {
            return;
        }

        target.Height = source.Height?.Value;
        target.CustomHeight = source.CustomHeight?.Value;
        target.Hidden = source.Hidden?.Value;
    }

    private static Cell GetOrCreateCell(Worksheet worksheet, string reference)
    {
        var sheetData = worksheet.GetFirstChild<SheetData>()
            ?? throw new InvalidDataException("La plantilla REBAJO no contiene datos.");
        var rowIndex = ParseRowIndex(reference);
        var row = GetOrCreateRow(sheetData, rowIndex);
        var existing = row.Elements<Cell>()
            .FirstOrDefault(value =>
                string.Equals(value.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var cell = new Cell { CellReference = reference };
        var columnIndex = ParseColumnIndex(reference);
        var nextCell = row.Elements<Cell>()
            .FirstOrDefault(value =>
                ParseColumnIndex(value.CellReference?.Value ?? "A1") > columnIndex);
        if (nextCell is null)
        {
            row.Append(cell);
        }
        else
        {
            row.InsertBefore(cell, nextCell);
        }

        return cell;
    }

    private static Row GetOrCreateRow(SheetData sheetData, uint rowIndex)
    {
        var existing = sheetData.Elements<Row>()
            .FirstOrDefault(value => value.RowIndex?.Value == rowIndex);
        if (existing is not null)
        {
            return existing;
        }

        var row = new Row { RowIndex = rowIndex };
        var nextRow = sheetData.Elements<Row>()
            .FirstOrDefault(value => (value.RowIndex?.Value ?? uint.MaxValue) > rowIndex);
        if (nextRow is null)
        {
            sheetData.Append(row);
        }
        else
        {
            sheetData.InsertBefore(row, nextRow);
        }

        return row;
    }

    private static uint ParseRowIndex(string reference)
    {
        var digits = new string(reference.Where(char.IsDigit).ToArray());
        return uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"Referencia de celda inválida: {reference}.");
    }

    private static uint ParseColumnIndex(string reference)
    {
        uint result = 0;
        foreach (var character in reference.TakeWhile(char.IsLetter))
        {
            result = result * 26U + (uint)(char.ToUpperInvariant(character) - 'A' + 1);
        }

        return result;
    }
}
