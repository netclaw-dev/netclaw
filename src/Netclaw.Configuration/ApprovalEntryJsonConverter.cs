// -----------------------------------------------------------------------
// <copyright file="ApprovalEntryJsonConverter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// Reads and writes the closed version-3 approval entry forms.
/// </summary>
public sealed class ApprovalEntryJsonConverter : JsonConverter<ApprovalEntry>
{
    /// <inheritdoc />
    public override ApprovalEntry Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ApprovalEntryValidation.ReadVersion3(document.RootElement);
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        ApprovalEntry value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        ApprovalEntryValidation.ValidateVersion3(value);

        writer.WriteStartObject();
        if (value.Match == ApprovalMatchKind.TokenPrefix)
        {
            writer.WriteString("shell", value.Shell!.Value.ToString());
            writer.WriteString("match", ApprovalMatchKind.TokenPrefix.ToString());
            writer.WritePropertyName("verbTokens");
            writer.WriteStartArray();
            foreach (var token in value.VerbTokens!)
            {
                writer.WriteStringValue(token);
            }

            writer.WriteEndArray();
        }
        else if (value.Match == ApprovalMatchKind.LegacyExact)
        {
            writer.WriteString("shell", value.Shell!.Value.ToString());
            writer.WriteString("match", ApprovalMatchKind.LegacyExact.ToString());
            writer.WriteString("verb", value.Verb);
        }
        else
        {
            writer.WriteString("verb", value.Verb);
        }

        writer.WritePropertyName("directory");
        if (value.Directory is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.Directory);
        }

        writer.WritePropertyName("createdAt");
        if (value.CreatedAt is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.CreatedAt.Value);
        }

        writer.WriteEndObject();
    }
}

internal static class ApprovalEntryValidation
{
    private static readonly HashSet<string> AllowedMembers =
        ["shell", "match", "verbTokens", "verb", "directory", "createdAt"];

    internal static ApprovalEntry ReadVersion3(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An approval entry must be an object.");
        }

        var members = ReadUniqueMembers(element, AllowedMembers);
        RequireMember(members, "directory");
        RequireMember(members, "createdAt");

        var directory = ReadNullableString(members["directory"], "directory");
        var createdAt = ReadNullableTimestamp(members["createdAt"]);
        var hasShell = members.TryGetValue("shell", out var shellElement);
        var hasMatch = members.TryGetValue("match", out var matchElement);
        var hasTokens = members.TryGetValue("verbTokens", out var tokensElement);
        var hasVerb = members.TryGetValue("verb", out var verbElement);

        if (!hasShell && !hasMatch && !hasTokens && hasVerb)
        {
            var verb = ReadRequiredString(verbElement, "verb");
            var entry = new ApprovalEntry(verb)
            {
                Directory = directory,
                CreatedAt = createdAt,
            };
            ValidateVersion3(entry);
            return entry;
        }

        if (!hasShell || !hasMatch)
        {
            throw new JsonException("A shell entry must contain shell and match.");
        }

