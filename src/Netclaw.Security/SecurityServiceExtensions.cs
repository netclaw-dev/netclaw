using Microsoft.Extensions.DependencyInjection;

namespace Netclaw.Security;

public static class SecurityServiceExtensions
{
    /// <summary>
    /// Registers content security services with no-op defaults.
    /// Replace individual registrations to plug in real scanning.
    /// </summary>
    public static IServiceCollection AddContentSecurity(this IServiceCollection services)
    {
        services.AddSingleton<ContentPolicy>();
        services.AddSingleton<IContentScanner, NullContentScanner>();
        services.AddSingleton<IPromptInjectionDetector, NullPromptInjectionDetector>();
        return services;
    }
}
