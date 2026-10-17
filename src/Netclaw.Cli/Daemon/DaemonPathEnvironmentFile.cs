// -----------------------------------------------------------------------
// <copyright file="DaemonPathEnvironmentFile.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.RegularExpressions;
using Netclaw.Configuration;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// The single contract for the netclaw-owned systemd <c>EnvironmentFile=</c> that
/// supplies the installed daemon's shell-tool <c>PATH</c>. The producer
/// (<see cref="DaemonManager"/> install), the rehydrator (<c>DoctorFixService</c>),
/// and the validator (<c>SystemdUnitPathDoctorCheck</c>) all go through this type so
/// the file's format, the operator-PATH capture, and the unit-parsing rules stay in
/// lockstep. See the repo's Cross-Boundary Contract Rule.
/// </summary>
/// <remarks>
/// A systemd <c>--user</c> service starts with a sanitized, non-interactive
/// environment and does NOT inherit the operator's login-shell <c>PATH</c>. Rather
/// than guess a directory list (the failure behind #1544, where <c>~/.dotnet</c> was
/// invisible), install captures the operator's real <c>PATH</c> from the CLI process
/// — a child of the operator's shell — with zero shell execution / dotfile sourcing,
/// and hands it to the daemon via this file.
/// </remarks>
internal static class DaemonPathEnvironmentFile
{
    internal const string PathAssignmentPrefix = "PATH=";
    internal const string ExecStartPrefix = "ExecStart=";
    internal const string EnvironmentFilePrefix = "EnvironmentFile=";
    internal const string InlinePathPrefix = "Environment=PATH=";

    /// <summary>
    /// The set of directories that must always be resolvable for the daemon's shell tool
    /// to function at all (a POSIX shell, coreutils, and admin <c>sbin</c> tools). This is
    /// NOT a guess at the operator's tools — the captured operator PATH supplies those — it
    /// is a functional floor guaranteed regardless of what the installing shell's PATH
    /// happened to contain, so an empty or partial capture can never leave the daemon
    /// unable to resolve <c>/bin/sh</c>, <c>ip</c>, etc. Mirrors the guarantee the old
    /// unit-baked PATH made unconditionally.
    /// </summary>
    private static readonly string[] SystemPathFloor =
        ["/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"];

    /// <summary>
    /// Reads the operator's real <c>PATH</c> from the current process environment.
    /// The netclaw CLI is a child of the operator's interactive shell, so this is
    /// the operator's live <c>PATH</c> — no shell spawned, no dotfiles sourced.
    /// </summary>
    internal static string? CaptureCurrentPath() => Environment.GetEnvironmentVariable("PATH");

    /// <summary>
    /// Composes the <c>PATH</c> value written to the environment file:
    /// <list type="number">
    ///   <item>the daemon's own install directory first (bundled <c>netclaw</c> CLI wins);</item>
    ///   <item>then the captured operator <c>PATH</c> (their real tool dirs — the point of #1544);</item>
    ///   <item>then <see cref="SystemPathFloor"/>, a guaranteed functional baseline.</item>
    /// </list>
    /// Entries are de-duplicated (order-preserving, ordinal) and <b>empty elements are
    /// dropped</b> — a POSIX empty <c>PATH</c> entry (from <c>::</c> or a leading/trailing
    /// <c>:</c>, common when a dotfile does <c>PATH="$PATH:"</c>) means "current directory",
    /// which would let a binary planted in an agent-controlled workspace shadow a system
    /// command when the daemon runs <c>bash -c</c>. Separator is the POSIX <c>':'</c>
    /// (systemd is Linux-only). Because the floor is always appended, an empty/unset
    /// captured PATH still yields a fully functional PATH rather than <c>installDir</c> alone.
    /// </summary>
    internal static string ComposePathValue(string installDir, string? capturedPath)
    {
        var ordered = new List<string> { installDir };
        if (!string.IsNullOrEmpty(capturedPath))
            ordered.AddRange(capturedPath.Split(':'));
        ordered.AddRange(SystemPathFloor);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(ordered.Count);
        foreach (var entry in ordered)
        {
            // Drop empty elements (the CWD-resolution hazard); keep everything else verbatim.
            if (entry.Length == 0)
                continue;
            if (seen.Add(entry))
                result.Add(entry);
        }

        return string.Join(':', result);
    }

