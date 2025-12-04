namespace WastingNoTime.HireFlow.CompanyJobs.Data.Entities;

public sealed class Job
{
    public long Id { get; set; }
    public long CompanyId { get; set; }
    public string Title { get; set; } = null!;
    public string Status { get; set; } = "draft"; // draft | published | closed TODO: enum

    public long? RecruiterId { get; set; }        // optional in M1

    public Company? Company { get; set; }
    public Recruiter? Recruiter { get; set; }
}