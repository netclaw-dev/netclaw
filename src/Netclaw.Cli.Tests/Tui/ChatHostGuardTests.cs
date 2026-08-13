// -----------------------------------------------------------------------
// <copyright file="ChatHostGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
// Copyright (c) Petabridge, LLC. All rights reserved.
// Licensed under the Apache 2.0 license. See LICENSE file in the project root for full license information.

using Netclaw.Cli.Tui;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class ChatHostGuardTests
{
    [Fact]
    public async Task Host_failure_is_visible_and_writes_the_crash_log()
    {
        var error = new StringWriter();
        Exception? logged = null;
        var failure = new InvalidOperationException("inline terminal unavailable");

        var started = await ChatHostGuard.TryRunAsync(
            () => Task.FromException(failure),
            error,
            ex => logged = ex);

        Assert.False(started);
        Assert.Same(failure, logged);
        Assert.Contains("chat UI could not run", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(failure.Message, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_host_does_not_write_an_error_or_crash_log()
    {
        var error = new StringWriter();
        Exception? logged = null;

        var started = await ChatHostGuard.TryRunAsync(
            () => Task.CompletedTask,
            error,
            ex => logged = ex);

        Assert.True(started);
        Assert.Null(logged);
        Assert.Equal(string.Empty, error.ToString());
    }
}
