using System.Text.Json.Serialization;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.InMemory;
using PrivacyLlm.Api.Jobs;
using PrivacyLlm.Core.Audit;
using PrivacyLlm.Core.Classification;
using PrivacyLlm.Core.Dpdp;
using PrivacyLlm.Core.Pipeline;
using PrivacyLlm.Core.Policy;
using PrivacyLlm.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPrivacyLlm(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDataProtection();
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

app.UseHangfireDashboard("/hangfire", new DashboardOptions { Authorization = new[] { new LocalRequestsOnlyAuthorizationFilter() } });

RecurringJob.AddOrUpdate<MaintenanceJobs>("purge-expired-payloads", j => j.PurgeExpiredPayloads(), Cron.Hourly());
RecurringJob.AddOrUpdate<MaintenanceJobs>("inference-health", j => j.CheckInferenceHealthAsync(CancellationToken.None), "*/5 * * * *");

app.MapGet("/", () => Results.Redirect("/hangfire"));

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
});

api.MapPost("/tasks", (LlmTask task, ProtectedPayloadStore store, IBackgroundJobClient jobs) =>
{
    var payloadId = store.Put(task, TimeSpan.FromDays(2));
    var jobId = jobs.Enqueue<LlmTaskJob>(j => j.RunAsync(payloadId, CancellationToken.None));
    return Results.Accepted($"/api/tasks/{payloadId}", new { payloadId, jobId });
});

api.MapGet("/tasks/{payloadId}", (string payloadId, ProtectedPayloadStore store) =>
{
    var result = store.Get<LlmTaskResult>(LlmTaskJob.ResultKey(payloadId));
    if (result is not null) return Results.Ok(result);
    return store.Get<LlmTask>(payloadId) is not null ? Results.Ok(new { status = "pending" }) : Results.NotFound();
});

api.MapPost("/tasks/sync", async (LlmTask task, IPrivacyGateway gateway, CancellationToken ct) =>
    Results.Ok(await gateway.ExecuteAsync(task, ct)));

api.MapPost("/consents", (GrantConsentRequest req, IConsentRegistry registry) =>
    Results.Ok(registry.Grant(req.DataPrincipalId, req.Purpose, req.Basis, req.ConsentManagerRef)));

api.MapDelete("/consents/{principalId}/{purpose}", (string principalId, string purpose, IConsentRegistry registry) =>
    registry.Withdraw(principalId, purpose) ? Results.NoContent() : Results.NotFound());

api.MapGet("/consents/{principalId}", (string principalId, IConsentRegistry registry) => Results.Ok(registry.ForPrincipal(principalId)));

// DPDP s.11: a data principal may ask for a summary of the processing of their data.
api.MapGet("/audit", (string? principalId, int? take, IAuditLog audit) => Results.Ok(audit.Query(principalId, take ?? 100)));

app.Run();

public sealed record ClassifyRequest(string Text, string? TaskType = null, List<string>? DeclaredFields = null);

public sealed record GrantConsentRequest(string DataPrincipalId, string Purpose, LawfulBasis Basis = LawfulBasis.Consent, string? ConsentManagerRef = null);

public partial class Program;
