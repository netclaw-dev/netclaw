// -----------------------------------------------------------------------
// <copyright file="ToolConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Media;

namespace Netclaw.Configuration;

/// <summary>
/// The Security and Tools configuration that the daemon binds at startup. The Tools defaults
/// depend on the resolved deployment posture, so both sections bind together here. The daemon,
/// <c>netclaw doctor</c>, and the tests use this one path.
/// </summary>
public sealed record PolicyConfiguration(
    SecurityPolicyConfig Security,
    EffectivePolicyDefaults Defaults,
    ToolConfig Tools,
    IReadOnlyList<string> ToolWarnings)
{
    public static PolicyConfiguration Bind(IConfiguration configuration)
    {
        var security = configuration.GetSection("Security").Get<SecurityPolicyConfig>() ?? new SecurityPolicyConfig();
        var defaults = SecurityPolicyDefaults.Resolve(security);
        var tools = ToolConfig.BindFromConfiguration(configuration.GetSection("Tools"), defaults.DeploymentPosture, out var warnings);
        return new PolicyConfiguration(security, defaults, tools, warnings);
    }
}

/// <summary>
/// Shared configuration for first-party tool execution.
/// </summary>
public sealed class ToolConfig
{
    public ShellExecutionMode? ShellMode { get; set; }

    /// <summary>
    /// The capture ceiling: the maximum characters of tool output captured (in
    /// bounded memory) to become the spill body written to a session file. It is
    /// NOT the inline budget — <c>SessionTuning.MaxInlineToolResultChars</c> (<c>N</c>)
    /// owns what the model sees inline. Output beyond this ceiling is drained-and-
    /// discarded (the source keeps draining so a live child never deadlocks) and the
    /// spill is a head+tail view. Sized so the spill is useful while staying
    /// redactable in a single in-memory pass.
    /// </summary>
    public int MaxOutputChars { get; set; } = 256_000;

    public ToolAudienceProfiles AudienceProfiles { get; set; } = new();
    public WebFetchConfig WebFetch { get; set; } = new();

    /// <summary>
    /// Additional shell command patterns to add to the hard deny list.
    /// These are verb-chain prefixes that are categorically blocked
    /// and cannot be approved. Added to the compiled-in defaults.
    /// </summary>
    public List<string> HardDenyPatterns { get; set; } = [];

    /// <summary>
    /// Binds the daemon <c>Tools</c> section on top of the defaults for
    /// <paramref name="posture"/> and validates the channel attachment policy. The caller logs
    /// each item in <paramref name="warnings"/> at startup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defaults come from <see cref="ToolAudienceProfileDefaults.CreateProfilesForPosture"/>,
    /// so a posture rule applies when netclaw.json does not set the key. For example, the
    /// Personal posture requires approval for <c>shell_execute</c> on the Personal audience.
    /// <c>netclaw init</c> writes only the posture, not the audience profiles. The caller passes
    /// the posture that <see cref="SecurityPolicyDefaults.Resolve"/> gives for the same
    /// configuration.
    /// </para>
    /// <para>
    /// The Microsoft binder adds configured items to a list that already has items, so an
    /// operator could not narrow a default grant list. This method binds each list in
    /// <see cref="DefaultedLists"/> with no default items, then applies the default only when
    /// the key is absent. For a configured key: items replace the default; <c>[]</c> or an
    /// empty value gives an empty list; <c>null</c> or <c>{}</c> gives an empty list and a
    /// warning; a scalar value, or a value from one source and items from another source,
    /// stops startup; an invalid enum item stops startup.
    /// </para>
    /// </remarks>
    public static ToolConfig BindFromConfiguration(
        IConfigurationSection section,
        DeploymentPosture posture,
        out IReadOnlyList<string> warnings)
    {
        // An undefined posture would silently get the Team and Public defaults.
        if (!Enum.IsDefined(posture))
            throw new ArgumentOutOfRangeException(nameof(posture), posture, "Undefined deployment posture.");

        var toolConfig = CreatePostureDefaults(posture);
        foreach (var list in DefaultedLists)
            list.Clear(toolConfig);
        section.Bind(toolConfig);

        var defaults = CreatePostureDefaults(posture);
        var found = new List<string>();
        foreach (var list in DefaultedLists)
            list.ApplyConfiguredOrDefault(section, toolConfig, defaults, found);
        MapLegacyDefaultAllowedTools(toolConfig.AudienceProfiles.Public, TrustAudience.Public, found);
        MapLegacyDefaultAllowedTools(toolConfig.AudienceProfiles.Team, TrustAudience.Team, found);
        warnings = found;

        var attachmentErrors = toolConfig.AudienceProfiles.ValidateChannelAttachments();
        if (attachmentErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid Tools.AudienceProfiles.ChannelAttachments configuration: "
                + string.Join("; ", attachmentErrors));
        }

