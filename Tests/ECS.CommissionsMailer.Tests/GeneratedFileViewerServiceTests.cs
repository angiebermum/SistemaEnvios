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
    public void ValidAssociatedPathRequestsShellExecutionOfExactAttachment()
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
        Assert.Equal(broker.AttachmentPaths.Single(), request.FileName);
        Assert.Equal(file.OutputPath, request.FileName);
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
        Assert.Single(launcher.Requests);
    }

    [Fact]
    public void AssociatedManualXlsKeepsExistingViewBehavior()
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

        Assert.True(result.Succeeded);
        Assert.Equal(path, Assert.Single(launcher.Requests).FileName);
        Assert.Equal([path], broker.AttachmentPaths);
        Assert.Empty(broker.GeneratedAttachmentPaths);
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
        IGeneratedFileProcessLauncher launcher) =>
        new(launcher, new FileLogger(new AppDataPaths(scope.File("app-data"))));

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
