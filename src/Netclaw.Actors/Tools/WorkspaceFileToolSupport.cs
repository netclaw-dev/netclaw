// -----------------------------------------------------------------------
// <copyright file="WorkspaceFileToolSupport.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;

namespace Netclaw.Actors.Tools;

internal static class WorkspaceFileToolSupport
{
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryResolveBound(
        int? requested,
        int defaultValue,
        int hardMaximum,
        string parameterName,
        out int value,
        out string error)
    {
        value = requested ?? defaultValue;
        if (value is > 0 && value <= hardMaximum)
        {
            error = string.Empty;
            return true;
        }

        error = $"Error: '{parameterName}' must be between 1 and {hardMaximum}.";
        return false;
    }

    public static async Task<BoundedText> ReadUtf8CharsAsync(
        string path,
        int maxChars,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            path,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: false);
        var buffer = new char[Math.Min(4096, Math.Max(1, maxChars))];
        var content = new StringBuilder(maxChars);

        while (content.Length < maxChars)
        {
            var count = Math.Min(buffer.Length, maxChars - content.Length);
            var read = await reader.ReadAsync(buffer.AsMemory(0, count), cancellationToken);
            if (read == 0)
                return new BoundedText(TrimUtf8Preamble(content.ToString()), Truncated: false);

            content.Append(buffer, 0, read);
        }

        return new BoundedText(TrimUtf8Preamble(content.ToString()), reader.Peek() >= 0);
    }

    public static async Task<BoundedText> ReadUtf8BytesAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[maxBytes];
        var total = 0;
        var truncated = false;
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete,
                         bufferSize: 4096,
                         useAsync: true))
        {
            while (total < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(total, bytes.Length - total), cancellationToken);
                if (read == 0)
                    break;
                total += read;
            }

            truncated = stream.Length > maxBytes;
        }

        var decoder = StrictUtf8.GetDecoder();
        var chars = new char[StrictUtf8.GetMaxCharCount(total)];
        decoder.Convert(
            bytes,
            0,
            total,
            chars,
            0,
            chars.Length,
            flush: !truncated,
            out _,
            out var charsUsed,
            out _);
        var content = new string(chars, 0, charsUsed);
        if (content.Length > 0 && content[0] == '\uFEFF')
            content = content[1..];

        return new BoundedText(content, truncated);
    }

    internal readonly record struct BoundedText(string Content, bool Truncated);

    private static string TrimUtf8Preamble(string content) =>
        content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;
}
