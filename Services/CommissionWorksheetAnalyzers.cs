using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public interface ICommissionWorksheetAnalyzer
{
    string Name { get; }
    int GetConfidence(WorksheetData worksheet);
    CommissionWorksheetAnalysis Analyze(WorksheetData worksheet);
}

public sealed class WorksheetData
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<WorksheetCellData> Cells { get; init; } = [];
    public IReadOnlyDictionary<uint, IReadOnlyList<WorksheetCellData>> Rows { get; init; } =
        new Dictionary<uint, IReadOnlyList<WorksheetCellData>>();
}

public sealed class WorksheetCellData
{
    public string Reference { get; init; } = string.Empty;
    public uint RowIndex { get; init; }
    public int ColumnIndex { get; init; }
    public string Text { get; init; } = string.Empty;
    public string NormalizedText { get; init; } = string.Empty;
    public decimal? NumericValue { get; init; }
    public string NumberFormat { get; init; } = string.Empty;
}

public sealed class StandardCommissionWorksheetAnalyzer : ICommissionWorksheetAnalyzer
{
    public string Name => "Estándar";

    public int GetConfidence(WorksheetData worksheet)
    {
        if (FindTabularHeader(worksheet) is not null)
        {
            return 100;
        }

        return CommissionWorksheetAnalyzerSupport.FindGrossSummaryCells(worksheet).Count > 0 ? 35 : 0;
    }

    public CommissionWorksheetAnalysis Analyze(WorksheetData worksheet)
    {
        var result = CommissionWorksheetAnalyzerSupport.CreateResult(worksheet.Name, Name);
        if (CommissionWorksheetAnalyzerSupport.FindGrossSummaryCells(worksheet).Count > 0)
        {
            return CommissionWorksheetAnalyzerSupport.AnalyzeSummaryLayout(worksheet, result);
        }

        var header = FindTabularHeader(worksheet);
        if (header is null)
        {
            return CommissionWorksheetAnalyzerSupport.AnalyzeSummaryLayout(worksheet, result);
        }

        var currenciesSeen = new HashSet<DeductionCurrency>();
        foreach (var row in worksheet.Rows.Where(value => value.Key > header.Value.Row).OrderBy(value => value.Key))
        {
            var currencyCell = row.Value.FirstOrDefault(value => value.ColumnIndex == header.Value.CurrencyColumn);
            if (!CommissionWorksheetAnalyzerSupport.TryParseCurrency(currencyCell?.Text, out var currency))
            {
                continue;
            }

            var amountCell = row.Value.FirstOrDefault(value => value.ColumnIndex == header.Value.CommissionColumn);
            if (amountCell?.NumericValue is not { } amount ||
                row.Value.Any(value => CommissionWorksheetAnalyzerSupport.IsSummaryLabel(value.NormalizedText)))
            {
                continue;
            }

            var summary = currency == DeductionCurrency.CRC ? result.Crc : result.Usd;
            summary.HasCommission = true;
            summary.GrossCommission += amount;
            summary.SourceCells.Add(amountCell.Reference);
            currenciesSeen.Add(currency);
        }

        result.Crc.GrossCommission = PaymentCalculationService.Round(result.Crc.GrossCommission);
        result.Usd.GrossCommission = PaymentCalculationService.Round(result.Usd.GrossCommission);
        if (currenciesSeen.Count == 0)
        {
            result.Errors.Add(
                $"No se encontraron filas de comisión interpretables debajo de los encabezados de la pestaña '{worksheet.Name}'.");
        }

        CommissionWorksheetAnalyzerSupport.AddNegativeWarnings(result);
        return result;
    }

