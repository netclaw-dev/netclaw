// -----------------------------------------------------------------------
// <copyright file="AttachmentDownloadResult.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels;

/// <summary>
/// Result of a streamed attachment download. The file resides on disk
/// at <see cref="FilePath"/> and is ready for content scanning and
/// inbox finalization.
/// </summary>
public sealed record AttachmentDownloadResult(string FilePath, long BytesWritten);
