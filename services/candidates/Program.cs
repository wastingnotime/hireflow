using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using WastingNoTime.HireFlow.Candidates.Api.HealthCheck;
using WastingNoTime.Hireflow.Candidates.Api.Middlewares;

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
    options.AddPolicy("candidates:read", p => p.Requirements.Add(new ScopeRequirement("candidates:read")));
    options.AddPolicy("candidates:write", p => p.Requirements.Add(new ScopeRequirement("candidates:write")));
});

builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, ScopeHandler>();


var mongoConnectionString =
    Environment.GetEnvironmentVariable("CANDIDATES_MONGO_CONNECTION_STRING") ??
    builder.Configuration["CANDIDATES_MONGO_CONNECTION_STRING"] ??
    builder.Configuration.GetConnectionString("Mongo") ??
    throw new InvalidOperationException("Missing Mongo connection string for Candidates");

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnectionString));

var rabbitConnectionString =
    Environment.GetEnvironmentVariable("RabbitMQ") ??
    builder.Configuration.GetConnectionString("RabbitMQ") ??
    builder.Configuration["RABBITMQ_CONNECTION_STRING"] ??
    throw new InvalidOperationException("Missing RabbitMQ connection string for Candidates");

builder.Services.AddSingleton(new ConnectionFactory
{
    Uri = new Uri(rabbitConnectionString)
});

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy())
    .AddCheck<MongoHealthCheck>("mongo")
    .AddCheck<RabbitMqHealthCheck>("rabbitmq");

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
                    return p != "/healthz" && p != "/ready";
                };
                o.RecordException = true;
            })
            .AddHttpClientInstrumentation(o => o.RecordException = true)
            .AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m
            .AddAspNetCoreInstrumentation()
            .AddPrometheusExporter();
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