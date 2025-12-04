namespace WastingNoTime.HireFlow.CompanyJobs.Data.Entities;

public sealed class Job
{
    public long Id { get; set; }
    public long CompanyId { get; set; }
    public string Title { get; set; } = null!;
    public string Status { get; set; } = "draft"; // draft | published | closed

    // nav (optional)
    public Company? Company { get; set; }
}