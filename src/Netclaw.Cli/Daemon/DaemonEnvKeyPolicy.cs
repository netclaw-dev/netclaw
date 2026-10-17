// -----------------------------------------------------------------------
// <copyright file="DaemonEnvKeyPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;

namespace Netclaw.Cli.Daemon;

internal static class DaemonEnvKeyPolicy
{
    private const string KeyPrefix = "NETCLAW_";

    private static readonly HashSet<string> ProcessSettings =
        new(["NETCLAW_HOME", "NETCLAW_DAEMON_ENDPOINT"], StringComparer.Ordinal);

    private static readonly Lazy<JsonObject> Schema = new(LoadSchema);

    internal static bool TryAccept(string key, string value, out string reason)
    {
        reason = string.Empty;
        if (ProcessSettings.Contains(key))
            return AcceptUnlessUrlHoldsCredentials(key, value, out reason);

        var segments = key[KeyPrefix.Length..].Split("__");
        var schema = Schema.Value;
        var resolution = Resolve(schema, schema, segments, section: string.Empty);

        switch (resolution.Outcome)
        {
            case Outcome.Undeclared:
                reason = $"The --env key '{key}' is not a setting that the netclaw.json schema declares. "
                    + "Check the spelling, or set the value in netclaw.json. Store a credential with `netclaw secrets set`.";
                return false;
            case Outcome.FreeForm:
                reason = $"The --env key '{key}' is inside the free-form section '{resolution.FreeFormSection}'. "
                    + "The schema gives no meaning to a name there, and daemon.env is plain text, so netclaw cannot tell a credential from a setting. "
                    + "Set the value in netclaw.json, or store a credential with `netclaw secrets set`.";
                return false;
            case Outcome.Section:
                reason = $"The --env key '{key}' names a section or a list, not a setting. Name a property or an index inside it, or set the value in netclaw.json. "
                    + "Store a credential with `netclaw secrets set`.";
                return false;
        }

        // A name that the operator chooses (a log category, a channel id) can be anything, so the
        // value must be one of the declared choices. A choice cannot hold a credential.
        if (resolution.Choices is { } choices && !choices.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            reason = $"The --env value for '{key}' must be one of: {string.Join(", ", choices)}.";
            return false;
        }

        var node = JsonValue.Create(value);
        foreach (var (name, declared) in resolution.DeclaredPath)
        {
            if (CanHoldText(declared) && SchemaFixResolver.HoldsCredential(name, node))
            {
                reason = $"Refusing to write '{key}' to daemon.env. The property '{name}' holds a credential, "
                    + "and daemon.env is plain text. Store the credential with `netclaw secrets set`.";
                return false;
            }
        }

        return AcceptUnlessUrlHoldsCredentials(key, value, out reason);
    }

