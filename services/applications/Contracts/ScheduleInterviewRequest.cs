namespace WastingNoTime.HireFlow.Applications.Contracts;

// Request used to schedule an interview
public sealed record ScheduleInterviewRequest(
    DateTime ScheduledAtUtc,
    int DurationMinutes,
    string? Location
);
