namespace WastingNoTime.HireFlow.Applications.Messaging;

public interface INotificationsCommandBus : IAsyncDisposable
{
    Task PublishSendEmailAsync(
        string to,
        string subject,
        string body,
        string applicationId,
        string interviewId,
        long jobId,
        CancellationToken ct = default);
}
