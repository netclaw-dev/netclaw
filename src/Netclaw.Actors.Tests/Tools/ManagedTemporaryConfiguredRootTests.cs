// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryConfiguredRootTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// PR #2559 review item S1. An operator can give a Team or Public profile a configured
/// read and write root that covers the shared managed temporary base, for example
/// <c>/tmp</c> over <c>/tmp/netclaw-&lt;uid&gt;</c>. That root must not reach the key folder
/// of another session (read, write, list, search). The own key folder and the other
/// paths below the configured root stay reachable. A protected path still wins.
/// </summary>
/// <remarks>
/// The configured root here is the parent of the base (the stand-in for <c>/tmp</c>). The
/// Netclaw home is in a different folder, so only the temporary layout decides.
/// </remarks>
public sealed class ManagedTemporaryConfiguredRootTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly string _configuredRoot;
    private readonly SessionStoragePaths _storage;
    private readonly SessionStoragePaths _otherStorage;
    private readonly string _ownFile;
    private readonly string _otherFile;
    private readonly string _sharedFile;

    public ManagedTemporaryConfiguredRootTests()
    {
        _paths = new NetclawPaths(Path.Combine(_dir.Path, "home"));
        _configuredRoot = Path.Combine(_dir.Path, "tmp");
        var layout = new ManagedTemporaryLayout.PrivateBase(
            new ManagedTemporaryBase(Path.Combine(_configuredRoot, "netclaw-base")));
        _storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "team-session")),
            layout);
        _otherStorage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "personal-dm")),
            layout);
        // The file tool checks the private folders on each write below the base. Make them
        // the way the daemon does, so the positive write row writes into private folders.
        // Windows has no private base: the folder check refuses it there.
        if (!OperatingSystem.IsWindows())
            Assert.Null(ManagedTemporaryEnvironment.EnsureFolders(_storage.ManagedTemporary));
        _ownFile = Seed(_storage.ManagedTemporary.Directory.Value, "own.txt", "own-marker");
        _otherFile = Seed(_otherStorage.ManagedTemporary.Directory.Value, "secret.env", "foreign-marker");
        _sharedFile = Seed(Path.Combine(_configuredRoot, "shared"), "notes.txt", "shared-marker");
    }

    public static bool IsPosix => !OperatingSystem.IsWindows();

    public void Dispose() => _dir.Dispose();

    // Negative example: a Team or Public session with a root over the base reads, writes,
    // lists, and searches nothing in the key folder of another session.
    [Theory]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Public)]
    public async Task A_configured_root_over_the_base_does_not_reach_the_key_folder_of_another_session(
        TrustAudience audience)
    {
        var (config, policy) = CreatePolicy(audience, new ToolPathPolicy([]));
        var ct = TestContext.Current.CancellationToken;
        var otherKeyFolder = _otherStorage.ManagedTemporary.StorageRoot.Value;
        var otherKey = Path.GetFileName(otherKeyFolder);

        var context = Context(audience);
        var read = await new FileReadTool(config, policy).ExecuteAsync(ToolInput.Create("Path", _otherFile), context, ct);
        AssertDenied(context, read);
        Assert.DoesNotContain("foreign-marker", read);

        var planted = Path.Combine(_otherStorage.ManagedTemporary.Directory.Value, "planted.txt");
        context = Context(audience);
        var write = await new FileWriteTool(policy).ExecuteAsync(ToolInput.Create("Path", planted, "Content", "planted"), context, ct);
        AssertDenied(context, write);
        Assert.False(File.Exists(planted), "A Team or Public write must not plant a file in the TMPDIR of another session.");

        context = Context(audience);
        var edit = await new FileEditTool(policy).ExecuteAsync(
            ToolInput.Create("Path", _otherFile, "OldString", "foreign-marker", "NewString", "changed"), context, ct);
        AssertDenied(context, edit);
        Assert.Equal("foreign-marker", await File.ReadAllTextAsync(_otherFile, ct));

        context = Context(audience);
        var list = await new FileListTool(policy).ExecuteAsync(ToolInput.Create("Path", otherKeyFolder), context, ct);
        AssertDenied(context, list);

        context = Context(audience);
        var search = await new FileSearchTool(policy).ExecuteAsync(
            ToolInput.Create("Root", otherKeyFolder, "Query", "marker", "Mode", "content"), context, ct);
        AssertDenied(context, search);
        Assert.DoesNotContain("foreign-marker", search);

        // A list or a search from the configured root must skip the key folder of the other session.
        context = Context(audience);
        var baseList = await new FileListTool(policy).ExecuteAsync(
            ToolInput.Create("Path", Path.GetDirectoryName(otherKeyFolder)!), context, ct);
        Assert.DoesNotContain(otherKey, baseList);

        context = Context(audience);
        var rootSearch = await new FileSearchTool(policy).ExecuteAsync(
            ToolInput.Create("Root", _configuredRoot, "Query", "marker", "Mode", "content"), context, ct);
        Assert.DoesNotContain("foreign-marker", rootSearch);
        Assert.DoesNotContain(otherKey, rootSearch);
    }

    // Positive example: the same root still reaches the own key folder and the other paths
    // below the configured root.
    [Theory]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Public)]
    public async Task A_configured_root_over_the_base_keeps_the_own_key_folder_and_the_rest_of_the_root(
        TrustAudience audience)
    {
        var (config, policy) = CreatePolicy(audience, new ToolPathPolicy([]));
        var ct = TestContext.Current.CancellationToken;

        foreach (var (path, marker) in new[] { (_ownFile, "own-marker"), (_sharedFile, "shared-marker") })
        {
            var context = Context(audience);
            var read = await new FileReadTool(config, policy).ExecuteAsync(ToolInput.Create("Path", path), context, ct);
            AssertAllowed(context, read);
            Assert.Contains(marker, read);
        }

        var listContext = Context(audience);
        var list = await new FileListTool(policy).ExecuteAsync(
            ToolInput.Create("Path", _storage.ManagedTemporary.Directory.Value), listContext, ct);
        AssertAllowed(listContext, list);
        Assert.Contains("own.txt", list);

        var searchContext = Context(audience);
        var search = await new FileSearchTool(policy).ExecuteAsync(
            ToolInput.Create("Root", _configuredRoot, "Query", "marker", "Mode", "content"), searchContext, ct);
        AssertAllowed(searchContext, search);
        Assert.Contains("own-marker", search);
        Assert.Contains("shared-marker", search);
    }

    // Positive example: the same root still writes into the own key folder. The private base
    // exists only on POSIX hosts, so this row runs only there.
    [SlopwatchSuppress("SW001", "The private base exists only on POSIX hosts.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The private base exists only on POSIX hosts.")]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Public)]
    public async Task A_configured_root_over_the_base_writes_into_the_own_key_folder(TrustAudience audience)
    {
        var (_, policy) = CreatePolicy(audience, new ToolPathPolicy([]));
        var ct = TestContext.Current.CancellationToken;

        var written = Path.Combine(_storage.ManagedTemporary.Directory.Value, "frame.txt");
        var writeContext = Context(audience);
        var write = await new FileWriteTool(policy).ExecuteAsync(ToolInput.Create("Path", written, "Content", "frame"), writeContext, ct);
        AssertAllowed(writeContext, write);
        Assert.True(File.Exists(written));
    }

    // Protection wins over the own key folder root and over the configured root. This row
    // guards the order of the checks: a fix for the other key folders must not put a session
    // root before the protected-path check.
    [Theory]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Public)]
    public async Task A_protected_path_in_the_own_key_folder_stays_denied(TrustAudience audience)
    {
        var (config, policy) = CreatePolicy(audience, new ToolPathPolicy([_ownFile]));
        var ct = TestContext.Current.CancellationToken;

        var context = Context(audience);
        var read = await new FileReadTool(config, policy).ExecuteAsync(ToolInput.Create("Path", _ownFile), context, ct);
        AssertDenied(context, read);
        Assert.DoesNotContain("own-marker", read);

        context = Context(audience);
        var write = await new FileWriteTool(policy).ExecuteAsync(ToolInput.Create("Path", _ownFile, "Content", "changed"), context, ct);
        AssertDenied(context, write);
        Assert.Equal("own-marker", await File.ReadAllTextAsync(_ownFile, ct));
    }

    private (ToolConfig Config, PathAccessPolicy Policy) CreatePolicy(TrustAudience audience, ToolPathPolicy protectedPaths)
    {
        var config = new ToolConfig();
        var profile = audience == TrustAudience.Public ? config.AudienceProfiles.Public : config.AudienceProfiles.Team;
        profile.ReadFiles = new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.Roots, Roots = [_configuredRoot] };
        profile.WriteFiles = new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.Roots, Roots = [_configuredRoot] };
        return (config, new PathAccessPolicy(config, _paths, protectedPaths));
    }

    private ToolExecutionContext Context(TrustAudience audience)
        => TestToolExecutionContext.CreateBoundWithStorage(
            "slack/team-session",
            _storage,
            new TestToolExecutionContextOptions
            {
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                ChannelType = "slack"
            });

    private static void AssertAllowed(ToolExecutionContext context, string result)
        => Assert.True(
            context.Receipt?.Category == ToolInvocationOutcomeCategory.Success,
            $"Expected success, got {context.Receipt?.Category}: {result}");

    private static void AssertDenied(ToolExecutionContext context, string result)
        => Assert.True(
            context.Receipt?.Category == ToolInvocationOutcomeCategory.AccessDenied,
            $"Expected access denied, got {context.Receipt?.Category}: {result}");

    private static string Seed(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
