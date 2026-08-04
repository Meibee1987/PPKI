using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Ppki.Application;
using Ppki.Domain;
using Ppki.FixEngine;
using Ppki.Infrastructure;
using Ppki.RuleEngine;
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
builder.Services.AddScoped<IAuditReadService, AuditReadService>();
builder.Services.AddScoped<IFixPlanSourceReader, FixPlanSourceReader>();
builder.Services.AddScoped<IFixPlanPreviewService, FixPlanPreviewService>();
builder.Services.AddScoped<IFixExecutionRepository, FixExecutionRepository>();
builder.Services.AddScoped<IFixExecutionService, FixExecutionService>();
builder.Services.AddScoped<IReauditService, ReauditService>();
builder.Services.AddSingleton<IResolvedRuleSetHasher, ResolvedRuleSetHasher>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRemediationCapabilityRegistry>(_ => ProductionFixCapabilities.CreatePreviewRegistry());
builder.Services.AddSingleton(ProductionFixCapabilities.CreateApplyRegistry());
builder.Services.AddSingleton<IFixApplyCapabilityResolver>(provider => provider.GetRequiredService<FixApplyCapabilityRegistry>());
builder.Services.AddSingleton<IFixPlanPreviewPlanner, DeterministicFixPlanPreviewPlanner>();
builder.Services.AddSingleton<IStorageObjectPathBuilder, StorageObjectPathBuilder>();
builder.Services.AddSingleton<IAuditTrailWriter, AuditTrailWriter>();
builder.Services.AddSingleton<IAuditScoreCalculator, AuditScoreCalculator>();
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

api.MapGet("/audits/{id:guid}", async (Guid id, ClaimsPrincipal user, IAuditReadService audits, CancellationToken ct) => {
    var result=await audits.GetSummaryAsync(id,UserId(user),ct);
    return result is null?Results.NotFound():Results.Ok(result);
});

api.MapGet("/audits/{id:guid}/findings", async (Guid id, ClaimsPrincipal user,
    IAuditReadService audits, string? severity, string? fixMode, string? domain,
    string? ruleCode, string? validationKey, string? sort, int? page, int? pageSize,
    CancellationToken ct) => {
    if(!AuditFindingQuery.TryCreate(severity,fixMode,domain,ruleCode,validationKey,
        sort,page,pageSize,out var query,out var errorCode))
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,
            title:"Invalid findings query.",extensions:new Dictionary<string,object?>{{"code",errorCode}});
    var result=await audits.GetFindingsAsync(id,UserId(user),query,ct);
    return result is null?Results.NotFound():Results.Ok(result);
});

api.MapGet("/audits/{id:guid}/findings/{findingId:guid}", async (Guid id,
    Guid findingId, ClaimsPrincipal user, IAuditReadService audits, CancellationToken ct) => {
    var result=await audits.GetFindingAsync(id,findingId,UserId(user),ct);
    return result is null?Results.NotFound():Results.Ok(result);
});

api.MapPost("/audits/{id:guid}/fix-plan-preview", async (Guid id,
    ClaimsPrincipal user, FixPlanPreviewRequest? request,
    IFixPlanPreviewService previews, CancellationToken ct) => {
    if(!FixPlanSelection.TryCreate(request?.FindingIds,out var selection,out var errorCode))
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,
            title:"Invalid fix plan selection.",extensions:new Dictionary<string,object?>{{"code",errorCode}});
    try {
        var result=await previews.PreviewAsync(id,UserId(user),selection,ct);
        return result is null?Results.NotFound():Results.Ok(result);
    } catch(FixPlanConfigurationException exception) {
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,
            title:"Invalid remediation capability configuration.",
            extensions:new Dictionary<string,object?>{{"code",exception.DiagnosticCode}});
    }
}).WithName("PreviewAuditFixPlan")
  .WithSummary("Build a deterministic, read-only fix-plan preview from audit snapshots.")
  .Accepts<FixPlanPreviewRequest>("application/json")
  .Produces<FixPlanPreview>(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status400BadRequest)
  .Produces(StatusCodes.Status404NotFound);

