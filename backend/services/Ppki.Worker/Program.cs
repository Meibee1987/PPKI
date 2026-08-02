using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Infrastructure;
using Ppki.RuleEngine;
using Ppki.Worker;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Database") ?? throw new InvalidOperationException("ConnectionStrings:Database is required.");
builder.Services.AddOptions<SupabaseOptions>()
    .Bind(builder.Configuration.GetSection(SupabaseOptions.SectionName))
    .Validate(x => Uri.TryCreate(x.Url, UriKind.Absolute, out _), "Supabase:Url must be an absolute URL.")
    .Validate(x => !string.IsNullOrWhiteSpace(x.PublishableKey), "Supabase:PublishableKey is required.")
    .Validate(x => !string.IsNullOrWhiteSpace(x.SecretKey), "Supabase:SecretKey is required.")
    .ValidateOnStart();
builder.Services.AddHttpClient();
builder.Services.AddPooledDbContextFactory<PpkiDbContext>(o=>o.UseNpgsql(connectionString));
builder.Services.AddSingleton<IFileStorage, SupabaseFileStorage>();
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
builder.Services.AddSingleton<AuditRunner>();
builder.Services.AddHostedService<QueuedAuditWorker>();
await builder.Build().RunAsync();
