using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using PrivacyLlm.Api.Auth;
using PrivacyLlm.Api.Jobs;
using PrivacyLlm.Core.Audit;
using PrivacyLlm.Core.Classification;
using PrivacyLlm.Core.Dpdp;
using PrivacyLlm.Core.Pipeline;
using PrivacyLlm.Core.Policy;
using PrivacyLlm.Infrastructure;
using PrivacyLlm.Persistence;

// `dotnet run -- generate-api-key`: print a new key (give it to the client) and its hash (put it in config).
if (args is ["generate-api-key", ..])
{
    var (key, hash) = ApiKeys.Generate();
    Console.WriteLine($"API key (give to the client, shown once): {key}");
    Console.WriteLine($"KeySha256 (put in Auth:Clients config):  {hash}");
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Authentication: every endpoint requires a valid API key unless it explicitly allows anonymous access,
// and each /api endpoint additionally requires one scope.
var authOptions = builder.Configuration.GetSection(ApiKeyOptions.Section).Get<ApiKeyOptions>() ?? new ApiKeyOptions();
ApiKeys.Validate(authOptions);
builder.Services.AddAuthentication(ApiKeyOptions.Scheme)
    .AddScheme<ApiKeyOptions, ApiKeyHandler>(ApiKeyOptions.Scheme, o => o.Clients = authOptions.Clients);
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder(ApiKeyOptions.Scheme).RequireAuthenticatedUser().Build())
    .AddPolicy(Scopes.Tasks, p => p.RequireClaim(ApiKeyHandler.ScopeClaim, Scopes.Tasks))
    .AddPolicy(Scopes.ConsentWrite, p => p.RequireClaim(ApiKeyHandler.ScopeClaim, Scopes.ConsentWrite))
    .AddPolicy(Scopes.ConsentRead, p => p.RequireClaim(ApiKeyHandler.ScopeClaim, Scopes.ConsentRead))
    .AddPolicy(Scopes.AuditRead, p => p.RequireClaim(ApiKeyHandler.ScopeClaim, Scopes.AuditRead));