api.MapPost("/audits/{id:guid}/fix-executions", async (Guid id,
    ClaimsPrincipal user, HttpRequest httpRequest, FixExecutionRequest? request,
    IFixExecutionService executions, CancellationToken ct) => {
    string? selectionError=null;
    if(request is null || !FixPlanSelection.TryCreate(request.FindingIds,out var selection,out selectionError))
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid fix execution request.",
            extensions:new Dictionary<string,object?>{{"code",selectionError??"fix-execution-request-invalid"}});
    var header=httpRequest.Headers["Idempotency-Key"];
    if(header.Count!=1 || !Guid.TryParse(header[0],out var idempotencyKey) || idempotencyKey==Guid.Empty)
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid Idempotency-Key.",
            extensions:new Dictionary<string,object?>{{"code","fix-execution-idempotency-key-invalid"}});
    try {
        var result=await executions.AcceptAsync(id,UserId(user),idempotencyKey,selection,request.PlanHash??string.Empty,ct);
        if(result is null)return Results.NotFound();
        return result.Replayed?Results.Ok(result):Results.Accepted($"/api/audits/{id}/fix-executions/{result.Id}",result);
    } catch(FixExecutionException exception) {
        var malformed=exception.DiagnosticCode is "fix-execution-plan-hash-invalid" or "fix-execution-idempotency-key-invalid";
        return Results.Problem(statusCode:malformed?StatusCodes.Status400BadRequest:StatusCodes.Status409Conflict,
            title:malformed?"Invalid fix execution request.":"Fix execution request conflicts with the approved plan.",
            extensions:new Dictionary<string,object?>{{"code",exception.DiagnosticCode}});
    }
}).WithName("CreateAuditFixExecution")
  .WithSummary("Accept an exact preview plan for asynchronous execution.")
  .Accepts<FixExecutionRequest>("application/json")
  .Produces<FixExecutionAccepted>(StatusCodes.Status202Accepted)
  .Produces<FixExecutionAccepted>(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status400BadRequest)
  .ProducesProblem(StatusCodes.Status409Conflict)
  .Produces(StatusCodes.Status404NotFound);

api.MapGet("/audits/{id:guid}/fix-executions/{executionId:guid}", async (Guid id,
    Guid executionId, ClaimsPrincipal user, IFixExecutionService executions, CancellationToken ct) => {
    var result=await executions.GetAsync(executionId,UserId(user),ct);
    return result is null||result.AuditId!=id?Results.NotFound():Results.Ok(result);
}).WithName("GetAuditFixExecution")
  .WithSummary("Read the safe lifecycle status of an owned fix execution.")
  .Produces<FixExecutionStatus>(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound);

api.MapPost("/fix-executions/{executionId}/re-audit", async (string executionId,
    ClaimsPrincipal user, IReauditService reaudits, CancellationToken ct) => {
    if(!Guid.TryParse(executionId,out var parsedExecutionId)||parsedExecutionId==Guid.Empty)
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid re-audit request.",
            extensions:new Dictionary<string,object?>{{"code","reaudit-execution-id-invalid"}});
    try {
        var result=await reaudits.CreateAsync(parsedExecutionId,UserId(user),ct);
        if(result is null)return Results.NotFound();
        return result.Replayed?Results.Ok(result):Results.Accepted($"/api/audits/{result.AuditId}",result);
    } catch(ReauditException exception) {
        return Results.Problem(statusCode:StatusCodes.Status409Conflict,
            title:"Re-audit request conflicts with its historical source context.",
            extensions:new Dictionary<string,object?>{{"code",exception.DiagnosticCode}});
    }
}).WithName("CreateFixExecutionReaudit")
  .WithSummary("Queue one canonical audit of a completed fix result using the exact source audit context.")
  .Produces<ReauditAccepted>(StatusCodes.Status202Accepted)
  .Produces<ReauditAccepted>(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status400BadRequest)
  .ProducesProblem(StatusCodes.Status409Conflict)
  .Produces(StatusCodes.Status404NotFound);

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
