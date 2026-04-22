using Netclaw.Actors.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels;

public interface IAclDecision
{
    bool IsAllowed { get; }
    string? DenyReason { get; }
    TrustAudience Audience { get; }
    PrincipalClassification Principal { get; }
    SourceProvenance Provenance { get; }
}
