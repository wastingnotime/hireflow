using Microsoft.EntityFrameworkCore;
using WastingNoTime.HireFlow.CompanyJobs.Api.Contracts;
using WastingNoTime.HireFlow.CompanyJobs.Data;
using WastingNoTime.HireFlow.CompanyJobs.Data.Entities;

namespace WastingNoTime.HireFlow.CompanyJobs.Api.Endpoints;

public static class CompanyJobsEndpoints
{
    public static IEndpointRouteBuilder MapCompanyJobsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/");

        // ----- Companies -----

        group.MapPost("/companies", async (CompanyCreateRequest req, CompanyJobsDbContext db) =>
        {
            var company = new Company
            {
                Name = req.Name,
                Domain = req.Domain
            };

            db.Companies.Add(company);
            await db.SaveChangesAsync();

            var response = new CompanyResponse(company.Id, company.Name, company.Domain);
            return Results.Created($"/companies/{company.Id}", response);
        });

        group.MapGet("/companies", async (CompanyJobsDbContext db) =>
        {
            var companies = await db.Companies
                .OrderBy(c => c.Id)
                .Select(c => new CompanyResponse(c.Id, c.Name, c.Domain))
                .ToListAsync();

            return Results.Ok(companies);
        });

        group.MapGet("/companies/{id:long}", async (long id, CompanyJobsDbContext db) =>
        {
            var company = await db.Companies
                .Where(c => c.Id == id)
                .Select(c => new CompanyResponse(c.Id, c.Name, c.Domain))
                .FirstOrDefaultAsync();

            return company is null ? Results.NotFound() : Results.Ok(company);
        });

        // ----- Jobs -----

        group.MapPost("/jobs", async (JobCreateRequest req, CompanyJobsDbContext db) =>
        {
            // ensure company exists
            var companyExists = await db.Companies.AnyAsync(c => c.Id == req.CompanyId);
            if (!companyExists)
                return Results.BadRequest(new { error = "Company does not exist." });

            var job = new Job
            {
                CompanyId = req.CompanyId,
                Title = req.Title,
                Status = "draft"
            };

            db.Jobs.Add(job);
            await db.SaveChangesAsync();

            var response = new JobResponse(job.Id, job.CompanyId, job.Title, job.Status);
            return Results.Created($"/jobs/{job.Id}", response);
        });

        group.MapGet("/jobs/{id:long}", async (long id, CompanyJobsDbContext db) =>
        {
            var job = await db.Jobs
                .Where(j => j.Id == id)
                .Select(j => new JobResponse(j.Id, j.CompanyId, j.Title, j.Status))
                .FirstOrDefaultAsync();

            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        // publish job: PATCH /jobs/{id}/publish
        group.MapPatch("/jobs/{id:long}/publish", async (long id, CompanyJobsDbContext db) =>
        {
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
            if (job is null)
                return Results.NotFound();

            if (job.Status == "published")
                return Results.Ok(new JobResponse(job.Id, job.CompanyId, job.Title, job.Status));

            job.Status = "published";
            await db.SaveChangesAsync();

            var response = new JobResponse(job.Id, job.CompanyId, job.Title, job.Status);
            return Results.Ok(response);
        });

        return app;
    }
}