    private static (uint Row, int CurrencyColumn, int CommissionColumn)? FindTabularHeader(WorksheetData worksheet)
    {
        foreach (var row in worksheet.Rows.OrderBy(value => value.Key))
        {
            var currency = row.Value.FirstOrDefault(value =>
                value.NormalizedText is "moneda" or "currency" ||
                value.NormalizedText.Contains("tipo moneda", StringComparison.Ordinal));
            var commission = row.Value.FirstOrDefault(value =>
                CommissionWorksheetAnalyzerSupport.IsCommissionHeader(value.NormalizedText));
            if (currency is not null && commission is not null && currency.ColumnIndex != commission.ColumnIndex)
            {
                return (row.Key, currency.ColumnIndex, commission.ColumnIndex);
            }
        }

        return null;
    }
}

public sealed class FlmCommissionWorksheetAnalyzer : ICommissionWorksheetAnalyzer
{
    public string Name => "FLM / resumen estructural";

    public int GetConfidence(WorksheetData worksheet)
    {
        var grossCells = CommissionWorksheetAnalyzerSupport.FindGrossSummaryCells(worksheet);
        if (grossCells.Count == 0)
        {
            return 0;
        }

        var currencyHeadings = worksheet.Cells.Count(value =>
            CommissionWorksheetAnalyzerSupport.TryParseCurrency(value.Text, out _));
        return currencyHeadings > 0 ? 90 : 55;
    }

    public CommissionWorksheetAnalysis Analyze(WorksheetData worksheet)
    {
        var result = CommissionWorksheetAnalyzerSupport.AnalyzeSummaryLayout(
            worksheet,
            CommissionWorksheetAnalyzerSupport.CreateResult(worksheet.Name, Name));
        result.Warnings.Insert(0,
            $"La pestaña '{worksheet.Name}' usa el formato estructural FLM y fue analizada con su adaptador específico.");
        return result;
    }
}

internal static class CommissionWorksheetAnalyzerSupport
{
    private static readonly string[] GrossLabels =
    [
        "monto bruto comision",
        "monto bruto comison",
        "comision bruta",
        "total comision corredor",
        "total comisiones corredor"
    ];

    public static CommissionWorksheetAnalysis CreateResult(string worksheetName, string analyzerName) => new()
    {
        WorksheetName = worksheetName,
        AnalyzerName = analyzerName,
        Crc = new CommissionCurrencySummary { Currency = DeductionCurrency.CRC },
        Usd = new CommissionCurrencySummary { Currency = DeductionCurrency.USD }
    };

    public static bool IsCommissionHeader(string normalized) =>
        (normalized.Contains("comision", StringComparison.Ordinal) ||
         normalized.Contains("comison", StringComparison.Ordinal)) &&
        !normalized.Contains("iva", StringComparison.Ordinal) &&
        !normalized.Contains("retencion", StringComparison.Ordinal) &&
        (normalized.Contains("corredor", StringComparison.Ordinal) ||
         normalized.Contains("brut", StringComparison.Ordinal) ||
         normalized is "comision" or "comison" or "monto comision");

    public static bool IsSummaryLabel(string normalized) =>
        normalized.StartsWith("total", StringComparison.Ordinal) ||
        normalized.Contains("monto bruto", StringComparison.Ordinal) ||
        normalized.Contains("monto factura", StringComparison.Ordinal) ||
        normalized.Contains("monto depositado", StringComparison.Ordinal) ||
        normalized.Contains("retencion", StringComparison.Ordinal) ||
        normalized is "iva" or "iva 13";

    public static List<WorksheetCellData> FindGrossSummaryCells(WorksheetData worksheet) =>
        worksheet.Cells.Where(value =>
                GrossLabels.Any(label => value.NormalizedText.Contains(label, StringComparison.Ordinal)) &&
                FindAmountNearLabel(worksheet, value) is not null)
            .ToList();

