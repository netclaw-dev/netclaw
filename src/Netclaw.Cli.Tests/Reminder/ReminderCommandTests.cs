// -----------------------------------------------------------------------
// <copyright file="ReminderCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Reminder;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Reminder;

public sealed class ReminderCommandTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public async Task Run_calls_daemon_run_endpoint_and_prints_success()
    {
        HttpRequestMessage? captured = null;
        var api = CreateDaemonApi(request =>
        {
            captured = request;
            return FakeHttpMessageHandler.JsonResponse(new
            {
                id = "daily-summary",
                started = true,
                message = "Reminder 'daily-summary' run started."
            });
        });

        var result = await CaptureConsoleAsync(() =>
            ReminderCommand.RunAsync(["reminder", "run", "daily-summary"], api));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(HttpMethod.Post, captured?.Method);
        Assert.Equal("/api/reminders/daily-summary/run", captured?.RequestUri?.AbsolutePath);
        Assert.Contains("run started", result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public async Task Run_surfaces_daemon_rejection()
    {
        var api = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(
            new { error = "Reminder 'daily-summary' is disabled." },
            HttpStatusCode.BadRequest));

        var result = await CaptureConsoleAsync(() =>
            ReminderCommand.RunAsync(["reminder", "run", "daily-summary"], api));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("disabled", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("run started", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_surfaces_daemon_problem_detail()
    {
        var api = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(
            new { detail = "Reminder 'daily-summary' is already executing." },
            HttpStatusCode.Conflict));

        var result = await CaptureConsoleAsync(() =>
            ReminderCommand.RunAsync(["reminder", "run", "daily-summary"], api));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("already executing", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_without_daemon_api_reports_daemon_requirement()
    {
        var result = await CaptureConsoleAsync(() =>
            ReminderCommand.RunAsync(["reminder", "run", "daily-summary"], daemonApi: null));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("reminder run: requires a running daemon", result.Stderr);
    }

    private DaemonApi CreateDaemonApi(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var configuration = new ConfigurationBuilder().Build();
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        return new DaemonApi(new FakeHttpClientFactory(handler), configuration, paths);
    }

    private static async Task<ConsoleResult> CaptureConsoleAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exitCode = await action();
            return new ConsoleResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    private sealed record ConsoleResult(int ExitCode, string Stdout, string Stderr);
}
