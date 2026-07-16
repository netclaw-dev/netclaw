// -----------------------------------------------------------------------
// <copyright file="DaemonShutdownConfiguration.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Daemon;

/// <summary>
/// Builds the Akka CoordinatedShutdown HOCON override that bounds the daemon's session-drain
/// phase. Extracted into its own testable method (rather than an inline string literal in
/// Program.cs's top-level statements) so a unit test can assert the interpolated timeout
/// tracks <see cref="DaemonConfig.GracefulShutdownBudget"/> instead of drifting back to a
/// hardcoded literal (see <see cref="DaemonConfig.GracefulShutdownBudget"/> remarks for why
/// that drift is the exact class of bug behind netclaw-dev/netclaw#1664 and #1665).
/// </summary>
internal static class DaemonShutdownConfiguration
{
    /// <summary>
    /// Coordinated-shutdown HOCON: disables the CLR-exit side effect (the daemon's own
    /// restart loop owns process lifetime, not CoordinatedShutdown) and sizes the
    /// <c>before-service-unbind</c> phase — where session draining
    /// (<c>SessionDrainHelper.DrainAsync</c>) actually runs — to <paramref name="gracefulShutdownBudget"/>.
    /// </summary>
    public static string BuildCoordinatedShutdownHocon(TimeSpan gracefulShutdownBudget) => $$"""
        akka.coordinated-shutdown {
            exit-clr = off
            phases.before-service-unbind.timeout = {{(int)gracefulShutdownBudget.TotalSeconds}}s
        }
        """;
}
