using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public sealed class WorkbookAnalysisService
{
    private readonly IReadOnlyList<ICommissionWorksheetAnalyzer> _analyzers;
    private readonly GeneratedFileHashService _hashService;

    public WorkbookAnalysisService(
        IEnumerable<ICommissionWorksheetAnalyzer>? analyzers = null,
        GeneratedFileHashService? hashService = null)
    {
        _analyzers = (analyzers ??
            [new StandardCommissionWorksheetAnalyzer(), new FlmCommissionWorksheetAnalyzer()]).ToList();
        _hashService = hashService ?? new GeneratedFileHashService();
    }

    public WorkbookAnalysisResult Analyze(string path)
    {
        var result = new WorkbookAnalysisResult { WorkbookPath = path };
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            result.Errors.Add("El Excel general no existe.");
            return result;
        }

        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add(
                "La generación fiel requiere un libro .xlsx. Los formatos .xls y .xlsm no se modifican " +
                "para evitar pérdida de contenido o macros.");
            return result;
        }

        try
        {
            result.WorkbookSha256 = _hashService.ComputeSha256(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = SpreadsheetDocument.Open(stream, false);
            var workbookPart = document.WorkbookPart
                ?? throw new InvalidDataException("El archivo no contiene una estructura de libro válida.");
            var workbook = workbookPart.Workbook
                ?? throw new InvalidDataException("El archivo no contiene la definición del libro.");
            var sheets = workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
            if (sheets.Count == 0)
            {
                result.Errors.Add("El Excel general no contiene pestañas.");
                return result;
            }

            foreach (var sheet in sheets)
            {
                var worksheetName = sheet.Name?.Value ?? string.Empty;
                if (sheet.Id?.Value is not { } relationshipId ||
                    workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
                {
                    result.Errors.Add($"La pestaña '{worksheetName}' no es una hoja de cálculo compatible.");
                    continue;
                }

                var worksheet = OpenXmlWorksheetReader.Read(workbookPart, worksheetPart, worksheetName);
                var candidates = _analyzers
                    .Select(value => new { Analyzer = value, Confidence = value.GetConfidence(worksheet) })
                    .Where(value => value.Confidence > 0)
                    .OrderByDescending(value => value.Confidence)
                    .ToList();
                if (candidates.Count == 0)
                {
                    result.Worksheets.Add(new CommissionWorksheetAnalysis
                    {
                        WorksheetName = worksheetName,
                        Errors =
                        [
                            $"No se reconocieron encabezados, moneda ni etiquetas de resumen en la pestaña '{worksheetName}'."
                        ]
                    });
                    continue;
                }

                CommissionWorksheetAnalysis? selected = null;
                foreach (var candidate in candidates)
                {
                    var analyzed = candidate.Analyzer.Analyze(worksheet);
                    if (analyzed.IsValid)
                    {
                        selected = analyzed;
                        break;
                    }

                    selected ??= analyzed;
                }

                result.Worksheets.Add(selected!);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result.Errors.Add($"No se pudo leer el Excel general: {ex.Message}");
        }
        catch (DocumentFormat.OpenXml.Packaging.OpenXmlPackageException ex)
        {
            result.Errors.Add($"El archivo no es un libro .xlsx válido: {ex.Message}");
        }

        return result;
    }
}
