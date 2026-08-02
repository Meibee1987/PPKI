using System.Security.Cryptography;
using System.Text;
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
    IEnumerable<IRuleValidator> validators)
{
    private readonly IReadOnlyDictionary<string, IRuleValidator> _validators =
        validators.ToDictionary(x => x.ValidationKey, StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync(Guid auditJobId, CancellationToken cancellationToken)
    {
        await using var startDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var audit = await startDb.AuditJobs
            .Include(x => x.DocumentVersion)
            .SingleAsync(x => x.Id == auditJobId, cancellationToken);

        audit.Status = AuditJobStatus.Processing;
        audit.StartedAt = DateTimeOffset.UtcNow;
        await startDb.SaveChangesAsync(cancellationToken);

        try
        {
            var rules = await startDb.Rules
                .AsNoTracking()
                .Where(x => x.IsImplemented)
                .OrderBy(x => x.RuleCode)
                .ToListAsync(cancellationToken);

            var filePath = await fileStorage.MaterializeToTempFileAsync(audit.DocumentVersion!.StorageBucket, audit.DocumentVersion.StorageKey, cancellationToken);
            ParsedDocument parsed;
            try { parsed = await docxParser.ParseAsync(filePath, cancellationToken); }
            finally { if (File.Exists(filePath)) File.Delete(filePath); }
            var pending = new List<AuditFinding>();

            foreach (var rule in rules)
            {
                if (!_validators.TryGetValue(rule.ValidationKey, out var validator))
                {
                    continue;
                }

                foreach (var result in validator.Validate(parsed, rule))
                {
                    pending.Add(new AuditFinding
                    {
                        AuditJobId = audit.Id,
                        RuleId = rule.Id,
                        Severity = rule.Severity,
                        Message = result.Message,
                        ActualValueJson = JsonSerializer.Serialize(result.Actual),
                        ExpectedValueJson = JsonSerializer.Serialize(result.Expected),
                        LocationJson = JsonSerializer.Serialize(result.Location),
                        Confidence = result.Confidence
                    });
                }
            }

            await using var completeDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            var trackedAudit = await completeDb.AuditJobs.SingleAsync(x => x.Id == auditJobId, cancellationToken);
            completeDb.AuditFindings.AddRange(pending);

            trackedAudit.TotalRules = rules.Count;
            trackedAudit.ErrorCount = pending.Count(x => x.Severity == RuleSeverity.Error);
            trackedAudit.WarningCount = pending.Count(x => x.Severity == RuleSeverity.Warning);
            trackedAudit.InfoCount = pending.Count(x => x.Severity == RuleSeverity.Info);
            trackedAudit.Score = CalculateScore(pending);
            trackedAudit.ResolvedRuleSetHash = HashRuleSet(rules);
            trackedAudit.Status = AuditJobStatus.Completed;
            trackedAudit.CompletedAt = DateTimeOffset.UtcNow;
            await completeDb.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            await using var failDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            var failedAudit = await failDb.AuditJobs.SingleAsync(x => x.Id == auditJobId, cancellationToken);
            failedAudit.Status = AuditJobStatus.Failed;
            failedAudit.ErrorMessage = exception.Message;
            failedAudit.CompletedAt = DateTimeOffset.UtcNow;
            await failDb.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private static decimal CalculateScore(IEnumerable<AuditFinding> findings)
    {
        var violatedRules = findings
            .GroupBy(x => x.RuleId)
            .Select(group => group.Max(x => x.Severity switch
            {
                RuleSeverity.Error => 8,
                RuleSeverity.Warning => 3,
                _ => 0
            }))
            .Sum();

        return Math.Max(0, 100 - violatedRules);
    }

    private static string HashRuleSet(IEnumerable<RuleDefinition> rules)
    {
        var canonical = string.Join('|', rules.Select(x => $"{x.RuleCode}:{x.ValidationKey}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
