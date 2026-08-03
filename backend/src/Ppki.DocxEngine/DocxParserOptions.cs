namespace Ppki.DocxEngine;

public sealed record DocxParserOptions
{
    public const long DefaultMaximumInputBytes = 25 * 1024 * 1024;
    public const long DefaultMaximumExpandedPackageBytes = 200 * 1024 * 1024;
    public const int DefaultMaximumPackageEntries = 50_000;
    public const int DefaultMaximumParagraphs = 100_000;
    public const int DefaultMaximumRuns = 500_000;
    public const int DefaultMaximumTables = 10_000;
    public const int DefaultMaximumRelationships = 20_000;
    public const int DefaultMaximumDiagnostics = 200;
    public const int DefaultMaximumStyleCount = 10_000;
    public const int DefaultMaximumStyleInheritanceDepth = 64;
    public const int DefaultMaximumAbstractNumberingDefinitions = 10_000;
    public const int DefaultMaximumNumberingInstances = 10_000;
    public const int DefaultMaximumNumberingLevels = 100_000;
    public const int DefaultMaximumOutlineNodes = 100_000;
    public const int DefaultMaximumSemanticSections = 10_000;
    public const int DefaultMaximumSectionAliases = 1_000;
    public const int DefaultMaximumSemanticHeadingLength = 512;
    public const int DefaultMaximumSystematicsEntries = 10_000;

    public long MaximumInputBytes { get; init; } = DefaultMaximumInputBytes;
    public long MaximumExpandedPackageBytes { get; init; } = DefaultMaximumExpandedPackageBytes;
    public int MaximumPackageEntries { get; init; } = DefaultMaximumPackageEntries;
    public int MaximumParagraphs { get; init; } = DefaultMaximumParagraphs;
    public int MaximumRuns { get; init; } = DefaultMaximumRuns;
    public int MaximumTables { get; init; } = DefaultMaximumTables;
    public int MaximumRelationships { get; init; } = DefaultMaximumRelationships;
    public int MaximumDiagnostics { get; init; } = DefaultMaximumDiagnostics;
    public int MaximumStyleCount { get; init; } = DefaultMaximumStyleCount;
    public int MaximumStyleInheritanceDepth { get; init; } = DefaultMaximumStyleInheritanceDepth;
    public int MaximumAbstractNumberingDefinitions { get; init; } = DefaultMaximumAbstractNumberingDefinitions;
    public int MaximumNumberingInstances { get; init; } = DefaultMaximumNumberingInstances;
    public int MaximumNumberingLevels { get; init; } = DefaultMaximumNumberingLevels;
    public int MaximumOutlineNodes { get; init; } = DefaultMaximumOutlineNodes;
    public int MaximumSemanticSections { get; init; } = DefaultMaximumSemanticSections;
    public int MaximumSectionAliases { get; init; } = DefaultMaximumSectionAliases;
    public int MaximumSemanticHeadingLength { get; init; } = DefaultMaximumSemanticHeadingLength;
    public int MaximumSystematicsEntries { get; init; } = DefaultMaximumSystematicsEntries;

    public void Validate()
    {
        if (MaximumInputBytes <= 0 || MaximumExpandedPackageBytes <= 0 || MaximumPackageEntries <= 0
            || MaximumParagraphs <= 0 || MaximumRuns <= 0
            || MaximumTables <= 0 || MaximumRelationships <= 0 || MaximumDiagnostics <= 0
            || MaximumStyleCount <= 0 || MaximumStyleInheritanceDepth <= 0
            || MaximumAbstractNumberingDefinitions <= 0 || MaximumNumberingInstances <= 0
            || MaximumNumberingLevels <= 0 || MaximumOutlineNodes <= 0
            || MaximumSemanticSections <= 0 || MaximumSectionAliases <= 0
            || MaximumSemanticHeadingLength <= 0 || MaximumSystematicsEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DocxParserOptions), "DOCX parser limits must be positive.");
        }
    }
}

public sealed class DocxParserException : Exception
{
    public DocxParserException(string code, string safeMessage) : base(safeMessage)
    {
        Code = code;
    }

    public string Code { get; }
}
