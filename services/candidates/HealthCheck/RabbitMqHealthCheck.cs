using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace WastingNoTime.HireFlow.Candidates.Api.HealthCheck;

public sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly ConnectionFactory _factory;

    public RabbitMqHealthCheck(ConnectionFactory factory)
    {
        _factory = factory;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = _factory.CreateConnection();
            using var channel = connection.CreateModel();
            // enough to consider broker is reachable
            return Task.FromResult(HealthCheckResult.Healthy("RabbitMQ is reachable."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("RabbitMQ check failed.", ex));
        }
    }
}
