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
        var path = Path.Join(Path.GetTempPath(), "netclaw-crash-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }
}