    /// <summary>
    /// Renders the full environment-file content: a single <c>PATH=</c> assignment
    /// with a trailing newline. systemd <c>EnvironmentFile=</c> parses bare
    /// <c>KEY=VALUE</c> lines and does not perform shell expansion, so the literal
    /// captured value is written verbatim.
    /// </summary>
    internal static string Render(string installDir, string? capturedPath)
        => Merge(existingText: null, installDir, capturedPath, assignments: []);

    /// <summary>
    /// Renders the file after an install or a <c>doctor --fix</c>. The first <c>PATH=</c> line
    /// changes in place, a later <c>PATH=</c> line goes, and <c>PATH=</c> goes at the end when
    /// none exists. An <paramref name="assignments"/> key replaces its line in place or goes
    /// after the last line. Every other line stays verbatim and in order: the method compares
    /// only the text before the first <c>=</c> and never reads an operator line.
    /// </summary>
    internal static string Merge(
        string? existingText,
        string installDir,
        string? capturedPath,
        IReadOnlyList<DaemonEnvAssignment> assignments)
    {
        var lines = string.IsNullOrEmpty(existingText)
            ? new List<string>()
            : existingText.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var eol = existingText is not null && existingText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var pathLine = $"{PathAssignmentPrefix}{ComposePathValue(installDir, capturedPath)}";

        var written = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>(lines.Count + assignments.Count + 1);
        var pathWritten = false;

        foreach (var line in lines)
        {
            var carriage = line.EndsWith('\r') ? "\r" : string.Empty;
            var content = line.TrimStart();

            if (content.StartsWith(PathAssignmentPrefix, StringComparison.Ordinal))
            {
                if (!pathWritten)
                {
                    output.Add(pathLine + carriage);
                    pathWritten = true;
                }

                continue;
            }

            var key = KeyOf(content);
            var assignment = assignments.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));
            if (assignment.Key is not null)
            {
                if (written.Add(assignment.Key))
                    output.Add($"{assignment.Key}={assignment.Value}{carriage}");

                continue;
            }

