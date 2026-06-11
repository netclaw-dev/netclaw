// -----------------------------------------------------------------------
// <copyright file="MimeType.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Media;

/// <summary>
/// Canonical MIME type value object. No implicit string conversion is provided;
/// use <see cref="Value"/> when crossing a primitive boundary.
/// </summary>
public readonly record struct MimeType
{
    public const string DefaultValue = "application/octet-stream";

    private readonly string? _value;

    // Computed getter so default(MimeType) — which bypasses the constructor —
    // still yields the canonical default instead of a null that would throw
    // inside the catalog's FrozenDictionary lookups.
    public string Value => _value ?? DefaultValue;

    public MimeType(string? mimeType)
    {
        _value = MimeTypeCatalog.Normalize(mimeType);
    }

    public MimeType() : this(DefaultValue)
    {
    }

    public static MimeType Default => new(DefaultValue);

    public override string ToString() => Value;
}

/// <summary>
/// MIME metadata supplied by an untrusted transport or caller.
/// </summary>
public readonly record struct DeclaredMimeType
{
    private readonly string? _value;

    public string Value => _value ?? MimeType.DefaultValue;

    public DeclaredMimeType(string? mimeType)
    {
        _value = new MimeType(mimeType).Value;
    }

    public DeclaredMimeType() : this(MimeType.DefaultValue)
    {
    }

    public override string ToString() => Value;
}

/// <summary>
/// MIME type returned by content scanning after bytes and filename validate.
/// </summary>
public readonly record struct VerifiedMimeType
{
    public MimeType MimeType { get; }

    public VerifiedMimeType(MimeType mimeType)
    {
        MimeType = mimeType;
    }

    public VerifiedMimeType(string? mimeType) : this(new MimeType(mimeType))
    {
    }

    public string Value => MimeType.Value;

    public override string ToString() => Value;
}

public readonly record struct FileExtension
{
    private readonly string? _value;

    public string Value => _value ?? string.Empty;

    public FileExtension(string? extension)
    {
        _value = Normalize(extension);
    }

    public static FileExtension FromPath(string path) => new(Path.GetExtension(path));

    public static FileExtension Empty => new(null);

    public bool IsEmpty => Value.Length == 0;

    public override string ToString() => Value;

    private static string Normalize(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        var trimmed = extension.Trim();
        return trimmed.StartsWith(".", StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : "." + trimmed.ToLowerInvariant();
    }
}
