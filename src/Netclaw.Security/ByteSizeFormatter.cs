// -----------------------------------------------------------------------
// <copyright file="ByteSizeFormatter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

public static class ByteSizeFormatter
{
    public static string Format(long size)
    {
        const long Mib = 1024 * 1024;
        const long Kib = 1024;
        if (size >= Mib)
            return $"{size / (double)Mib:F1} MiB";
        if (size >= Kib)
            return $"{size / (double)Kib:F1} KiB";
        return $"{size} bytes";
    }
}
