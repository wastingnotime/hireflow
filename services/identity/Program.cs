using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Trace;

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


if (app.Environment.ApplicationName?.Contains("identity", StringComparison.OrdinalIgnoreCase) == true)
{
    app.MapPost("/token/dev", async (HttpRequest req, IConfiguration cfg) =>
    {
        using var sr = new StreamReader(req.Body);
        var body = await sr.ReadToEndAsync();
        var dto = System.Text.Json.JsonSerializer.Deserialize<DevTokenReq>(body)!;

        var key = cfg["JwtSigningKey"] ?? "dev_hmac_super_secret_change_me";
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
            Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256
        );
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: "hireflow-dev",
            audience: "hireflow-dev",
            claims: new[]
            {
                new System.Security.Claims.Claim("email", dto.email),
                new System.Security.Claims.Claim("tenant_id", dto.tenantId ?? Guid.Empty.ToString()),
                new System.Security.Claims.Claim("role", dto.role ?? "Recruiter")
            },
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: credentials
        );
        return Results.Ok(new { access_token = handler.WriteToken(token) });
    });
}

app.Run();

record DevTokenReq(string email, string? tenantId, string? role);