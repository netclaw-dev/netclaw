namespace Netclaw.Actors.Memory;

/// <summary>
/// Classification of memory by durability and purpose.
/// </summary>
public enum MemoryClass
{
    DurableFact,
    Evidence,
    Trace
}

/// <summary>
/// Structural kind of a stored memory item.
/// </summary>
public enum MemoryKind
{
    Document,
    Record,
    Unknown
}

/// <summary>
/// Controls how and whether a memory participates in recall.
/// </summary>
public enum MemoryRecallMode
{
    Auto,
    Searchable,
    Never,
    Manual
}

/// <summary>
/// Sensitivity classification for a memory item.
/// </summary>
public enum MemorySensitivity
{
    Normal,
    Secret
}

/// <summary>
/// Write semantics that determine how a memory item is persisted and updated.
/// </summary>
public enum MemoryUpdateSemantics
{
    MergeDocument,
    AppendDocument,
    ImmutableRecord,
    ConversationTrace,
    Tombstone,
    SupersedeRecord
}

/// <summary>
/// The event that triggered a memory checkpoint.
/// </summary>
public enum CheckpointTriggerType
{
    TurnComplete,
    ExplicitMemoryRequest,
    SubagentFindings,
    ObservedMemoryProposals,
    VerifiedToolFinding,
    CompactionBoundary
}

/// <summary>
/// Subject identity classification for memory proposals.
/// </summary>
public enum SubjectKind
{
    User,
    Assistant,
    Agent
}

public static class MemoryDomainEnumExtensions
{
    // ── MemoryClass ────────────────────────────────────────────────