    private static bool AcceptUnlessUrlHoldsCredentials(string key, string value, out string reason)
    {
        if (SchemaFixResolver.HasUrlUserInfo(value))
        {
            reason = $"Refusing to write '{key}' to daemon.env. The value is a URL that holds a credential, "
                + "and daemon.env is plain text. Store the credential with `netclaw secrets set`.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private enum Outcome
    {
        Declared,
        Undeclared,
        FreeForm,
        Section,
    }

    private readonly record struct Resolution(
        Outcome Outcome,
        IReadOnlyList<(string Name, JsonObject Schema)> DeclaredPath,
        string FreeFormSection,
        IReadOnlyList<string>? Choices)
    {
        internal static Resolution Declared => new(Outcome.Declared, [], string.Empty, null);

        internal static Resolution DeclaredChoice(IReadOnlyList<string>? choices) => new(Outcome.Declared, [], string.Empty, choices);

        internal static Resolution Undeclared => new(Outcome.Undeclared, [], string.Empty, null);

        internal static Resolution NamesSection => new(Outcome.Section, [], string.Empty, null);

        // The top level of netclaw.json has no section name to report, and the schema declares each of its settings.
        internal static Resolution FreeFormIn(string section)
            => section.Length == 0 ? Undeclared : new(Outcome.FreeForm, [], section, null);
    }

    // The schema walk is SchemaFixResolver.StepInto, the same one that the doctor fix uses. This method adds
    // what only an environment key needs: a case-insensitive match, the oneOf alternatives, and the
    // decision whether a name in a keyed map or an open section is a setting or free text.
    private static Resolution Resolve(JsonObject root, JsonObject node, ReadOnlySpan<string> segments, string section)
    {
        if (Alternatives(node) is { } alternatives)
        {
            var best = Resolution.Undeclared;
            foreach (var alternative in alternatives)
            {
                if (alternative is not JsonObject shape || SchemaFixResolver.FollowRef(root, shape) is not { } followed)
                    continue;

                var resolution = Resolve(root, followed, segments, section);
                if (resolution.Outcome == Outcome.Declared)
                    return resolution;
                if (resolution.Outcome != Outcome.Undeclared)
                    best = resolution;
            }

            return best;
        }

        if (segments.IsEmpty)
            return IsSection(node) ? Resolution.NamesSection : Resolution.Declared;

        var rest = segments[1..];
        if (SchemaFixResolver.StepInto(root, node, segments[0], ignoreCase: true) is { } child)
        {
            switch (child.Kind)
            {
                case SchemaFixResolver.SchemaChildKind.Property:
                    var resolution = Resolve(root, child.Schema, rest, child.Name);
                    return resolution.Outcome == Outcome.Declared
                        ? resolution with { DeclaredPath = [(child.Name, child.Schema), .. resolution.DeclaredPath] }
                        : resolution;
                case SchemaFixResolver.SchemaChildKind.MapEntry:
                    if (HasShape(child.Schema))
                        return Resolve(root, child.Schema, rest, section);
                    return rest.IsEmpty && IsFixedChoice(child.Schema)
                        ? Resolution.DeclaredChoice(ChoicesOf(child.Schema))
                        : Resolution.FreeFormIn(section);
                default:
                    return Resolve(root, child.Schema, rest, section);
            }
        }

        // An open object takes any name. When every declared property of it is the same list of choices,
        // as in Logging:LogLevel:{Category}, a name that the schema does not declare takes those choices.
        if (node["additionalProperties"] is JsonValue open && open.TryGetValue<bool>(out var anything) && anything)
        {
            return rest.IsEmpty && SharedChoices(root, node) is { } shared
                ? Resolution.DeclaredChoice(shared)
                : Resolution.FreeFormIn(section);
        }

        return Resolution.Undeclared;
    }

    private static JsonArray? Alternatives(JsonObject node)
        => node["oneOf"] as JsonArray ?? node["anyOf"] as JsonArray;

    private static bool HasShape(JsonObject node)
        => node["properties"] is JsonObject || node["items"] is JsonObject || Alternatives(node) is not null;

    private static bool IsSection(JsonObject node)
        => node["properties"] is JsonObject
           || node["items"] is JsonObject
           || node["additionalProperties"] is JsonObject
           || (node["additionalProperties"] is JsonValue open && open.TryGetValue<bool>(out var anything) && anything);

    private static bool IsFixedChoice(JsonObject node)
        => node["enum"] is JsonArray || TypeNames(node).All(type => type is "boolean" or "integer" or "number" or "null");

    private static IReadOnlyList<string>? ChoicesOf(JsonObject node)
        => node["enum"] is JsonArray choices
            ? choices.Select(choice => choice?.GetValue<string>()).OfType<string>().ToList()
            : null;

    private static IReadOnlyList<string>? SharedChoices(JsonObject root, JsonObject node)
    {
        if (node["properties"] is not JsonObject properties || properties.Count == 0)
            return null;

        IReadOnlyList<string>? shared = null;
        foreach (var (_, declared) in properties)
        {
            if (declared is not JsonObject schema
                || SchemaFixResolver.FollowRef(root, schema) is not { } followed
                || ChoicesOf(followed) is not { } choices
                || (shared is not null && !shared.SequenceEqual(choices)))
            {
                return null;
            }

            shared = choices;
        }

        return shared;
    }

    private static bool CanHoldText(JsonObject node)
    {
        var types = TypeNames(node).ToList();
        return types.Count == 0 || types.Any(type => type is "string" or "object" or "array");
    }

    private static IEnumerable<string> TypeNames(JsonObject node)
        => node["type"] switch
        {
            JsonValue single when single.TryGetValue<string>(out var name) => [name],
            JsonArray many => many.Select(item => item?.GetValue<string>()).OfType<string>(),
            _ => [],
        };

    private static JsonObject LoadSchema()
    {
        var text = EmbeddedSchemaLoader.LoadConfigSchema(EmbeddedSchemaLoader.CurrentSchemaVersion)
            ?? throw new InvalidOperationException("The embedded config schema is missing, so netclaw cannot check the --env keys.");
        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidOperationException("The embedded config schema is not a JSON object.");
    }
}
