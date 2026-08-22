using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsPremiumTotalsPlanner
{
    ExpirationsPremiumTotalsPlan CreatePlan(
        string worksheetName,
        uint headerRowNumber,
        IReadOnlyList<uint> sourceRowNumbers,
        ExpirationsPremiumColumnResolution columns,
        IReadOnlyList<ExpirationsPremiumRowInspection> rows);
}

public sealed class ExpirationsPremiumTotalsPlanner(
    IExpirationsCurrencyClassifier? currencyClassifier = null) : IExpirationsPremiumTotalsPlanner
{
    private readonly IExpirationsCurrencyClassifier _currencyClassifier =
        currencyClassifier ?? new ExpirationsCurrencyClassifier();

    public ExpirationsPremiumTotalsPlan CreatePlan(
        string worksheetName,
        uint headerRowNumber,
        IReadOnlyList<uint> sourceRowNumbers,
        ExpirationsPremiumColumnResolution columns,
        IReadOnlyList<ExpirationsPremiumRowInspection> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worksheetName);
        ArgumentNullException.ThrowIfNull(sourceRowNumbers);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        if (!columns.IsSuccess)
            throw new ExpirationsGenerationException("No se resolvieron las columnas Prima y Moneda.");

        var selectedRows = sourceRowNumbers.Distinct().Order().ToArray();
        if (selectedRows.Length == 0)
            throw new ExpirationsGenerationException("No existen filas para calcular las primas del corredor.");
        if (headerRowNumber == 0 || selectedRows.Any(rowNumber => rowNumber <= headerRowNumber))
            throw new ExpirationsGenerationException("La distribución contiene una fila que no pertenece a la zona de datos.");

        var inspectionsByRow = rows.GroupBy(row => row.RowNumber).ToDictionary(group => group.Key, group => group.ToList());
        if (inspectionsByRow.Any(item => item.Value.Count != 1) ||
            !selectedRows.SequenceEqual(inspectionsByRow.Keys.Order()))
        {
            throw new ExpirationsGenerationException("La inspección de primas no coincide con las filas seleccionadas.");
        }

        // Excel compara texto con '=' sin distinguir mayúsculas. El mismo criterio
        // con otra capitalización no puede convertirse en un segundo SUMPRODUCT.
        var crcLabels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var usdLabels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rowNumber in selectedRows)
        {
            var row = inspectionsByRow[rowNumber][0];
            DemandNumericPremium(row);
            if (row.CurrencyHasFormula)
                throw new ExpirationsGenerationException($"Fila {row.RowNumber}: la moneda contiene una fórmula.");
            if (string.IsNullOrWhiteSpace(row.RawCurrencyText))
                throw new ExpirationsGenerationException($"Fila {row.RowNumber}: la moneda está vacía.");
            switch (_currencyClassifier.Classify(row.RawCurrencyText))
            {
                case ExpirationsCurrency.Crc:
                    crcLabels.Add(row.RawCurrencyText);
                    break;
                case ExpirationsCurrency.Usd:
                    usdLabels.Add(row.RawCurrencyText);
                    break;
                default:
                    throw new ExpirationsGenerationException(
                        $"Fila {row.RowNumber}: la moneda '{row.RawCurrencyText}' no está reconocida.");
            }
        }

        var dataFirstRow = checked(headerRowNumber + 1);
        var dataLastRow = checked(headerRowNumber + (uint)selectedRows.Length);
        return new ExpirationsPremiumTotalsPlan
        {
            WorksheetName = worksheetName,
            PremiumColumnReference = columns.PremiumColumnReference,
            CurrencyColumnReference = columns.CurrencyColumnReference,
            HeaderRowNumber = headerRowNumber,
            DataFirstRow = dataFirstRow,
            DataLastRow = dataLastRow,
            RowCount = selectedRows.Length,
            ObservedCrcLabels = crcLabels.ToArray(),
            ObservedUsdLabels = usdLabels.ToArray(),
            CrcFormula = BuildFormula(
                worksheetName,
                columns.PremiumColumnReference,
                columns.CurrencyColumnReference,
                dataFirstRow,
                dataLastRow,
                crcLabels),
            UsdFormula = BuildFormula(
                worksheetName,
                columns.PremiumColumnReference,
                columns.CurrencyColumnReference,
                dataFirstRow,
                dataLastRow,
                usdLabels)
        };
    }

    private static void DemandNumericPremium(ExpirationsPremiumRowInspection row)
    {
        switch (row.PremiumCellKind)
        {
            case ExpirationsPremiumCellKind.Numeric when row.NumericPremiumValue.HasValue:
                return;
            case ExpirationsPremiumCellKind.Missing:
                throw new ExpirationsGenerationException($"Fila {row.RowNumber}: la prima está vacía.");
            case ExpirationsPremiumCellKind.Formula:
                throw new ExpirationsGenerationException($"Fila {row.RowNumber}: la prima contiene una fórmula.");
            default:
                throw new ExpirationsGenerationException(
                    $"Fila {row.RowNumber}: la prima '{row.RawPremiumText}' no es un valor numérico de Excel.");
        }
    }

    private static string BuildFormula(
        string worksheetName,
        string premiumColumn,
        string currencyColumn,
        uint firstRow,
        uint lastRow,
        IEnumerable<string> labels)
    {
        var criteria = labels.ToArray();
        if (criteria.Length == 0)
            return "0";
        var escapedSheet = worksheetName.Replace("'", "''", StringComparison.Ordinal);
        var sheetReference = $"'{escapedSheet}'!";
        var premiumRange = $"{sheetReference}${premiumColumn}${firstRow}:${premiumColumn}${lastRow}";
        var currencyRange = $"{sheetReference}${currencyColumn}${firstRow}:${currencyColumn}${lastRow}";
        return string.Join(
            "+",
            criteria.Select(label =>
                $"SUMPRODUCT(--({currencyRange}=\"{label.Replace("\"", "\"\"", StringComparison.Ordinal)}\"),{premiumRange})"));
    }
}
