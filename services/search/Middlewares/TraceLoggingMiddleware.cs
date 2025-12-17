using System.Diagnostics;

public sealed class TraceLoggingMiddleware : IMiddleware
{
    private readonly ILogger<TraceLoggingMiddleware> _logger;

    public TraceLoggingMiddleware(ILogger<TraceLoggingMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var activity = Activity.Current;

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["trace_id"] = activity?.TraceId.ToString(),
            ["span_id"]  = activity?.SpanId.ToString(),
            ["request_id"] = context.TraceIdentifier,
            ["path"] = context.Request.Path.Value,
            ["method"] = context.Request.Method,
        }))
        {
            await next(context);
        }
    }
}
