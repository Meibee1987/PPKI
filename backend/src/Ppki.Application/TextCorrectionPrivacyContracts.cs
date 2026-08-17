using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.Application;

public enum TextCorrectionDataClass
{
    SourceText,
    SourceExcerpt,
    SuggestedReplacement,
    AdminReplacement,
    AnchorEvidence,
    DerivedMetadata
}

public enum TextCorrectionPersistencePolicy
{
    Prohibited,
    TransientOnly,
    PurposeSpecificBounded,
    PurposeSpecificAppendOnly,
    AllowedMetadata
}

public sealed record TextCorrectionDataPolicy(
    TextCorrectionDataClass DataClass,
    TextCorrectionPersistencePolicy Persistence,
    bool RestrictedBusinessData,
    string RuleCode);

public enum CorrectionReplacementValidationFailure
{
    None,
    Null,
    Empty,
    WhitespaceOnly,
    TooLong,
    InvalidUnicode,
    ControlCharacter,
    ParagraphBreak,
    BidiControl
}

public sealed class ValidatedCorrectionReplacement
{
    internal ValidatedCorrectionReplacement(string value, int scalarLength, string fingerprint)
    {
        Value = value;
        ScalarLength = scalarLength;
        Fingerprint = fingerprint;
    }

    public string Value { get; }
    public int ScalarLength { get; }
    public string Fingerprint { get; }

    public override string ToString() => "ValidatedCorrectionReplacement(Content=[REDACTED])";
}

public static class TextCorrectionPrivacyContract
{
    public const string ContractVersion = "text-correction-privacy/1.0";
    public const int MaximumReplacementScalars = 256;
    public const int MaximumTargetScalars = 256;
    public const int MaximumContextScalars = 512;
    public const int MaximumTransientPayloadScalars = 1_024;
    public const string ReplacementInvalidCode = "correction-replacement-invalid";
    public const string AnchorStaleCode = "correction-anchor-stale";
    public const string AnchorUnsupportedCode = "correction-anchor-unsupported";
    public const string ContextUnavailableCode = "correction-context-unavailable";
    public const string EvidenceConflictCode = "correction-evidence-conflict";

    public static IReadOnlyList<TextCorrectionDataPolicy> Policies { get; } =
    [
        new(TextCorrectionDataClass.SourceText, TextCorrectionPersistencePolicy.Prohibited, true, "source-canonical-docx-only"),
        new(TextCorrectionDataClass.SourceExcerpt, TextCorrectionPersistencePolicy.TransientOnly, true, "authorized-bounded-read-only"),
        new(TextCorrectionDataClass.SuggestedReplacement, TextCorrectionPersistencePolicy.PurposeSpecificBounded, true, "future-proposal-evidence-only"),
        new(TextCorrectionDataClass.AdminReplacement, TextCorrectionPersistencePolicy.PurposeSpecificAppendOnly, true, "future-admin-intent-only"),
        new(TextCorrectionDataClass.AnchorEvidence, TextCorrectionPersistencePolicy.AllowedMetadata, true, "hash-coordinate-span-only"),
        new(TextCorrectionDataClass.DerivedMetadata, TextCorrectionPersistencePolicy.AllowedMetadata, true, "bounded-safe-metadata-only")
    ];

    public static TextCorrectionDataPolicy Policy(TextCorrectionDataClass dataClass) =>
        Policies.Single(value => value.DataClass == dataClass);

    public static bool TryValidateReplacement(string? value, out ValidatedCorrectionReplacement? replacement,
        out CorrectionReplacementValidationFailure failure)
    {
        replacement = null;
        failure = CorrectionReplacementValidationFailure.None;
        if (value is null) return Fail(CorrectionReplacementValidationFailure.Null, out failure);
        if (value.Length == 0) return Fail(CorrectionReplacementValidationFailure.Empty, out failure);

        var scalarCount = 0;
        var whitespaceOnly = true;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done)
                return Fail(CorrectionReplacementValidationFailure.InvalidUnicode, out failure);
            scalarCount++;
            if (scalarCount > MaximumReplacementScalars)
                return Fail(CorrectionReplacementValidationFailure.TooLong, out failure);
            if (!Rune.IsWhiteSpace(rune)) whitespaceOnly = false;
            if (rune.Value is '\r' or '\n' or 0x2028 or 0x2029)
                return Fail(CorrectionReplacementValidationFailure.ParagraphBreak, out failure);
            if (IsBidiControl(rune.Value))
                return Fail(CorrectionReplacementValidationFailure.BidiControl, out failure);
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
                return Fail(CorrectionReplacementValidationFailure.ControlCharacter, out failure);
            remaining = remaining[consumed..];
        }
        if (whitespaceOnly) return Fail(CorrectionReplacementValidationFailure.WhitespaceOnly, out failure);

        replacement = new(value, scalarCount, Fingerprint("ppki:text-correction-replacement:v1", value));
        return true;
    }

    public static string ProposalIdentity(Guid auditId, Guid findingId, Guid documentVersionId,
        string anchorHash, string providerId, string providerVersion, ValidatedCorrectionReplacement replacement) =>
        Fingerprint("ppki:text-correction-proposal:v1", CanonicalFields(
            auditId.ToString("D"), findingId.ToString("D"), documentVersionId.ToString("D"),
            anchorHash, providerId, providerVersion, replacement.Fingerprint));

    public static string AdminIntentIdentity(Guid findingId, Guid documentVersionId, string anchorHash,
        Guid actorUserId, Guid idempotencyKey, ValidatedCorrectionReplacement replacement) =>
        Fingerprint("ppki:text-correction-admin-intent:v1", CanonicalFields(
            findingId.ToString("D"), documentVersionId.ToString("D"), anchorHash,
            actorUserId.ToString("D"), idempotencyKey.ToString("D"), replacement.Fingerprint));

    private static bool Fail(CorrectionReplacementValidationFailure value,
        out CorrectionReplacementValidationFailure failure)
    {
        failure = value;
        return false;
    }

    private static bool IsBidiControl(int value) => value is 0x061c or 0x200e or 0x200f
        or >= 0x202a and <= 0x202e or >= 0x2066 and <= 0x2069;

    private static string CanonicalFields(params string[] values) => string.Concat(values.Select(value =>
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        return bytes.ToString(CultureInfo.InvariantCulture) + ":" + value;
    }));

    private static string Fingerprint(string domain, string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalFields(domain, value))));
}

