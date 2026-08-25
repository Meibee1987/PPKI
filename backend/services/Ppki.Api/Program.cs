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
builder.Services.AddScoped<IAuditComparisonService, AuditComparisonService>();
builder.Services.AddScoped<IFindingResolutionService, FindingResolutionService>();
builder.Services.AddScoped<IInternalAdminAuthorizationService, InternalAdminAuthorizationService>();
builder.Services.AddScoped<ITextCorrectionService, TextCorrectionService>();
builder.Services.AddScoped<ITextCorrectionContextMaterializationService, TextCorrectionContextMaterializationService>();
builder.Services.AddScoped<IStructuralFindingExcerptService, StructuralFindingExcerptService>();
builder.Services.AddScoped<InternalAdminEndpointFilter>();
builder.Services.AddScoped<IFindingReviewService, FindingReviewService>();
builder.Services.AddSingleton<IResolvedRuleSetHasher, ResolvedRuleSetHasher>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRemediationCapabilityRegistry>(_ => ProductionFixCapabilities.CreatePreviewRegistry());
builder.Services.AddSingleton(ProductionFixCapabilities.CreateApplyRegistry());
builder.Services.AddSingleton<IFixApplyCapabilityResolver>(provider => provider.GetRequiredService<FixApplyCapabilityRegistry>());
builder.Services.AddSingleton<IFixEligibilityService, FixEligibilityService>();
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
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
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
var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter<InternalAdminEndpointFilter>();

api.MapGet("/me", async (ClaimsPrincipal user, PpkiDbContext db, CancellationToken ct) => {
    var id = UserId(user); var profile = await db.UserProfiles.AsNoTracking().SingleAsync(x=>x.Id==id,ct);
    return Results.Ok(new { id, profile.Email, profile.FullName, profile.Role });
});

api.MapGet("/rules/summary", async (PpkiDbContext db, CancellationToken ct) => Results.Ok(new {
    total = await db.Rules.CountAsync(ct), implemented = await db.Rules.CountAsync(x=>x.IsImplemented, ct)
}));

api.MapGet("/documents", async (ClaimsPrincipal user, PpkiDbContext db, CancellationToken ct) => {
    _ = UserId(user);
    var rows = await db.Documents.AsNoTracking().Include(x=>x.DocumentType).Include(x=>x.Versions).ThenInclude(x=>x.Audits).OrderByDescending(x=>x.UpdatedAt).ToListAsync(ct);
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
    var uid=UserId(user);
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
        db.DocumentRenderJobs.Add(CanonicalDocumentRenderContract.CreateJob(version.Id, version.Sha256));
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
    _=UserId(user); var doc=await db.Documents.AsNoTracking().Where(x=>x.Id==id).Include(x=>x.DocumentType).Include(x=>x.Versions).ThenInclude(x=>x.Audits).SingleOrDefaultAsync(ct);
    if(doc is null) return Results.NotFound();
    return Results.Ok(new{doc.Id,doc.Title,DocumentType=doc.DocumentType!.Name,doc.CurrentVersionNo,doc.CreatedAt,doc.UpdatedAt,
        Versions=doc.Versions.OrderByDescending(v=>v.VersionNo).Select(v=>new{v.Id,v.VersionNo,v.ParentVersionId,v.OriginalFilename,v.SizeBytes,v.Sha256,v.CreatedAt,Audits=v.Audits.OrderByDescending(a=>a.CreatedAt).Select(a=>new{a.Id,Status=a.Status.ToString(),a.Score,a.ErrorCount,a.WarningCount,a.InfoCount,a.CreatedAt})})});
});

