using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public sealed class SessionService
{
    private readonly AppDataPaths _paths;
    private readonly AtomicJsonFile _json;

    public SessionService(AppDataPaths paths, FileLogger logger)
    {
        _paths = paths;
        _json = new AtomicJsonFile(logger);
    }

    public List<string> Warnings { get; } = [];

    public CurrentSession? LoadCurrent()
    {
        var session = _json.Load<CurrentSession>(_paths.CurrentSessionFile, out var warning);
        AddWarning(warning);
        if (session is not null)
        {
            session.BrokerItems ??= [];
            foreach (var item in session.BrokerItems)
            {
                item.PrimaryRecipients ??= [];
                item.AttachmentPaths ??= [];
                item.RefreshComputedProperties();
            }
        }

        return session;
    }

    public void SaveCurrent(CurrentSession session)
    {
        session.SavedAt = DateTimeOffset.Now;
        _json.Save(_paths.CurrentSessionFile, session);
    }

    public List<SentEmailRecord> LoadRecentSends()
    {
        var records = _json.Load<List<SentEmailRecord>>(_paths.RecentSendsFile, out var warning) ?? [];
        AddWarning(warning);
        return records.OrderByDescending(record => record.SentAt).ToList();
    }

    public void SaveRecentSends(IEnumerable<SentEmailRecord> records) =>
        _json.Save(_paths.RecentSendsFile, records.OrderByDescending(record => record.SentAt).ToList());

    private void AddWarning(string? warning)
    {
        if (!string.IsNullOrWhiteSpace(warning))
        {
            Warnings.Add(warning);
        }
    }
}