    public static string ToWireValue(this MemoryClass value) => value switch
    {
        MemoryClass.DurableFact => "durable_fact",
        MemoryClass.Evidence => "evidence",
        MemoryClass.Trace => "trace",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static bool TryFromWireValue(string? wire, out MemoryClass value)
    {
        if (string.Equals(wire, "durable_fact", StringComparison.OrdinalIgnoreCase))
        { value = MemoryClass.DurableFact; return true; }
        if (string.Equals(wire, "evidence", StringComparison.OrdinalIgnoreCase))
        { value = MemoryClass.Evidence; return true; }
        if (string.Equals(wire, "trace", StringComparison.OrdinalIgnoreCase))
        { value = MemoryClass.Trace; return true; }
        value = default;
        return false;
    }

    // ── MemoryKind ─────────────────────────────────────────────────

    public static string ToWireValue(this MemoryKind value) => value switch
    {
        MemoryKind.Document => "document",
        MemoryKind.Record => "record",
        MemoryKind.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static bool TryFromWireValue(string? wire, out MemoryKind value)
    {
        if (string.Equals(wire, "document", StringComparison.OrdinalIgnoreCase))
        { value = MemoryKind.Document; return true; }
        if (string.Equals(wire, "record", StringComparison.OrdinalIgnoreCase))
        { value = MemoryKind.Record; return true; }
        if (string.Equals(wire, "unknown", StringComparison.OrdinalIgnoreCase))
        { value = MemoryKind.Unknown; return true; }
        value = default;
        return false;
    }

    // ── MemoryRecallMode ───────────────────────────────────────────

    public static string ToWireValue(this MemoryRecallMode value) => value switch
    {
        MemoryRecallMode.Auto => "auto",
        MemoryRecallMode.Searchable => "searchable",
        MemoryRecallMode.Never => "never",
        MemoryRecallMode.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static bool TryFromWireValue(string? wire, out MemoryRecallMode value)
    {
        if (string.Equals(wire, "auto", StringComparison.OrdinalIgnoreCase))
        { value = MemoryRecallMode.Auto; return true; }
        if (string.Equals(wire, "searchable", StringComparison.OrdinalIgnoreCase))
        { value = MemoryRecallMode.Searchable; return true; }
        if (string.Equals(wire, "never", StringComparison.OrdinalIgnoreCase))
        { value = MemoryRecallMode.Never; return true; }
        if (string.Equals(wire, "manual", StringComparison.OrdinalIgnoreCase))
        { value = MemoryRecallMode.Manual; return true; }
        value = default;
        return false;
    }

    // ── MemorySensitivity ──────────────────────────────────────────

    public static string ToWireValue(this MemorySensitivity value) => value switch
    {
        MemorySensitivity.Normal => "normal",
        MemorySensitivity.Secret => "secret",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static bool TryFromWireValue(string? wire, out MemorySensitivity value)
    {
        if (string.Equals(wire, "normal", StringComparison.OrdinalIgnoreCase))
        { value = MemorySensitivity.Normal; return true; }
        if (string.Equals(wire, "secret", StringComparison.OrdinalIgnoreCase))
        { value = MemorySensitivity.Secret; return true; }
        value = default;
        return false;
    }

    // ── MemoryUpdateSemantics ──────────────────────────────────────

    public static string ToWireValue(this MemoryUpdateSemantics value) => value switch
    {
        MemoryUpdateSemantics.MergeDocument => "merge-document",
        MemoryUpdateSemantics.AppendDocument => "append-document",
        MemoryUpdateSemantics.ImmutableRecord => "immutable-record",
        MemoryUpdateSemantics.ConversationTrace => "conversation_trace",
        MemoryUpdateSemantics.Tombstone => "tombstone",
        MemoryUpdateSemantics.SupersedeRecord => "supersede-record",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static bool TryFromWireValue(string? wire, out MemoryUpdateSemantics value)
    {
        if (string.Equals(wire, "merge-document", StringComparison.OrdinalIgnoreCase))
        { value = MemoryUpdateSemantics.MergeDocument; return true; }
        if (string.Equals(wire, "append-document", StringComparison.OrdinalIgnoreCase))
        { value = MemoryUpdateSemantics.AppendDocument; return true; }
        if (string.Equals(wire, "immutable-record", StringComparison.OrdinalIgnoreCase))
        { value = MemoryUpdateSemantics.ImmutableRecord; return true; }
        if (string.Equals(wire, "conversation_trace", StringComparison.OrdinalIgnoreCase))
        { value = MemoryUpdateSemantics.ConversationTrace; return true; }
        if (string.Equals(wire, "tombstone", StringComparison.OrdinalIgnoreCase))
        { value = MemoryUpdateSemantics.Tombstone; return true; }
        if (string.Equals(wire, "supersede-record", StringComparison.OrdinalIgnoreCase))
        { value = MemoryUpdateSemantics.SupersedeRecord; return true; }
        value = default;
        return false;
    }

    // ── CheckpointTriggerType ──────────────────────────────────────

    public static string ToWireValue(this CheckpointTriggerType value) => value switch
    {
        CheckpointTriggerType.TurnComplete => "turn-complete",
        CheckpointTriggerType.ExplicitMemoryRequest => "explicit-memory-request",
        CheckpointTriggerType.SubagentFindings => "subagent-findings",
        CheckpointTriggerType.ObservedMemoryProposals => "observed-memory-proposals",
        CheckpointTriggerType.VerifiedToolFinding => "verified-tool-finding",
        CheckpointTriggerType.CompactionBoundary => "compaction-boundary",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static bool TryFromWireValue(string? wire, out CheckpointTriggerType value)
    {
        if (string.Equals(wire, "turn-complete", StringComparison.OrdinalIgnoreCase))
        { value = CheckpointTriggerType.TurnComplete; return true; }
        if (string.Equals(wire, "explicit-memory-request", StringComparison.OrdinalIgnoreCase))
        { value = CheckpointTriggerType.ExplicitMemoryRequest; return true; }
        if (string.Equals(wire, "subagent-findings", StringComparison.OrdinalIgnoreCase))
        { value = CheckpointTriggerType.SubagentFindings; return true; }
        if (string.Equals(wire, "observed-memory-proposals", StringComparison.OrdinalIgnoreCase))
        { value = CheckpointTriggerType.ObservedMemoryProposals; return true; }
        if (string.Equals(wire, "verified-tool-finding", StringComparison.OrdinalIgnoreCase))
        { value = CheckpointTriggerType.VerifiedToolFinding; return true; }
        if (string.Equals(wire, "compaction-boundary", StringComparison.OrdinalIgnoreCase))
        { value = CheckpointTriggerType.CompactionBoundary; return true; }
        value = default;
        return false;
    }

    // ── SubjectKind ────────────────────────────────────────────────

    public static string ToWireValue(this SubjectKind value) => value switch
    {
        SubjectKind.User => "user",
        SubjectKind.Assistant => "assistant",
        SubjectKind.Agent => "agent",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static bool TryFromWireValue(string? wire, out SubjectKind value)
    {
        if (string.Equals(wire, "user", StringComparison.OrdinalIgnoreCase))
        { value = SubjectKind.User; return true; }
        if (string.Equals(wire, "assistant", StringComparison.OrdinalIgnoreCase))
        { value = SubjectKind.Assistant; return true; }
        if (string.Equals(wire, "agent", StringComparison.OrdinalIgnoreCase))
        { value = SubjectKind.Agent; return true; }
        value = default;
        return false;
    }

    // ── MemoryProposalOperation (from MemorySidecarContracts) ──────

    public static string ToWireValue(this Sessions.MemoryProposalOperation value) => value switch
    {
        Sessions.MemoryProposalOperation.UpsertDocument => "upsert_document",
        Sessions.MemoryProposalOperation.AppendRecord => "append_record",
        Sessions.MemoryProposalOperation.Ignore => "ignore",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static bool TryFromWireValue(string? wire, out Sessions.MemoryProposalOperation value)
    {
        if (string.Equals(wire, "upsert_document", StringComparison.OrdinalIgnoreCase))
        { value = Sessions.MemoryProposalOperation.UpsertDocument; return true; }
        if (string.Equals(wire, "append_record", StringComparison.OrdinalIgnoreCase))
        { value = Sessions.MemoryProposalOperation.AppendRecord; return true; }
        if (string.Equals(wire, "ignore", StringComparison.OrdinalIgnoreCase))
        { value = Sessions.MemoryProposalOperation.Ignore; return true; }
        value = default;
        return false;
    }
}
