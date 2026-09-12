// -----------------------------------------------------------------------
// <copyright file="SkillSyncResult.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Configuration;

/// <summary>
/// Wire contracts for one external skill source sync pass.
/// </summary>
public static class SkillSyncResult
{
    /// <summary>Result data for one pass.</summary>
    public sealed class Response : IWireType
    {
        public required string PassId { get; init; }

        public required List<SourceRow> Sources { get; init; }

        public required InventoryRow Inventory { get; init; }
    }

    /// <summary>Result data for one configured source.</summary>
    public sealed class SourceRow : IWireType
    {
        public required string Name { get; init; }

        public int ChangedCount { get; init; }

        public int UnchangedCount { get; init; }

        public int RejectedCount { get; init; }

        public int FailedCount { get; init; }

        /// <summary>Result for the optional native sub-agent sidecar.</summary>
        public required string Sidecar { get; init; }

        /// <summary>A safe operator message. This value never includes secrets.</summary>
        public string? Error { get; init; }
    }

    /// <summary>Result data for the inventory publication owned by this pass.</summary>
    public sealed class InventoryRow : IWireType
    {
        public required bool Succeeded { get; init; }

        public int AcceptedCount { get; init; }

        public int RejectedCount { get; init; }

        public string? Error { get; init; }
    }
}