public sealed record TextCorrectionContextRequest(
    Guid AuditId,
    Guid FindingId,
    Guid DocumentVersionId,
    string SourceSha256,
    ExactTextAnchor Anchor,
    int? PageNumber);

public sealed class TextCorrectionContext
{
    public TextCorrectionContext(Guid auditId, Guid findingId, Guid documentVersionId,
        string anchorHash, string targetText, string context, bool prefixTruncated,
        bool suffixTruncated, int? pageNumber)
    {
        AuditId = auditId;
        FindingId = findingId;
        DocumentVersionId = documentVersionId;
        AnchorHash = anchorHash;
        TargetText = targetText;
        Context = context;
        PrefixTruncated = prefixTruncated;
        SuffixTruncated = suffixTruncated;
        PageNumber = pageNumber;
    }

    public Guid AuditId { get; }
    public Guid FindingId { get; }
    public Guid DocumentVersionId { get; }
    public string AnchorHash { get; }
    public string TargetText { get; }
    public string Context { get; }
    public bool PrefixTruncated { get; }
    public bool SuffixTruncated { get; }
    public int? PageNumber { get; }

    public override string ToString() => $"TextCorrectionContext(FindingId={FindingId:D},Content=[REDACTED])";
}

public sealed record TextCorrectionContextResult(
    ExactTextTargetStatus Status,
    string? SafeFailureCode,
    TextCorrectionContext? Context);

public interface ITextCorrectionContextMaterializationService
{
    Task<TextCorrectionContextResult> MaterializeAsync(Guid actorUserId, string sourceDocxPath,
        TextCorrectionContextRequest request, CancellationToken cancellationToken);
}

public sealed class TextCorrectionContextMaterializationService(
    IInternalAdminAuthorizationService authorization,
    ExactTextAnchorMaterializer materializer) : ITextCorrectionContextMaterializationService
{
    public async Task<TextCorrectionContextResult> MaterializeAsync(Guid actorUserId, string sourceDocxPath,
        TextCorrectionContextRequest request, CancellationToken cancellationToken)
    {
        await authorization.RequirePpkiAdminAsync(actorUserId, cancellationToken);
        if (request.DocumentVersionId != request.Anchor.DocumentVersionId
            || !string.Equals(request.SourceSha256, request.Anchor.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return new(ExactTextTargetStatus.Stale, TextCorrectionPrivacyContract.AnchorStaleCode, null);

        var excerpt = await materializer.MaterializeExcerptAsync(sourceDocxPath, request.DocumentVersionId,
            request.Anchor, TextCorrectionPrivacyContract.MaximumTargetScalars,
            TextCorrectionPrivacyContract.MaximumContextScalars, cancellationToken);
        if (excerpt.Status == ExactTextTargetStatus.Stale)
            return new(excerpt.Status, TextCorrectionPrivacyContract.AnchorStaleCode, null);
        if (excerpt.Status == ExactTextTargetStatus.Unsupported)
            return new(excerpt.Status, TextCorrectionPrivacyContract.AnchorUnsupportedCode, null);
        if (excerpt.TargetText is null || excerpt.Context is null)
            return new(ExactTextTargetStatus.Unsupported, TextCorrectionPrivacyContract.ContextUnavailableCode, null);
        var payloadScalars = excerpt.TargetText.EnumerateRunes().Count() + excerpt.Context.EnumerateRunes().Count();
        if (payloadScalars > TextCorrectionPrivacyContract.MaximumTransientPayloadScalars)
            return new(ExactTextTargetStatus.Unsupported, TextCorrectionPrivacyContract.ContextUnavailableCode, null);

        return new(ExactTextTargetStatus.Exact, null, new TextCorrectionContext(
            request.AuditId, request.FindingId, request.DocumentVersionId, request.Anchor.AnchorHash,
            excerpt.TargetText, excerpt.Context, excerpt.PrefixTruncated, excerpt.SuffixTruncated,
            request.PageNumber));
    }
}
