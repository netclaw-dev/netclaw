// -----------------------------------------------------------------------
// <copyright file="ToolCallLogFormatter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// Makes the redacted and bounded text of a tool call for a log line that can
/// leave the host (OTLP export). Issue #2589.
/// </summary>
public static class ToolCallLogFormatter
{
    /// <summary>The maximum length of the arguments text, before the "..." suffix.</summary>
    public const int MaxArgumentsChars = 4096;

    /// <summary>A string value longer than this becomes a length and hash summary.</summary>
    public const int MaxInlineStringChars = 256;

    /// <summary>The maximum length of the rationale text, before the "..." suffix.</summary>
    public const int MaxRationaleChars = 500;

    // STUB (issue #2589): the test author added this signature so that the tests
    // compile. The implementer replaces the body.
    public static string FormatArguments(IDictionary<string, object?>? arguments) => string.Empty;

    // STUB (issue #2589): the test author added this signature so that the tests
    // compile. The implementer replaces the body.
    public static string FormatRationale(string? rationale) => string.Empty;
}
