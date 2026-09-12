// -----------------------------------------------------------------------
// <copyright file="DaemonCliArgs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Daemon;

/// <summary>
/// Classifies top-level command-line arguments passed to netclawd. Kept as a small,
/// independently testable predicate because Program.cs is top-level statements — extracting
/// the check here lets a unit test cover the arg-handling without booting the host.
/// </summary>
internal static class DaemonCliArgs
{
    /// <summary>
    /// Returns <c>true</c> if the first argument requests the version banner
    /// (<c>--version</c> or <c>-v</c>). Checked before the daemon acquires its lock file or
    /// starts the host so <c>netclawd --version</c> prints and exits without booting a real
    /// daemon instance (alpha.onnx.2 production canary regression).
    /// </summary>
    public static bool IsVersionRequest(string[] args)
        => args.Length > 0 && args[0] is "--version" or "-v";
}
