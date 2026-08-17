using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.FixEngine;
using Ppki.Infrastructure;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class FixApplyCapabilityTests
{
    [Fact]
    public void Production_registry_contains_explicit_versioned_formatting_capabilities()
    {
        var registry = ProductionFixCapabilities.CreatePreviewRegistry();
        Assert.Equal(7, registry.Capabilities.Count);
        var capability = registry.Capabilities.Single(value => value.ValidationKey == "body.justified");
        Assert.Equal("body.justified", capability.ValidationKey);
        Assert.Equal(BodyJustifiedFixProvider.Id, capability.CapabilityId);
        Assert.Equal("1.0", capability.CapabilityVersion);
        Assert.True(capability.DocumentMutationImplementationExists);
        Assert.IsType<BodyJustifiedFixProvider>(capability.Provider);
        Assert.Contains(registry.Capabilities, value => value.CapabilityId == BodyFontFixProvider.Id);
        Assert.Contains(registry.Capabilities, value => value.CapabilityId == BodyLineSpacingFixProvider.Id);
        Assert.Contains(registry.Capabilities, value => value.CapabilityId == BodyFirstLineIndentFixProvider.Id);
        Assert.Contains(registry.Capabilities, value => value.CapabilityId == AbstractParagraphSpacingFixProvider.Id);
        Assert.Contains(registry.Capabilities, value => value.CapabilityId == ChapterCenteredFixProvider.Id);
    }

    [Fact]
    public void Duplicate_apply_capability_is_rejected()
    {
        var exception = Assert.Throws<FixPlanConfigurationException>(() =>
            new FixApplyCapabilityRegistry([new BodyJustifiedFixProvider(), new BodyJustifiedFixProvider()]));
        Assert.Equal("fix-apply-capability-configuration-invalid", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("INVALID VERSION")]
    public void Empty_or_invalid_apply_capability_version_is_rejected(string version)
    {
        var exception = Assert.Throws<FixPlanConfigurationException>(() =>
            new FixApplyCapabilityRegistry([new StubApplyProvider("stub-capability", version)]));
        Assert.Equal("fix-apply-capability-configuration-invalid", exception.DiagnosticCode);
    }

    [Fact]
    public void Apply_registry_iteration_is_deterministic()
    {
        var registry = new FixApplyCapabilityRegistry([
            new StubApplyProvider("z-capability", "1.0"),
            new StubApplyProvider("a-capability", "1.0")
        ]);
        Assert.Equal(["a-capability", "z-capability"], registry.Providers.Select(value => value.CapabilityId));
    }

    [Fact]
    public void Apply_provider_version_mismatch_is_not_available()
    {
        var operation = Data.Plan().Preview.Operations.Single() with { CapabilityVersion = "2.0" };
        Assert.False(ProductionFixCapabilities.CreateApplyRegistry().CanApply(operation));
    }

    [Theory]
    [InlineData(FixMode.Auto)]
    [InlineData(FixMode.Confirm)]
    public void Fix_mode_alone_never_implies_apply_availability(FixMode mode)
    {
        var operation = Data.Plan().Preview.Operations.Single() with { CapabilityId = $"missing-{mode.ToString().ToLowerInvariant()}" };
        Assert.False(ProductionFixCapabilities.CreateApplyRegistry().CanApply(operation));
    }

    [Fact]
    public void Real_snapshot_creates_ready_exact_typed_operation()
    {
        var provider = new BodyJustifiedFixProvider();
        Assert.True(provider.TryCreate(Data.Finding(), out _, out var diagnostic), diagnostic);
        var plan = Data.Plan();
        Assert.Equal(FixPlanState.Ready, plan.Preview.State);
        var operation = Assert.Single(plan.Preview.Operations);
        Assert.Equal(FixOperationKind.SetProperty, operation.OperationKind);
        Assert.Equal("main-document-paragraph", operation.Target.Scope);
        Assert.Equal("paragraph.alignment", operation.PropertyIdentifier);
        Assert.Equal(new("enum-code", "justified"), operation.Expected);
        Assert.Equal("source-finding-snapshot-must-match", operation.PreconditionCode);
    }

    private sealed class StubApplyProvider(string id, string version) : IFixApplyProvider
    {
        public string CapabilityId => id;
        public string CapabilityVersion => version;
        public Task<FixApplyOutcome> ApplyAsync(FixApplyContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

public sealed class BodyJustifiedMutationTests
{
    [Fact]
    public async Task Mutation_matches_golden_after_preserves_text_and_source_and_reopens()
    {
        await using var beforeWorkspace = await DocxFixtureWorkspace.CreateAsync("minimal-invalid-layout");
        await using var expectedWorkspace = await DocxFixtureWorkspace.CreateAsync("minimal-invalid-layout-justified");
        var originalSha = beforeWorkspace.OriginalChecksum;
        var parser = new OpenXmlDocxParser();
        var before = await parser.ParseAsync(beforeWorkspace.WorkingPath, CancellationToken.None);
        var expected = await parser.ParseAsync(expectedWorkspace.WorkingPath, CancellationToken.None);
        var plan = Data.Plan();
        var provider = new BodyJustifiedFixProvider();

        var outcome = await provider.ApplyAsync(new(beforeWorkspace.WorkingPath, before,
            plan.Source.Findings.Single(), plan.Preview.Operations.Single()), CancellationToken.None);

        Assert.Equal(FixApplyOutcome.Changed, outcome);
        using (var package = WordprocessingDocument.Open(beforeWorkspace.WorkingPath, false))
        {
            var paragraph = Assert.IsType<Paragraph>(package.MainDocumentPart!.Document!.Body!.Elements().First());
            Assert.Equal(JustificationValues.Both, paragraph.ParagraphProperties!.Justification!.Val!.Value);
        }
        var after = await parser.ParseAsync(beforeWorkspace.WorkingPath, CancellationToken.None);
        Assert.Equal(OpenXmlDocxParser.SchemaVersion, after.ParserSchemaVersion);
        Assert.Equal(before.Paragraphs.Select(value => value.Text), after.Paragraphs.Select(value => value.Text));
        Assert.Equal(expected.Paragraphs.Single().DirectAlignment, after.Paragraphs.Single().DirectAlignment);
        Assert.Equal(before.Sections, after.Sections);
        Assert.Equal(before.Counts.ExternalRelationships, after.Counts.ExternalRelationships);
        Assert.Equal(originalSha, await DocxFixtureWorkspace.ComputeSha256Async(beforeWorkspace.OriginalPath));
    }

    [Theory]
    [InlineData("minimal-table-field-layout")]
    [InlineData("minimal-header-footer-layout")]
    public async Task Package_integrity_preserves_image_and_header_footer_relationships(string fixtureId)
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync(fixtureId);
        await ApplyAndValidatePackageAsync(workspace.WorkingPath);
    }

    [Fact]
    public async Task Package_integrity_preserves_external_hyperlink_identity()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-invalid-layout");
        AddExternalHyperlink(workspace.WorkingPath, "https://example.invalid/synthetic-reference-a");
        await ApplyAndValidatePackageAsync(workspace.WorkingPath);
    }

    [Fact]
    public async Task Package_integrity_rejects_same_relationship_count_with_changed_target()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-invalid-layout");
        AddExternalHyperlink(workspace.WorkingPath, "https://example.invalid/synthetic-reference-a");
        var snapshot = DocxPackageIntegrity.Capture(workspace.WorkingPath);
        ChangeExternalRelationshipTarget(workspace.WorkingPath, "https://example.invalid/synthetic-reference-b");

        var exception = Assert.Throws<FixExecutionException>(() =>
            DocxPackageIntegrity.ValidateMutation(snapshot, workspace.WorkingPath));

        Assert.Equal("fix-execution-package-integrity-failed", exception.DiagnosticCode);
    }

    [Fact]
    public async Task Mutation_changes_only_target_main_document_paragraph()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-header-footer-layout");
        var beforeXml = MainParagraphXml(workspace.WorkingPath);
        var parser = new OpenXmlDocxParser();
        var before = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var plan = Data.Plan();

        await new BodyJustifiedFixProvider().ApplyAsync(new(workspace.WorkingPath, before,
            plan.Source.Findings.Single(), plan.Preview.Operations.Single()), CancellationToken.None);

        var afterXml = MainParagraphXml(workspace.WorkingPath);
        Assert.NotEqual(beforeXml[0], afterXml[0]);
        Assert.Equal(beforeXml.Skip(1), afterXml.Skip(1));
    }

    [Fact]
    public async Task Repeated_apply_on_same_temporary_copy_is_no_change()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-invalid-layout");
        var parser = new OpenXmlDocxParser();
        var before = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var plan = Data.Plan();
        var provider = new BodyJustifiedFixProvider();
        await provider.ApplyAsync(new(workspace.WorkingPath, before, plan.Source.Findings.Single(),
            plan.Preview.Operations.Single()), CancellationToken.None);
        var after = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);

        var outcome = await provider.ApplyAsync(new(workspace.WorkingPath, after, plan.Source.Findings.Single(),
            plan.Preview.Operations.Single()), CancellationToken.None);

        Assert.Equal(FixApplyOutcome.NoChange, outcome);
    }

    [Fact]
    public async Task Wrong_location_fails_without_fallback_search()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-invalid-layout");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var plan = Data.Plan();
        var operation = plan.Preview.Operations.Single() with
        {
            Target = plan.Preview.Operations.Single().Target with { BodyElementIndex = 999 }
        };
        var exception = await Assert.ThrowsAsync<FixExecutionException>(() => new BodyJustifiedFixProvider().ApplyAsync(
            new(workspace.WorkingPath, parsed, plan.Source.Findings.Single(), operation), CancellationToken.None));
        Assert.Equal("fix-operation-target-precondition-failed", exception.DiagnosticCode);
    }

    [Fact]
    public async Task Wrong_actual_property_fails_controlled()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-invalid-layout");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var plan = Data.Plan();
        var finding = plan.Source.Findings.Single() with
        {
            ActualJson = plan.Source.Findings.Single().ActualJson.Replace("alignment", "fontName", StringComparison.Ordinal)
        };
        var exception = await Assert.ThrowsAsync<FixExecutionException>(() => new BodyJustifiedFixProvider().ApplyAsync(
            new(workspace.WorkingPath, parsed, finding, plan.Preview.Operations.Single()), CancellationToken.None));
        Assert.Equal("fix-operation-source-snapshot-mismatch", exception.DiagnosticCode);
    }

    [Fact]
    public async Task Cancellation_is_observed_before_mutation()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-invalid-layout");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var plan = Data.Plan();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BodyJustifiedFixProvider().ApplyAsync(
            new(workspace.WorkingPath, parsed, plan.Source.Findings.Single(), plan.Preview.Operations.Single()), cancellation.Token));
    }

    private static async Task ApplyAndValidatePackageAsync(string path)
    {
        var snapshot = DocxPackageIntegrity.Capture(path);
        var parser = new OpenXmlDocxParser();
        var before = await parser.ParseAsync(path, CancellationToken.None);
        var plan = Data.Plan();
        var outcome = await new BodyJustifiedFixProvider().ApplyAsync(new(path, before,
            plan.Source.Findings.Single(), plan.Preview.Operations.Single()), CancellationToken.None);

        Assert.Equal(FixApplyOutcome.Changed, outcome);
        DocxPackageIntegrity.ValidateMutation(snapshot, path);
        var after = await parser.ParseAsync(path, CancellationToken.None);
        Assert.Equal(OpenXmlDocxParser.SchemaVersion, after.ParserSchemaVersion);
        Assert.Equal(before.Paragraphs.Select(value => value.Text), after.Paragraphs.Select(value => value.Text));
    }

    private static void AddExternalHyperlink(string path, string target)
    {
        using var package = WordprocessingDocument.Open(path, true);
        var main = package.MainDocumentPart!;
        var relationship = main.AddHyperlinkRelationship(new Uri(target, UriKind.Absolute), true);
        var paragraph = Assert.IsType<Paragraph>(main.Document!.Body!.Elements().First());
        paragraph.Append(new Hyperlink(new Run(new Text(" Tautan sintetis"))) { Id = relationship.Id });
        main.Document.Save();
    }

    private static void ChangeExternalRelationshipTarget(string path, string target)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = archive.GetEntry("word/_rels/document.xml.rels")!;
        XDocument document;
        using (var stream = entry.Open()) document = XDocument.Load(stream);
        var relationship = document.Root!.Elements().Single(value =>
            string.Equals(value.Attribute("TargetMode")?.Value, "External", StringComparison.Ordinal));
        relationship.SetAttributeValue("Target", target);
        using var output = entry.Open();
        output.SetLength(0);
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static string[] MainParagraphXml(string path)
    {
        using var package = WordprocessingDocument.Open(path, false);
        return package.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Select(value => value.OuterXml).ToArray();
    }
}

