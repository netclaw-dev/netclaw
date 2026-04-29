// -----------------------------------------------------------------------
// <copyright file="MemoryExpiryDefaults.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Memory;

internal static class MemoryExpiryDefaults
{
    internal static readonly TimeSpan EvidenceExpiry = TimeSpan.FromDays(30);
    internal static readonly TimeSpan TraceExpiry = TimeSpan.FromHours(72);
}
