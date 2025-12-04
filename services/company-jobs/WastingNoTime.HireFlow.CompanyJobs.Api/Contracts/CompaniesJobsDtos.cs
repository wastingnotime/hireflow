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


// ----- Jobs -----

public sealed record JobCreateRequest(
    long CompanyId,
    string Title
    // later: description, requirements, etc.
);

public sealed record JobResponse(
    long Id,
    long CompanyId,
    string Title,
    string Status
);
