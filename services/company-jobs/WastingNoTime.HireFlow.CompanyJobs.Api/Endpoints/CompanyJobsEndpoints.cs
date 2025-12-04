using WastingNoTime.HireFlow.CompanyJobs.Api.Contracts;
using WastingNoTime.HireFlow.CompanyJobs.Data;
using WastingNoTime.HireFlow.CompanyJobs.Data.Entities;


namespace WastingNoTime.HireFlow.CompanyJobs.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

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

        // ----- Recruiters -----

        group.MapPost("/companies/{companyId:long}/recruiters",
            async (long companyId, RecruiterCreateRequest req, CompanyJobsDbContext db) =>
        {
            var companyExists = await db.Companies.AnyAsync(c => c.Id == companyId);
            if (!companyExists)
                return Results.NotFound(new { error = "Company not found." });

            var recruiter = new Recruiter
            {
                CompanyId = companyId,
                Name = req.Name,
                Email = req.Email
            };

            db.Recruiters.Add(recruiter);
            await db.SaveChangesAsync();

            var response = new RecruiterResponse(recruiter.Id, recruiter.CompanyId, recruiter.Name, recruiter.Email);
            return Results.Created($"/recruiters/{recruiter.Id}", response);
        });

        group.MapGet("/companies/{companyId:long}/recruiters",
            async (long companyId, CompanyJobsDbContext db) =>
        {
            var recruiters = await db.Recruiters
                .Where(r => r.CompanyId == companyId)
                .OrderBy(r => r.Id)
                .Select(r => new RecruiterResponse(r.Id, r.CompanyId, r.Name, r.Email))
                .ToListAsync();

            return Results.Ok(recruiters);
        });

        group.MapGet("/recruiters/{id:long}", async (long id, CompanyJobsDbContext db) =>
        {
            var recruiter = await db.Recruiters
                .Where(r => r.Id == id)
                .Select(r => new RecruiterResponse(r.Id, r.CompanyId, r.Name, r.Email))
                .FirstOrDefaultAsync();

            return recruiter is null ? Results.NotFound() : Results.Ok(recruiter);
        });

        // ----- Jobs -----

        group.MapPost("/jobs", async (JobCreateRequest req, CompanyJobsDbContext db) =>
        {
            // ensure company exists
            var companyExists = await db.Companies.AnyAsync(c => c.Id == req.CompanyId);
            if (!companyExists)
                return Results.BadRequest(new { error = "Company does not exist." });

            long? recruiterId = req.RecruiterId;

            if (recruiterId.HasValue)
            {
                var recruiter = await db.Recruiters
                    .Where(r => r.Id == recruiterId.Value)
                    .Select(r => new { r.Id, r.CompanyId })
                    .FirstOrDefaultAsync();

                if (recruiter is null)
                    return Results.BadRequest(new { error = "Recruiter does not exist." });

                if (recruiter.CompanyId != req.CompanyId)
                    return Results.BadRequest(new { error = "Recruiter does not belong to the specified company." });
            }

            var job = new Job
            {
                CompanyId = req.CompanyId,
                Title = req.Title,
                Status = "draft",
                RecruiterId = recruiterId
            };

            db.Jobs.Add(job);
            await db.SaveChangesAsync();

            var response = new JobResponse(job.Id, job.CompanyId, job.Title, job.Status, job.RecruiterId);
            return Results.Created($"/jobs/{job.Id}", response);
        });

        group.MapGet("/jobs/{id:long}", async (long id, CompanyJobsDbContext db) =>
        {
            var job = await db.Jobs
                .Where(j => j.Id == id)
                .Select(j => new JobResponse(j.Id, j.CompanyId, j.Title, j.Status, j.RecruiterId))
                .FirstOrDefaultAsync();

            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        group.MapPatch("/jobs/{id:long}/publish", async (long id, CompanyJobsDbContext db) =>
        {
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
            if (job is null)
                return Results.NotFound();

            if (job.Status == "published")
                return Results.Ok(new JobResponse(job.Id, job.CompanyId, job.Title, job.Status, job.RecruiterId));

            job.Status = "published";
            await db.SaveChangesAsync();

            var response = new JobResponse(job.Id, job.CompanyId, job.Title, job.Status, job.RecruiterId);
            return Results.Ok(response);
        });

        return app;
    }
}
