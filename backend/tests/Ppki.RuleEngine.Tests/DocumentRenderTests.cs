using System.Security.Cryptography;
using System.Net;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Options;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.Infrastructure;
using Ppki.RenderEngine;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class DocumentRenderTests
{
    [Fact]
    public void Canonical_identity_is_deterministic_version_and_contract_bound()
    {
        var version = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var sha = new string('a', 64);
        var first = CanonicalDocumentRenderContract.CreateJob(version, sha);
        var replay = CanonicalDocumentRenderContract.CreateJob(version, sha.ToUpperInvariant());
        var nextVersion = CanonicalDocumentRenderContract.CreateJob(Guid.Parse("22222222-2222-4222-8222-222222222222"), sha);
        Assert.Equal(first.RenderIdentity, replay.RenderIdentity);
        Assert.NotEqual(first.RenderIdentity, nextVersion.RenderIdentity);
        Assert.Equal("8.34.0+libreoffice-26.2.4.2", first.RendererVersion);
        Assert.Equal("ppki-liberation-noto/1.0", first.FontProfileVersion);
        Assert.Equal("page-map/1.0", first.PageMapSchemaVersion);
    }

    [Fact]
    public void Preview_path_is_server_owned_canonical_and_validated()
    {
        var paths = new StorageObjectPathBuilder();
        var value = paths.BuildDocumentPreviewPath(Guid.Empty, Guid.Parse("11111111-1111-4111-8111-111111111111"), Guid.Parse("22222222-2222-4222-8222-222222222222"));
        Assert.Equal("00000000-0000-0000-0000-000000000000/11111111-1111-4111-8111-111111111111/22222222-2222-4222-8222-222222222222.pdf", value);
        paths.ValidateStoredPath(StorageObjectPathBuilder.ReportBucket, value);
        Assert.Throws<ArgumentException>(() => paths.ValidateStoredPath(StorageObjectPathBuilder.ReportBucket, "../preview.pdf"));
    }

    [Fact]
    public async Task Malformed_package_fails_safely_without_mutating_source_and_cleans_workspace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ppki-render-invalid-{Guid.NewGuid():N}.docx");
        await File.WriteAllBytesAsync(path, "not-a-docx"u8.ToArray());
        var before = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        var renderer = new GotenbergCanonicalDocumentRenderer(new UnusedHttpClientFactory(),
            Options.Create(new DocumentRendererOptions()), new OpenXmlDocxParser());
        var exception = await Assert.ThrowsAsync<DocumentRenderException>(() => renderer.RenderAsync(path, CancellationToken.None));
        Assert.Equal("render-package-invalid", exception.DiagnosticCode);
        Assert.Equal(before, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        Assert.DoesNotContain(Directory.EnumerateDirectories(Path.GetTempPath(), "ppki-render-*"), value => Directory.EnumerateFiles(value).Contains(path));
        File.Delete(path);
    }

    [Fact]
    public async Task Golden_fixture_contains_structural_duplicate_run_hyperlink_table_and_break_cases()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("document-page-map-multipage");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        const string duplicate = "Penelitian ini dilakukan pada lokasi sintetis yang sama.";
        var duplicates = parsed.Paragraphs.Where(value => value.Text == duplicate).ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.NotEqual(duplicates[0].Location!.ParagraphIndex, duplicates[1].Location!.ParagraphIndex);
        Assert.Contains(parsed.Paragraphs, value => value.Text == "ABSTRAK");
        Assert.Contains(parsed.Paragraphs, value => value.Text == "BAB I PENDAHULUAN");
        Assert.Contains(parsed.Paragraphs, value => value.Location?.TableIndex is not null);
        Assert.Contains(parsed.Paragraphs, value => value.RunList.Count > 1);
        using var package = WordprocessingDocument.Open(workspace.WorkingPath, false);
        Assert.NotEmpty(package.MainDocumentPart!.Document!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Hyperlink>());
        Assert.NotEmpty(package.MainDocumentPart.Document.Descendants<DocumentFormat.OpenXml.Wordprocessing.Break>());
        Assert.NotEmpty(package.MainDocumentPart.Document.Descendants<DocumentFormat.OpenXml.Wordprocessing.SectionType>());
    }

    [Fact]
    public void Migration_is_additive_immutable_admin_only_and_indexed()
    {
        var sql = Source("supabase", "migrations", "202608080001_document_render_page_map.sql");
        Assert.Contains("on delete restrict", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unique (render_identity)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("page_number >= 1", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Terminal document render job is immutable", sql, StringComparison.Ordinal);
        Assert.Contains("Document render artifact is immutable", sql, StringComparison.Ordinal);
        Assert.Contains("Document page map entry is immutable", sql, StringComparison.Ordinal);
        Assert.Contains("document_page_map_entries(render_artifact_id, paragraph_index, run_index)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p.role='PPKIAdmin'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("on delete cascade", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_lifecycle_queues_both_initial_and_fix_result_versions()
    {
        Assert.Contains("CanonicalDocumentRenderContract.CreateJob(version.Id, version.Sha256)",
            Source("backend", "services", "Ppki.Api", "Program.cs"), StringComparison.Ordinal);
        Assert.Contains("CanonicalDocumentRenderContract.CreateJob(resultVersion.Id, resultVersion.Sha256)",
            Source("backend", "services", "Ppki.Worker", "FixExecutionProcessor.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Claim_terminalizes_exhausted_pending_or_expired_rows_and_never_starts_attempt_four()
    {
        var worker = Source("backend", "services", "Ppki.Worker", "QueuedDocumentRenderWorker.cs");
        var recovery = worker.IndexOf("await RecoverOneExhaustedAsync", StringComparison.Ordinal);
        var reset = worker.IndexOf("value.AttemptCount < value.MaxAttempts", recovery, StringComparison.Ordinal);
        var claim = worker.IndexOf("where state = 'Pending' and attempt_count < max_attempts", reset,
            StringComparison.Ordinal);
        var increment = worker.IndexOf("job.AttemptCount++", claim, StringComparison.Ordinal);

        Assert.True(recovery >= 0 && reset > recovery && claim > reset && increment > claim);
        Assert.Contains("where attempt_count >= max_attempts", worker, StringComparison.Ordinal);
        Assert.Contains("for update skip locked", worker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exhausted.State = DocumentRenderState.Processing", worker, StringComparison.Ordinal);
        Assert.Contains("exhausted.State = DocumentRenderState.Failed", worker, StringComparison.Ordinal);
        Assert.Contains("exhausted.ClaimToken = recoveryToken", worker, StringComparison.Ordinal);
        Assert.Contains("render-attempts-exhausted", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt_count <= max_attempts", worker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("state in ('Completed','Failed')", worker, StringComparison.OrdinalIgnoreCase);

        var migration = Source("supabase", "migrations", "202608080001_document_render_page_map.sql");
        Assert.Contains("attempt_count between 0 and max_attempts", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_and_page_map_reads_are_authorized_bounded_and_do_not_expose_paths()
    {
        var api = Source("backend", "services", "Ppki.Api", "Program.cs");
        Assert.Contains("MapGroup(\"/api\").RequireAuthorization().AddEndpointFilter<InternalAdminEndpointFilter>()", api, StringComparison.Ordinal);
        Assert.Contains("/document-versions/{id:guid}/preview", api, StringComparison.Ordinal);
        Assert.Contains("Results.File(bytes,\"application/pdf\"", api, StringComparison.Ordinal);
        var read = Source("backend", "src", "Ppki.Infrastructure", "AuditReadService.cs");
        Assert.Contains("Take(query.PageSize)", read, StringComparison.Ordinal);
        Assert.Contains("paragraphIndexes.Contains", read, StringComparison.Ordinal);
        Assert.DoesNotContain("ToListAsync(cancellationToken);\n        var offset", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_has_bounded_sandbox_and_structural_not_text_mapping()
    {
        var source = Source("backend", "src", "Ppki.RenderEngine", "GotenbergCanonicalDocumentRenderer.cs");
        Assert.Contains("exportBookmarksToPdfDestination", source, StringComparison.Ordinal);
        Assert.Contains("Directory.Delete(workspace, recursive: true)", source, StringComparison.Ordinal);
        Assert.Contains("MaximumInputBytes", source, StringComparison.Ordinal);
        Assert.Contains("CancelAfter", source, StringComparison.Ordinal);
        Assert.Contains("mapped.TryGetValue(anchor.Name", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IndexOf(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Contains(anchor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renderer_timeout_is_safe_and_source_path_must_be_absolute()
    {
        var renderer = new GotenbergCanonicalDocumentRenderer(new StaticHttpClientFactory(
            new HttpClient(new DelayedHandler()) { Timeout = Timeout.InfiniteTimeSpan }),
            Options.Create(new DocumentRendererOptions { BaseUrl = "http://127.0.0.1:1", TimeoutSeconds = 1 }),
            new OpenXmlDocxParser());
        var invalid = await Assert.ThrowsAsync<DocumentRenderException>(() => renderer.RenderAsync("relative.docx", CancellationToken.None));
        Assert.Equal("render-source-path-invalid", invalid.DiagnosticCode);
        await using var fixture = await DocxFixtureWorkspace.CreateAsync("document-page-map-multipage");
        var timeout = await Assert.ThrowsAsync<DocumentRenderException>(() => renderer.RenderAsync(fixture.WorkingPath, CancellationToken.None));
        Assert.Equal("render-timeout", timeout.DiagnosticCode);
        Assert.True(timeout.Retryable);
    }

    [Fact]
    public async Task Real_pinned_renderer_maps_duplicate_text_and_runs_deterministically_when_enabled()
    {
        var baseUrl = Environment.GetEnvironmentVariable("PPKI_RENDERER_URL");
        if (string.IsNullOrWhiteSpace(baseUrl)) return;
        await using var fixture = await DocxFixtureWorkspace.CreateAsync("document-page-map-multipage");
        var sourceBefore = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(fixture.WorkingPath)));
        var parser = new OpenXmlDocxParser();
        var parsed = await parser.ParseAsync(fixture.WorkingPath, CancellationToken.None);
        var renderer = new GotenbergCanonicalDocumentRenderer(new StaticHttpClientFactory(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan }),
            Options.Create(new DocumentRendererOptions { BaseUrl = baseUrl, TimeoutSeconds = 30 }), parser);
        var first = await renderer.RenderAsync(fixture.WorkingPath, CancellationToken.None);
        var replay = await renderer.RenderAsync(fixture.WorkingPath, CancellationToken.None);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(first.PdfBytes, 0, 5));
        Assert.True(first.PageCount >= 7);
        Assert.Equal(first.PageCount, replay.PageCount);
        Assert.Equal(first.SourceTextFingerprint, replay.SourceTextFingerprint);
        Assert.Equal(first.Entries, replay.Entries);
        Assert.Equal(sourceBefore, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(fixture.WorkingPath))));
        var duplicates = parsed.Paragraphs.Where(value => value.Text == "Penelitian ini dilakukan pada lokasi sintetis yang sama.").ToArray();
        var duplicatePages = duplicates.Select(value => first.Entries.Single(entry =>
            entry.ParagraphIndex == value.Location!.ParagraphIndex && entry.RunIndex is null)).ToArray();
        Assert.All(duplicatePages, entry => Assert.Equal(PageMapConfidence.Exact, entry.Confidence));
        Assert.NotEqual(duplicatePages[0].PageNumber, duplicatePages[1].PageNumber);
        Assert.All(duplicatePages, entry => Assert.True(entry.PageNumber >= 1));
        var boundary = parsed.Paragraphs.Single(value => value.Text.EndsWith("RUN-BATAS-SINTETIS", StringComparison.Ordinal));
        Assert.Equal(PageMapConfidence.Exact, first.Entries.Single(entry => entry.ParagraphIndex == boundary.Location!.ParagraphIndex
            && entry.RunIndex == boundary.RunList.Count - 1).Confidence);
    }

    private static string Source(params string[] segments) => File.ReadAllText(Path.Combine([RepositoryRoot(), .. segments]));

    private static string RepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (Directory.Exists(Path.Combine(candidate.FullName, "supabase", "migrations"))
                && Directory.Exists(Path.Combine(candidate.FullName, "backend"))) return candidate.FullName;
            candidate = candidate.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("HTTP must not be reached for malformed input.");
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelayedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
