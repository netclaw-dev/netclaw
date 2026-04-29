// -----------------------------------------------------------------------
// <copyright file="SkillActivationRouter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Netclaw.Configuration;

namespace Netclaw.Actors.Skills;

public enum SkillActivationPath
{
    Inline,
    Routed
}

public sealed record SkillActivationDecision(
    SkillActivationPath Path,
    string? RoutedSubagent,
    string? ErrorMessage)
{
    public bool IsError => ErrorMessage is not null;

    public static SkillActivationDecision Inline() => new(SkillActivationPath.Inline, null, null);

    public static SkillActivationDecision Routed(string subagent) => new(SkillActivationPath.Routed, subagent, null);

    public static SkillActivationDecision Error(string errorMessage) => new(SkillActivationPath.Inline, null, errorMessage);
}

public static partial class SkillActivationRouter
{
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SubagentNameRegex();

    public static SkillActivationDecision Resolve(SkillEntry skill)
    {
        if (!skill.HasSubagentRoutingMetadata)
            return SkillActivationDecision.Inline();

        if (!string.IsNullOrWhiteSpace(skill.SubagentMetadataError))
        {
            return SkillActivationDecision.Error(
                $"Skill '/{skill.Name}' has invalid metadata.subagent: {skill.SubagentMetadataError} "
                + $"Fix or remove metadata.subagent in {skill.FilePath}.");
        }

        var target = skill.Subagent?.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            return SkillActivationDecision.Error(
                $"Skill '/{skill.Name}' has invalid metadata.subagent: value is required. "
                + $"Fix or remove metadata.subagent in {skill.FilePath}.");
        }

        if (!IsValidSubagentName(target))
        {
            return SkillActivationDecision.Error(
                $"Skill '/{skill.Name}' has invalid metadata.subagent '{target}': expected lowercase kebab-case name. "
                + $"Fix or remove metadata.subagent in {skill.FilePath}.");
        }

        return SkillActivationDecision.Routed(target);
    }

    public static bool IsValidSubagentName(string value)
        => SubagentNameRegex().IsMatch(value);

    public static string UnknownTargetError(string skillName, string target)
        => $"Skill '/{skillName}' routes to subagent '{target}', but that subagent is not registered. "
           + "Add the missing subagent definition or fix metadata.subagent on the skill.";

    public static string InternalTargetError(string skillName, string target)
        => $"Skill '/{skillName}' routes to subagent '{target}', but that subagent is internal-only and not user-invocable. "
           + "Use a user-facing subagent or fix metadata.subagent on the skill.";
}
