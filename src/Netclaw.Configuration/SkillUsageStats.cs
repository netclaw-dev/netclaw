// -----------------------------------------------------------------------
// <copyright file="SkillUsageStats.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Wire types for the detailed skill-usage statistics endpoint.
/// </summary>
public static class SkillUsageStats
{
    public sealed class Response : IWireType
    {
        public required List<DailySkillRow> Daily { get; init; }
    }

    public sealed class DailySkillRow : IWireType
    {
        public required string Date { get; init; }

        public long TotalLoads { get; init; }

        public required List<MethodCount> Methods { get; init; }

        public required List<SkillCount> Skills { get; init; }
    }

    public sealed class MethodCount : IWireType
    {
        public required string Method { get; init; }

        public long Count { get; init; }
    }

    public sealed class SkillCount : IWireType
    {
        public required string SkillName { get; init; }

        public long TotalLoads { get; init; }

        public required List<MethodCount> Methods { get; init; }
    }
}
