// -----------------------------------------------------------------------
// <copyright file="HomeIdentity.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// The one definition of "the same Netclaw home". The daemon compares the home in a shutdown notice with
/// its own home, and the CLI compares the home that a process serves with the home of a command. Both
/// sides must read two spellings of one folder the same way.
/// </summary>
public static class HomeIdentity
{
    // Windows paths ignore case. The platform check stays outside the mutation gate: on the Linux
    // runner, a mutant of it is equal to the original or is killed by the case test.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The home that a process serves when <c>NETCLAW_HOME</c> is absent.</summary>
    public static string DefaultHome => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".netclaw"));

    // home-match:begin
    private const int MaxLinkHops = 40;

    /// <summary>
    /// The resolved home path: the full path with no trailing separator and every link resolved, one path
    /// component at a time, as <c>realpath(3)</c> does. A relative link target is joined to the folder that the
    /// walk has already resolved, so a <c>..</c> in a target leaves that folder. A part that does not exist is
    /// not a link and keeps its spelling. It throws <see cref="IOException"/> after more than 40 links.
    /// </summary>
    public static string Canonical(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var rootLength = Path.GetPathRoot(full.AsSpan()).Length;
        var done = full[..rootLength];
        var rest = new Stack<string>(Parts(full[rootLength..]).Reverse());
        var hops = 0;
        while (hops <= MaxLinkHops && rest.TryPop(out var part))
        {
            if (part == ".")
                continue;

            if (part == "..")
            {
                done = Path.GetDirectoryName(done) ?? done;
                continue;
            }

            var candidate = Path.Combine(done, part);
            var target = new DirectoryInfo(candidate).LinkTarget;
            if (target is null)
            {
                done = candidate;
                continue;
            }

            hops++;
            var targetRootLength = Path.GetPathRoot(target.AsSpan()).Length;
            if (targetRootLength > 0)
                done = target[..targetRootLength];

            foreach (var targetPart in Parts(target[targetRootLength..]).Reverse())
                rest.Push(targetPart);
        }

        // The bound is checked after the loop, so a cycle ends the loop and never spins.
        if (hops > MaxLinkHops)
            throw new IOException($"The path '{path}' follows more than {MaxLinkHops} links.");

        return Path.TrimEndingDirectorySeparator(done);
    }

    private static string[] Parts(string path)
        => path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Whether a home value from outside can be compared at all: it holds no NUL character and is fully
    /// qualified. A relative value would resolve in the working folder of the caller, and a NUL or an empty
    /// value makes <see cref="Canonical"/> throw. The shutdown endpoint and the CLI apply this one rule.
    /// </summary>
    public static bool IsFullPath(string path)
        => !path.Contains('\0', StringComparison.Ordinal) && Path.IsPathFullyQualified(path);

    /// <summary>Whether two paths name the same home. A path with no resolved form (a link cycle) matches nothing.</summary>
    public static bool Same(string first, string second)
    {
        try
        {
            return string.Equals(Canonical(first), Canonical(second), PathComparison);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a process with this environment serves <paramref name="home"/>, read as that process reads
    /// itself: <c>NETCLAW_HOME</c>, or <c>&lt;HOME&gt;/.netclaw</c> with the <c>HOME</c> of the same
    /// environment. No usable value gives no proof. <paramref name="environment"/> is the text of
    /// <c>/proc/&lt;pid&gt;/environ</c>: entries that end with a NUL character.
    /// </summary>
    public static bool EnvironmentServesHome(string environment, string home)
    {
        var entries = environment.Split('\0');
        var declared = ValueOf(entries, "NETCLAW_HOME=");
        string served;
        if (string.IsNullOrWhiteSpace(declared))
        {
            var profile = ValueOf(entries, "HOME=");
            if (string.IsNullOrEmpty(profile) || !Path.IsPathFullyQualified(profile) || profile.Contains('%', StringComparison.Ordinal))
                return false;

            served = Path.Combine(profile, ".netclaw");
        }
        else
        {
            served = declared.Trim();
            // NetclawPaths expands ~, $HOME, ${HOME}, and %USERPROFILE%. The CLI does not, so any of them gives no proof.
            if (!Path.IsPathFullyQualified(served)
                || served.Contains('~', StringComparison.Ordinal)
                || served.Contains('$', StringComparison.Ordinal)
                || served.Contains('%', StringComparison.Ordinal))
                return false;
        }

        return Same(served, home);
    }

    private static string? ValueOf(string[] entries, string prefix)
        => entries.FirstOrDefault(entry => entry.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    // home-match:end

    /// <summary>
    /// The environment of a process as text, or <c>null</c> when it cannot be read: no such process,
    /// no permission, or an operating system that has no <c>/proc</c>.
    /// </summary>
    public static string? ReadProcessEnvironment(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/environ");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the process shows that it serves <paramref name="home"/>. It is <c>false</c> when the
    /// environment cannot be read, so a caller needs no platform check: an unreadable process gives no proof.
    /// </summary>
    public static bool ProcessServesHome(int pid, string home)
        => ReadProcessEnvironment(pid) is { } environment && EnvironmentServesHome(environment, home);
}
