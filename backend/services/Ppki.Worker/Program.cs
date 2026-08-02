using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Infrastructure;
using Ppki.RuleEngine;
using Ppki.Worker;

var builder = Host.CreateApplicationBuilder(args);
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
builder.Services.AddSingleton<IDocxParser, OpenXmlDocxParser>();
builder.Services.AddSingleton<IRuleValidator, PageSizeA4Validator>();
builder.Services.AddSingleton<IRuleValidator, MarginLeftValidator>();
builder.Services.AddSingleton<IRuleValidator, MarginRightValidator>();
builder.Services.AddSingleton<IRuleValidator, MarginTopValidator>();
builder.Services.AddSingleton<IRuleValidator, MarginBottomValidator>();
builder.Services.AddSingleton<IRuleValidator, BodyFontValidator>();
builder.Services.AddSingleton<IRuleValidator, LineSpacingValidator>();
builder.Services.AddSingleton<IRuleValidator, FirstLineIndentValidator>();
builder.Services.AddSingleton<IRuleValidator, JustifiedValidator>();
builder.Services.AddSingleton<IResolvedRuleSetSnapshotBuilder, ResolvedRuleSetSnapshotBuilder>();
builder.Services.AddSingleton<IResolvedRuleSetHasher, ResolvedRuleSetHasher>();
builder.Services.AddSingleton<AuditRunner>();
builder.Services.AddHostedService<QueuedAuditWorker>();
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
