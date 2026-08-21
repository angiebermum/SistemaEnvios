namespace ECS.CommissionsMailer.Infrastructure.Firestore;

/// <summary>
/// Canonical timestamp precision supported by Cloud Firestore document fields.
/// Firestore truncates precision finer than one microsecond when values are stored.
/// This project is intentionally free of Google Cloud dependencies so both the
/// administrative tools and the authenticated desktop REST client use one policy.
/// </summary>
public static class FirestoreTimestampPrecision
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    public static DateTime Normalize(DateTime value)
    {
        var utc = value.ToUniversalTime();
        return new DateTime(TruncateSubMicrosecondTicks(utc.Ticks), DateTimeKind.Utc);
    }

    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(TruncateSubMicrosecondTicks(utc.Ticks), TimeSpan.Zero);
    }

    private static long TruncateSubMicrosecondTicks(long ticks) =>
        ticks - ticks % TicksPerMicrosecond;
}
