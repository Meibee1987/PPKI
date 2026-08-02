using Ppki.Infrastructure;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class ConfigurationOptionsValidatorTests
{
    [Fact]
    public void Rejects_missing_required_setting_without_exposing_its_value()
    {
        var options = CreateSupabaseOptions(secretKey: null!);

        var result = new SupabaseOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("Supabase:SecretKey", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_empty_and_placeholder_settings_without_exposing_values()
    {
        const string placeholder = "change-me-not-for-error";
        var emptyKey = CreateSupabaseOptions(publishableKey: "   ");
        var placeholderUrl = CreateSupabaseOptions(url: $"https://{placeholder}.supabase.co");

        var emptyResult = new SupabaseOptionsValidator().Validate(null, emptyKey);
        var placeholderResult = new SupabaseOptionsValidator().Validate(null, placeholderUrl);

        Assert.False(emptyResult.Succeeded);
        Assert.False(placeholderResult.Succeeded);
        Assert.DoesNotContain(placeholder, string.Join(" ", placeholderResult.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_non_https_or_non_hosted_supabase_url()
    {
        var options = CreateSupabaseOptions(url: "http://not-supabase.invalid");

        var result = new SupabaseOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("Supabase:Url", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_missing_database_connection_string()
    {
        var result = new DatabaseOptionsValidator().Validate(null, new DatabaseOptions { Database = "" });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("ConnectionStrings:Database", StringComparison.Ordinal));
    }

    [Fact]
    public void Accepts_valid_configuration_without_opening_a_connection()
    {
        var supabaseResult = new SupabaseOptionsValidator().Validate(null, CreateSupabaseOptions());
        var databaseResult = new DatabaseOptionsValidator().Validate(null, new DatabaseOptions
        {
            Database = "Host=valid-project.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.valid-project;Password=test-password;SSL Mode=Require"
        });

        Assert.True(supabaseResult.Succeeded);
        Assert.True(databaseResult.Succeeded);
    }

    private static SupabaseOptions CreateSupabaseOptions(
        string? url = "https://valid-project.supabase.co",
        string? publishableKey = "sb_publishable_valid",
        string? secretKey = "sb_secret_valid") => new()
    {
        Url = url!,
        PublishableKey = publishableKey!,
        SecretKey = secretKey!,
        Storage = new SupabaseOptions.StorageOptions
        {
            OriginalBucket = "documents-original",
            VersionBucket = "documents-versions",
            ReportBucket = "audit-reports"
        }
    };
}
