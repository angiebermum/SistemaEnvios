using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ECS.CommissionsMailer.Services;

internal static class BrokerPercentageNormalizer
{
    public static decimal NormalizeBrokerPercentage(
        string? rawValue,
        bool isNumericValue,
        bool hasPercentageNumberFormat)
    {
        var compact = string.Concat((rawValue ?? string.Empty).Where(character => !char.IsWhiteSpace(character)));
        if (compact.Length == 0)
        {
            throw new FormatException("El porcentaje está vacío.");
        }

        var percentageSymbolCount = compact.Count(character => character == '%');
        var hasExplicitPercentageSymbol = percentageSymbolCount == 1 && compact.EndsWith('%');
        if (percentageSymbolCount > 0)
        {
            if (!hasExplicitPercentageSymbol)
            {
                throw new FormatException("El símbolo de porcentaje no está en una posición válida.");
            }

            compact = compact[..^1];
        }

        if (compact.Length == 0 || compact.Contains('.') && compact.Contains(','))
        {
            throw new FormatException("El porcentaje no es numérico.");
        }

        var invariantValue = compact.Replace(',', '.');
        if (!decimal.TryParse(
                invariantValue,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var percentage))
        {
            throw new FormatException("El porcentaje no es numérico.");
        }

        // Excel stores a real percentage as a fraction. The scale conversion is applied only
        // to numeric cells whose number format is percentage; text and values with an explicit
        // '%' already express the historical 0-100 scale and must not be multiplied again.
        if (isNumericValue && hasPercentageNumberFormat && !hasExplicitPercentageSymbol)
        {
            try
            {
                percentage *= 100m;
            }
            catch (OverflowException)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rawValue),
                    rawValue,
                    "El porcentaje debe estar entre 0 y 100.");
            }
        }

        if (percentage is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rawValue),
                rawValue,
                "El porcentaje debe estar entre 0 y 100.");
        }

        return percentage;
    }
}

internal static class BrokerPercentageWorksheetNormalizer
{
    private const uint TwoDecimalNumberFormatId = 2U;

