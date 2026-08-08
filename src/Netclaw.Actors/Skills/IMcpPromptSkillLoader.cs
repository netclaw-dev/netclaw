// -----------------------------------------------------------------------
// <copyright file="IMcpPromptSkillLoader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Skills;

public interface IMcpPromptSkillLoader
{
    ValueTask<McpPromptSkillLoadResult> LoadAsync(
        McpPromptSkillSource source,
        IReadOnlyDictionary<string, string>? arguments,
        ToolInvocationContext context,
        CancellationToken cancellationToken);
}

public sealed record McpPromptSkillLoadResult(
    bool Success,
    string? Description,
    IReadOnlyList<McpPromptSkillMessage> Messages,
    string? Error)
{
    public static McpPromptSkillLoadResult Failed(string error)
        => new(false, null, [], error);

    public static McpPromptSkillLoadResult Loaded(
        string? description,
        IReadOnlyList<McpPromptSkillMessage> messages)
        => new(true, description, messages, null);
}

public sealed record McpPromptSkillMessage(string Role, string Text);
