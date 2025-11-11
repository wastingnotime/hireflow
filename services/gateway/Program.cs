var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", svc = app.Environment.ApplicationName }));

if (app.Environment.ApplicationName?.Contains("gateway", StringComparison.OrdinalIgnoreCase) == true)
{
    var http = new HttpClient { BaseAddress = new Uri("http://company-jobs.hireflow.svc.cluster.local") };
    app.MapPost("/api/companies", async (HttpRequest req) =>
    {
        using var sr = new StreamReader(req.Body);
        var json = await sr.ReadToEndAsync();
        var resp = await http.PostAsync("/companies", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        return Results.Content(await resp.Content.ReadAsStringAsync(), "application/json",null, resp.IsSuccessStatusCode ? 200 : 500);
    });
    app.MapPost("/api/jobs", async (HttpRequest req) =>
    {
        using var sr = new StreamReader(req.Body);
        var json = await sr.ReadToEndAsync();
        var resp = await http.PostAsync("/jobs", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        return Results.Content(await resp.Content.ReadAsStringAsync(), "application/json", null,resp.IsSuccessStatusCode ? 200 : 500);
    });
}

app.Run();