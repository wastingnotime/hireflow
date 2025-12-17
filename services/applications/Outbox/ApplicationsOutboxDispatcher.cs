using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using WastingNoTime.HireFlow.Applications.Data;
using WastingNoTime.HireFlow.Applications.Messaging;
using WastingNoTime.HireFlow.Applications.Models;

namespace WastingNoTime.HireFlow.Applications.Outbox;

public sealed class ApplicationsOutboxDispatcher : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("hireflow/applications-outbox"); // NEW

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ApplicationsOutboxDispatcher> _logger;

    private readonly TimeSpan _pollInterval;
    private readonly int _batchSize;
    private readonly int _maxRetries;
    private readonly string _lockOwner;

    public ApplicationsOutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<ApplicationsOutboxDispatcher> logger,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _pollInterval = TimeSpan.FromSeconds(GetInt(config, "OUTBOX_POLL_SECONDS", 5));
        _batchSize = GetInt(config, "OUTBOX_BATCH_SIZE", 10);
        _maxRetries = GetInt(config, "OUTBOX_MAX_RETRIES", 5);

        var pod = Environment.GetEnvironmentVariable("POD_NAME") ?? "local";
        _lockOwner = $"applications-outbox@{pod}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // NEW: scope for the worker lifetime
        using var workerScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["component"] = "applications-outbox",
            ["outbox.owner"] = _lockOwner
        });

        _logger.LogInformation(
            "Outbox dispatcher started. pollSeconds={PollSeconds} batchSize={BatchSize} maxRetries={MaxRetries}",
            _pollInterval.TotalSeconds, _batchSize, _maxRetries);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox dispatcher loop error.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Outbox dispatcher stopping.");
    }

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationsDb>();
        var bus = scope.ServiceProvider.GetRequiredService<INotificationsCommandBus>();

        var processed = 0;

        for (var i = 0; i < _batchSize; i++)
        {
            ct.ThrowIfCancellationRequested();

            var msg = await ClaimNextMessageAsync(db.OutboxMessages, ct);
            if (msg is null) break;

            // NEW: correlation id per message (use outbox id)
            var correlationId = msg.Id;

            // NEW: parse payload once so we can log identifiers even on failures
            SendEmailPayload? payload = null;
            if (msg.Type == "SendEmail.InterviewScheduled")
            {
                try
                {
                    payload = JsonSerializer.Deserialize<SendEmailPayload>(msg.PayloadJson);
                }
                catch
                {
                    // we'll let DispatchMessageAsync throw the "invalid JSON" error;
                    // scope below still has outbox.id/type.
                }
            }

            // NEW: per-message structured scope
            using var msgScope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["correlation_id"] = correlationId,
                ["outbox.id"] = msg.Id,
                ["outbox.type"] = msg.Type,
                ["outbox.retry_count"] = msg.RetryCount,
                ["applicationId"] = payload?.ApplicationId,
                ["interviewId"] = payload?.InterviewId,
                ["jobId"] = payload?.JobId
            });

            // NEW: span per dispatch attempt (shows in Jaeger)
            using var activity = ActivitySource.StartActivity(
                "outbox.dispatch",
                ActivityKind.Internal);

            activity?.SetTag("outbox.id", msg.Id);
            activity?.SetTag("outbox.type", msg.Type);
            activity?.SetTag("outbox.retry_count", msg.RetryCount);
            if (payload?.ApplicationId is not null) activity?.SetTag("applicationId", payload.ApplicationId);
            if (payload?.InterviewId is not null) activity?.SetTag("interviewId", payload.InterviewId);
            activity?.SetTag("jobId", payload?.JobId);

            try
            {
                _logger.LogInformation("Outbox dispatching message.");

                await DispatchMessageAsync(msg, bus, ct);
                await MarkProcessedAsync(db.OutboxMessages, msg.Id, ct);
                processed++;

                _logger.LogInformation("Outbox processed.");
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                await MarkFailedOrRetryAsync(db.OutboxMessages, msg, ex, ct);

                _logger.LogWarning(ex,
                    "Outbox send failed. attempt={Attempt}/{MaxRetries}",
                    msg.RetryCount + 1, _maxRetries);

                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }
        }

        if (processed > 0)
        {
            _logger.LogInformation("Outbox batch done. processed={Count}", processed);
        }
    }

    private async Task<OutboxMessage?> ClaimNextMessageAsync(IMongoCollection<OutboxMessage> outbox, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var filter =
            Builders<OutboxMessage>.Filter.And(
                Builders<OutboxMessage>.Filter.Eq(x => x.Status, "Pending"),
                Builders<OutboxMessage>.Filter.Or(
                    Builders<OutboxMessage>.Filter.Eq(x => x.NextAttemptAtUtc, null),
                    Builders<OutboxMessage>.Filter.Lte(x => x.NextAttemptAtUtc, now)
                )
            );

        var update =
            Builders<OutboxMessage>.Update
                .Set(x => x.Status, "Processing")
                .Set(x => x.ProcessingStartedAtUtc, now)
                .Set(x => x.LockedBy, _lockOwner);

        var opts = new FindOneAndUpdateOptions<OutboxMessage>
        {
            Sort = Builders<OutboxMessage>.Sort.Ascending(x => x.OccurredAtUtc),
            ReturnDocument = ReturnDocument.After
        };

        return await outbox.FindOneAndUpdateAsync(filter, update, opts, ct);
    }

    private async Task DispatchMessageAsync(OutboxMessage msg, INotificationsCommandBus bus, CancellationToken ct)
    {
        switch (msg.Type)
        {
            case "SendEmail.InterviewScheduled":
            {
                _logger.LogWarning("Outbox raw payload json type={Type} json={Json}", msg.Type, msg.PayloadJson);
                
                var payload = JsonSerializer.Deserialize<SendEmailPayload>(msg.PayloadJson)
                              ?? throw new InvalidOperationException("Outbox payload is invalid JSON.");
                
                _logger.LogInformation("Decoded payload ApplicationId={ApplicationId} InterviewId={InterviewId} JobId={JobId}",
                    payload.ApplicationId, payload.InterviewId, payload.JobId);

                
                _logger.LogInformation("Outbox payload json: {Json}", msg.PayloadJson);
                
                if (string.IsNullOrWhiteSpace(payload.ApplicationId))
                    throw new InvalidOperationException("Outbox payload missing ApplicationId.");
                if (string.IsNullOrWhiteSpace(payload.InterviewId))
                    throw new InvalidOperationException("Outbox payload missing InterviewId.");
                if (payload.JobId <= 0)
                    throw new InvalidOperationException("Outbox payload missing/invalid JobId.");
                
                _logger.LogInformation(
                    "Publishing SendEmail applicationId={ApplicationId} interviewId={InterviewId} jobId={JobId}",
                    payload.ApplicationId, payload.InterviewId, payload.JobId);


                await bus.PublishSendEmailAsync(
                    to: payload.To,
                    subject: payload.Subject,
                    body: payload.Body,
                    applicationId: payload.ApplicationId,
                    interviewId: payload.InterviewId,
                    jobId: payload.JobId,
                    ct: ct);

                return;
            }

            default:
                throw new InvalidOperationException($"Unknown outbox message type '{msg.Type}'.");
        }
    }

    private async Task MarkProcessedAsync(IMongoCollection<OutboxMessage> outbox, string id, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var filter = Builders<OutboxMessage>.Filter.Eq(x => x.Id, id);

        var update = Builders<OutboxMessage>.Update
            .Set(x => x.Status, "Processed")
            .Set(x => x.ProcessedAtUtc, now)
            .Set(x => x.LastError, null);

        await outbox.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    private async Task MarkFailedOrRetryAsync(
        IMongoCollection<OutboxMessage> outbox,
        OutboxMessage msg,
        Exception ex,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var nextRetryCount = msg.RetryCount + 1;

        var shouldFailPermanently = nextRetryCount >= _maxRetries || IsPermanentError(msg, ex);

        var nextAttemptAt = shouldFailPermanently
            ? (DateTime?)null
            : now.Add(ComputeBackoff(nextRetryCount));

        var filter = Builders<OutboxMessage>.Filter.Eq(x => x.Id, msg.Id);

        var update = Builders<OutboxMessage>.Update
            .Set(x => x.Status, shouldFailPermanently ? "Failed" : "Pending")
            .Set(x => x.RetryCount, nextRetryCount)
            .Set(x => x.LastError, Truncate(ex.Message, 900))
            .Set(x => x.NextAttemptAtUtc, nextAttemptAt)
            .Set(x => x.LockedBy, _lockOwner);

        await outbox.UpdateOneAsync(filter, update, cancellationToken: ct);

        if (shouldFailPermanently)
        {
            _logger.LogError(ex, "Outbox permanently failed. retries={Retries}", nextRetryCount);
        }
    }

    private static bool IsPermanentError(OutboxMessage msg, Exception ex)
    {
        if (ex is InvalidOperationException && ex.Message.Contains("Unknown outbox message type", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ex is InvalidOperationException && ex.Message.Contains("invalid JSON", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static TimeSpan ComputeBackoff(int retryCount) => retryCount switch
    {
        1 => TimeSpan.FromSeconds(1),
        2 => TimeSpan.FromSeconds(3),
        3 => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(30)
    };

    private static int GetInt(IConfiguration config, string key, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key) ?? config[key];
        return int.TryParse(raw, out var v) ? v : fallback;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);

    private sealed class SendEmailPayload
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "SendEmail";

        [JsonPropertyName("to")]
        public string To { get; set; } = default!;

        [JsonPropertyName("subject")]
        public string Subject { get; set; } = default!;

        [JsonPropertyName("body")]
        public string Body { get; set; } = default!;

        [JsonPropertyName("applicationId")]
        public string ApplicationId { get; set; } = default!;

        [JsonPropertyName("interviewId")]
        public string InterviewId { get; set; } = default!;

        [JsonPropertyName("jobId")]
        public long JobId { get; set; }
    }

}
