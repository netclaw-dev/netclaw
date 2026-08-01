// -----------------------------------------------------------------------
// <copyright file="SecurityServiceExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Security.Skills;
using ShellSyntaxTree;

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
        // Skill content scanning is temporarily disabled (see netclaw-dev/netclaw issue
        // for the hardening tracker): the regex detector false-positives on legitimate
        // ops documentation — e.g. "re-enable after fixing the root cause" trips the
        // PrivilegeEscalation pattern — and we re-scan trusted local skills at every
        // skill_load invocation, which keeps reopening that false-positive surface.
        // The RegexSkillContentScanner class is retained for tests and for the
        // eventual re-enable once trust-tier / scanner hardening lands.
        services.AddSingleton<ISkillContentScanner, NoOpSkillContentScanner>();
        return services;
    }

    /// <summary>
    /// Registers the canonical execution environment and its matching parser.
    /// </summary>
    public static IServiceCollection AddShellParser(this IServiceCollection services)
    {
        services.AddSingleton(ShellExecutionEnvironment.Current);
        services.AddSingleton<IShellParser>(ShellExecutionEnvironment.Current.Parser);
        return services;
    }
}
