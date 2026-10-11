// -----------------------------------------------------------------------
// <copyright file="MediaKind.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Media;

public enum MediaKind
{
    Unknown,
    Text,
    Image,
    Pdf,
    Document,
    Archive,
    Audio,
    Video
}

public enum MediaContentKind
{
    Unknown,
    Text,
    Binary
}

/// <summary>
/// Byte-signature family for a media type. The catalog owns the family; the
/// security scanner owns the byte-matcher for each family. This keeps the
/// MIME → matcher table derived from one list, so the two cannot drift.
/// <see cref="Any"/> is text-like content with no signature at offset 0;
/// <see cref="None"/> means the scanner has no matcher for the type.
/// </summary>
public enum SignatureFamily
{
    None,
    Any,
    Png,
    Jpeg,
    Gif,
    Webp,
    Bmp,
    Tiff,
    Pdf,
    Zip,
    Ole,
    Rtf,
    SevenZip,
    Gzip,
    Bzip2,
    Xz,
    Mp3,
    Ftyp,
    Wav,
    Ogg,
    Ebml,
    Avi
}
