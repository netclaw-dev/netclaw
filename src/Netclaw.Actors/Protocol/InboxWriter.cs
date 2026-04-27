using Netclaw.Security;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Writes inbound attachment bytes into a session's <c>inbox/</c>
/// subdirectory with filesystem-level collision suffixing and atomic
/// file writes. Channel adapters call this from their ingress pipeline
/// after the attachment has been downloaded, content-scanned, and
/// audience-gated. Callers are responsible for ensuring the directory
/// exists (use <see cref="SessionDirectoryHelper.GetOrCreateInboxDirectory"/>).
/// </summary>
public static class InboxWriter
{
    /// <summary>
    /// Maximum number of collision-resolution suffixes attempted before
    /// giving up. A single session with 99 same-named files is already
    /// pathological; higher counts indicate a misconfiguration or
    /// abuse.
    /// </summary>
    public const int MaxCollisionSuffix = 99;

    /// <summary>
    /// Thrown when collision-resolution suffixing exhausts
    /// <see cref="MaxCollisionSuffix"/> attempts and no free filename
    /// remains. Channel adapters SHALL translate this into a loud,
    /// user-visible rejection reply rather than silently dropping the
    /// attachment.
    /// </summary>
    public sealed class CollisionExhaustedException(string baseName)
        : InvalidOperationException(
            $"Exhausted {MaxCollisionSuffix} collision suffixes for filename '{baseName}' in inbox/");

    /// <summary>
    /// Reserves a unique path in <paramref name="inboxDir"/> for the
    /// given sanitized filename. If the filename already exists on
    /// disk, tries <c>foo_1.ext</c>, <c>foo_2.ext</c>, … up to
    /// <see cref="MaxCollisionSuffix"/>. Returns the full path of the
    /// reserved slot. Callers SHALL write to this path atomically via
    /// <see cref="WriteAtomicAsync"/>. Reservation is a best-effort
    /// check against the filesystem at call time; concurrent writers
    /// in the same session would race, but channel binding actors
    /// process inbound messages serially, so collisions here are
    /// cross-turn only.
    /// </summary>
    public static string ReserveUniquePath(string inboxDir, string safeFilename)
    {
        if (string.IsNullOrWhiteSpace(safeFilename))
            throw new ArgumentException("safeFilename must be non-empty", nameof(safeFilename));

        var candidate = Path.Combine(inboxDir, safeFilename);
        if (!File.Exists(candidate))
            return candidate;

        var nameOnly = Path.GetFileNameWithoutExtension(safeFilename);
        var extension = Path.GetExtension(safeFilename);

        for (var i = 1; i <= MaxCollisionSuffix; i++)
        {
            var suffixed = $"{nameOnly}_{i}{extension}";
            var full = Path.Combine(inboxDir, suffixed);
            if (!File.Exists(full))
                return full;
        }

        throw new CollisionExhaustedException(safeFilename);
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="targetPath"/>
    /// atomically by first writing to a temp sibling file in the same
    /// directory and then using <see cref="File.Move(string, string)"/>
    /// to rename into place. Callers SHALL always pass a path returned
    /// by <see cref="ReserveUniquePath"/> and ensure the parent directory
    /// already exists.
    /// </summary>
    public static async Task WriteAtomicAsync(
        string targetPath,
        ReadOnlyMemory<byte> bytes,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new ArgumentException("targetPath must include a directory", nameof(targetPath));

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tempPath, targetPath);
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // best-effort cleanup; do not mask the original exception
        }
    }

    /// <summary>
    /// Convenience: sanitizes the incoming filename with
    /// <see cref="FilenameSanitizer.Sanitize"/>, reserves a unique
    /// slot, writes the bytes atomically, and returns the full path
    /// that was written. Callers SHALL ensure <paramref name="inboxDir"/>
    /// already exists.
    /// </summary>
    public static async Task<string> SanitizeReserveAndWriteAsync(
        string inboxDir,
        string rawFilename,
        ReadOnlyMemory<byte> bytes,
        CancellationToken ct)
    {
        var safeName = FilenameSanitizer.Sanitize(rawFilename);
        var targetPath = ReserveUniquePath(inboxDir, safeName);
        await WriteAtomicAsync(targetPath, bytes, ct).ConfigureAwait(false);
        return targetPath;
    }

    /// <summary>
    /// Moves an existing temp file into the inbox with filename sanitization
    /// and collision resolution. Used by the streaming download path where the
    /// file is already fully written to a temp path in the same directory.
    /// The move is an atomic rename (same filesystem guaranteed by caller).
    /// </summary>
    public static string SanitizeReserveAndMove(
        string inboxDir,
        string rawFilename,
        string sourceTempPath)
    {
        var safeName = FilenameSanitizer.Sanitize(rawFilename);
        var targetPath = ReserveUniquePath(inboxDir, safeName);
        File.Move(sourceTempPath, targetPath);
        return targetPath;
    }
}
