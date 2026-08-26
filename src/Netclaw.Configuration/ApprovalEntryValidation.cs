// -----------------------------------------------------------------------
// <copyright file="ApprovalEntryValidation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;

namespace Netclaw.Configuration;

internal static class ApprovalEntryValidation
{
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

    private static bool IsBidiControl(char character) =>
        character is '\u061c' or '\u200e' or '\u200f' or
            >= '\u202a' and <= '\u202e' or
            >= '\u2066' and <= '\u2069';
}
