using System.Text.Json;
using MongoDB.Driver;
using WastingNoTime.HireFlow.Applications.Data;
using WastingNoTime.HireFlow.Applications.Models;
using WastingNoTime.HireFlow.Applications.Messaging;

namespace WastingNoTime.HireFlow.Applications.Outbox;

public sealed class ApplicationsOutboxDispatcher : BackgroundService
{
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
        _logger.LogInformation("Applications Outbox dispatcher started. owner={Owner} poll={Poll}s batch={Batch} maxRetries={Max}",
            _lockOwner, _pollInterval.TotalSeconds, _batchSize, _maxRetries);

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

        _logger.LogInformation("Applications Outbox dispatcher stopping.");
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

            // claim ONE message at a time (atomic).
            var msg = await ClaimNextMessageAsync(db.OutboxMessages, ct);
            if (msg is null)
            {
                break; // no work
            }

            try
            {
                await DispatchMessageAsync(msg, bus, ct);
                await MarkProcessedAsync(db.OutboxMessages, msg.Id, ct);
                processed++;

                _logger.LogInformation("Outbox processed: id={Id} type={Type} retries={Retries}",
                    msg.Id, msg.Type, msg.RetryCount);
            }
            catch (Exception ex)
            {
                await MarkFailedOrRetryAsync(db.OutboxMessages, msg, ex, ct);

                _logger.LogWarning(ex,
                    "Outbox send failed: id={Id} type={Type} attempt={Attempt}/{Max}",
                    msg.Id, msg.Type, msg.RetryCount + 1, _maxRetries);
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

        // todo: eligible -> Pending and due, or (optional) stuck Processing older than N minutes.
        // for demo, we only do Pending due.
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

        // sort by oldest first
        var opts = new FindOneAndUpdateOptions<OutboxMessage>
        {
            Sort = Builders<OutboxMessage>.Sort.Ascending(x => x.OccurredAtUtc),
            ReturnDocument = ReturnDocument.After
        };

        return await outbox.FindOneAndUpdateAsync(filter, update, opts, ct);
    }

    private async Task DispatchMessageAsync(OutboxMessage msg, INotificationsCommandBus bus, CancellationToken ct)
    {
        // keep it explicit and strict: unknown types are permanent failures.
        switch (msg.Type)
        {
            case "SendEmail.InterviewScheduled":
            {
                var payload = JsonSerializer.Deserialize<SendEmailPayload>(msg.PayloadJson)
                              ?? throw new InvalidOperationException("Outbox payload is invalid JSON.");

                await bus.PublishSendEmailAsync(
                    to: payload.To,
                    subject: payload.Subject,
                    body: payload.Body,
                    applicationId: payload.ApplicationId,
                    interviewId: payload.InterviewId,
                    jobId: payload.JobId,
                    ct: ct);

                break;
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
            .Set(x => x.NextAttemptAtUtc, nextAttemptAt);

        await outbox.UpdateOneAsync(filter, update, cancellationToken: ct);

        if (shouldFailPermanently)
        {
            _logger.LogError(ex, "Outbox permanently failed: id={Id} type={Type} retries={Retries}",
                msg.Id, msg.Type, nextRetryCount);
        }
    }

    private static bool IsPermanentError(OutboxMessage msg, Exception ex)
    {
        // for demo: unknown type is permanent; JSON invalid is permanent.
        // everything else we treat as transient (RabbitMQ down, network, etc).
        if (ex is InvalidOperationException && ex.Message.Contains("Unknown outbox message type", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ex is InvalidOperationException && ex.Message.Contains("invalid JSON", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static TimeSpan ComputeBackoff(int retryCount)
    {
        // demo-friendly backoff:
        // 1 -> 1s, 2 -> 3s, 3 -> 10s, then 30s...
        return retryCount switch
        {
            1 => TimeSpan.FromSeconds(1),
            2 => TimeSpan.FromSeconds(3),
            3 => TimeSpan.FromSeconds(10),
            _ => TimeSpan.FromSeconds(30)
        };
    }

    private static int GetInt(IConfiguration config, string key, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key) ?? config[key];
        return int.TryParse(raw, out var v) ? v : fallback;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);

    private sealed class SendEmailPayload
    {
        public string Type { get; set; } = "SendEmail";
        public string To { get; set; } = default!;
        public string Subject { get; set; } = default!;
        public string Body { get; set; } = default!;
        public string ApplicationId { get; set; } = default!;
        public string InterviewId { get; set; } = default!;
        public long JobId { get; set; }
    }
}
