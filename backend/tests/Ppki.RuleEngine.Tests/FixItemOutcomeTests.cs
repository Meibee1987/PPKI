using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.FixEngine;
using Ppki.Infrastructure;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class FixItemOutcomeTests
{
    private const string Sentinel = "SENTINEL full paragraph thesis content must never persist";

    [Fact]
    public void Entity_mapping_is_append_only_bounded_and_traces_exact_plan_item_attempt_and_versions()
    {
        using var db = OfflineDb();
        var entity = db.Model.FindEntityType(typeof(FixItemResult));
        Assert.NotNull(entity);
        Assert.Equal("fix_item_results", entity.GetTableName());
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(value => value.Name).SequenceEqual([
                nameof(FixItemResult.FixExecutionJobId), nameof(FixItemResult.AttemptNumber),
                nameof(FixItemResult.FixPlanItemId)]));
        Assert.Equal(128, entity.FindProperty(nameof(FixItemResult.ValidationKey))!.GetMaxLength());
        Assert.Equal(128, entity.FindProperty(nameof(FixItemResult.FixKey))!.GetMaxLength());
        Assert.Equal(128, entity.FindProperty(nameof(FixItemResult.FixerVersion))!.GetMaxLength());
        Assert.Equal("jsonb", entity.FindProperty(nameof(FixItemResult.StructuralAnchorJson))!.GetColumnType());
        Assert.Equal("jsonb", entity.FindProperty(nameof(FixItemResult.BeforePayloadJson))!.GetColumnType());
        Assert.Equal("jsonb", entity.FindProperty(nameof(FixItemResult.AfterPayloadJson))!.GetColumnType());
    }

    [Fact]
    public async Task Application_guard_rejects_result_update_and_delete()
    {
        await using var db = OfflineDb();
        var result = Result();
        db.Attach(result);
        result.SafeFailureCode = "different-safe-code";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.Entry(result).State = EntityState.Unchanged;
        db.Remove(result);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Migration_enforces_exact_lineage_outcomes_bounds_append_only_fencing_and_owner_scope()
    {
        var sql = Source("supabase", "migrations", "202608260001_fix_item_results.sql");
        Assert.Contains("create table public.fix_item_results", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fk_fix_item_results_job_plan", sql, StringComparison.Ordinal);
        Assert.Contains("fk_fix_item_results_item_plan", sql, StringComparison.Ordinal);
        Assert.Contains("uq_fix_item_results_attempt_item", sql, StringComparison.Ordinal);
        Assert.Contains("outcome in ('Applied','Skipped','Failed')", sql, StringComparison.Ordinal);
        Assert.Contains("pg_column_size(structural_anchor) <= 512", sql, StringComparison.Ordinal);
        Assert.Contains("pg_column_size(before_payload) <= 1024", sql, StringComparison.Ordinal);
        Assert.Contains("pg_column_size(after_payload) <= 1024", sql, StringComparison.Ordinal);
        Assert.Contains("Fix item results are append-only", sql, StringComparison.Ordinal);
        Assert.Contains("before update on public.fix_item_results", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("before delete on public.fix_item_results", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("job_claim <> new.claim_token", sql, StringComparison.Ordinal);
        Assert.Contains("job_lease <= statement_timestamp()", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_array_elements(snapshot.snapshot -> 'items')", sql, StringComparison.Ordinal);
        Assert.Contains("trg_fix_execution_jobs_item_result_aggregate", sql, StringComparison.Ordinal);
        Assert.Contains("result_count <> item_count", sql, StringComparison.Ordinal);
        Assert.Contains("plan_state <> 'Completed'", sql, StringComparison.Ordinal);
        Assert.Contains("fix_item_results_select_owned", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("grant insert on table public.fix_item_results to authenticated", sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grant update", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grant delete", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Actual_reparsed_format_values_and_anchor_exclude_paragraph_text_and_raw_xml()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ppki-fix-item-outcome-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "privacy.docx");
        try
        {
            CreateDocument(path, "20");
            var parser = new OpenXmlDocxParser();
            var before = await parser.ParseAsync(path, CancellationToken.None);
            var paragraph = Assert.Single(before.Paragraphs);
            var target = new FixTargetLocation("main-document-run", paragraph.Location!.BodyElementIndex,
                paragraph.Location.SectionIndex, paragraph.Location.ParagraphIndex, 0);
            var operation = Operation(target, "run.font-size", "half-points", "24");
            var beforePayload = FixItemOutcomeCapture.Value(operation, before);
            var fontOperation = Operation(target, "run.font-family-ascii", "font-family-token", "Times New Roman");
            var fontPayload = FixItemOutcomeCapture.Value(fontOperation, before);
            var anchor = FixItemOutcomeCapture.Anchor(target);

            using (var package = WordprocessingDocument.Open(path, true))
            {
                package.MainDocumentPart!.Document!.Body!.Descendants<Run>().Single()
                    .RunProperties!.FontSize = new FontSize { Val = "24" };
                package.MainDocumentPart.Document.Save();
            }
            var after = await parser.ParseAsync(path, CancellationToken.None);
            var afterPayload = FixItemOutcomeCapture.Value(operation, after);
            var executionId = Guid.NewGuid();
            var approved = Approval(operation);
            var applied = Assert.Single(FixItemOutcomeCapture.Successful(
                executionId, 1, approved, before, after, executionId));
            var skipped = Assert.Single(FixItemOutcomeCapture.Successful(
                executionId, 2, approved, after, after, null));
            var failed = Assert.Single(FixItemOutcomeCapture.Failed(
                executionId, 3, approved, "storage-upload-transient"));
            var replayedFailure = Assert.Single(FixItemOutcomeCapture.Failed(
                executionId, 3, approved, "storage-upload-transient"));

            Assert.NotEqual(beforePayload, afterPayload);
            Assert.Contains("\"value\":\"20\"", beforePayload, StringComparison.Ordinal);
            Assert.Contains("\"value\":\"24\"", afterPayload, StringComparison.Ordinal);
            Assert.Contains("\"value\":\"Times New Roman\"", fontPayload, StringComparison.Ordinal);
            Assert.Equal(FixItemOutcome.Applied, applied.Outcome);
            Assert.Equal((beforePayload, afterPayload), (applied.BeforePayloadJson, applied.AfterPayloadJson));
            Assert.Equal(FixItemOutcome.Skipped, skipped.Outcome);
            Assert.Equal(skipped.BeforePayloadJson, skipped.AfterPayloadJson);
            Assert.Equal(FixItemOutcome.Failed, failed.Outcome);
            Assert.Null(failed.AfterPayloadJson);
            Assert.Equal("storage-upload-transient", failed.SafeFailureCode);
            Assert.Equal(failed.Id, replayedFailure.Id);
            foreach (var persisted in new[] { beforePayload, afterPayload, anchor })
            {
                Assert.DoesNotContain(Sentinel, persisted, StringComparison.Ordinal);
                Assert.DoesNotContain("<w:", persisted, StringComparison.OrdinalIgnoreCase);
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(persisted) <= FixItemOutcomeCapture.MaximumPayloadBytes);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Capture_contract_covers_every_current_deterministic_format_property_without_text_values()
    {
        var source = Source("backend", "src", "Ppki.FixEngine", "FixItemOutcomeCapture.cs");
        string[] properties = [
            "section.page-size", "section.margin-left", "section.margin-right", "section.margin-top",
            "section.margin-bottom", "run.font-family-ascii", "run.font-family-high-ansi", "run.font-size",
            "paragraph.line-spacing-value", "paragraph.line-spacing-rule", "paragraph.spacing-before",
            "paragraph.spacing-after", "paragraph.first-line-indent", "paragraph.alignment",
            "heading.runs-bold", "heading.runs-underline", "heading.alignment"
        ];
        Assert.All(properties, property => Assert.Contains($"\"{property}\"", source, StringComparison.Ordinal));
        Assert.DoesNotContain("paragraph.Text", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OuterXml", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ActualJson", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpectedJson", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LocationJson", source, StringComparison.Ordinal);
        Assert.Contains("font-family-sha256", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Publication_and_failure_paths_finalize_results_with_job_plan_and_version_atomically()
    {
        var processor = Source("backend", "services", "Ppki.Worker", "FixExecutionProcessor.cs");
        var worker = Source("backend", "services", "Ppki.Worker", "QueuedFixExecutionWorker.cs");
        Assert.Contains("AddResults(db, source, claim, results, resultId)", processor, StringComparison.Ordinal);
        Assert.Contains("db.DocumentVersions.Add(resultVersion)", processor, StringComparison.Ordinal);
        Assert.Contains("job.State = FixExecutionState.Completed", processor, StringComparison.Ordinal);
        Assert.Contains("CompletePlanAsync", processor, StringComparison.Ordinal);
        Assert.Contains("transaction.CommitAsync", processor, StringComparison.Ordinal);
        Assert.Contains("FixItemOutcomeCapture.Failed", worker, StringComparison.Ordinal);
        Assert.Contains("plan.Fail", worker, StringComparison.Ordinal);
        Assert.Contains("plan.BeginApplying", worker, StringComparison.Ordinal);
        Assert.Contains("ReclaimExhaustedAsync", worker, StringComparison.Ordinal);
        Assert.Contains("job.ClaimToken = token", worker, StringComparison.Ordinal);
        Assert.Contains("job.LeaseExpiresAt = lease", worker, StringComparison.Ordinal);

        var publication = Slice(processor, "private async Task CompleteWithVersion", "private async Task CompleteNoChangeAsync");
        AssertOrdered(publication, "db.DocumentVersions.Add(resultVersion)", "await db.SaveChangesAsync",
            "AddResults(db, source, claim, results, resultId)", "await db.SaveChangesAsync",
            "job.State = FixExecutionState.Completed");
        var noChange = Slice(processor, "private async Task CompleteNoChangeAsync", "private async Task<SourceRow?> LoadAsync");
        AssertOrdered(noChange, "AddResults(db, source, claim, results, null)", "await db.SaveChangesAsync",
            "job.State = FixExecutionState.NoChange");
        var failure = Slice(worker, "internal async Task RetryOrFailAsync", "}", lastDelimiter: true);
        AssertOrdered(failure, "db.FixItemResults.AddRange", "await db.SaveChangesAsync",
            "job.ClaimToken = null", "job.State = FixExecutionState.Failed");
        Assert.DoesNotContain("Reaudit", processor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FindingStatus.Fixed", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("FindingStatus.Fixed", worker, StringComparison.Ordinal);
    }

    private static PpkiDbContext OfflineDb() => new(new DbContextOptionsBuilder<PpkiDbContext>()
        .UseNpgsql("Host=localhost;Database=fix_item_outcome_offline_test").Options);

    private static FixItemResult Result() => new()
    {
        Id = Guid.NewGuid(), FixExecutionJobId = Guid.NewGuid(), FixPlanId = Guid.NewGuid(),
        FixPlanItemId = Guid.NewGuid(), SourceDocumentVersionId = Guid.NewGuid(), AttemptNumber = 1,
        ClaimToken = Guid.NewGuid(), OperationOrdinal = 1, Outcome = FixItemOutcome.Failed,
        ValidationKey = "body.font", FixKey = "body-font", FixerVersion = "1.0",
        PropertyIdentifier = "run.font-size", StructuralAnchorJson = "{}",
        SafeFailureCode = "fix-provider-unavailable"
    };

    private static FixPlanOperation Operation(FixTargetLocation target, string property, string type, string value) =>
        new(FixOperationKind.SetProperty, "test-fixer", "1.0", "TEST-001", "test.format",
            [Guid.NewGuid()], target, property, new(type, value), false, 1,
            "source-finding-snapshot-must-match", "set-safe-format");

    private static ApprovedFixPlanSnapshot Approval(FixPlanOperation operation)
    {
        var planId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var item = new ApprovedFixPlanItemSnapshot(itemId, operation.SourceFindingIds.Single(), 1,
            operation.RuleCode, operation.ValidationKey, RuleSeverity.Error, FixMode.Auto,
            FindingStatus.Open, null, null, null, null, null, "{}", "{}", "{}", 1,
            FixEligibilityStatus.Eligible, FixEligibilityReasonCode.Eligible, false,
            FixPlanDraftPreviewItemState.Previewable, "fix-operation-planned", true,
            operation.CapabilityId, operation.CapabilityVersion, operation, null!, null!);
        return new(FixPlanApprovalSnapshotSerializer.SchemaVersion, planId, Guid.NewGuid(), Guid.NewGuid(),
            new string('0', 64), new string('1', 64), DocumentKind.Skripsi.ToString(), "preview/1.0",
            "analysis/1.0", new string('2', 64), [item], null!, Guid.NewGuid(), DateTimeOffset.UtcNow);
    }

    private static void CreateDocument(string path, string fontSize)
    {
        using var package = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = package.AddMainDocumentPart();
        main.Document = new Document(new Body(
            new Paragraph(new ParagraphProperties(
                    new Justification { Val = JustificationValues.Both },
                    new SpacingBetweenLines { Before = "0", After = "0", Line = "360", LineRule = LineSpacingRuleValues.Auto },
                    new Indentation { FirstLine = "720" }),
                new Run(new RunProperties(new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" },
                    new FontSize { Val = fontSize }, new Bold { Val = true },
                    new Underline { Val = UnderlineValues.None }), new Text(Sentinel))),
            new SectionProperties(new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Left = 2268, Right = 1701, Top = 1701, Bottom = 1701 })));
        main.Document.Save();
    }

    private static string Source(params string[] segments) =>
        File.ReadAllText(Path.Combine([Data.RepositoryRoot(), .. segments]));

    private static string Slice(string source, string start, string end, bool lastDelimiter = false)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = lastDelimiter ? source.LastIndexOf(end, StringComparison.Ordinal)
            : source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from);
        return source[from..to];
    }

    private static void AssertOrdered(string source, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = source.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{marker}' after the preceding transaction phase.");
            previous = current;
        }
    }
}
