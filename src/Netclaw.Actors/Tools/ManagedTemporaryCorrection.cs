// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryCorrection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>Advice that asks an agent to submit a different tool call. A correction grants no authority.</summary>
internal abstract record ToolCorrection
{
    private ToolCorrection() { }

    /// <summary>Suggests the current run's managed temporary directory instead of a platform temporary root.</summary>
    internal sealed record ManagedTemporaryDirectorySuggested(
        ManagedTemporaryCorrectionTarget Target) : ToolCorrection;

    /// <summary>Suggests a native tool instead of invoking that tool name through the shell.</summary>
    internal sealed record NativeToolSuggested(ToolName ToolName) : ToolCorrection;

    /// <summary>Suggests declaration of the shell directory as the current project.</summary>
    internal sealed record ProjectDirectorySuggested(string Directory) : ToolCorrection;
}

/// <summary>Captures the execution-relevant arguments of one corrected tool call.</summary>
internal abstract record ManagedTemporaryCallSemantics(string ToolName, TimeSpan Timeout)
{
    /// <summary>Captures one shell call without model-facing rationale.</summary>
    internal sealed record ShellCall(
        ApprovalShell Shell,
        string Command,
        string? WorkingDirectory,
        bool Background,
        TimeSpan Timeout)
        : ManagedTemporaryCallSemantics(ShellTool.ToolName, Timeout);

    /// <summary>Captures one structured file-write call.</summary>
    internal sealed record FileWriteCall(string Path, string? Content, TimeSpan Timeout)
        : ManagedTemporaryCallSemantics(FileWriteTool.ToolName, Timeout);

    /// <summary>Captures one structured file-edit call.</summary>
    internal sealed record FileEditCall(
        string Path,
        string? OldString,
        string? NewString,
        bool? ReplaceAll,
        TimeSpan Timeout)
        : ManagedTemporaryCallSemantics(FileEditTool.ToolName, Timeout);
}

/// <summary>Binds one exact corrected call to the platform root and suggested managed directory.</summary>
internal readonly record struct ManagedTemporaryCorrectionKey(
    ManagedTemporaryCallSemantics Call,
    ManagedTemporaryCorrectionTarget Target);

/// <summary>Describes an actor-owned change to the one-turn correction state.</summary>
internal abstract record ManagedTemporaryCorrectionChange
{
    private ManagedTemporaryCorrectionChange() { }

    /// <summary>Commits a correction key after its guidance reaches the model.</summary>
    internal sealed record Arm(ManagedTemporaryCorrectionKey Key) : ManagedTemporaryCorrectionChange;

    /// <summary>Removes a correction key after one matching retry claims it.</summary>
    internal sealed record Consume(ManagedTemporaryCorrectionKey Key) : ManagedTemporaryCorrectionChange;
}

/// <summary>Provides a thread-safe, consume-once view of the keys that one actor committed.</summary>
internal sealed class ManagedTemporaryCorrectionDispatch
{
    internal static ManagedTemporaryCorrectionDispatch Empty { get; } = new([]);

    private readonly IReadOnlyList<ManagedTemporaryCorrectionKey> _armed;
    private readonly ConcurrentDictionary<ManagedTemporaryCorrectionKey, byte> _consumed = new();

    internal ManagedTemporaryCorrectionDispatch(IEnumerable<ManagedTemporaryCorrectionKey> armed)
        => _armed = Array.AsReadOnly(armed.ToArray());

    internal bool TryConsume(ManagedTemporaryCallSemantics call, out ManagedTemporaryCorrectionKey key)
    {
        foreach (var candidate in _armed)
        {
            if (candidate.Call != call || !_consumed.TryAdd(candidate, 0))
                continue;

            key = candidate;
            return true;
        }

        key = default;
        return false;
    }
}

/// <summary>Owns correction keys for one actor turn. A key is armed after commit and consumed once.</summary>
internal sealed class ManagedTemporaryCorrectionState
{
    private readonly HashSet<ManagedTemporaryCorrectionKey> _keys = [];

    internal ManagedTemporaryCorrectionDispatch Snapshot() => new(_keys);

    internal void Apply(ManagedTemporaryCorrectionChange? change)
    {
        switch (change)
        {
            case ManagedTemporaryCorrectionChange.Arm arm:
                _keys.Add(arm.Key);
                break;
            case ManagedTemporaryCorrectionChange.Consume consume:
                _keys.Remove(consume.Key);
                break;
        }
    }

    internal void Clear() => _keys.Clear();
}

/// <summary>Builds shared correction state for parent and child tool execution paths.</summary>
internal static class ManagedTemporaryCorrection
{
    /// <summary>Projects a tool call to the fields that must remain equal on an immediate retry.</summary>
    internal static ManagedTemporaryCallSemantics? BuildCallSemantics(
        FunctionCallContent toolCall,
        ToolCallMeta? meta,
        TimeSpan timeout,
        ApprovalShell shell = ApprovalShell.Bash)
    {
        if (string.Equals(toolCall.Name, ShellTool.ToolName, StringComparison.Ordinal))
        {
            var command = ToolArgumentHelper.GetString(toolCall.Arguments, "Command")
                ?? ToolArgumentHelper.GetString(toolCall.Arguments, "command");
            if (string.IsNullOrWhiteSpace(command))
                return null;

            var explicitCwd = ToolArgumentHelper.GetString(toolCall.Arguments, "WorkingDirectory");
            return new ManagedTemporaryCallSemantics.ShellCall(
                shell,
                command,
                string.IsNullOrWhiteSpace(explicitCwd) ? null : explicitCwd,
                meta?.Background == true,
                timeout);
        }

        if (toolCall.Name is not (FileWriteTool.ToolName or FileEditTool.ToolName))
            return null;

        var path = ToolArgumentHelper.GetString(toolCall.Arguments, "Path")
            ?? ToolArgumentHelper.GetString(toolCall.Arguments, "path");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (toolCall.Name == FileWriteTool.ToolName)
        {
            return new ManagedTemporaryCallSemantics.FileWriteCall(
                path,
                ToolArgumentHelper.GetString(toolCall.Arguments, "Content"),
                timeout);
        }

        return new ManagedTemporaryCallSemantics.FileEditCall(
            path,
            ToolArgumentHelper.GetString(toolCall.Arguments, "OldString"),
            ToolArgumentHelper.GetString(toolCall.Arguments, "NewString"),
            ToolArgumentHelper.GetBoolStrict(toolCall.Arguments, "ReplaceAll"),
            timeout);
    }

    /// <summary>Builds the correction returned before an approval request.</summary>
    internal static string BuildSuggestion(string managedTemporaryDirectory)
        => "Tool execution deferred: use_managed_temporary_directory\n" +
           $"Managed temporary directory: '{managedTemporaryDirectory}'.";

    /// <summary>Builds the hint returned when the user denies the corrected retry.</summary>
    internal static string BuildDenialHint(string managedTemporaryDirectory)
        => $"Hint: Use the managed temporary directory '{managedTemporaryDirectory}' for disposable artifacts. " +
           "The shared platform temporary root is not a trusted root for this session.";
}
