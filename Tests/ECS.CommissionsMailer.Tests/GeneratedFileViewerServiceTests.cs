using System.Collections.ObjectModel;
using System.Diagnostics;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class GeneratedFileViewerServiceTests
{
    [Fact]
    public void BeforeGenerationNoFilesAreDisplayed()
    {
        using var scope = new TestDirectory();
        var broker = CreateBrokerSendItem(Guid.NewGuid(), "Corredor");
        var service = CreateService(scope, new RecordingProcessLauncher());

        var files = service.GetFilesForBroker(null, broker);

        Assert.Empty(files);
    }

    [Fact]
    public void BrokerWithOneFileDisplaysThatSameGeneratedFile()
    {
        using var scope = new TestDirectory();
        var brokerId = Guid.NewGuid();
        var file = CreateGeneratedFile(brokerId, "Corredor uno", "UNO", scope.File("uno.xlsx"));
        var broker = CreateBrokerSendItem(brokerId, "Corredor uno", file.OutputPath);
        var batch = new PaymentGenerationBatch { Files = [file] };
        var service = CreateService(scope, new RecordingProcessLauncher());

        var displayed = service.GetFilesForBroker(batch, broker);

        Assert.Same(file, Assert.Single(displayed));
    }

    [Fact]
    public void BrokerWithThreeFilesDisplaysEveryGeneratedFile()
    {
        using var scope = new TestDirectory();
        var brokerId = Guid.NewGuid();
        var files = new[]
        {
            CreateGeneratedFile(brokerId, "Corredor múltiple", "AR", scope.File("ar.xlsx")),
            CreateGeneratedFile(brokerId, "Corredor múltiple", "AR2", scope.File("ar2.xlsx")),
            CreateGeneratedFile(brokerId, "Corredor múltiple", "ANDRES", scope.File("andres.xlsx"))
        };
        var broker = CreateBrokerSendItem(
            brokerId,
            "Corredor múltiple",
            files.Select(value => value.OutputPath).ToArray());
        var batch = new PaymentGenerationBatch { Files = [.. files] };
        var service = CreateService(scope, new RecordingProcessLauncher());

        var displayed = service.GetFilesForBroker(batch, broker);

        Assert.Equal(3, displayed.Count);
        Assert.Equal(files, displayed);
    }

    [Fact]
    public void FilesFromDifferentBrokersAreNeverMixed()
    {
        using var scope = new TestDirectory();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstFile = CreateGeneratedFile(firstId, "Primero", "P1", scope.File("primero.xlsx"));
        var secondFile = CreateGeneratedFile(secondId, "Segundo", "P2", scope.File("segundo.xlsx"));
        var batch = new PaymentGenerationBatch { Files = [firstFile, secondFile] };
        var firstBroker = CreateBrokerSendItem(firstId, "Primero", firstFile.OutputPath);
        var secondBroker = CreateBrokerSendItem(secondId, "Segundo", secondFile.OutputPath);
        var service = CreateService(scope, new RecordingProcessLauncher());

        var displayedForFirst = service.GetFilesForBroker(batch, firstBroker);
        var displayedForSecond = service.GetFilesForBroker(batch, secondBroker);

        Assert.Same(firstFile, Assert.Single(displayedForFirst));
        Assert.Same(secondFile, Assert.Single(displayedForSecond));
        Assert.DoesNotContain(secondFile, displayedForFirst);
        Assert.DoesNotContain(firstFile, displayedForSecond);
    }

    [Fact]
    public void FileRemovedFromEmailAttachmentsIsNotDisplayedAsAssociated()
    {
        using var scope = new TestDirectory();
        var brokerId = Guid.NewGuid();
        var file = CreateGeneratedFile(brokerId, "Corredor", "AR", scope.File("removed.xlsx"));
        var broker = CreateBrokerSendItem(brokerId, "Corredor", file.OutputPath);
        broker.AttachmentPaths.Clear();
        var service = CreateService(scope, new RecordingProcessLauncher());

        var displayed = service.GetFilesForBroker(
            new PaymentGenerationBatch { Files = [file] },
            broker);

        Assert.Empty(displayed);
    }

    [Fact]
    public void ExistingXlsxIsAvailableAndDeletedFileIsNotFound()
    {
        using var scope = new TestDirectory();
        var path = scope.File("available.xlsx");
        File.WriteAllText(path, "test");

        Assert.Equal(
            GeneratedFileAvailability.Available,
            GeneratedFileViewerService.GetAvailability(path));

        File.Delete(path);

        Assert.Equal(
            GeneratedFileAvailability.NotFound,
            GeneratedFileViewerService.GetAvailability(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.xlsx")]
    [InlineData("not-an-excel.txt")]
    public void InvalidPathIsReportedAsNotFound(string path)
    {
        Assert.Equal(
            GeneratedFileAvailability.NotFound,
            GeneratedFileViewerService.GetAvailability(path));
    }

    [Fact]
    public void ValidGeneratedPathRequestsShellExecutionOfTemporaryCopy()
    {
        using var scope = new TestDirectory();
        var path = scope.File("payment.xlsx");
        File.WriteAllText(path, "test");
        var brokerId = Guid.NewGuid();
        var file = CreateGeneratedFile(brokerId, "Corredor", "AR", path);
        var broker = CreateBrokerSendItem(brokerId, "Corredor", path);
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(scope, launcher);

        var result = service.Open(file, broker);

        Assert.True(result.Succeeded);
        var request = Assert.Single(launcher.Requests);
        Assert.NotEqual(broker.AttachmentPaths.Single(), request.FileName);
        Assert.NotEqual(file.OutputPath, request.FileName);
        Assert.Equal(result.OpenedPath, request.FileName);
        Assert.Equal(
            Path.Combine(scope.File("app-data"), "TempView"),
            Path.GetDirectoryName(request.FileName));
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(request.FileName));
        Assert.True(request.UseShellExecute);
    }

    [Fact]
    public void MissingFileDoesNotStartAProcess()
    {
        using var scope = new TestDirectory();
        var path = scope.File("missing.xlsx");
        var brokerId = Guid.NewGuid();
        var file = CreateGeneratedFile(brokerId, "Corredor", "AR", path);
        var broker = CreateBrokerSendItem(brokerId, "Corredor", path);
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(scope, launcher);

        var result = service.Open(file, broker);

        Assert.Equal(GeneratedFileOpenStatus.FileNotFound, result.Status);
        Assert.Empty(launcher.Requests);
    }

    [Fact]
    public void EmptyPathIsHandledWithoutStartingAProcess()
    {
        using var scope = new TestDirectory();
        var brokerId = Guid.NewGuid();
        var file = CreateGeneratedFile(brokerId, "Corredor", "AR", string.Empty);
        var broker = CreateBrokerSendItem(brokerId, "Corredor", string.Empty);
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(scope, launcher);

        var result = service.Open(file, broker);

        Assert.Equal(GeneratedFileOpenStatus.MissingPath, result.Status);
        Assert.Empty(launcher.Requests);
    }

    [Fact]
    public void ExistingNonXlsxFileDoesNotStartAProcess()
    {
        using var scope = new TestDirectory();
        var path = scope.File("payment.xls");
        File.WriteAllText(path, "test");
        var brokerId = Guid.NewGuid();
        var file = CreateGeneratedFile(brokerId, "Corredor", "AR", path);
        var broker = CreateBrokerSendItem(brokerId, "Corredor", path);
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(scope, launcher);

        var result = service.Open(file, broker);

        Assert.Equal(GeneratedFileOpenStatus.UnsupportedExtension, result.Status);
        Assert.Empty(launcher.Requests);
    }

    [Fact]
    public void FileNotAssociatedWithBrokerDoesNotStartAProcess()
    {
        using var scope = new TestDirectory();
        var path = scope.File("payment.xlsx");
        File.WriteAllText(path, "test");
        var brokerId = Guid.NewGuid();
        var file = CreateGeneratedFile(brokerId, "Corredor", "AR", path);
        var broker = CreateBrokerSendItem(brokerId, "Corredor");
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(scope, launcher);

        var result = service.Open(file, broker);

        Assert.Equal(GeneratedFileOpenStatus.NotAssociated, result.Status);
        Assert.Empty(launcher.Requests);
    }

    [Fact]
    public void ProcessFailureIsCaughtAndReported()
    {
        using var scope = new TestDirectory();
        var path = scope.File("payment.xlsx");
        File.WriteAllText(path, "test");
        var brokerId = Guid.NewGuid();
        var file = CreateGeneratedFile(brokerId, "Corredor", "AR", path);
        var broker = CreateBrokerSendItem(brokerId, "Corredor", path);
        var launcher = new RecordingProcessLauncher { ExceptionToThrow = new InvalidOperationException("boom") };
        var service = CreateService(scope, launcher);

        var result = service.Open(file, broker);

        Assert.Equal(GeneratedFileOpenStatus.OpenFailed, result.Status);
        var temporaryPath = Assert.Single(launcher.Requests).FileName;
        Assert.NotEqual(path, temporaryPath);
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public void AssociatedManualXlsIsRejectedWithoutOpeningOriginal()
    {
        using var scope = new TestDirectory();
        var path = scope.File("manual.xls");
        File.WriteAllText(path, "legacy excel");
        var broker = new BrokerSendItem
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            AttachmentPaths = new ObservableCollection<string>([path])
        };
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(scope, launcher);

        var result = service.OpenAssociated(path, broker);

        Assert.Equal(GeneratedFileOpenStatus.UnsupportedExtension, result.Status);
        Assert.Empty(launcher.Requests);
        Assert.Equal([path], broker.AttachmentPaths);
        Assert.Empty(broker.GeneratedAttachmentPaths);
    }

    [Fact]
    public void ViewingAssociatedWorkbookPreservesOriginalHashTimestampAndAssociation()
    {
        using var scope = new TestDirectory();
        var path = scope.File("associated.xlsx");
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04, 0x10, 0x20]);
        var originalTimestamp = new DateTime(2026, 8, 6, 9, 35, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, originalTimestamp);
        var originalHash = new GeneratedFileHashService().ComputeSha256(path);
        var originalLength = new FileInfo(path).Length;
        var broker = new BrokerSendItem
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            AttachmentPaths = new ObservableCollection<string>([path])
        };
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(scope, launcher);

        var result = service.OpenAssociated(path, broker);

        Assert.True(result.Succeeded);
        var temporaryPath = Assert.Single(launcher.Requests).FileName;
        Assert.Equal(result.OpenedPath, temporaryPath);
        Assert.NotEqual(path, temporaryPath);
        Assert.True(File.Exists(temporaryPath));

        File.WriteAllText(temporaryPath, "Excel modificó únicamente la copia");

        Assert.Equal(originalHash, new GeneratedFileHashService().ComputeSha256(path));
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(originalLength, new FileInfo(path).Length);
        Assert.Equal([path], broker.AttachmentPaths);
        Assert.DoesNotContain(temporaryPath, broker.AttachmentPaths);
        Assert.Empty(broker.GeneratedAttachmentPaths);
    }

    [Fact]
    public void ExpirationsAssociatedWorkbookOpensOnlyAControlledTemporaryCopy()
    {
        using var scope = new TestDirectory();
        var path = scope.File("expirations.xlsx");
        ExpirationsUatCompletionTests.CreateValidWorkbook(path);
        var originalHash = new GeneratedFileHashService().ComputeSha256(path);
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(scope, launcher);

        var result = service.OpenAssociated(path, "Corredor Vencimientos", [path]);

        Assert.True(result.Succeeded);
        var temporaryPath = Assert.Single(launcher.Requests).FileName;
        Assert.NotEqual(path, temporaryPath);
        Assert.Equal(result.OpenedPath, temporaryPath);
        Assert.Equal(originalHash, new GeneratedFileHashService().ComputeSha256(path));
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(temporaryPath));
    }

    [Fact]
    public void EditingViewCopyDoesNotTriggerGeneratedOriginalHashWarning()
    {
        using var scope = new TestDirectory();
        var path = scope.File("generated.xlsx");
        File.WriteAllText(path, "original generado");
        var brokerId = Guid.NewGuid();
        var generatedFile = CreateGeneratedFile(brokerId, "Corredor", "ASW", path);
        generatedFile.Sha256 = new GeneratedFileHashService().ComputeSha256(path);
        var broker = CreateBrokerSendItem(brokerId, "Corredor", path);
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(scope, launcher);

        var result = service.OpenAssociated(path, broker);
        File.WriteAllText(result.OpenedPath!, "cambio interno realizado por Excel");
        var paths = new AppDataPaths(scope.File("app-data"));
        var history = new GenerationHistoryService(paths, new FileLogger(paths));
        var errors = history.ValidateGeneratedAttachments(
            new PaymentGenerationBatch { Files = [generatedFile] },
            brokerId,
            broker.GeneratedAttachmentPaths);

        Assert.True(result.Succeeded);
        Assert.Empty(errors);
        Assert.Equal("original generado", File.ReadAllText(path));
        Assert.Equal([path], broker.AttachmentPaths);
        Assert.Equal([path], broker.GeneratedAttachmentPaths);
    }

    [Fact]
    public void ViewingSameWorkbookSeveralTimesCreatesUniqueTemporaryNames()
    {
        using var scope = new TestDirectory();
        var path = scope.File("same.xlsx");
        File.WriteAllText(path, "original");
        var broker = new BrokerSendItem
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            AttachmentPaths = new ObservableCollection<string>([path])
        };
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(scope, launcher);

        var first = service.OpenAssociated(path, broker);
        var second = service.OpenAssociated(path, broker);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotEqual(first.OpenedPath, second.OpenedPath);
        Assert.Equal(2, launcher.Requests.Select(value => value.FileName).Distinct().Count());
        Assert.All(launcher.Requests, request => Assert.NotEqual(path, request.FileName));
    }

    [Fact]
    public void TemporaryDirectoryCreationFailurePreservesOriginalAndAssociation()
    {
        using var scope = new TestDirectory();
        var originalPath = scope.File("original.xlsx");
        File.WriteAllText(originalPath, "original intacto");
        var originalHash = new GeneratedFileHashService().ComputeSha256(originalPath);
        var paths = new AppDataPaths(scope.File("app-data"));
        File.WriteAllText(paths.TemporaryViewsDirectory, "impide crear el directorio");
        var launcher = new RecordingProcessLauncher();
        var service = new GeneratedFileViewerService(launcher, paths, new FileLogger(paths));
        var broker = new BrokerSendItem
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            AttachmentPaths = new ObservableCollection<string>([originalPath])
        };

        var result = service.OpenAssociated(originalPath, broker);

        Assert.Equal(GeneratedFileOpenStatus.TemporaryCopyFailed, result.Status);
        Assert.Empty(launcher.Requests);
        Assert.Equal(originalHash, new GeneratedFileHashService().ComputeSha256(originalPath));
        Assert.Equal([originalPath], broker.AttachmentPaths);
    }

    [Fact]
    public void SourceCopyFailurePreservesOriginalAndAssociation()
    {
        using var scope = new TestDirectory();
        var originalPath = scope.File("locked-original.xlsx");
        File.WriteAllText(originalPath, "original bloqueado");
        var originalHash = new GeneratedFileHashService().ComputeSha256(originalPath);
        var broker = new BrokerSendItem
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            AttachmentPaths = new ObservableCollection<string>([originalPath])
        };
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(scope, launcher);

        GeneratedFileOpenResult result;
        using (new FileStream(originalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = service.OpenAssociated(originalPath, broker);
        }

        Assert.Equal(GeneratedFileOpenStatus.TemporaryCopyFailed, result.Status);
        Assert.Empty(launcher.Requests);
        Assert.Equal(originalHash, new GeneratedFileHashService().ComputeSha256(originalPath));
        Assert.Equal([originalPath], broker.AttachmentPaths);
    }

    [Fact]
    public void LockedTemporaryCopyRemainsPendingUntilLaterCleanup()
    {
        using var scope = new TestDirectory();
        var originalPath = scope.File("locked-view.xlsx");
        File.WriteAllText(originalPath, "original");
        var broker = new BrokerSendItem
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            AttachmentPaths = new ObservableCollection<string>([originalPath])
        };
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(scope, launcher);
        var result = service.OpenAssociated(originalPath, broker);
        var temporaryPath = result.OpenedPath!;

        using (new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            service.CleanupTemporaryViewCopies();
            Assert.True(File.Exists(temporaryPath));
        }

        service.CleanupTemporaryViewCopies();

        Assert.False(File.Exists(temporaryPath));
        Assert.Equal("original", File.ReadAllText(originalPath));
        Assert.Equal([originalPath], broker.AttachmentPaths);
    }

    [Fact]
    public void StaleTemporaryCopyIsCleanedWhenViewerServiceStarts()
    {
        using var scope = new TestDirectory();
        var originalPath = scope.File("stale-view.xlsx");
        File.WriteAllText(originalPath, "original");
        var broker = new BrokerSendItem
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            AttachmentPaths = new ObservableCollection<string>([originalPath])
        };
        var launcher = new RecordingProcessLauncher();
        var firstService = CreateService(scope, launcher);
        var temporaryPath = firstService.OpenAssociated(originalPath, broker).OpenedPath!;
        File.SetLastWriteTimeUtc(temporaryPath, DateTime.UtcNow.AddDays(-2));

        _ = CreateService(scope, new RecordingProcessLauncher());

        Assert.False(File.Exists(temporaryPath));
        Assert.True(File.Exists(originalPath));
        Assert.Equal([originalPath], broker.AttachmentPaths);
    }

    [Fact]
    public void CleanupNeverDeletesAssociatedOriginalEvenWhenItIsInsideTempView()
    {
        using var scope = new TestDirectory();
        var paths = new AppDataPaths(scope.File("app-data"));
        Directory.CreateDirectory(paths.TemporaryViewsDirectory);
        var originalPath = Path.Combine(paths.TemporaryViewsDirectory, "real-associated.xlsx");
        File.WriteAllText(originalPath, "archivo real asociado");
        var broker = new BrokerSendItem
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            AttachmentPaths = new ObservableCollection<string>([originalPath])
        };
        var launcher = new RecordingProcessLauncher();
        var service = new GeneratedFileViewerService(launcher, paths, new FileLogger(paths));
        var result = service.OpenAssociated(originalPath, broker);

        service.CleanupTemporaryViewCopies();

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(originalPath));
        Assert.Equal("archivo real asociado", File.ReadAllText(originalPath));
        Assert.False(File.Exists(result.OpenedPath));
        Assert.Equal([originalPath], broker.AttachmentPaths);
    }

    [Fact]
    public void MissingAssociatedFileRemainsAssociatedUntilUserUnlinksIt()
    {
        using var scope = new TestDirectory();
        var path = scope.File("missing-associated.xlsx");
        var broker = new BrokerSendItem
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            AttachmentPaths = new ObservableCollection<string>([path])
        };
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(scope, launcher);

        var result = service.OpenAssociated(path, broker);

        Assert.Equal(GeneratedFileOpenStatus.FileNotFound, result.Status);
        Assert.Empty(launcher.Requests);
        Assert.Equal([path], broker.AttachmentPaths);
    }

    private static GeneratedFileViewerService CreateService(
        TestDirectory scope,
        IGeneratedFileProcessLauncher launcher)
    {
        var paths = new AppDataPaths(scope.File("app-data"));
        return new GeneratedFileViewerService(launcher, paths, new FileLogger(paths));
    }

    private static BrokerSendItem CreateBrokerSendItem(
        Guid brokerId,
        string brokerName,
        params string[] paths) =>
        new()
        {
            BrokerId = brokerId,
            BrokerName = brokerName,
            AttachmentPaths = new ObservableCollection<string>(paths),
            GeneratedAttachmentPaths = new ObservableCollection<string>(paths)
        };

    private static GeneratedPaymentFile CreateGeneratedFile(
        Guid brokerId,
        string brokerName,
        string worksheetName,
        string path) =>
        new()
        {
            BrokerId = brokerId,
            BrokerName = brokerName,
            WorksheetName = worksheetName,
            OutputPath = path
        };

    private sealed class RecordingProcessLauncher : IGeneratedFileProcessLauncher
    {
        public List<ProcessStartInfo> Requests { get; } = [];
        public Exception? ExceptionToThrow { get; init; }

        public void Start(ProcessStartInfo startInfo)
        {
            Requests.Add(startInfo);
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ECSGeneratedFileViewerTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
