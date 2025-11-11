var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", svc = app.Environment.ApplicationName }));

if (app.Environment.ApplicationName?.Contains("company-jobs", StringComparison.OrdinalIgnoreCase) == true)
{
    var companies = new Dictionary<Guid, string>();
    app.MapPost("/companies", async (HttpRequest req) =>
    {
        using var sr = new StreamReader(req.Body);
        var dto = System.Text.Json.JsonSerializer.Deserialize<CompanyReq>(await sr.ReadToEndAsync())!;
        var id = Guid.NewGuid(); companies[id] = dto.name;
        return Results.Json(new { id });
    });
    app.MapPost("/jobs", async (HttpRequest req) =>
    {
        using var sr = new StreamReader(req.Body);
        var dto = System.Text.Json.JsonSerializer.Deserialize<JobReq>(await sr.ReadToEndAsync())!;
        return Results.Json(new { id = Guid.NewGuid(), dto.companyId, dto.title });
    });

}

app.Run();

record CompanyReq(string name);
record JobReq(Guid companyId, string title);
