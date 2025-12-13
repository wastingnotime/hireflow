using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Driver;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using WastingNoTime.HireFlow.Applications.Contracts;
using WastingNoTime.HireFlow.Applications.Data;
using WastingNoTime.HireFlow.Applications.HealthCheck;
using WastingNoTime.HireFlow.Applications.Messaging;
using WastingNoTime.HireFlow.Applications.Models;
using WastingNoTime.HireFlow.Applications.Outbox;

var builder = WebApplication.CreateBuilder(args);

var mongoConnectionString =
    Environment.GetEnvironmentVariable("APPLICATIONS_MONGO_CONNECTION_STRING") ??
    builder.Configuration["APPLICATIONS_MONGO_CONNECTION_STRING"] ??
    builder.Configuration.GetConnectionString("ApplicationsMongo") ??
    throw new InvalidOperationException("Missing Mongo connection string for Applications");

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnectionString));
builder.Services.AddSingleton<ApplicationsDb>();

var rabbitConnectionString =
    Environment.GetEnvironmentVariable("RabbitMQ") ??
    builder.Configuration.GetConnectionString("RabbitMQ") ??
    builder.Configuration["RABBITMQ_CONNECTION_STRING"] ??
    throw new InvalidOperationException("Missing RabbitMQ connection string for Applications");

builder.Services.AddSingleton(_ => new ConnectionFactory
{
    Uri = new Uri(rabbitConnectionString)
});
builder.Services.AddSingleton<INotificationsCommandBus>(_ =>
    new RabbitMqNotificationsCommandBus(rabbitConnectionString));
builder.Services.AddHostedService<ApplicationsOutboxDispatcher>();

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy())
    .AddCheck<MongoHealthCheck>("mongo")
    .AddCheck<RabbitMqHealthCheck>("rabbitmq");

//todo: handle absence -> argmentnull exception
// var otlpEndpoint =
//     Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") ??
//     builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ??
//     "http://jaeger-collector.observability.svc.cluster.local:4318";

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
            })
            .AddHttpClientInstrumentation()
            .AddOtlpExporter();
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();

// ---------- Endpoints ----------

// kept only for validation purposes
var tracer = TracerProvider.Default.GetTracer("applications.manual");

app.MapGet("/applications/trace-ping", () =>
{
    using var span = tracer.StartActiveSpan("applications.trace-ping");
    span.SetAttribute("demo", true);
    return Results.Ok(new { ok = true });
});



// POST /applications : candidate applies with resume
app.MapPost("/applications", async (
    HttpRequest request,
    IWebHostEnvironment env,
    ApplicationsDb db,
    CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Expected multipart/form-data" });

    var form = await request.ReadFormAsync(ct);

    var jobIdRaw = form["jobId"].FirstOrDefault();
    var name = form["name"].FirstOrDefault();
    var email = form["email"].FirstOrDefault();
    var file = form.Files["resume"];

    if (string.IsNullOrWhiteSpace(jobIdRaw) ||
        string.IsNullOrWhiteSpace(name) ||
        string.IsNullOrWhiteSpace(email) ||
        file is null || file.Length == 0)
    {
        return Results.BadRequest(new
        {
            error = "Missing required fields. Expect jobId, name, email, resume"
        });
    }

    if (!long.TryParse(jobIdRaw, out var jobId))
    {
        return Results.BadRequest(new { error = "Invalid jobId" });
    }

    // Store resume on disk (M1 only; later: S3/Blob)
    var uploadsRoot = Path.Combine(env.ContentRootPath, "uploads");
    Directory.CreateDirectory(uploadsRoot);

    var safeFileName = Path.GetFileName(file.FileName);
    var storedFileName = $"{Guid.NewGuid()}_{safeFileName}";
    var storedPath = Path.Combine(uploadsRoot, storedFileName);

    await using (var stream = File.Create(storedPath))
    {
        await file.CopyToAsync(stream, ct);
    }

    var now = DateTime.UtcNow;

    var appDoc = new Application
    {
        JobId = jobId,
        CandidateName = name,
        CandidateEmail = email,
        ResumePath = storedPath,
        Status = "received",
        CreatedAtUtc = now
    };

    await db.Applications.InsertOneAsync(appDoc, cancellationToken: ct);

    var response = new ApplicationResponse(
        appDoc.Id,
        appDoc.JobId,
        appDoc.CandidateName,
        appDoc.CandidateEmail,
        appDoc.Status,
        appDoc.ResumePath,
        appDoc.CreatedAtUtc,
        appDoc.ScreeningScore,
        appDoc.ScreenedAtUtc,
        appDoc.ScreeningNotes
    );

    return Results.Created($"/applications/{appDoc.Id}", response);
});

