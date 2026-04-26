using Netclaw.Configuration;
using ProtoBuf;

namespace Netclaw.Actors.Jobs;

/// <summary>
/// Lightweight record persisted in <c>SessionState.ActiveBackgroundJobs</c>
/// so the LLM knows what it's waiting for after compaction or session resumption.
/// </summary>
[ProtoContract]
public sealed record ActiveJobInfo
{
    [ProtoMember(1)]
    public required string JobId { get; init; }

    [ProtoMember(2)]
    public required string Command { get; init; }

    [ProtoMember(3)]
    public required string Rationale { get; init; }

    [ProtoMember(4)]
    public required long StartedAtMs { get; init; }

    [ProtoMember(5)]
    public TrustAudience Audience { get; init; } = TrustAudience.Personal;

    [ProtoMember(6)]
    public string Boundary { get; init; } = SecurityPolicyDefaults.PersonalBoundary;
}
