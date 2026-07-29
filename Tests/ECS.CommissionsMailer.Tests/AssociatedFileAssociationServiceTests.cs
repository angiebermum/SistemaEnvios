using System.Collections.ObjectModel;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class AssociatedFileAssociationServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnlinkRemovesOnlyAssociationAndNeverPhysicalFile(bool generated)
    {
        using var scope = new TestDirectory();
        var path = scope.File(generated ? "generated.xlsx" : "manual.xlsx");
        File.WriteAllText(path, "physical-content");
        var broker = CreateBroker(path, generated);
        var service = CreateService(scope);

        var result = service.Unlink(broker, path, () => { });

        Assert.True(result.Succeeded);
        Assert.Equal(generated, result.WasGenerated);
        Assert.Empty(broker.AttachmentPaths);
        Assert.Empty(broker.GeneratedAttachmentPaths);
        Assert.True(File.Exists(path));
        Assert.Equal("physical-content", File.ReadAllText(path));
    }

    [Fact]
    public void UnlinkOneOfSeveralFilesPreservesEveryOtherAssociation()
    {
        using var scope = new TestDirectory();
        var first = scope.File("first.xlsx");
        var selected = scope.File("selected.xlsx");
        var last = scope.File("last.xlsx");
        File.WriteAllText(first, "first");
        File.WriteAllText(selected, "selected");
        File.WriteAllText(last, "last");
        var broker = new BrokerSendItem
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            AttachmentPaths = new ObservableCollection<string>([first, selected, last]),
            GeneratedAttachmentPaths = new ObservableCollection<string>([first, selected])
        };

        var result = CreateService(scope).Unlink(broker, selected, () => { });

        Assert.True(result.Succeeded);
        Assert.Equal([first, last], broker.AttachmentPaths);
        Assert.Equal([first], broker.GeneratedAttachmentPaths);
        Assert.All([first, selected, last], path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void UnlinkLastFileDeselectsBrokerBeforePersisting()
    {
        using var scope = new TestDirectory();
        var path = scope.File("last.xlsx");
        File.WriteAllText(path, "content");
        var broker = CreateBroker(path, generated: false);
        broker.IsSelected = true;
        var observedDeselected = false;

        var result = CreateService(scope).Unlink(
            broker,
            path,
            () => observedDeselected = !broker.IsSelected && broker.AttachmentPaths.Count == 0);

        Assert.True(result.Succeeded);
        Assert.True(result.RemovedLastFile);
        Assert.True(observedDeselected);
        Assert.False(broker.IsSelected);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void CancellingDeletionByNotInvokingServiceChangesNothing()
    {
        using var scope = new TestDirectory();
        var path = scope.File("cancelled.xlsx");
        File.WriteAllText(path, "content");
        var broker = CreateBroker(path, generated: true);

        Assert.Single(broker.AttachmentPaths);
        Assert.Single(broker.GeneratedAttachmentPaths);
        Assert.True(broker.IsSelected);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void PersistenceFailureRestoresAssociationSelectionAndStatus()
    {
        using var scope = new TestDirectory();
        var path = scope.File("restore.xlsx");
        File.WriteAllText(path, "content");
        var broker = CreateBroker(path, generated: true);
        broker.Status = SendStatus.Ready;
        broker.LastError = "previous";

        var result = CreateService(scope).Unlink(
            broker,
            path,
            () =>
            {
                broker.Status = SendStatus.Pending;
                broker.LastError = string.Empty;
                throw new IOException("persistence failed");
            });

        Assert.Equal(AssociatedFileUnlinkStatus.PersistenceFailed, result.Status);
        Assert.Equal([path], broker.AttachmentPaths);
        Assert.Equal([path], broker.GeneratedAttachmentPaths);
        Assert.True(broker.IsSelected);
        Assert.Equal(SendStatus.Ready, broker.Status);
        Assert.Equal("previous", broker.LastError);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void PersistedUnlinkDoesNotReappearWhenSessionReloads()
    {
        using var scope = new TestDirectory();
        var path = scope.File("persisted.xlsx");
        File.WriteAllText(path, "content");
        var broker = CreateBroker(path, generated: true);
        var paths = new AppDataPaths(scope.File("app-data"));
        var logger = new FileLogger(paths);
        var sessions = new SessionService(paths, logger);
        var service = new AssociatedFileAssociationService(logger);

        var result = service.Unlink(
            broker,
            path,
            () => sessions.SaveCurrent(new CurrentSession { BrokerItems = [broker] }));
        var reloaded = sessions.LoadCurrent()!.BrokerItems.Single();

        Assert.True(result.Succeeded);
        Assert.Empty(reloaded.AttachmentPaths);
        Assert.Empty(reloaded.GeneratedAttachmentPaths);
        Assert.False(reloaded.IsSelected);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RemovingGeneratedFileFromCurrentGenerationKeepsSendValidationCoherent()
    {
        using var scope = new TestDirectory();
        var keptPath = scope.File("kept.xlsx");
        var unlinkedPath = scope.File("unlinked.xlsx");
        File.WriteAllText(keptPath, "kept");
        File.WriteAllText(unlinkedPath, "unlinked");
        var brokerId = Guid.NewGuid();
        var hash = new GeneratedFileHashService();
        var batch = new PaymentGenerationBatch
        {
            Files =
            [
                new GeneratedPaymentFile
                {
                    BrokerId = brokerId,
                    OutputPath = keptPath,
                    Sha256 = hash.ComputeSha256(keptPath)
                },
                new GeneratedPaymentFile
                {
                    BrokerId = brokerId,
                    OutputPath = unlinkedPath,
                    Sha256 = hash.ComputeSha256(unlinkedPath)
                }
            ]
        };
        var appPaths = new AppDataPaths(scope.File("app-data"));
        var history = new GenerationHistoryService(appPaths, new FileLogger(appPaths));
        batch.Files.RemoveAt(1);

        var errors = history.ValidateGeneratedAttachments(batch, brokerId, [keptPath]);

        Assert.Empty(errors);
        Assert.True(File.Exists(unlinkedPath));
    }

    private static AssociatedFileAssociationService CreateService(TestDirectory scope)
    {
        var paths = new AppDataPaths(scope.File("app-data"));
        return new AssociatedFileAssociationService(new FileLogger(paths));
    }

    private static BrokerSendItem CreateBroker(string path, bool generated) =>
        new()
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            IsSelected = true,
            AttachmentPaths = new ObservableCollection<string>([path]),
            GeneratedAttachmentPaths = generated
                ? new ObservableCollection<string>([path])
                : []
        };

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ECSAssociatedFileTests",
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
