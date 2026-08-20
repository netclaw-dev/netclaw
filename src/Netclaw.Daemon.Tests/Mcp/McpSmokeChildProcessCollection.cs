// -----------------------------------------------------------------------
// <copyright file="McpSmokeChildProcessCollection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// Serializes the MCP test classes that start a real child process — the
/// deterministic <c>Netclaw.SmokeMcpServer</c> or the python prompt server.
/// The invariant: a child-process cold start must not share a small CI runner
/// with another child-process cold start.
/// <para>
/// Each spawn pays a full cold start before the server announces readiness: a
/// new CoreCLR, the DI graph, the MCP reflection scan, and a Kestrel bind for
/// the HTTP transport. xunit gives one collection per class and runs up to
/// <c>maxParallelThreads</c> collections together, so six such classes started
/// six child processes at once on the 4-vCPU Windows runner. The children
/// starved each other for CPU, and Windows added a Defender scan of the fresh
/// DLL per spawn.
/// <c>SmokeMcpServerHttpHeaderTests.ConfiguredHeader_WhenOAuthProbeReturnsMetadata_StillReachesServer</c>
/// then failed with "Smoke MCP server did not publish a listening URL within 30s".
/// The readiness signal itself is correct. The runner simply had no CPU left.
/// </para>
/// <para>
/// <c>DisableParallelization</c> changes the schedule only. It runs these
/// classes one at a time. It does not skip, disable, or weaken any test, and
/// no timeout value changes. Classes that use fake process doubles, such as
/// <c>PowerShellHostProbeTests</c>, stay in the default parallel pool.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class McpSmokeChildProcessCollection
{
    public const string Name = "McpSmokeChildProcess";
}
