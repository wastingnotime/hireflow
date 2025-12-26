using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using WastingNoTime.HireFlow.CompanyJobs.Api.Endpoints;
using WastingNoTime.HireFlow.CompanyJobs.Api.Middlewares;
using WastingNoTime.HireFlow.CompanyJobs.Data;

var builder = WebApplication.CreateBuilder(args);

var dbConnectionString =
    Environment.GetEnvironmentVariable("COMPANYJOBS_CONNECTION_STRING") ??
    builder.Configuration["COMPANYJOBS_CONNECTION_STRING"] ??
    builder.Configuration["CONNECTION_STRING"] ??
    builder.Configuration.GetConnectionString("CompanyJobs") ??
    throw new InvalidOperationException("Missing DB connection string for CompanyJobs");

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy())
    .AddSqlServer(
        connectionString: dbConnectionString,
        name: "sql",
        failureStatus: HealthStatus.Unhealthy,
        timeout: TimeSpan.FromSeconds(3));

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation(o =>
            {
                // lets ignore noise
                o.Filter = ctx =>
                {
                    var p = ctx.Request.Path.Value ?? "";
                    return !p.StartsWith("/healthz") && !p.StartsWith("/ready");
                };
                o.RecordException = true;
            })
            .AddHttpClientInstrumentation(o=>  o.RecordException = true)
            .AddEntityFrameworkCoreInstrumentation()
            .AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m
            .AddAspNetCoreInstrumentation()
            .AddPrometheusExporter();
    });

builder.Services.AddDbContext<CompanyJobsDbContext>(opt =>
    opt.UseSqlServer(dbConnectionString, sql =>
    {
        sql.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorNumbersToAdd: null);

        sql.CommandTimeout(30);

        sql.MigrationsHistoryTable("__EFMigrationsHistory", "companyjobs");
    }));

builder.Services.ConfigureHttpJsonOptions(opt =>
{
    opt.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddTransient<TraceLoggingMiddleware>();


var app = builder.Build();


if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<TraceLoggingMiddleware>();

app.UseRouting();

// Liveness – just says "process is running"
app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    Predicate = _ => false // don't run registered checks, just 200 if app is alive
});

// Readiness – can run all checks (for now it's same as self)
app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = _ => true
});


app.MapCompanyJobsEndpoints();

app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();