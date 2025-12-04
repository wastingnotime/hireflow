using Microsoft.EntityFrameworkCore;
using WastingNoTime.HireFlow.CompanyJobs.Data.Entities;

namespace WastingNoTime.HireFlow.CompanyJobs.Data;

public sealed class CompanyJobsDbContext : DbContext
{
    public CompanyJobsDbContext(DbContextOptions<CompanyJobsDbContext> options) : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Job> Jobs => Set<Job>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // service-owned schema
        b.HasDefaultSchema("companyjobs");

        b.Entity<Company>(e =>
        {
            e.ToTable("companies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();              // IDENTITY
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Domain).HasMaxLength(200);
        });

        b.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();  // draft/published/closed

            e.HasIndex(x => new { x.CompanyId, x.Status });

            e.HasOne(x => x.Company)
                .WithMany()
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
