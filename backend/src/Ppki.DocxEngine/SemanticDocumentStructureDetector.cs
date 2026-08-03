using System.Globalization;
using System.Text;

namespace Ppki.DocxEngine;

public sealed class SemanticDocumentStructureDetector
{
    public const string CatalogVersion = "1.0";

    private static readonly IReadOnlyDictionary<string, SemanticSectionKind> Aliases =
        new Dictionary<string, SemanticSectionKind>(StringComparer.Ordinal)
        {
            ["HALAMAN JUDUL"] = SemanticSectionKind.TitlePage,
            ["LEMBAR PENGESAHAN"] = SemanticSectionKind.ApprovalPage,
            ["HALAMAN PENGESAHAN"] = SemanticSectionKind.ApprovalPage,
            ["PERNYATAAN"] = SemanticSectionKind.StatementPage,
            ["HALAMAN PERNYATAAN"] = SemanticSectionKind.StatementPage,
            ["ABSTRAK"] = SemanticSectionKind.AbstractIndonesian,
            ["ABSTRACT"] = SemanticSectionKind.AbstractEnglish,
            ["RINGKASAN"] = SemanticSectionKind.SummaryIndonesian,
            ["SUMMARY"] = SemanticSectionKind.SummaryEnglish,
            ["KATA PENGANTAR"] = SemanticSectionKind.Preface,
            ["PRAKATA"] = SemanticSectionKind.Preface,
            ["DAFTAR ISI"] = SemanticSectionKind.TableOfContents,
            ["TABLE OF CONTENTS"] = SemanticSectionKind.TableOfContents,
            ["DAFTAR TABEL"] = SemanticSectionKind.ListOfTables,
            ["LIST OF TABLES"] = SemanticSectionKind.ListOfTables,
            ["DAFTAR GAMBAR"] = SemanticSectionKind.ListOfFigures,
            ["LIST OF FIGURES"] = SemanticSectionKind.ListOfFigures,
            ["DAFTAR LAMPIRAN"] = SemanticSectionKind.ListOfAppendices,
            ["LIST OF APPENDICES"] = SemanticSectionKind.ListOfAppendices,
            ["PENDAHULUAN"] = SemanticSectionKind.Introduction,
            ["INTRODUCTION"] = SemanticSectionKind.Introduction,
            ["TINJAUAN PUSTAKA"] = SemanticSectionKind.LiteratureReview,
            ["LITERATURE REVIEW"] = SemanticSectionKind.LiteratureReview,
            ["METODE"] = SemanticSectionKind.Methods,
            ["METODOLOGI"] = SemanticSectionKind.Methods,
            ["METHODS"] = SemanticSectionKind.Methods,
            ["MATERIALS AND METHODS"] = SemanticSectionKind.Methods,
            ["HASIL"] = SemanticSectionKind.Results,
            ["RESULTS"] = SemanticSectionKind.Results,
            ["PEMBAHASAN"] = SemanticSectionKind.Discussion,
            ["DISCUSSION"] = SemanticSectionKind.Discussion,
            ["HASIL DAN PEMBAHASAN"] = SemanticSectionKind.ResultsAndDiscussion,
            ["RESULTS AND DISCUSSION"] = SemanticSectionKind.ResultsAndDiscussion,
            ["KESIMPULAN"] = SemanticSectionKind.Conclusion,
            ["CONCLUSION"] = SemanticSectionKind.Conclusion,
            ["SIMPULAN"] = SemanticSectionKind.Conclusion,
            ["SARAN"] = SemanticSectionKind.Recommendations,
            ["RECOMMENDATIONS"] = SemanticSectionKind.Recommendations,
            ["DAFTAR PUSTAKA"] = SemanticSectionKind.References,
            ["REFERENCES"] = SemanticSectionKind.References,
            ["PUSTAKA"] = SemanticSectionKind.References,
            ["LAMPIRAN"] = SemanticSectionKind.Appendices,
            ["APPENDICES"] = SemanticSectionKind.Appendices,
            ["RIWAYAT HIDUP"] = SemanticSectionKind.Biography,
            ["BIOGRAPHY"] = SemanticSectionKind.Biography
        };