// GET /applications/{id} : simple fetch for debugging
app.MapGet("/applications/{id}", async (string id, ApplicationsDb db, CancellationToken ct) =>
{
    var filter = Builders<Application>.Filter.Eq(x => x.Id, id);
    var appDoc = await db.Applications.Find(filter).FirstOrDefaultAsync(ct);

    if (appDoc is null)
        return Results.NotFound();

    var response = new ApplicationResponse(
        appDoc.Id,
        appDoc.JobId,
        appDoc.CandidateName,
        appDoc.CandidateEmail,
        appDoc.Status,
        appDoc.ResumePath,
        appDoc.CreatedAtUtc,
        appDoc.ScreeningScore,
        appDoc.ScreenedAtUtc,
        appDoc.ScreeningNotes
    );

    return Results.Ok(response);
});

// POST /applications/{id}/screen : simple screening step (M1)
app.MapPost("/applications/{id}/screen", async (
    string id,
    ApplicationsDb db,
    CancellationToken ct) =>
{
    var filter = Builders<Application>.Filter.Eq(x => x.Id, id);
    var appDoc = await db.Applications.Find(filter).FirstOrDefaultAsync(ct);

    if (appDoc is null)
        return Results.NotFound(new { error = "Application not found." });

    // --- Simple heuristic for now ---
    // This is deliberately dumb and deterministic:
    // - base score 50
    // - +10 if email contains "senior"
    // - +10 if name length > 10
    var score = 50;

    if (appDoc.CandidateEmail.Contains("senior", StringComparison.OrdinalIgnoreCase))
        score += 10;

    if (!string.IsNullOrWhiteSpace(appDoc.CandidateName) &&
        appDoc.CandidateName.Length > 10)
        score += 10;

    // Cap between 0 and 100
    score = Math.Clamp(score, 0, 100);

    appDoc.ScreeningScore = score;
    appDoc.ScreenedAtUtc = DateTime.UtcNow;
    appDoc.ScreeningNotes = "Simple heuristic screening (M1 stub)";
    appDoc.Status = "screened";

    await db.Applications.ReplaceOneAsync(
        Builders<Application>.Filter.Eq(x => x.Id, appDoc.Id),
        appDoc,
        cancellationToken: ct
    );

    var response = new ApplicationResponse(
        appDoc.Id,
        appDoc.JobId,
        appDoc.CandidateName,
        appDoc.CandidateEmail,
        appDoc.Status,
        appDoc.ResumePath,
        appDoc.CreatedAtUtc,
        appDoc.ScreeningScore,
        appDoc.ScreenedAtUtc,
        appDoc.ScreeningNotes
    );

    return Results.Ok(response);
});


// POST /applications/{id}/interviews : schedule interview + move application to "interview"
app.MapPost("/applications/{id}/interviews", async (
    string id,
    ScheduleInterviewRequest req,
    ApplicationsDb db,
    IMongoClient mongoClient,
    CancellationToken ct) =>
{
    // load application
    var appFilter = Builders<Application>.Filter.Eq(x => x.Id, id);
    var appDoc = await db.Applications.Find(appFilter).FirstOrDefaultAsync(ct);

    if (appDoc is null)
        return Results.NotFound(new { error = "Application not found." });

    // create interview document
    var now = DateTime.UtcNow;

    var interview = new Interview
    {
        ApplicationId = appDoc.Id,
        JobId = appDoc.JobId,
        CandidateName = appDoc.CandidateName,
        CandidateEmail = appDoc.CandidateEmail,
        ScheduledAtUtc = req.ScheduledAtUtc,
        DurationMinutes = req.DurationMinutes <= 0 ? 60 : req.DurationMinutes,
        Location = string.IsNullOrWhiteSpace(req.Location) ? "Online" : req.Location,
        Status = "scheduled",
        CreatedAtUtc = now
    };

    // transaction: interview + application status + outbox
    // only works on replicaset, without it, we need to do "compensation script"
    // we accepted because we are targeting production env
    using var session = await mongoClient.StartSessionAsync(cancellationToken: ct);

    try
    {
        session.StartTransaction();

        // insert interview
        await db.Interviews.InsertOneAsync(session, interview, cancellationToken: ct);

        // preparing email command to outbox
        var subject = $"Interview scheduled for Job {interview.JobId}";
        var body =
            $"Hi {appDoc.CandidateName},\n\n" +
            $"Your interview has been scheduled.\n\n" +
            $"Date/Time (UTC): {interview.ScheduledAtUtc:yyyy-MM-dd HH:mm}\n" +
            $"Duration: {interview.DurationMinutes} minutes\n" +
            $"Location: {interview.Location}\n\n" +
            $"Thank you,\nHireflow";

        // construct outbox message
        var outbox = new OutboxMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            OccurredAtUtc = now,
            Type = "SendEmail.InterviewScheduled",
            PayloadJson = JsonSerializer.Serialize(new
            {
                type = "SendEmail",
                to = appDoc.CandidateEmail,
                subject,
                body,
                applicationId = appDoc.Id,
                interviewId = interview.Id,
                jobId = interview.JobId
            }),
            Status = "Pending",
            RetryCount = 0,
            NextAttemptAtUtc = now
        };

        // update application status
        var update = Builders<Application>.Update.Set(x => x.Status, "interview");
        await db.Applications.UpdateOneAsync(
            session,
            Builders<Application>.Filter.Eq(x => x.Id, appDoc.Id),
            update,
            cancellationToken: ct);

        // insert outbox message
        await db.OutboxMessages.InsertOneAsync(session, outbox, cancellationToken: ct);

        await session.CommitTransactionAsync(ct);
    }
    catch (Exception ex)
    {
        try
        {
            await session.AbortTransactionAsync(ct);
        }
        catch
        {
            /* ignore */
        }

        Console.WriteLine($"[ERROR] schedule interview failed for application {id}: {ex.Message}");
        return Results.Problem("Failed to schedule interview.");
    }


    var response = new InterviewResponse(
        interview.Id,
        interview.ApplicationId,
        interview.JobId,
        interview.CandidateName,
        interview.CandidateEmail,
        interview.ScheduledAtUtc,
        interview.DurationMinutes,
        interview.Location,
        interview.Status,
        interview.CreatedAtUtc
    );

    return Results.Created($"/interviews/{interview.Id}", response);
});