        return toolConfig;
    }

    private static ToolConfig CreatePostureDefaults(DeploymentPosture posture)
        => new() { AudienceProfiles = ToolAudienceProfileDefaults.CreateProfilesForPosture(posture) };

    // `netclaw init` wrote the complete default list, and the old binder added the current
    // defaults to it. Replacement would silently remove tools that later releases added to the
    // default, such as tool_output_read. Only an exact older default list maps to the current
    // default. A list that differs in any way is operator intent and is never widened. A copy of
    // the current default already gives the current tools, so it stays as written with no warning.
    private static void MapLegacyDefaultAllowedTools(ToolAudienceProfile profile, TrustAudience audience, List<string> warnings)
    {
        if (profile.ToolsMode != ToolProfileMode.Allowlist
            || ToolAudienceProfileDefaults.IsCurrentDefaultAllowedTools(audience, profile.AllowedTools)
            || !ToolAudienceProfileDefaults.IsLegacyDefaultAllowedTools(audience, profile.AllowedTools))
        {
            return;
        }

        warnings.Add(ToolAudienceProfileDefaults.DescribeLegacyDefaultAllowedTools(audience, profile.AllowedTools));
        profile.AllowedTools = [.. ToolAudienceProfileDefaults.CurrentDefaultAllowedTools(audience)];
    }

    // Every list in ToolConfig that has default items. Each one is an allow list, so null and
    // {} can safely mean "empty". ToolConfigBindingTests fails when a new list with default
    // items appears, which forces a review of that list and a new row here.
    internal static IReadOnlyList<IDefaultedList> DefaultedLists { get; } =
    [
        new DefaultedList<string>("AudienceProfiles:Public:AllowedTools", c => c.AudienceProfiles.Public.AllowedTools, (c, v) => c.AudienceProfiles.Public.AllowedTools = v),
        new DefaultedList<string>("AudienceProfiles:Public:ReadFiles:Roots", c => c.AudienceProfiles.Public.ReadFiles.Roots, (c, v) => c.AudienceProfiles.Public.ReadFiles.Roots = v),
        new DefaultedList<string>("AudienceProfiles:Public:WriteFiles:Roots", c => c.AudienceProfiles.Public.WriteFiles.Roots, (c, v) => c.AudienceProfiles.Public.WriteFiles.Roots = v),
        new DefaultedList<string>("AudienceProfiles:Public:AttachFiles:Roots", c => c.AudienceProfiles.Public.AttachFiles.Roots, (c, v) => c.AudienceProfiles.Public.AttachFiles.Roots = v),
        new DefaultedList<AttachmentCategory>("AudienceProfiles:Public:ChannelAttachments:AllowedCategories", c => c.AudienceProfiles.Public.ChannelAttachments.AllowedCategories, (c, v) => c.AudienceProfiles.Public.ChannelAttachments.AllowedCategories = v),
        new DefaultedList<string>("AudienceProfiles:Team:AllowedTools", c => c.AudienceProfiles.Team.AllowedTools, (c, v) => c.AudienceProfiles.Team.AllowedTools = v),
        new DefaultedList<string>("AudienceProfiles:Team:ReadFiles:Roots", c => c.AudienceProfiles.Team.ReadFiles.Roots, (c, v) => c.AudienceProfiles.Team.ReadFiles.Roots = v),
        new DefaultedList<string>("AudienceProfiles:Team:WriteFiles:Roots", c => c.AudienceProfiles.Team.WriteFiles.Roots, (c, v) => c.AudienceProfiles.Team.WriteFiles.Roots = v),
        new DefaultedList<string>("AudienceProfiles:Team:AttachFiles:Roots", c => c.AudienceProfiles.Team.AttachFiles.Roots, (c, v) => c.AudienceProfiles.Team.AttachFiles.Roots = v),
        new DefaultedList<AttachmentCategory>("AudienceProfiles:Team:ChannelAttachments:AllowedCategories", c => c.AudienceProfiles.Team.ChannelAttachments.AllowedCategories, (c, v) => c.AudienceProfiles.Team.ChannelAttachments.AllowedCategories = v),
        new DefaultedList<AttachmentCategory>("AudienceProfiles:Personal:ChannelAttachments:AllowedCategories", c => c.AudienceProfiles.Personal.ChannelAttachments.AllowedCategories, (c, v) => c.AudienceProfiles.Personal.ChannelAttachments.AllowedCategories = v),
        new DefaultedList<string>("AudienceProfiles:GlobalReadRoots", c => c.AudienceProfiles.GlobalReadRoots, (c, v) => c.AudienceProfiles.GlobalReadRoots = v),
        new DefaultedList<string>("WebFetch:HttpAllowList", c => c.WebFetch.HttpAllowList, (c, v) => c.WebFetch.HttpAllowList = v),
    ];

    internal interface IDefaultedList
    {
        string Key { get; }

        void Clear(ToolConfig config);

        void ApplyConfiguredOrDefault(IConfigurationSection tools, ToolConfig config, ToolConfig defaults, List<string> warnings);
    }

    private sealed record DefaultedList<T>(string Key, Func<ToolConfig, List<T>> Get, Action<ToolConfig, List<T>> Set)
        : IDefaultedList
    {
        public void Clear(ToolConfig config) => Set(config, []);

        public void ApplyConfiguredOrDefault(IConfigurationSection tools, ToolConfig config, ToolConfig defaults, List<string> warnings)
        {
            var key = tools.GetSection(Key);
            var path = key.Path.Replace(':', '.');
            var items = key.GetChildren().ToList();
            if (items.Count > 0)
            {
                // IConfiguration merges sources, so an empty NETCLAW_* variable cannot remove
                // items that netclaw.json sets. Silent items would ignore the operator.
                if (key.Value is not null)
                {
                    throw new InvalidOperationException(
                        $"Configuration key '{path}' has list items and also a value. One source sets items and "
                        + "another source sets a value. Remove one of them, for example the NETCLAW_* variable.");
                }

                ValidateItems(items, path, Get(config).Count);
                return;
            }

            if (key.Value is { Length: > 0 })
            {
                // The value is not echoed: a misplaced secret must not reach a startup error.
                throw new InvalidOperationException($"Configuration key '{path}' must be a list, but it has a scalar value.");
            }

            // JSON [] and an empty variable give "" (the list stays empty). JSON null and {} give
            // a key with a null value, which only the parent lists. An absent key gets the default.
            var present = key.Value is not null
                || tools.GetSection(Key[..Key.LastIndexOf(':')]).GetChildren()
                    .Any(child => string.Equals(child.Key, key.Key, StringComparison.OrdinalIgnoreCase));
            if (!present)
                Set(config, [.. Get(defaults)]);
            else if (key.Value is null)
                warnings.Add($"{path} is null or an empty object; treating it as an empty list.");
        }

        // The binder silently drops an item that it cannot convert, and it parses numbers and
        // comma lists as enum values. A grant list must fail on such an item, not narrow or widen.
        private static void ValidateItems(List<IConfigurationSection> items, string path, int boundCount)
        {
            if (typeof(T).IsEnum)
            {
                var names = Enum.GetNames(typeof(T));
                var invalid = items.FirstOrDefault(item => item.Value is null
                    || !names.Contains(item.Value, StringComparer.OrdinalIgnoreCase));
                if (invalid is not null)
                {
                    // The value is not echoed, the same as the scalar error.
                    throw new InvalidOperationException(
                        $"Configuration key '{path}.{invalid.Key}' is not a valid {typeof(T).Name} name.");
                }
            }

            if (boundCount != items.Count)
                throw new InvalidOperationException($"Configuration key '{path}' has an item that is not a valid {typeof(T).Name}.");
        }
    }
}
