// -----------------------------------------------------------------------
// <copyright file="MimeType.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// A MIME type (content type) value object.
/// Defaults to "application/octet-stream" for unknown or empty types.
/// NO implicit string conversion — use <see cref="Value"/> for explicit access.
/// </summary>
public readonly record struct MimeType
{
    public const string DefaultValue = "application/octet-stream";

    public string Value { get; }

    public MimeType(string? mimeType)
    {
        Value = string.IsNullOrWhiteSpace(mimeType) ? DefaultValue : mimeType.Trim();
    }

    public MimeType() : this(DefaultValue)
    {
    }

    public static MimeType Default => new(DefaultValue);

    public bool IsImage => Value.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public bool IsText => Value.StartsWith("text/", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Value;
}
