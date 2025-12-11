using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Driver;
using RabbitMQ.Client;
using WastingNoTime.HireFlow.Candidates.Api.HealthCheck;

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

builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields = HttpLoggingFields.All;
    logging.RequestBodyLogLimit = 0; // we don't log resume bodies
    logging.ResponseBodyLogLimit = 4096;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseHttpLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

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

app.Run();
