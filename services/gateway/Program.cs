using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Polly.CircuitBreaker;
using Polly.Timeout;
using WastingNoTime.HireFlow.Gateway.Middlewares;
using Yarp.ReverseProxy.Forwarder;

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

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("gateway-access", p =>
        p.RequireAuthenticatedUser()
            .RequireRole("recruiter", "service"));
});


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
                //enrich spans with useful data
                o.RecordException = true;
            })
            .AddHttpClientInstrumentation(o => { o.RecordException = true; })
            .AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m
            .AddAspNetCoreInstrumentation()
            .AddPrometheusExporter();
    });

builder.Services.AddSingleton<IForwarderHttpClientFactory, ResilientForwarderHttpClientFactory>();

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddTransient<TraceLoggingMiddleware>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<TraceLoggingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


// Liveness – just says "process is running"
app.MapHealthChecks("/healthz", new HealthCheckOptions
    {
        Predicate = _ => false // don't run registered checks, just 200 if app is alive
    })
    .AllowAnonymous();

// Readiness – can run all checks (for now it's same as self)
app.MapHealthChecks("/ready", new HealthCheckOptions
    {
        Predicate = _ => true
    })
    .AllowAnonymous();

app
    .MapReverseProxy(proxyPipeline =>
    {
        proxyPipeline.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.TryAdd("x-hireflow-gateway", "yarp");
            ctx.Response.Headers["x-hireflow-gateway-pod"] =
                Environment.GetEnvironmentVariable("HOSTNAME") ?? "unknown";

            await next();

            // only map forwarder errors (exceptions / connection issues / cancellations)
            var err = ctx.Features.Get<IForwarderErrorFeature>();
            if (err is null || err.Error == ForwarderError.None || ctx.Response.HasStarted)
                return;

            if (err.Exception is BrokenCircuitException)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.Headers["x-hireflow-failure"] = "circuit-open";
                await ctx.Response.WriteAsync("service unavailable (circuit open)");
                return;
            }

            if (err.Exception is TimeoutRejectedException ||
                err.Exception is TaskCanceledException ||
                err.Exception is OperationCanceledException)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                ctx.Response.Headers["x-hireflow-failure"] = "timeout";
                await ctx.Response.WriteAsync("gateway timeout");
                return;
            }

            ctx.Response.Clear();
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            ctx.Response.Headers["x-hireflow-failure"] = "downstream";
            ctx.Response.Headers["x-hireflow-failure-detail"] = err.Error.ToString();
            await ctx.Response.WriteAsync("bad gateway");
        });
    })
    .RequireAuthorization("gateway-access");

app
    .MapPrometheusScrapingEndpoint("/metrics")
    .AllowAnonymous();

app.Run();