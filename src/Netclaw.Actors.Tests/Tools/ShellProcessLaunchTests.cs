// -----------------------------------------------------------------------
// <copyright file="ShellProcessLaunchTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ShellProcessLaunchTests
{

    [Fact]
    public void Launch_does_not_select_a_working_directory_from_context()
    {
        using var directory = new DisposableTempDir();
        var environment = TestShellEnvironment.Current;
        var context = TestToolExecutionContext.CreateBound("launch/cwd", directory.Path, TrustAudience.Personal);

        Assert.Throws<ArgumentNullException>(() => new ShellProcessLaunch(
            "echo unexpected", null!, context.Invocation,
            new ShellCommandPolicy(environment), new ToolPathPolicy(environment, []), static _ => Task.CompletedTask));
    }

    [Fact]
    public async Task Child_environment_remains_the_submission_snapshot()
    {
        using var directory = new DisposableTempDir();
        var environment = TestShellEnvironment.Current;
        var key = "NETCLAW_LAUNCH_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable(key, "submitted");
            var command = environment.Grammar == ShellGrammar.Bash ? $"echo ${key}" : $"echo $env:{key}";
            var context = TestToolExecutionContext.CreateBound("launch/environment", directory.Path, TrustAudience.Personal);
            var launch = new ShellProcessLaunch(command, directory.Path, context.Invocation,
                new ShellCommandPolicy(environment), new ToolPathPolicy(environment, []), static _ => Task.CompletedTask);
            Environment.SetEnvironmentVariable(key, "changed");

            using var process = await launch.StartAsync(TestContext.Current.CancellationToken);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal("submitted", output.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [SlopwatchSuppress("SW001", "This test requires native POSIX symbolic-link behavior.")]
    [Fact(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsPosix),
        Skip = "POSIX-only symbolic-link semantics")]
    public async Task Final_start_rejects_a_link_that_changes_after_authorization()
    {
        using var directory = new DisposableTempDir();
        var safe = Directory.CreateDirectory(Path.Combine(directory.Path, "safe"));
        var denied = Directory.CreateDirectory(Path.Combine(directory.Path, "denied"));
        var link = Path.Combine(directory.Path, "target");
        Directory.CreateSymbolicLink(link, safe.FullName);
        var environment = TestShellEnvironment.Current;
        var context = TestToolExecutionContext.CreateBound("launch/path", directory.Path, TrustAudience.Personal);
        var launch = new ShellProcessLaunch("echo forbidden > target/output.txt", directory.Path, context.Invocation,
            new ShellCommandPolicy(environment), new ToolPathPolicy(environment, [denied.FullName]), _ =>
            {
                Directory.Delete(link);
                Directory.CreateSymbolicLink(link, denied.FullName);
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<ShellProcessStartException>(() => launch.StartAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(denied.FullName, "output.txt")));
    }

    [SlopwatchSuppress("SW001", "This test uses the native Bash TCP redirection and process identifiers.")]
    [Theory(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsPosix),
        Skip = "Native Bash process-tree proof")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Foreground_cancellation_stops_the_parent_and_child_process(bool stream)
    {
        using var directory = new DisposableTempDir();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var environment = TestShellEnvironment.Current;
        var tool = new ShellTool(new ToolConfig(), new ToolPathPolicy(environment, []), new ShellCommandPolicy(environment));
        var context = TestToolExecutionContext.CreateBound("launch/tree", directory.Path, TrustAudience.Personal);
        var command = $"sleep 120 & child=$!; printf '%s %s\\n' $$ \"$child\" > /dev/tcp/127.0.0.1/{port}; wait";
        using var cancellation = new CancellationTokenSource();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var call = ToolInput.Create("Command", command);
        var execution = stream ? ConsumeStreamAsync(tool, call, context, cancellation.Token)
            : tool.ExecuteAsync(call, context, cancellation.Token);

        try
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            using var reader = new StreamReader(connection.GetStream());
            var ids = (await reader.ReadLineAsync(deadline.Token))!.Split(' ').Select(int.Parse).ToArray();
            using var parent = Process.GetProcessById(ids[0]);
            using var child = Process.GetProcessById(ids[1]);
            Assert.False(parent.HasExited);
            Assert.False(child.HasExited);

            cancellation.Cancel();
            await execution.WaitAsync(deadline.Token);
            await parent.WaitForExitAsync(deadline.Token);
            await child.WaitForExitAsync(deadline.Token);
            Assert.True(parent.HasExited);
            Assert.True(child.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            await execution.WaitAsync(deadline.Token);
        }
    }

    private static async Task<string> ConsumeStreamAsync(
        ShellTool tool, IDictionary<string, object?> arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        await foreach (var update in tool.ExecuteStreamAsync(arguments, context, cancellationToken))
        {
            if (update is ToolCompletedUpdate completed)
                return completed.Result;
        }
        throw new InvalidOperationException("The shell stream has no completion.");
    }
}
