using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WastingNoTime.HireFlow.Candidates.Api.Models;

public sealed class Application
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    public long JobId { get; set; }

    public string CandidateName { get; set; } = null!;
    public string CandidateEmail { get; set; } = null!;

    public string ResumePath { get; set; } = null!;   // local disk path, or later: blob URL
    public string Status { get; set; } = "received";  // received | screened | interview | rejected | hired

    public DateTime CreatedAtUtc { get; set; }
}