api.MapPost("/document-versions/{versionId:guid}/audits", async (Guid versionId, ClaimsPrincipal user, PpkiDbContext db, IAuditTrailWriter auditTrail, CancellationToken ct) => {
    var uid=UserId(user); var documentKind=await db.DocumentVersions.Where(v=>v.Id==versionId).Select(v=>(DocumentKind?)v.Document!.DocumentType!.Kind).SingleOrDefaultAsync(ct); if(documentKind is null)return Results.NotFound();
    var active=await db.ProfileVersions.OrderByDescending(x=>x.VersionNo).FirstAsync(x=>x.Status=="Active",ct);
    var audit=new AuditJob{DocumentVersionId=versionId,ProfileVersionId=active.Id,DocumentKindSnapshot=documentKind,RequestedByUserId=uid,Status=AuditJobStatus.Queued};
    var automaticRemediation=new AutomaticRemediationOrchestration{SourceAuditJobId=audit.Id,OrchestrationType=AutomaticRemediationPolicy.OrchestrationType,PolicyVersion=AutomaticRemediationPolicy.Version,State=AutomaticRemediationState.Pending}; automaticRemediation.UpdatedAt=automaticRemediation.CreatedAt;
    var eventContext=AuditEventContext.User(uid,audit.Id);
    await using var transaction=await db.Database.BeginTransactionAsync(ct); await auditTrail.SetTransactionContextAsync(db,eventContext,ct);
    db.AuditJobs.Add(audit); db.AutomaticRemediationOrchestrations.Add(automaticRemediation); auditTrail.Add(db,eventContext,new AuditEventData(AuditActions.AuditRequested,AuditResourceTypes.AuditJob,audit.Id,uid,AuditEventMetadata.Create(("audit_status","Queued"))));
    await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    return Results.Accepted($"/api/audits/{audit.Id}",new{audit.Id,status=audit.Status.ToString()});
});

api.MapGet("/audits/{id:guid}", async (Guid id, ClaimsPrincipal user, IAuditReadService audits, CancellationToken ct) => {
    var result=await audits.GetSummaryAsync(id,UserId(user),ct);
    return result is null?Results.NotFound():Results.Ok(result);
});

