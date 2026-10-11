// -----------------------------------------------------------------------
// <copyright file="OneTimeConsent.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Frozen;

namespace Netclaw.Tools.Authorization.Consent;

/// <summary>
/// The operator's "Once" answer for one exact prompt. It names the tool and the
/// keys of the approval units that the operator saw: the authored patterns and
/// one digest for each candidate with its effective directory.
/// </summary>
/// <remarks>
/// The retry passes only when the new prompt produces the same key set. A
/// candidate that changes before the retry, for example after a symbolic link
/// swap, changes its key, so the gate asks again. The value lives on one
/// invocation attempt and is never stored.
/// </remarks>
public sealed record OneTimeConsent
{
    public OneTimeConsent(string toolName, IEnumerable<string> keys)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolName);
        ArgumentNullException.ThrowIfNull(keys);
        ToolName = toolName;
        Keys = keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The tool that the operator approved once.</summary>
    public string ToolName { get; }

    /// <summary>The approval-unit keys of the prompt that the operator answered.</summary>
    public IReadOnlySet<string> Keys { get; }

    /// <summary>
    /// True when a prompt for <paramref name="toolName"/> with exactly
    /// <paramref name="keys"/> is the prompt that the operator answered.
    /// </summary>
    public bool Covers(string toolName, IEnumerable<string> keys)
        => string.Equals(ToolName, toolName, StringComparison.Ordinal)
           && Keys.SetEquals(keys);

    /// <summary>True when both values name the same tool and the same key set.</summary>
    public bool Equals(OneTimeConsent? other)
        => other is not null && Covers(other.ToolName, other.Keys);

    public override int GetHashCode() => HashCode.Combine(ToolName, Keys.Count);
}
