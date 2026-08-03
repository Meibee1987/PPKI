using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Ppki.Application;
using Ppki.Domain;
using Ppki.Infrastructure;
using Ppki.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
var connectionString = builder.Configuration.GetConnectionString("Database") ?? string.Empty;
var ruleCatalogPath = builder.Configuration["RuleCatalog:Path"] ?? throw new InvalidOperationException("RuleCatalog:Path is required.");

builder.Services.AddOptions<SupabaseOptions>()
    .Bind(builder.Configuration.GetSection(SupabaseOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SupabaseOptions>, SupabaseOptionsValidator>();
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();
builder.Services.AddOptions<ReadinessHealthCheckOptions>()
    .Bind(builder.Configuration.GetSection(ReadinessHealthCheckOptions.SectionName))
    .Validate(options => options.TimeoutSeconds is >= 1 and <= 10, "HealthChecks:TimeoutSeconds must be between 1 and 10.")
    .ValidateOnStart();
builder.Services.AddHttpClient();
builder.Services.AddDbContextFactory<PpkiDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddScoped<IFileStorage, SupabaseFileStorage>();
builder.Services.AddSingleton<IStorageObjectPathBuilder, StorageObjectPathBuilder>();
builder.Services.AddSingleton<IAuditTrailWriter, AuditTrailWriter>();
builder.Services.AddScoped<IDatabaseReadinessProbe, DatabaseReadinessProbe>();
builder.Services.AddHealthChecks()
    .AddCheck("live", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<DatabaseReadinessHealthCheck>("database", tags: ["ready"])
    .AddCheck<StorageConfigurationHealthCheck>("storage-configuration", tags: ["ready"]);
builder.Services.AddAuthentication(SupabaseAuthenticationDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, SupabaseAuthenticationHandler>(SupabaseAuthenticationDefaults.Scheme, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddOpenApi();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => {
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").GetChildren().Select(x=>x.Value).Where(x=>!string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
    policy.WithOrigins(origins.Length == 0 ? ["http://localhost:3000"] : origins).AllowAnyHeader().AllowAnyMethod();
}));

var app = builder.Build();
_ = app.Services.GetRequiredService<IOptions<SupabaseOptions>>().Value;
_ = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
_ = app.Services.GetRequiredService<IOptions<ReadinessHealthCheckOptions>>().Value;
app.UseCors(); app.UseAuthentication(); app.UseAuthorization(); app.MapOpenApi();
await using (var scope = app.Services.CreateAsyncScope()) {
    var db = scope.ServiceProvider.GetRequiredService<PpkiDbContext>();
    await DatabaseInitializer.VerifyAndSeedRulesAsync(db, ruleCatalogPath);
}

var liveHealthOptions = new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live"),
    ResponseWriter = SafeHealthResponseWriter.WriteAsync,
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
};
var readyHealthOptions = new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = SafeHealthResponseWriter.WriteAsync,
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
};
app.MapHealthChecks("/health/live", liveHealthOptions);
app.MapHealthChecks("/health/ready", readyHealthOptions);
app.MapHealthChecks("/health", liveHealthOptions);
var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/me", async (ClaimsPrincipal user, PpkiDbContext db, CancellationToken ct) => {
    var id = UserId(user); var profile = await EnsureProfileAsync(user, db, ct);
    return Results.Ok(new { id, profile.Email, profile.FullName, profile.Role });
});

api.MapGet("/rules/summary", async (PpkiDbContext db, CancellationToken ct) => Results.Ok(new {
    total = await db.Rules.CountAsync(ct), implemented = await db.Rules.CountAsync(x=>x.IsImplemented, ct)
}));

