// -----------------------------------------------------------------------
// <copyright file="SessionStoragePathsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;
using Netclaw.Actors.Sessions;

namespace Netclaw.Actors.Tests.Protocol;

public sealed class SessionStoragePathsTests
{
    [Fact]
    public void Version_2_paths_share_one_envelope()
    {
        var envelope = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netclaw-storage", "session-42"));
        var storage = SessionStoragePaths.CreateVersion2(new SessionStorageEnvelopeRoot(envelope));

        Assert.Equal(SessionStorageLayoutVersion.Version2, storage.Binding?.LayoutVersion);
        Assert.Equal(Path.Combine(envelope, "workspace"), storage.SessionDirectory.Value);
        Assert.Equal(Path.Combine(envelope, "attachment-staging"), storage.AttachmentStagingDirectory.Value);
        Assert.Equal(Path.Combine(envelope, "artifacts"), storage.ArtifactDirectory.Value);
        Assert.Equal(Path.Combine(envelope, "tmp", "parent"), storage.ManagedTemporary.Directory.Value);
        Assert.Equal(envelope, storage.ManagedTemporary.StorageRoot.Value);
        Assert.Equal(Path.Combine(envelope, "worktrees"), storage.WorktreeDirectory.Value);
        Assert.Equal(Path.Combine(envelope, "logs", "session.log"), storage.LogPath.Value);
        Assert.Equal(envelope, storage.Binding?.EnvelopeRoot.Value);
    }

    [Fact]
    public void Child_paths_use_the_run_identifier_and_keep_parent_workspace()
    {
        var envelope = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netclaw-storage", "session-42"));
        var parent = SessionStoragePaths.CreateVersion2(new SessionStorageEnvelopeRoot(envelope));

        var child = parent.ForChild(
            new SubAgentRunId("run-7"),
            new SubAgentScopeId("session-42/subagent/example/run-7"));

        var childRoot = Path.Combine(envelope, "subagents", "run-7");
        Assert.Equal(parent.SessionDirectory, child.SessionDirectory);
        Assert.Equal(Path.Combine(childRoot, "artifacts"), child.ArtifactDirectory.Value);
        Assert.Equal(Path.Combine(childRoot, "tmp"), child.ManagedTemporary.Directory.Value);
        Assert.Equal(envelope, child.ManagedTemporary.StorageRoot.Value);
        Assert.Equal(Path.Combine(childRoot, "logs", "session.log"), child.LogPath.Value);
        Assert.Equal(parent.WorktreeDirectory, child.WorktreeDirectory);
        Assert.Equal(parent.Binding, child.Binding);
    }

    [Fact]
    public void Envelope_root_rejects_relative_and_noncanonical_paths()
    {
        Assert.Throws<ArgumentException>(() => new SessionStorageEnvelopeRoot("relative/path"));

        var noncanonical = Path.Combine(Path.GetTempPath(), "one", "..", "two");
        Assert.Throws<ArgumentException>(() => new SessionStorageEnvelopeRoot(noncanonical));
    }

    [Fact]
    public void Managed_temporary_location_rejects_a_directory_outside_its_storage_root()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netclaw-storage", "root"));
        var outside = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netclaw-storage", "outside"));

        Assert.Throws<ArgumentException>(() => ManagedTemporaryLocation.FromPersistedPaths(outside, root));
        Assert.Throws<ArgumentException>(() => ManagedTemporaryLocation.FromPersistedPaths(root, root));
    }

    [Fact]
    public void Parent_and_child_contexts_share_the_same_storage_guidance()
    {
        var envelope = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netclaw-storage", "session-42"));
        var storage = SessionStoragePaths.CreateVersion2(new SessionStorageEnvelopeRoot(envelope));

        var parentContext = SessionContextFormatter.Format(storage, "session-42");
        var childContext = SessionContextFormatter.Format(storage);

        Assert.Equal(parentContext.Replace("\nid: session-42", string.Empty, StringComparison.Ordinal), childContext);
        Assert.Contains("Use an explicitly required platform temporary path unchanged.", childContext);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("run/child")]
    [InlineData("run\\child")]
    public void Run_identifier_rejects_path_syntax(string value)
    {
        Assert.Throws<ArgumentException>(() => new SubAgentRunId(value));
    }

    [Fact]
    public void Legacy_paths_keep_the_existing_session_and_log_locations()
    {
        var basePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netclaw-storage"));
        var sessionDirectory = Path.Combine(basePath, "sessions", "signalr_example");
        var logBase = Path.Combine(basePath, "logs", "sessions");
        var storage = SessionStoragePaths.CreateLegacy(
            sessionDirectory,
            logBase,
            "signalr_example");

        Assert.Null(storage.Binding);
        Assert.Equal(sessionDirectory, storage.SessionDirectory.Value);
        Assert.Equal(Path.Combine(logBase, "signalr_example", "session.log"), storage.LogPath.Value);
        Assert.Equal(sessionDirectory, storage.ManagedTemporary.StorageRoot.Value);

        var child = storage.ForChild(
            new SubAgentRunId("run-7"),
            new SubAgentScopeId("signalr/example/subagent/example/run-7"));
        Assert.Equal(
            Path.Combine(logBase, "signalr_example_subagent_example_run-7", "session.log"),
            child.LogPath.Value);
    }
}
