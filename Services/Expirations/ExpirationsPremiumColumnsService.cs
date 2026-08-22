using System.Globalization;
using System.Text;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsPremiumColumnsService
{
    ExpirationsPremiumColumnResolution Resolve(
        string sourceWorkbookPath,
        string worksheetName,
        uint headerRowNumber,
        ExpirationsPremiumColumnOptions? options = null);
}

public sealed class ExpirationsPremiumColumnsService(
    IExpirationsWorkbookInspectionService? inspectionService = null) : IExpirationsPremiumColumnsService
{
    private readonly IExpirationsWorkbookInspectionService _inspectionService =
        inspectionService ?? new ExpirationsWorkbookInspectionService();

    public ExpirationsPremiumColumnResolution Resolve(
        string sourceWorkbookPath,
        string worksheetName,
        uint headerRowNumber,
        ExpirationsPremiumColumnOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceWorkbookPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(worksheetName);
        if (headerRowNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(headerRowNumber));

        var inspection = _inspectionService.Inspect(sourceWorkbookPath);
        var worksheet = inspection.Worksheets.FirstOrDefault(item =>
            string.Equals(item.WorksheetName, worksheetName, StringComparison.Ordinal));
        if (worksheet is null)
            return Failure(ExpirationsPremiumColumnResolutionStatus.WorksheetNotFound);
        var header = worksheet.HeaderRows.FirstOrDefault(item => item.RowNumber == headerRowNumber);
        if (header is null)
            return Failure(ExpirationsPremiumColumnResolutionStatus.HeaderRowNotFound);

        var premium = ResolveColumn(
            header.Columns,
            options?.PremiumColumnIndex,
            "PRIMA",
            ExpirationsPremiumColumnResolutionStatus.MissingPremiumColumn,
            ExpirationsPremiumColumnResolutionStatus.AmbiguousPremiumColumn,
            ExpirationsPremiumColumnResolutionStatus.InvalidPremiumColumnOverride);
        if (premium.Status != ExpirationsPremiumColumnResolutionStatus.Success)
            return Failure(premium.Status);

        var currency = ResolveColumn(
            header.Columns,
            options?.CurrencyColumnIndex,
            "MONEDA",
            ExpirationsPremiumColumnResolutionStatus.MissingCurrencyColumn,
            ExpirationsPremiumColumnResolutionStatus.AmbiguousCurrencyColumn,
            ExpirationsPremiumColumnResolutionStatus.InvalidCurrencyColumnOverride);
        if (currency.Status != ExpirationsPremiumColumnResolutionStatus.Success)
            return Failure(currency.Status);
        if (premium.Column!.ColumnIndex == currency.Column!.ColumnIndex)
            return Failure(ExpirationsPremiumColumnResolutionStatus.ConflictingColumnOverrides);

        return new ExpirationsPremiumColumnResolution
        {
            Status = ExpirationsPremiumColumnResolutionStatus.Success,
            PremiumColumnIndex = premium.Column.ColumnIndex,
            PremiumColumnReference = premium.Column.ColumnReference.ToUpperInvariant(),
            CurrencyColumnIndex = currency.Column.ColumnIndex,
            CurrencyColumnReference = currency.Column.ColumnReference.ToUpperInvariant()
        };
    }

    private static ColumnResult ResolveColumn(
        IReadOnlyList<ExpirationsColumnInspection> columns,
        int? overrideIndex,
        string expectedHeader,
        ExpirationsPremiumColumnResolutionStatus missingStatus,
        ExpirationsPremiumColumnResolutionStatus ambiguousStatus,
        ExpirationsPremiumColumnResolutionStatus invalidOverrideStatus)
    {
        if (overrideIndex.HasValue)
        {
            if (overrideIndex.Value <= 0)
                return new ColumnResult(invalidOverrideStatus, null);
            var overridden = columns.SingleOrDefault(column => column.ColumnIndex == overrideIndex.Value);
            return overridden is null
                ? new ColumnResult(invalidOverrideStatus, null)
                : new ColumnResult(ExpirationsPremiumColumnResolutionStatus.Success, overridden);
        }

        var matches = columns
            .Where(column => NormalizeHeader(column.HeaderText) == expectedHeader)
            .ToList();
        return matches.Count switch
        {
            0 => new ColumnResult(missingStatus, null),
            1 => new ColumnResult(ExpirationsPremiumColumnResolutionStatus.Success, matches[0]),
            _ => new ColumnResult(ambiguousStatus, null)
        };
    }

    private static string NormalizeHeader(string value)
    {
        var decomposed = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(character))
                result.Append(char.ToUpperInvariant(character));
        }
        return result.ToString();
    }

    private static ExpirationsPremiumColumnResolution Failure(
        ExpirationsPremiumColumnResolutionStatus status) => new() { Status = status };

    private sealed record ColumnResult(
        ExpirationsPremiumColumnResolutionStatus Status,
        ExpirationsColumnInspection? Column);
}
