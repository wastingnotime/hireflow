using Microsoft.EntityFrameworkCore;
using WastingNoTime.HireFlow.CompanyJobs.Data.Entities;

namespace WastingNoTime.HireFlow.CompanyJobs.Data;

public sealed class CompanyJobsDbContext : DbContext
{
    public CompanyJobsDbContext(DbContextOptions<CompanyJobsDbContext> options) : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Recruiter> Recruiters => Set<Recruiter>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("companyjobs");

        b.Entity<Company>(e =>
        {
            e.ToTable("companies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Domain).HasMaxLength(200);
        });

        b.Entity<Recruiter>(e =>
        {
            e.ToTable("recruiters");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Email).HasMaxLength(320).IsRequired(); // simple limit

            e.HasOne(x => x.Company)
             .WithMany()
             .HasForeignKey(x => x.CompanyId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.CompanyId, x.Email }).IsUnique();
        });

        b.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();

            e.HasIndex(x => new { x.CompanyId, x.Status });

            e.HasOne(x => x.Company)
             .WithMany()
             .HasForeignKey(x => x.CompanyId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Recruiter)
             .WithMany(r => r.Jobs)
             .HasForeignKey(x => x.RecruiterId)
             .OnDelete(DeleteBehavior.SetNull); // recruiter can be removed without killing job
        });
    }
}
