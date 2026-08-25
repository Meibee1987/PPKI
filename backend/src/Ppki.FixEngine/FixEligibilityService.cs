using Ppki.Application;
using Ppki.Domain;

namespace Ppki.FixEngine;

public sealed class FixEligibilityService(
    IRemediationCapabilityRegistry previewCapabilities,
    FixApplyCapabilityRegistry applyCapabilities) : IFixEligibilityService
{
    public FixEligibilityResult Evaluate(FixEligibilityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Finding);

        var finding = input.Finding;
        var requiresApproval = finding.FixMode == FixMode.Confirm;

        FixEligibilityResult Result(FixEligibilityStatus status, FixEligibilityReasonCode reason) =>
            new(finding.FindingId, finding.FixMode, input.Confidence, status, reason, requiresApproval);

        if (input.AuditId == Guid.Empty || input.SourceDocumentVersionId == Guid.Empty || finding.FindingId == Guid.Empty)
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.SourceContextInvalid);
        if (input.AuditStatus != AuditJobStatus.Completed)
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.AuditNotCompleted);
        if (finding.FindingState != FindingStatus.Open)
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.FindingNotOpen);
        if (input.ResolutionState is not (FindingResolutionState.Open or FindingResolutionState.VerifiedStillDetected))
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.ResolutionStateBlocksFix);
        if (input.ReviewState is not (FindingReviewState.NoReview or FindingReviewState.NeedsRevision))
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.ReviewStateBlocksFix);

        if (finding.FixMode == FixMode.Manual)
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.ManualFixMode);
        if (finding.FixMode == FixMode.Report)
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.ReportFixMode);
        if (finding.FixMode is not (FixMode.Auto or FixMode.Confirm))
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.FixModeUnsupported);

        if (string.IsNullOrWhiteSpace(finding.ValidationKey))
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.ValidationKeyUnsupported);
        if (!previewCapabilities.TryGet(finding.ValidationKey, out var capability))
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.FixerNotRegistered);
        if (!capability.DocumentMutationImplementationExists)
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.FixerNotRegistered);

        var availability = applyCapabilities.GetAvailability(capability.CapabilityId, capability.CapabilityVersion);
        if (availability == FixApplyProviderAvailability.NotRegistered)
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.FixerNotRegistered);
        if (availability == FixApplyProviderAvailability.VersionIncompatible)
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.FixerVersionIncompatible);

        try
        {
            if (!capability.Provider.TryCreate(finding, out _, out _))
                return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.FindingContractUnsupported);
        }
        catch (Exception)
        {
            return Result(FixEligibilityStatus.Ineligible, FixEligibilityReasonCode.FindingContractUnsupported);
        }

        return Result(FixEligibilityStatus.Eligible, FixEligibilityReasonCode.Eligible);
    }
}
