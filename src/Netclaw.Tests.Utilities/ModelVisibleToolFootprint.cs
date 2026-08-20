// -----------------------------------------------------------------------
// <copyright file="ModelVisibleToolFootprint.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Netclaw.Tests.Utilities;

/// <summary>
/// Aggregate size of the tool definitions sent to a model.
/// </summary>
public readonly record struct ModelVisibleToolFootprint(int Count, int SerializedDefinitionBytes);

/// <summary>
/// Measures model-visible tool definitions without returning or logging their content.
/// </summary>
public static class ModelVisibleToolFootprintCalculator
{
    /// <summary>
    /// Serializes each function name, description, and input schema as one compact JSON array.
    /// The returned value contains only the function count and UTF-8 byte count.
    /// </summary>
    public static ModelVisibleToolFootprint Measure(IEnumerable<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        var count = 0;

        writer.WriteStartArray();
        foreach (var tool in tools)
        {
            if (tool is not AIFunctionDeclaration function)
                throw new InvalidOperationException("Model-visible tool footprint requires function tools.");
            if (function.JsonSchema.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("Model-visible function schema must be defined.");

            writer.WriteStartObject();
            writer.WriteString("name", function.Name);
            writer.WriteString("description", function.Description ?? string.Empty);
            writer.WritePropertyName("inputSchema");
            function.JsonSchema.WriteTo(writer);
            writer.WriteEndObject();
            count++;
        }

        writer.WriteEndArray();
        writer.Flush();
        return new ModelVisibleToolFootprint(count, buffer.WrittenCount);
    }
}