api.MapGet("/audits/{id:guid}/findings", async (Guid id, ClaimsPrincipal user,
    IAuditReadService audits, string? severity, string? fixMode, string? disposition, bool? automaticallyResolved, string? domain,
    string? ruleCode, string? validationKey, string? search, string? sort, int? page, int? pageSize,
    CancellationToken ct) => {
    if(!AuditFindingQuery.TryCreate(severity,fixMode,disposition,automaticallyResolved,domain,ruleCode,validationKey,search,
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

api.MapGet("/fix-executions/{executionId}/comparison", async (string executionId,
    string? status, string? severity, string? domain, string? ruleCode,
    string? sort, int? page, int? pageSize, ClaimsPrincipal user,
    IAuditComparisonService comparisons, CancellationToken ct) => {
    if(!Guid.TryParse(executionId,out var parsedExecutionId)||parsedExecutionId==Guid.Empty)
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid audit comparison request.",
            extensions:new Dictionary<string,object?>{{"code","audit-comparison-execution-id-invalid"}});
    if(!AuditComparisonQuery.TryCreate(status,severity,domain,ruleCode,sort,page,pageSize,
        out var query,out var errorCode))
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid audit comparison query.",
            extensions:new Dictionary<string,object?>{{"code",errorCode}});
    try {
        var result=await comparisons.GetAsync(parsedExecutionId,UserId(user),query,ct);
        return result is null?Results.NotFound():Results.Ok(result);
    } catch(AuditComparisonException exception) {
        return Results.Problem(statusCode:StatusCodes.Status409Conflict,
            title:"Audit comparison is not ready for this fix execution.",
            extensions:new Dictionary<string,object?>{{"code",exception.DiagnosticCode}});
    }
}).WithName("GetFixExecutionAuditComparison")
  .WithSummary("Read a deterministic derived comparison of source and result audit findings.")
  .Produces<AuditComparisonDto>(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status400BadRequest)
  .ProducesProblem(StatusCodes.Status409Conflict)
  .Produces(StatusCodes.Status404NotFound);

api.MapGet("/audits/{auditId}/findings/{findingId}/resolution", async (string auditId,
    string findingId, ClaimsPrincipal user, IFindingResolutionService resolutions, CancellationToken ct) => {
    if(!Guid.TryParse(auditId,out var parsedAuditId)||parsedAuditId==Guid.Empty
        ||!Guid.TryParse(findingId,out var parsedFindingId)||parsedFindingId==Guid.Empty)
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid finding resolution request.",
            extensions:new Dictionary<string,object?>{{"code","resolution-id-invalid"}});
    var result=await resolutions.GetAsync(parsedAuditId,parsedFindingId,UserId(user),ct);
    return result is null?Results.NotFound():Results.Ok(result);
}).WithName("GetFindingResolution")
  .WithSummary("Read the append-only remediation evidence state for one owned historical finding.")
  .Produces<FindingResolutionDto>(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status400BadRequest)
  .Produces(StatusCodes.Status404NotFound);

api.MapPost("/fix-executions/{executionId}/resolution-reconciliation", async (string executionId,
    ClaimsPrincipal user, IFindingResolutionService resolutions, CancellationToken ct) => {
    if(!Guid.TryParse(executionId,out var parsedExecutionId)||parsedExecutionId==Guid.Empty)
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid finding resolution reconciliation request.",
            extensions:new Dictionary<string,object?>{{"code","resolution-execution-id-invalid"}});
    try {
        var result=await resolutions.ReconcileAsync(parsedExecutionId,UserId(user),ct);
        if(result is null)return Results.NotFound();
        if(result.State==FindingResolutionReconciliationState.Pending)
            return Results.Accepted($"/api/fix-executions/{parsedExecutionId}/resolution-reconciliation",result);
        return result.EventsCreated>0
            ?Results.Created($"/api/fix-executions/{parsedExecutionId}/resolution-reconciliation",result)
            :Results.Ok(result);
    } catch(FindingResolutionException exception) {
        return Results.Problem(statusCode:StatusCodes.Status409Conflict,
            title:"Finding resolution reconciliation conflicts with immutable remediation evidence.",
            extensions:new Dictionary<string,object?>{{"code",exception.DiagnosticCode}});
    }
}).WithName("ReconcileFixExecutionResolution")
  .WithSummary("Reconcile finding state from an owned completed fix execution and its canonical re-audit.")
  .Produces<FindingResolutionReconciliationResult>(StatusCodes.Status200OK)
  .Produces<FindingResolutionReconciliationResult>(StatusCodes.Status201Created)
  .Produces<FindingResolutionReconciliationResult>(StatusCodes.Status202Accepted)
  .ProducesProblem(StatusCodes.Status400BadRequest)
  .ProducesProblem(StatusCodes.Status409Conflict)
  .Produces(StatusCodes.Status404NotFound);

api.MapGet("/audits/{auditId}/findings/{findingId}/review", async (string auditId, string findingId,
    ClaimsPrincipal user, IFindingReviewService reviews, CancellationToken ct) => {
    if(!Guid.TryParse(auditId,out var parsedAuditId)||parsedAuditId==Guid.Empty
        ||!Guid.TryParse(findingId,out var parsedFindingId)||parsedFindingId==Guid.Empty)
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid finding review request.",
            extensions:new Dictionary<string,object?>{{"code","finding-review-id-invalid"}});
    var result=await reviews.GetAsync(parsedAuditId,parsedFindingId,UserId(user),ct);
    return result is null?Results.NotFound():Results.Ok(result);
}).WithName("GetFindingReview")
  .Produces<FindingReviewDto>(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status400BadRequest)
  .Produces(StatusCodes.Status404NotFound);

api.MapPost("/audits/{auditId}/findings/{findingId}/review-requests", async (string auditId,
    string findingId, HttpRequest httpRequest, FindingReviewRequest? request, ClaimsPrincipal user,
    IFindingReviewService reviews, CancellationToken ct) => {
    if(!Guid.TryParse(auditId,out var parsedAuditId)||parsedAuditId==Guid.Empty
        ||!Guid.TryParse(findingId,out var parsedFindingId)||parsedFindingId==Guid.Empty)
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid finding review request.",
            extensions:new Dictionary<string,object?>{{"code","finding-review-id-invalid"}});
    if(!TryIdempotencyKey(httpRequest,out var idempotencyKey)||request is null)
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid finding review command.",
            extensions:new Dictionary<string,object?>{{"code","finding-review-idempotency-key-invalid"}});
    try {
        var result=await reviews.RequestAsync(parsedAuditId,parsedFindingId,UserId(user),idempotencyKey,request,ct);
        if(result is null)return Results.NotFound();
        return result.Replayed?Results.Ok(result):Results.Created($"/api/audits/{parsedAuditId}/findings/{parsedFindingId}/review",result);
    } catch(FindingReviewException exception) { return FindingReviewProblem(exception); }
}).WithName("RequestFindingReview");

