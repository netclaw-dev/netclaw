// -----------------------------------------------------------------------
// <copyright file="SecretsJsonUpdater.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Json;

namespace Netclaw.Cli.Secrets;

internal static class SecretsJsonUpdater
{
    private static readonly char[] KeyPathDelimiters = ['.', ':'];

    public static string[] ParseKeyPath(string keyPath)
    {
        var segments = keyPath.Split(KeyPathDelimiters, StringSplitOptions.None)
            .Select(segment => segment.Trim())
            .ToArray();

        if (segments.Length == 0 || segments.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Secret key must be a non-empty dotted or colon-delimited path.");

        return segments;
    }

    public static void UpsertValue(JsonObject root, string[] segments, object value)
    {
        var node = JsonSerializer.SerializeToNode(value, JsonDefaults.ConfigFile);
        UpsertNode(root, segments, node);
    }

    public static void UpsertNode(JsonObject root, string[] segments, JsonNode? value)
    {
        RemoveCollisionsForSubtree(root, segments, value);

        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (current[segment] is JsonObject child)
            {
                current = child;
                continue;
            }

            var newChild = new JsonObject();
            current[segment] = newChild;
            current = newChild;
        }

        current[segments[^1]] = value?.DeepClone();
    }

    public static void MergeObject(JsonObject root, string[] segments, JsonObject incoming)
    {
        RemoveCollisionsForSubtree(root, segments, incoming);

        var target = EnsureObject(root, segments);
        MergeObjectInto(target, incoming);
    }

    private static JsonObject EnsureObject(JsonObject root, string[] segments)
    {
        var current = root;
        foreach (var segment in segments)
        {
            if (current[segment] is JsonObject child)
            {
                current = child;
                continue;
            }

            var newChild = new JsonObject();
            current[segment] = newChild;
            current = newChild;
        }

        return current;
    }

    private static void MergeObjectInto(JsonObject target, JsonObject incoming)
    {
        foreach (var (key, value) in incoming)
        {
            if (value is JsonObject incomingObject && target[key] is JsonObject targetObject)
            {
                MergeObjectInto(targetObject, incomingObject);
                continue;
            }

            target[key] = value?.DeepClone();
        }
    }

    private static void RemoveCollisionsForSubtree(JsonObject root, IReadOnlyList<string> prefix, JsonNode? value)
    {
        if (value is JsonObject obj)
        {
            foreach (var (key, child) in obj)
            {
                var childPath = prefix.Append(key).ToArray();
                RemoveCollisionsForSubtree(root, childPath, child);
            }

            return;
        }

        RemoveLiteralCollisionKeys(root, prefix);
    }

    private static void RemoveLiteralCollisionKeys(JsonObject root, IReadOnlyList<string> segments)
    {
        RemoveLiteralCollisionKeys(root, segments, offset: 0);
    }

    private static void RemoveLiteralCollisionKeys(JsonObject current, IReadOnlyList<string> segments, int offset)
    {
        for (var end = offset + 2; end <= segments.Count; end++)
        {
            current.Remove(string.Join(':', segments.Skip(offset).Take(end - offset)));
        }

        if (offset < segments.Count - 1 && current[segments[offset]] is JsonObject child)
            RemoveLiteralCollisionKeys(child, segments, offset + 1);
    }
}
