using Microsoft.AspNetCore.HttpLogging;
using MongoDB.Driver;
using WastingNoTime.HireFlow.Candidates.Api.Contracts;
using WastingNoTime.HireFlow.Candidates.Api.Data;
using WastingNoTime.HireFlow.Candidates.Api.Messaging;
using WastingNoTime.HireFlow.Candidates.Api.Models;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// Mongo connection
var mongoCs =
    Environment.GetEnvironmentVariable("CANDIDATES_MONGO_CONNECTION_STRING") ??
    configuration["CANDIDATES_MONGO_CONNECTION_STRING"] ??
    configuration["Mongo"] ??
    throw new InvalidOperationException("Missing Mongo connection string for Candidates");

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoCs));
builder.Services.AddSingleton<CandidatesDb>();

// RabbitMQ notifications publisher
builder.Services.AddSingleton<INotificationsCommandBus>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();

    var connString =
        Environment.GetEnvironmentVariable("RabbitMQ") ??
        config["RabbitMQ"] ??
        throw new InvalidOperationException("Missing RabbitMQ connection string for Candidates");

    return new RabbitMqNotificationsCommandBus(connString);
});


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

// ---------- Endpoints ----------

// POST /applications : candidate applies with resume
app.MapPost("/applications", async (
    HttpRequest request,
    IWebHostEnvironment env,
    CandidatesDb db,
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
app.MapGet("/applications/{id}", async (string id, CandidatesDb db, CancellationToken ct) =>
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
    CandidatesDb db,
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
    CandidatesDb db,
    INotificationsCommandBus notificationsBus,
    CancellationToken ct) =>
{
    // 1) Load application
    var appFilter = Builders<Application>.Filter.Eq(x => x.Id, id);
    var appDoc = await db.Applications.Find(appFilter).FirstOrDefaultAsync(ct);

    if (appDoc is null)
        return Results.NotFound(new { error = "Application not found." });

    // 2) Create interview document
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

    await db.Interviews.InsertOneAsync(interview, cancellationToken: ct);

    // 3) Move application status to "interview"
    appDoc.Status = "interview";
    await db.Applications.ReplaceOneAsync(
        Builders<Application>.Filter.Eq(x => x.Id, appDoc.Id),
        appDoc,
        cancellationToken: ct
    );

    // 4) Publish email command to RabbitMQ
    var subject = $"Interview scheduled for Job {interview.JobId}";
    var body =
        $"Hi {appDoc.CandidateName},\n\n" +
        $"Your interview has been scheduled.\n\n" +
        $"Date/Time (UTC): {interview.ScheduledAtUtc:yyyy-MM-dd HH:mm}\n" +
        $"Duration: {interview.DurationMinutes} minutes\n" +
        $"Location: {interview.Location}\n\n" +
        $"Thank you,\nHireflow";

    try
    {
        await notificationsBus.PublishSendEmailAsync(
            to: appDoc.CandidateEmail,
            subject: subject,
            body: body,
            applicationId: appDoc.Id,
            interviewId: interview.Id,
            jobId: interview.JobId,
            ct: ct);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WARN] Failed to publish email command for application {appDoc.Id}: {ex.Message}");
        // we don't fail the scheduling itself for M1
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
    CandidatesDb db,
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
    CandidatesDb db,
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

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", svc = app.Environment.ApplicationName }));

app.Run();