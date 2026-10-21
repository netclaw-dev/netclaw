// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryFirstWriteTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// PR #2559 review item B1. A file tool can be the first writer below the private
/// managed temporary base. A Team session has no shell, and the skill tells the agent to
/// use <c>file_write</c> in <c>temp_dir</c>. That write must leave the base, the key folder,
/// and the run folder without a group or other permission bit. After the write, the shell
/// launch check of this session and of another session must still pass.
/// </summary>
/// <remarks>
/// The test calls the tool directly with a fresh base. A resolve-time step is not
/// enough: a host cleaner can remove the folders of an idle session after the resolve,
/// and the next file tool write then makes them again. The base is below the private
/// temp folder of the test, never the <c>/tmp/netclaw-&lt;uid&gt;</c> folder of a live
/// daemon. The test proves the defect on a host with the usual umask (0022).
/// </remarks>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ManagedTemporaryFirstWriteTests : IDisposable
{
    private const UnixFileMode GroupAndOtherBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly string _base;
    private readonly SessionStoragePaths _storage;
    private readonly SessionStoragePaths _otherStorage;

    public ManagedTemporaryFirstWriteTests()
    {
        _paths = new NetclawPaths(Path.Combine(_dir.Path, "home"));
        _base = Path.Combine(_dir.Path, "netclaw-base");
        var layout = new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase(_base));
        _storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "session-a")),
            layout);
        _otherStorage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "session-b")),
            layout);
    }

    public static bool IsPosix => !OperatingSystem.IsWindows();

    public void Dispose() => _dir.Dispose();

    [SlopwatchSuppress("SW001", "The private base exists only on POSIX hosts.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The private base exists only on POSIX hosts.")]
    [InlineData(TrustAudience.Personal, FileWriteTool.ToolName, false)]
    [InlineData(TrustAudience.Team, FileWriteTool.ToolName, false)]
    [InlineData(TrustAudience.Team, FileEditTool.ToolName, false)]
    [InlineData(TrustAudience.Team, FileWriteTool.ToolName, true)]
    public async Task A_first_file_tool_write_to_temp_dir_keeps_the_folders_private_and_the_shell_working(
        TrustAudience audience,
        string toolName,
        bool childRun)
    {
        var run = childRun
            ? _storage.ForChild(new SubAgentRunId("run-7"), new SubAgentScopeId("s/subagent/a/run-7"))
            : _storage;
        var runFolder = run.ManagedTemporary.Directory.Value;
        var keyFolder = run.ManagedTemporary.StorageRoot.Value;
        var target = Path.Combine(runFolder, "notes.txt");
        Assert.False(Directory.Exists(_base), "No shell call ran, so the base must not exist yet.");

        var context = Context(audience, run);
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var input = ToolInput.Create("Path", target, "Content", "notes");
        var result = toolName == FileEditTool.ToolName
            ? await new FileEditTool(policy).ExecuteAsync(input, context, TestContext.Current.CancellationToken)
            : await new FileWriteTool(policy).ExecuteAsync(input, context, TestContext.Current.CancellationToken);

        Assert.True(
            context.Receipt?.Category == ToolInvocationOutcomeCategory.Success,
            $"The write to temp_dir must succeed: {result}");
        Assert.Equal("notes", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));

        foreach (var folder in new[] { _base, keyFolder, runFolder })
        {
            var mode = File.GetUnixFileMode(folder);
            Assert.True(
                (mode & GroupAndOtherBits) == 0,
                $"The file tool left '{folder}' with group or other access ({mode}).");
        }

        Assert.Null(ManagedTemporaryEnvironment.Prepare(new ProcessStartInfo("true"), run.ManagedTemporary));
        Assert.Null(ManagedTemporaryEnvironment.Prepare(new ProcessStartInfo("true"), _otherStorage.ManagedTemporary));
    }

    private static ToolExecutionContext Context(TrustAudience audience, SessionStoragePaths storage)
        => TestToolExecutionContext.CreateBoundWithStorage(
            "test/session-a",
            storage,
            new TestToolExecutionContextOptions
            {
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                ChannelType = audience == TrustAudience.Personal ? "signalr" : "slack"
            });
}
