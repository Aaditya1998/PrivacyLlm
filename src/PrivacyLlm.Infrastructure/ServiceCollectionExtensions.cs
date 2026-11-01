using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrivacyLlm.Core.Audit;
using PrivacyLlm.Core.Classification;
using PrivacyLlm.Core.Dpdp;
using PrivacyLlm.Core.Inference;
using PrivacyLlm.Core.Pipeline;
using PrivacyLlm.Core.Policy;
using PrivacyLlm.Core.Storage;

namespace PrivacyLlm.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPrivacyLlm(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SelfHostedLlmOptions>(config.GetSection(SelfHostedLlmOptions.Section));
        services.Configure<FrontierLlmOptions>(config.GetSection(FrontierLlmOptions.Section));
        services.Configure<RoutingOptions>(config.GetSection(RoutingOptions.Section));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RoutingOptions>>().Value);

        services.TryAddSingletonTimeProvider();
        services.AddSingleton<IPiiDetector, RegexPiiDetector>();
        services.AddSingleton<IDataClassifier, DataClassifier>();
        services.AddSingleton<IRoutingPolicy, PrivacyRoutingPolicy>();
        services.AddSingleton<IConsentRegistry, InMemoryConsentRegistry>();
        services.AddSingleton<IAuditLog, InMemoryAuditLog>();
        services.AddSingleton<IExpiringBlobStore, InMemoryBlobStore>();

        var hashKey = config["Audit:HashKey"];
        services.AddSingleton(new GatewaySecrets(string.IsNullOrEmpty(hashKey) ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(hashKey)));

        services.AddHttpClient<VllmClient>();
        services.AddTransient<ILlmClient>(sp => sp.GetRequiredService<VllmClient>());

        if (config.GetValue(FrontierLlmOptions.Section + ":Enabled", true))
        {
            services.AddHttpClient<FrontierClient>();
            services.AddTransient<ILlmClient>(sp => sp.GetRequiredService<FrontierClient>());
        }

        services.AddTransient<IPrivacyGateway, PrivacyGateway>();
        return services;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(TimeProvider))) services.AddSingleton(TimeProvider.System);
    }
}
