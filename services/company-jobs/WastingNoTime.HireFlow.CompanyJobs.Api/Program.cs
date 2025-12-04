using Microsoft.AspNetCore.HttpLogging;
using Microsoft.EntityFrameworkCore;
using WastingNoTime.HireFlow.CompanyJobs.Api.Endpoints;
using WastingNoTime.HireFlow.CompanyJobs.Data;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// Read from Environment first
var cs =
    Environment.GetEnvironmentVariable("COMPANYJOBS_CONNECTION_STRING") ??
    configuration["COMPANYJOBS_CONNECTION_STRING"] ??
    configuration["CONNECTION_STRING"] ??
    configuration.GetConnectionString("CompanyJobs") ??
    throw new InvalidOperationException("Missing DB connection string for CompanyJobs");



builder.Services.AddDbContext<CompanyJobsDbContext>(opt =>
    opt.UseSqlServer(cs, sql =>
    {
        sql.MigrationsHistoryTable("__EFMigrationsHistory", "companyjobs");
    }));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields = HttpLoggingFields.All;
    logging.RequestBodyLogLimit = 4096;
    logging.ResponseBodyLogLimit = 4096;
});


var app = builder.Build();

app.UseHttpLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", svc = app.Environment.ApplicationName }));

app.MapCompanyJobsEndpoints();

app.Run();