public sealed class FixExecutionPersistenceContractTests
{
    [Fact]
    public void Approved_plan_identity_is_immutable_but_lifecycle_is_mutable_in_ef()
    {
        var options = new DbContextOptionsBuilder<PpkiDbContext>()
            .UseNpgsql("Host=localhost;Database=fix_execution_offline_test").Options;
        using var db = new PpkiDbContext(options);
        var job = Data.Job();
        db.Attach(job);
        job.PlanHash = new string('c', 64);
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    [Fact]
    public void Migration_has_additive_rls_idempotency_state_and_immutability_guards()
    {
        var sql = File.ReadAllText(Path.Combine(Data.RepositoryRoot(), "supabase", "migrations", "202608040001_fix_execution_jobs.sql"));
        Assert.Contains("unique (audit_job_id, idempotency_key)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unique (source_document_version_id, plan_hash)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("for update skip locked", Source("backend", "services", "Ppki.Worker", "QueuedFixExecutionWorker.cs"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("enable row level security", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("revoke all on table public.fix_execution_jobs from anon, authenticated", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approved_plan_snapshot is distinct from new.approved_plan_snapshot", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("result.parent_version_id", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("result.created_by_user_id", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("result.sha256", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insert into public.fix_execution_jobs", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Api_response_contract_has_no_storage_or_document_content_fields()
    {
        var names = typeof(FixExecutionAccepted).GetProperties().Concat(typeof(FixExecutionStatus).GetProperties())
            .Select(value => value.Name).ToArray();
        Assert.DoesNotContain(names, value => value.Contains("Storage", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Filename", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Text", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Xml", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Snapshot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Architecture_keeps_api_thin_provider_storage_free_and_parser_fix_free()
    {
        var api = Source("backend", "services", "Ppki.Api", "Program.cs");
        var provider = Source("backend", "src", "Ppki.FixEngine", "BodyJustifiedFixProvider.cs");
        var parserProject = Source("backend", "src", "Ppki.DocxEngine", "Ppki.DocxEngine.csproj");
        Assert.DoesNotContain("WordprocessingDocument", api, StringComparison.Ordinal);
        Assert.DoesNotContain("IFileStorage", provider, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveChanges", provider, StringComparison.Ordinal);
        Assert.DoesNotContain("Ppki.Infrastructure", provider, StringComparison.Ordinal);
        Assert.DoesNotContain("Ppki.FixEngine", parserProject, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_uses_upload_first_and_reads_existing_result_only_after_storage_conflict()
    {
        var worker = Source("backend", "services", "Ppki.Worker", "FixExecutionProcessor.cs");
        var publish = worker.IndexOf("await storage.SaveAsync(stream", StringComparison.Ordinal);
        var conflict = worker.IndexOf("FileStorageFailureKind.Conflict", publish, StringComparison.Ordinal);
        var readCanonical = worker.IndexOf("existingResult = await storage.MaterializeToTempFileAsync", conflict, StringComparison.Ordinal);

        Assert.True(publish >= 0 && conflict > publish && readCanonical > conflict);
    }

    [Fact]
    public void Replay_comparison_accepts_jsonb_normalized_multi_finding_selection()
    {
        var first = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("30000000-0000-0000-0000-000000000002");
        var job = Data.Job();
        job.SelectedFindingIdsJson = $"[\"{first:D}\", \"{second:D}\"]";
        var candidate = new FixExecutionCandidate(Guid.NewGuid(), job.AuditJobId,
            job.SourceDocumentVersionId, job.RequestedByUserId, job.IdempotencyKey,
            job.PlanHash, job.PlannerVersion, $"[\"{first:D}\",\"{second:D}\"]",
            job.ApprovedPlanSnapshotJson, job.PlannedOperationCount, job.CreatedAt);
        var compare = typeof(FixExecutionRepository).GetMethod("Compare",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = Assert.IsType<FixExecutionEnqueueResult>(compare.Invoke(null, [job, candidate]));

        Assert.True(result.IsReplay);
        Assert.Same(job, result.Job);
    }

    private static string Source(params string[] segments) => File.ReadAllText(Path.Combine([Data.RepositoryRoot(), .. segments]));
}

public sealed class FixExecutionAcceptanceTests
{
    [Fact]
    public async Task Ready_plan_with_exact_hash_is_queued_and_exact_replay_is_canonical()
    {
        var plan = Data.Plan();
        var repository = new MemoryExecutionRepository();
        var service = Service(plan.Source, repository);
        var selection = new FixPlanSelection([plan.Source.Findings.Single().FindingId]);
        var key = Guid.NewGuid();

        var first = await service.AcceptAsync(plan.Source.AuditId, Guid.NewGuid(), key, selection,
            plan.Preview.PlanHash, CancellationToken.None);
        var replay = await service.AcceptAsync(plan.Source.AuditId, Guid.NewGuid(), key, selection,
            plan.Preview.PlanHash, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(replay);
        Assert.Equal(first.Id, replay.Id);
        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal("Queued", first.State);
        Assert.Equal(1, repository.Count);
    }

    [Fact]
    public async Task Plan_hash_mismatch_is_rejected_before_persistence()
    {
        var plan = Data.Plan();
        var repository = new MemoryExecutionRepository();
        var service = Service(plan.Source, repository);
        var exception = await Assert.ThrowsAsync<FixExecutionException>(() => service.AcceptAsync(
            plan.Source.AuditId, Guid.NewGuid(), Guid.NewGuid(),
            new([plan.Source.Findings.Single().FindingId]), new string('c', 64), CancellationToken.None));
        Assert.Equal("fix-plan-stale", exception.DiagnosticCode);
        Assert.Equal(0, repository.Count);
    }

    [Fact]
    public async Task Non_completed_source_and_missing_apply_provider_are_rejected()
    {
        var plan = Data.Plan();
        var incomplete = plan.Source with { AuditStatus = AuditJobStatus.Processing };
        var selection = new FixPlanSelection([plan.Source.Findings.Single().FindingId]);
        var first = await Assert.ThrowsAsync<FixExecutionException>(() => Service(incomplete, new()).AcceptAsync(
            incomplete.AuditId, Guid.NewGuid(), Guid.NewGuid(), selection,
            new DeterministicFixPlanPreviewPlanner(ProductionFixCapabilities.CreatePreviewRegistry()).Create(incomplete).PlanHash,
            CancellationToken.None));
        Assert.Equal("fix-execution-plan-not-ready", first.DiagnosticCode);

        var service = new FixExecutionService(new StaticSourceReader(plan.Source),
            new DeterministicFixPlanPreviewPlanner(ProductionFixCapabilities.CreatePreviewRegistry()),
            new NeverApplyResolver(), new MemoryExecutionRepository(), TimeProvider.System);
        var second = await Assert.ThrowsAsync<FixExecutionException>(() => service.AcceptAsync(plan.Source.AuditId,
            Guid.NewGuid(), Guid.NewGuid(), selection, plan.Preview.PlanHash, CancellationToken.None));
        Assert.Equal("fix-execution-apply-capability-unavailable", second.DiagnosticCode);
    }

    [Fact]
    public async Task Selection_order_and_duplicates_normalize_to_same_execution()
    {
        var plan = Data.Plan();
        var id = plan.Source.Findings.Single().FindingId;
        Assert.True(FixPlanSelection.TryCreate([id.ToString(), id.ToString()], out var selection, out _));
        var repository = new MemoryExecutionRepository();
        var accepted = await Service(plan.Source, repository).AcceptAsync(plan.Source.AuditId, Guid.NewGuid(),
            Guid.NewGuid(), selection, plan.Preview.PlanHash, CancellationToken.None);
        Assert.NotNull(accepted);
        Assert.Equal(1, accepted.SelectedFindingCount);
    }

    private static FixExecutionService Service(FixPlanSource source, MemoryExecutionRepository repository) => new(
        new StaticSourceReader(source),
        new DeterministicFixPlanPreviewPlanner(ProductionFixCapabilities.CreatePreviewRegistry()),
        ProductionFixCapabilities.CreateApplyRegistry(), repository, TimeProvider.System);

    private sealed class StaticSourceReader(FixPlanSource source) : IFixPlanSourceReader
    {
        public Task<FixPlanSource?> LoadAsync(Guid auditId, Guid ownerUserId, FixPlanSelection selection,
            CancellationToken cancellationToken) => Task.FromResult<FixPlanSource?>(auditId == source.AuditId ? source : null);
    }

    private sealed class NeverApplyResolver : IFixApplyCapabilityResolver
    {
        public bool CanApply(FixPlanOperation operation) => false;
    }

    private sealed class MemoryExecutionRepository : IFixExecutionRepository
    {
        private readonly object gate = new();
        private readonly List<FixExecutionJob> jobs = [];
        public int Count { get { lock (gate) return jobs.Count; } }

        public Task<FixExecutionEnqueueResult> EnqueueAsync(FixExecutionCandidate candidate, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                var sameKey = jobs.SingleOrDefault(value => value.AuditJobId == candidate.AuditJobId
                    && value.IdempotencyKey == candidate.IdempotencyKey);
                if (sameKey is not null)
                {
                    var samePayload = sameKey.PlanHash == candidate.PlanHash
                        && sameKey.SelectedFindingIdsJson == candidate.SelectedFindingIdsJson;
                    return Task.FromResult(samePayload ? new FixExecutionEnqueueResult(sameKey, true)
                        : new FixExecutionEnqueueResult(null, false, "fix-execution-idempotency-conflict"));
                }
                var canonical = jobs.SingleOrDefault(value => value.SourceDocumentVersionId == candidate.SourceDocumentVersionId
                    && value.PlanHash == candidate.PlanHash);
                if (canonical is not null) return Task.FromResult(new FixExecutionEnqueueResult(canonical, true));
                var job = new FixExecutionJob
                {
                    Id = candidate.ExecutionId, AuditJobId = candidate.AuditJobId,
                    SourceDocumentVersionId = candidate.SourceDocumentVersionId,
                    RequestedByUserId = candidate.RequestedByUserId, IdempotencyKey = candidate.IdempotencyKey,
                    PlanHash = candidate.PlanHash, PlannerVersion = candidate.PlannerVersion,
                    SelectedFindingIdsJson = candidate.SelectedFindingIdsJson,
                    ApprovedPlanSnapshotJson = candidate.ApprovedPlanSnapshotJson,
                    PlannedOperationCount = candidate.PlannedOperationCount, CreatedAt = candidate.CreatedAt,
                    MaxAttempts = FixRetryPolicy.MaximumAttempts
                };
                jobs.Add(job);
                return Task.FromResult(new FixExecutionEnqueueResult(job, false));
            }
        }

        public Task<FixExecutionJob?> GetOwnedAsync(Guid executionId, Guid ownerUserId,
            CancellationToken cancellationToken)
        {
            lock (gate) return Task.FromResult<FixExecutionJob?>(jobs.SingleOrDefault(value => value.Id == executionId));
        }
    }
}

internal static class Data
{
    internal static (FixPlanSource Source, FixPlanPreview Preview) Plan()
    {
        var finding = Finding();
        var source = new FixPlanSource(Guid.Parse("10000000-0000-0000-0000-000000000001"), AuditJobStatus.Completed,
            Guid.Parse("20000000-0000-0000-0000-000000000001"), new string('a', 64), new string('b', 64),
            DocumentKind.Skripsi, [finding]);
        return (source, new DeterministicFixPlanPreviewPlanner(ProductionFixCapabilities.CreatePreviewRegistry()).Create(source));
    }

    internal static FixPlanFindingSnapshot Finding() => new(
        Guid.Parse("30000000-0000-0000-0000-000000000001"), 19, "PPKI-LAY-019", "layout", "body",
        "body.justified", RuleSeverity.Error, FixMode.Auto, FindingStatus.Open,
        JsonSerializer.Serialize(new { Property = "alignment", RawValue = "Left", NormalizedValue = "Left", Unit = "enum",
            ResolutionState = "Resolved", SourceKind = "Direct", SourceStyleId = (string?)null, Inherited = false,
            DiagnosticCode = (string?)null, SectionIndex = 0, ParagraphIndex = 0, RunIndex = (int?)null }),
        JsonSerializer.Serialize(new { Property = "alignment", AcceptedValues = new[] { "Justified" }, Unit = "enum",
            Tolerance = (string?)null, ContractSource = "resolved-snapshot-validation-key", ValidationKey = "body.justified" }),
        JsonSerializer.Serialize(new { CompactLocation = "maindocument/s:0/b:0/p:0/kind:paragraph", SectionIndex = 0,
            BodyElementIndex = 0, ParagraphIndex = 0, RunIndex = (int?)null }), 1);

    internal static FixExecutionJob Job() => new()
    {
        AuditJobId = Guid.NewGuid(), SourceDocumentVersionId = Guid.NewGuid(), RequestedByUserId = Guid.NewGuid(),
        IdempotencyKey = Guid.NewGuid(), PlanHash = new string('a', 64), PlannerVersion = "fix-plan-preview/1.0",
        SelectedFindingIdsJson = "[\"30000000-0000-0000-0000-000000000001\"]",
        ApprovedPlanSnapshotJson = "{}", PlannedOperationCount = 1
    };

    internal static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "package.json"))) return current.FullName;
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
