using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Trace;
using Polly.CircuitBreaker;
using Polly.Timeout;
using WastingNoTime.HireFlow.Gateway.Middlewares;
using Yarp.ReverseProxy.Forwarder;

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
                //enrich spans with useful data
                o.RecordException = true;
            })
            .AddHttpClientInstrumentation(o => { o.RecordException = true; })
            .AddOtlpExporter();
    });

builder.Services.AddSingleton<IForwarderHttpClientFactory, ResilientForwarderHttpClientFactory>();

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddTransient<TraceLoggingMiddleware>();

var app = builder.Build();

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
});

// Readiness – can run all checks (for now it's same as self)
app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = _ => true
});

app.MapReverseProxy(proxyPipeline =>
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
});


app.Run();