// Each task can cost a model call, so creation is limited per client (not per IP: clients are servers).
var tasksPerMinute = builder.Configuration.GetValue("RateLimiting:TasksPerMinute", 60);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(TasksRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.Identity?.Name ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = tasksPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddPrivacyLlm(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDataProtection();
var storage = builder.Services.AddPrivacyLlmStorage(builder.Configuration);

// A durable audit trail needs a stable HMAC key, or InputHash values from before a restart can never be matched again.
// The key must not live in the audited database: the phone-number space is small enough to brute-force with it.
var auditKeyMissing = storage != StorageProvider.InMemory && string.IsNullOrEmpty(builder.Configuration["Audit:HashKey"]);
if (auditKeyMissing && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Audit:HashKey (base64, 32 bytes) is required when Storage:Provider is not InMemory.");
builder.Services.AddSingleton<ProtectedPayloadStore>();
builder.Services.AddTransient<LlmTaskJob>();
builder.Services.AddTransient<MaintenanceJobs>();

var hangfireSql = builder.Configuration.GetConnectionString("Hangfire");
builder.Services.AddHangfire(cfg =>
{
    cfg.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings();
    if (string.IsNullOrEmpty(hangfireSql)) cfg.UseInMemoryStorage(new InMemoryStorageOptions());
    else cfg.UseSqlServerStorage(hangfireSql);
});
builder.Services.AddHangfireServer(o =>
{
    o.Queues = new[] { "llm", "default" };
    // Bound concurrency to what one vLLM replica serves comfortably; vLLM batches internally.
    o.WorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", 8);
});

var app = builder.Build();

app.Services.EnsurePrivacyLlmStorageCreated();

// Load the name lists now: a missing or truncated list must stop the host, not quietly stop detecting names.
app.Services.GetRequiredService<IPiiDetector>();
app.Logger.LogInformation("Storage: {Provider}; Hangfire: {Hangfire}", storage, string.IsNullOrEmpty(hangfireSql) ? "in-memory" : "SQL Server");
if (storage != StorageProvider.InMemory && string.IsNullOrEmpty(hangfireSql))
    app.Logger.LogWarning("Payloads are durable but the Hangfire queue is in-memory: queued jobs are lost on restart and their payloads expire unprocessed. Set ConnectionStrings:Hangfire for a durable queue.");
if (authOptions.Clients.Count == 0)
    app.Logger.LogWarning("No API clients configured (Auth:Clients): every /api request will be rejected. Create a key with: dotnet run -- generate-api-key");
if (auditKeyMissing)
    app.Logger.LogWarning("Audit:HashKey is not set; using a random per-process key. Audit InputHash values will not match across restarts (allowed in Development only).");

app.UseHangfireDashboard("/hangfire", new DashboardOptions { Authorization = new[] { new LocalRequestsOnlyAuthorizationFilter() } });

// Resolve from DI rather than the static RecurringJob API, which uses process-wide JobStorage.Current.
var recurring = app.Services.GetRequiredService<IRecurringJobManager>();
recurring.AddOrUpdate<MaintenanceJobs>("purge-expired-payloads", j => j.PurgeExpiredPayloads(), Cron.Hourly());
recurring.AddOrUpdate<MaintenanceJobs>("inference-health", j => j.CheckInferenceHealthAsync(CancellationToken.None), "*/5 * * * *");

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/", () => Results.Redirect("/hangfire")).AllowAnonymous();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

var api = app.MapGroup("/api");

// Dry run: what would happen to this payload? No model is called.
api.MapPost("/classify", (ClassifyRequest req, IDataClassifier classifier, IRoutingPolicy routing) =>
{
    var c = classifier.Classify(req.Text, req.DeclaredFields);
    var d = routing.Decide(req.TaskType ?? "general", c);
    return Results.Ok(new
    {
        c.Sensitivity,
        c.Reasons,
        piiCounts = c.CountsByType,
        matches = c.Matches.Select(m => new { m.Type, m.Start, m.Length, m.Confidence }), // spans only, never echo values
        routing = d,
    });
}).RequireAuthorization(Scopes.Tasks);

api.MapPost("/tasks", (LlmTask task, ClaimsPrincipal user, ProtectedPayloadStore store, IBackgroundJobClient jobs) =>
{
    // Validate before enqueueing: an invalid id would otherwise fail at the audit write after the model call, and every Hangfire retry would call the model again.
    if (InputLimits.Validate(task) is { Count: > 0 } errors) return Results.ValidationProblem(errors);
    var payloadId = store.Put(new QueuedTask(user.Identity!.Name!, task), TimeSpan.FromDays(2));
    var jobId = jobs.Enqueue<LlmTaskJob>(j => j.RunAsync(payloadId, CancellationToken.None));
    return Results.Accepted($"/api/tasks/{payloadId}", new { payloadId, jobId });
}).RequireAuthorization(Scopes.Tasks).RequireRateLimiting(TasksRateLimit);

// Only the client that queued a task can see it; anyone else gets the same 404 as for an unknown id.
api.MapGet("/tasks/{payloadId}", (string payloadId, ClaimsPrincipal user, ProtectedPayloadStore store) =>
{
    var owner = user.Identity!.Name!;
    var result = store.Get<OwnedResult>(LlmTaskJob.ResultKey(payloadId));
    if (result is not null) return result.Owner == owner && result.Result is not null ? Results.Ok(result.Result) : Results.NotFound();
    return store.Get<QueuedTask>(payloadId) is { } queued && queued.Owner == owner ? Results.Ok(new { status = "pending" }) : Results.NotFound();
}).RequireAuthorization(Scopes.Tasks);

api.MapPost("/tasks/sync", async (LlmTask task, IPrivacyGateway gateway, CancellationToken ct) =>
    InputLimits.Validate(task) is { Count: > 0 } errors
        ? Results.ValidationProblem(errors)
        : Results.Ok(await gateway.ExecuteAsync(task, ct)))
    .RequireAuthorization(Scopes.Tasks).RequireRateLimiting(TasksRateLimit);

api.MapPost("/consents", (GrantConsentRequest req, ClaimsPrincipal user, IConsentRegistry registry, ILogger<Program> log) =>
{
    if (InputLimits.ValidateConsent(req.DataPrincipalId, req.Purpose, req.ConsentManagerRef) is { Count: > 0 } errors) return Results.ValidationProblem(errors);
    var record = registry.Grant(req.DataPrincipalId, req.Purpose, req.Basis, req.ConsentManagerRef);
    log.LogInformation("Consent {Basis} recorded for {Principal}, purpose {Purpose}, by client {Client}", req.Basis, req.DataPrincipalId, record.Purpose, user.Identity!.Name);
    return Results.Ok(record);
}).RequireAuthorization(Scopes.ConsentWrite);

api.MapDelete("/consents/{principalId}/{purpose}", (string principalId, string purpose, ClaimsPrincipal user, IConsentRegistry registry, ILogger<Program> log) =>
{
    if (InputLimits.ValidateConsent(principalId, purpose, null) is { Count: > 0 } errors) return Results.ValidationProblem(errors);
    if (!registry.Withdraw(principalId, purpose)) return Results.NotFound();
    log.LogInformation("Consent withdrawn for {Principal}, purpose {Purpose}, by client {Client}", principalId, purpose, user.Identity!.Name);
    return Results.NoContent();
}).RequireAuthorization(Scopes.ConsentWrite);

api.MapGet("/consents/{principalId}", (string principalId, IConsentRegistry registry) =>
    InputLimits.IsValidIdentifier(principalId) ? Results.Ok(registry.ForPrincipal(principalId)) : Results.BadRequest())
    .RequireAuthorization(Scopes.ConsentRead);

// DPDP s.11: a data principal may ask for a summary of the processing of their data.
api.MapGet("/audit", (string? principalId, int? take, IAuditLog audit) =>
    principalId is not null && !InputLimits.IsValidIdentifier(principalId)
        ? Results.BadRequest()
        : Results.Ok(audit.Query(principalId, Math.Clamp(take ?? 100, 1, 1000))))
    .RequireAuthorization(Scopes.AuditRead);

app.Run();

public sealed record ClassifyRequest(string Text, string? TaskType = null, List<string>? DeclaredFields = null);

public sealed record GrantConsentRequest(string DataPrincipalId, string Purpose, LawfulBasis Basis = LawfulBasis.Consent, string? ConsentManagerRef = null);

public partial class Program
{
    public const string TasksRateLimit = "tasks";
}
