// -----------------------------------------------------------------------
// <copyright file="JsonReadTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

[NetclawTool(ToolName,
    "Read selected values from an authorized JSON file using bounded RFC 6901 JSON Pointers without shell or executable queries.",
    Grant = "file")]
public sealed partial class JsonReadTool : NetclawTool<JsonReadTool.Params>
{
    public const string ToolName = "json_read";
    internal const int MaximumPointerCount = 32;
    internal const int MaximumInputBytes = 4 * 1024 * 1024;
    internal const int MaximumOutputChars = 256_000;
    private const int DefaultInputBytes = 1024 * 1024;
    private const int DefaultOutputChars = 64_000;

    private readonly ToolPathPolicy _pathPolicy;
    private readonly ScopedFileAccessPolicy _fileAccessPolicy;

    public record Params(
        [property: Description("JSON file path. Relative paths use the current project, then session scratch.")] string Path,
        [property: Description("RFC 6901 JSON Pointers to project, for example /status or /items/0/name.")] string[] Pointers,
        [property: Description("Maximum input bytes parsed (default 1048576, maximum 4194304).")] int? MaxInputBytes = null,
        [property: Description("Maximum characters returned (default 64000, maximum 256000).")] int? MaxOutputChars = null);

    public JsonReadTool(ToolConfig config, NetclawPaths paths, ToolPathPolicy pathPolicy)
    {
        _pathPolicy = pathPolicy;
        _fileAccessPolicy = new ScopedFileAccessPolicy(config, paths);
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (!TryValidatePointers(args.Pointers, out var pointers, out var pointerError))
            return context.InvalidInput(pointerError);

        if (!WorkspaceFileToolSupport.TryResolveBound(
                args.MaxInputBytes,
                DefaultInputBytes,
                MaximumInputBytes,
                nameof(args.MaxInputBytes),
                out var inputLimit,
                out var inputError))
        {
            return context.InvalidInput(inputError);
        }

        if (!WorkspaceFileToolSupport.TryResolveBound(
                args.MaxOutputChars,
                DefaultOutputChars,
                MaximumOutputChars,
                nameof(args.MaxOutputChars),
                out var outputLimit,
                out var outputError))
        {
            return context.InvalidInput(outputError);
        }

        if (!_fileAccessPolicy.TryResolveReadPath(
                args.Path,
                context,
                out var path,
                out var accessError,
                out var resolutionFailure))
        {
            return context.PathResolutionFailure(accessError, resolutionFailure);
        }

        if (_pathPolicy.IsReadDenied(path))
            return context.AccessDenied(FileToolErrors.CredentialReadDenied(path));

        if (!File.Exists(path))
            return context.NotFound($"Error: File not found: {path}");

        try
        {
            if (new FileInfo(path).Length > inputLimit)
                return context.InvalidInput($"Error: JSON input exceeds the {inputLimit}-byte limit.");

            var (bytes, truncated) = await ReadBoundedBytesAsync(path, inputLimit, ct);
            if (truncated)
                return context.InvalidInput($"Error: JSON input exceeds the {inputLimit}-byte limit.");

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });

            var selected = new List<(string Pointer, JsonElement Value)>(pointers.Count);
            foreach (var pointer in pointers)
            {
                if (!TryResolvePointer(document.RootElement, pointer.Tokens, out var value))
                    return context.NotFound($"Error: JSON pointer not found: {pointer.Source}");
                selected.Add((pointer.Source, value));
            }

            var output = WriteProjection(selected);
            if (output.Length > outputLimit)
            {
                return context.InvalidInput(
                    $"Error: projected JSON exceeds the {outputLimit}-character output limit. Request fewer pointers.");
            }

            return context.SuccessFile(output, path, ToolFileActivityKind.Read);
        }
        catch (JsonException ex)
        {
            return context.InvalidInput($"Error: Invalid JSON: {ex.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            return context.AccessDenied($"Error: Permission denied: {path}");
        }
        catch (FileNotFoundException)
        {
            return context.NotFound($"Error: File not found: {path}");
        }
        catch (DirectoryNotFoundException)
        {
            return context.NotFound($"Error: File not found: {path}");
        }
        catch (IOException ex)
        {
            return context.TransientFailure($"Error reading JSON: {ex.Message}");
        }
    }

    private static bool TryValidatePointers(
        string[]? authoredPointers,
        out IReadOnlyList<JsonPointer> pointers,
        out string error)
    {
        pointers = [];
        if (authoredPointers is not { Length: > 0 and <= MaximumPointerCount })
        {
            error = $"Error: 'Pointers' must contain between 1 and {MaximumPointerCount} entries.";
            return false;
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<JsonPointer>(authoredPointers.Length);
        foreach (var source in authoredPointers)
        {
            if (source is null || source.Any(char.IsControl) || !unique.Add(source))
            {
                error = $"Error: duplicate or invalid JSON pointer: {source ?? "<null>"}";
                return false;
            }

            if (!TryParsePointer(source, out var pointer))
            {
                error = $"Error: invalid RFC 6901 JSON pointer: {source}";
                return false;
            }

            validated.Add(pointer);
        }

        pointers = validated;
        error = string.Empty;
        return true;
    }

    private static bool TryParsePointer(string source, out JsonPointer pointer)
    {
        if (source.Length == 0)
        {
            pointer = new JsonPointer(source, []);
            return true;
        }

        if (source[0] != '/')
        {
            pointer = default;
            return false;
        }

        var tokens = new List<string>();
        foreach (var encodedToken in source[1..].Split('/'))
        {
            var token = new StringBuilder(encodedToken.Length);
            for (var index = 0; index < encodedToken.Length; index++)
            {
                if (encodedToken[index] != '~')
                {
                    token.Append(encodedToken[index]);
                    continue;
                }

                if (++index >= encodedToken.Length || encodedToken[index] is not ('0' or '1'))
                {
                    pointer = default;
                    return false;
                }

                token.Append(encodedToken[index] == '0' ? '~' : '/');
            }
            tokens.Add(token.ToString());
        }

        pointer = new JsonPointer(source, tokens);
        return true;
    }

    private static bool TryResolvePointer(
        JsonElement root,
        IReadOnlyList<string> tokens,
        out JsonElement value)
    {
        value = root;
        foreach (var token in tokens)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(token, out value))
                    return false;
                continue;
            }

            if (value.ValueKind != JsonValueKind.Array
                || token.Length == 0
                || token.Length > 1 && token[0] == '0'
                || !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 0
                || index >= value.GetArrayLength())
            {
                return false;
            }

            value = value[index];
        }

        return true;
    }

    private static string WriteProjection(IReadOnlyList<(string Pointer, JsonElement Value)> selected)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var (pointer, value) in selected)
            {
                writer.WritePropertyName(pointer);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static async Task<(ReadOnlyMemory<byte> Bytes, bool Truncated)> ReadBoundedBytesAsync(
        string path,
        int maxBytes,
        CancellationToken ct)
    {
        var buffer = new byte[maxBytes + 1];
        var count = 0;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, buffer.Length - count), ct);
            if (read == 0)
                break;
            count += read;
        }

        var truncated = count > maxBytes;
        return (buffer.AsMemory(0, Math.Min(count, maxBytes)), truncated);
    }

    private readonly record struct JsonPointer(string Source, IReadOnlyList<string> Tokens);
}
