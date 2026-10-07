// -----------------------------------------------------------------------
// <copyright file="NetclawPathsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Tests for <see cref="NetclawPaths"/> constructor precedence, covering the
/// explicit-argument, NETCLAW_HOME environment variable, and default paths.
///
/// These tests mutate a process-wide environment variable, so they are
/// serialized via a collection fixture to prevent parallel xUnit test runs
/// from clobbering each other's view of <c>NETCLAW_HOME</c>.
/// </summary>
[Collection(nameof(NetclawHomeEnvCollection))]
public sealed class NetclawPathsTests : IDisposable
{
    private const string EnvVar = "NETCLAW_HOME";
    private readonly string? _originalValue;

    public NetclawPathsTests()
    {
        _originalValue = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, _originalValue);
    }

    [Fact]
    public void ExplicitBasePath_overrides_env_var()
    {
        Environment.SetEnvironmentVariable(EnvVar, "/tmp/from-env");
        var explicitPath = Path.Combine(Path.GetTempPath(), "explicit-" + Guid.NewGuid().ToString("N"));

        var paths = new NetclawPaths(explicitPath);

        Assert.Equal(explicitPath, paths.BasePath);
        Assert.Equal(Path.Combine(explicitPath, "workspaces"), paths.WorkspacesDirectory);
    }

    [Fact]
    public void EnvVar_overrides_default_when_basePath_is_null()
    {
        var envPath = Path.Combine(Path.GetTempPath(), "env-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(EnvVar, envPath);

        var paths = new NetclawPaths();

        Assert.Equal(envPath, paths.BasePath);
        Assert.Equal(Path.Combine(envPath, "netclaw.db"), paths.SqliteDbPath);
        Assert.Equal(Path.Combine(envPath, "logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(envPath, "identity"), paths.IdentityDirectory);
    }

    [Fact]
    public void Relative_env_var_is_canonicalized_once_for_daemon_reuse()
    {
        var relativePath = "netclaw-relative-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(EnvVar, relativePath);

        var bootstrapPaths = new NetclawPaths();
        var reusedPaths = new NetclawPaths(bootstrapPaths.BasePath);

        Assert.Equal(Path.GetFullPath(relativePath), bootstrapPaths.BasePath);
        Assert.Equal(bootstrapPaths.BasePath, reusedPaths.BasePath);
        Assert.Equal(bootstrapPaths.RuntimeDirectory, reusedPaths.RuntimeDirectory);
    }

    [Fact]
    public void EnsureDirectoriesExist_creates_runtime_directory_idempotently()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "netclaw-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new NetclawPaths(basePath);

            paths.EnsureDirectoriesExist();
            paths.EnsureDirectoriesExist();

            Assert.Equal(Path.Combine(basePath, "runtime"), paths.RuntimeDirectory);
            Assert.True(Directory.Exists(paths.RuntimeDirectory));
        }
        finally
        {
            if (Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_blank_env_var_falls_back_to_user_profile_default(string? envValue)
    {
        Environment.SetEnvironmentVariable(EnvVar, envValue);
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".netclaw");

        var paths = new NetclawPaths();

        Assert.Equal(expected, paths.BasePath);
    }

    [Fact]
    public void EnvVar_value_is_trimmed()
    {
        var envPath = Path.Combine(Path.GetTempPath(), "trim-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(EnvVar, "  " + envPath + "  ");

        var paths = new NetclawPaths();

        Assert.Equal(envPath, paths.BasePath);
    }

    [Fact]
    public void Explicit_workspacesDirectory_overrides_derived_path()
    {
        var envPath = Path.Combine(Path.GetTempPath(), "ws-env-" + Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(Path.GetTempPath(), "ws-explicit-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(EnvVar, envPath);

        var paths = new NetclawPaths(workspacesDirectory: workspacePath);

        Assert.Equal(envPath, paths.BasePath);
        Assert.Equal(workspacePath, paths.WorkspacesDirectory);
    }

    [Theory]
    [InlineData("~/repositories")]
    [InlineData("$HOME/repositories")]
    [InlineData("${HOME}/repositories")]
    public void Tilde_and_HOME_tokens_in_workspacesDirectory_expand_to_user_home(string configured)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(home, "repositories");

        var paths = new NetclawPaths(workspacesDirectory: configured);

        Assert.Equal(expected, paths.WorkspacesDirectory);
    }

    [Fact]
    public void Tilde_in_basePath_expands_to_user_home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(home, ".netclaw-test");

        var paths = new NetclawPaths("~/.netclaw-test");

        Assert.Equal(expected, paths.BasePath);
    }

    [Fact]
    public void Tilde_in_NETCLAW_HOME_env_var_expands_to_user_home()
    {
        Environment.SetEnvironmentVariable(EnvVar, "~/.netclaw-env-test");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(home, ".netclaw-env-test");

        var paths = new NetclawPaths();

        Assert.Equal(expected, paths.BasePath);
    }

    [Fact]
    public void EnsureDirectoriesExist_reports_actionable_initialization_failures()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "netclaw-path-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(basePath, "not a directory");

        try
        {
            var paths = new NetclawPaths(basePath);

            var exception = Assert.Throws<NetclawDirectoryInitializationException>(paths.EnsureDirectoriesExist);

            Assert.Equal(basePath, exception.BasePath);
            Assert.Contains(exception.Failures, failure => failure.DirectoryPath == paths.IdentityDirectory);
            Assert.Contains("Failed to initialize Netclaw directories", exception.Message);
            Assert.Contains("Docker bind mount", exception.Message);
            Assert.Contains("sudo chown -R 1654:1654", exception.Message);
        }
        finally
        {
            File.Delete(basePath);
        }
    }
}

/// <summary>
/// Collection definition used to serialize tests that mutate the
/// <c>NETCLAW_HOME</c> process environment variable. xUnit runs test classes
/// in different collections in parallel; this collection ensures no other
/// test class racing against it can observe or overwrite the variable mid-run.
/// </summary>
[CollectionDefinition(nameof(NetclawHomeEnvCollection), DisableParallelization = true)]
public sealed class NetclawHomeEnvCollection
{
}
