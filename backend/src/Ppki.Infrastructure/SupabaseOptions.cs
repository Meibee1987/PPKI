namespace Ppki.Infrastructure;

public sealed class SupabaseOptions
{
    public const string SectionName = "Supabase";
    public required string Url { get; init; }
    public required string PublishableKey { get; init; }
    public required string SecretKey { get; init; }
    public StorageOptions Storage { get; init; } = new();

    public sealed class StorageOptions
    {
        public string OriginalBucket { get; init; } = "documents-original";
        public string VersionBucket { get; init; } = "documents-versions";
        public string ReportBucket { get; init; } = "audit-reports";
    }
}
