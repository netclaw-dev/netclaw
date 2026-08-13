// -----------------------------------------------------------------------
// <copyright file="SafeVerbList.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// An immutable catalog of reviewed shell phrases. Each phrase classifies
/// shell-authored invocation effects under its declared policy boundary.
/// </summary>
public sealed class SafeVerbList
{
    public static readonly SafeVerbList Empty = Create(
        OperatingSystem.IsWindows() ? ApprovalShell.PowerShell : ApprovalShell.Bash,
        []);

    private readonly ApprovalShell _shell;
    private readonly IReadOnlyList<SafeVerbPolicyEntry> _entries;
    private readonly HashSet<string> _exactPhrases;
    private readonly IReadOnlyCollection<string> _verbs;

    private SafeVerbList(
        ApprovalShell shell,
        IReadOnlyList<SafeVerbPolicyEntry> entries,
        HashSet<string> exactPhrases,
        IReadOnlyCollection<string> verbs)
    {
        _shell = shell;
        _entries = entries;
        _exactPhrases = exactPhrases;
        _verbs = verbs;
    }

    /// <summary>
    /// Builds a reviewed catalog for the current platform from explicit
    /// phrases. This compatibility factory classifies each phrase as a
    /// reviewed diagnostic.
    /// </summary>
    public static SafeVerbList FromVerbs(IEnumerable<string> verbs) =>
        FromVerbs(
            OperatingSystem.IsWindows() ? ApprovalShell.PowerShell : ApprovalShell.Bash,
            verbs);

    /// <summary>
    /// Builds a reviewed catalog for one canonical shell from explicit
    /// phrases. This factory classifies each phrase as a reviewed diagnostic.
    /// </summary>
    public static SafeVerbList FromVerbs(
        ApprovalShell shell,
        IEnumerable<string> verbs)
    {
        ArgumentNullException.ThrowIfNull(verbs);
        if (!Enum.IsDefined(shell))
            throw new ArgumentOutOfRangeException(nameof(shell));

        var comparer = ComparerFor(shell);
        var phrases = new HashSet<string>(comparer);
        var orderedPhrases = new List<string>();
        var entries = new List<SafeVerbPolicyEntry>();
        foreach (var verb in verbs)
        {
            if (string.IsNullOrWhiteSpace(verb))
                continue;

            var phrase = verb.Trim();
            if (!phrases.Add(phrase))
                continue;

            var tokens = phrase.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            orderedPhrases.Add(phrase);
            entries.Add(new SafeVerbPolicyEntry(
                Array.AsReadOnly(tokens),
                SafeVerbClassification.ReviewedDiagnostic));
        }

        return Create(shell, entries, orderedPhrases);
    }

    /// <summary>
    /// Returns true when the complete phrase is present in the catalog.
    /// </summary>
    public bool Contains(string candidateVerb) =>
        !string.IsNullOrEmpty(candidateVerb) && _exactPhrases.Contains(candidateVerb);

