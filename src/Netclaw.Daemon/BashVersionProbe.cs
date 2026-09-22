// -----------------------------------------------------------------------
// <copyright file="BashVersionProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Security;

namespace Netclaw.Daemon;

internal interface IBashVersionProbe
{
    Task<Version> ProbeAsync(
        ShellPlatform platform,
        CancellationToken cancellationToken);
}

internal sealed class BashVersionProbe(TimeProvider timeProvider) : IBashVersionProbe
{
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumVersionOutputLength = 32;
    private const string VersionProbeSource =
        "printf '%s.%s' \"${BASH_VERSINFO[0]}\" \"${BASH_VERSINFO[1]}\"";

    public async Task<Version> ProbeAsync(
        ShellPlatform platform,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(CreateStartInfo(platform))
            ?? throw new InvalidOperationException("The Bash version probe did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(ProbeTimeout, timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            await StopAsync(process).ConfigureAwait(false);
            throw new InvalidOperationException("The Bash version probe timed out.", ex);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process).ConfigureAwait(false);
            throw;
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The Bash version probe exited with code {process.ExitCode}.");
        }

        if (error.Length != 0)
            throw new InvalidOperationException("The Bash version probe wrote unexpected error output.");
        if (output.Length is 0 or > MaximumVersionOutputLength
            || !Version.TryParse(output, out var version)
            || version.Build >= 0
            || version.Revision >= 0)
        {
            throw new InvalidOperationException("The Bash version probe returned an invalid version.");
        }

        return version;
    }

    internal static ProcessStartInfo CreateStartInfo(ShellPlatform platform) =>
        ShellExecutionEnvironment.CreateBash(platform)
            .CreateProcessStartInfo(VersionProbeSource);

    private static async Task StopAsync(Process process)
    {
        if (process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The process exited between the state check and the stop request.
            return;
        }
    }
}
