// -----------------------------------------------------------------------
// <copyright file="ShellExecutionEnvironmentResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using ShellSyntaxTree;

namespace Netclaw.Daemon;

internal enum PowerShellFallbackReason
{
    PreferredHostNotFound,
    PreferredVersionUnsupported,
    PreferredHostProbeFailed
}

internal sealed record ShellEnvironmentResolution(
    ShellExecutionEnvironment Environment,
    PowerShellFallbackReason? FallbackReason = null,
    Version? RejectedPreferredVersion = null);

internal sealed class ShellExecutionEnvironmentResolver(IPowerShellHostProbe powerShellProbe)
{
    private static readonly Version MinimumPowerShell7 = new(7, 6, 4);
    private static readonly Version PowerShell7UpperBound = new(7, 7);

    public static ShellExecutionEnvironmentResolver CreateDefault(TimeProvider timeProvider) =>
        new(new PowerShellHostProbe(
            timeProvider,
            new WindowsPathPowerShellExecutableLocator(),
            new PowerShellProbeProcessFactory()));

    public static ShellPlatform DetectCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
            return ShellPlatform.Windows;
        if (OperatingSystem.IsLinux())
            return ShellPlatform.Linux;
        if (OperatingSystem.IsMacOS())
            return ShellPlatform.MacOS;

        throw new PlatformNotSupportedException(
            "Netclaw supports native shell execution only on Windows, Linux, and macOS.");
    }

    public async Task<ShellEnvironmentResolution> ResolveAsync(
        ShellPlatform platform,
        CancellationToken cancellationToken = default)
    {
        if (platform is ShellPlatform.Linux or ShellPlatform.MacOS)
            return new ShellEnvironmentResolution(ShellExecutionEnvironment.CreateBash(platform));
        if (platform != ShellPlatform.Windows)
            throw new ArgumentOutOfRangeException(nameof(platform), platform, "The shell platform is not supported.");

        var preferred = await powerShellProbe.ProbeAsync("pwsh.exe", cancellationToken)
            .ConfigureAwait(false);
        if (preferred is PowerShellHostProbeResult.Found preferredFound
            && IsSupportedPowerShell7(preferredFound.Version))
        {
            return new ShellEnvironmentResolution(
                ShellExecutionEnvironment.CreatePowerShell(
                    preferredFound.ExecutablePath,
                    PwshDialect.PowerShell7));
        }

        var (fallbackReason, rejectedVersion) = preferred switch
        {
            PowerShellHostProbeResult.NotFound =>
                (PowerShellFallbackReason.PreferredHostNotFound, (Version?)null),
            PowerShellHostProbeResult.Found found =>
                (PowerShellFallbackReason.PreferredVersionUnsupported, found.Version),
            PowerShellHostProbeResult.Failed =>
                (PowerShellFallbackReason.PreferredHostProbeFailed, (Version?)null),
            _ => throw new InvalidOperationException("The preferred PowerShell probe returned an unknown result.")
        };

        var fallback = await powerShellProbe.ProbeAsync("powershell.exe", cancellationToken)
            .ConfigureAwait(false);
        if (fallback is PowerShellHostProbeResult.Found fallbackFound
            && IsWindowsPowerShell51(fallbackFound.Version))
        {
            return new ShellEnvironmentResolution(
                ShellExecutionEnvironment.CreatePowerShell(
                    fallbackFound.ExecutablePath,
                    PwshDialect.WindowsPowerShell51),
                fallbackReason,
                rejectedVersion);
        }

        var preferredDescription = preferred switch
        {
            PowerShellHostProbeResult.NotFound => "pwsh.exe was not found",
            PowerShellHostProbeResult.Found found =>
                $"pwsh.exe reported unsupported version {found.Version}",
            PowerShellHostProbeResult.Failed failed =>
                $"pwsh.exe probe failed ({failed.Failure})",
            _ => "pwsh.exe was unavailable"
        };
        var fallbackDescription = fallback switch
        {
            PowerShellHostProbeResult.NotFound => "powershell.exe was not found",
            PowerShellHostProbeResult.Found found =>
                $"powershell.exe reported unsupported version {found.Version}",
            PowerShellHostProbeResult.Failed failed =>
                $"powershell.exe probe failed ({failed.Failure})",
            _ => "powershell.exe was unavailable"
        };

        throw new InvalidOperationException(
            $"No compatible Windows PowerShell host is available: {preferredDescription}; "
            + $"{fallbackDescription}. Install PowerShell >=7.6.4 and <7.7, or provide Windows PowerShell 5.1 on PATH.");
    }

    private static bool IsSupportedPowerShell7(Version version) =>
        version >= MinimumPowerShell7 && version < PowerShell7UpperBound;

    private static bool IsWindowsPowerShell51(Version version) =>
        version.Major == 5 && version.Minor == 1;
}
