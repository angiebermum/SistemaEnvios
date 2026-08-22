using System.Windows;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Views;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsGenerationUiStateTests
{
    [Fact]
    public void ExpirationsPeriodValidatesRangeAndUsesInvariantFileToken()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExpirationsPeriod(2026, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExpirationsPeriod(1999, 8));
        var period = new ExpirationsPeriod(2026, 8);
        Assert.Equal("2026-08", period.FileToken);
        Assert.Equal("Agosto", period.SpanishMonthName);
    }

    [Fact]
    public void GenerateIsEnabledOnlyForCleanPreviousMonthAndDisabledWhileBusy()
    {
        var state = new ExpirationsWindowState(User());
        Assert.False(state.CanGenerate);
        state.ApplySnapshot(ReadySnapshot(ExpirationsProcess.PreviousMonth));
        Assert.True(state.CanGenerate);
        Assert.Equal(Visibility.Collapsed, state.NextMonthPeriodVisibility);

        state.SetBusy(true, "Generando archivo 1 de 2...");
        Assert.False(state.CanGenerate);
        Assert.False(state.CanSelectFile);
        Assert.False(state.CanAnalyze);
        Assert.False(state.CanConfigureBrokers);
        Assert.Equal("Generando archivo 1 de 2...", state.OperationStatusText);
    }

    [Fact]
    public void CleanNextMonthRequiresExplicitPeriodAndSuccessfulPreflight()
    {
        var state = new ExpirationsWindowState(User());
        state.ApplySnapshot(ReadySnapshot(ExpirationsProcess.NextMonth));

        Assert.False(state.CanGenerate);
        Assert.Equal("Análisis completo.", state.StatusText);
        Assert.Equal(Visibility.Visible, state.NextMonthPeriodVisibility);
        Assert.Contains("El análisis está completo", state.ReadyText, StringComparison.Ordinal);

        state.SelectedMonthOption = state.MonthOptions.Single(option => option.Month == 8);
        state.NextMonthYearText = "2026";
        Assert.True(state.TryGetNextMonthPeriod(out var period));
        Assert.Equal("2026-08", period!.FileToken);
        Assert.False(state.CanGenerate);

        state.SetGenerationReadiness(true, "Listo", false);
        Assert.True(state.CanGenerate);
        state.NextMonthYearText = "2027";
        Assert.False(state.CanGenerate);
    }

    [Fact]
    public void SuccessfulGenerationShowsCountFolderWarningsAndNewSnapshotMakesItObsolete()
    {
        var state = new ExpirationsWindowState(User());
        state.ApplySnapshot(ReadySnapshot(ExpirationsProcess.PreviousMonth));
        state.ApplyGenerationBatch(new ExpirationsGenerationBatch
        {
            OutputDirectory = @"C:\salida\batch",
            Files = [new ExpirationsGeneratedFile(), new ExpirationsGeneratedFile()],
            Warnings = ["Advertencia de correo"]
        });

        Assert.Equal(Visibility.Visible, state.GenerationResultVisibility);
        Assert.Equal(2, state.GeneratedFileCount);
        Assert.Equal(@"C:\salida\batch", state.GeneratedOutputDirectory);
        Assert.Contains("Advertencia de correo", state.GenerationWarningsText, StringComparison.Ordinal);

        state.SelectedProcessOption = state.ProcessOptions.Single(option => option.Value == ExpirationsProcess.NextMonth);
        Assert.Equal(Visibility.Collapsed, state.GenerationResultVisibility);
        state.ApplyGenerationBatch(new ExpirationsGenerationBatch
        {
            OutputDirectory = @"C:\salida\batch",
            Files = [new ExpirationsGeneratedFile()]
        });
        state.ApplySnapshot(ReadySnapshot(ExpirationsProcess.PreviousMonth));
        Assert.Equal(Visibility.Collapsed, state.GenerationResultVisibility);
        Assert.Equal(0, state.GeneratedFileCount);
    }

    [Fact]
    public void BlockedAnalysisAndProcessChangeKeepGenerationDisabled()
    {
        var state = new ExpirationsWindowState(User());
        state.ApplySnapshot(new ExpirationsAnalysisSessionSnapshot
        {
            Process = ExpirationsProcess.PreviousMonth,
            SourcePath = "blocked.xlsx",
            Analysis = new ExpirationsWorkbookAnalysisResult
            {
                TotalRows = 1,
                RowsWithBlockingIssues = 1,
                CanGenerate = false
            },
            PendingIssues = [new ExpirationsPendingIssue { RowNumber = 2 }]
        });
        Assert.False(state.CanGenerate);

        state.SelectedProcessOption = state.ProcessOptions.Single(option => option.Value == ExpirationsProcess.NextMonth);
        Assert.False(state.CanGenerate);
    }

    [Fact]
    public void PremiumColumnSelectionRequiresTwoDistinctHeaderColumnsAndIsSessionOnly()
    {
        var columns = new[]
        {
            new ExpirationsColumnInspection { ColumnIndex = 2, ColumnReference = "B", HeaderText = "Importe" },
            new ExpirationsColumnInspection { ColumnIndex = 3, ColumnReference = "C", HeaderText = "Divisa" }
        };
        var selection = new ExpirationsPremiumColumnSelectionState(
            "Reporte",
            7U,
            columns,
            null);
        selection.SelectedPremiumColumn = columns[0];
        selection.SelectedCurrencyColumn = columns[0];
        Assert.False(selection.CanAccept);
        selection.SelectedCurrencyColumn = columns[1];
        Assert.True(selection.CanAccept);

        var state = new ExpirationsWindowState(User());
        state.SetPremiumColumnOptions(new ExpirationsPremiumColumnOptions(2, 3));
        Assert.Equal(2, state.PremiumColumnOptions!.PremiumColumnIndex);
        state.ResetPremiumColumnOptions();
        Assert.Null(state.PremiumColumnOptions);
    }

    private static ExpirationsAnalysisSessionSnapshot ReadySnapshot(ExpirationsProcess process) => new()
    {
        Process = process,
        SourcePath = "ready.xlsx",
        Analysis = new ExpirationsWorkbookAnalysisResult
        {
            ReadStatus = ExpirationsWorkbookReadStatus.Success,
            TotalRows = 2,
            ResolvedRows = 2,
            CanGenerate = true
        },
        Distribution =
        [
            new ExpirationsDistributionPreviewItem
            {
                BrokerId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                BrokerName = "Broker",
                RowCount = 2
            }
        ]
    };

    private static AppUser User() => new()
    {
        Uid = "generation-ui",
        Email = "generation-ui@example.test",
        DisplayName = "Generation UI",
        Role = AppUserRole.Operator,
        IsActive = true,
        CanUseExpirations = true
    };
}
