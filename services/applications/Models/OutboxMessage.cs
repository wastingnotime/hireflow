using MongoDB.Bson.Serialization.Attributes;

namespace WastingNoTime.HireFlow.Applications.Models;

public sealed class OutboxMessage
{
    [BsonId]
    public string Id { get; set; } = default!; // Guid string

    public DateTime OccurredAtUtc { get; set; }

    public string Type { get; set; } = default!; // e.g. "SendEmail.InterviewScheduled"
    public string PayloadJson { get; set; } = default!;

    public string Status { get; set; } = "Pending"; // Pending | Processing | Processed | Failed
    public int RetryCount { get; set; } = 0;

    public DateTime? NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessingStartedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }

    public string? LastError { get; set; }
    public string? LockedBy { get; set; } // worker/pod id
}