// GET /applications/{id}/interviews : list interviews for this application
app.MapGet("/applications/{id}/interviews", async (
    string id,
    ApplicationsDb db,
    CancellationToken ct) =>
{
    var filter = Builders<Interview>.Filter.Eq(x => x.ApplicationId, id);

    var interviews = await db.Interviews
        .Find(filter)
        .SortBy(x => x.ScheduledAtUtc)
        .ToListAsync(ct);

    var response = interviews
        .Select(i => new InterviewResponse(
            i.Id,
            i.ApplicationId,
            i.JobId,
            i.CandidateName,
            i.CandidateEmail,
            i.ScheduledAtUtc,
            i.DurationMinutes,
            i.Location,
            i.Status,
            i.CreatedAtUtc
        ))
        .ToList();

    return Results.Ok(response);
});

// GET /interviews/{id} : get a single interview
app.MapGet("/interviews/{id}", async (
    string id,
    ApplicationsDb db,
    CancellationToken ct) =>
{
    var filter = Builders<Interview>.Filter.Eq(x => x.Id, id);
    var interview = await db.Interviews.Find(filter).FirstOrDefaultAsync(ct);

    if (interview is null)
        return Results.NotFound();

    var response = new InterviewResponse(
        interview.Id,
        interview.ApplicationId,
        interview.JobId,
        interview.CandidateName,
        interview.CandidateEmail,
        interview.ScheduledAtUtc,
        interview.DurationMinutes,
        interview.Location,
        interview.Status,
        interview.CreatedAtUtc
    );

    return Results.Ok(response);
});

// GET /outbox?status=Pending&limit=50
app.MapGet("/outbox", async (
    string? status,
    int? limit,
    ApplicationsDb db,
    CancellationToken ct) =>
{
    var take = Math.Clamp(limit ?? 50, 1, 200);

    FilterDefinition<OutboxMessage> filter = FilterDefinition<OutboxMessage>.Empty;

    if (!string.IsNullOrWhiteSpace(status))
    {
        filter = Builders<OutboxMessage>.Filter.Eq(x => x.Status, status);
    }

    var items = await db.OutboxMessages
        .Find(filter)
        .SortByDescending(x => x.OccurredAtUtc)
        .Limit(take)
        .ToListAsync(ct);

    // return a smaller shape (payload can be huge)
    var response = items.Select(x => new
    {
        x.Id,
        x.Type,
        x.Status,
        x.RetryCount,
        x.OccurredAtUtc,
        x.NextAttemptAtUtc,
        x.ProcessingStartedAtUtc,
        x.ProcessedAtUtc,
        x.LockedBy,
        x.LastError
    });

    return Results.Ok(response);
});

// GET /outbox/{id}
app.MapGet("/outbox/{id}", async (
    string id,
    ApplicationsDb db,
    CancellationToken ct) =>
{
    var item = await db.OutboxMessages
        .Find(Builders<OutboxMessage>.Filter.Eq(x => x.Id, id))
        .FirstOrDefaultAsync(ct);

    return item is null ? Results.NotFound() : Results.Ok(item);
});

// GET /outbox/summary
app.MapGet("/outbox/summary", async (ApplicationsDb db, CancellationToken ct) =>
{
    var statuses = new[] { "Pending", "Processing", "Processed", "Failed" };

    var tasks = statuses.Select(async s =>
    {
        var count = await db.OutboxMessages.CountDocumentsAsync(
            Builders<OutboxMessage>.Filter.Eq(x => x.Status, s),
            cancellationToken: ct);

        return new { status = s, count };
    });

    var results = await Task.WhenAll(tasks);
    return Results.Ok(results);
});


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