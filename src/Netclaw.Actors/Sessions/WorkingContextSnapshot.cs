// -----------------------------------------------------------------------
// <copyright file="WorkingContextSnapshot.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Actors.Sessions;

public sealed record GitWorkingContextSnapshot
{
    public required string Worktree { get; init; }
    public required string CommonDirectory { get; init; }
    public string? Branch { get; init; }
    public bool Detached { get; init; }
    public string? Head { get; init; }
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public int Staged { get; init; }
    public int Modified { get; init; }
    public int Untracked { get; init; }
    public ImmutableHashSet<string> ChangedFiles { get; init; } = [];

    internal GitHeadState HeadState => GitHeadState.Create(Detached, Branch, Head, Upstream, Ahead, Behind);
}

internal abstract class GitHeadState
{
    private GitHeadState() { }

    internal sealed class DetachedHead(string head) : GitHeadState
    {
        internal string Head { get; } = !string.IsNullOrWhiteSpace(head)
            ? head : throw new FormatException("A detached Git head requires a commit.");
    }

    internal sealed class AttachedHead(string? branch, string? head, BranchTracking? tracking) : GitHeadState
    {
        // Public snapshots can omit branch metadata. A null commit also represents an unborn branch.
        internal string? Branch { get; } = branch;
        internal string? Head { get; } = head;
        internal BranchTracking? Tracking { get; } = tracking;
    }

    internal sealed class BranchTracking
    {
        internal BranchTracking(string upstream, int ahead, int behind)
        {
            if (string.IsNullOrWhiteSpace(upstream) || ahead < 0 || behind < 0)
                throw new FormatException("Git tracking requires an upstream and nonnegative counts.");
            Upstream = upstream;
            Ahead = ahead;
            Behind = behind;
        }

        internal string Upstream { get; }
        internal int Ahead { get; }
        internal int Behind { get; }
    }

    internal static GitHeadState Create(bool detached, string? branch, string? head, string? upstream, int ahead, int behind)
    {
        if (detached)
        {
            if (branch is not null || upstream is not null || ahead != 0 || behind != 0)
                throw new FormatException("A detached Git head cannot carry branch or tracking metadata.");
            return new DetachedHead(head ?? throw new FormatException("A detached Git head requires a commit."));
        }

        if (upstream is null && (ahead != 0 || behind != 0))
            throw new FormatException("Git divergence requires an upstream.");
        return new AttachedHead(branch, head, upstream is null ? null : new BranchTracking(upstream, ahead, behind));
    }

    internal GitWorkingContextSnapshot ApplyTo(GitWorkingContextSnapshot snapshot) => this switch
    {
        DetachedHead detached => snapshot with
        {
            Detached = true, Head = detached.Head, Branch = null, Upstream = null, Ahead = 0, Behind = 0
        },
        AttachedHead branch => snapshot with
        {
            Detached = false, Head = branch.Head, Branch = branch.Branch,
            Upstream = branch.Tracking?.Upstream, Ahead = branch.Tracking?.Ahead ?? 0, Behind = branch.Tracking?.Behind ?? 0
        },
        _ => throw new InvalidOperationException("Unexpected Git head state.")
    };
}

public abstract record GitWorkingContextInspection
{
    private GitWorkingContextInspection()
    {
    }

    public sealed record Skipped : GitWorkingContextInspection;

    public sealed record Available(GitWorkingContextSnapshot Snapshot) : GitWorkingContextInspection;

    public sealed record NotRepository : GitWorkingContextInspection;

    public sealed record ExecutableNotFound : GitWorkingContextInspection;

    public sealed record Unavailable(string Reason) : GitWorkingContextInspection;
}

public sealed record WorkingContextSnapshot
{
    public required WorkingContext WorkingContext { get; init; }
    public required GitWorkingContextInspection Git { get; init; }
    public ShellExecutionEnvironment? ShellEnvironment { get; init; }

    public bool IsEmpty => ShellEnvironment is null
                           && WorkingContext.IsEmpty
                           && Git is GitWorkingContextInspection.Skipped or GitWorkingContextInspection.NotRepository;

