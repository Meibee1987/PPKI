using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Ppki.Api;
using Ppki.Infrastructure;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class HealthCheckTests
{
    [Fact]
    public async Task Liveness_succeeds_without_invoking_a_database_probe()
    {
        var databaseProbe = new FakeDatabaseProbe(_ => Task.FromException<bool>(new InvalidOperationException("database should not be called")));
        using var provider = BuildProvider(databaseProbe, StorageOptions(), includeReadyChecks: true);
        var service = provider.GetRequiredService<HealthCheckService>();

        var report = await service.CheckHealthAsync(registration => registration.Tags.Contains("live"));

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(0, databaseProbe.CallCount);
    }

    [Fact]
    public async Task Readiness_succeeds_when_fake_dependencies_are_healthy()
    {
        using var provider = BuildProvider(new FakeDatabaseProbe(_ => Task.FromResult(true)), StorageOptions(), includeReadyChecks: true);
        var service = provider.GetRequiredService<HealthCheckService>();

        var report = await service.CheckHealthAsync(registration => registration.Tags.Contains("ready"));

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(["database", "storage-configuration"], report.Entries.Keys.Order().ToArray());
    }

    [Fact]
    public async Task Readiness_maps_a_failed_database_check_to_503()
    {
        using var provider = BuildProvider(new FakeDatabaseProbe(_ => Task.FromResult(false)), StorageOptions(), includeReadyChecks: true);
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(registration => registration.Tags.Contains("ready"));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await SafeHealthResponseWriter.WriteAsync(context, report);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task Database_check_honors_timeout_and_caller_cancellation()
    {
        var timeoutCheck = new DatabaseReadinessHealthCheck(new FakeDatabaseProbe(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        }), Options.Create(new ReadinessHealthCheckOptions { TimeoutSeconds = 1 }));

        var timeoutResult = await timeoutCheck.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, timeoutResult.Status);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => timeoutCheck.CheckHealthAsync(new HealthCheckContext(), cancellationSource.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("REPLACE_ME")]
    [InlineData("../documents")]
    [InlineData("https://bucket")]
    [InlineData("bucket_name")]
    public async Task Storage_configuration_rejects_unsafe_bucket_names(string bucket)
    {
        var check = new StorageConfigurationHealthCheck(Options.Create(StorageOptions(bucket)));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Health_response_only_contains_allowed_names_and_statuses()
    {
        const string connectionString = "Host=synthetic-host;Password=synthetic-password";
        const string secret = "sb_secret_synthetic_1234567890";
        var report = new HealthReport(new Dictionary<string, HealthReportEntry>
        {
            ["database"] = new(HealthStatus.Unhealthy, "Synthetic exception " + connectionString + " " + secret, TimeSpan.Zero, null, new Dictionary<string, object>())
        }, TimeSpan.Zero);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await SafeHealthResponseWriter.WriteAsync(context, report);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();

        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.DoesNotContain(connectionString, body, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic exception", body, StringComparison.Ordinal);
        Assert.Equal("{\"status\":\"Unhealthy\",\"checks\":[{\"name\":\"database\",\"status\":\"Unhealthy\"}]}", body);
    }

    private static ServiceProvider BuildProvider(FakeDatabaseProbe databaseProbe, SupabaseOptions storageOptions, bool includeReadyChecks)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDatabaseReadinessProbe>(databaseProbe);
        services.AddSingleton<IOptions<ReadinessHealthCheckOptions>>(Options.Create(new ReadinessHealthCheckOptions { TimeoutSeconds = 1 }));
        services.AddSingleton<IOptions<SupabaseOptions>>(Options.Create(storageOptions));
        var healthChecks = services.AddHealthChecks().AddCheck("live", () => HealthCheckResult.Healthy(), tags: ["live"]);
        if (includeReadyChecks)
        {
            healthChecks.AddCheck<DatabaseReadinessHealthCheck>("database", tags: ["ready"]);
            healthChecks.AddCheck<StorageConfigurationHealthCheck>("storage-configuration", tags: ["ready"]);
        }

        return services.BuildServiceProvider();
    }

    private static SupabaseOptions StorageOptions(string originalBucket = "documents-original") => new()
    {
        Url = "https://valid-project.supabase.co",
        PublishableKey = "sb_publishable_synthetic",
        SecretKey = "sb_secret_synthetic",
        Storage = new SupabaseOptions.StorageOptions
        {
            OriginalBucket = originalBucket,
            VersionBucket = "documents-versions",
            ReportBucket = "audit-reports"
        }
    };

    private sealed class FakeDatabaseProbe(Func<CancellationToken, Task<bool>> callback) : IDatabaseReadinessProbe
    {
        public int CallCount { get; private set; }

        public Task<bool> CanConnectAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return callback(cancellationToken);
        }
    }

}