api.MapPost("/finding-reviews/{reviewCaseId}/decisions", async (string reviewCaseId,
    HttpRequest httpRequest, FindingReviewDecisionRequest? request, ClaimsPrincipal user,
    IFindingReviewService reviews, CancellationToken ct) => {
    if(!Guid.TryParse(reviewCaseId,out var parsedCaseId)||parsedCaseId==Guid.Empty)
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid finding review decision.",
            extensions:new Dictionary<string,object?>{{"code","finding-review-id-invalid"}});
    if(!TryIdempotencyKey(httpRequest,out var idempotencyKey)||request is null)
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid finding review command.",
            extensions:new Dictionary<string,object?>{{"code","finding-review-idempotency-key-invalid"}});
    try {
        var result=await reviews.DecideAsync(parsedCaseId,UserId(user),idempotencyKey,request,ct);
        if(result is null)return Results.NotFound();
        return result.Replayed?Results.Ok(result):Results.Created($"/api/finding-reviews/{parsedCaseId}",result);
    } catch(FindingReviewException exception) { return FindingReviewProblem(exception); }
}).WithName("DecideFindingReview");

api.MapPost("/finding-reviews/{reviewCaseId}/manual-remediation-reports", async (string reviewCaseId,
    HttpRequest httpRequest, ManualRemediationReportRequest? request, ClaimsPrincipal user,
    IFindingReviewService reviews, CancellationToken ct) => {
    if(!Guid.TryParse(reviewCaseId,out var parsedCaseId)||parsedCaseId==Guid.Empty)
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid manual remediation report.",
            extensions:new Dictionary<string,object?>{{"code","finding-review-id-invalid"}});
    if(!TryIdempotencyKey(httpRequest,out var idempotencyKey)||request is null)
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid finding review command.",
            extensions:new Dictionary<string,object?>{{"code","finding-review-idempotency-key-invalid"}});
    try {
        var result=await reviews.ReportManualRemediationAsync(parsedCaseId,UserId(user),idempotencyKey,request,ct);
        if(result is null)return Results.NotFound();
        return result.Replayed?Results.Ok(result):Results.Created($"/api/finding-reviews/{parsedCaseId}",result);
    } catch(FindingReviewException exception) { return FindingReviewProblem(exception); }
}).WithName("ReportManualFindingRemediation");

api.MapGet("/audits/{auditId:guid}/text-corrections", async (Guid auditId, int? page, int? pageSize,
    ClaimsPrincipal user, ITextCorrectionService corrections, CancellationToken ct) => {
    if(!TextCorrectionProposalQuery.TryCreate(page,pageSize,out var query))
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid text correction query.",
            extensions:new Dictionary<string,object?>{{"code","correction-query-invalid"}});
    var result=await corrections.ListAsync(auditId,UserId(user),query,ct);
    return result is null?Results.NotFound():Results.Ok(result);
}).WithName("ListTextCorrections")
  .WithSummary("Read one DB-paginated language correction proposal page without source excerpts.")
  .Produces<TextCorrectionProposalPage>(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status400BadRequest)
  .Produces(StatusCodes.Status404NotFound);

api.MapGet("/text-corrections/{proposalId:guid}/context", async (Guid proposalId,
    ClaimsPrincipal user,ITextCorrectionService corrections,CancellationToken ct) => {
    try {
        var result=await corrections.ContextAsync(proposalId,UserId(user),ct);
        return result is null?Results.NotFound():Results.Ok(result);
    } catch(TextCorrectionException exception) { return TextCorrectionProblem(exception); }
}).WithName("GetTextCorrectionContext")
  .WithSummary("Materialize bounded exact source context transiently for an authorized admin.")
  .Produces<TextCorrectionProposalContext>(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status409Conflict)
  .Produces(StatusCodes.Status404NotFound);

