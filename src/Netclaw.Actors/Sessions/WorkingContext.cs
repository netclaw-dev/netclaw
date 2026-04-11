using System.Collections.Immutable;
using ProtoBuf;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Durable session state for "what the agent is currently working on."
/// Persisted through <see cref="SessionSnapshot"/> and
/// <see cref="Protocol.SessionCompacted"/> events so it survives compaction,
/// actor recovery, and daemon restart without depending on the observer LLM
/// to reconstruct it.
///
/// Currently carries a single field: <see cref="RecentFiles"/> — a bounded
/// ring buffer of file paths the agent has recently read/written/edited.
/// Most-recent-first, deduped on repeat access, capped at
/// <see cref="MaxRecentFiles"/> entries.
///
/// Goals and progress markers are intentionally NOT part of this struct.
/// The original design included `OpenGoals` and `ProgressMarkers` slots that
/// would be populated by an observer-output parser running after compaction,
/// but that parser is a separate OpenSpec change that hasn't landed yet.
/// Per the project's "no hypothetical future requirements" rule, the fields
/// live with their first real caller.
///
/// Also explicitly not in this struct: <c>CurrentWorkingDirectory</c> and
/// <c>ActiveProjectPath</c>. Those are tracked as GitHub issues
/// (Aaronontheweb/netclaw#595 and #596) and will land in separate changes.
/// </summary>
[ProtoContract]
public sealed record WorkingContext
{
    /// <summary>
    /// Maximum number of entries retained in <see cref="RecentFiles"/>.
    /// Older entries fall off the tail when a new distinct path is pushed.
    /// </summary>
    public const int MaxRecentFiles = 10;

    public static readonly WorkingContext Empty = new();

    [ProtoMember(1)]
    public ImmutableList<string> RecentFiles { get; init; } =
        ImmutableList<string>.Empty;

    /// <summary>
    /// Returns true when no recent files are tracked. Consumers use this to
    /// suppress the <c>[working-context]</c> block from the dynamic context
    /// injection when there is nothing to report.
    /// </summary>
    public bool IsEmpty => RecentFiles.IsEmpty;

    /// <summary>
    /// Push a file path to the front of <see cref="RecentFiles"/>, dedupe on
    /// repeat, and cap the list at <see cref="MaxRecentFiles"/> entries.
    /// Rejects paths containing control characters (newline, carriage return,
    /// null) — such paths are either malformed or adversarial (prompt
    /// injection into the <see cref="ToContextBlock"/> output). Returns the
    /// same instance when <paramref name="path"/> is already at the head or
    /// is rejected, so <c>ReferenceEquals</c> callers can short-circuit.
    /// </summary>
    public WorkingContext AddRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return this;

        // Reject control characters: a path containing `\n`, `\r`, or `\0`
        // would break out of the [working-context] block rendering and
        // inject arbitrary content into the LLM's system prompt. Real file
        // paths never contain these characters; anything that does is
        // adversarial or malformed input that shouldn't be tracked.
        if (path.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0)
            return this;

        // No-op when path is already the most-recent entry. Returning the
        // same instance lets ReferenceEquals-guarded callers skip the
        // surrounding SessionState allocation entirely.
        if (RecentFiles.Count > 0 && string.Equals(RecentFiles[0], path, StringComparison.Ordinal))
            return this;

        var builder = ImmutableList.CreateBuilder<string>();
        builder.Add(path);

        foreach (var existing in RecentFiles)
        {
            if (builder.Count >= MaxRecentFiles)
                break;

            if (string.Equals(existing, path, StringComparison.Ordinal))
                continue;

            builder.Add(existing);
        }

        return this with { RecentFiles = builder.ToImmutable() };
    }

    /// <summary>
    /// Render this context as a <c>[working-context]</c> block suitable
    /// for injection into the dynamic system message. Returns an empty
    /// string when the whole context is <see cref="IsEmpty"/> — callers
    /// should check that and suppress the injection entirely rather than
    /// emit a barren header.
    /// </summary>
    public string ToContextBlock()
    {
        if (IsEmpty)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.Append("[working-context]");
        sb.Append("\nrecent_files:");
        foreach (var path in RecentFiles)
            sb.Append("\n  - ").Append(path);

        return sb.ToString();
    }
}
