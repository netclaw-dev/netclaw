using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal sealed record GetUnapprovedPatterns(
    string? SessionId,
    TrustAudience Audience,
    ToolName ToolName,
    IReadOnlyList<string> Patterns);

internal sealed record UnapprovedPatternsResponse(IReadOnlyList<string> Patterns);

internal sealed record RecordToolApproval(
    string SessionId,
    TrustAudience Audience,
    ToolName ToolName,
    IReadOnlyList<string> Patterns,
    bool Persistent);