    /// <summary>
    /// Finds a reviewed diagnostic phrase at the start of the canonical
    /// tokens. The token count identifies the matched parser prefix.
    /// </summary>
    public bool TryMatchReviewedDiagnostic(
        ApprovalShell shell,
        IReadOnlyList<string>? candidateVerbTokens,
        out int matchedTokenCount)
    {
        matchedTokenCount = 0;
        if (shell != _shell
            || candidateVerbTokens is not { Count: > 0 }
            || candidateVerbTokens.Any(static token =>
                token.Length == 0 || token.Any(char.IsWhiteSpace)))
        {
            return false;
        }

        foreach (var entry in _entries)
        {
            if (entry.Classification != SafeVerbClassification.ReviewedDiagnostic
                || entry.Tokens.Count > candidateVerbTokens.Count)
            {
                continue;
            }

            var matches = true;
            for (var index = 0; index < entry.Tokens.Count; index++)
            {
                if (!ToolApprovalEntryComparer.Equals(
                        entry.Tokens[index],
                        candidateVerbTokens[index],
                        shell))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                matchedTokenCount = Math.Max(matchedTokenCount, entry.Tokens.Count);
        }

        return matchedTokenCount > 0;
    }

    /// <summary>
    /// Returns true when a listed phrase starts the candidate and an operand
    /// follows it. This compatibility API does not participate in safe-policy
    /// authorization.
    /// </summary>
    public bool IsOperandBearingMatch(string candidateVerb, string listedVerb) =>
        _exactPhrases.Contains(listedVerb)
        && candidateVerb.Length > listedVerb.Length
        && candidateVerb[listedVerb.Length] == ' '
        && _exactPhrases.Comparer.Equals(candidateVerb[..listedVerb.Length], listedVerb);

    /// <summary>
    /// The reviewed phrases in stable resource order. This property is for
    /// diagnostics, not policy matching.
    /// </summary>
    public IReadOnlyCollection<string> Verbs => _verbs;

    internal static SafeVerbList Create(
        ApprovalShell shell,
        IEnumerable<SafeVerbPolicyEntry> entries,
        IReadOnlyCollection<string>? exactPhrases = null)
    {
        if (!Enum.IsDefined(shell))
            throw new ArgumentOutOfRangeException(nameof(shell));

        var comparer = ComparerFor(shell);
        var immutableEntries = entries
            .Select(static entry => new SafeVerbPolicyEntry(
                Array.AsReadOnly(entry.Tokens.ToArray()),
                entry.Classification))
            .ToArray();
        var verbs = exactPhrases?.ToArray()
            ?? immutableEntries
                .Select(static entry => string.Join(' ', entry.Tokens))
                .ToArray();
        return new SafeVerbList(
            shell,
            Array.AsReadOnly(immutableEntries),
            new HashSet<string>(verbs, comparer),
            Array.AsReadOnly(verbs));
    }

    private static StringComparer ComparerFor(ApprovalShell shell) => shell switch
    {
        ApprovalShell.Bash => StringComparer.Ordinal,
        ApprovalShell.PowerShell => StringComparer.OrdinalIgnoreCase,
        _ => throw new ArgumentOutOfRangeException(nameof(shell)),
    };
}

internal enum SafeVerbClassification
{
    ReviewedDiagnostic = 0,
}

internal sealed record SafeVerbPolicyEntry(
    IReadOnlyList<string> Tokens,
    SafeVerbClassification Classification);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SafeVerbListFile
{
    [JsonPropertyName("$comment")]
    public string? Comment { get; init; }

    public ApprovalShell? Shell { get; init; }

    public List<SafeVerbPolicyEntryFile>? Entries { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SafeVerbPolicyEntryFile
{
    public List<string>? Tokens { get; init; }

    public SafeVerbClassification? Classification { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SafeVerbListFile))]
internal sealed partial class SafeVerbCatalogJsonContext : JsonSerializerContext;

/// <summary>
/// Loads the immutable reviewed catalog from the bundled platform resource.
/// </summary>
public static class SafeVerbLoader
{
    private const string LinuxResourceName = "Netclaw.Configuration.SafeVerbs.safe-verbs.linux.json";
    private const string WindowsResourceName = "Netclaw.Configuration.SafeVerbs.safe-verbs.windows.json";

    /// <summary>
    /// Loads the bundled catalog for the current operating system.
    /// </summary>
    public static SafeVerbList Load() => Load(OperatingSystem.IsWindows());

    /// <summary>
    /// Loads the bundled catalog for the selected platform identity.
    /// </summary>
    public static SafeVerbList Load(bool isWindows)
    {
        var expectedShell = isWindows ? ApprovalShell.PowerShell : ApprovalShell.Bash;
        var file = LoadBundled(isWindows);
        if (file.Shell != expectedShell)
        {
            throw new InvalidDataException(
                $"Bundled safe-verb resource declares '{file.Shell}' instead of '{expectedShell}'.");
        }

        if (file.Entries is null)
            throw new InvalidDataException("Bundled safe-verb resource has no entries array.");

        var comparer = isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var phrases = new HashSet<string>(comparer);
        var entries = new List<SafeVerbPolicyEntry>(file.Entries.Count);
        foreach (var entry in file.Entries)
        {
            if (entry.Classification != SafeVerbClassification.ReviewedDiagnostic
                || entry.Tokens is not { Count: > 0 }
                || entry.Tokens.Any(static token =>
                    token.Length == 0
                    || token.Any(char.IsWhiteSpace)
                    || !string.Equals(token, token.Trim(), StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Bundled safe-verb resource contains an invalid policy entry.");
            }

            var phrase = string.Join(' ', entry.Tokens);
            if (!phrases.Add(phrase))
                throw new InvalidDataException($"Bundled safe-verb resource contains duplicate phrase '{phrase}'.");

            entries.Add(new SafeVerbPolicyEntry(
                Array.AsReadOnly(entry.Tokens.ToArray()),
                entry.Classification.Value));
        }

        return SafeVerbList.Create(expectedShell, entries);
    }

    private static SafeVerbListFile LoadBundled(bool isWindows)
    {
        var resourceName = isWindows ? WindowsResourceName : LinuxResourceName;
        var assembly = typeof(SafeVerbLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Bundled safe-verb resource '{resourceName}' is missing from {assembly.FullName}. "
                + "SafeVerbs resources must be embedded.");

        return JsonSerializer.Deserialize(
                   stream,
                   SafeVerbCatalogJsonContext.Default.SafeVerbListFile)
               ?? throw new InvalidDataException(
                   $"Bundled safe-verb resource '{resourceName}' deserialized to null.");
    }
}
