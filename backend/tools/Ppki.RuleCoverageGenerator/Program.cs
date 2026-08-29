using System.Text;
using System.Text.Json;
using Ppki.Application;
using Ppki.FixEngine;
using Ppki.RuleEngine;

var root = RepositoryRoot();
var catalogPath = Path.Combine(root, "rules", "ppki-ipb-2019", "rules.json");
var outputPath = Path.Combine(root, "docs", "RULE_COVERAGE_MVP.md");
using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
var catalogRuleCodes = catalog.RootElement.GetProperty("rules").EnumerateArray()
    .Select(value => value.GetProperty("rule_id").GetString()!).ToArray();
var validators = new DocumentRuleValidatorRegistry(ProductionDocumentValidators.Create());
var capabilities = ProductionFixCapabilities.CreatePreviewRegistry().Capabilities
    .Select(value => new RuleCoverageCapability(value.ValidationKey, value.CapabilityId, value.CapabilityVersion));

RuleCoverageQualityGate.Validate(RuleCoverageManifest.Entries, catalogRuleCodes,
    validators.ValidationKeys, capabilities);
var generated = RuleCoverageDocumentation.Render(RuleCoverageManifest.Entries);
if (args.Contains("--check", StringComparer.Ordinal))
{
    if (!File.Exists(outputPath) || File.ReadAllText(outputPath).Replace("\r\n", "\n", StringComparison.Ordinal) != generated)
        throw new InvalidOperationException("docs/RULE_COVERAGE_MVP.md is not synchronized with the compiled manifest.");
    Console.WriteLine("Rule coverage manifest and generated documentation are valid and synchronized.");
    return;
}

File.WriteAllText(outputPath, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine("Generated docs/RULE_COVERAGE_MVP.md from the compiled manifest.");

static string RepositoryRoot()
{
    for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null; candidate = candidate.Parent)
        if (File.Exists(Path.Combine(candidate.FullName, "AGENTS.md"))) return candidate.FullName;
    throw new DirectoryNotFoundException("Repository root was not found.");
}
