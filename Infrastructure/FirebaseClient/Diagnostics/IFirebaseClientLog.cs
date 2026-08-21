namespace ECS.CommissionsMailer.Infrastructure.FirebaseClient.Diagnostics;

public interface IFirebaseClientLog
{
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}

public sealed class NullFirebaseClientLog : IFirebaseClientLog
{
    public static NullFirebaseClientLog Instance { get; } = new();
    public void Info(string message) { }
    public void Warning(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
