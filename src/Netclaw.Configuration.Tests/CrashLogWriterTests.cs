// -----------------------------------------------------------------------
// <copyright file="CrashLogWriterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

[Collection(nameof(NetclawHomeEnvCollection))]
public sealed class CrashLogWriterTests : IDisposable
{
    private const string EnvVar = "NETCLAW_HOME";
    private readonly string? _originalValue = Environment.GetEnvironmentVariable(EnvVar);
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, _originalValue);
        foreach (var directory in _tempDirectories)
            Directory.Delete(directory, recursive: true);
    }

    [Theory]
    [InlineData("crash-20260414-182900.log")]
    [InlineData("crash-20260414-182900-4242-123-1.log")]
    [InlineData("crash-20260414-182900-4242-0123456789abcdef0123456789abcdef.log")]
    public void TryParseFileName_accepts_what_the_writer_produces(string name)
    {
        Assert.True(CrashLogWriter.TryParseFileName(name, out var ts));
        Assert.Equal(DateTimeOffset.Parse("2026-04-14T18:29:00Z"), ts);
    }

    [Theory]
    [InlineData("crash-20260414-182900-notes.log")]
    [InlineData("crash-20260414.log")]
    [InlineData("crash-20260414report.log")]
    [InlineData("crash-20260414-182900.log.bak")]
    [InlineData("Crash-20260414-182900.log")]
    [InlineData("crash-20261414-182900.log")]
    [InlineData("crash-20260414-182900-4242.log")]
    public void TryParseFileName_rejects_everything_else(string name)
    {
        Assert.False(CrashLogWriter.TryParseFileName(name, out _));
    }

    [Fact]
    public void TryParseFileName_round_trips_a_name_the_writer_just_wrote()
    {
        var home = NewTempDirectory();
        var now = DateTimeOffset.Parse("2026-04-14T18:29:00Z");
        using var errors = new StringWriter();

        // The second write in the same second takes the uniqueness-suffix path.
        var first = CrashLogWriter.TryWrite(new InvalidOperationException("a"), "daemon", new FixedTime(now), errors, home);
        var second = CrashLogWriter.TryWrite(new InvalidOperationException("b"), "daemon", new FixedTime(now), errors, home);

        Assert.NotEqual(first, second);
        Assert.True(CrashLogWriter.TryParseFileName(Path.GetFileName(first!), out _));
        Assert.True(CrashLogWriter.TryParseFileName(Path.GetFileName(second!), out var ts));
        Assert.Equal(now, ts);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void TryWrite_DefaultDirectory_HonorsNetclawHome()
    {
        var home = NewTempDirectory();
        Environment.SetEnvironmentVariable(EnvVar, home);
        using var errors = new StringWriter();

        var crashPath = CrashLogWriter.TryWrite(
            new InvalidOperationException("boom"), "CLI", errorWriter: errors);

        Assert.NotNull(crashPath);
        Assert.Equal(Path.Join(home, "logs"), Path.GetDirectoryName(crashPath));
        Assert.True(File.Exists(crashPath));
        Assert.Contains(crashPath, errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryWrite_ExplicitDirectory_TakesPrecedenceOverNetclawHome()
    {
        var home = NewTempDirectory();
        var explicitLogs = NewTempDirectory();
        Environment.SetEnvironmentVariable(EnvVar, home);
        using var errors = new StringWriter();

        var crashPath = CrashLogWriter.TryWrite(
            new InvalidOperationException("boom"), "CLI",
            errorWriter: errors, logsDirectory: explicitLogs);

        Assert.NotNull(crashPath);
        Assert.Equal(explicitLogs, Path.GetDirectoryName(crashPath));
        Assert.True(File.Exists(crashPath));
        Assert.False(Directory.Exists(Path.Join(home, "logs")));
        Assert.Contains(crashPath, errors.ToString(), StringComparison.Ordinal);
    }

    private string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netclaw-crash-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }
}