            output.Add(line);
        }

        if (!pathWritten)
            output.Add(pathLine + (eol == "\r\n" ? "\r" : string.Empty));

        foreach (var assignment in assignments)
        {
            if (written.Add(assignment.Key))
                output.Add($"{assignment.Key}={assignment.Value}" + (eol == "\r\n" ? "\r" : string.Empty));
        }

        var text = new StringBuilder();
        foreach (var line in output)
            text.Append(line).Append('\n');
        return text.ToString();
    }

    internal static void Write(string envFilePath, string text)
    {
        EnsureLinkIsSafe(envFilePath);
        AtomicFile.WriteAllTextOwnerOnly(ResolveTarget(envFilePath), text);
    }

    /// <summary>The file that a write to <paramref name="envFilePath"/> changes. A symbolic link chain resolves to its end.</summary>
    internal static string ResolveTarget(string envFilePath)
    {
        var file = new FileInfo(envFilePath);
        return file.LinkTarget is null
            ? envFilePath
            : file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? envFilePath;
    }

    internal delegate bool OwnerReader(string path, out uint ownerId);

    /// <summary>
    /// Describes why <paramref name="envFilePath"/> is a link that no command can use: it points to a
    /// missing file, or it loops. Returns <c>null</c> for a regular file, for a link to an existing
    /// file, and for a path that does not exist. A command must call this before any call that
    /// follows the link, because those calls throw for a broken link.
    /// </summary>
    internal static string? FindLinkProblem(string envFilePath)
    {
        var link = new FileInfo(envFilePath);
        if (link.LinkTarget is null)
            return null;

        FileSystemInfo? target;
        try
        {
            target = link.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (IOException ex)
        {
            return $"{envFilePath} is a link that netclaw cannot follow ({ex.Message}). "
                + "Remove the link, or replace it with a regular file, then run the command again.";
        }

        return target is null || !target.Exists
            ? $"{envFilePath} is a link that points to a missing file, {target?.FullName ?? link.LinkTarget}. "
                + "Create that file or remove the link, then run the command again."
            : null;
    }

    internal static void EnsureLinkIsSafe(string envFilePath)
        => EnsureLinkIsSafe(envFilePath, UnixOwnership.TryGetOwnerId);

    internal static void EnsureLinkIsSafe(string envFilePath, OwnerReader readOwner)
    {
        if (FindLinkProblem(envFilePath) is { } problem)
            throw new UnsafeEnvFileLinkException(problem);

        var link = new FileInfo(envFilePath);
        if (link.LinkTarget is null || !UnixOwnership.IsSupported)
            return;

        var target = link.ResolveLinkTarget(returnFinalTarget: true)!;
        bool ownerKnown;
        uint ownerId;
        try
        {
            ownerKnown = readOwner(envFilePath, out ownerId);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            throw new UnsafeEnvFileLinkException(
                $"{envFilePath} is a link to {target.FullName}, and this system has no statx call in its C library "
                + "(glibc 2.28 or newer has it), so netclaw cannot check who owns the target. "
                + "Netclaw does not write to a file when it cannot check who owns it. Replace the link with a regular file.");
        }

        if (!ownerKnown)
        {
            throw new UnsafeEnvFileLinkException(
                $"{envFilePath} is a link to {target.FullName}, and netclaw cannot read the owner of that file. "
                + "Netclaw does not write to a file when it cannot check who owns it.");
        }

        if (ownerId != UnixOwnership.CurrentUserId())
        {
            throw new UnsafeEnvFileLinkException(
                $"{envFilePath} is a link to {target.FullName}, which another user owns (user id {ownerId}). "
                + "Netclaw does not change a file that another user owns. Replace the link with your own file, or ask the owner of the file to make the change.");
        }
    }

    /// <summary>
    /// True when group or others have any access to the file. Always false on Windows, for a missing
    /// file, and for a broken link. <see cref="FindLinkProblem"/> reports the broken link.
    /// </summary>
    internal static bool HasLooseMode(string envFilePath)
    {
        if (OperatingSystem.IsWindows() || FindLinkProblem(envFilePath) is not null || !File.Exists(envFilePath))
            return false;

        const UnixFileMode groupAndOthers = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(envFilePath) & groupAndOthers) != 0;
    }

    internal static void RestrictToOwner(string envFilePath)
    {
        if (OperatingSystem.IsWindows())
            return;

        EnsureLinkIsSafe(envFilePath);
        File.SetUnixFileMode(envFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string? KeyOf(string trimmedLine)
    {
        var equals = trimmedLine.IndexOf('=', StringComparison.Ordinal);
        return equals > 0 ? trimmedLine[..equals] : null;
    }

    /// <summary>
    /// True when the file holds a line other than a blank line, a comment, or a <c>PATH=</c>
    /// line. Uninstall keeps such a file. The method does not read the operator lines.
    /// </summary>
    internal static bool HasOperatorLines(string fileContent)
    {
        foreach (var raw in fileContent.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0
                || line[0] is '#' or ';'
                || line.StartsWith(PathAssignmentPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts the <c>PATH</c> value from environment-file content, or <c>null</c>
    /// when no <c>PATH=</c> assignment is present. Leading whitespace is tolerated;
    /// other keys are ignored.
    /// </summary>
    internal static string? ReadPathValue(string fileContent)
    {
        foreach (var raw in fileContent.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(PathAssignmentPrefix, StringComparison.Ordinal))
                return line[PathAssignmentPrefix.Length..];
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="pathValue"/> (a <c>':'</c>-separated PATH) contains
    /// <paramref name="directory"/> as one of its entries (ordinal, exact).
    /// </summary>
    internal static bool PathContainsDirectory(string pathValue, string directory)
    {
        var entries = pathValue.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries.Any(e => string.Equals(e, directory, StringComparison.Ordinal));
    }

    // ── systemd unit parsing (POSIX semantics regardless of host OS) ──

    /// <summary>
    /// Returns the first unit line whose whitespace-trimmed start matches
    /// <paramref name="prefix"/>, or <c>null</c>. systemd allows leading whitespace
    /// before directives; we accept it.
    /// </summary>
    internal static string? FindDirective(IReadOnlyList<string> lines, string prefix)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line;
        }

        return null;
    }

    /// <summary>
    /// Derives the daemon's install directory from the unit's <c>ExecStart=</c>
    /// (the parent directory of the first whitespace-delimited token). Returns
    /// <c>false</c> when <c>ExecStart=</c> is absent or has no directory component.
    /// </summary>
    internal static bool TryGetInstallDir(IReadOnlyList<string> unitLines, out string installDir)
    {
        installDir = string.Empty;

        var execStart = FindDirective(unitLines, ExecStartPrefix);
        if (execStart is null)
            return false;

        var binaryPath = ExtractFirstToken(execStart);
        var lastSlash = binaryPath.LastIndexOf('/');
        if (lastSlash <= 0)
            return false;

        installDir = binaryPath[..lastSlash];
        return installDir.Length > 0;
    }

    /// <summary>
    /// Extracts the environment-file path referenced by the unit's
    /// <c>EnvironmentFile=</c> directive, stripping systemd's optional <c>-</c>
    /// tolerant-load prefix. Returns <c>false</c> when the directive is absent.
    /// </summary>
    internal static bool TryGetEnvironmentFilePath(IReadOnlyList<string> unitLines, out string environmentFilePath)
    {
        environmentFilePath = string.Empty;

        var directive = FindDirective(unitLines, EnvironmentFilePrefix);
        if (directive is null)
            return false;

        var value = directive[EnvironmentFilePrefix.Length..].Trim();
        if (value.StartsWith('-'))
            value = value[1..];

        environmentFilePath = value;
        return environmentFilePath.Length > 0;
    }

    /// <summary>
    /// Extracts the value of a legacy inline <c>Environment=PATH=</c> directive (the
    /// pre-#1544 unit shape), or <c>false</c> when absent. Used to tell a still-functional
    /// legacy unit (inline PATH that resolves the install dir) apart from a broken one.
    /// </summary>
    internal static bool TryGetInlinePath(IReadOnlyList<string> unitLines, out string pathValue)
    {
        pathValue = string.Empty;

        var directive = FindDirective(unitLines, InlinePathPrefix);
        if (directive is null)
            return false;

        pathValue = directive[InlinePathPrefix.Length..].Trim();
        return pathValue.Length > 0;
    }

    /// <summary>
    /// Extracts the first whitespace-delimited token from a directive value
    /// (e.g. <c>ExecStart=/path/netclawd --flag</c> → <c>/path/netclawd</c>).
    /// </summary>
    private static string ExtractFirstToken(string directive)
    {
        var equalsIndex = directive.IndexOf('=', StringComparison.Ordinal);
        if (equalsIndex < 0 || equalsIndex == directive.Length - 1)
            return string.Empty;

        var value = directive[(equalsIndex + 1)..].TrimStart();
        var spaceIndex = value.IndexOf(' ', StringComparison.Ordinal);
        return spaceIndex < 0 ? value : value[..spaceIndex];
    }
}

internal sealed class UnsafeEnvFileLinkException(string message) : IOException(message);

/// <summary>
/// One <c>KEY=VALUE</c> line that <c>netclaw daemon install --env</c> writes to the daemon
/// environment file. The daemon reads each <c>NETCLAW_*</c> line through its environment
/// configuration source, so a key uses the <c>__</c> section separator.
/// </summary>
internal readonly partial record struct DaemonEnvAssignment(string Key, string Value)
{
    [GeneratedRegex(@"^NETCLAW_[A-Za-z0-9_]+\z")]
    private static partial Regex KeyPattern();

    /// <summary>
    /// Parses every entry before any file is written. The first bad entry stops the parse. No
    /// message holds a value, because daemon.env is plain text and a value may be a secret.
    /// </summary>
    internal static bool TryParseAll(
        IReadOnlyList<string> entries,
        out IReadOnlyList<DaemonEnvAssignment> assignments,
        out string error)
    {
        var parsed = new List<DaemonEnvAssignment>(entries.Count);
        assignments = parsed;

        foreach (var entry in entries)
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                error = "An --env entry must have the form KEY=VALUE. One entry has no '='.";
                return false;
            }

            var key = entry[..separator];
            var value = entry[(separator + 1)..];

            if (!KeyPattern().IsMatch(key))
            {
                error = $"The --env key '{key}' is not valid. A key starts with NETCLAW_ and holds only letters, digits, and '_'.";
                return false;
            }

            if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
            {
                error = $"The --env value for '{key}' holds a line break. A value must stay on one line.";
                return false;
            }

            if (value.AsSpan().IndexOfAny('\\', '\'', '"') >= 0 || value.Trim().Length != value.Length)
            {
                error = $"The --env value for '{key}' holds a backslash, a quote, or a space at its start or end. "
                    + "systemd reads these in an environment file as an escape, a quote, or a trim, so the daemon would see a different value. "
                    + "Use a value without them.";
                return false;
            }

            if (parsed.Any(existing => existing.Key == key))
            {
                error = $"The --env key '{key}' appears more than once.";
                return false;
            }

            if (!DaemonEnvKeyPolicy.TryAccept(key, value, out var refusal))
            {
                error = refusal;
                return false;
            }

            parsed.Add(new DaemonEnvAssignment(key, value));
        }

        error = string.Empty;
        return true;
    }
}
