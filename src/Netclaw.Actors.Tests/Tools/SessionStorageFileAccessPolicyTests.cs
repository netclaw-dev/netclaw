// -----------------------------------------------------------------------
// <copyright file="SessionStorageFileAccessPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using static Netclaw.Actors.Tests.Tools.PathAccessDecisionAssertions;

namespace Netclaw.Actors.Tests.Tools;

public sealed class SessionStorageFileAccessPolicyTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();
    private readonly NetclawPaths _paths;
    private readonly SessionStoragePaths _storage;
    private readonly PathAccessPolicy _policy;
    private readonly ToolInvocationContext _context;

    public SessionStorageFileAccessPolicyTests()
    {
        _paths = new NetclawPaths(_directory.Path);
        _paths.EnsureDirectoriesExist();
        var envelope = Path.Combine(_paths.SessionsDirectory, "current-session");
        _storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.GetFullPath(envelope)));
        _policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        _context = TestToolExecutionContext.CreateBoundWithStorage(
            "signalr/current-session",
            _storage,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.Personal,
                ChannelType = "signalr"
            }).Invocation;
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Current_parent_and_child_logs_follow_normal_operation_permissions()
    {
        var child = _storage.ForChild(
            new SubAgentRunId("run-1"),
            new SubAgentScopeId("signalr/current-session/subagent/test/run-1"));

        AssertAllowed(
            _policy.Evaluate(_storage.LogPath.Value, _context, PathAccessPolicy.FileOperation.Read),
            _storage.LogPath.Value);
        AssertAllowed(
            _policy.Evaluate(child.LogPath.Value, _context, PathAccessPolicy.FileOperation.Read),
            child.LogPath.Value);
        AssertAllowed(
            _policy.Evaluate(_storage.LogPath.Value, _context, PathAccessPolicy.FileOperation.Write),
            _storage.LogPath.Value);
        AssertAllowed(
            _policy.Evaluate(child.LogPath.Value, _context, PathAccessPolicy.FileOperation.Attach),
            child.LogPath.Value);
    }

    [Fact]
    public void Unrestricted_interactive_personal_profile_uses_ordinary_foreign_path_access()
    {
        var foreignMain = Path.Combine(
            _paths.SessionsDirectory,
            "foreign-session",
            "logs",
            "session.log");
        var foreignChild = Path.Combine(
            _paths.SessionsDirectory,
            "foreign-session",
            "subagents",
            "run-2",
            "logs",
            "session.log");

        AssertAllowed(
            _policy.Evaluate(foreignMain, _context, PathAccessPolicy.FileOperation.Read),
            foreignMain);
        AssertAllowed(
            _policy.Evaluate(foreignChild, _context, PathAccessPolicy.FileOperation.Read),
            foreignChild);
    }

    [Fact]
    public void Complete_current_session_envelope_is_one_ordinary_root()
    {
        var childArtifact = Path.Combine(
            _storage.Binding!.EnvelopeRoot.Value,
            "subagents",
            "run-1",
            "artifacts",
            "result.txt");
        var broadChildRoot = Path.Combine(
            _storage.Binding.EnvelopeRoot.Value,
            "subagents",
            "run-1");

        var temporaryResult = Path.Combine(_storage.ManagedTemporary.Directory.Value, "result.txt");
        AssertAllowed(
            _policy.Evaluate(temporaryResult, _context, PathAccessPolicy.FileOperation.Write),
            temporaryResult);
        AssertAllowed(
            _policy.Evaluate(childArtifact, _context, PathAccessPolicy.FileOperation.Read),
            childArtifact);
        AssertAllowed(
            _policy.Evaluate(broadChildRoot, _context, PathAccessPolicy.FileOperation.Read),
            broadChildRoot);
        AssertAllowed(
            _policy.Evaluate(
                _storage.Binding.EnvelopeRoot.Value,
                _context,
                PathAccessPolicy.FileOperation.Read),
            _storage.Binding.EnvelopeRoot.Value);
    }

    [Fact]
    public async Task File_read_and_search_do_not_interrupt_an_active_log_writer()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storage.LogPath.Value)!);
        await using var stream = new FileStream(
            _storage.LogPath.Value,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        await using var writer = new StreamWriter(stream) { AutoFlush = true };
        await writer.WriteLineAsync("active marker");

        var pathPolicy = new Netclaw.Security.ToolPathPolicy([]);
        var readTool = new FileReadTool(new ToolConfig(), _paths, pathPolicy);
        var searchTool = new FileSearchTool(new ToolConfig(), _paths, pathPolicy);
        var read = await readTool.ExecuteAsync(
            ToolInput.Create("Path", _storage.LogPath.Value),
            _context,
            TestContext.Current.CancellationToken);
        var search = await searchTool.ExecuteAsync(
            ToolInput.Create(
                "Root", Path.GetDirectoryName(_storage.LogPath.Value)!,
                "Query", "active marker",
                "Mode", "content"),
            _context,
            TestContext.Current.CancellationToken);

        Assert.Contains("active marker", read, StringComparison.Ordinal);
        Assert.Contains("active marker", search, StringComparison.Ordinal);
        await writer.WriteLineAsync("writer remains active");
        await writer.FlushAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Linked_session_root_does_not_grant_file_access()
    {
        var outside = Path.Combine(_directory.Path, "outside-envelope");
        var linkedEnvelope = Path.Combine(_paths.SessionsDirectory, "linked-envelope");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(linkedEnvelope, outside);
        var storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.GetFullPath(linkedEnvelope)));
        var context = TestToolExecutionContext.CreateBoundWithStorage(
            "signalr/linked-session",
            storage,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.Team,
                ChannelType = "signalr"
            }).Invocation;

        var requestedPath = Path.Combine(linkedEnvelope, "logs", "session.log");
        var decision = _policy.Evaluate(
            requestedPath,
            context,
            PathAccessPolicy.FileOperation.Read);

        AssertDenied(decision, Path.GetFullPath(requestedPath));
        Assert.Contains("symlinked paths", decision.Error, StringComparison.Ordinal);
    }
}
