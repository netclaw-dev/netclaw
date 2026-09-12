// -----------------------------------------------------------------------
// <copyright file="ApprovalEntryWireCodec.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
namespace Netclaw.Configuration;

internal static class ApprovalEntryWireCodec
{
    private static readonly HashSet<string> AllowedMembers =
        ["shell", "match", "verbTokens", "verb", "directory", "createdAt"];

    internal static ApprovalEntry ReadVersion3(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An approval entry must be an object.");
        }

        var members = ApprovalEntryValidation.ReadUniqueMembers(element, AllowedMembers);
        RequireMember(members, "directory");
        RequireMember(members, "createdAt");

        var hasShell = members.ContainsKey("shell");
        var hasMatch = members.TryGetValue("match", out var matchElement);
        var hasTokens = members.ContainsKey("verbTokens");
        var hasVerb = members.ContainsKey("verb");

        ApprovalEntry entry;
        if (!hasShell && !hasMatch && !hasTokens && hasVerb)
        {
            var wire = element.Deserialize(ApprovalStoreJsonContext.Default.NonShellApprovalEntryWire)
                       ?? throw new JsonException("The non-shell approval entry is null.");
            entry = new ApprovalEntry(ReadRequiredString(wire.Verb, "verb"))
            {
                Directory = wire.Directory,
                CreatedAt = wire.CreatedAt,
            };
        }
        else
        {
            if (!hasShell || !hasMatch)
            {
                throw new JsonException("A shell entry must contain shell and match.");
            }

            var match = ReadMatch(matchElement);
            entry = match switch
            {
                ApprovalMatchKind.TokenPrefix when hasTokens && !hasVerb =>
                    ReadTokenPrefix(element),
                ApprovalMatchKind.LegacyExact when hasVerb && !hasTokens =>
                    ReadLegacyExact(element),
                _ => throw new JsonException("The approval entry mixes incompatible phrase fields."),
            };
        }

        ApprovalEntryValidation.ValidateVersion3(entry);
        return entry;
    }

    internal static ApprovalEntryWire WriteVersion3(ApprovalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ApprovalEntryValidation.ValidateVersion3(entry);

        return entry.Match switch
        {
            ApprovalMatchKind.TokenPrefix => new TokenPrefixApprovalEntryWire
            {
                Shell = entry.Shell!.Value.ToString(),
                Match = ApprovalMatchKind.TokenPrefix.ToString(),
                VerbTokens = entry.VerbTokens!.Cast<string?>().ToArray(),
                Directory = entry.Directory,
                CreatedAt = entry.CreatedAt,
            },
            ApprovalMatchKind.LegacyExact => new LegacyExactApprovalEntryWire
            {
                Shell = entry.Shell!.Value.ToString(),
                Match = ApprovalMatchKind.LegacyExact.ToString(),
                Verb = entry.Verb,
                Directory = entry.Directory,
                CreatedAt = entry.CreatedAt,
            },
            null => new NonShellApprovalEntryWire
            {
                Verb = entry.Verb,
                Directory = entry.Directory,
                CreatedAt = entry.CreatedAt,
            },
            _ => throw new JsonException("The approval entry has an invalid match kind."),
        };
    }

    private static ApprovalEntry ReadTokenPrefix(JsonElement element)
    {
        var wire = element.Deserialize(ApprovalStoreJsonContext.Default.TokenPrefixApprovalEntryWire)
                   ?? throw new JsonException("The token-prefix approval entry is null.");
        return ApprovalEntry.CreateTokenPrefix(
            ReadShell(ReadRequiredString(wire.Shell, "shell")),
            ReadTokens(wire.VerbTokens),
            wire.Directory,
            wire.CreatedAt);
    }

    private static ApprovalEntry ReadLegacyExact(JsonElement element)
    {
        var wire = element.Deserialize(ApprovalStoreJsonContext.Default.LegacyExactApprovalEntryWire)
                   ?? throw new JsonException("The legacy-exact approval entry is null.");
        return ApprovalEntry.CreateLegacyExact(
            ReadShell(ReadRequiredString(wire.Shell, "shell")),
            ReadRequiredString(wire.Verb, "verb"),
            wire.Directory,
            wire.CreatedAt);
    }

    private static ApprovalShell ReadShell(string value) => value switch
    {
        "Bash" => ApprovalShell.Bash,
        "PowerShell" => ApprovalShell.PowerShell,
        _ => throw new JsonException("The shell value is invalid."),
    };

    private static IReadOnlyList<string> ReadTokens(string?[]? values)
    {
        if (values is null)
        {
            throw new JsonException("verbTokens must be an array.");
        }

        return values
            .Select(static value => ReadRequiredString(value, "verb token"))
            .ToArray();
    }

    private static string ReadRequiredString(string? value, string name) =>
        value ?? throw new JsonException($"The {name} must not be null.");

    private static ApprovalMatchKind ReadMatch(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("The match must be a string.");
        }

        return element.GetString() switch
        {
            "TokenPrefix" => ApprovalMatchKind.TokenPrefix,
            "LegacyExact" => ApprovalMatchKind.LegacyExact,
            _ => throw new JsonException("The match value is invalid."),
        };
    }

    private static void RequireMember(
        IReadOnlyDictionary<string, JsonElement> members,
        string name)
    {
        if (!members.ContainsKey(name))
        {
            throw new JsonException($"Missing JSON member '{name}'.");
        }
    }
}
