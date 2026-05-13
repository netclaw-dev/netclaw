// -----------------------------------------------------------------------
// <copyright file="SafeVerbList.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// Curated list of demonstrably read-only shell verb chains the approval gate
/// auto-allows when invoked inside a trusted zone. Loaded exclusively from
/// the daemon's bundled <c>safe-verbs.&lt;os&gt;.json</c> embedded resource —
/// there is no user-override file by design. Adding a verb to this list
/// loosens the policy (more silent auto-pass cases), so widening must go
/// through code review and a daemon release, not a config edit. The agent
/// has no path to extend its own read-only verb list at runtime.
///
/// Membership is exact-equality against the verb chain extracted by the
/// shell parser (case rules from
/// <see cref="ToolApprovalEntryComparer.Comparer"/>: Ordinal on POSIX,
/// OrdinalIgnoreCase on Windows). Mutating verbs (e.g. <c>git push</c>,
/// <c>sed -i</c>) are intentionally absent — they remain subject to the
/// interactive approval gate.
/// </summary>
public sealed class SafeVerbList
{
    public static readonly SafeVerbList Empty = new(new HashSet<string>(ToolApprovalEntryComparer.Comparer));

    private readonly HashSet<string> _verbs;

    internal SafeVerbList(HashSet<string> verbs)
    {
        _verbs = verbs;
    }

    /// <summary>
    /// Builds a <see cref="SafeVerbList"/> from an explicit verb collection.
    /// Used by tests and by callers that synthesize a list outside the
    /// bundled-plus-override loading path.
    /// </summary>
    public static SafeVerbList FromVerbs(IEnumerable<string> verbs)
    {
        var set = new HashSet<string>(ToolApprovalEntryComparer.Comparer);
        foreach (var verb in verbs)
        {
            if (!string.IsNullOrWhiteSpace(verb))
                set.Add(verb.Trim());
        }
        return new SafeVerbList(set);
    }

    /// <summary>
    /// Returns true when the candidate verb chain is on the safe-verbs list.
    /// </summary>
    public bool Contains(string candidateVerb)
        => !string.IsNullOrEmpty(candidateVerb) && _verbs.Contains(candidateVerb);

    /// <summary>The verbs in this list. Stable ordering; intended for diagnostics, not lookups.</summary>
    public IReadOnlyCollection<string> Verbs => _verbs;
}

/// <summary>
/// JSON deserialization shape for <c>safe-verbs.*.json</c> files.
/// </summary>
internal sealed class SafeVerbListFile
{
    [JsonPropertyName("verbs")]
    public List<string> Verbs { get; set; } = new();
}

/// <summary>
/// Loads the bundled safe-verbs list for the current OS from the embedded
/// resource. There is no user-override path — the safe-verbs list is
/// immutable at runtime so the agent cannot widen its own read-only
/// auto-pass set through file writes. Widening goes through code review
/// and a daemon release.
/// </summary>
public static class SafeVerbLoader
{
    private const string LinuxResourceName = "Netclaw.Configuration.SafeVerbs.safe-verbs.linux.json";
    private const string WindowsResourceName = "Netclaw.Configuration.SafeVerbs.safe-verbs.windows.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Loads the bundled safe-verbs list for the current OS. Always returns
    /// the shipped defaults — no merge from disk, no user-overridable
    /// surface. Throws <see cref="InvalidOperationException"/> only if the
    /// embedded resource itself is missing from the assembly, which is a
    /// build-packaging bug, not a runtime condition.
    /// </summary>
    public static SafeVerbList Load() => Load(OperatingSystem.IsWindows());

    internal static SafeVerbList Load(bool isWindows)
    {
        var comparer = ToolApprovalEntryComparer.Comparer;
        var verbs = new HashSet<string>(comparer);

        foreach (var verb in LoadBundled(isWindows))
            verbs.Add(verb);

        return new SafeVerbList(verbs);
    }

    private static IEnumerable<string> LoadBundled(bool isWindows)
    {
        var resourceName = isWindows ? WindowsResourceName : LinuxResourceName;
        var assembly = typeof(SafeVerbLoader).Assembly;

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Bundled safe-verbs resource '{resourceName}' is missing from {assembly.FullName}. "
                + "This is a build packaging error: SafeVerbs/*.json must be embedded.");

        var file = JsonSerializer.Deserialize<SafeVerbListFile>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Bundled safe-verbs resource '{resourceName}' deserialized to null.");

        foreach (var verb in file.Verbs)
        {
            if (!string.IsNullOrWhiteSpace(verb))
                yield return verb.Trim();
        }
    }
}