    public string ToContextBlock()
    {
        if (IsEmpty)
            return string.Empty;

        var sb = new StringBuilder("[working-context]");
        if (ShellEnvironment is { } shell)
        {
            sb.Append("\nshell:")
                .Append("\n  platform: ").Append(shell.Platform)
                .Append("\n  executable: ").Append(shell.ExecutablePath)
                .Append("\n  grammar: ").Append(shell.Grammar);
            if (shell.PowerShellDialect is { } dialect)
                sb.Append("\n  dialect: ").Append(dialect);
        }

        if (WorkingContext.ProjectDirectory is not null)
            sb.Append("\nproject_dir: ").Append(WorkingContext.ProjectDirectory);

        if (!WorkingContext.RecentFiles.IsEmpty)
        {
            sb.Append("\nrecent_files:");
            foreach (var path in WorkingContext.RecentFiles)
                sb.Append("\n  - ").Append(path);
        }

        switch (Git)
        {
            case GitWorkingContextInspection.Available { Snapshot: var git }:
                sb.Append("\ngit:")
                    .Append("\n  worktree: ").Append(git.Worktree)
                    .Append("\n  common_dir: ").Append(git.CommonDirectory);
                switch (git.HeadState)
                {
                    case GitHeadState.DetachedHead detached:
                        sb.Append("\n  branch: (detached)").Append("\n  head: ").Append(detached.Head);
                        break;
                    case GitHeadState.AttachedHead branch:
                        sb.Append("\n  branch: ").Append(branch.Branch)
                            .Append("\n  head: ").Append(branch.Head ?? "(unborn)");
                        if (branch.Tracking is { } tracking)
                        {
                            sb.Append("\n  upstream: ").Append(tracking.Upstream)
                                .Append("\n  ahead: ").Append(tracking.Ahead)
                                .Append("\n  behind: ").Append(tracking.Behind);
                        }
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected Git head state.");
                }

                sb.Append("\n  staged: ").Append(git.Staged)
                    .Append("\n  modified: ").Append(git.Modified)
                    .Append("\n  untracked: ").Append(git.Untracked);
                break;
            case GitWorkingContextInspection.Unavailable unavailable:
                sb.Append("\ngit:")
                    .Append("\n  status: unavailable")
                    .Append("\n  reason: ").Append(unavailable.Reason);
                break;
            case GitWorkingContextInspection.ExecutableNotFound:
                sb.Append("\ngit:")
                    .Append("\n  status: unavailable")
                    .Append("\n  reason: git executable not found");
                break;
        }

        return sb.ToString();
    }
}

public interface IGitWorkingContextInspector
{
    Task<GitWorkingContextInspection> InspectAsync(
        string projectDirectory,
        CancellationToken cancellationToken);
}

internal interface IGitCommandRunner
{
    Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed record GitCommandResult(
    bool Success,
    string StandardOutput,
    string StandardError,
    string Error,
    GitCommandFailureKind FailureKind)
{
    public static GitCommandResult Succeeded(string stdout, string stderr) =>
        new(true, stdout, stderr, string.Empty, GitCommandFailureKind.None);

    public static GitCommandResult Failed(string error) =>
        Failed(error, string.Empty, string.Empty);

    public static GitCommandResult Failed(string error, string stdout, string stderr) =>
        new(false, stdout, stderr, error, GitCommandFailureKind.CommandFailed);

    public static GitCommandResult ExecutableNotFound() =>
        new(false, string.Empty, string.Empty, "git executable not found", GitCommandFailureKind.ExecutableNotFound);
}

internal enum GitCommandFailureKind
{
    None,
    CommandFailed,
    ExecutableNotFound
}

public interface IWorkingContextSnapshotProvider
{
    Task<WorkingContextSnapshot> CreateAsync(
        WorkingContext context,
        TrustAudience audience,
        CancellationToken cancellationToken);
}

public sealed class WorkingContextSnapshotProvider : IWorkingContextSnapshotProvider
{
    private readonly IGitWorkingContextInspector _gitInspector;
    private readonly ILogger<WorkingContextSnapshotProvider> _logger;
    private readonly ShellExecutionEnvironment _environment;

    public WorkingContextSnapshotProvider(
        IGitWorkingContextInspector gitInspector,
        ILogger<WorkingContextSnapshotProvider> logger)
        : this(
            gitInspector,
            logger,
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux))
    {
    }

