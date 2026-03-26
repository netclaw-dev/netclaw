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
        services.AddSingleton<IPromptInjectionDetector, RegexPromptInjectionDetector>();
        services.AddSingleton<RegexSkillContentScanner>();
        services.AddSingleton<ISkillContentScanner>(sp =>
            new CachingSkillContentScanner(sp.GetRequiredService<RegexSkillContentScanner>()));
        return services;
    }
}
