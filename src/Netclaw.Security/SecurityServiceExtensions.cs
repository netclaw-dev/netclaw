using Microsoft.Extensions.DependencyInjection;
using Netclaw.Security.Skills;

namespace Netclaw.Security;

public static class SecurityServiceExtensions
{
    /// <summary>
    /// Registers default security services.
    /// </summary>
    public static IServiceCollection AddContentSecurity(this IServiceCollection services)
    {
        services.AddSingleton<ContentPolicy>();
        services.AddSingleton<IContentScanner, MagicByteContentScanner>();
        services.AddSingleton<IPromptInjectionDetector, NullPromptInjectionDetector>();
        services.AddSingleton<ISkillContentScanner, NoOpSkillContentScanner>();
        return services;
    }
}
