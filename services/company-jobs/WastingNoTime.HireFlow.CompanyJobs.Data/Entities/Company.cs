namespace WastingNoTime.HireFlow.CompanyJobs.Data.Entities;

public sealed class Company
{
    public long Id { get; set; }                // bigint IDENTITY (by convention)
    public string Name { get; set; } = null!;
    public string? Domain { get; set; }         // optional DNS/email domain
}