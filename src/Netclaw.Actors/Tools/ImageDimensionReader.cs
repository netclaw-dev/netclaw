// -----------------------------------------------------------------------
// <copyright file="ImageDimensionReader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Binary;
using Netclaw.Media;

namespace Netclaw.Actors.Tools;

internal static class ImageDimensionReader
{
    public static ImageDimensionStatus Read(
        MimeType mimeType,
        ReadOnlySpan<byte> header,
        out ImageDimensions dimensions)
    {
        var success = mimeType.Value switch
        {
            MimeTypeCatalog.ImagePng => TryReadPng(header, out dimensions),
            MimeTypeCatalog.ImageJpeg => TryReadJpeg(header, out dimensions),
            MimeTypeCatalog.ImageGif => TryReadGif(header, out dimensions),
            MimeTypeCatalog.ImageWebp => TryReadWebp(header, out dimensions),
            _ => Unsupported(out dimensions)
        };

        if (success is null)
            return ImageDimensionStatus.NotSupported;

        return success.Value && dimensions is { Width: > 0, Height: > 0 }
            ? ImageDimensionStatus.Valid
            : ImageDimensionStatus.Invalid;
    }

    private static bool? Unsupported(out ImageDimensions dimensions)
    {
        dimensions = default;
        return null;
    }

    private static bool TryReadPng(ReadOnlySpan<byte> header, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (header.Length < 24
            || !header.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
        if (width > int.MaxValue || height > int.MaxValue)
            return false;

        dimensions = new ImageDimensions((int)width, (int)height);
        return true;
    }

    private static bool TryReadGif(ReadOnlySpan<byte> header, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (header.Length < 10
            || !(header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)))
        {
            return false;
        }

        dimensions = new ImageDimensions(
            BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8, 2)));
        return true;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> header, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (header.Length < 4 || header[0] != 0xFF || header[1] != 0xD8)
            return false;

        var offset = 2;
        while (offset + 1 < header.Length)
        {
            if (header[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            while (offset < header.Length && header[offset] == 0xFF)
                offset++;
            if (offset >= header.Length)
                return false;

            var marker = header[offset++];
            if (marker is 0xD9 or 0xDA)
                return false;
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
                continue;
            if (offset + 2 > header.Length)
                return false;

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > header.Length)
                return false;

            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 7)
                    return false;
                dimensions = new ImageDimensions(
                    BinaryPrimitives.ReadUInt16BigEndian(header.Slice(offset + 5, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(header.Slice(offset + 3, 2)));
                return true;
            }

            offset += segmentLength;
        }

        return false;
    }

    private static bool IsStartOfFrame(byte marker)
        => marker is 0xC0 or 0xC1 or 0xC2 or 0xC3
            or 0xC5 or 0xC6 or 0xC7
            or 0xC9 or 0xCA or 0xCB
            or 0xCD or 0xCE or 0xCF;

    private static bool TryReadWebp(ReadOnlySpan<byte> header, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (header.Length < 21
            || !header[..4].SequenceEqual("RIFF"u8)
            || !header.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return false;
        }

        if (header.Slice(12, 4).SequenceEqual("VP8X"u8))
        {
            if (header.Length < 30)
                return false;
            dimensions = new ImageDimensions(
                1 + ReadUInt24LittleEndian(header.Slice(24, 3)),
                1 + ReadUInt24LittleEndian(header.Slice(27, 3)));
            return true;
        }

        if (header.Slice(12, 4).SequenceEqual("VP8L"u8))
        {
            if (header.Length < 25 || header[20] != 0x2F)
                return false;
            dimensions = new ImageDimensions(
                1 + header[21] + ((header[22] & 0x3F) << 8),
                1 + (header[22] >> 6) + (header[23] << 2) + ((header[24] & 0x0F) << 10));
            return true;
        }

        if (header.Slice(12, 4).SequenceEqual("VP8 "u8))
        {
            if (header.Length < 30 || !header.Slice(23, 3).SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
                return false;
            dimensions = new ImageDimensions(
                BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(26, 2)) & 0x3FFF,
                BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2)) & 0x3FFF);
            return true;
        }

        return false;
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> value)
        => value[0] | value[1] << 8 | value[2] << 16;
}

internal enum ImageDimensionStatus
{
    NotSupported,
    Valid,
    Invalid
}

internal readonly record struct ImageDimensions(int Width, int Height);
