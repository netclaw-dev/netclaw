// -----------------------------------------------------------------------
// <copyright file="RepositoryIdentityProcessAdversarialTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class RepositoryIdentityProcessAdversarialTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), $"repository-process-control-{Guid.NewGuid():N}"));

    [Fact]
    public async Task Both_pipes_drain_before_the_process_deadline()
    {
        // Git supplies the POSIX shell for aliases on Windows too.
        const string script = "!i=0; while test $i -lt 4096; do "
            + "printf '%0256d\\n' 0; printf '%0256d\\n' 0 >&2; i=$((i+1)); done; "
            + "printf 'stdout-complete\\n'; printf 'stderr-complete\\n' >&2";
        using var process = CreateGit("-c", $"alias.fixture-output={script}", "fixture-output");
        try
        {
            var result = await RepositoryIdentityTests.RunProcessAsync(
                process, TestContext.Current.CancellationToken);

            Assert.True(process.HasExited);
            Assert.Equal(0, result.ExitCode);
            var payload = string.Concat(Enumerable.Repeat(new string('0', 256) + "\n", 4096));
            Assert.Equal(payload + "stdout-complete\n", result.StandardOutput);
            Assert.Equal(payload + "stderr-complete\n", result.StandardError);
        }
        finally
        {
            await StopOwnedProcessAsync(process);
        }
    }

    [Fact]
    public async Task Deadline_failure_reaps_the_process_before_return()
    {
        using var process = CreateGit("hash-object", "--stdin");
        process.StartInfo.RedirectStandardInput = true;
        try
        {
            var error = await Record.ExceptionAsync(() => RepositoryIdentityTests.RunProcessAsync(
                process, TestContext.Current.CancellationToken));

            Assert.NotNull(error);
            var aggregate = Assert.IsType<AggregateException>(error);
            Assert.IsType<TimeoutException>(aggregate.InnerExceptions[0]);
            Assert.True(process.HasExited, "The failed helper must not leave its Git process alive.");
        }
        finally
        {
            await StopOwnedProcessAsync(process);
        }
    }

    [Fact]
    public async Task Caller_cancellation_reaps_a_started_process_before_return()
    {
        using var process = CreateGit("hash-object", "--stdin");
        process.StartInfo.RedirectStandardInput = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        try
        {
            var operation = RepositoryIdentityTests.RunProcessAsync(process, cancellation.Token);
            Assert.True(process.Id > 0);
            Assert.False(process.HasExited);
            cancellation.Cancel();

            var error = await Record.ExceptionAsync(() => operation);

            Assert.NotNull(error);
            var aggregate = Assert.IsType<AggregateException>(error);
            Assert.IsAssignableFrom<OperationCanceledException>(aggregate.InnerExceptions[0]);
            Assert.True(process.HasExited, "Cancellation must not leave its Git process alive.");
        }
        finally
        {
            await StopOwnedProcessAsync(process);
        }
    }

    public void Dispose() => _root.Delete(recursive: true);

    private Process CreateGit(params string[] arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = _root.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        return process;
    }

    private static async Task StopOwnedProcessAsync(Process process)
    {
        // A faulty helper must fail its assertions without an orphan from the fault probe.
        if (process.HasExited)
            return;
        process.Kill(entireProcessTree: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(deadline.Token);
    }
}
