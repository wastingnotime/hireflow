namespace WastingNoTime.HireFlow.Candidates.Api.Contracts;

public sealed record ApplicationResponse(
    string Id,
    long JobId,
    string CandidateName,
    string CandidateEmail,
    string Status,
    string ResumePath,
    DateTime CreatedAtUtc,
    int? ScreeningScore,
    DateTime? ScreenedAtUtc,
    string? ScreeningNotes
);