using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.Infrastructure;

namespace Ppki.RuleEngine;

public sealed class AuditRunner(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IFileStorage fileStorage,
    IDocxParser docxParser,
    IEnumerable<IRuleValidator> validators,
    IResolvedRuleSetSnapshotBuilder snapshotBuilder,
    IResolvedRuleSetHasher snapshotHasher)
{
    private readonly IReadOnlyDictionary<string, IRuleValidator> _validators =
        validators.ToDictionary(x => x.ValidationKey, StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync(Guid auditJobId, CancellationToken cancellationToken)
    {
        try
        {
            AuditJob audit;
            IReadOnlyList<RuleDefinition> resolvedRules;
            string resolutionLayer;

            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                audit = await db.AuditJobs
                    .AsNoTracking()
                    .Include(x => x.DocumentVersion)
                    .SingleAsync(x => x.Id == auditJobId && x.Status == AuditJobStatus.Processing, cancellationToken);

                var assignedRules = await db.ProfileRules
                    .AsNoTracking()
                    .Where(assignment => assignment.ProfileVersionId == audit.ProfileVersionId && assignment.Rule!.IsImplemented)
                    .Select(assignment => assignment.Rule!)
                    .ToListAsync(cancellationToken);

                if (assignedRules.Count > 0)
                {
                    resolvedRules = assignedRules;
                    resolutionLayer = "profile";
                }
                else
                {
                    resolvedRules = await db.Rules
                        .AsNoTracking()
                        .Where(rule => rule.IsImplemented)
                        .ToListAsync(cancellationToken);
                    resolutionLayer = "catalog-default";
                }
            }

            var proposedSnapshots = snapshotBuilder.Build(auditJobId, resolvedRules, resolutionLayer, precedence: 0);
            var snapshots = await EnsureRuleSnapshotsAsync(auditJobId, proposedSnapshots, cancellationToken);

            var filePath = await fileStorage.MaterializeToTempFileAsync(
                audit.DocumentVersion!.StorageBucket,
                audit.DocumentVersion.StorageKey,
                cancellationToken);
            ParsedDocument parsed;
            try
            {
                parsed = await docxParser.ParseAsync(filePath, cancellationToken);
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
            }

            var pending = new List<AuditFinding>();
            foreach (var snapshot in snapshots)
            {
                if (!_validators.TryGetValue(snapshot.ValidationKey, out var validator)) continue;
                var rule = RuleFromSnapshot(snapshot);

                foreach (var result in validator.Validate(parsed, rule))
                {
                    pending.Add(new AuditFinding
                    {
                        AuditJobId = audit.Id,
                        RuleId = snapshot.RuleId,
                        Severity = snapshot.Severity,
                        RuleCodeSnapshot = snapshot.RuleCode,
                        FixModeSnapshot = snapshot.FixMode,
                        SourceSectionSnapshot = rule.SourceSection,
                        PdfPageSnapshot = rule.PdfPage,
                        PrintedPageSnapshot = rule.PrintedPage,
                        Message = result.Message,
                        ActualValueJson = JsonSerializer.Serialize(result.Actual),
                        ExpectedValueJson = JsonSerializer.Serialize(result.Expected),
                        LocationJson = JsonSerializer.Serialize(result.Location),
                        Confidence = result.Confidence
                    });
                }
            }

            await CompleteAuditAsync(auditJobId, pending, cancellationToken);
        }
        catch
        {
            await FailAuditIfProcessingAsync(auditJobId);
            throw;
        }
    }

    private async Task<IReadOnlyList<AuditRuleSnapshot>> EnsureRuleSnapshotsAsync(
        Guid auditJobId,
        IReadOnlyList<AuditRuleSnapshot> proposedSnapshots,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var audit = await db.AuditJobs
            .FromSqlInterpolated($"select * from public.audit_jobs where id = {auditJobId} for update")
            .SingleAsync(cancellationToken);
        if (audit.Status != AuditJobStatus.Processing)
        {
            throw new InvalidOperationException("Audit job is not processing.");
        }

        var snapshots = await db.AuditRuleSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.AuditJobId == auditJobId)
            .OrderBy(snapshot => snapshot.Ordinal)
            .ToListAsync(cancellationToken);

        if (snapshots.Count == 0 && audit.ResolvedRuleSetHash is null)
        {
            db.AuditRuleSnapshots.AddRange(proposedSnapshots);
            await db.SaveChangesAsync(cancellationToken);
            snapshots = proposedSnapshots.ToList();
        }

        var hash = snapshotHasher.Hash(snapshots);
        if (audit.ResolvedRuleSetHash is null)
        {
            audit.ResolvedRuleSetHash = hash;
            audit.ApplicableRuleCount = snapshots.Count;
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (!string.Equals(audit.ResolvedRuleSetHash, hash, StringComparison.Ordinal)
            || audit.ApplicableRuleCount != snapshots.Count)
        {
            throw new InvalidOperationException("Persisted audit rule snapshot is inconsistent.");
        }

        await transaction.CommitAsync(cancellationToken);
        return snapshots;
    }

    private async Task CompleteAuditAsync(
        Guid auditJobId,
        IReadOnlyList<AuditFinding> findings,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var audit = await db.AuditJobs.SingleAsync(
            item => item.Id == auditJobId && item.Status == AuditJobStatus.Processing,
            cancellationToken);

        db.AuditFindings.AddRange(findings);
        await db.SaveChangesAsync(cancellationToken);

        audit.TotalRules = audit.ApplicableRuleCount;
        audit.ErrorCount = findings.Count(item => item.Severity == RuleSeverity.Error);
        audit.WarningCount = findings.Count(item => item.Severity == RuleSeverity.Warning);
        audit.InfoCount = findings.Count(item => item.Severity == RuleSeverity.Info);
        audit.Score = CalculateScore(findings);
        audit.Status = AuditJobStatus.Completed;
        audit.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task FailAuditIfProcessingAsync(Guid auditJobId)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            await db.AuditJobs
                .Where(item => item.Id == auditJobId && item.Status == AuditJobStatus.Processing)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, AuditJobStatus.Failed)
                    .SetProperty(item => item.ErrorMessage, "Audit processing failed.")
                    .SetProperty(item => item.CompletedAt, DateTimeOffset.UtcNow), CancellationToken.None);
        }
        catch
        {
            // Preserve the original worker exception. Recovery may safely retry
            // only a still-processing job and cannot duplicate snapshots.
        }
    }

    private static RuleDefinition RuleFromSnapshot(AuditRuleSnapshot snapshot)
    {
        using var requirement = JsonDocument.Parse(snapshot.RequirementJson);
        using var source = JsonDocument.Parse(snapshot.SourceReferenceJson);
        var requirementRoot = requirement.RootElement;
        var sourceRoot = source.RootElement;

        return new RuleDefinition
        {
            Id = snapshot.RuleId,
            RuleCode = snapshot.RuleCode,
            Domain = snapshot.Domain,
            Subdomain = snapshot.Subdomain,
            AppliesTo = snapshot.AppliesTo,
            Element = snapshot.Element,
            OfficialRequirement = requirementRoot.GetProperty("officialRequirement").GetString() ?? string.Empty,
            ExpectedValuePattern = requirementRoot.GetProperty("expectedValuePattern").GetString() ?? string.Empty,
            Severity = snapshot.Severity,
            FixMode = snapshot.FixMode,
            ValidationKey = snapshot.ValidationKey,
            IsImplemented = true,
            SourceSection = NullableString(sourceRoot, "sourceSection"),
            PdfPage = NullableInt(sourceRoot, "pdfPage"),
            PrintedPage = NullableString(sourceRoot, "printedPage")
        };
    }

    private static string? NullableString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static int? NullableInt(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt32()
            : null;

    private static decimal CalculateScore(IEnumerable<AuditFinding> findings)
    {
        var violatedRules = findings
            .GroupBy(item => item.RuleId)
            .Select(group => group.Max(item => item.Severity switch
            {
                RuleSeverity.Error => 8,
                RuleSeverity.Warning => 3,
                _ => 0
            }))
            .Sum();

        return Math.Max(0, 100 - violatedRules);
    }
}
