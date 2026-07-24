using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

internal static class EmailBatchSelection
{
    public static List<BrokerSendItem> GetSelected(IEnumerable<BrokerSendItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Where(item => item.IsSelected).ToList();
    }
}