api.MapGet("/audits/{auditId:guid}/findings/{findingId:guid}/excerpt", async (Guid auditId,
    Guid findingId,ClaimsPrincipal user,IStructuralFindingExcerptService excerpts,CancellationToken ct) => {
    var result=await excerpts.MaterializeAsync(auditId,findingId,UserId(user),ct);
    return result is null?Results.NotFound():Results.Ok(result);
}).WithName("GetStructuralFindingExcerpt")
  .WithSummary("Materialize one bounded exact structural excerpt transiently for an authorized admin.")
  .Produces<StructuralFindingExcerptDto>(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound);

api.MapPost("/text-corrections/{proposalId:guid}/decisions", async (Guid proposalId,
    HttpRequest httpRequest,TextCorrectionDecisionRequest? request,ClaimsPrincipal user,
    ITextCorrectionService corrections,CancellationToken ct) => {
    if(!TryIdempotencyKey(httpRequest,out var key)||request is null)
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid text correction decision.",
            extensions:new Dictionary<string,object?>{{"code","correction-idempotency-key-invalid"}});
    try {
        var result=await corrections.DecideAsync(proposalId,UserId(user),key,request,ct);
        if(result is null)return Results.NotFound();
        return result.Replayed?Results.Ok(result):Results.Created($"/api/text-corrections/{proposalId}/decisions/{result.Id}",result);
    } catch(TextCorrectionException exception) { return TextCorrectionProblem(exception); }
}).WithName("DecideTextCorrection")
  .WithSummary("Append UseSuggestion, EditManual, or Ignore intent for an immutable proposal.")
  .Produces<TextCorrectionDecisionAccepted>(StatusCodes.Status201Created)
  .Produces<TextCorrectionDecisionAccepted>(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status400BadRequest)
  .ProducesProblem(StatusCodes.Status409Conflict)
  .Produces(StatusCodes.Status404NotFound);

api.MapPost("/audits/{auditId:guid}/text-correction-batches", async (Guid auditId,
    HttpRequest httpRequest,TextCorrectionBatchRequest? request,ClaimsPrincipal user,
    ITextCorrectionService corrections,CancellationToken ct) => {
    if(!TryIdempotencyKey(httpRequest,out var key))
        return Results.Problem(statusCode:StatusCodes.Status400BadRequest,title:"Invalid text correction batch.",
            extensions:new Dictionary<string,object?>{{"code","correction-idempotency-key-invalid"}});
    try {
        var result=await corrections.CreateBatchAsync(auditId,UserId(user),key,request??new(),ct);
        if(result is null)return Results.NotFound();
        return result.Replayed?Results.Ok(result):Results.Accepted($"/api/text-correction-batches/{result.Id}",result);
    } catch(TextCorrectionException exception) { return TextCorrectionProblem(exception); }
}).WithName("CreateTextCorrectionBatch")
  .WithSummary("Queue one all-or-nothing correction mutation for active accepted decisions.")
  .Produces<TextCorrectionBatchAccepted>(StatusCodes.Status202Accepted)
  .Produces<TextCorrectionBatchAccepted>(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status400BadRequest)
  .ProducesProblem(StatusCodes.Status409Conflict)
  .Produces(StatusCodes.Status404NotFound);

api.MapGet("/text-correction-batches/{batchId:guid}", async (Guid batchId,ClaimsPrincipal user,
    ITextCorrectionService corrections,CancellationToken ct) => {
    var result=await corrections.GetBatchAsync(batchId,UserId(user),ct);
    return result is null?Results.NotFound():Results.Ok(result);
}).WithName("GetTextCorrectionBatch")
  .WithSummary("Read shared PPKIAdmin correction batch and verification state.")
  .Produces<TextCorrectionBatchStatus>(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound);