api.MapGet("/documents", async (ClaimsPrincipal user, PpkiDbContext db, CancellationToken ct) => {
    var uid = UserId(user);
    var rows = await db.Documents.AsNoTracking().Where(x=>x.OwnerUserId==uid).Include(x=>x.DocumentType).Include(x=>x.Versions).ThenInclude(x=>x.Audits).OrderByDescending(x=>x.UpdatedAt).ToListAsync(ct);
    return Results.Ok(rows.Select(x=>new { x.Id, x.Title, DocumentType=x.DocumentType!.Name, x.CurrentVersionNo, x.UpdatedAt,
        LatestAudit=x.Versions.SelectMany(v=>v.Audits).OrderByDescending(a=>a.CreatedAt).Select(a=>new {a.Id,Status=a.Status.ToString(),a.Score,a.ErrorCount,a.WarningCount,a.InfoCount}).FirstOrDefault() }));
});

api.MapPost("/documents", async (ClaimsPrincipal user, HttpRequest request, PpkiDbContext db, IDbContextFactory<PpkiDbContext> dbFactory, IFileStorage storage, IStorageObjectPathBuilder pathBuilder, IAuditTrailWriter auditTrail, IOptions<SupabaseOptions> supabase, CancellationToken ct) => {
    if (!request.HasFormContentType) return Results.BadRequest(new { error="multipart/form-data is required." });
    var form = await request.ReadFormAsync(ct); var title=form["title"].ToString().Trim(); var code=form["documentTypeCode"].ToString().Trim().ToUpperInvariant(); var file=form.Files.GetFile("file");
    if (string.IsNullOrWhiteSpace(title)||string.IsNullOrWhiteSpace(code)||file is null) return Results.BadRequest(new {error="title, documentTypeCode, and file are required."});
    if (!Path.GetExtension(file.FileName).Equals(".docx",StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new {error="Only .docx files are supported."});
    if (!string.Equals(file.ContentType, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new {error="DOCX MIME type is required."});
    if (file.Length is <=0 or >50*1024*1024) return Results.BadRequest(new {error="File must be between 1 byte and 50 MB."});
    var type=await db.DocumentTypes.SingleOrDefaultAsync(x=>x.Code==code,ct); if(type is null) return Results.BadRequest(new{error="Unknown document type."});
    var uid=UserId(user); await EnsureProfileAsync(user,db,ct);
    var createdAt=DateTimeOffset.UtcNow;
    var document=new DocumentRecord{OwnerUserId=uid,DocumentTypeId=type.Id,Title=title,CurrentVersionNo=1,CreatedAt=createdAt,UpdatedAt=createdAt};
    var versionId=Guid.NewGuid();
    var bucket=supabase.Value.Storage.OriginalBucket;
    var key=pathBuilder.BuildOriginalPath(uid,document.Id,versionId);
    var eventContext=AuditEventContext.User(uid,Guid.NewGuid());
    StoredFile? stored=null;
    try {
        await using var stream=file.OpenReadStream(); stored=await storage.SaveAsync(stream,file.FileName,file.ContentType,bucket,key,ct);
        var version=new DocumentVersion{Id=versionId,Document=document,VersionNo=1,StorageBucket=stored.StorageBucket,StorageKey=stored.StorageKey,OriginalFilename=stored.OriginalFilename,MimeType=stored.ContentType,SizeBytes=stored.SizeBytes,Sha256=stored.Sha256,CreatedByUserId=uid};
        await using var transaction=await db.Database.BeginTransactionAsync(ct);
        await auditTrail.SetTransactionContextAsync(db,eventContext,ct);
        db.Documents.Add(document); db.DocumentVersions.Add(version);
        auditTrail.Add(db,eventContext,new AuditEventData(AuditActions.DocumentUploadCompleted,AuditResourceTypes.DocumentVersion,versionId,uid,AuditEventMetadata.Create(("file_size_bytes",stored.SizeBytes),("mime_type",stored.ContentType))));
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    } catch {
        if (stored is not null) {
            var cleaned=false; try { await storage.DeleteAsync(stored.StorageBucket, stored.StorageKey, CancellationToken.None); cleaned=true; } catch { }
            if(cleaned) await TryWriteOrphanCleanupAsync(dbFactory,auditTrail,eventContext,versionId,uid,CancellationToken.None);
        }
        return Results.Problem(statusCode:StatusCodes.Status500InternalServerError,title:"Document upload failed.");
    }
    return Results.Created($"/api/documents/{document.Id}",new{document.Id,versionId,document.Title,document.CurrentVersionNo,sha256=stored!.Sha256});
}).DisableAntiforgery();

api.MapGet("/documents/{id:guid}", async (Guid id, ClaimsPrincipal user, PpkiDbContext db, CancellationToken ct) => {
    var uid=UserId(user); var doc=await db.Documents.AsNoTracking().Where(x=>x.Id==id&&x.OwnerUserId==uid).Include(x=>x.DocumentType).Include(x=>x.Versions).ThenInclude(x=>x.Audits).SingleOrDefaultAsync(ct);
    if(doc is null) return Results.NotFound();
    return Results.Ok(new{doc.Id,doc.Title,DocumentType=doc.DocumentType!.Name,doc.CurrentVersionNo,doc.CreatedAt,doc.UpdatedAt,
        Versions=doc.Versions.OrderByDescending(v=>v.VersionNo).Select(v=>new{v.Id,v.VersionNo,v.OriginalFilename,v.SizeBytes,v.Sha256,v.CreatedAt,Audits=v.Audits.OrderByDescending(a=>a.CreatedAt).Select(a=>new{a.Id,Status=a.Status.ToString(),a.Score,a.ErrorCount,a.WarningCount,a.InfoCount,a.CreatedAt})})});
});

api.MapPost("/document-versions/{versionId:guid}/audits", async (Guid versionId, ClaimsPrincipal user, PpkiDbContext db, IAuditTrailWriter auditTrail, CancellationToken ct) => {
    var uid=UserId(user); var documentKind=await db.DocumentVersions.Where(v=>v.Id==versionId&&v.Document!.OwnerUserId==uid).Select(v=>(DocumentKind?)v.Document!.DocumentType!.Kind).SingleOrDefaultAsync(ct); if(documentKind is null)return Results.NotFound();
    var active=await db.ProfileVersions.OrderByDescending(x=>x.VersionNo).FirstAsync(x=>x.Status=="Active",ct);
    var audit=new AuditJob{DocumentVersionId=versionId,ProfileVersionId=active.Id,DocumentKindSnapshot=documentKind,RequestedByUserId=uid,Status=AuditJobStatus.Queued};
    var eventContext=AuditEventContext.User(uid,audit.Id);
    await using var transaction=await db.Database.BeginTransactionAsync(ct); await auditTrail.SetTransactionContextAsync(db,eventContext,ct);
    db.AuditJobs.Add(audit); auditTrail.Add(db,eventContext,new AuditEventData(AuditActions.AuditRequested,AuditResourceTypes.AuditJob,audit.Id,uid,AuditEventMetadata.Create(("audit_status","Queued"))));
    await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    return Results.Accepted($"/api/audits/{audit.Id}",new{audit.Id,status=audit.Status.ToString()});
});

api.MapGet("/audits/{id:guid}", async (Guid id, ClaimsPrincipal user, PpkiDbContext db, CancellationToken ct) => {
    var uid=UserId(user); var a=await db.AuditJobs.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.DocumentVersion!.Document!.OwnerUserId==uid,ct); if(a is null)return Results.NotFound();
    return Results.Ok(new{a.Id,Status=a.Status.ToString(),TotalRules=a.ApplicableRuleCount,a.ErrorCount,a.WarningCount,a.InfoCount,a.Score,a.ResolvedRuleSetHash,a.StartedAt,a.CompletedAt,a.ErrorMessage});
});

api.MapGet("/audits/{id:guid}/findings", async (Guid id, ClaimsPrincipal user, PpkiDbContext db, CancellationToken ct) => {
    var uid=UserId(user); var owned=await db.AuditJobs.AnyAsync(x=>x.Id==id&&x.DocumentVersion!.Document!.OwnerUserId==uid,ct); if(!owned)return Results.NotFound();
    var rows=await db.AuditFindings.AsNoTracking().Where(x=>x.AuditJobId==id).OrderBy(x=>x.Severity).ThenBy(x=>x.RuleCodeSnapshot).ToListAsync(ct);
    var snapshots=await db.AuditRuleSnapshots.AsNoTracking().Where(x=>x.AuditJobId==id).ToDictionaryAsync(x=>x.RuleCode,ct);
    return Results.Ok(rows.Select(x=>{snapshots.TryGetValue(x.RuleCodeSnapshot,out var snapshot);return new{x.Id,RuleCode=x.RuleCodeSnapshot,Element=snapshot?.Element??string.Empty,Domain=snapshot?.Domain??string.Empty,Severity=x.Severity.ToString(),FixMode=x.FixModeSnapshot.ToString(),x.Message,Actual=JsonSerializer.Deserialize<JsonElement>(x.ActualValueJson),Expected=JsonSerializer.Deserialize<JsonElement>(x.ExpectedValueJson),Location=JsonSerializer.Deserialize<JsonElement>(x.LocationJson),x.Confidence,Source=new{SourceSection=x.SourceSectionSnapshot,PdfPage=x.PdfPageSnapshot,PrintedPage=x.PrintedPageSnapshot}};}));
});

api.MapGet("/document-versions/{id:guid}/download", async (Guid id, ClaimsPrincipal user, PpkiDbContext db, IFileStorage storage, IStorageObjectPathBuilder pathBuilder, IAuditTrailWriter auditTrail, IOptions<SupabaseOptions> supabase, CancellationToken ct) => {
    var uid=UserId(user); var version=await db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(v=>v.Id==id&&v.Document!.OwnerUserId==uid,ct); if(version is null)return Results.NotFound();
    var expected=pathBuilder.BuildOriginalPath(uid,version.DocumentId,version.Id); if(version.StorageBucket!=supabase.Value.Storage.OriginalBucket||version.StorageKey!=expected)return Results.NotFound();
    var lifetime=TimeSpan.FromSeconds(supabase.Value.Storage.SignedUrlLifetimeSeconds); string url;
    try { url=await storage.CreateSignedDownloadUrlAsync(version.StorageBucket,version.StorageKey,lifetime,ct); }
    catch { return Results.Problem(statusCode:StatusCodes.Status502BadGateway,title:"Document download authorization failed."); }
    var eventContext=AuditEventContext.User(uid,Guid.NewGuid()); await using var transaction=await db.Database.BeginTransactionAsync(ct); await auditTrail.SetTransactionContextAsync(db,eventContext,ct);
    auditTrail.Add(db,eventContext,new AuditEventData(AuditActions.DocumentDownloadAuthorized,AuditResourceTypes.DocumentVersion,version.Id,uid,AuditEventMetadata.Create(("download_kind","original")))); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    return Results.Ok(new{url,expiresInSeconds=(int)lifetime.TotalSeconds});
});

app.Run();

static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Missing user id claim."));
static async Task<UserProfile> EnsureProfileAsync(ClaimsPrincipal user, PpkiDbContext db, CancellationToken ct) {
    var id=UserId(user); var existing=await db.UserProfiles.SingleOrDefaultAsync(x=>x.Id==id,ct); if(existing is not null)return existing;
    var profile=new UserProfile{Id=id,Email=user.FindFirstValue(ClaimTypes.Email)??$"{id}@unknown.local",FullName=user.Identity?.Name??"Pengguna",Role="Student"}; db.UserProfiles.Add(profile); await db.SaveChangesAsync(ct); return profile;
}

static async Task TryWriteOrphanCleanupAsync(IDbContextFactory<PpkiDbContext> dbFactory, IAuditTrailWriter auditTrail, AuditEventContext context, Guid versionId, Guid ownerUserId, CancellationToken ct) {
    try {
        var serviceContext=AuditEventContext.Service("api",context.CorrelationId,context.CausationId);
        await using var db=await dbFactory.CreateDbContextAsync(ct); await using var transaction=await db.Database.BeginTransactionAsync(ct); await auditTrail.SetTransactionContextAsync(db,serviceContext,ct);
        auditTrail.Add(db,serviceContext,new AuditEventData(AuditActions.StorageOrphanCleanup,AuditResourceTypes.StorageObject,versionId,ownerUserId,AuditEventMetadata.Create(("cleanup_reason","database_insert_failed"))));
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    } catch { }
}