        var shell = ReadShell(shellElement);
        var match = ReadMatch(matchElement);
        var shellEntry = match switch
        {
            ApprovalMatchKind.TokenPrefix when hasTokens && !hasVerb =>
                ApprovalEntry.CreateTokenPrefix(
                    shell,
                    ReadTokens(tokensElement),
                    directory,
                    createdAt),
            ApprovalMatchKind.LegacyExact when hasVerb && !hasTokens =>
                ApprovalEntry.CreateLegacyExact(
                    shell,
                    ReadRequiredString(verbElement, "verb"),
                    directory,
                    createdAt),
            _ => throw new JsonException("The approval entry mixes incompatible phrase fields."),
        };
        ValidateVersion3(shellEntry);
        return shellEntry;
    }

    internal static void ValidateVersion3(ApprovalEntry entry)
    {
        ValidatePersistedString(entry.Verb, "verb", allowWhitespace: true);
        if (entry.Verb.Length == 0 || entry.Verb != entry.Verb.Trim())
        {
            throw new JsonException("The verb must be nonempty and canonical.");
        }

        if (entry.Directory is not null)
        {
            ValidatePersistedString(entry.Directory, "directory", allowWhitespace: true);
            var hasValidPath = entry.Shell switch
            {
                ApprovalShell.PowerShell => IsCanonicalWindowsAbsolutePath(entry.Directory),
                ApprovalShell.Bash => IsCanonicalPosixAbsolutePath(entry.Directory),
                null => IsCanonicalPosixAbsolutePath(entry.Directory) ||
                        IsCanonicalWindowsAbsolutePath(entry.Directory),
                _ => false,
            };
            if (!hasValidPath)
            {
                throw new JsonException("The directory must be an absolute path.");
            }
        }

        if (entry.Match is null && entry.Shell is null && entry.VerbTokens is null)
        {
            return;
        }

        if (entry.Shell is null || !Enum.IsDefined(entry.Shell.Value) ||
            entry.Match is null || !Enum.IsDefined(entry.Match.Value))
        {
            throw new JsonException("The shell phrase has an invalid discriminator.");
        }

        switch (entry.Match)
        {
            case ApprovalMatchKind.TokenPrefix when entry.VerbTokens is not null:
                ValidateTokens(entry.VerbTokens);
                if (!string.Equals(entry.Verb, string.Join(" ", entry.VerbTokens), StringComparison.Ordinal))
                {
                    throw new JsonException("The token phrase and display verb differ.");
                }

                return;
            case ApprovalMatchKind.LegacyExact when entry.VerbTokens is null:
                return;
            default:
                throw new JsonException("The approval entry has an invalid phrase form.");
        }
    }

    internal static void ValidateTokens(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            throw new JsonException("A token-prefix phrase must have at least one token.");
        }

        foreach (var token in tokens)
        {
            ValidatePersistedString(token, "verb token", allowWhitespace: false);
            if (token.Length == 0)
            {
                throw new JsonException("A verb token must not be empty.");
            }
        }
    }

    internal static void ValidateVerb(string verb)
    {
        ValidatePersistedString(verb, "verb", allowWhitespace: true);
        if (verb.Length == 0 || verb != verb.Trim())
        {
            throw new ArgumentException("The verb must be nonempty and canonical.", nameof(verb));
        }
    }

    internal static bool IsCanonicalWindowsAbsolutePath(string path)
    {
        if (path.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        var hasDriveRoot = path.Length >= 3 &&
                           char.IsAsciiLetter(path[0]) &&
                           path[1] == ':' &&
                           path[2] == '\\';
        var hasUncRoot = path.Length > 4 &&
                         path[0] == '\\' &&
                         path[1] == '\\';
        if (!hasDriveRoot && !hasUncRoot)
        {
            return false;
        }

        if (hasDriveRoot && path.Length == 3)
        {
            return true;
        }

        var start = hasDriveRoot ? 3 : 2;
        var remainder = path[start..];
        if (remainder.Contains("\\\\", StringComparison.Ordinal) ||
            remainder.EndsWith('\\'))
        {
            return false;
        }

        var components = remainder.Split('\\', StringSplitOptions.None);
        if (hasUncRoot && components.Length < 2)
        {
            return false;
        }

        return components.All(static component =>
            component.Length > 0 && component is not "." and not "..");
    }

    internal static bool IsCanonicalPosixAbsolutePath(string path)
    {
        if (path.Length == 0 || path[0] != '/')
        {
            return false;
        }

        if (path == "/")
        {
            return true;
        }

        if (path.EndsWith('/') || path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        return path[1..]
            .Split('/', StringSplitOptions.None)
            .All(static component => component.Length > 0 && component is not "." and not "..");
    }

    internal static Dictionary<string, JsonElement> ReadUniqueMembers(
        JsonElement element,
        IReadOnlySet<string> allowedMembers)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedMembers.Contains(property.Name))
            {
                throw new JsonException($"Unknown JSON member '{property.Name}'.");
            }

            if (!result.TryAdd(property.Name, property.Value))
            {
                throw new JsonException($"Duplicate JSON member '{property.Name}'.");
            }
        }

        return result;
    }

    internal static void ValidatePersistedString(
        string value,
        string fieldName,
        bool allowWhitespace)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new JsonException($"The {fieldName} has invalid Unicode.");
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                throw new JsonException($"The {fieldName} has invalid Unicode.");
            }

            if (char.IsControl(character) || IsBidiControl(character) ||
                !allowWhitespace && char.IsWhiteSpace(character))
            {
                throw new JsonException($"The {fieldName} has a prohibited character.");
            }
        }
    }

    private static Dictionary<string, JsonElement> ReadUniqueMembers(
        JsonElement element,
        params string[] allowedMembers) =>
        ReadUniqueMembers(element, new HashSet<string>(allowedMembers, StringComparer.Ordinal));

    private static void RequireMember(
        IReadOnlyDictionary<string, JsonElement> members,
        string name)
    {
        if (!members.ContainsKey(name))
        {
            throw new JsonException($"Missing JSON member '{name}'.");
        }
    }

    private static string ReadRequiredString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"The {name} must be a string.");
        }

        return element.GetString() ?? throw new JsonException($"The {name} must not be null.");
    }

    private static string? ReadNullableString(JsonElement element, string name) =>
        element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            _ => throw new JsonException($"The {name} must be a string or null."),
        };

    private static DateTimeOffset? ReadNullableTimestamp(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String || !element.TryGetDateTimeOffset(out var value))
        {
            throw new JsonException("The createdAt value must be an ISO-8601 timestamp or null.");
        }

        return value;
    }

    private static ApprovalShell ReadShell(JsonElement element)
    {
        var value = ReadRequiredString(element, "shell");
        return value switch
        {
            "Bash" => ApprovalShell.Bash,
            "PowerShell" => ApprovalShell.PowerShell,
            _ => throw new JsonException("The shell value is invalid."),
        };
    }

    private static ApprovalMatchKind ReadMatch(JsonElement element)
    {
        var value = ReadRequiredString(element, "match");
        return value switch
        {
            "TokenPrefix" => ApprovalMatchKind.TokenPrefix,
            "LegacyExact" => ApprovalMatchKind.LegacyExact,
            _ => throw new JsonException("The match value is invalid."),
        };
    }

    private static IReadOnlyList<string> ReadTokens(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("verbTokens must be an array.");
        }

        var tokens = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            tokens.Add(ReadRequiredString(item, "verb token"));
        }

        ValidateTokens(tokens);
        return tokens;
    }

    private static bool IsBidiControl(char character) =>
        character is '\u061c' or '\u200e' or '\u200f' or
            >= '\u202a' and <= '\u202e' or
            >= '\u2066' and <= '\u2069';
}