    private readonly int _maximumSections;
    private readonly int _maximumAliases;
    private readonly int _maximumHeadingLength;
    private readonly int _maximumSystematicsEntries;

    public SemanticDocumentStructureDetector(
        int maximumSections,
        int maximumAliases,
        int maximumHeadingLength,
        int maximumSystematicsEntries)
    {
        if (maximumSections <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSections));
        if (maximumAliases <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAliases));
        if (maximumHeadingLength <= 0) throw new ArgumentOutOfRangeException(nameof(maximumHeadingLength));
        if (maximumSystematicsEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSystematicsEntries));
        _maximumSections = maximumSections;
        _maximumAliases = maximumAliases;
        _maximumHeadingLength = maximumHeadingLength;
        _maximumSystematicsEntries = maximumSystematicsEntries;
        if (Aliases.Count > _maximumAliases) throw Limit("semantic-section-aliases");
    }

    public SemanticSectionDetectionResult Detect(
        IReadOnlyList<ParsedParagraph> paragraphs,
        IReadOnlyList<ParsedBodyElement> bodyElements,
        IReadOnlyList<ParsedHeading> headings)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        ArgumentNullException.ThrowIfNull(bodyElements);
        ArgumentNullException.ThrowIfNull(headings);

        var paragraphLookup = paragraphs.ToDictionary(value => value.Index);
        var bodyContent = bodyElements
            .Where(value => value.Kind is ParsedBodyElementKind.Paragraph or ParsedBodyElementKind.Table)
            .OrderBy(value => value.Index).ToArray();
        var diagnostics = new List<ParserDiagnostic>();
        var excluded = new List<ExcludedSemanticHeadingCandidate>();
        var candidates = new List<Candidate>();

        foreach (var heading in headings.OrderBy(value => value.Order))
        {
            if (!paragraphLookup.TryGetValue(heading.ParagraphIndex, out var paragraph)
                || heading.Location.PartKind != DocumentPartKind.MainDocument) continue;
            if (paragraph.IsInTable)
            {
                excluded.Add(new(heading.Index, heading.Location, SemanticSectionEvidenceKind.ExcludedTableHeading));
                continue;
            }
            if (candidates.Count >= _maximumSections) throw Limit("semantic-sections");

            var evidence = StructuralEvidence(heading);
            SemanticSectionKind kind;
            SemanticClassificationState state;
            SemanticClassificationBasis basis;
            var codes = new List<string>();
            var tooLong = paragraph.Text.Length > _maximumHeadingLength;
            var normalized = tooLong ? null : Normalize(paragraph.Text, paragraph.EffectiveNumbering?.Label?.Value);
            if (tooLong)
            {
                kind = UnknownKind(SemanticSectionZone.Unknown);
                state = SemanticClassificationState.Unresolved;
                basis = SemanticClassificationBasis.StructuralHeading;
                AddDiagnostic(diagnostics, codes, "semantic-heading-too-long", heading.Location);
            }
            else if (normalized is not null && Aliases.TryGetValue(normalized, out kind))
            {
                state = SemanticClassificationState.Confirmed;
                basis = SemanticClassificationBasis.ExactAlias;
                evidence.Add(new(SemanticSectionEvidenceKind.ExactHeadingAlias, heading.Level));
            }
            else if (normalized is not null && IsChapterMarker(normalized))
            {
                kind = SemanticSectionKind.Chapter;
                state = heading.Level == 1 ? SemanticClassificationState.Confirmed : SemanticClassificationState.Candidate;
                basis = SemanticClassificationBasis.ChapterMarker;
                evidence.Add(new(SemanticSectionEvidenceKind.ChapterMarker, heading.Level, heading.Numbering?.Level));
                if (state == SemanticClassificationState.Candidate)
                    AddDiagnostic(diagnostics, codes, "chapter-classification-ambiguous", heading.Location);
            }
            else
            {
                kind = UnknownKind(SemanticSectionZone.Unknown);
                state = heading.Level == 1 ? SemanticClassificationState.Candidate : SemanticClassificationState.Unresolved;
                basis = SemanticClassificationBasis.StructuralHeading;
            }
            candidates.Add(new(heading, kind, state, basis, evidence, codes,
                basis is SemanticClassificationBasis.ExactAlias or SemanticClassificationBasis.ChapterMarker));
        }

        AssignZones(candidates, diagnostics);
        AssignUnknownKinds(candidates);
        AssignParents(candidates, diagnostics);
        AssignRanges(candidates, bodyContent, paragraphs, diagnostics);
        AssignDuplicates(candidates, diagnostics);

        var sections = candidates.Select((value, index) => value.ToSection(index)).ToArray();
        var abstracts = CreateAbstractDescriptors(sections, paragraphs);
        var systematics = CreateSystematics(sections, diagnostics);
        return new(
            new(CatalogVersion, sections, abstracts, excluded.ToArray()),
            systematics,
            diagnostics.ToArray());
    }

    private static List<SemanticSectionEvidence> StructuralEvidence(ParsedHeading heading)
    {
        var result = new List<SemanticSectionEvidence>
        {
            new(SemanticSectionEvidenceKind.StructuralHeading, heading.Level),
            new(SemanticSectionEvidenceKind.HeadingLevel, heading.Level),
            new(SemanticSectionEvidenceKind.BodyOrder, heading.Level)
        };
        foreach (var item in heading.Evidence)
        {
            var kind = item.Kind switch
            {
                HeadingEvidenceKind.DirectOutlineLevel => SemanticSectionEvidenceKind.DirectOutline,
                HeadingEvidenceKind.ParagraphStyleOutlineLevel => SemanticSectionEvidenceKind.StyleOutline,
                HeadingEvidenceKind.BasedOnHeadingStyle => SemanticSectionEvidenceKind.BasedOnHeadingStyle,
                HeadingEvidenceKind.NumberingLevelLinkedToHeadingStyle => SemanticSectionEvidenceKind.NumberingLinkedHeadingStyle,
                _ => (SemanticSectionEvidenceKind?)null
            };
            if (kind is not null && !result.Any(value => value.Kind == kind))
                result.Add(new(kind.Value, heading.Level, item.NumberingLevel));
        }
        if (heading.Numbering?.State == NumberingResolutionState.Resolved)
            result.Add(new(SemanticSectionEvidenceKind.ResolvedNumbering, heading.Level, heading.Numbering.Level));
        if (heading.StartsNewSection)
            result.Add(new(SemanticSectionEvidenceKind.StartsNewOpenXmlSection, heading.Level));
        return result;
    }

    private static string Normalize(string text, string? numberingLabel)
    {
        var value = text.Normalize(NormalizationForm.FormKC).Trim();
        if (!string.IsNullOrWhiteSpace(numberingLabel) && value.StartsWith(numberingLabel, StringComparison.Ordinal))
            value = value[numberingLabel.Length..].TrimStart();
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { pendingSpace = builder.Length > 0; continue; }
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation)
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(rune.ToString().ToUpperInvariant());
        }
        return builder.ToString().Trim();
    }

    private static bool IsChapterMarker(string normalized)
    {
        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is not ("BAB" or "CHAPTER")) return false;
        return IsPositiveDecimal(parts[1]) || IsRoman(parts[1]);
    }

    private static bool IsPositiveDecimal(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;

    private static bool IsRoman(string value)
    {
        if (value.Length is < 1 or > 12) return false;
        foreach (var character in value)
            if (character is not ('I' or 'V' or 'X' or 'L' or 'C' or 'D' or 'M')) return false;
        return true;
    }

    private static void AssignZones(List<Candidate> candidates, List<ParserDiagnostic> diagnostics)
    {
        var firstMain = candidates.FindIndex(value => KindZone(value.Kind) == SemanticSectionZone.MainMatter
            && value.State == SemanticClassificationState.Confirmed);
        var firstBack = candidates.FindIndex(value => KindZone(value.Kind) == SemanticSectionZone.BackMatter);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var intrinsic = candidate.HasSemanticKind ? KindZone(candidate.Kind) : SemanticSectionZone.Unknown;
            var observed = firstBack >= 0 && index >= firstBack
                ? SemanticSectionZone.BackMatter
                : firstMain >= 0 && index >= firstMain
                    ? SemanticSectionZone.MainMatter
                    : firstMain >= 0 && index < firstMain
                        ? SemanticSectionZone.FrontMatter
                        : intrinsic;

            if (intrinsic != SemanticSectionZone.Unknown && observed != intrinsic)
            {
                candidate.Zone = SemanticSectionZone.Unknown;
                candidate.State = SemanticClassificationState.Ambiguous;
                candidate.Basis = SemanticClassificationBasis.ConflictingEvidence;
                AddDiagnostic(diagnostics, candidate.Codes, "semantic-zone-regression", candidate.Heading.Location);
                AddDiagnostic(diagnostics, candidate.Codes, "semantic-section-ambiguous", candidate.Heading.Location);
            }
            else
            {
                candidate.Zone = observed;
                if (observed != SemanticSectionZone.Unknown)
                    candidate.Evidence.Add(new(SemanticSectionEvidenceKind.ZoneBoundary, candidate.Heading.Level));
            }
        }
    }

    private static void AssignUnknownKinds(List<Candidate> candidates)
    {
        foreach (var candidate in candidates.Where(value => !value.HasSemanticKind))
        {
            candidate.Kind = UnknownKind(candidate.Zone);
        }
    }

    private static void AssignParents(List<Candidate> candidates, List<ParserDiagnostic> diagnostics)
    {
        var stack = new List<int>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var level = candidates[index].Heading.Level;
            while (stack.Count > 0 && candidates[stack[^1]].Heading.Level >= level) stack.RemoveAt(stack.Count - 1);
            candidates[index].ParentIndex = stack.Count == 0 ? null : stack[^1];
            if (candidates[index].Kind == SemanticSectionKind.Chapter && candidates[index].ParentIndex is not null)
            {
                candidates[index].State = SemanticClassificationState.Ambiguous;
                AddDiagnostic(diagnostics, candidates[index].Codes, "chapter-classification-ambiguous", candidates[index].Heading.Location);
            }
            stack.Add(index);
        }
    }

    private static void AssignRanges(
        List<Candidate> candidates,
        IReadOnlyList<ParsedBodyElement> bodyContent,
        IReadOnlyList<ParsedParagraph> paragraphs,
        List<ParserDiagnostic> diagnostics)
    {
        var bodyEnd = bodyContent.Count == 0 ? 0 : bodyContent[^1].Index;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var start = candidate.Heading.Location.BodyElementIndex ?? 0;
            var boundary = bodyEnd + 1;
            for (var next = index + 1; next < candidates.Count; next++)
            {
                if (IsAbstract(candidate.Kind) || candidates[next].Heading.Level <= candidate.Heading.Level)
                {
                    boundary = candidates[next].Heading.Location.BodyElementIndex ?? boundary;
                    break;
                }
            }
            var content = bodyContent.Where(value => value.Index > start && value.Index < boundary).ToArray();
            var contentStart = content.FirstOrDefault()?.Location;
            var end = content.LastOrDefault()?.Location ?? candidate.Heading.Location;
            var paragraphCount = paragraphs.Count(value => value.Location?.PartKind == DocumentPartKind.MainDocument
                && value.Location.BodyElementIndex > start && value.Location.BodyElementIndex < boundary);
            candidate.Range = new(candidate.Heading.Location, contentStart, end, start,
                content.Length == 0 ? start : content[^1].Index, paragraphCount);
            if (content.Length == 0)
            {
                AddDiagnostic(diagnostics, candidate.Codes, "semantic-section-empty", candidate.Heading.Location);
                if (IsAbstract(candidate.Kind))
                    AddDiagnostic(diagnostics, candidate.Codes, "abstract-section-empty", candidate.Heading.Location);
            }
            if (boundary <= start)
            {
                AddDiagnostic(diagnostics, candidate.Codes, "semantic-section-boundary-unresolved", candidate.Heading.Location);
                AddDiagnostic(diagnostics, candidate.Codes, "semantic-section-overlap", candidate.Heading.Location);
            }
        }
    }

    private static void AssignDuplicates(List<Candidate> candidates, List<ParserDiagnostic> diagnostics)
    {
        var group = 0;
        foreach (var duplicate in candidates.Select((value, index) => (value, index))
                     .Where(item => item.value.Kind is not (SemanticSectionKind.Chapter
                         or SemanticSectionKind.OtherFrontMatter
                         or SemanticSectionKind.OtherMainMatter
                         or SemanticSectionKind.OtherBackMatter))
                     .GroupBy(item => item.value.Kind)
                     .Where(items => items.Count() > 1)
                     .OrderBy(items => items.Min(item => item.index)))
        {
            group++;
            foreach (var item in duplicate.OrderBy(value => value.index))
            {
                item.value.DuplicateGroup = group;
                AddDiagnostic(diagnostics, item.value.Codes, "semantic-section-duplicate", item.value.Heading.Location);
                if (IsAbstract(item.value.Kind))
                    AddDiagnostic(diagnostics, item.value.Codes, "abstract-section-duplicate", item.value.Heading.Location);
            }
        }
    }

    private static IReadOnlyList<AbstractSectionDescriptor> CreateAbstractDescriptors(
        IReadOnlyList<SemanticDocumentSection> sections,
        IReadOnlyList<ParsedParagraph> paragraphs) => sections.Where(value => IsAbstract(value.Kind)).Select(value => new AbstractSectionDescriptor(
            value.Index,
            value.Kind,
            value.Kind is SemanticSectionKind.AbstractIndonesian or SemanticSectionKind.SummaryIndonesian
                ? SemanticSectionLanguage.Indonesian : SemanticSectionLanguage.English,
            value.HeadingLocation,
            value.Range.ContentStartLocation,
            value.Range.EndLocation,
            value.Range.ParagraphCount,
            FindKeywordParagraph(value, paragraphs),
            value.Evidence,
            value.DiagnosticCodes)).ToArray();

    private static DocumentElementLocation? FindKeywordParagraph(
        SemanticDocumentSection section,
        IReadOnlyList<ParsedParagraph> paragraphs)
    {
        foreach (var paragraph in paragraphs.Where(value => !value.IsInTable
                     && value.Location?.BodyElementIndex > section.Range.StartBodyElementIndex
                     && value.Location.BodyElementIndex <= section.Range.EndBodyElementIndex))
        {
            var text = paragraph.Text.Normalize(NormalizationForm.FormKC).TrimStart();
            var separator = text.IndexOf(':');
            if (separator is <= 0 or > 16) continue;
            var prefix = text[..separator].Trim().ToUpperInvariant();
            if (prefix is "KATA KUNCI" or "KEYWORDS") return paragraph.Location;
        }
        return null;
    }

    private DocumentSystematics CreateSystematics(
        IReadOnlyList<SemanticDocumentSection> sections,
        IReadOnlyList<ParserDiagnostic> diagnostics)
    {
        if (sections.Count > _maximumSystematicsEntries) throw Limit("document-systematics-entries");
        var entries = sections.Select((section, ordinal) => new DocumentSystematicsEntry(
            ordinal,
            section.Index,
            section.Kind,
            section.Zone,
            section.Range.StartLocation,
            section.Range.EndLocation,
            section.HeadingLevel,
            section.ParentSectionIndex,
            section.ClassificationState,
            section.Evidence.Select(value => value.Kind).Distinct().ToArray(),
            section.DuplicateGroup)).ToArray();
        return new(
            entries,
            sections.FirstOrDefault(value => value.Zone == SemanticSectionZone.FrontMatter)?.Range.StartLocation,
            sections.FirstOrDefault(value => value.Zone == SemanticSectionZone.MainMatter)?.Range.StartLocation,
            sections.FirstOrDefault(value => value.Zone == SemanticSectionZone.BackMatter)?.Range.StartLocation,
            sections.Count(value => value.Kind == SemanticSectionKind.Chapter && value.ClassificationState == SemanticClassificationState.Confirmed),
            sections.Where(value => IsAbstract(value.Kind)).Select(value => value.Index).ToArray(),
            sections.Where(value => value.DuplicateGroup is not null).Select(value => value.Index).ToArray(),
            sections.Where(value => value.ClassificationState == SemanticClassificationState.Ambiguous).Select(value => value.Index).ToArray(),
            sections.Where(value => value.ClassificationState is SemanticClassificationState.Candidate or SemanticClassificationState.Unresolved
                && value.Kind is SemanticSectionKind.OtherFrontMatter or SemanticSectionKind.OtherMainMatter or SemanticSectionKind.OtherBackMatter)
                .Select(value => value.HeadingIndex).ToArray(),
            diagnostics.Select(value => value.Code).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static SemanticSectionZone KindZone(SemanticSectionKind kind) => kind switch
    {
        <= SemanticSectionKind.OtherFrontMatter => SemanticSectionZone.FrontMatter,
        <= SemanticSectionKind.OtherMainMatter => SemanticSectionZone.MainMatter,
        _ => SemanticSectionZone.BackMatter
    };

    private static SemanticSectionKind UnknownKind(SemanticSectionZone zone) => zone switch
    {
        SemanticSectionZone.FrontMatter => SemanticSectionKind.OtherFrontMatter,
        SemanticSectionZone.MainMatter => SemanticSectionKind.OtherMainMatter,
        SemanticSectionZone.BackMatter => SemanticSectionKind.OtherBackMatter,
        _ => SemanticSectionKind.OtherMainMatter
    };

    private static bool IsAbstract(SemanticSectionKind kind) => kind is
        SemanticSectionKind.AbstractIndonesian or SemanticSectionKind.AbstractEnglish
        or SemanticSectionKind.SummaryIndonesian or SemanticSectionKind.SummaryEnglish;

    private static SemanticNumberingCategory NumberingCategory(ParsedHeading heading) =>
        heading.Numbering?.State switch
        {
            NumberingResolutionState.Resolved => SemanticNumberingCategory.ResolvedNumbering,
            NumberingResolutionState.Unresolved => SemanticNumberingCategory.UnresolvedNumbering,
            _ => SemanticNumberingCategory.None
        };

    private static void AddDiagnostic(
        ICollection<ParserDiagnostic> diagnostics,
        ICollection<string> sectionCodes,
        string code,
        DocumentElementLocation location)
    {
        if (sectionCodes.Contains(code, StringComparer.Ordinal)) return;
        sectionCodes.Add(code);
        diagnostics.Add(new(code, ParserDiagnosticSeverity.Warning, $"parser.{code.Replace('-', '_')}", location));
    }

    private static DocxParserException Limit(string resource) =>
        new("resource-limit-exceeded", $"DOCX parser resource limit exceeded: {resource}.");

    private sealed class Candidate(
        ParsedHeading heading,
        SemanticSectionKind kind,
        SemanticClassificationState state,
        SemanticClassificationBasis basis,
        List<SemanticSectionEvidence> evidence,
        List<string> codes,
        bool hasSemanticKind)
    {
        public ParsedHeading Heading { get; } = heading;
        public SemanticSectionKind Kind { get; set; } = kind;
        public SemanticSectionZone Zone { get; set; } = SemanticSectionZone.Unknown;
        public SemanticClassificationState State { get; set; } = state;
        public SemanticClassificationBasis Basis { get; set; } = basis;
        public List<SemanticSectionEvidence> Evidence { get; } = evidence;
        public List<string> Codes { get; } = codes;
        public bool HasSemanticKind { get; } = hasSemanticKind;
        public int? ParentIndex { get; set; }
        public SemanticSectionRange? Range { get; set; }
        public int? DuplicateGroup { get; set; }

        public SemanticDocumentSection ToSection(int index) => new(
            index, Kind, Zone, State, Basis, Heading.Index, Heading.Location, Heading.Level,
            Basis == SemanticClassificationBasis.ChapterMarker ? SemanticNumberingCategory.ChapterMarker : NumberingCategory(Heading),
            Evidence.ToArray(), Heading.Order, ParentIndex, Range!, DuplicateGroup, Codes.ToArray());
    }
}
