// -----------------------------------------------------------------------
// <copyright file="SubAgentFindingReview.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Sessions;

internal enum SubAgentFindingReviewDecision
{
    Accepted,
    Deferred,
    Rejected
}

internal sealed record SubAgentFindingReviewResult(
    SubAgentFindingReviewDecision Decision,
    string? Reason);

internal static class SubAgentFindingReviewDecisionExtensions
{
    public static string ToWireValue(this SubAgentFindingReviewDecision decision)
        => decision switch
        {
            SubAgentFindingReviewDecision.Accepted => "accepted",
            SubAgentFindingReviewDecision.Deferred => "deferred",
            SubAgentFindingReviewDecision.Rejected => "rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null)
        };
}