    public static CommissionWorksheetAnalysis AnalyzeSummaryLayout(
        WorksheetData worksheet,
        CommissionWorksheetAnalysis result)
    {
        var grossLabels = FindGrossSummaryCells(worksheet);
        if (grossLabels.Count == 0)
        {
            result.Errors.Add(
                $"No se encontró el encabezado de comisión ni una etiqueta de monto bruto en la pestaña '{worksheet.Name}'.");
            return result;
        }

        foreach (var label in grossLabels)
        {
            var amount = FindAmountNearLabel(worksheet, label);
            if (amount is null)
            {
                result.Errors.Add(
                    $"No se encontró un valor numérico asociado a '{label.Text}' en {label.Reference}.");
                continue;
            }

            if (!TryInferCurrency(worksheet, label, amount, out var currency))
            {
                result.Errors.Add(
                    $"No se pudo determinar la moneda del monto ubicado en {amount.Reference}, asociado a {label.Reference}.");
                continue;
            }

            var summary = currency == DeductionCurrency.CRC ? result.Crc : result.Usd;
            if (summary.HasCommission)
            {
                result.Errors.Add(
                    $"Se encontraron varios montos brutos para {currency} ({string.Join(", ", summary.SourceCells)}, {amount.Reference}).");
                continue;
            }

            summary.HasCommission = true;
            summary.GrossCommission = PaymentCalculationService.Round(amount.NumericValue!.Value);
            summary.SourceCells.Add(amount.Reference);
        }

        AddNegativeWarnings(result);
        return result;
    }

    public static bool TryParseCurrency(string? value, out DeductionCurrency currency)
    {
        var normalized = Normalize(value);
        if (normalized is "crc" or "colon" or "colones" or "₡" ||
            normalized.Contains("colon costarricense", StringComparison.Ordinal))
        {
            currency = DeductionCurrency.CRC;
            return true;
        }

        if (normalized is "usd" or "dolar" or "dolares" or "$" or "us$" ||
            normalized.Contains("dolar estadounidense", StringComparison.Ordinal))
        {
            currency = DeductionCurrency.USD;
            return true;
        }

        currency = default;
        return false;
    }

    public static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return string.Join(' ', builder.ToString()
            .Normalize(NormalizationForm.FormC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static void AddNegativeWarnings(CommissionWorksheetAnalysis result)
    {
        foreach (var summary in new[] { result.Crc, result.Usd })
        {
            if (summary.HasCommission && summary.GrossCommission < 0)
            {
                result.Warnings.Add(
                    $"La comisión detectada en {summary.Currency} es negativa ({summary.GrossCommission:N2}).");
            }
        }
    }

    private static WorksheetCellData? FindAmountNearLabel(WorksheetData worksheet, WorksheetCellData label)
    {
        if (!worksheet.Rows.TryGetValue(label.RowIndex, out var row))
        {
            return null;
        }

        return row.Where(value => value.ColumnIndex > label.ColumnIndex && value.ColumnIndex <= label.ColumnIndex + 6)
            .OrderBy(value => value.ColumnIndex)
            .FirstOrDefault(value => value.NumericValue.HasValue);
    }

    private static bool TryInferCurrency(
        WorksheetData worksheet,
        WorksheetCellData label,
        WorksheetCellData amount,
        out DeductionCurrency currency)
    {
        if (amount.NumberFormat.Contains('₡') ||
            amount.NumberFormat.Contains("CRC", StringComparison.OrdinalIgnoreCase))
        {
            currency = DeductionCurrency.CRC;
            return true;
        }

        if (amount.NumberFormat.Contains('$') ||
            amount.NumberFormat.Contains("USD", StringComparison.OrdinalIgnoreCase))
        {
            currency = DeductionCurrency.USD;
            return true;
        }

        var candidates = worksheet.Cells
            .Where(value =>
                value.RowIndex <= label.RowIndex &&
                label.RowIndex - value.RowIndex <= 30 &&
                Math.Min(
                    Math.Abs(value.ColumnIndex - label.ColumnIndex),
                    Math.Abs(value.ColumnIndex - amount.ColumnIndex)) <= 3 &&
                TryParseCurrency(value.Text, out _))
            .OrderBy(value => label.RowIndex - value.RowIndex)
            .ThenBy(value => Math.Abs(value.ColumnIndex - amount.ColumnIndex))
            .ToList();
        if (candidates.Count > 0 && TryParseCurrency(candidates[0].Text, out currency))
        {
            return true;
        }

        var sameRowCurrency = worksheet.Rows[label.RowIndex]
            .OrderBy(value => Math.Abs(value.ColumnIndex - label.ColumnIndex))
            .FirstOrDefault(value => TryParseCurrency(value.Text, out _));
        return TryParseCurrency(sameRowCurrency?.Text, out currency);
    }
}

internal static class OpenXmlWorksheetReader
{
    public static WorksheetData Read(
        WorkbookPart workbookPart,
        WorksheetPart worksheetPart,
        string worksheetName)
    {
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        var cells = new List<WorksheetCellData>();
        var worksheet = worksheetPart.Worksheet
            ?? throw new InvalidDataException($"La pestaña '{worksheetName}' no contiene datos de hoja.");
        foreach (var cell in worksheet.Descendants<Cell>())
        {
            var reference = cell.CellReference?.Value ?? string.Empty;
            var text = GetCellText(cell, sharedStrings);
            cells.Add(new WorksheetCellData
            {
                Reference = reference,
                RowIndex = GetRowIndex(reference, cell),
                ColumnIndex = GetColumnIndex(reference),
                Text = text,
                NormalizedText = CommissionWorksheetAnalyzerSupport.Normalize(text),
                NumericValue = GetNumericValue(cell),
                NumberFormat = GetNumberFormat(cell, stylesheet)
            });
        }

        return new WorksheetData
        {
            Name = worksheetName,
            Cells = cells,
            Rows = cells.GroupBy(value => value.RowIndex)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<WorksheetCellData>)group.OrderBy(value => value.ColumnIndex).ToList())
        };
    }