    public static void Normalize(
        WorkbookPart workbookPart,
        WorksheetPart worksheetPart,
        string worksheetName)
    {
        var worksheetData = OpenXmlWorksheetReader.Read(workbookPart, worksheetPart, worksheetName);
        var layouts = FindHeaderLayouts(worksheetData);
        if (layouts.Count == 0)
        {
            return;
        }

        var worksheet = worksheetPart.Worksheet
            ?? throw new InvalidDataException($"La pestaña '{worksheetName}' no contiene datos de hoja.");
        var cellsByReference = worksheet.Descendants<Cell>()
            .Where(cell => !string.IsNullOrWhiteSpace(cell.CellReference?.Value))
            .ToDictionary(cell => cell.CellReference!.Value!, StringComparer.OrdinalIgnoreCase);
        var styleIndexes = new Dictionary<uint, uint>();
        var changed = false;
        var headerRows = layouts.Select(layout => layout.HeaderRow).Distinct().OrderBy(row => row).ToList();

        foreach (var layout in layouts)
        {
            var nextHeaderRow = headerRows.FirstOrDefault(row => row > layout.HeaderRow);
            foreach (var row in worksheetData.Rows
                         .Where(row => row.Key > layout.HeaderRow &&
                                       (nextHeaderRow == 0U || row.Key < nextHeaderRow))
                         .OrderBy(row => row.Key))
            {
                var percentageData = FindCell(row.Value, layout.PercentageColumn);
                if (percentageData is null ||
                    !cellsByReference.TryGetValue(percentageData.Reference, out var percentageCell) ||
                    !CellHasContent(percentageCell))
                {
                    continue;
                }

                var rawText = percentageData.Text.Trim();
                if (string.IsNullOrWhiteSpace(rawText) ||
                    rawText.Equals("% Corredor", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                decimal normalizedPercentage;
                try
                {
                    normalizedPercentage = BrokerPercentageNormalizer.NormalizeBrokerPercentage(
                        rawText,
                        IsNumericCell(percentageCell),
                        HasPercentageNumberFormat(percentageCell, workbookPart.WorkbookStylesPart?.Stylesheet));
                }
                catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
                {
                    throw CreateInvalidPercentageException(
                        worksheetName,
                        percentageData.Reference,
                        row.Key,
                        rawText,
                        ex);
                }

                WriteNormalizedPercentage(
                    percentageCell,
                    normalizedPercentage,
                    workbookPart,
                    styleIndexes);
                changed = true;

                var grossData = FindCell(row.Value, layout.GrossCommissionColumn);
                if (grossData is null ||
                    !cellsByReference.TryGetValue(grossData.Reference, out var grossCell) ||
                    !CellHasContent(grossCell))
                {
                    continue;
                }

                var grossValue = TryReadDecimal(grossCell, out var parsedGross) ? parsedGross : (decimal?)null;
                var commissionValue = grossValue * normalizedPercentage / 100m;
                var grossReference = GetCellReference(layout.GrossCommissionColumn, row.Key);
                var percentageReference = GetCellReference(layout.PercentageColumn, row.Key);
                var commissionReference = GetCellReference(layout.BrokerCommissionColumn, row.Key);
                UpdateCalculatedCell(
                    row.Value,
                    cellsByReference,
                    layout.BrokerCommissionColumn,
                    $"{grossReference}*{percentageReference}/100",
                    commissionValue);

                decimal? retentionValue = null;
                if (layout.RetentionColumn is { } retentionColumn)
                {
                    retentionValue = commissionValue * 0.02m;
                    UpdateCalculatedCell(
                        row.Value,
                        cellsByReference,
                        retentionColumn,
                        $"{commissionReference}*2%",
                        retentionValue);
                }

                if (layout.TotalFinalColumn is { } totalFinalColumn &&
                    layout.RetentionColumn is { } totalRetentionColumn)
                {
                    var retentionReference = GetCellReference(totalRetentionColumn, row.Key);
                    UpdateCalculatedCell(
                        row.Value,
                        cellsByReference,
                        totalFinalColumn,
                        $"{commissionReference}-{retentionReference}",
                        commissionValue - retentionValue);
                }

            }
        }

        if (!changed)
        {
            return;
        }

        worksheet.Save();
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        if (stylesheet?.CellFormats is not null)
        {
            stylesheet.CellFormats.Count = (uint)stylesheet.CellFormats.ChildElements.Count;
            stylesheet.Save();
        }
    }

    private static List<BrokerPercentageHeaderLayout> FindHeaderLayouts(WorksheetData worksheet)
    {
        var layouts = new List<BrokerPercentageHeaderLayout>();
        foreach (var row in worksheet.Rows.OrderBy(row => row.Key))
        {
            var percentageHeaders = row.Value
                .Where(cell => IsPercentageHeader(CanonicalHeader(cell.NormalizedText)))
                .OrderBy(cell => cell.ColumnIndex)
                .ToList();
            foreach (var percentageHeader in percentageHeaders)
            {
                var previousPercentageColumn = percentageHeaders
                    .Where(cell => cell.ColumnIndex < percentageHeader.ColumnIndex)
                    .Select(cell => cell.ColumnIndex)
                    .DefaultIfEmpty(0)
                    .Max();
                var nextPercentageColumn = percentageHeaders
                    .Where(cell => cell.ColumnIndex > percentageHeader.ColumnIndex)
                    .Select(cell => cell.ColumnIndex)
                    .DefaultIfEmpty(int.MaxValue)
                    .Min();
                var candidates = row.Value.Where(cell =>
                        cell.ColumnIndex > previousPercentageColumn &&
                        cell.ColumnIndex < nextPercentageColumn)
                    .ToList();
                var gross = FindNearestHeader(
                    candidates,
                    percentageHeader.ColumnIndex,
                    normalized => normalized == "comision bruta",
                    preferBefore: true);
                var brokerCommission = FindNearestHeader(
                    candidates,
                    percentageHeader.ColumnIndex,
                    normalized => normalized == "comision corredor",
                    preferBefore: false);
                if (gross is null || brokerCommission is null)
                {
                    continue;
                }

                var retention = FindNearestHeader(
                    candidates,
                    percentageHeader.ColumnIndex,
                    normalized => normalized is "2%" or "retencion 2%" or "retencion del 2%",
                    preferBefore: false);
                var totalFinal = FindNearestHeader(
                    candidates,
                    percentageHeader.ColumnIndex,
                    normalized => normalized == "total final",
                    preferBefore: false);
                layouts.Add(new BrokerPercentageHeaderLayout(
                    row.Key,
                    gross.ColumnIndex,
                    percentageHeader.ColumnIndex,
                    brokerCommission.ColumnIndex,
                    retention?.ColumnIndex,
                    totalFinal?.ColumnIndex));
            }
        }

        return layouts;
    }

    private static WorksheetCellData? FindNearestHeader(
        IEnumerable<WorksheetCellData> cells,
        int percentageColumn,
        Func<string, bool> matches,
        bool preferBefore)
    {
        return cells
            .Where(cell => matches(CanonicalHeader(cell.NormalizedText)))
            .OrderBy(cell =>
                preferBefore
                    ? cell.ColumnIndex < percentageColumn ? 0 : 1
                    : cell.ColumnIndex > percentageColumn ? 0 : 1)
            .ThenBy(cell => Math.Abs(cell.ColumnIndex - percentageColumn))
            .FirstOrDefault();
    }

    private static string CanonicalHeader(string normalized) =>
        normalized.Replace(" %", "%", StringComparison.Ordinal);

    private static bool IsPercentageHeader(string normalized) =>
        normalized is "% corredor" or "porcentaje corredor";

    private static WorksheetCellData? FindCell(IEnumerable<WorksheetCellData> cells, int column) =>
        cells.FirstOrDefault(cell => cell.ColumnIndex == column);

    private static bool CellHasContent(Cell? cell) =>
        cell is not null &&
        (cell.CellFormula is not null ||
         !string.IsNullOrWhiteSpace(cell.CellValue?.Text) ||
         !string.IsNullOrWhiteSpace(cell.InlineString?.InnerText));

    private static bool IsNumericCell(Cell? cell) =>
        cell is not null &&
        (cell.DataType is null || cell.DataType.Value == CellValues.Number);

    private static bool HasPercentageNumberFormat(Cell? cell, Stylesheet? stylesheet)
    {
        if (cell is null ||
            stylesheet?.CellFormats is null ||
            cell.StyleIndex?.Value is not { } styleIndex ||
            styleIndex >= stylesheet.CellFormats.ChildElements.Count ||
            stylesheet.CellFormats.ChildElements[(int)styleIndex] is not CellFormat format ||
            format.NumberFormatId?.Value is not { } numberFormatId)
        {
            return false;
        }

        if (numberFormatId is 9U or 10U)
        {
            return true;
        }

        var formatCode = stylesheet.NumberingFormats?.Elements<NumberingFormat>()
            .FirstOrDefault(format => format.NumberFormatId?.Value == numberFormatId)
            ?.FormatCode?.Value;
        return ContainsUnescapedPercentageSymbol(formatCode);
    }

    private static bool ContainsUnescapedPercentageSymbol(string? formatCode)
    {
        var quoted = false;
        var escaped = false;
        foreach (var character in formatCode ?? string.Empty)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && character == '%')
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteNormalizedPercentage(
        Cell cell,
        decimal percentage,
        WorkbookPart workbookPart,
        IDictionary<uint, uint> styleIndexes)
    {
        cell.CellFormula = null;
        cell.InlineString = null;
        cell.DataType = CellValues.Number;
        cell.CellValue = new CellValue(percentage.ToString(CultureInfo.InvariantCulture));
        cell.StyleIndex = GetTwoDecimalStyleIndex(cell.StyleIndex?.Value ?? 0U, workbookPart, styleIndexes);
    }

    private static uint GetTwoDecimalStyleIndex(
        uint sourceStyleIndex,
        WorkbookPart workbookPart,
        IDictionary<uint, uint> styleIndexes)
    {
        if (styleIndexes.TryGetValue(sourceStyleIndex, out var existing))
        {
            return existing;
        }

        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet
            ?? throw new InvalidDataException("El libro no contiene estilos válidos.");
        var cellFormats = stylesheet.CellFormats
            ?? throw new InvalidDataException("El libro no contiene formatos de celda válidos.");
        var sourceFormat = sourceStyleIndex < cellFormats.ChildElements.Count &&
                           cellFormats.ChildElements[(int)sourceStyleIndex] is CellFormat existingFormat
            ? existingFormat
            : new CellFormat();
        var normalizedFormat = (CellFormat)sourceFormat.CloneNode(true);
        normalizedFormat.NumberFormatId = TwoDecimalNumberFormatId;
        normalizedFormat.ApplyNumberFormat = true;
        cellFormats.Append(normalizedFormat);
        var normalizedIndex = (uint)(cellFormats.ChildElements.Count - 1);
        styleIndexes[sourceStyleIndex] = normalizedIndex;
        return normalizedIndex;
    }

    private static void UpdateCalculatedCell(
        IReadOnlyList<WorksheetCellData> row,
        IReadOnlyDictionary<string, Cell> cellsByReference,
        int column,
        string formula,
        decimal? calculatedValue)
    {
        var data = FindCell(row, column);
        if (data is null || !cellsByReference.TryGetValue(data.Reference, out var cell))
        {
            return;
        }

        if (cell.CellFormula is not null)
        {
            cell.InlineString = null;
            cell.DataType = null;
            cell.CellFormula = new CellFormula(formula);
            cell.CellValue = calculatedValue is { } cached
                ? new CellValue(cached.ToString(CultureInfo.InvariantCulture))
                : null;
            return;
        }

        if (!TryReadDecimal(cell, out _))
        {
            return;
        }

        cell.InlineString = null;
        cell.DataType = CellValues.Number;
        cell.CellValue = calculatedValue is { } value
            ? new CellValue(value.ToString(CultureInfo.InvariantCulture))
            : null;
    }

    private static bool TryReadDecimal(Cell cell, out decimal value)
    {
        if (cell.DataType?.Value == CellValues.Error)
        {
            value = default;
            return false;
        }

        var raw = cell.CellValue?.Text;
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return decimal.TryParse(
            raw?.Replace(',', '.'),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static string GetCellReference(int column, uint row)
    {
        var dividend = column;
        var columnName = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = (char)('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }

        return $"{columnName}{row}";
    }

    private static InvalidDataException CreateInvalidPercentageException(
        string worksheetName,
        string cellReference,
        uint row,
        string rawValue,
        Exception innerException)
    {
        var displayValue = string.IsNullOrWhiteSpace(rawValue)
            ? "(vacío)"
            : string.Join(' ', rawValue.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (displayValue.Length > 60)
        {
            displayValue = $"{displayValue[..57]}...";
        }

        return new InvalidDataException(
            $"No se pudo generar la pestaña '{worksheetName}'.{Environment.NewLine}" +
            $"El porcentaje del corredor en la celda {cellReference} (fila {row}) no es válido: “{displayValue}”.{Environment.NewLine}" +
            "Corrija el Excel general e intente nuevamente.",
            innerException);
    }

    private sealed record BrokerPercentageHeaderLayout(
        uint HeaderRow,
        int GrossCommissionColumn,
        int PercentageColumn,
        int BrokerCommissionColumn,
        int? RetentionColumn,
        int? TotalFinalColumn);
}
