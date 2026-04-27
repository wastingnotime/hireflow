using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using WastingNoTime.HireFlow.CompanyJobs.Api.Endpoints;
using WastingNoTime.HireFlow.CompanyJobs.Api.Middlewares;
using WastingNoTime.HireFlow.CompanyJobs.Data;

var builder = WebApplication.CreateBuilder(args);

var issuer = builder.Configuration["JWT_ISSUER"] ?? "hireflow-identity";
var audience = builder.Configuration["JWT_AUDIENCE"] ?? "hireflow-api";
var signingKey = builder.Configuration["JWT_SIGNING_KEY"];

if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("JWT_SIGNING_KEY must be at least 32 bytes for HS256.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.RequireHttpsMetadata = false; // ok for minikube/dev
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,

            ValidateAudience = true,
            ValidAudience = audience,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Simple role policy example
    options.AddPolicy("recruiter", p => p.RequireRole("recruiter"));

    // Scope policies (space-separated "scope" claim)
    options.AddPolicy("companies:read", p => p.Requirements.Add(new ScopeRequirement("companies:read")));
    options.AddPolicy("companies:write", p => p.Requirements.Add(new ScopeRequirement("companies:write")));
    options.AddPolicy("jobs:write", p => p.Requirements.Add(new ScopeRequirement("jobs:write")));
});

builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, ScopeHandler>();


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

app.UseAuthentication();
app.UseAuthorization();

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




// ---- Scope authorization (supports "scope": "a b c") ----
public sealed record ScopeRequirement(string Scope) : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement;

public sealed class ScopeHandler : Microsoft.AspNetCore.Authorization.AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context,
        ScopeRequirement requirement)
    {
        var scopeClaim = context.User.FindFirst("scope")?.Value;
        if (string.IsNullOrWhiteSpace(scopeClaim))
            return Task.CompletedTask;

        var scopes = scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (scopes.Contains(requirement.Scope))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}