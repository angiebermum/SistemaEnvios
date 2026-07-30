using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public enum AssociatedFileUnlinkStatus
{
    Removed,
    NotAssociated,
    PersistenceFailed
}

public sealed record AssociatedFileUnlinkResult(
    AssociatedFileUnlinkStatus Status,
    bool WasGenerated = false,
    bool RemovedLastFile = false,
    string ErrorMessage = "")
{
    public bool Succeeded => Status == AssociatedFileUnlinkStatus.Removed;
}

public sealed class AssociatedFileAssociationService
{
    private readonly FileLogger _logger;

    public AssociatedFileAssociationService(FileLogger logger) => _logger = logger;

    public AssociatedFileUnlinkResult Unlink(
        BrokerSendItem broker,
        string path,
        Action persistChanges)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(persistChanges);

        var attachmentSnapshot = broker.AttachmentPaths.ToList();
        var generatedSnapshot = broker.GeneratedAttachmentPaths.ToList();
        var selectedSnapshot = broker.IsSelected;
        var statusSnapshot = broker.Status;
        var errorSnapshot = broker.LastError;
        var wasGenerated = generatedSnapshot.Any(candidate => PathsEqual(candidate, path));

        if (!attachmentSnapshot.Any(candidate => PathsEqual(candidate, path)))
        {
            return new AssociatedFileUnlinkResult(AssociatedFileUnlinkStatus.NotAssociated);
        }

        try
        {
            RemoveMatchingPaths(broker.AttachmentPaths, path);
            RemoveMatchingPaths(broker.GeneratedAttachmentPaths, path);
            var removedLastFile = broker.AttachmentPaths.Count == 0;
            if (removedLastFile)
            {
                broker.IsSelected = false;
            }

            persistChanges();
            _logger.Info(
                $"Se quitó la asociación del archivo '{path}' con '{broker.BrokerName}'. " +
                "El archivo físico no fue modificado.");
            return new AssociatedFileUnlinkResult(
                AssociatedFileUnlinkStatus.Removed,
                wasGenerated,
                removedLastFile);
        }
        catch (Exception ex)
        {
            Restore(broker, attachmentSnapshot, generatedSnapshot, selectedSnapshot, statusSnapshot, errorSnapshot);
            _logger.Error(
                $"No fue posible persistir la desvinculación del archivo '{path}' de '{broker.BrokerName}'.",
                ex);
            return new AssociatedFileUnlinkResult(
                AssociatedFileUnlinkStatus.PersistenceFailed,
                wasGenerated,
                false,
                ex.Message);
        }
    }

    public static bool IsAssociated(BrokerSendItem broker, string path)
    {
        ArgumentNullException.ThrowIfNull(broker);
        return broker.AttachmentPaths.Any(candidate => PathsEqual(candidate, path));
    }

    public static bool IsGenerated(BrokerSendItem broker, string path)
    {
        ArgumentNullException.ThrowIfNull(broker);
        return broker.GeneratedAttachmentPaths.Any(candidate => PathsEqual(candidate, path));
    }

    private static void RemoveMatchingPaths(
        System.Collections.ObjectModel.ObservableCollection<string> paths,
        string candidate)
    {
        for (var index = paths.Count - 1; index >= 0; index--)
        {
            if (PathsEqual(paths[index], candidate))
            {
                paths.RemoveAt(index);
            }
        }
    }

    private static void Restore(
        BrokerSendItem broker,
        IReadOnlyList<string> attachments,
        IReadOnlyList<string> generatedAttachments,
        bool isSelected,
        SendStatus status,
        string lastError)
    {
        broker.AttachmentPaths.Clear();
        foreach (var attachment in attachments)
        {
            broker.AttachmentPaths.Add(attachment);
        }

        broker.GeneratedAttachmentPaths.Clear();
        foreach (var generatedAttachment in generatedAttachments)
        {
            broker.GeneratedAttachmentPaths.Add(generatedAttachment);
        }

        broker.IsSelected = isSelected;
        broker.Status = status;
        broker.LastError = lastError;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
