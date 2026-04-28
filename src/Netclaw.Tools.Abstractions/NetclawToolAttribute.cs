// -----------------------------------------------------------------------
// <copyright file="NetclawToolAttribute.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>
/// Marks a class as a Netclaw tool for source generation.
/// The generator emits JSON schema, argument parsing, and
/// <see cref="INetclawTool"/> implementation glue at compile time.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NetclawToolAttribute : Attribute
{
    public NetclawToolAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }

    /// <summary>Tool name as seen by the LLM (e.g. "shell_execute").</summary>
    public string Name { get; }

    /// <summary>Human-readable description included in the tool schema.</summary>
    public string Description { get; }

    /// <summary>ACL grant category for policy filtering (e.g. "shell", "file").</summary>
    public string Grant { get; set; } = "default";
}
