// -----------------------------------------------------------------------
// <copyright file="ModelThinking.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// The provider-neutral thinking level of a model definition. <see cref="Max"/> means the highest
/// level that the provider offers. An unset level means "the model's default".
/// </summary>
public enum ThinkingLevel
{
    Off,
    Low,
    Medium,
    High,
    Max,
}

/// <summary>
/// The wire dialect that a provider entry uses to send a thinking level and the reasoning
/// suppression intent. <see cref="Unresolved"/> is not a configuration value: it is the dialect of an
/// OpenAI-compatible provider that has no <c>VendorOptions:ThinkingDialect</c> value.
/// </summary>
public enum ThinkingDialect
{
    Native,
    DeepSeek,
    ReasoningEffort,
    ChatTemplateKwargs,
    OllamaThink,
    None,
    Unresolved,
}

public enum ThinkingSupportKind
{
    Unknown,
    None,
    AlwaysOn,
    Controllable,
}

/// <summary>
/// Whether a model thinks, and the levels that it accepts. A model that thinks but cannot turn
/// thinking off is <see cref="ThinkingSupportKind.Controllable"/> without <see cref="ThinkingLevel.Off"/>.
/// </summary>
public sealed record ThinkingSupport
{
    private ThinkingSupport(ThinkingSupportKind kind, IReadOnlyList<ThinkingLevel> levels)
    {
        Kind = kind;
        Levels = levels;
    }

    public ThinkingSupportKind Kind { get; }

    /// <summary>The accepted levels in enum order. Empty unless <see cref="Kind"/> is Controllable.</summary>
    public IReadOnlyList<ThinkingLevel> Levels { get; }

    public static ThinkingSupport Unknown { get; } = new(ThinkingSupportKind.Unknown, []);

    public static ThinkingSupport None { get; } = new(ThinkingSupportKind.None, []);

    public static ThinkingSupport AlwaysOn { get; } = new(ThinkingSupportKind.AlwaysOn, []);

    public static ThinkingSupport Controllable(params ThinkingLevel[] levels)
        => new(ThinkingSupportKind.Controllable, levels.Distinct().Order().ToArray());

    public bool Equals(ThinkingSupport? other)
        => other is not null && Kind == other.Kind && Levels.SequenceEqual(other.Levels);

    public override int GetHashCode() => HashCode.Combine(Kind, Levels.Count);

    public override string ToString()
        => Kind == ThinkingSupportKind.Controllable ? $"Controllable({string.Join(", ", Levels)})" : Kind.ToString();
}
