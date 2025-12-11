using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WastingNoTime.HireFlow.Applications.Models;

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
    
    // ---- Screening fields (for M1 + rule engine later) ----
    public int? ScreeningScore { get; set; }
    public DateTime? ScreenedAtUtc { get; set; }
    public string? ScreeningNotes { get; set; }
}
