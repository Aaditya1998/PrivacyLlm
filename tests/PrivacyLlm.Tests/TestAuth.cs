using Microsoft.AspNetCore.Hosting;
using PrivacyLlm.Api.Auth;

namespace PrivacyLlm.Tests;

/// <summary>
/// Hangfire keeps process-wide state (including its logger), so API hosts started and stopped in parallel can
/// hand a request a disposed logger and fail it with 500. Every test class that boots the API joins this collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiHostCollection
{
    public const string Name = "api-hosts";
}

/// <summary>Configures API clients on a test host and builds authenticated HttpClients.</summary>
internal static class TestAuth
{
    public const string FullKey = "test-key-full-access-0123456789abcdef";

    /// <summary>
    /// Registers a client named <paramref name="name"/> with the given key and scopes at index <paramref name="index"/>.
    /// Uses the "Testing" environment so a developer's user-secrets (which may hold real Auth:Clients entries)
    /// are not merged into the test's client list.
    /// </summary>
    public static IWebHostBuilder WithClient(this IWebHostBuilder b, int index, string name, string key, params string[] scopes)
    {
        b.UseEnvironment("Testing");
        b.UseSetting($"Auth:Clients:{index}:Name", name);
        b.UseSetting($"Auth:Clients:{index}:KeySha256", ApiKeys.Hash(key));
        for (var i = 0; i < scopes.Length; i++) b.UseSetting($"Auth:Clients:{index}:Scopes:{i}", scopes[i]);
        return b;
    }

    public static IWebHostBuilder WithFullAccessClient(this IWebHostBuilder b) => b.WithClient(0, "test", FullKey, Scopes.All);

    public static HttpClient WithKey(this HttpClient http, string key)
    {
        http.DefaultRequestHeaders.Add("X-Api-Key", key);
        return http;
    }
}
