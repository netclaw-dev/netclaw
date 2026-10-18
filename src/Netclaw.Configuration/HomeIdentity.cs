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
    /// <summary>
    /// The full path with no trailing separator and every link resolved, in every path part. A part that
    /// does not exist is not a link and keeps its spelling, so a home that does not exist still compares.
    /// A link target is not checked for links of its own: two spellings then compare as different homes,
    /// and a caller refuses. It never makes two different folders compare as one.
    /// </summary>
    public static string Canonical(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current))
                continue;

            // Every part is checked, so a link in a parent folder counts (macOS keeps /var and /tmp behind links).
            var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
        }

        return current;
    }

    public static bool Same(string first, string second)
        => string.Equals(Canonical(first), Canonical(second), PathComparison);

    /// <summary>
    /// Whether a process with this environment serves <paramref name="home"/>. A process with no
    /// <c>NETCLAW_HOME</c> serves the default home. <paramref name="environment"/> is the text of
    /// <c>/proc/&lt;pid&gt;/environ</c>: entries that end with a NUL character.
    /// </summary>
    public static bool EnvironmentServesHome(string environment, string home)
    {
        var declared = environment.Split('\0')
            .FirstOrDefault(entry => entry.StartsWith("NETCLAW_HOME=", StringComparison.Ordinal))
            ?["NETCLAW_HOME=".Length..];
        var served = string.IsNullOrEmpty(declared) ? DefaultHome : declared;
        return Same(served, home);
    }
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
