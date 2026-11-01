using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PrivacyLlm.Api.Auth;

/// <summary>What a key may do. Each endpoint requires exactly one.</summary>
public static class Scopes
{
    public const string Tasks = "tasks";               // queue tasks, run sync, classify, read own results
    public const string ConsentWrite = "consent:write"; // grant and withdraw consent
    public const string ConsentRead = "consent:read";
    public const string AuditRead = "audit:read";

    public static readonly string[] All = { Tasks, ConsentWrite, ConsentRead, AuditRead };
}

public sealed class ApiClient
{
    /// <summary>
    /// Stable client name; owns the tasks it queues and appears in logs. Several entries with the same Name are
    /// one client with several keys (used for rotation).
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>Hex SHA-256 of the key. The key itself is never stored in configuration.</summary>
    public string KeySha256 { get; set; } = "";

    public List<string> Scopes { get; set; } = new();
}

public sealed class ApiKeyOptions : AuthenticationSchemeOptions
{
    public const string Section = "Auth";
    public const string Scheme = "ApiKey";
    public const string Header = "X-Api-Key";

    public List<ApiClient> Clients { get; set; } = new();
}

/// <summary>
/// Authenticates the <c>X-Api-Key</c> header against hashed keys in configuration. Hashing means a leaked
/// appsettings file or config dump does not leak usable keys; keys are random 256-bit values, so an
/// unsalted SHA-256 is enough (there is nothing to brute-force).
/// </summary>
public sealed class ApiKeyHandler(IOptionsMonitor<ApiKeyOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyOptions>(options, logger, encoder)
{
    public const string ScopeClaim = "scope";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyOptions.Header, out var values) || values.Count != 1 || string.IsNullOrEmpty(values[0]))
            return Task.FromResult(AuthenticateResult.NoResult());

        var presented = ApiKeys.Hash(values[0]!);
        ApiClient? match = null;

        // Compare against every configured hash in constant time, so timing reveals nothing about which (if any) matched.
        foreach (var client in Options.Clients)
        {
            if (ApiKeys.HashesEqual(presented, client.KeySha256)) match ??= client;
        }

        if (match is null)
        {
            Logger.LogWarning("Rejected API key from {Remote}", Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("invalid API key"));
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, match.Name) };
        claims.AddRange(match.Scopes.Select(s => new Claim(ScopeClaim, s)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyOptions.Scheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, ApiKeyOptions.Scheme)));
    }
}

public static class ApiKeys
{
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static bool HashesEqual(string presentedHex, string configuredHex)
    {
        if (string.IsNullOrEmpty(configuredHex)) return false;
        var a = Encoding.ASCII.GetBytes(presentedHex.ToUpperInvariant());
        var b = Encoding.ASCII.GetBytes(configuredHex.Trim().ToUpperInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>A new random key (256 bits, URL-safe) and the hash to put in configuration.</summary>
    public static (string Key, string Sha256) Generate()
    {
        var key = "pllm_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (key, Hash(key));
    }

    /// <summary>Rejects configurations that would silently weaken auth: duplicate names or hashes, unknown scopes, malformed hashes.</summary>
    public static void Validate(ApiKeyOptions options)
    {
        var errors = new List<string>();
        foreach (var c in options.Clients)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) errors.Add("a client has no Name");
            if (c.KeySha256.Trim().Length != 64 || !c.KeySha256.Trim().All(Uri.IsHexDigit)) errors.Add($"client '{c.Name}': KeySha256 must be 64 hex characters");
            foreach (var s in c.Scopes.Where(s => !Scopes.All.Contains(s))) errors.Add($"client '{c.Name}': unknown scope '{s}'");
        }

        // Entries may share a Name: that is one client with several keys, which is how keys are rotated without the
        // client losing access to tasks it queued under the old key. The same key twice is always a mistake.
        foreach (var dup in options.Clients.GroupBy(c => c.KeySha256.Trim().ToUpperInvariant()).Where(g => g.Count() > 1)) errors.Add("two entries share a key");

        if (errors.Count > 0) throw new InvalidOperationException("Invalid Auth configuration: " + string.Join("; ", errors));
    }
}
