using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using WastingNoTime.HireFlow.Identity.Middlewares;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy());

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
            .AddHttpClientInstrumentation(o=>o.RecordException = true)
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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<TraceLoggingMiddleware>();

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


app.MapPost("/token", (TokenRequest req, IConfiguration cfg, ILoggerFactory lf) =>
{
    var log = lf.CreateLogger("identity.token");

    // TODO: replace this with DB-backed client store later
    if (req.ClientId != "recruiter-demo" || req.ClientSecret != "demo")
    {
        log.LogWarning("token issuance failed client_id={client_id} reason=invalid_credentials", req.ClientId);
        return Results.Unauthorized();
    }

    var issuer = cfg["JWT_ISSUER"] ?? "hireflow-identity";
    var audience = cfg["JWT_AUDIENCE"] ?? "hireflow-api";
    var expiresMinutes = int.TryParse(cfg["JWT_EXPIRES_MINUTES"], out var m) ? m : 60;

    var signingKey = cfg["JWT_SIGNING_KEY"];
    if (string.IsNullOrWhiteSpace(signingKey))
    {
        log.LogError("missing JWT_SIGNING_KEY");
        return Results.Problem("Server misconfigured", statusCode: 500);
    }

    var now = DateTimeOffset.UtcNow;
    var expires = now.AddMinutes(expiresMinutes);

    var claims = new List<Claim>
    {
        new(JwtRegisteredClaimNames.Sub, req.ClientId),
        new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),

        // authz
        new(ClaimTypes.Role, "recruiter"),
        new("scope", "companies:read companies:write jobs:read jobs:write applications:read applications:write")
    };

    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        issuer: issuer,
        audience: audience,
        claims: claims,
        notBefore: now.UtcDateTime,
        expires: expires.UtcDateTime,
        signingCredentials: creds);

    var jwt = new JwtSecurityTokenHandler().WriteToken(token);

    log.LogInformation("token issued client_id={client_id} exp={exp}", req.ClientId, expires);

    return Results.Ok(new
    {
        access_token = jwt,
        token_type = "Bearer",
        expires_in = (int)(expires - now).TotalSeconds
    });
});


app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();

record TokenRequest(string ClientId, string ClientSecret);