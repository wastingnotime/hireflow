using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Driver;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using WastingNoTime.HireFlow.Candidates.Api.HealthCheck;
using WastingNoTime.Hireflow.Candidates.Api.Middlewares;

var builder = WebApplication.CreateBuilder(args);

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