    public WorkingContextSnapshotProvider(
        IGitWorkingContextInspector gitInspector,
        ILogger<WorkingContextSnapshotProvider> logger,
        ShellExecutionEnvironment environment)
    {
        _gitInspector = gitInspector;
        _logger = logger;
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public async Task<WorkingContextSnapshot> CreateAsync(
        WorkingContext context,
        TrustAudience audience,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (audience == TrustAudience.Public || context.ProjectDirectory is null)
        {
            return new WorkingContextSnapshot
            {
                WorkingContext = audience == TrustAudience.Public ? WorkingContext.Empty : context,
                Git = new GitWorkingContextInspection.Skipped(),
                ShellEnvironment = audience == TrustAudience.Personal ? _environment : null
            };
        }

        var inspection = Directory.Exists(context.ProjectDirectory)
            ? await _gitInspector.InspectAsync(context.ProjectDirectory, cancellationToken).ConfigureAwait(false)
            : new GitWorkingContextInspection.Unavailable("project directory does not exist");

        switch (inspection)
        {
            case GitWorkingContextInspection.ExecutableNotFound:
                _logger.LogWarning("Git working-context inspection failed: git executable not found");
                break;
            case GitWorkingContextInspection.Unavailable unavailable:
                _logger.LogWarning("Git working-context inspection failed: {Reason}", unavailable.Reason);
                break;
        }

        return new WorkingContextSnapshot
        {
            WorkingContext = context,
            ShellEnvironment = audience == TrustAudience.Personal ? _environment : null,
            Git = inspection switch
            {
                GitWorkingContextInspection.Unavailable failure =>
                    new GitWorkingContextInspection.Unavailable(SanitizeReason(failure.Reason)),
                _ => inspection
            }
        };
    }

    private static string SanitizeReason(string reason)
    {
        var firstLine = reason.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                        ?? "inspection failed";
        return firstLine.Length <= 200 ? firstLine : firstLine[..200];
    }
}

public sealed class GitWorkingContextInspector : IGitWorkingContextInspector
{
    internal static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(2);
    private readonly IGitCommandRunner _commandRunner;
    private readonly TimeProvider _timeProvider;

    public GitWorkingContextInspector(TimeProvider timeProvider)
        : this(new GitCommandRunner(), timeProvider)
    {
    }

    internal GitWorkingContextInspector(
        IGitCommandRunner commandRunner,
        TimeProvider timeProvider)
    {
        _commandRunner = commandRunner;
        _timeProvider = timeProvider;
    }

    public async Task<GitWorkingContextInspection> InspectAsync(
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        var deadline = new GitInspectionDeadline(_timeProvider.GetUtcNow() + GitTimeout);
        var roots = await _commandRunner.RunAsync(
            projectDirectory,
            ["rev-parse", "--show-toplevel", "--path-format=absolute", "--git-common-dir"],
            deadline.Remaining(_timeProvider),
            cancellationToken).ConfigureAwait(false);
        if (!roots.Success)
        {
            if (roots.FailureKind == GitCommandFailureKind.ExecutableNotFound)
                return new GitWorkingContextInspection.ExecutableNotFound();

            var notRepository = roots.StandardError.Contains(
                "not a git repository",
                StringComparison.OrdinalIgnoreCase);
            return notRepository
                ? new GitWorkingContextInspection.NotRepository()
                : new GitWorkingContextInspection.Unavailable(roots.Error);
        }

        var rootLines = roots.StandardOutput.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rootLines.Length != 2)
        {
            return new GitWorkingContextInspection.Unavailable(
                "git returned an unexpected repository-root response");
        }

        var remaining = deadline.Remaining(_timeProvider);
        if (remaining <= TimeSpan.Zero)
            return new GitWorkingContextInspection.Unavailable("git inspection timed out");

        var status = await _commandRunner.RunAsync(
            projectDirectory,
            ["status", "--porcelain=v2", "--branch", "--untracked-files=normal"],
            remaining,
            cancellationToken).ConfigureAwait(false);
        if (!status.Success)
            return new GitWorkingContextInspection.Unavailable(status.Error);

        try
        {
            return new GitWorkingContextInspection.Available(
                ParseStatus(rootLines[0], rootLines[1], status.StandardOutput));
        }
        catch (FormatException ex)
        {
            return new GitWorkingContextInspection.Unavailable(ex.Message);
        }
    }

