using System.Windows;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;
using ECS.CommissionsMailer.Views;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsPresentationServicesTests
{
    private static readonly Guid Andres = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Ana = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Jerrika = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Henry = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Donald = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public void PreviewDeduplicatesValuesAndRowsWhileKeepingKnownPartialDestinations()
    {
        var catalog = new[]
        {
            Broker(Andres, "Andrés"),
            Broker(Ana, "Ana Luisa"),
            Broker(Jerrika, "Jerrika"),
            Broker(Henry, "Henry"),
            Broker(Donald, "Donald")
        };
        var associations = new[]
        {
            Association(1, Andres, "AS1"),
            Association(2, Andres, "AS5"),
            Association(3, Andres, "AS20"),
            Association(4, Ana, "ALS7"),
            Association(5, Jerrika, "JHS"),
            Association(6, Henry, "HD1"),
            Association(7, Donald, "DD1")
        };
        var analysis = new ExpirationsWorkbookAnalysisService().Analyze(
            SuccessfulRead(
                Row(2, "AS1, AS5, AS20"),
                Row(3, "Ana Luisa ALS7, Jerrika JHS"),
                Row(4, "Henry HD1; Donald DD1"),
                Row(5, "AS1, DESCONOCIDO")),
            catalog,
            associations);

        var preview = new ExpirationsDistributionPreviewService().Build(analysis, catalog);

        Assert.False(analysis.CanGenerate);
        var andres = preview.Single(item => item.BrokerId == Andres);
        Assert.Equal(2, andres.RowCount);
        Assert.Equal(["AS1", "AS20", "AS5"], andres.DetectedValues);
        Assert.Equal(1, preview.Single(item => item.BrokerId == Ana).RowCount);
        Assert.Equal(1, preview.Single(item => item.BrokerId == Jerrika).RowCount);
        Assert.Equal(1, preview.Single(item => item.BrokerId == Henry).RowCount);
        Assert.Equal(1, preview.Single(item => item.BrokerId == Donald).RowCount);
    }

    [Fact]
    public void IntermediarioRequiresInspectionSelectionAndThenAnalysisContinues()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "manual.xlsx");
        CreateIntermediarioWorkbook(path);
        var reader = new ExpirationsWorkbookReader();

        var automatic = reader.Read(path);
        var inspection = new ExpirationsWorkbookInspectionService().Inspect(path);
        var state = new ExpirationsWorkbookSelectionState(inspection)
        {
            SelectedWorksheet = inspection.Worksheets.Single()
        };
        state.SelectedHeaderRow = state.HeaderRows.Single(row => row.RowNumber == 3);
        state.SelectedColumn = state.Columns.Single(column => column.HeaderText == "Intermediario");
        Assert.True(state.TryCreateOptions(out var options));
        var selected = reader.Read(path, options);
        var analysis = new ExpirationsWorkbookAnalysisService().Analyze(
            selected,
            [Broker(Andres, "Andrés")],
            []);

        Assert.Equal(ExpirationsWorkbookReadStatus.HeaderNotFound, automatic.Status);
        Assert.Equal("Datos", options.WorksheetName);
        Assert.Equal((uint)3, options.HeaderRowNumber);
        Assert.Equal(4, options.BrokerColumnIndex);
        Assert.True(selected.IsSuccess);
        Assert.True(analysis.CanGenerate);
    }

    [Fact]
    public void WindowStateCoversInitialBusyReadyBlockedAndIssueSelection()
    {
        var state = new ExpirationsWindowState(User());
        Assert.Null(state.SelectedProcessOption);
        Assert.False(state.CanAnalyze);
        Assert.Equal(Visibility.Collapsed, state.ResultVisibility);

        state.SelectedProcessOption = state.ProcessOptions[0];
        state.ApplySnapshot(new ExpirationsAnalysisSessionSnapshot
        {
            Process = ExpirationsProcess.PreviousMonth,
            SourcePath = "report.xlsx"
        });
        Assert.True(state.CanAnalyze);

        state.SetBusy(true);
        Assert.False(state.CanAnalyze);
        Assert.False(state.CanSelectFile);
        Assert.False(state.CanResolve);
        Assert.Equal(Visibility.Visible, state.BusyVisibility);

        state.SetBusy(false);
        state.ApplySnapshot(new ExpirationsAnalysisSessionSnapshot
        {
            Process = ExpirationsProcess.PreviousMonth,
            SourcePath = "report.xlsx",
            Analysis = new ExpirationsWorkbookAnalysisResult
            {
                TotalRows = 2,
                ResolvedRows = 2,
                CanGenerate = true
            }
        });
        Assert.Equal(Visibility.Visible, state.ReadyVisibility);
        Assert.Contains("Análisis completo", state.StatusText, StringComparison.Ordinal);

        var unresolved = Issue(10, ExpirationsBrokerResolutionStatus.Unresolved, "NUEVO");
        state.ApplySnapshot(BlockedSnapshot(unresolved));
        state.SelectedPendingIssue = unresolved;
        Assert.True(state.CanResolve);
        Assert.Equal(Visibility.Visible, state.PendingVisibility);

        var missing = Issue(11, ExpirationsBrokerResolutionStatus.MissingBroker, string.Empty);
        state.ApplySnapshot(BlockedSnapshot(missing));
        state.SelectedPendingIssue = missing;
        Assert.False(state.CanResolve);
    }

    [Fact]
    public void ResolutionDialogNeverPreselectsBrokerAndSuggestsOnlyAssociationType()
    {
        var codeState = new ExpirationsBrokerResolutionDialogState(
            Issue(10, ExpirationsBrokerResolutionStatus.Unresolved, "AS20"),
            [Broker(Andres, "Andrés"), Broker(Ana, "Inactivo", false)]);
        var aliasState = new ExpirationsBrokerResolutionDialogState(
            Issue(11, ExpirationsBrokerResolutionStatus.Unresolved, "Luis Arturo Quesada"),
            [Broker(Andres, "Andrés")]);
        var ambiguousState = new ExpirationsBrokerResolutionDialogState(
            Issue(12, ExpirationsBrokerResolutionStatus.Ambiguous, "AMA"),
            [Broker(Andres, "Andrés")]);

        Assert.Null(codeState.SelectedBroker);
        Assert.False(codeState.CanConfirm);
        Assert.Single(codeState.Brokers);
        codeState.SelectedBroker = Assert.Single(codeState.Brokers);
        Assert.True(codeState.CanConfirm);
        Assert.DoesNotContain("Archivo destino", codeState.ConfirmationSummary, StringComparison.Ordinal);
        Assert.Equal(ExpirationsAssociationKind.Code, codeState.SelectedKind.Value);
        Assert.Equal(ExpirationsAssociationKind.Alias, aliasState.SelectedKind.Value);
        Assert.Equal(Visibility.Visible, ambiguousState.AmbiguityWarningVisibility);
        Assert.Equal(Visibility.Visible, ambiguousState.AssociationTypeVisibility);
        Assert.Equal("Confirmar asociación", ambiguousState.ConfirmButtonText);
    }

    [Fact]
    public void AnalysisWithoutOverridesRemainsCompatibleAndInvalidOverridesStayBlocking()
    {
        var inactive = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var catalog = new[]
        {
            Broker(Ana, "Ana"),
            Broker(Jerrika, "Jerrika"),
            Broker(inactive, "Inactivo", false)
        };
        var associations = new[]
        {
            Association(1, Ana, "AMA"),
            Association(2, Jerrika, "AMA")
        };
        var read = SuccessfulRead(Row(10, "AMA"));
        var service = new ExpirationsWorkbookAnalysisService();

        var unchanged = service.Analyze(read, catalog, associations);
        var unknownOverride = service.Analyze(
            read,
            catalog,
            associations,
            [new ExpirationsManualResolutionOverride(10, 0, Guid.NewGuid())]);
        var inactiveOverride = service.Analyze(
            read,
            catalog,
            associations,
            [new ExpirationsManualResolutionOverride(10, 0, inactive)]);
        var validOverride = service.Analyze(
            read,
            catalog,
            associations,
            [new ExpirationsManualResolutionOverride(10, 0, Ana)]);

        Assert.False(unchanged.CanGenerate);
        Assert.False(unknownOverride.CanGenerate);
        Assert.False(inactiveOverride.CanGenerate);
        Assert.True(validOverride.CanGenerate);
        Assert.Equal(Ana, Assert.Single(validOverride.RowResolutions[0].DistinctDestinationBrokerIds));
    }

    private static ExpirationsAnalysisSessionSnapshot BlockedSnapshot(ExpirationsPendingIssue issue) => new()
    {
        Process = ExpirationsProcess.PreviousMonth,
        SourcePath = "report.xlsx",
        Analysis = new ExpirationsWorkbookAnalysisResult
        {
            TotalRows = 1,
            RowsWithBlockingIssues = 1,
            CanGenerate = false
        },
        PendingIssues = [issue]
    };

    private static ExpirationsPendingIssue Issue(
        uint row,
        ExpirationsBrokerResolutionStatus status,
        string value) => new()
    {
        RowNumber = row,
        ComponentIndex = 0,
        RawValue = value,
        NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(value),
        Status = status,
        StatusText = status.ToString()
    };

    private static AppUser User() => new()
    {
        Uid = "ui-state",
        Email = "ui@example.test",
        DisplayName = "UI",
        Role = AppUserRole.Operator,
        IsActive = true,
        CanUseExpirations = true
    };

    private static ExpirationsBrokerCatalogItem Broker(Guid id, string name, bool active = true) => new()
    {
        BrokerId = id,
        Name = name,
        PrimaryEmailAddresses = [$"{id:D}@example.test"],
        IsActive = active
    };

    private static ExpirationsBrokerAssociation Association(int id, Guid brokerId, string value) => new()
    {
        Id = Guid.Parse($"{id:x8}-0000-0000-0000-000000000000"),
        BrokerId = brokerId,
        Kind = ExpirationsAssociationKind.Code,
        Value = value,
        NormalizedValue = value,
        IsActive = true
    };

    private static ExpirationsSourceRow Row(uint rowNumber, string value) => new()
    {
        RowNumber = rowNumber,
        RawBrokerValue = value
    };

    private static ExpirationsWorkbookReadResult SuccessfulRead(params ExpirationsSourceRow[] rows) => new()
    {
        Status = ExpirationsWorkbookReadStatus.Success,
        Workbook = new ExpirationsSourceWorkbook
        {
            SourcePath = "synthetic.xlsx",
            WorksheetName = "Datos",
            HeaderRowNumber = 1,
            BrokerColumnIndex = 1,
            Rows = rows
        }
    };

    private static void CreateIntermediarioWorkbook(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData(
            new Row(InlineCell("A1", "Título")) { RowIndex = 1 },
            new Row(InlineCell("A3", "Póliza"), InlineCell("D3", "Intermediario")) { RowIndex = 3 },
            new Row(InlineCell("A4", "P-1"), InlineCell("D4", "Andrés")) { RowIndex = 4 }));
        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.Sheets!.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Datos"
        });
        workbookPart.Workbook.Save();
    }

    private static Cell InlineCell(string reference, string value) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })
    };

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ECS-expirations-presentation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
