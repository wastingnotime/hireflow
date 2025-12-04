namespace WastingNoTime.HireFlow.CompanyJobs.Api.Contracts;

// ----- Companies -----

public sealed record CompanyCreateRequest(
    string Name,
    string? Domain
);

public sealed record CompanyResponse(
    long Id,
    string Name,
    string? Domain
);

// ----- Recruiters -----

public sealed record RecruiterCreateRequest(
    string Name,
    string Email
);

public sealed record RecruiterResponse(
    long Id,
    long CompanyId,
    string Name,
    string Email
);

// ----- Jobs -----

public sealed record JobCreateRequest(
    long CompanyId,
    string Title,
    long? RecruiterId    // optional in M1
);

public sealed record JobResponse(
    long Id,
    long CompanyId,
    string Title,
    string Status,
    long? RecruiterId
);