    internal static GitWorkingContextSnapshot ParseStatus(
        string worktree,
        string commonDirectory,
        string output)
    {
        string? branch = null;
        string? head = null;
        string? upstream = null;
        var detached = false;
        var ahead = 0;
        var behind = 0;
        var staged = 0;
        var modified = 0;
        var untracked = 0;
        var files = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                var value = line[13..].Trim();
                head = value == "(initial)" ? null : value;
            }
            else if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var value = line[14..].Trim();
                detached = value == "(detached)";
                branch = detached ? null : value;
            }
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = line[18..].Trim();
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var pieces = line[12..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length != 2
                    || !int.TryParse(pieces[0].TrimStart('+'), out ahead)
                    || !int.TryParse(pieces[1].TrimStart('-'), out behind))
                    throw new FormatException("git returned an invalid ahead/behind response");
            }
            else if (line.StartsWith("? ", StringComparison.Ordinal))
            {
                untracked++;
                files.Add(line[2..]);
            }
            else if (line.StartsWith("1 ", StringComparison.Ordinal) || line.StartsWith("2 ", StringComparison.Ordinal))
            {
                var isRename = line[0] == '2';
                var fields = line.Split(' ', isRename ? 10 : 9, StringSplitOptions.None);
                if (fields.Length < 2 || fields[1].Length != 2)
                    throw new FormatException("git returned an invalid file-status response");
                if (fields[1][0] != '.') staged++;
                if (fields[1][1] != '.') modified++;
                files.Add(isRename ? fields[^1].Split('\t', 2)[0] : fields[^1]);
            }
            else if (line.StartsWith("u ", StringComparison.Ordinal))
            {
                staged++;
                modified++;
                var fields = line.Split(' ', 11, StringSplitOptions.None);
                files.Add(fields[^1]);
            }
        }

        var headState = GitHeadState.Create(detached, branch, head, upstream, ahead, behind);
        return headState.ApplyTo(new GitWorkingContextSnapshot
        {
            Worktree = worktree,
            CommonDirectory = commonDirectory,
            Staged = staged,
            Modified = modified,
            Untracked = untracked,
            ChangedFiles = files.ToImmutable()
        });
    }

    internal readonly record struct GitInspectionDeadline(DateTimeOffset ExpiresAt)
    {
        public TimeSpan Remaining(TimeProvider provider)
        {
            var remaining = ExpiresAt - provider.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
}

internal sealed class GitCommandRunner : IGitCommandRunner
{
    private const int MaxOutputChars = 256 * 1024;
    private readonly IGitProcessFactory _processFactory;

    public GitCommandRunner()
        : this(new GitProcessFactory())
    {
    }

    internal GitCommandRunner(IGitProcessFactory processFactory)
    {
        _processFactory = processFactory;
    }

    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = _processFactory.Start(workingDirectory, arguments);
            using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            commandTimeout.CancelAfter(timeout);
            var stdout = process.ReadStandardOutputAsync(commandTimeout.Token);
            var stderr = process.ReadStandardErrorAsync(commandTimeout.Token);

            try
            {
                await process.WaitForExitAsync(commandTimeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).WaitAsync(commandTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var terminated = process.TryKillTree();
                if (terminated)
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await ObserveCancelledReadAsync(stdout).ConfigureAwait(false);
                await ObserveCancelledReadAsync(stderr).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                return GitCommandResult.Failed(terminated
                    ? "git inspection timed out"
                    : "git inspection timed out and process termination failed");
            }

            var standardOutput = Bound(await stdout.ConfigureAwait(false));
            var standardError = Bound(await stderr.ConfigureAwait(false));
            return process.ExitCode == 0
                ? GitCommandResult.Succeeded(standardOutput, standardError)
                : GitCommandResult.Failed(
                    string.IsNullOrWhiteSpace(standardError)
                        ? $"git exited with code {process.ExitCode}"
                        : standardError,
                    standardOutput,
                    standardError);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return GitCommandResult.ExecutableNotFound();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return GitCommandResult.Failed(ex.Message);
        }
    }

    private static async Task ObserveCancelledReadAsync(Task<string> read)
    {
        try
        {
            await read.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }

    internal static string Bound(string value) => value.Length <= MaxOutputChars ? value : value[..MaxOutputChars];

}

internal interface IGitProcessFactory
{
    IRunningGitProcess Start(string workingDirectory, IReadOnlyList<string> arguments);
}

internal interface IRunningGitProcess : IDisposable
{
    int ExitCode { get; }

    Task<string> ReadStandardOutputAsync(CancellationToken cancellationToken);

    Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken);

    Task WaitForExitAsync(CancellationToken cancellationToken);

    bool TryKillTree();
}

internal sealed class GitProcessFactory : IGitProcessFactory
{
    public IRunningGitProcess Start(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            process.Start();
            return new RunningGitProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

internal sealed class RunningGitProcess(Process process) : IRunningGitProcess
{
    public int ExitCode => process.ExitCode;

    public Task<string> ReadStandardOutputAsync(CancellationToken cancellationToken) =>
        process.StandardOutput.ReadToEndAsync(cancellationToken);

    public Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken) =>
        process.StandardError.ReadToEndAsync(cancellationToken);

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
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void Dispose() => process.Dispose();
}
