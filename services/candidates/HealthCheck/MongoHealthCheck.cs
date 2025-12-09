using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Driver;

namespace WastingNoTime.HireFlow.Candidates.Api.HealthCheck;

public sealed class MongoHealthCheck : IHealthCheck
{
    private readonly IMongoClient _client;

    public MongoHealthCheck(IMongoClient client)
    {
        _client = client;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Cheap ping to Mongo
            await _client.ListDatabaseNamesAsync(cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy("MongoDB is reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MongoDB check failed.", ex);
        }
    }
}
