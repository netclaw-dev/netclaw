// -----------------------------------------------------------------------
// <copyright file="SubAgentFindingEnums.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

public enum SubAgentFindingShape
{
    Conclusion,
    Worklog,
    Transcript
}

public enum SubAgentFindingDurability
{
    Durable,
    Transient
}

public enum SubAgentFindingReusability
{
    Reusable,
    TaskLocal
}

public enum SubAgentFindingSensitivity
{
    Normal,
    Secret
}

public enum SubAgentFindingRecallMode
{
    Auto,
    Searchable,
    Never
}

public static class SubAgentFindingEnumExtensions
{
    public static string ToWireValue(this SubAgentFindingShape shape)
        => shape switch
        {
            SubAgentFindingShape.Conclusion => "conclusion",
            SubAgentFindingShape.Worklog => "worklog",
            SubAgentFindingShape.Transcript => "transcript",
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };

    public static string ToWireValue(this SubAgentFindingDurability durability)
        => durability switch
        {
            SubAgentFindingDurability.Durable => "durable",
            SubAgentFindingDurability.Transient => "transient",
            _ => throw new ArgumentOutOfRangeException(nameof(durability), durability, null)
        };

    public static string ToWireValue(this SubAgentFindingReusability reusability)
        => reusability switch
        {
            SubAgentFindingReusability.Reusable => "reusable",
            SubAgentFindingReusability.TaskLocal => "task-local",
            _ => throw new ArgumentOutOfRangeException(nameof(reusability), reusability, null)
        };

    public static string ToWireValue(this SubAgentFindingSensitivity sensitivity)
        => sensitivity switch
        {
            SubAgentFindingSensitivity.Normal => "normal",
            SubAgentFindingSensitivity.Secret => "secret",
            _ => throw new ArgumentOutOfRangeException(nameof(sensitivity), sensitivity, null)
        };

    public static string ToWireValue(this SubAgentFindingRecallMode recallMode)
        => recallMode switch
        {
            SubAgentFindingRecallMode.Auto => "auto",
            SubAgentFindingRecallMode.Searchable => "searchable",
            SubAgentFindingRecallMode.Never => "never",
            _ => throw new ArgumentOutOfRangeException(nameof(recallMode), recallMode, null)
        };
}
