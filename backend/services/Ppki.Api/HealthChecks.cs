using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Ppki.Infrastructure;

namespace Ppki.Api;

public sealed class ReadinessHealthCheckOptions
{
    public const string SectionName = "HealthChecks";

    public int TimeoutSeconds { get; init; } = 3;
}

public interface IDatabaseReadinessProbe
{
    Task<bool> CanConnectAsync(CancellationToken cancellationToken);
}

public sealed class DatabaseReadinessProbe(IDbContextFactory<PpkiDbContext> dbContextFactory) : IDatabaseReadinessProbe
{
    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Database.CanConnectAsync(cancellationToken);
    }
}

public sealed class DatabaseReadinessHealthCheck(
    IDatabaseReadinessProbe databaseReadinessProbe,
    IOptions<ReadinessHealthCheckOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            return await databaseReadinessProbe.CanConnectAsync(linkedSource.Token)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database is unavailable.");
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Database is unavailable.");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Database is unavailable.");
        }
    }
}

public sealed class StorageConfigurationHealthCheck(IOptions<SupabaseOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = new SupabaseOptionsValidator().Validate(null, options.Value);
        return Task.FromResult(validation.Succeeded
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Storage configuration is unavailable."));
    }
}

public static class SafeHealthResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static int StatusCodeFor(HealthStatus status) => status switch
    {
        HealthStatus.Unhealthy => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status200OK
    };

    public static Task WriteAsync(HttpContext httpContext, HealthReport report)
    {
        httpContext.Response.StatusCode = StatusCodeFor(report.Status);
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        var response = new SafeHealthResponse(
            report.Status.ToString(),
            report.Entries
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new SafeHealthCheck(entry.Key, entry.Value.Status.ToString()))
                .ToArray());
        return JsonSerializer.SerializeAsync(httpContext.Response.Body, response, SerializerOptions);
    }

    private sealed record SafeHealthResponse(string Status, IReadOnlyList<SafeHealthCheck> Checks);
    private sealed record SafeHealthCheck(string Name, string Status);
}
