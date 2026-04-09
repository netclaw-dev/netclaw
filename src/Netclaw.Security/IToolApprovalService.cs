using Netclaw.Configuration;

namespace Netclaw.Security;

public interface IToolApprovalService
{
    Task<IReadOnlyList<string>> GetUnapprovedPatternsAsync(
        string? sessionId,
        TrustAudience audience,
        string toolName,
        IReadOnlyList<string> patterns,
        CancellationToken ct = default);

    Task RecordApprovalAsync(
        string sessionId,
        TrustAudience audience,
        string toolName,
        IReadOnlyList<string> patterns,
        bool persistent,
        CancellationToken ct = default);
}