    private static string GetCellText(Cell cell, SharedStringTable? sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(cell.CellValue?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
            sharedStrings is not null && index >= 0 && index < sharedStrings.ChildElements.Count)
        {
            return sharedStrings.ChildElements[index].InnerText;
        }

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        return cell.CellValue?.Text ?? cell.InnerText ?? string.Empty;
    }

    private static decimal? GetNumericValue(Cell cell)
    {
        if (cell.DataType is not null && cell.DataType.Value != CellValues.Number)
        {
            return null;
        }

        return decimal.TryParse(cell.CellValue?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string GetNumberFormat(Cell cell, Stylesheet? stylesheet)
    {
        if (stylesheet?.CellFormats is null || cell.StyleIndex?.Value is not { } styleIndex ||
            styleIndex >= stylesheet.CellFormats.ChildElements.Count ||
            stylesheet.CellFormats.ChildElements[(int)styleIndex] is not CellFormat format ||
            format.NumberFormatId?.Value is not { } numberFormatId)
        {
            return string.Empty;
        }

        if (numberFormatId >= 164 && stylesheet.NumberingFormats is not null)
        {
            return stylesheet.NumberingFormats.Elements<NumberingFormat>()
                .FirstOrDefault(value => value.NumberFormatId?.Value == numberFormatId)
                ?.FormatCode?.Value ?? string.Empty;
        }

        return numberFormatId switch
        {
            5 or 6 or 7 or 8 => "$",
            _ => string.Empty
        };
    }

    private static uint GetRowIndex(string reference, Cell cell)
    {
        if (cell.Parent is Row row && row.RowIndex?.Value is { } rowIndex)
        {
            return rowIndex;
        }

        var digits = new string(reference.Where(char.IsDigit).ToArray());
        return uint.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static int GetColumnIndex(string reference)
    {
        var result = 0;
        foreach (var character in reference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            result = (result * 26) + char.ToUpperInvariant(character) - 'A' + 1;
        }

        return result;
    }
}
