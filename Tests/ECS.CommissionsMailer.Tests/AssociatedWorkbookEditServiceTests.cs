using System.Collections.ObjectModel;
using System.Diagnostics;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class AssociatedWorkbookEditServiceTests
{
    [Fact]
    public void BeginEditCreatesIsolatedRecognizableCopyAndOpensOnlyThatCopy()
    {
        using var scope = new TestDirectory();
        var original = scope.File("payment.xlsx");
        CreateWorkbook(original, "original");
        var launcher = new RecordingLauncher();
        var service = CreateService(scope, launcher);
        var broker = CreateBroker(original, generated: true);

        var start = service.BeginEdit(original);

        Assert.True(start.Succeeded);
        Assert.NotEqual(original, start.Session!.TemporaryPath);
        Assert.EndsWith(".xlsx", start.Session.TemporaryPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Edición - payment.xlsx", start.Session.TemporaryPath);
        Assert.StartsWith(
            new AppDataPaths(scope.File("app-data")).TemporaryEditsDirectory,
            start.Session.TemporaryPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(start.Session.TemporaryPath));
        Assert.Equal(start.Session.TemporaryPath, Assert.Single(launcher.Requests).FileName);
        Assert.Equal([original], broker.AttachmentPaths);
        Assert.DoesNotContain(start.Session.TemporaryPath, broker.AttachmentPaths);
        service.Cancel(start.Session);
    }

    [Fact]
    public void CheckWithoutChangesDoesNotReplaceOriginal()
    {
        using var scope = new TestDirectory();
        var original = scope.File("unchanged.xlsx");
        CreateWorkbook(original, "original");
        var service = CreateService(scope);
        var start = service.BeginEdit(original);
        var originalHash = Hash(original);

        var check = service.CheckChanges(start.Session!);

        Assert.Equal(WorkbookChangeCheckStatus.NoChanges, check.Status);
        Assert.Equal(originalHash, Hash(original));
        service.Cancel(start.Session!);
    }

    [Fact]
    public void CancelAfterEditingPreservesOriginalAndRemovesTemporaryCopy()
    {
        using var scope = new TestDirectory();
        var original = scope.File("cancel.xlsx");
        CreateWorkbook(original, "original");
        var service = CreateService(scope);
        var start = service.BeginEdit(original);
        var originalHash = Hash(original);
        CreateWorkbook(start.Session!.TemporaryPath, "edited");

        service.Cancel(start.Session);

        Assert.Equal(originalHash, Hash(original));
        Assert.False(Directory.Exists(start.Session.TemporaryDirectory));
    }

    [Fact]
    public void ChangedCopyDeclinedByUserLeavesOriginalAndAssociationIntact()
    {
        using var scope = new TestDirectory();
        var original = scope.File("declined.xlsx");
        CreateWorkbook(original, "original");
        var broker = CreateBroker(original, generated: false);
        var service = CreateService(scope);
        var start = service.BeginEdit(original);
        var originalHash = Hash(original);
        CreateWorkbook(start.Session!.TemporaryPath, "edited");

        var check = service.CheckChanges(start.Session);
        service.Cancel(start.Session);

        Assert.True(check.HasChanges);
        Assert.Equal(originalHash, Hash(original));
        Assert.Equal([original], broker.AttachmentPaths);
        Assert.Empty(broker.GeneratedAttachmentPaths);
    }

    [Fact]
    public void ConfirmedReplacementKeepsExactPathAssociationAndCreatesNoDuplicate()
    {
        using var scope = new TestDirectory();
        var original = scope.File("replace.xlsx");
        CreateWorkbook(original, "original");
        var broker = CreateBroker(original, generated: true);
        var service = CreateService(scope);
        var start = service.BeginEdit(original);
        CreateWorkbook(start.Session!.TemporaryPath, "edited");
        var editedHash = Hash(start.Session.TemporaryPath);
        var metadataHash = string.Empty;

        var replacement = service.Replace(start.Session, hash => metadataHash = hash);

        Assert.True(replacement.Succeeded);
        Assert.Equal(editedHash, Hash(original));
        Assert.Equal(editedHash, metadataHash);
        Assert.Equal([original], broker.AttachmentPaths);
        Assert.Equal([original], broker.GeneratedAttachmentPaths);
        Assert.False(Directory.Exists(start.Session.TemporaryDirectory));
        Assert.Equal([original], Directory.GetFiles(scope.Path, "*.xlsx"));
    }

    [Fact]
    public void InvalidEditedWorkbookNeverReplacesOriginal()
    {
        using var scope = new TestDirectory();
        var original = scope.File("invalid-edit.xlsx");
        CreateWorkbook(original, "original");
        var service = CreateService(scope);
        var start = service.BeginEdit(original);
        var originalHash = Hash(original);
        File.WriteAllText(start.Session!.TemporaryPath, "not an xlsx package");

        var check = service.CheckChanges(start.Session);
        var replacement = service.Replace(start.Session);

        Assert.Equal(WorkbookChangeCheckStatus.InvalidWorkbook, check.Status);
        Assert.Equal(WorkbookReplaceStatus.InvalidWorkbook, replacement.Status);
        Assert.Equal(originalHash, Hash(original));
        service.Cancel(start.Session);
    }

    [Fact]
    public void OpenTemporaryWorkbookIsReportedAsLocked()
    {
        using var scope = new TestDirectory();
        var original = scope.File("locked.xlsx");
        CreateWorkbook(original, "original");
        var service = CreateService(scope);
        var start = service.BeginEdit(original);

        using (new FileStream(
                   start.Session!.TemporaryPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            var check = service.CheckChanges(start.Session);
            Assert.Equal(WorkbookChangeCheckStatus.FileLocked, check.Status);
        }

        service.Cancel(start.Session!);
    }

    [Fact]
    public void OpenOriginalWorkbookBlocksReplacementBeforeAtomicOperation()
    {
        using var scope = new TestDirectory();
        var original = scope.File("locked-original.xlsx");
        CreateWorkbook(original, "original");
        var atomicReplacer = new RecordingAtomicReplacer();
        var service = CreateService(scope, atomicReplacer: atomicReplacer);
        var start = service.BeginEdit(original);
        CreateWorkbook(start.Session!.TemporaryPath, "edited");
        var originalHash = Hash(original);

        using (new FileStream(
                   original,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            var replacement = service.Replace(start.Session);
            Assert.Equal(WorkbookReplaceStatus.FileLocked, replacement.Status);
        }

        Assert.False(atomicReplacer.WasCalled);
        Assert.Equal(originalHash, Hash(original));
        service.Cancel(start.Session!);
    }

    [Fact]
    public void AtomicReplacementFailurePreservesOriginalAndAssociation()
    {
        using var scope = new TestDirectory();
        var original = scope.File("atomic-failure.xlsx");
        CreateWorkbook(original, "original");
        var broker = CreateBroker(original, generated: true);
        var originalHash = Hash(original);
        var service = CreateService(
            scope,
            atomicReplacer: new ThrowingAtomicReplacer());
        var start = service.BeginEdit(original);
        CreateWorkbook(start.Session!.TemporaryPath, "edited");

        var replacement = service.Replace(start.Session);

        Assert.Equal(WorkbookReplaceStatus.ReplacementFailedOriginalPreserved, replacement.Status);
        Assert.Equal(originalHash, Hash(original));
        Assert.Equal([original], broker.AttachmentPaths);
        Assert.Equal([original], broker.GeneratedAttachmentPaths);
        Assert.DoesNotContain(
            Directory.GetFiles(scope.Path),
            path => path.Contains(".ecs-replacement.", StringComparison.OrdinalIgnoreCase));
        service.Cancel(start.Session);
    }

    [Fact]
    public void MetadataPersistenceFailureRestoresOriginalContent()
    {
        using var scope = new TestDirectory();
        var original = scope.File("metadata-failure.xlsx");
        CreateWorkbook(original, "original");
        var originalHash = Hash(original);
        var service = CreateService(scope);
        var start = service.BeginEdit(original);
        CreateWorkbook(start.Session!.TemporaryPath, "edited");

        var replacement = service.Replace(
            start.Session,
            _ => throw new IOException("metadata persistence failed"));

        Assert.Equal(WorkbookReplaceStatus.ReplacementFailedOriginalPreserved, replacement.Status);
        Assert.Equal(originalHash, Hash(original));
        service.Cancel(start.Session);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplacementPreservesGeneratedOrManualOrigin(bool generated)
    {
        using var scope = new TestDirectory();
        var original = scope.File(generated ? "generated.xlsx" : "manual.xlsx");
        CreateWorkbook(original, "original");
        var broker = CreateBroker(original, generated);
        var service = CreateService(scope);
        var start = service.BeginEdit(original);
        CreateWorkbook(start.Session!.TemporaryPath, "edited");

        var replacement = service.Replace(start.Session);

        Assert.True(replacement.Succeeded);
        Assert.Equal([original], broker.AttachmentPaths);
        Assert.Equal(
            generated ? [original] : [],
            broker.GeneratedAttachmentPaths);
    }

    [Fact]
    public void SendCollectionContinuesUsingOriginalPathWithReplacedContentNotTemporaryPath()
    {
        using var scope = new TestDirectory();
        var original = scope.File("send.xlsx");
        CreateWorkbook(original, "original");
        var broker = CreateBroker(original, generated: false);
        var service = CreateService(scope);
        var start = service.BeginEdit(original);
        var temporaryPath = start.Session!.TemporaryPath;
        CreateWorkbook(temporaryPath, "edited");
        var editedHash = Hash(temporaryPath);

        var replacement = service.Replace(start.Session);
        var sendPaths = broker.AttachmentPaths.ToList();

        Assert.True(replacement.Succeeded);
        Assert.Equal([original], sendPaths);
        Assert.DoesNotContain(temporaryPath, sendPaths);
        Assert.Equal(editedHash, Hash(sendPaths.Single()));
    }

    [Fact]
    public void ExternalChangeToOriginalBlocksReplacement()
    {
        using var scope = new TestDirectory();
        var original = scope.File("concurrent.xlsx");
        CreateWorkbook(original, "original");
        var service = CreateService(scope);
        var start = service.BeginEdit(original);
        CreateWorkbook(start.Session!.TemporaryPath, "user edit");
        CreateWorkbook(original, "external edit");
        var externalHash = Hash(original);

        var check = service.CheckChanges(start.Session);
        var replacement = service.Replace(start.Session);

        Assert.Equal(WorkbookChangeCheckStatus.OriginalChanged, check.Status);
        Assert.Equal(WorkbookReplaceStatus.OriginalChanged, replacement.Status);
        Assert.Equal(externalHash, Hash(original));
        service.Cancel(start.Session);
    }

    [Fact]
    public void MissingUnsupportedAndDamagedOriginalsAreRejectedBeforeCopying()
    {
        using var scope = new TestDirectory();
        var service = CreateService(scope);
        var missing = scope.File("missing.xlsx");
        var legacy = scope.File("legacy.xls");
        var damaged = scope.File("damaged.xlsx");
        File.WriteAllText(legacy, "legacy");
        File.WriteAllText(damaged, "damaged");

        var missingResult = service.BeginEdit(missing);
        var legacyResult = service.BeginEdit(legacy);
        var damagedResult = service.BeginEdit(damaged);

        Assert.Equal(WorkbookEditStartStatus.OriginalNotFound, missingResult.Status);
        Assert.Equal(WorkbookEditStartStatus.UnsupportedExtension, legacyResult.Status);
        Assert.Equal(WorkbookEditStartStatus.InvalidWorkbook, damagedResult.Status);
    }

    private static AssociatedWorkbookEditService CreateService(
        TestDirectory scope,
        IGeneratedFileProcessLauncher? launcher = null,
        IAssociatedWorkbookAtomicReplacer? atomicReplacer = null)
    {
        var paths = new AppDataPaths(scope.File("app-data"));
        return new AssociatedWorkbookEditService(
            paths,
            new FileLogger(paths),
            launcher ?? new RecordingLauncher(),
            atomicReplacer: atomicReplacer);
    }

    private static BrokerSendItem CreateBroker(string path, bool generated) =>
        new()
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            AttachmentPaths = new ObservableCollection<string>([path]),
            GeneratedAttachmentPaths = generated
                ? new ObservableCollection<string>([path])
                : []
        };

    private static string Hash(string path) => new GeneratedFileHashService().ComputeSha256(path);

    private static void CreateWorkbook(string path, string value)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(
            new SheetData(
                new Row(
                    new Cell
                    {
                        DataType = CellValues.InlineString,
                        InlineString = new InlineString(new Text(value))
                    })));
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Sheet1"
        });
        workbookPart.Workbook.Save();
    }

    private sealed class RecordingLauncher : IGeneratedFileProcessLauncher
    {
        public List<ProcessStartInfo> Requests { get; } = [];
        public void Start(ProcessStartInfo startInfo) => Requests.Add(startInfo);
    }

    private sealed class ThrowingAtomicReplacer : IAssociatedWorkbookAtomicReplacer
    {
        public void Replace(string preparedPath, string destinationPath, string backupPath) =>
            throw new IOException("simulated replacement failure");
    }

    private sealed class RecordingAtomicReplacer : IAssociatedWorkbookAtomicReplacer
    {
        public bool WasCalled { get; private set; }

        public void Replace(string preparedPath, string destinationPath, string backupPath)
        {
            WasCalled = true;
            File.Replace(preparedPath, destinationPath, backupPath, true);
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ECSWorkbookEditTests",
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
