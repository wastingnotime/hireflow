namespace WastingNoTime.HireFlow.Applications.Contracts;

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
