using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Infrastructure;
using Ppki.FixEngine;
using Ppki.RuleEngine;
using Ppki.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
var connectionString = builder.Configuration.GetConnectionString("Database") ?? string.Empty;
builder.Services.AddOptions<SupabaseOptions>()
    .Bind(builder.Configuration.GetSection(SupabaseOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SupabaseOptions>, SupabaseOptionsValidator>();
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();
builder.Services.AddHttpClient();
builder.Services.AddPooledDbContextFactory<PpkiDbContext>(o=>o.UseNpgsql(connectionString));
builder.Services.AddSingleton<IFileStorage, SupabaseFileStorage>();
builder.Services.AddSingleton<IStorageObjectPathBuilder, StorageObjectPathBuilder>();
builder.Services.AddSingleton<IAuditTrailWriter, AuditTrailWriter>();
builder.Services.AddSingleton<IDocxParser, OpenXmlDocxParser>();
builder.Services.AddSingleton<IDocumentRuleValidator, PageSizeA4Validator>();
builder.Services.AddSingleton<IDocumentRuleValidator, MarginLeftValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, MarginRightValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, MarginTopValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, MarginBottomValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, BodyFontValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, LineSpacingValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, FirstLineIndentValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, JustifiedValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, ChapterNumberingValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, HeadingDepthValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, ChapterUppercaseValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, ChapterBoldValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, ChapterDecorationValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, ChapterAlignmentValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, SubheadingNumberingAlignmentValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, SubheadingDecorationValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, SubSubheadingNumberingAlignmentValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, SubSubheadingDecorationValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, SkripsiAbstractLanguagePairValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, SkripsiAbstractParagraphCountValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, SkripsiAbstractWordCountValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, SkripsiAbstractSpacingValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, ThesisSummaryLanguagePairValidator>();
builder.Services.AddSingleton<IDocumentRuleValidator, AbstractSummarySpacingValidator>();
builder.Services.AddSingleton<DocumentRuleValidatorRegistry>();
builder.Services.AddSingleton<DocumentLayoutValidationEngine>();
builder.Services.AddSingleton<IResolvedRuleSetSnapshotBuilder, ResolvedRuleSetSnapshotBuilder>();
builder.Services.AddSingleton<IResolvedRuleSetHasher, ResolvedRuleSetHasher>();
builder.Services.AddSingleton<AuditRunner>();
builder.Services.AddSingleton(ProductionFixCapabilities.CreateApplyRegistry());
builder.Services.AddSingleton<IFixApplyCapabilityResolver>(provider => provider.GetRequiredService<FixApplyCapabilityRegistry>());
builder.Services.AddSingleton<FixExecutionProcessor>();
builder.Services.AddSingleton<IRemediationFaultInjector, NoopRemediationFaultInjector>();
builder.Services.AddHostedService<QueuedAuditWorker>();
builder.Services.AddHostedService<QueuedFixExecutionWorker>();
var host = builder.Build();
_ = host.Services.GetRequiredService<IOptions<SupabaseOptions>>().Value;
_ = host.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
await host.StartAsync();
var environment = host.Services.GetRequiredService<IHostEnvironment>();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ppki.Worker.Startup");
var version = typeof(QueuedAuditWorker).Assembly.GetName().Version?.ToString() ?? "unknown";
logger.LogInformation(
    "Worker startup completed: {ServiceName}; {Environment}; {Version}; ConfigurationValidated={ConfigurationValidated}; QueueReady={QueueReady}",
    "ppki-worker",
    environment.EnvironmentName,
    version,
    true,
    true);
await host.WaitForShutdownAsync();
