using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsStandardWorkbookGenerator
{
    void Generate(ExpirationsStandardWorkbookGenerationRequest request, CancellationToken cancellationToken = default);
}

public sealed class ExpirationsStandardWorkbookGenerator : IExpirationsStandardWorkbookGenerator
{
    private static readonly Regex CellReferencePattern = new(
        @"^\$?(?<column>[A-Za-z]+)\$?(?<row>[0-9]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public void Generate(
        ExpirationsStandardWorkbookGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceWorkbookPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationWorkbookPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorksheetName);
        if (request.HeaderRowNumber == 0)
            throw new ExpirationsGenerationException("La fila de encabezado no es válida.");

        var sourceRows = request.SourceRowNumbers.Distinct().Order().ToArray();
        if (sourceRows.Length == 0)
            throw new ExpirationsGenerationException("No existen filas para materializar en el archivo del corredor.");
        if (sourceRows.Any(rowNumber => rowNumber <= request.HeaderRowNumber))
            throw new ExpirationsGenerationException("La distribución contiene una fila que no pertenece a la zona de datos.");

        var destinationPath = Path.GetFullPath(request.DestinationWorkbookPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath))
            throw new IOException($"El archivo staged ya existe: '{destinationPath}'.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(Path.GetFullPath(request.SourceWorkbookPath), destinationPath, overwrite: false);
            var copiedAttributes = File.GetAttributes(destinationPath);
            if ((copiedAttributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(destinationPath, copiedAttributes & ~FileAttributes.ReadOnly);
            var expectations = MaterializeCopy(destinationPath, request, sourceRows, cancellationToken);
            ValidateGeneratedWorkbook(destinationPath, request, sourceRows.Length, expectations);
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
    }

    private static WorkbookExpectations MaterializeCopy(
        string destinationPath,
        ExpirationsStandardWorkbookGenerationRequest request,
        IReadOnlyList<uint> sourceRows,
        CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Open(destinationPath, true);
        var workbookPart = document.WorkbookPart ??
            throw new ExpirationsGenerationException("El archivo no contiene un workbook válido.");
        var workbook = workbookPart.Workbook ??
            throw new ExpirationsGenerationException("El archivo no contiene la definición del workbook.");
        var sheets = workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
        var selectedSheet = sheets.SingleOrDefault(sheet =>
            string.Equals(sheet.Name?.Value, request.WorksheetName, StringComparison.Ordinal));
        if (selectedSheet is null || string.IsNullOrWhiteSpace(selectedSheet.Id?.Value))
            throw new ExpirationsGenerationException($"La hoja analizada '{request.WorksheetName}' ya no existe en el archivo.");
        var selectedSheetIndex = sheets.IndexOf(selectedSheet);
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(selectedSheet.Id!);

        DemandSupportedPackage(workbookPart, worksheetPart);
        var worksheet = worksheetPart.Worksheet ??
            throw new ExpirationsGenerationException("La hoja seleccionada no contiene una definición válida.");
        var originalColumnsXml = worksheet.GetFirstChild<Columns>()?.OuterXml ?? string.Empty;
        var sheetData = worksheet.GetFirstChild<SheetData>() ??
            throw new ExpirationsGenerationException("La hoja seleccionada no contiene datos.");
        var allRows = sheetData.Elements<Row>().ToList();
        var rowByNumber = allRows
            .Where(row => row.RowIndex?.Value is not null)
            .GroupBy(row => row.RowIndex!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        if (rowByNumber.Any(item => item.Value.Count != 1))
            throw new ExpirationsGenerationException("La hoja contiene números de fila duplicados y no puede filtrarse con seguridad.");

        var selectedSourceRows = new List<Row>(sourceRows.Count);
        foreach (var rowNumber in sourceRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rowByNumber.TryGetValue(rowNumber, out var matches))
                throw new ExpirationsGenerationException($"La fila fuente {rowNumber} ya no existe en la hoja analizada.");
            var sourceRow = matches[0];
            if (sourceRow.Descendants<CellFormula>().Any())
            {
                throw new ExpirationsGenerationException(
                    "El reporte contiene fórmulas en filas de datos.\nNo es seguro reubicar esas filas automáticamente.");
            }
            selectedSourceRows.Add((Row)sourceRow.CloneNode(true));
        }

        var rowMap = sourceRows
            .Select((sourceRowNumber, index) => new
            {
                Source = sourceRowNumber,
                Destination = checked(request.HeaderRowNumber + (uint)index + 1)
            })
            .ToDictionary(item => item.Source, item => item.Destination);
        UpdateMergedCells(worksheet, request.HeaderRowNumber, rowMap);

        foreach (var row in allRows.Where(row => (row.RowIndex?.Value ?? uint.MaxValue) > request.HeaderRowNumber))
            row.Remove();
        foreach (var sourceRow in selectedSourceRows)
        {
            var originalNumber = sourceRow.RowIndex!.Value;
            var destinationNumber = rowMap[originalNumber];
            sourceRow.RowIndex = destinationNumber;
            foreach (var cell in sourceRow.Elements<Cell>())
                cell.CellReference = ChangeRow(cell.CellReference?.Value, destinationNumber);
            sheetData.Append(sourceRow);
        }

        var lastRowNumber = checked(request.HeaderRowNumber + (uint)sourceRows.Count);
        UpdateAutoFilter(worksheet, lastRowNumber);
        UpdateTables(worksheetPart, request.HeaderRowNumber, lastRowNumber);
        UpdateSheetDimension(worksheet, lastRowNumber);
        RemoveOtherWorksheets(workbookPart, selectedSheet, selectedSheetIndex, sheets);
        if (workbookPart.CalculationChainPart is not null)
            workbookPart.DeletePart(workbookPart.CalculationChainPart);
        RebuildSharedStrings(workbookPart, worksheetPart);

        worksheet.Save();
        workbook.Save();
        return new WorkbookExpectations(originalColumnsXml);
    }

    private static void DemandSupportedPackage(WorkbookPart workbookPart, WorksheetPart worksheetPart)
    {
        if (workbookPart.GetPartsOfType<PivotTableCacheDefinitionPart>().Any() ||
            worksheetPart.PivotTableParts.Any())
        {
            throw new ExpirationsGenerationException(
                "El workbook contiene una tabla dinámica o caché no soportada para filtrado seguro.");
        }
        if (workbookPart.ExternalWorkbookParts.Any())
        {
            throw new ExpirationsGenerationException(
                "El workbook contiene vínculos externos no soportados para filtrado seguro.");
        }
        var worksheet = worksheetPart.Worksheet ??
            throw new ExpirationsGenerationException("La hoja seleccionada no contiene una definición válida.");
        if (worksheetPart.WorksheetCommentsPart is not null ||
            worksheet.Descendants<Hyperlink>().Any())
        {
            throw new ExpirationsGenerationException(
                "La hoja contiene comentarios o hipervínculos que no pueden remapearse con seguridad.");
        }
    }

    private static void UpdateMergedCells(
        Worksheet worksheet,
        uint headerRowNumber,
        IReadOnlyDictionary<uint, uint> rowMap)
    {
        var mergeCells = worksheet.Elements<MergeCells>().SingleOrDefault();
        if (mergeCells is null)
            return;

        foreach (var mergeCell in mergeCells.Elements<MergeCell>().ToList())
        {
            var range = ParseRange(mergeCell.Reference?.Value,
                "El workbook contiene una combinación de celdas no válida.");
            if (range.EndRow <= headerRowNumber)
                continue;
            if (range.StartRow <= headerRowNumber)
            {
                throw new ExpirationsGenerationException(
                    "El workbook contiene una combinación de celdas que atraviesa el encabezado y la zona de datos.");
            }

            var mappedRows = Enumerable.Range(0, checked((int)(range.EndRow - range.StartRow + 1)))
                .Select(offset => checked(range.StartRow + (uint)offset))
                .Where(rowMap.ContainsKey)
                .ToList();
            if (mappedRows.Count == 0)
            {
                mergeCell.Remove();
                continue;
            }
            if (mappedRows.Count != checked((int)(range.EndRow - range.StartRow + 1)))
            {
                throw new ExpirationsGenerationException(
                    "El workbook contiene una combinación de celdas en filas de datos que no puede trasladarse con seguridad.");
            }

            var destinationRows = mappedRows.Select(row => rowMap[row]).ToList();
            if (destinationRows[^1] - destinationRows[0] + 1 != destinationRows.Count)
            {
                throw new ExpirationsGenerationException(
                    "El workbook contiene una combinación de celdas no contigua después del filtrado.");
            }
            mergeCell.Reference = $"{range.StartColumn}{destinationRows[0]}:{range.EndColumn}{destinationRows[^1]}";
        }

        mergeCells.Count = (uint)mergeCells.ChildElements.Count;
        if (mergeCells.ChildElements.Count == 0)
            mergeCells.Remove();
    }

    private static void UpdateAutoFilter(Worksheet worksheet, uint lastRowNumber)
    {
        var autoFilter = worksheet.Elements<AutoFilter>().SingleOrDefault();
        if (autoFilter?.Reference?.Value is not { Length: > 0 } reference)
            return;
        var range = ParseRange(reference, "El AutoFilter de la hoja no tiene un rango compatible.");
        autoFilter.Reference = $"{range.StartColumn}{range.StartRow}:{range.EndColumn}{lastRowNumber}";
    }

    private static void UpdateTables(WorksheetPart worksheetPart, uint headerRowNumber, uint lastRowNumber)
    {
        foreach (var tablePart in worksheetPart.TableDefinitionParts)
        {
            var table = tablePart.Table;
            if (table?.Reference?.Value is not { Length: > 0 } reference)
                throw new ExpirationsGenerationException("La hoja contiene una tabla sin rango válido.");
            var range = ParseRange(reference, "La hoja contiene una tabla con un rango no compatible.");
            if (range.StartRow != headerRowNumber || (table.TotalsRowCount?.Value ?? 0) > 0)
            {
                throw new ExpirationsGenerationException(
                    "La hoja contiene una estructura de tabla no compatible con el filtrado seguro.");
            }
            var updatedReference = $"{range.StartColumn}{range.StartRow}:{range.EndColumn}{lastRowNumber}";
            table.Reference = updatedReference;
            if (table.AutoFilter is not null)
                table.AutoFilter.Reference = updatedReference;
            table.Save();
        }
    }

    private static void UpdateSheetDimension(Worksheet worksheet, uint lastRowNumber)
    {
        var dimension = worksheet.Elements<SheetDimension>().SingleOrDefault();
        if (dimension?.Reference?.Value is not { Length: > 0 } reference)
            return;
        var range = ParseRange(reference, "La dimensión de la hoja no tiene un rango compatible.");
        dimension.Reference = $"{range.StartColumn}{range.StartRow}:{range.EndColumn}{lastRowNumber}";
    }

    private static void RemoveOtherWorksheets(
        WorkbookPart workbookPart,
        Sheet selectedSheet,
        int selectedSheetIndex,
        IReadOnlyList<Sheet> originalSheets)
    {
        var deletedNames = originalSheets
            .Where(sheet => !ReferenceEquals(sheet, selectedSheet))
            .Select(sheet => sheet.Name?.Value ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToArray();
        CleanDefinedNames(workbookPart, selectedSheet.Name?.Value ?? string.Empty, selectedSheetIndex, deletedNames);

        foreach (var sheet in originalSheets.Where(sheet => !ReferenceEquals(sheet, selectedSheet)))
        {
            if (sheet.Id?.Value is { Length: > 0 } relationshipId)
            {
                var part = workbookPart.GetPartById(relationshipId);
                sheet.Remove();
                workbookPart.DeletePart(part);
            }
        }
        var workbook = workbookPart.Workbook ??
            throw new ExpirationsGenerationException("El archivo no contiene la definición del workbook.");
        foreach (var view in workbook.BookViews?.Elements<WorkbookView>() ?? [])
        {
            view.ActiveTab = 0U;
            view.FirstSheet = 0U;
        }
    }

    private static void CleanDefinedNames(
        WorkbookPart workbookPart,
        string selectedSheetName,
        int selectedSheetIndex,
        IReadOnlyList<string> deletedSheetNames)
    {
        var workbook = workbookPart.Workbook ??
            throw new ExpirationsGenerationException("El archivo no contiene la definición del workbook.");
        var definedNames = workbook.DefinedNames;
        if (definedNames is null)
            return;
        foreach (var definedName in definedNames.Elements<DefinedName>().ToList())
        {
            var text = definedName.Text ?? string.Empty;
            var localSheetId = definedName.LocalSheetId?.Value;
            var referencesDeletedSheet = deletedSheetNames.Any(name => ReferencesSheet(text, name));
            var referencesSelectedSheet = ReferencesSheet(text, selectedSheetName);
            var belongsToSelectedSheet = localSheetId == (uint)selectedSheetIndex;
            if (referencesDeletedSheet ||
                (localSheetId.HasValue && !belongsToSelectedSheet) ||
                (!localSheetId.HasValue && !referencesSelectedSheet))
            {
                definedName.Remove();
                continue;
            }
            if (belongsToSelectedSheet)
                definedName.LocalSheetId = 0U;
        }
        if (!definedNames.Elements<DefinedName>().Any())
            definedNames.Remove();
    }

    private static bool ReferencesSheet(string formula, string sheetName)
    {
        if (sheetName.Length == 0)
            return false;
        var escaped = sheetName.Replace("'", "''", StringComparison.Ordinal);
        return formula.Contains($"'{escaped}'!", StringComparison.OrdinalIgnoreCase) ||
               formula.Contains($"{sheetName}!", StringComparison.OrdinalIgnoreCase);
    }

    private static void RebuildSharedStrings(WorkbookPart workbookPart, WorksheetPart worksheetPart)
    {
        var sharedPart = workbookPart.SharedStringTablePart;
        if (sharedPart?.SharedStringTable is null)
            return;
        var oldItems = sharedPart.SharedStringTable.Elements<SharedStringItem>()
            .Select(item => (SharedStringItem)item.CloneNode(true))
            .ToList();
        var worksheet = worksheetPart.Worksheet ??
            throw new ExpirationsGenerationException("La hoja seleccionada no contiene una definición válida.");
        var cells = worksheet.Descendants<Cell>()
            .Where(cell => cell.DataType?.Value == CellValues.SharedString)
            .ToList();
        var indexMap = new Dictionary<int, int>();
        var rebuilt = new SharedStringTable();
        foreach (var cell in cells)
        {
            if (!int.TryParse(cell.CellValue?.InnerText, NumberStyles.None, CultureInfo.InvariantCulture, out var oldIndex) ||
                oldIndex < 0 || oldIndex >= oldItems.Count)
            {
                throw new ExpirationsGenerationException("El workbook contiene un índice de sharedStrings no válido.");
            }
            if (!indexMap.TryGetValue(oldIndex, out var newIndex))
            {
                newIndex = indexMap.Count;
                indexMap.Add(oldIndex, newIndex);
                rebuilt.Append((SharedStringItem)oldItems[oldIndex].CloneNode(true));
            }
            cell.CellValue = new CellValue(newIndex.ToString(CultureInfo.InvariantCulture));
        }
        rebuilt.Count = (uint)cells.Count;
        rebuilt.UniqueCount = (uint)indexMap.Count;
        sharedPart.SharedStringTable = rebuilt;
        sharedPart.SharedStringTable.Save();
    }

    private static void ValidateGeneratedWorkbook(
        string path,
        ExpirationsStandardWorkbookGenerationRequest request,
        int expectedRowCount,
        WorkbookExpectations expectations)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new ExpirationsGenerationException("El archivo generado está vacío.");
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart ??
            throw new ExpirationsGenerationException("El archivo generado no contiene un workbook válido.");
        var workbook = workbookPart.Workbook ??
            throw new ExpirationsGenerationException("El archivo generado no contiene la definición del workbook.");
        var sheets = workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
        if (sheets.Count != 1 || !string.Equals(sheets[0].Name?.Value, request.WorksheetName, StringComparison.Ordinal))
            throw new ExpirationsGenerationException("El archivo generado no contiene exactamente la hoja esperada.");
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheets[0].Id!);
        var worksheet = worksheetPart.Worksheet ??
            throw new ExpirationsGenerationException("El archivo generado no contiene una hoja válida.");
        var rows = worksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToList() ?? [];
        if (!rows.Any(row => row.RowIndex?.Value == request.HeaderRowNumber))
            throw new ExpirationsGenerationException("El archivo generado no conserva la fila de encabezado.");
        var actualDataRows = rows.Count(row => (row.RowIndex?.Value ?? 0) > request.HeaderRowNumber);
        if (actualDataRows != expectedRowCount)
            throw new ExpirationsGenerationException("El archivo generado no contiene el número esperado de registros.");
        var expectedLastRow = request.HeaderRowNumber + (uint)expectedRowCount;
        if (rows.Where(row => (row.RowIndex?.Value ?? 0) > request.HeaderRowNumber)
            .Select(row => row.RowIndex!.Value)
            .Order()
            .SequenceEqual(Enumerable.Range(1, expectedRowCount).Select(index => request.HeaderRowNumber + (uint)index)) is false)
        {
            throw new ExpirationsGenerationException("Las filas del archivo generado no quedaron compactas.");
        }
        if (rows.Any(row => (row.RowIndex?.Value ?? 0) > expectedLastRow))
            throw new ExpirationsGenerationException("El archivo generado contiene filas ajenas a la distribución esperada.");
        if (!string.Equals(
                worksheet.GetFirstChild<Columns>()?.OuterXml ?? string.Empty,
                expectations.ColumnsOuterXml,
                StringComparison.Ordinal))
        {
            throw new ExpirationsGenerationException("El archivo generado no conserva las columnas originales.");
        }

        var sharedPart = workbookPart.SharedStringTablePart;
        if (sharedPart?.SharedStringTable is not null)
        {
            var referenced = worksheet.Descendants<Cell>()
                .Where(cell => cell.DataType?.Value == CellValues.SharedString)
                .Select(cell => int.Parse(cell.CellValue!.InnerText, CultureInfo.InvariantCulture))
                .Distinct()
                .Order()
                .ToArray();
            var itemCount = sharedPart.SharedStringTable.Elements<SharedStringItem>().Count();
            if (!referenced.SequenceEqual(Enumerable.Range(0, itemCount)))
                throw new ExpirationsGenerationException("El archivo generado conserva sharedStrings sin uso.");
        }
    }

    private static string ChangeRow(string? cellReference, uint destinationRow)
    {
        if (string.IsNullOrWhiteSpace(cellReference))
            throw new ExpirationsGenerationException("Una celda de datos no contiene referencia A1 válida.");
        var match = CellReferencePattern.Match(cellReference);
        if (!match.Success)
            throw new ExpirationsGenerationException($"La referencia de celda '{cellReference}' no es compatible.");
        return $"{match.Groups["column"].Value}{destinationRow}";
    }

    private static CellRange ParseRange(string? reference, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new ExpirationsGenerationException(errorMessage);
        var parts = reference.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2)
            throw new ExpirationsGenerationException(errorMessage);
        var start = ParseCell(parts[0], errorMessage);
        var end = ParseCell(parts.Length == 2 ? parts[1] : parts[0], errorMessage);
        if (end.Row < start.Row)
            throw new ExpirationsGenerationException(errorMessage);
        return new CellRange(start.Column, start.Row, end.Column, end.Row);
    }

    private static (string Column, uint Row) ParseCell(string reference, string errorMessage)
    {
        var match = CellReferencePattern.Match(reference);
        if (!match.Success || !uint.TryParse(match.Groups["row"].Value, out var row) || row == 0)
            throw new ExpirationsGenerationException(errorMessage);
        return (match.Groups["column"].Value.ToUpperInvariant(), row);
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
            // El servicio de batch vuelve a intentar la limpieza del staging completo.
        }
    }

    private sealed record CellRange(string StartColumn, uint StartRow, string EndColumn, uint EndRow);
    private sealed record WorkbookExpectations(string ColumnsOuterXml);
}
