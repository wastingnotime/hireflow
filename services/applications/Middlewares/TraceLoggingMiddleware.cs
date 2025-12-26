using System.Diagnostics;

namespace WastingNoTime.HireFlow.Applications.Middlewares;

public sealed class TraceLoggingMiddleware : IMiddleware
{
    private readonly ILogger<TraceLoggingMiddleware> _logger;
    private readonly string _service;
    private readonly string _env;

    public TraceLoggingMiddleware(ILogger<TraceLoggingMiddleware> logger, IConfiguration config, IHostEnvironment env)
    {
        _logger = logger;

        // prefer OTEL standard env var, fallback to app name
        _service = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
                   ?? config["OTEL_SERVICE_NAME"]
                   ?? env.ApplicationName;

        _env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
               ?? config["ASPNETCORE_ENVIRONMENT"]
               ?? env.EnvironmentName;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var activity = Activity.Current;

        // pull correlation_id from header if you standardize it at gateway
        var correlationId = context.Request.Headers["x-correlation-id"].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = null;

        using (_logger.BeginScope(new Dictionary<string, object?>
               {
                   ["service"] = _service,
                   ["env"] = _env,

                   ["trace_id"] = activity?.TraceId.ToString(),
                   ["span_id"]  = activity?.SpanId.ToString(),

                   ["correlation_id"] = correlationId,
                   ["event_type"] = "http.request",

                   ["request_id"] = context.TraceIdentifier,
                   ["path"] = context.Request.Path.Value,
                   ["method"] = context.Request.Method,
               }))
        {
            await next(context);
        }
    }
}