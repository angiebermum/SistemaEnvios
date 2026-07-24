namespace ECS.CommissionsMailer.Models;

public enum SendStatus
{
    Pending,
    Ready,
    Sending,
    Sent,
    Error,
    ReviewRequired
}
