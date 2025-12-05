using Microsoft.AspNetCore.HttpLogging;
using MongoDB.Driver;
using WastingNoTime.HireFlow.Candidates.Api.Contracts;
using WastingNoTime.HireFlow.Candidates.Api.Data;
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
        appDoc.CreatedAtUtc
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
        appDoc.CreatedAtUtc
    );

    return Results.Ok(response);
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", svc = app.Environment.ApplicationName }));

app.Run();