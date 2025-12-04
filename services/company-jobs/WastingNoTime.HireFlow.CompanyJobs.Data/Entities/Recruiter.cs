namespace WastingNoTime.HireFlow.CompanyJobs.Data.Entities;

public sealed class Recruiter
{
  public long Id { get; set; }
  public long CompanyId { get; set; }
  public string Name { get; set; } = null!;
  public string Email { get; set; } = null!;

  public Company? Company { get; set; }
  public ICollection<Job> Jobs { get; set; } = new List<Job>();
}
