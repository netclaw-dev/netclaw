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
}

public sealed record WorkingContextSnapshot
{
    public required WorkingContext WorkingContext { get; init; }
    public GitWorkingContextSnapshot? Git { get; init; }
    public string? GitUnavailableReason { get; init; }

    public bool IsEmpty => WorkingContext.IsEmpty && Git is null && GitUnavailableReason is null;

    public string ToContextBlock()
    {
        if (IsEmpty)
            return string.Empty;

        var sb = new StringBuilder("[working-context]");
        if (WorkingContext.ProjectDirectory is not null)
            sb.Append("\nproject_dir: ").Append(WorkingContext.ProjectDirectory);

        if (!WorkingContext.RecentFiles.IsEmpty)
        {
            sb.Append("\nrecent_files:");
            foreach (var path in WorkingContext.RecentFiles)
                sb.Append("\n  - ").Append(path);
        }

        if (Git is { } git)
        {
            sb.Append("\ngit:")
                .Append("\n  worktree: ").Append(git.Worktree)
                .Append("\n  common_dir: ").Append(git.CommonDirectory)
                .Append("\n  branch: ").Append(git.Detached ? "(detached)" : git.Branch)
                .Append("\n  head: ").Append(git.Head ?? "(unborn)");
            if (git.Upstream is not null)
            {
                sb.Append("\n  upstream: ").Append(git.Upstream)
                    .Append("\n  ahead: ").Append(git.Ahead)
                    .Append("\n  behind: ").Append(git.Behind);
            }
            sb.Append("\n  staged: ").Append(git.Staged)
                .Append("\n  modified: ").Append(git.Modified)
                .Append("\n  untracked: ").Append(git.Untracked);
        }
        else if (GitUnavailableReason is not null)
        {
            sb.Append("\ngit:")
                .Append("\n  status: unavailable")
                .Append("\n  reason: ").Append(GitUnavailableReason);
        }

        return sb.ToString();
    }
}

public interface IWorkingContextSnapshotProvider
{
    WorkingContextSnapshot Create(WorkingContext context, TrustAudience audience);
}

public sealed class WorkingContextSnapshotProvider(ILogger<WorkingContextSnapshotProvider> logger)
    : IWorkingContextSnapshotProvider
{
    internal static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(2);
    private const int MaxOutputChars = 256 * 1024;

    public WorkingContextSnapshot Create(WorkingContext context, TrustAudience audience)
    {
        if (audience == TrustAudience.Public)
            return new WorkingContextSnapshot { WorkingContext = WorkingContext.Empty };
        if (context.ProjectDirectory is null)
            return new WorkingContextSnapshot { WorkingContext = context };

        var inspection = InspectGit(context.ProjectDirectory);
        if (inspection.Snapshot is not null)
            return new WorkingContextSnapshot { WorkingContext = context, Git = inspection.Snapshot };
        if (inspection.IsNotRepository)
            return new WorkingContextSnapshot { WorkingContext = context };

        logger.LogWarning("Git working-context inspection failed: {Reason}", inspection.Error);
        return new WorkingContextSnapshot
        {
            WorkingContext = context,
            GitUnavailableReason = SanitizeReason(inspection.Error)
        };
    }

    internal static GitInspectionResult InspectGit(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
            return GitInspectionResult.Failed("project directory does not exist");

        var roots = RunGit(projectDirectory,
            ["rev-parse", "--show-toplevel", "--path-format=absolute", "--git-common-dir"]);
        if (!roots.Success)
        {
            var notRepo = roots.StandardError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase);
            return notRepo ? GitInspectionResult.NotRepository() : GitInspectionResult.Failed(roots.Error);
        }

        var rootLines = roots.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rootLines.Length != 2)
            return GitInspectionResult.Failed("git returned an unexpected repository-root response");

        var status = RunGit(projectDirectory, ["status", "--porcelain=v2", "--branch", "--untracked-files=normal"]);
        if (!status.Success)
            return GitInspectionResult.Failed(status.Error);

        try
        {
            return GitInspectionResult.Succeeded(ParseStatus(rootLines[0], rootLines[1], status.StandardOutput));
        }
        catch (FormatException ex)
        {
            return GitInspectionResult.Failed(ex.Message);
        }
    }

    internal static GitWorkingContextSnapshot ParseStatus(string worktree, string commonDirectory, string output)
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

        return new GitWorkingContextSnapshot
        {
            Worktree = worktree,
            CommonDirectory = commonDirectory,
            Branch = branch,
            Detached = detached,
            Head = head,
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            Staged = staged,
            Modified = modified,
            Untracked = untracked,
            ChangedFiles = files.ToImmutable()
        };
    }

    private static GitCommandResult RunGit(string workingDirectory, IReadOnlyList<string> arguments)
    {
        try
        {
            using var process = new Process
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

            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeout))
            {
                process.Kill(entireProcessTree: true);
                return GitCommandResult.Failed("git inspection timed out");
            }

            if (!Task.WaitAll([stdout, stderr], GitTimeout))
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                return GitCommandResult.Failed("git output collection timed out");
            }
            var standardOutput = Bound(stdout.Result);
            var standardError = Bound(stderr.Result);
            return process.ExitCode == 0
                ? GitCommandResult.Succeeded(standardOutput, standardError)
                : GitCommandResult.Failed(string.IsNullOrWhiteSpace(standardError)
                    ? $"git exited with code {process.ExitCode}"
                    : standardError, standardOutput, standardError);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or AggregateException)
        {
            return GitCommandResult.Failed(ex is AggregateException ? "git output collection timed out" : ex.Message);
        }
    }

    private static string Bound(string value) => value.Length <= MaxOutputChars ? value : value[..MaxOutputChars];

    private static string SanitizeReason(string reason)
    {
        var firstLine = reason.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                        ?? "inspection failed";
        return firstLine.Length <= 200 ? firstLine : firstLine[..200];
    }

    internal sealed record GitInspectionResult(
        GitWorkingContextSnapshot? Snapshot,
        bool IsNotRepository,
        string Error)
    {
        public static GitInspectionResult Succeeded(GitWorkingContextSnapshot snapshot) => new(snapshot, false, string.Empty);
        public static GitInspectionResult NotRepository() => new(null, true, string.Empty);
        public static GitInspectionResult Failed(string error) => new(null, false, error);
    }

    private sealed record GitCommandResult(bool Success, string StandardOutput, string StandardError, string Error)
    {
        public static GitCommandResult Succeeded(string stdout, string stderr) => new(true, stdout, stderr, string.Empty);
        public static GitCommandResult Failed(string error, string stdout = "", string stderr = "") => new(false, stdout, stderr, error);
    }
}
