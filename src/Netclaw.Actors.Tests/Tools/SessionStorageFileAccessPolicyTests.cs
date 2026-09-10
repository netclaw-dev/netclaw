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
        Assert.Contains("symlinked paths", Assert.IsType<PathAccessPolicy.PathAccessDecision.Denied>(decision).Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TrustAudience.Public, false)]
    [InlineData(TrustAudience.Team, false)]
    [InlineData(TrustAudience.Personal, false)]
    [InlineData(TrustAudience.Public, true)]
    [InlineData(TrustAudience.Team, true)]
    [InlineData(TrustAudience.Personal, true)]
    public async Task AuthorityRegression_Sibling_tools_preserve_own_access(TrustAudience audience, bool legacy)
    {
        var own = CreateStorage("own", legacy);
        var sibling = CreateStorage("own-extra", legacy);
        var ownFile = await SeedFile(own.SessionDirectory.Value, "own.txt", "own-marker");
        var siblingFile = await SeedFile(sibling.SessionDirectory.Value, "hidden.txt", "hidden-marker");
        var read = new FileReadTool(new ToolConfig(), _policy);
        var list = new FileListTool(_policy);
        var search = new FileSearchTool(_policy);
        var attach = new AttachFileTool(_policy);

        foreach (var (storage, path, expected) in new[]
                 {
                     (own, ownFile, true),
                     (sibling, siblingFile, audience == TrustAudience.Personal)
                 })
        {
            var context = CreateContext(own, audience);
            var result = await read.ExecuteAsync(ToolInput.Create("Path", path), context, TestContext.Current.CancellationToken);
            AssertReceipt(context, expected);
            Assert.Equal(expected, result.Contains(storage == own ? "own-marker" : "hidden-marker", StringComparison.Ordinal));

            context = CreateContext(own, audience);
            result = await list.ExecuteAsync(ToolInput.Create("Path", storage.SessionDirectory.Value), context, TestContext.Current.CancellationToken);
            AssertReceipt(context, expected);
            Assert.Equal(expected, result.Contains(Path.GetFileName(path), StringComparison.Ordinal));

            context = CreateContext(own, audience);
            result = await search.ExecuteAsync(ToolInput.Create("Root", storage.SessionDirectory.Value, "Query", "marker", "Mode", "content"), context, TestContext.Current.CancellationToken);
            AssertReceipt(context, expected);
            Assert.Equal(expected, result.Contains(storage == own ? "own-marker" : "hidden-marker", StringComparison.Ordinal));

            context = CreateContext(own, audience);
            await attach.ExecuteAsync(ToolInput.Create("Path", path), context, TestContext.Current.CancellationToken);
            AssertReceipt(context, expected);
            Assert.Equal(expected ? 1 : 0, context.FileAttachments.Count);
        }

        var traversal = Path.GetRelativePath(own.SessionDirectory.Value, siblingFile);
        var traversed = CreateContext(own, audience);
        await read.ExecuteAsync(ToolInput.Create("Path", traversal), traversed, TestContext.Current.CancellationToken);
        AssertReceipt(traversed, audience == TrustAudience.Personal);
        Assert.Equal("hidden-marker", await File.ReadAllTextAsync(siblingFile, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(TrustAudience.Public)]
    [InlineData(TrustAudience.Team)]
    public async Task AuthorityRegression_Legacy_exact_log_has_no_directory_grant(TrustAudience audience)
    {
        var own = CreateStorage("legacy-own", true);
        await SeedFile(Path.GetDirectoryName(own.LogPath.Value)!, "session.log", "own-log-marker");
        var adjacent = await SeedFile(Path.GetDirectoryName(own.LogPath.Value)!, "adjacent.txt", "hidden-marker");
        var read = new FileReadTool(new ToolConfig(), _policy);
        var context = CreateContext(own, audience);
        var result = await read.ExecuteAsync(ToolInput.Create("Path", own.LogPath.Value), context, TestContext.Current.CancellationToken);
        AssertReceipt(context, true);
        Assert.Contains("own-log-marker", result);

        context = CreateContext(own, audience);
        await new AttachFileTool(_policy).ExecuteAsync(ToolInput.Create("Path", own.LogPath.Value), context, TestContext.Current.CancellationToken);
        AssertReceipt(context, true);
        Assert.Single(context.FileAttachments);

        context = CreateContext(own, audience);
        result = await read.ExecuteAsync(ToolInput.Create("Path", adjacent), context, TestContext.Current.CancellationToken);
        AssertReceipt(context, false);
        Assert.DoesNotContain("hidden-marker", result);
        foreach (var directory in new[] { Path.GetDirectoryName(own.LogPath.Value)!, _paths.SessionLogsDirectory })
        {
            context = CreateContext(own, audience);
            await new FileListTool(_policy).ExecuteAsync(ToolInput.Create("Path", directory), context, TestContext.Current.CancellationToken);
            AssertReceipt(context, false);
            context = CreateContext(own, audience);
            await new FileSearchTool(_policy).ExecuteAsync(ToolInput.Create("Root", directory, "Query", "marker", "Mode", "content"), context, TestContext.Current.CancellationToken);
            AssertReceipt(context, false);
        }
        AssertDenied(_policy.Evaluate(Path.Combine(own.LogPath.Value, "child"), context.Invocation, PathAccessPolicy.FileOperation.Read), Path.Combine(own.LogPath.Value, "child"));
        AssertDenied(_policy.Evaluate(own.LogPath.Value, context.Invocation, PathAccessPolicy.FileOperation.DeclareProjectScope), own.LogPath.Value);
        Assert.DoesNotContain(own.LogPath.Value, _policy.GetTrustedRoots(context.Invocation, PathAccessPolicy.FileOperation.Read));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityRegression_Sibling_team_write_has_no_foreign_side_effect(bool legacy)
    {
        var own = CreateStorage("writer", legacy);
        var sibling = CreateStorage("other-writer", legacy);
        var write = new FileWriteTool(_policy);
        foreach (var (storage, allowed) in new[] { (own, true), (sibling, false) })
        {
            var target = Path.Combine(storage.SessionDirectory.Value, "created", "result.txt");
            var context = CreateContext(own, TrustAudience.Team);
            await write.ExecuteAsync(ToolInput.Create("Path", target, "Content", "written"), context, TestContext.Current.CancellationToken);
            AssertReceipt(context, allowed);
            Assert.Equal(allowed, File.Exists(target));
            Assert.Equal(allowed, Directory.Exists(Path.GetDirectoryName(target)));

            var existing = await SeedFile(storage.SessionDirectory.Value, "existing.txt", "original");
            context = CreateContext(own, TrustAudience.Team);
            await new FileEditTool(_policy).ExecuteAsync(ToolInput.Create("Path", existing, "OldString", "original", "NewString", "changed"), context, TestContext.Current.CancellationToken);
            AssertReceipt(context, allowed);
            Assert.Equal(allowed ? "changed" : "original", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(TrustAudience.Team, false)]
    [InlineData(TrustAudience.Team, true)]
    [InlineData(TrustAudience.Personal, false)]
    [InlineData(TrustAudience.Personal, true)]
    public async Task AuthorityRegression_Child_uses_inherited_roots_and_exact_own_log(TrustAudience audience, bool legacy)
    {
        var parent = CreateStorage("parent", legacy);
        var child = parent.ForChild(new SubAgentRunId("child"), new SubAgentScopeId("parent/subagent/child"));
        var peer = parent.ForChild(new SubAgentRunId("peer"), new SubAgentScopeId("parent/subagent/peer"));
        var foreign = CreateStorage("foreign", legacy);
        foreach (var storage in new[] { parent, child, peer, foreign })
            await SeedFile(Path.GetDirectoryName(storage.LogPath.Value)!, "session.log", "log-marker");
        var artifact = await SeedFile(child.ArtifactDirectory.Value, "result.txt", "artifact-marker");
        var read = new FileReadTool(new ToolConfig(), _policy);
        foreach (var caller in new[] { parent, child })
        {
            foreach (var target in new[] { parent, child, peer, foreign })
            {
                var allowed = audience == TrustAudience.Personal || (target != foreign && (!legacy || caller == target));
                var context = CreateContext(caller, audience);
                var result = await read.ExecuteAsync(ToolInput.Create("Path", target.LogPath.Value), context, TestContext.Current.CancellationToken);
                AssertReceipt(context, allowed);
                Assert.Equal(allowed, result.Contains("log-marker", StringComparison.Ordinal));
            }
            var artifactContext = CreateContext(caller, audience);
            await read.ExecuteAsync(ToolInput.Create("Path", artifact), artifactContext, TestContext.Current.CancellationToken);
            AssertReceipt(artifactContext, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityRegression_Links_check_ancestors_above_the_current_root(bool legacy)
    {
        var outside = Path.Combine(_directory.Path, "outside");
        Directory.CreateDirectory(outside);
        var storageBase = legacy ? _paths.SessionLogsDirectory : _paths.SessionsDirectory;
        var link = Path.Combine(storageBase, "linked");
        Directory.CreateSymbolicLink(link, outside);
        var storage = legacy
            ? SessionStoragePaths.CreateLegacy(Path.Combine(_paths.SessionsDirectory, "own"), link, "nested")
            : SessionStoragePaths.CreateVersion2(new SessionStorageEnvelopeRoot(Path.Combine(link, "nested")));
        await SeedFile(Path.GetDirectoryName(storage.LogPath.Value)!, "session.log", "outside-marker");
        var context = CreateContext(storage, TrustAudience.Team);
        var result = await new FileReadTool(new ToolConfig(), _policy).ExecuteAsync(ToolInput.Create("Path", storage.LogPath.Value), context, TestContext.Current.CancellationToken);
        AssertReceipt(context, false);
        Assert.DoesNotContain("outside-marker", result);
    }

    private SessionStoragePaths CreateStorage(string id, bool legacy)
    {
        var root = Path.Combine(_paths.SessionsDirectory, id);
        var storage = legacy
            ? SessionStoragePaths.CreateLegacy(root, _paths.SessionLogsDirectory, id)
            : SessionStoragePaths.CreateVersion2(new SessionStorageEnvelopeRoot(root));
        Directory.CreateDirectory(storage.SessionDirectory.Value);
        return storage;
    }

    private static ToolExecutionContext CreateContext(SessionStoragePaths storage, TrustAudience audience)
        => TestToolExecutionContext.CreateBoundWithStorage("test/session", storage, new TestToolExecutionContextOptions
        {
            Audience = audience,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
            ChannelType = audience == TrustAudience.Personal ? "signalr" : "slack"
        });

    private static async Task<string> SeedFile(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }

    private static void AssertReceipt(ToolExecutionContext context, bool allowed)
    {
        Assert.Equal(allowed ? ToolInvocationOutcomeCategory.Success : ToolInvocationOutcomeCategory.AccessDenied, context.Receipt?.Category);
        if (!allowed)
        {
            Assert.Empty(context.FileAttachments);
            Assert.Empty(context.ModelInputFiles);
            Assert.Empty(context.Receipt!.FileActivity);
        }
    }

    [Theory]
    [InlineData(TrustAudience.Public, false)]
    [InlineData(TrustAudience.Public, true)]
    [InlineData(TrustAudience.Team, false)]
    [InlineData(TrustAudience.Team, true)]
    public async Task AuthorityRegression_Profiles_and_protection_remain_authoritative(TrustAudience audience, bool legacy)
    {
        var own = CreateStorage("profile-own", legacy);
        var foreign = CreateStorage("profile-foreign", legacy);
        await SeedFile(Path.GetDirectoryName(own.LogPath.Value)!, "session.log", "own-log");
        await SeedFile(Path.GetDirectoryName(foreign.LogPath.Value)!, "session.log", "foreign-log");
        var config = new ToolConfig();
        var profile = audience == TrustAudience.Public ? config.AudienceProfiles.Public : config.AudienceProfiles.Team;
        profile.ReadFiles.Roots = [Path.GetDirectoryName(foreign.LogPath.Value)!];
        var configured = new PathAccessPolicy(config, _paths, new ToolPathPolicy([]));
        var context = CreateContext(own, audience);
        await new FileReadTool(config, configured).ExecuteAsync(ToolInput.Create("Path", foreign.LogPath.Value), context, TestContext.Current.CancellationToken);
        AssertReceipt(context, true);

        profile.ReadFiles.Mode = ToolFilesystemMode.None;
        var denied = new PathAccessPolicy(config, _paths, new ToolPathPolicy([]));
        context = CreateContext(own, audience);
        await new FileReadTool(config, denied).ExecuteAsync(ToolInput.Create("Path", own.LogPath.Value), context, TestContext.Current.CancellationToken);
        AssertReceipt(context, false);

        var protectedPolicy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([own.LogPath.Value]));
        context = CreateContext(own, audience);
        await new FileReadTool(new ToolConfig(), protectedPolicy).ExecuteAsync(ToolInput.Create("Path", own.LogPath.Value), context, TestContext.Current.CancellationToken);
        AssertReceipt(context, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityRegression_Own_log_reader_preserves_writer_and_write_profile(bool legacy)
    {
        var storage = CreateStorage("active-log", legacy);
        Directory.CreateDirectory(Path.GetDirectoryName(storage.LogPath.Value)!);
        await using (var stream = new FileStream(storage.LogPath.Value, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        await using (var writer = new StreamWriter(stream) { AutoFlush = true })
        {
            await writer.WriteLineAsync("first-marker");
            var context = CreateContext(storage, TrustAudience.Public);
            var read = await new FileReadTool(new ToolConfig(), _policy).ExecuteAsync(ToolInput.Create("Path", storage.LogPath.Value), context, TestContext.Current.CancellationToken);
            AssertReceipt(context, true);
            Assert.Contains("first-marker", read);
            await writer.WriteLineAsync("second-marker");
        }
        Assert.Contains("second-marker", await File.ReadAllTextAsync(storage.LogPath.Value, TestContext.Current.CancellationToken));
        var team = CreateContext(storage, TrustAudience.Team);
        await new FileEditTool(_policy).ExecuteAsync(ToolInput.Create("Path", storage.LogPath.Value, "OldString", "second-marker", "NewString", "changed-marker"), team, TestContext.Current.CancellationToken);
        AssertReceipt(team, true);
    }
}
