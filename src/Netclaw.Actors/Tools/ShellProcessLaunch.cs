// -----------------------------------------------------------------------
// <copyright file="ShellProcessLaunch.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Retains one exact shell invocation across the background queue. This object is never persisted.
/// </summary>
public sealed class ShellProcessLaunch
{
    private readonly ShellCommandPolicy _commandPolicy;
    private readonly ToolPathPolicy _pathPolicy;
    private readonly ToolInvocationContext _context;
    private readonly Func<CancellationToken, Task> _authorize;
    private readonly ProcessStartInfo _startInfo;
    private int _startRequested;

    internal ShellProcessLaunch(
        string command,
        string? workingDirectory,
        ToolInvocationContext context,
        ShellCommandPolicy commandPolicy,
        ToolPathPolicy pathPolicy,
        Func<CancellationToken, Task> authorize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorize);
        if (!ReferenceEquals(commandPolicy.Environment, pathPolicy.Environment))
            throw new ArgumentException("Shell command and path policies must use the same shell environment.");

        Command = command;
        WorkingDirectory = context.ResolveShellCwd(workingDirectory)
            ?? throw new InvalidOperationException("Shell execution requires a working directory.");
        _context = context;
        _commandPolicy = commandPolicy;
        _pathPolicy = pathPolicy;
        _authorize = authorize;
        _startInfo = commandPolicy.Environment.CreateProcessStartInfo(command);
        // Materialize the inherited environment before a queued request can observe later daemon changes.
        _ = _startInfo.Environment.Count;
    }

    public string Command { get; }
    public string WorkingDirectory { get; }
    internal ToolInvocationContext Context => _context;
    internal ShellExecutionEnvironment Environment => _commandPolicy.Environment;
    internal SessionStoragePaths Storage => _context.SessionStorage
        ?? throw new InvalidOperationException("Shell execution requires resolved session storage.");

    internal async Task<Process> StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _startRequested, 1) != 0)
            throw new InvalidOperationException("A shell launch can start only one process.");

        cancellationToken.ThrowIfCancellationRequested();
        var analysis = CheckHardPolicies();
        PrepareDirectories();
        var pathsBeforeApproval = CapturePathState(analysis);
        await _authorize(cancellationToken).ConfigureAwait(false);
        // Do not put a queue or another await between the final policy result and process creation.
        cancellationToken.ThrowIfCancellationRequested();
        analysis = CheckHardPolicies();
        PrepareDirectories();
        if (!pathsBeforeApproval.SequenceEqual(CapturePathState(analysis)))
            throw new ToolAccessDeniedException("shell_launch_paths_changed",
                "Shell paths changed during authorization. The command did not start.");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Process.Start(_startInfo)
                ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            var environment = _commandPolicy.Environment;
            throw new ShellProcessStartException(
                $"Error starting shell '{environment.ExecutableName}' at '{environment.ExecutablePath}': {ex.Message}", ex);
        }
    }

    private LaunchPathState[] CapturePathState(ShellCommandAnalysis analysis)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal)
        {
            WorkingDirectory,
            Storage.ManagedTemporary.Directory.Value,
            Storage.ManagedTemporary.StorageRoot.Value,
            Environment.ExecutablePath
        };
        if (_context.ProjectDirectory is { } projectDirectory)
            paths.Add(projectDirectory);
        foreach (var view in ShellPolicyPathFacts.CreateExecutionViews(analysis))
        {
            if (view.ResolutionBase.Path is { } resolutionBase)
                paths.Add(resolutionBase.Value);
            foreach (var fact in view.Facts)
            foreach (var path in fact.Paths)
                paths.Add(path.Value);
        }

        return paths.Order(StringComparer.Ordinal).Select(static path =>
        {
            ToolPathPolicy.TryResolveSymlinksInPath(path, out var target);
            return new LaunchPathState(path, target, Directory.Exists(path));
        }).ToArray();
    }

    private readonly record struct LaunchPathState(string Path, string Target, bool DirectoryExists);

    private ShellCommandAnalysis CheckHardPolicies()
    {
        // Parse again because filesystem facts and policy can change while the request waits.
        var analysis = _commandPolicy.Analyze(Command, WorkingDirectory);
        var decision = _commandPolicy.Evaluate(analysis);
        if (!decision.Allowed)
            throw new ShellProcessStartException($"Error: Command blocked by hard deny policy: {decision.DenyReason}");
        if (_pathPolicy.CommandReferencesDeniedPath(analysis))
            throw new ShellProcessStartException("Error: Command references a protected file path. Access denied by security policy.");
        return analysis;
    }

    private void PrepareDirectories()
    {
        var temporaryError = ManagedTemporaryEnvironment.Prepare(_startInfo, Storage.ManagedTemporary);
        if (temporaryError is not null)
            throw new ShellProcessStartException(temporaryError);

        if (_context.SessionDirectory is { } sessionDirectory
            && PathUtility.AreEquivalentPaths(WorkingDirectory, sessionDirectory))
        {
            try
            {
                Directory.CreateDirectory(WorkingDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                       or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new ShellProcessStartException($"Error preparing session working directory: {ex.Message}", ex);
            }
        }
        else if (!Directory.Exists(WorkingDirectory))
        {
            if (File.Exists(WorkingDirectory))
                throw new ShellProcessStartException($"Error: Working directory '{WorkingDirectory}' is a file, not a directory.");

            throw new ShellProcessStartException(
                $"Error: Working directory '{WorkingDirectory}' does not exist. Create it first, e.g.: {CreateDirectoryHint()}");
        }

        _startInfo.WorkingDirectory = WorkingDirectory;
    }

    private string CreateDirectoryHint()
        => _commandPolicy.Environment.PathStyle == ShellPathStyle.Windows
            ? $"New-Item -ItemType Directory -Force -Path '{WorkingDirectory.Replace("'", "''", StringComparison.Ordinal)}'"
            : $"mkdir -p -- '{WorkingDirectory.Replace("'", "'\\''", StringComparison.Ordinal)}'";

}

internal sealed class ShellProcessStartException(string message, Exception? innerException = null)
    : Exception(message, innerException);
