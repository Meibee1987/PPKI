using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.RegularExpressions;

namespace Ppki.Infrastructure;

public sealed class DatabaseOptions
{
    public const string SectionName = "ConnectionStrings";

    public string? Database { get; init; }
}

public sealed class SupabaseOptionsValidator : IValidateOptions<SupabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, SupabaseOptions options)
    {
        var failures = new List<string>();
        ValidateSupabaseUrl(options.Url, failures);
        ValidateRequired("Supabase:PublishableKey", options.PublishableKey, failures);
        ValidateRequired("Supabase:SecretKey", options.SecretKey, failures);

        var storage = options.Storage;
        if (storage is null)
        {
            failures.Add("Supabase:Storage is required.");
        }
        else
        {
            ValidateBucket("Supabase:Storage:OriginalBucket", storage.OriginalBucket, failures);
            ValidateBucket("Supabase:Storage:VersionBucket", storage.VersionBucket, failures);
            ValidateBucket("Supabase:Storage:ReportBucket", storage.ReportBucket, failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateSupabaseUrl(string? value, ICollection<string> failures)
    {
        if (IsMissingOrPlaceholder(value))
        {
            failures.Add("Supabase:Url is required and must not be a placeholder.");
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Supabase:Url must be an HTTPS Supabase hosted URL.");
        }
    }

    private static void ValidateBucket(string settingName, string? value, ICollection<string> failures)
    {
        var bucket = value ?? string.Empty;
        if (IsMissingOrPlaceholder(bucket)
            || bucket.Any(char.IsWhiteSpace)
            || bucket.Contains("..", StringComparison.Ordinal)
            || bucket.Contains('/')
            || bucket.Contains('\\')
            || bucket.Contains("://", StringComparison.Ordinal)
            || !Regex.IsMatch(bucket, "^[a-z0-9][a-z0-9-]{1,62}$", RegexOptions.CultureInvariant))
        {
            failures.Add($"{settingName} is required and must use a supported bucket name.");
        }
    }

    private static void ValidateRequired(string settingName, string? value, ICollection<string> failures)
    {
        if (IsMissingOrPlaceholder(value))
        {
            failures.Add($"{settingName} is required and must not be a placeholder.");
        }
    }

    internal static bool IsMissingOrPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value?.Contains("project_ref", StringComparison.OrdinalIgnoreCase) == true
        || value?.Contains("your-key", StringComparison.OrdinalIgnoreCase) == true
        || value?.Contains("change-me", StringComparison.OrdinalIgnoreCase) == true
        || value?.Contains("replace_me", StringComparison.OrdinalIgnoreCase) == true
        || value?.Contains("example", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        if (SupabaseOptionsValidator.IsMissingOrPlaceholder(options.Database))
        {
            return ValidateOptionsResult.Fail("ConnectionStrings:Database is required and must not be a placeholder.");
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(options.Database);
            if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database))
            {
                return ValidateOptionsResult.Fail("ConnectionStrings:Database must be a valid PostgreSQL connection string.");
            }
        }
        catch (ArgumentException)
        {
            return ValidateOptionsResult.Fail("ConnectionStrings:Database must be a valid PostgreSQL connection string.");
        }

        return ValidateOptionsResult.Success;
    }
}
