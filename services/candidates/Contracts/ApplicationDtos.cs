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

// Request used to schedule an interview
public sealed record ScheduleInterviewRequest(
    DateTime ScheduledAtUtc,
    int DurationMinutes,
    string? Location
);

// Response for interview views
public sealed record InterviewResponse(
    string Id,
    string ApplicationId,
    long JobId,
    string CandidateName,
    string CandidateEmail,
    DateTime ScheduledAtUtc,
    int DurationMinutes,
    string? Location,
    string Status,
    DateTime CreatedAtUtc
);
