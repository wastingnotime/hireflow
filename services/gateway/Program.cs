using Microsoft.AspNetCore.HttpLogging;
using Yarp.ReverseProxy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields = HttpLoggingFields.All;
    logging.RequestBodyLogLimit = 4096;
    logging.ResponseBodyLogLimit = 4096;
});


var app = builder.Build();

app.UseHttpLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}


app.MapGet("/healthz", () => Results.Ok(new { status = "ok", svc = app.Environment.ApplicationName }));

app.MapReverseProxy();

app.Run();