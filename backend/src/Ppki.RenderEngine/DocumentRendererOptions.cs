namespace Ppki.RenderEngine;

public sealed class DocumentRendererOptions
{
    public const string SectionName = "DocumentRenderer";
    public string BaseUrl { get; init; } = "http://renderer:3000";
    public int TimeoutSeconds { get; init; } = 30;
    public long MaximumInputBytes { get; init; } = 50L * 1024 * 1024;
    public long MaximumPdfBytes { get; init; } = 50L * 1024 * 1024;
}

public sealed class DocumentRenderException(string diagnosticCode, bool retryable, Exception? inner = null)
    : Exception("Canonical document rendering failed.", inner)
{
    public string DiagnosticCode { get; } = diagnosticCode;
    public bool Retryable { get; } = retryable;
}
