using System.Text;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class TextCorrectionPrivacyTests
{
    private static readonly Guid AuditId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FindingId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid VersionOne = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid VersionTwo = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AdminA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AdminB = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Student = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid Reviewer = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
    private static readonly Guid UnitAdmin = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");

    [Fact]
    public void Classification_and_persistence_matrix_are_explicit_and_versioned()
    {
        Assert.Equal("text-correction-privacy/1.0", TextCorrectionPrivacyContract.ContractVersion);
        Assert.Equal(TextCorrectionPersistencePolicy.Prohibited,
            TextCorrectionPrivacyContract.Policy(TextCorrectionDataClass.SourceText).Persistence);
        Assert.Equal(TextCorrectionPersistencePolicy.TransientOnly,
            TextCorrectionPrivacyContract.Policy(TextCorrectionDataClass.SourceExcerpt).Persistence);
        Assert.Equal(TextCorrectionPersistencePolicy.AllowedMetadata,
            TextCorrectionPrivacyContract.Policy(TextCorrectionDataClass.AnchorEvidence).Persistence);
        Assert.Equal(TextCorrectionPersistencePolicy.PurposeSpecificBounded,
            TextCorrectionPrivacyContract.Policy(TextCorrectionDataClass.SuggestedReplacement).Persistence);
        Assert.Equal(TextCorrectionPersistencePolicy.PurposeSpecificAppendOnly,
            TextCorrectionPrivacyContract.Policy(TextCorrectionDataClass.AdminReplacement).Persistence);
        Assert.All(TextCorrectionPrivacyContract.Policies, value => Assert.True(value.RestrictedBusinessData));
    }

    [Fact]
    public void Replacement_validator_is_scalar_bounded_exact_unicode_and_fail_closed()
    {
        AssertAccepted("dianalisis", 10);
        AssertAccepted("dianalisis 😀", 12);
        AssertAccepted("e\u0301", 2);
        var decomposed = Valid("e\u0301");
        var composed = Valid("é");
        Assert.NotEqual(decomposed.Value, composed.Value);
        Assert.NotEqual(decomposed.Fingerprint, composed.Fingerprint);

        AssertRejected(null, CorrectionReplacementValidationFailure.Null);
        AssertRejected("", CorrectionReplacementValidationFailure.Empty);
        AssertRejected(" \u00a0 ", CorrectionReplacementValidationFailure.WhitespaceOnly);
        AssertRejected("a\0b", CorrectionReplacementValidationFailure.ControlCharacter);
        AssertRejected("a\tb", CorrectionReplacementValidationFailure.ControlCharacter);
        AssertRejected("a\nb", CorrectionReplacementValidationFailure.ParagraphBreak);
        AssertRejected("a\rb", CorrectionReplacementValidationFailure.ParagraphBreak);
        AssertRejected("a\u2029b", CorrectionReplacementValidationFailure.ParagraphBreak);
        AssertRejected("a\u202eb", CorrectionReplacementValidationFailure.BidiControl);
        AssertRejected("a\ud800b", CorrectionReplacementValidationFailure.InvalidUnicode);
        AssertAccepted(new string('a', TextCorrectionPrivacyContract.MaximumReplacementScalars),
            TextCorrectionPrivacyContract.MaximumReplacementScalars);
        AssertRejected(new string('a', TextCorrectionPrivacyContract.MaximumReplacementScalars + 1),
            CorrectionReplacementValidationFailure.TooLong);
    }

    [Fact]
    public async Task Authorized_transient_context_returns_exact_duplicate_and_split_run_without_mutation()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var roles = Roles();
        var service = Service(roles);
        var before = await DocxFixtureWorkspace.ComputeSha256Async(workspace.WorkingPath);

        var second = await AnchorForText(workspace, parsed, 0, "di analisa", 1);
        var duplicate = await service.MaterializeAsync(AdminA, workspace.WorkingPath, Request(second, null), CancellationToken.None);
        Assert.Equal(ExactTextTargetStatus.Exact, duplicate.Status);
        Assert.Equal("di analisa", duplicate.Context!.TargetText);
        Assert.Contains("Hasil di analisa kembali", duplicate.Context.Context, StringComparison.Ordinal);
        Assert.Null(duplicate.Context.PageNumber);

        var split = await AnchorForText(workspace, parsed, 4, "di analisa", 0);
        var splitResult = await service.MaterializeAsync(AdminB, workspace.WorkingPath, Request(split, 23), CancellationToken.None);
        Assert.Equal("di analisa", splitResult.Context!.TargetText);
        Assert.Equal(23, splitResult.Context.PageNumber);
        Assert.InRange(splitResult.Context.TargetText.EnumerateRunes().Count()
            + splitResult.Context.Context.EnumerateRunes().Count(), 1,
            TextCorrectionPrivacyContract.MaximumTransientPayloadScalars);
        Assert.Equal(before, await DocxFixtureWorkspace.ComputeSha256Async(workspace.WorkingPath));
    }

    [Fact]
    public async Task Context_fails_closed_for_stale_unsupported_and_cross_version_evidence()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var service = Service(Roles());
        var anchor = await AnchorForText(workspace, parsed, 1, "di analisa", 0);

        var v2 = await service.MaterializeAsync(AdminA, workspace.WorkingPath,
            Request(anchor, null) with { DocumentVersionId = VersionTwo }, CancellationToken.None);
        Assert.Equal(ExactTextTargetStatus.Stale, v2.Status);
        Assert.Equal(TextCorrectionPrivacyContract.AnchorStaleCode, v2.SafeFailureCode);

        var badSha = await service.MaterializeAsync(AdminA, workspace.WorkingPath,
            Request(anchor, null) with { SourceSha256 = new string('0', 64) }, CancellationToken.None);
        Assert.Equal(ExactTextTargetStatus.Stale, badSha.Status);

        var wrongSourceAnchor = anchor with { SourceSha256 = new string('0', 64) };
        var sourceMismatch = await service.MaterializeAsync(AdminA, workspace.WorkingPath,
            Request(wrongSourceAnchor, null), CancellationToken.None);
        Assert.Equal(ExactTextTargetStatus.Stale, sourceMismatch.Status);
        Assert.Equal(TextCorrectionPrivacyContract.AnchorStaleCode, sourceMismatch.SafeFailureCode);

        var fieldLocation = parsed.Paragraphs[7].Location!;
        var fieldAnchor = anchor with { ParagraphLocation = fieldLocation, Start = 0, Length = 10 };
        var unsupported = await service.MaterializeAsync(AdminA, workspace.WorkingPath,
            Request(fieldAnchor, null), CancellationToken.None);
        Assert.Equal(ExactTextTargetStatus.Unsupported, unsupported.Status);
        Assert.Equal(TextCorrectionPrivacyContract.AnchorUnsupportedCode, unsupported.SafeFailureCode);
        Assert.Null(unsupported.Context);
    }

    [Fact]
    public async Task Only_exact_authoritative_database_role_admin_has_shared_access()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var roles = Roles();
        var service = Service(roles);
        var anchor = await AnchorForText(workspace, parsed, 1, "di analisa", 0);
        var request = Request(anchor, null);

        Assert.Equal(ExactTextTargetStatus.Exact,
            (await service.MaterializeAsync(AdminA, workspace.WorkingPath, request, CancellationToken.None)).Status);
        Assert.Equal(ExactTextTargetStatus.Exact,
            (await service.MaterializeAsync(AdminB, workspace.WorkingPath, request, CancellationToken.None)).Status);
        await Assert.ThrowsAsync<InternalAdminAuthorizationException>(() =>
            service.MaterializeAsync(Student, workspace.WorkingPath, request, CancellationToken.None));
        await Assert.ThrowsAsync<InternalAdminAuthorizationException>(() =>
            service.MaterializeAsync(Reviewer, workspace.WorkingPath, request, CancellationToken.None));
        await Assert.ThrowsAsync<InternalAdminAuthorizationException>(() =>
            service.MaterializeAsync(UnitAdmin, workspace.WorkingPath, request, CancellationToken.None));
        await Assert.ThrowsAsync<InternalAdminAuthorizationException>(() =>
            service.MaterializeAsync(Guid.NewGuid(), workspace.WorkingPath, request, CancellationToken.None));
        Assert.DoesNotContain("Claim", typeof(TextCorrectionContextMaterializationService)
            .GetMethod(nameof(TextCorrectionContextMaterializationService.MaterializeAsync))!.GetParameters()
            .Select(value => value.ParameterType.Name));
    }

    [Fact]
    public void Proposal_and_admin_intent_identities_are_deterministic_version_bound_and_text_safe()
    {
        var replacement = Valid("dianalisis");
        var proposalA = TextCorrectionPrivacyContract.ProposalIdentity(AuditId, FindingId, VersionOne,
            new string('a', 64), "provider", "1.0", replacement);
        var proposalB = TextCorrectionPrivacyContract.ProposalIdentity(AuditId, FindingId, VersionOne,
            new string('a', 64), "provider", "1.0", replacement);
        var proposalV2 = TextCorrectionPrivacyContract.ProposalIdentity(AuditId, FindingId, VersionTwo,
            new string('a', 64), "provider", "1.0", replacement);
        var intent = TextCorrectionPrivacyContract.AdminIntentIdentity(FindingId, VersionOne,
            new string('a', 64), AdminA, Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), replacement);

        Assert.Equal(proposalA, proposalB);
        Assert.NotEqual(proposalA, proposalV2);
        Assert.DoesNotContain("dianalisis", proposalA, StringComparison.Ordinal);
        Assert.DoesNotContain("dianalisis", intent, StringComparison.Ordinal);
        Assert.Equal(64, proposalA.Length);
        Assert.Contains("REDACTED", replacement.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_persistence_logs_routes_and_errors_cannot_receive_correction_content()
    {
        var root = RepositoryRoot();
        var correction = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.Application", "TextCorrectionPrivacyContracts.cs"));
        var domain = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.Domain", "Entities.cs"));
        var review = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.Application", "FindingReviewContracts.cs"));
        var resolution = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.Application", "FindingResolutionContracts.cs"));
        var api = File.ReadAllText(Path.Combine(root, "backend", "services", "Ppki.Api", "Program.cs"));
        var anchor = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "ExactTextAnchors.cs"));
        var parser = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "OpenXmlDocxParser.cs"));

        Assert.DoesNotContain("TextCorrection", domain, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceExcerpt", domain, StringComparison.Ordinal);
        Assert.DoesNotContain("SuggestedReplacement", review, StringComparison.Ordinal);
        Assert.DoesNotContain("AdminReplacement", review, StringComparison.Ordinal);
        Assert.DoesNotContain("TextCorrection", resolution, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPost(\"/correction", api, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StorageKey", correction, StringComparison.Ordinal);
        Assert.DoesNotContain("ObjectKey", correction, StringComparison.Ordinal);
        Assert.DoesNotContain("ILogger", correction, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer", correction, StringComparison.Ordinal);
        Assert.DoesNotContain("ActualValueJson", correction, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpectedValueJson", correction, StringComparison.Ordinal);
        foreach (var source in new[] { correction, anchor })
            foreach (var forbidden in new[] { ".Replace(", "Regex.Replace", ".IndexOf(", "Levenshtein", "Similarity" })
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        Assert.Contains("public const string SchemaVersion = \"4.0\"", parser, StringComparison.Ordinal);

        var persistedFindingProperties = typeof(AuditFinding).GetProperties().Select(value => value.Name).ToArray();
        Assert.DoesNotContain(persistedFindingProperties, value => value.Contains("SourceText", StringComparison.Ordinal)
            || value.Contains("Excerpt", StringComparison.Ordinal) || value.Contains("Replacement", StringComparison.Ordinal));
        Assert.All(new[]
        {
            TextCorrectionPrivacyContract.ReplacementInvalidCode,
            TextCorrectionPrivacyContract.AnchorStaleCode,
            TextCorrectionPrivacyContract.AnchorUnsupportedCode,
            TextCorrectionPrivacyContract.ContextUnavailableCode,
            TextCorrectionPrivacyContract.EvidenceConflictCode
        }, value => Assert.Matches("^[a-z-]+$", value));
    }

    [Fact]
    public void Transient_content_types_redact_automatic_string_rendering()
    {
        var context = new TextCorrectionContext(AuditId, FindingId, VersionOne, new string('a', 64),
            "di analisa", "Data di analisa.", false, false, 23);
        Assert.DoesNotContain("di analisa", context.ToString(), StringComparison.Ordinal);
        Assert.Contains("REDACTED", context.ToString(), StringComparison.Ordinal);
    }

    private static Dictionary<Guid, UserRole> Roles() => new()
    {
        [AdminA] = UserRole.PPKIAdmin,
        [AdminB] = UserRole.PPKIAdmin,
        [Student] = UserRole.Student,
        [Reviewer] = UserRole.Reviewer,
        [UnitAdmin] = UserRole.UnitAdmin
    };

    private static TextCorrectionContextMaterializationService Service(IReadOnlyDictionary<Guid, UserRole> roles) =>
        new(new FakeAuthorization(roles), new ExactTextAnchorMaterializer());

    private static TextCorrectionContextRequest Request(ExactTextAnchor anchor, int? page) =>
        new(AuditId, FindingId, anchor.DocumentVersionId, anchor.SourceSha256, anchor, page);

    private static async Task<ExactTextAnchor> AnchorForText(DocxFixtureWorkspace workspace,
        ParsedDocument parsed, int paragraphIndex, string target, int occurrence)
    {
        var paragraph = parsed.Paragraphs[paragraphIndex].Text;
        var utf16Index = -1;
        var searchFrom = 0;
        for (var current = 0; current <= occurrence; current++)
        {
            utf16Index = paragraph.IndexOf(target, searchFrom, StringComparison.Ordinal);
            Assert.True(utf16Index >= 0);
            searchFrom = utf16Index + target.Length;
        }
        var result = await new ExactTextAnchorMaterializer().BuildAsync(workspace.WorkingPath, VersionOne,
            workspace.OriginalChecksum, paragraphIndex, paragraph[..utf16Index].EnumerateRunes().Count(),
            target.EnumerateRunes().Count());
        Assert.Equal(ExactTextTargetStatus.Exact, result.Status);
        return result.Anchor!;
    }

    private static ValidatedCorrectionReplacement Valid(string value)
    {
        Assert.True(TextCorrectionPrivacyContract.TryValidateReplacement(value, out var replacement, out var failure));
        Assert.Equal(CorrectionReplacementValidationFailure.None, failure);
        return replacement!;
    }

    private static void AssertAccepted(string value, int scalarLength)
    {
        var replacement = Valid(value);
        Assert.Equal(scalarLength, replacement.ScalarLength);
        Assert.Equal(value, replacement.Value);
    }

    private static void AssertRejected(string? value, CorrectionReplacementValidationFailure expected)
    {
        Assert.False(TextCorrectionPrivacyContract.TryValidateReplacement(value, out var replacement, out var failure));
        Assert.Null(replacement);
        Assert.Equal(expected, failure);
    }

    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null; candidate = candidate.Parent)
            if (File.Exists(Path.Combine(candidate.FullName, "AGENTS.md"))) return candidate.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class FakeAuthorization(IReadOnlyDictionary<Guid, UserRole> roles) : IInternalAdminAuthorizationService
    {
        public Task<UserRole?> GetAuthoritativeRoleAsync(Guid actorUserId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(roles.TryGetValue(actorUserId, out var role) ? (UserRole?)role : null);
        }

        public async Task RequirePpkiAdminAsync(Guid actorUserId, CancellationToken cancellationToken)
        {
            if (!await IsPpkiAdminAsync(actorUserId, cancellationToken)) throw new InternalAdminAuthorizationException();
        }

        public async Task<bool> IsPpkiAdminAsync(Guid actorUserId, CancellationToken cancellationToken) =>
            await GetAuthoritativeRoleAsync(actorUserId, cancellationToken) == UserRole.PPKIAdmin;
    }
}
