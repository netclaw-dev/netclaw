using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Memory;

namespace Netclaw.Actors.Sessions;

internal static class SessionSidecarRunner
{
    public static async Task<T?> RunJsonAsync<T>(
        IChatClient client,
        string systemPrompt,
        string userPrompt,
        TimeSpan timeout,
        Action<string> logWarning)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var messages = new List<ChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System, systemPrompt),
                new(Microsoft.Extensions.AI.ChatRole.User, userPrompt)
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: cts.Token);
            var text = response.Messages[^1].Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                logWarning("Sidecar returned empty response");
                return default;
            }

            var normalized = NormalizeJsonPayload<T>(text);

            if (typeof(T) == typeof(IReadOnlyList<MemoryProposal>) || typeof(T) == typeof(List<MemoryProposal>))
                normalized = NormalizeMemoryProposalArray(normalized);

            return JsonSerializer.Deserialize<T>(normalized, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            logWarning($"Sidecar failed: {ex.Message}");
            return default;
        }
    }

    private static string NormalizeJsonPayload<T>(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n', StringComparison.Ordinal);
            if (firstNewline >= 0)
            {
                text = text[(firstNewline + 1)..];
                var fence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (fence >= 0)
                    text = text[..fence];
            }
        }

        text = text.Trim();

        var node = TryParseJsonNode(text);

        if (typeof(T) == typeof(IReadOnlyList<MemoryProposal>) || typeof(T) == typeof(List<MemoryProposal>))
        {
            var proposals = ExtractProposalArray(node);
            if (proposals is not null)
                return proposals.ToJsonString();
        }

        if (typeof(T) == typeof(RecallQueryPlan))
        {
            var plan = ExtractRecallPlanObject(node);
            if (plan is not null)
                return NormalizeRecallPlanObject(plan).ToJsonString();
        }

        return text;
    }

    private static string NormalizeMemoryProposalArray(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is not JsonArray arr)
            return json;

        foreach (var item in arr.OfType<JsonObject>())
        {
            NormalizeProposalObject(item);

            if (item["operation"] is JsonValue operationValue && operationValue.TryGetValue<string>(out var operation))
                item["operation"] = NormalizeOperation(operation);

            if (item["memoryClass"] is JsonValue memoryClassValue && memoryClassValue.TryGetValue<string>(out var memoryClass))
                item["memoryClass"] = NormalizeMemoryClass(memoryClass);
        }

        return arr.ToJsonString();
    }

    private static string NormalizeOperation(string raw)
    {
        var upsert = MemoryProposalOperation.UpsertDocument.ToWireValue();
        var append = MemoryProposalOperation.AppendRecord.ToWireValue();
        var ignore = MemoryProposalOperation.Ignore.ToWireValue();

        var value = NormalizeToken(raw);
        return value switch
        {
            "upsertdocument" => upsert,
            "upsert_document" or "store_document" or "store" or "save" or "remember" => upsert,
            "appendrecord" => append,
            "append_record" or "append" or "record" or "evidence_record" => append,
            "ignore" or "skip" or "none" => ignore,
            _ => raw
        };
    }

    private static string NormalizeMemoryClass(string raw)
    {
        var durableFact = MemoryClass.DurableFact.ToWireValue();
        var evidence = MemoryClass.Evidence.ToWireValue();
        var trace = MemoryClass.Trace.ToWireValue();

        var value = NormalizeToken(raw);
        return value switch
        {
            "durablefact" => durableFact,
            "durable_fact" or "durable" or "fact" or "preference" => durableFact,
            "evidence" or "research" or "finding" => evidence,
            "trace" or "breadcrumb" or "diagnostic" => trace,
            _ => raw
        };
    }

    private static JsonNode? TryParseJsonNode(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch
        {
            var candidate = ExtractJsonCandidate(text);
            if (candidate is null)
                return null;

            return JsonNode.Parse(candidate);
        }
    }

    private static string? ExtractJsonCandidate(string text)
    {
        var objectStart = text.IndexOf("{", StringComparison.Ordinal);
        var arrayStart = text.IndexOf("[", StringComparison.Ordinal);

        var start = objectStart switch
        {
            -1 => arrayStart,
            _ when arrayStart == -1 => objectStart,
            _ => Math.Min(objectStart, arrayStart)
        };

        if (start < 0)
            return null;

        var objectEnd = text.LastIndexOf("}", StringComparison.Ordinal);
        var arrayEnd = text.LastIndexOf("]", StringComparison.Ordinal);
        var end = Math.Max(objectEnd, arrayEnd);

        if (end < start)
            return null;

        return text[start..(end + 1)];
    }

    private static JsonArray? ExtractProposalArray(JsonNode? node)
    {
        if (node is JsonArray arr)
            return arr;

        if (node is not JsonObject obj)
            return null;

        foreach (var key in new[] { "proposals", "items", "memories", "results", "candidates", "data" })
        {
            if (TryGetProperty(obj, key) is JsonArray directArray)
                return directArray;

            if (TryGetProperty(obj, key) is JsonObject nestedObject)
            {
                var nestedArray = ExtractProposalArray(nestedObject);
                if (nestedArray is not null)
                    return nestedArray;
            }
        }

        if (obj.Count == 1 && obj.FirstOrDefault().Value is JsonNode onlyChild)
            return ExtractProposalArray(onlyChild);

        return null;
    }

    private static JsonObject? ExtractRecallPlanObject(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "plan", "queryPlan", "recallPlan", "query_plan", "recall_plan", "data" })
            {
                if (TryGetProperty(obj, key) is JsonObject nested)
                    return nested;
            }

            return obj;
        }

        return null;
    }

    private static JsonObject NormalizeRecallPlanObject(JsonObject obj)
    {
        RemapProperty(obj, "mode", "mode");
        RemapProperty(obj, "intent", "intent");
        RemapProperty(obj, "entities", "entities");
        RemapProperty(obj, "constraints", "constraints");
        RemapProperty(obj, "searchTerms", "searchTerms", "search_terms", "terms");
        RemapProperty(obj, "memoryClasses", "memoryClasses", "memory_classes", "classes");
        RemapProperty(obj, "maxResults", "maxResults", "max_results");
        RemapProperty(obj, "allowExpiredEvidence", "allowExpiredEvidence", "allow_expired_evidence");
        return obj;
    }

    private static void NormalizeProposalObject(JsonObject item)
    {
        RemapProperty(item, "operation", "operation", "op", "action");
        RemapProperty(item, "memoryClass", "memoryClass", "memory_class", "class", "memoryType", "memory_type", "type");
        RemapProperty(item, "subjectKind", "subjectKind", "subject_kind", "subjectType", "subject_type");
        RemapProperty(item, "subjectValue", "subjectValue", "subject_value");
        RemapProperty(item, "targetSurface", "targetSurface", "target_surface");
        RemapProperty(item, "recallMode", "recallMode", "recall_mode");
        RemapProperty(item, "freshUntilMs", "freshUntilMs", "fresh_until_ms");
        RemapProperty(item, "expiresAtMs", "expiresAtMs", "expires_at_ms");

        if (TryGetProperty(item, "anchor") is JsonObject anchor)
        {
            RemapProperty(anchor, "canonicalName", "canonicalName", "canonical_name", "name");
            RemapProperty(anchor, "anchorType", "anchorType", "anchor_type", "type", "kind");
        }

        if (TryGetProperty(item, "relations") is JsonArray relations)
        {
            foreach (var relationNode in relations.OfType<JsonObject>())
            {
                RemapProperty(relationNode, "relationType", "relationType", "relation_type", "type");

                if (TryGetProperty(relationNode, "targetAnchor") is JsonObject targetAnchor)
                {
                    RemapProperty(targetAnchor, "canonicalName", "canonicalName", "canonical_name", "name");
                    RemapProperty(targetAnchor, "anchorType", "anchorType", "anchor_type", "type", "kind");
                }
            }
        }
    }

    private static void RemapProperty(JsonObject obj, string canonicalName, params string[] aliases)
    {
        if (obj.ContainsKey(canonicalName))
            return;

        foreach (var alias in aliases)
        {
            if (!obj.TryGetPropertyValue(alias, out var value) || value is null)
                continue;

            obj[canonicalName] = value.DeepClone();
            return;
        }
    }

    private static JsonNode? TryGetProperty(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out var exact))
            return exact;

        foreach (var property in obj)
        {
            if (string.Equals(NormalizeToken(property.Key), NormalizeToken(name), StringComparison.Ordinal))
                return property.Value;
        }

        return null;
    }

    private static string NormalizeToken(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        var normalized = trimmed
            .Replace("-", "_", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal);

        for (var i = 1; i < normalized.Length; i++)
        {
            if (!char.IsUpper(normalized[i]) || normalized[i - 1] == '_')
                continue;

            normalized = normalized.Insert(i, "_");
            i++;
        }

        return normalized.ToLowerInvariant();
    }
}
