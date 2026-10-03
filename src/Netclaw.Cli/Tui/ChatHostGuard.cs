// -----------------------------------------------------------------------
// <copyright file="ChatHostGuard.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
// Copyright (c) Petabridge, LLC. All rights reserved.
// Licensed under the Apache 2.0 license. See LICENSE file in the project root for full license information.

namespace Netclaw.Cli.Tui;

internal static class ChatHostGuard
{
    public static async Task<bool> TryRunAsync(
        Func<Task> runHostAsync,
        TextWriter error,
        Action<Exception> writeCrashLog)
    {
        ArgumentNullException.ThrowIfNull(runHostAsync);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(writeCrashLog);

        try
        {
            await runHostAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            writeCrashLog(ex);
            await error.WriteLineAsync($"netclaw: chat UI could not run: {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }
}
