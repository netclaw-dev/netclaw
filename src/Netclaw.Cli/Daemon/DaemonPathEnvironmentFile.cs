// -----------------------------------------------------------------------
// <copyright file="DaemonPathEnvironmentFile.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    /// Renders the environment file after an install or a <c>doctor --fix</c>. A missing file gives a
    /// single <c>PATH=</c> assignment. systemd <c>EnvironmentFile=</c> parses bare <c>KEY=VALUE</c> lines
    /// and does not expand text, so the captured value is written verbatim.
    /// A line is a <c>PATH</c> line when its key, the text before the first <c>=</c>, is <c>PATH</c> once trimmed.
    /// The first <c>PATH</c> line changes in place, a later <c>PATH</c> line goes, and
    /// <c>PATH=</c> goes at the end when none exists. Every other line stays verbatim and in
    /// order, because the operator may keep settings such as <c>NETCLAW_HOME</c> in this file.
    /// The method keeps the line ending style of the file, and the same input gives the same output.
    /// </summary>
    internal static string Merge(string? existingText, string installDir, string? capturedPath)
    {
        var lines = string.IsNullOrEmpty(existingText)
            ? new List<string>()
            : existingText.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var carriage = existingText is not null && existingText.Contains("\r\n", StringComparison.Ordinal)
            ? "\r"
            : string.Empty;
        var pathLine = $"{PathAssignmentPrefix}{ComposePathValue(installDir, capturedPath)}{carriage}";

        var output = new List<string>(lines.Count + 1);
        var pathWritten = false;
        foreach (var line in lines)
        {
            if (!TryReadAssignment(line, "PATH", out _))
            {
                output.Add(line);
                continue;
            }

            // systemd keeps the last assignment of a key, so a second PATH line would win over
            // the first. Keep one line, at the place of the first.
            if (!pathWritten)
            {
                output.Add(pathLine);
                pathWritten = true;
            }
        }

        if (!pathWritten)
            output.Add(pathLine);

        return string.Concat(output.Select(line => line + "\n"));
    }

    /// <summary>
    /// Extracts the <c>PATH</c> value from environment-file content, or <c>null</c>
    /// when no <c>PATH</c> assignment is present. Whitespace around the key and the value is
    /// tolerated, as systemd does; other keys are ignored.
    /// </summary>
    internal static string? ReadPathValue(string fileContent)
    {
        foreach (var raw in fileContent.Split('\n'))
        {
            if (TryReadAssignment(raw, "PATH", out var value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// The value of the last <paramref name="key"/> assignment in environment-file content (a later
    /// assignment overrides an earlier one in systemd), or <c>null</c> when there is none. A value in a
    /// matching pair of quotes is unquoted.
    /// </summary>
    internal static string? ReadLastValue(string fileContent, string key)
    {
        string? last = null;
        foreach (var raw in fileContent.Split('\n'))
        {
            if (TryReadAssignment(raw, key, out var value))
                last = value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[1..^1] : value;
        }

        return last;
    }

    // systemd reads the key before the first '=' with its whitespace trimmed, so `PATH = /x` and
    // `PATH<TAB>=/x` assign PATH. `export PATH=/x` assigns a key named `export PATH`, which is
    // not PATH, so it stays an operator line.
    private static bool TryReadAssignment(string line, string key, out string value)
    {
        value = string.Empty;
        var equals = line.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0 || !string.Equals(line[..equals].Trim(), key, StringComparison.Ordinal))
            return false;

        value = line[(equals + 1)..].Trim();
        return true;
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