api.MapGet("/document-versions/{id:guid}/preview-state", async (Guid id, PpkiDbContext db, CancellationToken ct) => {
    var exists=await db.DocumentVersions.AsNoTracking().AnyAsync(value=>value.Id==id,ct);
    if(!exists)return Results.NotFound();
    var render=await db.DocumentRenderJobs.AsNoTracking()
        .Where(value=>value.DocumentVersionId==id
            && value.RendererId==CanonicalDocumentRenderContract.RendererId
            && value.RendererVersion==CanonicalDocumentRenderContract.RendererVersion
            && value.RendererContractVersion==CanonicalDocumentRenderContract.RendererContractVersion
            && value.FontProfileVersion==CanonicalDocumentRenderContract.FontProfileVersion)
        .Select(value=>new{value.State,value.SafeFailureCode,
            PageCount=value.Artifact==null?(int?)null:value.Artifact.PageCount,
            PreviewAvailable=value.State==DocumentRenderState.Completed&&value.Artifact!=null})
        .SingleOrDefaultAsync(ct);
    return Results.Ok(new DocumentRenderStateDto(render?.State.ToString()??"Pending",render?.PageCount,
        CanonicalDocumentRenderContract.RendererVersion,CanonicalDocumentRenderContract.RendererContractVersion,
        CanonicalDocumentRenderContract.FontProfileVersion,CanonicalDocumentRenderContract.PageMapSchemaVersion,
        render?.SafeFailureCode,render?.PreviewAvailable??false));
}).WithName("GetDocumentPreviewState");

api.MapGet("/document-versions/{id:guid}/preview", async (Guid id, PpkiDbContext db,
    IFileStorage storage,IStorageObjectPathBuilder pathBuilder,IOptions<SupabaseOptions> supabase,CancellationToken ct) => {
    var artifact=await db.DocumentRenderArtifacts.AsNoTracking()
        .Where(value=>value.DocumentVersionId==id
            && value.RenderJob!.State==DocumentRenderState.Completed
            && value.RendererId==CanonicalDocumentRenderContract.RendererId
            && value.RendererVersion==CanonicalDocumentRenderContract.RendererVersion
            && value.RendererContractVersion==CanonicalDocumentRenderContract.RendererContractVersion
            && value.FontProfileVersion==CanonicalDocumentRenderContract.FontProfileVersion)
        .Select(value=>new{value.StorageBucket,value.StorageKey,value.PdfSha256,value.SizeBytes,
            value.RenderJobId,DocumentId=value.DocumentVersion!.DocumentId,
            OwnerUserId=value.DocumentVersion.Document!.OwnerUserId})
        .SingleOrDefaultAsync(ct);
    if(artifact is null)return Results.NotFound();
    var expectedPath=pathBuilder.BuildDocumentPreviewPath(artifact.OwnerUserId,artifact.DocumentId,artifact.RenderJobId);
    if(artifact.StorageBucket!=supabase.Value.Storage.ReportBucket||artifact.StorageKey!=expectedPath)
        return Results.NotFound();
    try {
        var bytes=await storage.ReadBytesAsync(artifact.StorageBucket,artifact.StorageKey,50L*1024*1024,ct);
        var sha=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        if(bytes.LongLength!=artifact.SizeBytes||!StringComparer.Ordinal.Equals(sha,artifact.PdfSha256))
            return Results.Problem(statusCode:StatusCodes.Status502BadGateway,title:"Document preview is unavailable.");
        return Results.File(bytes,"application/pdf",enableRangeProcessing:true);
    } catch(FileStorageException exception) when(exception.Kind==FileStorageFailureKind.NotFound) { return Results.NotFound(); }
      catch { return Results.Problem(statusCode:StatusCodes.Status502BadGateway,title:"Document preview is unavailable."); }
}).WithName("GetDocumentPreview");

