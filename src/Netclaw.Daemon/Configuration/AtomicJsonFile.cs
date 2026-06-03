// -----------------------------------------------------------------------
// <copyright file="AtomicJsonFile.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Reads and atomically writes a JSON document that lives on disk as a single
/// root object. Shared by the config-persistence services that edit one
/// sub-tree of <c>netclaw.json</c> (or <c>secrets.json</c>) while preserving
/// every other key verbatim.
/// </summary>
internal static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Loads the file as a <see cref="JsonObject"/>. A missing, empty, or
    /// non-object document yields a fresh empty object so callers can write
    /// into it unconditionally.
    /// </summary>
    public static JsonObject Load(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();

        var node = JsonNode.Parse(text);
        return node as JsonObject ?? new JsonObject();
    }

    /// <summary>
    /// Serializes <paramref name="root"/> and replaces the file atomically:
    /// the content is written to a sibling temp file, then renamed into place.
    /// </summary>
    public static void Write(string path, JsonObject root)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = Serialize(root);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, json);

            // File.Move(overwrite) is an atomic rename on the same volume on
            // both Windows and Unix, and also handles the create-new case —
            // unlike File.Replace, which requires the destination to already
            // exist and has platform-specific edge cases on non-Windows.
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Never leave a partial temp file behind if the write or rename throws.
            if (File.Exists(temp))
                File.Delete(temp);
            throw;
        }
    }

    /// <summary>
    /// Serializes a root object with the shared formatting. Use this when the
    /// resulting text is handed to a different writer (e.g. an encrypting
    /// secrets writer) rather than written here.
    /// </summary>
    public static string Serialize(JsonObject root) => root.ToJsonString(WriteOptions);
}
