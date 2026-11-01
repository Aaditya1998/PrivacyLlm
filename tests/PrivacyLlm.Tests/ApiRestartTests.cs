using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace PrivacyLlm.Tests;

/// <summary>
/// Boots the real API twice against the same SQLite file to prove that consent, the audit trail and
/// encrypted payloads (including the Data Protection keys needed to decrypt them) survive a restart.
/// </summary>
public sealed class ApiRestartTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"privacyllm-test-{Guid.NewGuid():N}.db");

    private WebApplicationFactory<Program> Boot() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Storage:Provider", "Sqlite");
        b.UseSetting("ConnectionStrings:PrivacyLlm", $"Data Source={_dbPath}");
        // Port 9 (discard) refuses connections, so jobs fail fast and stay queued instead of calling a model.
        b.UseSetting("SelfHostedLlm:BaseUrl", "http://127.0.0.1:9");
        b.UseSetting("FrontierLlm:Enabled", "false");
    });

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-shm", _dbPath + "-wal" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public async Task Consent_audit_and_payloads_survive_restart()
    {
        string payloadId;
        using (var first = Boot())
        {
            var http = first.CreateClient();
            (await http.PostAsJsonAsync("/api/consents", new { dataPrincipalId = "cust-42", purpose = "order-support", basis = "VoluntarilyProvided" })).EnsureSuccessStatusCode();

            // Rejected task -> one audit event.
            await http.PostAsJsonAsync("/api/tasks/sync", new
            {
                taskId = "t-marketing", taskType = "x", purpose = "marketing", dataPrincipalId = "cust-42", systemPrompt = "s", input = "call 9876543210",
            });

            var queued = await http.PostAsJsonAsync("/api/tasks", new
            {
                taskId = "t-queued", taskType = "x", purpose = "order-support", dataPrincipalId = "cust-42", systemPrompt = "s", input = "call 9876543210",
            });
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
            payloadId = (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("payloadId").GetString()!;
        }

        using var second = Boot();
        var http2 = second.CreateClient();

        var consents = await http2.GetFromJsonAsync<JsonElement>("/api/consents/cust-42");
        Assert.Equal("VoluntarilyProvided", consents[0].GetProperty("basis").GetString());

        var audit = await http2.GetFromJsonAsync<JsonElement>("/api/audit?principalId=cust-42");
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("taskId").GetString() == "t-marketing"
            && e.GetProperty("outcome").GetString() == "RejectedNoConsent");

        // Decrypting the payload needs the key ring from the first boot, which now lives in the database.
        var status = await http2.GetFromJsonAsync<JsonElement>($"/api/tasks/{payloadId}");
        Assert.Equal("pending", status.GetProperty("status").GetString());

        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        using var cmd = raw.CreateCommand();

        // The key ring must be in the database itself, not just in the machine-wide default folder
        // (which would make the decrypt above pass on one box and fail on a second instance).
        cmd.CommandText = "SELECT COUNT(*) FROM DataProtectionKeys";
        Assert.True((long)cmd.ExecuteScalar()! > 0);

        cmd.CommandText = "SELECT Data FROM ProtectedBlobs";
        foreach (var blob in ReadAll(cmd))
            Assert.DoesNotContain("9876543210", System.Text.Encoding.UTF8.GetString(blob));
    }

    private static IEnumerable<byte[]> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return (byte[])r[0];
    }
}