api.MapGet("/document-versions/{id:guid}/download", async (Guid id, ClaimsPrincipal user, PpkiDbContext db, IFileStorage storage, IStorageObjectPathBuilder pathBuilder, IAuditTrailWriter auditTrail, IOptions<SupabaseOptions> supabase, CancellationToken ct) => {
    var uid=UserId(user); var version=await db.DocumentVersions.AsNoTracking().Where(v=>v.Id==id).Select(v=>new{Version=v,OwnerUserId=v.Document!.OwnerUserId}).SingleOrDefaultAsync(ct); if(version is null)return Results.NotFound();
    var isOriginal=version.Version.ParentVersionId is null;
    var expected=isOriginal
        ? pathBuilder.BuildOriginalPath(version.OwnerUserId,version.Version.DocumentId,version.Version.Id)
        : pathBuilder.BuildVersionPath(version.OwnerUserId,version.Version.DocumentId,version.Version.Id);
    var expectedBucket=isOriginal?supabase.Value.Storage.OriginalBucket:supabase.Value.Storage.VersionBucket;
    if(version.Version.StorageBucket!=expectedBucket||version.Version.StorageKey!=expected)return Results.NotFound();
    var lifetime=TimeSpan.FromSeconds(supabase.Value.Storage.SignedUrlLifetimeSeconds); string url;
    try { url=await storage.CreateSignedDownloadUrlAsync(version.Version.StorageBucket,version.Version.StorageKey,lifetime,ct); }
    catch { return Results.Problem(statusCode:StatusCodes.Status502BadGateway,title:"Document download authorization failed."); }
    var eventContext=AuditEventContext.User(uid,Guid.NewGuid()); await using var transaction=await db.Database.BeginTransactionAsync(ct); await auditTrail.SetTransactionContextAsync(db,eventContext,ct);
    auditTrail.Add(db,eventContext,new AuditEventData(AuditActions.DocumentDownloadAuthorized,AuditResourceTypes.DocumentVersion,version.Version.Id,version.OwnerUserId,AuditEventMetadata.Create(("download_kind",isOriginal?"original":"remediated")))); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    return Results.Ok(new{url,expiresInSeconds=(int)lifetime.TotalSeconds});
});

app.Run();

static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Missing user id claim."));
static bool TryIdempotencyKey(HttpRequest request,out Guid key) {
    key=Guid.Empty;
    var header=request.Headers["Idempotency-Key"];
    return header.Count==1&&Guid.TryParse(header[0],out key)&&key!=Guid.Empty;
}
static IResult FindingReviewProblem(FindingReviewException exception) {
    var status=exception.DiagnosticCode switch {
        "finding-review-note-invalid" or "finding-review-reason-required" or "finding-review-idempotency-key-invalid" or "finding-review-not-available"=>StatusCodes.Status400BadRequest,
        _=>StatusCodes.Status409Conflict};
    return Results.Problem(statusCode:status,title:"Finding review command was rejected.",
        extensions:new Dictionary<string,object?>{{"code",exception.DiagnosticCode}});
}

static IResult TextCorrectionProblem(TextCorrectionException exception) {
    var status=exception.DiagnosticCode switch {
        "correction-idempotency-key-invalid" or "correction-query-invalid" or "correction-decision-invalid"
            or "correction-replacement-invalid" or "correction-batch-size-invalid"=>StatusCodes.Status400BadRequest,
        _=>StatusCodes.Status409Conflict};
    return Results.Problem(statusCode:status,title:"Text correction command was rejected.",
        extensions:new Dictionary<string,object?>{{"code",exception.DiagnosticCode}});
}

static async Task TryWriteOrphanCleanupAsync(IDbContextFactory<PpkiDbContext> dbFactory, IAuditTrailWriter auditTrail, AuditEventContext context, Guid versionId, Guid ownerUserId, CancellationToken ct) {
    try {
        var serviceContext=AuditEventContext.Service("api",context.CorrelationId,context.CausationId);
        await using var db=await dbFactory.CreateDbContextAsync(ct); await using var transaction=await db.Database.BeginTransactionAsync(ct); await auditTrail.SetTransactionContextAsync(db,serviceContext,ct);
        auditTrail.Add(db,serviceContext,new AuditEventData(AuditActions.StorageOrphanCleanup,AuditResourceTypes.StorageObject,versionId,ownerUserId,AuditEventMetadata.Create(("cleanup_reason","database_insert_failed"))));
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    } catch { }
}
