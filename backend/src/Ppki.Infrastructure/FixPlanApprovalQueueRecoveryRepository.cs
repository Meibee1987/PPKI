using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed class FixPlanApprovalQueueRecoveryRepository(
    IDbContextFactory<PpkiDbContext> dbFactory) : IFixPlanApprovalQueueRecoveryRepository
{
    public async Task<FixPlanApprovalQueueRecoveryCandidate?> LoadNextMissingAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await (from plan in db.FixPlans.AsNoTracking()
                      join snapshot in db.FixPlanApprovalSnapshots.AsNoTracking()
                          on plan.Id equals snapshot.FixPlanId
                      where plan.State == FixPlanLifecycleState.Approved
                          && !db.FixExecutionJobs.Any(execution =>
                              execution.AuditJobId == plan.SourceAuditJobId
                                  && execution.IdempotencyKey == plan.Id
                              || execution.SourceDocumentVersionId == plan.SourceDocumentVersionId
                                  && execution.PlanHash == snapshot.PlanHash)
                      orderby plan.ApprovedAt, plan.Id
                      select new FixPlanApprovalQueueRecoveryCandidate(plan, snapshot))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
