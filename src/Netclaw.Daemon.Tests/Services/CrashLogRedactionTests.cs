// -----------------------------------------------------------------------
// <copyright file="CrashLogRedactionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

/// <summary>
/// Crash logs bypass the logging pipeline, so they redact with the same redactor that the
/// daemon passes in production.
/// </summary>
public sealed class CrashLogRedactionTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();

    [Fact]
    public void A_crash_log_masks_a_secret_in_the_exception()
    {
        using var errors = new StringWriter();

        var crashPath = CrashLogWriter.TryWrite(
            new InvalidOperationException("login failed for password=hunter2"),
            "daemon",
            SecretOutputRedactor.Redact,
            errorWriter: errors,
            logsDirectory: _directory.Path);

        Assert.NotNull(crashPath);
        var text = File.ReadAllText(crashPath);
        Assert.Contains("password=***REDACTED***", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stderr_fallback_masks_a_secret_in_the_exception()
    {
        // A file where the logs directory should be makes the crash log impossible to write.
        var blocked = Path.Join(_directory.Path, "not-a-directory");
        File.WriteAllText(blocked, string.Empty);
        using var errors = new StringWriter();

        var crashPath = CrashLogWriter.TryWrite(
            new InvalidOperationException("login failed for password=hunter2"),
            "daemon",
            SecretOutputRedactor.Redact,
            errorWriter: errors,
            logsDirectory: blocked);

        Assert.Null(crashPath);
        Assert.Contains("password=***REDACTED***", errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", errors.ToString(), StringComparison.Ordinal);
    }

    public void Dispose() => _directory.Dispose();
}
