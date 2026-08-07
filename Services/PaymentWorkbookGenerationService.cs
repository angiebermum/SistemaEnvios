using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public sealed class PaymentWorkbookGenerationService
{
    private readonly PaymentCalculationService _calculationService;
    private readonly FileNameSanitizer _fileNameSanitizer;
    private readonly GeneratedFileHashService _hashService;
    private readonly GenerationHistoryService _historyService;
    private readonly FileLogger _logger;
    private readonly SpecialRebateSheetService _specialRebateSheetService;

    public PaymentWorkbookGenerationService(
        PaymentCalculationService calculationService,
        FileNameSanitizer fileNameSanitizer,
        GeneratedFileHashService hashService,
        GenerationHistoryService historyService,
        FileLogger logger,
        SpecialRebateSheetService? specialRebateSheetService = null)
    {
        _calculationService = calculationService;
        _fileNameSanitizer = fileNameSanitizer;
        _hashService = hashService;
        _historyService = historyService;
        _logger = logger;
        _specialRebateSheetService = specialRebateSheetService ?? new SpecialRebateSheetService();
    }

    public PaymentGenerationBatch Generate(
        PaymentGenerationRequest request,
        ICollection<PaymentGenerationBatch> history,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var periodErrors = _fileNameSanitizer.ValidatePeriod(request.Period);
        if (periodErrors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, periodErrors));
        }

        var calculations = new List<(CommissionWorksheetAnalysis Analysis, WorksheetBrokerAssignment Assignment,
            PaymentCalculationResult Calculation, string FileName)>();
        var financialErrors = new List<string>();
        var assignmentIndex = request.Assignments.ToDictionary(
            value => WorksheetBrokerMappingService.NormalizeWorksheetName(value.WorksheetName),
            StringComparer.OrdinalIgnoreCase);
        foreach (var worksheet in request.Analysis.Worksheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!assignmentIndex.TryGetValue(
                    WorksheetBrokerMappingService.NormalizeWorksheetName(worksheet.WorksheetName),
                    out var assignment))
            {
                throw new InvalidDataException($"La pestaña '{worksheet.WorksheetName}' no tiene corredor asociado.");
            }

            var calculation = _calculationService.Calculate(worksheet, assignment.Broker.Deductions);
            if (!calculation.IsValid)
            {
                financialErrors.Add(
                    $"{worksheet.WorksheetName}: {string.Join(" ", calculation.Errors)}");
                continue;
            }

            calculations.Add((
                worksheet,
                assignment,
                calculation,
                _fileNameSanitizer.CreatePaymentFileName(
                    assignment.Broker.Name,
                    worksheet.WorksheetName,
                    request.Period)));
        }

        if (financialErrors.Count > 0)
        {
            throw new InvalidDataException(
                "Se encontraron errores financieros:\n" +
                string.Join(Environment.NewLine, financialErrors.Select(value => $"• {value}")));
        }

        var duplicateNames = calculations.GroupBy(value => value.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(value => value.Count() > 1)
            .Select(value => value.Key)
            .ToList();
        if (duplicateNames.Count > 0)
        {
            throw new InvalidDataException(
                $"Los nombres sanitizados producen archivos duplicados: {string.Join(", ", duplicateNames)}.");
        }

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        var outputExisted = Directory.Exists(outputDirectory);
        if (outputExisted && !request.ReplaceExistingFiles)
        {
            throw new IOException($"La carpeta de salida ya existe: {outputDirectory}");
        }

        var outputParent = Path.GetDirectoryName(outputDirectory)
            ?? throw new DirectoryNotFoundException("No se pudo determinar la carpeta padre de la salida.");
        Directory.CreateDirectory(outputParent);
        var tempRoot = Path.Combine(outputParent, $".ECS-payment-generation-{Guid.NewGuid():N}");
        var stagedDirectory = Path.Combine(tempRoot, "staged");
        var backupDirectory = Path.Combine(tempRoot, "backup");
        Directory.CreateDirectory(stagedDirectory);
        Directory.CreateDirectory(backupDirectory);
        var movedFinalPaths = new List<string>();
        var backedUpFiles = new List<(string Backup, string Final)>();
        PaymentGenerationBatch? pendingBatch = null;
        try
        {
            var batch = new PaymentGenerationBatch
            {
                Period = request.Period.Trim(),
                CreatedAt = DateTimeOffset.Now,
                SourceWorkbookPath = Path.GetFullPath(request.SourceWorkbookPath),
                SourceWorkbookSha256 = request.Analysis.WorkbookSha256,
                OutputDirectory = outputDirectory,
                Status = PaymentGenerationStatus.Generated
            };
            pendingBatch = batch;
            foreach (var broker in calculations.Select(value => value.Assignment.Broker)
                         .DistinctBy(value => value.Id)
                         .Where(value => value.PrimaryEmailAddresses.Count == 0))
            {
                batch.Warnings.Add(
                    $"El corredor '{broker.Name}' no tiene correo principal; sus archivos se generarán, " +
                    "pero el envío quedará bloqueado.");
            }
            foreach (var brokerGroup in calculations.GroupBy(value => value.Assignment.Broker.Id)
                         .Where(value => value.Count() > 1))
            {
                batch.Warnings.Add(
                    $"El corredor '{brokerGroup.First().Assignment.Broker.Name}' tiene {brokerGroup.Count()} pestañas; " +
                    "todos sus archivos se agruparán en un único correo.");
            }
            for (var index = 0; index < calculations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = calculations[index];
                progress?.Report(
                    $"Generando archivo {index + 1} de {calculations.Count}: {item.Analysis.WorksheetName}...");
                var stagedPath = Path.Combine(stagedDirectory, item.FileName);
                try
                {
                    File.Copy(request.SourceWorkbookPath, stagedPath, false);
                    CreateIndividualWorkbook(
                        stagedPath,
                        item.Analysis.WorksheetName,
                        item.Assignment.Broker,
                        item.Calculation,
                        out var rebateSheetAdded);
                    ValidateGeneratedWorkbook(stagedPath, rebateSheetAdded);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                               or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
                {
                    _logger.Error(
                        $"No se pudo generar la pestaña 'Monto de factura' para " +
                        $"'{item.Analysis.WorksheetName}'.",
                        ex);
                    throw new InvalidDataException(
                        $"No se pudo copiar o validar la pestaña '{item.Analysis.WorksheetName}': {ex.Message}",
                        ex);
                }

                var hash = _hashService.ComputeSha256(stagedPath);
                batch.Files.Add(new GeneratedPaymentFile
                {
                    BrokerId = item.Assignment.Broker.Id,
                    BrokerName = item.Assignment.Broker.Name,
                    WorksheetName = item.Analysis.WorksheetName,
                    OutputPath = Path.Combine(outputDirectory, item.FileName),
                    Sha256 = hash,
                    AnalyzerName = item.Analysis.AnalyzerName,
                    GeneratedAt = batch.CreatedAt,
                    Crc = item.Calculation.Crc,
                    Usd = item.Calculation.Usd
                });
                batch.Warnings.AddRange(item.Analysis.Warnings.Select(value =>
                    $"{item.Analysis.WorksheetName}: {value}"));
                batch.Warnings.AddRange(item.Calculation.Crc.Warnings.Select(value =>
                    $"{item.Analysis.WorksheetName}: {value}"));
                batch.Warnings.AddRange(item.Calculation.Usd.Warnings.Select(value =>
                    $"{item.Analysis.WorksheetName}: {value}"));
            }

            Directory.CreateDirectory(outputDirectory);
            foreach (var file in batch.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existingPath = file.OutputPath;
                if (File.Exists(existingPath))
                {
                    var backupPath = Path.Combine(backupDirectory, Path.GetFileName(existingPath));
                    File.Move(existingPath, backupPath, true);
                    backedUpFiles.Add((backupPath, existingPath));
                }

                File.Move(Path.Combine(stagedDirectory, Path.GetFileName(existingPath)), existingPath, false);
                movedFinalPaths.Add(existingPath);
            }

            batch.Status = PaymentGenerationStatus.ReadyToSend;
            _historyService.Upsert(history, batch);
            _logger.Info(
                $"Generación de detalles de pago completada. Id={batch.Id}; periodo={batch.Period}; " +
                $"archivos={batch.Files.Count}; carpeta={batch.OutputDirectory}.");
            return batch;
        }
        catch
        {
            if (pendingBatch is not null)
            {
                history.Remove(pendingBatch);
            }

            RollBackFiles(movedFinalPaths, backedUpFiles);
            if (!outputExisted)
            {
                TryDeleteEmptyDirectory(outputDirectory);
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ValidateRequest(PaymentGenerationRequest request)
    {
        if (!File.Exists(request.SourceWorkbookPath))
        {
            throw new FileNotFoundException("El Excel general no existe.", request.SourceWorkbookPath);
        }

        if (!request.Analysis.IsValid)
        {
            var errors = request.Analysis.Errors.Concat(
                request.Analysis.Worksheets.SelectMany(value => value.Errors));
            throw new InvalidDataException(
                "El análisis del Excel general contiene errores: " + string.Join(" ", errors));
        }

        if (request.Analysis.Worksheets.Count == 0 ||
            request.Assignments.Count != request.Analysis.Worksheets.Count)
        {
            throw new InvalidDataException("Todas las pestañas deben estar asociadas antes de generar.");
        }

        if (!string.Equals(
                new GeneratedFileHashService().ComputeSha256(request.SourceWorkbookPath),
                request.Analysis.WorkbookSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "El Excel general cambió después del análisis. Selecciónelo y analícelo nuevamente.");
        }
    }

    private void CreateIndividualWorkbook(
        string path,
        string sourceWorksheetName,
        Broker broker,
        PaymentCalculationResult calculation,
        out bool rebateSheetAdded)
    {
        using var document = SpreadsheetDocument.Open(path, true);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("La copia no contiene un libro válido.");
        var workbook = workbookPart.Workbook
            ?? throw new InvalidDataException("La copia no contiene la definición del libro.");
        var sheets = workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
        var selected = sheets.FirstOrDefault(value =>
            string.Equals(value.Name?.Value, sourceWorksheetName, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"No se encontró la pestaña '{sourceWorksheetName}' en la copia.");
        var selectedRelationshipId = selected.Id?.Value
            ?? throw new InvalidDataException($"La pestaña '{sourceWorksheetName}' no tiene relación interna.");
        if (workbookPart.GetPartById(selectedRelationshipId) is not WorksheetPart selectedWorksheetPart)
        {
            throw new InvalidDataException($"La pestaña '{sourceWorksheetName}' no es una hoja compatible.");
        }

        var removedNames = new List<string>();
        foreach (var sheet in sheets.Where(value => !ReferenceEquals(value, selected)).ToList())
        {
            removedNames.Add(sheet.Name?.Value ?? string.Empty);
            if (sheet.Id?.Value is { } relationshipId)
            {
                var part = workbookPart.GetPartById(relationshipId);
                workbookPart.DeletePart(part);
            }

            sheet.Remove();
        }

        selected.Name = "Detalle";
        RemoveInvalidDefinedNames(workbook, removedNames);
        if (workbookPart.CalculationChainPart is not null)
        {
            workbookPart.DeletePart(workbookPart.CalculationChainPart);
        }

        workbook.CalculationProperties ??= new CalculationProperties();
        workbook.CalculationProperties.CalculationMode = CalculateModeValues.Auto;
        workbook.CalculationProperties.CalculationOnSave = true;
        workbook.CalculationProperties.ForceFullCalculation = true;
        workbook.CalculationProperties.FullCalculationOnLoad = true;
        if (workbook.BookViews?.Elements<WorkbookView>().FirstOrDefault() is { } view)
        {
            view.ActiveTab = 0U;
        }

        var detailTotals = ResolveDetailGrossCommissionReferences(
            workbookPart,
            selectedWorksheetPart,
            sourceWorksheetName,
            calculation);
        var summaryPart = workbookPart.AddNewPart<WorksheetPart>();
        summaryPart.Worksheet = BuildPaymentWorksheet(workbookPart, calculation, detailTotals);
        var nextSheetId = workbook.Sheets!.Elements<Sheet>()
            .Select(value => value.SheetId?.Value ?? 0U)
            .DefaultIfEmpty(0U)
            .Max() + 1U;
        workbook.Sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(summaryPart),
            SheetId = nextSheetId,
            Name = "Monto de factura"
        });
        rebateSheetAdded = _specialRebateSheetService.AppendInformationalSheet(
            workbookPart,
            broker,
            sourceWorksheetName,
            calculation);
        BrokerPercentageWorksheetNormalizer.Normalize(
            workbookPart,
            selectedWorksheetPart,
            sourceWorksheetName);
        workbook.Save();
    }

    private static DetailGrossCommissionReferences ResolveDetailGrossCommissionReferences(
        WorkbookPart workbookPart,
        WorksheetPart detailWorksheetPart,
        string sourceWorksheetName,
        PaymentCalculationResult calculation)
    {
        var detail = OpenXmlWorksheetReader.Read(workbookPart, detailWorksheetPart, "Detalle");
        var detected = CommissionWorksheetAnalyzerSupport.AnalyzeSummaryLayout(
            detail,
            CommissionWorksheetAnalyzerSupport.CreateResult("Detalle", "Totales de Detalle"));
        if (!detected.IsValid && (detected.Crc.HasCommission || detected.Usd.HasCommission))
        {
            throw new InvalidDataException(
                $"No se pudo generar la pestaña 'Monto de factura' para '{sourceWorksheetName}'." +
                Environment.NewLine +
                string.Join(Environment.NewLine, detected.Errors));
        }

        return new DetailGrossCommissionReferences(
            ResolveDetailGrossCommissionReference(
                sourceWorksheetName,
                calculation.Crc,
                detected.Crc),
            ResolveDetailGrossCommissionReference(
                sourceWorksheetName,
                calculation.Usd,
                detected.Usd));
    }

    private static string? ResolveDetailGrossCommissionReference(
        string sourceWorksheetName,
        PaymentCurrencyCalculation calculation,
        CommissionCurrencySummary detected)
    {
        if (!calculation.HasCommission)
        {
            return null;
        }

        if (!detected.HasCommission || detected.SourceCells.Count != 1)
        {
            throw new InvalidDataException(
                $"No se pudo generar la pestaña 'Monto de factura' para '{sourceWorksheetName}'." +
                Environment.NewLine +
                $"No se encontró con seguridad el total 'Monto bruto comisión' correspondiente a " +
                $"{calculation.Currency} en la pestaña 'Detalle'.");
        }

        return detected.SourceCells[0];
    }

    private static Worksheet BuildPaymentWorksheet(
        WorkbookPart workbookPart,
        PaymentCalculationResult calculation,
        DetailGrossCommissionReferences detailTotals)
    {
        var styles = EnsurePaymentStyles(workbookPart);
        var worksheet = new Worksheet();
        worksheet.Append(new Columns(
            new Column { Min = 1U, Max = 1U, Width = 4D, CustomWidth = true },
            new Column { Min = 2U, Max = 2U, Width = 42D, CustomWidth = true },
            new Column { Min = 3U, Max = 3U, Width = 22D, CustomWidth = true },
            new Column { Min = 4U, Max = 4U, Width = 4D, CustomWidth = true },
            new Column { Min = 5U, Max = 5U, Width = 42D, CustomWidth = true },
            new Column { Min = 6U, Max = 6U, Width = 22D, CustomWidth = true }));
        var sheetData = new SheetData();
        worksheet.Append(sheetData);
        var mergeCells = new MergeCells();
        worksheet.Append(mergeCells);
        uint crcRowIndex = 2;
        uint usdRowIndex = 2;
        WriteCurrencyBlock(
            sheetData,
            mergeCells,
            ref crcRowIndex,
            "B",
            "C",
            "COLONES",
            calculation.Crc,
            detailTotals.Crc,
            styles);
        WriteCurrencyBlock(
            sheetData,
            mergeCells,
            ref usdRowIndex,
            "E",
            "F",
            "DÓLARES",
            calculation.Usd,
            detailTotals.Usd,
            styles);
        worksheet.Append(new PageMargins
        {
            Left = 0.4D,
            Right = 0.4D,
            Top = 0.5D,
            Bottom = 0.5D,
            Header = 0.2D,
            Footer = 0.2D
        });
        worksheet.Append(new PageSetup
        {
            Orientation = OrientationValues.Portrait,
            FitToWidth = 1U,
            FitToHeight = 0U
        });
        return worksheet;
    }

    private static void WriteCurrencyBlock(
        SheetData sheetData,
        MergeCells mergeCells,
        ref uint rowIndex,
        string labelColumn,
        string amountColumn,
        string title,
        PaymentCurrencyCalculation calculation,
        string? detailGrossCommissionReference,
        PaymentSheetStyles styles)
    {
        var amountStyle = calculation.Currency == DeductionCurrency.CRC ? styles.CrcAmount : styles.UsdAmount;
        var strongAmountStyle = calculation.Currency == DeductionCurrency.CRC
            ? styles.CrcStrongAmount
            : styles.UsdStrongAmount;
        var negativeAmountStyle = calculation.Currency == DeductionCurrency.CRC
            ? styles.CrcNegativeAmount
            : styles.UsdNegativeAmount;
        var showFinancialAmounts = calculation.HasCommission &&
                                   !calculation.MinimumApplied &&
                                   calculation.IsValid &&
                                   calculation.GrossCommissionOriginal >= calculation.MinimumAmount;

        var titleRow = GetOrCreateRow(sheetData, rowIndex, 24D);
        titleRow.Append(TextCell($"{labelColumn}{rowIndex}", title, styles.Title));
        titleRow.Append(TextCell($"{amountColumn}{rowIndex}", string.Empty, styles.Title));
        mergeCells.Append(new MergeCell
        {
            Reference = $"{labelColumn}{rowIndex}:{amountColumn}{rowIndex}"
        });
        rowIndex++;

        var grossRow = rowIndex++;
        var grossReference = $"{amountColumn}{grossRow}";
        WriteFormulaAmountRow(
            sheetData,
            grossRow,
            labelColumn,
            amountColumn,
            "Monto bruto comisión",
            detailGrossCommissionReference is null
                ? "0"
                : $"'Detalle'!{detailGrossCommissionReference}",
            detailGrossCommissionReference is null ? 0m : calculation.GrossCommissionOriginal,
            styles.StrongLabel,
            strongAmountStyle);

        var grossDeductions = calculation.Deductions
            .Where(value => value.ApplicationType == DeductionApplicationType.GrossCommission)
            .OrderBy(value => value.DisplayOrder)
            .ToList();
        var grossAdjustmentRow = rowIndex++;
        var grossAdjustmentReference = $"{amountColumn}{grossAdjustmentRow}";
        var firstGrossDeductionRow = rowIndex;
        if (grossDeductions.Count == 0)
        {
            WriteFormulaAmountRow(
                sheetData,
                grossAdjustmentRow,
                labelColumn,
                amountColumn,
                "Ajustes al monto bruto",
                "0",
                0m,
                styles.StrongLabel,
                amountStyle);
        }
        else
        {
            WriteEmptyAmountRow(
                sheetData,
                grossAdjustmentRow,
                labelColumn,
                amountColumn,
                "Ajustes al monto bruto",
                styles.StrongLabel,
                amountStyle);
        }

        foreach (var deduction in grossDeductions)
        {
            WriteAmountRow(
                sheetData,
                rowIndex++,
                labelColumn,
                amountColumn,
                $"   {deduction.Description}",
                showFinancialAmounts ? -deduction.AppliedAmount : 0m,
                styles.Label,
                amountStyle);
        }

        var adjustedGrossRow = rowIndex++;
        var adjustedGrossReference = $"{amountColumn}{adjustedGrossRow}";
        var adjustedGrossBaseFormula = grossDeductions.Count == 0
            ? $"{grossReference}-{grossAdjustmentReference}"
            : $"{grossReference}+SUM({amountColumn}{firstGrossDeductionRow}:" +
              $"{amountColumn}{firstGrossDeductionRow + (uint)grossDeductions.Count - 1U})";
        var adjustedGrossFormula = showFinancialAmounts || !calculation.HasCommission
            ? adjustedGrossBaseFormula
            : $"IF(OR({grossReference}<0,{adjustedGrossBaseFormula}<" +
              $"{calculation.MinimumAmount.ToString(CultureInfo.InvariantCulture)}),0," +
              $"{adjustedGrossBaseFormula})";
        WriteFormulaAmountRow(
            sheetData,
            adjustedGrossRow,
            labelColumn,
            amountColumn,
            "Monto bruto ajustado",
            adjustedGrossFormula,
            showFinancialAmounts ? calculation.AdjustedGrossCommission : 0m,
            styles.StrongLabel,
            strongAmountStyle);
        var vatRow = rowIndex++;
        var vatReference = $"{amountColumn}{vatRow}";
        WriteFormulaAmountRow(
            sheetData,
            vatRow,
            labelColumn,
            amountColumn,
            "IVA 13%",
            $"{adjustedGrossReference}*13%",
            showFinancialAmounts ? calculation.Vat : 0m,
            styles.Label,
            amountStyle);
        var invoiceRow = rowIndex++;
        var invoiceReference = $"{amountColumn}{invoiceRow}";
        WriteFormulaAmountRow(
            sheetData,
            invoiceRow,
            labelColumn,
            amountColumn,
            "Monto factura",
            $"{adjustedGrossReference}+{vatReference}",
            showFinancialAmounts ? calculation.InvoiceAmount : 0m,
            styles.InvoiceLabel,
            calculation.Currency == DeductionCurrency.CRC
                ? styles.CrcInvoiceAmount
                : styles.UsdInvoiceAmount);
        var withholdingRow = rowIndex++;
        var withholdingReference = $"{amountColumn}{withholdingRow}";
        WriteFormulaAmountRow(
            sheetData,
            withholdingRow,
            labelColumn,
            amountColumn,
            "Retención 2%",
            $"-({adjustedGrossReference}*2%)",
            showFinancialAmounts ? -calculation.Withholding : 0m,
            styles.Label,
            showFinancialAmounts && calculation.Withholding > 0m
                ? negativeAmountStyle
                : amountStyle);

        var finalDeductions = calculation.Deductions
            .Where(value => value.ApplicationType == DeductionApplicationType.PayableAmount)
            .OrderBy(value => value.DisplayOrder)
            .ToList();
        var finalDeductionsRow = rowIndex++;
        var firstFinalDeductionRow = rowIndex;
        WriteEmptyAmountRow(
            sheetData,
            finalDeductionsRow,
            labelColumn,
            amountColumn,
            "Deducciones",
            styles.StrongLabel,
            amountStyle);
        foreach (var deduction in finalDeductions)
        {
            var displayedAmount = showFinancialAmounts ? -deduction.AppliedAmount : 0m;
            WriteAmountRow(
                sheetData,
                rowIndex++,
                labelColumn,
                amountColumn,
                $"   {deduction.Description}",
                displayedAmount,
                styles.Label,
                displayedAmount < 0m ? negativeAmountStyle : amountStyle);
        }

        var depositedFormula = finalDeductions.Count == 0
            ? $"{invoiceReference}+{withholdingReference}"
            : $"{invoiceReference}+{withholdingReference}+" +
              $"SUM({amountColumn}{firstFinalDeductionRow}:" +
              $"{amountColumn}{firstFinalDeductionRow + (uint)finalDeductions.Count - 1U})";

        WriteFormulaAmountRow(
            sheetData,
            rowIndex++,
            labelColumn,
            amountColumn,
            "Monto depositado",
            depositedFormula,
            showFinancialAmounts ? calculation.DepositedAmount : 0m,
            styles.TotalLabel,
            calculation.Currency == DeductionCurrency.CRC ? styles.CrcTotalAmount : styles.UsdTotalAmount);
        if (!string.IsNullOrWhiteSpace(calculation.Observation))
        {
            var noteStyle = calculation.MinimumApplied ||
                            calculation.Observation.StartsWith("Comisión negativa:", StringComparison.Ordinal)
                ? styles.MinimumNote
                : styles.Note;
            var noteRow = GetOrCreateRow(sheetData, rowIndex);
            noteRow.Append(TextCell($"{labelColumn}{rowIndex}", calculation.Observation, noteStyle));
            noteRow.Append(TextCell($"{amountColumn}{rowIndex}", string.Empty, noteStyle));
            mergeCells.Append(new MergeCell
            {
                Reference = $"{labelColumn}{rowIndex}:{amountColumn}{rowIndex}"
            });
            rowIndex++;
        }
    }

    private static void WriteAmountRow(
        SheetData sheetData,
        uint rowIndex,
        string labelColumn,
        string amountColumn,
        string label,
        decimal amount,
        uint labelStyle,
        uint amountStyle)
    {
        var row = GetOrCreateRow(sheetData, rowIndex, 19D);
        row.Append(TextCell($"{labelColumn}{rowIndex}", label, labelStyle));
        row.Append(NumberCell($"{amountColumn}{rowIndex}", amount, amountStyle));
    }

    private static void WriteFormulaAmountRow(
        SheetData sheetData,
        uint rowIndex,
        string labelColumn,
        string amountColumn,
        string label,
        string formula,
        decimal cachedAmount,
        uint labelStyle,
        uint amountStyle)
    {
        if (string.IsNullOrWhiteSpace(formula) || formula.Contains("#REF!", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"No se pudo escribir una fórmula válida para '{label}' en Monto de factura.");
        }

        var row = GetOrCreateRow(sheetData, rowIndex, 19D);
        row.Append(TextCell($"{labelColumn}{rowIndex}", label, labelStyle));
        row.Append(FormulaCell(
            $"{amountColumn}{rowIndex}",
            formula,
            cachedAmount,
            amountStyle));
    }

    private static void WriteEmptyAmountRow(
        SheetData sheetData,
        uint rowIndex,
        string labelColumn,
        string amountColumn,
        string label,
        uint labelStyle,
        uint amountStyle)
    {
        var row = GetOrCreateRow(sheetData, rowIndex, 19D);
        row.Append(TextCell($"{labelColumn}{rowIndex}", label, labelStyle));
        row.Append(TextCell($"{amountColumn}{rowIndex}", string.Empty, amountStyle));
    }

    private static Row GetOrCreateRow(SheetData sheetData, uint rowIndex, double? height = null)
    {
        var row = sheetData.Elements<Row>().FirstOrDefault(value => value.RowIndex?.Value == rowIndex);
        if (row is null)
        {
            row = new Row { RowIndex = rowIndex };
            sheetData.Append(row);
        }

        if (height.HasValue && (row.Height?.Value ?? 0D) < height.Value)
        {
            row.Height = height.Value;
            row.CustomHeight = true;
        }

        return row;
    }

    private static Cell TextCell(string reference, string value, uint style) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve }),
        StyleIndex = style
    };

    private static Cell NumberCell(string reference, decimal value, uint style) => new()
    {
        CellReference = reference,
        DataType = CellValues.Number,
        CellValue = new CellValue(value.ToString("0.00", CultureInfo.InvariantCulture)),
        StyleIndex = style
    };

    private static Cell FormulaCell(
        string reference,
        string formula,
        decimal cachedValue,
        uint style) => new()
    {
        CellReference = reference,
        DataType = CellValues.Number,
        CellFormula = new CellFormula(formula),
        CellValue = new CellValue(cachedValue.ToString("0.00", CultureInfo.InvariantCulture)),
        StyleIndex = style
    };

    private static PaymentSheetStyles EnsurePaymentStyles(WorkbookPart workbookPart)
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
        stylesheet.CellStyles ??= new CellStyles(new CellStyle
        {
            Name = "Normal",
            FormatId = 0U,
            BuiltinId = 0U
        });

        var headerFont = Append(stylesheet.Fonts, new Font(
            new Bold(),
            new Color { Rgb = "FFFFFFFF" },
            new FontSize { Val = 12D },
            new FontName { Val = "Calibri" }));
        var boldFont = Append(stylesheet.Fonts, new Font(
            new Bold(),
            new Color { Rgb = "FF1F2937" },
            new FontSize { Val = 11D },
            new FontName { Val = "Calibri" }));
        var normalFont = Append(stylesheet.Fonts, new Font(
            new Color { Rgb = "FF1F2937" },
            new FontSize { Val = 11D },
            new FontName { Val = "Calibri" }));
        var redFont = Append(stylesheet.Fonts, new Font(
            new Color { Rgb = "FFFF0000" },
            new FontSize { Val = 11D },
            new FontName { Val = "Calibri" }));
        var noteFont = Append(stylesheet.Fonts, new Font(
            new Italic(),
            new Color { Rgb = "FF6B7280" },
            new FontSize { Val = 10D },
            new FontName { Val = "Calibri" }));
        var minimumNoteFont = Append(stylesheet.Fonts, new Font(
            new Bold(),
            new Italic(),
            new Color { Rgb = "FF6B7280" },
            new FontSize { Val = 10D },
            new FontName { Val = "Calibri" }));
        var headerFill = Append(stylesheet.Fills, new Fill(new PatternFill(
            new ForegroundColor { Rgb = "FF2F6F73" },
            new BackgroundColor { Indexed = 64U })
        { PatternType = PatternValues.Solid }));
        var totalFill = Append(stylesheet.Fills, new Fill(new PatternFill(
            new ForegroundColor { Rgb = "FFE8F3F3" },
            new BackgroundColor { Indexed = 64U })
        { PatternType = PatternValues.Solid }));
        var invoiceFill = Append(stylesheet.Fills, new Fill(new PatternFill(
            new ForegroundColor { Rgb = "FFFFF2CC" },
            new BackgroundColor { Indexed = 64U })
        { PatternType = PatternValues.Solid }));
        var thinBorder = Append(stylesheet.Borders, new Border(
            new LeftBorder { Style = BorderStyleValues.Thin, Color = new Color { Rgb = "FF7A7A7A" } },
            new RightBorder { Style = BorderStyleValues.Thin, Color = new Color { Rgb = "FF7A7A7A" } },
            new TopBorder { Style = BorderStyleValues.Thin, Color = new Color { Rgb = "FF7A7A7A" } },
            new BottomBorder { Style = BorderStyleValues.Thin, Color = new Color { Rgb = "FF7A7A7A" } },
            new DiagonalBorder()));

        stylesheet.NumberingFormats ??= new NumberingFormats();
        var existingIds = stylesheet.NumberingFormats.Elements<NumberingFormat>()
            .Select(value => value.NumberFormatId?.Value ?? 163U)
            .ToList();
        var crcFormatId = Math.Max(164U, existingIds.DefaultIfEmpty(163U).Max() + 1U);
        var usdFormatId = crcFormatId + 1U;
        stylesheet.NumberingFormats.Append(
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
        stylesheet.NumberingFormats.Count = (uint)stylesheet.NumberingFormats.ChildElements.Count;

        var title = Append(stylesheet.CellFormats, Format(headerFont, headerFill, thinBorder, 0U, true,
            HorizontalAlignmentValues.Center));
        var label = Append(stylesheet.CellFormats, Format(normalFont, 0U, thinBorder));
        var strongLabel = Append(stylesheet.CellFormats, Format(boldFont, 0U, thinBorder));
        var totalLabel = Append(stylesheet.CellFormats, Format(boldFont, totalFill, thinBorder));
        var crcAmount = Append(stylesheet.CellFormats, Format(normalFont, 0U, thinBorder, crcFormatId, true,
            HorizontalAlignmentValues.Right));
        var usdAmount = Append(stylesheet.CellFormats, Format(normalFont, 0U, thinBorder, usdFormatId, true,
            HorizontalAlignmentValues.Right));
        var crcNegativeAmount = Append(stylesheet.CellFormats, Format(
            redFont, 0U, thinBorder, crcFormatId, true, HorizontalAlignmentValues.Right));
        var usdNegativeAmount = Append(stylesheet.CellFormats, Format(
            redFont, 0U, thinBorder, usdFormatId, true, HorizontalAlignmentValues.Right));
        var crcStrong = Append(stylesheet.CellFormats, Format(boldFont, 0U, thinBorder, crcFormatId, true,
            HorizontalAlignmentValues.Right));
        var usdStrong = Append(stylesheet.CellFormats, Format(boldFont, 0U, thinBorder, usdFormatId, true,
            HorizontalAlignmentValues.Right));
        var crcTotal = Append(stylesheet.CellFormats, Format(boldFont, totalFill, thinBorder, crcFormatId, true,
            HorizontalAlignmentValues.Right));
        var usdTotal = Append(stylesheet.CellFormats, Format(boldFont, totalFill, thinBorder, usdFormatId, true,
            HorizontalAlignmentValues.Right));
        var invoiceLabel = Append(stylesheet.CellFormats, Format(boldFont, invoiceFill, thinBorder));
        var crcInvoice = Append(stylesheet.CellFormats, Format(
            boldFont, invoiceFill, thinBorder, crcFormatId, true, HorizontalAlignmentValues.Right));
        var usdInvoice = Append(stylesheet.CellFormats, Format(
            boldFont, invoiceFill, thinBorder, usdFormatId, true, HorizontalAlignmentValues.Right));
        var note = Append(stylesheet.CellFormats, Format(noteFont, 0U, thinBorder, 0U, true,
            HorizontalAlignmentValues.Left, wrapText: true));
        var minimumNote = Append(stylesheet.CellFormats, Format(minimumNoteFont, 0U, thinBorder, 0U, true,
            HorizontalAlignmentValues.Left, wrapText: true));

        stylesheet.Fonts.Count = (uint)stylesheet.Fonts.ChildElements.Count;
        stylesheet.Fills.Count = (uint)stylesheet.Fills.ChildElements.Count;
        stylesheet.Borders.Count = (uint)stylesheet.Borders.ChildElements.Count;
        stylesheet.CellFormats.Count = (uint)stylesheet.CellFormats.ChildElements.Count;
        stylesheet.Save();
        return new PaymentSheetStyles(
            title, label, strongLabel, totalLabel, crcAmount, usdAmount,
            crcNegativeAmount, usdNegativeAmount, crcStrong, usdStrong, crcTotal, usdTotal,
            invoiceLabel, crcInvoice, usdInvoice, note, minimumNote);
    }

    private static CellFormat Format(
        uint fontId,
        uint fillId,
        uint borderId,
        uint numberFormatId = 0U,
        bool applyAlignment = false,
        HorizontalAlignmentValues? horizontal = null,
        bool wrapText = false) => new()
    {
        FontId = fontId,
        FillId = fillId,
        BorderId = borderId,
        NumberFormatId = numberFormatId,
        ApplyFont = true,
        ApplyFill = true,
        ApplyBorder = true,
        ApplyNumberFormat = numberFormatId != 0U,
        ApplyAlignment = applyAlignment,
        Alignment = applyAlignment
            ? new Alignment { Horizontal = horizontal ?? HorizontalAlignmentValues.Left, WrapText = wrapText }
            : null
    };

    private static uint Append(OpenXmlCompositeElement parent, OpenXmlElement child)
    {
        parent.Append(child);
        return (uint)(parent.ChildElements.Count - 1);
    }

    private static void RemoveInvalidDefinedNames(Workbook workbook, IReadOnlyCollection<string> removedSheetNames)
    {
        if (workbook.DefinedNames is not { } definedNames)
        {
            return;
        }

        foreach (var definedName in definedNames.Elements<DefinedName>().ToList())
        {
            if (definedName.LocalSheetId?.Value is { } localId && localId != 0U ||
                removedSheetNames.Any(name =>
                    definedName.Text?.Contains($"'{name.Replace("'", "''")}'!", StringComparison.OrdinalIgnoreCase) == true ||
                    definedName.Text?.Contains($"{name}!", StringComparison.OrdinalIgnoreCase) == true))
            {
                definedName.Remove();
            }
            else if (definedName.LocalSheetId is not null)
            {
                definedName.LocalSheetId = 0U;
            }
        }

        if (!definedNames.Elements<DefinedName>().Any())
        {
            definedNames.Remove();
        }
    }

    private static void ValidateGeneratedWorkbook(string path, bool rebateSheetExpected)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = SpreadsheetDocument.Open(stream, false);
        var names = document.WorkbookPart?.Workbook?.Sheets?.Elements<Sheet>()
            .Select(value => value.Name?.Value)
            .ToList() ?? [];
        var expectedNames = rebateSheetExpected
            ? new[] { "Detalle", "Monto de factura", "REBAJO" }
            : ["Detalle", "Monto de factura"];
        if (!names.SequenceEqual(expectedNames, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"El archivo '{Path.GetFileName(path)}' no contiene exactamente las hojas requeridas.");
        }
    }

    private static void RollBackFiles(
        IEnumerable<string> movedFinalPaths,
        IEnumerable<(string Backup, string Final)> backedUpFiles)
    {
        foreach (var path in movedFinalPaths.Reverse())
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // El error original sigue siendo prioritario; la restauración continúa con los demás archivos.
            }
        }

        foreach (var (backup, final) in backedUpFiles.Reverse())
        {
            try
            {
                if (File.Exists(backup))
                {
                    File.Move(backup, final, true);
                }
            }
            catch
            {
                // El caller recibirá el error original y el log conservará el contexto de la operación.
            }
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // La limpieza no debe reemplazar el error funcional de generación.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Un temporal residual se podrá limpiar manualmente desde %TEMP%.
        }
    }

    private sealed record PaymentSheetStyles(
        uint Title,
        uint Label,
        uint StrongLabel,
        uint TotalLabel,
        uint CrcAmount,
        uint UsdAmount,
        uint CrcNegativeAmount,
        uint UsdNegativeAmount,
        uint CrcStrongAmount,
        uint UsdStrongAmount,
        uint CrcTotalAmount,
        uint UsdTotalAmount,
        uint InvoiceLabel,
        uint CrcInvoiceAmount,
        uint UsdInvoiceAmount,
        uint Note,
        uint MinimumNote);

    private sealed record DetailGrossCommissionReferences(string? Crc, string? Usd);
}
