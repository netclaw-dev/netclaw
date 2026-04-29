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
    public void Unset_env_var_falls_back_to_user_profile_default()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".netclaw");

        var paths = new NetclawPaths();

        Assert.Equal(expected, paths.BasePath);
    }

    [Fact]
    public void Empty_env_var_falls_back_to_default()
    {
        Environment.SetEnvironmentVariable(EnvVar, "");
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".netclaw");

        var paths = new NetclawPaths();

        Assert.Equal(expected, paths.BasePath);
    }

    [Fact]
    public void Whitespace_only_env_var_falls_back_to_default()
    {
        Environment.SetEnvironmentVariable(EnvVar, "   ");
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
