// -----------------------------------------------------------------------
// <copyright file="BackgroundShellLaunchFixture.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tests.Jobs;

internal static class BackgroundShellLaunchFixture
{
    // Lifecycle tests supply explicit authority. Coordinator tests exercise grant decisions separately.
    internal static ShellProcessLaunch Create(
        string command,
        string sessionDirectory,
        string sessionId,
        ShellExecutionEnvironment environment,
        string? workingDirectory = null)
    {
        var context = TestToolExecutionContext.CreateBound(sessionId, sessionDirectory,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.Personal
            });
        return new ShellProcessLaunch(command, workingDirectory, context.Invocation,
            new ShellCommandPolicy(environment), new ToolPathPolicy(environment, []),
            static _ => Task.CompletedTask);
    }
}
