// -----------------------------------------------------------------------
// <copyright file="ApprovalStoreCodec.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;

namespace Netclaw.Configuration;

internal static class ApprovalStoreCodec
{
    private static readonly HashSet<string> RootMembers = ["version", "audiences"];
    private static readonly HashSet<string> Version2EntryMembers =
        ["verb", "directory", "createdAt"];

    internal static int ReadVersion(JsonElement root)
    {
        var members = ReadRoot(root);
        var version = members["version"];
        if (version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var value))
        {
            throw Invalid("The approval store version must be an integer.");
        }

        return value;
    }

    internal static ToolApprovalData ReadVersion3(JsonElement root, string shellToolName)
    {
        var members = ReadRoot(root);
        RequireVersion(members["version"], ToolApprovalStore.CurrentSchemaVersion);
        var data = new ToolApprovalData();
        ReadAudienceMap(
            members["audiences"],
            (entry, toolName) => ReadVersion3Entry(entry, toolName, shellToolName),
            data);
        return data;
    }

    private static ApprovalEntry ReadVersion3Entry(
        JsonElement element,
        string toolName,
        string shellToolName)
    {
        var entry = ApprovalEntryWireCodec.ReadVersion3(element);
        var isShellTool = string.Equals(toolName, shellToolName, StringComparison.Ordinal);
        if (isShellTool != (entry.Shell is not null))
        {
            throw Invalid("The approval entry form does not match its tool key.");
        }

        return entry;
    }

    internal static ToolApprovalData ConvertVersion2(
        JsonElement root,
        ApprovalStoreMigrationContext? context,
        out int omittedEntries)
    {
        var members = ReadRoot(root);
        RequireVersion(members["version"], 2);
        var data = new ToolApprovalData();
        var omitted = new[] { 0 };
        ReadAudienceMap(
            members["audiences"],
            (entry, toolName) => ConvertVersion2Entry(
                entry,
                toolName,
                context,
                omitted),
            data);
        omittedEntries = omitted[0];
        return data;
    }

    internal static string Serialize(ToolApprovalData data)
    {
        data.Version = ToolApprovalStore.CurrentSchemaVersion;
        var audiences = new Dictionary<string, Dictionary<string, List<ApprovalEntryWire>>>(StringComparer.Ordinal);
        foreach (var (audienceName, tools) in data.Audiences)
        {
            var wireTools = new Dictionary<string, List<ApprovalEntryWire>>(StringComparer.Ordinal);
            foreach (var (toolName, entries) in tools)
            {
                wireTools.Add(
                    toolName,
                    entries.Select(ApprovalEntryWireCodec.WriteVersion3).ToList());
            }

            audiences.Add(audienceName, wireTools);
        }

        var wire = new ApprovalStoreWire
        {
            Version = ToolApprovalStore.CurrentSchemaVersion,
            Audiences = audiences,
        };
        return JsonSerializer.Serialize(wire, ApprovalStoreJsonContext.Default.ApprovalStoreWire);
    }

    private static Dictionary<string, JsonElement> ReadRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("The approval store root must be an object.");
        }

        Dictionary<string, JsonElement> members;
        try
        {
            members = ApprovalEntryValidation.ReadUniqueMembers(root, RootMembers);
        }
        catch (JsonException ex)
        {
            throw Invalid("The approval store root is invalid.", ex);
        }

        if (!members.ContainsKey("version") || !members.ContainsKey("audiences"))
        {
            throw Invalid("The approval store must contain version and audiences.");
        }

        return members;
    }

    private static void ReadAudienceMap(
        JsonElement audiencesElement,
        Func<JsonElement, string, ApprovalEntry?> readEntry,
        ToolApprovalData data)
    {
        if (audiencesElement.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("The audiences member must be an object.");
        }

        var audienceNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var audienceProperty in audiencesElement.EnumerateObject())
        {
            if (!audienceNames.Add(audienceProperty.Name) ||
                !SecurityPolicyDefaults.TryParseAudience(audienceProperty.Name, out _))
            {
                throw Invalid("The approval store has a duplicate or unknown audience.");
            }

            if (audienceProperty.Value.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("An audience value must be an object.");
            }

            var tools = new Dictionary<string, List<ApprovalEntry>>(StringComparer.Ordinal);
            var toolNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var toolProperty in audienceProperty.Value.EnumerateObject())
            {
                if (!toolNames.Add(toolProperty.Name))
                {
                    throw Invalid("The approval store has a duplicate tool key.");
                }

                ValidateToolName(toolProperty.Name);
                if (toolProperty.Value.ValueKind != JsonValueKind.Array)
                {
                    throw Invalid("A tool approval value must be an array.");
                }

                var entries = new List<ApprovalEntry>();
                foreach (var entryElement in toolProperty.Value.EnumerateArray())
                {
                    if (entryElement.ValueKind == JsonValueKind.Null)
                    {
                        throw Invalid("An approval entry must not be null.");
                    }

                    try
                    {
                        if (readEntry(entryElement, toolProperty.Name) is { } entry)
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (ApprovalStoreException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is JsonException or ArgumentException)
                    {
                        throw Invalid("An approval entry is invalid.", ex);
                    }
                }

                tools.Add(toolProperty.Name, entries);
            }

            data.Audiences.Add(audienceProperty.Name, tools);
        }
    }

    private static ApprovalEntry? ConvertVersion2Entry(
        JsonElement element,
        string toolName,
        ApprovalStoreMigrationContext? context,
        int[] omittedEntries)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("A version-2 approval entry must be an object.");
        }

        Dictionary<string, JsonElement> members;
        try
        {
            members = ApprovalEntryValidation.ReadUniqueMembers(element, Version2EntryMembers);
        }
        catch (JsonException ex)
        {
            throw Invalid("A version-2 approval entry has an invalid shape.", ex);
        }

        if (!members.TryGetValue("verb", out var verbElement) ||
            verbElement.ValueKind != JsonValueKind.String)
        {
            throw Invalid("A version-2 approval entry must have a string verb.");
        }

        var verb = verbElement.GetString();
        if (verb is null)
        {
            throw Invalid("A version-2 verb must not be null.");
        }

        var directory = ReadVersion2Directory(members, out var directoryRepresentable);
        var createdAt = ReadVersion2Timestamp(members);
        if (!IsRepresentableVersion2Verb(verb) || !directoryRepresentable)
        {
            omittedEntries[0]++;
            return null;
        }

        if (context is not null &&
            string.Equals(toolName, context.ShellToolName, StringComparison.Ordinal))
        {
            return ApprovalEntry.CreateLegacyExact(
                context.NativeShell,
                verb,
                directory,
                createdAt);
        }

        if (context is null &&
            string.Equals(toolName, "shell_execute", StringComparison.Ordinal))
        {
            throw new ApprovalStoreException(
                ApprovalStoreFailure.MigrationFailed,
                "Version-2 shell approvals require a canonical shell for conversion.");
        }

        return new ApprovalEntry(verb)
        {
            Directory = directory,
            CreatedAt = createdAt,
        };
    }

    private static string? ReadVersion2Directory(
        IReadOnlyDictionary<string, JsonElement> members,
        out bool representable)
    {
        representable = true;
        if (!members.TryGetValue("directory", out var directoryElement) ||
            directoryElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (directoryElement.ValueKind != JsonValueKind.String)
        {
            throw Invalid("A version-2 directory must be a string or null.");
        }

        var directory = directoryElement.GetString();
        if (string.IsNullOrEmpty(directory) || !Path.IsPathFullyQualified(directory))
        {
            representable = false;
            return null;
        }

        try
        {
            ApprovalEntryValidation.ValidatePersistedString(
                directory,
                "directory",
                allowWhitespace: true);
            var fullPath = Path.GetFullPath(directory);
            var root = Path.GetPathRoot(fullPath);
            if (string.Equals(fullPath, root, ToolApprovalEntryComparer.Comparison))
            {
                return fullPath;
            }

            var normalized = fullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (normalized.Length == 0 || !Path.IsPathFullyQualified(normalized))
            {
                representable = false;
                return null;
            }

            return normalized;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or NotSupportedException)
        {
            representable = false;
            return null;
        }
    }

    private static DateTimeOffset? ReadVersion2Timestamp(
        IReadOnlyDictionary<string, JsonElement> members)
    {
        if (!members.TryGetValue("createdAt", out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String ||
            !element.TryGetDateTimeOffset(out var value))
        {
            throw Invalid("A version-2 timestamp is invalid.");
        }

        return value;
    }

    private static bool IsRepresentableVersion2Verb(string verb)
    {
        if (verb.Length == 0 || verb != verb.Trim())
        {
            return false;
        }

        try
        {
            ApprovalEntryValidation.ValidatePersistedString(
                verb,
                "verb",
                allowWhitespace: true);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static void ValidateToolName(string toolName)
    {
        if (toolName.Length == 0 || toolName != toolName.Trim())
        {
            throw Invalid("A tool key must be nonempty and canonical.");
        }

        try
        {
            ApprovalEntryValidation.ValidatePersistedString(
                toolName,
                "tool key",
                allowWhitespace: false);
        }
        catch (JsonException ex)
        {
            throw Invalid("A tool key has a prohibited character.", ex);
        }
    }

    private static void RequireVersion(JsonElement element, int expected)
    {
        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var version) || version != expected)
        {
            throw Invalid("The approval store has an unexpected schema version.");
        }
    }

    private static ApprovalStoreException Invalid(string message, Exception? inner = null) =>
        new(ApprovalStoreFailure.InvalidData, message, inner);
}
