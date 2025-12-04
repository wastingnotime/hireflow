using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WastingNoTime.HireFlow.CompanyJobs.Data;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration(cfg => cfg.AddEnvironmentVariables())
    .ConfigureServices((ctx, services) =>
    {
        var cs = ctx.Configuration["COMPANYJOBS_CONNECTION_STRING"]
                 ?? throw new InvalidOperationException("COMPANYJOBS_CONNECTION_STRING missing");

        services.AddDbContext<CompanyJobsDbContext>(opt =>
            opt.UseSqlServer(cs, sql =>
            {
                // keep EF history inside the service-owned schema
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "companyjobs");
            }));
    })
    .Build();

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<CompanyJobsDbContext>();
await db.Database.MigrateAsync();   // idempotent: applies only pending migrations