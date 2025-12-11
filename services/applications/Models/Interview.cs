using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WastingNoTime.HireFlow.Applications.Models;

public sealed class Interview
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    public string ApplicationId { get; set; } = null!;

    public long JobId { get; set; }
    public string CandidateName { get; set; } = null!;
    public string CandidateEmail { get; set; } = null!;

    public DateTime ScheduledAtUtc { get; set; }
    public int DurationMinutes { get; set; }

    public string? Location { get; set; }      // e.g. "Google Meet", "Office", "Zoom"
    public string Status { get; set; } = "scheduled"; // scheduled | completed | canceled

    public DateTime CreatedAtUtc { get; set; }
}
