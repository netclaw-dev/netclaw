// -----------------------------------------------------------------------
// <copyright file="AttachmentStagingCleanup.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;

namespace Netclaw.Channels;

public static class AttachmentStagingCleanup
{
    public static void TryDelete(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clean up staged attachment file {Path}", path);
        }
    }
}
