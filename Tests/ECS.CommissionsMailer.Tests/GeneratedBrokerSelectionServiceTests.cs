using System.Collections.ObjectModel;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class GeneratedBrokerSelectionServiceTests
{
    [Fact]
    public void BrokerWithOneValidCurrentGeneratedFileIsSelected()
    {
        using var scope = new TestDirectory();
        var broker = CreateBroker();
        var file = scope.CreateGeneratedFile(broker.Id, "uno.xlsx");
        var item = CreateItem(broker, file.OutputPath);

        var result = scope.Service.SelectEligibleBrokers(
            [item], [broker], scope.CreateBatch(file), scope.SourceWorkbookPath);

        Assert.True(result.Succeeded);
        Assert.True(item.IsSelected);
        Assert.Equal(1, result.SelectedBrokerCount);
    }

    [Fact]
    public void BrokerWithThreeValidFilesIsSelectedOnlyOnce()
    {
        using var scope = new TestDirectory();
        var broker = CreateBroker();
        var files = new[]
        {
            scope.CreateGeneratedFile(broker.Id, "ar.xlsx"),
            scope.CreateGeneratedFile(broker.Id, "ar2.xlsx"),
            scope.CreateGeneratedFile(broker.Id, "andres.xlsx")
        };
        var item = CreateItem(broker, files.Select(file => file.OutputPath).ToArray());

        var result = scope.Service.SelectEligibleBrokers(
            [item], [broker], scope.CreateBatch(files), scope.SourceWorkbookPath);

        Assert.True(item.IsSelected);
        Assert.Equal(3, result.GeneratedFileCount);
        Assert.Equal(1, result.BrokersWithGeneratedFilesCount);
        Assert.Equal(1, result.SelectedBrokerCount);
    }

    [Fact]
    public void TwoBrokersWithValidFilesAreBothSelected()
    {
        using var scope = new TestDirectory();
        var first = CreateBroker();
        var second = CreateBroker();
        var firstFile = scope.CreateGeneratedFile(first.Id, "primero.xlsx");
        var secondFile = scope.CreateGeneratedFile(second.Id, "segundo.xlsx");
        var firstItem = CreateItem(first, firstFile.OutputPath);
        var secondItem = CreateItem(second, secondFile.OutputPath);

        var result = scope.Service.SelectEligibleBrokers(
            [firstItem, secondItem],
            [first, second],
            scope.CreateBatch(firstFile, secondFile),
            scope.SourceWorkbookPath);

        Assert.True(firstItem.IsSelected);
        Assert.True(secondItem.IsSelected);
        Assert.Equal(2, result.SelectedBrokerCount);
    }

    [Fact]
    public void BrokerWithoutCurrentGeneratedFilesIsNotSelected()
    {
        using var scope = new TestDirectory();
        var broker = CreateBroker();
        var item = CreateItem(broker);

        var result = scope.Service.SelectEligibleBrokers(
            [item], [broker], scope.CreateBatch(), scope.SourceWorkbookPath);

        Assert.False(item.IsSelected);
        Assert.Equal(0, result.SelectedBrokerCount);
    }

    [Fact]
    public void MissingGeneratedFileDoesNotSelectBroker()
    {
        using var scope = new TestDirectory();
        var broker = CreateBroker();
        var file = scope.CreateGeneratedFile(broker.Id, "missing.xlsx", createPhysicalFile: false);
        var item = CreateItem(broker, file.OutputPath);

        var result = scope.Service.SelectEligibleBrokers(
            [item], [broker], scope.CreateBatch(file), scope.SourceWorkbookPath);

        Assert.False(item.IsSelected);
        Assert.Equal(1, result.ExcludedBrokerCount);
    }

    [Fact]
    public void EmptyGeneratedPathDoesNotSelectBroker()
    {
        using var scope = new TestDirectory();
        var broker = CreateBroker();
        var file = new GeneratedPaymentFile { BrokerId = broker.Id, OutputPath = string.Empty };
        var item = CreateItem(broker, string.Empty);

        var result = scope.Service.SelectEligibleBrokers(
            [item], [broker], scope.CreateBatch(file), scope.SourceWorkbookPath);

        Assert.False(item.IsSelected);
        Assert.Equal(1, result.ExcludedBrokerCount);
    }

    [Fact]
    public void NonXlsxGeneratedFileDoesNotSelectBroker()
    {
        using var scope = new TestDirectory();
        var broker = CreateBroker();
        var file = scope.CreateGeneratedFile(broker.Id, "legacy.xls");
        var item = CreateItem(broker, file.OutputPath);

        scope.Service.SelectEligibleBrokers(
            [item], [broker], scope.CreateBatch(file), scope.SourceWorkbookPath);

        Assert.False(item.IsSelected);
    }

    [Fact]
    public void InactiveBrokerWithValidFileIsNotSelected()
    {
        using var scope = new TestDirectory();
        var broker = CreateBroker(isActive: false);
        var file = scope.CreateGeneratedFile(broker.Id, "inactivo.xlsx");
        var item = CreateItem(broker, file.OutputPath);

        scope.Service.SelectEligibleBrokers(
            [item], [broker], scope.CreateBatch(file), scope.SourceWorkbookPath);

        Assert.False(item.IsSelected);
    }

    [Fact]
    public void ManualFileDoesNotCauseAutomaticSelection()
    {
        using var scope = new TestDirectory();
        var broker = CreateBroker();
        var manualPath = scope.CreateFile("manual.xlsx");
        var item = CreateItem(broker);
        item.AttachmentPaths.Add(manualPath);

        scope.Service.SelectEligibleBrokers(
            [item], [broker], scope.CreateBatch(), scope.SourceWorkbookPath);

        Assert.False(item.IsSelected);
        Assert.True(item.HasManualAttachments);
    }

    [Fact]
    public void BatchFileNotAssociatedAsGeneratedDoesNotCauseAutomaticSelection()
    {
        using var scope = new TestDirectory();
        var broker = CreateBroker();
        var file = scope.CreateGeneratedFile(broker.Id, "no-asociado.xlsx");
        var item = CreateItem(broker);
        item.AttachmentPaths.Add(file.OutputPath);

        scope.Service.SelectEligibleBrokers(
            [item], [broker], scope.CreateBatch(file), scope.SourceWorkbookPath);

        Assert.False(item.IsSelected);
        Assert.True(item.HasManualAttachments);
    }

    [Fact]
    public void FileFromPreviousGenerationDoesNotCauseAutomaticSelection()
    {
        using var scope = new TestDirectory();
        var previousBroker = CreateBroker();
        var currentBroker = CreateBroker();
        var previousPath = scope.CreateFile("anterior.xlsx");
        var currentFile = scope.CreateGeneratedFile(currentBroker.Id, "actual.xlsx");
        var previousItem = CreateItem(previousBroker, previousPath);
        var currentItem = CreateItem(currentBroker, currentFile.OutputPath);

        scope.Service.SelectEligibleBrokers(
            [previousItem, currentItem],
            [previousBroker, currentBroker],
            scope.CreateBatch(currentFile),
            scope.SourceWorkbookPath);

        Assert.False(previousItem.IsSelected);
        Assert.True(currentItem.IsSelected);
    }

    [Fact]
    public void NewGenerationReplacesEveryPreviousSelection()
    {
        using var scope = new TestDirectory();
        var eligible = CreateBroker();
        var noFiles = CreateBroker();
        var eligibleFile = scope.CreateGeneratedFile(eligible.Id, "elegible.xlsx");
        var eligibleItem = CreateItem(eligible, eligibleFile.OutputPath);
        var noFilesItem = CreateItem(noFiles);
        eligibleItem.IsSelected = false;
        noFilesItem.IsSelected = true;

        scope.Service.SelectEligibleBrokers(
            [eligibleItem, noFilesItem],
            [eligible, noFiles],
            scope.CreateBatch(eligibleFile),
            scope.SourceWorkbookPath);

        Assert.True(eligibleItem.IsSelected);
        Assert.False(noFilesItem.IsSelected);
    }

    [Fact]
    public void PartiallyUnavailableGenerationSelectsOnlyBrokerWithValidFile()
    {
        using var scope = new TestDirectory();
        var available = CreateBroker();
        var unavailable = CreateBroker();
        var availableFile = scope.CreateGeneratedFile(available.Id, "disponible.xlsx");
        var missingFile = scope.CreateGeneratedFile(unavailable.Id, "eliminado.xlsx", createPhysicalFile: false);
        var availableItem = CreateItem(available, availableFile.OutputPath);
        var unavailableItem = CreateItem(unavailable, missingFile.OutputPath);

        var result = scope.Service.SelectEligibleBrokers(
            [availableItem, unavailableItem],
            [available, unavailable],
            scope.CreateBatch(availableFile, missingFile),
            scope.SourceWorkbookPath);

        Assert.True(availableItem.IsSelected);
        Assert.False(unavailableItem.IsSelected);
        Assert.Equal(1, result.SelectedBrokerCount);
        Assert.Equal(1, result.ExcludedBrokerCount);
    }

    [Fact]
    public void FailedGenerationLeavesEveryBrokerUnselected()
    {
        using var scope = new TestDirectory();
        var broker = CreateBroker();
        var file = scope.CreateGeneratedFile(broker.Id, "fallido.xlsx");
        var item = CreateItem(broker, file.OutputPath);
        item.IsSelected = true;
        var batch = scope.CreateBatch(file);
        batch.Status = PaymentGenerationStatus.Failed;

        var result = scope.Service.SelectEligibleBrokers(
            [item], [broker], batch, scope.SourceWorkbookPath);

        Assert.True(result.Succeeded);
        Assert.False(item.IsSelected);
        Assert.Equal(0, result.SelectedBrokerCount);
    }

    [Fact]
    public void ManualChangeAfterAutomaticSelectionIsPreserved()
    {
        using var scope = new TestDirectory();
        var broker = CreateBroker();
        var file = scope.CreateGeneratedFile(broker.Id, "manual-change.xlsx");
        var item = CreateItem(broker, file.OutputPath);
        scope.Service.SelectEligibleBrokers(
            [item], [broker], scope.CreateBatch(file), scope.SourceWorkbookPath);

        item.IsSelected = false;

        Assert.False(item.IsSelected);
    }

    [Fact]
    public void EmailBatchSelectionUsesTheSamePropertyUpdatedByAutomaticSelection()
    {
        using var scope = new TestDirectory();
        var eligible = CreateBroker();
        var excluded = CreateBroker();
        var file = scope.CreateGeneratedFile(eligible.Id, "envio.xlsx");
        var eligibleItem = CreateItem(eligible, file.OutputPath);
        var excludedItem = CreateItem(excluded);
        scope.Service.SelectEligibleBrokers(
            [eligibleItem, excludedItem],
            [eligible, excluded],
            scope.CreateBatch(file),
            scope.SourceWorkbookPath);

        var selectedForSending = EmailBatchSelection.GetSelected([eligibleItem, excludedItem]);

        Assert.Same(eligibleItem, Assert.Single(selectedForSending));
    }

    [Fact]
    public void SelectionExceptionIsLoggedAndLeavesNoPartialSelection()
    {
        using var scope = new TestDirectory();
        var first = CreateBroker();
        var second = CreateBroker();
        var firstFile = scope.CreateGeneratedFile(first.Id, "primero.xlsx");
        var secondFile = scope.CreateGeneratedFile(second.Id, "segundo.xlsx");
        var firstItem = CreateItem(first, firstFile.OutputPath);
        var secondItem = CreateItem(second, secondFile.OutputPath);
        firstItem.IsSelected = true;
        secondItem.IsSelected = true;
        var service = new GeneratedBrokerSelectionService(
            scope.Logger,
            _ => throw new IOException("Fallo simulado de disponibilidad."));

        var result = service.SelectEligibleBrokers(
            [firstItem, secondItem],
            [first, second],
            scope.CreateBatch(firstFile, secondFile),
            scope.SourceWorkbookPath);

        Assert.False(result.Succeeded);
        Assert.False(firstItem.IsSelected);
        Assert.False(secondItem.IsSelected);
        Assert.Contains("seleccionar automáticamente", File.ReadAllText(scope.Logger.LogFilePath));
    }

    [Fact]
    public void GenerationFromDifferentWorkbookLeavesEveryBrokerUnselected()
    {
        using var scope = new TestDirectory();
        var broker = CreateBroker();
        var file = scope.CreateGeneratedFile(broker.Id, "otro-origen.xlsx");
        var item = CreateItem(broker, file.OutputPath);
        var batch = scope.CreateBatch(file);
        batch.SourceWorkbookPath = scope.CreateFile("otro-general.xlsx");

        scope.Service.SelectEligibleBrokers(
            [item], [broker], batch, scope.SourceWorkbookPath);

        Assert.False(item.IsSelected);
    }

    private static Broker CreateBroker(bool isActive = true) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Corredor de prueba",
        IsActive = isActive
    };

    private static BrokerSendItem CreateItem(Broker broker, params string[] generatedPaths) => new()
    {
        BrokerId = broker.Id,
        BrokerName = broker.Name,
        IsSelected = true,
        AttachmentPaths = new ObservableCollection<string>(generatedPaths),
        GeneratedAttachmentPaths = new ObservableCollection<string>(generatedPaths)
    };

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ECSGeneratedBrokerSelectionTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            SourceWorkbookPath = CreateFile("general.xlsx");
            Logger = new FileLogger(new AppDataPaths(System.IO.Path.Combine(Path, "app-data")));
            Service = new GeneratedBrokerSelectionService(Logger);
        }

        public string Path { get; }
        public string SourceWorkbookPath { get; }
        public FileLogger Logger { get; }
        public GeneratedBrokerSelectionService Service { get; }

        public string CreateFile(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, "test");
            return path;
        }

        public GeneratedPaymentFile CreateGeneratedFile(
            Guid brokerId,
            string name,
            bool createPhysicalFile = true)
        {
            var path = System.IO.Path.Combine(Path, name);
            if (createPhysicalFile)
            {
                File.WriteAllText(path, "test");
            }

            return new GeneratedPaymentFile
            {
                BrokerId = brokerId,
                BrokerName = "Corredor de prueba",
                WorksheetName = System.IO.Path.GetFileNameWithoutExtension(name),
                OutputPath = path
            };
        }

        public PaymentGenerationBatch CreateBatch(params GeneratedPaymentFile[] files) => new()
        {
            SourceWorkbookPath = SourceWorkbookPath,
            Status = PaymentGenerationStatus.ReadyToSend,
            Files = [.. files]
        };

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
