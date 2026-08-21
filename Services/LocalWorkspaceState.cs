using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public sealed class LocalWorkspaceState
{
    public string? SignatureImagePath { get; set; }
    public string GeneralWorkbookPath { get; set; } = string.Empty;
    public string GeneratedOutputDirectory { get; set; } = string.Empty;
    public Dictionary<Guid, BrokerItemLocalWorkspace> BrokerItems { get; set; } = [];
    public Dictionary<Guid, List<string>> RecentSendArchivedAttachmentPaths { get; set; } = [];
    public Dictionary<Guid, GenerationLocalWorkspace> PaymentGenerations { get; set; } = [];
}

public sealed class BrokerItemLocalWorkspace
{
    public List<string> AttachmentPaths { get; set; } = [];
    public List<string> GeneratedAttachmentPaths { get; set; } = [];
}

public sealed class GenerationLocalWorkspace
{
    public string SourceWorkbookPath { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public Dictionary<Guid, string> OutputPathsByFileId { get; set; } = [];
}

public sealed class LocalWorkspaceStore
{
    private readonly AppDataPaths _paths;
    private readonly AtomicJsonFile _json;

    public LocalWorkspaceStore(AppDataPaths paths, FileLogger logger)
    {
        _paths = paths;
        _json = new AtomicJsonFile(logger);
    }

    public LocalWorkspaceState Load() =>
        _json.Load<LocalWorkspaceState>(_paths.LocalWorkspaceStateFile, out _) ?? new LocalWorkspaceState();

    public void Save(LocalWorkspaceState value) => _json.Save(_paths.LocalWorkspaceStateFile, value);

    public static LocalWorkspaceState FromLegacy(
        AppConfiguration configuration,
        CurrentSession? session,
        IEnumerable<SentEmailRecord> recentSends,
        IEnumerable<PaymentGenerationBatch> generations,
        Func<Guid, GeneratedPaymentFile, Guid> fileIdFactory)
    {
        var state = new LocalWorkspaceState
        {
            SignatureImagePath = configuration.SignatureImagePath,
            GeneralWorkbookPath = session?.GeneralWorkbookPath ?? string.Empty,
            GeneratedOutputDirectory = session?.GeneratedOutputDirectory ?? string.Empty
        };
        foreach (var item in session?.BrokerItems ?? [])
        {
            state.BrokerItems[item.BrokerId] = new BrokerItemLocalWorkspace
            {
                AttachmentPaths = [.. item.AttachmentPaths],
                GeneratedAttachmentPaths = [.. item.GeneratedAttachmentPaths]
            };
        }

        foreach (var send in recentSends)
            state.RecentSendArchivedAttachmentPaths[send.Id] = [.. send.ArchivedAttachmentPaths];

        foreach (var generation in generations)
        {
            var local = new GenerationLocalWorkspace
            {
                SourceWorkbookPath = generation.SourceWorkbookPath,
                OutputDirectory = generation.OutputDirectory
            };
            foreach (var file in generation.Files)
                local.OutputPathsByFileId[fileIdFactory(generation.Id, file)] = file.OutputPath;
            state.PaymentGenerations[generation.Id] = local;
        }

        return state;
    }
}
