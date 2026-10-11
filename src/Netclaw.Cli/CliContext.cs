// -----------------------------------------------------------------------
// <copyright file="CliContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli;

/// <summary>
/// Holds the execution environment for one CLI invocation.
/// The caller owns the streams. TUI components use their own terminal services.
/// </summary>
internal sealed record CliContext(
    NetclawPaths Paths,
    TimeProvider Time,
    TextReader Input,
    TextWriter Output,
    TextWriter Error)
{
    internal static CliContext ForProcess(NetclawPaths paths, TimeProvider time)
        => new(paths, time, Console.In, Console.Out, Console.Error);
}
