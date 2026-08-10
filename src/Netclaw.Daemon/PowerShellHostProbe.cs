// -----------------------------------------------------------------------
// <copyright file="PowerShellHostProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;

namespace Netclaw.Daemon;

internal enum PowerShellProbeFailure
{
    ExecutableLookupFailed,
    AccessDenied,
    StartFailed,
    Timeout,
    TerminationFailed,
    NonZeroExit,
    UnexpectedErrorOutput,
    OutputTooLarge,
    MalformedVersion
}

internal abstract record PowerShellHostProbeResult
{
    private PowerShellHostProbeResult()
    {
    }

    internal sealed record NotFound : PowerShellHostProbeResult;

    internal sealed record Found(string ExecutablePath, Version Version) : PowerShellHostProbeResult;

    internal sealed record Failed(PowerShellProbeFailure Failure, int? ExitCode = null) : PowerShellHostProbeResult;
}

internal interface IPowerShellHostProbe
{
    Task<PowerShellHostProbeResult> ProbeAsync(
        string executableName,
        CancellationToken cancellationToken);
}

internal sealed class PowerShellHostProbe(
    TimeProvider timeProvider,
    IPowerShellExecutableLocator executableLocator,
    IPowerShellProbeProcessFactory processFactory) : IPowerShellHostProbe
{
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ProbeRetryDelay = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(1);
    private const int MaxOutputChars = 4096;
    private const int MaxProbeAttempts = 2;

    public async Task<PowerShellHostProbeResult> ProbeAsync(
        string executableName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await ProbeOnceAsync(executableName, cancellationToken).ConfigureAwait(false);
            if (result is not PowerShellHostProbeResult.Failed { Failure: PowerShellProbeFailure.Timeout }
                || attempt >= MaxProbeAttempts)
            {
                return result;
            }

            // Cold-start (Defender scan, first-run init) is transient: retry once
            // before surfacing a Timeout, so a slow-but-healthy host still resolves.
            await Task.Delay(ProbeRetryDelay, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PowerShellHostProbeResult> ProbeOnceAsync(
        string executableName,
        CancellationToken cancellationToken)
    {
        var lookup = executableLocator.Locate(executableName);
        if (lookup is PowerShellExecutableLookup.NotFound)
            return new PowerShellHostProbeResult.NotFound();
        if (lookup is PowerShellExecutableLookup.Failed lookupFailure)
            return new PowerShellHostProbeResult.Failed(lookupFailure.Failure);

        var executablePath = ((PowerShellExecutableLookup.Found)lookup).ExecutablePath;
        IPowerShellProbeProcess process;
        try
        {
            process = processFactory.Start(executablePath);
        }
        catch (UnauthorizedAccessException)
        {
            return new PowerShellHostProbeResult.Failed(PowerShellProbeFailure.AccessDenied);
        }
        catch (SecurityException)
        {
            return new PowerShellHostProbeResult.Failed(PowerShellProbeFailure.AccessDenied);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new PowerShellHostProbeResult.Failed(PowerShellProbeFailure.StartFailed);
        }

        using (process)
        using (var timeout = new CancellationTokenSource(ProbeTimeout, timeProvider))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   timeout.Token))
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, linked.Token);
            var stderr = ReadBoundedAsync(process.StandardError, linked.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var probeTimedOut = timeout.IsCancellationRequested;
                TryCancel(timeout);
                var terminated = await TerminateAsync(process, stdout, stderr).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                if (ex is OperationCanceledException && probeTimedOut)
                {
                    return new PowerShellHostProbeResult.Failed(
                        terminated
                            ? PowerShellProbeFailure.Timeout
                            : PowerShellProbeFailure.TerminationFailed);
                }

                if (IsExpectedPostStartFailure(ex))
                {
                    return new PowerShellHostProbeResult.Failed(
                        terminated
                            ? PowerShellProbeFailure.StartFailed
                            : PowerShellProbeFailure.TerminationFailed);
                }

                throw;
            }

            var standardOutput = await stdout.ConfigureAwait(false);
            var standardError = await stderr.ConfigureAwait(false);
            if (standardOutput.Truncated || standardError.Truncated)
                return new PowerShellHostProbeResult.Failed(
                    PowerShellProbeFailure.OutputTooLarge);
            if (process.ExitCode != 0)
            {
                return new PowerShellHostProbeResult.Failed(
                    PowerShellProbeFailure.NonZeroExit,
                    process.ExitCode);
            }

            if (!string.IsNullOrWhiteSpace(standardError.Text))
                return new PowerShellHostProbeResult.Failed(PowerShellProbeFailure.UnexpectedErrorOutput);
            if (!TryParseVersion(standardOutput.Text, out var version))
                return new PowerShellHostProbeResult.Failed(PowerShellProbeFailure.MalformedVersion);

            return new PowerShellHostProbeResult.Found(executablePath, version);
        }
    }

    internal static bool TryParseVersion(string output, out Version version)
    {
        var value = output.Trim();
        if (value.Length == 0 || value.IndexOfAny(['\r', '\n']) >= 0)
        {
            version = new Version();
            return false;
        }

        return Version.TryParse(value, out version!);
    }

    private static bool IsExpectedPostStartFailure(Exception exception) =>
        exception is OperationCanceledException or Win32Exception or IOException or
            ObjectDisposedException or InvalidOperationException;

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PowerShell probe reader cancellation failed: {ex.Message}");
        }
    }

    private static async Task<BoundedText> ReadBoundedAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[256];
        var output = new StringBuilder();
        var truncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            var remaining = MaxOutputChars - output.Length;
            if (remaining > 0)
                output.Append(buffer, 0, Math.Min(read, remaining));
            if (read > remaining)
                truncated = true;
        }

        return new BoundedText(output.ToString(), truncated);
    }

    private async Task<bool> TerminateAsync(
        IPowerShellProbeProcess process,
        Task<BoundedText> stdout,
        Task<BoundedText> stderr)
    {
        var terminated = false;
        try
        {
            terminated = process.TryKillTree();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PowerShell probe process-tree termination failed: {ex.Message}");
        }

        using var cleanupTimeout = new CancellationTokenSource(TerminationTimeout, timeProvider);
        var exitObserved = terminated;
        if (terminated)
        {
            try
            {
                await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
            {
                exitObserved = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PowerShell probe exit observation failed: {ex.Message}");
                exitObserved = false;
            }
        }

        var stdoutObserved = await ObserveReadAsync(stdout, cleanupTimeout.Token).ConfigureAwait(false);
        var stderrObserved = await ObserveReadAsync(stderr, cleanupTimeout.Token).ConfigureAwait(false);
        return terminated && exitObserved && stdoutObserved && stderrObserved;
    }

    private static async Task<bool> ObserveReadAsync(
        Task<BoundedText> read,
        CancellationToken cleanupToken)
    {
        try
        {
            await read.WaitAsync(cleanupToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (read.IsCompleted)
        {
            Debug.WriteLine($"PowerShell probe reader completed with an error: {ex.Message}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PowerShell probe reader observation failed: {ex.Message}");
            ObserveEventually(read);
            return false;
        }
    }

    private static void ObserveEventually(Task read)
    {
        _ = read.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed record BoundedText(string Text, bool Truncated);
}

internal abstract record PowerShellExecutableLookup
{
    private PowerShellExecutableLookup()
    {
    }

    internal sealed record NotFound : PowerShellExecutableLookup;

    internal sealed record Found(string ExecutablePath) : PowerShellExecutableLookup;

    internal sealed record Failed(PowerShellProbeFailure Failure) : PowerShellExecutableLookup;
}

internal interface IPowerShellExecutableLocator
{
    PowerShellExecutableLookup Locate(string executableName);
}

internal enum ExecutablePathInspection
{
    Missing,
    File,
    AccessDenied,
    Failed
}

internal sealed class WindowsPathPowerShellExecutableLocator : IPowerShellExecutableLocator
{
    private readonly Func<string?> _pathProvider;
    private readonly Func<string, ExecutablePathInspection> _pathInspector;
    private readonly Func<string, string> _getFullPath;

    public WindowsPathPowerShellExecutableLocator()
        : this(
            () => Environment.GetEnvironmentVariable("PATH"),
            InspectPath,
            Path.GetFullPath)
    {
    }

    internal WindowsPathPowerShellExecutableLocator(
        Func<string?> pathProvider,
        Func<string, ExecutablePathInspection> pathInspector,
        Func<string, string> getFullPath)
    {
        _pathProvider = pathProvider;
        _pathInspector = pathInspector;
        _getFullPath = getFullPath;
    }

    public PowerShellExecutableLookup Locate(string executableName)
    {
        if (executableName is not ("pwsh.exe" or "powershell.exe"))
            throw new ArgumentOutOfRangeException(nameof(executableName), executableName, "The PowerShell executable name is not supported.");

        var path = _pathProvider();
        if (string.IsNullOrWhiteSpace(path))
            return new PowerShellExecutableLookup.NotFound();

        foreach (var rawEntry in path.Split(';'))
        {
            if (!TryNormalizeEntry(rawEntry, out var entry))
                continue;

            var candidate = $"{entry.TrimEnd('\\', '/')}\\{executableName}";
            switch (_pathInspector(candidate))
            {
                case ExecutablePathInspection.Missing:
                    continue;
                case ExecutablePathInspection.AccessDenied:
                    return new PowerShellExecutableLookup.Failed(PowerShellProbeFailure.AccessDenied);
                case ExecutablePathInspection.Failed:
                    return new PowerShellExecutableLookup.Failed(PowerShellProbeFailure.ExecutableLookupFailed);
                case ExecutablePathInspection.File:
                    try
                    {
                        return new PowerShellExecutableLookup.Found(_getFullPath(candidate));
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or SecurityException)
                    {
                        return new PowerShellExecutableLookup.Failed(PowerShellProbeFailure.ExecutableLookupFailed);
                    }
                default:
                    return new PowerShellExecutableLookup.Failed(PowerShellProbeFailure.ExecutableLookupFailed);
            }
        }

        return new PowerShellExecutableLookup.NotFound();
    }

    private static bool TryNormalizeEntry(string rawEntry, out string entry)
    {
        entry = rawEntry.Trim();
        if (entry.Length >= 2 && entry[0] == '"' && entry[^1] == '"')
            entry = entry[1..^1];
        else if (entry.Contains('"', StringComparison.Ordinal))
            return false;

        return IsFullyQualifiedWindowsPath(entry);
    }

    private static bool IsFullyQualifiedWindowsPath(string path)
    {
        if (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && IsSeparator(path[2]))
        {
            return true;
        }

        if (path.Length < 5 || !IsSeparator(path[0]) || !IsSeparator(path[1]))
            return false;

        var serverEnd = path.IndexOfAny(['\\', '/'], 2);
        return serverEnd > 2 && serverEnd < path.Length - 1;
    }

    private static bool IsSeparator(char value) => value is '\\' or '/';

    private static ExecutablePathInspection InspectPath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory)
                ? ExecutablePathInspection.Missing
                : ExecutablePathInspection.File;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ExecutablePathInspection.Missing;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return ExecutablePathInspection.AccessDenied;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return ExecutablePathInspection.Failed;
        }
    }
}

internal interface IPowerShellProbeProcessFactory
{
    IPowerShellProbeProcess Start(string executablePath);
}

internal interface IPowerShellProbeProcess : IDisposable
{
    TextReader StandardOutput { get; }

    TextReader StandardError { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    bool TryKillTree();
}

internal sealed class PowerShellProbeProcessFactory : IPowerShellProbeProcessFactory
{
    internal const string VersionProbeSource =
        "[Console]::Out.Write($PSVersionTable.PSVersion.ToString())";

    public IPowerShellProbeProcess Start(string executablePath)
    {
        var process = new Process { StartInfo = CreateStartInfo(executablePath) };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Process.Start returned false.");
            process.StandardInput.Close();
            return new RunningPowerShellProbeProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(VersionProbeSource);
        return startInfo;
    }
}

internal sealed class RunningPowerShellProbeProcess(Process process) : IPowerShellProbeProcess
{
    public TextReader StandardOutput => process.StandardOutput;

    public TextReader StandardError => process.StandardError;

    public int ExitCode => process.ExitCode;

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        process.WaitForExitAsync(cancellationToken);

    public bool TryKillTree()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public void Dispose() => process.Dispose();
}
