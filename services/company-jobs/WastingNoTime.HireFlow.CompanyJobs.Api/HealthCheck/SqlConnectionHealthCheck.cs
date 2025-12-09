using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WastingNoTime.HireFlow.CompanyJobs.Api.HealthCheck;

public sealed class SqlConnectionHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    public SqlConnectionHealthCheck(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using DbConnection connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            return HealthCheckResult.Healthy("SQL connection is OK.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQL connection failed.", ex);
        }
    }
}
