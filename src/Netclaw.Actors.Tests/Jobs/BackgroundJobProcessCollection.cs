// -----------------------------------------------------------------------
// <copyright file="BackgroundJobProcessCollection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Actors.Tests.Jobs;

/// <summary>
/// Serializes the background-job test classes that spawn REAL OS processes
/// (<c>sleep</c>/<c>echo</c> via <c>BackgroundJobExecutionActor</c>). Run
/// concurrently with each other — or with the rest of the heavily-parallel
/// suite — they mutually starve the shared thread pool, process table, and
/// temp filesystem. Under that load a manager's own message handler can throw
/// transiently (e.g. an <c>IOException</c> persisting a job definition), the
/// actor restarts, and startup reconciliation marks the test's freshly-created
/// in-flight jobs as <c>Lost</c> — a correct production reaction to a restart,
/// but a spurious one to induce in a unit test. <c>DisableParallelization</c>
/// makes these classes run on their own, eliminating the mutual starvation
/// without weakening any assertion. Pure-I/O job tests
/// (<c>BackgroundJobDefinitionStoreTests</c>, <c>JobOutputLogTests</c>,
/// <c>CheckBackgroundJobToolTests</c>) do not spawn processes and stay in the
/// default parallel pool.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BackgroundJobProcessCollection
{
    public const string Name = "BackgroundJobProcess";
}
